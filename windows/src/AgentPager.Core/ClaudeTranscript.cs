using System.Text;
using System.Text.Json;

namespace AgentPager.Core;

/// <summary>
/// 转录本尾部一次读取能拿到的全部内容（与 agy 的 ExtractLatest 同构，多一项排队）。
/// <c>QueuedAt</c> 为 null 表示"队里最后一条已被取走"，非 null 表示还有一条在排队。
/// </summary>
public sealed record ClaudeTranscriptSnapshot(
    string? Title,
    TokenUsage? Tokens,
    string? LatestReply,
    string? Model,
    string? QueuedPrompt = null,
    DateTimeOffset? QueuedAt = null,
    string? LatestPrompt = null,
    /// <summary>正文那一条 assistant 行的 timestamp。用来判断它到底属于**本轮**还是上一轮：</summary>
    DateTimeOffset? LatestReplyAt = null,
    /// <summary>
    /// 本轮**最近一次说过的正文全文**（不截断），供展开后的 REPLY 面板用。
    /// 与 <see cref="LatestReply"/> 的取法不同：后者认"最新一步"，最新一步是工具调用就作废；
    /// 这里可以**跨过工具步往回找** —— "说一句就调工具"是回合中的常态，不跨就永远拿不到正文。
    /// 仍严格限定在本轮内（不跨回合捡上一轮的），空白表示本轮还没说过话。
    /// </summary>
    string? LatestReplyFull = null,
    /// <summary>上面那条正文的 timestamp，用来判定它属于本轮（与 <see cref="LatestReplyAt"/> 同理）。</summary>
    DateTimeOffset? LatestReplyFullAt = null);

/// <summary>
/// Claude Code 转录本（<c>~/.claude/projects/&lt;slug&gt;/&lt;session_id&gt;.jsonl</c>）尾部读取器。
///
/// 与 agy 的 AgyTranscriptReader 同一套路（只读尾部、共享读锁、倒序扫描、提前收敛），
/// 区别只在行结构：Claude 是 JSONL，每行一个 type，实测行型见
/// docs/claude-code-state-mapping.md 第一节。
///
/// 三件事都在这里拿：
///   1) 标题 —— 用户自设 custom-title 与 Claude 自动生成的 ai-title（**绝不从 prompt 自造**）；
///   2) Token —— 最新一条**主会话** assistant 行的 message.usage（不取最大值，压缩后如实回落）；
///   3) 正文 —— 当前回合最后一条主会话 assistant 的 text 块（Stop 时也优先用 payload 的
///      last_assistant_message，本读取器是兜底与竞速补救）。
///
/// 严格排除 isSidechain（子代理写进同一文件的行），否则子代理的正文与用量会冒充父任务。
/// </summary>
public static class ClaudeTranscriptReader
{
    /// <summary>尾部最多读 512 KB：Claude 的工具结果行可能极大，256 KB 有时读不到最后一条 assistant。</summary>
    private const int TailBytes = 512 * 1024;

