using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace AgentPager.Core;

public sealed record AgyToolCall(
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("args")] JsonElement? Args = null);

public sealed record AgyHookPayload(
    [property: JsonPropertyName("event")] string? Event = null,
    [property: JsonPropertyName("hook_event_name")] string? HookEventName = null,
    [property: JsonPropertyName("conversationId")] string? ConversationId = null,
    [property: JsonPropertyName("workspacePaths")] string[]? WorkspacePaths = null,
    [property: JsonPropertyName("transcriptPath")] string? TranscriptPath = null,
    [property: JsonPropertyName("artifactDirectoryPath")] string? ArtifactDirectoryPath = null,
    [property: JsonPropertyName("modelName")] string? ModelName = null,
    [property: JsonPropertyName("toolCall")] AgyToolCall? ToolCall = null,
    [property: JsonPropertyName("terminationReason")] string? TerminationReason = null,
    [property: JsonPropertyName("error")] string? Error = null)
{
    [JsonIgnore]
    public string EffectiveEvent => Event ?? HookEventName ?? "Unknown";

    [JsonIgnore]
    public string SessionID => !string.IsNullOrWhiteSpace(ConversationId) ? ConversationId : "antigravity";

    [JsonIgnore]
    public string Cwd => WorkspacePaths?.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)) ?? "";

    [JsonIgnore]
    public string ProjectName =>
        Path.GetFileName(Cwd.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) is { Length: > 0 } value
            ? value
            : "Antigravity";
}

public sealed record AgyConversationSummary(
    string ConversationId,
    string? Title,
    string? ParentConversationId,
    string? AgentName,
    string? Status);

public static class AgyConversationDatabase
{
    public static string DefaultDbPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".gemini", "antigravity-cli", "conversation_summaries.db");

    public static AgyConversationSummary? Query(string conversationId, string? dbPath = null)
    {
        if (string.IsNullOrWhiteSpace(conversationId)) return null;
        var path = dbPath ?? DefaultDbPath;
        if (!File.Exists(path)) return null;

        try
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Shared,
            };
            using var connection = new SqliteConnection(builder.ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT title, parent_conversation_id, agent_name, status FROM conversation_summaries WHERE conversation_id = @id LIMIT 1";
            command.Parameters.AddWithValue("@id", conversationId);

            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                var title = reader.IsDBNull(0) ? null : reader.GetString(0);
                var parentId = reader.IsDBNull(1) ? null : reader.GetString(1);
                var agentName = reader.IsDBNull(2) ? null : reader.GetString(2);
                var status = reader.IsDBNull(3) ? null : reader.GetString(3);
                return new AgyConversationSummary(conversationId, title, parentId, agentName, status);
            }
        }
        catch
        {
            // Fail-open
        }
        return null;
    }
}

public static class AgyHistoryReader
{
    public static string DefaultHistoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".gemini", "antigravity-cli", "history.jsonl");

    public static (string? Prompt, DateTimeOffset? Timestamp) ExtractLatestPrompt(string conversationId, string? historyPath = null)
    {
        if (string.IsNullOrWhiteSpace(conversationId)) return (null, null);
        var path = historyPath ?? DefaultHistoryPath;
        if (!File.Exists(path)) return (null, null);

        try
        {
            var fileInfo = new FileInfo(path);
            var readSize = (int)Math.Min(fileInfo.Length, 16 * 1024);
            if (readSize <= 0) return (null, null);

            byte[] buffer = new byte[readSize];
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                stream.Seek(-readSize, SeekOrigin.End);
                int bytesRead = stream.Read(buffer, 0, readSize);
                if (bytesRead <= 0) return (null, null);
            }

            var text = Encoding.UTF8.GetString(buffer);
            var lines = text.Split('\n');

            for (var i = lines.Length - 1; i >= 0; i--)
            {
                var line = lines[i].Trim();
                if (line.Length == 0) continue;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("conversationId", out var cIdProp) ||
                        cIdProp.GetString() != conversationId)
                        continue;

                    if (!root.TryGetProperty("display", out var dispProp)) continue;
                    var display = dispProp.GetString();
                    if (string.IsNullOrWhiteSpace(display) || display.StartsWith('/')) continue;

                    DateTimeOffset? timestamp = null;
                    if (root.TryGetProperty("timestamp", out var tsProp) && tsProp.TryGetInt64(out var tsMs))
                    {
                        timestamp = DateTimeOffset.FromUnixTimeMilliseconds(tsMs);
                    }

                    return (display.Trim(), timestamp);
                }
                catch { }
            }
        }
        catch
        {
            // Fail-open
        }

        return (null, null);
    }
}

