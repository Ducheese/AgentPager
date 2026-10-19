using System.Text.Json;
using System.Text.Json.Nodes;
using AgentPager.Core;

var failures = new List<string>();
void Check(bool condition, string name)
{
    if (condition) Console.WriteLine($"PASS  {name}");
    else { failures.Add(name); Console.WriteLine($"FAIL  {name}"); }
}

static OpenCodeHookPayload H(string evt, object props) =>
    new(evt, JsonSerializer.SerializeToElement(props, WireJson.Options));

static AgyHookPayload A(string evt, object props)
{
    var node = JsonSerializer.SerializeToNode(props)!.AsObject();
    node["event"] = evt;
    return JsonSerializer.Deserialize<AgyHookPayload>(node.ToJsonString(), WireJson.Options)!;
}

var catalog = new TaskCatalog();
var t0 = DateTimeOffset.UtcNow;
long created = t0.ToUnixTimeMilliseconds();

TaskSnapshot Task() => catalog.Projection().Tasks.First(t => t.Id == "ses_1");

// 1. session.created **不建行**（2026-10-11 修 opencode 启动幽灵）：会话一建就发这条，
//    而那一刻还没有标题——建出来的行只能顶着项目目录名（实测 `opencode run` 启动即冒幽灵）。
//    这条事件现在只用来登记会话目录。
catalog.Accept(H("session.created", new
{
    sessionID = "ses_1",
    info = new { id = "ses_1", directory = @"F:\Git\demo", title = "New session - 2026-10-06T00:00:00.000Z", time = new { created, updated = created } },
}));
Check(catalog.Projection().Tasks.Count == 0, "opencode: session.created alone creates no task (no boot ghost)");

// 2. chat.message 才建行；项目名回退到会话记住的目录（chat.message 不带目录）
Thread.Sleep(15);
catalog.Accept(H("chat.message", new
{
    sessionID = "ses_1",
    model = new { providerID = "ccproxy", modelID = "deepseek/deepseek-v4.1-flash" },
    text = "修复登录问题\n并补充测试",
}));
Check(Task().Source == AgentSource.OpenCode, "source=opencode");
Check(Task().ProjectName == "demo", "project name falls back to the remembered session directory");
Check(Task().Lifecycle == AgentLifecycle.Running, "chat.message -> running");
Check(Task().Activity == AgentActivity.Thinking, "chat.message -> thinking");
Check(Task().UserPrompt == "修复登录问题 并补充测试", "prompt normalized");
Check(Task().ModelName == "deepseek-v4.1-flash", $"model label parsed from modelID (got '{Task().ModelName}')");
Check(!Task().PromptQueued, "fresh prompt is not queued");
Check(Task().LatestStep is null, $"no thinking placeholder; step stays sticky (got '{Task().LatestStep}')");
Check(Task().Title == "demo", $"no prompt-derived title, stays project name (got '{Task().Title}')");
Check(Task().StartedAt > DateTimeOffset.FromUnixTimeMilliseconds(created), "first prompt starts the turn timer");
var firstTurnStart = Task().StartedAt;
Thread.Sleep(15);
catalog.Accept(H("chat.message", new { sessionID = "ses_1", text = "顺手看看最新的日志输出" }));
Check(Task().StartedAt == firstTurnStart, "steering prompt keeps the turn timer");
Check(Task().ModelName == "deepseek-v4.1-flash", "steering prompt keeps model label");
Check(Task().UserPrompt == "顺手看看最新的日志输出", "steering prompt updates userPrompt promptly");

// 2b. 用户 prompt 永不进入 laststep（输入摘要 / 工具标题两条通道）
var stepBeforeLeak = Task().LatestStep;
catalog.Accept(H("message.part.updated", new
{
    sessionID = "ses_1",
    part = new { id = "prt_leak1", callID = "call_leak1", tool = "bash", state = new { status = "running", input = new { command = "echo 顺手看看最新的日志输出" } } },
}));
Check(Task().LatestStep == stepBeforeLeak, $"prompt echo rejected from input summary (got '{Task().LatestStep}')");
catalog.Accept(H("message.part.updated", new
{
    sessionID = "ses_1",
    part = new { id = "prt_leak2", callID = "call_leak2", tool = "bash", state = new { status = "running", title = "顺手看看最新的日志输出", input = new { command = "echo x" } } },
}));
Check(Task().LatestStep == stepBeforeLeak, "prompt echo rejected from tool title");

// 3. tool running (bash npm test)
catalog.Accept(H("message.part.updated", new
{
    sessionID = "ses_1",
    part = new { id = "prt_1", callID = "call_1", tool = "bash", state = new { status = "running", input = new { command = "npm test" } } },
}));
Check(Task().Lifecycle == AgentLifecycle.Running, "tool running -> running");
Check(Task().Activity == AgentActivity.Testing, "bash npm test -> testing");
Check(Task().LatestStep == "Bash npm test", $"latest step (got '{Task().LatestStep}')");

// 3a. 测试判定按独立词（新任务隔离，不污染 ses_1 第三行）
catalog.Accept(H("message.part.updated", new
{
    sessionID = "ses_pat",
    part = new { id = "prt_pat1", callID = "call_pat1", tool = "bash", state = new { status = "running", input = new { command = "gradlew build" } } },
}));
Check(catalog.Projection().Tasks.First(t => t.Id == "ses_pat").Activity == AgentActivity.Executing, "gradlew build -> executing (not testing)");
catalog.Accept(H("message.part.updated", new
{
    sessionID = "ses_pat",
    part = new { id = "prt_pat2", callID = "call_pat2", tool = "bash", state = new { status = "running", input = new { command = "Get-Content latest.log" } } },
}));
Check(catalog.Projection().Tasks.First(t => t.Id == "ses_pat").Activity == AgentActivity.Executing, "latest substring no longer means testing");
catalog.Accept(H("message.part.updated", new
{
    sessionID = "ses_pat",
    part = new { id = "prt_pat3", callID = "call_pat3", tool = "bash", state = new { status = "running", input = new { command = "cargo test" } } },
}));
Check(catalog.Projection().Tasks.First(t => t.Id == "ses_pat").Activity == AgentActivity.Testing, "cargo test -> testing");

// 3b. 统一 `工具名 内容` 前缀（Codex/Claude 共用格式化；命令类不再裸命令）
Check(CodexEventReducer.LatestStep("Bash", "git status") == "Bash git status", "command tool keeps its tool name");
Check(CodexEventReducer.LatestStep("shell", "git commit -m x") == "Bash git commit -m x", "shell maps to Bash label");
Check(CodexEventReducer.LatestStep("Edit", @"F:\Git\x\a.kt") == @"Edit F:\Git\x\a.kt", "edit keeps its tool name");
Check(CodexEventReducer.LatestStep("Bash", "Bash echo hi") == "Bash echo hi", "already-prefixed title not duplicated");

// 4. permission.asked
catalog.Accept(H("permission.asked", new { sessionID = "ses_1", id = "per_1", permission = "bash", patterns = new[] { "npm test *" } }));
Check(Task().Lifecycle == AgentLifecycle.WaitingApproval, "permission.asked -> waitingApproval");
Check(Task().Activity == AgentActivity.Executing, "permission -> executing");
var projection = catalog.Projection();
var request = projection.PendingRequests.FirstOrDefault(r => r.TaskID == "ses_1");
Check(request is not null && request.Kind == PendingRequestKind.Approval, "pending approval registered");
Check(request?.Summary == "bash · npm test *", $"approval summary (got '{request?.Summary}')");
// 竞态：permission.asked 之后工具 part 补发 title，不得压回 running
catalog.Accept(H("message.part.updated", new
{
    sessionID = "ses_1",
    part = new { id = "prt_p", callID = "call_p", tool = "bash", state = new { status = "running", title = "npm test" } },
}));
Check(Task().Lifecycle == AgentLifecycle.WaitingApproval, "late part update keeps waitingApproval");
Check(Task().LatestStep == "Bash npm test", "late part title does not replace step");
Check(catalog.Projection().PendingRequests.Any(r => r.TaskID == "ses_1"), "late part update keeps approval request");
var ack = catalog.Perform(Guid.NewGuid(), new ControlPayload("ses_1", ControlAction.Approve, null), new NullResolver());
Check(ack.Result == ControlResult.Unsupported, "phone approve unsupported (baseline)");
Check(Task().Lifecycle == AgentLifecycle.WaitingApproval, "approve attempt leaves state");

// 5. permission.replied once
catalog.Accept(H("permission.replied", new { sessionID = "ses_1", requestID = "per_1", reply = "once" }));
Check(Task().Lifecycle == AgentLifecycle.Running, "permission allowed -> running");
Check(catalog.Projection().PendingRequests.Count == 0, "pending cleared after reply");

// 6. tool completed
catalog.Accept(H("message.part.updated", new
{
    sessionID = "ses_1",
    part = new { id = "prt_1", callID = "call_1", tool = "bash", state = new { status = "completed", title = "npm test", input = new { command = "npm test" } } },
}));
Check(Task().Activity == AgentActivity.Thinking, "tool completed -> thinking");
Check(Task().LatestStep == "Bash npm test", $"tool completed keeps its title (got '{Task().LatestStep}')");
// 6b. 改代码类工具统一带工具名前缀（Edit/Write）
catalog.Accept(H("message.part.updated", new
{
    sessionID = "ses_1",
    part = new { id = "prt_edit1", callID = "call_edit1", tool = "edit", state = new { status = "completed", title = "src/App.kt" } },
}));
Check(Task().LatestStep == "Edit src/App.kt", $"edit step prefixed (got '{Task().LatestStep}')");
catalog.Accept(H("message.part.updated", new
{
    sessionID = "ses_1",
    part = new { id = "prt_write1", callID = "call_write1", tool = "write", state = new { status = "completed", title = ".gitignore" } },
}));
Check(Task().LatestStep == "Write .gitignore", $"write step prefixed (got '{Task().LatestStep}')");
// 6c. 轮内插入 prompt 不清空第三行（只有新一轮才清）
catalog.Accept(H("chat.message", new { sessionID = "ses_1", text = "顺手再补个注释" }));
Check(Task().LatestStep == "Write .gitignore", "steering prompt keeps the step");

// 6d. 工具运行中迟到的正文帧不得盖掉命令（实测乱序：结束帧晚于工具 part ~200ms）
catalog.Accept(H("message.part.updated", new
{
    sessionID = "ses_1",
    part = new { id = "prt_run1", callID = "call_run1", tool = "bash", state = new { status = "running", input = new { command = "Start-Sleep -Seconds 12" } } },
}));
Check(Task().LatestStep == "Bash Start-Sleep -Seconds 12", $"running tool step visible (got '{Task().LatestStep}')");
var stepDuringTool = Task().LatestStep;
catalog.Accept(H("reply", new { sessionID = "ses_1", text = "上一段的迟到正文" }));
Check(Task().LatestStep == stepDuringTool, "stale reply during active tool is dropped");
catalog.Accept(H("message.part.updated", new
{
    sessionID = "ses_1",
    part = new { id = "prt_run1", callID = "call_run1", tool = "bash", state = new { status = "completed", title = "Start-Sleep -Seconds 12" } },
}));
Check(Task().Activity == AgentActivity.Thinking, "tool completed -> thinking");
catalog.Accept(H("reply", new { sessionID = "ses_1", text = "命令跑完后的正文" }));
Check(Task().LatestStep == "命令跑完后的正文", "reply accepted after tool finished");

// 6e. thinking 类工具运行中的残帧由“工具活动”守卫截获（状态判定），工具完成后放行
catalog.Accept(H("message.part.updated", new
{
    sessionID = "ses_1",
    part = new { id = "prt_todo1", callID = "call_todo1", tool = "todowrite", state = new { status = "running", input = new { todos = "x" } } },
}));
Check(Task().Activity == AgentActivity.Thinking, "todowrite -> thinking (activity guard blind spot)");
var stepBeforeTodo = Task().LatestStep;
catalog.Accept(H("reply", new { sessionID = "ses_1", text = "残帧-应被工具活动守卫丢弃" }));
Check(Task().LatestStep == stepBeforeTodo, "stale reply while tool active dropped even for thinking tools");
catalog.Accept(H("message.part.updated", new
{
    sessionID = "ses_1",
    part = new { id = "prt_todo1", callID = "call_todo1", tool = "todowrite", state = new { status = "completed" } },
}));
catalog.Accept(H("reply", new { sessionID = "ses_1", text = "工具完成后的正文" }));
Check(Task().LatestStep == "工具完成后的正文", "reply accepted once tool finished");

// 7. assistant message with tokens + completion
catalog.Accept(H("message.updated", new
{
    sessionID = "ses_1",
    info = new
    {
        role = "assistant",
        time = new { created, completed = created + 5000 },
        tokens = new { input = 1200, output = 340, reasoning = 90, cache = new { read = 800, write = 20 }, total = 2450 },
        modelID = "meta/muse-spark-1.3-contributor",
    },
}));
Check(Task().TokenUsage is { Input: 1200, CachedInput: 800, Output: 340, ReasoningOutput: 90, Total: 2450 }, "token usage mapped");
Check(Task().ModelName == "muse-spark-1.3-contributor", "model label updated from assistant modelID");

// 7b. a later, smaller message must replace the previous usage (follow latest, e.g. after compaction)
catalog.Accept(H("message.updated", new
{
    sessionID = "ses_1",
    info = new
    {
        role = "assistant",
        time = new { created, completed = created + 6000 },
        tokens = new { input = 100, output = 10, reasoning = 0, cache = new { read = 50, write = 5 }, total = 165 },
    },
}));
Check(Task().TokenUsage?.Total == 165, $"later smaller usage replaces earlier (got {Task().TokenUsage?.Total})");

// 8. session.idle
catalog.Accept(H("session.idle", new { sessionID = "ses_1" }));
Check(Task().Lifecycle == AgentLifecycle.Succeeded, "idle -> succeeded");
Check(Task().IsUnread && Task().CompletedAt is not null, "idle marks unread + completed");

// 9. session.updated title
catalog.Accept(H("session.updated", new
{
    sessionID = "ses_1",
    info = new { id = "ses_1", directory = @"F:\Git\demo", title = "修复登录流程", time = new { created, updated = created + 6000 } },
}));
Check(Task().Title == "demo · 修复登录流程", $"generated title applied (got '{Task().Title}')");

// 9b. 终态（succeeded）后迟到的工具 part 不得复活任务
var stepAfterIdle = Task().LatestStep;
catalog.Accept(H("message.part.updated", new
{
    sessionID = "ses_1",
    part = new { id = "prt_late1", callID = "call_late1", tool = "edit", state = new { status = "completed", title = "src/Late.kt" } },
}));
Check(Task().Lifecycle == AgentLifecycle.Succeeded, "late part after succeeded does not resurrect");
Check(Task().LatestStep == stepAfterIdle, "late part after succeeded does not touch the step");

// 10. error turn: new prompt -> error -> idle keeps interrupted
var beforeSecondTurn = Task().StartedAt;
var completedFirst = Task().CompletedAt;
Check(completedFirst is not null, "first turn completed timestamp set (timer stopped)");
Thread.Sleep(15);
catalog.Accept(H("chat.message", new { sessionID = "ses_1", text = "再试一次" }));
Check(Task().Lifecycle == AgentLifecycle.Running, "second prompt -> running");
Check(Task().StartedAt > beforeSecondTurn, "next prompt restarts the turn timer");
Check(Task().LatestStep is null, $"new turn clears the step for lifecycle fallback (got '{Task().LatestStep}')");
catalog.Accept(H("session.error", new { sessionID = "ses_1", error = new { name = "MessageAbortedError", message = "Aborted" } }));
Check(Task().Lifecycle == AgentLifecycle.Interrupted, "session.error -> interrupted");
Check(Task().LatestStep == "✗ Aborted", $"error step (got '{Task().LatestStep}')");
catalog.Accept(H("session.status", new { sessionID = "ses_1", status = new { type = "idle" } }));
Check(Task().Lifecycle == AgentLifecycle.Interrupted, "late idle keeps interrupted");
catalog.Accept(H("session.idle", new { sessionID = "ses_1" }));
Check(Task().Lifecycle == AgentLifecycle.Interrupted, "duplicate idle keeps interrupted");

// 11. permission reject -> interrupted; later tool error cannot resurrect
catalog.Accept(H("chat.message", new { sessionID = "ses_1", text = "继续" }));
catalog.Accept(H("permission.asked", new { sessionID = "ses_1", id = "per_2", permission = "edit", patterns = new[] { "src/**" } }));
catalog.Accept(H("permission.replied", new { sessionID = "ses_1", requestID = "per_2", reply = "reject" }));
Check(Task().Lifecycle == AgentLifecycle.Interrupted, "permission reject -> interrupted");
catalog.Accept(H("message.part.updated", new
{
    sessionID = "ses_1",
    part = new { id = "prt_2", callID = "call_2", tool = "edit", state = new { status = "error", error = "Tool execution aborted" } },
}));
Check(Task().Lifecycle == AgentLifecycle.Interrupted, "tool error keeps interrupted");

// 12. question.asked options
catalog.Accept(H("chat.message", new { sessionID = "ses_1", text = "帮我选" }));
catalog.Accept(H("question.asked", new
{
    sessionID = "ses_1",
    id = "que_1",
    questions = new[]
    {
        new { header = "选择方案", question = "用哪种实现？", options = new[] { new { label = "方案A", description = "简单" }, new { label = "方案B", description = "快" } } },
    },
}));
Check(Task().Lifecycle == AgentLifecycle.WaitingAnswer, "question.asked -> waitingAnswer");
var question = catalog.Projection().PendingRequests.First(r => r.TaskID == "ses_1");
Check(question.Kind == PendingRequestKind.Question && question.Summary == "用哪种实现？", $"question card summary uses full text (got '{question.Summary}')");
Check(Task().LatestStep == "选择方案", $"question third line keeps header (got '{Task().LatestStep}')");
Check(question.Options.Count == 2 && question.Options[0] == "方案A", "question options");
// 竞态：question.asked 之后工具 part 补发 running，不得压回 waitingAnswer / 清掉挂起请求
catalog.Accept(H("message.part.updated", new
{
    sessionID = "ses_1",
    part = new { id = "prt_q", callID = "call_q", tool = "question", state = new { status = "running" } },
}));
Check(Task().Lifecycle == AgentLifecycle.WaitingAnswer, "late part update keeps waitingAnswer");
Check(Task().LatestStep == "选择方案", "late part update keeps question card step");
Check(catalog.Projection().PendingRequests.Any(r => r.TaskID == "ses_1"), "late part update keeps pending request");
catalog.Accept(H("reply", new { sessionID = "ses_1", text = "等待期间不应盖标题" }));
Check(Task().LatestStep == "选择方案", "late reply during question wait does not replace step");
catalog.Accept(H("question.replied", new { sessionID = "ses_1", requestID = "que_1" }));
Check(Task().Lifecycle == AgentLifecycle.Running && catalog.Projection().PendingRequests.Count == 0, "question.replied -> running");
// 问题工具 part 完成（清出工具活动集，回放真实时序）
catalog.Accept(H("message.part.updated", new
{
    sessionID = "ses_1",
    part = new { id = "prt_q", callID = "call_q", tool = "question", state = new { status = "completed" } },
}));

// 12b. 正文预览（reply）：只改第三行，不动生命周期；回显 prompt 的预览被守卫丢弃
catalog.Accept(H("chat.message", new { sessionID = "ses_1", text = "把最新的日志整理给我" }));
catalog.Accept(H("reply", new { sessionID = "ses_1", text = "正在为你修复登录问题" }));
Check(Task().LatestStep == "正在为你修复登录问题", $"reply preview sets step (got '{Task().LatestStep}')");
Check(Task().Lifecycle == AgentLifecycle.Running, "reply does not change lifecycle");
catalog.Accept(H("reply", new { sessionID = "ses_1", text = "你说的是「把最新的日志整理给我」，我这就处理" }));
Check(Task().LatestStep == "正在为你修复登录问题", "reply echoing prompt is rejected");

// 12c. 排队标记：busy 期间提交 → queued；assistant 开始接单 → prompt.active 清除
catalog.Accept(H("chat.message", new { sessionID = "ses_1", text = "排队等我一下", queued = true }));
Check(Task().PromptQueued, "queued prompt flagged");
catalog.Accept(H("prompt.active", new { sessionID = "ses_1" }));
Check(!Task().PromptQueued, "prompt.active clears queued flag");
catalog.Accept(H("chat.message", new { sessionID = "ses_1", text = "正常提交", queued = false }));
Check(!Task().PromptQueued, "normal prompt not queued");

// 12d. 子代理映射（subagent 事件）
catalog.Accept(H("subagent", new { sessionID = "ses_1", id = "ses_sub", phase = "started", agent = "explore", title = "列出 docs 目录文件 (@explore subagent)" }));
var subagent = Task().Subagents.FirstOrDefault(s => s.Id == "ses_sub");
Check(subagent is not null && subagent.DisplayName == "Explore" && subagent.Path == "/explore" && subagent.Lifecycle == AgentLifecycle.Running, $"subagent started (got '{subagent?.DisplayName}')");
Check(Task().Activity == AgentActivity.Delegating, "parent delegating while subagent runs");
catalog.Accept(H("subagent", new { sessionID = "ses_1", id = "ses_sub", phase = "tool", tool = "read", step = "read src/app.ts" }));
Check(Task().Subagents.First(s => s.Id == "ses_sub").Activity == AgentActivity.Reading, "subagent tool activity");
Check(Task().Subagents.First(s => s.Id == "ses_sub").LatestStep == "read src/app.ts", "subagent tool step");
catalog.Accept(H("subagent", new { sessionID = "ses_1", id = "ses_sub", phase = "tool", tool = "edit", title = "src/app.ts" }));
Check(Task().Subagents.First(s => s.Id == "ses_sub").LatestStep == "Edit src/app.ts", "subagent tool title prefixed");
catalog.Accept(H("subagent", new { sessionID = "ses_1", id = "ses_sub", phase = "tokens", tokens = new { input = 10, output = 5, reasoning = 1, cache = new { read = 2, write = 0 }, total = 18 } }));
Check(Task().Subagents.First(s => s.Id == "ses_sub").TokenUsage?.Total == 18, "subagent tokens");
catalog.Accept(H("subagent", new { sessionID = "ses_1", id = "ses_sub", phase = "done" }));
Check(Task().Subagents.First(s => s.Id == "ses_sub").Lifecycle == AgentLifecycle.Succeeded, "subagent done");
Check(Task().Activity == AgentActivity.Thinking, "parent back to thinking after subagents done");
catalog.Accept(H("subagent", new { sessionID = "ses_1", id = "ses_sub", phase = "tool", tool = "bash", step = "bash late" }));
Check(Task().Subagents.First(s => s.Id == "ses_sub").LatestStep != "bash late", "late tool after done ignored");
catalog.Accept(H("subagent", new { sessionID = "ses_unknown", id = "ses_grandchild", phase = "started", agent = "explore" }));
Check(catalog.Projection().Tasks.All(t => t.Id != "ses_unknown"), "subagent for unknown parent ignored");

// 13. child session ignored by bridge when plugin drops it (belt & braces: unknown child not created)
catalog.Accept(H("message.part.updated", new
{
    sessionID = "ses_child",
    part = new { id = "prt_c", callID = "call_c", tool = "read", state = new { status = "running", input = new { filePath = "x.cs" } } },
}));
Check(catalog.Projection().Tasks.Any(t => t.Id == "ses_child"), "unknown session event creates task lazily");
Check(catalog.Projection().Tasks.First(t => t.Id == "ses_child").Activity == AgentActivity.Reading, "read -> reading");

// 14. dispose: unknown session ignored; known non-terminal -> offline
var before = catalog.Projection().Tasks.Count;
catalog.Accept(H("plugin.dispose", new { sessionID = "ses_unknown" }));
Check(catalog.Projection().Tasks.Count == before, "dispose for unknown session ignored");
// 14a. dispose 时未终态的子代理级联扫为 interrupted（O3）
catalog.Accept(H("subagent", new { sessionID = "ses_1", id = "ses_stuck", phase = "started", agent = "explore" }));
Check(Task().Subagents.First(s => s.Id == "ses_stuck").Lifecycle == AgentLifecycle.Running, "stuck subagent running before dispose");
catalog.Accept(H("plugin.dispose", new { sessionID = "ses_1" }));
Check(Task().Lifecycle == AgentLifecycle.Offline, "dispose -> offline for non-terminal session");
Check(Task().Subagents.First(s => s.Id == "ses_stuck").Lifecycle == AgentLifecycle.Interrupted, "orphaned subagent swept on dispose");
catalog.Accept(H("message.part.updated", new
{
    sessionID = "ses_1",
    part = new { id = "prt_off1", callID = "call_off1", tool = "read", state = new { status = "running", input = new { filePath = "x.cs" } } },
}));
Check(Task().Lifecycle == AgentLifecycle.Offline, "late part after offline does not resurrect");

// 14c. 未知 session.status 类型不得被当作回合结束（O4）
catalog.Accept(H("session.status", new { sessionID = "ses_1", status = new { type = "future_type" } }));
Check(Task().Lifecycle == AgentLifecycle.Offline, "unknown status type ignored (no idle fallback)");

// 15. wire serialization
var snapshot = catalog.Projection();
var json = JsonSerializer.Serialize(
    MessageEnvelope<StateSnapshotPayload>.Create("state.snapshot", new StateSnapshotPayload(snapshot.Tasks, null, snapshot.FocusedTaskID, snapshot.PendingRequests)),
    WireJson.Options);
Check(json.Contains("\"source\":\"opencode\""), "wire source name is 'opencode'");
Check(!json.Contains("\"OpenCode\""), "wire has no PascalCase enum leakage");

// 16. plugin installer against temp dir: three states + version
var temp = Path.Combine(Path.GetTempPath(), "ap-verify-plugins-" + Guid.NewGuid().ToString("N"));
var installer = new OpenCodePluginConfiguration(temp);
Check(installer.Status == OpenCodePluginStatus.NotInstalled, "status: 未安装");
Check(OpenCodePluginConfiguration.EmbeddedVersion == 23, $"embedded plugin version = 23 (got {OpenCodePluginConfiguration.EmbeddedVersion})");
var first = installer.Install();
Check(first.Changed && File.Exists(Path.Combine(temp, "plugins", "agentpager-opencode.js")), "install writes plugin");
Check(installer.Status == OpenCodePluginStatus.Installed && installer.IsInstalled(), "status: 已写入（与内置一致）");
Check(installer.InstalledVersion == 23, "installed version = 23");
var second = installer.Install();
Check(!second.Changed, "reinstall is idempotent");
var pluginPath = Path.Combine(temp, "plugins", "agentpager-opencode.js");
var content = File.ReadAllText(pluginPath);
Check(content.Contains("Managed by AgentPager (OpenCode)"), "managed marker present");
Check(content.Contains("agentpager-opencode v23"), "version header present");
Check(content.Contains("hook_source: \"opencode\"") && content.Contains("49361"), "plugin content sane");
File.WriteAllText(pluginPath, content.Replace("const MAX_TEXT = 1000", "const MAX_TEXT = 500"));
Check(installer.Status == OpenCodePluginStatus.Outdated, "status: 待更新（内容与内置不一致）");
Check(installer.InstalledVersion == 23, "outdated file still reports v23");
var repair = installer.Install();
Check(repair.Changed && installer.Status == OpenCodePluginStatus.Installed, "repair restores 已写入");
var uninstalled = installer.Uninstall();
Check(uninstalled.Changed && !File.Exists(Path.Combine(temp, "plugins", "agentpager-opencode.js")), "uninstall removes managed file");

// 17. foreign file backup + preserved on uninstall
Directory.CreateDirectory(Path.Combine(temp, "plugins"));
File.WriteAllText(Path.Combine(temp, "plugins", "agentpager-opencode.js"), "// user's own plugin\n");
var backupResult = installer.Install();
Check(backupResult.Changed && backupResult.BackupPath is not null && File.Exists(backupResult.BackupPath), "foreign file backed up on install");
Check(File.ReadAllText(backupResult.BackupPath!) == "// user's own plugin\n", "backup content correct");
var uninstallForeign = installer.Install(); // reinstall managed copy, then...
Check(!uninstallForeign.Changed, "managed content already in place");
// simulate user replacing managed file with foreign content; uninstall must not delete it
File.WriteAllText(Path.Combine(temp, "plugins", "agentpager-opencode.js"), "// not ours\n");
var uninstallResult = installer.Uninstall();
Check(!uninstallResult.Changed && File.Exists(Path.Combine(temp, "plugins", "agentpager-opencode.js")), "uninstall spares foreign file");

Directory.Delete(temp, true);

// ===================== 18. AgyEventReducer / AgyTranscriptReader 断言（A7） =====================
var agyNow = DateTimeOffset.UtcNow;
var agyInfo = new AgyConversationSummary("agy_1", "修复登录", null, "research", "running");
var agy = AgyEventReducer.Reduce(A("PreInvocation", new { conversationId = "agy_1", workspacePaths = new[] { @"F:\Git\demo" } }), null, agyInfo, agyNow);
Check(agy.Source == AgentSource.Antigravity && agy.Lifecycle == AgentLifecycle.Running, "agy: preinvocation -> running");
Check(agy.Title == "demo · 修复登录", $"agy: title from db summary only (got '{agy.Title}')");
Check(agy.LatestStep is null, "agy: new round clears step");

agy = AgyEventReducer.Reduce(A("PreToolUse", new { conversationId = "agy_1", workspacePaths = new[] { @"F:\Git\demo" }, toolCall = new { name = "run_command", args = new { CommandLine = "npm test" } } }), agy, agyInfo, agyNow.AddSeconds(1));
Check(agy.LatestStep == "Bash npm test" && agy.Activity == AgentActivity.Testing, $"agy: run_command test -> Bash + testing (got '{agy.LatestStep}/{agy.Activity}')");

agy = AgyEventReducer.Reduce(A("PreToolUse", new { conversationId = "agy_1", workspacePaths = new[] { @"F:\Git\demo" }, toolCall = new { name = "view_file", args = new { AbsolutePath = @"F:\Git\demo\src\app.ts" } } }), agy, agyInfo, agyNow.AddSeconds(2));
Check(agy.LatestStep == "Read src/app.ts", $"agy: view_file -> Read + relative path (got '{agy.LatestStep}')");

// agy: 测试判定按独立词（与桥共享 ShellTestRun）
Check(AgyEventReducer.ActivityFor("run_command", JsonSerializer.SerializeToElement(new { CommandLine = "gradlew build" })) == AgentActivity.Executing, "agy: gradlew build -> executing");
Check(AgyEventReducer.ActivityFor("run_command", JsonSerializer.SerializeToElement(new { CommandLine = "Get-Content latest.log" })) == AgentActivity.Executing, "agy: latest substring -> executing");
Check(AgyEventReducer.ActivityFor("run_command", JsonSerializer.SerializeToElement(new { CommandLine = "cargo test" })) == AgentActivity.Testing, "agy: cargo test -> testing");

agy = AgyEventReducer.Reduce(A("PreToolUse", new { conversationId = "agy_1", toolCall = new { name = "ask_question", args = new { questions = new[] { new { question = "选哪个方案？", options = new[] { "A", "B" } } } } } }), agy, agyInfo, agyNow.AddSeconds(3));
Check(agy.Lifecycle == AgentLifecycle.WaitingAnswer && agy.LatestStep == "选哪个方案？", $"agy: ask_question -> waitingAnswer (got '{agy.Lifecycle}/{agy.LatestStep}')");

agy = AgyEventReducer.Reduce(A("PostToolUse", new { conversationId = "agy_1", toolCall = new { name = "run_command", args = new { CommandLine = "echo x" } } }), agy, agyInfo, agyNow.AddSeconds(4));
Check(agy.Lifecycle == AgentLifecycle.WaitingAnswer, "agy: parallel tool while waiting ignored");

agy = AgyEventReducer.Reduce(A("PostToolUse", new { conversationId = "agy_1", toolCall = new { name = "ask_question", args = new { } } }), agy, agyInfo, agyNow.AddSeconds(5));
Check(agy.Lifecycle == AgentLifecycle.Running, "agy: ask_question answered -> running");

agy = AgyEventReducer.Reduce(A("PostToolUse", new { conversationId = "agy_1", toolCall = new { name = "run_command", args = new { CommandLine = "x" } }, error = "boom" }), agy, agyInfo, agyNow.AddSeconds(6));
Check(agy.LatestStep is not null && agy.LatestStep.StartsWith("✗"), $"agy: tool error -> ✗ step (got '{agy.LatestStep}')");

// ---- agy 远程作答：卡片走共享机件（选项 / 多选 / 多题逐题），答案经 deny+reason 回给模型 ----
// 依据：2026-10-14 探针实测（overwrite 预填被 Go 的 json unmarshal 静默丢弃、与空对照逐字节相同）。
var agyAskAt = DateTimeOffset.UtcNow;
var agyAskCatalog = new TaskCatalog();
agyAskCatalog.Accept(A("PreToolUse", new
{
    conversationId = "agy_ask",
    workspacePaths = new[] { @"F:\Git\demo" },
    toolCall = new { name = "ask_question", args = new { questions = new[] { new { question = "选哪个方案？", options = new[] { "A", "B" }, is_multi_select = false } } } }
}), agyAskAt);
var agyAskTask = agyAskCatalog.Projection().Tasks.First(t => t.Id == "agy_ask");
Check(agyAskTask.Lifecycle == AgentLifecycle.WaitingAnswer && agyAskTask.Capabilities.Contains(TaskCapability.Answer),
    $"agy ask: waitingAnswer + answer capability (got {agyAskTask.Lifecycle}/[{string.Join(",", agyAskTask.Capabilities)}])");
var agyAskCard = agyAskCatalog.Projection().PendingRequests.First(r => r.TaskID == "agy_ask");
Check(agyAskCard.Kind == PendingRequestKind.Question && agyAskCard.Summary == "选哪个方案？" &&
      agyAskCard.Options is ["A", "B"] && !agyAskCard.MultiSelect,
    $"agy ask: card carries the real options (got '{agyAskCard.Summary}' / [{string.Join(",", agyAskCard.Options)}] / multi={agyAskCard.MultiSelect})");

// 没有手机连着时不抢提问：卡片照旧带题目与选项，但不给作答入口（终端自己弹框，作答入口是假的）
var agyNoPhoneCatalog = new TaskCatalog();
agyNoPhoneCatalog.Accept(A("PreToolUse", new
{
    conversationId = "agy_ask_nophone",
    toolCall = new { name = "ask_question", args = new { questions = new[] { new { question = "终端自己问？", options = new[] { "A", "B" }, is_multi_select = false } } } }
}), agyAskAt, answerable: false);
var agyNoPhoneTask = agyNoPhoneCatalog.Projection().Tasks.First(t => t.Id == "agy_ask_nophone");
var agyNoPhoneCard = agyNoPhoneCatalog.Projection().PendingRequests.First(r => r.TaskID == "agy_ask_nophone");
Check(agyNoPhoneTask.Lifecycle == AgentLifecycle.WaitingAnswer && !agyNoPhoneTask.Capabilities.Contains(TaskCapability.Answer),
    $"agy ask: no phone -> no answer capability (got {agyNoPhoneTask.Lifecycle}/[{string.Join(",", agyNoPhoneTask.Capabilities)}])");
Check(agyNoPhoneCard.Summary == "终端自己问？" && agyNoPhoneCard.Options is ["A", "B"],
    $"agy ask: card keeps question + options when nobody can answer (got '{agyNoPhoneCard.Summary}')");

// 多题：与 Claude/opencode 同一条队列——先呈现第 1 题（带题号前缀），每题各带自己的选项与多选位
var agyAskMultiCatalog = new TaskCatalog();
agyAskMultiCatalog.Accept(A("PreToolUse", new
{
    conversationId = "agy_ask_multi",
    toolCall = new
    {
        name = "ask_question",
        args = new
        {
            questions = new[]
            {
                new { question = "第一题单选？", options = new[] { "A", "B" }, is_multi_select = false },
                new { question = "第二题多选？", options = new[] { "C", "D" }, is_multi_select = true }
            }
        }
    }
}), agyAskAt);
var agyAskMultiCard = agyAskMultiCatalog.Projection().PendingRequests.First(r => r.TaskID == "agy_ask_multi");
Check(agyAskMultiCard.Summary == "第 1/2 题： 第一题单选？" && agyAskMultiCard.Options is ["A", "B"] && !agyAskMultiCard.MultiSelect,
    $"agy ask: multi-question shows 1/2 with its own options (got '{agyAskMultiCard.Summary}' / multi={agyAskMultiCard.MultiSelect})");

// 字符串形态的 questions 也认（hook 实测是数组，但 transcript/历史实现写过字符串，两种都得能解析）
const string agyStringified = """[{"question":"字符串形态？","options":["X","Y"],"is_multi_select":true}]""";
var agyAskStringCatalog = new TaskCatalog();
agyAskStringCatalog.Accept(A("PreToolUse", new
{
    conversationId = "agy_ask_string",
    toolCall = new { name = "ask_question", args = new { questions = agyStringified } }
}), agyAskAt);
var agyAskStringCard = agyAskStringCatalog.Projection().PendingRequests.First(r => r.TaskID == "agy_ask_string");
Check(agyAskStringCard.Summary == "字符串形态？" && agyAskStringCard.Options is ["X", "Y"] && agyAskStringCard.MultiSelect,
    $"agy ask: stringified questions still parse (got '{agyAskStringCard.Summary}' / [{string.Join(",", agyAskStringCard.Options)}] / multi={agyAskStringCard.MultiSelect})");

// 回包：`deny` + `reason` 承载答案（终端不再弹框；reason 是文档里"shown to the user/agent"的那个字段）。
// 题目与选项一并带上——终端那份记录里能留下的只有这段文字，不带选项终端就只剩"被拒绝了"。
var agyReplyText = System.Text.Encoding.UTF8.GetString(
    AgyHookOutput.QuestionReply([new QuestionAnswer("选哪个方案？", ["A"], null, ["A", "B"])]));
