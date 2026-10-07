using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentPager.Core;

public sealed record CodexRolloutSignal(
    string SessionID,
    string Cwd,
    AgentLifecycle? Lifecycle,
    AgentActivity? Activity,
    PendingRequestKind? RequestKind,
    string? Summary,
    string? UserPrompt,
    string? LatestStep,
    TokenUsage? TokenUsage,
    string? SubagentID,
    string? SubagentPath,
    DateTimeOffset Timestamp);

public sealed record TaskCatalogProjection(
    ulong Revision,
    List<TaskSnapshot> Tasks,
    string? FocusedTaskID,
    List<PendingRequest> PendingRequests);

public interface ICodexPermissionResolver
{
    bool Resolve(string sessionID, CodexPermissionDecision decision);
}

public sealed class TaskCatalog
{
    private readonly List<TaskSnapshot> _tasks;
    private readonly Dictionary<string, PendingRequest> _requests;
    private readonly int _capacity;
    private ulong _revision;

    public TaskCatalog(IEnumerable<TaskSnapshot>? restoring = null, int capacity = 20)
    {
        _tasks = restoring?.ToList() ?? [];
        _requests = [];
        _capacity = capacity;
        Normalize(DateTimeOffset.UtcNow);
    }

    public TaskCatalogProjection Projection(string? focusedTaskIDOverride = null)
    {
        var tasks = _tasks.OrderByDescending(task => task.UpdatedAt).ToList();
        return new(
            _revision,
            tasks,
            focusedTaskIDOverride ?? tasks.MaxBy(task => (task.EffectivePriority, task.UpdatedAt))?.Id,
            _requests.Values.OrderBy(request => request.TaskID).ToList());
    }

    public bool Accept(CodexHookPayload hook, DateTimeOffset? receivedAt = null)
    {
        var now = receivedAt ?? DateTimeOffset.UtcNow;
        var existing = _tasks.FirstOrDefault(task => task.Id == hook.SessionID);
        Upsert(CodexEventReducer.Reduce(hook, existing, now));
        switch (hook.HookEventName)
        {
            case CodexHookEventName.PermissionRequest:
                _requests[hook.SessionID] = new(hook.SessionID, PendingRequestKind.Approval, hook.ToolInput?.Summary, []);
                break;
            case CodexHookEventName.Stop:
                _requests.Remove(hook.SessionID);
                break;
            default:
                if (_tasks.First(task => task.Id == hook.SessionID).Lifecycle == AgentLifecycle.Running)
                    _requests.Remove(hook.SessionID);
                break;
        }
        Changed();
        return true;
    }

    public bool Accept(ClaudeHookPayload hook, DateTimeOffset? receivedAt = null)
    {
        var now = receivedAt ?? DateTimeOffset.UtcNow;
        var existing = _tasks.FirstOrDefault(task => task.Id == hook.SessionID);
        Upsert(ClaudeEventReducer.Reduce(hook, existing, now));
        switch (hook.HookEventName)
        {
            case "PermissionRequest":
                _requests[hook.SessionID] = new(
                    hook.SessionID,
                    PendingRequestKind.Approval,
                    ClaudeEventReducer.Summary(hook.ToolInput) ?? hook.ToolName,
                    []);
                break;
            case "Notification":
                if (ClaudeEventReducer.IsAnswerNotification(hook.NotificationType))
                    _requests[hook.SessionID] = new(
                        hook.SessionID,
                        PendingRequestKind.Question,
                        hook.Title ?? hook.Message,
                        []);
                break;
            case "Stop":
            case "SessionEnd":
            case "PermissionDenied":
                _requests.Remove(hook.SessionID);
                break;
            default:
                if (_tasks.First(task => task.Id == hook.SessionID).Lifecycle == AgentLifecycle.Running)
                    _requests.Remove(hook.SessionID);
                break;
        }
        Changed();
        return true;
    }

    public bool Accept(OpenCodeHookPayload hook, DateTimeOffset? receivedAt = null)
    {
        var now = receivedAt ?? DateTimeOffset.UtcNow;
        var sessionID = OpenCodeEventReducer.SessionID(hook);
        if (sessionID is null) return false;
        var existing = _tasks.FirstOrDefault(task => task.Id == sessionID);
        if (existing is null && hook.Event is "plugin.dispose" or "session.deleted" or "subagent") return false;
        Upsert(OpenCodeEventReducer.Reduce(hook, existing, now));
        switch (hook.Event)
        {
            case "permission.asked":
                _requests[sessionID] = new(
                    sessionID,
                    PendingRequestKind.Approval,
                    OpenCodeEventReducer.PermissionSummary(hook.Properties),
                    []);
                break;
            case "question.asked":
                _requests[sessionID] = new(
                    sessionID,
                    PendingRequestKind.Question,
                    OpenCodeEventReducer.QuestionText(hook.Properties),
                    OpenCodeEventReducer.QuestionOptions(hook.Properties));
                break;
            case "permission.replied":
            case "question.replied":
            case "question.rejected":
            case "session.idle":
            case "session.error":
                _requests.Remove(sessionID);
                break;
            default:
                if (_tasks.First(task => task.Id == sessionID).Lifecycle is not
                    (AgentLifecycle.Starting or AgentLifecycle.WaitingApproval or AgentLifecycle.WaitingAnswer))
                    _requests.Remove(sessionID);
                break;
        }
        Changed();
        return true;
    }

    public bool Accept(IEnumerable<CodexRolloutSignal> values)
    {
        var changed = false;
        foreach (var signal in values.OrderBy(value => value.Timestamp))
        {
            Apply(signal);
            changed = true;
        }
        if (changed) Changed();
        return changed;
    }

    public bool Maintain(DateTimeOffset? now = null)
    {
        var before = _tasks.Count + _tasks.Sum(task => task.Subagents.Count);
        Normalize(now ?? DateTimeOffset.UtcNow);
        if (before == _tasks.Count + _tasks.Sum(task => task.Subagents.Count)) return false;
        Changed();
        return true;
    }

