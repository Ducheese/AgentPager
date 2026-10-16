using System.Runtime.InteropServices;
using System.Text;

namespace AgentPager.Core;

/// <summary>
/// 从**另一个进程的 PEB** 里读它启动时的整套环境变量（用户裁定 2026-10-16：远程注入
/// 的 CLI 回退子进程要全量继承原对话宿主进程的 provider / 模型 / effort / 代理设置，
/// `claude --resume` 不带身份变量自己默认会拒登——裸跑实测 Not logged in exit 1）。
///
/// 为什么走 PEB 而不是 `GetEnvironmentVariable`（那只会读桥自己的）或进程启动参数：
/// 宿主进程在终端里被 `init-claudecode-commandcodeproxy.ps1` 拉起来，变量只存在于它的
/// 进程环境块里，Windows 没有公开 API 能读别的进程的环境——PEB 是唯一的路。
/// 本文件是探针脚本 `peb_env_probe3.ps1` / `dump_host_env_names.ps1`（PebEnv4，
/// 已实测读出宿主 69 项变量）的 C# 移植，参数与踩过的坑逐条保留：
///
/// - OpenProcess 取 <c>0x0410</c>（VM_READ|QUERY_INFORMATION），失败再试 <c>0x1010</c>
///   （QUERY_LIMITED_INFORMATION 变体；实测两者都能开）。
/// - PBI 只需前两字段：<c>PebBaseAddress</c> 在 x64 的偏移 8。
/// - x64 布局：PEB+0x20 → ProcessParameters，ProcessParameters+0x80 → Environment。
/// - 环境块**分块 2KB 逐段读**直到出现双 NUL 终止符：Bun/Node 的 EnvironmentSize 为 0，
///   一次性大块读会 ERROR_PARTIAL_COPY (299)——分块是探针实测出来的唯一稳法。
///
/// 只支持 64 位桥读 64 位进程（Claude Code = node x64）；任何一步失败返回 null，
/// 调用方按"读不到"降级（WARN + 用桥自身环境启动），绝不抛异常。
/// </summary>
public static class ClaudeHostEnvironment
{
    public static Dictionary<string, string>? Read(int pid)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return null;
        if (IntPtr.Size != 8) return null;

        var handle = OpenProcess(0x0410, false, pid);
        if (handle == IntPtr.Zero) handle = OpenProcess(0x1010, false, pid);
        if (handle == IntPtr.Zero) return null;

        try
        {
            var pbi = new byte[48];
            if (NtQueryInformationProcess(handle, 0, pbi, pbi.Length, out _) != 0) return null;
            var peb = BitConverter.ToInt64(pbi, 8);

            var pointer = new byte[8];
            if (!ReadProcessMemory(handle, (IntPtr)(peb + 0x20), pointer, pointer.Length, out _)) return null;
            var processParameters = BitConverter.ToInt64(pointer, 0);
            if (!ReadProcessMemory(handle, (IntPtr)(processParameters + 0x80), pointer, pointer.Length, out _)) return null;
            var environment = BitConverter.ToInt64(pointer, 0);
            if (environment == 0) return null;

            using var buffer = new MemoryStream();
            var chunk = new byte[2048];
            var address = environment;
            for (var chunks = 0; chunks < 512; chunks++)
            {
                if (!ReadProcessMemory(handle, (IntPtr)address, chunk, chunk.Length, out _)) return null;
                buffer.Write(chunk, 0, chunk.Length);
                var all = buffer.ToArray();
                var end = FindDoubleNull(all);
                if (end < 0)
                {
                    address += chunk.Length;
                    continue;
                }

                var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in Encoding.Unicode.GetString(all, 0, end).Split('\0'))
                {
                    var separator = entry.IndexOf('=');
                    // separator < 1 同时跳过空项与 "=C:" 这类驱动器工作目录伪变量（无名字）。
                    if (separator < 1) continue;
                    result[entry[..separator]] = entry[(separator + 1)..];
                }
                return result.Count > 0 ? result : null;
            }
            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static int FindDoubleNull(byte[] bytes)
    {
        for (var i = 0; i + 3 < bytes.Length; i += 2)
        {
            if (bytes[i] == 0 && bytes[i + 1] == 0 && bytes[i + 2] == 0 && bytes[i + 3] == 0) return i;
        }
        return -1;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int informationClass, byte[] buffer, int length, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, int size, out IntPtr bytesRead);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
