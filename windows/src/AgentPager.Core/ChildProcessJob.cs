using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AgentPager.Core;

/// <summary>
/// 把一个子进程绑进 Windows Job Object 并置 <c>KILL_ON_JOB_CLOSE</c>：宿主进程只要一死
/// ——**哪怕是被 Task Manager / <c>Stop-Process</c> 强杀、走不到 StopAsync**——job 句柄随进程
/// 消失，系统立刻带走 job 里的全部子进程。收尾从此不靠"对方讲礼貌"。
///
/// 修的是 2026-10-19 实测踩到的坑：强杀桥留下**孤儿 ngrok**，它还占着 ngrok 免费版的
/// 单会话名额，下一次启动的 ngrok 被云端直接拒绝（退出码 1）；而新桥又去轮询
/// <c>127.0.0.1:4040</c>（那是孤儿进程的 API），于是报出"失败与在线并存"的混合状态。
///
/// 分配失败不阻断（受限环境、嵌套 job 等）：调用方记 WARN，退回"正常退出时杀进程树"。
/// </summary>
public sealed class ChildProcessJob : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private readonly Action<string, string>? _log;
    private IntPtr _handle;
    private bool _disposed;

    private ChildProcessJob(IntPtr handle, Action<string, string>? log)
    {
        _handle = handle;
        _log = log;
    }

    /// <summary>建一个"随本进程消失就杀光成员"的 job；建不起来返回 null（调用方降级）。</summary>
    public static ChildProcessJob? TryCreate(Action<string, string>? log = null)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return null;

        var handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle == IntPtr.Zero)
        {
            log?.Invoke("WARN", $"创建 Job Object 失败（err={Marshal.GetLastWin32Error()}），子进程将退回「正常退出时杀进程树」");
            return null;
        }

        var information = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JobObjectLimitKillOnJobClose,
            },
        };
        var size = (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, ref information, size))
        {
            var error = Marshal.GetLastWin32Error();
            CloseHandle(handle);
            log?.Invoke("WARN", $"设置 Job Object 的 KILL_ON_JOB_CLOSE 失败（err={error}），子进程将退回「正常退出时杀进程树」");
            return null;
        }

        return new ChildProcessJob(handle, log);
    }

    /// <summary>把子进程塞进 job。失败只记 WARN——收尾还有 StopAsync 那条腿。</summary>
    public bool TryAssign(Process process, Action<string, string>? log = null)
    {
        if (_disposed || _handle == IntPtr.Zero) return false;
        try
        {
            if (AssignProcessToJobObject(_handle, process.Handle)) return true;
            log?.Invoke("WARN", $"把子进程（PID {process.Id}）绑进 Job Object 失败（err={Marshal.GetLastWin32Error()}）：强杀本进程时它可能变孤儿");
            return false;
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            log?.Invoke("WARN", $"把子进程（PID {process.Id}）绑进 Job Object 异常：{error.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_handle == IntPtr.Zero) return;
        // 关句柄即触发 KILL_ON_JOB_CLOSE：job 里的子进程被系统带走（这正是机制本身）。
        CloseHandle(_handle);
        _handle = IntPtr.Zero;
        _log?.Invoke("DEBUG", "已关闭 Job Object（其成员子进程随之收尾）");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateJobObjectW")]
    private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int jobObjectInformationClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInformation, uint cbJobObjectInformationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
