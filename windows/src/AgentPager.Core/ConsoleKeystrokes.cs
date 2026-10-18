using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AgentPager.Core;

/// <summary>
/// 把一段文字当作**键盘输入**写进目标进程的控制台输入缓冲区（拟人输入）。
///
/// 用途：Antigravity 完成态下远程下发 Prompt。Stop-pull 只能在同一回合收尾那一刻交接，
/// 回合已经结束就没有 pull 的机会；而 agy 的 TUI 此刻正停在提示符上等人打字——
/// 往它的控制台写按键等于用户亲手输入并回车，新一轮由 agy 自己开起来：
/// 不新起进程、不烧第二套环境、终端里看得见。
///
/// 机制与 <c>tool-dev/antigravity-discord-channel</c> 的 inject-worker 同款
/// （AttachConsole + CONIN$ + WriteConsoleInputW）。2026-10-19 本机实证：
/// 普通 conhost（CREATE_NO_WINDOW）与 Windows Terminal 的 ConPTY（父链
/// <c>python &lt;- cmd &lt;- WindowsTerminal</c>）两条路径都能把整行送达目标。
///
/// 约束：
/// ① **调用进程自身必须没有控制台**——AttachConsole 对已挂控制台的进程返回
///    ERROR_ACCESS_DENIED。桥是常驻 WPF 进程（可能被从终端拉起），且一旦挂上用户终端，
///    终端关闭会连带把它带走，所以一律经 <see cref="InjectViaWorker"/> 起短命子进程执行，
///    子进程是 WinExe（无控制台），进去先 FreeConsole 兜一道；
/// ② 目标进程必须真的挂着一个控制台：终端里跑的 agy ✓，stdout 被重定向/管道接走的 ✗
///    （此时报"打开 CONIN$ 失败"，调用方按失败处理，不装作成功）。
/// </summary>
public static class ConsoleKeystrokes
{
    /// <summary>子进程开关：本进程只做一次注入就退出，不进 WPF 启动流程。</summary>
    public const string WorkerSwitch = "--console-inject";

    private const uint GenericReadWrite = 0xC0000000;
    private const uint FileShareReadWrite = 0x00000003;
    private const uint OpenExisting = 0x00000003;
    private const ushort KeyEventType = 0x0001;
    private const ushort VirtualKeyReturn = 0x000D;

    /// <summary>
    /// 折成单行、去首尾空白：TUI 的输入框按回车提交，正文里的换行会把一句话截成两次提交。
    /// （与 antigravity-discord-channel 的 inject-worker 同一处理。）
    /// </summary>
    public static string NormalizeText(string? text) =>
        (text ?? string.Empty).Replace("\r\n", " ").Replace('\n', ' ').Trim();

