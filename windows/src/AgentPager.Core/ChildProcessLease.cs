using System.Diagnostics;
using System.Text;

namespace AgentPager.Core;

/// <summary>
/// 子进程租约：把"我起的这个子进程"记进一个小 pid 文件，下次启动先来收尸。
///
/// 为什么需要它 —— Job Object（<see cref="ChildProcessJob"/>）管的是**本次**运行：本进程一死，
/// 成员子进程跟着死。但本修复上线**之前**留下的孤儿收不回来（2026-10-19 实测：ngrok 13308 的
/// 父进程是已被强杀的 v92 桥，它还占着免费版的单会话名额），所以启动时按 pid 文件补一刀。
///
/// **保险丝**：pid 文件是我们自己写的，但 pid 会被系统复用，所以杀之前必须核对进程名——
/// 名字不符就放过（宁可留着孤儿，也绝不误伤用户自己开的同名程序）。
/// </summary>
public static class ChildProcessLease
{
    /// <summary>记住本次起的子进程 PID。</summary>
    public static void Remember(string pidFilePath, int pid)
    {
        try
        {
            var directory = Path.GetDirectoryName(pidFilePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(pidFilePath, pid.ToString(), Encoding.UTF8);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // 记不上不影响功能：最坏是下次留个孤儿，用户手动收。
        }
    }

    /// <summary>正常收尾后销账：别让下次启动白杀一个已复用的 PID。</summary>
    public static void Forget(string pidFilePath)
    {
        try { if (File.Exists(pidFilePath)) File.Delete(pidFilePath); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// 收掉上一次留给我们的子进程。<paramref name="expectedProcessName"/> 是保险丝：
    /// 名字不符一律放过。返回是否真的杀掉了一个。
    /// </summary>
    public static bool ReapPrevious(string pidFilePath, string expectedProcessName, Action<string, string>? log = null)
    {
        string raw;
        try
        {
            if (!File.Exists(pidFilePath)) return false;
            raw = File.ReadAllText(pidFilePath).Trim();
            File.Delete(pidFilePath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            log?.Invoke("WARN", $"读取遗留子进程 PID 文件失败（{pidFilePath}）：{error.Message}");
            return false;
        }

        if (!int.TryParse(raw, out var pid) || pid <= 0 || pid == Environment.ProcessId) return false;

        Process previous;
        try
        {
            previous = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            return false; // 早就没了：常态，不必吭声
        }

        using (previous)
        {
            string name;
            try
            {
                name = previous.ProcessName;
            }
            catch (InvalidOperationException)
            {
                return false; // 刚好在这两步之间退了
            }

            if (!string.Equals(name, expectedProcessName, StringComparison.OrdinalIgnoreCase))
            {
                log?.Invoke("DEBUG", $"遗留 pid 文件里的 PID {pid} 现在是 {name}，不是我们要收的 {expectedProcessName}（PID 复用），放过");
                return false;
            }

            try
            {
                previous.Kill(entireProcessTree: true);
                previous.WaitForExit(5000);
                log?.Invoke("INFO", $"已收掉上一次遗留的 {expectedProcessName} 子进程（PID {pid}）——强杀宿主留下的孤儿会占住下一次启动要用的资源");
                return true;
            }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                log?.Invoke("WARN", $"收掉遗留 {expectedProcessName}（PID {pid}）失败：{error.Message}");
                return false;
            }
        }
    }
}
