using System.Collections.Concurrent;

namespace AgentPager.Core;

public static class ClaudePromptDispatcher
{
    /// <summary>
    /// 桥端待办队列：会话运行中到达的远程 prompt 在这里等上一回合 Stop。
    /// 条目带入队时间戳，超 <see cref="QueueEntryTtl"/> 未放行就地丢弃并记日志——
    /// 放出去那条回合没有 Stop（失败/中断/卡审批），队头不能无限期卡住。
    /// </summary>
    public static readonly TimeSpan QueueEntryTtl = TimeSpan.FromMinutes(30);

    private record struct QueuedPrompt(string Prompt, string? Cwd, DateTimeOffset EnqueuedAt);

    private static readonly ConcurrentDictionary<string, ConcurrentQueue<QueuedPrompt>> PendingPrompts = new(StringComparer.Ordinal);

    public static bool QueueOrExecute(
        string sessionID,
        string prompt,
        string? cwd,
        bool isRunning,
        Action<string, string>? log = null,
        bool skipPipe = false)
    {
        if (isRunning)
        {
            var queue = PendingPrompts.GetOrAdd(sessionID, _ => new());
            queue.Enqueue(new(prompt, cwd, DateTimeOffset.UtcNow));
            log?.Invoke("INFO", $"[ClaudeCode] 任务正在运行，Prompt 已加入桥端待办队列（会话 {sessionID}，排队数 {queue.Count}）");
            return true;
        }

        return ClaudePeerClient.SendPrompt(sessionID, prompt, cwd, log, skipPipe);
    }

    /// <summary>
    /// 放行队头：先发送，成功才出队。发送失败保留在队头，下个 Stop 重试（记 WARN）。
    /// 过期条目先由 <see cref="SweepExpired"/> 就地丢弃。空队列返回 false（静默）。
    /// 注意：出队成功后故意不删字典条目——删条目与并发入队竞速会丢消息，
    /// 空队列的 HasQueued/QueuedCount 本来就是 false/0，留个空壳无代价。
    /// </summary>
    public static bool TryDrainQueued(string sessionID, Action<string, string>? log = null)
    {
        SweepExpired(sessionID, QueueEntryTtl, log);
        if (!PendingPrompts.TryGetValue(sessionID, out var queue)) return false;
        if (!queue.TryPeek(out var pending)) return false;

        if (!ClaudePeerClient.SendPrompt(sessionID, pending.Prompt, pending.Cwd, log))
        {
            log?.Invoke("WARN", $"[ClaudeCode] 排队 Prompt 发送失败，保留在桥端队列等下个 Stop 重试（会话 {sessionID}，剩余排队 {queue.Count}）");
            return false;
        }

        queue.TryDequeue(out _);
        log?.Invoke("INFO", $"[ClaudeCode] 当前回合结束，自动触发接单排队的 Prompt（会话 {sessionID}，剩余排队 {queue.Count}）");
        return true;
    }

    /// <summary>
    /// 丢弃队头起连续过期的条目，返回丢弃数。ConcurrentQueue 只能从队头出队，
    /// 队头新鲜时深处的过期条目动不了——它们在 drain 时同样会被丢弃。now 可注入（单测）。
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
            log?.Invoke("WARN", $"[ClaudeCode] 桥端队列 {dropped} 条 Prompt 超 {maxAge.TotalMinutes:0} 分钟未放行，已丢弃（会话 {sessionID}）");
        return dropped;
    }

    public static bool HasQueued(string sessionID) =>
        PendingPrompts.TryGetValue(sessionID, out var queue) && !queue.IsEmpty;

    public static int QueuedCount(string sessionID) =>
        PendingPrompts.TryGetValue(sessionID, out var queue) ? queue.Count : 0;

    /// <summary>队尾（最新一条）的文本；徽章自愈时与入队登记保持同一语义（UserPrompt=最新排队）。</summary>
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

    /// <summary>
    /// 全会话 TTL 清扫：连任务行都已不在的僵尸条目（重启后会话再没事件）只有这里能清。
    /// 任务还在的走 CheckClaudeRuntime 里的逐任务 Sweep。返回丢弃总数。
    /// </summary>
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
    /// 启动时恢复：逐条重新入队并立即按 TTL 过期一批（重启间隔里过期的不等下次 Stop）。
    /// 返回实际恢复条数。重复导入会追加——调用方只在启动时调一次。
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
            log?.Invoke("INFO", $"[ClaudeCode] 重启恢复桥端排队 {restored} 条（下个 Stop 按序放行）");
        return restored;
    }
}
