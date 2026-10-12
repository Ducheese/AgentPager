# Claude Code → AgentPager 状态映射表（v1 基线）

本文档定义 Claude Code（闭源 CLI，本机实测 **2.1.236**）到 AgentPager `TaskSnapshot` 的完整映射。
范围：Windows Bridge + Android。macOS 遵循同一逻辑（差异见第六节）。

> **与另外两篇的关系**：opencode / Antigravity 两篇写于"实现之前"，是纯设计稿；Claude 这篇写于实现**之后**——仓库里已有一套 Claude 实现（早于 opencode/agy，`0cf9f6c` 起），但从未按统一规范校订过。
> 因此本文的写法是：**每节先给"目标基线"（应该是什么），再用 `现:` 标注当前代码的实际行为**。逐条差距、证据与取舍集中在配套的 `claude-code-integration-notes.md`（差距编号 G1…G24，与本文的 `——Gx` 标注一一对应）。
> **本文只做研究，不改代码。**所有"事实"都附取证路径；无法确证的标 `待实测`。

> **落地状态（2026-10-08）**：本规范已**全部落地**（逐条清单见 `claude-code-integration-notes.md` §三）。
> 因此下文所有 `现:` / "现状" / "当前代码" 标注一律读作**改动前的旧行为**，保留它们是作为问题出处与判据；
> 当前行为以代码为准。两处**有意偏离**本稿原案、以落地实现为准：
> ① 第三行回复态走**转录本**而非 `MessageDisplay`（零渲染侵入，与 agy 同一套读取器），`Stop` 的
> `last_assistant_message` 作兜底；② 通道由"exe `--hook`"改为 **Node 转发脚本 + exec 形式**（见第六节判例 2）。

---

## 一、闭源环境下的依据链（Ground Truth）

Claude Code 无源码可读，但本机有四个权威可观测面，本次研究全部结论均可回指其中之一：

### 1. `bin/claude.exe`（一手，最强）

`C:\Users\alord\AppData\Roaming\npm\node_modules\@anthropic-ai\claude-code\bin\claude.exe`，330 MB 单文件。它是 **bun 打包产物，内部完整嵌着压缩后的 JS 源码与 Claude Code 自己的 hook 参考元数据**，可直接读实现。

```bash
# 提取可打印串（临时物，用完即删）
tr -c '[:print:]' '\n' < claude.exe | awk 'length($0)>=16' > strings.txt
```

关键提取物：

| 目标 | 检索式 | 得到什么 |
|---|---|---|
| 全部事件名 | `grep -o 'hook_event_name:"[A-Za-z]*"'` | **每个事件恰好一处构造点** = 该版本的权威事件表（31 个，见第二节） |
| 公共入参 | `grep -o 'function Ny(.\{0,700\}'` | 所有事件共享字段的构造式 |
| 单事件入参 | `grep -o '.\{0,260\}hook_event_name:"Stop".\{0,260\}'` | 该事件对象字面量 = 精确字段表 |
| 输出 schema | `grep -o '.\{0,80\}hookEventName:kt("PermissionRequest").\{0,500\}'` | zod 联合体，可逐字校验我们的回包 |
| 厂商文档 | `grep -n "When a permission dialog is displayed"` 后读邻近行 | 内置 `/hooks` 参考文本（每个事件的 summary + 退出码语义） |

### 2. 官方 hooks 文档

`https://code.claude.com/docs/en/hooks`（`docs.claude.com/.../hooks` 301 跳到这里）。**注意版本差**：该文档描述的是比本机更新的版本——文档里的 `PreModelSwitch` / `PostModelSwitch` / `scratchpad_dir` 在本机 2.1.236 中**实测 0 命中**（`grep -c "PreModelSwitch\|PostModelSwitch"` = 0）。凡文档与本机二进制冲突，**以二进制为准**，文档只用来解释语义。

### 3. 转录本 `~/.claude/projects/<slug>/<session_id>.jsonl`

与 agy 的 `transcript.jsonl` 地位相同，是**唯一能拿到标题 / Token / 正文 / 排队**的地方。实测行类型（本会话 370 行统计）：

```
last-prompt / mode / permission-mode / atis-latch / attachment / file-history-snapshot
user / assistant / ai-title / summary / custom-title / tag / pr-link / relocated / leafUuid
system(子类型 stop_hook_summary / turn_duration / local_command / away_summary)
queue-operation(enqueue / remove)
```

### 4. 会话注册表 `~/.claude/sessions/<pid>.json`

**本机活跃 Claude 进程的实时清单**（agy 的 `conversation_summaries.db` 对应物）：

```json
{"pid":13116,"sessionId":"203afbaa-...","cwd":"F:\\Git","startedAt":1791437904395,
 "version":"2.1.236","kind":"interactive","entrypoint":"cli","name":"git-90",
 "nameSource":"derived","status":"busy","updatedAt":1791438208049}
```