public static class AgyTranscriptReader
{
    private static readonly Regex UserRequestRegex = new(
        @"<USER_REQUEST>([\s\S]*?)</USER_REQUEST>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MetadataTagRegex = new(
        @"<(?:ADDITIONAL_METADATA|USER_SETTINGS_CHANGE)>[\s\S]*?</(?:ADDITIONAL_METADATA|USER_SETTINGS_CHANGE)>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static (string? UserPrompt, string? LatestResponse, TokenUsage? Tokens) ExtractLatest(string? transcriptPath)
    {
        if (string.IsNullOrWhiteSpace(transcriptPath) || !File.Exists(transcriptPath))
            return (null, null, null);

        try
        {
            var fileInfo = new FileInfo(transcriptPath);
            var readSize = (int)Math.Min(fileInfo.Length, 256 * 1024); // 读取尾部最多 256KB
            if (readSize <= 0) return (null, null, null);

            byte[] buffer = new byte[readSize];
            using (var stream = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                stream.Seek(-readSize, SeekOrigin.End);
                int bytesRead = stream.Read(buffer, 0, readSize);
                if (bytesRead <= 0) return (null, null, null);
            }

            var text = Encoding.UTF8.GetString(buffer);
            var lines = text.Split('\n');
            string? userPrompt = null;
            string? latestResponse = null;
            TokenUsage? tokens = null;
            var foundLatestPlannerInTurn = false;
            var turnBoundaryReached = false;

            for (var index = lines.Length - 1; index >= 0; index--)
            {
                var line = lines[index].Trim();
                if (line.Length == 0) continue;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("type", out var typeProp)) continue;
                    var type = typeProp.GetString();

                    if (!turnBoundaryReached && (type == "USER_INPUT" || type == "USER_EXPLICIT"))
                    {
                        turnBoundaryReached = true;
                        if (userPrompt is null && root.TryGetProperty("content", out var contentProp) && contentProp.GetString() is { Length: > 0 } rawContent)
                        {
                            userPrompt = CleanUserPrompt(rawContent);
                        }
                    }
                    else if (type == "PLANNER_RESPONSE")
                    {
                        // 提取最新模型调用的 Token 用量（首个遇到的 PLANNER_RESPONSE，且严格限于当前回合）
                        if (!turnBoundaryReached && tokens is null)
                        {
                            int input = root.TryGetProperty("input_tokens", out var inProp) && inProp.TryGetInt32(out var inp) ? inp : 0;
                            int cache = root.TryGetProperty("cache_read_tokens", out var cacheProp) && cacheProp.TryGetInt32(out var cp) ? cp : 0;
                            int output = root.TryGetProperty("output_tokens", out var outProp) && outProp.TryGetInt32(out var op) ? op : 0;
                            if (input > 0 || cache > 0 || output > 0)
                            {
                                tokens = new TokenUsage(input, cache, output, 0, input + cache + output);
                            }
                        }

                        // 只有在当前回合内（尚未跨过 USER_INPUT）且是当前回合最新的第一个 PLANNER_RESPONSE，
                        // 且该步骤为纯文本回复（不包含 tool_calls）：
                        // 如果它含有正文文本，才作为正文预览 candidate。
                        // 一旦跨过 USER_INPUT，或者当前回合最新的步骤含有工具调用，latestResponse 严格保持 null！
                        if (!turnBoundaryReached && !foundLatestPlannerInTurn)
                        {
                            foundLatestPlannerInTurn = true;
                            var hasToolCalls = root.TryGetProperty("tool_calls", out var tcProp) &&
                                tcProp.ValueKind == JsonValueKind.Array && tcProp.GetArrayLength() > 0;
                            if (!hasToolCalls && root.TryGetProperty("content", out var contentProp) && contentProp.GetString() is { Length: > 0 } resp)
                            {
                                var normalized = Normalize(resp);
                                if (!string.IsNullOrWhiteSpace(normalized))
                                {
                                    latestResponse = normalized.Length > 120 ? normalized[..120] : normalized;
                                }
                            }
                        }
                    }
                }
                catch { }

                if (userPrompt is not null && turnBoundaryReached)
                    break;
            }

            return (userPrompt, latestResponse, tokens);
        }
        catch
        {
            return (null, null, null);
        }
    }

    private static string? CleanUserPrompt(string raw)
    {
        var match = UserRequestRegex.Match(raw);
        var text = match.Success ? match.Groups[1].Value : raw;
        text = MetadataTagRegex.Replace(text, "");
        var cleaned = Normalize(text);
        if (cleaned is null) return null;
        return cleaned.Length > 1000 ? cleaned[..1000] : cleaned;
    }

    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var result = string.Join(' ', value.Replace('\r', ' ').Replace('\n', ' ')
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return result.Length == 0 ? null : result;
    }
}

