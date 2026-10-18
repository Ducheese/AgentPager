namespace AgentPager.Core;

/// <summary>
/// Antigravity 的远程 Prompt 投递：**到达即按键注入**宿主终端，桥端不再排队。
///
/// 为什么不排队（2026-10-19 用户裁定）：键盘输入就是用户输入，agy 的 TUI 自己就带排队/打断语义
/// （`/config` 的 `Queued Messages`：`Queue` 攒到回合末、`Send Immediately` 直接打断，
/// 另可 `alt+m` 反向发送）——"这条算排队还是打断"是 agy 自己的决定，桥替它排一遍只会多一层
/// 状态（旧 Stop-pull 队列、TTL 清扫、徽章自愈、持久化都据此撤除）。opencode 那条腿也是同一模式：
/// 桥只负责把 Prompt 送进会话，排不排由对方决定。
///
/// 机制与实证见 <see cref="ConsoleKeystrokes"/>：不新起进程、终端里打得出来、完成态也能开新一轮。
/// 代价是**寻址只到进程**——按键落进那个终端**当前显示**的会话，终端换过会话
/// （`/resume` 选择器、`@conversation:`、导入别会话）时就不再保证是目标任务。
/// </summary>
public static class AgyCliDispatcher
{
    public delegate bool ConsoleInjector(int pid, string text, out string reason);

    /// <summary>控制台物理按键注入器（默认起自身短命子进程执行；单测替换成探针）。</summary>
    public static ConsoleInjector Injector { get; set; } = ConsoleKeystrokes.InjectViaWorker;

    /// <summary>会话 → 宿主 agy 终端 PID（注入目标）。生产环境挂 <c>TaskCatalog.AgyHostPidOf</c>。</summary>
    public static Func<string, int?> HostPidProvider { get; set; } = _ => null;

    /// <summary>
    /// 寻址闸门：该会话的宿主终端是否"正对着它"。生产环境挂 <c>TaskCatalog.AgyHostPidServes</c>——
    /// 终端换过会话（`/resume` 等）后，那个 pid 最近上报的会话就不是本任务了，返回 false。
    /// 默认放行（未接线的宿主保持旧行为）。
    /// </summary>
    public static Func<string, bool> HostServesSession { get; set; } = _ => true;

    /// <summary>
    /// 投递一条 Prompt：定位该会话的宿主终端，按键注入 + 回车，由 agy 自己决定
    /// 「接着跑（Queue 模式）还是打断当前回合（Send Immediately 模式）」。
    /// <paramref name="workspaceDir"/> 不参与投递（注入不换工作目录），保留该参数只为与
    /// Claude / OpenCode 共用 <c>DispatchPrompt</c> 的签名。
    /// </summary>
    public static bool Dispatch(
        string sessionID,
        string prompt,
        string? workspaceDir,
        bool isRunning,
        Action<string, string>? log = null)
    {
        var hostPid = HostPidProvider(sessionID);
        if (hostPid is null || hostPid <= 0)
        {
            log?.Invoke("WARN", $"[Antigravity] 找不到会话 {sessionID} 的宿主终端 PID（hook 未上报，或桥重启后还没收到该会话的事件），无法注入");
            return false;
        }

        if (!ClaudeSessionRegistry.ProcessAliveChecker(hostPid.Value))
        {
            log?.Invoke("WARN", $"[Antigravity] 会话 {sessionID} 的宿主终端（PID {hostPid.Value}）已退出，注入取消");
            return false;
        }

        // 寻址闸门：那个终端最近一次上报的会话不是本任务（换过会话），按键会落进当前显示的那个会话——
        // 我们无法核实是哪一个（TUI 不显示会话标识），所以拒发而不是赌一把。
        if (!HostServesSession(sessionID))
        {
            log?.Invoke("WARN", $"[Antigravity] 会话 {sessionID} 的宿主终端（PID {hostPid.Value}）最近跑的是别的会话（/resume 换过会话），拒绝注入以免打进别的会话");
            return false;
        }

        if (!Injector(hostPid.Value, prompt, out var reason))
        {
            log?.Invoke("WARN", $"[Antigravity] 向宿主终端（PID {hostPid.Value}）注入 Prompt 失败（会话 {sessionID}）：{reason}");
            return false;
        }

        var moment = isRunning ? "回合进行中，排队或打断由 agy 的 Queued Messages 决定" : "已完成，原地开新一轮";
        log?.Invoke("INFO", $"[Antigravity] 已按键注入 Prompt（会话 {sessionID}，宿主终端 PID {hostPid.Value}，投递时{moment}）");
        return true;
    }
}