`nameSource` 为 `derived` 时名字是从目录派生的默认名（**不是真标题**），与 opencode 的 `New session - ` 默认名同理，必须忽略。

---

## 二、通道机制（现状）

### 1. 信封与传输

Claude Code 的 hook 是"**外部命令 + stdin/stdout**"模型。AgentPager 与 Antigravity 同构：由 Bridge 部署一个轻量 Node 脚本，由它把 payload 包成统一信封写 TCP `127.0.0.1:49361`。

```json
{ "hook_source": "claude", "payload": { "hook_event_name": "PreToolUse", "session_id": "...", ... } }
```

- **载体**：`~/.claude/agentpager-claude.js`（版本头 `// agentpager-claude vN`，由 Bridge 内置资源部署，卸载时按托管标记删除）。**不再**冷启 bridge exe——旧形态 `"<exe>" --hook --source claude` 每次事件要拉起约 220 MB 的自包含 .NET 进程（agy 判例 #1 实测 300~800 ms/次）。
- **命令形态是 exec 形式**：`{"type":"command","command":"node","args":["<脚本绝对路径>"],"statusMessage":"Managed by AgentPager (Claude Code)"}`。2.1.236 的 schema 保证 `args` 存在时 "spawned directly with these arguments — no shell"，于是 Windows 默认 shell（有 Git Bash 用 bash、否则 PowerShell）的引号/反斜杠/`$` 转义问题整体消失。**前提：`node` 在该进程的 PATH 里**。
- **脚本路径固定**，与 bridge 装在哪个 vN 目录无关——**换 bridge 目录不再需要重装 hook**（Codex 仍是 exe 形态，那条仍需重装）。
- **写入 `~/.claude/settings.json`**：三层嵌套 `事件 → matcher 组 → handler`，保留用户既有 hook、幂等替换、卸载只删托管项；托管识别兼容三代形态（exec 形式 / `statusMessage` 标记 / 旧 exe 命令）。
- **fail-open**：脚本 500 ms 连接与空闲超时；任何异常静默 `exit 0`，**非权限事件 stdout 恒空**（旧实现在每条事件回写一个裸换行，而纯文本 stdout 会在 UserPromptSubmit / SessionStart 上被注入 Claude 上下文）。Bridge 不在线绝不阻塞 Claude。
- **权限事件例外**：脚本保持连接、把 Bridge 的答复原样写到 stdout；Bridge 不在线则不回包，Claude 照常弹本地对话框。
- 事件到达 Bridge 后统一进 `TaskCatalog.Accept(ClaudeHookPayload)`。

### 2. 已注册事件表（14 个，`ClaudeHooks.cs:85-101`）

`SessionStart` `SessionEnd` `UserPromptSubmit` `Stop` `StopFailure` `SubagentStart` `SubagentStop` `Notification` `PreToolUse` `PermissionRequest`(timeout 86400) `PostToolUse` `PostToolUseFailure` `PermissionDenied` `PreCompact`

对照本机 2.1.236 的**完整事件表（31 个，实测构造点唯一）**：

```
SessionStart  Setup  UserPromptSubmit  UserPromptExpansion
PreToolUse  PermissionRequest  PermissionDenied
PostToolUse  PostToolUseFailure  PostToolBatch
Notification  MessageDisplay
SubagentStart  SubagentStop  TaskCreated  TaskCompleted
Stop  StopFailure  TeammateIdle
InstructionsLoaded  ConfigChange  CwdChanged  DirectoryAdded  FileChanged
WorktreeCreate  WorktreeRemove  PreCompact  PostCompact
Elicitation  ElicitationResult  SessionEnd
```

**未注册但对状态流有直接价值**：`MessageDisplay`（正文流唯一源头）、`PostCompact`、`TaskCreated`/`TaskCompleted`、`TeammateIdle`、`UserPromptExpansion`。其余属配置/文件/工作树类，与任务状态无关，可长期忽略（差距 G2）。

### 3. 公共入参（实测构造式）

```js
{session_id, transcript_path, cwd, prompt_id, permission_mode,
 agent_id, agent_type, effort:{level}}
```

要点：

- **`agent_id` / `agent_type` 无条件出现在对象上**（子代理外为 `undefined`）→ **任何事件只要 `agent_id` 非空，就说明它发生在子代理内部**。这是 Claude 侧区分"主子代理"的唯一通用判据（差距 G11 的依据）。
- `permission_mode` 只在部分事件里传（`default`/`plan`/`acceptEdits`/`auto`/`dontAsk`/`bypassPermissions`）。
- `prompt_id` 在首次用户输入前不存在。

### 4. 权限事件的特殊通道（唯一双向）