public static class AgyEventReducer
{
    private static readonly string[] SpecificArgKeys =
        ["CommandLine", "TargetFile", "AbsolutePath", "filePath", "path", "command", "pattern", "query", "Url", "url", "Action", "TypeName", "Role", "description"];

    public static TaskSnapshot Reduce(AgyHookPayload hook, TaskSnapshot? existing, DateTimeOffset now) =>
        Reduce(hook, existing, null, now);

    public static TaskSnapshot Reduce(AgyHookPayload hook, TaskSnapshot? existing, AgyConversationSummary? dbInfo, DateTimeOffset now)
    {
        var sessionID = hook.SessionID;
        var project = hook.ProjectName;
        dbInfo ??= AgyConversationDatabase.Query(sessionID);
        var sessionTitle = !string.IsNullOrWhiteSpace(dbInfo?.Title) ? dbInfo.Title : null;
        var title = sessionTitle is not null
            ? $"{project} · {sessionTitle}"
            : (existing?.Title?.StartsWith($"{project} · ", StringComparison.Ordinal) == true
                ? existing.Title
                : project);

        var task = existing ?? new TaskSnapshot(
            sessionID,
            AgentSource.Antigravity,
            project,
            title,
            null, null, null, [],
            AgentLifecycle.Running, AgentActivity.Thinking,
            now, now, null,
            false, false, false, []);

        task = task with
        {
            ProjectName = project,
            Title = title,
            UpdatedAt = now,
            ModelName = FormatModelName(hook.ModelName) ?? task.ModelName,
        };

        // 从转录本中提取最新的 Prompt、正文预览与 Token 用量（严格不自造标题覆盖 Title）。
        // 按事件门控（A5）：仅 PreInvocation / PostInvocation / Stop 需要，工具事件跳过整段 256KB 读取。
        string? userPrompt = null;
        string? latestResponse = null;
        TokenUsage? tokens = null;
        if (hook.EffectiveEvent is "PreInvocation" or "PostInvocation" or "Stop" &&
            !string.IsNullOrWhiteSpace(hook.TranscriptPath))
        {
            (userPrompt, latestResponse, tokens) = AgyTranscriptReader.ExtractLatest(hook.TranscriptPath);
            if (userPrompt is not null && !task.PromptQueued)
            {
                task = task with { UserPrompt = userPrompt };
            }
            if (tokens is not null)
            {
                task = task with { TokenUsage = tokens };
            }
        }

        // 判定是否进入新一轮对话（OpenCode 规范）：
        // 1) 首次创建任务；2) 上一轮为终态/闲置；3) 用户提示词发生变化（等待态作答退出除外）；4) 此前处于排队提示词状态
        var wasWaitingBefore = existing?.Lifecycle is AgentLifecycle.WaitingApproval or AgentLifecycle.WaitingAnswer;
        var isNewPrompt = !wasWaitingBefore && existing?.UserPrompt is not null && userPrompt is not null && existing.UserPrompt != userPrompt;
        var wasQueued = existing?.PromptQueued == true;
        var isTerminalBefore = existing is null || existing.Lifecycle is
            AgentLifecycle.Idle or AgentLifecycle.Succeeded or AgentLifecycle.Interrupted or AgentLifecycle.Offline;
        var isNewRound = isNewPrompt || wasQueued || isTerminalBefore;

        // OpenCode 规范：正文回复预览仅在回合真正结束（Stop 且未终态）时写入 LatestStep；
        // 运行期间（PostInvocation/PreToolUse/PostToolUse）绝对不得以旧正文覆盖工具执行步骤
        if (!task.IsTerminal && hook.EffectiveEvent == "Stop" && latestResponse is not null)
        {
            if (task.Activity is null or AgentActivity.Thinking &&
                task.Lifecycle is not (AgentLifecycle.WaitingApproval or AgentLifecycle.WaitingAnswer))
            {
                task = WithStep(task, latestResponse);
            }
        }

        // 检查是否有排队中的新 Prompt（在当前回合启动之后用户新提交的输入）
        if (hook.EffectiveEvent != "PreInvocation" &&
            DetectQueuedPrompt(sessionID, task) is { } queued)
        {
            task = task with
            {
                UserPrompt = queued.Prompt,
                PromptQueued = true,
            };
        }

        switch (hook.EffectiveEvent)
        {
            case "PreInvocation":
            {
                return task with
                {
                    Lifecycle = AgentLifecycle.Running,
                    Activity = AgentActivity.Thinking,
                    StartedAt = isNewRound ? now : task.StartedAt,
                    // 新一轮开聊时清空第三行，让 lifecycle 文案兜底；轮内插入不清空
                    LatestStep = isNewRound ? null : task.LatestStep,
                    CompletedAt = null,
                    Capabilities = [],
                    PromptQueued = false, // 排队接单，标记解除
                };
            }

            case "PreToolUse":
            {
                // 终态忽略任何工具调用启动（防复活守卫）
                if (task.IsTerminal) return task;

                var toolName = hook.ToolCall?.Name ?? "";
                if (toolName.Equals("ask_question", StringComparison.OrdinalIgnoreCase))
                {
                    var question = QuestionText(hook.ToolCall?.Args);
                    return WithStep(task with
                    {
                        Lifecycle = AgentLifecycle.WaitingAnswer,
                        Activity = AgentActivity.Thinking,
                        CompletedAt = null,
                        Capabilities = [],
                    }, question ?? "等待输入与选择");
                }

                // 等待态忽略其他工具调用（包括子代理派生），防止冲掉提问交互
                if (task.Lifecycle is AgentLifecycle.WaitingApproval or AgentLifecycle.WaitingAnswer)
                    return task;

                if (toolName.Equals("invoke_subagent", StringComparison.OrdinalIgnoreCase))
                {
                    // 子代理行只认真实子会话（其 PreInvocation 钩子到达时经父级路由建立，A1：
                    // 哈希占位轨已移除；遗留占位的前缀清理仍保留在 ApplyAgySubagent / PruneAgyGhostTasks）。
                    var step = FormatToolStep(toolName, hook.ToolCall?.Args, hook.Cwd) ?? "派生子代理";
                    return WithStep(task with
                    {
                        Lifecycle = AgentLifecycle.Running,
                        Activity = AgentActivity.Delegating,
                        CompletedAt = null,
                        Capabilities = [],
                    }, step);
                }

                var activity = ActivityFor(toolName, hook.ToolCall?.Args);
                var stepStr = FormatToolStep(toolName, hook.ToolCall?.Args, hook.Cwd);

                return task with
                {
                    Lifecycle = AgentLifecycle.Running,
                    Activity = activity,
                    LatestStep = WithStep(task, stepStr).LatestStep,
                    CompletedAt = null,
                    Capabilities = [],
                };
            }

            case "PostToolUse":
            {
                if (task.IsTerminal) return task;
                var toolName = hook.ToolCall?.Name ?? "";

                // 如果是 ask_question，PostToolUse 意味着用户回答完毕已放行
                if (toolName.Equals("ask_question", StringComparison.OrdinalIgnoreCase))
                {
                    return task with
                    {
                        Lifecycle = AgentLifecycle.Running,
                        Activity = AgentActivity.Thinking,
                        CompletedAt = null,
                    };
                }

                // 等待回答/审批期间，迟到的其他工具一律忽略（OpenCode 守卫 ①：乱序防御）
                if (task.Lifecycle is AgentLifecycle.WaitingApproval or AgentLifecycle.WaitingAnswer)
                    return task;

                if (!string.IsNullOrWhiteSpace(hook.Error))
                {
                    return WithStep(task with
                    {
                        Lifecycle = AgentLifecycle.Running,
                        Activity = AgentActivity.Thinking,
                        CompletedAt = null,
                    }, $"✗ {NormalizeText(hook.Error)}");
                }

                return task with
                {
                    Lifecycle = AgentLifecycle.Running,
                    Activity = task.Subagents.Any(s => !s.IsTerminal) ? AgentActivity.Delegating : AgentActivity.Thinking,
                    CompletedAt = null,
                };
            }

            case "PostInvocation":
            {
                if (task.IsTerminal) return task;
                if (task.Lifecycle is AgentLifecycle.WaitingApproval or AgentLifecycle.WaitingAnswer) return task;

                // 乱序守卫：若已有工具正在活动（非 Thinking），保持工具活动，绝不被 PostInvocation 洗回 Thinking
                var activity = task.Activity is not null and not AgentActivity.Thinking
                    ? task.Activity
                    : (task.Subagents.Any(s => !s.IsTerminal) ? AgentActivity.Delegating : AgentActivity.Thinking);

                return task with
                {
                    Lifecycle = AgentLifecycle.Running,
                    Activity = activity,
                    CompletedAt = null,
                };
            }

            case "Stop":
            {
                var isError = hook.TerminationReason == "error" || !string.IsNullOrWhiteSpace(hook.Error);
                // 前提说明：此处的子代理随父任务终态化建立在 Antigravity 当前 invoke_subagent 为同步回合语义
                // （子代理生命周期随父任务回合结束而收敛）的前提下。若未来 agy 支持跨轮次的独立异步守护子代理，
                // 则需改为由子代理独立会话的 Stop 钩子进行收敛。
                var finalizedSubagents = task.Subagents.Select(s => s.IsTerminal ? s : s with
                {
                    Lifecycle = isError ? AgentLifecycle.Interrupted : AgentLifecycle.Succeeded,
                    Activity = null,
                    UpdatedAt = now,
                }).ToList();

                if (isError)
                {
                    return task with
                    {
                        Lifecycle = AgentLifecycle.Interrupted,
                        Activity = null,
                        Subagents = finalizedSubagents,
                        LatestStep = WithStep(task, $"✗ {hook.Error ?? hook.TerminationReason ?? "执行异常终止"}").LatestStep,
                        CompletedAt = now,
                        IsUnread = true,
                        Capabilities = [],
                        PromptQueued = false,
                    };
                }

                return task with
                {
                    Lifecycle = task.Lifecycle == AgentLifecycle.Interrupted ? AgentLifecycle.Interrupted : AgentLifecycle.Succeeded,
                    Activity = null,
                    Subagents = finalizedSubagents,
                    CompletedAt = now,
                    IsUnread = true,
                    Capabilities = [],
                    PromptQueued = false,
                };
            }

            default:
                return task;
        }
    }

