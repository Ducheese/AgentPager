using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using AgentPager.Core;

namespace AgentPager.Bridge;

public sealed class NgrokTunnelService : ITunnelService
{
    private readonly DailyFileLog _log;
    private readonly Action<string?> _onHostAssigned;
    private readonly Action<TunnelStatus, string?> _onStatusChanged;
    private readonly object _gate = new();
    private readonly object _statusGate = new();

    private Process? _process;
    private CancellationTokenSource? _cts;
    private string? _activeHost;
    private TunnelStatus _status = TunnelStatus.Stopped;
    private ChildProcessJob? _job;
    /// <summary>本轮 attempt 是否成功拿到过域名；成功后 backoff 归零。</summary>
    private bool _runReachedRunning;

    /// <summary>我们自己起的子进程名（收尸时的保险丝，见 <see cref="ChildProcessLease"/>）。</summary>
    private const string ChildProcessName = "ngrok";

    /// <summary>上一次起的 ngrok PID 落在这里：强杀桥会留孤儿，下次启动先按它收尸。</summary>
    private static string ChildPidFilePath => Path.Combine(AgentPagerPaths.DataRoot, "ngrok.pid");

    /// <summary>
    /// 等公网域名的最长时间。开机自启时 ngrok 可能先于网络/代理就绪，到点没域名就杀掉重来，
    /// 不能像旧实现那样让进程半死不活地挂着。
    /// </summary>
    private static readonly TimeSpan HostWaitLimit = TimeSpan.FromSeconds(30);

    /// <summary>测试注入：绕过真实 exe 查找/进程，只验证 supervisor 状态机。</summary>
    internal static Func<string?>? ExecutableOverride { get; set; }
    internal static Func<string, bool, CancellationToken, Task<string?>>? RunOnceOverride { get; set; }
    internal static Func<TimeSpan, CancellationToken, Task>? RetryDelayOverride { get; set; }

    internal CancellationTokenSource? CurrentOwnerForTest => _cts;
    internal static TimeSpan RetryDelayForTest(int attempt) => RetryDelay(attempt);
    internal void SetStatusForTest(TunnelStatus status, string message, CancellationTokenSource owner) =>
        SetStatus(status, message, owner);
    internal void MarkReachedRunningForTest() => _runReachedRunning = true;

    public TunnelStatus Status => _status;
    public string? ActiveHost => _activeHost;

    public NgrokTunnelService(
        DailyFileLog log,
        Action<string?> onHostAssigned,
        Action<TunnelStatus, string?> onStatusChanged)
    {
        _log = log;
        _onHostAssigned = onHostAssigned;
        _onStatusChanged = onStatusChanged;
    }

    public static string? FindNgrokExecutable()
    {
        // 1. 用户自定义或用户目录 bin
        var userBin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "bin", "ngrok.exe");
        if (File.Exists(userBin)) return userBin;

