using System.Text.Json;
using System.Threading.Channels;
using AgentPager.Core;

namespace AgentPager.Bridge;

public sealed record BridgeViewState(
    string ServiceStatus,
    int PhoneCount,
    bool HookInstalled,
    string ClaudeHookText,
    ClaudeHookStatus ClaudeHookState,
    string OpenCodePluginText,
    OpenCodePluginStatus OpenCodePluginState,
    DateTimeOffset? LastHookAt,
    string? LastError,
    string PairingText,
    string SelectedAddress,
    List<NetworkAddressOption> Networks,
    int TaskCount,
    string AgyHookText = "未安装",
    AntigravityPluginStatus AgyHookState = AntigravityPluginStatus.NotInstalled);

public sealed class BridgeRuntime : IAsyncDisposable
{
    private abstract record BridgeEvent;
    private sealed record HookEvent(HookEnvelope Envelope) : BridgeEvent;
    private sealed record RolloutEvent(List<CodexRolloutSignal> Signals) : BridgeEvent;
    private sealed record ControlEvent(string Text) : BridgeEvent;
    private sealed record PhoneCountEvent(int Count) : BridgeEvent;
    private sealed record UsageEvent(UsageSnapshot? Usage, List<UsageSnapshot>? Usages = null) : BridgeEvent;
    private sealed record MaintenanceEvent : BridgeEvent;

    /// <summary>
    /// 人类等待答复的上限（审批与提问同一口径，三家共用）：到点就把这次询问交回终端。
    /// 与离线判定的 10 分钟取同一个数 —— 超时交回后若代理真的没了，静默离线规则正好接上 ✓。
    /// 插件侧（opencode 的 HOLD_TIMEOUT_MS）与 Claude 的 hook timeout 都按这个值对齐。
    /// </summary>
    private static readonly TimeSpan HoldLimit = TimeSpan.FromMinutes(10);

    private readonly DailyFileLog _log = new();
    private readonly Channel<BridgeEvent> _events = Channel.CreateUnbounded<BridgeEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly TaskSnapshotPersistence _persistence = new();
    private readonly CodexSessionTitleReader _titleReader = new();
    private readonly CodexRolloutObserver _rollout = new();
    private readonly CodexUsageLoader _usageLoader = new();
    private readonly AgyUsageLoader _agyUsageLoader = new();
    private readonly CommandCodeUsageLoader _commandCodeUsageLoader = new();
    private readonly CodexHookConfiguration _hookConfiguration = new();
    private readonly ClaudeHookConfiguration _claudeHookConfiguration = new();
    private readonly OpenCodePluginConfiguration _openCodePluginConfiguration = new();
    private readonly AgyHookConfiguration _agyHookConfiguration = new();
    private readonly ReplayGuard _replayGuard = new();
    private readonly byte[] _pairingSecret;
    private readonly TaskCatalog _catalog;
    private HookTcpServer? _hookServer;
    private LanBridgeServer? _lanServer;
    private CancellationTokenSource? _linkedCancellation;
    private Task? _eventTask;
    private Task? _pollTask;
    private Task? _usageTask;
    private UsageSnapshot? _usage;
    private List<UsageSnapshot> _usages = [];
    private DateTimeOffset? _lastHookAt;
    private string _serviceStatus = "正在启动";
    private string? _lastError;
    private int _phoneCount;
    private string _selectedAddress;
    private List<NetworkAddressOption> _networks;

    public BridgeRuntime()
    {
        Settings = BridgeSettings.Load();
        _networks = NetworkAddresses.Available();
        _selectedAddress = _networks.FirstOrDefault(value => value.Address == Settings.SelectedNetworkAddress)?.Address
            ?? _networks.FirstOrDefault()?.Address ?? "127.0.0.1";
        Settings.SelectedNetworkAddress = _selectedAddress;
        // 两家额度拉取都可能静默返回 null——没有日志就只能靠猜（实测踩过）。
        _agyUsageLoader.Log = _log.Write;
        _commandCodeUsageLoader.Log = _log.Write;
        _pairingSecret = new PairingSecretStore().LoadOrCreate();
        _catalog = new(_persistence.Load());
        _catalog.ApplyTitles(_titleReader.Load());
        State = CreateState();
    }