    public ControlAckPayload Perform(Guid requestID, ControlPayload control, ICodexPermissionResolver resolver)
    {
        var task = _tasks.FirstOrDefault(candidate => candidate.Id == control.TaskID);
        if (task is null) return new(requestID, ControlResult.Stale, "任务已结束或不存在");

        switch (control.Action)
        {
            case ControlAction.Approve:
            case ControlAction.Deny:
                {
                    var capability = control.Action == ControlAction.Approve ? TaskCapability.Approve : TaskCapability.Deny;
                    if (!task.Capabilities.Contains(capability) ||
                        !_requests.TryGetValue(task.Id, out var request) ||
                        request.Kind != PendingRequestKind.Approval)
                        return new(requestID, ControlResult.Unsupported, "当前任务不能处理该审批");
                    var decision = control.Action == ControlAction.Approve
                        ? CodexPermissionDecision.Allow
                        : CodexPermissionDecision.Deny;
                    if (!resolver.Resolve(task.Id, decision))
                        return new(requestID, ControlResult.Rejected, "Hook 审批通道已失效");
                    _requests.Remove(task.Id);
                    var now = DateTimeOffset.UtcNow;
                    Upsert(task with
                    {
                        Lifecycle = decision == CodexPermissionDecision.Allow
                            ? AgentLifecycle.Running
                            : AgentLifecycle.Interrupted,
                        Activity = decision == CodexPermissionDecision.Allow ? AgentActivity.Thinking : null,
                        UpdatedAt = now,
                        CompletedAt = decision == CodexPermissionDecision.Allow ? null : now,
                        IsUnread = decision == CodexPermissionDecision.Deny || task.IsUnread,
                        Capabilities = [],
                    });
                    Changed();
                    return new(requestID, ControlResult.Accepted, null);
                }
            case ControlAction.Mute:
                Upsert(task with { IsMuted = !task.IsMuted, UpdatedAt = DateTimeOffset.UtcNow });
                break;
            case ControlAction.MarkRead:
                Upsert(task with { IsUnread = false, UpdatedAt = DateTimeOffset.UtcNow });
                break;
            case ControlAction.Pin:
                Upsert(task with { IsPinned = !task.IsPinned, UpdatedAt = DateTimeOffset.UtcNow });
                break;
            default:
                return new(requestID, ControlResult.Unsupported, "当前 Codex 通道暂未提供稳定能力");
        }
        Changed();
        return new(requestID, ControlResult.Accepted, null);
    }

    public bool ApplyTitles(IReadOnlyDictionary<string, string> titles)
    {
        var changed = false;
        for (var index = 0; index < _tasks.Count; index++)
        {
            var task = _tasks[index];
            if (!titles.TryGetValue(task.Id, out var codexTitle)) continue;
            var prefix = $"{task.ProjectName} · ";
            var title = codexTitle == task.ProjectName || codexTitle.StartsWith(prefix, StringComparison.Ordinal)
                ? codexTitle
                : prefix + codexTitle;
            if (title == task.Title) continue;
            _tasks[index] = task with { Title = title };
            changed = true;
        }
        if (changed) Changed();
        return changed;
    }

    private void Apply(CodexRolloutSignal signal)
    {
        var task = _tasks.FirstOrDefault(value => value.Id == signal.SessionID) ??
            NewTask(signal.SessionID, signal.Cwd, signal.Lifecycle ?? AgentLifecycle.Running, signal.Timestamp);

        if (signal.SubagentID is not null)
        {
            ApplySubagent(task, signal);
            return;
        }

        var current = signal.Timestamp >= task.UpdatedAt;
        var lifecycle = signal.Lifecycle is not null && (current || task.Lifecycle == AgentLifecycle.Starting)
            ? signal.Lifecycle.Value : task.Lifecycle;
        var activity = signal.Activity is not null && (current || task.Activity is null)
            ? signal.Activity : task.Activity;
        var prompt = signal.UserPrompt is not null && (current || task.UserPrompt is null)
            ? signal.UserPrompt : task.UserPrompt;
        var title = task.Title;
        if (prompt is not null && title == task.ProjectName)
            title = CodexEventReducer.Title(task.ProjectName, prompt);
        var latestStep = signal.LatestStep is not null && (current || task.LatestStep is null)
            ? signal.LatestStep : task.LatestStep;
        var usage = signal.TokenUsage is not null &&
                    signal.TokenUsage.Total >= (task.TokenUsage?.Total ?? 0)
            ? signal.TokenUsage : task.TokenUsage;
        var updatedAt = signal.Timestamp > task.UpdatedAt ? signal.Timestamp : task.UpdatedAt;
        var terminal = lifecycle is AgentLifecycle.Succeeded or AgentLifecycle.Interrupted;

        task = task with
        {
            Lifecycle = lifecycle,
            Activity = activity,
            UserPrompt = prompt,
            Title = title,
            LatestStep = latestStep,
            TokenUsage = usage,
            UpdatedAt = updatedAt,
            CompletedAt = terminal ? Max(task.CompletedAt, signal.Timestamp) : current ? null : task.CompletedAt,
            IsUnread = terminal || task.IsUnread,
        };
        if (current) task = ApplyRequest(task, signal);
        Upsert(task);
    }

    private TaskSnapshot ApplyRequest(TaskSnapshot task, CodexRolloutSignal signal)
    {
        if (signal.RequestKind == PendingRequestKind.Approval)
        {
            _requests[task.Id] = new(task.Id, PendingRequestKind.Approval, signal.Summary, []);
            return task with
            {
                Capabilities = task.Capabilities.Where(value =>
                    value is TaskCapability.Approve or TaskCapability.Deny).ToHashSet(),
            };
        }
        if (signal.RequestKind == PendingRequestKind.Question)
        {
            _requests[task.Id] = new(task.Id, PendingRequestKind.Question, signal.Summary, []);
            return task with { Capabilities = [] };
        }
        if (signal.Lifecycle == AgentLifecycle.Running || task.IsTerminal)
        {
            _requests.Remove(task.Id);
            return task with { Capabilities = [] };
        }
        return task;
    }