using var agyReplyDoc = JsonDocument.Parse(agyReplyText);
Check(agyReplyDoc.RootElement.GetProperty("decision").GetString() == "deny" &&
      agyReplyDoc.RootElement.GetProperty("reason").GetString() is { } reason &&
      reason.Contains("Q1: 选哪个方案？") && reason.Contains("选项：A / B") &&
      reason.Contains("A1: A") && reason.Contains("不要再重复提问"),
    $"agy ask: reply is deny + reason carrying question/options/answer (got '{agyReplyText.Trim()}')");

// 回显守卫：步骤包含当前 prompt（≥4 字）应被丢弃
agy = agy with { UserPrompt = "帮我看看日志" };
var agyStepBeforeEcho = agy.LatestStep;
agy = AgyEventReducer.Reduce(A("PreToolUse", new { conversationId = "agy_1", toolCall = new { name = "run_command", args = new { CommandLine = "echo 帮我看看日志" } } }), agy, agyInfo, agyNow.AddSeconds(7));
Check(agy.LatestStep == agyStepBeforeEcho, "agy: prompt echo rejected from tool step");

// 子代理占位 + Stop 级联终态
agy = AgyEventReducer.Reduce(A("PreToolUse", new { conversationId = "agy_1", toolCall = new { name = "invoke_subagent", args = new { Subagents = new[] { new { Role = "researcher", TypeName = "research", Prompt = "调查" } } } } }), agy, agyInfo, agyNow.AddSeconds(8));
Check(agy.Subagents.Count == 0, "agy: invoke_subagent no longer fabricates placeholder (A1)");
Check(agy.Activity == AgentActivity.Delegating, "agy: delegating while subagent active");

agy = AgyEventReducer.Reduce(A("Stop", new { conversationId = "agy_1", terminationReason = "model_stop" }), agy, agyInfo, agyNow.AddSeconds(9));
Check(agy.Lifecycle == AgentLifecycle.Succeeded, "agy: stop -> succeeded");
Check(agy.Subagents.All(s => s.IsTerminal), "agy: stop cascades subagent finalization");

agy = AgyEventReducer.Reduce(A("PostToolUse", new { conversationId = "agy_1", toolCall = new { name = "run_command", args = new { CommandLine = "late" } } }), agy, agyInfo, agyNow.AddSeconds(10));
Check(agy.Lifecycle == AgentLifecycle.Succeeded, "agy: late tool after stop ignored");

// A4: 静默 ≥10 分钟判离线。2026-10-14 起**不再豁免"工具活动中"**——
// 死在工具中途的会话（最后一次事件是 PreToolUse，随后进程消失）活动位永远停在 executing，
// 旧规则把它永久豁免，实测三条僵尸行挂了一个多小时不收敛。
var agyCatalog = new TaskCatalog();
var agyT0 = DateTimeOffset.UtcNow;
agyCatalog.Accept(A("PreInvocation", new { conversationId = "agy_off", workspacePaths = new[] { @"F:\Git\demo" } }), agyT0);
agyCatalog.Accept(A("PreToolUse", new { conversationId = "agy_off", toolCall = new { name = "run_command", args = new { CommandLine = "Start-Sleep 1" } } }), agyT0.AddSeconds(1));
Check(agyCatalog.Projection().Tasks.First(t => t.Id == "agy_off").Lifecycle == AgentLifecycle.Running, "A4: agy task running before timeout");
agyCatalog.Maintain(agyT0.AddMinutes(29));
Check(agyCatalog.Projection().Tasks.First(t => t.Id == "agy_off").Lifecycle == AgentLifecycle.Running,
    "A4: a tool actually running gets the longer 30 minute grace");
agyCatalog.Maintain(agyT0.AddMinutes(31));
Check(agyCatalog.Projection().Tasks.First(t => t.Id == "agy_off").Lifecycle == AgentLifecycle.Offline,
    "A4: a session stuck mid-tool converges once the 30 minute cap passes");

// 判死的会话不该继续摆着可点的卡片
var agyCardCatalog = new TaskCatalog();
agyCardCatalog.Accept(A("PreToolUse", new
{
    conversationId = "agy_off_card",
    toolCall = new { name = "ask_question", args = new { questions = new[] { new { question = "还问吗？", options = new[] { "A", "B" } } } } }
}), agyT0);
Check(agyCardCatalog.Projection().PendingRequests.Count == 1, "A4: question card registered before the timeout");
agyCardCatalog.Maintain(agyT0.AddMinutes(11));
Check(agyCardCatalog.Projection().PendingRequests.Count == 0, "A4: the dead session's card is dropped with it");

// 重启只认终态行：非终态的一律丢弃（僵尸行不留）
var restartPath = Path.Combine(Path.GetTempPath(), "ap-verify-tasks-" + Guid.NewGuid().ToString("N") + ".json");
var restartStore = new TaskSnapshotPersistence(restartPath);
var restartCatalog = new TaskCatalog();
restartCatalog.Accept(A("PreInvocation", new { conversationId = "restart_live" }), agyT0);
restartCatalog.Accept(A("PreInvocation", new { conversationId = "restart_done" }), agyT0);
restartCatalog.Accept(A("Stop", new { conversationId = "restart_done", terminationReason = "model_stop" }), agyT0.AddSeconds(1));
Check(restartCatalog.Projection().Tasks.Count == 2, "restart: fixture has one live and one finished row");
restartStore.Save(restartCatalog.Projection().Tasks);
var restartLoaded = restartStore.Load();
Check(restartLoaded.Count == 1 && restartLoaded[0].Id == "restart_done",
    $"restart: only terminal rows survive a reload (got {restartLoaded.Count}: {string.Join(",", restartLoaded.Select(t => t.Id))})");
File.Delete(restartPath);

// A5: 转录本读取按事件门控（工具事件跳过，PostInvocation/Stop 读取）
var agyTp3 = Path.Combine(Path.GetTempPath(), "ap-verify-agy-" + Guid.NewGuid().ToString("N") + ".jsonl");
File.WriteAllLines(agyTp3, new[]
{
    "{\"type\":\"USER_INPUT\",\"content\":\"<USER_REQUEST>门控测试</USER_REQUEST>\"}",
    "{\"type\":\"PLANNER_RESPONSE\",\"content\":\"完成\",\"input_tokens\":7,\"output_tokens\":3}",
});
var agyBase = AgyEventReducer.Reduce(A("PreInvocation", new { conversationId = "agy_gate" }), null, agyInfo, agyNow);
var agyGated = AgyEventReducer.Reduce(A("PreToolUse", new { conversationId = "agy_gate", transcriptPath = agyTp3, toolCall = new { name = "run_command", args = new { CommandLine = "echo x" } } }), agyBase, agyInfo, agyNow.AddSeconds(1));
Check(agyGated.TokenUsage is null && agyGated.LatestStep == "Bash echo x", "A5: tool event skips transcript read");
var agyRead = AgyEventReducer.Reduce(A("PostInvocation", new { conversationId = "agy_gate", transcriptPath = agyTp3 }), agyGated, agyInfo, agyNow.AddSeconds(2));
Check(agyRead.TokenUsage is { Input: 7, Output: 3 }, "A5: PostInvocation reads transcript tokens");
File.Delete(agyTp3);

// 条目1: Stop 读转录本竞速 → Maintain 补读终稿
var agyRc = new TaskCatalog();
var agyTp4 = Path.Combine(Path.GetTempPath(), "ap-verify-agy-" + Guid.NewGuid().ToString("N") + ".jsonl");
File.WriteAllLines(agyTp4, new[] { "{\"type\":\"USER_INPUT\",\"content\":\"<USER_REQUEST>竞速测试</USER_REQUEST>\"}" });
var agyT1 = DateTimeOffset.UtcNow;
agyRc.Accept(A("PreInvocation", new { conversationId = "agy_rc", workspacePaths = new[] { @"F:\Git\demo" }, transcriptPath = agyTp4 }), agyT1);
agyRc.Accept(A("PreToolUse", new { conversationId = "agy_rc", toolCall = new { name = "run_command", args = new { CommandLine = "echo x" } } }), agyT1.AddSeconds(1));
agyRc.Accept(A("PostToolUse", new { conversationId = "agy_rc", toolCall = new { name = "run_command", args = new { CommandLine = "x" } } }), agyT1.AddSeconds(2));
agyRc.Accept(A("Stop", new { conversationId = "agy_rc", transcriptPath = agyTp4, terminationReason = "model_stop" }), agyT1.AddSeconds(3));
Check(agyRc.Projection().Tasks.First(t => t.Id == "agy_rc").Lifecycle == AgentLifecycle.Succeeded, "recheck: stop without preview still succeeds");
Check(agyRc.Projection().Tasks.First(t => t.Id == "agy_rc").LatestStep == "Bash echo x", "recheck: tool step retained when preview raced");
File.AppendAllText(agyTp4, "{\"type\":\"PLANNER_RESPONSE\",\"content\":\"补读的终稿\",\"input_tokens\":1,\"output_tokens\":1}\n");
agyRc.Maintain(agyT1.AddSeconds(6));
Check(agyRc.Projection().Tasks.First(t => t.Id == "agy_rc").LatestStep == "补读的终稿", "recheck: late preview applied by maintenance");
File.Delete(agyTp4);

// 转录本解析：prompt 清洗 / 当前回合 token / 预览仅当末步为纯文本
var agyTp = Path.Combine(Path.GetTempPath(), "ap-verify-agy-" + Guid.NewGuid().ToString("N") + ".jsonl");
File.WriteAllLines(agyTp, new[]
{
    "{\"type\":\"GENERIC\",\"content\":\"noise\"}",
    "{\"type\":\"USER_INPUT\",\"content\":\"<USER_REQUEST>修好登录</USER_REQUEST><ADDITIONAL_METADATA>x</ADDITIONAL_METADATA>\"}",
    "{\"type\":\"PLANNER_RESPONSE\",\"content\":\"先看代码\",\"tool_calls\":[{\"name\":\"view_file\"}],\"input_tokens\":100,\"cache_read_tokens\":20,\"output_tokens\":30}",
    "{\"type\":\"GENERIC\",\"content\":\"tool output\"}",
    "{\"type\":\"PLANNER_RESPONSE\",\"content\":\"已修复\",\"input_tokens\":150,\"cache_read_tokens\":30,\"output_tokens\":40}",
});
var (agyPrompt, agyPreview, agyTokens, agyFull) = AgyTranscriptReader.ExtractLatest(agyTp);
Check(agyPrompt == "修好登录", $"agy: transcript prompt cleaned (got '{agyPrompt}')");
Check(agyPreview == "已修复", $"agy: transcript preview from pure-text latest step (got '{agyPreview}')");
Check(agyFull == "已修复", $"agy: transcript full response preserved (got '{agyFull}')");
Check(agyTokens is { Input: 150, CachedInput: 30, Output: 40, Total: 220 }, $"agy: transcript tokens follow latest (got '{agyTokens}')");
File.Delete(agyTp);

var agyTp2 = Path.Combine(Path.GetTempPath(), "ap-verify-agy-" + Guid.NewGuid().ToString("N") + ".jsonl");
File.WriteAllLines(agyTp2, new[]
{
    "{\"type\":\"USER_INPUT\",\"content\":\"<USER_REQUEST>再看看</USER_REQUEST>\"}",
    "{\"type\":\"PLANNER_RESPONSE\",\"content\":\"正在执行\",\"tool_calls\":[{\"name\":\"run_command\"}],\"input_tokens\":10,\"output_tokens\":5}",
});
var (agyPrompt2, agyPreview2, agyTokens2, agyFull2) = AgyTranscriptReader.ExtractLatest(agyTp2);
Check(agyPrompt2 == "再看看", "agy: transcript prompt 2");
Check(agyPreview2 is null, "agy: preview suppressed when latest step has tool calls");
Check(agyFull2 is null, "agy: full response suppressed when latest step has tool calls");
Check(agyTokens2 is { Input: 10, Output: 5 }, "agy: tokens from tool-call planner");
File.Delete(agyTp2);

// ===================== 19. Claude Code 断言 =====================
static ClaudeHookPayload C(string evt, object props)
{
    var node = JsonSerializer.SerializeToNode(props)!.AsObject();
    node["hook_event_name"] = evt;
    if (node["session_id"] is null) node["session_id"] = "claude_1";
    if (node["cwd"] is null) node["cwd"] = @"F:\Git\demo";
    return JsonSerializer.Deserialize<ClaudeHookPayload>(node.ToJsonString(), WireJson.Options)!;
}

static ClaudeTranscriptSnapshot ReadTranscript(string[] lines)
{
    var path = Path.Combine(Path.GetTempPath(), "ap-verify-claude-" + Guid.NewGuid().ToString("N") + ".jsonl");
    File.WriteAllLines(path, lines);
    try { return ClaudeTranscriptReader.Read(path); }
    finally { File.Delete(path); }
}

// ---- 标题：只取会话真实标题，绝不从 prompt 自造（G1）----
var claudeNow = DateTimeOffset.UtcNow;
var claude = new TaskCatalog();
claude.Accept(C("SessionStart", new { source = "startup" }), claudeNow);
TaskSnapshot Claude() => claude.Projection().Tasks.First(t => t.Id == "claude_1");

// 全新会话那条 SessionStart **不建行**（启动幽灵修复）：它由 Claude Code 每次进程启动无条件发出，
// 而那一刻转录本文件尚未创建、载荷里也没有 session_title —— 标题三条来源全空，建行只能得到一个
// 顶着 cwd 目录名的"没有 session title 的幽灵任务"（用户实测 100% 复现，探针实测 exists=false）。
Check(claude.Projection().Tasks.Count == 0, "claude: fresh SessionStart creates no task (no titleless ghost)");

claude.Accept(C("UserPromptSubmit", new { prompt = "  帮我看看   那个登录问题  " }), claudeNow.AddSeconds(1));
Check(Claude().Lifecycle == AgentLifecycle.Running && Claude().Activity == AgentActivity.Thinking, "claude: prompt -> running/thinking");
Check(Claude().UserPrompt == "帮我看看 那个登录问题", $"claude: prompt normalized (got '{Claude().UserPrompt}')");
Check(Claude().StartedAt == claudeNow.AddSeconds(1), "claude: first prompt starts this turn's timer");
Check(Claude().Title == "demo", $"claude: title still project name, never derived from prompt (got '{Claude().Title}')");

// SessionStart 的语义只对**已经在面板上**的行生效；且**终态受保护**（2026-10-11 修，对齐 opencode）：
// 已完成的任务被 resume/clear 打回 idle 会掉优先级（300→100）并丢未读高亮——用户实测报出过
// "app 重连后原本 succeeded 的任务变成 idle"。
var resumeCatalog = new TaskCatalog();
resumeCatalog.Accept(C("UserPromptSubmit", new { session_id = "resume_1", prompt = "第一轮" }), claudeNow);
resumeCatalog.Accept(C("Stop", new { session_id = "resume_1", last_assistant_message = "完事" }), claudeNow.AddSeconds(1));
TaskSnapshot Resume() => resumeCatalog.Projection().Tasks.First(t => t.Id == "resume_1");
Check(Resume().Lifecycle == AgentLifecycle.Succeeded, "claude: resume fixture finished before the second start");
var resumeCompletedAt = Resume().CompletedAt;
resumeCatalog.Accept(C("SessionStart", new { session_id = "resume_1", source = "resume" }), claudeNow.AddSeconds(2));
Check(Resume().Lifecycle == AgentLifecycle.Succeeded && Resume().CompletedAt == resumeCompletedAt,
    $"claude: SessionStart must not revive a finished task (got {Resume().Lifecycle})");
// 优先级 = succeeded 300 + 未读 50；打回 idle 的话只剩 100（用户看到的就是"完成的任务掉下去了"）
Check(Resume().EffectivePriority == 350, $"claude: a finished task keeps its priority across resume (got {Resume().EffectivePriority})");

// 非终态的行走老规矩：resume / clear 把它复位成 idle
var idleResumeCatalog = new TaskCatalog();
idleResumeCatalog.Accept(C("UserPromptSubmit", new { session_id = "resume_2", prompt = "干着活" }), claudeNow);
Check(idleResumeCatalog.Projection().Tasks.First(t => t.Id == "resume_2").Lifecycle == AgentLifecycle.Running, "claude: resume fixture is running");
idleResumeCatalog.Accept(C("SessionStart", new { session_id = "resume_2", source = "clear" }), claudeNow.AddSeconds(1));
Check(idleResumeCatalog.Projection().Tasks.First(t => t.Id == "resume_2").Lifecycle == AgentLifecycle.Idle,
    "claude: SessionStart still resets a live session to idle");

var claudeSnapshot = ReadTranscript(
[
    "{\"type\":\"user\",\"isSidechain\":false,\"message\":{\"role\":\"user\",\"content\":\"帮我看看那个登录问题\"}}",
    "{\"type\":\"assistant\",\"isSidechain\":false,\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"已修复登录流程\"}],\"usage\":{\"input_tokens\":150,\"cache_read_input_tokens\":30,\"output_tokens\":40,\"output_tokens_details\":{\"thinking_tokens\":5}},\"model\":\"deepseek/deepseek-v4.1-flash\"}}",
    "{\"type\":\"ai-title\",\"aiTitle\":\"修复登录流程\"}",
]);
Check(claudeSnapshot.Title == "修复登录流程", $"claude: ai-title from transcript (got '{claudeSnapshot.Title}')");
Check(claudeSnapshot.Tokens is { Input: 150, CachedInput: 30, Output: 40, ReasoningOutput: 5, Total: 220 }, "claude: token usage mapped from message.usage");
Check(claudeSnapshot.Model == "deepseek-v4.1-flash", $"claude: model label takes last segment (got '{claudeSnapshot.Model}')");
Check(claudeSnapshot.LatestReply == "已修复登录流程", $"claude: reply preview from transcript (got '{claudeSnapshot.LatestReply}')");
Check(claudeSnapshot.LatestReplyFull == "已修复登录流程", $"claude: full reply from transcript (got '{claudeSnapshot.LatestReplyFull}')");

// 预览截 120、全文不截 —— 展开后的 REPLY 面板画的是全文，共用截断那份只会显示半句话。
// 这也正是它必须单独存在一个字段的理由（agy 的 latestResponse / fullResponse 是同一对分工）。
var longReplyText = new string('字', 300);
var longReply = ReadTranscript(
[
    "{\"type\":\"user\",\"isSidechain\":false,\"message\":{\"role\":\"user\",\"content\":\"写长一点\"}}",
    "{\"type\":\"assistant\",\"isSidechain\":false,\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"" + longReplyText + "\"}]}}",
]);
Check(longReply.LatestReply?.Length == 120, $"claude: preview stays truncated to 120 (got {longReply.LatestReply?.Length})");
Check(longReply.LatestReplyFull?.Length == 300, $"claude: full reply is not truncated (got {longReply.LatestReplyFull?.Length})");

// 正文全文**必须保留换行**。Normalize 会把 \r\n 换成空格再合并，那是给第三行那种单行预览用的；
// 正文一旦被压平，Markdown 的块级语法就全废 —— 标题、列表、表格**全靠换行划界**。
// 实测（2026-10-15 真机）：表格在手机上"散架"（管道符和横线原样画出来），根因就是这里被压平。
// 这条断言存在的意义：上一版测试喂给解析器的是**带换行的原文**，而真实链路早把换行抽干了，
// 于是测试全绿而线上是坏的 —— 断点必须卡在"读取器吐出来的字符串"上，不是解析器的入参上。
var multiLine = ReadTranscript(
[
    "{\"type\":\"user\",\"isSidechain\":false,\"message\":{\"role\":\"user\",\"content\":\"给我张表\"}}",
    "{\"type\":\"assistant\",\"isSidechain\":false,\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"先说一句。\\n\\n| 甲 | 乙 |\\n|---|---|\\n| 1 | 2 |\"}]}}",
]);
Check(multiLine.LatestReply?.Contains('\n') == false, "claude body: third-line preview is still flattened to one line");
Check(multiLine.LatestReplyFull?.Contains('\n') == true, "claude body: panel body keeps its newlines");
Check(multiLine.LatestReplyFull == "先说一句。\n\n| 甲 | 乙 |\n|---|---|\n| 1 | 2 |",
    $"claude body: newlines survive verbatim (got '{multiLine.LatestReplyFull?.Replace("\n", "\\n")}')");

// TOOL 行（实验）：取本轮最新一条 tool_result，保留换行；遇到新一轮用户输入即封口。
var toolResultSnapshot = ReadTranscript(
[
    "{\"type\":\"user\",\"isSidechain\":false,\"message\":{\"role\":\"user\",\"content\":\"跑一下测试\"}}",
    "{\"type\":\"assistant\",\"isSidechain\":false,\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"tool_use\",\"id\":\"tu_1\",\"name\":\"Bash\",\"input\":{\"command\":\"npm test\"}}]}}",
    "{\"type\":\"user\",\"isSidechain\":false,\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"tu_1\",\"content\":[{\"type\":\"text\",\"text\":\"line1\\nline2\"}]}]}}",
    "{\"type\":\"assistant\",\"isSidechain\":false,\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"测试通过\"}]}}",
]);
Check(toolResultSnapshot.LatestToolOutput == "line1\nline2",
    $"claude tool: latest tool_result keeps newlines (got '{toolResultSnapshot.LatestToolOutput?.Replace("\n", "\\n")}')");
var toolTurnOver = ReadTranscript(
[
    "{\"type\":\"user\",\"isSidechain\":false,\"message\":{\"role\":\"user\",\"content\":\"跑一下测试\"}}",
    "{\"type\":\"user\",\"isSidechain\":false,\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"tu_1\",\"content\":[{\"type\":\"text\",\"text\":\"line1\\nline2\"}]}]}}",
    "{\"type\":\"user\",\"isSidechain\":false,\"message\":{\"role\":\"user\",\"content\":\"新一轮\"}}",
]);
var parallelToolResults = ReadTranscript(
[
    "{\"type\":\"user\",\"isSidechain\":false,\"message\":{\"role\":\"user\",\"content\":\"并行跑两个\"}}",
    "{\"type\":\"user\",\"isSidechain\":false,\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"tu_a\",\"content\":[{\"type\":\"text\",\"text\":\"first\"}]},{\"type\":\"tool_result\",\"tool_use_id\":\"tu_b\",\"content\":\"second\"}]}}",
]);
Check(parallelToolResults.LatestToolOutput == "second",
    $"claude tool: a parallel-result line reports the last result (got '{parallelToolResults.LatestToolOutput}')");
Check(toolTurnOver.LatestToolOutput is null, "claude tool: a new user prompt closes the old tool result");

// opencode：completed part 的 output 落到 LatestToolOutput；新一轮也要清掉。
var ocToolCatalog = new TaskCatalog();
ocToolCatalog.Accept(
    new OpenCodeHookPayload(
        "chat.message",
        JsonSerializer.SerializeToElement(new { sessionID = "ses_tool_out", text = "run tests" }, WireJson.Options),
        null),
    claudeNow);
ocToolCatalog.Accept(
    new OpenCodeHookPayload(
        "message.part.updated",
        JsonSerializer.SerializeToElement(new
        {
            sessionID = "ses_tool_out",
            part = new
            {
                id = "p1",
                tool = "bash",
                state = new { status = "completed", output = "ok\nsecond" },
            },
        }, WireJson.Options),
        null),
    claudeNow.AddSeconds(1));
var ocToolValue = ocToolCatalog.Projection().Tasks.First(t => t.Id == "ses_tool_out").LatestToolOutput;
Check(ocToolValue == "ok\nsecond",
    $"opencode tool: completed part output lands in LatestToolOutput (got '{ocToolValue?.Replace("\n", "\\n") ?? "<null>"}')");
ocToolCatalog.Accept(
    new OpenCodeHookPayload(
        "session.idle",
        JsonSerializer.SerializeToElement(new { sessionID = "ses_tool_out" }, WireJson.Options),
        null),
    claudeNow.AddSeconds(2));
ocToolCatalog.Accept(
    new OpenCodeHookPayload(
        "chat.message",
        JsonSerializer.SerializeToElement(new { sessionID = "ses_tool_out", text = "next" }, WireJson.Options),
        null),
    claudeNow.AddSeconds(3));
Check(ocToolCatalog.Projection().Tasks.First(t => t.Id == "ses_tool_out").LatestToolOutput is null,
    "opencode tool: a new turn clears the previous tool output");

var titled = ClaudeEventReducer.ApplyTranscript(Claude(), claudeSnapshot);
Check(titled.Title == "demo · 修复登录流程", $"claude: title composed as project · title (got '{titled.Title}')");
Check(titled.TokenUsage?.Total == 220 && titled.ModelName == "deepseek-v4.1-flash", "claude: tokens + model applied to task");

// ---- 计时：轮内插入不重置，新一轮才重置；新一轮清第三行（G5/G6）----
claude.Accept(C("PreToolUse", new { tool_name = "Bash", tool_input = new { command = "npm test" } }), claudeNow.AddSeconds(2));
Check(Claude().LatestStep == "Bash npm test", $"claude: step composed (got '{Claude().LatestStep}')");
Check(Claude().Activity == AgentActivity.Testing, $"claude: bash npm test -> testing (got {Claude().Activity})");

claude.Accept(C("UserPromptSubmit", new { prompt = "顺手也看看日志" }), claudeNow.AddSeconds(3));
Check(Claude().StartedAt == claudeNow.AddSeconds(1), "claude: steering prompt keeps the turn timer");
Check(Claude().LatestStep == "Bash npm test", "claude: steering prompt keeps the running step");

claude.Accept(C("Stop", new { last_assistant_message = "跑完了" }), claudeNow.AddSeconds(4));
Check(Claude().Lifecycle == AgentLifecycle.Succeeded && Claude().CompletedAt == claudeNow.AddSeconds(4), "claude: stop -> succeeded");
Check(Claude().LatestStep == "跑完了", $"claude: stop preview from last_assistant_message fallback (got '{Claude().LatestStep}')");

claude.Accept(C("UserPromptSubmit", new { prompt = "再来一轮" }), claudeNow.AddSeconds(5));
Check(Claude().StartedAt == claudeNow.AddSeconds(5), "claude: new turn restarts the timer");
Check(Claude().LatestStep is null, "claude: new turn clears the third line");

// ---- 工具映射：判据是工具名 + 命令文本/入参（G8/G19/G20）----
Check(ClaudeEventReducer.ActivityFor("Bash", JsonSerializer.SerializeToElement(new { command = "gradlew build" })) == AgentActivity.Executing, "claude: gradlew build -> executing");
Check(ClaudeEventReducer.ActivityFor("Bash", JsonSerializer.SerializeToElement(new { command = "Get-Content latest.log" })) == AgentActivity.Executing, "claude: latest substring -> executing");
Check(ClaudeEventReducer.ActivityFor("PowerShell", JsonSerializer.SerializeToElement(new { command = "cargo test" })) == AgentActivity.Testing, "claude: powershell cargo test -> testing");
Check(ClaudeEventReducer.ActivityFor("TodoWrite", JsonSerializer.SerializeToElement(new { todos = new[] { "a" } })) == AgentActivity.Thinking, "claude: TodoWrite -> thinking (not editing)");
Check(ClaudeEventReducer.ActivityFor("Glob", JsonSerializer.SerializeToElement(new { pattern = "**/*.md" })) == AgentActivity.Searching, "claude: Glob -> searching");
Check(ClaudeEventReducer.ActivityFor("NotebookEdit", null) == AgentActivity.Editing, "claude: NotebookEdit -> editing");
Check(ClaudeEventReducer.ActivityFor("Agent", JsonSerializer.SerializeToElement(new { description = "调研" })) == AgentActivity.Delegating, "claude: Agent -> delegating");
Check(ClaudeEventReducer.ActivityFor("Read", null) == AgentActivity.Reading && ClaudeEventReducer.ActivityFor("WebSearch", null) == AgentActivity.Browsing, "claude: read/web mapped");
Check(CodexEventReducer.LatestStep("TodoWrite", "x") == "Todo x" &&
      CodexEventReducer.LatestStep("PowerShell", "git status") == "PowerShell git status" &&
      CodexEventReducer.LatestStep("AskUserQuestion", "选哪个") == "Question 选哪个",
    "claude: tool display labels");

// 白名单不含 prompt：Agent 一类入参带 prompt 也不得进第三行（G18）
Check(ClaudeEventReducer.Summary(JsonSerializer.SerializeToElement(new { prompt = "整段用户提示词", description = "调研用法" })) == "调研用法", "claude: summary skips prompt key");

// ---- 提问态：认 AskUserQuestion 工具名（G7）----
var askInput = JsonSerializer.SerializeToElement(new
{
    questions = new[] { new { question = "选哪个方案？", header = "方案", options = new[] { new { label = "A" }, new { label = "B" } } } },
});
Check(ClaudeEventReducer.QuestionText(askInput) == "选哪个方案？" && ClaudeEventReducer.QuestionHeader(askInput) == "方案", "claude: question text/header");
Check(ClaudeEventReducer.QuestionOptions(askInput) is ["A", "B"], "claude: question options");

// 一次提问可带多题，而手机只渲染一行 summary：必须**全部拼进来**并标号
// （实测踩过：一次问两题，手机上只看得见第一题，用户也就只答了第一题）
var multiAsk = JsonSerializer.SerializeToElement(new
{
    questions = new[]
    {
        new { question = "第一题？", header = "题一", options = new[] { new { label = "A" }, new { label = "B" } } },
        new { question = "第二题？", header = "题二", options = new[] { new { label = "C" }, new { label = "D" } } },
    },
});
Check(ClaudeEventReducer.QuestionText(multiAsk) == "1. 第一题？ / 2. 第二题？",
    $"claude: multi-question text keeps every question (got '{ClaudeEventReducer.QuestionText(multiAsk)}')");
Check(ClaudeEventReducer.QuestionHeader(multiAsk) == "题一（共 2 题）",
    $"claude: multi-question header shows the count (got '{ClaudeEventReducer.QuestionHeader(multiAsk)}')");
Check(ClaudeEventReducer.QuestionOptions(multiAsk) is [],
    "claude: multi-question sends no options (phone must not offer tap-to-answer on an ambiguous card)");

claude.Accept(C("PreToolUse", new { tool_name = "AskUserQuestion", tool_input = askInput }), claudeNow.AddSeconds(6));
Check(Claude().Lifecycle == AgentLifecycle.WaitingAnswer, $"claude: AskUserQuestion -> waitingAnswer (got {Claude().Lifecycle})");
Check(Claude().LatestStep == "方案", $"claude: question header on third line (got '{Claude().LatestStep}')");
var questionCard = claude.Projection().PendingRequests.FirstOrDefault(r => r.TaskID == "claude_1");
Check(questionCard is { Kind: PendingRequestKind.Question, Summary: "选哪个方案？" }, "claude: question card registered");

// 等待态锁：并行工具事件不得把等待态压回 running
claude.Accept(C("PreToolUse", new { tool_name = "Bash", tool_input = new { command = "echo x" } }), claudeNow.AddSeconds(7));
Check(Claude().Lifecycle == AgentLifecycle.WaitingAnswer, "claude: parallel tool during waiting ignored");
claude.Accept(C("PostToolUse", new { tool_name = "AskUserQuestion" }), claudeNow.AddSeconds(8));
Check(Claude().Lifecycle == AgentLifecycle.Running, "claude: question answered -> running");
Check(claude.Projection().PendingRequests.Count == 0, "claude: question card cleared after answering");

// AskUserQuestion 是"需要用户交互"的工具，**它同时会发 PermissionRequest**。两条都要收敛到
// "提问"：不能变成"等待批准 + 允许/拒绝按钮"（选择题用那个答不了），也不能把 QUESTION 卡片顶掉。
claude.Accept(C("PreToolUse", new { tool_name = "AskUserQuestion", tool_input = askInput }), claudeNow.AddSeconds(50));
claude.Accept(C("PermissionRequest", new { tool_name = "AskUserQuestion", tool_input = askInput }), claudeNow.AddSeconds(51));
Check(Claude().Lifecycle == AgentLifecycle.WaitingAnswer, $"claude: question tool's permission request stays waitingAnswer (got {Claude().Lifecycle})");
var questionAfterPermission = claude.Projection().PendingRequests.First();
Check(questionAfterPermission.Kind == PendingRequestKind.Question && questionAfterPermission.Summary == "选哪个方案？",
    "claude: question card survives its own permission request");
Check(Claude().Capabilities.Contains(TaskCapability.Answer) && !Claude().Capabilities.Contains(TaskCapability.Approve),
    "claude: question offers Answer only (no approve/deny buttons)");
Check(Claude().LatestStep == "方案", $"claude: question header kept on the third line (got '{Claude().LatestStep}')");
claude.Accept(C("PostToolUse", new { tool_name = "AskUserQuestion" }), claudeNow.AddSeconds(52));
Check(Claude().Lifecycle == AgentLifecycle.Running && claude.Projection().PendingRequests.Count == 0,
    "claude: question released by its own PostToolUse");

// 反向顺序（PermissionRequest 先到，没有 PreToolUse）同样要进提问态
claude.Accept(C("PermissionRequest", new { tool_name = "AskUserQuestion", tool_input = askInput }), claudeNow.AddSeconds(53));
Check(Claude().Lifecycle == AgentLifecycle.WaitingAnswer &&
      claude.Projection().PendingRequests.First().Kind == PendingRequestKind.Question,
    "claude: question permission request alone enters waitingAnswer");
claude.Accept(C("PostToolUse", new { tool_name = "AskUserQuestion" }), claudeNow.AddSeconds(54));

// ---- 空闲通知不是提问（用户实测："app 上有个 question …但没人发起过 question"）----
// idle_prompt 的发射条件是"会话空闲超过阈值"，文案就是 "Claude is waiting for your input"——
// 它只是终端空着，不是提问；旧实现把它当提问，于是每次空闲都弹一张没有选项的问题卡。
var idleNotif = new TaskCatalog();
idleNotif.Accept(C("UserPromptSubmit", new { session_id = "idle_notif", prompt = "跑一下" }), claudeNow);
idleNotif.Accept(C("Notification", new { session_id = "idle_notif", notification_type = "idle_prompt", message = "Claude is waiting for your input" }), claudeNow.AddSeconds(1));
TaskSnapshot IdleNotifTask() => idleNotif.Projection().Tasks.First(t => t.Id == "idle_notif");
Check(idleNotif.Projection().PendingRequests.Count == 0, "claude: idle_prompt registers no question card");
Check(IdleNotifTask().Lifecycle == AgentLifecycle.Running,
    $"claude: idle_prompt leaves the lifecycle alone (got {IdleNotifTask().Lifecycle})");
// 真正的输入弹窗仍旧是问题卡（回归守卫）
idleNotif.Accept(C("Notification", new { session_id = "idle_notif", notification_type = "elicitation_dialog", message = "Claude Code needs your input" }), claudeNow.AddSeconds(2));
var elicitationCard = idleNotif.Projection().PendingRequests.FirstOrDefault(r => r.TaskID == "idle_notif");
Check(elicitationCard is { Kind: PendingRequestKind.Question, Summary: "Claude Code needs your input" },
    $"claude: elicitation_dialog still gets a question card (got '{elicitationCard?.Summary}')");
Check(IdleNotifTask().Lifecycle == AgentLifecycle.WaitingAnswer,
    $"claude: elicitation_dialog enters waitingAnswer (got {IdleNotifTask().Lifecycle})");

// 普通工具的权限询问仍然是审批卡片（回归守卫）
claude.Accept(C("PermissionRequest", new { tool_name = "Bash", tool_input = new { command = "rm -rf x" } }), claudeNow.AddSeconds(55));
Check(Claude().Lifecycle == AgentLifecycle.WaitingApproval &&
      claude.Projection().PendingRequests.First().Kind == PendingRequestKind.Approval &&
      Claude().Capabilities.Contains(TaskCapability.Approve),
    "claude: ordinary tool still gets an approval card");
claude.Accept(C("PostToolUse", new { tool_name = "Bash" }), claudeNow.AddSeconds(56));

// ---- 权限：唯一双向能力（Capabilities 决定手机按钮）----
claude.Accept(C("PermissionRequest", new { tool_name = "Bash", tool_input = new { command = "rm -rf build" } }), claudeNow.AddSeconds(9));
Check(Claude().Lifecycle == AgentLifecycle.WaitingApproval, $"claude: permission request -> waitingApproval (got {Claude().Lifecycle})");
Check(Claude().Capabilities.Contains(TaskCapability.Approve) && Claude().Capabilities.Contains(TaskCapability.Deny), "claude: approve/deny capabilities");
Check(claude.Projection().PendingRequests.First().Summary == "rm -rf build", "claude: approval card summary");
claude.Accept(C("PostToolUse", new { tool_name = "Bash" }), claudeNow.AddSeconds(10));
Check(Claude().Lifecycle == AgentLifecycle.Running && claude.Projection().PendingRequests.Count == 0, "claude: answered-in-terminal permission released by PostToolUse");

// ---- 终态语义：PermissionDenied 不是回合结束；StopFailure 是（G13/G15）----
claude.Accept(C("PermissionDenied", new { tool_name = "Bash", tool_input = new { command = "curl x" }, reason = "auto" }), claudeNow.AddSeconds(11));
Check(Claude().Lifecycle == AgentLifecycle.Running, $"claude: PermissionDenied keeps running (got {Claude().Lifecycle})");
Check(Claude().LatestStep == "✗ 已拒绝 curl x", $"claude: denial step (got '{Claude().LatestStep}')");
Check(Claude().CompletedAt is null, "claude: denial does not complete the turn");

claude.Accept(C("StopFailure", new { error = "rate_limit" }), claudeNow.AddSeconds(12));
Check(Claude().Lifecycle == AgentLifecycle.Interrupted, $"claude: stop failure -> interrupted (got {Claude().Lifecycle})");
Check(Claude().LatestStep == "✗ rate_limit" && Claude().IsUnread, "claude: stop failure step + unread");

// ---- 守卫：终态不可逆（G14/G16/G17）----
claude.Accept(C("PreCompact", new { trigger = "auto" }), claudeNow.AddSeconds(13));
Check(Claude().Lifecycle == AgentLifecycle.Interrupted, "claude: precompact never revives a terminal task");
claude.Accept(C("PostToolUse", new { tool_name = "Bash" }), claudeNow.AddSeconds(14));
Check(Claude().Lifecycle == AgentLifecycle.Interrupted, "claude: late tool after terminal does not revive");
claude.Accept(C("Stop", new { }), claudeNow.AddSeconds(15));
Check(Claude().Lifecycle == AgentLifecycle.Interrupted, "claude: stop keeps interrupted (esc path)");