        // 2. PATH 环境变量
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, "ngrok.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }
        return null;
    }

    public Task StartAsync()
    {
        CancellationTokenSource cts;
        CancellationToken token;
        lock (_gate)
        {
            // Retrying 也算"已经在跑"：重试循环还活着，不能再开一条。
            if (_status is TunnelStatus.Running or TunnelStatus.Starting or TunnelStatus.Retrying)
                return Task.CompletedTask;

            _cts?.Cancel();
            _cts?.Dispose();
            cts = new CancellationTokenSource();
            _cts = cts;
            token = cts.Token;
            _status = TunnelStatus.Starting;
            _activeHost = null;
        }

        PublishStatus(TunnelStatus.Starting, "正在检测与启动 ngrok...", cts);
        _ = Task.Run(() => SuperviseAsync(cts, token), token);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 启动 + 守护循环：任何一次启动失败（进程立刻退出、30 秒拿不到域名）都退避后重来，
    /// 直到 StopAsync / 切换提供方取消。开机自启时网络或代理晚于桥就绪是常态，
    /// 旧实现失败一次就永久摆烂，用户得手动去切一遍隧道。
    /// </summary>
    private async Task SuperviseAsync(CancellationTokenSource owner, CancellationToken token)
    {
        var attempt = 0;
        var firstRun = true;
        while (!token.IsCancellationRequested)
        {
            var ngrokExe = ExecutableOverride?.Invoke() ?? FindNgrokExecutable();
            if (ngrokExe is null)
            {
                SetStatus(TunnelStatus.NotFound, "未找到 ngrok.exe（请安装并加入 PATH 或用户目录 bin）", owner);
                return;
            }

            if (attempt > 0)
                SetStatus(TunnelStatus.Starting, $"第 {attempt + 1} 次启动 ngrok...", owner);

            _runReachedRunning = false;
            var failure = RunOnceOverride is { } runOnce
                ? await runOnce(ngrokExe, firstRun, token)
                : await RunOnceAsync(ngrokExe, firstRun, token);
            firstRun = false;
            if (token.IsCancellationRequested) return;

            // 这次至少跑到过 Running：下一次进程退出重新从 5 秒退避开始。
            if (_runReachedRunning) attempt = 0;
            if (failure is null) return;

            attempt++;
            var delay = RetryDelay(attempt);
            SetStatus(TunnelStatus.Retrying,
                $"启动失败：{failure}。{delay.TotalSeconds:0} 秒后自动重试（第 {attempt} 次）", owner);
            _log.Write("WARN", $"ngrok {failure}，{delay.TotalSeconds:0} 秒后重试（第 {attempt} 次）");

            try
            {
                if (RetryDelayOverride is { } delayStub) await delayStub(delay, token);
                else await Task.Delay(delay, token);
            }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>5s / 10s / 20s / 40s / 60s，封顶 60 秒——别把免费版单会话名额打成限流。</summary>
    private static TimeSpan RetryDelay(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(60, 5 * Math.Pow(2, Math.Min(attempt - 1, 4))));

    /// <summary>
    /// 跑一次进程。正常情况会一直挂到进程退出（返回退出原因）或被 Stop 取消（返回 null）。
    /// </summary>
    private async Task<string?> RunOnceAsync(string ngrokExe, bool firstAttempt, CancellationToken token)
    {
        Process? process = null;
        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            // 上一次若被强杀（Task Manager / Stop-Process）会留下孤儿 ngrok：它还占着 ngrok 免费版
            // 的单会话名额与保留域名，害我们这次起的 ngrok 被云端拒绝（退出码 1）。先收尸再启动。
            if (firstAttempt)
                ChildProcessLease.ReapPrevious(ChildPidFilePath, ChildProcessName, _log.Write);

            var psi = new ProcessStartInfo
            {
                FileName = ngrokExe,
                Arguments = $"http {LanBridgeServer.PhonePort} --log stdout --log-format json",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = false,
                CreateNoWindow = true,
            };

            // 核心排坑：剥离代理环境变量，规避免费版 ERR_NGROK_9009 报错拒绝启动
            psi.Environment.Remove("HTTP_PROXY");
            psi.Environment.Remove("HTTPS_PROXY");
            psi.Environment.Remove("http_proxy");
            psi.Environment.Remove("https_proxy");

            process = new Process { StartInfo = psi };
            if (!process.Start())
                return "启动 ngrok 进程失败";

            lock (_gate)
            {
                // StopAsync 先把 _cts 置空；不能在 Stop 之后再把新进程登记进来。
                if (_cts is null || token.IsCancellationRequested) return null;
                _process = process;
            }
            _log.Write("INFO", $"已拉起 ngrok 隧道守护进程 PID={process.Id}，使用可执行文件：{ngrokExe}");

            // 绑进 Job Object：本进程一死（含强杀）系统立刻带走它——孤儿从此不再产生。
            // 重试时复用同一个 job，别每次重建。_job 与 _process 一样受 _gate 保护，
            // 否则 StopAsync 可能刚好 Dispose 掉新一轮刚创建的 job。
            lock (_gate)
            {
                if (_cts is null || token.IsCancellationRequested) return null;
                _job ??= ChildProcessJob.TryCreate(_log.Write);
                _job?.TryAssign(process, _log.Write);
                // PID 帐也要和 _cts 同一把锁，避免 StopAsync 的 Forget 被这次 Remember 覆盖。
                ChildProcessLease.Remember(ChildPidFilePath, process.Id);
            }

            var hostSource = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = Task.Run(() => ReadStdoutAsync(process, hostSource, attemptCts.Token), attemptCts.Token);
            _ = Task.Run(() => PollTunnelsApiAsync(hostSource, attemptCts.Token), attemptCts.Token);
            var exitTask = process.WaitForExitAsync(attemptCts.Token);

            var host = await WaitForHostAsync(hostSource.Task, exitTask, token);
            if (token.IsCancellationRequested) return null;

            if (host is null)
            {
                if (process.HasExited)
                    return $"进程已退出 (代码 {SafeExitCode(process)})";
                return $"{HostWaitLimit.TotalSeconds:0} 秒内未获得公网域名";
            }

            UpdateRunningHost(host, process);
            _runReachedRunning = true;

            try { await exitTask; }
            catch (OperationCanceledException) { return null; }
            if (token.IsCancellationRequested) return null;
            return $"进程已退出 (代码 {SafeExitCode(process)})";
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _log.Write("ERROR", $"启动 ngrok 异常：{ex}");
            return ex.Message;
        }
        finally
        {
            attemptCts.Cancel();
            if (process is not null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(2000);
                    }
                }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException or AggregateException)
                {
                    _log.Write("DEBUG", $"收掉 ngrok 进程异常：{error.Message}");
                }
                lock (_gate)
                {
                    if (_process == process) _process = null;
                }
                process.Dispose();
            }
        }
    }

    private static async Task<string?> WaitForHostAsync(
        Task<string> hostTask, Task exitTask, CancellationToken token)
    {
        // 进程退出 / 拿到域名 / 超时，谁先发生算谁。超时由调用方负责杀进程。
        var timeout = Task.Delay(HostWaitLimit, token);
        var completed = await Task.WhenAny(hostTask, exitTask, timeout);
        return completed == hostTask ? await hostTask : null;
    }

    private async Task ReadStdoutAsync(Process process, TaskCompletionSource<string> hostSource, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var line = await process.StandardOutput.ReadLineAsync(token);
                if (line is null) break;
                if (!line.Contains("\"url\"", StringComparison.Ordinal)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (!IsOwnTunnel(doc.RootElement)) continue;
                    if (doc.RootElement.TryGetProperty("url", out var urlProp))
                    {
                        var normalized = PairingPayload.NormalizeTunnelHost(urlProp.GetString());
                        if (!string.IsNullOrEmpty(normalized)) hostSource.TrySetResult(normalized);
                    }
                }
                catch { }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.Write("DEBUG", $"ngrok stdout 监听异常：{ex.Message}");
        }
    }

    private async Task PollTunnelsApiAsync(
        TaskCompletionSource<string> hostSource, CancellationToken token)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        while (!token.IsCancellationRequested && !hostSource.Task.IsCompleted)
        {
            try
            {
                var json = await http.GetStringAsync("http://127.0.0.1:4040/api/tunnels", token);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("tunnels", out var tunnels))
                {
                    // 4040 是 ngrok 的**全局** API：机器上还有别人的（或我们上一次留下的孤儿）
                    // 隧道时，必须只认转发到我们自己端口的那一条。
                    var first = tunnels.EnumerateArray().FirstOrDefault(IsOwnTunnel);
                    if (first.ValueKind == JsonValueKind.Object && first.TryGetProperty("public_url", out var pubUrl))
                    {
                        var normalized = PairingPayload.NormalizeTunnelHost(pubUrl.GetString());
                        if (!string.IsNullOrEmpty(normalized))
                        {
                            hostSource.TrySetResult(normalized);
                            return;
                        }
                    }
                }
            }
            catch (OperationCanceledException) { return; }
            catch { }

            try { await Task.Delay(1000, token); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// 这条隧道是不是我们自己的？ngrok 的 4040 API 是**全局**的：机器上还存在别人（或我们上一次
    /// 留下的孤儿）的 ngrok 时，会把它的隧道当成我们的报上去——2026-10-19 实测就是这样报出
    /// "子进程退出码 1"却又"已获取公网域名"的混合状态。判据是隧道配置里的转发目标必须是我们
    /// 绑定的手机端口；拿不到 addr 就不挑剔（老版本 ngrok 不暴露）。
    /// </summary>
    private static bool IsOwnTunnel(JsonElement tunnel)
    {
        string? address = null;
        if (tunnel.ValueKind == JsonValueKind.Object)
        {
            if (tunnel.TryGetProperty("addr", out var topLevel)) address = topLevel.GetString();
            else if (tunnel.TryGetProperty("config", out var config) && config.TryGetProperty("addr", out var nested)) address = nested.GetString();
        }
        return string.IsNullOrEmpty(address) || address.Contains(LanBridgeServer.PhonePort.ToString(), StringComparison.Ordinal);
    }

    private void UpdateRunningHost(string host, Process process)
    {
        bool changed;
        lock (_gate)
        {
            if (_process != process) return;
            changed = _status != TunnelStatus.Running || _activeHost != host;
            _status = TunnelStatus.Running;
            _activeHost = host;
        }
        if (!changed) return;

        lock (_statusGate)
        {
            lock (_gate)
            {
                if (_process != process || _status != TunnelStatus.Running || _activeHost != host)
                    return;
            }
            SafeLog("INFO", $"ngrok 隧道就绪，公网域名：{host}");
            SafeNotifyHost(host);
            SafeNotifyStatus(TunnelStatus.Running, $"在线：{host}");
        }
    }

    private void SetStatus(TunnelStatus status, string message, CancellationTokenSource owner)
    {
        lock (_gate)
        {
            // StopAsync 会先把 _cts 置空；仍拿着旧 owner 的 supervisor 不能再写状态。
            if (!ReferenceEquals(_cts, owner) || owner.IsCancellationRequested) return;
            _status = status;
            if (status != TunnelStatus.Running) _activeHost = null;
        }
        PublishStatus(status, message, owner);
    }

    private void PublishStatus(TunnelStatus status, string message, CancellationTokenSource? owner)
    {
        // 状态已写入 _gate；发布也串行化，避免旧 supervisor 的 Retrying 落在 Stop 的 Stopped 之后。
        lock (_statusGate)
        {
            lock (_gate)
            {
                if (owner is not null)
                {
                    if (!ReferenceEquals(_cts, owner)) return;
                }
                else if (_status != status)
                {
                    return;
                }
            }
            SafeNotifyStatus(status, message);
        }
    }

    private void SafeNotifyHost(string host)
    {
        try { _onHostAssigned(host); }
        catch (Exception ex) { SafeLog("WARN", $"ngrok 域名回调异常：{ex.Message}"); }
    }

    private void SafeNotifyStatus(TunnelStatus status, string message)
    {
        try { _onStatusChanged(status, message); }
        catch (Exception ex) { SafeLog("WARN", $"ngrok 状态回调异常：{ex.Message}"); }
    }

    private void SafeLog(string level, string message)
    {
        try { _log.Write(level, message); }
        catch { }
    }

    private static int SafeExitCode(Process process)
    {
        try { return process.ExitCode; }
        catch { return -1; }
    }

    public async Task StopAsync()
    {
        Process? procToKill;
        CancellationTokenSource? cts;
        ChildProcessJob? jobToDispose;
        lock (_gate)
        {
            cts = _cts;
            _cts = null;
            procToKill = _process;
            _process = null;
            jobToDispose = _job;
            _job = null;
            _activeHost = null;
            _status = TunnelStatus.Stopped;
        }

        cts?.Cancel();
        cts?.Dispose();
        PublishStatus(TunnelStatus.Stopped, "未启用", null);

        if (procToKill is not null)
        {
            try
            {
                if (!procToKill.HasExited)
                {
                    procToKill.Kill(entireProcessTree: true);
                    await procToKill.WaitForExitAsync();
                }
                procToKill.Dispose();
            }
            catch (Exception ex)
            {
                _log.Write("WARN", $"终止 ngrok 进程异常：{ex.Message}");
            }
        }

        // 正常收尾：job 关掉（成员子进程随之收尾）并销账，别让下次启动白杀一个已复用的 PID。
        jobToDispose?.Dispose();
        ChildProcessLease.Forget(ChildPidFilePath);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
