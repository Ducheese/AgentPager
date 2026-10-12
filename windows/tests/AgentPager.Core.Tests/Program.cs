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

// 1. session.created
catalog.Accept(H("session.created", new
{
    sessionID = "ses_1",
    info = new { id = "ses_1", directory = @"F:\Git\demo", title = "New session - 2026-10-06T00:00:00.000Z", time = new { created, updated = created } },
}));
Check(Task().Source == AgentSource.OpenCode, "source=opencode");
Check(Task().Lifecycle == AgentLifecycle.Idle, "session.created -> idle");
Check(Task().ProjectName == "demo", "project name from directory");
Check(Task().StartedAt == DateTimeOffset.FromUnixTimeMilliseconds(created), "startedAt from session.created");

// 2. chat.message
Thread.Sleep(15);
catalog.Accept(H("chat.message", new
{
    sessionID = "ses_1",
    model = new { providerID = "ccproxy", modelID = "deepseek/deepseek-v4.1-flash" },
    text = "修复登录问题\n并补充测试",
}));
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
Check(OpenCodePluginConfiguration.EmbeddedVersion == 10, $"embedded plugin version = 10 (got {OpenCodePluginConfiguration.EmbeddedVersion})");
var first = installer.Install();
Check(first.Changed && File.Exists(Path.Combine(temp, "plugins", "agentpager-opencode.js")), "install writes plugin");
Check(installer.Status == OpenCodePluginStatus.Installed && installer.IsInstalled(), "status: 已写入（与内置一致）");
Check(installer.InstalledVersion == 10, "installed version = 10");
var second = installer.Install();
Check(!second.Changed, "reinstall is idempotent");
var pluginPath = Path.Combine(temp, "plugins", "agentpager-opencode.js");
var content = File.ReadAllText(pluginPath);
Check(content.Contains("Managed by AgentPager (OpenCode)"), "managed marker present");
Check(content.Contains("agentpager-opencode v10"), "version header present");
Check(content.Contains("hook_source: \"opencode\"") && content.Contains("49361"), "plugin content sane");
File.WriteAllText(pluginPath, content.Replace("const MAX_TEXT = 1000", "const MAX_TEXT = 500"));
Check(installer.Status == OpenCodePluginStatus.Outdated, "status: 待更新（内容与内置不一致）");
Check(installer.InstalledVersion == 10, "outdated file still reports v10");
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

// A4: 静默 ≥10 分钟判离线（工具活动中不算静默）
var agyCatalog = new TaskCatalog();
var agyT0 = DateTimeOffset.UtcNow;
agyCatalog.Accept(A("PreInvocation", new { conversationId = "agy_off", workspacePaths = new[] { @"F:\Git\demo" } }), agyT0);
agyCatalog.Accept(A("PreToolUse", new { conversationId = "agy_off", toolCall = new { name = "run_command", args = new { CommandLine = "Start-Sleep 1" } } }), agyT0.AddSeconds(1));
Check(agyCatalog.Projection().Tasks.First(t => t.Id == "agy_off").Lifecycle == AgentLifecycle.Running, "A4: agy task running before timeout");
agyCatalog.Maintain(agyT0.AddMinutes(11));
Check(agyCatalog.Projection().Tasks.First(t => t.Id == "agy_off").Lifecycle == AgentLifecycle.Running, "A4: active tool is not silent");
agyCatalog.Accept(A("PostToolUse", new { conversationId = "agy_off", toolCall = new { name = "run_command", args = new { CommandLine = "x" } } }), agyT0.AddSeconds(2));
agyCatalog.Maintain(agyT0.AddMinutes(12));
Check(agyCatalog.Projection().Tasks.First(t => t.Id == "agy_off").Lifecycle == AgentLifecycle.Offline, "A4: silent agy task -> offline after 10min");

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
var (agyPrompt, agyPreview, agyTokens) = AgyTranscriptReader.ExtractLatest(agyTp);
Check(agyPrompt == "修好登录", $"agy: transcript prompt cleaned (got '{agyPrompt}')");
Check(agyPreview == "已修复", $"agy: transcript preview from pure-text latest step (got '{agyPreview}')");
Check(agyTokens is { Input: 150, CachedInput: 30, Output: 40, Total: 220 }, $"agy: transcript tokens follow latest (got '{agyTokens}')");
File.Delete(agyTp);