    /// <summary>排队 prompt 检测（事件侧与 Maintain 心跳共用，A3）：运行中且 history.jsonl
    /// 出现晚于本轮起点的新 prompt 时返回该 prompt。</summary>
    public static (string Prompt, DateTimeOffset Timestamp)? DetectQueuedPrompt(string sessionID, TaskSnapshot task)
    {
        if (task.Lifecycle != AgentLifecycle.Running) return null;
        var (prompt, timestamp) = AgyHistoryReader.ExtractLatestPrompt(sessionID);
        if (prompt is null || timestamp is null) return null;
        if (timestamp.Value <= task.StartedAt || prompt == task.UserPrompt) return null;
        return (prompt, timestamp.Value);
    }

    /// <summary>Stop 终稿落盘竞速补救（条目 1）：重读转录本，有纯文本终稿时经回显守卫补写第三行；
    /// 暂无则返回 null（由调用方保留登记、下个心跳再试）。</summary>
    public static TaskSnapshot? TryApplyLatePreview(TaskSnapshot task, string transcriptPath)
    {
        var (_, preview, _) = AgyTranscriptReader.ExtractLatest(transcriptPath);
        if (preview is null) return null;
        return WithStep(task, preview);
    }

    public static string? QuestionText(JsonElement? args)
    {
        if (args is not { ValueKind: JsonValueKind.Object } value) return null;
        if (value.TryGetProperty("questions", out var questions) && questions.ValueKind == JsonValueKind.Array)
        {
            var first = questions.EnumerateArray().FirstOrDefault();
            if (first.ValueKind == JsonValueKind.Object && first.TryGetProperty("question", out var q))
                return NormalizeText(q.GetString());
        }
        if (value.TryGetProperty("toolSummary", out var toolSummary))
            return NormalizeText(toolSummary.GetString());
        return null;
    }

