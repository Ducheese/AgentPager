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
Check(OpenCodePluginConfiguration.EmbeddedVersion == 8, $"embedded plugin version = 8 (got {OpenCodePluginConfiguration.EmbeddedVersion})");
var first = installer.Install();
Check(first.Changed && File.Exists(Path.Combine(temp, "plugins", "agentpager-opencode.js")), "install writes plugin");
Check(installer.Status == OpenCodePluginStatus.Installed && installer.IsInstalled(), "status: 已写入（与内置一致）");
Check(installer.InstalledVersion == 8, "installed version = 8");
var second = installer.Install();
Check(!second.Changed, "reinstall is idempotent");
var pluginPath = Path.Combine(temp, "plugins", "agentpager-opencode.js");
var content = File.ReadAllText(pluginPath);
Check(content.Contains("Managed by AgentPager (OpenCode)"), "managed marker present");
Check(content.Contains("agentpager-opencode v8"), "version header present");
Check(content.Contains("hook_source: \"opencode\"") && content.Contains("49361"), "plugin content sane");
File.WriteAllText(pluginPath, content.Replace("const MAX_TEXT = 1000", "const MAX_TEXT = 500"));
Check(installer.Status == OpenCodePluginStatus.Outdated, "status: 待更新（内容与内置不一致）");
Check(installer.InstalledVersion == 8, "outdated file still reports v8");
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

Console.WriteLine();
Console.WriteLine(failures.Count == 0 ? "ALL CHECKS PASSED" : $"FAILURES: {string.Join(", ", failures)}");
return failures.Count == 0 ? 0 : 1;

sealed class NullResolver : ICodexPermissionResolver
{
    public bool Resolve(string sessionID, CodexPermissionDecision decision) => false;
}
