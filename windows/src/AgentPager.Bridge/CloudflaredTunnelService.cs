using System.Diagnostics;
using AgentPager.Core;

namespace AgentPager.Bridge;

/// <summary>
/// Cloudflare Quick Tunnel（trycloudflare.com）的进程托管。
///
/// 与 ngrok 的关键差异：
/// 1. 公网域名只出现在 **stderr 日志**里，没有 4040 那样的本地 JSON API，所以必须逐行读 stderr；
/// 2. 每次进程重启都会换域名，和 ngrok 免费版一致；
/// 3. 不需要账号、域名或 API token —— 这也是选它替代 ngrok 的原因（无 1GB 流量上限）。
/// </summary>
public sealed class CloudflaredTunnelService : ITunnelService
{
    private readonly DailyFileLog _log;
    private readonly Action<string?> _onHostAssigned;
    private readonly Action<TunnelStatus, string?> _onStatusChanged;
    private readonly object _gate = new();

    private Process? _process;
    private CancellationTokenSource? _cts;
    private string? _activeHost;
    private TunnelStatus _status = TunnelStatus.Stopped;
    private ChildProcessJob? _job;

    /// <summary>我们自己起的子进程名（收尸时的保险丝，见 <see cref="ChildProcessLease"/>）。</summary>
    private const string ChildProcessName = "cloudflared";

    /// <summary>上一次起的 cloudflared PID 落在这里：强杀桥会留孤儿，下次启动先按它收尸。</summary>
    private static string ChildPidFilePath => Path.Combine(AgentPagerPaths.DataRoot, "cloudflared.pid");

    /// <summary>等公网域名的最长时间。cloudflared 启动前有一轮连通性预检，30 秒足够。</summary>
    private static readonly TimeSpan HostWaitLimit = TimeSpan.FromSeconds(30);

    public TunnelStatus Status => _status;
    public string? ActiveHost => _activeHost;

    public CloudflaredTunnelService(
        DailyFileLog log,
        Action<string?> onHostAssigned,
        Action<TunnelStatus, string?> onStatusChanged)
    {
        _log = log;
        _onHostAssigned = onHostAssigned;
        _onStatusChanged = onStatusChanged;
    }

    public static string? FindCloudflaredExecutable()
    {
        // 1. 用户目录 bin（与 ngrok.exe 同位置）
        var userBin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "bin", "cloudflared.exe");
        if (File.Exists(userBin)) return userBin;

