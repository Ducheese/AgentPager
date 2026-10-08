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
    private sealed record UsageEvent(UsageSnapshot? Usage) : BridgeEvent;
    private sealed record MaintenanceEvent : BridgeEvent;

    private readonly DailyFileLog _log = new();
    private readonly Channel<BridgeEvent> _events = Channel.CreateUnbounded<BridgeEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly TaskSnapshotPersistence _persistence = new();
    private readonly CodexSessionTitleReader _titleReader = new();
    private readonly CodexRolloutObserver _rollout = new();
    private readonly CodexUsageLoader _usageLoader = new();
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
                envelope => _events.Writer.WriteAsync(new HookEvent(envelope), token).AsTask(), _log);
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
                            HookEnvelope.OpenCode openCode => _catalog.Accept(openCode.Hook),
                            HookEnvelope.Antigravity agy => _catalog.Accept(agy.Hook),
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
                        changed = true;
                        break;
                    case MaintenanceEvent:
                        changed = _catalog.Maintain();
                        changed = _catalog.ApplyTitles(_titleReader.Load()) || changed;
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
                var usage = await Task.Run(() => _usageLoader.Load(), cancellationToken);
                await _events.Writer.WriteAsync(new UsageEvent(usage), cancellationToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception error)
            {
                _log.Write("ERROR", $"读取 Codex 额度失败：{error}");
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
        var payload = new StateSnapshotPayload(tasks, _usage, projection.FocusedTaskID, projection.PendingRequests);
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