`PermissionRequest` 是**唯一** Bridge 会挂住不回的形态（`HookTcpServer.cs:93-100`）：TCP 连接被存进 `_pending[sessionId]`，等手机端发 `Approve`/`Deny` 后由 `Resolve()` 写回 stdout（`HookTcpServer.cs:27-44`），hook 进程最长等 24 小时（`Program.cs:66`，与注册的 `timeout: 86400` 对齐）。

**厂商语义已核实**：二进制内置参考文本对该事件的 summary 是 **"When a permission dialog is displayed"**（不是一个"拦截在对话框之前"的钩子）。即：终端上权限对话框照常弹出，hook 只是**并行提供另一个答案**。因此"Bridge 挂住 24 小时 = 终端卡死"这一担心**不成立**；本机用户随时可以在终端照常作答。`待实测`：本机作答后 Claude 是否 abort 该 hook 子进程（推得是，`abortController.signal` 已传进 hook 调用）。

### 5. 回包副作用（现状风险）

非权限事件处理完后，Bridge 会回一个裸换行 `\n`（`HookTcpServer.cs:103`），hook 端把它原样复制到 stdout（`Program.cs:81`）。按官方解析规则，**exit 0 的非 JSON stdout 会被当作纯文本注入上下文**，且恰好只在 `UserPromptSubmit` / `UserPromptExpansion` / `SessionStart` / `PostModelSwitch` 上生效——AgentPager 注册的正是前两个。即**每次提交提示词与每次会话启动，都可能往 Claude 的上下文里塞一个空行**。opencode / agy 完全带外，无此副作用（差距 G3）。

---

## 三、生命周期（Lifecycle）映射总表

决策用的目标基线。`Activity` 见第四节；`现:` 标注当前代码行为。