// ---- 中断信号：PostToolUseFailure.is_interrupt（G12）----
var interrupted = ClaudeEventReducer.Reduce(
    C("PostToolUseFailure", new { tool_name = "Bash", error = "Interrupted", is_interrupt = true }),
    Claude() with { Lifecycle = AgentLifecycle.Running, CompletedAt = null }, null, claudeNow.AddSeconds(16));
Check(interrupted.Lifecycle == AgentLifecycle.Interrupted, $"claude: is_interrupt -> interrupted (got {interrupted.Lifecycle})");

var failed = ClaudeEventReducer.Reduce(
    C("PostToolUseFailure", new { tool_name = "Bash", error = "boom\nat line 2" }),
    Claude() with { Lifecycle = AgentLifecycle.Running, CompletedAt = null }, null, claudeNow.AddSeconds(17));
Check(failed.Lifecycle == AgentLifecycle.Running && failed.LatestStep == "✗ boom", $"claude: tool failure keeps running (got {failed.Lifecycle}/{failed.LatestStep})");

// ---- 正文预览的回显守卫（G18）----
var echoTask = Claude() with
{
    Lifecycle = AgentLifecycle.Running,
    UserPrompt = "帮我把构建脚本修一下",
    LatestStep = "Bash npm test",
    CompletedAt = null,
};
var echoed = ClaudeEventReducer.Reduce(
    C("Stop", new { last_assistant_message = "好的，帮我把构建脚本修一下已完成" }), echoTask, null, claudeNow.AddSeconds(18));
Check(echoed.LatestStep == "Bash npm test", $"claude: prompt echo rejected from preview (got '{echoed.LatestStep}')");

// ---- 子代理：路由进父任务、父任务终态不被复活（G11/G14）----
var subCatalog = new TaskCatalog();
var subNow = DateTimeOffset.UtcNow;
TaskSnapshot SubTask() => subCatalog.Projection().Tasks.First(t => t.Id == "claude_2");
subCatalog.Accept(C("UserPromptSubmit", new { session_id = "claude_2", prompt = "调研一下" }), subNow.AddSeconds(1));
subCatalog.Accept(C("PreToolUse", new { session_id = "claude_2", tool_name = "Bash", tool_input = new { command = "npm test" } }), subNow.AddSeconds(2));
var parentStep = SubTask().LatestStep;
Check(parentStep == "Bash npm test", $"claude: parent step before delegating (got '{parentStep}')");

subCatalog.Accept(C("SubagentStart", new { session_id = "claude_2", agent_id = "a1", agent_type = "Explore" }), subNow.AddSeconds(3));
subCatalog.Accept(C("PreToolUse", new { session_id = "claude_2", agent_id = "a1", agent_type = "Explore", tool_name = "Grep", tool_input = new { pattern = "TODO" } }), subNow.AddSeconds(4));
Check(SubTask().LatestStep == parentStep, "claude: subagent step never overwrites the parent third line");
Check(SubTask().Subagents is [{ DisplayName: "Explore" }], "claude: subagent row named by agent_type");
Check(SubTask().Subagents[0].LatestStep == "Grep TODO", $"claude: subagent row carries its own step (got '{SubTask().Subagents[0].LatestStep}')");
Check(SubTask().Activity == AgentActivity.Delegating, "claude: parent delegates while subagent runs");

// 项目名**跟随** payload cwd（用户裁定：Claude 的 cwd 随 Bash 的 cd 走，这一行反映"此刻在哪个
// 目录干活"，是期望行为）。曾经按"另两家用稳定目录"冻结过，被用户撤销——这条断言防止再冻回去。
var cwdCatalog = new TaskCatalog();
var cwdNow = DateTimeOffset.UtcNow;
cwdCatalog.Accept(C("UserPromptSubmit", new { session_id = "claude_4", cwd = @"F:\Git\proj-a", prompt = "干活" }), cwdNow.AddSeconds(1));
cwdCatalog.Accept(C("PreToolUse", new { session_id = "claude_4", cwd = @"F:\Git\proj-a\sub\deep", tool_name = "Bash", tool_input = new { command = "ls" } }), cwdNow.AddSeconds(2));
TaskSnapshot CwdTask() => cwdCatalog.Projection().Tasks.First(x => x.Id == "claude_4");
Check(CwdTask().ProjectName == "deep", $"claude: project name follows payload cwd (got '{CwdTask().ProjectName}')");

// 没有 Start 的 Stop 不得凭空造幽灵子代理行（实测踩过：手机上突然冒出一行"已完成"的子代理，
// 那条 Stop 的 agent_type 是空的）
var ghostCatalog = new TaskCatalog();
var ghostNow = DateTimeOffset.UtcNow;
ghostCatalog.Accept(C("UserPromptSubmit", new { session_id = "claude_3", prompt = "跑一下" }), ghostNow);
ghostCatalog.Accept(C("SubagentStop", new { session_id = "claude_3", agent_id = "ghost" }), ghostNow.AddSeconds(1));
Check(ghostCatalog.Projection().Tasks.First(x => x.Id == "claude_3").Subagents.Count == 0,
    "claude: lone SubagentStop does not fabricate a ghost row");

subCatalog.Accept(C("Stop", new { session_id = "claude_2", last_assistant_message = "完成了" }), subNow.AddSeconds(5));
Check(SubTask().Lifecycle == AgentLifecycle.Succeeded, "claude: parent finished while subagent row still open");
subCatalog.Accept(C("SubagentStop", new { session_id = "claude_2", agent_id = "a1", agent_type = "Explore" }), subNow.AddSeconds(6));
Check(SubTask().Lifecycle == AgentLifecycle.Succeeded, "claude: subagent stop never revives a finished parent");
Check(SubTask().Subagents[0].IsTerminal, "claude: subagent row finalized");

// 子代理的权限询问挂父任务（它阻塞的是父任务这一回合，手机要能放行）
subCatalog.Accept(C("PermissionRequest", new { session_id = "claude_2", agent_id = "a1", agent_type = "Explore", tool_name = "Bash", tool_input = new { command = "rm -rf x" } }), subNow.AddSeconds(7));
Check(SubTask().Lifecycle == AgentLifecycle.Succeeded, "claude: finished parent is not revived by a subagent permission request");
Check(subCatalog.Projection().PendingRequests.Count == 0, "claude: no phantom card on a finished parent");

// ---- 排队：转录本 queue-operation（G1'''）----
var turnStart = DateTimeOffset.UtcNow;
var queueSnapshot = ReadTranscript(
[
    "{\"type\":\"queue-operation\",\"operation\":\"enqueue\",\"timestamp\":\"" + turnStart.AddSeconds(30).ToString("o") + "\",\"content\":\"<task-notification>后台任务完成\"}",
    "{\"type\":\"queue-operation\",\"operation\":\"enqueue\",\"timestamp\":\"" + turnStart.AddSeconds(40).ToString("o") + "\",\"content\":\"再跑一遍测试\"}",
]);
Check(queueSnapshot.QueuedPrompt == "再跑一遍测试" && queueSnapshot.QueuedAt is not null, $"claude: queued user prompt read (got '{queueSnapshot.QueuedPrompt}')");

var queued = ClaudeEventReducer.ApplyTranscript(
    Claude() with { Lifecycle = AgentLifecycle.Running, StartedAt = turnStart, UserPrompt = "原提示词", PromptQueued = false },
    queueSnapshot);
Check(queued.PromptQueued && queued.UserPrompt == "再跑一遍测试", "claude: queued prompt marked + shown on second line");

var drained = ClaudeEventReducer.ApplyTranscript(
    Claude() with { Lifecycle = AgentLifecycle.Running, StartedAt = turnStart, PromptQueued = true },
    ReadTranscript(["{\"type\":\"queue-operation\",\"operation\":\"remove\",\"content\":\"再跑一遍测试\"}"]));
Check(!drained.PromptQueued, "claude: queue drained clears the queued flag");

// 本地斜杠命令不是"用户在排队提交提示词"（实测踩过：/context 只写 enqueue、不写 remove，
// 按"最后一条操作说了算"会永远停在排队中，并把 /context 顶到第二行）
var slashSnapshot = ReadTranscript(["{\"type\":\"queue-operation\",\"operation\":\"enqueue\",\"content\":\"/context\"}"]);
Check(slashSnapshot.QueuedAt is null && slashSnapshot.QueuedPrompt is null, "claude: slash command is not a queued prompt");

// 排空后必须还原成本回合真实输入，不能让排队文本永久冒充提示词
var restoreSnapshot = ReadTranscript(
[
    "{\"type\":\"user\",\"isSidechain\":false,\"message\":{\"role\":\"user\",\"content\":\"本轮真正的提示词\"}}",
    "{\"type\":\"assistant\",\"isSidechain\":false,\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"收到\"}],\"usage\":{\"input_tokens\":5,\"output_tokens\":1}}}",
    "{\"type\":\"queue-operation\",\"operation\":\"remove\",\"content\":\"中途排队的那条\"}",
]);
Check(restoreSnapshot.LatestPrompt == "本轮真正的提示词", $"claude: latest prompt read from transcript (got '{restoreSnapshot.LatestPrompt}')");
var restored = ClaudeEventReducer.ApplyTranscript(
    Claude() with { Lifecycle = AgentLifecycle.Running, PromptQueued = true, UserPrompt = "中途排队的那条" },
    restoreSnapshot);
Check(!restored.PromptQueued && restored.UserPrompt == "本轮真正的提示词",
    $"claude: drained queue restores the real prompt (got '{restored.UserPrompt}')");

// 复现 0fb27147：任何投递（管道注入/CLI）都在转录本里留下 enqueue+dequeue 对，dequeue 行
// **没有 content 字段**——旧模型跳过它、倒扫到 enqueue，把"已跑完"读成"仍在排队"，第二行
// 被注入文本永久冒充（实测踩过）。成对抵消后必须判"没有排队"，且不得再触发排队还原。
var pairNow = DateTimeOffset.UtcNow;
var pipePair = ReadTranscript(
[
    "{\"type\":\"queue-operation\",\"operation\":\"enqueue\",\"timestamp\":\"" + pairNow.AddMilliseconds(1).ToString("o") + "\",\"content\":\"注入测试\"}",
    "{\"type\":\"queue-operation\",\"operation\":\"dequeue\",\"timestamp\":\"" + pairNow.AddMilliseconds(8).ToString("o") + "\"}",
]);
Check(pipePair.QueuedAt is null && pipePair.QueuedPrompt is null,
    $"claude: enqueue+dequeue pair reads as drained (got '{pipePair.QueuedPrompt}')");
var afterPair = ClaudeEventReducer.ApplyTranscript(
    Claude() with { StartedAt = pairNow, PromptQueued = true, UserPrompt = "注入测试" },
    pipePair);
Check(!afterPair.PromptQueued, "claude: drained pair never leaves the queued flag stuck");

// 两条消息先后排空（两对 enqueue/dequeue 交错）：全抵消后同样判"没有排队"
var twoPairs = ReadTranscript(
[
    "{\"type\":\"queue-operation\",\"operation\":\"enqueue\",\"timestamp\":\"" + pairNow.AddSeconds(1).ToString("o") + "\",\"content\":\"第一条\"}",
    "{\"type\":\"queue-operation\",\"operation\":\"dequeue\",\"timestamp\":\"" + pairNow.AddSeconds(2).ToString("o") + "\"}",
    "{\"type\":\"queue-operation\",\"operation\":\"enqueue\",\"timestamp\":\"" + pairNow.AddSeconds(3).ToString("o") + "\",\"content\":\"第二条\"}",
    "{\"type\":\"queue-operation\",\"operation\":\"dequeue\",\"timestamp\":\"" + pairNow.AddSeconds(4).ToString("o") + "\"}",
]);
Check(twoPairs.QueuedAt is null && twoPairs.QueuedPrompt is null, "claude: two drained pairs cancel out");

// 抵账只吃掉配对取走的旧 enqueue：后面新排的那条仍算"在队里"（正向对照）
var stillQueued = ReadTranscript(
[
    "{\"type\":\"queue-operation\",\"operation\":\"enqueue\",\"timestamp\":\"" + pairNow.AddSeconds(5).ToString("o") + "\",\"content\":\"旧条\"}",
    "{\"type\":\"queue-operation\",\"operation\":\"dequeue\",\"timestamp\":\"" + pairNow.AddSeconds(6).ToString("o") + "\"}",
    "{\"type\":\"queue-operation\",\"operation\":\"enqueue\",\"timestamp\":\"" + pairNow.AddSeconds(7).ToString("o") + "\",\"content\":\"新条\"}",
]);
Check(stillQueued.QueuedPrompt == "新条" && stillQueued.QueuedAt is not null,
    $"claude: unmatched newest enqueue stays queued (got '{stillQueued.QueuedPrompt}')");

// 裸 remove（无 content）与 popAll 都是排空信号：旧模型对前者完全瞎（remove 行无 content 被跳过）
var bareRemove = ReadTranscript(
[
    "{\"type\":\"queue-operation\",\"operation\":\"enqueue\",\"content\":\"裸移除\"}",
    "{\"type\":\"queue-operation\",\"operation\":\"remove\"}",
]);
Check(bareRemove.QueuedPrompt is null && bareRemove.QueuedAt is null, "claude: bare remove drains the queue");
var popAll = ReadTranscript(
[
    "{\"type\":\"queue-operation\",\"operation\":\"enqueue\",\"content\":\"被清空\"}",
    "{\"type\":\"queue-operation\",\"operation\":\"popAll\"}",
]);
Check(popAll.QueuedPrompt is null && popAll.QueuedAt is null, "claude: popAll drains the queue");

// 斜杠命令自己的 enqueue/dequeue 对同样抵账：不能因它停在"排队中"，也不能冒名顶到第二行
var slashPair = ReadTranscript(
[
    "{\"type\":\"queue-operation\",\"operation\":\"enqueue\",\"content\":\"/context\"}",
    "{\"type\":\"queue-operation\",\"operation\":\"dequeue\"}",
]);
Check(slashPair.QueuedPrompt is null && slashPair.QueuedAt is null,
    "claude: slash enqueue+dequeue pair reads as drained");

// ---- 离线：10 分钟静默兜底（G23）----
claude.Accept(C("UserPromptSubmit", new { prompt = "长跑任务" }), claudeNow.AddSeconds(22));
claude.Maintain(claudeNow.AddMinutes(2));
Check(Claude().Lifecycle == AgentLifecycle.Running, "claude: still running before the silence window");
claude.Maintain(claudeNow.AddMinutes(12));
Check(Claude().Lifecycle == AgentLifecycle.Offline, $"claude: silent task -> offline (got {Claude().Lifecycle})");