    private void ApplySubagent(TaskSnapshot task, CodexRolloutSignal signal)
    {
        var id = signal.SubagentID!;
        var path = signal.SubagentPath ??
                   task.Subagents.FirstOrDefault(value => value.Id == id)?.Path ??
                   "subagent";
        var subagent = task.Subagents.FirstOrDefault(value => value.Id == id) ??
            new(id, path, SubagentSnapshot.NameFromPath(path),
                signal.Lifecycle ?? AgentLifecycle.Running,
                signal.Activity ?? AgentActivity.Thinking,
                null, null, signal.Timestamp, signal.Timestamp);
        var current = signal.Timestamp >= subagent.UpdatedAt;
        subagent = subagent with
        {
            Path = path,
            DisplayName = SubagentSnapshot.NameFromPath(path),
            Lifecycle = signal.Lifecycle is not null && current ? signal.Lifecycle.Value : subagent.Lifecycle,
            Activity = signal.Activity is not null && (current || subagent.Activity is null)
                ? signal.Activity : subagent.Activity,
            LatestStep = signal.LatestStep is not null && (current || subagent.LatestStep is null)
                ? signal.LatestStep : subagent.LatestStep,
            TokenUsage = signal.TokenUsage is not null &&
                         signal.TokenUsage.Total >= (subagent.TokenUsage?.Total ?? 0)
                ? signal.TokenUsage : subagent.TokenUsage,
            UpdatedAt = signal.Timestamp > subagent.UpdatedAt ? signal.Timestamp : subagent.UpdatedAt,
        };
        var subagents = task.Subagents.Where(value => value.Id != id).Append(subagent)
            .OrderBy(value => value.IsTerminal).ThenBy(value => value.StartedAt).ToList();
        Upsert(task with
        {
            Subagents = subagents,
            UpdatedAt = signal.Timestamp > task.UpdatedAt ? signal.Timestamp : task.UpdatedAt,
            Activity = task.Lifecycle == AgentLifecycle.Running
                ? subagents.Any(value => !value.IsTerminal) ? AgentActivity.Delegating : AgentActivity.Thinking
                : task.Activity,
        });
    }

    private void Normalize(DateTimeOffset now)
    {
        _tasks.RemoveAll(task =>
            task.Id.StartsWith("agentgrid-", StringComparison.Ordinal) && now - task.UpdatedAt >= TimeSpan.FromMinutes(1) ||
            task.IsTerminal && task.CompletedAt is not null && now - task.CompletedAt > TimeSpan.FromMinutes(30));
        for (var index = 0; index < _tasks.Count; index++)
            _tasks[index] = _tasks[index] with
            {
                Subagents = _tasks[index].Subagents
                    .Where(value => !value.IsTerminal || now - value.UpdatedAt < TimeSpan.FromSeconds(4)).ToList(),
            };
        foreach (var key in _requests.Keys.Where(key => _tasks.All(task => task.Id != key)).ToList())
            _requests.Remove(key);
        while (_tasks.Count > _capacity)
        {
            var removable = _tasks.Where(task => task.IsTerminal).MinBy(task => task.UpdatedAt);
            if (removable is null) break;
            _tasks.Remove(removable);
        }
    }

    private void Changed()
    {
        _revision++;
        Normalize(_tasks.Select(task => task.UpdatedAt).DefaultIfEmpty(DateTimeOffset.UtcNow).Max());
    }

    private void Upsert(TaskSnapshot task)
    {
        var index = _tasks.FindIndex(value => value.Id == task.Id);
        if (index < 0) _tasks.Add(task);
        else _tasks[index] = task;
    }

    private static TaskSnapshot NewTask(string id, string cwd, AgentLifecycle lifecycle, DateTimeOffset now)
    {
        var project = Path.GetFileName(cwd.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(project)) project = "Codex";
        return new(id, AgentSource.CodexCLI, project, project, null, null, null, [],
            lifecycle, null, now, now, null, false, false, false, []);
    }

    private static DateTimeOffset Max(DateTimeOffset? left, DateTimeOffset right) =>
        left is not null && left > right ? left.Value : right;
}