| # | 事件 | 触发时机 | Lifecycle | Activity | 附带字段与请求 | 旧实现差异（本轮已按左列改动） |
|---|---|---|---|---|---|---|
| 1 | `SessionStart`（startup/resume/clear/compact/fork） | 会话开始或恢复 | `idle`（从 offline 恢复同此） | 无 | `Title` 取 `session_title`（自定义标题，可缺）；`Source` 记 `startup/resume/...`。**全新会话的那条 `SessionStart` 不建行**（它到达时转录本尚未创建、载荷也没有 `session_title`，建行只能得到一个顶着 cwd 目录名的无名幽灵）——见 G46 | ✅ 已按左列：不置 `Starting`（G4）；并由"无条件建行"改为"**只复位已知会话**"（G46） |
| 2 | `UserPromptSubmit` | 用户提交提示词（含 Claude 自启的轮次） | `running` | `thinking` | `UserPrompt`=prompt；**本轮计时起点**（仅上一状态为 idle/offline/succeeded/interrupted 时 `StartedAt=now`）；清空第三行与 `CompletedAt`；`PromptQueued=false`；`Title` 若 `session_title` 变化则同步 | ⚠ 现不重置计时、不清第三行、不认 `session_title`——G5/G6 |
| 3 | `PreToolUse`（`agent_id` 为空） | 工具即将执行（同步门禁） | `running` | 工具映射 | `LatestStep`=统一 `工具名 内容`（`ToolStep.Compose`）；**命令类必须带命令文本判测试** | ⚠ 现用工具名子串判 Activity，`Bash npm test` 落 Executing——G8 |
| 4 | `PreToolUse`（`tool_name=AskUserQuestion`） | 模型提问、等用户选择 | `waitingAnswer` | `thinking` | 入参 `questions[].{question, header, options[].label, multiSelect}`（实测 schema，1-4 题）→ `PendingRequest(question, 首题 question, 各题 option label)`、第三行=`header ?? question`、置顶+提示音 | ❌ 现无此分支，提问期间显示为普通 running+工具步——G7 |
| 5 | `PreToolUse`/`PostToolUse`（`agent_id` 非空） | 子代理内部工具 | **不改父任务第三行** | 只更新 `Subagents` 行 | 子代理行的 `LatestStep`=该步；父任务 `Activity=delegating` | ❌ 现全部按父任务处理，子代理的工具步会**覆盖父任务第三行**——G11 |
| 6 | `PostToolUse` | 工具成功 | `running` | `thinking` | 第三行粘滞不变（`tool_response` 不进第三行） | ✅ 一致 |
| 7 | `PostToolUseFailure` | 工具失败 | `running`（已 `interrupted` 则保持） | `thinking` | `LatestStep="✗ "+错误首行`；**`is_interrupt=true` 是用户 Esc 中断的唯一及时信号** | ⚠ 现无 `error`/`is_interrupt` 字段（payload record 未建模）——G12 |
| 8 | `PostToolBatch` | 一批并行工具全部落定 | 不变 | 不变 | 可选：无（不注册也不影响） | — |
| 9 | `PermissionRequest` | **权限对话框已弹出** | `waitingApproval` | `executing` | `PendingRequest(approval, tool_name + 摘要)`、`Capabilities=[Approve,Deny]` | 全项目唯一可手机操作的能力。**例外：`tool_name=AskUserQuestion`** 时按第 4 行走提问路径——它是 "requires user interaction" 工具，权限询问就是提问本身（实测：按审批处理会让手机上出现用不了的允许/拒绝按钮，还会把问题文本换成工具名） |
| 10 | `PermissionDenied` | **auto 模式**分类器拒绝该次工具调用（非用户拒绝） | `running`（保持，回合未结束） | `thinking` | `LatestStep="✗ 已拒绝"`；可回 `retry:true` | ❌ 现映射 `interrupted`+`CompletedAt`，**误判回合结束**——G13 |
| 11 | `Notification`（`idle_prompt` / `agent_needs_input` / `elicitation_dialog` / `elicitation_url_dialog`） | 等待用户输入类通知 | `waitingAnswer` | `thinking` | 挂起 question 卡（摘要取 `title ?? message`） | ⚠ 现只认前三种，漏 `elicitation_url_dialog`——G7 |
| 12 | `Notification`（`permission_prompt` / `auth_success` / `agent_completed` / `quota_auto_resume_*` / `elicitation_complete` / `elicitation_response`） | 其他通知 | 不变 | 不变 | 忽略 | ✅ |
| 13 | `MessageDisplay`（**不注册**） | assistant 正文流，每批新完成行触发（`delta`/`final`/`index`） | 不变 | 不变 | 正文预览改由**转录本 + 心跳**供给（见下） | 不注册的理由：每个 delta 批次要拉一次 hook 进程（`forceSyncExecution: true`、超时 10 s），有渲染侵入风险。改用转录本：每条 assistant 消息一行，心跳每 3 秒 mtime 门控读一次 |
| 14 | `SubagentStart` | 子代理派生（`Agent` 工具） | 父任务保持 | 父 `delegating` | 建 `Subagents` 行：`Id=agent_id`、`DisplayName=agent_type`、`Path="/"+agent_type` | ⚠ 现用 `task_description` 填步骤，而该字段**不存在于 SubagentStart 入参**（实测它属于 `TaskCreated`/`TaskCompleted`）→ 恒空——G10 |
| 15 | `SubagentStop` | 子代理结束 | 父任务**不得被拉回 running** | 全终态则回 `thinking` | 子代理行 → `Succeeded`；`last_assistant_message` 可作子代理终稿 | ❌ 现无条件 `Lifecycle=Running`，会复活终态任务——G14 |
| 16 | `TaskCreated` / `TaskCompleted` | 任务清单（`TaskCreate`/`TaskUpdate`）动作 | 不变 | 不变 | 可忽略（opencode 的 todo 同样未映射） | 新入参：`task_id/task_subject/task_description/teammate_name/team_name` |
| 17 | `Stop` | 回合结束 | `succeeded`（当前为 `interrupted` 时保持） | 清空 | `CompletedAt=now`、`IsUnread=true`、第三行=正文预览（`last_assistant_message` 单行截断，可作流式缺失时的兜底） | ✅ 主干一致；⚠ 现未用 `last_assistant_message` |
| 18 | `StopFailure` | 回合因 API 错误结束 | `interrupted` + `CompletedAt` | 清空 | 第三行=`✗ `+错误类型（`error` 字段；枚举 12 种） | ❌ 现映射为"从 succeeded 退回 running"的反向逻辑，语义不符——G15 |
| 19 | `PreCompact` / `PostCompact` | 压缩前/后 | 不变 | 不变 | 可选：第三行=`压缩上下文`；`PostCompact` 携带 `compact_summary` | ❌ 现把 `PreCompact` 当"运行中"写回，**会把终态任务复活**——G16 |
| 20 | `SessionEnd` | 会话终止（`reason`） | `offline`（终态保持） | 清空 | 未终态子代理级联扫 `interrupted` | ✅ 一致（现存代码还对 terminal 保持 CompletedAt） |

不在上表的事件（`Setup`、`UserPromptExpansion`、`InstructionsLoaded`、`ConfigChange`、`CwdChanged`、`DirectoryAdded`、`FileChanged`、`WorktreeCreate/Remove`、`Elicitation`/`ElicitationResult`、`TeammateIdle`）一律忽略。

### 通用竞态守卫（目标基线，四道，与 opencode/agy 对齐）

opencode 与 agy 都在文档里把这组守卫写死成条文（各自四条，措辞不同、覆盖面一致），Claude 目前**一道都没有**（G17）：

