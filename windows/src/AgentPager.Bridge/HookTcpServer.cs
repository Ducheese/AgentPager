using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentPager.Core;

namespace AgentPager.Bridge;

public sealed class HookTcpServer(Func<HookEnvelope, Task> hookHandler, DailyFileLog log)
    : ICodexPermissionResolver, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, (TcpClient Client, HookSource Source)> _pending = new(StringComparer.Ordinal);
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
        if (!_pending.TryRemove(sessionID, out var entry)) return false;
        try
        {
            using (entry.Client)
            {
                var response = entry.Source == HookSource.Claude
                    ? ClaudeHookOutput.Permission(decision == CodexPermissionDecision.Allow
                        ? ClaudePermissionDecision.Allow
                        : ClaudePermissionDecision.Deny)
                    : CodexHookOutput.Permission(decision);
                entry.Client.GetStream().Write(response);
            }
            return true;
        }
        catch (IOException) { return false; }
    }

    /// <summary>
    /// 作答：与 <see cref="Resolve"/> 同一条待决通道，只是回包换成"带答案的放行"。
    /// 只有 Claude 走这条（其它来源没有"提问即权限询问"这种形态）。
    /// </summary>
    public bool ResolveAnswer(string sessionID, string rawToolInput, string answer)
    {
        if (!_pending.TryRemove(sessionID, out var entry))
        {
            log.Write("WARN", $"[Claude] 作答时没有待决连接：会话 {sessionID}");
            return false;
        }
        try
        {
            using (entry.Client)
            {
                if (entry.Source != HookSource.Claude)
                {
                    log.Write("WARN", $"[Claude] 作答通道来源不是 Claude：{entry.Source}");
                    return false;
                }
                entry.Client.GetStream().Write(ClaudeHookOutput.Answer(rawToolInput, answer));
            }
            log.Write("INFO", $"[Claude] 作答已回填（{answer.Length} 字，会话 {sessionID}）");
            return true;
        }
        catch (IOException error)
        {
            log.Write("WARN", $"[Claude] 作答回填失败：{error.Message}");
            return false;
        }
    }

    /// <summary>是否还攥着某会话的待决连接（权限/作答等待中）。</summary>
    public bool HasPending(string sessionID) => _pending.ContainsKey(sessionID);

    /// <summary>
    /// 释放挂住的连接，**不写任何回复**。
    ///
    /// 场景：等待已经以别的方式结束了——用户在终端自己作答、工具跑完、回合结束。
    /// 实测（2026-10-08）：这种情况下 Claude Code **不会**替我们杀掉 hook 进程，那个 node
    /// 会一直挂到 `timeout`（提问/权限是 86400 秒）才收场。主动关掉 socket，脚本看到 close
    /// 即 exit 0，Claude Code 视作"没有输出"——正是我们要的"不干预"。
    /// </summary>
    public bool Cancel(string sessionID)
    {
        if (!_pending.TryRemove(sessionID, out var entry)) return false;
        entry.Client.Dispose();
        log.Write("INFO", $"[Claude] 释放挂起的 hook（等待已在别处结束，会话 {sessionID}）");
        return true;
    }

    public bool ResolveAnswers(string sessionID, string rawToolInput, IReadOnlyDictionary<string, string> answers)
    {
        if (!_pending.TryRemove(sessionID, out var entry))
        {
            log.Write("WARN", $"[Claude] 多题回填时没有待决连接：会话 {sessionID}");
            return false;
        }
        try
        {
            using (entry.Client)
            {
                if (entry.Source != HookSource.Claude)
                {
                    log.Write("WARN", $"[Claude] 作答通道来源不是 Claude：{entry.Source}");
                    return false;
                }
                entry.Client.GetStream().Write(ClaudeHookOutput.AnswerMap(rawToolInput, answers));
            }
            log.Write("INFO", $"[Claude] 多题作答已回填（{answers.Count} 题，会话 {sessionID}）");
            return true;
        }
        catch (IOException error)
        {
            log.Write("WARN", $"[Claude] 多题回填失败：{error.Message}");
            return false;
        }
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

            var envelope = DecodeEnvelope(line);
            if (envelope is null)
            {
                // 无法识别来源时按 Codex 兼容旧版 CLI；解析失败则放行不阻塞。
                envelope = TryDecodeCodex(line);
            }
            if (envelope is null) { client.Dispose(); return; }

            var isPermission = envelope switch
            {
                HookEnvelope.Codex codex => codex.Hook.HookEventName == CodexHookEventName.PermissionRequest,
                HookEnvelope.Claude claude => claude.Hook.HookEventName == "PermissionRequest",
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
                var source = envelope is HookEnvelope.Claude ? HookSource.Claude : HookSource.Codex;
                if (_pending.TryGetValue(sessionID, out var previous)) previous.Client.Dispose();
                _pending[sessionID] = (client, source);
                log.Write("INFO", $"[{source}] 挂起等待（会话 {sessionID}）");
                await hookHandler(envelope);
                return;
            }

            await hookHandler(envelope);
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
        if (_acceptTask is not null)
            try { await _acceptTask; } catch (OperationCanceledException) { }
        _cancellation?.Dispose();
    }
}
