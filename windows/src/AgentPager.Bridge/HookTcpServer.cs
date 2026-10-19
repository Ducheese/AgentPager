using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentPager.Core;

namespace AgentPager.Bridge;

public sealed class HookTcpServer(
    Func<HookEnvelope, Task> hookHandler,
    DailyFileLog log,
    Func<bool>? isPhoneConnected = null,
    Action<string>? onPendingAbandoned = null)
    : ICodexPermissionResolver, IAsyncDisposable
{
    // 没有手机连着就别抢提问：agy 的 ask_question 若无人可答，应当放行让终端自己弹框，
    // 而不是把工具调用攥住到超时（用户裁定，2026-10-14）。其它来源不套这个门（行为不变）。
    private readonly Func<bool> _isPhoneConnected = isPhoneConnected ?? (() => true);

    private readonly ConcurrentDictionary<string, (TcpClient Client, HookSource Source)> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<TcpClient, byte> _openCodeControlClients = new();
    public bool HasOpenCodeControlClients => !_openCodeControlClients.IsEmpty;
    private TcpListener? _listener;
    private CancellationTokenSource? _cancellation;
    private Task? _acceptTask;

    public void Start()
    {
        _cancellation = new();
        _listener = new(IPAddress.Loopback, 49361);
        _listener.Start();
        _acceptTask = AcceptLoop(_cancellation.Token);
    }

    public bool Resolve(string sessionID, CodexPermissionDecision decision)
    {
        if (!_pending.TryRemove(sessionID, out var entry))
        {
            // 手机上点了"允许/拒绝"却没有待决连接 = 这条审批早就被撤了（等待在别处结束了，
            // 或并行工具的事件把卡片撤走、连带把 hook 也撤了）。以前这里一声不吭 ✗ ——
            // 用户只看到"点了没反应"，日志里查不到任何痕迹（2026-10-11 实测踩到）。必须落盘。
            log.Write("WARN", $"[审批] 没有待决连接（会话 {sessionID}）：审批回填失败");
            return false;
        }
        try
        {
            using (entry.Client)
            {
                var response = entry.Source switch
                {
                    HookSource.Claude => ClaudeHookOutput.Permission(decision == CodexPermissionDecision.Allow
                        ? ClaudePermissionDecision.Allow
                        : ClaudePermissionDecision.Deny),
                    HookSource.OpenCode => OpenCodeHookOutput.Decision(decision == CodexPermissionDecision.Allow),
                    _ => CodexHookOutput.Permission(decision),
                };
                log.Write("INFO", $"[{entry.Source}] 审批已回填（{(decision == CodexPermissionDecision.Allow ? "allow" : "deny")}，会话 {sessionID}）");
                entry.Client.GetStream().Write(response);
            }
            return true;
        }
        catch (IOException) { return false; }
    }

    /// <summary>
    /// 作答：与 <see cref="Resolve"/> 同一条待决通道，只是回包换成"带答案的放行"。
    /// 各家的答案形态不同，**落形在这一层做**（只有它认得出这条连接来自谁）：
    ///   · Claude → 改写 `PermissionRequest` 的 `updatedInput`；
    ///   · opencode → "每题一组 label"的 answers 数组（插件拿去调它自己的 reply 接口）；
    ///   · agy → `deny` + `reason`（答案随拒绝理由进模型；`overwrite` 预填已实测证伪，
    ///     见 <see cref="AgyHookOutput"/>）。
    /// </summary>
    public bool ResolveQuestion(string sessionID, string? rawToolInput, IReadOnlyList<QuestionAnswer> answers)
    {
        if (!_pending.TryRemove(sessionID, out var entry))
        {
            log.Write("WARN", $"[作答] 没有待决连接（会话 {sessionID}）：作答回填失败");
            return false;
        }
        try
        {
            using (entry.Client)
            {
                byte[] response;
                switch (entry.Source)
                {
                    case HookSource.OpenCode:
                        response = OpenCodeHookOutput.Answers(
                            answers.Select(answer => answer.Entries).ToList());
                        break;
                    case HookSource.Claude when rawToolInput is { Length: > 0 }:
                        response = ClaudeHookOutput.QuestionReply(rawToolInput, answers);
                        break;
                    case HookSource.Antigravity:
                        response = AgyHookOutput.QuestionReply(answers);
                        break;
                    default:
                        log.Write("WARN", $"[作答] 该来源不支持作答：{entry.Source}");
                        return false;
                }
                entry.Client.GetStream().Write(response);
                log.Write("INFO", $"[{entry.Source}] 作答已回填（{answers.Count} 题，会话 {sessionID}）");
            }
            return true;
        }
        catch (IOException error)
        {
            log.Write("WARN", $"[作答] 回填失败：{error.Message}");
            return false;
        }
    }

    public bool DispatchPrompt(string sessionID, AgentSource source, string prompt, string? workspaceDir = null, bool isRunning = false)
    {
        switch (source)
        {
            case AgentSource.OpenCode:
                return DispatchOpenCodePrompt(sessionID, prompt);

            case AgentSource.ClaudeCode:
                return ClaudePromptDispatcher.QueueOrExecute(sessionID, prompt, workspaceDir, isRunning, log.Write);

            case AgentSource.Antigravity:
                return AgyCliDispatcher.Dispatch(sessionID, prompt, workspaceDir, isRunning, log.Write);

            default:
                log.Write("WARN", $"[Prompt] 不支持的 Agent 来源：{source}");
                return false;
        }
    }

    private void RegisterOpenCodeControlClient(TcpClient client)
    {
        _openCodeControlClients.TryAdd(client, 0);
        log.Write("INFO", "[OpenCode] 常驻控制通道已连接");
        _ = Task.Run(async () =>
        {
            var buffer = new byte[1024];
            try
            {
                var stream = client.GetStream();
                while (await stream.ReadAsync(buffer) > 0)
                {
                    // 保持连接，持续读取
                }
            }
            catch { }
            finally
            {
                _openCodeControlClients.TryRemove(client, out _);
                client.Dispose();
                log.Write("INFO", "[OpenCode] 常驻控制通道已断开");
            }
        });
    }

    private bool DispatchOpenCodePrompt(string sessionID, string prompt)
    {
        var clients = _openCodeControlClients.Keys.ToList();
        if (clients.Count == 0)
        {
            log.Write("WARN", $"[OpenCode] 没有可用的常驻控制连接，无法下发 Prompt（会话 {sessionID}）");
            return false;
        }

        var json = JsonSerializer.Serialize(new { action = "prompt", sessionID, text = prompt }) + "\n";
        var bytes = Encoding.UTF8.GetBytes(json);
        var successCount = 0;

        foreach (var client in clients)
        {
            try
            {
                if (client.Connected)
                {
                    client.GetStream().Write(bytes);
                    successCount++;
                }
            }
            catch (Exception ex)
            {
                log.Write("WARN", $"[OpenCode] 向控制通道发送失败：{ex.Message}");
                _openCodeControlClients.TryRemove(client, out _);
                client.Dispose();
            }
        }

        if (successCount > 0)
        {
            log.Write("INFO", $"[OpenCode] Prompt 已下发至 {successCount} 个控制连接（会话 {sessionID}）");
            return true;
        }
        return false;
    }

    /// <summary>是否还攥着某会话的待决连接（权限/作答等待中）。</summary>
    public bool HasPending(string sessionID) => _pending.ContainsKey(sessionID);

    /// <summary>
    /// 释放挂住的连接，**不写任何回复**。
    ///
    /// 场景：等待已经以别的方式结束了——用户在终端自己作答、工具跑完、回合结束。
    /// 实测（2026-10-08）：这种情况下 Claude Code **不会**替我们杀掉 hook 进程，那个 node
    /// 会一直挂到 `timeout`（提问/权限是 600 秒）才收场。主动关掉 socket，脚本看到 close
    /// 即 exit 0，Claude Code 视作"没有输出"——正是我们要的"不干预"。
    /// </summary>
    public bool Cancel(string sessionID)
    {
        if (!_pending.TryRemove(sessionID, out var entry)) return false;
        entry.Client.Dispose();
        log.Write("INFO", $"[{entry.Source}] 释放挂起的通道（等待已在别处结束，会话 {sessionID}）");
        return true;
    }

    /// <summary>
    /// 等这条挂住连接的**对端关掉**，然后摘掉待决记录并通知上层撤卡。
    ///
    /// 必须区分"对端自己走了"与"我方主动收尾"：回填/取消/被新请求顶替都会先删记录再 Dispose，
    /// 此时条件删除会失败，不会误发"交回终端"。只有记录还在、对端却断了，才是 hook 进程真的没了。
    /// </summary>
    private async Task WatchHeldConnection(string sessionID, TcpClient client, HookSource source)
    {
        await PendingHold.WaitForPeerCloseAsync(client);

        var held = new KeyValuePair<string, (TcpClient Client, HookSource Source)>(sessionID, (client, source));
        var removed = ((ICollection<KeyValuePair<string, (TcpClient Client, HookSource Source)>>)_pending).Remove(held);
        if (!removed) return;

        log.Write("WARN", $"[{source}] 挂起的连接已断开（会话 {sessionID}）：本次询问交回终端");
        try { onPendingAbandoned?.Invoke(sessionID); }
        catch (Exception error) { log.Write("ERROR", $"[{source}] 撤卡通知失败：{error.Message}"); }
    }

    private async Task AcceptLoop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(cancellationToken);
                _ = Handle(client, cancellationToken);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException error) { log.Write("ERROR", $"Hook accept: {error.Message}"); }
        }
    }

    private async Task Handle(TcpClient client, CancellationToken cancellationToken)
    {
        try
        {
            var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) { client.Dispose(); return; }

            if (line.Contains("\"control.attach\""))
            {
                RegisterOpenCodeControlClient(client);
                return;
            }

            var envelope = DecodeEnvelope(line);
            if (envelope is null)
            {
                // 无法识别来源时按 Codex 兼容旧版 CLI；解析失败则放行不阻塞。
                envelope = TryDecodeCodex(line);
            }
            if (envelope is null) { client.Dispose(); return; }

            // "要不要挂住这条连接等手机" —— 三家形态不同，意思相同：
            //   · Codex/Claude：权限询问事件（Claude 的提问工具也走这条，回包带 updatedInput）
            //   · opencode：插件显式带 hold 标记的帧（权限询问 v13 起挂住、提问 v16 起也挂住）
            //   · agy：**只有 ask_question 的 PreToolUse**（脚本 v3 只在这一条上等回包）。
            //     边界必须写死成提问本身：曾经把"所有 PreToolUse"都攥住，agy 在每一把普通工具上
            //     被卡住（2026-10-14 实测），因为脚本那时也在等一条永远不会来的回包。
            var isPermission = envelope switch
            {
                HookEnvelope.Codex codex => codex.Hook.HookEventName == CodexHookEventName.PermissionRequest,
                HookEnvelope.Claude claude => claude.Hook.HookEventName == "PermissionRequest",
                HookEnvelope.OpenCode openCode => openCode.Hook.Hold is { Length: > 0 },
                HookEnvelope.Antigravity agy => agy.Hook.EffectiveEvent == "PreToolUse" &&
                                                agy.Hook.ToolCall?.Name?.Equals("ask_question", StringComparison.OrdinalIgnoreCase) == true &&
                                                _isPhoneConnected(),
                _ => false,
            };
            var sessionID = envelope switch
            {
                HookEnvelope.Codex codex => codex.Hook.SessionID,
                HookEnvelope.Claude claude => claude.Hook.SessionID,
                HookEnvelope.OpenCode openCode => OpenCodeEventReducer.SessionID(openCode.Hook),
                HookEnvelope.Antigravity agy => agy.Hook.SessionID,
                _ => null,
            };
            if (sessionID is null) { client.Dispose(); return; }

            if (isPermission)
            {
                var source = envelope switch
                {
                    HookEnvelope.Claude => HookSource.Claude,
                    HookEnvelope.OpenCode => HookSource.OpenCode,
                    HookEnvelope.Antigravity => HookSource.Antigravity,
                    _ => HookSource.Codex,
                };
                if (_pending.TryGetValue(sessionID, out var previous)) previous.Client.Dispose();
                _pending[sessionID] = (client, source);
                log.Write("INFO", $"[{source}] 挂起等待（会话 {sessionID}）");
                // 看着这条连接：hook 进程自己退出（脚本超时/被杀/Claude 退出）时把死记录摘掉，
                // 否则后续手机点"允许"会往死 socket 写，卡片也永远撤不掉。
                _ = WatchHeldConnection(sessionID, client, source);
                await hookHandler(envelope);
                return;
            }

            await hookHandler(envelope);
            // Antigravity 的 Stop 不再做 pull 交接：投递已改成「到达即按键注入」
            // （AgyCliDispatcher.Dispatch），Stop 只管照常收尾——空回包让插件打 {}，agy 正常结束回合。
            // 排队/打断的语义由 agy 自己的 Queued Messages 决定，桥不再替它排队。
            await stream.WriteAsync("\n"u8.ToArray(), cancellationToken);
            client.Dispose();
        }
        catch (Exception error) when (error is IOException or JsonException or OperationCanceledException)
        {
            client.Dispose();
            log.Write("WARN", $"Hook 请求失败：{error.Message}");
        }
    }

    private static HookEnvelope? DecodeEnvelope(string line)
    {
        JsonObject? root;
        try { root = JsonNode.Parse(line)?.AsObject(); }
        catch { return null; }
        if (root is null) return null;
        if (root["hook_source"]?.GetValue<string>() is not { } rawSource ||
            !Enum.TryParse<HookSource>(rawSource, ignoreCase: true, out var source) ||
            root["payload"] is null)
            return null;

        var payloadNode = root["payload"]!.ToJsonString();
        return source switch
        {
            HookSource.Codex => JsonSerializer.Deserialize<CodexHookPayload>(payloadNode, WireJson.Options) is { } codex
                ? new HookEnvelope.Codex(codex)
                : null,
            HookSource.Claude => JsonSerializer.Deserialize<ClaudeHookPayload>(payloadNode, WireJson.Options) is { } claude
                ? new HookEnvelope.Claude(claude)
                : null,
            HookSource.OpenCode => JsonSerializer.Deserialize<OpenCodeHookPayload>(payloadNode, WireJson.Options) is { } openCode
                ? new HookEnvelope.OpenCode(openCode)
                : null,
            HookSource.Antigravity => JsonSerializer.Deserialize<AgyHookPayload>(payloadNode, WireJson.Options) is { } agy
                ? new HookEnvelope.Antigravity(agy)
                : null,
            _ => null,
        };
    }

    private static HookEnvelope? TryDecodeCodex(string line)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<CodexHookPayload>(line, WireJson.Options);
            return payload is null ? null : new HookEnvelope.Codex(payload);
        }
        catch (JsonException) { return null; }
    }

    public async ValueTask DisposeAsync()
    {
        _cancellation?.Cancel();
        _listener?.Stop();
        foreach (var entry in _pending.Values) entry.Client.Dispose();
        _pending.Clear();
        foreach (var c in _openCodeControlClients.Keys) c.Dispose();
        _openCodeControlClients.Clear();
        if (_acceptTask is not null)
            try { await _acceptTask; } catch (OperationCanceledException) { }
        _cancellation?.Dispose();
    }
}