1. **等待态锁**：`WaitingApproval` / `WaitingAnswer` 期间，工具事件不得把状态压回 `running`，也不得清掉挂起卡片。
2. **终态不可逆**：`Succeeded` / `Interrupted` / `Offline` 之后，迟到的 `PostToolUse` / `PreCompact` / `SubagentStop` **绝对不得复活**为 `running`（现 `SubagentStop`、`PreCompact` 两处正在违反）。
3. **工具执行期正文覆盖守卫**：`PreToolUse`/`PostToolUse` 处于活动期时，`MessageDisplay` 的正文候选整条丢弃；正文只在工具结束后或回合结束时落到第三行。
4. **UserPrompt 回显守卫**：候选第三行文本与当前 `UserPrompt` 相同或整段包含（≥4 字）时丢弃、保留上一值——**第三行永不回显第二行**。现 Claude 的 `Summary()` 白名单里**恰恰含 `prompt` 键**（`TaskCatalog.cs:1077`），而 opencode 的同类白名单是**刻意排除 prompt 的**（`TaskCatalog.cs:1175`），agy 同样"严格排除 Prompt"——这是三家里唯一没有守卫的一家（G18）。

---

## 四、工具 → Activity 映射

Claude 侧工具名（2.1.236 实测存在于二进制；`Agent` = 派子代理的当前工具名，`Task` 是其历史名）。目标映射：

| 工具（`tool_name`） | Activity | 当前代码实际落点（`CodexEventReducer.ActivityFor`，子串匹配） |
|---|---|---|
| `Read`、`NotebookRead`、`ReadMcpResource*`、`ListMcpResources` | `reading` | ✅ `read`/`list` 命中 |
| `Glob`、`Grep`、MCP 名含 search/find/query | `searching` | ⚠ `Glob` 落 `executing`（只有 `grep` 命中） |
| `Edit`、`Write`、`NotebookEdit`、`MultiEdit` | `editing` | ✅ 命中（但见下 `TodoWrite`） |
| `WebFetch`、`WebSearch` | `browsing` | ✅ `web` 命中 |
| `Agent`、`Task`、`SendMessage` | `delegating` | ✅ `agent` 命中（`SendMessage` 落 executing） |
| `Bash` / `PowerShell`，命令含独立词 `test`/`pytest`/`jest`/`vitest` | `testing` | ❌ **落 `executing`**：`ActivityFor` 只看工具名，`ShellTestRun`（`TaskCatalog.cs:842`，opencode/agy 共用的词边界判据）在 Claude 路径上从未被调用——G8 |
| `Bash` / `PowerShell` 其余、未知工具、MCP | `executing` | ✅ |
| `TodoWrite`、`TaskCreate`/`TaskUpdate`/`TaskList`/`TaskGet`、`AskUserQuestion`、`EnterPlanMode`/`ExitPlanMode` | `thinking` | ❌ **`TodoWrite` 落 `editing`**（子串 `write` 先命中）。这正是 opencode 文档写死的坑（"不得让 todowrite 误命中 write，先判 todo"），Claude 走的是同一个 Codex 匹配器，**同一个坑原样复现**——G19 |
| `Skill`、`Workflow`、`Monitor` | `reading` / `executing` | ⚠ 落 `executing`（opencode 把 skill 归 reading） |

第三行的**工具显示名**（`ToolStep.Label`，`TaskCatalog.cs:811-828`）已含 glob/grep/skill/todo/websearch/webfetch/read/edit/write/bash 等，但**缺 Claude 专有名**：`PowerShell`（应显示"Bash"或"PowerShell"）、`AskUserQuestion`、`NotebookEdit`、`TaskCreate` 家族、`Monitor`、`Workflow` —— 现会原样打长名（差距 G20，与上表同批修）。

---

## 五、字段映射

> 第三列是**改动前的旧行为**；左侧是现行实现。