    public static List<string> QuestionOptions(JsonElement? args)
    {
        var result = new List<string>();
        if (args is not { ValueKind: JsonValueKind.Object } value) return result;
        if (value.TryGetProperty("questions", out var questions) && questions.ValueKind == JsonValueKind.Array)
        {
            var first = questions.EnumerateArray().FirstOrDefault();
            if (first.ValueKind == JsonValueKind.Object && first.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array)
            {
                foreach (var opt in options.EnumerateArray())
                {
                    if (opt.GetString() is { Length: > 0 } text) result.Add(text);
                }
            }
        }
        return result;
    }

    public static string? FormatToolStep(string? toolName, JsonElement? args, string cwd = "")
    {
        if (string.IsNullOrWhiteSpace(toolName)) return null;
        var tool = toolName.Trim();
        var detail = ExtractToolDetail(tool, args, cwd);
        return ToolStep.Compose(tool, detail);
    }

    public static string? ExtractToolDetail(string toolName, JsonElement? args, string cwd)
    {
        if (args is not { ValueKind: JsonValueKind.Object } value) return null;

        var lower = toolName.ToLowerInvariant();
        if (lower == "run_command" && value.TryGetProperty("CommandLine", out var cmd))
            return NormalizeText(cmd.GetString());

        if (lower is "view_file" or "read_resource" && value.TryGetProperty("AbsolutePath", out var absPath))
            return RelativizePath(absPath.GetString(), cwd);

        if (lower is "write_to_file" or "replace_file_content" && value.TryGetProperty("TargetFile", out var target))
            return RelativizePath(target.GetString(), cwd);

        if (lower is "search_web" && value.TryGetProperty("query", out var query))
            return NormalizeText(query.GetString());

        if (lower is "read_url_content" && value.TryGetProperty("Url", out var url))
            return NormalizeText(url.GetString());

        if (lower is "call_mcp_tool")
        {
            var server = value.TryGetProperty("ServerName", out var s) ? s.GetString() : null;
            var tName = value.TryGetProperty("ToolName", out var t) ? t.GetString() : null;
            if (server is not null && tName is not null) return $"{server}.{tName}";
            if (tName is not null) return tName;
        }

        if (lower is "manage_subagents" && value.TryGetProperty("Action", out var act))
            return NormalizeText(act.GetString());

        if (lower is "invoke_subagent" && value.TryGetProperty("Subagents", out var subs) && subs.ValueKind == JsonValueKind.Array)
        {
            var first = subs.EnumerateArray().FirstOrDefault();
            if (first.ValueKind == JsonValueKind.Object)
            {
                var role = first.TryGetProperty("Role", out var r) ? r.GetString() : null;
                var type = first.TryGetProperty("TypeName", out var t) ? t.GetString() : null;
                return NormalizeText(role ?? type ?? "subagent");
            }
        }

        // 白名单参数扫描（严格不含 Prompt）
        foreach (var key in SpecificArgKeys)
        {
            if (value.TryGetProperty(key, out var prop) && prop.GetString() is { Length: > 0 } text)
            {
                var clean = NormalizeText(text);
                if (!string.IsNullOrWhiteSpace(clean)) return clean;
            }
        }

        // 兜底：使用 toolAction 或 toolSummary（过滤与 Label 重复的前缀动词）
        if (value.TryGetProperty("toolAction", out var action) && action.GetString() is { Length: > 0 } actStr)
            return CleanActionVerb(toolName, NormalizeText(actStr));

        if (value.TryGetProperty("toolSummary", out var summary) && summary.GetString() is { Length: > 0 } sumStr)
            return CleanActionVerb(toolName, NormalizeText(sumStr));

        return null;
    }