    public BridgeSettings Settings { get; }
    public BridgeViewState State { get; private set; }
    public event Action? StateChanged;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _linkedCancellation.Token;
        _eventTask = EventLoop(token);
        try
        {
            _hookServer = new HookTcpServer(
                envelope => _events.Writer.WriteAsync(new HookEvent(envelope), token).AsTask(),
                _log,
                // "有没有手机连着" —— agy 的提问拦截只在有人能作答时才成立（见 HookTcpServer 的说明）。
                () => Volatile.Read(ref _phoneCount) > 0);
            _hookServer.Start();
            _lanServer = new(
                PairingText,
                SnapshotText,
                text => _events.Writer.WriteAsync(new ControlEvent(text), token).AsTask(),
                count => _events.Writer.TryWrite(new PhoneCountEvent(count)),
                _log);
            await _lanServer.StartAsync(token);
            _serviceStatus = "局域网服务运行中";
            _log.Write("INFO", "Bridge 服务已启动。");
        }
        catch (Exception error)
        {
            _serviceStatus = "服务启动失败";
            _lastError = error.Message;
            _log.Write("ERROR", error.ToString());
        }
        PublishState();
        _pollTask = PollLoop(token);
        _usageTask = UsageLoop(token);
    }

    public void InstallHook()
    {
        try
        {
            var result = _hookConfiguration.Install(Environment.ProcessPath!);
            _log.Write("INFO", result.Changed ? "Codex Hook 已安装。" : "Codex Hook 已是最新状态。");
            _lastError = null;
        }
        catch (Exception error)
        {
            _lastError = $"安装 Hook 失败：{error.Message}";
            _log.Write("ERROR", _lastError);
        }
        PublishState();
    }

    public void UninstallHook()
    {
        try
        {
            _ = _hookConfiguration.Uninstall();
            _lastError = null;
            _log.Write("INFO", "Codex Hook 已移除。");
        }
        catch (Exception error)
        {
            _lastError = $"卸载 Hook 失败：{error.Message}";
            _log.Write("ERROR", _lastError);
        }
        PublishState();
    }

    public void InstallClaudeHook()
    {
        try
        {
            var result = _claudeHookConfiguration.Install();
            _log.Write("INFO", result.Changed ? "Claude Code Hook 已安装。" : "Claude Code Hook 已是最新状态。");
            _lastError = null;
        }
        catch (Exception error)
        {
            _lastError = $"安装 Claude Code Hook 失败：{error.Message}";
            _log.Write("ERROR", _lastError);
        }
        PublishState();
    }

    public void UninstallClaudeHook()
    {
        try
        {
            _ = _claudeHookConfiguration.Uninstall();
            _lastError = null;
            _log.Write("INFO", "Claude Code Hook 已移除。");
        }
        catch (Exception error)
        {
            _lastError = $"卸载 Claude Code Hook 失败：{error.Message}";
            _log.Write("ERROR", _lastError);
        }
        PublishState();
    }

    public void InstallOpenCodePlugin()
    {
        try
        {
            var result = _openCodePluginConfiguration.Install();
            var version = OpenCodePluginConfiguration.EmbeddedVersion;
            _log.Write("INFO", result.Changed ? $"OpenCode 插件已安装（v{version}）。" : $"OpenCode 插件已是最新状态（v{version}）。");
            _lastError = null;
        }
        catch (Exception error)
        {
            _lastError = $"安装 OpenCode 插件失败：{error.Message}";
            _log.Write("ERROR", _lastError);
        }
        PublishState();
    }

    public void UninstallOpenCodePlugin()
    {
        try
        {
            _ = _openCodePluginConfiguration.Uninstall();
            _lastError = null;
            _log.Write("INFO", "OpenCode 插件已移除。");
        }
        catch (Exception error)
        {
            _lastError = $"卸载 OpenCode 插件失败：{error.Message}";
            _log.Write("ERROR", _lastError);
        }
        PublishState();
    }

    public void InstallAgyHook()
    {
        try
        {
            var result = _agyHookConfiguration.Install();
            var version = AgyHookConfiguration.EmbeddedVersion;
            _log.Write("INFO", result.Changed ? $"Antigravity Hook 已安装（v{version}）。" : $"Antigravity Hook 已是最新状态（v{version}）。");
            _lastError = null;
        }
        catch (Exception error)
        {
            _lastError = $"安装 Antigravity Hook 失败：{error.Message}";
            _log.Write("ERROR", _lastError);
        }
        PublishState();
    }

    public void UninstallAgyHook()
    {
        try
        {
            _ = _agyHookConfiguration.Uninstall();
            _lastError = null;
            _log.Write("INFO", "Antigravity Hook 已移除。");
        }
        catch (Exception error)
        {
            _lastError = $"卸载 Antigravity Hook 失败：{error.Message}";
            _log.Write("ERROR", _lastError);
        }
        PublishState();
    }

    public void SelectAddress(string address)
    {
        if (_networks.All(value => value.Address != address)) return;
        _selectedAddress = address;
        Settings.SelectedNetworkAddress = address;
        Settings.Save();
        PublishState();
    }

    public void SetLaunchAtLogin(bool enabled)
    {
        Settings.LaunchAtLogin = enabled;
        Settings.Save();
        PublishState();
    }

    public void RefreshNetworks()
    {
        _networks = NetworkAddresses.Available();
        if (_networks.All(value => value.Address != _selectedAddress))
            _selectedAddress = _networks.FirstOrDefault()?.Address ?? "127.0.0.1";
        Settings.SelectedNetworkAddress = _selectedAddress;
        Settings.Save();
        PublishState();
    }

    private async Task EventLoop(CancellationToken cancellationToken)
    {
        await foreach (var value in _events.Reader.ReadAllAsync(cancellationToken))
        {
            try
            {
                var changed = false;
                switch (value)
                {
                    case HookEvent hook:
                        _lastHookAt = DateTimeOffset.Now;
                        changed = hook.Envelope switch
                        {
                            HookEnvelope.Codex codex => HandleCodexHook(codex.Hook),
                            HookEnvelope.Claude claude => AcceptClaudeHook(claude.Hook),
                            HookEnvelope.OpenCode openCode => AcceptOpenCodeHook(openCode.Hook),
                            HookEnvelope.Antigravity agy => AcceptAgyHook(agy.Hook),
                            _ => false,
                        };
                        break;
                    case RolloutEvent rollout:
                        changed = _catalog.Accept(rollout.Signals);
                        break;
                    case ControlEvent control:
                        changed = await HandleControl(control.Text);
                        break;
                    case PhoneCountEvent count:
                        _phoneCount = count.Count;
                        changed = true;
                        break;
                    case UsageEvent usage:
                        _usage = usage.Usage;
                        _usages = usage.Usages ?? (usage.Usage is not null ? [usage.Usage] : []);
                        changed = true;
                        break;
                    case MaintenanceEvent:
                        changed = _catalog.Maintain();
                        changed = _catalog.ApplyTitles(_titleReader.Load()) || changed;
                        // 挂起超时（三家统一）：等手机答复超过 HoldLimit 就把这次询问**交回终端** ——
                        // 撤卡 + 释放挂住的连接 + 回 running。用户可能在终端上作答 ✓，判死/判离线
                        // 会造出与事实相反的显示 ✗（见 ExpireStaleWaits 的说明）。
                        foreach (var stuck in _catalog.ExpireStaleWaits(HoldLimit))
                        {
                            _hookServer?.Cancel(stuck);
                            _log.Write("INFO", $"挂起超时（{HoldLimit.TotalMinutes:0} 分钟无人答复）：已交回终端（会话 {stuck}）");
                        }
                        break;
                }
                if (!changed) continue;
                SaveAndBroadcast();
            }
            catch (Exception error)
            {
                _lastError = error.Message;
                _log.Write("ERROR", error.ToString());
                PublishState();
            }
        }
    }

    /// <summary>
    /// Claude 事件统一入口：处理完后**顺手释放已经用不上的待决连接**。
    /// 等待若以别的方式结束（用户在终端作答、工具跑完、回合结束），挂住的 hook 就没人管了——
    /// 实测 Claude Code 不会替我们杀它，那个 node 进程会一直挂到 24 小时超时。
    /// </summary>
    private bool AcceptClaudeHook(ClaudeHookPayload hook)
    {
        var changed = _catalog.Accept(hook);
        if (_hookServer is not null &&
            _hookServer.HasPending(hook.SessionID) &&
            !_catalog.HasPendingRequest(hook.SessionID))
            _hookServer.Cancel(hook.SessionID);
        return changed;
    }

    /// <summary>
    /// opencode 事件统一入口：与 <see cref="AcceptClaudeHook"/> 同一个目的——
    /// 等待若已以别的方式结束（用户直接在终端里答复了权限），那条挂住的连接就该放掉：
    /// 不放的话插件那边的 socket 会一直悬到它自己的超时（10 分钟），而且手机再点一次
    /// 就是对着一个已经作废的询问答复 ✗。
    /// </summary>
    private bool AcceptOpenCodeHook(OpenCodeHookPayload hook)
    {
        // 插件的自诊断（插件侧 console.log 在 opencode 里看不到 ✗，真机踩过"点了没反应却查不到痕迹"的亏）：
        // 只落日志，**不进 catalog**（否则会凭空建出一行任务 ✗）。
        if (hook.Event == "plugin.note")
        {
            var note = hook.Properties is { } props && props.ValueKind == JsonValueKind.Object &&
                       props.TryGetProperty("note", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
            if (!string.IsNullOrWhiteSpace(note)) _log.Write("WARN", $"[OpenCode] 插件报告：{note}");
            return false;
        }

        var changed = _catalog.Accept(hook);

        // 释放挂住的连接**只认"等待真的结束了"的事件**，不能按"这一刻 catalog 里没有挂起项"来判 ✗：
        // opencode 的事件流很密（工具 part / status 成串来），任何一个把生命周期从等待态推走的事件
        // 都会让挂起项消失一瞬间，于是**刚挂上就被撤** —— 实测日志：挂起 3 毫秒后就被
        // "等待已在别处结束"撤掉，手机上点允许自然没反应 ✗。
        var sessionID = OpenCodeEventReducer.SessionID(hook);
        if (_hookServer is not null && sessionID is not null && _hookServer.HasPending(sessionID) &&
            hook.Event is "permission.replied" or "question.replied" or "question.rejected"
                or "plugin.dispose" or "session.deleted" or "session.idle" or "session.error")
            _hookServer.Cancel(sessionID);
        return changed;
    }

    /// <summary>
    /// agy 事件统一入口：与 <see cref="AcceptClaudeHook"/> 同一个目的——提问的等待若已以别的方式
    /// 结束（工具跑完 / 回合结束），那条被脚本 v3 挂住的连接就该放掉，否则脚本会一直吊到
    /// 它自己的 11 分钟上限。判据只认"等待真的结束了"的事件，不按"这一刻没有挂起项"来判
    /// （opencode 那里的教训：事件流一密，刚挂上就会被误撤）。
    /// </summary>
    private bool AcceptAgyHook(AgyHookPayload hook)
    {
        // 提问的作答入口只在"有手机连着"时才给：没有手机时桥并不挂住那条钩子（HookTcpServer
        // 同一判据），终端会自己弹框作答，此时卡片若还画着作答按钮就是假的。
        var changed = _catalog.Accept(hook, null, Volatile.Read(ref _phoneCount) > 0);
        if (_hookServer is not null && _hookServer.HasPending(hook.SessionID))
        {
            var isAskQuestion = hook.ToolCall?.Name?.Equals("ask_question", StringComparison.OrdinalIgnoreCase) == true;
            if (hook.EffectiveEvent == "Stop" || (hook.EffectiveEvent == "PostToolUse" && isAskQuestion))
                _hookServer.Cancel(hook.SessionID);
        }
        return changed;
    }

    private bool HandleCodexHook(CodexHookPayload hook)
    {
        _rollout.Include(hook);
        return _catalog.Accept(hook);
    }

    private async Task<bool> HandleControl(string text)
    {
        SignedControlEnvelope? request;
        try { request = JsonSerializer.Deserialize<SignedControlEnvelope>(text, WireJson.Options); }
        catch (JsonException) { return false; }
        if (request is null) return false;
        ControlAckPayload acknowledgment;
        try
        {
            _replayGuard.Validate(request, _pairingSecret);
            acknowledgment = _catalog.Perform(request.MessageId, request.Payload,
                _hookServer ?? throw new InvalidOperationException("Hook 通道尚未就绪"));
        }
        catch (ProtocolException error)
        {
            // 这条以前是静默的：App 那边只看到"没反应"，日志里一点痕迹都没有（实测踩过，
            // 手机作答因为签名编码不一致被拒，排查全靠猜）。失败原因必须落盘。
            _log.Write("WARN", $"控制消息被拒（{error.Failure}）：{request.Payload.Action} / 任务 {request.Payload.TaskID}");
            acknowledgment = new(request.MessageId, ControlResult.Rejected, "签名或序号无效");
        }
        // 未被接受的控制也要落盘（原因：签名之外，最常见的是"能力位不在/没有挂起卡"与
        // "Hook 审批通道已失效"）。这批以前全是静默的 ✗ —— App 上只看到"点了没反应"。
        if (acknowledgment.Result != ControlResult.Accepted)
            _log.Write("WARN", $"控制未被接受（{acknowledgment.Result}）：{request.Payload.Action} / 任务 {request.Payload.TaskID} / {acknowledgment.Reason}");
        var envelope = MessageEnvelope<ControlAckPayload>.Create("control.ack", acknowledgment);
        if (_lanServer is not null)
            await _lanServer.BroadcastAsync(JsonSerializer.Serialize(envelope, WireJson.Options));
        return acknowledgment.Result == ControlResult.Accepted;
    }

    private async Task PollLoop(CancellationToken cancellationToken)
    {
        var maintenanceCounter = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(750, cancellationToken);
                var signals = _rollout.Observe();
                if (signals.Count > 0) await _events.Writer.WriteAsync(new RolloutEvent(signals), cancellationToken);
                if (++maintenanceCounter >= 4)
                {
                    maintenanceCounter = 0;
                    await _events.Writer.WriteAsync(new MaintenanceEvent(), cancellationToken);
                }
            }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task UsageLoop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var codexTask = Task.Run(() => _usageLoader.Load(), cancellationToken);
                var agyTask = Task.Run(() => _agyUsageLoader.Load(), cancellationToken);
                var commandCodeTask = Task.Run(() => _commandCodeUsageLoader.Load(), cancellationToken);

                await Task.WhenAll(codexTask, agyTask, commandCodeTask);

                var codex = await codexTask;
                var agy = await agyTask;
                var commandCode = await commandCodeTask;

                var list = new List<UsageSnapshot>();
                if (agy is not null) list.Add(agy);
                if (commandCode is not null) list.Add(commandCode);
                if (codex is not null) list.Add(codex);

                var primary = agy ?? commandCode ?? codex;
                await _events.Writer.WriteAsync(new UsageEvent(primary, list), cancellationToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception error)
            {
                _log.Write("ERROR", $"读取额度失败：{error}");
            }

            try { await Task.Delay(TimeSpan.FromMinutes(10), cancellationToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void SaveAndBroadcast()
    {
        var projection = _catalog.Projection();
        try { _persistence.Save(projection.Tasks); }
        catch (IOException error)
        {
            _lastError = $"保存任务状态失败：{error.Message}";
            _log.Write("ERROR", _lastError);
        }
        PublishState();
        if (_lanServer is not null) _ = _lanServer.BroadcastAsync(SnapshotText());
    }

    private string PairingText() =>
        JsonSerializer.Serialize(new PairingPayload(
            1,
            $"agentgrid-{Environment.MachineName}",
            _selectedAddress,
            49362,
            Convert.ToBase64String(_pairingSecret)), WireJson.Options);

    private string SnapshotText()
    {
        var projection = _catalog.Projection();
        var tasks = projection.Tasks.Select(task => task with
        {
            LatestStep = ToolStepSanitizer.Sanitize(task.LatestStep),
            Subagents = task.Subagents.Select(subagent =>
                subagent with { LatestStep = ToolStepSanitizer.Sanitize(subagent.LatestStep) }).ToList(),
        }).ToList();
        var payload = new StateSnapshotPayload(tasks, _usage, projection.FocusedTaskID, projection.PendingRequests, _usages.Count > 0 ? _usages : null);
        return JsonSerializer.Serialize(MessageEnvelope<StateSnapshotPayload>.Create("state.snapshot", payload), WireJson.Options);
    }

    private BridgeViewState CreateState()
    {
        var pluginStatus = _openCodePluginConfiguration.Status;
        var pluginVersion = OpenCodePluginConfiguration.EmbeddedVersion;
        var installedVersion = _openCodePluginConfiguration.InstalledVersion;
        var pluginText = pluginStatus switch
        {
            OpenCodePluginStatus.NotInstalled => "未安装",
            OpenCodePluginStatus.Outdated => installedVersion is int installed
                ? $"待更新 v{installed} → v{pluginVersion}"
                : $"待更新 → v{pluginVersion}",
            _ => $"已写入 v{pluginVersion}",
        };
        var claudeStatus = _claudeHookConfiguration.Status;
        var claudeVersion = ClaudeHookConfiguration.EmbeddedVersion;
        var claudeInstalledVersion = _claudeHookConfiguration.InstalledVersion;
        var claudeText = claudeStatus switch
        {
            ClaudeHookStatus.NotInstalled => "未安装",
            ClaudeHookStatus.Outdated => claudeInstalledVersion is int installed
                ? $"待更新 v{installed} → v{claudeVersion}"
                : $"待更新 → v{claudeVersion}",
            _ => $"已写入 v{claudeVersion}",
        };
        var agyStatus = _agyHookConfiguration.Status;
        var agyVersion = AgyHookConfiguration.EmbeddedVersion;
        var agyInstalledVersion = _agyHookConfiguration.InstalledVersion;
        var agyText = agyStatus switch
        {
            AntigravityPluginStatus.NotInstalled => "未安装",
            AntigravityPluginStatus.Outdated => agyInstalledVersion is int installed
                ? $"待更新 v{installed} → v{agyVersion}"
                : $"待更新 → v{agyVersion}",
            _ => $"已写入 v{agyVersion}",
        };
        return new(
            _serviceStatus,
            _phoneCount,
            _hookConfiguration.IsInstalled(),
            claudeText,
            claudeStatus,
            pluginText,
            pluginStatus,
            _lastHookAt,
            _lastError,
            PairingText(),
            _selectedAddress,
            _networks,
            _catalog.Projection().Tasks.Count,
            agyText,
            agyStatus);
    }

    private void PublishState()
    {
        State = CreateState();
        StateChanged?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        _linkedCancellation?.Cancel();
        if (_pollTask is not null) try { await _pollTask; } catch (OperationCanceledException) { }
        if (_usageTask is not null) try { await _usageTask; } catch (OperationCanceledException) { }
        if (_hookServer is not null) await _hookServer.DisposeAsync();
        if (_lanServer is not null) await _lanServer.DisposeAsync();
        _events.Writer.TryComplete();
        if (_eventTask is not null) try { await _eventTask; } catch (OperationCanceledException) { }
        _linkedCancellation?.Dispose();
    }
}