var agyTp2 = Path.Combine(Path.GetTempPath(), "ap-verify-agy-" + Guid.NewGuid().ToString("N") + ".jsonl");
File.WriteAllLines(agyTp2, new[]
{
    "{\"type\":\"USER_INPUT\",\"content\":\"<USER_REQUEST>再看看</USER_REQUEST>\"}",
    "{\"type\":\"PLANNER_RESPONSE\",\"content\":\"正在执行\",\"tool_calls\":[{\"name\":\"run_command\"}],\"input_tokens\":10,\"output_tokens\":5}",
});
var (agyPrompt2, agyPreview2, agyTokens2) = AgyTranscriptReader.ExtractLatest(agyTp2);
Check(agyPrompt2 == "再看看", "agy: transcript prompt 2");
Check(agyPreview2 is null, "agy: preview suppressed when latest step has tool calls");
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

// SessionStart 的语义只对**已经在面板上**的行生效：resume / clear 把会话复位成 idle。
var resumeCatalog = new TaskCatalog();
resumeCatalog.Accept(C("UserPromptSubmit", new { session_id = "resume_1", prompt = "第一轮" }), claudeNow);
resumeCatalog.Accept(C("Stop", new { session_id = "resume_1", last_assistant_message = "完事" }), claudeNow.AddSeconds(1));
TaskSnapshot Resume() => resumeCatalog.Projection().Tasks.First(t => t.Id == "resume_1");
Check(Resume().Lifecycle == AgentLifecycle.Succeeded, "claude: resume fixture finished before the second start");
resumeCatalog.Accept(C("SessionStart", new { session_id = "resume_1", source = "resume" }), claudeNow.AddSeconds(2));
Check(Resume().Lifecycle == AgentLifecycle.Idle && Resume().CompletedAt is null,
    $"claude: SessionStart on a known session still resets to idle (got {Resume().Lifecycle})");

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
Check(installedHooks["PermissionRequest"]!.AsArray()[0]!["hooks"]!.AsArray()[0]!["timeout"]!.GetValue<int>() == 86_400, "claude: permission hook keeps the long timeout");
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

Check(!claudeHooks.Install().Changed, "claude: install is idempotent");

// 三态：脚本落后于内置版本时必须是"待更新"，而不是"未安装"（bool 表达不了这一态）
Check(claudeHooks.Status == ClaudeHookStatus.Installed, $"claude: fresh install reads as installed (got {claudeHooks.Status})");
File.WriteAllText(claudeHooks.ScriptPath, "// agentpager-claude v0\r\n// Managed by AgentPager (Claude Code) - do not edit this file manually.\r\n");
Check(claudeHooks.Status == ClaudeHookStatus.Outdated, $"claude: stale script reads as outdated (got {claudeHooks.Status})");
Check(claudeHooks.IsInstalled(), "claude: outdated still counts as installed (hooks are registered)");
Check(claudeHooks.InstalledVersion == 0, $"claude: installed version read from the script header (got {claudeHooks.InstalledVersion})");
Check(claudeHooks.Install().Changed, "claude: reinstall repairs the stale script");
Check(claudeHooks.Status == ClaudeHookStatus.Installed, "claude: repaired back to installed");

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
Check(recorder.AnswerText == "就用 A", "claude answer: the text reaches the resolver");
Check(JsonNode.Parse(recorder.RawInput!)!["questions"] is JsonArray { Count: 1 }, "claude answer: raw tool input passed back whole");
Check(AnswerTask().Capabilities.Count == 0 && AnswerTask().Lifecycle == AgentLifecycle.Running,
    "claude answer: capability cleared and the turn resumes");
Check(answerCatalog.Projection().PendingRequests.Count == 0, "claude answer: question card cleared");

// 没有原始入参时不给 Answer 能力位（免得 App 画出送不出去的输入框）
var noInputCatalog = new TaskCatalog();
noInputCatalog.Accept(C("PermissionRequest", new { session_id = "claude_8", tool_name = "AskUserQuestion" }), answerNow);
Check(!noInputCatalog.Projection().Tasks.First(x => x.Id == "claude_8").Capabilities.Contains(TaskCapability.Answer),
    "claude answer: no raw input -> no Answer capability");

// ===================== 22. 中间正文进第三行（B） =====================
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
Check(ReplyTask().LatestStep == "先说一句中间的话", $"claude reply: mid-turn text reaches the third line (got '{ReplyTask().LatestStep}')");