// 会话注册表解析（离线正判的来源）
var registryDir = Path.Combine(Path.GetTempPath(), "ap-verify-claude-reg-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(registryDir);
File.WriteAllText(Path.Combine(registryDir, "1234.json"),
    "{\"pid\":1234,\"sessionId\":\"claude_9\",\"cwd\":\"F:\\\\Git\",\"name\":\"git-90\",\"nameSource\":\"derived\",\"status\":\"busy\",\"startedAt\":1791437904395}");
var registry = ClaudeSessionRegistry.Scan(registryDir);
Check(registry.TryGetValue("claude_9", out var entry) && entry.Status == "busy" && entry.NameSource == "derived", "claude: session registry entry parsed");
Directory.Delete(registryDir, true);

// ===================== 20. Claude Hook 安装器（隔离在临时配置目录，不碰真实 ~/.claude） =====================
var claudeHome = Path.Combine(Path.GetTempPath(), "ap-verify-claude-home-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(claudeHome);
// 预置：用户自己的 hook + 一份**旧形态**托管项（"<exe>" --hook --source claude）
File.WriteAllText(Path.Combine(claudeHome, "settings.json"),
    """
    {"hooks":{"Stop":[{"hooks":[{"type":"command","command":"node \"C:/user/own.js\""}]},{"hooks":[{"type":"command","command":"\"C:/old/AgentPagerBridge.exe\" --hook --source claude","statusMessage":"Managed by AgentPager (Claude Code)"}]}]}}
    """);

var claudeHooks = new ClaudeHookConfiguration(claudeHome);
Check(ClaudeHookConfiguration.EmbeddedVersion == 2, $"claude: embedded script version (got {ClaudeHookConfiguration.EmbeddedVersion})");
Check(!claudeHooks.IsInstalled(), "claude: not installed before install");

var install = claudeHooks.Install();
Check(install.Changed, "claude: first install reports a change");
Check(install.BackupPath is not null && File.Exists(install.BackupPath), "claude: settings backed up before rewrite");
Check(File.Exists(claudeHooks.ScriptPath), "claude: forwarding script deployed");
Check(claudeHooks.InstalledVersion == ClaudeHookConfiguration.EmbeddedVersion,
    "claude: deployed script version matches the embedded one");
Check(claudeHooks.IsInstalled(), "claude: installed state");

var installedSettings = JsonNode.Parse(File.ReadAllText(claudeHooks.SettingsPath))!.AsObject();
var installedHooks = installedSettings["hooks"]!.AsObject();
Check(installedHooks["UserPromptSubmit"] is JsonArray, "claude: lifecycle events registered");
// 挂起上限三家统一为 10 分钟（桥侧 HoldLimit 同值 + opencode 插件的 HOLD_TIMEOUT_MS 同值）：
// 到点把询问交回终端，不再让 hook 挂满 24 小时 ✗。
Check(installedHooks["PermissionRequest"]!.AsArray()[0]!["hooks"]!.AsArray()[0]!["timeout"]!.GetValue<int>() == 600,
    "claude: permission hook times out with the shared 10-minute hold limit");
var stopGroups = installedHooks["Stop"]!.AsArray();
Check(stopGroups.Count == 2, $"claude: user hook preserved alongside ours (got {stopGroups.Count})");
Check(stopGroups.Any(g => g!["hooks"]!.AsArray().Any(h =>
        h!["command"]?.GetValue<string>()?.Contains("own.js", StringComparison.Ordinal) == true)),
    "claude: user's own Stop hook untouched");

var ours = installedHooks["UserPromptSubmit"]!.AsArray()[0]!["hooks"]!.AsArray()[0]!.AsObject();
Check(ours["command"]!.GetValue<string>() == "node", $"claude: exec form uses bare node (got '{ours["command"]}')");
Check(ours["args"]!.AsArray()[0]!.GetValue<string>().Replace('\\', '/').EndsWith("agentpager-claude.js", StringComparison.Ordinal),
    "claude: exec form passes the script path as an argument");
Check(!installedSettings.ToJsonString().Contains("--hook --source claude", StringComparison.Ordinal),
    "claude: legacy exe-form hooks replaced");
Check(installedSettings["crossSessionInbound"]?.GetValue<string>() == "accept",
    "claude: crossSessionInbound is configured to accept");

Check(!claudeHooks.Install().Changed, "claude: install is idempotent");

// 三态：脚本落后于内置版本时必须是"待更新"，而不是"未安装"（bool 表达不了这一态）
Check(claudeHooks.Status == ClaudeHookStatus.Installed, $"claude: fresh install reads as installed (got {claudeHooks.Status})");
File.WriteAllText(claudeHooks.ScriptPath, "// agentpager-claude v0\r\n// Managed by AgentPager (Claude Code) - do not edit this file manually.\r\n");
Check(claudeHooks.Status == ClaudeHookStatus.Outdated, $"claude: stale script reads as outdated (got {claudeHooks.Status})");
Check(claudeHooks.IsInstalled(), "claude: outdated still counts as installed (hooks are registered)");
Check(claudeHooks.InstalledVersion == 0, $"claude: installed version read from the script header (got {claudeHooks.InstalledVersion})");
Check(claudeHooks.Install().Changed, "claude: reinstall repairs the stale script");
Check(claudeHooks.Status == ClaudeHookStatus.Installed, "claude: repaired back to installed");

// 缺少 crossSessionInbound 配置也视作待更新
var noInboundSettings = JsonNode.Parse(File.ReadAllText(claudeHooks.SettingsPath))!.AsObject();
noInboundSettings.Remove("crossSessionInbound");
File.WriteAllText(claudeHooks.SettingsPath, noInboundSettings.ToJsonString());
Check(claudeHooks.Status == ClaudeHookStatus.Outdated, "claude: missing crossSessionInbound reads as outdated");
Check(claudeHooks.Install().Changed, "claude: reinstall repairs missing crossSessionInbound");
Check(claudeHooks.Status == ClaudeHookStatus.Installed, "claude: repaired back to installed with crossSessionInbound");

var uninstall = claudeHooks.Uninstall();
Check(uninstall.Changed, "claude: uninstall reports a change");
Check(!File.Exists(claudeHooks.ScriptPath), "claude: uninstall removes the managed script");
Check(!claudeHooks.IsInstalled(), "claude: uninstalled state");
var afterUninstall = JsonNode.Parse(File.ReadAllText(claudeHooks.SettingsPath))!.AsObject()["hooks"]!.AsObject();
Check(afterUninstall["UserPromptSubmit"] is null, "claude: managed event groups removed");
Check(afterUninstall["Stop"]!.AsArray().Count == 1, "claude: user hook survives uninstall");
Directory.Delete(claudeHome, true);

// ===================== 21. 手机作答（PermissionRequest + updatedInput.response） =====================
// 回包形状是整个机制的关键：必须 behavior=allow、必须整份带回 questions、答案放 response。
var answerBytes = ClaudeHookOutput.Answer(
    """{"questions":[{"question":"用哪种实现？","header":"实现","options":[{"label":"A"},{"label":"B"}]}]}""",
    "就用 A");
var answerRoot = JsonNode.Parse(System.Text.Encoding.UTF8.GetString(answerBytes).Trim())!.AsObject();
Check(answerRoot["hookSpecificOutput"]!["hookEventName"]!.GetValue<string>() == "PermissionRequest",
    "claude answer: hookEventName is PermissionRequest");
var answerDecision = answerRoot["hookSpecificOutput"]!["decision"]!.AsObject();
Check(answerDecision["behavior"]!.GetValue<string>() == "allow", "claude answer: behavior is allow");
var answerUpdated = answerDecision["updatedInput"]!.AsObject();
Check(answerUpdated["questions"] is JsonArray { Count: 1 }, "claude answer: original questions echoed back whole");
Check(answerUpdated["questions"]![0]!["question"]!.GetValue<string>() == "用哪种实现？", "claude answer: question text preserved");
Check(answerUpdated["response"]!.GetValue<string>() == "就用 A", "claude answer: free text rides on response");

// 点选项（手机按钮）→ 结构化 answers{问题全文: label}，不再落 response
var labelBytes = ClaudeHookOutput.Answer(
    """{"questions":[{"question":"用哪种实现？","header":"实现","options":[{"label":"A"},{"label":"B"}]}]}""",
    "B");
var labelInput = JsonNode.Parse(System.Text.Encoding.UTF8.GetString(labelBytes).Trim())!
    .AsObject()["hookSpecificOutput"]!["decision"]!["updatedInput"]!.AsObject();
Check(labelInput["answers"] is JsonObject answers &&
      answers["用哪种实现？"]!.GetValue<string>() == "B",
    "claude answer: a tapped option becomes a structured answer keyed by the question text");
Check(labelInput["response"] is null, "claude answer: a tapped option does not also set response");
Check(labelInput["questions"] is JsonArray { Count: 1 }, "claude answer: questions still echoed when an option is tapped");

// 端到端：PermissionRequest(提问工具) → 手机作答 → 回填原始入参
var answerCatalog = new TaskCatalog();
var answerNow = DateTimeOffset.UtcNow;
answerCatalog.Accept(C("PermissionRequest", new { session_id = "claude_7", tool_name = "AskUserQuestion", tool_input = askInput }), answerNow);
TaskSnapshot AnswerTask() => answerCatalog.Projection().Tasks.First(x => x.Id == "claude_7");
Check(AnswerTask().Capabilities.Contains(TaskCapability.Answer), "claude answer: asking grants the Answer capability");
Check(!AnswerTask().Capabilities.Contains(TaskCapability.Approve), "claude answer: no approve/deny on a question");
Check(answerCatalog.Projection().PendingRequests.First().Kind == PendingRequestKind.Question, "claude answer: question card pending");

var recorder = new RecordingResolver();
var answerAck = answerCatalog.Perform(
    Guid.NewGuid(), new ControlPayload("claude_7", ControlAction.Answer, "就用 A"), recorder);
Check(answerAck.Result == ControlResult.Accepted, $"claude answer: phone answer accepted (got {answerAck.Result}/{answerAck.Reason})");
Check(recorder.Written is [{ Labels.Count: 0, Text: "就用 A" }], "claude answer: the free text reaches the resolver as text (not as a label)");
Check(JsonNode.Parse(recorder.RawInput!)!["questions"] is JsonArray { Count: 1 }, "claude answer: raw tool input passed back whole");
Check(!AnswerTask().Capabilities.Contains(TaskCapability.Answer) && AnswerTask().Lifecycle == AgentLifecycle.Running,
    "claude answer: capability cleared and the turn resumes");
Check(answerCatalog.Projection().PendingRequests.Count == 0, "claude answer: question card cleared");

// 没有原始入参时不给 Answer 能力位（免得 App 画出送不出去的输入框）
var noInputCatalog = new TaskCatalog();
noInputCatalog.Accept(C("PermissionRequest", new { session_id = "claude_8", tool_name = "AskUserQuestion" }), answerNow);
Check(!noInputCatalog.Projection().Tasks.First(x => x.Id == "claude_8").Capabilities.Contains(TaskCapability.Answer),
    "claude answer: no raw input -> no Answer capability");

// ===================== 22. 心跳只喂正文面板，不碰第三行（B） =====================
// 2026-10-15 用户裁定：第三行的语义是"最新一步"，**中途正文不进第三行**。
//
// 这里原本有一条"没有工具在跑时把中间正文落进第三行"的路径，已撤掉。理由：Claude 把正文和
// 工具调用写成**相隔约 2ms 的两行**（实测 15:36:30.048 `[text]` → .050 `[tool_use]`），
// 于是"最新一行是正文"这个状态几乎不存在 —— 那条路径实际上永远送不出去，留着只会让人
// 以为它在工作。撤掉之后语义反而干净：第三行只认工具步，中途正文只走展开后的 REPLY 面板。
var replyCatalog = new TaskCatalog();
var replyNow = DateTimeOffset.UtcNow;
var replyPath = Path.Combine(Path.GetTempPath(), "ap-verify-claude-reply-" + Guid.NewGuid().ToString("N") + ".jsonl");
File.WriteAllLines(replyPath,
    ["{\"type\":\"user\",\"isSidechain\":false,\"message\":{\"role\":\"user\",\"content\":\"干活\"}}"]);
replyCatalog.Accept(C("UserPromptSubmit", new { session_id = "claude_6", prompt = "干活", transcript_path = replyPath }), replyNow);
TaskSnapshot ReplyTask() => replyCatalog.Projection().Tasks.First(x => x.Id == "claude_6");

File.AppendAllLines(replyPath,
    ["{\"type\":\"assistant\",\"isSidechain\":false,\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"先说一句中间的话\"}]}}"]);
replyCatalog.Maintain(replyNow.AddSeconds(3));
Check(ReplyTask().LatestStep is null, $"claude reply: mid-turn text must NOT reach the third line (got '{ReplyTask().LatestStep}')");
Check(ReplyTask().ResponseBody == "先说一句中间的话", $"claude reply: mid-turn text goes to the panel instead (got '{ReplyTask().ResponseBody}')");

// 工具在跑：第三行是工具步；正文继续只走面板
replyCatalog.Accept(C("PreToolUse", new { session_id = "claude_6", transcript_path = replyPath, tool_name = "Bash", tool_input = new { command = "npm test" } }), replyNow.AddSeconds(4));
File.AppendAllLines(replyPath,
    ["{\"type\":\"assistant\",\"isSidechain\":false,\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"工具跑着的时候写的字\"}]}}"]);
replyCatalog.Maintain(replyNow.AddSeconds(6));
Check(ReplyTask().LatestStep == "Bash npm test", $"claude reply: third line holds the tool step (got '{ReplyTask().LatestStep}')");
Check(ReplyTask().ResponseBody == "工具跑着的时候写的字", $"claude reply: panel still updates while a tool runs (got '{ReplyTask().ResponseBody}')");

// 工具结束：正文**不会**回到第三行（面板照旧更新）
replyCatalog.Accept(C("PostToolUse", new { session_id = "claude_6", transcript_path = replyPath, tool_name = "Bash" }), replyNow.AddSeconds(7));
File.AppendAllLines(replyPath,
    ["{\"type\":\"assistant\",\"isSidechain\":false,\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"工具跑完之后写的话\"}]}}"]);
replyCatalog.Maintain(replyNow.AddSeconds(9));
Check(ReplyTask().LatestStep == "Bash npm test", $"claude reply: third line does NOT resume with mid-turn text (got '{ReplyTask().LatestStep}')");
Check(ReplyTask().ResponseBody == "工具跑完之后写的话", $"claude reply: panel tracks the newest text (got '{ReplyTask().ResponseBody}')");

// 残留守卫：回合刚开始时转录本里还没有新一轮的 user 行，倒序扫到的是**上一回合**的正文，
// 它早于本轮起点，必须被丢弃（实测踩过：第三行"经常残留上一个 reply"）
var staleCatalog = new TaskCatalog();
var staleNow = DateTimeOffset.UtcNow;
var stalePath = Path.Combine(Path.GetTempPath(), "ap-verify-claude-stale-" + Guid.NewGuid().ToString("N") + ".jsonl");
File.WriteAllLines(stalePath,
[
    "{\"type\":\"user\",\"isSidechain\":false,\"timestamp\":\"2020-01-01T00:00:00.000Z\",\"message\":{\"role\":\"user\",\"content\":\"上一轮的问题\"}}",
    "{\"type\":\"assistant\",\"isSidechain\":false,\"timestamp\":\"2020-01-01T00:00:05.000Z\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"上一轮的回复\"}]}}",
]);
staleCatalog.Accept(C("UserPromptSubmit", new { session_id = "claude_10", prompt = "新的一轮", transcript_path = stalePath }), staleNow);
staleCatalog.Maintain(staleNow.AddSeconds(3));
Check(staleCatalog.Projection().Tasks.First(x => x.Id == "claude_10").LatestStep is null,
    $"claude reply: previous turn's reply must not leak in (got '{staleCatalog.Projection().Tasks.First(x => x.Id == "claude_10").LatestStep}')");
// 正文能跨工具步，但**绝不能跨回合** —— 面板上显示上一轮说过的话比空着更糟
Check(staleCatalog.Projection().Tasks.First(x => x.Id == "claude_10").ResponseBody is null,
    $"claude body: previous turn's reply must not leak into the panel (got '{staleCatalog.Projection().Tasks.First(x => x.Id == "claude_10").ResponseBody}')");

// 回合内"最新一步是工具调用"时，不得回退去拿更早的正文
// （用户实测："laststep 工具调用内容无法覆盖上一个 reply"——心跳把工具步盖成了上一段正文）
var toolLatest = ReadTranscript(
[
    "{\"type\":\"user\",\"isSidechain\":false,\"message\":{\"role\":\"user\",\"content\":\"干活\"}}",
    "{\"type\":\"assistant\",\"isSidechain\":false,\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"先看看代码\"}]}}",
    "{\"type\":\"assistant\",\"isSidechain\":false,\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"tool_use\",\"name\":\"Bash\"}]}}",
]);
Check(toolLatest.LatestReply is null,
    $"claude reply: tool-only latest step yields no reply candidate (got '{toolLatest.LatestReply}')");
// 正文全文**可以**跨过工具步往回找（与 LatestReply 正相反）。实测（2026-10-15 本机真跑）
// Claude 把正文和工具调用写成两行：`[text]` 紧跟 `[tool_use]`，倒序扫到的第一条永远是工具调用，
// 不跨就整个回合拿不到正文——而"说一句就调工具"正是回合中的常态。
Check(toolLatest.LatestReplyFull == "先看看代码",
    $"claude reply: full body looks back past the tool step (got '{toolLatest.LatestReplyFull}')");

// 最新一步又是正文时，照常给出候选
var textLatest = ReadTranscript(
[
    "{\"type\":\"user\",\"isSidechain\":false,\"message\":{\"role\":\"user\",\"content\":\"干活\"}}",
    "{\"type\":\"assistant\",\"isSidechain\":false,\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"tool_use\",\"name\":\"Bash\"}]}}",
    "{\"type\":\"assistant\",\"isSidechain\":false,\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"跑完了，接着看\"}]}}",
]);
Check(textLatest.LatestReply == "跑完了，接着看",
    $"claude reply: text after a tool still becomes the candidate (got '{textLatest.LatestReply}')");
Check(textLatest.LatestReplyFull == "跑完了，接着看",
    $"claude reply: text after a tool becomes the full body too (got '{textLatest.LatestReplyFull}')");

// 端到端：心跳不写第三行，但面板照收 —— 两者在同一个 Maintain 里分道扬镳
var clobberCatalog = new TaskCatalog();
var clobberNow = DateTimeOffset.UtcNow;
var clobberPath = Path.Combine(Path.GetTempPath(), "ap-verify-claude-clobber-" + Guid.NewGuid().ToString("N") + ".jsonl");
File.WriteAllLines(clobberPath,
[
    "{\"type\":\"user\",\"isSidechain\":false,\"message\":{\"role\":\"user\",\"content\":\"干活\"}}",
    "{\"type\":\"assistant\",\"isSidechain\":false,\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"先看看代码\"}]}}",
]);
clobberCatalog.Accept(C("UserPromptSubmit", new { session_id = "claude_11", prompt = "干活", transcript_path = clobberPath }), clobberNow);
clobberCatalog.Maintain(clobberNow.AddSeconds(3));
Check(clobberCatalog.Projection().Tasks.First(x => x.Id == "claude_11").LatestStep is null,
    $"claude clobber: heartbeat leaves the third line alone (got '{clobberCatalog.Projection().Tasks.First(x => x.Id == "claude_11").LatestStep}')");
Check(clobberCatalog.Projection().Tasks.First(x => x.Id == "claude_11").ResponseBody == "先看看代码",
    $"claude clobber: the same heartbeat still feeds the panel (got '{clobberCatalog.Projection().Tasks.First(x => x.Id == "claude_11").ResponseBody}')");
clobberCatalog.Accept(C("PreToolUse", new { session_id = "claude_11", transcript_path = clobberPath, tool_name = "Bash", tool_input = new { command = "ls" } }), clobberNow.AddSeconds(4));
File.AppendAllLines(clobberPath,
    ["{\"type\":\"assistant\",\"isSidechain\":false,\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"tool_use\",\"name\":\"Bash\"}]}}"]);
clobberCatalog.Accept(C("PostToolUse", new { session_id = "claude_11", transcript_path = clobberPath, tool_name = "Bash" }), clobberNow.AddSeconds(5));
clobberCatalog.Maintain(clobberNow.AddSeconds(8));
Check(clobberCatalog.Projection().Tasks.First(x => x.Id == "claude_11").LatestStep == "Bash ls",
    $"claude clobber: the tool step owns the third line (got '{clobberCatalog.Projection().Tasks.First(x => x.Id == "claude_11").LatestStep}')");
// 数据侧：非完成态也得有 ResponseBody，展开后的 REPLY 面板全靠它显示中途正文。
// 要紧的是它在工具步顶掉第三行之后**照样留着** —— 这就是"面板比第三行留得住正文"的根据。
Check(clobberCatalog.Projection().Tasks.First(x => x.Id == "claude_11").Lifecycle == AgentLifecycle.Running,
    "claude body: the fixture is still mid-turn when the body is asserted");
Check(clobberCatalog.Projection().Tasks.First(x => x.Id == "claude_11").ResponseBody == "先看看代码",
    $"claude body: full body lands on the heartbeat and survives the tool step (got '{clobberCatalog.Projection().Tasks.First(x => x.Id == "claude_11").ResponseBody}')");
File.Delete(clobberPath);

// 这个功能的立身之本：工具**正在跑**的时候，正文照样进面板，而第三行保持工具步不动。
// 第三行与面板在这里必须分道扬镳 —— 同进同退就等于没做这个功能。
var activeCatalog = new TaskCatalog();
var activeNow = DateTimeOffset.UtcNow;
var activePath = Path.Combine(Path.GetTempPath(), "ap-verify-claude-body-active-" + Guid.NewGuid().ToString("N") + ".jsonl");
File.WriteAllLines(activePath,
[
    "{\"type\":\"user\",\"isSidechain\":false,\"message\":{\"role\":\"user\",\"content\":\"干活\"}}",
]);
activeCatalog.Accept(C("UserPromptSubmit", new { session_id = "claude_12", prompt = "干活", transcript_path = activePath }), activeNow);
activeCatalog.Accept(C("PreToolUse", new { session_id = "claude_12", transcript_path = activePath, tool_name = "Bash", tool_input = new { command = "ls" } }), activeNow.AddSeconds(1));
File.AppendAllLines(activePath,
[
    "{\"type\":\"assistant\",\"isSidechain\":false,\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"先看一眼目录\"}]}}",
    "{\"type\":\"assistant\",\"isSidechain\":false,\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"tool_use\",\"name\":\"Bash\"}]}}",
]);
activeCatalog.Maintain(activeNow.AddSeconds(4));
var activeTask = activeCatalog.Projection().Tasks.First(x => x.Id == "claude_12");
Check(activeTask.LatestStep == "Bash ls",
    $"claude body/active: the tool step keeps the third line (got '{activeTask.LatestStep}')");
Check(activeTask.ResponseBody == "先看一眼目录",
    $"claude body/active: the panel updates even while a tool is running (got '{activeTask.ResponseBody}')");
File.Delete(activePath);

// 本轮自己的正文（时间晚于本轮起点）照常生效
File.AppendAllLines(stalePath,
[
    "{\"type\":\"user\",\"isSidechain\":false,\"timestamp\":\"" + staleNow.AddSeconds(1).ToString("o") + "\",\"message\":{\"role\":\"user\",\"content\":\"新的一轮\"}}",
    "{\"type\":\"assistant\",\"isSidechain\":false,\"timestamp\":\"" + staleNow.AddSeconds(2).ToString("o") + "\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"这一轮的回复\"}]}}",
]);
staleCatalog.Maintain(staleNow.AddSeconds(6));
Check(staleCatalog.Projection().Tasks.First(x => x.Id == "claude_10").LatestStep is null,
    $"claude reply: heartbeat still never writes the third line (got '{staleCatalog.Projection().Tasks.First(x => x.Id == "claude_10").LatestStep}')");
Check(staleCatalog.Projection().Tasks.First(x => x.Id == "claude_10").ResponseBody == "这一轮的回复",
    $"claude body: current turn's reply reaches the panel (got '{staleCatalog.Projection().Tasks.First(x => x.Id == "claude_10").ResponseBody}')");
File.Delete(stalePath);

// 回显候选（正文整段包含用户提示词）。
// 心跳路径现在完全不写第三行了，所以"心跳不放回显"退化成恒真 —— 这条改成钉住那个事实本身。
//
// **面板不走回显守卫**（下面第二条断言是刻意的）：候选是"模型真说过的话"，逐字引用用户的
// 提示词再作答是正常表达，面板把它吞掉反而更糟。第三行才需要这条守卫 —— 那里一行顶一行，
// 回显会冒充成"进度"，而面板是明确标着 REPLY 的正文区，没有冒名的风险。
var echoCatalog = new TaskCatalog();
echoCatalog.Accept(C("UserPromptSubmit", new { session_id = "claude_9", prompt = "把构建脚本修一下", transcript_path = replyPath }), replyNow);
File.AppendAllLines(replyPath,
    ["{\"type\":\"assistant\",\"isSidechain\":false,\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"好，把构建脚本修一下这项工作我这就开始\"}]}}"]);
echoCatalog.Maintain(replyNow.AddSeconds(3));
Check(echoCatalog.Projection().Tasks.First(x => x.Id == "claude_9").LatestStep is null,
    "claude reply: heartbeat never writes the third line (echo or not)");
Check(echoCatalog.Projection().Tasks.First(x => x.Id == "claude_9").ResponseBody == "好，把构建脚本修一下这项工作我这就开始",
    $"claude body: panel keeps a reply that quotes the prompt (got '{echoCatalog.Projection().Tasks.First(x => x.Id == "claude_9").ResponseBody}')");

// ============ 等待作答的"顺延补偿"到底会不会毁掉面板上的正文（用户要求复现）============
// 待验的假设：任务退出等待态时 Upsert 把 StartedAt 往前推 pauseDuration，
// 于是"提问之前写的那段正文"时间戳落到新起点之前，被回合闸判成上一轮的 → 面板空。
// 这条复现把每一步都钉住，让代码自己回答，别靠推演。
var pauseCatalog = new TaskCatalog();
var pauseNow = DateTimeOffset.UtcNow;
var pausePath = Path.Combine(Path.GetTempPath(), "ap-verify-claude-pause-" + Guid.NewGuid().ToString("N") + ".jsonl");
File.WriteAllLines(pausePath,
    ["{\"type\":\"user\",\"isSidechain\":false,\"message\":{\"role\":\"user\",\"content\":\"干活\"}}"]);
pauseCatalog.Accept(C("UserPromptSubmit", new { session_id = "claude_13", prompt = "干活", transcript_path = pausePath }), pauseNow);
TaskSnapshot PauseTask() => pauseCatalog.Projection().Tasks.First(x => x.Id == "claude_13");

// ① 正文先落进面板。**助手行必须带 timestamp** —— 真转录本每行都有（实测
// `ts=2026-10-15T15:36:30.048Z`），而回合闸的写法是 `at is not { } at || at >= turnStart`：
// 没有时间戳就直接放行。fixture 漏了它，这条复现就会假通过。
File.AppendAllLines(pausePath,
    ["{\"type\":\"assistant\",\"isSidechain\":false,\"timestamp\":\"" + pauseNow.AddSeconds(3).ToString("o")
     + "\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"我先问你个问题\"}]}}"]);
pauseCatalog.Maintain(pauseNow.AddSeconds(3));
Check(PauseTask().ResponseBody == "我先问你个问题", $"pause (1) body lands before the question (got '{PauseTask().ResponseBody}')");
var pauseStartedAt = PauseTask().StartedAt;

// ② 进入等待作答
pauseCatalog.Accept(C("PermissionRequest", new { session_id = "claude_13", tool_name = "AskUserQuestion", tool_input = askInput }), pauseNow.AddSeconds(4));
Check(PauseTask().Lifecycle == AgentLifecycle.WaitingAnswer, $"pause (2) task waits for an answer (got {PauseTask().Lifecycle})");

// ③ 五分钟后人作答放行 → 看 StartedAt 有没有被推后
pauseCatalog.Accept(C("PostToolUse", new { session_id = "claude_13", transcript_path = pausePath, tool_name = "AskUserQuestion" }), pauseNow.AddMinutes(5));
Check(PauseTask().StartedAt > pauseStartedAt,
    $"pause (3) StartedAt is pushed forward by the wait (was {pauseStartedAt:HH:mm:ss}, now {PauseTask().StartedAt:HH:mm:ss})");

// ④ 被推后之后，提问前那段正文还过不过"属于本轮"的闸？
var pauseTranscript = ClaudeTranscriptReader.Read(pausePath);
Check(ClaudeEventReducer.FreshReplyFull(pauseTranscript, PauseTask().StartedAt) is null,
    "pause (4) the pre-question body no longer passes the turn gate");

// ⑤ 但**已经在面板上的正文没人清它** —— 这是关键：闸管的是"能不能新写入"，不是"要不要擦掉"
Check(PauseTask().ResponseBody == "我先问你个问题",
    $"pause (5) a body already on the panel is NOT wiped by the wait (got '{PauseTask().ResponseBody}')");

// ⑥ 心跳再跑一次也不会擦（RefreshClaudeTranscript 只写不清）
pauseCatalog.Maintain(pauseNow.AddMinutes(5).AddSeconds(3));
Check(PauseTask().ResponseBody == "我先问你个问题",
    $"pause (6) the heartbeat does not clear it either (got '{PauseTask().ResponseBody}')");
File.Delete(pausePath);
File.Delete(replyPath);

// ===================== 23. 控制消息签名：转义必须与 App 端 kotlinx 逐字节一致 =====================
// 实测踩过：默认 STJ 把非 ASCII 转成 \uXXXX，kotlinx 原样输出 → HMAC 对不上 → 静默 InvalidSignature，
// 手机作答"点了没反应"。批准/拒绝一直好使，只因为它们 value 是 null、不走这段。
var signEnvelope = new SignedControlEnvelope(
    1,
    Guid.Parse("11111111-2222-3333-4444-555555555555"),
    "control",
    1_700_000_000_000,
    "dev-1",
    7,
    "nonce-1",
    new ControlPayload("ses_1", ControlAction.Answer, "有按钮但点了没反应"),
    "sig");
var signingText = System.Text.Encoding.UTF8.GetString(ControlSigner.SigningData(signEnvelope));
Check(signingText.StartsWith(
        "1\n11111111-2222-3333-4444-555555555555\n1700000000000\ndev-1\n7\nnonce-1\ncontrol\n",
        StringComparison.Ordinal),
    "control signing: envelope fields keep the agreed canonical order");
Check(signingText.EndsWith(
        """{"action":"answer","taskID":"ses_1","value":"有按钮但点了没反应"}""",
        StringComparison.Ordinal),
    $"control signing: non-ASCII value stays raw, byte-identical to kotlinx (got '{signingText[(signingText.LastIndexOf('{') + 1)..]}')");

var nullValueSigning = System.Text.Encoding.UTF8.GetString(ControlSigner.SigningData(
    signEnvelope with { Payload = new ControlPayload("ses_1", ControlAction.Approve, null) }));
Check(nullValueSigning.EndsWith("""{"action":"approve","taskID":"ses_1"}""", StringComparison.Ordinal),
    "control signing: null value keeps the short canonical form");

// 多选题的 values 追加在末尾，且与 App 端 ControlSigner.canonicalPayload 逐字同形（那边同名测试钉着）
var multiSigning = System.Text.Encoding.UTF8.GetString(ControlSigner.SigningData(
    signEnvelope with { Payload = new ControlPayload("ses_1", ControlAction.Answer, "就用 A", ["A（甲）", "B（乙）"]) }));
Check(multiSigning.EndsWith(
        """{"action":"answer","taskID":"ses_1","value":"就用 A","values":["A（甲）","B（乙）"]}""",
        StringComparison.Ordinal),
    "control signing: multi-select values are appended last, raw (non-ASCII intact)");
Check(System.Text.Encoding.UTF8.GetString(ControlSigner.SigningData(signEnvelope with
        {
            Payload = new ControlPayload("ses_1", ControlAction.Answer, null, ["A", "B"]),
        }))
        .EndsWith("""{"action":"answer","taskID":"ses_1","values":["A","B"]}""", StringComparison.Ordinal),
    "control signing: values-only keeps the same order");
Check(System.Text.Encoding.UTF8.GetString(ControlSigner.SigningData(signEnvelope with
        {
            Payload = new ControlPayload("ses_1", ControlAction.Answer, "x", []),
        }))
        .EndsWith("""{"action":"answer","taskID":"ses_1","value":"x"}""", StringComparison.Ordinal),
    "control signing: an empty values list is not signed (same rule as the app)");

// ===================== 24. 复审缺口回归（子代理挂卡撤销 / 撤卡清单 / 离线 / 清理 / 终稿补读） =====================
var gapNow = DateTimeOffset.UtcNow;

// ── 子代理的权限询问：卡挂父任务，但撤销必须认来源 ──
var subPerm = new TaskCatalog();
subPerm.Accept(C("UserPromptSubmit", new { session_id = "claude_20", prompt = "干活" }), gapNow);
subPerm.Accept(C("SubagentStart", new { session_id = "claude_20", agent_id = "a1", agent_type = "Explore" }), gapNow.AddSeconds(1));
subPerm.Accept(C("PermissionRequest", new { session_id = "claude_20", agent_id = "a1", agent_type = "Explore", tool_name = "Bash", tool_input = new { command = "rm -rf x" } }), gapNow.AddSeconds(2));
TaskSnapshot SubPermTask() => subPerm.Projection().Tasks.First(x => x.Id == "claude_20");
Check(SubPermTask().Lifecycle == AgentLifecycle.WaitingApproval &&
      SubPermTask().Capabilities.Contains(TaskCapability.Approve),
    "subagent permission: parent waits with an approval card");
Check(subPerm.Projection().PendingRequests.Count == 1, "subagent permission: card registered");

subPerm.Accept(C("PostToolUse", new { session_id = "claude_20", agent_id = "a2", agent_type = "Explore", tool_name = "Bash" }), gapNow.AddSeconds(3));
Check(subPerm.Projection().PendingRequests.Count == 1, "subagent permission: another agent's tool must not release the card");

subPerm.Accept(C("PostToolUse", new { session_id = "claude_20", agent_id = "a1", agent_type = "Explore", tool_name = "Bash" }), gapNow.AddSeconds(4));
Check(subPerm.Projection().PendingRequests.Count == 0, "subagent permission: its own tool completion releases the card");
Check(SubPermTask().Lifecycle == AgentLifecycle.Running && !SubPermTask().Capabilities.Contains(TaskCapability.Approve),
    $"subagent permission: parent returns to running (got {SubPermTask().Lifecycle})");

// ── 子代理问问题：卡片类型同样要跟对（不能给允许/拒绝） ──
var subAsk = new TaskCatalog();
subAsk.Accept(C("UserPromptSubmit", new { session_id = "claude_21", prompt = "干活" }), gapNow);
subAsk.Accept(C("SubagentStart", new { session_id = "claude_21", agent_id = "b1", agent_type = "Explore" }), gapNow.AddSeconds(1));
subAsk.Accept(C("PermissionRequest", new { session_id = "claude_21", agent_id = "b1", agent_type = "Explore", tool_name = "AskUserQuestion", tool_input = askInput }), gapNow.AddSeconds(2));
var subAskTask = subAsk.Projection().Tasks.First(x => x.Id == "claude_21");
Check(subAsk.Projection().PendingRequests.First().Kind == PendingRequestKind.Question, "subagent question: question card, not approval");
Check(subAskTask.Lifecycle == AgentLifecycle.WaitingAnswer && subAskTask.Capabilities.Contains(TaskCapability.Answer),
    "subagent question: waitingAnswer + Answer capability");
Check(!subAskTask.Capabilities.Contains(TaskCapability.Approve), "subagent question: no approve/deny buttons");

// ── 撤卡清单：StopFailure 与 PermissionDenied ──
var stopFail = new TaskCatalog();
stopFail.Accept(C("PermissionRequest", new { session_id = "claude_22", tool_name = "Bash", tool_input = new { command = "rm x" } }), gapNow);
Check(stopFail.Projection().PendingRequests.Count == 1, "stop-failure: card registered");
stopFail.Accept(C("StopFailure", new { session_id = "claude_22", error = "rate_limit" }), gapNow.AddSeconds(1));
Check(stopFail.Projection().PendingRequests.Count == 0, "stop-failure: an API-failed turn clears the stuck card");
Check(stopFail.Projection().Tasks.First(x => x.Id == "claude_22").Lifecycle == AgentLifecycle.Interrupted,
    "stop-failure: turn is interrupted");

// ── 离线清卡 ──
var offCatalog = new TaskCatalog();
offCatalog.Accept(C("PermissionRequest", new { session_id = "claude_23", tool_name = "Bash", tool_input = new { command = "rm x" } }), gapNow);
Check(offCatalog.Projection().PendingRequests.Count == 1, "offline: card registered");
offCatalog.Maintain(gapNow.AddMinutes(12));
Check(offCatalog.Projection().Tasks.First(x => x.Id == "claude_23").Lifecycle == AgentLifecycle.Offline, "offline: task goes offline");
Check(offCatalog.Projection().PendingRequests.Count == 0, "offline: a dead session must not keep a dead card");

// ── 容量裁剪后不留幽灵卡 ──
var capCatalog = new TaskCatalog(capacity: 1);
capCatalog.Accept(C("PermissionRequest", new { session_id = "claude_24", tool_name = "Bash", tool_input = new { command = "a" } }), gapNow);
capCatalog.Accept(C("Stop", new { session_id = "claude_24" }), gapNow.AddSeconds(1));   // 终态 → 可被裁
capCatalog.Accept(C("PermissionRequest", new { session_id = "claude_25", tool_name = "Bash", tool_input = new { command = "b" } }), gapNow.AddSeconds(2));
Check(capCatalog.Projection().PendingRequests.All(r => r.TaskID == "claude_25"),
    "capacity prune: a pruned task leaves no phantom card behind");

// ── Stop 终稿：两路都落空时，Maintain 在限期内补读 ──
var lateCatalog = new TaskCatalog();
var latePath = Path.Combine(Path.GetTempPath(), "ap-verify-claude-late-" + Guid.NewGuid().ToString("N") + ".jsonl");
File.WriteAllLines(latePath,
    ["{\"type\":\"user\",\"isSidechain\":false,\"message\":{\"role\":\"user\",\"content\":\"干活\"}}"]);
lateCatalog.Accept(C("UserPromptSubmit", new { session_id = "claude_26", prompt = "干活", transcript_path = latePath }), gapNow);
lateCatalog.Accept(C("PreToolUse", new { session_id = "claude_26", transcript_path = latePath, tool_name = "Bash", tool_input = new { command = "ls" } }), gapNow.AddSeconds(1));
lateCatalog.Accept(C("PostToolUse", new { session_id = "claude_26", transcript_path = latePath, tool_name = "Bash" }), gapNow.AddSeconds(2));
// 此时第三行是工具步：Stop 不该登记补读（工具步按设计保留）
lateCatalog.Accept(C("Stop", new { session_id = "claude_26", transcript_path = latePath }), gapNow.AddSeconds(3));
File.AppendAllLines(latePath,
    ["{\"type\":\"assistant\",\"isSidechain\":false,\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"迟到的正文\"}]}}"]);
lateCatalog.Maintain(gapNow.AddSeconds(6));
Check(lateCatalog.Projection().Tasks.First(x => x.Id == "claude_26").LatestStep == "Bash ls",
    "late preview: a turn that left a tool step keeps it (no recheck)");

// 第三行整个是空时才登记，并能在限期内补上
var emptyCatalog = new TaskCatalog();
var emptyPath = Path.Combine(Path.GetTempPath(), "ap-verify-claude-empty-" + Guid.NewGuid().ToString("N") + ".jsonl");
File.WriteAllLines(emptyPath,
    ["{\"type\":\"user\",\"isSidechain\":false,\"message\":{\"role\":\"user\",\"content\":\"干活\"}}"]);
emptyCatalog.Accept(C("UserPromptSubmit", new { session_id = "claude_27", prompt = "干活", transcript_path = emptyPath }), gapNow);
emptyCatalog.Accept(C("Stop", new { session_id = "claude_27", transcript_path = emptyPath }), gapNow.AddSeconds(1));
Check(emptyCatalog.Projection().Tasks.First(x => x.Id == "claude_27").LatestStep is null, "late preview: nothing landed yet");
File.AppendAllLines(emptyPath,
    ["{\"type\":\"assistant\",\"isSidechain\":false,\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"补上的终稿\"}]}}"]);
emptyCatalog.Maintain(gapNow.AddSeconds(4));
Check(emptyCatalog.Projection().Tasks.First(x => x.Id == "claude_27").LatestStep == "补上的终稿",
    $"late preview: maintenance fills in the missed final text (got '{emptyCatalog.Projection().Tasks.First(x => x.Id == "claude_27").LatestStep}')");
File.Delete(latePath);
File.Delete(emptyPath);

// ===================== 25. 多题逐题推进（桥端拆题，零协议改动） =====================
var multiCatalog = new TaskCatalog();
var multiNow = DateTimeOffset.UtcNow;
multiCatalog.Accept(C("PermissionRequest", new { session_id = "claude_30", tool_name = "AskUserQuestion", tool_input = multiAsk }), multiNow);
TaskSnapshot Multi() => multiCatalog.Projection().Tasks.First(x => x.Id == "claude_30");

var firstCard = multiCatalog.Projection().PendingRequests.First();
Check(firstCard.Summary == "第 1/2 题： 第一题？" && firstCard.Options is ["A", "B"],
    $"multi advance: only the first question is presented (got '{firstCard.Summary}' / {string.Join(",", firstCard.Options)})");
Check(Multi().LatestStep == "第 1/2 题： 题一", $"multi advance: third line tracks the current question (got '{Multi().LatestStep}')");

var multiResolver = new RecordingResolver();
var firstAck = multiCatalog.Perform(Guid.NewGuid(), new ControlPayload("claude_30", ControlAction.Answer, "A"), multiResolver);
Check(firstAck.Result == ControlResult.Accepted, "multi advance: first answer accepted");
Check(multiResolver.Written is null, "multi advance: nothing is written back until every question is answered");

var secondCard = multiCatalog.Projection().PendingRequests.First();
Check(secondCard.Summary == "第 2/2 题： 第二题？" && secondCard.Options is ["C", "D"],
    $"multi advance: the card moves on to the second question (got '{secondCard.Summary}' / {string.Join(",", secondCard.Options)})");
Check(Multi().Lifecycle == AgentLifecycle.WaitingAnswer && Multi().Capabilities.Contains(TaskCapability.Answer),
    "multi advance: still waiting and still answerable");
Check(Multi().LatestStep == "第 2/2 题： 题二", $"multi advance: third line follows along (got '{Multi().LatestStep}')");

var lastAck = multiCatalog.Perform(Guid.NewGuid(), new ControlPayload("claude_30", ControlAction.Answer, "C"), multiResolver);
Check(lastAck.Result == ControlResult.Accepted, "multi advance: last answer accepted");
Check(multiResolver.Written is [{ Question: "第一题？", Labels: ["A"] }, { Question: "第二题？", Labels: ["C"] }],
    "multi advance: every answer is written back in question order");
Check(multiCatalog.Projection().PendingRequests.Count == 0 && Multi().Lifecycle == AgentLifecycle.Running,
    "multi advance: the wait ends and the turn resumes");

// 单题自由文本：不点选项就落 Text（Claude 那边会写成 response），照旧不走逐题推进
var singleResolver = new RecordingResolver();
var singleAdvance = new TaskCatalog();
singleAdvance.Accept(C("PermissionRequest", new { session_id = "claude_31", tool_name = "AskUserQuestion", tool_input = askInput }), multiNow);
singleAdvance.Perform(Guid.NewGuid(), new ControlPayload("claude_31", ControlAction.Answer, "就用 A"), singleResolver);
Check(singleResolver.RawInput is not null && singleResolver.Written is [{ Labels.Count: 0, Text: "就用 A" }],
    "multi advance: a single question's free text is recorded as text");

// AnswerMap 的形状：整张 answers 表 + 原样带回 questions
var mapBytes = ClaudeHookOutput.AnswerMap(
    """{"questions":[{"question":"q1"},{"question":"q2"}]}""",
    new Dictionary<string, string> { ["q1"] = "A", ["q2"] = "C" });
var mapInput = JsonNode.Parse(System.Text.Encoding.UTF8.GetString(mapBytes).Trim())!
    .AsObject()["hookSpecificOutput"]!["decision"]!["updatedInput"]!.AsObject();
Check(mapInput["answers"] is JsonObject writtenMap &&
      writtenMap["q1"]!.GetValue<string>() == "A" && writtenMap["q2"]!.GetValue<string>() == "C",
    "multi advance: AnswerMap writes the structured answers map");
Check(mapInput["questions"] is JsonArray { Count: 2 }, "multi advance: AnswerMap still echoes the original questions");

// ===================== 26. 选项注解写在选项框里（`label（注解）`）+ 回传归一化 =====================
var describedAsk = JsonSerializer.SerializeToElement(new
{
    questions = new[]
    {
        new
        {
            question = "选哪个？",
            header = "选择",
            options = new[]
            {
                new { label = "A", description = "选 A 会怎样" },
                new { label = "B", description = "选 B 会怎样" },
            },
        },
    },
});
Check(ClaudeEventReducer.QuestionOptions(describedAsk) is ["A（选 A 会怎样）", "B（选 B 会怎样）"],
    $"option display: the annotation rides inside the option text (got '{string.Join(" | ", ClaudeEventReducer.QuestionOptions(describedAsk))}')");

// 单题：App 回传整串 → 仍要落成结构化 answers{问题: 纯 label}，不能退化成自由文本
var annotatedBytes = ClaudeHookOutput.Answer(
    """{"questions":[{"question":"选哪个？","header":"选择","options":[{"label":"A","description":"选 A 会怎样"},{"label":"B","description":"选 B 会怎样"}]}]}""",
    "A（选 A 会怎样）");
var annotatedInput = JsonNode.Parse(System.Text.Encoding.UTF8.GetString(annotatedBytes).Trim())!
    .AsObject()["hookSpecificOutput"]!["decision"]!["updatedInput"]!.AsObject();
Check(annotatedInput["answers"] is JsonObject annotated && annotated["选哪个？"]!.GetValue<string>() == "A",
    "option display: an annotated button text still normalises to the bare label");
Check(annotatedInput["response"] is null, "option display: it does not degrade into a free-text response");

// label 自己就以括号结尾时不能被归一化削掉（先原样对表）
var parenthesisedBytes = ClaudeHookOutput.Answer(
    """{"questions":[{"question":"q","options":[{"label":"A（重要）","description":"注解"}]}]}""",
    "A（重要）");
var parenthesisedInput = JsonNode.Parse(System.Text.Encoding.UTF8.GetString(parenthesisedBytes).Trim())!
    .AsObject()["hookSpecificOutput"]!["decision"]!["updatedInput"]!.AsObject();
Check(parenthesisedInput["answers"]!["q"]!.GetValue<string>() == "A（重要）",
    "option display: a label that itself ends with parentheses is matched as-is");

// 多题：卡片上当前题的选项同样带注解，回传整串也要还原成纯 label
var describedMulti = JsonSerializer.SerializeToElement(new
{
    questions = new[]
    {
        new { question = "第一题？", header = "题一", options = new[] { new { label = "A", description = "注解 A" }, new { label = "B", description = "注解 B" } } },
        new { question = "第二题？", header = "题二", options = new[] { new { label = "C", description = "注解 C" }, new { label = "D", description = "注解 D" } } },
    },
});
var annotatedCatalog = new TaskCatalog();
annotatedCatalog.Accept(C("PermissionRequest", new { session_id = "claude_41", tool_name = "AskUserQuestion", tool_input = describedMulti }), multiNow);
Check(annotatedCatalog.Projection().PendingRequests.First().Options is ["A（注解 A）", "B（注解 B）"],
    "option display: per-question advancement annotates the current question's options");

var annotatedResolver = new RecordingResolver();
annotatedCatalog.Perform(Guid.NewGuid(), new ControlPayload("claude_41", ControlAction.Answer, "A（注解 A）"), annotatedResolver);
annotatedCatalog.Perform(Guid.NewGuid(), new ControlPayload("claude_41", ControlAction.Answer, "C（注解 C）"), annotatedResolver);
Check(annotatedResolver.Written is [{ Labels: ["A"] }, { Labels: ["C"] }],
    "option display: multi answers normalise back to bare labels");

// ===================== 27. 多选题（Claude `multiSelect` / opencode `multiple`） =====================
// 题上的多选标志必须原样落到卡片上，App 靠它决定画"点一下即作答"还是"可反复点选的开关"
var multiSelectAsk = JsonSerializer.SerializeToElement(new
{
    questions = new[]
    {
        new
        {
            question = "要开哪些功能？",
            header = "功能",
            multiSelect = true,
            options = new[] { new { label = "A", description = "甲" }, new { label = "B", description = "乙" } },
        },
    },
});
var pickCatalog = new TaskCatalog();
pickCatalog.Accept(C("PermissionRequest", new { session_id = "claude_60", tool_name = "AskUserQuestion", tool_input = multiSelectAsk }), multiNow);
var pickCard = pickCatalog.Projection().PendingRequests.First();
Check(pickCard.MultiSelect, "multi-select: multiSelect=true reaches the card");
Check(pickCard.Options is ["A（甲）", "B（乙）"], "multi-select: options keep their annotations");

var pickResolver = new RecordingResolver();
var pickAck = pickCatalog.Perform(
    Guid.NewGuid(),
    new ControlPayload("claude_60", ControlAction.Answer, null, ["A（甲）", "B（乙）"]),
    pickResolver);
Check(pickAck.Result == ControlResult.Accepted, $"multi-select: a multi answer is accepted (got {pickAck.Result}/{pickAck.Reason})");
Check(pickResolver.Written is [{ Labels: ["A", "B"], Text: null }],
    "multi-select: the picked display texts normalise back to bare labels, in the order tapped");

// 多选 + 打字：打字内容算"再选一项"（Claude 自己的多选界面就是这么算的）
var pickTypedCatalog = new TaskCatalog();
pickTypedCatalog.Accept(C("PermissionRequest", new { session_id = "claude_61", tool_name = "AskUserQuestion", tool_input = multiSelectAsk }), multiNow);
var pickTypedResolver = new RecordingResolver();
pickTypedCatalog.Perform(
    Guid.NewGuid(),
    new ControlPayload("claude_61", ControlAction.Answer, "再加一个 C", ["A（甲）"]),
    pickTypedResolver);
Check(pickTypedResolver.Written is [{ Labels: ["A"], Text: "再加一个 C" }],
    "multi-select: typed text rides alongside the picked options");

// 空作答要挡住（不发一问空答案给模型）
var pickEmptyCatalog = new TaskCatalog();
pickEmptyCatalog.Accept(C("PermissionRequest", new { session_id = "claude_62", tool_name = "AskUserQuestion", tool_input = multiSelectAsk }), multiNow);
var pickEmpty = pickEmptyCatalog.Perform(
    Guid.NewGuid(), new ControlPayload("claude_62", ControlAction.Answer, "   ", []), new RecordingResolver());
Check(pickEmpty.Result == ControlResult.Unsupported, $"multi-select: an empty answer is refused (got {pickEmpty.Result})");

// 回包形状：多选按 Claude 自己的格式用 ", " 连接，落在 answers[问题全文]
var multiReplyBytes = ClaudeHookOutput.QuestionReply(
    """{"questions":[{"question":"要开哪些功能？","multiSelect":true,"options":[{"label":"A"},{"label":"B"}]}]}""",
    [new QuestionAnswer("要开哪些功能？", ["A", "B"], null)]);
var multiReplyInput = JsonNode.Parse(System.Text.Encoding.UTF8.GetString(multiReplyBytes).Trim())!
    .AsObject()["hookSpecificOutput"]!["decision"]!["updatedInput"]!.AsObject();
Check(multiReplyInput["answers"]!["要开哪些功能？"]!.GetValue<string>() == "A, B",
    $"multi-select: the round trip joins with ', ' (got '{multiReplyInput["answers"]!["要开哪些功能？"]}')");
Check(multiReplyInput["response"] is null, "multi-select: a multi answer does not also set response");

// 单题单选点选项：同样落 answers（不是 response），与自由文本区分开
var optionReplyBytes = ClaudeHookOutput.QuestionReply(
    """{"questions":[{"question":"q","options":[{"label":"A"}]}]}""",
    [new QuestionAnswer("q", ["A"], null)]);
var optionReplyInput = JsonNode.Parse(System.Text.Encoding.UTF8.GetString(optionReplyBytes).Trim())!
    .AsObject()["hookSpecificOutput"]!["decision"]!["updatedInput"]!.AsObject();
Check(optionReplyInput["answers"]!["q"]!.GetValue<string>() == "A" && optionReplyInput["response"] is null,
    "multi-select: a single tapped option lands in answers, not response");

// 单题自由文本仍走 response（App 的输入框与它一对一）
var textReplyBytes = ClaudeHookOutput.QuestionReply(
    """{"questions":[{"question":"q","options":[{"label":"A"}]}]}""",
    [new QuestionAnswer("q", [], "就用 A")]);
var textReplyInput = JsonNode.Parse(System.Text.Encoding.UTF8.GetString(textReplyBytes).Trim())!
    .AsObject()["hookSpecificOutput"]!["decision"]!["updatedInput"]!.AsObject();
Check(textReplyInput["response"]!.GetValue<string>() == "就用 A" && textReplyInput["answers"] is null,
    "multi-select: a lone free-text answer still rides on response");

// ===================== 28. opencode 远程作答（与 Claude 同一套节奏） =====================
var ocAnswerCatalog = new TaskCatalog();
var ocAnswerNow = DateTimeOffset.UtcNow;
var ocQuestions = new
{
    sessionID = "ses_q",
    id = "que_1",
    questions = new object[]
    {
        new { question = "先选哪个？", header = "第一", options = new object[] { "A", "B" } },
        new { question = "再选哪些？", header = "第二", multiple = true, options = new object[] { "C", "D", "E" } },
    },
};
ocAnswerCatalog.Accept(H("chat.message", new { sessionID = "ses_q", text = "干活" }), ocAnswerNow);
Check(ocAnswerCatalog.Accept(
        new OpenCodeHookPayload("question.asked", JsonSerializer.SerializeToElement(ocQuestions, WireJson.Options), "question", "que_1"),
        ocAnswerNow.AddSeconds(1)),
    "opencode question: a held question is accepted");

TaskSnapshot OcAnswer() => ocAnswerCatalog.Projection().Tasks.First(x => x.Id == "ses_q");
var ocFirst = ocAnswerCatalog.Projection().PendingRequests.First();
Check(ocFirst.Summary == "第 1/2 题： 先选哪个？" && ocFirst.Options is ["A", "B"] && !ocFirst.MultiSelect,
    $"opencode question: one question per screen, own options (got '{ocFirst.Summary}' / {string.Join(",", ocFirst.Options)})");
Check(OcAnswer().Lifecycle == AgentLifecycle.WaitingAnswer && OcAnswer().Capabilities.Contains(TaskCapability.Answer),
    "opencode question: waiting and answerable");

var ocFirstResolver = new RecordingResolver();
var ocFirstAck = ocAnswerCatalog.Perform(
    Guid.NewGuid(), new ControlPayload("ses_q", ControlAction.Answer, "A"), ocFirstResolver);
Check(ocFirstAck.Result == ControlResult.Accepted && ocFirstResolver.Written is null,
    "opencode question: the first answer is banked, not sent");

var ocSecond = ocAnswerCatalog.Projection().PendingRequests.First();
Check(ocSecond.Summary == "第 2/2 题： 再选哪些？" && ocSecond.Options is ["C", "D", "E"] && ocSecond.MultiSelect,
    $"opencode question: the multiple flag rides the card too (got '{ocSecond.Summary}' / multi={ocSecond.MultiSelect})");

var ocFinalResolver = new RecordingResolver();
var ocFinalAck = ocAnswerCatalog.Perform(
    Guid.NewGuid(), new ControlPayload("ses_q", ControlAction.Answer, null, ["C", "E"]), ocFinalResolver);
Check(ocFinalAck.Result == ControlResult.Accepted, $"opencode question: the last answer is accepted (got {ocFinalAck.Result}/{ocFinalAck.Reason})");
Check(ocFinalResolver.Written is [{ Labels: ["A"] }, { Labels: ["C", "E"] }],
    "opencode question: both questions land in one ordered set, multi-select intact");
Check(ocAnswerCatalog.Projection().PendingRequests.Count == 0 && OcAnswer().Lifecycle == AgentLifecycle.Running,
    "opencode question: the wait ends and the turn resumes");

// 没有 hold 的提问：只有卡片、没有按钮（发不出去的输入框不许出现）
var ocPlainCatalog = new TaskCatalog();
ocPlainCatalog.Accept(H("chat.message", new { sessionID = "ses_q2", text = "干活" }), ocAnswerNow);
ocPlainCatalog.Accept(H("question.asked", JsonSerializer.SerializeToElement(
    new { sessionID = "ses_q2", id = "que_2", questions = new object[] { new { question = "选哪个？", options = new object[] { "A" } } } }, WireJson.Options)),
    ocAnswerNow.AddSeconds(1));
Check(!ocPlainCatalog.Projection().Tasks.First(x => x.Id == "ses_q2").Capabilities.Contains(TaskCapability.Answer),
    "opencode question: a plain (unheld) question grants no answer capability");

// 心跳（A 口径）：工具步粘滞，不被更早的正文盖掉（这条在 C 回退后仍然成立，钉住它防再放宽）
var heartbeatCatalog = new TaskCatalog();
var heartbeatPath = Path.Combine(Path.GetTempPath(), "ap-verify-claude-hb-" + Guid.NewGuid().ToString("N") + ".jsonl");
File.WriteAllLines(heartbeatPath,
[
    "{\"type\":\"user\",\"isSidechain\":false,\"message\":{\"role\":\"user\",\"content\":\"干活\"}}",
    "{\"type\":\"assistant\",\"isSidechain\":false,\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"我先说一句再干活\"}]}}",
    "{\"type\":\"assistant\",\"isSidechain\":false,\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"tool_use\",\"name\":\"Bash\"}]}}",
]);
heartbeatCatalog.Accept(C("UserPromptSubmit", new { session_id = "claude_51", prompt = "干活", transcript_path = heartbeatPath }), gapNow);
heartbeatCatalog.Accept(C("PreToolUse", new { session_id = "claude_51", transcript_path = heartbeatPath, tool_name = "Bash", tool_input = new { command = "ls" } }), gapNow.AddSeconds(1));
heartbeatCatalog.Accept(C("PostToolUse", new { session_id = "claude_51", transcript_path = heartbeatPath, tool_name = "Bash" }), gapNow.AddSeconds(2));
heartbeatCatalog.Maintain(gapNow.AddSeconds(5));
Check(heartbeatCatalog.Projection().Tasks.First(x => x.Id == "claude_51").LatestStep == "Bash ls",
    $"stop preview: the heartbeat keeps the tool step (A behaviour unchanged) (got '{heartbeatCatalog.Projection().Tasks.First(x => x.Id == "claude_51").LatestStep}')");
File.Delete(heartbeatPath);

// 等待作答时钟顺延补偿测试：人类等待时间不计入 Agent 净工作耗时
var clockShiftCatalog = new TaskCatalog();
var csT0 = DateTimeOffset.UtcNow;
clockShiftCatalog.Accept(H("chat.message", new { sessionID = "turn_clock", message = "测试耗时" }), csT0);
var taskRunning = clockShiftCatalog.Projection().Tasks.First(x => x.Id == "turn_clock");
Check(taskRunning.StartedAt == csT0, "clock: turn started at t0");
// 10秒后发起提问
clockShiftCatalog.Accept(H("question.asked", new { sessionID = "turn_clock", questions = new[] { new { question = "选A还是B？" } } }), csT0.AddSeconds(10));
var taskWaiting = clockShiftCatalog.Projection().Tasks.First(x => x.Id == "turn_clock");
Check(taskWaiting.Lifecycle == AgentLifecycle.WaitingAnswer, "clock: entered waitingAnswer");
// 人类等待了 50 秒才作答（总时间经过 60 秒）
clockShiftCatalog.Accept(H("question.replied", new { sessionID = "turn_clock" }), csT0.AddSeconds(60));
var taskResumed = clockShiftCatalog.Projection().Tasks.First(x => x.Id == "turn_clock");
Check(taskResumed.Lifecycle == AgentLifecycle.Running, "clock: resumed to running");
// 顺延 50 秒后，StartedAt 应为 csT0 + 50s，此时净耗时 = 60s - (csT0 + 50s) = 10s（精确扣除 50s 等待）
Check(taskResumed.StartedAt == csT0.AddSeconds(50), "clock: startedAt shifted forward by pause duration");

// ── 离线回收与终态保持测试 ──
// 1) 终态保持：Succeeded 任务收到 SessionEnd 必须保持 Succeeded 和 CompletedAt
var termCatalog = new TaskCatalog();
var termT0 = DateTimeOffset.UtcNow;
termCatalog.Accept(C("UserPromptSubmit", new { session_id = "term_keep", prompt = "测试" }), termT0);
termCatalog.Accept(C("Stop", new { session_id = "term_keep" }), termT0.AddSeconds(5));
var termFinished = termCatalog.Projection().Tasks.First(x => x.Id == "term_keep");
Check(termFinished.Lifecycle == AgentLifecycle.Succeeded && termFinished.CompletedAt == termT0.AddSeconds(5),
    "terminal keep: task succeeded with completedAt");
termCatalog.Accept(C("SessionEnd", new { session_id = "term_keep" }), termT0.AddSeconds(10));
var termAfterEnd = termCatalog.Projection().Tasks.First(x => x.Id == "term_keep");
Check(termAfterEnd.Lifecycle == AgentLifecycle.Offline && termAfterEnd.CompletedAt == termT0.AddSeconds(5),
    "terminal keep: sessionEnd transitions to offline while preserving completedAt");

// 2) 离线超时清理：30 分钟后被 Normalize 移除
termCatalog.Maintain(termT0.AddMinutes(36));
Check(termCatalog.Projection().Tasks.All(x => x.Id != "term_keep"),
    "terminal prune: task purged after 30 minutes");

// 3) 离线任务超容裁剪：离线任务应作为可裁剪对象被容量裁剪清除
var offCapCatalog = new TaskCatalog(capacity: 1);
offCapCatalog.Accept(C("UserPromptSubmit", new { session_id = "off_1", prompt = "被裁的" }), termT0);
offCapCatalog.Accept(C("SessionEnd", new { session_id = "off_1" }), termT0.AddSeconds(1));
Check(offCapCatalog.Projection().Tasks.First(x => x.Id == "off_1").Lifecycle == AgentLifecycle.Offline,
    "offline capacity: first task is offline");
offCapCatalog.Accept(C("UserPromptSubmit", new { session_id = "off_2", prompt = "留下的" }), termT0.AddSeconds(2));
// off_1 离线可被裁，容量为 1 时被清除
Check(offCapCatalog.Projection().Tasks.Count == 1 && offCapCatalog.Projection().Tasks.First().Id == "off_2",
    "offline capacity: offline task is evicted when capacity exceeded");

// 4) 空离线任务 1 分钟快速回收。夹具改用 PreToolUse 造无提示词的行——SessionStart 已不再建行，
//    而"桥在会话中途才起来、第一帧就是工具调用"仍是真会造出无提示词行的路径，回收规则必须留着
//    （opencode 的 session.created 同样会造出无提示词的 idle 行，这条规则对全局生效）。
var emptyGhostCatalog = new TaskCatalog();
emptyGhostCatalog.Accept(C("PreToolUse", new { session_id = "ghost_1", tool_name = "Bash", tool_input = new { command = "ls" } }), termT0);
emptyGhostCatalog.Accept(C("SessionEnd", new { session_id = "ghost_1" }), termT0.AddSeconds(1));
emptyGhostCatalog.Maintain(termT0.AddMinutes(2));
Check(emptyGhostCatalog.Projection().Tasks.All(x => x.Id != "ghost_1"),
    "empty offline: ghost task without prompt pruned after 1 minute");

// ── Antigravity 子代理路由与幽灵卡片防护测试 ──
var agySubCatalog = new TaskCatalog();
var subT0 = DateTimeOffset.UtcNow;
// 1) 父任务创建
agySubCatalog.Accept(A("PreInvocation", new { conversationId = "parent_agy", workspacePaths = new[] { @"C:\test" } }), subT0);
var pTask = agySubCatalog.Projection().Tasks.First(x => x.Id == "parent_agy");
Check(pTask.Lifecycle == AgentLifecycle.Running, "agy sub: parent created as running");

// 2) 父任务发起 invoke_subagent，带 Role
agySubCatalog.Accept(A("PreToolUse", new
{
    conversationId = "parent_agy",
    toolCall = new
    {
        name = "invoke_subagent",
        args = new
        {
            Subagents = new[]
            {
                new { Role = "Pro Security Auditor", TypeName = "self" }
            }
        }
    }
}), subT0.AddSeconds(1));

// 3) 子代理到达 PreInvocation，模拟转录本尚未落盘的极端竞态（无 transcriptPath）
var subTranscript = Path.Combine(Path.GetTempPath(), $"agy_sub_{Guid.NewGuid():N}.jsonl");
File.WriteAllLines(subTranscript, new[]
{
    "{\"step_index\":0,\"source\":\"SYSTEM\",\"type\":\"SYSTEM_MESSAGE\",\"content\":\"<SYSTEM_MESSAGE>\\n[Message] sender=parent_agy content=测试任务\"}"
});

agySubCatalog.Accept(A("PreInvocation", new
{
    conversationId = "child_agy_1",
    // 首帧不传 transcriptPath，验证先验派发窗口在转录本未落盘时依然能瞬间认领，彻底消除闪烁
}), subT0.AddSeconds(2));

// 验证：绝对不生成顶层 child_agy_1 任务！
Check(agySubCatalog.Projection().Tasks.All(x => x.Id != "child_agy_1"),
    "agy sub: child task is NEVER created as top-level card");

// 验证：挂入父任务 Subagents，且 Role 为 Pro Security Auditor，父任务处于 Delegating
var pTaskWithSub = agySubCatalog.Projection().Tasks.First(x => x.Id == "parent_agy");
Check(pTaskWithSub.Subagents.Count == 1, "agy sub: child mounted into parent subagents");
Check(pTaskWithSub.Subagents[0].DisplayName == "Pro Security Auditor", "agy sub: child display name uses role");
Check(pTaskWithSub.Activity == AgentActivity.Delegating, "agy sub: parent activity is delegating");

// 4) 子代理执行工具并在 3 秒后完成
agySubCatalog.Accept(A("PreToolUse", new
{
    conversationId = "child_agy_1",
    toolCall = new
    {
        name = "run_command",
        args = new { CommandLine = "git status" }
    },
    transcriptPath = subTranscript
}), subT0.AddSeconds(3));

var pTaskTool = agySubCatalog.Projection().Tasks.First(x => x.Id == "parent_agy");
Check(pTaskTool.Subagents[0].LatestStep?.Contains("git status") == true, "agy sub: tool step captured");

agySubCatalog.Accept(A("Stop", new
{
    conversationId = "child_agy_1",
    transcriptPath = subTranscript
}), subT0.AddSeconds(4));

var pTaskDone = agySubCatalog.Projection().Tasks.First(x => x.Id == "parent_agy");
Check(pTaskDone.Subagents[0].Lifecycle == AgentLifecycle.Succeeded, "agy sub: child marked succeeded");

// 5) 4 秒淡出收拢：超过 4 秒后由 Maintain 清理
agySubCatalog.Maintain(subT0.AddSeconds(9));
var pTaskPruned = agySubCatalog.Projection().Tasks.First(x => x.Id == "parent_agy");
Check(pTaskPruned.Subagents.Count == 0, "agy sub: child faded out after 4 seconds");
File.Delete(subTranscript);

// ===================== 27. 1a/1c 回归（子代理只进面板 / 掐掉要同步 / agy 主对话不被误删）=====================

// ---- 1c：Claude Code 掐子代理时**不发 SubagentStop**（探针实测），靠 TaskStop 入参收敛 ----
var killCatalog = new TaskCatalog();
var killNow = DateTimeOffset.UtcNow;
killCatalog.Accept(C("UserPromptSubmit", new { session_id = "claude_kill", prompt = "跑个子代理" }), killNow);
killCatalog.Accept(C("PreToolUse", new { session_id = "claude_kill", tool_name = "Bash", tool_input = new { command = "npm run build" } }), killNow.AddSeconds(1));
killCatalog.Accept(C("SubagentStart", new { session_id = "claude_kill", agent_id = "a_kill", agent_type = "general-purpose" }), killNow.AddSeconds(2));
TaskSnapshot KillTask() => killCatalog.Projection().Tasks.First(t => t.Id == "claude_kill");
Check(KillTask().Subagents is [{ Lifecycle: AgentLifecycle.Running }], "kill: subagent row starts running");
Check(KillTask().Activity == AgentActivity.Delegating, "kill: parent delegates while it runs");

// 掐掉：父级 PreToolUse(TaskStop)，入参 task_id 与 SubagentStart 的 agent_id 逐字相同（实测）
killCatalog.Accept(C("PreToolUse", new { session_id = "claude_kill", tool_name = "TaskStop", tool_input = new { task_id = "a_kill" } }), killNow.AddSeconds(3));
Check(KillTask().Subagents is [{ Lifecycle: AgentLifecycle.Interrupted }],
    $"kill: TaskStop finalizes the subagent as interrupted (got {KillTask().Subagents[0].Lifecycle})");
Check(KillTask().Subagents[0].LatestStep == "✗ 已被停止", $"kill: killed subagent says so (got '{KillTask().Subagents[0].LatestStep}')");
Check(KillTask().Activity == AgentActivity.Thinking, "kill: parent stops delegating once the subagent is terminal");
// TaskStop 这条工具调用不得把父任务第三行冲成光秃秃的 "Task"
Check(KillTask().LatestStep == "Bash npm run build", $"kill: TaskStop leaves the parent third line alone (got '{KillTask().LatestStep}')");

// 目标不是已知子代理（后台 bash 任务等）→ 静默跳过，不许误伤
killCatalog.Accept(C("PreToolUse", new { session_id = "claude_kill", tool_name = "TaskStop", tool_input = new { task_id = "bash_42" } }), killNow.AddSeconds(4));
Check(KillTask().Subagents.Count == 1 && KillTask().Subagents[0].Lifecycle == AgentLifecycle.Interrupted, "kill: unknown task id is ignored");

// 嵌套：子代理内部掐它自己的子代理，同样收敛（agent_id 非空，走子代理路径）
killCatalog.Accept(C("SubagentStart", new { session_id = "claude_kill", agent_id = "b_nested", agent_type = "Explore" }), killNow.AddSeconds(5));
killCatalog.Accept(C("PreToolUse", new { session_id = "claude_kill", agent_id = "a_kill", agent_type = "general-purpose", tool_name = "TaskStop", tool_input = new { task_id = "b_nested" } }), killNow.AddSeconds(6));
Check(KillTask().Subagents.Any(s => s.Id == "b_nested" && s.Lifecycle == AgentLifecycle.Interrupted),
    "kill: a nested subagent stopped from inside another subagent is finalized too");

// ---- agy：父任务成功结束**不得**收走仍在跑的子代理（2026-10-11 用户报出）----
// agy 的 invoke_subagent 是**异步**的：父回合先 Stop（实测 16:50:28），子代理还在跑，
// 10 秒后才回报并 resume 父任务（16:50:38）。旧实现在父 Stop 时无条件把未终态子代理扫成
// succeeded（succeeded 的子代理行 4 秒后淡出）= 手机上"父任务一完成，子代理卡片就被收起"。
var agyKeepCatalog = new TaskCatalog();
var keepT0 = DateTimeOffset.UtcNow;
TaskSnapshot KeepTask() => agyKeepCatalog.Projection().Tasks.First(t => t.Id == "p_keep");
agyKeepCatalog.Accept(A("PreInvocation", new { conversationId = "p_keep", workspacePaths = new[] { @"C:\test" } }), keepT0);
agyKeepCatalog.Accept(A("PreToolUse", new
{
    conversationId = "p_keep",
    toolCall = new { name = "invoke_subagent", args = new { Subagents = new[] { new { Role = "File Lister", TypeName = "research" } } } }
}), keepT0.AddSeconds(1));
agyKeepCatalog.Accept(A("PreInvocation", new { conversationId = "c_keep", workspacePaths = new[] { @"C:\test" } }), keepT0.AddSeconds(2));
Check(KeepTask().Subagents is [{ Lifecycle: AgentLifecycle.Running }], "agy keep: child is running before the parent finishes");

agyKeepCatalog.Accept(A("Stop", new { conversationId = "p_keep" }), keepT0.AddSeconds(3));
Check(KeepTask().Lifecycle == AgentLifecycle.Succeeded, "agy keep: parent turn finished");
Check(KeepTask().Subagents is [{ Lifecycle: AgentLifecycle.Running }],
    $"agy keep: a still-running child is NOT collapsed by the parent's Stop (got {KeepTask().Subagents[0].Lifecycle})");
agyKeepCatalog.Maintain(keepT0.AddSeconds(5));
Check(KeepTask().Subagents is [{ Lifecycle: AgentLifecycle.Running }], "agy keep: the heartbeat does not collapse it either");

// 子代理随后自己回报：由它自己的 Stop 收敛
agyKeepCatalog.Accept(A("Stop", new { conversationId = "c_keep" }), keepT0.AddSeconds(6));
Check(KeepTask().Subagents is [{ Lifecycle: AgentLifecycle.Succeeded }], "agy keep: child converges on its own Stop");

// 兜底：父已终态而子代理整整 10 分钟没有任何事件 = 真的失联 → interrupted（卡片不能永远转圈）
var agyLostCatalog = new TaskCatalog();
var lostT0 = DateTimeOffset.UtcNow;
agyLostCatalog.Accept(A("PreInvocation", new { conversationId = "p_lost", workspacePaths = new[] { @"C:\test" } }), lostT0);
agyLostCatalog.Accept(A("PreToolUse", new
{
    conversationId = "p_lost",
    toolCall = new { name = "invoke_subagent", args = new { Subagents = new[] { new { Role = "researcher", TypeName = "research" } } } }
}), lostT0.AddSeconds(1));
agyLostCatalog.Accept(A("PreInvocation", new { conversationId = "c_lost", workspacePaths = new[] { @"C:\test" } }), lostT0.AddSeconds(2));
agyLostCatalog.Accept(A("Stop", new { conversationId = "p_lost" }), lostT0.AddSeconds(3));
TaskSnapshot LostTask() => agyLostCatalog.Projection().Tasks.First(t => t.Id == "p_lost");
Check(LostTask().Subagents is [{ Lifecycle: AgentLifecycle.Running }], "agy lost: child still running right after the parent finished");
agyLostCatalog.Maintain(lostT0.AddMinutes(14));
Check(LostTask().Subagents is [{ Lifecycle: AgentLifecycle.Interrupted }],
    $"agy lost: a child silent for 10 minutes is swept as interrupted (got {LostTask().Subagents[0].Lifecycle})");

// 异常终止仍然级联：这一摊确实崩了，未终态子代理跟着扫成 interrupted
var agyCrashCatalog = new TaskCatalog();
var crashT0 = DateTimeOffset.UtcNow;
agyCrashCatalog.Accept(A("PreInvocation", new { conversationId = "p_crash", workspacePaths = new[] { @"C:\test" } }), crashT0);
agyCrashCatalog.Accept(A("PreToolUse", new
{
    conversationId = "p_crash",
    toolCall = new { name = "invoke_subagent", args = new { Subagents = new[] { new { Role = "researcher", TypeName = "research" } } } }
}), crashT0.AddSeconds(1));
agyCrashCatalog.Accept(A("PreInvocation", new { conversationId = "c_crash", workspacePaths = new[] { @"C:\test" } }), crashT0.AddSeconds(2));
agyCrashCatalog.Accept(A("Stop", new { conversationId = "p_crash", error = "boom" }), crashT0.AddSeconds(3));
var crashTask = agyCrashCatalog.Projection().Tasks.First(t => t.Id == "p_crash");
Check(crashTask.Lifecycle == AgentLifecycle.Interrupted, "agy crash: parent is interrupted");
Check(crashTask.Subagents is [{ Lifecycle: AgentLifecycle.Interrupted }],
    $"agy crash: children are cascaded to interrupted on an abnormal end (got {crashTask.Subagents[0].Lifecycle})");

// ---- 子代理静默收敛：三家通用、两级阈值、且"正在跑工具的不算静默" ----
// 场景一：opencode 子代理跑测试命令 → 子代理行也必须是 Testing（蓝），与父任务行同色
var ocTestCatalog = new TaskCatalog();
var ocT0 = DateTimeOffset.UtcNow;
ocTestCatalog.Accept(H("chat.message", new { sessionID = "ses_oc", text = "跑测试" }), ocT0);
ocTestCatalog.Accept(H("subagent", new { sessionID = "ses_oc", id = "sub_a", phase = "started", agent = "build" }), ocT0.AddSeconds(1));
ocTestCatalog.Accept(H("subagent", new
{
    sessionID = "ses_oc",
    id = "sub_a",
    phase = "tool",
    tool = "bash",
    title = "npm test",
    command = "npm test"
}), ocT0.AddSeconds(2));
var ocSub = ocTestCatalog.Projection().Tasks.First(t => t.Id == "ses_oc").Subagents[0];
Check(ocSub.Activity == AgentActivity.Testing,
    $"opencode subagent: a test command is Testing on the subagent row too (got {ocSub.Activity})");

// 场景二：正在执行工具（activity=Executing）的子代理**不算静默**——一个 15 分钟的构建不该被判死
var silentCatalog = new TaskCatalog();
var silentT0 = DateTimeOffset.UtcNow;
silentCatalog.Accept(C("UserPromptSubmit", new { session_id = "claude_silent", prompt = "跑构建" }), silentT0);
silentCatalog.Accept(C("SubagentStart", new { session_id = "claude_silent", agent_id = "s_busy", agent_type = "general-purpose" }), silentT0.AddSeconds(1));
silentCatalog.Accept(C("PreToolUse", new { session_id = "claude_silent", agent_id = "s_busy", agent_type = "general-purpose", tool_name = "Bash", tool_input = new { command = "npm run build" } }), silentT0.AddSeconds(2));
// s_quiet 只发 Start、不再有任何事件：它的活动位停在 Delegating = "没在跑工具"，这才是可判静默的那类
silentCatalog.Accept(C("SubagentStart", new { session_id = "claude_silent", agent_id = "s_quiet", agent_type = "Explore" }), silentT0.AddSeconds(3));
TaskSnapshot Silent() => silentCatalog.Projection().Tasks.First(t => t.Id == "claude_silent");
Check(Silent().Subagents.First(s => s.Id == "s_busy").Activity == AgentActivity.Executing, "silence: the busy subagent is executing (protected)");
Check(Silent().Subagents.First(s => s.Id == "s_quiet").Activity == AgentActivity.Delegating, "silence: the quiet subagent sits at delegating");
// 让父任务终态，并把时间推过 10 分钟
silentCatalog.Accept(C("Stop", new { session_id = "claude_silent", last_assistant_message = "完事" }), silentT0.AddSeconds(5));
silentCatalog.Maintain(silentT0.AddMinutes(11));
Check(Silent().Subagents.First(s => s.Id == "s_busy").Lifecycle == AgentLifecycle.Running,
    "silence: a subagent running a tool is NOT swept even when silent for 10 minutes");
Check(Silent().Subagents.First(s => s.Id == "s_quiet").Lifecycle == AgentLifecycle.Interrupted,
    $"silence: an idle-then-silent subagent IS swept (got {Silent().Subagents.First(s => s.Id == "s_quiet").Lifecycle})");

// 场景三：父任务仍在跑时用 30 分钟这档（20 分钟不动它）。
// 这里必须用 agy 做夹具：Claude 的父任务静默 10 分钟就会被离线规则收走（离线时会顺带扫子代理），
// 而 agy 的父任务在等子代理时 Activity=Delegating，正好豁免它自己的离线判定，能一直保持 Running。
var slowCatalog = new TaskCatalog();
var slowT0 = DateTimeOffset.UtcNow;
slowCatalog.Accept(A("PreInvocation", new { conversationId = "p_slow", workspacePaths = new[] { @"C:\test" } }), slowT0);
slowCatalog.Accept(A("PreToolUse", new
{
    conversationId = "p_slow",
    toolCall = new { name = "invoke_subagent", args = new { Subagents = new[] { new { Role = "researcher", TypeName = "research" } } } }
}), slowT0.AddSeconds(1));
slowCatalog.Accept(A("PreInvocation", new { conversationId = "c_slow", workspacePaths = new[] { @"C:\test" } }), slowT0.AddSeconds(2));
TaskSnapshot SlowTask() => slowCatalog.Projection().Tasks.First(t => t.Id == "p_slow");
Check(SlowTask().Lifecycle == AgentLifecycle.Running && SlowTask().Activity == AgentActivity.Delegating,
    "silence: the agy parent stays running (delegating exempts it from the offline rule)");
slowCatalog.Maintain(slowT0.AddMinutes(20));
Check(SlowTask().Subagents is [{ Lifecycle: AgentLifecycle.Running }],
    $"silence: while the parent is still running, 20 minutes is not enough to sweep (got {SlowTask().Subagents[0].Lifecycle})");
slowCatalog.Maintain(slowT0.AddMinutes(31));
Check(SlowTask().Subagents is [{ Lifecycle: AgentLifecycle.Interrupted }],
    $"silence: while the parent is still running, 31 minutes is enough (got {SlowTask().Subagents[0].Lifecycle})");

// ---- agy 计时：异步子代理唤醒父任务时，生命周期照实翻 running，但计时与第三行不许动 ----
// 实测（2026-10-11 16:50）：父回合 16:50:28 Stop → 子代理 16:50:38 回报 → 父又连续发起模型调用。
// 那**不是**用户的新回合（同一个请求还在继续），按 isNewRound 归零会让手机上"已完成的任务重新计时"。
var agyTimerPath = Path.Combine(Path.GetTempPath(), $"agy_timer_{Guid.NewGuid():N}.jsonl");
File.WriteAllLines(agyTimerPath, new[]
{
    "{\"step_index\":0,\"type\":\"USER_INPUT\",\"content\":\"<USER_REQUEST>读三个文件</USER_REQUEST>\"}",
});
var agyTimerCatalog = new TaskCatalog();
var timerT0 = DateTimeOffset.UtcNow;
TaskSnapshot TimerTask() => agyTimerCatalog.Projection().Tasks.First(t => t.Id == "p_timer");
agyTimerCatalog.Accept(A("PreInvocation", new { conversationId = "p_timer", workspacePaths = new[] { @"C:\test" }, transcriptPath = agyTimerPath }), timerT0);
agyTimerCatalog.Accept(A("PreToolUse", new
{
    conversationId = "p_timer",
    transcriptPath = agyTimerPath,
    toolCall = new { name = "view_file", args = new { AbsolutePath = @"C:\test\a.md" } }
}), timerT0.AddSeconds(1));
var timerStartedAt = TimerTask().StartedAt;
var timerStep = TimerTask().LatestStep;
Check(timerStep is not null, "agy timer: the fixture has a step before finishing");
agyTimerCatalog.Accept(A("Stop", new { conversationId = "p_timer", transcriptPath = agyTimerPath }), timerT0.AddSeconds(2));
Check(TimerTask().Lifecycle == AgentLifecycle.Succeeded, "agy timer: turn finished");

// 子代理回报后父任务被唤醒续跑：同一条提示词、没有排队
agyTimerCatalog.Accept(A("PreInvocation", new { conversationId = "p_timer", workspacePaths = new[] { @"C:\test" }, transcriptPath = agyTimerPath }), timerT0.AddSeconds(12));
Check(TimerTask().Lifecycle == AgentLifecycle.Running, $"agy timer: a woken parent is honestly shown as running (got {TimerTask().Lifecycle})");
Check(TimerTask().StartedAt == timerStartedAt, "agy timer: a woken parent does NOT restart the turn timer");
Check(TimerTask().LatestStep == timerStep, $"agy timer: a woken parent keeps its third line (got '{TimerTask().LatestStep}')");

// 真的换了提示词 → 仍然是新回合，该重置就重置。
// 时序要照实：先按**旧**转录本收掉这一轮，用户才开始打字（提示词在 PreInvocation 之前落盘）。
agyTimerCatalog.Accept(A("Stop", new { conversationId = "p_timer", transcriptPath = agyTimerPath }), timerT0.AddSeconds(20));
File.WriteAllLines(agyTimerPath, new[]
{
    "{\"step_index\":0,\"type\":\"USER_INPUT\",\"content\":\"<USER_REQUEST>读三个文件</USER_REQUEST>\"}",
    "{\"step_index\":1,\"type\":\"USER_INPUT\",\"content\":\"<USER_REQUEST>再看看日志</USER_REQUEST>\"}",
});
agyTimerCatalog.Accept(A("PreInvocation", new { conversationId = "p_timer", workspacePaths = new[] { @"C:\test" }, transcriptPath = agyTimerPath }), timerT0.AddSeconds(21));
Check(TimerTask().StartedAt == timerT0.AddSeconds(21), $"agy timer: a genuinely new prompt still resets the timer (got {TimerTask().StartedAt:HH:mm:ss})");
Check(TimerTask().LatestStep is null, "agy timer: a genuinely new prompt clears the third line");
File.Delete(agyTimerPath);

// ---- Claude：排队消息被投递**不重置计时**（用户裁定：计时只在一轮完了之后重新开始才重置）----
// 时序取证（用户转录本里 9 次真实 dequeue）：每一次投递都在上一条 assistant 消息之后 130~200ms、
// 随后 10~30ms 就是新的用户消息 —— 即"上一轮刚结束就被投递"，单看生命周期（已是 succeeded）
// 必然误判成新回合 ✗。守卫：Accept 事先把"曾显示为排队中"的文本记下来，投递时对表。
var claudeQueuePath = Path.Combine(Path.GetTempPath(), $"ap_claude_queue_{Guid.NewGuid():N}.jsonl");
var claudeQueueCatalog = new TaskCatalog();
var claudeQueueT0 = DateTimeOffset.UtcNow;
TaskSnapshot ClaudeQueueTask() => claudeQueueCatalog.Projection().Tasks.First(t => t.Id == "claude_queue");
// 转录本时间戳必须**晚于本轮开始**才会被判成"排队中"（判据是 queuedAt > StartedAt）→ 一律按 t0 生成
string Stamp(int seconds) => claudeQueueT0.AddSeconds(seconds).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
var firstUserLine = $"{{\"type\":\"user\",\"isSidechain\":false,\"timestamp\":\"{Stamp(0)}\",\"message\":{{\"role\":\"user\",\"content\":\"第一轮那句\"}}}}";
var enqueueLine = $"{{\"type\":\"queue-operation\",\"operation\":\"enqueue\",\"timestamp\":\"{Stamp(5)}\",\"content\":\"排队那句\"}}";
var dequeueLine = $"{{\"type\":\"queue-operation\",\"operation\":\"dequeue\",\"timestamp\":\"{Stamp(6)}\"}}";
var deliveredLine = $"{{\"type\":\"user\",\"isSidechain\":false,\"timestamp\":\"{Stamp(6)}\",\"message\":{{\"role\":\"user\",\"content\":\"排队那句\"}}}}";
try
{
    File.WriteAllLines(claudeQueuePath, new[] { firstUserLine });
    claudeQueueCatalog.Accept(C("UserPromptSubmit", new { session_id = "claude_queue", prompt = "第一轮那句", transcript_path = claudeQueuePath }), claudeQueueT0);
    claudeQueueCatalog.Accept(C("PreToolUse", new { session_id = "claude_queue", transcript_path = claudeQueuePath, tool_name = "Bash", tool_input = new { command = "npm run build" } }), claudeQueueT0.AddSeconds(1));
    var claudeQueueStart = ClaudeQueueTask().StartedAt;

    // 用户中途又打了一句 → 转录本出现 enqueue。真实世界里这个"排队中"标记是**心跳**采到的
    // （Maintain 约 3 秒一拍、mtime 门控地重读转录本）——不是靠某个工具事件，所以这里也走心跳。
    File.WriteAllLines(claudeQueuePath, new[] { firstUserLine, enqueueLine });
    claudeQueueCatalog.Maintain(claudeQueueT0.AddSeconds(5));
    Check(ClaudeQueueTask().PromptQueued && ClaudeQueueTask().UserPrompt == "排队那句",
        $"claude queue: the inserted prompt is shown as queued (queued={ClaudeQueueTask().PromptQueued} / '{ClaudeQueueTask().UserPrompt}')");

    // 上一轮结束（投递发生在这之后 130~200ms —— 实测时序）
    claudeQueueCatalog.Accept(C("Stop", new { session_id = "claude_queue", transcript_path = claudeQueuePath, last_assistant_message = "第一轮完事" }), claudeQueueT0.AddSeconds(6));
    Check(ClaudeQueueTask().Lifecycle == AgentLifecycle.Succeeded, "claude queue: turn finished before delivery");
    var claudeQueueStep = ClaudeQueueTask().LatestStep;

    // 投递（dequeue 后变成一条新的用户消息）
    File.WriteAllLines(claudeQueuePath, new[] { firstUserLine, enqueueLine, dequeueLine, deliveredLine });
    claudeQueueCatalog.Accept(C("UserPromptSubmit", new { session_id = "claude_queue", prompt = "排队那句", transcript_path = claudeQueuePath }), claudeQueueT0.AddSeconds(7));
    Check(ClaudeQueueTask().Lifecycle == AgentLifecycle.Running, "claude queue: delivery goes running");
    Check(ClaudeQueueTask().UserPrompt == "排队那句", "claude queue: delivery updates the second line");
    Check(ClaudeQueueTask().StartedAt == claudeQueueStart, "claude queue: delivery must NOT restart the turn timer");
    Check(ClaudeQueueTask().LatestStep == claudeQueueStep, $"claude queue: delivery keeps the third line (got '{ClaudeQueueTask().LatestStep}')");

    // 这一轮也结束，用户**之后**才重新开口 → 该重置就重置
    claudeQueueCatalog.Accept(C("Stop", new { session_id = "claude_queue", transcript_path = claudeQueuePath, last_assistant_message = "第二轮完事" }), claudeQueueT0.AddSeconds(9));
    claudeQueueCatalog.Accept(C("UserPromptSubmit", new { session_id = "claude_queue", prompt = "真正的新一句", transcript_path = claudeQueuePath }), claudeQueueT0.AddSeconds(20));
    Check(ClaudeQueueTask().StartedAt == claudeQueueT0.AddSeconds(20), $"claude queue: a genuinely new prompt still resets (got {ClaudeQueueTask().StartedAt:HH:mm:ss.fff} expected {claudeQueueT0.AddSeconds(20):HH:mm:ss.fff})");
}
finally
{
    try { File.Delete(claudeQueuePath); } catch { /* 清理失败不影响断言 */ }
}

// ---- agy 排队被接单**不重置计时**（用户裁定：中途插入属于同一个 burst，计量不该归零）----
// 这条还顺手钉住一个坑：排队时 CheckAgyQueuedPrompts 会把排队文本先写进 task.UserPrompt
// （转录本尚未落盘），所以接单那一下 isNewPrompt 会假阳性命中 —— 判据必须是 `isNewPrompt && !wasQueued`。
var agyQueuePath = Path.Combine(Path.GetTempPath(), $"agy_queue_{Guid.NewGuid():N}.jsonl");
var previousHistoryPath = AgyHistoryReader.HistoryPathOverride;
AgyHistoryReader.HistoryPathOverride = agyQueuePath;
try
{
    var queueCatalog = new TaskCatalog();
    var queueT0 = DateTimeOffset.UtcNow;
    TaskSnapshot QueueTask() => queueCatalog.Projection().Tasks.First(t => t.Id == "p_queue");
    queueCatalog.Accept(A("PreInvocation", new { conversationId = "p_queue", workspacePaths = new[] { @"C:\test" } }), queueT0);
    queueCatalog.Accept(A("PreToolUse", new
    {
        conversationId = "p_queue",
        toolCall = new { name = "view_file", args = new { AbsolutePath = @"C:\test\a.md" } }
    }), queueT0.AddSeconds(1));
    var queueStartedAt = QueueTask().StartedAt;
    var queueStep = QueueTask().LatestStep;

    // 中途插入：history.jsonl 里出现一条更晚的用户输入，Maintain 扫到后置排队标记
    File.WriteAllLines(agyQueuePath, new[]
    {
        $"{{\"conversationId\":\"p_queue\",\"display\":\"插一句：顺便看看日志\",\"timestamp\":{queueT0.AddSeconds(5).ToUnixTimeMilliseconds()}}}",
    });
    queueCatalog.Maintain(queueT0.AddSeconds(6));
    Check(QueueTask().PromptQueued && QueueTask().UserPrompt == "插一句：顺便看看日志",
        $"agy queue: the inserted prompt is marked as queued (got queued={QueueTask().PromptQueued} / '{QueueTask().UserPrompt}')");

    // 接单：PreInvocation
    queueCatalog.Accept(A("PreInvocation", new { conversationId = "p_queue", workspacePaths = new[] { @"C:\test" } }), queueT0.AddSeconds(8));
    Check(!QueueTask().PromptQueued, "agy queue: pickup clears the queued flag");
    Check(QueueTask().Lifecycle == AgentLifecycle.Running, "agy queue: pickup goes running");
    Check(QueueTask().StartedAt == queueStartedAt, "agy queue: pickup must NOT restart the turn timer (same burst)");
    Check(QueueTask().LatestStep == queueStep, $"agy queue: pickup keeps the third line (got '{QueueTask().LatestStep}')");
}
finally
{
    AgyHistoryReader.HistoryPathOverride = previousHistoryPath;
    try { File.Delete(agyQueuePath); } catch { /* 清理失败不影响断言 */ }
}

// ---- opencode 离线兜底：与 agy 同一口径（10 分钟无更新 + 没在跑工具）----
var ocOfflineCatalog = new TaskCatalog();
var ocOfflineT0 = DateTimeOffset.UtcNow;
ocOfflineCatalog.Accept(H("chat.message", new { sessionID = "ses_off", text = "跑着" }), ocOfflineT0);
TaskSnapshot OcOffline() => ocOfflineCatalog.Projection().Tasks.First(t => t.Id == "ses_off");
Check(OcOffline().Lifecycle == AgentLifecycle.Running, "opencode offline: fixture is running");
ocOfflineCatalog.Maintain(ocOfflineT0.AddMinutes(11));
Check(OcOffline().Lifecycle == AgentLifecycle.Offline,
    $"opencode offline: a silent session goes offline after 10 minutes (got {OcOffline().Lifecycle})");

// 正在跑工具的不算静默：一个长命令不该被判离线
var ocBusyCatalog = new TaskCatalog();
var ocBusyT0 = DateTimeOffset.UtcNow;
ocBusyCatalog.Accept(H("chat.message", new { sessionID = "ses_busy", text = "跑构建" }), ocBusyT0);
ocBusyCatalog.Accept(H("message.part.updated", new
{
    sessionID = "ses_busy",
    part = new { id = "prt_busy", sessionID = "ses_busy", type = "tool", tool = "bash", state = new { status = "running", input = new { command = "npm run build" } } }
}), ocBusyT0.AddSeconds(1));
ocBusyCatalog.Maintain(ocBusyT0.AddMinutes(11));
Check(ocBusyCatalog.Projection().Tasks.First(t => t.Id == "ses_busy").Lifecycle == AgentLifecycle.Running,
    "opencode offline: a session running a tool is NOT marked offline");

// ---- 1a：agy 主对话的转录本里出现 `sender=<子代理>` 不得把它判成子会话（2026-10-11 实测事故）----
// 场景复刻：主对话 badb2e69 的转录本前 15 行里有一行是"子代理回报"的系统消息（sender=子代理id），
// 旧实现的转录本启发式把**自己的子代理**当成了父 → 主对话被判成子会话 → 顶层行被物理删除。
var agyBrain = Path.Combine(Path.GetTempPath(), "ap-agy-brain-" + Guid.NewGuid().ToString("N"));
var agyDb = Path.Combine(Path.GetTempPath(), "ap-agy-db-" + Guid.NewGuid().ToString("N") + ".db");
var previousBrainRoot = AgyTranscriptReader.BrainRoot;
var previousDbOverride = AgyConversationDatabase.DatabasePathOverride;
AgyTranscriptReader.BrainRoot = agyBrain;
AgyConversationDatabase.DatabasePathOverride = agyDb;
try
{
    using (var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={agyDb}"))
    {
        db.Open();
        using (var create = db.CreateCommand())
        {
            create.CommandText =
                "CREATE TABLE conversation_summaries (conversation_id TEXT, title TEXT, " +
                "parent_conversation_id TEXT, agent_name TEXT, status TEXT, nesting_depth INTEGER)";
            create.ExecuteNonQuery();
        }
        using var insert = db.CreateCommand();
        insert.CommandText =
            "INSERT INTO conversation_summaries VALUES " +
            "('root_agy', '文件读取与派发', '', '', 'CASCADE_RUN_STATUS_IDLE', 0), " +
            "('child_agy', '', 'root_agy', 'research', 'CASCADE_RUN_STATUS_IDLE', 1)";
        insert.ExecuteNonQuery();
    }

    // 主对话转录本：前 15 行里就有一行 `sender=<子代理>`（那行是子代理回报被转述进主对话的系统消息）
    var rootTranscript = AgyTranscriptReader.TranscriptPathFor("root_agy");
    Directory.CreateDirectory(Path.GetDirectoryName(rootTranscript)!);
    File.WriteAllLines(rootTranscript,
    [
        "{\"step_index\":0,\"type\":\"USER_INPUT\",\"content\":\"<USER_REQUEST>读文件</USER_REQUEST>\"}",
        "{\"step_index\":7,\"type\":\"SYSTEM_MESSAGE\",\"content\":\"[Message] timestamp=2026-10-11T16:50:38Z sender=child_agy\"}",
    ]);
    Check(AgyTranscriptReader.DetectParentFromTranscript(rootTranscript) == "child_agy",
        "agy: the transcript heuristic really does read a child's id as the parent (the trap this test covers)");

    var agyCatalog2 = new TaskCatalog();
    var pruneNow = DateTimeOffset.UtcNow;
    agyCatalog2.Accept(A("PreInvocation", new { conversationId = "root_agy", workspacePaths = new[] { @"C:\test" } }), pruneNow);
    Check(agyCatalog2.Projection().Tasks.Any(t => t.Id == "root_agy"),
        "agy: root conversation is created top-level (DB says parent is empty)");
    agyCatalog2.Maintain(pruneNow.AddSeconds(5));
    Check(agyCatalog2.Projection().Tasks.Any(t => t.Id == "root_agy"),
        "agy: root conversation survives the ghost prune (regression: it used to be deleted as a child)");

    // 真子会话仍然按老路子挂进父任务的子代理行（证明这套机制没被改坏）
    agyCatalog2.Accept(A("PreInvocation", new { conversationId = "child_agy", workspacePaths = new[] { @"C:\test" } }), pruneNow.AddSeconds(1));
    Check(agyCatalog2.Projection().Tasks.All(t => t.Id != "child_agy"), "agy: a real child never becomes a top-level card");
    var rootAfter = agyCatalog2.Projection().Tasks.First(t => t.Id == "root_agy");
    Check(rootAfter.Subagents.Count == 1 && rootAfter.Subagents[0].Id == "child_agy",
        $"agy: real child is mounted into the parent subagents (got {rootAfter.Subagents.Count})");
}
finally
{
    AgyTranscriptReader.BrainRoot = previousBrainRoot;
    AgyConversationDatabase.DatabasePathOverride = previousDbOverride;
    try { Directory.Delete(agyBrain, true); } catch { /* 清理失败不影响断言 */ }
    try { File.Delete(agyDb); } catch { /* 同上 */ }
}

// ---- 1b：agy 库跑 WAL 时，标题门控必须把 -wal 一起看（2026-10-16 用户实测报出）----
// 坑：库是 WAL 模式，写入落在 conversation_summaries.db-wal 上，**主库文件的 mtime 只在 checkpoint
// 时才动**（agy 一直开着库，checkpoint 很少发生）。原来两处门控 —— MaintainAgyDatabase 与
// LookupAgyConversation —— 都只看主库 mtime，于是水位线永不前进：缓存永不过期、CheckAgyTitles
// 永不重跑，**模型后来才生成的标题永远进不来**，面板一直显示项目名兜底。
// 实测现场：主库 mtime 停在 02:34，-wal 已经到 03:16，而 DB 里 title 早已有值。
var agyWalBrain = Path.Combine(Path.GetTempPath(), "ap-agy-wal-brain-" + Guid.NewGuid().ToString("N"));
var agyWalDb = Path.Combine(Path.GetTempPath(), "ap-agy-wal-db-" + Guid.NewGuid().ToString("N") + ".db");
var previousWalBrainRoot = AgyTranscriptReader.BrainRoot;
var previousWalDbOverride = AgyConversationDatabase.DatabasePathOverride;
AgyTranscriptReader.BrainRoot = agyWalBrain;
AgyConversationDatabase.DatabasePathOverride = agyWalDb;
try
{
    // **连接全程不关**：一关 SQLite 就 checkpoint 并把 -wal 收掉，主库 mtime 跟着变，测试就白做了。
    using var walConnection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={agyWalDb}");
    walConnection.Open();
    void WalExec(string sql)
    {
        using var sqliteCommand = walConnection.CreateCommand();
        sqliteCommand.CommandText = sql;
        sqliteCommand.ExecuteNonQuery();
    }
    WalExec("PRAGMA journal_mode=WAL");
    WalExec("CREATE TABLE conversation_summaries (conversation_id TEXT, title TEXT, " +
        "parent_conversation_id TEXT, agent_name TEXT, status TEXT, nesting_depth INTEGER)");
    WalExec("INSERT INTO conversation_summaries VALUES ('wal_agy', '', '', '', 'CASCADE_RUN_STATUS_IDLE', 0)");

    // 两个目录：A 走"周期刷新"（Maintain），B 走"事件读取缓存"（Accept）—— 两处门控各测一条。
    var walCatalogA = new TaskCatalog();
    var walCatalogB = new TaskCatalog();
    var walT0 = DateTimeOffset.UtcNow;
    walCatalogA.Accept(A("PreInvocation", new { conversationId = "wal_agy", workspacePaths = new[] { @"C:\test" } }), walT0);
    walCatalogB.Accept(A("PreInvocation", new { conversationId = "wal_agy", workspacePaths = new[] { @"C:\test" } }), walT0);
    Check(walCatalogA.Projection().Tasks.First(t => t.Id == "wal_agy").Title == "test",
        $"agy wal: DB has no title yet -> project name fallback (got '{walCatalogA.Projection().Tasks.First(t => t.Id == "wal_agy").Title}')");

    // 建立水位线：此刻两边都已经看过一遍库（标题还是空的）。
    walCatalogA.Maintain(walT0.AddSeconds(5));
    walCatalogB.Maintain(walT0.AddSeconds(5));

    var dbStampBefore = File.GetLastWriteTimeUtc(agyWalDb);
    WalExec("UPDATE conversation_summaries SET title='模型生成的标题' WHERE conversation_id='wal_agy'");
    var dbStampAfter = File.GetLastWriteTimeUtc(agyWalDb);

    // 自校验：这一步必须**真的没碰主库**，否则这个测试就没在测那个坑。
    Check(dbStampBefore == dbStampAfter,
        $"agy wal: the UPDATE really left the main db file untouched (trap precondition; {dbStampBefore:O} -> {dbStampAfter:O})");
    Check(File.Exists(agyWalDb + "-wal"), "agy wal: writes landed in the -wal file (journal really is WAL)");

    // ① 周期刷新这条路：门控认 -wal 才会重跑 CheckAgyTitles
    walCatalogA.Maintain(walT0.AddSeconds(10));
    Check(walCatalogA.Projection().Tasks.First(t => t.Id == "wal_agy").Title == "test · 模型生成的标题",
        $"agy wal: model-generated title lands on the Maintain path (got '{walCatalogA.Projection().Tasks.First(t => t.Id == "wal_agy").Title}')");

    // ② 事件读取这条路：门控认 -wal 才会清掉那份永不过期的缓存
    walCatalogB.Accept(A("PreInvocation", new { conversationId = "wal_agy", workspacePaths = new[] { @"C:\test" } }), walT0.AddSeconds(11));
    Check(walCatalogB.Projection().Tasks.First(t => t.Id == "wal_agy").Title == "test · 模型生成的标题",
        $"agy wal: model-generated title lands on the Accept path too (got '{walCatalogB.Projection().Tasks.First(t => t.Id == "wal_agy").Title}')");
}
finally
{
    AgyTranscriptReader.BrainRoot = previousWalBrainRoot;
    AgyConversationDatabase.DatabasePathOverride = previousWalDbOverride;
    try { Directory.Delete(agyWalBrain, true); } catch { /* 清理失败不影响断言 */ }
    foreach (var leftover in new[] { agyWalDb, agyWalDb + "-wal", agyWalDb + "-shm" })
        try { File.Delete(leftover); } catch { /* 同上 */ }
}

// ===================== 28. 工具路径缩写（三家共用：工作区相对 / 家目录 ~/ / 其它全路径）=====================
var previousHomeRoot = PathDisplay.HomeRootOverride;
PathDisplay.HomeRootOverride = @"C:\Users\tester";
try
{
    Check(PathDisplay.Shorten(@"F:\Git\proj\src\app.ts", @"F:\Git\proj") == "src/app.ts",
        $"path: in-workspace becomes relative (got {PathDisplay.Shorten(@"F:\Git\proj\src\app.ts", @"F:\Git\proj")})");
    Check(PathDisplay.Shorten(@"C:\Users\tester\.claude\x.json", @"F:\Git\proj") == "~/.claude/x.json",
        $"path: under the home directory becomes ~/… (got {PathDisplay.Shorten(@"C:\Users\tester\.claude\x.json", @"F:\Git\proj")})");
    Check(PathDisplay.Shorten(@"C:\Windows\win.ini", @"F:\Git\proj") == "C:/Windows/win.ini",
        $"path: outside stays a full path (got {PathDisplay.Shorten(@"C:\Windows\win.ini", @"F:\Git\proj")})");
    Check(PathDisplay.Shorten(@"F:\GitHub\a.ts", @"F:\Git") == "F:/GitHub/a.ts",
        "path: the workspace root must match on a directory boundary (F:\\Git must not swallow F:\\GitHub)");
    Check(PathDisplay.Shorten(@"F:\Git\proj", @"F:\Git\proj") == "proj",
        "path: the workspace root itself degrades to its leaf");

    // Claude：file_path 走缩写，command 里的路径一个字都不许动
    var pathCatalog = new TaskCatalog();
    var pathT0 = DateTimeOffset.UtcNow;
    pathCatalog.Accept(C("UserPromptSubmit", new { session_id = "claude_path", cwd = @"F:\Git\proj", prompt = "看代码" }), pathT0);
    pathCatalog.Accept(C("PreToolUse", new { session_id = "claude_path", cwd = @"F:\Git\proj", tool_name = "Read", tool_input = new { file_path = @"F:\Git\proj\src\app.ts" } }), pathT0.AddSeconds(1));
    TaskSnapshot PathTask() => pathCatalog.Projection().Tasks.First(t => t.Id == "claude_path");
    Check(PathTask().LatestStep == "Read src/app.ts", $"claude path: file_path shortened (got '{PathTask().LatestStep}')");
    pathCatalog.Accept(C("PreToolUse", new { session_id = "claude_path", cwd = @"F:\Git\proj", tool_name = "Read", tool_input = new { file_path = @"C:\Users\tester\.claude\settings.json" } }), pathT0.AddSeconds(2));
    Check(PathTask().LatestStep == "Read ~/.claude/settings.json", $"claude path: home becomes ~/… (got '{PathTask().LatestStep}')");
    pathCatalog.Accept(C("PreToolUse", new { session_id = "claude_path", cwd = @"F:\Git\proj", tool_name = "Bash", tool_input = new { command = @"cat F:\Git\proj\src\app.ts" } }), pathT0.AddSeconds(3));
    Check(PathTask().LatestStep == @"Bash cat F:\Git\proj\src\app.ts",
        $"claude path: a command string is never rewritten (got '{PathTask().LatestStep}')");

    // agy：工作区内相对；工作区外**全路径**（旧实现只留文件名，目录整条丢）
    var agyPathCatalog = new TaskCatalog();
    var agyPathT0 = DateTimeOffset.UtcNow;
    agyPathCatalog.Accept(A("PreInvocation", new { conversationId = "p_path", workspacePaths = new[] { @"F:\Git\proj" } }), agyPathT0);
    agyPathCatalog.Accept(A("PreToolUse", new
    {
        conversationId = "p_path",
        workspacePaths = new[] { @"F:\Git\proj" },
        toolCall = new { name = "view_file", args = new { AbsolutePath = @"F:\Git\proj\docs\a.md" } }
    }), agyPathT0.AddSeconds(1));
    TaskSnapshot AgyPathTask() => agyPathCatalog.Projection().Tasks.First(t => t.Id == "p_path");
    Check(AgyPathTask().LatestStep == "Read docs/a.md", $"agy path: in-workspace relative (got '{AgyPathTask().LatestStep}')");
    agyPathCatalog.Accept(A("PreToolUse", new
    {
        conversationId = "p_path",
        workspacePaths = new[] { @"F:\Git\proj" },
        toolCall = new { name = "view_file", args = new { AbsolutePath = @"C:\Windows\win.ini" } }
    }), agyPathT0.AddSeconds(2));
    Check(AgyPathTask().LatestStep == "Read C:/Windows/win.ini",
        $"agy path: outside the workspace keeps the full path, not just the file name (got '{AgyPathTask().LatestStep}')");

    // opencode：工作区根取自会话目录（chat.message 不带目录，所以要记在会话上）
    var ocPathCatalog = new TaskCatalog();
    var ocPathT0 = DateTimeOffset.UtcNow;
    ocPathCatalog.Accept(H("session.created", new { sessionID = "ses_path", info = new { id = "ses_path", directory = @"F:\Git\proj", time = new { created = 1 } } }), ocPathT0);
    ocPathCatalog.Accept(H("message.part.updated", new
    {
        sessionID = "ses_path",
        part = new { id = "prt_1", sessionID = "ses_path", type = "tool", tool = "read", state = new { status = "running", input = new { filePath = @"F:\Git\proj\src\b.ts" } } }
    }), ocPathT0.AddSeconds(1));
    Check(ocPathCatalog.Projection().Tasks.First(t => t.Id == "ses_path").LatestStep == "Read src/b.ts",
        $"opencode path: shortened against the session directory (got '{ocPathCatalog.Projection().Tasks.First(t => t.Id == "ses_path").LatestStep}')");
}
finally
{
    PathDisplay.HomeRootOverride = previousHomeRoot;
}

// ---- 拒绝 = 拒绝这一次调用，不是把任务判死（2026-10-11 用户实测报出"拒绝后直接变离线"）----
// 三家被拒之后都会把"用户拒绝了"回报给模型、回合继续；终态交给代理自己的结束信号。
var denyCatalog = new TaskCatalog();
var denyT0 = DateTimeOffset.UtcNow;
denyCatalog.Accept(C("UserPromptSubmit", new { session_id = "claude_deny", prompt = "删一下" }), denyT0);
denyCatalog.Accept(C("PermissionRequest", new { session_id = "claude_deny", tool_name = "Bash", tool_input = new { command = "rm -rf build" } }), denyT0.AddSeconds(1));
TaskSnapshot DenyTask() => denyCatalog.Projection().Tasks.First(t => t.Id == "claude_deny");
Check(DenyTask().Lifecycle == AgentLifecycle.WaitingApproval, "deny: fixture is waiting for approval");
var denyAck = denyCatalog.Perform(Guid.NewGuid(), new ControlPayload("claude_deny", ControlAction.Deny, null), new AllowResolver());
Check(denyAck.Result == ControlResult.Accepted, $"deny: accepted (got {denyAck.Result})");
Check(DenyTask().Lifecycle == AgentLifecycle.Running, $"deny: the task keeps running, it is not killed (got {DenyTask().Lifecycle})");
Check(DenyTask().Activity == AgentActivity.Thinking, "deny: back to thinking");
Check(DenyTask().LatestStep == "✗ 已拒绝 rm -rf build", $"deny: the refusal shows on the third line (got '{DenyTask().LatestStep}')");
Check(!DenyTask().Capabilities.Contains(TaskCapability.Approve), "deny: capabilities cleared");
// 之后代理自己结束 → 才是终态（不会被前面的拒绝卡在"已中断"）
denyCatalog.Accept(C("Stop", new { session_id = "claude_deny", last_assistant_message = "那我不删了" }), denyT0.AddSeconds(3));
Check(DenyTask().Lifecycle == AgentLifecycle.Succeeded, $"deny: the agent's own end signal decides the terminal state (got {DenyTask().Lifecycle})");

// ---- 挂起超时（三家统一）：等手机超过 10 分钟没人答 → **交回终端**，不判死、不判离线 ----
// 用户可能在终端上作答 ✓，而 Claude 没有"权限已作答"这类 hook ✗（只能靠后续事件间接同步 ✓），
// 此时若把行判死，手机上就会显示与事实相反的状态 ✗。交回 running 之后：代理活着 → 它自己的
// 事件继续更新 ✓；真的死了 → 静默离线规则自然接管（它认的就是 running + thinking ✓）。
var holdTimeoutCatalog = new TaskCatalog();
var holdT0 = DateTimeOffset.UtcNow;
holdTimeoutCatalog.Accept(C("UserPromptSubmit", new { session_id = "claude_timeout", prompt = "干个敏感活" }), holdT0);
holdTimeoutCatalog.Accept(C("PermissionRequest", new { session_id = "claude_timeout", tool_name = "Bash", tool_input = new { command = "rm -rf build" } }), holdT0.AddSeconds(1));
TaskSnapshot TimeoutTask() => holdTimeoutCatalog.Projection().Tasks.First(t => t.Id == "claude_timeout");
Check(TimeoutTask().Lifecycle == AgentLifecycle.WaitingApproval && TimeoutTask().Capabilities.Contains(TaskCapability.Approve),
    "hold timeout: fixture is waiting with capabilities");

// 9 分钟：还不到点，不许动
Check(holdTimeoutCatalog.ExpireStaleWaits(TimeSpan.FromMinutes(10), holdT0.AddMinutes(9)).Count == 0,
    "hold timeout: nothing expires before the limit");
Check(TimeoutTask().Lifecycle == AgentLifecycle.WaitingApproval, "hold timeout: still waiting at 9 minutes");

// 超过 10 分钟 → 交回终端
var expired = holdTimeoutCatalog.ExpireStaleWaits(TimeSpan.FromMinutes(10), holdT0.AddMinutes(11));
Check(expired.Count == 1 && expired[0] == "claude_timeout", $"hold timeout: the stale wait is handed back (got {expired.Count})");
Check(TimeoutTask().Lifecycle == AgentLifecycle.Running && TimeoutTask().Activity == AgentActivity.Thinking,
    $"hold timeout: back to running/thinking, not dead (got {TimeoutTask().Lifecycle}/{TimeoutTask().Activity})");
Check(!TimeoutTask().Capabilities.Contains(TaskCapability.Approve), "hold timeout: capabilities cleared");
Check(!holdTimeoutCatalog.Projection().PendingRequests.Any(r => r.TaskID == "claude_timeout"), "hold timeout: the card is dropped");
// 计时顺延：等待的 10 分钟不计入净耗时（离开等待态时 Upsert 自动补偿）
Check(TimeoutTask().StartedAt >= holdT0.AddMinutes(10), $"hold timeout: the wait is excluded from the clock (got {TimeoutTask().StartedAt:HH:mm:ss})");

// 提问同样适用（审批与提问同一口径）
var holdQuestionCatalog = new TaskCatalog();
var hqT0 = DateTimeOffset.UtcNow;
holdQuestionCatalog.Accept(H("chat.message", new { sessionID = "ses_hq", text = "问一下" }), hqT0);
holdQuestionCatalog.Accept(new OpenCodeHookPayload("question.asked", JsonSerializer.SerializeToElement(new { sessionID = "ses_hq", id = "q_x", questions = new[] { new { question = "选哪个？", options = new[] { "A", "B" } } } }, WireJson.Options), "question"), hqT0.AddSeconds(1));
Check(holdQuestionCatalog.ExpireStaleWaits(TimeSpan.FromMinutes(10), hqT0.AddMinutes(12)).Count == 1,
    "hold timeout: questions expire on the same rule");
Check(holdQuestionCatalog.Projection().Tasks.First(t => t.Id == "ses_hq").Lifecycle == AgentLifecycle.Running,
    "hold timeout: an expired question goes back to running too");

// ===================== 29. opencode 远程审批 / 作答（插件 v13）=====================
// 通道：插件看到权限询问事件 → 另挂一条连接给桥（带 hold 标记）→ 桥攥住 → 手机允许/拒绝
// → 桥回 {"decision":…} → 插件用 opencode 自己的接口替它答复。
// 桥侧要做的两件事：① 认出 hold 帧并挂住连接 ② 给这张卡补上能力位（否则手机上是点不动的假按钮）。
var ocHoldCatalog = new TaskCatalog();
var ocHoldT0 = DateTimeOffset.UtcNow;
ocHoldCatalog.Accept(H("chat.message", new { sessionID = "ses_hold", text = "删点东西" }), ocHoldT0);
TaskSnapshot OcHold() => ocHoldCatalog.Projection().Tasks.First(t => t.Id == "ses_hold");

// 普通 permission.asked（桥没攥着回话通道）→ 不给能力位
ocHoldCatalog.Accept(H("permission.asked", new { sessionID = "ses_hold", id = "per_a", permission = "bash", patterns = new[] { "rm -rf build" } }), ocHoldT0.AddSeconds(1));
Check(OcHold().Lifecycle == AgentLifecycle.WaitingApproval, "opencode hold: permission.asked goes waitingApproval");
Check(OcHold().Capabilities.Count == 0, $"opencode hold: a plain ask grants no approve capability (got {OcHold().Capabilities.Count})");

// hold 帧 → 给 Approve/Deny，手机才点得动
ocHoldCatalog.Accept(new OpenCodeHookPayload("permission.asked", JsonSerializer.SerializeToElement(new { sessionID = "ses_hold", id = "per_b", permission = "bash", patterns = new[] { "rm -rf build" } }, WireJson.Options), "approval"), ocHoldT0.AddSeconds(2));
Check(OcHold().Capabilities.Contains(TaskCapability.Approve) && OcHold().Capabilities.Contains(TaskCapability.Deny),
    "opencode hold: a held ask grants approve/deny");

// **只升不降**（实测踩过的坑）：hold 帧给上能力位之后，再来一条普通询问不许把它冲空 ——
// 插件当时同时发了两帧、谁后到谁说了算，"普通帧后到"就等于手机上只剩卡片没有按钮 ✗。
ocHoldCatalog.Accept(H("permission.asked", new { sessionID = "ses_hold", id = "per_c", permission = "bash", patterns = new[] { "rm -rf build" } }), ocHoldT0.AddSeconds(2).AddMilliseconds(500));
Check(OcHold().Capabilities.Contains(TaskCapability.Approve),
    $"opencode hold: a trailing plain ask must not clear the capabilities (got {OcHold().Capabilities.Count})");

// 提问：带 hold 才给作答能力位
ocHoldCatalog.Accept(H("question.asked", new { sessionID = "ses_hold", id = "q_1", questions = new[] { new { question = "选哪个？", options = new[] { "A", "B" } } } }), ocHoldT0.AddSeconds(3));
Check(!OcHold().Capabilities.Contains(TaskCapability.Answer), "opencode hold: a plain question has no answer capability");
ocHoldCatalog.Accept(new OpenCodeHookPayload("question.asked", JsonSerializer.SerializeToElement(new { sessionID = "ses_hold", id = "q_2", questions = new[] { new { question = "选哪个？", options = new[] { "A", "B" } } } }, WireJson.Options), "question"), ocHoldT0.AddSeconds(4));
Check(OcHold().Capabilities.Contains(TaskCapability.Answer), "opencode hold: a held question grants the answer capability");

// 回包编码：插件按这个解析
Check(System.Text.Encoding.UTF8.GetString(OpenCodeHookOutput.Decision(true)).Trim() == "{\"decision\":\"allow\"}",
    "opencode reply: allow encodes as expected");
Check(System.Text.Encoding.UTF8.GetString(OpenCodeHookOutput.Decision(false)).Trim() == "{\"decision\":\"deny\"}",
    "opencode reply: deny encodes as expected");
Check(System.Text.Encoding.UTF8.GetString(OpenCodeHookOutput.Answers([["B"]])).Trim() == "{\"answers\":[[\"B\"]]}",
    $"opencode reply: answers are per-question arrays (got {System.Text.Encoding.UTF8.GetString(OpenCodeHookOutput.Answers([["B"]])).Trim()})");
Check(System.Text.Encoding.UTF8.GetString(OpenCodeHookOutput.Answers([["A", "C"], ["B"]])).Trim() == "{\"answers\":[[\"A\",\"C\"],[\"B\"]]}",
    "opencode reply: multi-select and multi-question ride the two dimensions (inner = selected labels, outer = questions)");

// 载荷解析：hold / request_id 两个字段
var holdPayload = JsonSerializer.Deserialize<OpenCodeHookPayload>(
    "{\"event\":\"permission.asked\",\"hold\":\"approval\",\"request_id\":\"req_9\",\"properties\":{}}", WireJson.Options)!;
Check(holdPayload.Hold == "approval" && holdPayload.RequestID == "req_9", "opencode hold: payload carries hold + request_id");

// opencode reply: fullText 保留换行并注入 ResponseBody，text 供第三行预览
var ocReplyCatalog = new TaskCatalog();
var ocReplyT0 = DateTimeOffset.UtcNow;
ocReplyCatalog.Accept(H("session.created", new { id = "ses_reply_test", directory = "F:/Git/app" }), ocReplyT0);
ocReplyCatalog.Accept(H("chat.message", new { sessionID = "ses_reply_test", text = "写个表格" }), ocReplyT0.AddSeconds(1));
const string markdownWithTables = "# 标题\n| A | B |\n|---|---|\n| 1 | 2 |";
ocReplyCatalog.Accept(H("reply", new { sessionID = "ses_reply_test", text = "# 标题 | A | B |", fullText = markdownWithTables }), ocReplyT0.AddSeconds(2));
var ocReplyTask = ocReplyCatalog.Projection().Tasks.First(t => t.Id == "ses_reply_test");
Check(ocReplyTask.LatestStep == "# 标题 | A | B |", "opencode reply: LatestStep gets preview");
Check(ocReplyTask.ResponseBody == markdownWithTables, "opencode reply: ResponseBody gets full multiline markdown");

// 工具活动期间的 reply：第三行保护工具步不被冲掉，但 ResponseBody 必须收录完整正文（防丢字）
ocReplyCatalog.Accept(H("message.part.updated", new {
    sessionID = "ses_reply_test",
    part = new {
        id = "prt_tool_reply_test",
        callID = "call_reply_test",
        type = "tool",
        tool = "bash",
        state = new { status = "running", input = new { command = "npm test" } }
    }
}), ocReplyT0.AddSeconds(3));
var ocToolTask = ocReplyCatalog.Projection().Tasks.First(t => t.Id == "ses_reply_test");
Check(ocToolTask.LatestStep?.StartsWith("Bash") == true, "opencode reply tool guard: tool step is displayed");

const string midTurnFullText = "这是正文写完之后随工具带过来的权威全文，绝不能被丢弃";
ocReplyCatalog.Accept(H("reply", new { sessionID = "ses_reply_test", text = "这是正文写完...", fullText = midTurnFullText }), ocReplyT0.AddSeconds(4));
var ocMidTurnTask = ocReplyCatalog.Projection().Tasks.First(t => t.Id == "ses_reply_test");
Check(ocMidTurnTask.LatestStep?.StartsWith("Bash") == true, "opencode reply tool guard: tool step is NOT overwritten by reply");
Check(ocMidTurnTask.ResponseBody == midTurnFullText, "opencode reply tool guard: ResponseBody captures fullText even while tool is running");



// AgyUsageLoader: 解析真实的 agy /usage JSON 输出
const string sampleAgyQuotaJson = """
{
  "status": "SUCCESS",
  "command": {
    "name": "usage",
    "data": {
      "groups": [
        {
          "name": "Gemini Models",
          "buckets": [
            {
              "id": "gemini-weekly",
              "name": "Weekly Limit Remaining",
              "window": "weekly",
              "remaining_fraction": 0.368,
              "reset_time": "2026-10-15T08:11:53Z"
            },
            {
              "id": "gemini-5h",
              "name": "Five Hour Limit Remaining",
              "window": "5h",
              "remaining_fraction": 0.957,
              "reset_time": "2026-10-14T21:06:44Z"
            }
          ]
        },
        {
          "name": "Claude and GPT models",
          "buckets": [
            {
              "id": "3p-weekly",
              "name": "Weekly Limit Remaining",
              "window": "weekly",
              "remaining_fraction": 1.0,
              "reset_time": "2026-10-21T16:48:42Z"
            },
            {
              "id": "3p-5h",
              "name": "Five Hour Limit Remaining",
              "window": "5h",
              "remaining_fraction": 1.0,
              "reset_time": "2026-10-14T21:48:42Z"
            }
          ]
        }
      ]
    }
  }
}
""";

var testNow = DateTimeOffset.UtcNow;
var parsedQuota = AgyUsageLoader.ParseQuotaJson(sampleAgyQuotaJson, testNow);
Check(parsedQuota is not null, "agy quota: JSON parsed successfully");
Check(parsedQuota!.Value.PlanType == "Antigravity", "agy quota: PlanType is Antigravity");
// 手机上那行订阅名直接取 limitID（App 只做大写），所以这里钉住文案。
Check(parsedQuota!.Value.LimitID == "Google AI Pro", $"agy quota: LimitID is the subscription name (got '{parsedQuota.Value.LimitID}')");
Check(parsedQuota!.Value.Windows.Count == 4, $"agy quota: 4 windows extracted (got {parsedQuota.Value.Windows.Count})");

var gemini5h = parsedQuota.Value.Windows.FirstOrDefault(w => w.Key == "primary");
Check(gemini5h is not null && Math.Abs(gemini5h.RemainingPercentage - 95.7) < 0.1, "agy quota: primary is Gemini 5H ~95.7%");
Check(gemini5h?.WindowMinutes == 300, "agy quota: primary windowMinutes=300");

var geminiWeekly = parsedQuota.Value.Windows.FirstOrDefault(w => w.Key == "secondary");
Check(geminiWeekly is not null && Math.Abs(geminiWeekly.RemainingPercentage - 36.8) < 0.1, "agy quota: secondary is Gemini Weekly ~36.8%");
Check(geminiWeekly?.WindowMinutes == 10080, "agy quota: secondary windowMinutes=10080");

var threeP5h = parsedQuota.Value.Windows.FirstOrDefault(w => w.Key == "3p-5h");
Check(threeP5h is not null && Math.Abs(threeP5h.RemainingPercentage - 100.0) < 0.1, "agy quota: 3p-5h is 100%");

// AgyUsageLoader.Load 联动测试（mock brain 目录与 transcript.jsonl）
var tempBrainDir = Path.Combine(Path.GetTempPath(), "agentpager_test_brain_" + Guid.NewGuid().ToString("N"));
var tempSessionDir = Path.Combine(tempBrainDir, "test_session", ".system_generated", "logs");
Directory.CreateDirectory(tempSessionDir);
var tempTranscript = Path.Combine(tempSessionDir, "transcript.jsonl");

var mockDate = testNow.Date.ToString("yyyy-MM-dd");
File.WriteAllText(tempTranscript,
    $"{{\"step_index\":1,\"type\":\"PLANNER_RESPONSE\",\"created_at\":\"{mockDate}T12:00:00Z\",\"input_tokens\":1000,\"cache_read_tokens\":500,\"output_tokens\":200}}\n" +
    $"{{\"step_index\":2,\"type\":\"PLANNER_RESPONSE\",\"created_at\":\"{mockDate}T12:05:00Z\",\"input_tokens\":2000,\"cache_read_tokens\":1000,\"output_tokens\":300}}\n");

var prevBrainRoot = AgyTranscriptReader.BrainRoot;
AgyTranscriptReader.BrainRoot = tempBrainDir;
AgyUsageLoader.QuotaJsonProviderOverride = () => sampleAgyQuotaJson;

try
{
    var agyLoader = new AgyUsageLoader();
    var agySnapshot = agyLoader.Load(testNow);
    Check(agySnapshot is not null, "agy usage loader: snapshot loaded");
    Check(agySnapshot!.PlanType == "Antigravity", "agy usage loader: planType is Antigravity");
    Check(agySnapshot.DailyUsage.Count == 90, "agy usage loader: dailyUsage has 90 points");

    var todayPoint = agySnapshot.DailyUsage.FirstOrDefault(d => d.Date == mockDate);
    Check(todayPoint is not null && todayPoint.TotalTokens == (1000 + 500 + 200 + 2000 + 1000 + 300),
        $"agy usage loader: aggregated total tokens for today is 5000 (got {todayPoint?.TotalTokens})");
    Check(todayPoint?.InputTokens == 3000, $"agy usage loader: input tokens = 3000 (got {todayPoint?.InputTokens})");
    Check(todayPoint?.CachedInputTokens == 1500, $"agy usage loader: cached tokens = 1500 (got {todayPoint?.CachedInputTokens})");
    Check(todayPoint?.OutputTokens == 500, $"agy usage loader: output tokens = 500 (got {todayPoint?.OutputTokens})");
}
finally
{
    AgyTranscriptReader.BrainRoot = prevBrainRoot;
    AgyUsageLoader.QuotaJsonProviderOverride = null;
    try { Directory.Delete(tempBrainDir, recursive: true); } catch { }
}

// ── MarkRead 状态操作与排序稳定测试 ──
var tOlder = testNow.AddMinutes(-10);
var tNewer = testNow.AddMinutes(-5);
var taskOlder = new TaskSnapshot("task_older", AgentSource.CodexCLI, "proj", "proj", null, null, null, [],
    AgentLifecycle.Succeeded, null, tOlder, tOlder, tOlder, true, false, false, []);
var taskNewer = new TaskSnapshot("task_newer", AgentSource.CodexCLI, "proj", "proj", null, null, null, [],
    AgentLifecycle.Succeeded, null, tNewer, tNewer, tNewer, false, false, false, []);

var markReadCatalog = new TaskCatalog(new[] { taskOlder, taskNewer });
var beforeMarkRead = markReadCatalog.Projection().Tasks;
Check(beforeMarkRead[0].Id == "task_newer" && beforeMarkRead[1].Id == "task_older", "markRead: newer task is ahead of older task initially");

// 对 older 标记已读
markReadCatalog.Perform(Guid.NewGuid(), new ControlPayload("task_older", ControlAction.MarkRead, null), new NullResolver());
var afterMarkRead = markReadCatalog.Projection().Tasks;
var olderAfter = afterMarkRead.First(t => t.Id == "task_older");
Check(!olderAfter.IsUnread, "markRead: task is now marked as read");
Check(olderAfter.UpdatedAt == tOlder, $"markRead: UpdatedAt remains unchanged (expected {tOlder}, got {olderAfter.UpdatedAt})");
Check(afterMarkRead[0].Id == "task_newer" && afterMarkRead[1].Id == "task_older", "markRead: older task does NOT jump ahead of newer task");

// Command Code（cmdc）额度解析：样本是 2026-10-15 从 api.commandcode.ai 真实抓下来的响应。
// 字段名或单位错一个，手机上就是空白或离谱数字，而这类错误不会在任何地方报错。
const string cmdcCredits = """
{"credits":{"belowThreshold":false,"creditThreshold":0,"monthlyCredits":0.1311898708,"purchasedCredits":0,"freeCredits":0},
 "windowLimits":{"limited":true,"exceeded":null,
   "fiveHour":{"used":0.846497784,"cap":3,"exceeded":false,"resetAt":1792012561503},
   "weekly":{"used":3.868543266,"cap":6,"exceeded":false,"resetAt":1792560907759}},
 "sandboxAccess":false}
""";
const string cmdcSubscriptions = """
{"success":true,"data":{"id":"sub_x","status":"active","currentPeriodEnd":"2026-11-04T11:56:42.000Z","planId":"individual-go-v1"}}
""";
const string cmdcSummary = """
{"totalCount":2094,"totalCredits":9.859189651200007,
 "daily":[{"date":"2026-10-04","tokens":90721819,"requests":222},{"date":"2026-10-14","tokens":388623077,"requests":1098}]}
""";

var cmdcWindows = CommandCodeUsageLoader.ParseWindows(cmdcCredits, cmdcSubscriptions, DateTimeOffset.Now);
Check(cmdcWindows.Count == 3, $"cmdc: three windows (5h/weekly/monthly), got {cmdcWindows.Count}");

var cmdcFiveHour = cmdcWindows.FirstOrDefault(w => w.Key == "cc-5h");
Check(cmdcFiveHour is not null, "cmdc: five-hour window present");
Check(cmdcFiveHour is not null && Math.Abs(cmdcFiveHour.UsedPercentage - 28.2166) < 0.01,
    $"cmdc: five-hour used% = used/cap (got {cmdcFiveHour?.UsedPercentage:0.###})");
Check(cmdcFiveHour?.RemainingPercentage is > 71 and < 72, $"cmdc: five-hour remaining ~71.8% (got {cmdcFiveHour?.RemainingPercentage:0.###})");
Check(cmdcFiveHour?.WindowMinutes == 300, "cmdc: five-hour window is 300 minutes");
Check(cmdcFiveHour?.ResetsAt is not null, "cmdc: five-hour reset time parsed");

var cmdcWeekly = cmdcWindows.FirstOrDefault(w => w.Key == "cc-weekly");
Check(cmdcWeekly is not null && Math.Abs(cmdcWeekly.UsedPercentage - 64.4757) < 0.01,
    $"cmdc: weekly used% (got {cmdcWeekly?.UsedPercentage:0.###})");
Check(cmdcWeekly?.WindowMinutes == 10080, "cmdc: weekly window is 10080 minutes");

var cmdcMonthly = cmdcWindows.FirstOrDefault(w => w.Key == "cc-monthly");
Check(cmdcMonthly?.Detail is not null && cmdcMonthly.Detail.Contains("$0.13"),
    $"cmdc: monthly is a text row showing the balance (got '{cmdcMonthly?.Detail}')");
Check(cmdcMonthly?.ResetsAt == new DateTimeOffset(2026, 11, 4, 11, 56, 42, TimeSpan.Zero),
    $"cmdc: monthly reset = subscription period end (got {cmdcMonthly?.ResetsAt})");

// 套餐名：App 显示成 "<LIMITID> 订阅"，所以从 planId 推出 "Command Code Go"。
Check(CommandCodeUsageLoader.PlanLabel(cmdcSubscriptions) == "Command Code Go",
    $"cmdc: plan label from planId (got '{CommandCodeUsageLoader.PlanLabel(cmdcSubscriptions)}')");
Check(CommandCodeUsageLoader.PlanLabel("{\"data\":{\"planId\":\"team-max-v2\"}}") == "Command Code Max",
    "cmdc: other tiers map too (team-max-v2 -> Max)");
Check(CommandCodeUsageLoader.PlanLabel("{}") == "Command Code", "cmdc: unknown plan falls back to the product name");

var cmdcDaily = CommandCodeUsageLoader.ParseDaily(cmdcSummary, DateTimeOffset.Now);
Check(cmdcDaily.Count == 2 && cmdcDaily[0].Date == "2026-10-04" && cmdcDaily[0].TotalTokens == 90721819,
    $"cmdc: daily usage parsed (got {cmdcDaily.Count} points, first={cmdcDaily.FirstOrDefault()?.Date}/{cmdcDaily.FirstOrDefault()?.TotalTokens})");

// 缺字段不能崩：credits 缺席时应当返回空列表，交给上层决定是否沿用缓存。
Check(CommandCodeUsageLoader.ParseWindows("{}", null, DateTimeOffset.Now).Count == 0, "cmdc: empty payload yields no windows");
Check(CommandCodeUsageLoader.ParseDaily(null, DateTimeOffset.Now).Count == 0, "cmdc: missing summary yields no daily points");
Check(CommandCodeUsageLoader.SubscriptionPeriodEnd("not json") is null, "cmdc: malformed subscription json is tolerated");

// ---------------------------------------------------------------------------
// 远程 Prompt 全链路单测（OpenCode / ClaudeCode / Antigravity）
// ---------------------------------------------------------------------------
// 1. OpenCode 运行中 Prompt 下发 -> 队列标记
var opencodeCatalog = new TaskCatalog();
var remoteOcT0 = DateTimeOffset.UtcNow;
opencodeCatalog.Accept(H("session.created", new { id = "oc-1", directory = "F:/Git/repo" }), remoteOcT0);
opencodeCatalog.Accept(H("chat.message", new { sessionID = "oc-1", text = "初始提示词" }), remoteOcT0.AddSeconds(1));
var ocRec = new RecordingResolver();
var ocAck = opencodeCatalog.Perform(Guid.NewGuid(), new ControlPayload("oc-1", ControlAction.Prompt, "追加命令：查一下状态"), ocRec);
Check(ocAck.Result == ControlResult.Accepted, "prompt: opencode running prompt accepted");
Check(ocRec.PromptSessionID == "oc-1" && ocRec.PromptSource == AgentSource.OpenCode && ocRec.PromptText == "追加命令：查一下状态" && ocRec.PromptIsRunning == true, "prompt: opencode resolver recorded running");
var ocTask = opencodeCatalog.Projection().Tasks.First(t => t.Id == "oc-1");
Check(ocTask.PromptQueued && ocTask.UserPrompt == "追加命令：查一下状态", "prompt: opencode queued state set");

// 2. OpenCode 空闲态下发 Prompt -> 唤醒新一轮
opencodeCatalog.Accept(H("session.idle", new { sessionID = "oc-1" }), remoteOcT0.AddSeconds(2));
var ocAck2 = opencodeCatalog.Perform(Guid.NewGuid(), new ControlPayload("oc-1", ControlAction.Prompt, "开启下一轮新需求"), ocRec);
Check(ocAck2.Result == ControlResult.Accepted, "prompt: opencode idle prompt accepted");
var ocTask2 = opencodeCatalog.Projection().Tasks.First(t => t.Id == "oc-1");
Check(ocTask2.Lifecycle == AgentLifecycle.Running && !ocTask2.PromptQueued && ocTask2.UserPrompt == "开启下一轮新需求", "prompt: opencode idle prompt starts new turn");

// 3. Claude Code Prompt 下发与存活性一票否决门禁
var claudeCatalog = new TaskCatalog();
claudeCatalog.ClaudeRegistryScanner = () => new Dictionary<string, ClaudeSessionRegistry.Entry>
{
    ["cc-1"] = new("cc-1", null, null, "busy", null, null, Environment.ProcessId, null)
};
claudeCatalog.Accept(C("SessionStart", new { session_id = "cc-1" }));
claudeCatalog.Accept(C("UserPromptSubmit", new { session_id = "cc-1", prompt = "第一问" }));
Check(claudeCatalog.Projection().Tasks.First(t => t.Id == "cc-1").Capabilities.Contains(TaskCapability.Prompt), "prompt: claude has prompt capability when process alive");
var ccRec = new RecordingResolver();
var ccAck = claudeCatalog.Perform(Guid.NewGuid(), new ControlPayload("cc-1", ControlAction.Prompt, "帮我看看编译错误"), ccRec);
Check(ccAck.Result == ControlResult.Accepted, "prompt: claude prompt accepted");
Check(ccRec.PromptSessionID == "cc-1" && ccRec.PromptSource == AgentSource.ClaudeCode && ccRec.PromptText == "帮我看看编译错误", "prompt: claude resolver recorded");

// 3.1 Claude Code 前台进程死亡 -> 能力位一票否决
claudeCatalog.ClaudeRegistryScanner = () => new();
Check(!claudeCatalog.Projection().Tasks.First(t => t.Id == "cc-1").Capabilities.Contains(TaskCapability.Prompt), "prompt: claude loses prompt capability when process dead");
var ccDeadAck = claudeCatalog.Perform(Guid.NewGuid(), new ControlPayload("cc-1", ControlAction.Prompt, "死进程不可发"), ccRec);
Check(ccDeadAck.Result == ControlResult.Unsupported, "prompt: rejected when claude process dead");

// 3.2 OpenCode 控制通道断开 -> 能力位一票否决
var ocDisconnectedCatalog = new TaskCatalog();
ocDisconnectedCatalog.HasOpenCodeControl = () => false;
ocDisconnectedCatalog.Accept(H("session.idle", new { sessionID = "oc-dead" }));
Check(!ocDisconnectedCatalog.Projection().Tasks.First(t => t.Id == "oc-dead").Capabilities.Contains(TaskCapability.Prompt), "prompt: opencode loses prompt capability when control channel broken");
var ocDeadAck = ocDisconnectedCatalog.Perform(Guid.NewGuid(), new ControlPayload("oc-dead", ControlAction.Prompt, "通道断开不可发"), ocRec);
Check(ocDeadAck.Result == ControlResult.Unsupported, "prompt: rejected when opencode channel broken");

// 4. Antigravity Prompt 下发与工作区记录（回调链，注入腿在 5.1c / agy host 段单测）
var remoteAgyCatalog = new TaskCatalog();
remoteAgyCatalog.Accept(new AgyHookPayload("PreInvocation", ConversationId: "agy-1", WorkspacePaths: ["F:\\Git\\agy-proj"], HostPid: Environment.ProcessId));
var agyRec = new RecordingResolver();
var agyAck = remoteAgyCatalog.Perform(Guid.NewGuid(), new ControlPayload("agy-1", ControlAction.Prompt, "重构一下模型层"), agyRec);
Check(agyAck.Result == ControlResult.Accepted, "prompt: agy running prompt accepted");
Check(agyRec.PromptSessionID == "agy-1" && agyRec.PromptWorkspace == "F:\\Git\\agy-proj" && agyRec.PromptIsRunning == true, "prompt: agy workspace passed to resolver");

// 5.1 ClaudePromptDispatcher 队列测试
var prevClaudeStarter = ClaudePeerClient.ProcessStarter;
try
{
    ClaudePeerClient.ProcessStarter = _ => null;
    ClaudePromptDispatcher.QueueOrExecute("claude-q1", "排队注入的指令1", "F:\\Git\\proj", isRunning: true);
    ClaudePromptDispatcher.QueueOrExecute("claude-q1", "排队注入的指令2", "F:\\Git\\proj", isRunning: true);
    Check(ClaudePromptDispatcher.HasQueued("claude-q1"), "claude dispatcher: prompt queued when running");
    Check(ClaudePromptDispatcher.QueuedCount("claude-q1") == 2, "claude dispatcher: queue count is 2");
    Check(ClaudePromptDispatcher.TryDrainQueued("claude-q1"), "claude dispatcher: first prompt drained");
    Check(ClaudePromptDispatcher.QueuedCount("claude-q1") == 1, "claude dispatcher: queue count is 1 after first drain");
    Check(ClaudePromptDispatcher.TryDrainQueued("claude-q1"), "claude dispatcher: second prompt drained");
    Check(!ClaudePromptDispatcher.HasQueued("claude-q1"), "claude dispatcher: queue is empty after draining all");
}
finally
{
    ClaudePeerClient.ProcessStarter = prevClaudeStarter;
}

// 5.1b 桥端队列硬化（2026-10-17）：drain 发送失败保留队头 / TTL / 离线与回收清队列。
// drain 发送失败必须保留队头等下个 Stop 重试（旧实现先出队再发送，失败=静默丢）。
var failPrevStarter = ClaudePeerClient.ProcessStarter;
var failPrevScanner = ClaudePeerClient.RegistryScanner;
try
{
    ClaudePeerClient.RegistryScanner = () => new();
    ClaudePeerClient.ProcessStarter = _ => throw new InvalidOperationException("通道已断");
    ClaudePromptDispatcher.QueueOrExecute("claude-qf", "发不出去的那条", null, isRunning: true);
    Check(!ClaudePromptDispatcher.TryDrainQueued("claude-qf"), "claude queue: failed drain keeps the head");
    Check(ClaudePromptDispatcher.QueuedCount("claude-qf") == 1, "claude queue: nothing dequeued on failure");
}
finally
{
    ClaudePeerClient.ProcessStarter = failPrevStarter;
    ClaudePeerClient.RegistryScanner = failPrevScanner;
}
// 通道恢复后下个 Stop 放行同一条（还躺在内存里）。
var recoverPrevStarter = ClaudePeerClient.ProcessStarter;
var recoverPrevScanner = ClaudePeerClient.RegistryScanner;
try
{
    ClaudePeerClient.RegistryScanner = () => new();
    ClaudePeerClient.ProcessStarter = _ => null;
    Check(ClaudePromptDispatcher.TryDrainQueued("claude-qf"), "claude queue: retry after recovery drains");
    Check(!ClaudePromptDispatcher.HasQueued("claude-qf"), "claude queue: empty after recovered drain");
}
finally
{
    ClaudePeerClient.ProcessStarter = recoverPrevStarter;
    ClaudePeerClient.RegistryScanner = recoverPrevScanner;
}

// TTL：超期条目在 sweep/drain 时就地丢弃，不能无限期卡住队头。
ClaudePromptDispatcher.QueueOrExecute("claude-qt", "过期的那条", null, isRunning: true);
Check(ClaudePromptDispatcher.SweepExpired("claude-qt", ClaudePromptDispatcher.QueueEntryTtl, now: DateTimeOffset.UtcNow + TimeSpan.FromMinutes(31)) == 1,
    "claude queue: expired entry swept");
Check(!ClaudePromptDispatcher.HasQueued("claude-qt"), "claude queue: swept queue reads empty");
ClaudePromptDispatcher.QueueOrExecute("claude-qt2", "新鲜的那条", null, isRunning: true);
Check(ClaudePromptDispatcher.SweepExpired("claude-qt2", ClaudePromptDispatcher.QueueEntryTtl) == 0,
    "claude queue: fresh entry survives sweep");
Check(ClaudePromptDispatcher.ClearQueued("claude-qt2") == 1, "claude queue: clear returns dropped count");
Check(ClaudePromptDispatcher.ClearQueued("claude-qt2") == 0, "claude queue: clearing an empty queue drops nothing");

// 离线清队列：会话死掉时桥端排队文本不可能再放行，连徽章一起熄灭（旧实现两边都留）。
// 注：RecordingResolver 只记录不转发，这里用 QueueOrExecute 按生产路径（HookTcpServer）真实入队。
var qoCatalog = new TaskCatalog
{
    ClaudeRegistryScanner = () => new Dictionary<string, ClaudeSessionRegistry.Entry>
    {
        ["claude-qo"] = new("claude-qo", null, null, "idle", null, null, Environment.ProcessId, null),
    },
};
var qoT0 = DateTimeOffset.UtcNow;
qoCatalog.Accept(C("UserPromptSubmit", new { session_id = "claude-qo", prompt = "在跑的活" }), qoT0);
var qoAck = qoCatalog.Perform(Guid.NewGuid(), new ControlPayload("claude-qo", ControlAction.Prompt, "排队那条"), new RecordingResolver());
Check(qoAck.Result == ControlResult.Accepted, "claude queue: prompt queued while running");
ClaudePromptDispatcher.QueueOrExecute("claude-qo", "排队那条", null, isRunning: true);
Check(ClaudePromptDispatcher.HasQueued("claude-qo"), "claude queue: bridge holds the text");
// 11 分钟无音讯 + 注册表/宿主双无（静态 Scan 里没有 claude-qo）= 离线。
qoCatalog.Maintain(qoT0.AddMinutes(11));
var qoTask = qoCatalog.Projection().Tasks.First(t => t.Id == "claude-qo");
Check(qoTask.Lifecycle == AgentLifecycle.Offline, $"claude queue: dead session goes offline (got {qoTask.Lifecycle})");
Check(!ClaudePromptDispatcher.HasQueued("claude-qo"), "claude queue: offline drops the bridge queue");
Check(!qoTask.PromptQueued, "claude queue: offline clears the queued badge");

// 任务回收清队列：终态 30 分钟后 Normalize 回收任务，桥端队列不能留下僵尸。
var purgeCatalog = new TaskCatalog
{
    ClaudeRegistryScanner = () => new Dictionary<string, ClaudeSessionRegistry.Entry>
    {
        ["claude-qp"] = new("claude-qp", null, null, "idle", null, null, Environment.ProcessId, null),
    },
};
var purgeT0 = DateTimeOffset.UtcNow;
purgeCatalog.Accept(C("UserPromptSubmit", new { session_id = "claude-qp", prompt = "在跑的活" }), purgeT0);
var purgeAck = purgeCatalog.Perform(Guid.NewGuid(), new ControlPayload("claude-qp", ControlAction.Prompt, "没等到的那条"), new RecordingResolver());
Check(purgeAck.Result == ControlResult.Accepted, "claude queue: prompt queued before the turn ends");
ClaudePromptDispatcher.QueueOrExecute("claude-qp", "没等到的那条", null, isRunning: true);
Check(ClaudePromptDispatcher.HasQueued("claude-qp"), "claude queue: bridge holds the text before purge");
purgeCatalog.Accept(C("Stop", new { session_id = "claude-qp" }), purgeT0.AddSeconds(2));
purgeCatalog.Accept(C("SessionEnd", new { session_id = "claude-qp" }), purgeT0.AddSeconds(3));
purgeCatalog.Maintain(purgeT0.AddMinutes(34));
Check(!purgeCatalog.Projection().Tasks.Any(t => t.Id == "claude-qp"), "claude queue: terminal task purged after 30 minutes");
Check(!ClaudePromptDispatcher.HasQueued("claude-qp"), "claude queue: purge drops the bridge queue");

// 持久化 round-trip：导出 → 清空 → 导入恢复，顺序不变（FIFO），drain 照常工作。
var persistPrevStarter = ClaudePeerClient.ProcessStarter;
var persistPrevScanner = ClaudePeerClient.RegistryScanner;
try
{
    ClaudePeerClient.RegistryScanner = () => new();
    var drainedTexts = new List<string>();
    ClaudePeerClient.ProcessStarter = psi => { drainedTexts.Add(psi.ArgumentList[3]); return null; };
    ClaudePromptDispatcher.QueueOrExecute("claude-qx", "持久第一条", null, isRunning: true);
    ClaudePromptDispatcher.QueueOrExecute("claude-qx", "持久第二条", null, isRunning: true);
    var exported = ClaudePromptDispatcher.ExportQueues();
    Check(exported.TryGetValue("claude-qx", out var expItems) && expItems.Count == 2 &&
          expItems[0].Prompt == "持久第一条" && expItems[1].Prompt == "持久第二条",
        "claude queue: export keeps order");
    Check(ClaudePromptDispatcher.ClearQueued("claude-qx") == 2, "claude queue: cleared before import");
    Check(ClaudePromptDispatcher.ImportQueues(exported) == 2, "claude queue: import restores all");
    Check(ClaudePromptDispatcher.TryDrainQueued("claude-qx"), "claude queue: drain after import works");
    Check(drainedTexts.Count == 1 && drainedTexts[0] == "持久第一条",
        $"claude queue: FIFO order preserved across restart (got '{string.Join("|", drainedTexts)}')");
    Check(ClaudePromptDispatcher.ClearQueued("claude-qx") == 1, "claude queue: cleanup");
}
finally
{
    ClaudePeerClient.ProcessStarter = persistPrevStarter;
    ClaudePeerClient.RegistryScanner = persistPrevScanner;
}

// 导入即过期：重启间隔里超 TTL 的不等下次 Stop，直接丢弃记数。
var staleState = new Dictionary<string, List<QueuedPromptRecord>>(StringComparer.Ordinal)
{
    ["claude-qy"] = new()
    {
        new("过期的那条", null, DateTimeOffset.UtcNow - TimeSpan.FromMinutes(31)),
        new("新鲜的那条", null, DateTimeOffset.UtcNow),
    },
};
Check(ClaudePromptDispatcher.ImportQueues(staleState) == 1, "claude queue: import drops expired entries");
Check(ClaudePromptDispatcher.QueuedCount("claude-qy") == 1 &&
      ClaudePromptDispatcher.PeekLastText("claude-qy") == "新鲜的那条",
    "claude queue: only the fresh entry survives import");
Check(ClaudePromptDispatcher.ClearQueued("claude-qy") == 1, "claude queue: cleanup");

// 徽章自愈：重启后任务行是 hook 重建的（徽章没了），队列还在——Maintain 补回徽章。
var healCatalog = new TaskCatalog();
var healT0 = DateTimeOffset.UtcNow;
healCatalog.Accept(C("UserPromptSubmit", new { session_id = "claude-qh", prompt = "重建的那轮" }), healT0);
ClaudePromptDispatcher.QueueOrExecute("claude-qh", "恢复的那条", null, isRunning: true);
Check(!healCatalog.Projection().Tasks.First(t => t.Id == "claude-qh").PromptQueued,
    "claude queue: rebuilt task starts without the badge");
healCatalog.Maintain(healT0.AddSeconds(5));
var healTask = healCatalog.Projection().Tasks.First(t => t.Id == "claude-qh");
Check(healTask.PromptQueued && healTask.UserPrompt == "恢复的那条",
    $"claude queue: maintain heals the badge from the queue (queued={healTask.PromptQueued} / '{healTask.UserPrompt}')");
Check(ClaudePromptDispatcher.ClearQueued("claude-qh") == 1, "claude queue: cleanup");

// 文件 round-trip（独立文件，不进 tasks.json：非终态行重启时整行丢弃，塞进去等于没存）。
var qpPath = Path.Combine(Path.GetTempPath(), "ap-queue-persist-" + Guid.NewGuid().ToString("N") + ".json");
try
{
    var qpStore = new PromptQueuePersistence(qpPath);
    qpStore.Save(new Dictionary<string, List<QueuedPromptRecord>>(StringComparer.Ordinal)
    {
        ["claude-qz"] = new() { new("落盘那条", @"F:\Git\demo", DateTimeOffset.UtcNow) },
    });
    var qpLoaded = qpStore.Load();
    Check(qpLoaded.TryGetValue("claude-qz", out var qpItems) && qpItems.Count == 1 &&
          qpItems[0].Prompt == "落盘那条" && qpItems[0].Cwd == @"F:\Git\demo",
        "claude queue: file round-trip keeps text and cwd");
}
finally
{
    try { File.Delete(qpPath); } catch { }
}

// 5.1c agy 到达即注入（2026-10-19 用户裁定：桥端队列撤除，排队/打断交 agy 的 Queued Messages）
{
    var previousInjector = AgyCliDispatcher.Injector;
    var previousHostPid = AgyCliDispatcher.HostPidProvider;
    try
    {
        var injected = new List<(int Pid, string Text)>();
        var livePid = Environment.ProcessId;
        AgyCliDispatcher.HostPidProvider = _ => livePid;
        AgyCliDispatcher.Injector = (int pid, string text, out string reason) =>
        {
            injected.Add((pid, text));
            reason = "ok";
            return true;
        };

        // 运行中到达 → 立刻注入（不等 Stop、不进任何队列）
        Check(AgyCliDispatcher.Dispatch("agy-live", "回合中插话", null, isRunning: true), "agy inject: running prompt dispatched immediately");
        // 完成态到达 → 同样立刻注入
        Check(AgyCliDispatcher.Dispatch("agy-idle", "完成态再发", null, isRunning: false), "agy inject: completed prompt dispatched immediately");
        Check(injected.Count == 2 && injected[0].Pid == livePid && injected[0].Text == "回合中插话" && injected[1].Text == "完成态再发",
            "agy inject: both deliveries hit the host terminal with the raw text");

        // 没有宿主 PID / 宿主已死 → 如实失败，不留任何状态
        AgyCliDispatcher.HostPidProvider = _ => null;
        Check(!AgyCliDispatcher.Dispatch("agy-nohost", "无处可注", null, isRunning: false), "agy inject: no host pid fails honestly");
        AgyCliDispatcher.HostPidProvider = _ => 2147483632;
        Check(!AgyCliDispatcher.Dispatch("agy-deadhost", "注不进去", null, isRunning: false), "agy inject: dead host terminal is refused");

        // 注入器自己失败（AttachConsole 失败等）→ 原样上报
        AgyCliDispatcher.HostPidProvider = _ => 4242;
        AgyCliDispatcher.Injector = (int _, string _, out string reason) => { reason = "AttachConsole 失败"; return false; };
        Check(!AgyCliDispatcher.Dispatch("agy-fail", "注不进去", null, isRunning: false), "agy inject: injector failure is reported");
    }
    finally
    {
        AgyCliDispatcher.Injector = previousInjector;
        AgyCliDispatcher.HostPidProvider = previousHostPid;
    }
}

// Stop 收尾：这一轮里用户自己提交过跟进（转录本检测）才保留排队徽章，其余清零。
var agyStopCatalog = new TaskCatalog();
var agyStopT0 = DateTimeOffset.UtcNow;
agyStopCatalog.Accept(A("PreInvocation", new { conversationId = "agy-qs" }), agyStopT0);
agyStopCatalog.Accept(A("Stop", new { conversationId = "agy-qs" }), agyStopT0.AddSeconds(2));
Check(!agyStopCatalog.Projection().Tasks.First(t => t.Id == "agy-qs").PromptQueued,
    "agy stop: a plain turn ends without the queued badge");

// 5.1d 「最后观测」寻址：复刻真机场景（A/pidA、B/pidB 都完成 → 关掉 pidB → pidA 的终端 /resume 到 B 并跑一轮）
{
    var switchCatalog = new TaskCatalog();
    var switchT0 = DateTimeOffset.UtcNow;
    var livePid = Environment.ProcessId;   // A 的终端（活着）
    const int deadPid = 2147483632;        // B 的终端（已关闭）
    switchCatalog.Accept(A("PreInvocation", new { conversationId = "agy-sw-a", hostPid = livePid }), switchT0);
    switchCatalog.Accept(A("Stop", new { conversationId = "agy-sw-a", hostPid = livePid }), switchT0.AddSeconds(1));
    switchCatalog.Accept(A("PreInvocation", new { conversationId = "agy-sw-b", hostPid = deadPid }), switchT0.AddSeconds(2));
    switchCatalog.Accept(A("Stop", new { conversationId = "agy-sw-b", hostPid = deadPid }), switchT0.AddSeconds(3));
    // 桥至少跑过一次 Maintain（生产里每 300ms 一次）：此时 A 的终端活着 → A 进 _agyHostSeen
    switchCatalog.Maintain(switchT0.AddSeconds(1));
    // pidB 的终端关闭；A 的终端 /resume 到 B 并跑了一轮（hook 从 pidA 上报 conversationId=B）
    switchCatalog.Accept(A("PreInvocation", new { conversationId = "agy-sw-b", hostPid = livePid }), switchT0.AddSeconds(4));

    var switched = switchCatalog.Projection().Tasks.ToDictionary(task => task.Id);
    Check(switched["agy-sw-b"].Capabilities.Contains(TaskCapability.Prompt),
        "agy addressing: the conversation the terminal last ran keeps the prompt capability");
    Check(!switched["agy-sw-a"].Capabilities.Contains(TaskCapability.Prompt),
        "agy addressing: the conversation the terminal switched away from loses the prompt capability");
    Check(switchCatalog.AgyHostPidServes("agy-sw-b") && !switchCatalog.AgyHostPidServes("agy-sw-a"),
        "agy addressing: the gate follows the last observed conversation on that pid");
    Check(switchCatalog.AgyHostPidOf("agy-sw-a") is null,
        "agy addressing: the superseded conversation's pid mapping is dropped (C)");

    // C 的连带：被切走的任务已不在任何终端 → 下一次 Maintain 直接判离线（不吃"hook 还没到"的十分钟宽限）
    switchCatalog.Maintain(switchT0.AddSeconds(5));
    var movedAway = switchCatalog.Projection().Tasks.First(task => task.Id == "agy-sw-a");
    Check(movedAway.Lifecycle == AgentLifecycle.Offline,
        "agy addressing: a task whose terminal moved on goes Offline instead of pretending to be watched");

    // 反向恢复：A 在（同一台）终端里重新跑起来 → 映射重建、入口回来（旧映射已清，不会被 incumbent 粘住）
    switchCatalog.Accept(A("PreInvocation", new { conversationId = "agy-sw-a", hostPid = livePid }), switchT0.AddSeconds(10));
    var revived = switchCatalog.Projection().Tasks.First(task => task.Id == "agy-sw-a");
    Check(revived.Lifecycle == AgentLifecycle.Running && revived.Capabilities.Contains(TaskCapability.Prompt),
        "agy addressing: a conversation that moves back gets its prompt entry again");
    Check(switchCatalog.AgyHostPidOf("agy-sw-b") is null,
        "agy addressing: the other side's mapping is dropped symmetrically");
}

// 5.1e 父子共用一台终端：子代理在跑不该让父会话失去入口（输入框仍归父会话）
{
    var familyCatalog = new TaskCatalog();
    var familyT0 = DateTimeOffset.UtcNow;
    var livePid = Environment.ProcessId;
    familyCatalog.Accept(A("PreInvocation", new { conversationId = "agy-par", hostPid = livePid }), familyT0);
    familyCatalog.Accept(A("PreToolUse", new
    {
        conversationId = "agy-par",
        hostPid = livePid,
        toolCall = new { name = "invoke_subagent", args = new { Subagents = new[] { new { Role = "researcher", TypeName = "research", Prompt = "调查" } } } },
    }), familyT0.AddSeconds(1));
    familyCatalog.Accept(A("PreInvocation", new { conversationId = "agy-par-child", hostPid = livePid }), familyT0.AddSeconds(2));

    var parent = familyCatalog.Projection().Tasks.First(task => task.Id == "agy-par");
    Check(parent.Subagents.Any(child => child.Id == "agy-par-child"), "agy addressing: the child conversation attaches to the parent");
    Check(parent.Capabilities.Contains(TaskCapability.Prompt),
        "agy addressing: a running subagent does not take the parent's prompt entry away");
    Check(familyCatalog.AgyHostPidServes("agy-par"), "agy addressing: the gate treats parent and child as one terminal");
    Check(familyCatalog.AgyHostPidOf("agy-par") == livePid,
        "agy addressing: the parent keeps its pid mapping while the child reports from the same terminal");
}

// ---------------------------------------------------------------------------
// 5.2 远程注入第二条腿（2026-10-16 探针定案）：宿主台账 / parked 存活 / env 全量继承 / 送达确认
// ---------------------------------------------------------------------------

// T1-T2 台账：Observe 只登记"活着的正主"（kind=interactive 且 entrypoint=cli 且有 pid），
// 在位宿主不被新行改写（prefer-alive-incumbent），死了才换人；Forget 清账。
var prevAliveChecker = ClaudeSessionRegistry.ProcessAliveChecker;
try
{
    var obsAlive = new HashSet<int> { 1111, 2222 };
    ClaudeSessionRegistry.ProcessAliveChecker = pid => obsAlive.Contains(pid);
    ClaudeSessionRegistry.Observe(
    [
        new ClaudeSessionRegistry.Entry("obs-cli", null, null, "idle", null, null, 1111, null, Kind: "interactive", Entrypoint: "cli"),
        new ClaudeSessionRegistry.Entry("obs-sdk", null, null, "idle", null, null, 3333, null, Kind: "interactive", Entrypoint: "sdk-cli"),
        new ClaudeSessionRegistry.Entry("obs-nopid", null, null, "idle", null, null, null, null, Kind: "interactive", Entrypoint: "cli"),
    ]);
    Check(ClaudeSessionRegistry.HostPidOf("obs-cli") == 1111, "hostpids: cli host recorded");
    Check(ClaudeSessionRegistry.HostPidOf("obs-sdk") is null, "hostpids: sdk-cli injection child ignored");
    Check(ClaudeSessionRegistry.HostPidOf("obs-nopid") is null, "hostpids: pid-less row ignored");

    ClaudeSessionRegistry.Observe(
    [
        new ClaudeSessionRegistry.Entry("obs-cli", null, null, "idle", null, null, 2222, null, Kind: "interactive", Entrypoint: "cli"),
    ]);
    Check(ClaudeSessionRegistry.HostPidOf("obs-cli") == 1111, "hostpids: alive incumbent is not overwritten");

    obsAlive.Remove(1111);
    ClaudeSessionRegistry.Observe(
    [
        new ClaudeSessionRegistry.Entry("obs-cli", null, null, "idle", null, null, 2222, null, Kind: "interactive", Entrypoint: "cli"),
    ]);
    Check(ClaudeSessionRegistry.HostPidOf("obs-cli") == 2222, "hostpids: dead incumbent is replaced");

    ClaudeSessionRegistry.Forget("obs-cli");
    Check(ClaudeSessionRegistry.HostPidOf("obs-cli") is null, "hostpids: forget drops the record");
}
finally
{
    ClaudeSessionRegistry.ProcessAliveChecker = prevAliveChecker;
    ClaudeSessionRegistry.Forget("obs-cli");
    ClaudeSessionRegistry.Forget("obs-sdk");
    ClaudeSessionRegistry.Forget("obs-nopid");
}

// T3 parked：注册表里没有、但宿主进程活着 —— 能力保留、不吃 10 分钟兜底；
// Forget 后（宿主真死）下一次 Maintain 立刻按常规判定转 Offline。
var parkedCatalog = new TaskCatalog();
var parkedT0 = DateTimeOffset.UtcNow;
ClaudeSessionRegistry.Observe(
[
    new ClaudeSessionRegistry.Entry("parked-1", null, null, "idle", null, null, Environment.ProcessId, null, Kind: "interactive", Entrypoint: "cli"),
]);
try
{
    parkedCatalog.Accept(C("SessionStart", new { session_id = "parked-1" }), parkedT0);
    parkedCatalog.Accept(C("UserPromptSubmit", new { session_id = "parked-1", prompt = "被 /resume 切走的会话" }), parkedT0.AddSeconds(1));
    Check(parkedCatalog.Projection().Tasks.First(t => t.Id == "parked-1").Capabilities.Contains(TaskCapability.Prompt),
        "parked: host alive keeps prompt capability without a registry entry");

    parkedCatalog.Maintain(parkedT0.AddMinutes(11));
    Check(parkedCatalog.Projection().Tasks.First(t => t.Id == "parked-1").Lifecycle != AgentLifecycle.Offline,
        "parked: survives the 10-minute offline fallback while the host is alive");

    ClaudeSessionRegistry.Forget("parked-1");
    parkedCatalog.Maintain(parkedT0.AddMinutes(12));
    Check(parkedCatalog.Projection().Tasks.First(t => t.Id == "parked-1").Lifecycle == AgentLifecycle.Offline,
        "parked: host gone -> next maintain goes offline");
}
finally
{
    ClaudeSessionRegistry.Forget("parked-1");
}

// T4 ClaudeHostEnvironment：PEB 读取器的最小实证（读自己进程；真跨进程已在探针实测 69 项）。
var pebProbe = "AGENTPAGER_PEB_PROBE_" + Guid.NewGuid().ToString("N")[..8];
Environment.SetEnvironmentVariable(pebProbe, "peb-ok");
try
{
    var pebEnv = ClaudeHostEnvironment.Read(Environment.ProcessId);
    if (Environment.Is64BitProcess)
        Check(pebEnv is not null && pebEnv.TryGetValue(pebProbe, out var pebValue) && pebValue == "peb-ok",
            "host environment: PEB reader sees a just-set variable of the live host process");
    else
        Check(pebEnv is null, "host environment: 32-bit bridge degrades to null (documented)");
}
finally
{
    Environment.SetEnvironmentVariable(pebProbe, null);
}

// T5 身份三枚：大小写不敏感地常备剔除（socket / token / entrypoint）。
Check(ClaudePeerClient.HostEnvironmentExclusions.Count == 3 &&
      ClaudePeerClient.HostEnvironmentExclusions.Contains("claude_code_messaging_socket") &&
      ClaudePeerClient.HostEnvironmentExclusions.Contains("claude_code_messaging_token") &&
      ClaudePeerClient.HostEnvironmentExclusions.Contains("CLAUDE_CODE_ENTRYPOINT"),
    "claude env: identity exclusions complete and case-insensitive");

// T6 前置：启动器解析（2026-10-17 真机定案修复）。Windows 上 npm 只落 claude.cmd shim，
// 没有 claude.exe；FileName="claude" 必被 CreateProcess 拒（v81 真机实测：CLI 回退腿从未
// 启动过）。解析器必须按 npm 布局找到包体内的原生 claude.exe。
var resolvedClaudeExe = ClaudePeerClient.ResolveCliExecutable();
Check(File.Exists(resolvedClaudeExe),
    $"claude launcher: resolver finds a real executable on this machine (got '{resolvedClaudeExe}')");

// T6 CLI 回退子进程的启动环境：宿主（此处用测试进程代理）整套环境叠加、身份三枚无条件剔除、
// 参数与工作目录照旧（claude --resume <id> -p <prompt>，cwd 用会话记录的那个）。
var envProbe = "AGENTPAGER_ENV_PROBE_" + Guid.NewGuid().ToString("N")[..8];
var prevEnvProbe = Environment.GetEnvironmentVariable(envProbe);
var prevEnvToken = Environment.GetEnvironmentVariable("CLAUDE_CODE_MESSAGING_TOKEN");
var prevEnvEntrypoint = Environment.GetEnvironmentVariable("CLAUDE_CODE_ENTRYPOINT");
var prevProvBase = Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL");
var prevProvModel = Environment.GetEnvironmentVariable("ANTHROPIC_MODEL");
var prevEnvStarter = ClaudePeerClient.ProcessStarter;
var prevEnvScanner = ClaudePeerClient.RegistryScanner;
try
{
    Environment.SetEnvironmentVariable(envProbe, "host-ok");
    Environment.SetEnvironmentVariable("CLAUDE_CODE_MESSAGING_TOKEN", "parent-token");
    Environment.SetEnvironmentVariable("CLAUDE_CODE_ENTRYPOINT", "cli");
    ClaudeSessionRegistry.Observe(
    [
        new ClaudeSessionRegistry.Entry("env-1", null, null, "idle", null, null, Environment.ProcessId, null, Kind: "interactive", Entrypoint: "cli"),
    ]);

    System.Diagnostics.ProcessStartInfo? envStartInfo = null;
    ClaudePeerClient.RegistryScanner = () => new();
    ClaudePeerClient.ProcessStarter = psi => { envStartInfo = psi; return null; };
    Check(ClaudePromptDispatcher.QueueOrExecute("env-1", "继承宿主环境", @"F:\Git\demo", isRunning: false),
        "claude env: direct dispatch started via CLI fallback");

    var envSi = envStartInfo;
    Check(envSi is not null &&
          envSi.FileName == resolvedClaudeExe &&
          envSi.WorkingDirectory == @"F:\Git\demo" &&
          envSi.ArgumentList.Count == 4 &&
          envSi.ArgumentList[0] == "--resume" && envSi.ArgumentList[1] == "env-1" &&
          envSi.ArgumentList[2] == "-p" && envSi.ArgumentList[3] == "继承宿主环境",
        "claude env: child launched as claude --resume <id> -p <prompt> in the session cwd");

    Check(envSi is not null && envSi.Environment.TryGetValue(envProbe, out var envValue) && envValue == "host-ok",
        "claude env: host environment carried into the child");

    Check(envSi is not null &&
          !envSi.Environment.ContainsKey("CLAUDE_CODE_MESSAGING_TOKEN") &&
          !envSi.Environment.ContainsKey("CLAUDE_CODE_ENTRYPOINT"),
        "claude env: identity variables stripped even when present in the parent");

    // 用户裁定（2026-10-17 重申，防压缩丢失）：注入过程不得碰提供商/模型设置，通通继承原对话。
    // 锁死：provider/模型变量必须原样带进子进程（与身份三枚的剔除对照；参数个数锁在上面）。
    Environment.SetEnvironmentVariable("ANTHROPIC_BASE_URL", "https://provider.example/v1");
    Environment.SetEnvironmentVariable("ANTHROPIC_MODEL", "opus");
    System.Diagnostics.ProcessStartInfo? provStartInfo = null;
    ClaudePeerClient.ProcessStarter = psi => { provStartInfo = psi; return null; };
    Check(ClaudePromptDispatcher.QueueOrExecute("env-1", "再来一条", @"F:\Git\demo", isRunning: false),
        "claude env: provider inheritance dispatch started");
    Check(provStartInfo is not null &&
          provStartInfo.Environment.TryGetValue("ANTHROPIC_BASE_URL", out var provBase) && provBase == "https://provider.example/v1" &&
          provStartInfo.Environment.TryGetValue("ANTHROPIC_MODEL", out var provModel) && provModel == "opus" &&
          !provStartInfo.ArgumentList.Any(a => a.StartsWith("--model", StringComparison.Ordinal)),
        "claude env: provider/model settings inherited untouched, no --model flag");
}
finally
{
    ClaudePeerClient.ProcessStarter = prevEnvStarter;
    ClaudePeerClient.RegistryScanner = prevEnvScanner;
    Environment.SetEnvironmentVariable(envProbe, prevEnvProbe);
    Environment.SetEnvironmentVariable("CLAUDE_CODE_MESSAGING_TOKEN", prevEnvToken);
    Environment.SetEnvironmentVariable("CLAUDE_CODE_ENTRYPOINT", prevEnvEntrypoint);
    Environment.SetEnvironmentVariable("ANTHROPIC_BASE_URL", prevProvBase);
    Environment.SetEnvironmentVariable("ANTHROPIC_MODEL", prevProvModel);
    ClaudeSessionRegistry.Forget("env-1");
}

// T6b 重投强制换腿（2026-10-17 真机定案）：skipPipe=true 时即使注册表里有"活管道"条目也
// 必须直接走 CLI——parked 会话的陈旧管道"收得下字节却无人消费"，首投可能已被它静默吞掉，
// 重投再走它等于白费第二次机会。这里用一条真实存在但无人喂数据的命名管道当"陈旧管道"：
// 若 skipPipe 失效，连接会成功（accept 完成）且 CLI 腿被跳过，断言当场抓现行。
var skipPipeName = "agentpager-skip-test-" + Guid.NewGuid().ToString("N")[..8];
var prevSkipStarter = ClaudePeerClient.ProcessStarter;
var prevSkipScanner = ClaudePeerClient.RegistryScanner;
try
{
    using var skipServer = new System.IO.Pipes.NamedPipeServerStream(
        skipPipeName, System.IO.Pipes.PipeDirection.InOut, 1,
        System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);
    var skipAccept = skipServer.WaitForConnectionAsync();

    System.Diagnostics.ProcessStartInfo? skipStartInfo = null;
    ClaudePeerClient.ProcessStarter = psi => { skipStartInfo = psi; return null; };
    ClaudePeerClient.RegistryScanner = () => new()
    {
        ["skip-1"] = new("skip-1", null, null, "idle", null,
            MessagingSocketPath: @"\\.\pipe\" + skipPipeName,
            Pid: Environment.ProcessId, Cwd: @"F:\Git\demo", Kind: "interactive", Entrypoint: "cli"),
    };
    var skipLogs = new List<(string Level, string Message)>();
    var skipOk = ClaudePeerClient.SendPrompt("skip-1", "重投强制换腿", skipPipe: true,
        log: (level, message) => skipLogs.Add((level, message)));

    Check(skipOk && skipStartInfo is not null &&
          skipStartInfo.ArgumentList.Count == 4 && skipStartInfo.ArgumentList[1] == "skip-1",
        "claude retry: skipPipe dispatches straight to the CLI leg");
    Check(skipLogs.All(entry => !entry.Message.Contains("管道注入")),
        "claude retry: skipPipe never touches the pipe (no pipe success or error logged)");
    Check(!skipAccept.IsCompleted,
        "claude retry: skipPipe leaves a live pipe untouched (no connection made)");
}
finally
{
    ClaudePeerClient.ProcessStarter = prevSkipStarter;
    ClaudePeerClient.RegistryScanner = prevSkipScanner;
}

// T7 送达确认：直接投递（非运行中）登记；45s 无音讯 -> 静默重投一次；再 45s -> 诚实落
// "未确认"；期间任意 hook 事件到达即清账（在途不重投）。
var delCatalog = new TaskCatalog
{
    ClaudeRegistryScanner = () => new Dictionary<string, ClaudeSessionRegistry.Entry>
    {
        ["del-1"] = new("del-1", null, null, "idle", null, null, Environment.ProcessId, null),
        ["del-2"] = new("del-2", null, null, "idle", null, null, Environment.ProcessId, null),
    },
};
var delT0 = DateTimeOffset.UtcNow;
foreach (var delID in new[] { "del-1", "del-2" })
{
    delCatalog.Accept(C("SessionStart", new { session_id = delID }), delT0);
    delCatalog.Accept(C("UserPromptSubmit", new { session_id = delID, prompt = "原有对话" }), delT0.AddSeconds(1));
    delCatalog.Accept(C("Stop", new { session_id = delID }), delT0.AddSeconds(2));
}
var delRec = new RecordingResolver();
var delAck1 = delCatalog.Perform(Guid.NewGuid(), new ControlPayload("del-1", ControlAction.Prompt, "继续看看"), delRec);
Check(delAck1.Result == ControlResult.Accepted && delRec.PromptIsRunning == false,
    "delivery: finished (succeeded) session takes the direct path");
var delTask1 = delCatalog.Projection().Tasks.First(t => t.Id == "del-1");
Check(delTask1.Lifecycle == AgentLifecycle.Running && !delTask1.PromptQueued && delTask1.UserPrompt == "继续看看",
    "delivery: revival restarts the turn without queuing");
var delAck2 = delCatalog.Perform(Guid.NewGuid(), new ControlPayload("del-2", ControlAction.Prompt, "第二条"), delRec);
Check(delAck2.Result == ControlResult.Accepted, "delivery: second session delivered directly");
delCatalog.Accept(C("PostToolUse", new { session_id = "del-2" }), delT0.AddSeconds(10));

var delPrevStarter = ClaudePeerClient.ProcessStarter;
var delPrevScanner = ClaudePeerClient.RegistryScanner;
try
{
    var delSends = 0;
    ClaudePeerClient.RegistryScanner = () => new();
    ClaudePeerClient.ProcessStarter = _ => { delSends++; return null; };

    delCatalog.Maintain(delT0.AddSeconds(50));
    Check(delSends == 1, $"delivery: silent retry once after the first timeout (sends={delSends})");
    Check(delCatalog.Projection().Tasks.First(t => t.Id == "del-1").LatestStep != "⚠ 远程注入未确认送达（会话无响应）",
        "delivery: retry does not write the warning yet");
    Check(delCatalog.Projection().Tasks.First(t => t.Id == "del-2").LatestStep != "⚠ 远程注入未确认送达（会话无响应）",
        "delivery: acked session is not retried");

    delCatalog.Maintain(delT0.AddSeconds(100));
    Check(delSends == 1, "delivery: no second retry after the retried flag is set");
    Check(delCatalog.Projection().Tasks.First(t => t.Id == "del-1").LatestStep == "⚠ 远程注入未确认送达（会话无响应）",
        "delivery: second timeout writes the honest warning");

    delCatalog.Maintain(delT0.AddSeconds(150));
    Check(delSends == 1 && delCatalog.Projection().Tasks.First(t => t.Id == "del-2").LatestStep != "⚠ 远程注入未确认送达（会话无响应）",
        "delivery: settled sessions stay quiet on later maintains");
}
finally
{
    ClaudePeerClient.ProcessStarter = delPrevStarter;
    ClaudePeerClient.RegistryScanner = delPrevScanner;
}

// 6. 异常与边界分支
var emptyAck = opencodeCatalog.Perform(Guid.NewGuid(), new ControlPayload("oc-1", ControlAction.Prompt, "   "), ocRec);
Check(emptyAck.Result == ControlResult.Unsupported, "prompt: empty prompt rejected");

var waitCatalog = new TaskCatalog();
var waitT0 = DateTimeOffset.UtcNow;
waitCatalog.Accept(H("session.created", new { id = "oc-wait", directory = "F:/Git/repo" }), waitT0);
waitCatalog.Accept(H("chat.message", new { sessionID = "oc-wait", text = "做个操作" }), waitT0.AddSeconds(1));
waitCatalog.Accept(new OpenCodeHookPayload("permission.asked", JsonSerializer.SerializeToElement(new { sessionID = "oc-wait", id = "req-1", permission = "bash" }, WireJson.Options), "approval"), waitT0.AddSeconds(2));
var waitAck = waitCatalog.Perform(Guid.NewGuid(), new ControlPayload("oc-wait", ControlAction.Prompt, "打断一下"), ocRec);
Check(waitAck.Result == ControlResult.Unsupported, "prompt: rejected while waiting approval");

var failAck = opencodeCatalog.Perform(Guid.NewGuid(), new ControlPayload("oc-1", ControlAction.Prompt, "正常指令"), new NullResolver());
Check(failAck.Result == ControlResult.Rejected, "prompt: delivery failure returns Rejected");

Console.WriteLine();
// 关机：只验参数形状与失败路径（桩掉 Starter，绝不真关机）。
var prevPowerStarter = SystemPower.Starter;
try
{
    System.Diagnostics.ProcessStartInfo? powerStartInfo = null;
    SystemPower.Starter = psi => { powerStartInfo = psi; return null; };
    Check(SystemPower.Shutdown(60), "shutdown: dispatch started");
    Check(powerStartInfo is not null &&
          powerStartInfo.FileName == "shutdown" &&
          powerStartInfo.ArgumentList.Contains("/s") &&
          powerStartInfo.ArgumentList.Contains("60"),
        "shutdown: shutdown /s /t 60 with delay");
    SystemPower.Starter = _ => throw new InvalidOperationException("no shutdown privilege");
    Check(!SystemPower.Shutdown(60), "shutdown: starter failure returns false instead of throwing");
}
finally
{
    SystemPower.Starter = prevPowerStarter;
}

// ===================== 29. 宿主存活离线判定、防翻生与 Prompt 能力规范 =====================
Console.WriteLine();
Console.WriteLine("--- 宿主存活离线判定与生命周期规范 ---");

// 1. 防翻生：处于 Offline 的任务，迟到的 session.idle / Stop / reply 绝不得翻生回 Succeeded/Running
var resurrectCatalog = new TaskCatalog();
var resT0 = DateTimeOffset.UtcNow;
// a) OpenCode: dispose 判离线后收到迟到的 session.idle 与 reply
resurrectCatalog.Accept(H("chat.message", new { sessionID = "oc_res", text = "开工" }), resT0);
resurrectCatalog.Accept(H("plugin.dispose", new { sessionID = "oc_res" }), resT0.AddSeconds(1));
Check(resurrectCatalog.Projection().Tasks.First(t => t.Id == "oc_res").Lifecycle == AgentLifecycle.Offline,
    "resurrect guard: opencode task is offline after dispose");
resurrectCatalog.Accept(H("session.idle", new { sessionID = "oc_res" }), resT0.AddSeconds(2));
Check(resurrectCatalog.Projection().Tasks.First(t => t.Id == "oc_res").Lifecycle == AgentLifecycle.Offline,
    "resurrect guard: late session.idle does NOT resurrect offline task to succeeded");
resurrectCatalog.Accept(H("reply", new { sessionID = "oc_res", text = "迟到的正文" }), resT0.AddSeconds(3));
Check(resurrectCatalog.Projection().Tasks.First(t => t.Id == "oc_res").Lifecycle == AgentLifecycle.Offline,
    "resurrect guard: late reply does NOT touch offline task");

// b) Claude: SessionEnd 判离线后收到迟到的 Stop
resurrectCatalog.Accept(C("UserPromptSubmit", new { session_id = "cl_res", prompt = "开工" }), resT0);
resurrectCatalog.Accept(C("SessionEnd", new { session_id = "cl_res" }), resT0.AddSeconds(1));
Check(resurrectCatalog.Projection().Tasks.First(t => t.Id == "cl_res").Lifecycle == AgentLifecycle.Offline,
    "resurrect guard: claude task is offline after SessionEnd");
resurrectCatalog.Accept(C("Stop", new { session_id = "cl_res" }), resT0.AddSeconds(2));
Check(resurrectCatalog.Projection().Tasks.First(t => t.Id == "cl_res").Lifecycle == AgentLifecycle.Offline,
    "resurrect guard: late Stop does NOT resurrect offline claude task");

// c) Agy: 离线后收到迟到的 Stop
resurrectCatalog.Accept(A("PreInvocation", new { conversationId = "agy_res" }), resT0);
// 模拟 11 分钟无活动被静默判离线
resurrectCatalog.Maintain(resT0.AddMinutes(11));
Check(resurrectCatalog.Projection().Tasks.First(t => t.Id == "agy_res").Lifecycle == AgentLifecycle.Offline,
    "resurrect guard: agy task goes offline after silent timeout");
resurrectCatalog.Accept(A("Stop", new { conversationId = "agy_res" }), resT0.AddMinutes(12));
Check(resurrectCatalog.Projection().Tasks.First(t => t.Id == "agy_res").Lifecycle == AgentLifecycle.Offline,
    "resurrect guard: late Stop does NOT resurrect offline agy task");

// 2. 宿主存活状态下，Succeeded 任务保持在线，永不因 30 分钟无动态被移出面板
var aliveKeepCatalog = new TaskCatalog();
var aliveT0 = DateTimeOffset.UtcNow;
var fakePid = 999988;
var isHostAlive = true;
var alivePrevChecker = ClaudeSessionRegistry.ProcessAliveChecker;
try
{
    ClaudeSessionRegistry.ProcessAliveChecker = pid => pid == fakePid ? isHostAlive : alivePrevChecker(pid);

    // a) Claude: 回合跑完进入 Succeeded，宿主活着时，35 分钟后依然在面板且能输入 Prompt
    aliveKeepCatalog.Accept(C("UserPromptSubmit", new { session_id = "cl_alive", prompt = "算个数" }), aliveT0);
    aliveKeepCatalog.Accept(C("Stop", new { session_id = "cl_alive" }), aliveT0.AddSeconds(5));
    Check(aliveKeepCatalog.Projection().Tasks.First(t => t.Id == "cl_alive").Lifecycle == AgentLifecycle.Succeeded,
        "alive host: claude task is succeeded after turn");

    // 登记宿主 PID
    ClaudeSessionRegistry.Observe([new ClaudeSessionRegistry.Entry("cl_alive", "test", "derived", "idle", 0, null, fakePid, @"C:\test", "interactive", "cli")]);
    Check(ClaudeSessionRegistry.HostPidOf("cl_alive") == fakePid, "alive host: host PID recorded");

    // 宿主活着时，35 分钟过去：任务依然在面板（不被 purge），依然是 Succeeded，依然有 Prompt 能力
    aliveKeepCatalog.Maintain(aliveT0.AddMinutes(35));
    var clAliveTask = aliveKeepCatalog.Projection().Tasks.FirstOrDefault(t => t.Id == "cl_alive");
    Check(clAliveTask is not null, "alive host: claude task is NOT purged after 35 minutes while host alive");
    Check(clAliveTask!.Lifecycle == AgentLifecycle.Succeeded, "alive host: task remains in Succeeded state");
    Check(clAliveTask.Capabilities.Contains(TaskCapability.Prompt), "alive host: Succeeded task CAN receive prompt while host alive");

    // 宿主前台终端关闭（PID 死亡）
    isHostAlive = false;
    aliveKeepCatalog.Maintain(aliveT0.AddMinutes(36));
    var clDeadTask = aliveKeepCatalog.Projection().Tasks.FirstOrDefault(t => t.Id == "cl_alive");
    Check(clDeadTask is not null && clDeadTask.Lifecycle == AgentLifecycle.Offline,
        "alive host: claude task immediately goes Offline when host process dies");
    Check(!clDeadTask!.Capabilities.Contains(TaskCapability.Prompt),
        "alive host: Offline task CANNOT receive prompt");

    // 离线满 30 分钟后，才被 Normalize 移出面板
    aliveKeepCatalog.Maintain(aliveT0.AddMinutes(36).AddMinutes(31));
    Check(aliveKeepCatalog.Projection().Tasks.All(t => t.Id != "cl_alive"),
        "alive host: task purged after 30 minutes in Offline state");
}
finally
{
    ClaudeSessionRegistry.ProcessAliveChecker = alivePrevChecker;
    ClaudeSessionRegistry.Forget("cl_alive");
}

// b) OpenCode: 控制通道在线时保持 Succeeded 与 Prompt 能力，通道断开立即转 Offline
var ocAliveCatalog = new TaskCatalog();
var ocControlUp = true;
ocAliveCatalog.HasOpenCodeControl = () => ocControlUp;
var ocLiveT0 = DateTimeOffset.UtcNow;
ocAliveCatalog.Accept(H("chat.message", new { sessionID = "oc_live", text = "做个功能" }), ocLiveT0);
ocAliveCatalog.Accept(H("session.idle", new { sessionID = "oc_live" }), ocLiveT0.AddSeconds(5));
Check(ocAliveCatalog.Projection().Tasks.First(t => t.Id == "oc_live").Lifecycle == AgentLifecycle.Succeeded,
    "opencode control: task is succeeded");
// 控制通道在位，35 分钟过去：不被删除，依然有 Prompt 能力
ocAliveCatalog.Maintain(ocLiveT0.AddMinutes(35));
var ocLiveTask = ocAliveCatalog.Projection().Tasks.FirstOrDefault(t => t.Id == "oc_live");
Check(ocLiveTask is not null && ocLiveTask.Lifecycle == AgentLifecycle.Succeeded,
    "opencode control: task NOT purged after 35 minutes while control channel up");
Check(ocLiveTask!.Capabilities.Contains(TaskCapability.Prompt),
    "opencode control: Succeeded task CAN receive prompt while control up");

// 控制通道断开（前台终端关闭）
ocControlUp = false;
ocAliveCatalog.Maintain(ocLiveT0.AddMinutes(36));
var ocDeadTask = ocAliveCatalog.Projection().Tasks.FirstOrDefault(t => t.Id == "oc_live");
Check(ocDeadTask is not null && ocDeadTask.Lifecycle == AgentLifecycle.Offline,
    "opencode control: task immediately goes Offline when control channel disconnects");
Check(!ocDeadTask!.Capabilities.Contains(TaskCapability.Prompt),
    "opencode control: Offline task CANNOT receive prompt");

// c) Antigravity: 宿主 PID 在线时保持 Succeeded，35 分钟不被误判离线；宿主 PID 死亡立即转 Offline
var agyAliveCatalog = new TaskCatalog();
var agyLiveT0 = DateTimeOffset.UtcNow;
var agyFakePid = 999977;
var isAgyHostAlive = true;
var agyPrevChecker = ClaudeSessionRegistry.ProcessAliveChecker;
try
{
    ClaudeSessionRegistry.ProcessAliveChecker = pid => pid == agyFakePid ? isAgyHostAlive : agyPrevChecker(pid);

    agyAliveCatalog.Accept(new AgyHookPayload(
        Event: "PreInvocation",
        ConversationId: "agy_live",
        HostPid: agyFakePid
    ), agyLiveT0);
    var agyRunningTask = agyAliveCatalog.Projection().Tasks.First(t => t.Id == "agy_live");
    Check(agyRunningTask.Lifecycle == AgentLifecycle.Running, "agy host: task is running");
    Check(agyRunningTask.Capabilities.Contains(TaskCapability.Prompt), "agy host: running task CAN receive prompt while host alive");

    agyAliveCatalog.Accept(new AgyHookPayload(
        Event: "Stop",
        ConversationId: "agy_live",
        HostPid: agyFakePid
    ), agyLiveT0.AddSeconds(5));
    Check(agyAliveCatalog.Projection().Tasks.First(t => t.Id == "agy_live").Lifecycle == AgentLifecycle.Succeeded,
        "agy host: task is succeeded after turn");

    // 宿主活着时，35 分钟过去：任务依然在面板（不被超时误判离线，不被 purge），依然是 Succeeded；
    // 统一控制台按键注入模型：完成态下依然具备 Prompt 能力，可原地按键唤醒
    agyAliveCatalog.Maintain(agyLiveT0.AddMinutes(35));
    var agyLiveTask = agyAliveCatalog.Projection().Tasks.FirstOrDefault(t => t.Id == "agy_live");
    Check(agyLiveTask is not null && agyLiveTask.Lifecycle == AgentLifecycle.Succeeded,
        "agy host: task NOT purged after 35 minutes while host PID is alive");
    Check(agyLiveTask!.Capabilities.Contains(TaskCapability.Prompt),
        "agy host: Succeeded task CAN receive prompt while host alive (console injection)");

    // 控制台按键注入单测：完成态与运行中都**立刻**注入（桥端已无队列）
    int lastInjectedPid = 0;
    string? lastInjectedText = null;
    AgyCliDispatcher.Injector = (int pid, string text, out string reason) =>
    {
        lastInjectedPid = pid;
        lastInjectedText = text;
        reason = "ok";
        return true;
    };
    AgyCliDispatcher.HostPidProvider = agyAliveCatalog.AgyHostPidOf;

    var agyIdleAck = agyAliveCatalog.Perform(Guid.NewGuid(), new ControlPayload("agy_live", ControlAction.Prompt, "完成态按键注入"), new AllowResolver());
    Check(agyIdleAck.Result == ControlResult.Accepted, "agy console injection: completed prompt accepted by catalog");
    var injectedDirectly = AgyCliDispatcher.Dispatch("agy_live", "完成态按键注入", null, isRunning: false);
    Check(injectedDirectly && lastInjectedPid == agyFakePid && lastInjectedText == "完成态按键注入",
        $"agy console injection: completed prompt injected via console keystrokes (pid={lastInjectedPid}, text={lastInjectedText})");

    // 运行中到达也立刻注入（排队或打断由 agy 的 Queued Messages 决定，桥不等 Stop）
    var injectedRunning = AgyCliDispatcher.Dispatch("agy_live", "回合中插话", null, isRunning: true);
    Check(injectedRunning && lastInjectedPid == agyFakePid && lastInjectedText == "回合中插话",
        "agy console injection: running prompt injected immediately (no bridge queue)");

    // 宿主前台终端关闭（PID 死亡）
    isAgyHostAlive = false;
    agyAliveCatalog.Maintain(agyLiveT0.AddMinutes(36));
    var agyDeadTask = agyAliveCatalog.Projection().Tasks.FirstOrDefault(t => t.Id == "agy_live");
    Check(agyDeadTask is not null && agyDeadTask.Lifecycle == AgentLifecycle.Offline,
        "agy host: task immediately goes Offline when host PID dies");
    Check(!agyDeadTask!.Capabilities.Contains(TaskCapability.Prompt),
        "agy host: Offline task CANNOT receive prompt");

    // 离线满 30 分钟后，才被 Normalize 移出面板
    agyAliveCatalog.Maintain(agyLiveT0.AddMinutes(36).AddMinutes(31));
    Check(agyAliveCatalog.Projection().Tasks.All(t => t.Id != "agy_live"),
        "agy host: task purged after 30 minutes in Offline state");
}
finally
{
    ClaudeSessionRegistry.ProcessAliveChecker = agyPrevChecker;
    AgyCliDispatcher.Injector = ConsoleKeystrokes.InjectViaWorker;
    AgyCliDispatcher.HostPidProvider = _ => null;
}

// 外网隧道域名清洗。ngrok 域名靠手填或整段粘贴，脏格式必须在写入配对文本前就挡住——
// 手机那边是照抄这个值拼 URL 的，漏一个 https:// 就会拼出 wss://https://... 的废地址。
Check(PairingPayload.NormalizeTunnelHost(null) is null, "pairing: null tunnel host stays null");
Check(PairingPayload.NormalizeTunnelHost("   ") is null, "pairing: blank tunnel host becomes null");
Check(PairingPayload.NormalizeTunnelHost("https://") is null, "pairing: scheme-only input becomes null");
Check(PairingPayload.NormalizeTunnelHost("xxx-yyy.ngrok-free.dev") == "xxx-yyy.ngrok-free.dev", "pairing: bare domain passes through");
Check(PairingPayload.NormalizeTunnelHost("https://xxx-yyy.ngrok-free.dev") == "xxx-yyy.ngrok-free.dev", "pairing: scheme is stripped");
Check(PairingPayload.NormalizeTunnelHost("https://xxx-yyy.ngrok-free.dev/") == "xxx-yyy.ngrok-free.dev", "pairing: trailing slash is stripped");
Check(PairingPayload.NormalizeTunnelHost("xxx-yyy.ngrok-free.dev:8443") == "xxx-yyy.ngrok-free.dev", "pairing: port is stripped");
Check(PairingPayload.NormalizeTunnelHost("  https://xxx-yyy.ngrok-free.dev/agentgrid  ") == "xxx-yyy.ngrok-free.dev", "pairing: path and padding are stripped");

// 配对文本的线上契约：字段名是 tunnelHost（手机按这个名字读），
// 没配隧道时整个字段不写出去 —— 旧版手机看到的就是原来那五个字段。
static string PairingJson(string? tunnelHost) => JsonSerializer.Serialize(
    new PairingPayload(1, "agentgrid-PC", "192.168.3.5", 49362, "c2VjcmV0", tunnelHost), WireJson.Options);
Check(PairingJson(null).Contains("\"serviceID\""), "pairing: serviceID casing unchanged (phone contract)");
Check(PairingJson("xxx-yyy.ngrok-free.dev").Contains("\"tunnelHost\""), "pairing: tunnel host is serialized as tunnelHost");
Check(JsonSerializer.Deserialize<PairingPayload>(PairingJson("xxx-yyy.ngrok-free.dev"), WireJson.Options)?.TunnelHost == "xxx-yyy.ngrok-free.dev", "pairing: tunnel host round-trips");
Check(!PairingJson(null).Contains("tunnelHost"), "pairing: absent tunnel host is omitted from the payload");
Check(JsonSerializer.Deserialize<PairingPayload>(PairingJson(null), WireJson.Options)?.TunnelHost is null, "pairing: payload without tunnel host still parses");

// 握手密钥比对。这是唯一挡在公网和全部快照之间的东西 —— 端口经隧道暴露后，
// 任何拿到域名的人都能连上收走任务标题/正文/额度，所以比对规则必须有断言钉住。
var pairingKey = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
var pairingKeyText = Convert.ToBase64String(pairingKey);
Check(PairingSecret.Matches(pairingKey, pairingKeyText), "auth: correct secret is accepted");
Check(!PairingSecret.Matches(pairingKey, Convert.ToBase64String(new byte[] { 1, 2, 3, 4, 5, 6, 7, 9 })), "auth: wrong secret is rejected");
Check(!PairingSecret.Matches(pairingKey, null), "auth: missing header is rejected");
Check(!PairingSecret.Matches(pairingKey, ""), "auth: empty header is rejected");
Check(!PairingSecret.Matches(pairingKey, "   "), "auth: whitespace header is rejected");
Check(!PairingSecret.Matches(pairingKey, "not-base64-at-all"), "auth: garbage header is rejected");
// 长度不同不能抛异常 —— 抛出去就是 500，而不是干净的 401
Check(!PairingSecret.Matches(pairingKey, pairingKeyText + "AA"), "auth: longer secret is rejected without throwing");
Check(!PairingSecret.Matches(pairingKey, pairingKeyText[..8]), "auth: truncated secret is rejected without throwing");

// ---------------------------------------------------------------------------
// ngrok 孤儿治理（2026-10-19 实测：强杀桥留下孤儿 ngrok，占住免费版单会话名额，
// 下一次启动的 ngrok 被云端拒绝——退出码 1）
// ---------------------------------------------------------------------------
static System.Diagnostics.Process SpawnVictim()
{
    var info = new System.Diagnostics.ProcessStartInfo
    {
        FileName = "cmd.exe",
        Arguments = "/c ping -n 60 127.0.0.1 > nul",
        UseShellExecute = false,
        CreateNoWindow = true,
    };
    return System.Diagnostics.Process.Start(info)!;
}

// 1. Job Object：关掉 job（等价于宿主进程消失）时成员子进程必须跟着死
{
    using var job = ChildProcessJob.TryCreate();
    Check(job is not null, "child job: job object created");
    var victim = SpawnVictim();
    try
    {
        Check(job is not null && job.TryAssign(victim), "child job: victim assigned to the job");
        job?.Dispose();
        Check(victim.WaitForExit(5000), "child job: closing the job takes the child with it");
    }
    finally
    {
        try { if (!victim.HasExited) victim.Kill(entireProcessTree: true); } catch { }
        victim.Dispose();
    }
}

// 2. 租约收尸：名字不符必须放过（PID 复用保险丝），名字符合才收
{
    var leaseFile = Path.Combine(Path.GetTempPath(), $"agentpager-lease-{Guid.NewGuid():N}.pid");
    var victim = SpawnVictim();
    try
    {
        ChildProcessLease.Remember(leaseFile, victim.Id);
        Check(File.Exists(leaseFile), "child lease: pid is remembered on disk");
        Check(!ChildProcessLease.ReapPrevious(leaseFile, "ngrok"), "child lease: a pid whose name does not match is left alone (pid reuse fuse)");
        Check(!victim.WaitForExit(500), "child lease: the mismatched process is still alive");
        Check(!File.Exists(leaseFile), "child lease: the stale pid file is consumed even when nothing is killed");

        ChildProcessLease.Remember(leaseFile, victim.Id);
        Check(ChildProcessLease.ReapPrevious(leaseFile, "cmd"), "child lease: a matching process name is reaped");
        Check(victim.WaitForExit(5000), "child lease: the reaped process is gone");
        Check(!File.Exists(leaseFile), "child lease: the pid file is deleted after reaping");

        ChildProcessLease.Remember(leaseFile, 2147483632);
        Check(!ChildProcessLease.ReapPrevious(leaseFile, "ngrok"), "child lease: a pid that no longer exists is a quiet no-op");
    }
    finally
    {
        try { if (!victim.HasExited) victim.Kill(entireProcessTree: true); } catch { }
        victim.Dispose();
        try { if (File.Exists(leaseFile)) File.Delete(leaseFile); } catch { }
    }
}

// 控制台按键注入的线格式：字符必须是 **16 位 UTF-16** 落进记录。
// 2026-10-19 真机 bug（另一会话发现、本机复现）：结构体少写 CharSet.Unicode → char 按 ANSI 封送，
// ASCII 一字不差、中文/全角整条丢失。这里钉住字节层，不依赖真控制台。
{
    var encoded = ConsoleKeystrokes.EncodeKeyRecords("回A");
    Check(encoded.Length == 6 * 20, $"console keystrokes: 3 chars + cr = 6 records x 20 bytes (got {encoded.Length})");
    // '回' = U+56DE → UTF-16LE DE 56，落在第 0 条的 UnicodeChar（偏移 14）
    Check(encoded[14] == 0xDE && encoded[15] == 0x56, "console keystrokes: CJK char lands as 16-bit utf-16 (回)");
    // 'A' = U+0041 → 41 00（ASCII 也必须走同一个 16 位槽）
    Check(encoded[40 + 14] == 0x41 && encoded[40 + 15] == 0x00, "console keystrokes: ascii uses the same 16-bit slot (A)");
    Check(encoded[80 + 14] == 0x0D && encoded[80 + 15] == 0x00, "console keystrokes: trailing carriage return closes the line");

    // 换行折成空格、尾附回车（TUI 按回车提交）："一\n二" → 一 空格 二 CR = 4 字符 = 8 条
    var folded = ConsoleKeystrokes.EncodeKeyRecords("一\n二");
    Check(folded.Length == 8 * 20, $"console keystrokes: newline folded to a space (got {folded.Length})");
    Check(folded[14] == 0x00 && folded[15] == 0x4E, "console keystrokes: first char after fold is 一");
    Check(folded[40 + 14] == 0x20 && folded[40 + 15] == 0x00, "console keystrokes: folded separator is a real space");
    Check(folded[80 + 14] == 0x8C && folded[80 + 15] == 0x4E, "console keystrokes: third char after fold is 二");
    Check(folded[120 + 14] == 0x0D, "console keystrokes: folded line still ends with cr");
}

// ---------------------------------------------------------------------------
// 6 Claude 生命周期纠偏（转录本）与挂起连接释放（2026-10-19）
// ---------------------------------------------------------------------------
{
    static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "ap-claude-reconcile", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
    static string TranscriptPath(string root, string sessionID)
    {
        var dir = Path.Combine(root, "F--Git-demo");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, sessionID + ".jsonl");
    }
    static string ClaudeStamp(DateTimeOffset at) => at.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
    static string UserLine(string text, DateTimeOffset at) =>
        $"{{\"type\":\"user\",\"timestamp\":\"{ClaudeStamp(at)}\",\"message\":{{\"content\":\"{text}\"}}}}";
    static string AssistantLine(DateTimeOffset at, string? stopReason = null) =>
        $"{{\"type\":\"assistant\",\"timestamp\":\"{ClaudeStamp(at)}\",\"message\":{{\"content\":[{{\"type\":\"text\",\"text\":\"ok\"}}]{(stopReason is null ? "" : $",\"stop_reason\":\"{stopReason}\"")}}}}}";
    static string StopSummaryLine(DateTimeOffset at) =>
        $"{{\"type\":\"system\",\"subtype\":\"stop_hook_summary\",\"timestamp\":\"{ClaudeStamp(at)}\"}}";
    static TaskSnapshot Find(TaskCatalog catalog, string sessionID) =>
        catalog.Projection().Tasks.First(task => task.Id == sessionID);
    static void Write(string path, params string[] lines) => File.WriteAllText(path, string.Join('\n', lines) + "\n");

    var reconcileT0 = DateTimeOffset.UtcNow;

    // 6.1 Stop 丢了（Desktop 预热/超时杀脚本）：转录本里本回合已有 end_turn → 判成功
    {
        var root = NewRoot();
        const string sid = "claude-lost-stop";
        var path = TranscriptPath(root, sid);
        var scoped = new TaskCatalog(claudeProjectsRoot: root);
        scoped.Accept(C("UserPromptSubmit", new { session_id = sid, prompt = "跑一遍测试", transcript_path = path }), reconcileT0);
        Check(Find(scoped, sid).Lifecycle == AgentLifecycle.Running, "claude reconcile: prompt starts a running task");

        Write(path,
            UserLine("跑一遍测试", reconcileT0.AddSeconds(1)),
            AssistantLine(reconcileT0.AddSeconds(2), "end_turn"));
        scoped.Maintain(reconcileT0.AddSeconds(61));

        var done = Find(scoped, sid);
        Check(done.Lifecycle == AgentLifecycle.Succeeded,
            "claude reconcile: a missing Stop is recovered from the transcript");
        Check(done.IsUnread && done.CompletedAt is not null,
            "claude reconcile: recovered completion is unread and carries the stop time");
    }

    // 6.2 打开的轮次（工具在跑、没有结束证据）：绝不因为"没有 Stop"就判结束，也不能被静默扫描判中断
    {
        var root = NewRoot();
        const string sid = "claude-open-turn";
        var path = TranscriptPath(root, sid);
        var scoped = new TaskCatalog(claudeProjectsRoot: root);
        scoped.Accept(C("UserPromptSubmit", new { session_id = sid, prompt = "跑个长任务", transcript_path = path }), reconcileT0);
        Write(path,
            UserLine("跑个长任务", reconcileT0.AddSeconds(1)),
            AssistantLine(reconcileT0.AddSeconds(2)));

        scoped.Maintain(reconcileT0.AddSeconds(61));
        Check(Find(scoped, sid).Lifecycle == AgentLifecycle.Running,
            "claude reconcile: an open turn is never declared finished");
        scoped.Maintain(reconcileT0.AddSeconds(11 * 60));
        var aged = Find(scoped, sid);
        Check(aged.Lifecycle != AgentLifecycle.Succeeded,
            "claude reconcile: an open turn is never completed by the silence sweep");
        Check(aged.Lifecycle == AgentLifecycle.Offline,
            "claude reconcile: with no session registry entry, 'gone' is still the runtime sweep's call (Offline)");
    }

    // 6.4 结束证据属于上一轮（时间早于 StartedAt）：不能被当成这一轮已完成
    {
        var root = NewRoot();
        const string sid = "claude-stale-stop";
        var path = TranscriptPath(root, sid);
        Write(path,
            UserLine("上一轮", reconcileT0.AddSeconds(-120)),
            AssistantLine(reconcileT0.AddSeconds(-110), "end_turn"),
            UserLine("这一轮", reconcileT0.AddSeconds(-60)));
        var scoped = new TaskCatalog(claudeProjectsRoot: root);
        scoped.Accept(C("UserPromptSubmit", new { session_id = sid, prompt = "这一轮", transcript_path = path }), reconcileT0);

        scoped.Maintain(reconcileT0.AddSeconds(61));
        Check(Find(scoped, sid).Lifecycle == AgentLifecycle.Running,
            "claude reconcile: an end_turn from an earlier turn does not complete this one");
    }

    // 6.5 证据读取本身：stop_hook_summary 也算结束证据，之后的用户输入重新打开轮次
    {
        var path = Path.Combine(Path.GetTempPath(), "ap-turn-evidence-" + Guid.NewGuid().ToString("N") + ".jsonl");
        Write(path,
            UserLine("你好", reconcileT0),
            AssistantLine(reconcileT0.AddSeconds(1)),
            StopSummaryLine(reconcileT0.AddSeconds(2)));
        var closed = ClaudeTranscriptReader.ReadTurnEvidence(path);
        Check(closed is { HasOpenTurn: false } && closed.StopAt is not null,
            "claude reconcile: stop_hook_summary closes the turn");
        Check(closed!.LastActivityAt is not null, "claude reconcile: evidence carries the last activity time");

        File.AppendAllText(path, UserLine("再来", reconcileT0.AddSeconds(3)) + "\n");
        var reopened = ClaudeTranscriptReader.ReadTurnEvidence(path);
        Check(reopened is { HasOpenTurn: true }, "claude reconcile: a newer user line reopens the turn");
        Check(ClaudeTranscriptReader.ReadTurnEvidence(path + ".missing") is null,
            "claude reconcile: a missing transcript is unknown, never 'finished'");
    }

    // 6.6 挂住的连接对端关闭：PendingHold 必须放行（HookTcpServer 据此摘死记录并撤卡）
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var acceptTask = listener.AcceptTcpClientAsync();
        using var held = new System.Net.Sockets.TcpClient();
        held.Connect(System.Net.IPAddress.Loopback, port);
        using var serverSide = await acceptTask;

        var closed = PendingHold.WaitForPeerCloseAsync(serverSide);
        await System.Threading.Tasks.Task.Delay(80);
        Check(!closed.IsCompleted, "pending hold: waits while the peer is still connected");
        held.Close();
        Check(await System.Threading.Tasks.Task.WhenAny(closed, System.Threading.Tasks.Task.Delay(2000)) == closed,
            "pending hold: peer close releases the waiter");
        listener.Stop();
    }
}

