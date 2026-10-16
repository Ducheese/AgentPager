using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace AgentPager.Core;

public static class ClaudePeerClient
{
    public static Func<ProcessStartInfo, Process?> ProcessStarter { get; set; } = Process.Start;

    /// <summary>测试可替换的注册表扫描钩子（照抄 TaskCatalog.ClaudeRegistryScanner 的模式）。</summary>
    public static Func<Dictionary<string, ClaudeSessionRegistry.Entry>>? RegistryScanner { get; set; }

    /// <summary>
    /// CLI 回退子进程**不得继承**的三枚环境变量（用户裁定 2026-10-16：注入过程全量继承
    /// 原对话宿主进程的环境与 provider/模型设置，但这三枚是"进程身份/IPC"不是模型设置）：
    /// - CLAUDE_CODE_MESSAGING_SOCKET / CLAUDE_CODE_MESSAGING_TOKEN：宿主正在用的对等
    ///   消息管道的名字与钥匙；带上会让子进程去认领宿主进程的管道（探针实测：不带时子进程
    ///   会自建自己的管道，注册与收发正常）。
    /// - CLAUDE_CODE_ENTRYPOINT：宿主写的是 "cli"，照搬会让 -p 回退子进程在会话注册表里
    ///   冒充"真宿主"（探针实测：不带时子进程老实自报 "sdk-cli"，HostPids 才不被污染）。
    /// </summary>
    public static readonly IReadOnlySet<string> HostEnvironmentExclusions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CLAUDE_CODE_MESSAGING_SOCKET",
            "CLAUDE_CODE_MESSAGING_TOKEN",
            "CLAUDE_CODE_ENTRYPOINT",
        };

    /// <summary>
    /// 向某个 Claude Code 会话注入一条用户 Prompt：注册表里有带 messaging socket 的条目
    /// 就走管道直投，否则（含被 /resume 切走的 parked 态）回退 <c>claude --resume -p</c> 子进程。
    /// </summary>
    /// <param name="skipPipe">
    /// 跳过管道直投腿、直接走 CLI 回退（送达确认的 45s 重投专用，2026-10-17 真机定案）：
    /// 管道"写进去就算成功"——parked 会话的陈旧管道收得下字节却无人消费，首投可能已被
    /// 它静默吞掉；重投若再走同一条路等于白费第二次机会，必须换腿。
    /// </param>
    public static bool SendPrompt(
        string sessionID,
        string prompt,
        string? cwd = null,
        Action<string, string>? log = null,
        bool skipPipe = false)
    {
        var entries = RegistryScanner?.Invoke() ?? ClaudeSessionRegistry.Scan();
        entries.TryGetValue(sessionID, out var entry);

        if (!skipPipe && entry?.MessagingSocketPath is { Length: > 0 } socketPath)
        {
            try
            {
                var peerToken = LoadPeerToken(entry.Pid);
                if (TrySendViaSocket(socketPath, peerToken, sessionID, prompt, log))
                {
                    log?.Invoke("INFO", $"[ClaudeCode] 命名管道/UDS 注入 Prompt 成功（会话 {sessionID}，PID {entry.Pid}）");
                    return true;
                }
            }
            catch (Exception error)
            {
                log?.Invoke("WARN", $"[ClaudeCode] 管道注入异常（会话 {sessionID}）：{error.Message}，尝试 CLI 回退");
            }
        }

        // 回退：通过 `claude --resume <sessionID> -p "<prompt>"` 唤醒并续接会话
        try
        {
            var workDir = cwd ?? entry?.Cwd ?? Environment.CurrentDirectory;
            var startInfo = new ProcessStartInfo
            {
                FileName = ResolveCliExecutable(),
                WorkingDirectory = workDir,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("--resume");
            startInfo.ArgumentList.Add(sessionID);
            startInfo.ArgumentList.Add("-p");
            startInfo.ArgumentList.Add(prompt);
            ApplyHostEnvironment(startInfo, ClaudeSessionRegistry.HostPidOf(sessionID), log);
            ProcessStarter(startInfo);
            log?.Invoke("INFO", $"[ClaudeCode] 启动 claude --resume 子进程执行 Prompt（会话 {sessionID}，{startInfo.FileName}）");
            return true;
        }
        catch (Exception error)
        {
            log?.Invoke("WARN", $"[ClaudeCode] 启动 claude CLI 失败（会话 {sessionID}）：{error.Message}");
            return false;
        }
    }

    /// <summary>
    /// 把宿主进程（<see cref="ClaudeSessionRegistry.HostPidOf"/> 记下的那个）的整套环境变量
    /// 叠加到回退子进程的启动环境上：provider / 模型 / effort / 代理一概随原对话，桥里
    /// <c>ProcessStartInfo</c> 默认继承的那套被逐项覆盖。只剔除
    /// <see cref="HostEnvironmentExclusions"/> 三枚身份变量（剔除是无条件的——桥若本身从
    /// claude 终端继承过这些名字，Remove 这一步把它挡在门外）。
    /// 拿不到宿主 PID 或 PEB 读取失败时**降级不阻断**：以桥自身环境启动，WARN 里说清楚。
    /// </summary>
    public static void ApplyHostEnvironment(ProcessStartInfo startInfo, int? hostPid, Action<string, string>? log = null)
    {
        foreach (var key in HostEnvironmentExclusions)
            startInfo.Environment.Remove(key);

        if (hostPid is null)
        {
            log?.Invoke("WARN", "[ClaudeCode] 未记录宿主进程 PID，无法继承其环境（provider/模型设置可能缺失），以桥自身环境启动回退子进程");
            return;
        }

        var hostEnvironment = ClaudeHostEnvironment.Read(hostPid.Value);
        if (hostEnvironment is null)
        {
            log?.Invoke("WARN", $"[ClaudeCode] 读取宿主进程（PID {hostPid}）环境失败（进程已退或权限不足），以桥自身环境启动回退子进程");
            return;
        }

        var applied = 0;
        foreach (var (key, value) in hostEnvironment)
        {
            if (HostEnvironmentExclusions.Contains(key)) continue;
            startInfo.Environment[key] = value;
            applied++;
        }
        log?.Invoke("INFO", $"[ClaudeCode] 已全量继承宿主进程（PID {hostPid}）环境 {applied} 项（provider/模型随原对话，另剔除身份变量 {HostEnvironmentExclusions.Count} 枚）");
    }

    /// <summary>
    /// 解析 claude 启动器的真实可执行路径（2026-10-17 真机定案修复）。Windows 上 npm 全局
    /// 安装只落 <c>claude.cmd</c> / <c>claude.ps1</c> 两个 shim，并没有 <c>claude.exe</c>；
    /// 而 <see cref="ProcessStartInfo"/>（UseShellExecute=false → CreateProcess）只认带扩展名
    /// 的可执行体，<c>FileName = "claude"</c> 必抛"系统找不到指定的文件"（v81 真机日志实锤：
    /// CLI 回退腿从未真正启动过）。claude.cmd 的内容就是转调
    /// <c>&lt;npm根&gt;\node_modules\@anthropic-ai\claude-code\bin\claude.exe</c>（原生启动器），
    /// 故按同一布局逐级查找：PATH 每个目录的 <c>claude.exe</c> / 包内 claude.exe
    /// （官方原生安装的布局）→ <c>%APPDATA%\npm</c>（npm 全局根）→
    /// <c>%ProgramFiles%\nodejs</c>（Node 官方安装器的全局根）。全都找不到时退回裸
    /// <c>"claude"</c>：保持旧行为，失败时 WARN 会照旧大声报"启动 claude CLI 失败"。
    /// </summary>
    public static string ResolveCliExecutable()
    {
        var packageLauncher = Path.Combine("node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe");

        foreach (var rawDir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dir = rawDir.Trim('"');
            if (dir.Length == 0) continue;
            if (FirstExisting(Path.Combine(dir, "claude.exe"), Path.Combine(dir, packageLauncher)) is { } fromPath)
                return fromPath;
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (appData.Length > 0 &&
            FirstExisting(Path.Combine(appData, "npm", packageLauncher)) is { } fromAppData)
            return fromAppData;

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (programFiles.Length > 0 &&
            FirstExisting(Path.Combine(programFiles, "nodejs", packageLauncher)) is { } fromProgramFiles)
            return fromProgramFiles;

        return "claude";
    }

    private static string? FirstExisting(params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            try
            {
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
                // PATH 里的非法项（含通配/保留字符）忽略，继续找。
            }
        }
        return null;
    }

    private static string? LoadPeerToken(int? pid)
    {
        if (pid is null) return null;
        try
        {
            var dir = ClaudeSessionRegistry.Directory;
            if (!Directory.Exists(dir)) return null;
            var files = Directory.GetFiles(dir, $"{pid}.*.key");
            if (files.Length == 0) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(files[0]));
            return doc.RootElement.TryGetProperty("peerToken", out var prop) && prop.ValueKind == JsonValueKind.String
                ? prop.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool TrySendViaSocket(
        string socketPath,
        string? peerToken,
        string sessionID,
        string prompt,
        Action<string, string>? log)
    {
        var msgId = Guid.NewGuid().ToString("n")[..16];
        var authFrame = peerToken is not null
            ? JsonSerializer.Serialize(new { type = "auth", token = peerToken }, WireJson.Options) + "\n"
            : null;
        // session_id 必带（探针实测的误投根因）：宿主进程按消息里的 session_id 决定投给哪个
        // 已加载的会话；不带时它落到"当前会话"——用户在手机上发消息前一瞬在电脑上切了会话，
        // 这句话就进错了对话（测试矩阵 L2）。带上的代价为零：Node 侧读不到该字段时忽略。
        var userFrame = JsonSerializer.Serialize(new
        {
            type = "user",
            session_id = sessionID,
            message = new { role = "user", content = prompt },
            priority = "next",
            msg_id = msgId,
        }, WireJson.Options) + "\n";

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var pipeName = socketPath.StartsWith(@"\\.\pipe\", StringComparison.OrdinalIgnoreCase)
                ? socketPath[@"\\.\pipe\".Length..]
                : socketPath;

            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
            pipe.Connect(3000);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true);
            if (authFrame is not null)
            {
                writer.Write(authFrame);
                writer.Flush();
            }
            writer.Write(userFrame);
            writer.Flush();
            return true;
        }
        else
        {
            var endpoint = new UnixDomainSocketEndPoint(socketPath);
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(endpoint);
            using var stream = new NetworkStream(socket);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            if (authFrame is not null)
            {
                writer.Write(authFrame);
                writer.Flush();
            }
            writer.Write(userFrame);
            writer.Flush();
            return true;
        }
    }
}