// 工具在跑：新的中间正文不得盖掉工具步
replyCatalog.Accept(C("PreToolUse", new { session_id = "claude_6", transcript_path = replyPath, tool_name = "Bash", tool_input = new { command = "npm test" } }), replyNow.AddSeconds(4));
File.AppendAllLines(replyPath,
    ["{\"type\":\"assistant\",\"isSidechain\":false,\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"工具跑着的时候写的字\"}]}}"]);
replyCatalog.Maintain(replyNow.AddSeconds(6));
Check(ReplyTask().LatestStep == "Bash npm test", $"claude reply: tool step survives while a tool runs (got '{ReplyTask().LatestStep}')");

// 工具结束：新的正文可以回来（心跳按转录本 mtime 门控，所以这里要再追加一次）
replyCatalog.Accept(C("PostToolUse", new { session_id = "claude_6", transcript_path = replyPath, tool_name = "Bash" }), replyNow.AddSeconds(7));
File.AppendAllLines(replyPath,
    ["{\"type\":\"assistant\",\"isSidechain\":false,\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"工具跑完之后写的话\"}]}}"]);
replyCatalog.Maintain(replyNow.AddSeconds(9));
Check(ReplyTask().LatestStep == "工具跑完之后写的话", $"claude reply: preview resumes after the tool ends (got '{ReplyTask().LatestStep}')");

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

// 最新一步又是正文时，照常给出候选
var textLatest = ReadTranscript(
[
    "{\"type\":\"user\",\"isSidechain\":false,\"message\":{\"role\":\"user\",\"content\":\"干活\"}}",
    "{\"type\":\"assistant\",\"isSidechain\":false,\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"tool_use\",\"name\":\"Bash\"}]}}",
    "{\"type\":\"assistant\",\"isSidechain\":false,\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"跑完了，接着看\"}]}}",
]);
Check(textLatest.LatestReply == "跑完了，接着看",
    $"claude reply: text after a tool still becomes the candidate (got '{textLatest.LatestReply}')");

// 端到端：工具跑完后心跳不得把工具步盖成上一段正文
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
Check(clobberCatalog.Projection().Tasks.First(x => x.Id == "claude_11").LatestStep == "先看看代码",
    "claude clobber: text shows while the model writes");
clobberCatalog.Accept(C("PreToolUse", new { session_id = "claude_11", transcript_path = clobberPath, tool_name = "Bash", tool_input = new { command = "ls" } }), clobberNow.AddSeconds(4));
File.AppendAllLines(clobberPath,
    ["{\"type\":\"assistant\",\"isSidechain\":false,\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"tool_use\",\"name\":\"Bash\"}]}}"]);
clobberCatalog.Accept(C("PostToolUse", new { session_id = "claude_11", transcript_path = clobberPath, tool_name = "Bash" }), clobberNow.AddSeconds(5));
clobberCatalog.Maintain(clobberNow.AddSeconds(8));
Check(clobberCatalog.Projection().Tasks.First(x => x.Id == "claude_11").LatestStep == "Bash ls",
    $"claude clobber: heartbeat must not overwrite the tool step with an older reply (got '{clobberCatalog.Projection().Tasks.First(x => x.Id == "claude_11").LatestStep}')");
File.Delete(clobberPath);

// 本轮自己的正文（时间晚于本轮起点）照常生效
File.AppendAllLines(stalePath,
[
    "{\"type\":\"user\",\"isSidechain\":false,\"timestamp\":\"" + staleNow.AddSeconds(1).ToString("o") + "\",\"message\":{\"role\":\"user\",\"content\":\"新的一轮\"}}",
    "{\"type\":\"assistant\",\"isSidechain\":false,\"timestamp\":\"" + staleNow.AddSeconds(2).ToString("o") + "\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"这一轮的回复\"}]}}",
]);
staleCatalog.Maintain(staleNow.AddSeconds(6));
Check(staleCatalog.Projection().Tasks.First(x => x.Id == "claude_10").LatestStep == "这一轮的回复",
    $"claude reply: current turn's reply still lands (got '{staleCatalog.Projection().Tasks.First(x => x.Id == "claude_10").LatestStep}')");
File.Delete(stalePath);

// 回显守卫在正文路径同样生效
var echoCatalog = new TaskCatalog();
echoCatalog.Accept(C("UserPromptSubmit", new { session_id = "claude_9", prompt = "把构建脚本修一下", transcript_path = replyPath }), replyNow);
File.AppendAllLines(replyPath,
    ["{\"type\":\"assistant\",\"isSidechain\":false,\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"好，把构建脚本修一下这项工作我这就开始\"}]}}"]);
