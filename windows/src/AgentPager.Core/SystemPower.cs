using System.Diagnostics;

namespace AgentPager.Core;

/// <summary>
/// 本机电源操作（目前只有手机端"关闭计算机"在用）。
/// 跑在用户自己的 Windows 会话里，直接调 shutdown.exe——普通登录用户自带本机关机权限，
/// 不需要提权。延迟执行（默认 60 秒）是唯一的反悔窗口：误触后在电脑上 `shutdown /a` 取消。
/// </summary>
public static class SystemPower
{
    public static Func<ProcessStartInfo, Process?> Starter { get; set; } = Process.Start;

    public static int ShutdownDelaySeconds = 60;

    public static bool Shutdown(int delaySeconds, Action<string, string>? log = null)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "shutdown",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("/s");
            startInfo.ArgumentList.Add("/t");
            startInfo.ArgumentList.Add(delaySeconds.ToString());
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add("AgentPager remote shutdown");
            Starter(startInfo);
            log?.Invoke("WARN", $"[System] 已下发关机指令（{delaySeconds} 秒后关机，电脑上 shutdown /a 可取消）");
            return true;
        }
        catch (Exception error)
        {
            log?.Invoke("WARN", $"[System] 关机指令启动失败：{error.Message}");
            return false;
        }
    }
}