    private static string? RelativizePath(string? path, string cwd)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var normalized = path.Replace('\\', '/').Trim();
        if (!string.IsNullOrWhiteSpace(cwd))
        {
            var normalizedCwd = cwd.Replace('\\', '/').TrimEnd('/') + "/";
            if (normalized.StartsWith(normalizedCwd, StringComparison.OrdinalIgnoreCase))
                return normalized[normalizedCwd.Length..];
        }
        var leaf = Path.GetFileName(normalized);
        return string.IsNullOrEmpty(leaf) ? normalized : leaf;
    }

    private static string? CleanActionVerb(string toolName, string? action)
    {
        if (string.IsNullOrWhiteSpace(action)) return null;
        var label = ToolStep.Label(toolName);
        if (action.StartsWith(label + " ", StringComparison.OrdinalIgnoreCase))
            action = action[(label.Length + 1)..].Trim();
        return action;
    }

    public static AgentActivity ActivityFor(string toolName, JsonElement? args)
    {
        var lower = toolName.ToLowerInvariant();
        if (lower is "view_file" or "read_url_content" or "read_resource" or "list_resources")
            return AgentActivity.Reading;

        if (lower is "search_web")
            return AgentActivity.Browsing;

        if (lower is "write_to_file" or "replace_file_content")
            return AgentActivity.Editing;

        if (lower is "run_command")
        {
            var commandLine = args is { ValueKind: JsonValueKind.Object } val &&
                              val.TryGetProperty("CommandLine", out var cmd) ? cmd.GetString() : null;
            return ShellTestRun.Matches(commandLine) ? AgentActivity.Testing : AgentActivity.Executing;
        }

        if (lower is "invoke_subagent" or "send_message" or "manage_subagents")
            return AgentActivity.Delegating;

        if (lower.Contains("search") || lower.Contains("find") || lower.Contains("query"))
            return AgentActivity.Searching;

        if (lower.Contains("read") || lower.Contains("view"))
            return AgentActivity.Reading;

        if (lower.Contains("write") || lower.Contains("edit") || lower.Contains("patch"))
            return AgentActivity.Editing;

        return AgentActivity.Executing;
    }

    private static TaskSnapshot WithStep(TaskSnapshot task, string? candidate)
    {
        if (candidate is null) return task;
        // 守卫：候选文本若原样包含当前用户 Prompt（≥4 字），或与 Prompt 相同，判定为模型回显，予以丢弃
        var normCandidate = NormalizeText(candidate);
        var normPrompt = NormalizeText(task.UserPrompt);
        if (normCandidate is not null && normPrompt is not null)
        {
            if (normCandidate.Equals(normPrompt, StringComparison.OrdinalIgnoreCase)) return task;
            if (normPrompt.Length >= 4 && normCandidate.Contains(normPrompt, StringComparison.OrdinalIgnoreCase)) return task;
        }

        var clamped = candidate.Length > 220 ? candidate[..220] : candidate;
        return task with { LatestStep = clamped };
    }

    private static string? FormatModelName(string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName) || modelName.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return null;
        return modelName.Split('/').Last().Trim();
    }

    private static string? NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var result = string.Join(' ', value.Replace('\r', ' ').Replace('\n', ' ')
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return result.Length == 0 ? null : result;
    }
}