    public static ClaudeTranscriptSnapshot Read(string? transcriptPath)
    {
        if (string.IsNullOrWhiteSpace(transcriptPath) || !File.Exists(transcriptPath)) return Empty;

        string text;
        try
        {
            var fileInfo = new FileInfo(transcriptPath);
            var readSize = (int)Math.Min(fileInfo.Length, TailBytes);
            if (readSize <= 0) return Empty;

            var buffer = new byte[readSize];
            using var stream = new FileStream(
                transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.Seek(-readSize, SeekOrigin.End);
            var bytesRead = stream.Read(buffer, 0, readSize);
            if (bytesRead <= 0) return Empty;
            text = Encoding.UTF8.GetString(buffer, 0, bytesRead);
        }
        catch
        {
            return Empty;
        }

        var lines = text.Split('\n');
        string? title = null;
        string? customTitle = null;
        string? reply = null;
        string? model = null;
        TokenUsage? tokens = null;
        string? queuedPrompt = null;
        DateTimeOffset? queuedAt = null;
        string? latestPrompt = null;
        DateTimeOffset? replyAt = null;
        // 正文全文：与 reply **不同**的取法（见 assistant 分支里的说明）——
        // reply 认"最新一步"，一步是工具调用就作废；这里要的是"本轮最近一次说过的正文"，
        // 可以跨过工具步往回找。
        string? bodyReply = null;
        DateTimeOffset? bodyReplyAt = null;
        // 本回合"最新一步"已经定过没有（倒序扫到的第一条主会话 assistant 行即是）。
        var sawLatestStep = false;
        var queueSettled = false;
        // 队列操作的"欠账"：倒序扫到 dequeue / remove 就 +1，后面的 enqueue 先还账
        // （说明这条有过配对取走）。详见 queue-operation 分支的说明。
        var queueOwed = 0;
        // 正文只在"当前回合"内找：倒序扫到真实用户输入即封口，绝不跨回合捡上一轮的正文。
        var replySettled = false;

        for (var index = lines.Length - 1; index >= 0; index--)
        {
            var line = lines[index].Trim();
            if (line.Length == 0) continue;

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                if (!root.TryGetProperty("type", out var typeProperty)) continue;

                switch (typeProperty.GetString())
                {
                    case "ai-title":
                        title ??= Text(root, "aiTitle");
                        break;
                    case "custom-title":
                        customTitle ??= Text(root, "customTitle");
                        break;
                    case "queue-operation":
                        // 队列状态必须按操作序列**成对抵消**来判，不能只看"最后一条带内容的操作"：
                        // dequeue / remove 行**没有 content 字段**，旧模型会跳过它们、继续倒扫到
                        // 更老的 enqueue，把"已排空"读成"仍在排队"——实测踩过（管道注入的消息
                        // 跑完后，第二行被排队文本永久冒充，见 ClaudeEventReducer.ApplyTranscript
                        // 的还原分支）。倒序扫描（dequeue 取队首，与更早的 enqueue 两两配对）：
                        //   · dequeue / remove → 欠账 +1；
                        //   · popAll → 整队清空，直接定案"没有排队"；
                        //   · enqueue → 尚有欠账先抵账（这条被配对取走过）；还不上账、且
                        //     content 是有效用户文本（系统注入 <task-notification> 与本地
                        //     斜杠命令 /context 等排除）的，才是"当前还在队里"的那条。
                        // 局限：同队多条交错时，报道的文本/时间戳可能配错一条（徽章开关不受影响）。
                        if (!queueSettled && Text(root, "operation") is { Length: > 0 } queueOp)
                        {
                            if (queueOp is "dequeue" or "remove")
                            {
                                queueOwed++;
                            }
                            else if (queueOp == "popAll")
                            {
                                queueSettled = true;
                            }
                            else if (queueOp == "enqueue")
                            {
                                if (queueOwed > 0)
                                {
                                    queueOwed--;
                                }
                                else if (Text(root, "content") is { } queuedContent &&
                                         !queuedContent.TrimStart().StartsWith('<') &&
                                         !queuedContent.TrimStart().StartsWith('/'))
                                {
                                    queueSettled = true;
                                    queuedPrompt = queuedContent;
                                    queuedAt = Timestamp(root);
                                }
                            }
                        }
                        break;
                    case "assistant":
                        // 子代理写进同一文件的行（isSidechain）不得冒充主会话。
                        if (IsSidechain(root)) break;
                        if (root.TryGetProperty("message", out var message))
                        {
                            // Token / 模型永远跟随最新一条 assistant（不取最大值，压缩后如实回落）。
                            if (tokens is null)
                            {
                                tokens = ReadUsage(message);
                                model = Text(message, "model") ?? model;
                            }

                            // 正文候选由**本回合第一条被倒序扫到的主会话 assistant 行**决定：
                            //   · 它有 text 块 → 那就是当前最新一步，取它；
                            //   · 它只有 tool_use（没有 text）→ 当前最新一步是**工具调用**，
                            //     正文候选必须保持 null，**不许再往回找更早的正文**。
                            // 后半句是 agy 明文写死的规则（"当前回合内最新步骤若为工具调用，
                            // 正文候选保持 null"）。少了它，心跳会把已经显示的工具步
                            // 覆盖成上一段正文——实测踩过（用户报"工具调用内容无法覆盖上一个 reply"）。
                            // **也别放宽成"往回找最近的一段正文"**：Stop 有"终稿还没落盘"的竞态，
                            // 宽松口径会捞到更早的一段并钉在第三行（实测踩过，已回退）。
                            if (!replySettled && !sawLatestStep)
                            {
                                sawLatestStep = true;
                                if (ReadTextReply(root) is { } replyText)
                                {
                                    reply = replyText;
                                    replyAt = Timestamp(root);
                                }
                            }

                            // 正文全文（给展开后的 REPLY 面板）：这里**故意不套上面那条"最新一步"规则**。
                            // 实测（2026-10-15，本机真跑）：Claude 把正文和工具调用写成**两行**——
                            // `assistant blocks=[text]` 紧跟 `assistant blocks=[tool_use]`——
                            // 于是倒序扫到的第一条永远是工具调用，reply 直接作废，面板整个回合都拿不到正文。
                            // 而"说一句就调工具"正是回合中的常态，所以正文必须能跨过工具步往回找。
                            // 边界仍严格限定在本轮：replySettled 一置位就说明已越过本轮用户输入，
                            // 再往回全是上一轮的正文（拿时间戳兜底，见 FreshReplyFull）。
                            if (!replySettled && bodyReply is null && ReadTextReply(root) is { } bodyText)
                            {
                                bodyReply = bodyText;
                                bodyReplyAt = Timestamp(root);
                            }
                        }
                        break;
                    case "user":
                        if (TurnBoundaryText(root) is { } boundary)
                        {
                            replySettled = true;
                            // 倒序扫描：第一条遇到的回合边界就是**本轮**的用户输入。
                            // 它是排队消息排空后还原 UserPrompt 的唯一依据（见 ClaudeEventReducer.ApplyTranscript）。
                            latestPrompt ??= Normalize(boundary);
                        }
                        break;
                }
            }
            catch
            {
                // 半行（正在写入的最后一行）或结构不认识：跳过。
            }

            if (replySettled && queueSettled && tokens is not null &&
                (customTitle is not null || title is not null)) break;
        }

        var resolved = Normalize(customTitle) ?? Normalize(title);
        return new ClaudeTranscriptSnapshot(
            resolved is { Length: > 0 } ? resolved : null,
            tokens,
            Truncate(Normalize(reply), 120),
            ModelNames.Label(model),
            Truncate(Normalize(queuedPrompt), 1000),
            queuedAt,
            Truncate(latestPrompt, 1000),
            replyAt,
            // **不能过 Normalize**：它把 \r\n 全换成空格再合并，是给第三行那种单行预览用的。
            // 正文全靠换行划块（标题/列表/表格的分界），压平之后 Markdown 解析器看到的是
            // 一整段纯文本，管道符和横线原样画出来 —— 实测就是"表格散架"的真正原因。
            // agy 的 fullResponse、opencode 的 fullText 都是原文，只有这里之前错了。
            string.IsNullOrWhiteSpace(bodyReply) ? null : bodyReply.Trim(),
            bodyReplyAt);
    }