/// <summary>第三行工具步骤的统一显示：`工具名 内容`；命令类工具同样带工具名，内容已带工具名时不重复。</summary>
internal static class ToolStep
{
    public static string? Compose(string? toolName, string? detail)
    {
        var tool = Normalize(toolName);
        var text = Normalize(detail);
        if (tool is null) return text is null ? null : Clamp(text);
        var label = Label(tool);
        if (text is null) return Clamp(label);
        if (text.Equals(label, StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith(label + " ", StringComparison.OrdinalIgnoreCase))
            return Clamp(text);
        return Clamp($"{label} {text}");
    }

    public static string Label(string toolName) => toolName.Split('.').Last().ToLowerInvariant() switch
    {
        "bash" or "shell" or "exec" or "exec_command" or "terminal_interaction" => "Bash",
        "apply_patch" or "patch" => "Patch",
        "edit" => "Edit",
        "write" => "Write",
        "read" or "read_file" or "view" => "Read",
        "glob" => "Glob",
        "grep" => "Grep",
        "task" => "Task",
        "webfetch" => "WebFetch",
        "websearch" => "WebSearch",
        "todowrite" or "todoread" or "todo" => "Todo",
        "skill" => "Skill",
        "lsp" => "LSP",
        "question" => "Question",
        _ => toolName,
    };

    private static string? Normalize(string? value)
    {
        if (value is null) return null;
        var result = string.Join(' ', value.Replace('\n', ' ').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return result.Length == 0 ? null : result;
    }

    private static string Clamp(string value) => value.Length > 220 ? value[..220] : value;
}

public static class CodexEventReducer
{
    public static TaskSnapshot Reduce(CodexHookPayload hook, TaskSnapshot? existing, DateTimeOffset now)
    {
        var task = existing ?? new(
            hook.SessionID,
            hook.Source == "app" ? AgentSource.CodexDesktop : AgentSource.CodexCLI,
            hook.ProjectName,
            hook.ProjectName,
            null, null, null, [],
            AgentLifecycle.Starting, null,
            now, now, null,
            false, false, false, []);
        task = task with { ProjectName = hook.ProjectName, UpdatedAt = now };
        return hook.HookEventName switch
        {
            CodexHookEventName.SessionStart => task with
            {
                Lifecycle = AgentLifecycle.Starting,
                Activity = AgentActivity.Thinking,
                Capabilities = [],
            },
            CodexHookEventName.UserPromptSubmit => UserPrompt(task, hook.Prompt),
            CodexHookEventName.PreToolUse => task with
            {
                Lifecycle = AgentLifecycle.Running,
                Activity = ActivityFor(hook.ToolName),
                LatestStep = LatestStep(hook.ToolName, hook.ToolInput?.Summary),
                Capabilities = [],
            },
            CodexHookEventName.PostToolUse => task with
            {
                Lifecycle = AgentLifecycle.Running,
                Activity = AgentActivity.Thinking,
                Capabilities = [],
            },
            CodexHookEventName.PermissionRequest => task with
            {
                Lifecycle = AgentLifecycle.WaitingApproval,
                Activity = AgentActivity.Executing,
                Capabilities = [TaskCapability.Approve, TaskCapability.Deny],
            },
            CodexHookEventName.Stop => task with
            {
                Lifecycle = task.Lifecycle == AgentLifecycle.Interrupted
                    ? AgentLifecycle.Interrupted : AgentLifecycle.Succeeded,
                Activity = null,
                CompletedAt = now,
                IsUnread = true,
                Capabilities = [],
            },
            _ => task,
        };
    }

    public static string Title(string projectName, string? prompt)
    {
        var normalized = Normalize(prompt);
        return normalized is null ? projectName : $"{projectName} · {normalized[..Math.Min(34, normalized.Length)]}";
    }

    public static string? LatestStep(string? toolName, string? summary) =>
        ToolStep.Compose(toolName, summary);

    public static AgentActivity ActivityFor(string? toolName)
    {
        var name = toolName?.ToLowerInvariant() ?? "";
        if (name.Contains("test") || name.Contains("gradle") || name.Contains("xcode")) return AgentActivity.Testing;
        if (name.Contains("read") || name.Contains("view") || name.Contains("list")) return AgentActivity.Reading;
        if (name.Contains("web") || name.Contains("browser")) return AgentActivity.Browsing;
        if (name.Contains("search") || name.Contains("find") || name.Contains("grep")) return AgentActivity.Searching;
        if (name.Contains("patch") || name.Contains("write") || name.Contains("edit")) return AgentActivity.Editing;
        if (name.Contains("agent") || name.Contains("thread")) return AgentActivity.Delegating;
        return AgentActivity.Executing;
    }

    private static TaskSnapshot UserPrompt(TaskSnapshot task, string? prompt)
    {
        var normalized = Normalize(prompt);
        return task with
        {
            Lifecycle = AgentLifecycle.Running,
            Activity = AgentActivity.Thinking,
            UserPrompt = normalized ?? task.UserPrompt,
            Title = normalized is not null && task.Title == task.ProjectName
                ? Title(task.ProjectName, normalized) : task.Title,
            Capabilities = [],
        };
    }

    private static string? Normalize(string? value)
    {
        if (value is null) return null;
        var result = string.Join(' ', value.Replace('\n', ' ')
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return result.Length == 0 ? null : result;
    }
}

public static class ClaudeEventReducer
{
    public static TaskSnapshot Reduce(ClaudeHookPayload hook, TaskSnapshot? existing, DateTimeOffset now)
    {
        var task = existing ?? new(
            hook.SessionID,
            AgentSource.ClaudeCode,
            hook.ProjectName,
            hook.ProjectName,
            null, null, null, [],
            AgentLifecycle.Starting, null,
            now, now, null,
            false, false, false, []);
        task = task with { ProjectName = hook.ProjectName, UpdatedAt = now, CompletedAt = null };

        switch (hook.HookEventName)
        {
            case "SessionStart":
                return task with
                {
                    Lifecycle = AgentLifecycle.Starting,
                    Activity = AgentActivity.Thinking,
                    CompletedAt = null,
                    Capabilities = [],
                };
            case "UserPromptSubmit":
                return UserPrompt(task, hook.Prompt);
            case "PreToolUse":
                return task with
                {
                    Lifecycle = AgentLifecycle.Running,
                    Activity = CodexEventReducer.ActivityFor(hook.ToolName),
                    LatestStep = CodexEventReducer.LatestStep(hook.ToolName, Summary(hook.ToolInput)),
                    CompletedAt = null,
                    Capabilities = [],
                };
            case "PostToolUse":
            case "PostToolUseFailure":
            case "PreCompact":
                return task with
                {
                    Lifecycle = AgentLifecycle.Running,
                    Activity = AgentActivity.Thinking,
                    CompletedAt = null,
                    Capabilities = [],
                };
            case "PermissionRequest":
                return task with
                {
                    Lifecycle = AgentLifecycle.WaitingApproval,
                    Activity = AgentActivity.Executing,
                    LatestStep = CodexEventReducer.LatestStep(hook.ToolName, Summary(hook.ToolInput)),
                    CompletedAt = null,
                    Capabilities = [TaskCapability.Approve, TaskCapability.Deny],
                };
            case "PermissionDenied":
                return task with
                {
                    Lifecycle = AgentLifecycle.Interrupted,
                    Activity = null,
                    CompletedAt = now,
                    IsUnread = true,
                    Capabilities = [],
                };
            case "Notification":
                // 只有确实等待用户输入的通知才进入等待回答；其他通知保持现有状态。
                return IsAnswerNotification(hook.NotificationType)
                    ? task with
                    {
                        Lifecycle = AgentLifecycle.WaitingAnswer,
                        Activity = AgentActivity.Thinking,
                        CompletedAt = null,
                        Capabilities = [],
                    }
                    : task;
            case "Stop":
                return task with
                {
                    Lifecycle = task.Lifecycle == AgentLifecycle.Interrupted
                        ? AgentLifecycle.Interrupted : AgentLifecycle.Succeeded,
                    Activity = null,
                    CompletedAt = now,
                    IsUnread = true,
                    Capabilities = [],
                };
            case "StopFailure":
                return task with
                {
                    Lifecycle = task.Lifecycle == AgentLifecycle.Succeeded ? AgentLifecycle.Running : task.Lifecycle,
                    CompletedAt = task.Lifecycle == AgentLifecycle.Succeeded ? null : task.CompletedAt,
                    Capabilities = [],
                };
            case "SessionEnd":
                return task with
                {
                    Lifecycle = task.IsTerminal ? task.Lifecycle : AgentLifecycle.Offline,
                    Activity = null,
                    CompletedAt = task.IsTerminal ? task.CompletedAt : null,
                    Capabilities = [],
                };
            case "SubagentStart":
            case "SubagentStop":
                return ApplySubagent(hook, task, now);
            default:
                return task with { UpdatedAt = now };
        }
    }

    /// <summary>从 Claude 的任意 tool_input JSON 中提取最具信息量的字段。</summary>
    public static string? Summary(JsonElement? toolInput)
    {
        if (toolInput is null) return null;
        var value = toolInput.Value;
        if (value.ValueKind != JsonValueKind.Object)
            return DisplayValue(value);
        foreach (var key in new[] { "command", "file_path", "path", "pattern", "query", "prompt", "description", "url" })
        {
            if (value.TryGetProperty(key, out var child) && DisplayValue(child) is { Length: > 0 } text)
                return text;
        }
        return null;
    }

    /// <summary>会让 Claude 暂停并等待用户输入的通知类型。</summary>
    public static bool IsAnswerNotification(string? notificationType) =>
        notificationType is "idle_prompt" or "elicitation_dialog" or "agent_needs_input";

    private static TaskSnapshot ApplySubagent(ClaudeHookPayload hook, TaskSnapshot task, DateTimeOffset now)
    {
        var id = hook.AgentID ?? hook.ToolUseID;
        if (id is null) return task;
        var displayName = !string.IsNullOrWhiteSpace(hook.AgentType) ? hook.AgentType! : "Claude 子代理";
        var path = "/" + (hook.AgentType ?? "subagent");
        var subagent = task.Subagents.FirstOrDefault(value => value.Id == id) ??
            new(id, path, displayName,
                AgentLifecycle.Running, AgentActivity.Thinking,
                null, null, now, now);
        if (hook.HookEventName == "SubagentStart")
        {
            subagent = subagent with
            {
                Lifecycle = AgentLifecycle.Running,
                Activity = AgentActivity.Delegating,
                Path = path,
                DisplayName = displayName,
                LatestStep = Normalize(hook.TaskDescription) ?? subagent.LatestStep,
            };
        }
        else if (hook.HookEventName == "SubagentStop")
        {
            subagent = subagent with { Lifecycle = AgentLifecycle.Succeeded, Activity = null };
        }
        subagent = subagent with { UpdatedAt = now };
        var subagents = task.Subagents.Where(value => value.Id != id).Append(subagent)
            .OrderBy(value => value.IsTerminal).ThenBy(value => value.StartedAt).ToList();
        return task with
        {
            Subagents = subagents,
            Lifecycle = AgentLifecycle.Running,
            Activity = subagents.Any(value => !value.IsTerminal) ? AgentActivity.Delegating : AgentActivity.Thinking,
            UpdatedAt = now,
            CompletedAt = null,
        };
    }

    private static TaskSnapshot UserPrompt(TaskSnapshot task, string? prompt)
    {
        var normalized = Normalize(prompt);
        return task with
        {
            Lifecycle = AgentLifecycle.Running,
            Activity = AgentActivity.Thinking,
            UserPrompt = normalized ?? task.UserPrompt,
            Title = normalized is not null && task.Title == task.ProjectName
                ? CodexEventReducer.Title(task.ProjectName, normalized) : task.Title,
            CompletedAt = null,
            Capabilities = [],
        };
    }

    private static string? DisplayValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "null",
        _ => null,
    };

    private static string? Normalize(string? value)
    {
        if (value is null) return null;
        var result = string.Join(' ', value.Replace('\n', ' ')
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return result.Length == 0 ? null : result;
    }
}

public static class ModelNames
{
    /// <summary>ccproxy/deepseek-flash → deepseek-flash（取最后一个 / 段）。</summary>
    public static string? Label(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        var segment = model.Trim().Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return string.IsNullOrEmpty(segment) ? null : segment;
    }
}

public static class OpenCodeEventReducer
{
    private static readonly string[] InputKeys =
        ["command", "description", "filePath", "file_path", "path", "pattern", "query", "url", "subagent_type", "agent", "skill"];

    // 与 opencode Session.isDefaultTitle 对齐：New session - / Child session - + ISO 时间戳。
    private static readonly Regex DefaultTitlePattern = new(
        @"^(New session - |Child session - )\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string? SessionID(OpenCodeHookPayload hook)
    {
        if (hook.Properties is not { } properties) return null;
        if (Text(properties, "sessionID") is { Length: > 0 } sessionID) return sessionID;
        if (Child(properties, "info") is { } info && Text(info, "id") is { Length: > 0 } id) return id;
        return null;
    }

    public static TaskSnapshot Reduce(OpenCodeHookPayload hook, TaskSnapshot? existing, DateTimeOffset now)
    {
        var sessionID = SessionID(hook) ?? existing?.Id ?? "opencode";
        var project = ProjectName(hook.Properties) ?? existing?.ProjectName ?? "opencode";
        var task = existing ?? new TaskSnapshot(
            sessionID, AgentSource.OpenCode, project, project,
            null, null, null, [],
            AgentLifecycle.Idle, null,
            SessionCreatedAt(hook.Properties) ?? now, now, null,
            false, false, false, []);
        task = task with { ProjectName = project, UpdatedAt = now };

        switch (hook.Event)
        {
            case "session.created":
                return task with
                {
                    Lifecycle = task.IsTerminal ? task.Lifecycle : AgentLifecycle.Idle,
                    Activity = null,
                    StartedAt = SessionCreatedAt(hook.Properties) ?? task.StartedAt,
                    Title = SessionTitleText(task, hook.Properties) ?? task.Title,
                    Capabilities = [],
                };
            case "session.updated":
                return SessionTitle(task, hook.Properties);
            case "session.deleted":
                return task with
                {
                    Lifecycle = task.IsTerminal ? task.Lifecycle : AgentLifecycle.Offline,
                    Activity = null,
                    Capabilities = [],
                    PromptQueued = false,
                };
            case "chat.message":
            {
                var next = UserPrompt(task, Text(hook.Properties, "text"), now);
                if (ModelFromProperties(hook.Properties) is { } model) next = next with { ModelName = model };
                return next with { PromptQueued = Flag(hook.Properties, "queued") };
            }
            case "prompt.active":
                // 排队的 prompt 被接单（新的 assistant 消息开始生成）
                return task.PromptQueued ? task with { PromptQueued = false } : task;
            case "subagent":
                return ApplyOpenCodeSubagent(task, hook.Properties, now);
            case "session.status":
                return StatusEvent(task, hook.Properties, now);
            case "session.idle":
                return Idle(task, now);
            case "session.error":
                return Failed(task, ErrorSummary(SessionError(hook.Properties)), now);
            case "message.updated":
                return MessageUpdated(task, hook.Properties, now);
            case "message.part.updated":
                return PartUpdated(task, hook.Properties);
            case "reply":
                // 正文预览（插件从 assistant text part 节流转发）：只更新第三行，不动生命周期；
                // 等待用户输入期间忽略（迟到正文不盖掉问题/批准卡片的标题）。
                if (task.Lifecycle is AgentLifecycle.WaitingApproval or AgentLifecycle.WaitingAnswer) return task;
                // 工具活动期间忽略：正文结束帧可能晚于工具 part 约 200ms（实测乱序），
                // 不拦会把刚写入的命令/工具步骤盖回上一段正文。
                if (task.Activity is not null and not AgentActivity.Thinking) return task;
                return Text(hook.Properties, "text") is { } preview ? WithStep(task, Clamp(preview, 220)) : task;
            case "permission.asked":
                return task with
                {
                    Lifecycle = AgentLifecycle.WaitingApproval,
                    Activity = AgentActivity.Executing,
                    CompletedAt = null,
                    Capabilities = [],
                };
            case "permission.replied":
                return PermissionReply(task, hook.Properties, now);
            case "question.asked":
                return WithStep(task with
                {
                    Lifecycle = AgentLifecycle.WaitingAnswer,
                    Activity = AgentActivity.Thinking,
                    CompletedAt = null,
                    Capabilities = [],
                }, QuestionSummary(hook.Properties));
            case "question.replied":
                return task with
                {
                    Lifecycle = AgentLifecycle.Running,
                    Activity = AgentActivity.Thinking,
                    CompletedAt = null,
                    Capabilities = [],
                };
            case "question.rejected":
                return Interrupted(task, null, now);
            case "plugin.dispose":
                return task with
                {
                    Lifecycle = task.IsTerminal ? task.Lifecycle : AgentLifecycle.Offline,
                    Activity = null,
                    Capabilities = [],
                    PromptQueued = false,
                };
            default:
                return task;
        }
    }

    public static string? PermissionSummary(JsonElement? properties)
    {
        if (properties is not { } value) return null;
        var permission = Text(value, "permission");
        var patterns = StringArray(value, "patterns");
        var detail = patterns.Count > 0 ? string.Join(", ", patterns.Take(4)) : null;
        var summary = (permission, detail) switch
        {
            (not null, not null) => $"{permission} · {detail}",
            (not null, null) => permission,
            (null, not null) => detail,
            _ => null,
        };
        return summary is null ? null : Clamp(summary, 220);
    }

    public static string? QuestionSummary(JsonElement? properties)
    {
        if (properties is not { } value) return null;
        if (!value.TryGetProperty("questions", out var questions) || questions.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var item in questions.EnumerateArray())
        {
            var summary = Text(item, "header") ?? Text(item, "question");
            if (summary is not null) return Clamp(summary, 220);
        }
        return null;
    }

    /// <summary>卡片摘要用：优先问题全文，其次 header。</summary>
    public static string? QuestionText(JsonElement? properties)
    {
        if (properties is not { } value) return null;
        if (!value.TryGetProperty("questions", out var questions) || questions.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var item in questions.EnumerateArray())
        {
            var text = Text(item, "question") ?? Text(item, "header");
            if (text is not null) return Clamp(text, 220);
        }
        return null;
    }

    public static List<string> QuestionOptions(JsonElement? properties)
    {
        var result = new List<string>();
        if (properties is not { } value) return result;
        if (!value.TryGetProperty("questions", out var questions) || questions.ValueKind != JsonValueKind.Array)
            return result;
        foreach (var item in questions.EnumerateArray()) result.AddRange(StringArray(item, "options"));
        return result.Take(16).ToList();
    }

    private static TaskSnapshot StatusEvent(TaskSnapshot task, JsonElement? properties, DateTimeOffset now)
    {
        var status = properties is { } value ? Child(value, "status") : null;
        var type = status is { } state ? Text(state, "type") : null;
        switch (type)
        {
            case "busy":
                return task with
                {
                    Lifecycle = AgentLifecycle.Running,
                    Activity = task.Activity ?? AgentActivity.Thinking,
                    CompletedAt = null,
                    Capabilities = [],
                };
            case "retry":
                var attempt = Number(status, "attempt");
                var message = Text(status, "message");
                var step = message is null ? "重试中" : $"重试 #{attempt}: {message}";
                return WithStep(task with
                {
                    Lifecycle = AgentLifecycle.Running,
                    Activity = AgentActivity.Thinking,
                    CompletedAt = null,
                    Capabilities = [],
                }, Clamp(step, 220));
            default:
                return Idle(task, now);
        }
    }

    private static TaskSnapshot Idle(TaskSnapshot task, DateTimeOffset now)
    {
        if (task.Lifecycle == AgentLifecycle.Interrupted) return task;
        return task with
        {
            Lifecycle = AgentLifecycle.Succeeded,
            Activity = null,
            CompletedAt = task.IsTerminal ? task.CompletedAt ?? now : now,
            IsUnread = true,
            Capabilities = [],
        };
    }

    private static TaskSnapshot Failed(TaskSnapshot task, string? step, DateTimeOffset now)
    {
        var next = task with
        {
            Lifecycle = AgentLifecycle.Interrupted,
            Activity = null,
            CompletedAt = now,
            IsUnread = true,
            Capabilities = [],
        };
        return step is null ? next : WithStep(next, Clamp($"✗ {step}", 220));
    }

    private static TaskSnapshot Interrupted(TaskSnapshot task, string? step, DateTimeOffset now) =>
        Failed(task, step, now);

    private static TaskSnapshot MessageUpdated(TaskSnapshot task, JsonElement? properties, DateTimeOffset now)
    {
        if (properties is not { } value) return task;
        var info = Child(value, "info");
        if (info is not { } message) return task;
        var usage = TokenUsageFrom(message);
        if (usage is not null) task = task with { TokenUsage = usage };
        if (ModelNames.Label(Text(message, "modelID")) is { } model) task = task with { ModelName = model };
        var error = Child(message, "error");
        return error is { } failure ? Failed(task, ErrorSummary(failure), now) : task;
    }

    private static string? ModelFromProperties(JsonElement? properties)
    {
        var model = properties is { } value ? Child(value, "model") : null;
        if (model is { } source && ModelNames.Label(Text(source, "modelID")) is { } label) return label;
        return ModelNames.Label(Text(properties, "modelID"));
    }

    private static bool Flag(JsonElement? element, string name) =>
        element is { } value &&
        value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var child) &&
        child.ValueKind == JsonValueKind.True;

    private static TaskSnapshot PartUpdated(TaskSnapshot task, JsonElement? properties)
    {
        if (properties is not { } value) return task;
        var part = Child(value, "part");
        if (part is not { } item) return task;
        var state = Child(item, "state");
        var status = state is { } current ? Text(current, "status") : null;
        var tool = Text(item, "tool");
        var step = LatestStep(tool, state);

        // 终态/等待态忽略工具 part 更新：
        // - Interrupted/Waiting*：question.asked / permission.asked 之后工具 part 补发 running 的竞态，
        //   不拦会把 waiting 状态压回 running 并清掉挂起请求；
        // - Succeeded/Offline：回合末尾迟到的 part 不得把已完成任务复活回 running。
        if (task.Lifecycle is AgentLifecycle.Interrupted or AgentLifecycle.Succeeded or AgentLifecycle.Offline
            or AgentLifecycle.WaitingApproval or AgentLifecycle.WaitingAnswer)
            return task;

        switch (status)
        {
            case "pending":
            case "running":
                return WithStep(task with
                {
                    Lifecycle = AgentLifecycle.Running,
                    Activity = ActivityFor(tool, state),
                    CompletedAt = null,
                    Capabilities = [],
                }, step);
            case "completed":
                return WithStep(task with
                {
                    Lifecycle = AgentLifecycle.Running,
                    Activity = AgentActivity.Thinking,
                    CompletedAt = null,
                    Capabilities = [],
                }, step);
            case "error":
                var failure = state is { } failed ? Text(failed, "error") : null;
                return WithStep(task with
                {
                    Lifecycle = AgentLifecycle.Running,
                    Activity = AgentActivity.Thinking,
                    CompletedAt = null,
                    Capabilities = [],
                }, failure is null ? null : Clamp($"✗ {failure}", 220));
            default:
                return task;
        }
    }

    /// <summary>
    /// 第三行（LatestStep）永不显示用户 prompt：候选为空则保留原值；
    /// 候选与当前 UserPrompt 相同或整段包含它（prompt ≥4 字）时丢弃，保留上一步。
    /// </summary>
    private static TaskSnapshot WithStep(TaskSnapshot task, string? step)
    {
        if (step is null || EchoesPrompt(task, step)) return task;
        return task with { LatestStep = step };
    }

    /// <summary>候选文本与当前 UserPrompt 相同或整段包含它（prompt ≥4 字）→ 视为回显。</summary>
    private static bool EchoesPrompt(TaskSnapshot task, string candidate) =>
        task.UserPrompt is { Length: >= 4 } prompt && candidate.Contains(prompt, StringComparison.OrdinalIgnoreCase);

    /// <summary>opencode 子会话（task 工具派生）映射为父任务的 Subagents 行。</summary>
    private static TaskSnapshot ApplyOpenCodeSubagent(TaskSnapshot task, JsonElement? properties, DateTimeOffset now)
    {
        if (properties is not { } value) return task;
        var id = Text(value, "id");
        var phase = Text(value, "phase");
        if (id is null || phase is null) return task;
        var existing = task.Subagents.FirstOrDefault(item => item.Id == id);
        if (existing?.IsTerminal == true && phase != "tokens") return task;
        var agent = Text(value, "agent");
        var path = existing?.Path ?? (agent is null ? "/subagent" : "/" + agent);
        var subagent = existing ?? new SubagentSnapshot(
            id, path, SubagentName(agent, Text(value, "title")),
            AgentLifecycle.Running, AgentActivity.Delegating, null, null, now, now);
        subagent = subagent with { UpdatedAt = now };

        switch (phase)
        {
            case "started":
                subagent = subagent with { Lifecycle = AgentLifecycle.Running };
                break;
            case "tool":
                var tool = Text(value, "tool");
                var rawError = Text(value, "error");
                var toolTitle = Text(value, "title");
                var step = rawError is not null ? $"✗ {rawError}"
                    : toolTitle is not null ? ToolStep.Compose(tool, toolTitle)
                    : Text(value, "step");
                if (step is not null && !EchoesPrompt(task, step)) subagent = subagent with { LatestStep = Clamp(step, 220) };
                subagent = subagent with { Lifecycle = AgentLifecycle.Running, Activity = ActivityFor(tool, null) };
                break;
            case "tokens":
                if (TokenUsageFrom(value) is { } usage) subagent = subagent with { TokenUsage = usage };
                break;
            case "done":
                subagent = subagent with { Lifecycle = AgentLifecycle.Succeeded, Activity = null };
                break;
            case "failed":
                var failure = Text(value, "step");
                subagent = subagent with
                {
                    Lifecycle = AgentLifecycle.Interrupted,
                    Activity = null,
                    LatestStep = failure is null ? subagent.LatestStep : Clamp($"✗ {failure}", 220),
                };
                break;
            default:
                return task;
        }

        var subagents = task.Subagents.Where(item => item.Id != id).Append(subagent)
            .OrderBy(item => item.IsTerminal).ThenBy(item => item.StartedAt).ToList();
        return task with
        {
            Subagents = subagents,
            UpdatedAt = now,
            Activity = task.Lifecycle == AgentLifecycle.Running
                ? subagents.Any(item => !item.IsTerminal) ? AgentActivity.Delegating : AgentActivity.Thinking
                : task.Activity,
        };
    }

    private static string SubagentName(string? agent, string? title)
    {
        if (agent is not null) return char.ToUpperInvariant(agent[0]) + agent[1..];
        if (title is not null)
        {
            var index = title.IndexOf(" (@", StringComparison.Ordinal);
            return Clamp(index > 0 ? title[..index] : title, 40);
        }
        return "opencode 子代理";
    }

    private static TaskSnapshot PermissionReply(TaskSnapshot task, JsonElement? properties, DateTimeOffset now)
    {
        var reply = Text(properties, "reply");
        if (reply == "reject") return Interrupted(task, null, now);
        return task with
        {
            Lifecycle = AgentLifecycle.Running,
            Activity = AgentActivity.Thinking,
            CompletedAt = null,
            Capabilities = [],
        };
    }

    private static TaskSnapshot UserPrompt(TaskSnapshot task, string? prompt, DateTimeOffset now)
    {
        var normalized = Normalize(prompt);
        // 本轮计时：一轮的第一个 prompt 开始计时；轮内插入的 prompt 不重置。
        var startsTurn = task.Lifecycle is AgentLifecycle.Idle or AgentLifecycle.Offline
            or AgentLifecycle.Succeeded or AgentLifecycle.Interrupted;
        return task with
        {
            Lifecycle = AgentLifecycle.Running,
            Activity = AgentActivity.Thinking,
            UserPrompt = normalized ?? task.UserPrompt,
            StartedAt = startsTurn ? now : task.StartedAt,
            // 新一轮开始时清空第三行，让 lifecycle 文案兜底；轮内插入不清。
            LatestStep = startsTurn ? null : task.LatestStep,
            CompletedAt = null,
            Capabilities = [],
        };
    }

    private static TaskSnapshot SessionTitle(TaskSnapshot task, JsonElement? properties)
    {
        var next = SessionTitleText(task, properties);
        return next is null || next == task.Title ? task : task with { Title = next };
    }

    private static string? SessionTitleText(TaskSnapshot task, JsonElement? properties)
    {
        var info = properties is { } value ? Child(value, "info") : null;
        var title = info is { } session ? Text(session, "title") : null;
        if (title is null || DefaultTitlePattern.IsMatch(title)) return null;
        return Clamp($"{task.ProjectName} · {title}", 120);
    }

    private static string? ProjectName(JsonElement? properties)
    {
        var info = properties is { } value ? Child(value, "info") : null;
        var directory = info is { } session ? Text(session, "directory") : null;
        if (directory is null) return null;
        var leaf = directory.TrimEnd('\\', '/').Split('\\', '/').LastOrDefault(part => part.Length > 0);
        return string.IsNullOrEmpty(leaf) ? null : leaf;
    }

    private static DateTimeOffset? SessionCreatedAt(JsonElement? properties)
    {
        var info = properties is { } value ? Child(value, "info") : null;
        if (info is not { } session) return null;
        var time = Child(session, "time");
        if (time is not { } clock || clock.ValueKind != JsonValueKind.Object) return null;
        if (!clock.TryGetProperty("created", out var created) || created.ValueKind != JsonValueKind.Number) return null;
        var milliseconds = created.GetDouble();
        return milliseconds <= 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(milliseconds));
    }

    private static AgentActivity ActivityFor(string? tool, JsonElement? state)
    {
        var name = tool?.ToLowerInvariant() ?? "";
        if (name.Length == 0) return AgentActivity.Executing;
        if (name.Contains("todo")) return AgentActivity.Thinking;
        if (name.Contains("question")) return AgentActivity.Thinking;
        if (name.Contains("read") || name == "list" || name.EndsWith("_list")) return AgentActivity.Reading;
        if (name.Contains("glob") || name.Contains("grep") || name.Contains("search")) return AgentActivity.Searching;
        if (name.Contains("task") || name.Contains("agent")) return AgentActivity.Delegating;
        if (name.Contains("webfetch") || name.Contains("websearch") || name.Contains("browser")) return AgentActivity.Browsing;
        if (name.Contains("edit") || name.Contains("write") || name.Contains("patch")) return AgentActivity.Editing;
        if (name.Contains("skill") || name.Contains("lsp")) return AgentActivity.Reading;
        if (name.Contains("bash") || name.Contains("shell") || name.Contains("terminal"))
            return IsTestCommand(state) ? AgentActivity.Testing : AgentActivity.Executing;
        if (name.Contains("test")) return AgentActivity.Testing;
        return AgentActivity.Executing;
    }

    private static bool IsTestCommand(JsonElement? state)
    {
        var input = state is { } value ? Child(value, "input") : null;
        var command = input is { } arguments ? Text(arguments, "command") : null;
        return command is not null &&
               (command.Contains("test", StringComparison.OrdinalIgnoreCase) ||
                command.Contains("gradle", StringComparison.OrdinalIgnoreCase));
    }

    private static string? LatestStep(string? tool, JsonElement? state)
    {
        var detail = state is { } value ? Text(value, "title") ?? SummarizeInput(value) : null;
        return ToolStep.Compose(tool, detail);
    }

    private static string? SummarizeInput(JsonElement state)
    {
        var input = Child(state, "input");
        if (input is not { } value) return null;
        foreach (var key in InputKeys)
            if (Text(value, key) is { } text)
                return text;
        return null;
    }

    private static TokenUsage? TokenUsageFrom(JsonElement info)
    {
        var tokens = Child(info, "tokens");
        if (tokens is not { } value) return null;
        var input = Number(value, "input");
        var output = Number(value, "output");
        var reasoning = Number(value, "reasoning");
        var cached = Child(value, "cache") is { } cache ? Number(cache, "read") : 0;
        var total = Number(value, "total");
        if (total <= 0) total = input + output + reasoning + cached;
        if (total <= 0 && input <= 0 && output <= 0) return null;
        return new TokenUsage(input, cached, output, reasoning, total);
    }

    private static JsonElement? SessionError(JsonElement? properties) =>
        properties is { } value ? Child(value, "error") : null;

    private static string? ErrorSummary(JsonElement? error)
    {
        if (error is not { } value) return null;
        var data = Child(value, "data");
        var message = data is { } detail ? Text(detail, "message") : null;
        message ??= Text(value, "message");
        return message is null ? Text(value, "name") : Clamp(message, 220);
    }

    private static JsonElement? Child(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object &&
        parent.TryGetProperty(name, out var value) &&
        value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? value
            : null;

    private static string? Text(JsonElement? element, string name)
    {
        if (element is not { } value || value.ValueKind != JsonValueKind.Object) return null;
        if (!value.TryGetProperty(name, out var child) || child.ValueKind != JsonValueKind.String) return null;
        return Normalize(child.GetString());
    }

    private static int Number(JsonElement? element, string name) =>
        element is { } value &&
        value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var child) &&
        child.ValueKind == JsonValueKind.Number
            ? (int)Math.Max(0, Math.Round(child.GetDouble()))
            : 0;

    private static List<string> StringArray(JsonElement element, string name)
    {
        var result = new List<string>();
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array) return result;
        foreach (var item in value.EnumerateArray())
        {
            var text = item.ValueKind == JsonValueKind.String
                ? Normalize(item.GetString())
                : item.ValueKind == JsonValueKind.Object ? Text(item, "label") : null;
            if (text is not null) result.Add(text);
        }
        return result;
    }

    private static string? Normalize(string? value)
    {
        if (value is null) return null;
        var result = string.Join(' ', value.Replace('\n', ' ')
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return result.Length == 0 ? null : result;
    }

    private static string Clamp(string value, int limit) => value.Length > limit ? value[..limit] : value;
}

public static class ToolStepSanitizer
{
    public static string? Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        var result = value.Trim();
        foreach (var prefix in new[] { "functions.", "tools.", "mcp__" })
            if (result.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                result = result[(result.IndexOf(' ') + 1)..].Trim();
        return result.Length > 220 ? result[..220] : result;
    }
}