public enum AntigravityPluginStatus { NotInstalled, Installed, Outdated }

public sealed class AgyHookConfiguration(string? configDirectory = null)
{
    public const string ManagedMarker = "Managed by AgentPager (Antigravity)";
    public const string TagName = "agentpager";
    public const string ScriptFileName = "agentpager-antigravity.js";
    private const string ResourceName = "AgentPager.Core.Resources.agentpager-antigravity.js";

    private static readonly Regex VersionPattern = new(
        @"^//\s*agentpager-antigravity\s+v(\d+)\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly Lazy<string> EmbeddedContent = new(LoadEmbedded);

    public string ConfigDirectory { get; } = configDirectory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "config");

    public string HooksConfigPath => Path.Combine(ConfigDirectory, "hooks.json");
    public string ScriptPath => Path.Combine(ConfigDirectory, ScriptFileName);

    public static int? EmbeddedVersion => ParseVersion(EmbeddedContent.Value);

    public int? InstalledVersion
    {
        get
        {
            try { return File.Exists(ScriptPath) ? ParseVersion(File.ReadAllText(ScriptPath)) : null; }
            catch { return null; }
        }
    }

    public AntigravityPluginStatus Status
    {
        get
        {
            if (!File.Exists(HooksConfigPath))
                return AntigravityPluginStatus.NotInstalled;

            try
            {
                var hooksJson = File.ReadAllText(HooksConfigPath, Encoding.UTF8);
                var root = JsonNode.Parse(hooksJson) as JsonObject;
                var hasHook = root?[TagName] is JsonObject;

                if (!hasHook && !File.Exists(ScriptPath))
                    return AntigravityPluginStatus.NotInstalled;

                if (!hasHook || !File.Exists(ScriptPath))
                    return AntigravityPluginStatus.Outdated;

                var scriptMatch = Normalize(File.ReadAllText(ScriptPath)) == Normalize(EmbeddedContent.Value);
                if (!scriptMatch) return AntigravityPluginStatus.Outdated;

                var agyNode = root![TagName] as JsonObject;
                var hasAllEvents = agyNode?["PreToolUse"] is not null
                    && agyNode?["PostToolUse"] is not null
                    && agyNode?["PreInvocation"] is not null
                    && agyNode?["PostInvocation"] is not null
                    && agyNode?["Stop"] is not null;

                if (!hasAllEvents) return AntigravityPluginStatus.Outdated;

                return AntigravityPluginStatus.Installed;
            }
            catch
            {
                return AntigravityPluginStatus.Outdated;
            }
        }
    }