Console.WriteLine(failures.Count == 0 ? "ALL CHECKS PASSED" : $"FAILURES: {string.Join(", ", failures)}");
return failures.Count == 0 ? 0 : 1;

/// <summary>啥都放行的桩：用来测"手机上点了之后桥该怎么写状态"。</summary>
sealed class AllowResolver : ICodexPermissionResolver
{
    public bool Resolve(string sessionID, CodexPermissionDecision decision) => true;

    public bool ResolveQuestion(string sessionID, string? rawToolInput, IReadOnlyList<QuestionAnswer> answers) => true;

    public bool DispatchPrompt(string sessionID, AgentSource source, string prompt, string? workspaceDir = null, bool isRunning = false) => true;
}

sealed class NullResolver : ICodexPermissionResolver
{
    public bool Resolve(string sessionID, CodexPermissionDecision decision) => false;

    public bool ResolveQuestion(string sessionID, string? rawToolInput, IReadOnlyList<QuestionAnswer> answers) => false;

    public bool DispatchPrompt(string sessionID, AgentSource source, string prompt, string? workspaceDir = null, bool isRunning = false) => false;
}

/// <summary>记下作答回填内容的桩：验证"整份带回原始 tool_input + 按题序的答案"。</summary>
sealed class RecordingResolver : ICodexPermissionResolver
{
    public string? RawInput;
    public IReadOnlyList<QuestionAnswer>? Written;
    public string? PromptSessionID;
    public AgentSource? PromptSource;
    public string? PromptText;
    public string? PromptWorkspace;
    public bool? PromptIsRunning;

    public bool Resolve(string sessionID, CodexPermissionDecision decision) => true;

    public bool ResolveQuestion(string sessionID, string? rawToolInput, IReadOnlyList<QuestionAnswer> answers)
    {
        RawInput = rawToolInput;
        Written = answers;
        return true;
    }

    public bool DispatchPrompt(string sessionID, AgentSource source, string prompt, string? workspaceDir = null, bool isRunning = false)
    {
        PromptSessionID = sessionID;
        PromptSource = source;
        PromptText = prompt;
        PromptWorkspace = workspaceDir;
        PromptIsRunning = isRunning;
        return true;
    }
}
