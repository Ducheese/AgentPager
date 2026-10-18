using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using AgentPager.Core;

namespace AgentPager.Bridge;

public enum NgrokTunnelStatus
{
    Stopped,
    Starting,
    Running,
    Failed,
    NotFound,
}

public sealed class NgrokTunnelService : IAsyncDisposable
{
    private readonly DailyFileLog _log;
    private readonly Action<string?> _onHostAssigned;
    private readonly Action<NgrokTunnelStatus, string?> _onStatusChanged;
    private readonly object _gate = new();

    private Process? _process;
    private CancellationTokenSource? _cts;
    private string? _activeHost;
    private NgrokTunnelStatus _status = NgrokTunnelStatus.Stopped;

    public NgrokTunnelStatus Status => _status;
    public string? ActiveHost => _activeHost;

    public NgrokTunnelService(
        DailyFileLog log,
        Action<string?> onHostAssigned,
        Action<NgrokTunnelStatus, string?> onStatusChanged)
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
        lock (_gate)
        {
            if (_status is NgrokTunnelStatus.Running or NgrokTunnelStatus.Starting)
                return Task.CompletedTask;

            _status = NgrokTunnelStatus.Starting;
        }
        _onStatusChanged(NgrokTunnelStatus.Starting, "正在检测与启动 ngrok...");

        var ngrokExe = FindNgrokExecutable();
        if (ngrokExe is null)
        {
            lock (_gate)
            {
                _status = NgrokTunnelStatus.NotFound;
            }
            _onStatusChanged(NgrokTunnelStatus.NotFound, "未找到 ngrok.exe（请安装并加入 PATH 或用户目录 bin）");
            return Task.CompletedTask;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ngrokExe,
                Arguments = "http 49362 --log stdout --log-format json",
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
                NgrokTunnelStatus newStatus = NgrokTunnelStatus.Stopped;
                int exitCode = 0;
                bool isCurrent = false;
                string? logMessage = null;
                lock (_gate)
                {
                    if (_process == process)
                    {
                        isCurrent = true;
                        exitCode = process.ExitCode;
                        _status = exitCode == 0 ? NgrokTunnelStatus.Stopped : NgrokTunnelStatus.Failed;
                        _activeHost = null;
                        newStatus = _status;
                        logMessage = $"ngrok 进程已退出，退出码：{exitCode}";
                    }
                }
                if (isCurrent)
                {
                    _log.Write("WARN", logMessage!);
                    _onStatusChanged(newStatus, $"ngrok 进程已退出 (代码 {exitCode})");
                }
            };

            if (!process.Start())
            {
                lock (_gate)
                {
                    _status = NgrokTunnelStatus.Failed;
                }
                _onStatusChanged(NgrokTunnelStatus.Failed, "启动 ngrok 进程失败");
                return Task.CompletedTask;
            }

            lock (_gate)
            {
                _process = process;
            }
            _log.Write("INFO", $"已拉起 ngrok 隧道守护进程 PID={process.Id}，使用可执行文件：{ngrokExe}");

            _ = Task.Run(() => MonitorOutputAsync(process, token), token);
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _status = NgrokTunnelStatus.Failed;
            }
            _onStatusChanged(NgrokTunnelStatus.Failed, $"启动失败：{ex.Message}");
            _log.Write("ERROR", $"启动 ngrok 异常：{ex}");
        }

        return Task.CompletedTask;
    }

    private async Task MonitorOutputAsync(Process process, CancellationToken token)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested && !process.HasExited)
                {
                    var line = await process.StandardOutput.ReadLineAsync(token);
                    if (line is null) break;

                    if (line.Contains("\"started tunnel\"") || line.Contains("\"url\""))
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(line);
                            if (doc.RootElement.TryGetProperty("url", out var urlProp))
                            {
                                var rawUrl = urlProp.GetString();
                                var normalized = PairingPayload.NormalizeTunnelHost(rawUrl);
                                if (!string.IsNullOrEmpty(normalized))
                                {
                                    UpdateRunningHost(normalized);
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _log.Write("DEBUG", $"ngrok stdout 监听异常：{ex.Message}");
            }
        }, token);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        try
        {
            for (var i = 0; i < 20 && !token.IsCancellationRequested && !process.HasExited; i++)
            {
                lock (_gate)
                {
                    if (_status == NgrokTunnelStatus.Running) break;
                }
                await Task.Delay(500, token);
                try
                {
                    var json = await http.GetStringAsync("http://127.0.0.1:4040/api/tunnels", token);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("tunnels", out var tunnels) && tunnels.GetArrayLength() > 0)
                    {
                        var first = tunnels[0];
                        if (first.TryGetProperty("public_url", out var pubUrl))
                        {
                            var normalized = PairingPayload.NormalizeTunnelHost(pubUrl.GetString());
                            if (!string.IsNullOrEmpty(normalized))
                            {
                                UpdateRunningHost(normalized);
                                break;
                            }
                        }
                    }
                }
                catch { }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.Write("DEBUG", $"ngrok 4040 轮询异常：{ex.Message}");
        }
    }

    private void UpdateRunningHost(string host)
    {
        bool changed = false;
        lock (_gate)
        {
            if (_status != NgrokTunnelStatus.Running || _activeHost != host)
            {
                _status = NgrokTunnelStatus.Running;
                _activeHost = host;
                changed = true;
                _log.Write("INFO", $"ngrok 隧道就绪，公网域名：{host}");
            }
        }
        if (changed)
        {
            _onHostAssigned(host);
            _onStatusChanged(NgrokTunnelStatus.Running, $"在线：{host}");
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
            _status = NgrokTunnelStatus.Stopped;
        }

        cts?.Cancel();
        cts?.Dispose();
        _onStatusChanged(NgrokTunnelStatus.Stopped, "已停止");

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
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
