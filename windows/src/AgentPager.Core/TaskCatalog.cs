using System.Text.Json;
using System.Text.Json.Nodes;
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

    /// <summary>
    /// 把"用户在手机上作答"写回挂起的权限请求（目前只有 Claude 的提问工具走这条路）。
    /// <paramref name="rawToolInput"/> 是该提问工具**原始**的 tool_input——回填时必须整份带回。
    /// </summary>
    bool ResolveAnswer(string sessionID, string rawToolInput, string answer);

    /// <summary>多题攒齐后一次性回填（"问题全文 → 答案"整张表）。见 <see cref="ClaudeHookOutput.AnswerMap"/>。</summary>
    bool ResolveAnswers(string sessionID, string rawToolInput, IReadOnlyDictionary<string, string> answers);
}

public sealed class TaskCatalog
{
    private readonly List<TaskSnapshot> _tasks;
    private readonly Dictionary<string, PendingRequest> _requests;
    // opencode：乱序残帧守卫——处于 pending/running 的工具 part 所属会话（见 Accept(OpenCodeHookPayload)）。
    private readonly HashSet<string> _openCodeToolActive = new();
    // opencode：会话 → 工作区目录（session.created/updated 的 info.directory）。
    // 只用来做工具步里的路径缩写（opencode 的 chat.message 不带目录，行是那时才建的）。
    private readonly Dictionary<string, string> _openCodeDirectories = new(StringComparer.Ordinal);
    // Claude Code：会话 → 我们**曾显示为排队中**的那条提示词文本（投递时用来判不算新回合）。
    private readonly Dictionary<string, string> _claudeQueuedText = new(StringComparer.Ordinal);
    // Claude Code：会话 → 转录本路径（每个 hook 事件都带 transcript_path），供 Maintain 心跳复用。
    private readonly Dictionary<string, string> _claudeTranscripts = new(StringComparer.Ordinal);
    // 转录本 mtime 门控：文件没变就不重读（照抄 agy 的 DB mtime 门控思路）。
    private readonly Dictionary<string, DateTime> _claudeTranscriptMtimes = new(StringComparer.Ordinal);
    // 在 ~/.claude/sessions 里见过该会话：只有见过才敢用"文件消失"判离线（注册并非所有模式都开）。
    private readonly HashSet<string> _claudeRegistrySeen = new(StringComparer.Ordinal);
    // 提问工具挂起时的**原始 tool_input**：手机上作答要整份回填（只塞答案会把问题抹掉）。
    private readonly Dictionary<string, string> _claudeQuestionInputs = new(StringComparer.Ordinal);
    // 是否有工具正在跑：中间正文只在"没有工具在跑"时进第三行（与 opencode 的 reply 守卫同义）。
    private readonly HashSet<string> _claudeToolActive = new(StringComparer.Ordinal);
    // 多题提问的逐题推进：桥把 N 题拆成"手机上一次一题"喂给手机（App 照旧渲染单题卡，
    // 零协议改动），攒齐后再一次性回填 updatedInput.answers。
    // 为什么值得做：手机端协议里 options 是扁平列表，多题无法按题归属；而"拆轮提问"只存在于
    // 对话上下文里——换个 session 就没人记得了，只有写在桥里才是稳定的。
    private readonly Dictionary<string, ClaudeQuestionProgress> _claudeQuestionProgress = new(StringComparer.Ordinal);

    /// <summary>逐题推进状态：当前第几题 + 已收集的答案（问题全文 → 答案）。</summary>
    private sealed record ClaudeQuestionProgress(int Index, Dictionary<string, string> Answers);

    // 权限卡是"谁"挂的：父级询问存空串，子代理询问存 agent_id。
    // 撤销必须认来源——子代理的卡挂在父任务上，若用"任意事件就撤"会误撤父级/别的子代理正在等的卡。
    private readonly Dictionary<string, string> _claudeApprovalSource = new(StringComparer.Ordinal);
    // Stop 终稿的竞速补救：转录本与 payload 两路都没拿到正文时登记，交由 Maintain 限期内重读一次
    // （照搬 agy 的 _agyPreviewRecheck；两条路同时落空才会触发）。
    private readonly Dictionary<string, (string Path, DateTimeOffset Deadline)> _claudePreviewRecheck = new(StringComparer.Ordinal);
    // 等待人类输入/审批的起始时刻：用于时钟顺延补偿，剔除人类作答等待时间
    private readonly Dictionary<string, DateTimeOffset> _waitingSince = new(StringComparer.Ordinal);
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
        if (string.IsNullOrWhiteSpace(hook.SessionID)) return false;

        // 子代理内部的事件（公共入参里 agent_id 非空）一律路由进父任务的 Subagents：
        // 绝不建独立任务，也绝不用子代理的步骤覆盖父任务第三行（对齐 opencode 的子会话
        // 与 agy 的 parent_conversation_id 路由）。
        if (hook.IsInsideSubagent) return AcceptClaudeSubagent(hook, now);

        var existing = _tasks.FirstOrDefault(task => task.Id == hook.SessionID);

        // 不造幽灵任务：这些事件只会"结束/推进"一个已知会话，不该凭空建行。
        //
        // **SessionStart 也在列**，理由不是"它不推进会话"，而是"它出生时不可能有标题"：
        // 它由 Claude Code **每次进程启动**无条件发出（实测载荷只有 session_id / transcript_path /
        // cwd / hook_event_name / source，**连 session_title 字段都没有**），而此刻转录本文件
        // **尚未创建**（探针实测 exists=false）——标题的三条来源（转录本 ai-title/custom-title、
        // 载荷 session_title、注册表 name）一条都拿不到，于是新行只能顶着 cwd 目录名出生
        // （ClaudeTitle 在无标题时返回项目名）。用户看到的就是"每次启动 100% 冒出一个
        // 没有 session title 的幽灵任务"。行为对齐 agy：它的事件表里根本没有"会话创建"事件，
        // 行随第一个回合（PreInvocation）与提示词一起诞生。
        //
        // 对**已存在**的行，SessionStart 的语义原样保留（resume/clear 把它复位成 Idle）。
        if (existing is null && hook.HookEventName is
            "SessionStart" or "SessionEnd" or "PreCompact" or "Stop" or "StopFailure" or "PermissionDenied"
            or "PostToolUse" or "PostToolUseFailure")
            return false;

        // 每个事件都带 transcript_path：登记下来供 Maintain 心跳复用（标题/Token 刷新）。
        if (!string.IsNullOrWhiteSpace(hook.TranscriptPath))
            _claudeTranscripts[hook.SessionID] = hook.TranscriptPath!;

        // 工具活动跟踪：中间正文（心跳路径）只在没有工具在跑时进第三行。
        switch (hook.HookEventName)
        {
            case "PreToolUse":
                _claudeToolActive.Add(hook.SessionID);
                break;
            case "PostToolUse":
            case "PostToolUseFailure":
            case "Stop":
            case "StopFailure":
            case "SessionEnd":
            case "PermissionDenied":
                // PermissionDenied 也算"这一步结束了"：被 auto 分类器拒掉之后没有工具在跑，
                // 不清的话中间正文会被这条陈旧标志一直挡在第三行之外。
                _claudeToolActive.Remove(hook.SessionID);
                break;
        }

        var transcript = ReadClaudeTranscript(hook);

        // 记下"我们曾把它显示成排队中"的那条提示词。用途只有一个：**排队消息被投递时不算新回合**
        // （用户裁定：一轮完了之后重新开始才重置计时；中途插入的那句是同一个 burst）。
        // 为什么不能直接用 PromptQueued 标记：投递时最后一条队列操作已经变成 dequeue/remove，
        // 标记已被清空 ✗ —— 所以要另存一份文本，投递时对表。
        if (transcript?.QueuedPrompt is { Length: > 0 } queuedText)
            _claudeQueuedText[hook.SessionID] = queuedText;

        var queuedDelivery = hook.HookEventName == "UserPromptSubmit" &&
            _claudeQueuedText.TryGetValue(hook.SessionID, out var queuedPrompt) &&
            queuedPrompt == ClaudeEventReducer.NormalizePrompt(hook.Prompt);
        Upsert(ClaudeEventReducer.Reduce(hook, existing, transcript, now, queuedDelivery));
        if (queuedDelivery) _claudeQueuedText.Remove(hook.SessionID);

        switch (hook.HookEventName)
        {
            case "PreToolUse" when ClaudeEventReducer.IsTaskStopTool(hook.ToolName):
                // Claude Code 掐子代理时**不发 SubagentStop**（探针实测：一次掐掉只看到
                // SubagentStart + 主会话自己的 Stop），桥上就永远收不到终态。但 TaskStop 的入参
                // 带着目标 id，且与 SubagentStart 的 agent_id 逐字相同 —— 用这条把子代理行收敛掉。
                StopClaudeSubagent(hook.SessionID, ClaudeEventReducer.TaskStopTarget(hook.ToolInput), now);
                break;
            case "PreToolUse" when ClaudeEventReducer.IsQuestionTool(hook.ToolName):
                _requests[hook.SessionID] = new(
                    hook.SessionID,
                    PendingRequestKind.Question,
                    ClaudeEventReducer.QuestionText(hook.ToolInput) ?? "等待用户选择",
                    ClaudeEventReducer.QuestionOptions(hook.ToolInput));
                break;
            case "PermissionRequest" when ClaudeEventReducer.IsQuestionTool(hook.ToolName):
                // 提问工具的权限询问 = 提问本身：挂"问题"卡片（带选项），不挂审批卡片。
                // 作答要整份回填原始入参；拿不到原始入参就没有作答通道（收回 Answer 能力位，
                // 免得 App 画出一个发不出去的输入框）。
                if (hook.ToolInput?.GetRawText() is { Length: > 0 } rawInput)
                {
                    _claudeQuestionInputs[hook.SessionID] = rawInput;
                    _claudeQuestionProgress.Remove(hook.SessionID);   // 新一次提问，进度从头算
                    PresentClaudeQuestion(hook.SessionID);            // 单题=整题；多题=先呈现第 1 题
                }
                else
                {
                    _claudeQuestionInputs.Remove(hook.SessionID);
                    _claudeQuestionProgress.Remove(hook.SessionID);
                    _requests[hook.SessionID] = new(
                        hook.SessionID,
                        PendingRequestKind.Question,
                        ClaudeEventReducer.QuestionText(hook.ToolInput) ?? "等待用户选择",
                        ClaudeEventReducer.QuestionOptions(hook.ToolInput));
                    Upsert(_tasks.First(task => task.Id == hook.SessionID) with { Capabilities = [] });
                }
                break;
            case "PermissionRequest":
                _requests[hook.SessionID] = new(
                    hook.SessionID,
                    PendingRequestKind.Approval,
                    ClaudeEventReducer.Summary(hook.ToolInput, hook.Cwd) ?? hook.ToolName,
                    []);
                _claudeApprovalSource[hook.SessionID] = string.Empty; // 父级询问
                break;
            case "Notification":
                if (ClaudeEventReducer.IsAnswerNotification(hook.NotificationType))
                    _requests[hook.SessionID] = new(
                        hook.SessionID,
                        PendingRequestKind.Question,
                        hook.Title ?? hook.Message,
                        []);
                break;
            // 挂起卡片的撤销点写得明确，不用"lifecycle 不是等待态就删"这种兜底：
            // 并行工具批次里任意一个 PostToolUse 都会把卡片撤掉，兜底会连正在等答复的卡一起撤。
            case "PostToolUse":
            case "PostToolUseFailure":
            case "Stop":
            case "StopFailure":
            case "SessionEnd":
            case "PermissionDenied":
                DropClaudeRequest(hook.SessionID);
                break;
        }

        // Stop 终稿的竞速补救：两路（转录本 / last_assistant_message）都没拿到正文时登记，
        // 交给 Maintain 在 1 分钟窗口内重读一次转录本。只在本回合第三行**整个是空**时登记——
        // 若已经显示着工具步，那是设计上要保留的（agy 同规则），不能拿更早的正文去盖。
        if (hook.HookEventName == "Stop" && !string.IsNullOrWhiteSpace(hook.TranscriptPath) &&
            _tasks.First(task => task.Id == hook.SessionID).LatestStep is null)
        {
            _claudePreviewRecheck[hook.SessionID] = (hook.TranscriptPath!, now.AddMinutes(1));
        }
        else if (hook.HookEventName == "UserPromptSubmit")
        {
            _claudePreviewRecheck.Remove(hook.SessionID);
        }

