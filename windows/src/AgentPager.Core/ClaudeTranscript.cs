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
    DateTimeOffset? LatestReplyAt = null);

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
        // 本回合"最新一步"已经定过没有（倒序扫到的第一条主会话 assistant 行即是）。
        var sawLatestStep = false;
        var queueSettled = false;
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
                        // 系统注入项（<task-notification> 一类）不参与判定，跳过继续往前找；
                        // 第一条"非系统"队列操作说了算（见 ReadQueueOperation 的说明）。
                        if (!queueSettled && Text(root, "content") is { } queuedContent &&
                            !queuedContent.TrimStart().StartsWith('<'))
                        {
                            queueSettled = true;
                            ReadQueueOperation(root, out queuedPrompt, out queuedAt);
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
            replyAt);
    }

    /// <summary>行内 ISO 时间戳（Claude 每行都带，形如 2026-10-08T12:34:56.789Z）。</summary>
    private static DateTimeOffset? Timestamp(JsonElement root) =>
        Text(root, "timestamp") is { Length: > 0 } stamp &&
        DateTimeOffset.TryParse(stamp, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;

    /// <summary>
    /// 倒序遇到的第一条 queue-operation：enqueue 表示"还有一条在队里"，
    /// remove 表示"队里最后一条已被取走"（此时明确置空）。
    ///
    /// 两类内容必须排除，否则会被误当成"用户在排队提交提示词"（**实测踩过**）：
    ///   ① 系统注入：Claude 把后台任务完成通知也塞进同一队列（content 以 &lt;task-notification&gt; 开头）；
    ///   ② **本地斜杠命令**（`/context`、`/status`…）：它们是本地动作，且 Claude 只写 enqueue
    ///      而**不写配对的 remove**——按"最后一条操作说了算"的模型会永远停在"排队中"，
    ///      并把 `/context` 当成用户的提示词顶到第二行。
    /// </summary>
    private static void ReadQueueOperation(JsonElement root, out string? prompt, out DateTimeOffset? queuedAt)
    {
        prompt = null;
        queuedAt = null;
        var content = Text(root, "content");
        if (string.IsNullOrWhiteSpace(content)) return;
        var trimmed = content.TrimStart();
        if (trimmed.StartsWith('<') || trimmed.StartsWith('/')) return;
        if (Text(root, "operation") != "enqueue") return;
        prompt = content;
        if (Text(root, "timestamp") is { Length: > 0 } stamp &&
            DateTimeOffset.TryParse(stamp, null, System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed))
            queuedAt = parsed;
    }

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
            if (builder.Length > 0) builder.Append(' ');
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