    /// <summary>
    /// 转录本里"这一轮结束没有"的最小证据（只给生命周期纠偏用，与上面的预览读取完全解耦）。
    ///
    /// 判据与上游 macOS 实现一致：最后一条**活动**（user / assistant，排除 isSidechain）之后
    /// 没有出现结束证据，就是这一轮还开着。结束证据有两种：
    ///   · <c>type=system, subtype=stop_hook_summary</c>（Stop 钩子落盘的摘要）；
    ///   · assistant 的 <c>message.stop_reason</c> 为 <c>end_turn</c> / <c>stop_sequence</c>
    ///     （Desktop 路径不一定发 Stop，但最终回复本身就能证明这一轮结束了）。
    /// <c>StopAt</c> 是**最后一条**结束证据的时间；调用方必须同时确认 <see cref="HasOpenTurn"/>
    /// 为 false 才可以用它（否则它属于更早的轮次）。
    /// </summary>
    public sealed record ClaudeTurnEvidence(
        bool HasOpenTurn,
        DateTimeOffset? StopAt,
        DateTimeOffset? LastActivityAt);

    /// <summary>
    /// 读转录本尾部的轮次证据。文件不存在/读不了返回 null（未知，绝不当作"已结束"）；
    /// 文件存在但没有可用行时返回空证据（<see cref="ClaudeTurnEvidence.HasOpenTurn"/> = false、
    /// <see cref="ClaudeTurnEvidence.StopAt"/> = null）——这和"没有文件"是两件事：
    /// 前者不能据此判中断，后者才可以。
    /// </summary>
    public static ClaudeTurnEvidence? ReadTurnEvidence(string? transcriptPath)
    {
        if (string.IsNullOrWhiteSpace(transcriptPath) || !File.Exists(transcriptPath)) return null;

        string text;
        var tailStartedMidFile = false;
        try
        {
            var fileInfo = new FileInfo(transcriptPath);
            var readSize = (int)Math.Min(fileInfo.Length, TailBytes);
            if (readSize <= 0) return new ClaudeTurnEvidence(false, null, null);
            tailStartedMidFile = readSize < fileInfo.Length;

            var buffer = new byte[readSize];
            using var stream = new FileStream(
                transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.Seek(-readSize, SeekOrigin.End);
            var bytesRead = stream.Read(buffer, 0, readSize);
            if (bytesRead <= 0) return new ClaudeTurnEvidence(false, null, null);
            text = Encoding.UTF8.GetString(buffer, 0, bytesRead);
        }
        catch
        {
            return null;
        }

        // 尾读可能从一行中间开始：丢掉不完整的首行，否则那半行会解析失败（无害）或误判。
        if (tailStartedMidFile)
        {
            var newline = text.IndexOf('\n');
            text = newline >= 0 ? text[(newline + 1)..] : string.Empty;
        }

        var lastActivityIndex = -1;
        var lastStopIndex = -1;
        DateTimeOffset? stopAt = null;
        DateTimeOffset? lastActivityAt = null;
        var index = -1;

        foreach (var line in text.Split('\n'))
        {
            index++;
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            try
            {
                using var document = JsonDocument.Parse(trimmed);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;

                if (Timestamp(root) is { } stamp && (lastActivityAt is null || stamp > lastActivityAt))
                    lastActivityAt = stamp;

                if (IsSidechain(root)) continue;
                var type = Text(root, "type");
                if (type is "user" or "assistant") lastActivityIndex = index;

                if (type == "assistant" &&
                    root.TryGetProperty("message", out var message) &&
                    Text(message, "stop_reason") is "end_turn" or "stop_sequence")
                {
                    lastStopIndex = index;
                    stopAt = Timestamp(root);
                }
                else if (type == "system" && Text(root, "subtype") == "stop_hook_summary")
                {
                    lastStopIndex = index;
                    stopAt = Timestamp(root);
                }
            }
            catch
            {
                // 半行或结构不认识：跳过。
            }
        }

        return new ClaudeTurnEvidence(lastActivityIndex > lastStopIndex, stopAt, lastActivityAt);
    }

    /// <summary>行内 ISO 时间戳（Claude 每行都带，形如 2026-10-08T12:34:56.789Z）。</summary>
    private static DateTimeOffset? Timestamp(JsonElement root) =>
        Text(root, "timestamp") is { Length: > 0 } stamp &&
        DateTimeOffset.TryParse(stamp, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;

    /// <summary>正文预览上限与 agy/opencode 一致：120 字单行。</summary>
    private static string? Truncate(string? value, int limit) =>
        value is null ? null : value.Length > limit ? value[..limit] : value;

    private static bool IsSidechain(JsonElement root) =>
        root.TryGetProperty("isSidechain", out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>
    /// 真实用户输入才算回合边界：content 为字符串（工具结果行是数组）、非 meta、
    /// 且不是系统注入的尖括号载荷（如 &lt;task-notification&gt; 后台任务完成通知）。
    /// 返回该输入文本，非用户输入时返回 null。
    /// </summary>
    private static string? TurnBoundaryText(JsonElement root)
    {
        if (IsSidechain(root)) return null;
        if (root.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True) return null;
        if (!root.TryGetProperty("message", out var message)) return null;
        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String)
            return null;
        var text = content.GetString();
        return string.IsNullOrWhiteSpace(text) || text.TrimStart().StartsWith('<') ? null : text;
    }

    /// <summary>assistant 行里所有 text 块的拼接（thinking / tool_use 不计入）。</summary>
    private static string? ReadTextReply(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var message)) return null;
        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return null;

        var builder = new StringBuilder();
        foreach (var block in content.EnumerateArray())
        {
            if (!block.TryGetProperty("type", out var type) || type.GetString() != "text") continue;
            if (!block.TryGetProperty("text", out var text)) continue;
            var value = text.GetString();
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (builder.Length > 0) builder.Append("\n\n");
            builder.Append(value);
        }
        return builder.Length == 0 ? null : builder.ToString();
    }

    /// <summary>message.usage → TokenUsage：与 opencode 的 tokens 口径一一对应。</summary>
    private static TokenUsage? ReadUsage(JsonElement message)
    {
        if (!message.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return null;

        var input = Int(usage, "input_tokens");
        var cached = Int(usage, "cache_read_input_tokens");
        var output = Int(usage, "output_tokens");
        var reasoning = 0;
        if (usage.TryGetProperty("output_tokens_details", out var details) &&
            details.ValueKind == JsonValueKind.Object)
            reasoning = Int(details, "thinking_tokens");

        if (input == 0 && cached == 0 && output == 0) return null;
        return new TokenUsage(input, cached, output, reasoning, input + cached + output);
    }

    private static int Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var result)
            ? result
            : 0;

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var result = string.Join(' ', value.Replace('\r', ' ').Replace('\n', ' ')
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return result.Length == 0 ? null : result;
    }

    private static readonly ClaudeTranscriptSnapshot Empty = new(null, null, null, null, null, null, null, null);
}