| `TaskSnapshot` 字段 | 来源（现行） | 旧行为 |
|---|---|---|
| `Id` | `session_id`（UUID） | ✅ |
| `Source` | 常量 `claudeCode`（wire 名，`WireModels.cs:234`） | ✅ |
| `ProjectName` | `cwd` 叶子目录名；空则 `Claude Code` | ✅ |
| `Title` | **只取会话真实标题**：转录本尾部 `{"type":"custom-title","customTitle":…}`（用户在终端自设）与 `{"type":"ai-title","aiTitle":…}`（Claude 自动生成的摘要标题），格式 `项目名 · <标题>`；`SessionStart`/`UserPromptSubmit` 的 `session_title` 可作实时通道；`sessions/<pid>.json` 的 `name` 注册表里的 `name` / `nameSource` 目前**只解析、未使用**（见 notes G23），标题只认上面那几处。**默认名一律忽略，绝不从 prompt 自造** | ❌ 现为 `CodexEventReducer.Title(项目名, prompt)`（`TaskCatalog.cs:1135`）——**用提示词前 24 字自造标题**，与 opencode/agy 两条明文规范直接冲突——G1 |
| `UserPrompt` | `UserPromptSubmit.prompt`，折叠空白后截 1000 字 | ✅ |
| `LatestStep` | 二态：**工具态**=统一 `工具名 内容` 粘滞；**回复态**=转录本正文预览（≤120 字单行）。回合**中**由心跳落（**工具在跑时整条忽略**，免得正文盖掉工具步）；回合**末**由 `Stop` 落终稿并保留（**严格口径**：本回合最新一行必须是正文才算；放宽会踩 Stop 的落盘竞态，见 notes G45——已试过并回退） | 两条路径都过同一道回显守卫（`ToolStepSanitizer.EchoesPrompt`）；`Summary()` 白名单已剔除 `prompt`。**注意**：模型"写完正文立刻调工具"时，两行在转录本里只差 1–3 毫秒，所以中途的正文预览**结构性看不到**，这是设计取舍不是缺陷 |
| `TokenUsage` | 转录本最新 assistant 行的 `message.usage`：`input_tokens`→Input、`cache_read_input_tokens`→CachedInput、`output_tokens`→Output、`output_tokens_details.thinking_tokens`→ReasoningOutput、求和→Total；每条完成消息**覆盖为最新值**（不取最大，压缩后如实回落） | ❌ 恒为空（Claude 路径从不写 `TokenUsage`）——G1' |
| `ModelName` | **只**取转录本 assistant 行的 `message.model`（payload 里的 `model`（`SessionStart.model`，形如 `claude-opus-5`）目前只被解析、没有读取点）（形如 `deepseek/deepseek-v4.1-flash`），按 opencode 规则取**最后一个 `/` 段**（`ModelNames.Label`）。2.1.236 **无** `PostModelSwitch` 事件，会话中途换模型只能靠转录本兜底 | ❌ 恒为空（payload 有 `Model` 字段但 reducer 从不使用）——G1'' |
| `Subagents` | `SubagentStart`/`SubagentStop`（`agent_id`/`agent_type`）；名字=`agent_type`；步骤=该 `agent_id` 名下事件；**子代理的子代理**（`agent-*.meta.json` 里的 `parentAgentId`/`spawnDepth` 实测存在嵌套）需按父 id 归位 | ⚠ 主干在，但步骤字段用了不存在的 `task_description`、且无终态守卫——G10/G11/G14 |
| `StartedAt` | 本轮制：本轮首个 prompt 到达时置 now，轮内插入不重置，终态冻结，下一轮重新起算 | ❌ 现取会话创建时间后**再不更新**（`UserPromptSubmit` 不重置）——G5 |
| `CompletedAt` | `Stop`/`StopFailure`/`SessionEnd`/拒绝 的接收时间 | ⚠ 主干在；`StopFailure`/`PermissionDenied` 语义错——G13/G15 |
| `IsUnread` | 成功 / 失败 / 拒绝 置 true | ✅ |
| `Capabilities` | 恒 `[Approve,Deny]` 仅在 `PermissionRequest` 期间；其余为空 | ✅（但 README 说"暂不支持"，见第六节） |
| `PromptQueued` | 排队提示词。`UserPromptSubmit` 是否"排队被接单"需从**提交时刻 vs 当前轮 `StartedAt`**判（Claude 不给 busy 标志）；旁证：转录本 `queue-operation`(enqueue/remove) 与 `sessions/<pid>.json` 的 `status:"busy"` | ❌ 未实现——G1''' |
| `updatedAt` | Bridge 接收时间 | ✅ |

---

## 六、手机控制

Claude 是四条来源里**唯一有双向能力**的一条，两个动作都是真的能改变 Claude 行为的：

- **权限批准/拒绝**：`PermissionRequest` 设 `Capabilities=[Approve,Deny]` → App 渲染允许/拒绝 → `Perform` 校验能力位后 `resolver.Resolve` 写回 hook stdout。回包形状与官方 zod 联合体逐字对齐。
- **多题在手机上一次一题（桥端逐题推进）**：一次带 N 题的提问，桥**只把当前这一题**放进待处理请求（`第 i/N 题： <题目>` + **该题自己的选项按钮**），App 照旧渲染单题那张卡 → **零协议改动、零 App 改动**；答完一题自动推进到下一题，第三行跟着当前题走，攒齐后一次性回填 `updatedInput.answers{q1:…, q2:…}`（`updatedInput.answers` 本来就是"问题全文 → 答案"的 map）。
  这样绕开了"协议里 `options` 是扁平列表、多题的选项无法归属"这个限制；**不要**改成"我分几轮提问"——那是纯对话行为，换个 session 就没人记得。
- **选项按钮的文本是 `label（注解）`**：注解写在选项框里（用户的明确要求；塞进卡片正文被判定为错排法并回退过）。App 零改动——它只是把收到的字符串画成按钮、原样回传；回传后由 `NormalizeOptionLabel` 还原成纯 label（先原样对表，不中再剥尾部 `（…）`）。
- **作答**：提问工具的 `PermissionRequest` 设 `Capabilities=[Answer]` → App 出现**选项按钮（青色）**与**输入框 + 黄色「发送回答」**（后者是项目早期就画好的 UI，此前从未被点亮）→ `ControlAction.Answer` → 按值分流：
  - 值**命中某个选项 label** → 回 `updatedInput{questions…, answers{"<问题全文>": "<label>"}}`（结构化选择）
  - 否则 → 回 `updatedInput{questions…, response:"<文本>"}`（用户手打的自由文本）
  机制与证据见 notes §八。**一条待处理请求只承载一题的选项**——多题由桥逐题推进（见上），协议与 App 都不需要知道"这是多题"。
  **前提**：必须是我们**持有 hook 连接**的那条路（`PermissionRequest`）。只走 `PreToolUse`（权限没触发询问）时不发 `Answer`，否则 App 会画出一个送不出去的输入框。
  **竞态**：用户随时可能在终端抢先作答——那之后 hook 的答案就作废（正常现象）。
- **问答的通知渠道**（`elicitation_*` 类 MCP 输入）维持只读提醒：置顶 + 提示音 + QUESTION 卡片，作答留在终端。
- `Capabilities` 是 App 唯一的分层依据；其余三条来源恒空。

---

## 七、已知限制

1. **版本敏感**：本文事实锚定 2.1.236。官方文档已覆盖 2.1.257+ 的 `PreModelSwitch`/`PostModelSwitch`/`scratchpad_dir`——升级后应重跑第一节的构造点提取，事件表会变。
2. **强杀无 `SessionEnd`**：任务管理器杀进程时无事件，任务停在 `running`；`sessions/<pid>.json` 消失可作离线判据（**兜底已实现**：注册表"见过、后又消失"即判离线，从没见过则退回"非终态且静默 ≥10 分钟且无工具活动"——两段都在 `CheckClaudeRuntime` 里）。
3. **正文预览非必要走 hook**：`MessageDisplay` 每个 delta 批次都会拉起一次 hook 子进程（`forceSyncExecution: true`，超时上限 10 s），对终端渲染有侵入风险；从转录本尾部异步摘取正文是更省的一条路（与 agy 同构）。二选一或双通道，`待实测` 开销后再定。
4. **每事件冷启 exe**：现状每次 hook 都冷启约 220 MB 的自包含 .NET 进程（与 Codex 同一份 exe），agy 已用 Node 胶水脚本规避（判例 #1 实测 300~800 ms/次），Claude 的 hook 频率更高（每次工具调用 2 次）——通道层问题，见 notes 文 G22。
5. **`stop_hook_active`** 仅表示"Stop hook 因上一次阻止而重入"，与状态映射无关，忽略。

---

## 八、验证清单（用户实测用）

1. 启动 Bridge（新构建），面板「Claude Code 生命周期 Hook」一行确认 **「已写入 v2」**（紫）；
   若显示「待更新 vX → vN」（琥珀）说明脚本落后于内置版本，点一次「安装 Claude Code Hook」即可。
   核对两处落地物：`~/.claude/settings.json` 出现 14 组托管项（`statusMessage` 标记 + `command":"node"` + `args`），`~/.claude/agentpager-claude.js` 存在且首行是 `// agentpager-claude v2`。
2. 启动 `claude` → 手机出现「项目名 · 空闲」（**不是**「正在运行」）。
3. 发一条提示词 → `正在运行`，本轮耗时从 0 起算；**第三行为空**（不得显示提示词回显）。
4. 生成标题后（数秒）→ 标题变为「项目名 · <AI 标题>」，与终端 `/resume` 列表里看到的一致。
5. 工具阶段：`Read x.ts` → 横向扫描线（reading）；`Glob **/*.md` → 跑马灯（searching）；`Bash npm test` → 斜向扫线（testing）；`Write .gitignore` → 书写波（editing）；`TodoWrite` → 四角轮流（**thinking，不是 editing**）。
6. 模型作答 → 第三行滚动显示正文预览（单行截断）；回合结束 → 爆闪+打勾，预览保留，耗时冻结；点开任务行看 `IN/CACHE/OUT/TOTAL`（与终端 `/cost` 量级吻合）。
7. `Esc` 中断 → `已中断`（不得停成"任务完成"）。
8. 触发权限对话框 → 手机置顶「等待批准」+ 提示音，**终端对话框同时照常可见可作答**；在手机上点允许 → 终端对话框消失、工具继续；在手机上点拒绝 → 工具不执行。
9. 派生子代理（`Agent`）→ 父任务「正在协作」+ 子代理面板 `CLAUDE (1)`，名字=agent_type；**子代理里的工具调用不得覆盖父任务第三行**；子代理结束约 4 秒后淡出。
10. 触发 `AskUserQuestion` → 手机出现 QUESTION 卡片：**带题号的问题全文 + 选项**，**并且有输入框和「发送回答」按钮**（能力位 `answer` 生效）；在手机上打字发送 → 终端**不弹对话框**、模型直接收到答案；若改在终端作答，卡片同样消失、回到运行。
11. **回合进行中**：模型在工具之间写正文时，第三行滚动显示正文预览；**工具一开跑就换成工具步**（正文不得盖工具步）；回合结束由 `Stop` 落终稿并保留。
12. 关闭 `claude` → `离线`（子代理行级联为已中断；挂起中的卡片同时撤掉，不留死卡）。

---

## 九、三层映射总表（像素图 ↔ TaskSnapshot ↔ Claude Code）

> 性质同上两篇：**代码现状快照**，非设计稿。像素动画名是对 `PixelMotionEngine`（`android/app/src/main/java/com/agentgrid/mobile/render/PixelMotionEngine.kt`）分支的直观描述，如有出入以源码为准。

### 链路

```
Claude Code hook ──stdin/stdout + exe --hook──► TCP 49361 ──► TaskSnapshot ──wire──► app 任务行 ──► 像素图(3×3 九格)
```

### 主表

| Claude 实际事件 | Lifecycle | Activity | 第三行 LastStep | 像素图动画 |
|---|---|---|---|---|
| `SessionStart` | idle | — | — | 中心慢呼吸（十字微光） |
| `UserPromptSubmit` | running | thinking | 新一轮清空 | 四角轮流 + 中心脉动 |
| `PreToolUse`（普通） | running | 工具映射（第四节） | 「工具名 内容」粘滞 | 对应工具动画 |
| `PreToolUse`（`agent_id` 非空） | 不变 | 父 delegating | **不改父第三行** | 分支脉冲 |
| `PostToolUse` / `PostToolUseFailure` | running | thinking | 粘滞 / `✗ 错误首行` | 四角轮流 |
| 中间正文（**不注册 `MessageDisplay`**，靠转录本 + 心跳） | 不变 | 不变 | 正文预览 ≤120 字（工具在跑时不写） | 保持当前 |
| `PermissionRequest` | waitingApproval | executing | 「工具名 内容」 | 双拍心跳 + 中心冲击（强辉光） |
| `PermissionDenied` | running（保持） | thinking | `✗ 已拒绝` | 四角轮流 |
| `AskUserQuestion` / 等待类 `Notification` | waitingAnswer | thinking | 问题文本 | 中行三点省略号（强辉光） |
| `SubagentStart` | 不变 | delegating | — | 中心脉冲 + 外圈分支扩散 |
| `SubagentStop` | 不变（终态不回退） | 未终态则 delegating，否则 thinking | — | 回落四角轮流 |
| `Stop` | succeeded（interrupted 保持） | 清空 | 正文预览 / 保持最终工具步 | 爆闪 + 打勾（1.2s 后定格） |
| `StopFailure` | interrupted | 清空 | `✗ <error>` | 闪断 → 沉降为断口（定格） |
| `SessionEnd` | offline（终态保持） | 清空 | — | 暗十字间歇双闪 |

### 三个易误认为「设计」的代码事实

1. **第三行的回复态有两条来路**：回合**中**由心跳落（每 3 秒、转录本 mtime 门控，见 §五 与 notes G31）——
   但**只在没有工具在跑时**才落，工具一开跑立刻换成工具步（工具步粘滞至下一段正文/下一次工具）；
   回合**末**由 `Stop` 落终稿并保留。与 opencode（事件流逐段推）的差别只在**粒度**（这里是"每条 assistant
   消息"而非逐字）与**时延**（≤ 心跳间隔），换来的是不注册 `MessageDisplay`（那要给每个 delta 批次拉一个 hook 进程）。
2. **等待态的锁只管"前置事件"**：`WaitingApproval` / `WaitingAnswer` 期间 `PreToolUse` 与通知被忽略（防手机卡片被压掉），
   但 `PostToolUse` / `PostToolUseFailure` 必须放行——它们正是"这一等结束了"的信号（手机放行、本机作答、工具被拒后照常返回）。
   一刀切锁死会让任务卡在等待态（本轮实现时先写成了一刀切，被测试当场抓住）。
3. 同一 lifecycle+activity 的连续事件不重播动画（真实任务 revision 恒 0），重播只发生在状态真实迁移时。