    public bool IsInstalled() => Status != AntigravityPluginStatus.NotInstalled;

    public HookConfigurationChange Install()
    {
        Directory.CreateDirectory(ConfigDirectory);

        // 1. 安装/更新 agentpager-antigravity.js
        var scriptContent = EmbeddedContent.Value;
        var existingScript = File.Exists(ScriptPath) ? File.ReadAllText(ScriptPath) : null;
        if (existingScript is null || Normalize(existingScript) != Normalize(scriptContent))
        {
            File.WriteAllText(ScriptPath, scriptContent, new UTF8Encoding(false));
        }

        // 2. 写入 ~/.gemini/config/hooks.json（统一正斜杠路径，杜绝转义与 BOM 问题）
        var normalizedScriptPath = ScriptPath.Replace('\\', '/');
        var hookCommand = $"node {normalizedScriptPath}";

        return MutateHooks(root =>
        {
            var agyConfig = new JsonObject
            {
                ["enabled"] = true,
                ["PreToolUse"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["matcher"] = "*",
                        ["hooks"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["type"] = "command",
                                ["command"] = $"{hookCommand} PreToolUse",
                                ["timeout"] = 5,
                            }
                        }
                    }
                },
                ["PostToolUse"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["matcher"] = "*",
                        ["hooks"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["type"] = "command",
                                ["command"] = $"{hookCommand} PostToolUse",
                                ["timeout"] = 5,
                            }
                        }
                    }
                },
                ["PreInvocation"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "command",
                        ["command"] = $"{hookCommand} PreInvocation",
                        ["timeout"] = 5,
                    }
                },
                ["PostInvocation"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "command",
                        ["command"] = $"{hookCommand} PostInvocation",
                        ["timeout"] = 5,
                    }
                },
                ["Stop"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "command",
                        ["command"] = $"{hookCommand} Stop",
                        ["timeout"] = 5,
                    }
                },
            };

            root[TagName] = agyConfig;
            return root;
        });
    }

    public HookConfigurationChange Uninstall()
    {
        if (File.Exists(ScriptPath))
        {
            try
            {
                var content = File.ReadAllText(ScriptPath);
                if (content.Contains(ManagedMarker, StringComparison.Ordinal))
                    File.Delete(ScriptPath);
            }
            catch { }
        }

        return MutateHooks(root =>
        {
            root.Remove(TagName);
            return root;
        }, deleteWhenEmpty: true);
    }

    private HookConfigurationChange MutateHooks(Func<JsonObject, JsonObject> mutation, bool deleteWhenEmpty = false)
    {
        var original = File.Exists(HooksConfigPath) ? File.ReadAllText(HooksConfigPath, Encoding.UTF8) : null;
        JsonObject root;
        try { root = original is null ? [] : JsonNode.Parse(original)?.AsObject() ?? []; }
        catch (Exception error) { throw new InvalidDataException("Antigravity hooks.json 不是有效的 JSON。", error); }

        var result = mutation(root);
        var output = result.Count == 0 && deleteWhenEmpty
            ? null
            : result.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        if (Normalize(original) == Normalize(output)) return new(false);

        Directory.CreateDirectory(Path.GetDirectoryName(HooksConfigPath)!);
        var backup = original is null ? null : Backup(original);

        if (output is null)
        {
            File.Delete(HooksConfigPath);
        }
        else
        {
            // Windows 文件写入红线：必须使用 UTF-8 No-BOM
            File.WriteAllText(HooksConfigPath, output + Environment.NewLine, new UTF8Encoding(false));
        }

        return new(true, backup);
    }

    private string Backup(string contents)
    {
        var stem = $"{HooksConfigPath}.agentpager-backup-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        var path = stem;
        for (var index = 1; File.Exists(path); index++) path = $"{stem}-{index}";
        File.WriteAllText(path, contents, new UTF8Encoding(false));
        return path;
    }

    private static string LoadEmbedded()
    {
        using var stream = typeof(AgyHookConfiguration).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("缺少 Antigravity 胶水脚本嵌入资源。");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("\r\n", "\n");
    }

    private static int? ParseVersion(string contents)
    {
        var match = VersionPattern.Match(contents);
        return match.Success && int.TryParse(match.Groups[1].Value, out var version) ? version : null;
    }

    private static string? Normalize(string? value) => value?.Replace("\r\n", "\n").Trim();
}