        Changed();
        return true;
    }

    /// <summary>
    /// 把被 <c>TaskStop</c> 掐掉的子代理行收敛成 <c>interrupted</c>。
    ///
    /// 为什么需要它：Claude Code 在子代理被掐掉时**不发 `SubagentStop`**（本机 2.1.236 探针实测，
    /// 2026-10-11：一次"起子代理→TaskStop"的完整回合里，SubagentStop 钩子一次都没触发，
    /// 只有 SubagentStart 与主会话自己的 Stop）——没有这条补丁，那条子代理行会永远停在 running，
    /// 父任务也永远显示 delegating。目标不是子代理（例如后台 bash 任务）时静默跳过。
    /// </summary>
    private void StopClaudeSubagent(string sessionID, string? targetID, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(targetID)) return;
        var parent = _tasks.FirstOrDefault(task => task.Id == sessionID);
        if (parent is null) return;

        var index = parent.Subagents.FindIndex(value => value.Id == targetID);
        if (index < 0 || parent.Subagents[index].IsTerminal) return;

        var subagents = parent.Subagents.ToList();
        subagents[index] = subagents[index] with
        {
            // 被掐掉 = 中断，不是完成（与 opencode 的 failed、agy 的 TerminationReason=error 同一口径）
            Lifecycle = AgentLifecycle.Interrupted,
            Activity = null,
            LatestStep = "✗ 已被停止",
            UpdatedAt = now,
        };
        // 父任务的活动位不用在这里算：Accept 结尾的 Changed() → Normalize 会按"还有没有非终态子代理"
        // 自动把 delegating 收回 thinking。
        Upsert(parent with { Subagents = subagents, UpdatedAt = now });
    }

    /// <summary>
    /// 撤掉某会话的挂起项与全部伴随状态。**所有撤销点都走这里**——
    /// 以前只 `_requests.Remove`，于是 `_claudeApprovalSource`/`_claudeQuestionInputs` 会跟着漂。
    /// </summary>
    private void DropClaudeRequest(string sessionID)
    {
        _requests.Remove(sessionID);
        _claudeApprovalSource.Remove(sessionID);
        _claudeQuestionInputs.Remove(sessionID);
        _claudeQuestionProgress.Remove(sessionID);
    }

    /// <summary>
    /// 从原始 tool_input 里读出各题：全文 / 短标签（header 优先）/ 选项（**纯 label** 与**显示文本**两套）。
    /// 显示文本是 `label（注解）`，用于画按钮；纯 label 用于把回传值还原成答案，两套必须分开。
    /// </summary>
    private static List<(string Text, string Short, List<string> Labels, List<string> Display)> RawQuestions(string rawInput)
    {
        var result = new List<(string, string, List<string>, List<string>)>();
        try
        {
            if (JsonNode.Parse(rawInput) is not JsonObject root || root["questions"] is not JsonArray questions)
                return result;
            foreach (var node in questions)
            {
                if (node is not JsonObject question) continue;
                var header = NodeText(question, "header");
                var text = NodeText(question, "question") ?? header;
                if (string.IsNullOrWhiteSpace(text)) continue;

                var labels = new List<string>();
                var display = new List<string>();
                if (question["options"] is JsonArray items)
                    foreach (var item in items)
                    {
                        if (item is not JsonObject option) continue;
                        if (NodeText(option, "label") is not { Length: > 0 } label) continue;
                        labels.Add(label);
                        display.Add(ClaudeEventReducer.OptionDisplayText(label, NodeText(option, "description")));
                    }

                result.Add((text, string.IsNullOrWhiteSpace(header) ? text : header, labels, display));
            }
        }
        catch
        {
            // 原始入参不可解析：当作"没有题"，调用方退回旧行为。
        }
        return result;
    }

    private static string? NodeText(JsonNode? node, string name) =>
        node?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>
    /// 建/推进提问卡片。多题时**只呈现当前这一题**（题目 + 它自己的选项），App 照旧渲染单题那张卡
    /// —— 于是多题在手机上变成"一次一题、每题都能点"，而协议一个字都不用改。
    /// 单题就是"只有一题"的退化情形，行为与以前完全一致。
    /// </summary>
    private void PresentClaudeQuestion(string sessionID)
    {
        if (!_claudeQuestionInputs.TryGetValue(sessionID, out var raw)) return;
        var questions = RawQuestions(raw);
        if (questions.Count == 0) return;

        var progress = _claudeQuestionProgress.TryGetValue(sessionID, out var current) && current.Index < questions.Count
            ? current
            : new ClaudeQuestionProgress(0, new Dictionary<string, string>(StringComparer.Ordinal));
        _claudeQuestionProgress[sessionID] = progress;

        var (text, shortText, _, display) = questions[progress.Index];
        var prefix = questions.Count > 1 ? $"第 {progress.Index + 1}/{questions.Count} 题： " : string.Empty;
        _requests[sessionID] = new(sessionID, PendingRequestKind.Question, prefix + text, display);

        // 多题时第三行跟着"当前这一题"走；单题保持 reducer 给的短标签（行为不变）。
        if (questions.Count > 1 && _tasks.FirstOrDefault(candidate => candidate.Id == sessionID) is { } task)
            Upsert(task with { LatestStep = $"第 {progress.Index + 1}/{questions.Count} 题： {shortText}" });
    }

    /// <summary>
    /// 子代理事件 → 父任务的 Subagents 行。父任务的 Lifecycle **永不**被这里改动
    /// （G14：子代理在父任务已完成后收尾是很常见的，旧实现无条件写回 Running 会复活终态任务）。
    /// 唯一例外是子代理的权限询问——它阻塞的是父任务这一回合，卡片必须挂父任务上，
    /// 手机才能放行（HookTcpServer 的待决表本来就按 session_id 索引，回包链路一致）。
    /// </summary>
    private bool AcceptClaudeSubagent(ClaudeHookPayload hook, DateTimeOffset now)
    {
        var parent = _tasks.FirstOrDefault(task => task.Id == hook.SessionID);
        if (parent is null) return false;

        if (hook.HookEventName == "PermissionRequest")
        {
            if (parent.IsTerminal) return false;
            // 子代理同样可能"问问题"（AskUserQuestion 是 requiresUserInteraction 工具，也会发权限询问）。
            // 卡片类型必须跟着走：按审批处理的话，手机上会出现用不了的允许/拒绝按钮，
            // 而且问题文本会被换成工具名——父亲级踩过同一个坑，子代理这边一样。
            var isQuestion = ClaudeEventReducer.IsQuestionTool(hook.ToolName);
            if (isQuestion && hook.ToolInput?.GetRawText() is { Length: > 0 } rawInput)
            {
                _claudeQuestionInputs[parent.Id] = rawInput;
                _claudeQuestionProgress.Remove(parent.Id);
            }
            else
            {
                _claudeQuestionInputs.Remove(parent.Id);
                _claudeQuestionProgress.Remove(parent.Id);
            }

            if (isQuestion && _claudeQuestionInputs.ContainsKey(parent.Id))
                PresentClaudeQuestion(parent.Id);   // 子代理的多题同样逐题推进
            else
                _requests[parent.Id] = new(
                    parent.Id,
                    isQuestion ? PendingRequestKind.Question : PendingRequestKind.Approval,
                    isQuestion
                        ? ClaudeEventReducer.QuestionText(hook.ToolInput) ?? "等待用户选择"
                        : ClaudeEventReducer.Summary(hook.ToolInput, hook.Cwd) ?? hook.ToolName,
                    isQuestion ? ClaudeEventReducer.QuestionOptions(hook.ToolInput) : []);
            _claudeApprovalSource[parent.Id] = hook.AgentID!;

            var capabilities = new HashSet<TaskCapability>();
            if (isQuestion)
            {
                if (_claudeQuestionInputs.ContainsKey(parent.Id)) capabilities.Add(TaskCapability.Answer);
            }
            else
            {
                capabilities.Add(TaskCapability.Approve);
                capabilities.Add(TaskCapability.Deny);
            }

            Upsert(parent with
            {
                Lifecycle = isQuestion ? AgentLifecycle.WaitingAnswer : AgentLifecycle.WaitingApproval,
                Activity = isQuestion ? AgentActivity.Thinking : AgentActivity.Executing,
                CompletedAt = null,
                Capabilities = capabilities,
                UpdatedAt = now,
            });
            Changed();
            return true;
        }

        // 子代理内部再掐它自己的子代理（嵌套 TaskStop）：入参里的 task_id 同样是子代理 id，
        // 而所有子代理行都挂在同一个父任务下，所以用同一个收敛函数即可（目标行不在就静默跳过）。
        if (hook.HookEventName == "PreToolUse" && ClaudeEventReducer.IsTaskStopTool(hook.ToolName))
        {
            StopClaudeSubagent(parent.Id, ClaudeEventReducer.TaskStopTarget(hook.ToolInput), now);
            Changed();
            return true;
        }

        var id = hook.AgentID!;
        var existing = parent.Subagents.FirstOrDefault(value => value.Id == id);

        // 子代理自己的权限/提问询问，撤销点也要落在**它自己**身上：
        // 卡片是挂在父任务上的，而子代理的事件根本不进父级那段撤销清单，于是终端作答后
        // 父任务会一直停在等待态、手机留一张点不动的卡，挂住的 node hook 也要等父级下一次事件才回收
        // （实测踩过）。这里认来源：只有"挂卡的就是这个子代理"且它这一步结束了，才撤。
        if (hook.HookEventName is "PostToolUse" or "PostToolUseFailure" or "PermissionDenied" or "SubagentStop" &&
            _claudeApprovalSource.TryGetValue(parent.Id, out var pendingSource) &&
            pendingSource == id)
        {
            DropClaudeRequest(parent.Id);
            // 等待结束 → 父任务回到运行（它的回合还在跑，只是刚才被这条询问拦住）
            parent = parent with
            {
                Lifecycle = AgentLifecycle.Running,
                Capabilities = [],
                CompletedAt = null,
            };
        }

        // **只有 SubagentStart 能新建行。** 没有配对的 Start 就来一条 Stop 是实测会发生的事
        // （那条 Stop 的 agent_type 为空），若也允许新建，手机上就会凭空冒出一行
        // "已完成"的幽灵子代理——用户看到的就是"突然多出一个绿色的子代理"。
        if (existing is null && hook.HookEventName != "SubagentStart") return false;

        var agentName = string.IsNullOrWhiteSpace(hook.AgentType)
            ? existing?.DisplayName ?? "Claude 子代理"
            : hook.AgentType!.Trim();
        var subagent = existing ?? new(id, "/" + agentName, agentName, AgentLifecycle.Running,
            AgentActivity.Delegating, null, null, now, now);

        switch (hook.HookEventName)
        {
            case "SubagentStart":
                subagent = subagent with
                {
                    DisplayName = agentName,
                    Path = "/" + agentName,
                    Lifecycle = AgentLifecycle.Running,
                    Activity = AgentActivity.Delegating,
                };
                break;
            case "PreToolUse":
                if (subagent.IsTerminal) break;
                subagent = subagent with
                {
                    Lifecycle = AgentLifecycle.Running,
                    Activity = ClaudeEventReducer.ActivityFor(hook.ToolName, hook.ToolInput),
                    LatestStep = CodexEventReducer.LatestStep(
                        hook.ToolName, ClaudeEventReducer.Summary(hook.ToolInput, hook.Cwd)) ?? subagent.LatestStep,
                };
                break;
            case "PostToolUse":
            case "PostToolUseFailure":
                if (subagent.IsTerminal) break;
                subagent = subagent with
                {
                    Lifecycle = AgentLifecycle.Running,
                    Activity = AgentActivity.Thinking,
                    LatestStep = hook.HookEventName == "PostToolUseFailure"
                        ? ClampText($"✗ {ClaudeEventReducer.ErrorSummary(hook) ?? "工具失败"}", 220)
                        : subagent.LatestStep,
                };
                break;
            case "SubagentStop":
                subagent = subagent with { Lifecycle = AgentLifecycle.Succeeded, Activity = null };
                // 子代理自己的转录本（agent_transcript_path）与主会话同格式，读一次取用量。
                if (!string.IsNullOrWhiteSpace(hook.AgentTranscriptPath))
                {
                    var snapshot = ClaudeTranscriptReader.Read(hook.AgentTranscriptPath);
                    if (snapshot.Tokens is not null) subagent = subagent with { TokenUsage = snapshot.Tokens };
                    if (snapshot.LatestReply is not null) subagent = subagent with { LatestStep = snapshot.LatestReply };
                }
                break;
            default:
                break;
        }

        subagent = subagent with { UpdatedAt = now };
        var subagents = parent.Subagents.Where(value => value.Id != id).Append(subagent)
            .OrderBy(value => value.IsTerminal).ThenBy(value => value.StartedAt).ToList();
        var anyRunning = subagents.Any(value => !value.IsTerminal);

        Upsert(parent with
        {
            Subagents = subagents,
            UpdatedAt = now,
            // 只动 Activity，且只在父任务确实在跑时——终态父任务一个字段都不碰。
            Activity = parent.Lifecycle == AgentLifecycle.Running
                ? anyRunning
                    ? AgentActivity.Delegating
                    : parent.Activity == AgentActivity.Delegating ? AgentActivity.Thinking : parent.Activity
                : parent.Activity,
        });
        Changed();
        return true;
    }

    /// <summary>
    /// 只在需要转录本的时机读（512 KB 尾部）：会话/回合边界取标题与 Token，回合结束取正文预览。
    /// 工具事件一律不读——每次工具调用读一遍文件是纯浪费，而它并不提供第三行需要的东西。
    /// </summary>
    private static ClaudeTranscriptSnapshot? ReadClaudeTranscript(ClaudeHookPayload hook) =>
        hook.HookEventName is "SessionStart" or "UserPromptSubmit" or "Stop" or "StopFailure"
            ? ClaudeTranscriptReader.Read(hook.TranscriptPath)
            : null;

    public bool Accept(OpenCodeHookPayload hook, DateTimeOffset? receivedAt = null)
    {
        var now = receivedAt ?? DateTimeOffset.UtcNow;
        var sessionID = OpenCodeEventReducer.SessionID(hook);
        if (sessionID is null) return false;
        // 乱序残帧守卫：工具 part 处于 pending/running 期间，reply 一律丢弃。
        // 状态判定不依赖 activity，覆盖 todowrite 类 thinking 工具；不做时间窗，
        // 避免“工具结束后 1 秒内的合法短终稿被吞”（乱序残帧源头拦截由插件 v8 结束帧守卫负责）。
        if (hook.Event == "reply" && _openCodeToolActive.Contains(sessionID))
            return false;
        if (hook.Event == "message.part.updated" && OpenCodeEventReducer.ToolPartStatus(hook.Properties) is { } partStatus)
        {
            if (partStatus is "pending" or "running") _openCodeToolActive.Add(sessionID);
            else _openCodeToolActive.Remove(sessionID);
        }
        if (hook.Event is "session.idle" or "session.error" or "plugin.dispose" or "session.deleted")
            _openCodeToolActive.Remove(sessionID);
        var existing = _tasks.FirstOrDefault(task => task.Id == sessionID);

        // 会话目录**必须在不建行早退之前登记**：它只在 session.created/updated 出现（chat.message
        // 不带），而 session.created 恰恰是不再建行的那条 —— 挡在后面的话行永远拿不到目录。
        if (hook.Event is "session.created" or "session.updated" &&
            OpenCodeEventReducer.Directory(hook.Properties) is { Length: > 0 } directory)
            _openCodeDirectories[sessionID] = directory;

        // 不造幽灵任务（三家同一条规矩，见 Claude 的 SessionStart 处置）：
        //   · plugin.dispose / session.deleted / subagent 只"结束/归拢"已知会话；
        //   · **session.created 也只登记不建行**——opencode 一建会话（TUI 一启动）就发这条，
        //     而那一刻会话还没有标题，建出来的行只能顶着项目目录名（实测 `opencode run` 启动即
        //     冒出一行 title=项目名 / prompt 空 / idle 的无名幽灵，直到第一个提示词到达）。
        //     行改由 chat.message 建，那时提示词、标题都已在手。
        if (existing is null && hook.Event is "plugin.dispose" or "session.deleted" or "subagent" or "session.created") return false;

        Upsert(OpenCodeEventReducer.Reduce(hook, existing, now, _openCodeDirectories.GetValueOrDefault(sessionID)));
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

    public bool Accept(AgyHookPayload hook, DateTimeOffset? receivedAt = null)
    {
        var now = receivedAt ?? DateTimeOffset.UtcNow;
        var sessionID = hook.SessionID;
        if (string.IsNullOrWhiteSpace(sessionID)) return false;

        // 1. 查询会话元数据（SQLite，mtime 门控缓存，A2）：会话真实标题与父会话 ID
        var dbInfo = LookupAgyConversation(sessionID);

        // 2. 解析父会话（内存映射 / 先验派发窗口认领 / 转录本 sender 探测 / SQLite 四重保障）
        var parentSessionID = ResolveAgyParentSession(hook, now, out var resolvedRole);
        parentSessionID ??= !string.IsNullOrWhiteSpace(dbInfo?.ParentConversationId) ? dbInfo.ParentConversationId : null;

        // 3. 如果是子代理会话，更新父任务的 Subagents 列表，绝不建立幽灵顶层任务
        if (parentSessionID is not null)
        {
            // 物理剔除顶层幽灵任务（若此前首包由于时差被误建为顶层任务）
            _tasks.RemoveAll(task => task.Id == sessionID);

            var parentTask = _tasks.FirstOrDefault(task => task.Id == parentSessionID);
            if (parentTask is not null)
            {
                var updatedParent = ApplyAgySubagent(parentTask, hook, dbInfo, resolvedRole, now);
                Upsert(updatedParent);
                Changed();
                return true;
            }
            return true;
        }

        // 4. 顶层主任务状态流转
        var existing = _tasks.FirstOrDefault(task => task.Id == sessionID);
        var updated = AgyEventReducer.Reduce(hook, existing, dbInfo, now);
        Upsert(updated);

        // 侦测父任务调用的 invoke_subagent，记录子代理绑定关系与先验派发窗口
        InspectInvokeSubagent(hook, now);

        // Stop 终稿落盘竞速补救登记（条目 1）：预览缺失时交给 Maintain 限期内重读。
        if (hook.EffectiveEvent == "Stop" && !string.IsNullOrWhiteSpace(hook.TranscriptPath))
        {
            var (_, preview, _) = AgyTranscriptReader.ExtractLatest(hook.TranscriptPath);
            if (preview is null) _agyPreviewRecheck[sessionID] = (hook.TranscriptPath, now.AddMinutes(1));
            else _agyPreviewRecheck.Remove(sessionID);
        }

        // 5. 用户交互问题（ask_question）挂起与移除逻辑
        if (hook.EffectiveEvent == "PreToolUse" &&
            hook.ToolCall?.Name?.Equals("ask_question", StringComparison.OrdinalIgnoreCase) == true)
        {
            var questionText = AgyEventReducer.QuestionText(hook.ToolCall?.Args) ?? "等待输入与选择";
            var questionOptions = AgyEventReducer.QuestionOptions(hook.ToolCall?.Args);
            _requests[sessionID] = new(
                sessionID,
                PendingRequestKind.Question,
                questionText,
                questionOptions);
        }
        else if (hook.EffectiveEvent == "PostToolUse" &&
                 hook.ToolCall?.Name?.Equals("ask_question", StringComparison.OrdinalIgnoreCase) == true)
        {
            // 问题作答完毕，移除挂起请求
            _requests.Remove(sessionID);
        }
        else if (hook.EffectiveEvent is "Stop")
        {
            _requests.Remove(sessionID);
        }
        else
        {
            if (updated.Lifecycle is not (AgentLifecycle.Starting or AgentLifecycle.WaitingApproval or AgentLifecycle.WaitingAnswer))
                _requests.Remove(sessionID);
        }

        Changed();
        return true;
    }

    private string? ResolveAgyParentSession(AgyHookPayload hook, DateTimeOffset now, out string? role)
    {
        role = null;
        var sessionID = hook.SessionID;
        if (string.IsNullOrWhiteSpace(sessionID)) return null;

        // 通道 1：内存映射缓存（此前事件已解析出）
        if (_agyChildToParent.TryGetValue(sessionID, out var parentId))
        {
            _agyChildRoles.TryGetValue(sessionID, out role);
            return parentId;
        }

        // 通道 2：先验派发窗口直接认领（彻底消除子代理 PreInvocation 首帧转录本尚未落盘的竞态闪烁）
        // 若当前会话尚未在顶层建立，且系统内有父任务刚刚调用 invoke_subagent 正在派发，
        // 则在首帧第一微秒立即认领该子代理，赋予对应 Role，物理杜绝独立幽灵卡片闪现。
        if (_tasks.All(t => t.Id != sessionID) && _agySpawningParents.Count > 0)
        {
            foreach (var (parentCandidate, entry) in _agySpawningParents.ToList())
            {
                if (now <= entry.Deadline && entry.Roles.Count > 0)
                {
                    role = entry.Roles[0];
                    entry.Roles.RemoveAt(0);
                    if (entry.Roles.Count == 0) _agySpawningParents.Remove(parentCandidate);
                    _agyChildToParent[sessionID] = parentCandidate;
                    _agyChildRoles[sessionID] = role;
                    return parentCandidate;
                }
            }
        }

        // **通道 3 的前置门：DB 有行时以 DB 为准。**（2026-10-11 实测踩到的误判）
        // DB 里 `parent_conversation_id` 为空 = 这是根会话；此时**绝不能**再拿转录本启发式认父：
        // 根会话的转录本前 15 行里会出现"子代理回报"的系统消息（形如
        // `[Message] timestamp=… sender=<子代理id>`），启发式会把**自己的子代理**当成它的父，
        // 父子方向整个颠倒 —— 后果是根会话被当子会话、顶层行被物理删除（同款门见 PruneAgyGhostTasks）。
        var dbInfo = LookupAgyConversation(sessionID);
        if (dbInfo is not null && string.IsNullOrWhiteSpace(dbInfo.ParentConversationId))
            return null;

        // 通道 3：转录本首帧 sender 探测（零时差，首毫秒即命中）。只在**DB 还没有这一行**时使用
        // ——它的存在意义就是覆盖 SQLite 落盘时差，DB 一旦有行，权威性高于 `sender=` 一行文本。
        var transcriptPath = hook.TranscriptPath;
        if (string.IsNullOrWhiteSpace(transcriptPath))
        {
            var defaultPath = AgyTranscriptReader.TranscriptPathFor(sessionID);
            if (File.Exists(defaultPath)) transcriptPath = defaultPath;
        }

        if (!string.IsNullOrWhiteSpace(transcriptPath) && File.Exists(transcriptPath))
        {
            var detectedParent = AgyTranscriptReader.DetectParentFromTranscript(transcriptPath);
            if (!string.IsNullOrWhiteSpace(detectedParent))
            {
                _agyChildToParent[sessionID] = detectedParent;
                if (_agyPendingParentRoles.TryGetValue(detectedParent, out var pending) && pending.Count > 0)
                {
                    role = pending[0];
                    pending.RemoveAt(0);
                    _agyChildRoles[sessionID] = role;
                }
                return detectedParent;
            }
        }

        // 通道 4：SQLite 元数据查询（dbInfo 已在前置门处取过）
        if (!string.IsNullOrWhiteSpace(dbInfo?.ParentConversationId))
        {
            _agyChildToParent[sessionID] = dbInfo.ParentConversationId;
            if (!string.IsNullOrWhiteSpace(dbInfo.Title))
            {
                role = dbInfo.Title;
                _agyChildRoles[sessionID] = role;
            }
            return dbInfo.ParentConversationId;
        }

        return null;
    }

    private void InspectInvokeSubagent(AgyHookPayload hook, DateTimeOffset now)
    {
        var sessionID = hook.SessionID;
        if (string.IsNullOrWhiteSpace(sessionID)) return;

        if (hook.EffectiveEvent == "PreToolUse" &&
            hook.ToolCall?.Name?.Equals("invoke_subagent", StringComparison.OrdinalIgnoreCase) == true)
        {
            var roles = ExtractRolesFromInvokeSubagent(hook.ToolCall?.Args);
            if (roles.Count == 0) roles.Add("Subagent");
            if (_agySpawningParents.TryGetValue(sessionID, out var entry))
            {
                entry.Roles.AddRange(roles);
                _agySpawningParents[sessionID] = (entry.Roles, now.AddSeconds(30));
            }
            else
            {
                _agySpawningParents[sessionID] = (roles, now.AddSeconds(30));
            }
        }

        if (!string.IsNullOrWhiteSpace(hook.TranscriptPath))
        {
            var createdSubs = AgyTranscriptReader.DetectCreatedSubagents(hook.TranscriptPath);
            foreach (var childId in createdSubs)
            {
                if (!_agyChildToParent.ContainsKey(childId))
                {
                    _agyChildToParent[childId] = sessionID;
                    if (_agyPendingParentRoles.TryGetValue(sessionID, out var pending) && pending.Count > 0)
                    {
                        var role = pending[0];
                        pending.RemoveAt(0);
                        _agyChildRoles[childId] = role;
                    }
                }
            }
        }
    }

    private static List<string> ExtractRolesFromInvokeSubagent(JsonElement? args)
    {
        var result = new List<string>();
        if (args is null) return result;
        try
        {
            if (args.Value.TryGetProperty("Subagents", out var subsProp))
            {
                JsonElement arrayElement;
                JsonDocument? parsedDoc = null;
                if (subsProp.ValueKind == JsonValueKind.String)
                {
                    parsedDoc = JsonDocument.Parse(subsProp.GetString()!);
                    arrayElement = parsedDoc.RootElement;
                }
                else
                {
                    arrayElement = subsProp;
                }

                if (arrayElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in arrayElement.EnumerateArray())
                    {
                        if (item.TryGetProperty("Role", out var roleProp) && roleProp.GetString() is { Length: > 0 } role)
                        {
                            result.Add(role);
                        }
                    }
                }
                parsedDoc?.Dispose();
            }
        }
        catch { }
        return result;
    }

    private static TaskSnapshot ApplyAgySubagent(TaskSnapshot parent, AgyHookPayload hook, AgyConversationSummary? dbInfo, string? customRole, DateTimeOffset now)
    {
        var id = hook.SessionID;
        var existing = parent.Subagents.FirstOrDefault(item => item.Id == id);
        if (existing?.IsTerminal == true && hook.EffectiveEvent != "PostInvocation") return parent;

        var agentName = dbInfo?.AgentName ?? "research";
        var role = !string.IsNullOrWhiteSpace(customRole) ? customRole : (!string.IsNullOrWhiteSpace(dbInfo?.Title) ? dbInfo.Title : agentName);
        var displayName = SubagentDisplayName(role, null);
        var path = existing?.Path ?? ("/" + agentName);

        var subagent = existing ?? new SubagentSnapshot(
            id, path, displayName,
            AgentLifecycle.Running, AgentActivity.Thinking,
            null, null, now, now);
        subagent = subagent with { UpdatedAt = now };

        switch (hook.EffectiveEvent)
        {
            case "PreInvocation":
                subagent = subagent with { Lifecycle = AgentLifecycle.Running, Activity = AgentActivity.Thinking };
                break;
            case "PreToolUse":
            {
                var tool = hook.ToolCall?.Name;
                var activity = AgyEventReducer.ActivityFor(tool ?? "", hook.ToolCall?.Args);
                var step = AgyEventReducer.FormatToolStep(tool, hook.ToolCall?.Args, hook.Cwd);
                subagent = subagent with
                {
                    Lifecycle = AgentLifecycle.Running,
                    Activity = activity,
                    LatestStep = step is not null ? ClampText(step, 220) : subagent.LatestStep,
                };
                break;
            }
            case "PostToolUse":
                subagent = subagent with
                {
                    Lifecycle = AgentLifecycle.Running,
                    Activity = AgentActivity.Thinking,
                };
                break;
            case "Stop":
            {
                var isError = hook.TerminationReason == "error" || !string.IsNullOrWhiteSpace(hook.Error);
                subagent = subagent with
                {
                    Lifecycle = isError ? AgentLifecycle.Interrupted : AgentLifecycle.Succeeded,
                    Activity = null,
                    LatestStep = isError ? ClampText($"✗ {hook.Error ?? "子代理异常终止"}", 220) : subagent.LatestStep,
                };
                break;
            }
        }

        if (!string.IsNullOrWhiteSpace(hook.TranscriptPath))
        {
            var (_, _, tokens) = AgyTranscriptReader.ExtractLatest(hook.TranscriptPath);
            if (tokens is not null) subagent = subagent with { TokenUsage = tokens };
        }

        // 关键：剔除同一子代理的临时占位符（如 research-123456），确保真实 UUID 子代理唯一
        var subagents = parent.Subagents
            .Where(item => item.Id != id && !item.Id.StartsWith($"{agentName}-", StringComparison.OrdinalIgnoreCase))
            .Append(subagent)
            .OrderBy(item => item.IsTerminal).ThenBy(item => item.StartedAt).ToList();

        return parent with
        {
            Subagents = subagents,
            UpdatedAt = now,
            Activity = parent.Lifecycle == AgentLifecycle.Running
                ? subagents.Any(item => !item.IsTerminal)
                    ? AgentActivity.Delegating
                    : (parent.Activity == AgentActivity.Delegating ? AgentActivity.Thinking : parent.Activity)
                : parent.Activity,
        };
    }

    private static string SubagentDisplayName(string agentName, string? title)
    {
        if (!string.IsNullOrWhiteSpace(title)) return ClampText(title, 40);
        return agentName.Length > 0 ? char.ToUpperInvariant(agentName[0]) + agentName[1..] : "Subagent";
    }

    private static string ClampText(string value, int limit) => value.Length > limit ? value[..limit] : value;

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

    private DateTimeOffset? _lastAgyDbModified;
    // Accept 路径的会话元数据缓存（mtime 门控，A2）：避免每个 hook 事件开一次 SQLite。
    private DateTime? _agyAcceptDbMtime;
    private readonly Dictionary<string, AgyConversationSummary?> _agyAcceptDbCache = new();
    // Stop 终稿落盘竞速补救（条目 1）：转录本路径 + 重读期限。
    private readonly Dictionary<string, (string TranscriptPath, DateTimeOffset Deadline)> _agyPreviewRecheck = new();
    // 子代理与父任务映射关系与角色记录（通道 1 转录本 sender 探测 / 通道 2 invoke_subagent / 通道 3 SQLite）
    private readonly Dictionary<string, string> _agyChildToParent = new();
    private readonly Dictionary<string, string> _agyChildRoles = new();
    private readonly Dictionary<string, List<string>> _agyPendingParentRoles = new();
    // 先验派发窗口：父任务调用 invoke_subagent 时开启，子代理在 PreInvocation 首帧零时差直接认领绑定
    private readonly Dictionary<string, (List<string> Roles, DateTimeOffset Deadline)> _agySpawningParents = new();


    public bool Maintain(DateTimeOffset? now = null)
    {
        var currentTime = now ?? DateTimeOffset.UtcNow;
        var changed = CheckAgyQueuedPrompts(currentTime);
        changed = CheckSilentOffline(currentTime) || changed;
        changed = CheckSubagentSilence(currentTime) || changed;
        changed = CheckAgyPreviewRecheck(currentTime) || changed;
        changed = MaintainAgyDatabase(currentTime) || changed;
        changed = CheckClaudePreviewRecheck(currentTime) || changed;
        changed = CheckClaudeRuntime(currentTime) || changed;
        foreach (var key in _agySpawningParents.Where(p => currentTime > p.Value.Deadline).Select(p => p.Key).ToList())
            _agySpawningParents.Remove(key);
        var before = _tasks.Count + _tasks.Sum(task => task.Subagents.Count);
        Normalize(currentTime);
        if (changed || before != _tasks.Count + _tasks.Sum(task => task.Subagents.Count))
        {
            Changed();
            return true;
        }
        return false;
    }

    private bool MaintainAgyDatabase(DateTimeOffset now)
    {
        if (!_tasks.Any(task => task.Source == AgentSource.Antigravity))
            return false;

        var dbPath = AgyConversationDatabase.DefaultDbPath;
        try
        {
            var changed = false;
            if (File.Exists(dbPath))
            {
                var lastWrite = File.GetLastWriteTimeUtc(dbPath);
                if (_lastAgyDbModified is null || lastWrite > _lastAgyDbModified.Value)
                {
                    _lastAgyDbModified = lastWrite;
                    changed = CheckAgyTitles(now, dbPath) || changed;
                }
            }
            // 扫描幽灵任务：不论 DB 文件是否更新，转录本或已登记映射随时可能命中子代理并清理
            changed = PruneAgyGhostTasks(now, dbPath) || changed;
            return changed;
        }
        catch
        {
            return false;
        }
    }

    private bool PruneAgyGhostTasks(DateTimeOffset now, string dbPath)
    {
        var changed = false;
        for (var i = _tasks.Count - 1; i >= 0; i--)
        {
            var task = _tasks[i];
            if (task.Source != AgentSource.Antigravity) continue;

            string? parentId = null;
            string? roleName = null;

            if (_agyChildToParent.TryGetValue(task.Id, out var mappedParent))
            {
                parentId = mappedParent;
                _agyChildRoles.TryGetValue(task.Id, out roleName);
            }

            AgyConversationSummary? dbInfo = null;
            if (File.Exists(dbPath))
            {
                dbInfo = AgyConversationDatabase.Query(task.Id, dbPath);
                if (!string.IsNullOrWhiteSpace(dbInfo?.ParentConversationId))
                {
                    parentId = dbInfo.ParentConversationId;
                    if (!string.IsNullOrWhiteSpace(dbInfo.Title)) roleName = dbInfo.Title;
                }
            }

            // 转录本启发式**只在 DB 没有这一行时**才允许开口（它存在的意义就是覆盖落盘时差）。
            // 2026-10-11 实测事故：主对话自己的转录本前 15 行里有子代理回报的系统消息
            // （`sender=<子代理id>`），启发式把**自己的子代理**当成了父 → 主对话被判成子会话 →
            // 顶层行被这里物理删除、又因为"父"（子代理 id）不是任务而无处可挂 → 整行凭空消失。
            // 同一份 DB 里 `nesting_depth=0`、`parent_conversation_id=''` 明明写着它是根。
            if (parentId is null && dbInfo is null)
            {
                var transcriptPath = AgyTranscriptReader.TranscriptPathFor(task.Id);
                if (File.Exists(transcriptPath))
                {
                    parentId = AgyTranscriptReader.DetectParentFromTranscript(transcriptPath);
                }
            }

            // 兜底防线：确认下来的"父"**必须是一个已知任务**才允许搬移——否则搬无可搬，
            // 删掉就是纯粹的数据丢失（宁可留一行可见的顶层行，也不要让它无声消失）。
            if (!string.IsNullOrWhiteSpace(parentId) && _tasks.All(t => t.Id != parentId))
                parentId = null;

            if (!string.IsNullOrWhiteSpace(parentId))
            {
                _tasks.RemoveAt(i);
                changed = true;

                var parent = _tasks.FirstOrDefault(t => t.Id == parentId);
                if (parent is not null)
                {
                    var agentName = dbInfo?.AgentName ?? "research";
                    var displayName = SubagentDisplayName(roleName ?? agentName, dbInfo?.Title);
                    var sub = new SubagentSnapshot(
                        task.Id, "/" + agentName, displayName,
                        task.Lifecycle, task.Activity ?? AgentActivity.Thinking,
                        task.LatestStep, task.TokenUsage,
                        task.StartedAt, task.UpdatedAt);
                    var updatedSubs = parent.Subagents
                        .Where(s => s.Id != task.Id && !s.Id.StartsWith($"{agentName}-", StringComparison.OrdinalIgnoreCase))
                        .Append(sub)
                        .OrderBy(s => s.IsTerminal).ThenBy(s => s.StartedAt).ToList();
                    var parentIdx = _tasks.FindIndex(t => t.Id == parentId);
                    if (parentIdx >= 0)
                    {
                        _tasks[parentIdx] = parent with
                        {
                            Subagents = updatedSubs,
                            UpdatedAt = now,
                            Activity = parent.Lifecycle == AgentLifecycle.Running
                                ? updatedSubs.Any(s => !s.IsTerminal)
                                    ? AgentActivity.Delegating
                                    : (parent.Activity == AgentActivity.Delegating ? AgentActivity.Thinking : parent.Activity)
                                : parent.Activity,
                        };
                    }
                }
            }
        }
        return changed;
    }

    private bool CheckAgyTitles(DateTimeOffset now, string dbPath)
    {
        var changed = false;
        for (var i = 0; i < _tasks.Count; i++)
        {
            var task = _tasks[i];
            if (task.Source != AgentSource.Antigravity) continue;

            var dbInfo = AgyConversationDatabase.Query(task.Id, dbPath);
            if (string.IsNullOrWhiteSpace(dbInfo?.Title)) continue;

            var expectedTitle = $"{task.ProjectName} · {dbInfo.Title}";
            if (task.Title != expectedTitle)
            {
                _tasks[i] = task with
                {
                    Title = expectedTitle,
                    UpdatedAt = now,
                };
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>agy 会话元数据读取（mtime 门控缓存，A2）：DB 文件未变即复用上次结果。</summary>
    private AgyConversationSummary? LookupAgyConversation(string sessionID)
    {
        var dbPath = AgyConversationDatabase.DefaultDbPath;
        if (!File.Exists(dbPath)) return null;
        var lastWrite = File.GetLastWriteTimeUtc(dbPath);
        if (_agyAcceptDbMtime != lastWrite)
        {
            _agyAcceptDbMtime = lastWrite;
            _agyAcceptDbCache.Clear();
        }
        if (_agyAcceptDbCache.TryGetValue(sessionID, out var cached)) return cached;
        var value = AgyConversationDatabase.Query(sessionID, dbPath);
        if (value is not null)
        {
            _agyAcceptDbCache[sessionID] = value;
        }
        return value;
    }

    private bool CheckAgyQueuedPrompts(DateTimeOffset now)
    {
        var changed = false;
        for (var i = 0; i < _tasks.Count; i++)
        {
            var task = _tasks[i];
            if (task.Source != AgentSource.Antigravity) continue;
            if (AgyEventReducer.DetectQueuedPrompt(task.Id, task) is not { } queued) continue;
            if (!task.PromptQueued || task.UserPrompt != queued.Prompt)
            {
                _tasks[i] = task with
                {
                    UserPrompt = queued.Prompt,
                    PromptQueued = true,
                    UpdatedAt = now,
                };
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>Stop 终稿落盘竞速补救（条目 1）：限期内重读转录本，任务仍处终态时补写正文预览；
    /// 新回合已开始（非终态）或超期即放弃。</summary>
    private bool CheckAgyPreviewRecheck(DateTimeOffset now)
    {
        if (_agyPreviewRecheck.Count == 0) return false;
        var changed = false;
        foreach (var key in _agyPreviewRecheck.Keys.ToList())
        {
            var (transcriptPath, deadline) = _agyPreviewRecheck[key];
            var index = _tasks.FindIndex(task => task.Id == key);
            if (index < 0 || now > deadline || !_tasks[index].IsTerminal)
            {
                _agyPreviewRecheck.Remove(key);
                continue;
            }
            if (AgyEventReducer.TryApplyLatePreview(_tasks[index], transcriptPath) is not { } patched)
                continue;
            if (patched.LatestStep != _tasks[index].LatestStep)
            {
                _tasks[index] = patched with { UpdatedAt = now };
                changed = true;
            }
            _agyPreviewRecheck.Remove(key);
        }
        return changed;
    }

    /// <summary>agy 无退出钩子：静默 ≥10 分钟且无工具活动（activity 为空或 thinking）时判为离线（A4）；
    /// 长工具运行中（executing/reading 等）不算静默。</summary>
    /// <summary>
    /// Claude Code 的心跳：离线判定 + 转录本刷新（标题 / Token / 排队）。
    ///
    /// 离线有两条判据，都是**正判**，不确定时什么都不做：
    /// ① <c>~/.claude/sessions/&lt;pid&gt;.json</c>：Claude 正常退出时自己 unlink 这份文件
    ///    （实测源码 process.on("exit", …unlinkSync) + 异步兜底），崩溃残留由它的 sweep 清理，
    ///    所以"见过、后来又没了"就是离线。注册并非所有模式都开，**没见过就不敢断**。
    /// ② 兜底：非终态且静默 ≥10 分钟且没有工具活动（与 agy 的 A4 同一条规则）——
    ///    覆盖强杀、注册未开、注册表被清空等所有拿不到正判的情况。
    /// </summary>
    private bool CheckClaudeRuntime(DateTimeOffset now)
    {
        var claude = _tasks.Where(task => task.Source == AgentSource.ClaudeCode).ToList();
        if (claude.Count == 0) return false;

        var changed = false;
        var registry = ClaudeSessionRegistry.Scan();

        foreach (var task in claude)
        {
            if (task.IsTerminal || task.Lifecycle == AgentLifecycle.Offline) continue;

            if (registry.ContainsKey(task.Id))
            {
                _claudeRegistrySeen.Add(task.Id);
            }
            else if (_claudeRegistrySeen.Contains(task.Id) ||
                     now - task.UpdatedAt > TimeSpan.FromMinutes(10))
            {
                // 会话没了，挂起卡也随之失效：不清的话手机会永久留一张点不动的卡
                // （权限挂起中被杀 = 死会话带死卡）。
                DropClaudeRequest(task.Id);
                _claudePreviewRecheck.Remove(task.Id);
                Upsert(task with
                {
                    Lifecycle = AgentLifecycle.Offline,
                    Activity = null,
                    UpdatedAt = now,
                    CompletedAt = task.CompletedAt ?? now,
                    Subagents = SubagentSweep.Finalize(task.Subagents),
                    Capabilities = [],
                });
                changed = true;
                continue;
            }

            if (RefreshClaudeTranscript(task, now)) changed = true;
        }

        return changed;
    }

    /// <summary>
    /// mtime 门控的转录本刷新：文件没变就不重读。更新标题 / Token / 模型 / 排队标记，
    /// **并在没有工具在跑时**把回合中的正文写进第三行（工具在跑就整条忽略，
    /// 免得正文盖掉工具步——这条守卫用户实测踩过）。回合终稿仍由 `Stop` 落。
    /// </summary>
    private bool RefreshClaudeTranscript(TaskSnapshot task, DateTimeOffset now)
    {
        if (!_claudeTranscripts.TryGetValue(task.Id, out var path)) return false;
        if (!File.Exists(path)) return false;

        DateTime modified;
        try { modified = File.GetLastWriteTimeUtc(path); }
        catch { return false; }

        if (_claudeTranscriptMtimes.TryGetValue(task.Id, out var previous) && previous == modified)
            return false;
        _claudeTranscriptMtimes[task.Id] = modified;

        var snapshot = ClaudeTranscriptReader.Read(path);
        var updated = ClaudeEventReducer.ApplyTranscript(task, snapshot);

        // 中间正文（B）：模型在工具之间写的正文也进第三行，但**只在没有工具在跑时**——
        // 工具活动期间整条忽略，免得正文盖掉工具步（opencode 的 reply 守卫同义）。
        // 回合结束的正文不归这里，仍由 Stop 落最终预览。
        if (task.Lifecycle == AgentLifecycle.Running && !_claudeToolActive.Contains(task.Id))
            updated = ClaudeEventReducer.WithPreview(
                updated, ClaudeEventReducer.FreshReply(snapshot, task.StartedAt));

        if (updated == task) return false;
        Upsert(updated);
        return true;
    }

    /// <summary>
    /// 静默离线兜底（agy 与 opencode 共用同一口径，2026-10-11 统一）。
    ///
    /// 为什么需要：两家的"会话结束"信号都不牢靠——agy 只在自己的 `Stop` 里收场（进程被强杀就没有），
    /// opencode 只有 `plugin.dispose` / `session.deleted`（插件没跑或进程被强杀同样没有）。
    /// 没有这条兜底，那条 running 行会**永远**留着：容量裁剪只挑终态任务（`Normalize`），
    /// 非终态连裁都裁不掉。判据与实测一致：10 分钟无更新 **且** 活动位为 null/thinking
    /// （正在跑工具的长任务不算死）。
    /// </summary>
    private bool CheckSilentOffline(DateTimeOffset now)
    {
        var changed = false;
        for (var i = 0; i < _tasks.Count; i++)
        {
            var task = _tasks[i];
            if (task.Source is not (AgentSource.Antigravity or AgentSource.OpenCode)) continue;
            if (task.IsTerminal || task.Lifecycle == AgentLifecycle.Offline) continue;
            if (now - task.UpdatedAt <= TimeSpan.FromMinutes(10)) continue;
            if (task.Activity is not (null or AgentActivity.Thinking)) continue;
            _tasks[i] = task with
            {
                Lifecycle = AgentLifecycle.Offline,
                Activity = null,
                UpdatedAt = now,
                CompletedAt = task.CompletedAt ?? now,
                Subagents = SubagentSweep.Finalize(task.Subagents),
            };
            changed = true;
        }

        return changed;
    }

    // 子代理静默收敛的两级阈值：父任务已终态是强旁证（回合都结束了它还没回来），10 分钟就收；
    // 父还在跑时它可能正被等着，放宽到 30 分钟。
    private static readonly TimeSpan SubagentSilenceAfterTerminal = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SubagentSilenceWhileRunning = TimeSpan.FromMinutes(30);

    /// <summary>
    /// 子代理行的静默收敛（三家通用）。**为什么需要**：三条来源都有"子代理死了却不发终态"的路径——
    /// Claude 用 TaskStop 之外的方式掐、opencode 的子会话被删而插件没来得及跑、agy 被掐时**彻底静默**
    /// （它的会话库里连"被取消"这个状态都没有，实测只有 IDLE/空串两种）。没有这条兜底，卡片会一直转圈。
    ///
    /// **判据刻意保守**：① 只收"没在跑工具"的子代理（activity 为 null / thinking / delegating）——
    /// 正在执行工具的子代理本来就可能长时间没有新事件（一个 15 分钟的构建），拿它判死会误伤，
    /// 这条守卫与任务级离线判定的 `Activity is null or Thinking` 同一思路；
    /// ② 父任务已终态 10 分钟 / 父仍在跑 30 分钟（见上面两个阈值）。
    /// 万一真的误伤，各来源的终态事件随后到达时会把行改回正确状态（子代理的 Stopped/Done 分支不设终态守卫）。
    /// </summary>
    private bool CheckSubagentSilence(DateTimeOffset now)
    {
        var changed = false;
        for (var i = 0; i < _tasks.Count; i++)
        {
            var task = _tasks[i];
            if (!task.Subagents.Any(s => IsSilent(s, task))) continue;
            _tasks[i] = task with
            {
                Subagents = task.Subagents.Select(s => IsSilent(s, task)
                    ? s with
                    {
                        Lifecycle = AgentLifecycle.Interrupted,
                        Activity = null,
                        LatestStep = "✗ 失去联系",
                        UpdatedAt = now,
                    }
                    : s).ToList(),
                UpdatedAt = now,
            };
            changed = true;
        }
        return changed;

        bool IsSilent(SubagentSnapshot subagent, TaskSnapshot parent) =>
            !subagent.IsTerminal &&
            subagent.Activity is null or AgentActivity.Thinking or AgentActivity.Delegating &&
            now - subagent.UpdatedAt > (parent.IsTerminal ? SubagentSilenceAfterTerminal : SubagentSilenceWhileRunning);
    }

    /// <summary>该任务此刻是否还挂着待处理请求（权限/作答）。桥用它决定要不要释放挂住的 hook。</summary>
    public bool HasPendingRequest(string taskID) => _requests.ContainsKey(taskID);

    /// <summary>
    /// Stop 终稿的竞速补救（照搬 agy 的 <c>_agyPreviewRecheck</c>）：转录本与 payload 两路同时落空时，
    /// 在 1 分钟窗口内重读转录本，拿到正文就补写第三行。
    /// </summary>
    private bool CheckClaudePreviewRecheck(DateTimeOffset now)
    {
        if (_claudePreviewRecheck.Count == 0) return false;
        var changed = false;
        foreach (var (sessionID, entry) in _claudePreviewRecheck.ToList())
        {
            var task = _tasks.FirstOrDefault(candidate => candidate.Id == sessionID);
            if (task is null || now > entry.Deadline)
            {
                _claudePreviewRecheck.Remove(sessionID);
                continue;
            }

            var reply = ClaudeEventReducer.FreshReply(ClaudeTranscriptReader.Read(entry.Path), task.StartedAt);
            if (reply is null) continue;
            Upsert(ClaudeEventReducer.WithPreview(task, reply));
            _claudePreviewRecheck.Remove(sessionID);
            changed = true;
        }
        return changed;
    }

    public ControlAckPayload Perform(Guid requestID, ControlPayload control, ICodexPermissionResolver resolver)
    {
        var task = _tasks.FirstOrDefault(candidate => candidate.Id == control.TaskID);
        if (task is null) return new(requestID, ControlResult.Stale, "任务已结束或不存在");

        switch (control.Action)
        {
            case ControlAction.Answer:
                {
                    // 手机作答：把文本当作 PermissionRequest 的 updatedInput 回给 Claude
                    // （提问工具会把入参里的答案原样当成工具结果，见 notes §八）。
                    if (!task.Capabilities.Contains(TaskCapability.Answer) ||
                        !_requests.TryGetValue(task.Id, out var question) ||
                        question.Kind != PendingRequestKind.Question)
                        return new(requestID, ControlResult.Unsupported, "当前任务不能处理该作答");
                    if (string.IsNullOrWhiteSpace(control.Value))
                        return new(requestID, ControlResult.Unsupported, "作答内容为空");
                    if (!_claudeQuestionInputs.TryGetValue(task.Id, out var rawToolInput))
                        return new(requestID, ControlResult.Rejected, "Hook 作答通道已失效");

                    var questions = RawQuestions(rawToolInput);
                    var progress = _claudeQuestionProgress.TryGetValue(task.Id, out var current) && current.Index < questions.Count
                        ? current
                        : new ClaudeQuestionProgress(0, new Dictionary<string, string>(StringComparer.Ordinal));

                    // 单题：维持"点选项→answers / 打字→response"的老行为（response 的语义是
                    // "用户手打的自由文本"，别把它降级成 answers 里的一项）。
                    if (questions.Count <= 1)
                    {
                        if (!resolver.ResolveAnswer(task.Id, rawToolInput, control.Value!))
                            return new(requestID, ControlResult.Rejected, "Hook 作答通道已失效");
                    }
                    else
                    {
                        // 多题：先记账，攒齐了再一次性回填（工具层的 answers 本来就是"问题全文 → 答案"的 map）。
                        // 回传的是按钮上的显示文本（`label（注解）`），记进答案前要还原成纯 label。
                        var answers = new Dictionary<string, string>(progress.Answers, StringComparer.Ordinal)
                        {
                            [questions[progress.Index].Text] =
                                ClaudeHookOutput.NormalizeOptionLabel(control.Value!, questions[progress.Index].Labels),
                        };
                        if (progress.Index + 1 < questions.Count)
                        {
                            // 还有下一题：推进卡片并留在等待态——**不写回 hook**，等攒齐。
                            _claudeQuestionProgress[task.Id] = progress with { Index = progress.Index + 1, Answers = answers };
                            PresentClaudeQuestion(task.Id);
                            Upsert(_tasks.First(candidate => candidate.Id == task.Id) with
                            {
                                Lifecycle = AgentLifecycle.WaitingAnswer,
                                Activity = AgentActivity.Thinking,
                                CompletedAt = null,
                                UpdatedAt = DateTimeOffset.UtcNow,
                            });
                            Changed();
                            return new(requestID, ControlResult.Accepted, null);
                        }
                        if (!resolver.ResolveAnswers(task.Id, rawToolInput, answers))
                            return new(requestID, ControlResult.Rejected, "Hook 作答通道已失效");
                    }

                    DropClaudeRequest(task.Id);
                    var answeredAt = DateTimeOffset.UtcNow;
                    Upsert(task with
                    {
                        Lifecycle = AgentLifecycle.Running,
                        Activity = AgentActivity.Thinking,
                        UpdatedAt = answeredAt,
                        CompletedAt = null,
                        Capabilities = [],
                    });
                    Changed();
                    return new(requestID, ControlResult.Accepted, null);
                }
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
            task.Lifecycle == AgentLifecycle.Offline && task.UserPrompt is null && task.Subagents.Count == 0 && now - task.UpdatedAt >= TimeSpan.FromMinutes(1) ||
            task.IsTerminal && now - (task.CompletedAt ?? task.UpdatedAt) > TimeSpan.FromMinutes(30));
        for (var index = 0; index < _tasks.Count; index++)
        {
            var task = _tasks[index];
            var subs = task.Subagents;
            // 仅对 Antigravity 来源执行终态级联，杜绝跨来源误杀 Claude / OpenCode 正常运行中的子代理。
            // **且只在父任务"中断"时级联**（2026-10-11 修正）：agy 的子代理是异步的，父回合成功结束后
            // 子代理可能仍在跑（随后回报并 resume 父任务）——那时把子代理扫成 succeeded 就是
            // "父任务一完成，未跑完的子代理卡片被收起"。成功终态下子代理各自用自己的 Stop 收敛，
            // 真的失联则由 CheckAgyOffline 的静默收敛兜底。
            if (task.Source == AgentSource.Antigravity && task.Lifecycle == AgentLifecycle.Interrupted && subs.Any(s => !s.IsTerminal))
            {
                subs = subs.Select(s => s.IsTerminal ? s : s with
                {
                    Lifecycle = AgentLifecycle.Interrupted,
                    Activity = null,
                    UpdatedAt = now,
                }).ToList();
            }

            _tasks[index] = task with
            {
                Subagents = subs.Where(value => !value.IsTerminal || now - value.UpdatedAt < TimeSpan.FromSeconds(4)).ToList(),
                Activity = task.Lifecycle == AgentLifecycle.Running
                    ? subs.Any(value => !value.IsTerminal)
                        ? AgentActivity.Delegating
                        : (task.Activity == AgentActivity.Delegating ? AgentActivity.Thinking : task.Activity)
                    : task.Activity,
            };
        }
        foreach (var key in _requests.Keys.Where(key => _tasks.All(task => task.Id != key)).ToList())
            _requests.Remove(key);
        _openCodeToolActive.RemoveWhere(key => _tasks.All(task => task.Id != key));

        // 容量裁剪必须跑在下面那段清理**之前**——否则被容量裁掉的任务，
        // 它的会话状态还是漏的（按"任务还在不在"清理，得先把该裁的裁掉）。
        while (_tasks.Count > _capacity)
        {
            var removable = _tasks.Where(task => task.IsTerminal).MinBy(task => task.UpdatedAt);
            if (removable is null) break;
            _tasks.Remove(removable);
        }

        // Claude 侧按会话索引的伴随状态同样要清：任务被裁掉之后它们没有读者了。
        // opencode 的集合一直有这一步，Claude 的这些漏了——表现为长跑微泄漏，
        // 以及**被裁任务的挂起卡仍留在 _requests 里**（projection 会把它当幽灵卡片发出去）。
        var live = _tasks.Select(task => task.Id).ToHashSet(StringComparer.Ordinal);
        _claudeToolActive.RemoveWhere(id => !live.Contains(id));
        _claudeRegistrySeen.RemoveWhere(id => !live.Contains(id));
        PruneSessionMap(_openCodeDirectories, live);
        PruneSessionMap(_claudeQueuedText, live);
        PruneSessionMap(_claudeTranscripts, live);
        PruneSessionMap(_claudeTranscriptMtimes, live);
        PruneSessionMap(_claudeQuestionInputs, live);
        PruneSessionMap(_claudeQuestionProgress, live);
        PruneSessionMap(_claudeApprovalSource, live);
        PruneSessionMap(_claudePreviewRecheck, live);
        PruneSessionMap(_requests, live);
        PruneSessionMap(_waitingSince, live);

        static void PruneSessionMap<T>(Dictionary<string, T> map, HashSet<string> live)
        {
            foreach (var key in map.Keys.Where(key => !live.Contains(key)).ToList()) map.Remove(key);
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
        var existing = index >= 0 ? _tasks[index] : null;

        var wasWaiting = existing is not null &&
            (existing.Lifecycle is AgentLifecycle.WaitingApproval or AgentLifecycle.WaitingAnswer);
        var isNowWaiting = task.Lifecycle is AgentLifecycle.WaitingApproval or AgentLifecycle.WaitingAnswer;

        if (isNowWaiting)
        {
            // 首次进入等待态：记录挂起点
            if (!_waitingSince.ContainsKey(task.Id))
                _waitingSince[task.Id] = task.UpdatedAt;
        }
        else if (wasWaiting && _waitingSince.Remove(task.Id, out var waitStart))
        {
            // 退出等待态（人类已作答放行）：计算人类等待时长，时钟顺延补偿
            var pauseDuration = task.UpdatedAt - waitStart;
            if (pauseDuration > TimeSpan.Zero)
            {
                task = task with
                {
                    StartedAt = task.StartedAt + pauseDuration
                };
            }
        }

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
        "bash" or "shell" or "exec" or "exec_command" or "terminal_interaction" or "run_command" => "Bash",
        "powershell" => "PowerShell",
        "apply_patch" or "patch" => "Patch",
        "edit" or "replace_file_content" or "multiedit" => "Edit",
        "write" or "write_to_file" => "Write",
        "notebookedit" => "NotebookEdit",
        "read" or "read_file" or "view" or "view_file" or "read_url_content" or "read_resource" or "list_resources" => "Read",
        "glob" => "Glob",
        "grep" => "Grep",
        "task" or "invoke_subagent" or "manage_subagents" or "manage_task"
            or "agent" or "taskcreate" or "taskupdate" or "tasklist" or "taskget" or "taskstop" or "taskoutput" => "Task",
        "webfetch" => "WebFetch",
        "websearch" or "search_web" => "WebSearch",
        "todowrite" or "todoread" or "todo" => "Todo",
        "skill" => "Skill",
        "lsp" => "LSP",
        "question" or "ask_question" or "askuserquestion" => "Question",
        "monitor" => "Monitor",
        "workflow" => "Workflow",
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

/// <summary>
/// 工具步里的**路径展示**（三家共用，2026-10-11 起）。
///
/// 规则：**工作区根下的写工作区相对路径；用户主目录下的写 <c>~/…</c>；其它写全路径**。
/// 分隔符统一为正斜杠（与 agy 既有行为一致，手机上也不会被反斜杠的转义搞乱）。
///
/// 只对**路径型字段**调用（<c>file_path</c>/<c>path</c>/<c>notebook_path</c>/<c>AbsolutePath</c>/<c>TargetFile</c>/<c>filePath</c>…）：
/// 命令行文本、URL、pattern 一律不动——把一条 shell 命令里的路径改写掉只会变成误导。
/// </summary>
public static class PathDisplay
{
    /// <summary>路径型入参键（命中才做缩写；命令/URL/pattern 一律原样）。</summary>
    public static bool IsPathKey(string key) => key is "file_path" or "path" or "notebook_path" or "filePath";

    /// <summary>用户主目录的注入点（测试用；null = 真实主目录）。</summary>
    public static string? HomeRootOverride { get; set; }

    public static string? Shorten(string? path, string? workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var value = path.Replace('\\', '/').Trim();
        if (value.Length == 0) return null;

        if (Root(workspaceRoot) is { } root && UnderRoot(value, root))
        {
            var relative = value[root.Length..].TrimStart('/');
            // 路径正好等于工作区根：相对形式是空的，退化成根目录名（否则步骤会变成光秃秃的 "Read"）
            return relative.Length > 0 ? relative : Leaf(root);
        }

        var home = Root(HomeRootOverride) ?? Root(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (home is not null && UnderRoot(value, home))
        {
            var relative = value[home.Length..].TrimStart('/');
            return relative.Length > 0 ? "~/" + relative : "~";
        }

        return value;
    }

    /// <summary>把根规范化成"正斜杠、无尾斜杠"；空值返回 null。</summary>
    private static string? Root(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Replace('\\', '/').Trim().TrimEnd('/');
        return normalized.Length == 0 ? null : normalized;
    }

    /// <summary>按**目录边界**判定在不在根下：F:/Git 不该把 F:/GitHub 吃掉。</summary>
    private static bool UnderRoot(string path, string root) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);

    private static string Leaf(string normalizedPath) =>
        normalizedPath.Split('/').LastOrDefault(part => part.Length > 0) ?? normalizedPath;

    /// <summary>目录的叶子名（opencode 建行时拿会话目录兜项目名用）。</summary>
    public static string? LeafOf(string? path)
    {
        if (Root(path) is not { } normalized) return null;
        return normalized.Split('/').LastOrDefault(part => part.Length > 0);
    }
}

/// <summary>测试命令判定：按独立词匹配 test/pytest/jest/vitest（词边界强约束，
/// latest 等子串不再误报；gradle 仅在带 test 任务时才命中，gradlew build 不算测试）。</summary>
internal static class ShellTestRun
{
    private static readonly Regex Pattern = new(
        @"\b(?:test|pytest|jest|vitest)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool Matches(string? command) =>
        !string.IsNullOrWhiteSpace(command) && Pattern.IsMatch(command);
}

/// <summary>把未终态子代理扫为 interrupted（进程退出/会话删除/静默离线时不再有子会话事件）。</summary>
internal static class SubagentSweep
{
    public static List<SubagentSnapshot> Finalize(List<SubagentSnapshot> subagents) =>
        subagents.Select(s => s.IsTerminal ? s : s with
        {
            Lifecycle = AgentLifecycle.Interrupted,
            Activity = null,
        }).ToList();
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
    public static TaskSnapshot Reduce(
        ClaudeHookPayload hook,
        TaskSnapshot? existing,
        ClaudeTranscriptSnapshot? transcript,
        DateTimeOffset now,
        bool queuedDelivery = false)
    {
        var task = existing ?? new(
            hook.SessionID,
            AgentSource.ClaudeCode,
            hook.ProjectName,
            hook.ProjectName,
            null, null, null, [],
            // 会话建立就是 idle（对齐 opencode 的明文论证：不置 starting，避免空会话误报"正在启动"）。
            AgentLifecycle.Idle, null,
            now, now, null,
            false, false, false, []);
        // 项目名**跟随 payload 的 cwd**（每个事件都取）：Claude 的 cwd 会跟着 Bash 的 `cd` 走，
        // 于是这一行反映的是"这个会话此刻在哪个目录干活"。用户实测确认这是期望行为
        // （曾按"另两家都用稳定目录"的理由冻结成首次取值，被用户裁定撤销——别再用直觉改语义）。
        task = ApplyTranscript(
            task with { ProjectName = hook.ProjectName, UpdatedAt = now },
            hook, transcript);

        switch (hook.HookEventName)
        {
            case "SessionStart":
                // 只会走到这里的是**已存在的行**（resume / clear / compact / fork 把会话复位成 idle）；
                // 全新会话的那条 SessionStart 在 Accept 里就被挡掉了——它在转录本尚未创建时到达，
                // 那时候建行只能得到一个顶着 cwd 目录名的无名幽灵（详见 Accept 里的注释）。
                //
                // **终态保护**（2026-10-11 修，对齐 opencode 的 session.created 写法）：已完成的任务
                // 被 resume/clear 打回 idle 会让它掉优先级（300→100）、丢掉未读高亮——用户实测报出
                // "app 重连后原本 succeeded 的任务变成 idle"。会话重新打开不代表上一轮没完成；
                // 用户随后真发提示词时 UserPrompt 会自然把它推回 running。
                if (task.IsTerminal) return task;
                return task with
                {
                    Lifecycle = AgentLifecycle.Idle,
                    Activity = null,
                    StartedAt = now,
                    CompletedAt = null,
                    Capabilities = [],
                    PromptQueued = false,
                };
            case "UserPromptSubmit":
                return UserPrompt(task, hook.Prompt, now, queuedDelivery);
            case "PreToolUse" when IsTaskStopTool(hook.ToolName):
                // 掐子代理（TaskStop）这条调用**不动父任务**：入参只有 `{"task_id":"…"}`，白名单取不到摘要，
                // 走通用分支会让第三行退化成光秃秃的 "Task" 并且粘住（PostToolUse 不清 step）。
                // 真正的动作是"把被掐的子代理行收敛掉"，那件事在 Accept 里做（reducer 拿不到 _tasks）。
                return task;
            case "PreToolUse" when IsQuestionTool(hook.ToolName):
                // 模型主动提问：这是"在等用户作答"的精确信号，不必靠 Notification 类型去猜。
                if (!CanAdvance(task)) return task;
                return task with
                {
                    Lifecycle = AgentLifecycle.WaitingAnswer,
                    Activity = AgentActivity.Thinking,
                    LatestStep = QuestionHeader(hook.ToolInput) ?? task.LatestStep,
                    CompletedAt = null,
                    Capabilities = [],
                };
            case "PreToolUse":
                if (!CanAdvance(task)) return task;
                return task with
                {
                    Lifecycle = AgentLifecycle.Running,
                    Activity = ActivityFor(hook.ToolName, hook.ToolInput),
                    LatestStep = CodexEventReducer.LatestStep(hook.ToolName, Summary(hook.ToolInput, hook.Cwd)) ?? task.LatestStep,
                    CompletedAt = null,
                    Capabilities = [],
                };
            case "PostToolUse":
                // 工具跑完 = 之前的权限等待结束（无论手机放行、本机作答还是被拒后照常返回）。
                if (IsTerminalState(task)) return task;
                return task with
                {
                    Lifecycle = AgentLifecycle.Running,
                    Activity = AgentActivity.Thinking,
                    CompletedAt = null,
                    Capabilities = [],
                };
            case "PostToolUseFailure":
                if (IsTerminalState(task)) return task;
                return task with
                {
                    // is_interrupt 是 Claude 侧唯一及时的中断信号（用户按了 Esc）：
                    // 交到 Stop 时它会因为"当前已是 Interrupted"而保持，不会被翻成"已完成"。
                    Lifecycle = hook.IsInterrupt == true ? AgentLifecycle.Interrupted : AgentLifecycle.Running,
                    Activity = hook.IsInterrupt == true ? null : AgentActivity.Thinking,
                    LatestStep = $"✗ {ErrorSummary(hook) ?? "工具失败"}",
                    CompletedAt = hook.IsInterrupt == true ? now : null,
                    IsUnread = hook.IsInterrupt == true || task.IsUnread,
                    Capabilities = [],
                };
            case "PreCompact":
                // 压缩只是回合中的一件事，不迁移生命周期。旧实现把它当"运行中"写回，
                // 一次自动压缩就能把已完成的任务复活成运行中（G16）。
                return task;
            case "PermissionRequest" when IsQuestionTool(hook.ToolName):
                // AskUserQuestion 是"需要用户交互"的工具：**它同时会发 PermissionRequest**，
                // 而这条权限询问就是提问本身。按普通工具走审批路径会出两个错（实测踩过）：
                // ① 手机上出现"允许/拒绝"按钮——选择题根本没法用它答；
                // ② 把已经进入的"等待回答"压成"等待批准"，QUESTION 卡片被 APPROVAL 卡片顶掉。
                // 这里用 IsTerminal 而不是 CanAdvance：它必须能够**进入/保持** WaitingAnswer。
                if (task.IsTerminal) return task;
                return task with
                {
                    Lifecycle = AgentLifecycle.WaitingAnswer,
                    Activity = AgentActivity.Thinking,
                    LatestStep = QuestionHeader(hook.ToolInput) ?? QuestionText(hook.ToolInput) ?? task.LatestStep,
                    CompletedAt = null,
                    // 权限询问这条路上我们**持有 hook 连接**，所以能作答（回包即答案）；
                    // 只走 PreToolUse 而没有 PermissionRequest 时拿不到连接，那时不给这个能力位。
                    Capabilities = [TaskCapability.Answer],
                };
            case "PermissionRequest":
                if (task.IsTerminal) return task;
                return task with
                {
                    Lifecycle = AgentLifecycle.WaitingApproval,
                    Activity = AgentActivity.Executing,
                    LatestStep = CodexEventReducer.LatestStep(hook.ToolName, Summary(hook.ToolInput, hook.Cwd)) ?? task.LatestStep,
                    CompletedAt = null,
                    Capabilities = [TaskCapability.Approve, TaskCapability.Deny],
                };
            case "PermissionDenied":
                // 厂商语义是 auto 模式分类器拒了"这一次工具调用"（二进制内置文档原文
                // "After auto mode classifier denies a tool call"），回合并未结束：
                // 旧实现映射成 Interrupted，会在手机上刷出大量假的"已中断"（G13）。
                if (task.IsTerminal) return task;
                return task with
                {
                    Lifecycle = AgentLifecycle.Running,
                    Activity = AgentActivity.Thinking,
                    LatestStep = $"✗ 已拒绝 {Summary(hook.ToolInput, hook.Cwd) ?? hook.ToolName}".TrimEnd(),
                    CompletedAt = null,
                    Capabilities = [],
                };
            case "Notification":
                // 只有确实等待用户输入的通知才进入等待回答；其他通知保持现有状态。
                return IsAnswerNotification(hook.NotificationType) && CanAdvance(task)
                    ? task with
                    {
                        Lifecycle = AgentLifecycle.WaitingAnswer,
                        Activity = AgentActivity.Thinking,
                        CompletedAt = null,
                        Capabilities = [],
                    }
                    : task;
            case "Stop":
                return Stop(task, hook, transcript, now);
            case "StopFailure":
                return task with
                {
                    // 回合因 API 错误结束（rate_limit / overloaded / max_output_tokens …）：
                    // 是终态，不是"退回运行中"（旧实现方向反了，G15）。
                    Lifecycle = AgentLifecycle.Interrupted,
                    Activity = null,
                    LatestStep = $"✗ {hook.Error ?? "回合异常结束"}",
                    CompletedAt = now,
                    IsUnread = true,
                    Capabilities = [],
                };
            case "SessionEnd":
                return task with
                {
                    Lifecycle = task.IsTerminal ? task.Lifecycle : AgentLifecycle.Offline,
                    Activity = null,
                    CompletedAt = task.IsTerminal ? task.CompletedAt : (task.CompletedAt ?? now),
                    // 进程退出后不会再有子代理事件，未终态的残留行级联扫为 interrupted。
                    Subagents = SubagentSweep.Finalize(task.Subagents),
                    Capabilities = [],
                    PromptQueued = false,
                };
            default:
                return task with { UpdatedAt = now };
        }
    }

    /// <summary>
    /// 竞争守卫（对齐 opencode 的实测结论与 agy 的条文）：
    /// ① 终态（Succeeded/Interrupted/Offline）之后，任何迟到事件都不得复活任务；
    /// ② 等待态（WaitingApproval/WaitingAnswer）期间，**前置**事件（PreToolUse、通知）不得
    ///    把等待压回 running——否则手机上的等待卡片会被清掉，用户以为没事了。
    /// 工具**完成**事件（PostToolUse/PostToolUseFailure）是例外：它正是"这一等结束了"的信号
    /// （手机放行、本机作答、或工具被拒后照常返回），必须放行，否则任务会卡死在等待态。
    /// </summary>
    private static bool CanAdvance(TaskSnapshot task) =>
        task.Lifecycle is not (AgentLifecycle.Succeeded or AgentLifecycle.Interrupted
            or AgentLifecycle.Offline or AgentLifecycle.WaitingApproval or AgentLifecycle.WaitingAnswer);

    /// <summary>只挡终态：等待态可以、也应该被工具完成事件解开。</summary>
    private static bool IsTerminalState(TaskSnapshot task) =>
        task.Lifecycle is AgentLifecycle.Succeeded or AgentLifecycle.Interrupted or AgentLifecycle.Offline;

    /// <summary>
    /// 回合结束。已经是 Interrupted 的保持 Interrupted（Esc 中断后 Stop 一定会跟着到）。
    /// 第三行落正文预览：转录本优先，payload 的 last_assistant_message 兜底
    /// （后者取的是最后一条助手消息，能在终稿还没落盘时救急）。
    /// </summary>
    private static TaskSnapshot Stop(
        TaskSnapshot task, ClaudeHookPayload hook, ClaudeTranscriptSnapshot? transcript, DateTimeOffset now)
    {
        var interrupted = task.Lifecycle == AgentLifecycle.Interrupted;
        // 转录本那条要过"属于本轮"的闸；过不了就退回 payload 自带的终稿（它一定是本轮的）。
        // **别改成"宽松口径"**：Stop 存在"终稿还没落盘"的竞态，宽松口径会一路往回捞到**更早**的一段
        // 正文并把它钉在第三行（且因为第三行非空，G41 的补读也不会触发）——实测踩过，已回退。
        var preview = FreshReply(transcript, task.StartedAt) ?? Normalize(hook.LastAssistantMessage);
        var next = task with
        {
            Lifecycle = interrupted ? AgentLifecycle.Interrupted : AgentLifecycle.Succeeded,
            Activity = null,
            CompletedAt = now,
            IsUnread = true,
            Capabilities = [],
        };
        return WithPreview(next, preview);
    }

    /// <summary>
    /// 取"属于本轮"的正文预览。
    ///
    /// **实测踩过（用户报"laststep 经常残留上一个 reply"）**：`UserPromptSubmit` 这个 hook
    /// 按设计跑在提示词进入对话**之前**，所以回合刚开始时转录本里还没有新一轮的 user 行——
    /// 倒序扫描拿到的"本回合最后一条正文"其实是**上一回合**的，于是第三行把上一轮的 reply
    /// 又显示了一遍（心跳 3 秒一次，正好落在这个窗口里，所以是"经常"而不是偶发）。
    /// assistant 行自带 timestamp：早于本轮起点的一律丢弃。
    /// 没有时间戳时按老样子放行——宁可显示，也不要因为格式变化让整条链路静默失效。
    /// </summary>
    public static string? FreshReply(ClaudeTranscriptSnapshot? transcript, DateTimeOffset turnStart) =>
        transcript?.LatestReply is { } reply && (transcript.LatestReplyAt is not { } at || at >= turnStart)
            ? reply
            : null;

    /// <summary>
    /// 正文预览落到第三行，并守住"第三行永不回显第二行"（opencode/agy 都有的守卫，G18）。
    /// 两个调用点：`Stop` 落回合终稿，心跳落回合中的中间正文。
    /// </summary>
    public static TaskSnapshot WithPreview(TaskSnapshot task, string? candidate)
    {
        if (candidate is null) return task;
        var preview = ToolStepSanitizer.Sanitize(candidate);
        if (preview is null || ToolStepSanitizer.EchoesPrompt(task, preview)) return task;
        return task with { LatestStep = preview.Length > 120 ? preview[..120] : preview };
    }

    /// <summary>
    /// 转录本 → 标题 / Token / 模型 / 排队。标题只取会话真实标题（用户自设优先，其次 AI 生成），
    /// 缺了才回落 payload 的 session_title；**绝不从 prompt 自造**——与 opencode/agy 同一条规范（G1）。
    /// </summary>
    private static TaskSnapshot ApplyTranscript(
        TaskSnapshot task, ClaudeHookPayload hook, ClaudeTranscriptSnapshot? transcript)
    {
        var result = ApplyTranscript(task, transcript);
        // 即便没有转录本，session_title（用户自设标题）也随 SessionStart / UserPromptSubmit 到达。
        if (!string.IsNullOrWhiteSpace(hook.SessionTitle))
            result = result with { Title = ClaudeTitle(result.ProjectName, hook.SessionTitle!) };
        return result;
    }

    /// <summary>心跳路径（无 hook 载荷）也能复用的同一份套用逻辑。</summary>
    public static TaskSnapshot ApplyTranscript(TaskSnapshot task, ClaudeTranscriptSnapshot? transcript)
    {
        var result = task;

        var title = transcript?.Title;
        if (!string.IsNullOrWhiteSpace(title))
            result = result with { Title = ClaudeTitle(result.ProjectName, title!) };

        if (transcript?.Tokens is not null) result = result with { TokenUsage = transcript.Tokens };
        if (transcript?.Model is not null) result = result with { ModelName = transcript.Model };

        // 排队：转录本里最后一条**非系统**队列操作是 enqueue，且时间晚于本轮起点，
        // 就说明"本轮跑着的时候用户又提交了一条"（opencode 的 queued 语义）。
        if (transcript is not null)
        {
            var queued = transcript.QueuedAt is { } queuedAt &&
                         transcript.QueuedPrompt is { Length: > 0 } pending &&
                         queuedAt > result.StartedAt
                ? pending
                : null;

            if (queued is not null)
            {
                result = result with { PromptQueued = true, UserPrompt = queued };
            }
            else if (result.PromptQueued)
            {
                // 队排空（remove）后必须把 UserPrompt **还原成本回合真实的那条输入**。
                // 不还原的话排队文本会永久冒充用户的提示词：实测踩过——一条中途排队的消息
                // 排空后，第二行一直显示它，本轮的提示词被顶掉且再也回不来。
                result = result with
                {
                    PromptQueued = false,
                    UserPrompt = transcript.LatestPrompt ?? result.UserPrompt,
                };
            }
        }

        return result;
    }

    private static string ClaudeTitle(string projectName, string title)
    {
        var value = Normalize(title) ?? title.Trim();
        if (value.Length == 0 || value == projectName) return projectName;
        var composed = $"{projectName} · {value}";
        return composed.Length > 120 ? composed[..120] : composed;
    }

    /// <summary>
    /// 从 Claude 的任意 tool_input JSON 中提取最具信息量的字段。
    /// **白名单刻意不含 prompt**：`Agent` 一类工具的入参带 prompt，取它会让第三行回显第二行
    /// （opencode 的白名单同样排除 prompt，agy 也"严格排除 Prompt"——三家同一条规范，G18）。
    ///
    /// <paramref name="workspaceRoot"/> 只用于路径型键的缩写；命令/URL/pattern 一律原样。
    /// </summary>
    public static string? Summary(JsonElement? toolInput, string? workspaceRoot = null)
    {
        if (toolInput is null) return null;
        var value = toolInput.Value;
        if (value.ValueKind != JsonValueKind.Object)
            return DisplayValue(value);
        foreach (var key in new[]
                 {
                     "command", "file_path", "path", "pattern", "query", "url", "description",
                     "notebook_path", "subject", "skill", "subagent_type", "agent",
                 })
        {
            if (value.TryGetProperty(key, out var child) && DisplayValue(child) is { Length: > 0 } text)
                return PathDisplay.IsPathKey(key) ? PathDisplay.Shorten(text, workspaceRoot) ?? text : text;
        }
        return null;
    }

    /// <summary>模型主动提问的工具（提问态用工具名判，不用通知类型猜）。</summary>
    public static bool IsQuestionTool(string? toolName) =>
        toolName is not null && toolName.Equals("AskUserQuestion", StringComparison.OrdinalIgnoreCase);

    /// <summary>掐掉后台任务/子代理的工具（实测工具名 <c>TaskStop</c>）。</summary>
    public static bool IsTaskStopTool(string? toolName) =>
        toolName is not null && toolName.Equals("TaskStop", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// TaskStop 的目标 id。实测入参是 <c>{"task_id":"a2bb7464c16c26320"}</c>，与 SubagentStart 的
    /// <c>agent_id</c> **逐字相同**——掐子代理时桥靠这个把对应子代理行收敛掉。
    /// </summary>
    public static string? TaskStopTarget(JsonElement? toolInput) =>
        toolInput is { ValueKind: JsonValueKind.Object } value &&
        value.TryGetProperty("task_id", out var id) && id.ValueKind == JsonValueKind.String
            ? id.GetString()
            : null;

    /// <summary>
    /// 提问卡片正文。一次提问可带 1-4 题（实测 schema），而手机端只渲染**一行** summary，
    /// 所以多题必须全部拼进来并标号——只取第一题会让后面的题在手机上完全看不见
    /// （实测踩过：一次问两题，手机上只显示第一题，用户也就只答了第一题）。
    /// </summary>
    public static string? QuestionText(JsonElement? toolInput)
    {
        var texts = new List<string>();
        foreach (var question in Questions(toolInput))
        {
            var text = Text(question, "question") ?? Text(question, "header");
            if (!string.IsNullOrWhiteSpace(text)) texts.Add(Normalize(text)!);
        }
        if (texts.Count == 0) return null;
        if (texts.Count == 1) return texts[0];
        var joined = string.Join(" / ", texts.Select((text, index) => $"{index + 1}. {text}"));
        return joined.Length > 300 ? joined[..300] : joined;
    }

    /// <summary>第三行用短标签（header）：手机上放得下，全文留在卡片里；多题时补一个题数。</summary>
    public static string? QuestionHeader(JsonElement? toolInput)
    {
        var questions = Questions(toolInput).ToList();
        if (questions.Count == 0) return null;
        var header = Normalize(Text(questions[0], "header") ?? Text(questions[0], "question"));
        if (header is null) return null;
        return questions.Count > 1 ? $"{header}（共 {questions.Count} 题）" : header;
    }

    /// <summary>
    /// 提问选项——**只在"只有一题"时下发**。
    ///
    /// 手机端靠"有没有选项"决定要不要把选项画成可点的按钮；而协议里 options 是一个扁平列表，
    /// 多题的选项混在一起无法归属，画出来点了也不知道答的是哪一题——那把按钮换成文本框更诚实。
    /// （多题时正文里已经带题号，用户打自由文本，走 `response`。）
    /// </summary>
    public static List<string> QuestionOptions(JsonElement? toolInput)
    {
        var questions = Questions(toolInput).ToList();
        if (questions.Count != 1) return [];
        var options = new List<string>();
        if (!questions[0].TryGetProperty("options", out var list) || list.ValueKind != JsonValueKind.Array)
            return options;
        foreach (var option in list.EnumerateArray())
            if (Text(option, "label") is { Length: > 0 } label)
                options.Add(OptionDisplayText(label, Text(option, "description")));
        return options;
    }

    /// <summary>
    /// 选项在手机上的显示文本：`label（注解）`——注解跟着按钮走（用户的明确要求：
    /// "注解写在选项框里"；塞进卡片正文被他判定为错排法并回退过）。
    /// 回传时由 <see cref="ClaudeHookOutput.NormalizeOptionLabel"/> 还原成纯 label。
    /// </summary>
    public static string OptionDisplayText(string label, string? description) =>
        string.IsNullOrWhiteSpace(description) ? label : $"{label}（{Normalize(description)}）";

    /// <summary>工具失败的一行摘要：error 优先，其次 reason，最后 error_details。</summary>
    public static string? ErrorSummary(ClaudeHookPayload hook) =>
        Normalize(FirstLine(hook.Error)) ?? Normalize(FirstLine(hook.Reason)) ?? Normalize(hook.ErrorDetails);

    private static IEnumerable<JsonElement> Questions(JsonElement? toolInput)
    {
        if (toolInput is not { } value || value.ValueKind != JsonValueKind.Object) yield break;
        if (!value.TryGetProperty("questions", out var questions) || questions.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var question in questions.EnumerateArray())
            if (question.ValueKind == JsonValueKind.Object) yield return question;
    }

    private static string? FirstLine(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        var index = value.IndexOfAny(['\r', '\n']);
        return index < 0 ? value : value[..index];
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>会让 Claude 暂停并等待用户输入的通知类型（本机 2.1.236 实测存在的四种）。</summary>
    public static bool IsAnswerNotification(string? notificationType) =>
        notificationType is "idle_prompt" or "elicitation_dialog" or "elicitation_url_dialog" or "agent_needs_input";

    /// <summary>
    /// 工具 → Activity。与 opencode/agy 的判据统一为"工具名 + 入参文本"：
    /// ① 命令类工具看**命令文本**（`Bash npm test` 必须是 testing——旧实现只看工具名，
    ///    于是所有测试命令都落 executing，G8）；
    /// ② 先判 todo/task 家族再判 edit/write——子串匹配会让 TodoWrite 命中 write（G19）。
    /// </summary>
    public static AgentActivity ActivityFor(string? toolName, JsonElement? toolInput)
    {
        var name = toolName?.Trim() ?? "";
        var lower = name.ToLowerInvariant();

        // 1. 命令类：判据是命令文本，不是工具名。
        if (lower is "bash" or "powershell" or "shell" or "run_command" or "exec")
            return ShellTestRun.Matches(Command(toolInput)) ? AgentActivity.Testing : AgentActivity.Executing;

        // 2. 清单/提问类先于 write/edit 判定（杜绝 TodoWrite → editing）。
        if (lower is "todowrite" or "todoread" or "todo" or "askuserquestion" or "question"
            or "enterplanmode" or "exitplanmode" or "taskcreate" or "taskupdate" or "tasklist" or "taskget")
            return AgentActivity.Thinking;

        // 3. 派生/协同。Task / Agent 是 Claude 派子代理的两个名字。
        if (lower is "task" or "agent" or "sendmessage" or "invoke_subagent" or "workflow")
            return AgentActivity.Delegating;

        // 4. 只读。
        if (lower is "read" or "notebookread" or "view" or "view_file" or "read_file" or "list"
            or "listmcpresourcestool" or "readmcpresourcetool" or "readmcpresourcedirtool" or "skill"
            or "listagents" or "ls")
            return AgentActivity.Reading;

        // 5. 检索（Glob 以前落 executing，因为判据只认 grep）。
        if (lower is "glob" or "grep" or "search" or "find")
            return AgentActivity.Searching;

        // 6. 写入。
        if (lower is "edit" or "write" or "multiedit" or "notebookedit" or "apply_patch" or "patch"
            or "replace_file_content" or "write_to_file")
            return AgentActivity.Editing;

        // 7. 联网。
        if (lower is "webfetch" or "websearch" or "search_web" or "read_url_content")
            return AgentActivity.Browsing;

        // 8. MCP / 未知工具：回落子串启发式（先判 todo，避免重蹈 write 误命中）。
        if (lower.Contains("todo") || lower.Contains("task")) return AgentActivity.Thinking;
        return CodexEventReducer.ActivityFor(toolName);
    }

    /// <summary>命令类工具的入参文本（tool_input.command）。</summary>
    public static string? Command(JsonElement? toolInput)
    {
        if (toolInput is not { } value || value.ValueKind != JsonValueKind.Object) return null;
        return Text(value, "command");
    }

    private static TaskSnapshot UserPrompt(TaskSnapshot task, string? prompt, DateTimeOffset now, bool queuedDelivery = false)
    {
        var normalized = Normalize(prompt);
        // 本轮计时：一轮的第一个 prompt 开始计时；轮内插入的 prompt 不重置；新一轮清第三行。
        // 与 opencode/agy 同一条规则（旧实现把 StartedAt 停在会话创建时刻，显示的是"会话总长"，G5/G6）。
        //
        // **排队投递不算新回合**（用户裁定，2026-10-11）：计时只在"一轮完了之后又重新开始"时才重置，
        // 而排队消息是用户**在上一轮没完时**说的那半句 —— 属于同一个 burst，不该归零。它到达时
        // 上一轮往往刚好 Stop（状态已是 succeeded），单看生命周期必然误判成新回合 ✗，
        // 所以由 Accept 事先对表（`queuedDelivery`）把它摁回同一轮。
        var startsTurn = !queuedDelivery && task.Lifecycle is AgentLifecycle.Idle or AgentLifecycle.Offline
            or AgentLifecycle.Succeeded or AgentLifecycle.Interrupted;
        return task with
        {
            Lifecycle = AgentLifecycle.Running,
            Activity = AgentActivity.Thinking,
            UserPrompt = normalized ?? task.UserPrompt,
            StartedAt = startsTurn ? now : task.StartedAt,
            LatestStep = startsTurn ? null : task.LatestStep,
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

    /// <summary>
    /// 提示词归一化（排队文本对表用）。**必须与 `UserPrompt` 里走的同一套**，否则
    /// "排队那句"和"投递那句"会因空白差异对不上表，守卫静默失效。
    /// </summary>
    public static string? NormalizePrompt(string? value) => Normalize(value);

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

    /// <summary>工具 part 的状态（非工具 part 或无状态返回 null）：乱序残帧守卫判据。</summary>
    public static string? ToolPartStatus(JsonElement? properties)
    {
        var part = properties is { } value ? Child(value, "part") : null;
        if (part is not { } item || Text(item, "tool") is null) return null;
        var state = Child(item, "state");
        return state is { } current ? Text(current, "status") : null;
    }

    public static TaskSnapshot Reduce(OpenCodeHookPayload hook, TaskSnapshot? existing, DateTimeOffset now, string? workspaceRoot = null)
    {
        var sessionID = SessionID(hook) ?? existing?.Id ?? "opencode";
        // 行现在由 chat.message 建（session.created 不再建行），而 chat.message 不带目录 ——
        // 回退用会话记住的目录名，否则新行标题会变成字面的 "opencode"。
        var project = ProjectName(hook.Properties) ?? PathDisplay.LeafOf(workspaceRoot) ?? existing?.ProjectName ?? "opencode";
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
                    CompletedAt = task.IsTerminal ? task.CompletedAt : (task.CompletedAt ?? now),
                    // 退出/删除时未终态的子代理不会再有子会话事件（随进程一起消失），
                    // 级联扫为 interrupted，防永久 Running 行残留并落盘（O3）。
                    Subagents = SubagentSweep.Finalize(task.Subagents),
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
                return PartUpdated(task, hook.Properties, workspaceRoot);
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
                    CompletedAt = task.IsTerminal ? task.CompletedAt : (task.CompletedAt ?? now),
                    Subagents = SubagentSweep.Finalize(task.Subagents),
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
                // 未知状态类型一律忽略，避免未来 opencode 新增类型被误判为回合结束（O4）。
                return task;
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

    private static TaskSnapshot PartUpdated(TaskSnapshot task, JsonElement? properties, string? workspaceRoot)
    {
        if (properties is not { } value) return task;
        var part = Child(value, "part");
        if (part is not { } item) return task;
        var state = Child(item, "state");
        var status = state is { } current ? Text(current, "status") : null;
        var tool = Text(item, "tool");
        var step = LatestStep(tool, state, workspaceRoot);

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
        ToolStepSanitizer.EchoesPrompt(task, candidate);

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
                // 命令文本由插件单独带过来（子代理事件没有完整 state）：不带就判不出"测试"，
                // 同一个 `npm test` 会让父任务行显蓝、子代理行显橙。
                subagent = subagent with
                {
                    Lifecycle = AgentLifecycle.Running,
                    Activity = ActivityFor(tool, ShellTestRun.Matches(Text(value, "command"))),
                };
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

    /// <summary>会话目录（session.created/updated 的 info.directory）；没有返回 null。</summary>
    public static string? Directory(JsonElement? properties)
    {
        var info = properties is { } value ? Child(value, "info") : null;
        return info is { } session ? Text(session, "directory") : null;
    }

    private static string? ProjectName(JsonElement? properties)
    {
        var directory = Directory(properties);
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

    private static AgentActivity ActivityFor(string? tool, JsonElement? state) =>
        ActivityFor(tool, IsTestCommand(state));

    /// <summary>
    /// 判据与父任务行完全相同，只有"是不是测试命令"这一位由调用方给出——
    /// 子代理事件里没有完整的 state（插件只带 command 字段），不能走 state 版本。
    /// </summary>
    private static AgentActivity ActivityFor(string? tool, bool isTest)
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
            return isTest ? AgentActivity.Testing : AgentActivity.Executing;
        if (name.Contains("test")) return AgentActivity.Testing;
        return AgentActivity.Executing;
    }

    private static bool IsTestCommand(JsonElement? state)
    {
        var input = state is { } value ? Child(value, "input") : null;
        var command = input is { } arguments ? Text(arguments, "command") : null;
        return ShellTestRun.Matches(command);
    }

    private static string? LatestStep(string? tool, JsonElement? state, string? workspaceRoot = null)
    {
        var detail = state is { } value ? Text(value, "title") ?? SummarizeInput(value, workspaceRoot) : null;
        return ToolStep.Compose(tool, detail);
    }

    /// <summary>opencode 自己的 state.title 优先；没有才用入参，且路径型入参按三档缩写。</summary>
    private static string? SummarizeInput(JsonElement state, string? workspaceRoot)
    {
        var input = Child(state, "input");
        if (input is not { } value) return null;
        foreach (var key in InputKeys)
            if (Text(value, key) is { } text)
                return PathDisplay.IsPathKey(key)
                    ? PathDisplay.Shorten(text, workspaceRoot) ?? text
                    : text;
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

    /// <summary>
    /// 第三行永不回显第二行：候选文本与当前 UserPrompt 相同或整段包含它（≥4 字）时丢弃。
    /// opencode 与 agy 各有一份同逻辑实现，这里收成唯一一份，三家共用（G18）。
    /// 残余风险一样：模型改写过的提示词片段拦不住。
    /// </summary>
    public static bool EchoesPrompt(TaskSnapshot task, string candidate) =>
        task.UserPrompt is { Length: >= 4 } prompt && candidate.Contains(prompt, StringComparison.OrdinalIgnoreCase);
}