        // 2. PATH 环境变量
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, "cloudflared.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }
        return null;
    }

    public Task StartAsync()
    {
        lock (_gate)
        {
            if (_status is TunnelStatus.Running or TunnelStatus.Starting)
                return Task.CompletedTask;

            _status = TunnelStatus.Starting;
        }
        _onStatusChanged(TunnelStatus.Starting, "正在检测与启动 cloudflared...");

        var cloudflaredExe = FindCloudflaredExecutable();
        if (cloudflaredExe is null)
        {
            lock (_gate)
            {
                _status = TunnelStatus.NotFound;
            }
            _onStatusChanged(TunnelStatus.NotFound, "未找到 cloudflared.exe（请安装并加入 PATH 或用户目录 bin）");
            return Task.CompletedTask;
        }

        try
        {
            // 强杀宿主留下的孤儿会占着 Quick Tunnel 的并发名额，先收尸再启动。
            ChildProcessLease.ReapPrevious(ChildPidFilePath, ChildProcessName, _log.Write);

            var psi = new ProcessStartInfo
            {
                FileName = cloudflaredExe,
                // --protocol http2：国内部分运营商对 QUIC/UDP 7844 做 QoS，实测会出现
                // 一个 region 通、另一个不通；固定走 TCP 443 最稳。
                // --no-autoupdate：别让 cloudflared 在运行时替换自己的 exe。
                Arguments = $"tunnel --no-autoupdate --url http://127.0.0.1:{LanBridgeServer.PhonePort} --protocol http2",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            // 与 ngrok 同理：代理环境变量会让 cloudflared 的出口也绕一圈，直接剥掉。
            psi.Environment.Remove("HTTP_PROXY");
            psi.Environment.Remove("HTTPS_PROXY");
            psi.Environment.Remove("http_proxy");
            psi.Environment.Remove("https_proxy");

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            CancellationTokenSource cts;
            lock (_gate)
            {
                _cts?.Cancel();
                _cts?.Dispose();
                _cts = new CancellationTokenSource();
                cts = _cts;
            }
            var token = cts.Token;

            process.Exited += (_, _) =>
            {
                TunnelStatus newStatus = TunnelStatus.Stopped;
                int exitCode = 0;
                bool isCurrent = false;
                string? logMessage = null;
                lock (_gate)
                {
                    if (_process == process)
                    {
                        isCurrent = true;
                        exitCode = process.ExitCode;
                        _status = exitCode == 0 ? TunnelStatus.Stopped : TunnelStatus.Failed;
                        _activeHost = null;
                        newStatus = _status;
                        logMessage = $"cloudflared 进程已退出，退出码：{exitCode}";
                    }
                }
                if (isCurrent)
                {
                    _log.Write("WARN", logMessage!);
                    _onStatusChanged(newStatus, $"cloudflared 进程已退出 (代码 {exitCode})");
                }
            };

            if (!process.Start())
            {
                lock (_gate)
                {
                    _status = TunnelStatus.Failed;
                }
                _onStatusChanged(TunnelStatus.Failed, "启动 cloudflared 进程失败");
                return Task.CompletedTask;
            }

            lock (_gate)
            {
                _process = process;
            }
            _log.Write("INFO", $"已拉起 cloudflared 隧道守护进程 PID={process.Id}，使用可执行文件：{cloudflaredExe}");

            // 绑进 Job Object：本进程一死（含强杀）系统立刻带走它——孤儿从此不再产生。
            _job?.Dispose();
            _job = ChildProcessJob.TryCreate(_log.Write);
            _job?.TryAssign(process, _log.Write);
            ChildProcessLease.Remember(ChildPidFilePath, process.Id);

            _ = Task.Run(() => MonitorOutputAsync(process, token), token);
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _status = TunnelStatus.Failed;
            }
            _onStatusChanged(TunnelStatus.Failed, $"启动失败：{ex.Message}");
            _log.Write("ERROR", $"启动 cloudflared 异常：{ex}");
        }

        return Task.CompletedTask;
    }

    private async Task MonitorOutputAsync(Process process, CancellationToken token)
    {
        // stdout 也要排空：Quick Tunnel 的域名在 stderr，但留下一个没人读的管道，
        // 万一 cloudflared 往 stdout 写东西就会把子进程堵死。
        _ = Task.Run(() => DrainAsync(process.StandardOutput, token), token);
        _ = Task.Run(() => WatchStartupAsync(process, token), token);

        try
        {
            while (!token.IsCancellationRequested && !process.HasExited)
            {
                var line = await process.StandardError.ReadLineAsync(token);
                if (line is null) break;

                var host = CloudflaredLog.TryParseQuickTunnelHost(line);
                if (host is not null)
                {
                    UpdateRunningHost(host);
                }
                else if (line.Contains(" ERR ", StringComparison.Ordinal))
                {
                    // 预检表里的正常信息不记；只留错误行，出问题时能从日志里看到原因。
                    _log.Write("DEBUG", $"cloudflared: {line}");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.Write("DEBUG", $"cloudflared stderr 监听异常：{ex.Message}");
        }
    }

    private static async Task DrainAsync(StreamReader reader, CancellationToken token)
    {
        try
        {
            while (await reader.ReadLineAsync(token) is not null) { }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }
    }

    /// <summary>
    /// 兜底：cloudflared 进程还活着却一直不给域名（被墙、出口被 QoS、Quick Tunnel 服务异常）。
    /// 到点标 Failed 让用户看见；之后若域名才到，UpdateRunningHost 会把状态翻回 Running。
    /// </summary>
    private async Task WatchStartupAsync(Process process, CancellationToken token)
    {
        try
        {
            var deadline = DateTime.UtcNow + HostWaitLimit;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(500, token);
                lock (_gate)
                {
                    if (_process != process || _status != TunnelStatus.Starting) return;
                }
            }

            lock (_gate)
            {
                if (_process != process || _status != TunnelStatus.Starting) return;
                _status = TunnelStatus.Failed;
            }
            _log.Write("WARN", $"cloudflared {HostWaitLimit.TotalSeconds:0} 秒内没有给出 trycloudflare 域名");
            _onStatusChanged(TunnelStatus.Failed, $"{HostWaitLimit.TotalSeconds:0} 秒内未获得公网域名（检查网络/代理）");
        }
        catch (OperationCanceledException) { }
    }

    private void UpdateRunningHost(string host)
    {
        bool changed = false;
        lock (_gate)
        {
            if (_status != TunnelStatus.Running || _activeHost != host)
            {
                _status = TunnelStatus.Running;
                _activeHost = host;
                changed = true;
                _log.Write("INFO", $"cloudflared 隧道就绪，公网域名：{host}");
            }
        }
        if (changed)
        {
            _onHostAssigned(host);
            _onStatusChanged(TunnelStatus.Running, $"在线：{host}");
        }
    }

    public async Task StopAsync()
    {
        Process? procToKill;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _cts;
            _cts = null;
            procToKill = _process;
            _process = null;
            _activeHost = null;
            _status = TunnelStatus.Stopped;
        }

        cts?.Cancel();
        cts?.Dispose();
        _onStatusChanged(TunnelStatus.Stopped, "已停止");

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
                _log.Write("WARN", $"终止 cloudflared 进程异常：{ex.Message}");
            }
        }

        // 正常收尾：job 关掉（成员子进程随之收尾）并销账，别让下次启动白杀一个已复用的 PID。
        _job?.Dispose();
        _job = null;
        ChildProcessLease.Forget(ChildPidFilePath);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