    /// <summary>子进程入口：<c>--console-inject &lt;pid&gt; &lt;base64&gt;</c>，把结果写 stdout 供父进程记日志。</summary>
    public static int RunWorker(string[] args)
    {
        var index = Array.FindIndex(args, value => string.Equals(value, WorkerSwitch, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 2 >= args.Length)
        {
            Console.Error.WriteLine("用法：--console-inject <pid> <base64-text>");
            return 1;
        }

        if (!int.TryParse(args[index + 1], out var pid) || pid <= 0)
        {
            Console.Error.WriteLine("注入目标 PID 非法");
            return 1;
        }

        string text;
        try
        {
            text = Encoding.UTF8.GetString(Convert.FromBase64String(args[index + 2]));
        }
        catch (FormatException)
        {
            Console.Error.WriteLine("注入文本 base64 解码失败");
            return 1;
        }

        var ok = TryInject(pid, text, out var reason);
        Console.WriteLine(reason);
        return ok ? 0 : 2;
    }

    /// <summary>
    /// 起短命子进程执行注入（桥的唯一入口）。返回子进程退出码为 0 即已写入输入缓冲区，
    /// 中文说明从子进程 stdout 带回来（失败原因原样记进桥日志）。
    /// </summary>
    public static bool InjectViaWorker(int pid, string? text, out string reason)
    {
        var normalized = NormalizeText(text);
        if (normalized.Length == 0)
        {
            reason = "注入文本为空";
            return false;
        }

        var executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable))
        {
            reason = "拿不到自身可执行文件路径，无法起注入子进程";
            return false;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(WorkerSwitch);
        startInfo.ArgumentList.Add(pid.ToString());
        startInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(normalized)));

        try
        {
            using var worker = Process.Start(startInfo);
            if (worker is null)
            {
                reason = "注入子进程没有起来";
                return false;
            }

            var stdout = worker.StandardOutput.ReadToEndAsync();
            var stderr = worker.StandardError.ReadToEndAsync();
            if (!worker.WaitForExit(5000))
            {
                try { worker.Kill(entireProcessTree: true); } catch { /* 已经退了 */ }
                reason = "注入子进程 5 秒未收场，已强杀";
                return false;
            }

            var output = (stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult()).Trim();
            reason = output.Length > 0 ? output : $"注入子进程退出码 {worker.ExitCode}";
            return worker.ExitCode == 0;
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            reason = $"起注入子进程失败：{error.Message}";
            return false;
        }
    }

    /// <summary>真正干活的：只允许在无控制台的进程里调（子进程）。写不进去就如实报错。</summary>
    public static bool TryInject(int pid, string? text, out string reason)
    {
        reason = "";
        var normalized = NormalizeText(text);
        if (normalized.Length == 0)
        {
            reason = "注入文本为空";
            return false;
        }

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            reason = "控制台按键注入只有 Windows 实现";
            return false;
        }

        // 先脱掉自己可能继承的控制台：AttachConsole 对已挂控制台的进程返回 ERROR_ACCESS_DENIED。
        FreeConsole();
        if (!AttachConsole((uint)pid))
        {
            reason = $"AttachConsole 失败（目标 PID {pid} 可能没有控制台）err={Marshal.GetLastWin32Error()}";
            return false;
        }

        var handle = IntPtr.Zero;
        try
        {
            handle = CreateFileW("CONIN$", GenericReadWrite, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            {
                reason = $"打开 CONIN$ 失败 err={Marshal.GetLastWin32Error()}";
                return false;
            }

            var full = normalized + "\r";
            var records = BuildKeyRecords(full);

            if (!WriteConsoleInputW(handle, records, (uint)records.Length, out var written))
            {
                reason = $"WriteConsoleInputW 失败 err={Marshal.GetLastWin32Error()}";
                return false;
            }

            reason = $"已写入 {written} 条按键记录（{full.Length} 字符含回车）";
            return written == records.Length;
        }
        finally
        {
            if (handle != IntPtr.Zero && handle != new IntPtr(-1)) CloseHandle(handle);
            FreeConsole();
        }
    }

    private static InputRecord[] BuildKeyRecords(string full)
    {
        var records = new InputRecord[full.Length * 2];
        for (var i = 0; i < full.Length; i++)
        {
            var character = full[i];
            var virtualKey = character == '\r' ? VirtualKeyReturn : (ushort)0;
            records[i * 2] = MakeKeyRecord(keyDown: true, virtualKey, character);
            records[(i * 2) + 1] = MakeKeyRecord(keyDown: false, virtualKey, character);
        }
        return records;
    }

    /// <summary>
    /// 把一段文字编成要写进控制台的字节流（KEY_EVENT 记录数组，每条 20 字节：KeyDown@4、UnicodeChar@14）。
    /// 单独暴露是为了给单测钉住**字符必须原样以 16 位 WCHAR 落进记录**：2026-10-19 那个
    /// 「ASCII 一字不差、中文整条丢失」的 bug，就是结构体少写 `CharSet.Unicode` 被 ANSI 封送打散的。
    /// </summary>
    public static byte[] EncodeKeyRecords(string? text)
    {
        var records = BuildKeyRecords(NormalizeText(text) + "\r");
        var recordSize = Marshal.SizeOf<InputRecord>();
        var bytes = new byte[records.Length * recordSize];
        var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            for (var i = 0; i < records.Length; i++)
                Marshal.StructureToPtr(records[i], IntPtr.Add(pinned.AddrOfPinnedObject(), i * recordSize), false);
        }
        finally
        {
            pinned.Free();
        }
        return bytes;
    }

    private static InputRecord MakeKeyRecord(bool keyDown, ushort virtualKey, char character) => new()
    {
        EventType = KeyEventType,
        KeyEvent = new KeyEventRecord
        {
            KeyDown = keyDown ? 1 : 0,
            RepeatCount = 1,
            VirtualKeyCode = virtualKey,
            VirtualScanCode = 0,
            UnicodeChar = character,
            ControlKeyState = 0,
        },
    };

    // CharSet 必须显式写 Unicode：默认（Ansi）会把 char 按 ANSI 代码页封送，
    // 非 ASCII（中文/全角）在 1 字节槽里被打散——实测 ASCII 一字不差、中文整条丢失
    // （2026-10-19 由另一会话在真机上先发现，本机复现：worker 报写入成功，目标收不到）。
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct KeyEventRecord
    {
        public int KeyDown;            // BOOL
        public ushort RepeatCount;
        public ushort VirtualKeyCode;
        public ushort VirtualScanCode;
        public char UnicodeChar;       // WCHAR / CHAR 联合体
        public uint ControlKeyState;
    }

    [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Unicode)]
    private struct InputRecord
    {
        [FieldOffset(0)] public ushort EventType;
        [FieldOffset(4)] public KeyEventRecord KeyEvent; // 4 字节对齐填充，整条 20 字节
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    private static extern IntPtr CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "WriteConsoleInputW")]
    private static extern bool WriteConsoleInputW(
        IntPtr hConsoleInput,
        [In] InputRecord[] lpBuffer,
        uint nLength,
        out uint lpNumberOfEventsWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