echoCatalog.Maintain(replyNow.AddSeconds(3));
Check(echoCatalog.Projection().Tasks.First(x => x.Id == "claude_9").LatestStep is null,
    "claude reply: prompt echo rejected on the heartbeat path too");
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
Check(SubPermTask().Lifecycle == AgentLifecycle.Running && SubPermTask().Capabilities.Count == 0,
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
Check(multiResolver.AnswersWritten is null, "multi advance: nothing is written back until every question is answered");

var secondCard = multiCatalog.Projection().PendingRequests.First();
Check(secondCard.Summary == "第 2/2 题： 第二题？" && secondCard.Options is ["C", "D"],
    $"multi advance: the card moves on to the second question (got '{secondCard.Summary}' / {string.Join(",", secondCard.Options)})");
Check(Multi().Lifecycle == AgentLifecycle.WaitingAnswer && Multi().Capabilities.Contains(TaskCapability.Answer),
    "multi advance: still waiting and still answerable");
Check(Multi().LatestStep == "第 2/2 题： 题二", $"multi advance: third line follows along (got '{Multi().LatestStep}')");

var lastAck = multiCatalog.Perform(Guid.NewGuid(), new ControlPayload("claude_30", ControlAction.Answer, "C"), multiResolver);
Check(lastAck.Result == ControlResult.Accepted, "multi advance: last answer accepted");
Check(multiResolver.AnswersWritten is { Count: 2 } written &&
      written["第一题？"] == "A" && written["第二题？"] == "C",
    "multi advance: every answer is written back as one map");
Check(multiCatalog.Projection().PendingRequests.Count == 0 && Multi().Lifecycle == AgentLifecycle.Running,
    "multi advance: the wait ends and the turn resumes");

// 单题不受影响：仍走"点选项→answers / 打字→response"的老路，不进逐题推进
var singleResolver = new RecordingResolver();
var singleAdvance = new TaskCatalog();
singleAdvance.Accept(C("PermissionRequest", new { session_id = "claude_31", tool_name = "AskUserQuestion", tool_input = askInput }), multiNow);
singleAdvance.Perform(Guid.NewGuid(), new ControlPayload("claude_31", ControlAction.Answer, "就用 A"), singleResolver);
Check(singleResolver.RawInput is not null && singleResolver.AnswersWritten is null,
    "multi advance: a single question still uses the plain answer path");

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
Check(annotatedResolver.AnswersWritten is { Count: 2 } annotatedMap &&
      annotatedMap["第一题？"] == "A" && annotatedMap["第二题？"] == "C",
    "option display: multi answers normalise back to bare labels");

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
Check(termAfterEnd.Lifecycle == AgentLifecycle.Succeeded && termAfterEnd.CompletedAt == termT0.AddSeconds(5),
    "terminal keep: sessionEnd preserves succeeded state and completedAt");

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

Console.WriteLine();
Console.WriteLine(failures.Count == 0 ? "ALL CHECKS PASSED" : $"FAILURES: {string.Join(", ", failures)}");
return failures.Count == 0 ? 0 : 1;

sealed class NullResolver : ICodexPermissionResolver
{
    public bool Resolve(string sessionID, CodexPermissionDecision decision) => false;

    public bool ResolveAnswer(string sessionID, string rawToolInput, string answer) => false;

    public bool ResolveAnswers(string sessionID, string rawToolInput, IReadOnlyDictionary<string, string> answers) => false;
}

/// <summary>记下作答回填内容的桩：验证"整份带回原始 tool_input + 自由文本 / 多题整张答案表"。</summary>
sealed class RecordingResolver : ICodexPermissionResolver
{
    public string? RawInput;
    public string? AnswerText;
    public Dictionary<string, string>? AnswersWritten;

    public bool Resolve(string sessionID, CodexPermissionDecision decision) => true;

    public bool ResolveAnswer(string sessionID, string rawToolInput, string answer)
    {
        RawInput = rawToolInput;
        AnswerText = answer;
        return true;
    }

    public bool ResolveAnswers(string sessionID, string rawToolInput, IReadOnlyDictionary<string, string> answers)
    {
        AnswersWritten = new Dictionary<string, string>(answers, StringComparer.Ordinal);
        return true;
    }
}
