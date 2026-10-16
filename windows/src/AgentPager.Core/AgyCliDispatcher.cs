using System.Collections.Concurrent;
using System.Diagnostics;

namespace AgentPager.Core;

public static class AgyCliDispatcher
{
    /// <summary>
    /// 桥端待办队列：会话运行中到达的远程 prompt 在这里等 Stop hook 来 pull。
    /// 投递腿是 Stop 回包（pull 模型，实证见 docs）：脚本问桥要、桥给一条就出队一条，
    /// 同一进程内续跑——没有新进程、无锁、不烧第二次环境。条目带入队时间戳，
    /// 超 <see cref="QueueEntryTtl"/> 未被 pull 就地丢弃并记日志。
    /// </summary>
    public static readonly TimeSpan QueueEntryTtl = TimeSpan.FromMinutes(30);

    private record struct QueuedPrompt(string Prompt, string? Cwd, DateTimeOffset EnqueuedAt);

    private static readonly ConcurrentDictionary<string, ConcurrentQueue<QueuedPrompt>> PendingPrompts = new(StringComparer.Ordinal);

    public static Func<ProcessStartInfo, Process?> ProcessStarter { get; set; } = Process.Start;

    public static bool QueueOrExecute(
        string sessionID,
        string prompt,
        string? workspaceDir,
        bool isRunning,
        Action<string, string>? log = null)
    {
        if (isRunning)
        {
            var queue = PendingPrompts.GetOrAdd(sessionID, _ => new());
            queue.Enqueue(new(prompt, workspaceDir, DateTimeOffset.UtcNow));
            log?.Invoke("INFO", $"[Antigravity] 任务正在运行，Prompt 已加入桥端待办队列（会话 {sessionID}，排队数 {queue.Count}）");
            return true;
        }

        return ExecuteAgy(sessionID, prompt, workspaceDir, log);
    }

    /// <summary>
    /// Stop-pull 交接：取出队头（出队即消费）。调出方把文本装进 Stop 回包；
    /// 回包写失败由调出方把条目重回队尾（见 HookTcpServer）。
    /// </summary>
    public static string? TryTakeQueued(string sessionID)
    {
        if (!PendingPrompts.TryGetValue(sessionID, out var queue)) return null;
        return queue.TryDequeue(out var pending) ? pending.Prompt : null;
    }

    /// <summary>重回队尾：交接回包写失败时的兜底（localhost 上基本不可能，防丢失不断链）。</summary>
    public static void Requeue(string sessionID, string prompt, string? workspaceDir = null)
    {
        var queue = PendingPrompts.GetOrAdd(sessionID, _ => new());
        queue.Enqueue(new(prompt, workspaceDir, DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// 丢弃队头起连续过期的条目，返回丢弃数。now 可注入（单测）。
    /// </summary>
    public static int SweepExpired(
        string sessionID,
        TimeSpan maxAge,
        Action<string, string>? log = null,
        DateTimeOffset? now = null)
    {
        var current = now ?? DateTimeOffset.UtcNow;
        if (!PendingPrompts.TryGetValue(sessionID, out var queue)) return 0;

        var dropped = 0;
        while (queue.TryPeek(out var head) && current - head.EnqueuedAt > maxAge)
        {
            queue.TryDequeue(out _);
            dropped++;
        }

        if (dropped > 0)
            log?.Invoke("WARN", $"[Antigravity] 桥端队列 {dropped} 条 Prompt 超 {maxAge.TotalMinutes:0} 分钟未被 pull，已丢弃（会话 {sessionID}）");
        return dropped;
    }

    /// <summary>全会话 TTL 清扫（任务行已不在的僵尸条目也清），返回丢弃总数。</summary>
    public static int SweepAllExpired(
        TimeSpan maxAge,
        Action<string, string>? log = null,
        DateTimeOffset? now = null)
    {
        var total = 0;
        foreach (var sessionID in PendingPrompts.Keys.ToList())
            total += SweepExpired(sessionID, maxAge, log, now);
        return total;
    }

    public static bool HasQueued(string sessionID) =>
        PendingPrompts.TryGetValue(sessionID, out var queue) && !queue.IsEmpty;

    public static int QueuedCount(string sessionID) =>
        PendingPrompts.TryGetValue(sessionID, out var queue) ? queue.Count : 0;

    /// <summary>队尾（最新一条）的文本；徽章自愈时与入队登记保持同一语义。</summary>
    public static string? PeekLastText(string sessionID)
    {
        if (!PendingPrompts.TryGetValue(sessionID, out var queue)) return null;
        var snapshot = queue.ToArray();
        return snapshot.Length == 0 ? null : snapshot[^1].Prompt;
    }

    /// <summary>
    /// 清空整个会话队列（离线/任务回收时调用，调用方把返回值记进日志），返回丢弃条数。
    /// </summary>
    public static int ClearQueued(string sessionID)
    {
        if (!PendingPrompts.TryRemove(sessionID, out var queue)) return 0;
        return queue.Count;
    }

    /// <summary>持久化导出：会话 → 有序条目（队头在前）。空队列不导出。</summary>
    public static Dictionary<string, List<QueuedPromptRecord>> ExportQueues()
    {
        var result = new Dictionary<string, List<QueuedPromptRecord>>(StringComparer.Ordinal);
        foreach (var (sessionID, queue) in PendingPrompts)
        {
            var items = queue.Select(item => new QueuedPromptRecord(item.Prompt, item.Cwd, item.EnqueuedAt)).ToList();
            if (items.Count > 0) result[sessionID] = items;
        }
        return result;
    }

    /// <summary>
    /// 启动时恢复：逐条重新入队并立即按 TTL 过期一批。返回实际恢复条数。
    /// 重复导入会追加——调用方只在启动时调一次。
    /// </summary>
    public static int ImportQueues(
        Dictionary<string, List<QueuedPromptRecord>>? state,
        Action<string, string>? log = null,
        DateTimeOffset? now = null)
    {
        if (state is null) return 0;
        var restored = 0;
        foreach (var (sessionID, items) in state)
        {
            var queue = PendingPrompts.GetOrAdd(sessionID, _ => new());
            foreach (var item in items)
            {
                if (string.IsNullOrEmpty(item.Prompt)) continue;
                queue.Enqueue(new(item.Prompt, item.Cwd, item.EnqueuedAt));
                restored++;
            }
            restored -= SweepExpired(sessionID, QueueEntryTtl, log, now);
        }
        if (restored > 0)
            log?.Invoke("INFO", $"[Antigravity] 重启恢复桥端排队 {restored} 条（等 Stop pull）");
        return restored;
    }

    private static bool ExecuteAgy(
        string sessionID,
        string prompt,
        string? workspaceDir,
        Action<string, string>? log = null)
    {
        try
        {
            var workDir = workspaceDir ?? Environment.CurrentDirectory;
            var startInfo = new ProcessStartInfo
            {
                FileName = "agy.exe",
                WorkingDirectory = workDir,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("--conversation");
            startInfo.ArgumentList.Add(sessionID);
            startInfo.ArgumentList.Add("-p");
            startInfo.ArgumentList.Add(prompt);
            startInfo.ArgumentList.Add("--mode");
            startInfo.ArgumentList.Add("accept-edits");
            ProcessStarter(startInfo);
            log?.Invoke("INFO", $"[Antigravity] 已启动 agy 子进程执行新一轮 Prompt（会话 {sessionID}）");
            return true;
        }
        catch (Exception error)
        {
            log?.Invoke("WARN", $"[Antigravity] 启动 agy 子进程失败（会话 {sessionID}）：{error.Message}");
            return false;
        }
    }
}
