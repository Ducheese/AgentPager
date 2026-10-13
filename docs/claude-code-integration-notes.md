# Claude Code ↔ AgentPager 集成协作备忘

> 本文记录 Claude Code 与 AgentPager 对接的**现状盘点（已有实现的不足与未统一项）**、目标形态、协作节奏与判例清单。
> 用途：后续会话 / 新 agent 按同样方式工作，避免重复踩坑与重复研究。
> 定稿日期：2026-10-08（对应 `527956e`，Claude Code 2.1.236）
> 配套规范：`docs/claude-code-state-mapping.md`（映射表；差距编号 G1…G24 与本文一一对应）

---

## 一、协作节奏（核心，务必保持）

用户提要求 → agent 改 → 构建 → **明确告知哪些要重启** → 用户验证 → 反馈 → 下一轮。
小步推进，一次只解决一件被点名的事。

**Agent 侧固定动作：**

1. **先读现状再开口**：任何关于代码的断言前，先 Read 相关文件（跨会话 / 用户可能手动改过，旧快照不可信）；涉及行为先 `git status` + `git diff` 体检。
2. **范围最小**：只动用户点到的事。用户只是"描述现象 / 提问"时**不要动代码**；没被要求的"顺手改"一律不做，最多列出来问。
3. **每改必验**：先跑 `windows/tests` 隔离验证，全绿再构建；构建失败如实报，不静默放过。
4. **构建后给"重启清单"**：明确说清这次要重启什么、不用重启什么（见第二节矩阵）。
5. **产物路径写到底**：bridge exe 全路径、APK 同步路径、落盘位置。
6. **诊断拿证据**：二进制提取 / 转录本查询 / WS 观察，不靠猜。临时物**用完即还原**（本次研究的 `F:\Git\scratch\claude-hook-recon\` 属临时目录，结论入档后删除）。
7. **诚实汇报**：没验证的说没验证；推得的标明"推得"。本文所有 `待实测` 标记即此类。

**用户沟通风格（适应）**：极简、命令式、一次一个点；"别改"即停；验证后给出精确现象描述（最有价值的输入，直接当测试用例）。

---

## 二、重启矩阵（改哪里 → 要做什么）

| 改动类型 | 需要做的动作 |
|---|---|
| bridge（C#） | 退出旧 bridge → 启动 `dist\opencode-main-baseline-v<N>\AgentPagerBridge.exe` |
| **Claude Hook 重装** | 只在两种情形需要：① **本轮通道改造这一次**（hook 命令从 `"<exe>" --hook` 换成 `node <脚本>`，老配置不改就等于什么都没装）；② 面板显示「待更新 vX → vN」（脚本内容落后于内置版本）。**换 bridge 的 vN 目录不再需要重装**——hook 指向 `~/.claude/agentpager-claude.js` 这个固定路径，与 bridge 装在哪无关（**Codex 仍是 exe 形态，那条换目录必须重装**）。重装 = 部署脚本 + 重写 14 组托管项，用户自己的 hook 原样保留 |
| hook 配置（`~/.claude/settings.json`） | 装完即改写；Claude Code 有配置变更侦测，但 hook 是否对**当前正在跑的会话**即时生效**未实测**——保守做法：重开一个 `claude` 会话验证 |
| Android App | 重装 APK（同步到 `…\AgentPager-debug.apk` 并核 SHA-256） |
| 仅 bridge 内部逻辑（数据格式不变） | 只换 bridge（若换目录，仍要点一次重装 hook） |
| 仅文档 / 测试 | 无需重启 |

---

## 三、现状盘点：已有实现的不足与未统一项

Claude 的实现（`0cf9f6c` 起）早于 opencode/agy，因此它是唯一"先有代码、后有规范"的一条来源——**下面几乎每一条都是"规范已定、Claude 没跟上"**。

> **落地状态（2026-10-08 本轮）**：G1–G24 全部处理完毕，`windows/tests` 221 条断言全绿。下表的"状态"是权威结论，其后的逐条说明写于**改动之前**，用作问题描述与判据出处，不要当成现状读。

| 编号 | 主题 | 状态 |
|---|---|---|
| G22 | 每事件冷启 220 MB 原生 exe | ✅ 已换 Node 转发脚本 `agentpager-claude.js`（exec 形式） |
| G3 | 非权限事件回写裸 `\n` 污染上下文 | ✅ 脚本对非权限事件 stdout 恒空 |
| G23 | `sessions/<pid>.json` 注册表未用 | ✅ 接入离线正判（见过→消失即离线）+ 10 分钟静默兜底 |
| G2 | 事件集 14/31 | ⚠️ 维持 14 个（正文/标题/Token 全走转录本，多注册只增加进程数）；`PostCompact` 等仍不注册 |
| G4 | `SessionStart` 落 `Starting` | ✅ 改 `idle` |
| G11 | 子代理事件未按 `agent_id` 分流 | ✅ 路由进父任务 Subagents 行；子代理权限询问挂父任务 |
| G17 | 四道竞态守卫缺失 | ✅ 已加（等待态锁 + 终态不可逆 + 工具期不让正文盖工具步 + 回显守卫） |
| G14 | `SubagentStop` 复活父任务 | ✅ 子代理路径不再触碰父任务 Lifecycle |
| G16 | `PreCompact` 复活终态 | ✅ 改为不迁移生命周期 |
| G13 | `PermissionDenied` 当回合中断 | ✅ 保持 running + `✗ 已拒绝 …` |
| G15 | `StopFailure` 方向反了 | ✅ → interrupted + `✗ <error>` |
| G7 | 提问态靠通知类型猜 | ✅ 改认 `AskUserQuestion`（卡片带问题全文与选项）；通知类型补 `elicitation_url_dialog` |
| G1 | 标题从 prompt 自造 | ✅ 只取转录本 `ai-title`/`custom-title` 与 `session_title` |
| G1' | Token 恒空 | ✅ 转录本 `message.usage` → IN/CACHE/OUT/REASONING/TOTAL |
| G1'' | 模型标签恒空 | ✅ 转录本 `message.model` 取最后 `/` 段 |
| G1''' | `PromptQueued` 未实现 | ⚠️ 已实现（转录本 `queue-operation`，过滤 `<task-notification>`），**用户手动排队的样本尚未采到，待实测** |
| G5 | 计时不是本轮制 | ✅ 改本轮制 |
| G6 | 新一轮不清第三行 / 不认 `session_title` | ✅ 两者都改 |
| G10 | 子代理步骤用不存在的 `task_description` | ✅ 改由该 `agent_id` 名下的工具事件驱动 |
| G12 | `PostToolUseFailure` 载荷未建模 | ✅ 补 `error`/`is_interrupt`；`is_interrupt` → interrupted（Esc 唯一及时信号） |
| G9 | 缺"回复态"第三行 | ✅ 走转录本（Stop 时落）；`last_assistant_message` 作兜底 |
| G18 | 无回显守卫 | ✅ `ToolStepSanitizer.EchoesPrompt` 收成唯一一份，三家共用 |
| G8 | `Bash npm test` 落 executing | ✅ 判据改为工具名 + **命令文本** |
| G19 | `TodoWrite` 误命中 `write` | ✅ 显式工具表，先判 todo/task |
| G20 | `Label` 缺 Claude 专有名 | ✅ 补 PowerShell/NotebookEdit/AskUserQuestion/Task 家族/Monitor/Workflow |
| G24 | 零测试覆盖 | ✅ 新增 60+ 条 Claude 断言 |
| G21 | README 与代码矛盾 | ✅ README 中英两表均改为"支持批准或拒绝权限请求" |
| G25 | 安装状态只有 bool，无三态 | ✅ 已补 `ClaudeHookStatus` + 面板「已写入 vN / 待更新 vX → vN / 未安装」（用户提出；opencode/agy 早有，Claude/Codex 一直是 bool） |
| G26 | 斜杠命令被当成排队提示词 | ✅ 实测踩到（`/context`）；已排除斜杠与系统注入项，并让排队排空后**还原** UserPrompt |
| G27 | 幽灵子代理行（孤立 SubagentStop） | ✅ 实测踩到；已改为只有 `SubagentStart` 能新建行 |
| G28 | `AskUserQuestion` 的权限询问被当成普通审批 | ✅ 实测踩到；已按"提问"处理（QUESTION 卡片、无允许/拒绝、保住问题文本） |
| G29 | 项目名跟着 Bash 的 `cd` 漂 | ⛔ **已撤销（用户裁定）**——"跟随 cd"是期望行为：这一行反映"会话此刻在哪个目录干活"。曾按"另两家都用稳定目录"的理由冻结成首值，被用户指出后回退；已加断言防止再冻回去 |
| G30 | 多题提问只显示第一题 | ✅ 用户实测发现；当轮先把各题标号拼进卡片正文（≤300 字）+ 第三行补"（共 N 题）"。**后续被 G44 取代**：多题现在由桥逐题推进，卡片一次只显示当前这一题（含它自己的选项按钮） |
| G31 | 回合中间的正文不上第三行 | ✅ 已实现（用户选 B）：心跳路径把**回合中**的正文写进第三行，**工具在跑时整条忽略**（与 opencode 的 reply 守卫同义）；回合终稿仍由 `Stop` 落。粒度=每条 assistant 消息，延迟 ≤ 心跳 3 秒 |
| G32 | 手机作答（App 有 UI、桥端从未接） | ✅ 已实现：`ControlAction.Answer` 接进 `Perform`，回包 = `PermissionRequest` 的 `updatedInput{questions…, response}`；`Capabilities` 只在**持有 hook 连接**（即 PermissionRequest 那条路）时给 `Answer`。/ App 端零改动 |
| G33 | **控制消息签名的转义两侧不一致 → 作答被静默拒绝** | ✅ 实测踩到（"手机上点了没反应"）；桥端默认 STJ 把非 ASCII 转成 `\uXXXX`，App 端 kotlinx 原样输出 → HMAC 对不上。已让桥端改用 `UnsafeRelaxedJsonEscaping`（App 零改动）+ 逐字节断言锁住；**并给静默拒绝补了 WARN 日志**（此前这类失败在日志里一点痕迹都没有） |
| G34 | 等待在别处结束后 hook 进程无人回收 | ✅ 实测踩到（终端作答后 hook 挂到 24 小时超时）；已加 `Cancel`：等待以别的方式结束时释放连接 |
| G35 | 第三行"经常残留上一个 reply" | ✅ 用户实测报出。**结构性原因**：`UserPromptSubmit` 按设计跑在提示词进入对话**之前**，那一刻转录本里还没有新一轮的 user 行，倒序扫到的"本回合最后一条正文"其实是**上一回合**的——心跳 3 秒一次正好落在这个窗口里。已按 assistant 行的 `timestamp` 加闸（早于本轮起点的一律丢弃）；无时间戳时放行（宁可显示，也不要因格式变化整条失效） |
| G36 | 手机作答只有文本框、选项从不渲染 | ✅ 用户指出。选项一路被送到 App 模型（`PendingRequest.options`）但**全项目 0 处渲染**。现：单题下发选项 → App 画成可点按钮 → 点一下把 label 原样发回 → 桥端对表命中即回结构化 `answers{问题全文: label}`；多题见 G44（桥逐题推进，一次一题）。最初多题**不下发选项**（扁平列表无法按题归属，画出来点了也不知道答哪题） |
| G37 | 心跳把工具步盖回上一段正文 | ✅ 用户实测报出："laststep 工具调用内容无法覆盖上一个 reply"。**同一回合内**倒序扫到的"最新正文"可能早于当前的工具步 → 心跳（3 秒一次）把它盖回去。修法与 agy 的明文规则一致：**本回合倒序第一条主会话 assistant 行若是工具调用（无 text），正文候选保持 null，不许再往回找** |
| G38 | 子代理权限卡不撤 → 父任务假等待 | ✅ 外部复审发现（逐条核对属实）。子代理事件全走 `AcceptClaudeSubagent`，而撤卡清单只在父级 switch 里 → 你在终端放行后父任务一直停在等待态、手机留一张点不动的卡、挂住的 hook 也要等父级下一次事件才回收。已按**来源识别**修复（记 agent_id，只认挂卡的那个子代理）；顺带补上同源缺口：**子代理问问题时卡片类型也错**（按审批处理会给用不了的允许/拒绝按钮） |
| G39 | 撤卡清单漏 `StopFailure`；`PermissionDenied` 不清工具活动 | ✅ 复审发现，已修 |
| G40 | 离线/容量裁剪后留幽灵卡；Claude 五张会话表无清理 | ✅ 复审发现，已修。**并修正了一个执行顺序**：容量裁剪原本跑在清理之后，被裁任务的状态仍会漏 |
| G41 | `Stop` 终稿两路同时失守时无补读 | ✅ 复审发现，已照搬 agy 的限期重读（1 分钟窗口）。注意"工具步保留"是设计（不登记），只有第三行**整个是空**才登记 |
| G42 | 文档旧稿残留 4 处 + G23 措辞与实现不符 | ✅ 复审发现，已修。最危险的是判例 9 里那句"项目名只在任务建立与 SessionStart 时取"——与用户裁定正面冲突，会把下一个 agent 诱导回冻结 |

| G43 | 选项的注解（`description`）在手机上丢失 | ✅ 已修（按用户给的排法）：**注解写进选项框里** —— 按钮文本 = `label（注解）`，**App 零改动**（协议里 `options` 本来就是字符串列表）。回传的是这整串，由 `ClaudeHookOutput.NormalizeOptionLabel` 还原成**纯 label**（**先原样对表**、不中再剥尾部 `（…）`——label 自己以括号结尾也不会被误剥）；单题与多题两条路都过这一层。~~第一版把注解塞进卡片正文~~已被用户回退——**注解跟着按钮走，别塞正文** |
| G44 | 多题既不能点选项、也不显示选项 | ✅ 已实现**桥端逐题推进**并**真机实测通过**（v46）：卡片 `第 i/N 题： <题目>` + **该题自己的选项按钮**，App 照旧渲染单题那张卡（**零协议、零 App 改动**）；答完一题自动推进，第三行跟着当前题走，攒齐后一次性回填 `updatedInput.answers{q1:…, q2:…}`。单题不受影响（点选项→`answers` / 打字→`response`）。<br>**实测证据**：日志只有一条 `多题作答已回填（3 题）` 且出现在最后一题之后（前两题**零写回**——逐题写回的话 hook 第一次就关了）；转录本 `answers` 三条、key 为三个问题全文、`response` 为空 |
| G45 | 中途正文在手机上**结构性看不见** | ⛔ **试过 C 方案并已回退**。**取证**：我写的正文与紧随的工具调用在转录本里是两行、**只差 1–3 毫秒**（同一个 API 响应里的 text 块 + tool_use 块）→ "最新一行是纯正文"的窗口只有几毫秒，心跳 3 秒一次**永远采不到**；而 G37 那条规则又必然命中 → 正文候选恒为 null，中途正文**结构性看不见**。<br>**C 的错法（已回退）**：给 `Stop` 终稿加"宽松口径"（本回合最近的一段正文，后面跟没跟工具都算）。坏在 **`Stop` 有"终稿还没落盘"的竞态**：宽松口径取不到最新那段时不返回 null，而是**一路往回捞到更早的一段**并钉在第三行；又因为第三行非空，G41 的补读也不会触发 → 手机上停在**旧正文**上（用户实测报出）。严格口径在这种竞态下返回 null → 走兜底/补读，反而更稳。<br>**教训**：`LatestReply` 的"本回合最新一行必须是正文"这条限制**不是可选的严格性**，它是 Stop 竞态下的**安全阀**——放宽它等于去掉这个阀门。代码里已加注释与断言钉住。 |
| G46 | 每次启动 Claude Code **100% 冒出一个"没有 session title 的幽灵任务"** | ✅ 用户报出，已修（v58）。**根因**：`SessionStart` 由 Claude Code **每次进程启动无条件**发出，而它到达时**转录本文件尚未创建**（探针实测 `transcript_path exists=false`）且载荷里**连 `session_title` 字段都没有** → 标题三条来源（转录本 `ai-title`/`custom-title`、载荷 `session_title`、注册表 `name`）一条都拿不到，新行只能顶着 `cwd` 目录名出生（`ClaudeTitle` 在无标题时返回项目名）。**实测证据**：探针载荷 `{session_id, transcript_path, cwd, hook_event_name, source}`；把会话起在 `%TEMP%\opencode` 下，幽灵行标题就叫 `opencode`——直接证明"标题=启动目录名"。**时间线**：`idle` 出生 →（进程当即退出时）147 ms 后 `offline` → 满 1 分钟被 `Normalize` 回收；**交互式会话则是"你打完字之前它一直挂着"**。**改法**：把 `SessionStart` 并进 `Accept` 的"不造行"清单（与 G27 治子代理幽灵同一招），行改由 `UserPromptSubmit`/`PreToolUse`/`PermissionRequest` 创建；**已存在**的行仍按 resume/clear 语义复位 `idle`。行为与 agy 对齐（它的事件表里根本没有"会话创建"事件，行随第一个回合诞生）<br>**同源观察项（未修，非本次范围）**：headless `claude -p` 会话**从不写 `ai-title`**，所以那几个真任务行的标题也一直是目录名（同一张快照里能同时看到 3 条）——真任务的"没有 session title"是"标题只能来自转录本"的另一面 |
| G47 | **Claude 掐掉子代理，app 永不同步**（行停在 running） | ✅ 已修（v59）。**取证**：`TaskStop` 掐掉一个 `sleep 240` 的子代理后，子上行 6 分钟仍 `running`；用探针 hook 在全新会话里复现——一次掐掉只看到 `SubagentStart` + 主会话 `Stop`，**`SubagentStop` 一次都没有**（故 Claude 侧这条钩子靠不住）。**修法**：父级 `PreToolUse` 的 `TaskStop` 入参带 `{"task_id":"a2bb7464c16c26320"}`，与 `SubagentStart` 的 `agent_id` **逐字相同**（探针实测）→ 桥据此把对应子代理行收敛成 `interrupted`（"✗ 已被停止"）。顺带把 `TaskStop` 这条调用从父任务第三行的更新里摘出去——它的入参取不到摘要，走通用分支会把第三行冲成光秃秃的 `Task` 并粘住 |
| G48 | **agy 主对话被当成子会话、顶层行被物理删除** | ✅ 已修（v59）。**实测事故（2026-10-11）**：一轮派发过子代理的 agy 回合结束后 3 秒，那条顶层行**凭空消失**（连 `tasks.json` 里也没了）。**根因**：`DetectParentFromTranscript` 的前提是"拿**子代理的**转录本探父"，却被告知用在**所有** agy 任务上；实现是"前 15 行里第一个 `sender=` 就当父"。主对话自己的转录本里，**子代理回报被转述进来**的那行带着 `sender=<子代理id>`（实测第 8 行）→ 父子方向整个颠倒 → 主对话被判成子会话 → `_tasks.RemoveAll`/`RemoveAt` 物理删除，而"父"（子代理 id）不是任务 → 搬无可搬 → 整行无损消失。**同一份 SQLite 里 `nesting_depth=0`、`parent_conversation_id=''` 明明写着它是根**——代码却信了启发式。**修法两条**：① 转录本启发式**只在 DB 没有这一行时**才允许开口（它存在的意义就是覆盖落盘时差）；② 兜底防线——认定下来的"父"**必须是已知任务**才允许搬移（宁可留一行可见的顶层行，也不要让它无声消失）。测试用真 SQLite 临时库 + 注入的 brain 根复刻了该场景 |
| G49 | opencode 子会话被删除 → 子代理行永远 running | ✅ 已修（插件 v9）。插件的 `session.deleted` 分支原本只删映射**不发终态**。修法：在清理块内、**断映射之前**补一条 `failed`（正常跑完的子会话早由 `session.idle` 发过 `done`，桥对已终态的行忽略后续相位，故不会把"完成"误报成"中断"）。**位置是关键**：插在清理块之后的 `isChild` 分支等于发不出去（那时 `childSessions`/`childParent` 已被删空）——这个坑由新加的插件断言当场抓出 |
| G50 | **三来源子代理状态的三处不统一**（用户问"opencode 的子代理状态逻辑和 agy/claudecode 一样吗"） | ✅ 已修其中两处（v61）。**先说结论**：App 的配色是**一个共享函数**（`AgentGridScreen.kt:1071 lifecycleColor`，任务行与子代理行同一条），**不看来源**——绿=完成、灰=中断/离线、紫=运行中（思考/委派）、橙=执行、蓝=测试、靛=编辑、青=读/空闲、黄=等待回答、琥珀=等待批准。所以差异不在颜色，在**桥给三家吐的 lifecycle/activity 是否同义**。查出的三处：<br>① **opencode 子代理行的"测试"判不出来** → 已修：子代理事件原来不带命令文本，桥只能 `ActivityFor(tool, null)`，同一个 `npm test` 父行显蓝、子行显橙。现插件 v10 在 `tool` 相位多带 `command` 字段，桥侧据此判测试。<br>② **"多久变灰"的路径与延迟不同** → 已修：静默兜底从 agy 专属推广为**三家通用**（`CheckSubagentSilence`，两级阈值 + "只收没在跑工具的子代理"的活动位守卫，见 agy notes 判例 9）。<br>③ **出生时的颜色**：agy 一出生就是 `Running+Thinking`（紫），Claude/opencode 出生是 `Idle`（青）——**未改**，这是 agy 的回合语义（`PreInvocation` 就意味着"正在跑"），不是缺陷 |
| G51 | **工具步里的路径写法三家各不相同**（用户问"路径省略是否统一"） | ✅ 已修（v62）。**旧状**：agy 有 `RelativizePath`（区内相对、**区外只留文件名**——实测 `C:/Windows/win.ini` 显示成 `win.ini`，目录信息整条丢）；Claude 的 `Summary()` 原样输出（全绝对路径）；opencode 直接用 opencode 自己的 `state.title`（实测同一调用里 `Read` / 全路径 / `Windows\win.ini` 三种形态乱跳）。**统一后**（新增共享 `PathDisplay`）：**工作区根下→工作区相对；用户主目录下→`~/…`；其它→全路径**，分隔符统一正斜杠；按**目录边界**命中（`F:/Git` 不吃 `F:/GitHub`）；路径正好等于根时退化成根目录名（否则步骤会变成光秃秃的 `Read`）。**只对路径型键生效**（`file_path`/`path`/`notebook_path`/`filePath`/`AbsolutePath`/`TargetFile`）——**命令行文本、URL、pattern 一个字都不动**（改写命令里的路径只会误导）。工作区根：Claude 用 payload 的 `cwd`、agy 用 `workspacePaths[0]`、opencode 用会话 `directory`（存在 `_openCodeDirectories` 里，因为 `chat.message` 不带目录）。<br>**遗留**：Claude 的 `cwd` 会随 Bash 的 `cd` 漂（判例 10 的期望行为），漂了就退化成写全路径（信息更多，不是错误）；若要更准需让转发脚本多带 `CLAUDE_PROJECT_DIR`（脚本升 v3）。opencode 的 `state.title` 仍是首选详情源，它自己的"半截相对路径"（`Windows\win.ini`）改不了——那是 opencode 自己生成的文本 |
| G52 | **Claude `SessionStart` 会把已完成的任务打回 `idle`** | ✅ 已修（v63）。用户实测报出："app 重连后，原本 `succeeded` 的任务变成 `idle`，优先级就变了"。`SessionStart`（resume/clear/compact/fork）原来**无条件写 `Idle`** —— 优先级 300+50（未读）→ 100，未读高亮也丢。**修法与 opencode 的 `session.created` 对齐**：终态行原样返回（会话重新打开 ≠ 上一轮没完成）；非终态行仍按 resume/clear 复位 `idle`。用户随后真发提示词时 `UserPrompt` 会自然把它推回 `running` |
| G53 | **opencode 有和 Claude 同款的启动幽灵** | ✅ 已修（v63）。实测 `opencode run` 启动即冒出一行 `title=项目目录名 / prompt 空 / idle`，468 ms 后才带上提示词。成因与 Claude 的 `SessionStart` 完全同源：`session.created`（TUI 一启动就发）无条件建行，而那一刻会话还没有标题。**修法**：`session.created` 只**登记不建行**（行改由 `chat.message` 建）。**关键顺序**：会话目录（`info.directory`）只在 `session.created/updated` 里出现，**必须在"不建行"早退之前登记**，否则行永远拿不到目录 → 项目名只能回退成字面的 `opencode`；建行时再回退用这张表（`PathDisplay.LeafOf`）|
| G54 | **opencode 没有任何离线兜底** | ✅ 已修（v63）。原来只有显式信号（`plugin.dispose` / `session.deleted`），**进程被强杀就永远转圈**——而容量裁剪只挑终态任务，这条非终态行连裁都裁不掉。现与 agy 合并成同一口径 `CheckSilentOffline`：**10 分钟无更新 + 活动位为 null/thinking → `offline`**（正在跑工具的长任务不算死），离线时顺手 `SubagentSweep` 扫子代理 |
| G55 | **排队被接单会重置计时** | ✅ 已修（v66）。用户定的规矩：**计时只在一轮对话完了之后又重新开始才重置** —— 中途插入的排队消息属于同一个 burst，不该归零（用户为此点名改了 agy，见 agy notes 判例 12 ③；opencode 本来就不重置 ✓）。Claude 是三条里唯一没对齐的：它的排队消息在**上一轮结束后**才投递（**实测时序**：转录本里 9 次真实 `dequeue`，每次都紧跟在上一条 assistant 消息之后 130~200ms、随后 10~30ms 就是新的用户消息），`UserPromptSubmit` 到达时状态已是 `succeeded` → 命中 `startsTurn` → `StartedAt = now` ✗。<br>**修法**：Accept 里把"我们曾显示为排队中"的那条文本记在 `_claudeQueuedText`，投递时（payload 的 prompt 与它逐字相同 → `NormalizePrompt` 归一化后对表）**摁回同一轮**（不重置 `StartedAt`、不清第三行），用完即清。为什么不用 `task.PromptQueued` 标记：投递时最后一条队列操作已变成 dequeue/remove，标记早被清空 ✗。<br>**一处容易踩空的实现细节**：`ReadClaudeTranscript` 只在 `SessionStart/UserPromptSubmit/Stop/StopFailure` 才读转录本（其余事件不读），所以"排队中"标记实际是靠**心跳**（Maintain 约 3 秒一拍、mtime 门控）采到的 —— 写测试夹具时必须走心跳，用 `PostToolUse` 带 `transcript_path` 是空转 ✗（我踩了）。 |
| G56 | **审批动作的跨来源标准（用户 2026-10-11 定）** | **允许 = allow once（一次性放行）**；**拒绝 = reject**。三家落地形态不同，语义必须一致：<br>· **Claude**：`decision: {behavior:"allow"}` ✅ 已符合 —— **不带 `updatedPermissions`** 就是一次性；带了才是"允许且不再问"（我们从不发它 ✓）。拒绝 = `{behavior:"deny"}`（不带 `message`/`interrupt` ✓）。<br>· **opencode**：`response: "once"` / `"reject"` ✅ 已实现（插件 v14 起，写入 `/session/{sessionID}/permissions/{permissionID}`）。注意它**还支持 `always`**（"允许且不再问" ✓）—— App 现在只有两个按钮，将来要加"始终允许"时三家都现成（Claude 的 `updatedPermissions`、agy 的 grant 资源串）。<br>· **agy**：门禁回 `{decision:"allow"}` / `{decision:"deny"}`（枚举里另有 `ask` / `force_ask` / `deny_unless_prior_grant` ✓）—— 待实现，落地时按同一标准（一次性放行，不建持久授权）。 |
| G57 | **拒绝一次调用 = 把整个任务判死** | ✅ 已修（v72）。用户实测报出："拒绝后 task 直接变离线状态了"。旧实现把 Deny 写成 `Interrupted`（**终态** ✗）—— 可三家被拒之后都会把"用户拒绝了"回报给模型、**回合继续**（实测：Claude 拒绝后模型还在回话 ✓，那行的第三行就是它被拒后的回复 "我没删，先停下…" ✓），而终态行会被守卫挡住 ✗ → 于是它**永远停在"已中断/灰掉"**，后续再多的活动都推不动它 ✗✓。<br>**修法**：拒绝也回到 `running` + `thinking`，第三行写一句 `✗ 已拒绝 <请求摘要>`（与 `PermissionDenied` 同一措辞，让拒绝这件事在手机上看得到 ✓），终态交给代理自己的结束信号（`Stop` / `session.idle` / `StopFailure`）✓。允许本来就是 `running` ✓ 不变。 |
| G58 | **挂起没有统一上限；两边都不答就永久卡住** | ✅ 已修（v73）。**标准（用户 2026-10-11 定）**：人类等待的上限**三家同值 10 分钟**，到点把这次询问**交回终端**（撤卡 + 释放挂住的连接 + 该行回 `running`/`thinking`），**不判死也不判离线** ✓。旧状：Claude 的 hook `timeout` 是 **24 小时**（桥侧还没有自己的计时器 ✗）、opencode 插件是 **5 分钟** ✗，两边不一致；而"两边都不答"时那行会**永远停在等待态** ✗（探针任务当场撞上过 ✗）。<br>**为什么不判离线**（用户当场纠正）：用户**可能在终端上作答** ✓，而 Claude **没有"权限已作答"这类 hook** ✗ —— 它只能靠后续事件间接同步（本机放行/拒绝 → 那次调用的 `PostToolUse` ✓；兜底 `Stop` ✓），中间有一段"卡片还旧着"的窗口 ✗。此时若把行判死，手机上会显示**与事实相反**的状态 ✗✗。改成交回 running 之后：代理其实活着 → 它自己的事件继续更新 ✓；真的死了 → **10 分钟的静默离线规则自然接管**（它认的正是 `running + thinking` ✓，两条规则串得上 ✓）。<br>**落地**：桥侧 `HoldLimit = 10min` ✓ + `TaskCatalog.ExpireStaleWaits`（审批与提问同一口径 ✓，离开等待态时时钟顺延补偿照旧生效 ✓）；Claude hook `timeout` 86400→**600** ✓；opencode 插件 `HOLD_TIMEOUT_MS` 300000→**600000**（插件 v15 ✓）。<br>**注**：Claude 的 hook 配置是**托管项**，改了 timeout 之后要让桥重写一次配置（点「安装或修复」）才生效 ✓ —— 三态判定只比对事件是否注册与脚本是否同版，**不比对 handler 的选项** ✗。 |
| — | 为什么不采用"拆轮提问" | 用户点名：**拆轮是纯对话行为，换个 session 就没人记得**。写进桥里才是代码级、稳定的——这也是本项目的一条通则：**规则要么写进代码、要么写进文档，别留在对话上下文里** |

### A. 通道层（4 条）

**G2｜事件集只注册了 14 / 31，漏掉的恰好是"状态流缺口"那几个**
本机 2.1.236 实测 31 个事件（每个事件在二进制里只有一处构造点，故清单是确定的）。`ClaudeHooks.cs:85-101` 注册 14 个。缺的 17 个里，配置/文件/工作树类（`InstructionsLoaded` `ConfigChange` `CwdChanged` `DirectoryAdded` `FileChanged` `WorktreeCreate/Remove` `Elicitation*` `Setup`）可以长期忽略，但下面五个直接对应状态流缺口：
`MessageDisplay`（正文预览唯一来源 → G9）、`PostCompact`（压缩收尾 → G16）、`TaskCreated`/`TaskCompleted`（任务清单，可选）、`TeammateIdle`（agent 团队，可选）、`UserPromptExpansion`（斜杠命令展开，可作 prompt 补充）。

**G22｜每事件冷启 220 MB 原生 exe（最高优先级）**
`ClaudeHooks.cs:123` 的 hook 命令是 `"<bridge exe>" --hook --source claude`，即**每次 hook 触发都冷启一个自包含 .NET 单文件进程**。
agy 的判例 #1 已用实测把这条路否掉（"绝对禁止直接通过钩子拉起笨重原生 exe"，单次 300~800 ms，一轮 5-10 次工具调用即 CPU/磁盘雪崩，终端强卡顿），agy 改用 Node 胶水脚本（冷启 ~25 ms / <20 MB）。
Claude 侧的触发频率**比 agy 更高**：`PreToolUse` + `PostToolUse` 各一次/工具调用，Claude Code 的工具调用本来就密。Codex 走的是同一条路（`CodexHooks.cs:118` 同样是 `"<exe>" --hook`），但 Claude 的事件密度更高、且多了 `MessageDisplay` 这条潜在的逐帧通道。→ **两条来源都该换轻量转发器，Claude 更急。**
统一方向：给 Claude 也上 `agentpager-claude.js`（与 agy 胶水脚本同构、同一 TCP 信封），settings.json 里指向 node 脚本；权限事件仍需 stdin/stdout 双向，胶水脚本承担首帧放行 + 异步转发 + 权限回包桥接。

**G25｜安装状态是 bool，表达不了"脚本待更新"（本轮改造之后才成为真问题）**
opencode 与 agy 都有三态（`NotInstalled / Installed / Outdated`）并在面板上显示版本（`待更新 vX → vN`），Claude 与 Codex 一直是 `bool IsInstalled()`。
改造前 Claude 其实无所谓——那时"装上"只等于 settings.json 里有那 14 组，没有独立的落地文件。**本轮起 Claude 有了版本化的部署脚本**（`~/.claude/agentpager-claude.js` + 版本头），于是出现了新的一态：**hooks 注册着、脚本却是旧版本**。bool 会把它报成"未安装"（`ScriptIsCurrent()` 为假），面板于是撒谎——用户看到"未安装"以为没装，其实 hook 正在工作。
已补 `ClaudeHookStatus`（脚本陈旧 → `Outdated`）+ 面板文案与配色（紫=已写入 / 琥珀=待更新 / 灰=未安装），并加了"脚本被改旧必须是待更新、且仍算已安装"的断言。**Codex 仍是 bool**（本轮未动，属另一条来源的同类缺口）。

**G3｜非权限事件回写裸 `\n` 到 stdout，可能污染上下文**
`HookTcpServer.cs:103` 对每个非权限事件回一个 `\n`，`Program.cs:81` 把它复制到 stdout。官方解析规则：exit 0 的非 JSON stdout 视为**纯文本**，而纯文本只在 `UserPromptSubmit` / `UserPromptExpansion` / `SessionStart` / `PostModelSwitch` 上**注入 Claude 上下文**——AgentPager 恰好注册了前两个。opencode/agy 完全带外（只写 TCP），无此副作用。
修法：无回包需求时**不回字节、也不写 stdout**。

**G23｜`sessions/<pid>.json` 这张现成的注册表没人用**
`~/.claude/sessions/<pid>.json` 实时给出 `sessionId / cwd / name / nameSource / status(busy) / startedAt / updatedAt / version`。本轮只接入了它的**离线正判**：Claude 正常退出时自己 unlink 这份文件（实测源码 `process.on("exit", …unlinkSync)` + 异步兜底），崩溃残留由它自己的 sweep 清理——所以"见过、后来又没了"就是离线；从没见过则退回"静默 10 分钟"兜底（注册并非所有模式都开）。
**实现口径（免得被误读成设计稿）**：`ClaudeSessionRegistry.Scan()` 每个心跳（3 秒）把目录里的 `*.json` 整份读一遍，**没有 mtime 门控**——目录里通常只有个位数个小文件，这点开销换的是"进程一退出就能立刻判离线"；`status` / `name` / `cwd` 等字段目前**只被解析、未被使用**（排队标记走转录本的 `queue-operation`，不走 `status:"busy"`）。

### B. 状态层（8 条；其中 G14/G16 会把已终态任务**复活**，G13/G15 把终态**判错**——四条都在终态语义上出错）

**G4｜`SessionStart` 落 `Starting`，与 opencode 的明文论证相反**
`TaskCatalog.cs:979-986`：`SessionStart → Starting`。opencode 的 `session.created` 刻意落 `idle`，理由写在条文里——"不置 Starting，避免空会话误报"（一个刚打开的会话在手机上显示成"正在启动"的动效，但什么也没干）。Claude 应同样：会话建立 = `idle`，提示词到达才 `running`。（agy 无此事件，不在对比范围。）

**G11｜子代理内部的工具事件没有按 `agent_id` 分流**
`agent_id` 在**任何**事件上只要非空就说明"这个事件发生在子代理内部"（实测公共入参构造式 `Ny()` 无条件带上 `agent_id/agent_type`）。opencode 把子会话的工具 part 全部路由进 `Subagents` 行、**明确不上报主任务**；agy 同构（子会话按父 id 归位）。Claude 侧 `Accept()` 只按 `SessionID` 找任务（`TaskCatalog.cs:83`），`agent_id` 从未被读——子代理里的每一步都会覆盖**父任务**的第三行，手机上看不到"父在等我、子代在干活"的分层。

**G17｜四道通用竞态守卫，Claude 一道都没有**
opencode 在代码里就有等待态锁 + 终态不可逆（`TaskCatalog.cs:1454-1460`）与正文覆盖守卫（`:124-125`），agy 把四道写进条文。Claude 侧没有任何守卫代码 → 直接导致 G14/G16 两条复活类缺陷。

**G14｜`SubagentStop` 无条件把父任务拉回 `Running`**
`TaskCatalog.cs:1117-1124`：`ApplySubagent` 无论子代理是起是停，都返回 `Lifecycle = Running`。子代理在父任务已 `Succeeded` 之后收尾（很常见）即**复活已完成任务**。

**G16｜`PreCompact` 把任务写成"运行中"**
`TaskCatalog.cs:1000-1007`：`PreCompact` 与 `PostToolUse` 同分支，落 `Running` + `Thinking`。一次自动压缩就能把 `Succeeded` 的任务翻回运行中。

**G13｜`PermissionDenied` 被当成回合中断**
`TaskCatalog.cs:1017-1025` 映射为 `Interrupted` + `CompletedAt`。但厂商语义是 **auto 模式分类器拒绝了这一次工具调用**（二进制内置文档原文："After auto mode classifier denies a tool call"，且退出码与 stderr 都被忽略、模型可据 `retry:true` 重试）——**回合并未结束**。正解：保持 `Running`，第三行 `✗ 已拒绝`。副作用：现在手机上会频繁看到"已中断"的假终态。

**G15｜`StopFailure` 的映射方向反了**
`TaskCatalog.cs:1047-1053` 写的是"从 `Succeeded` 退回 `Running`"。而 `StopFailure` 是**回合因 API 错误结束**（`error` 为 12 种枚举之一，如 `rate_limit` / `overloaded` / `max_output_tokens`）→ 应为 `Interrupted` + `CompletedAt` + `IsUnread`，第三行 `✗ <error>`。

**G7｜提问态靠"通知类型"猜，且漏一种**
`IsAnswerNotification`（`TaskCatalog.cs:1086`）只认 `idle_prompt` / `elicitation_dialog` / `agent_needs_input`，漏 `elicitation_url_dialog`（本机实测存在的第四种）。更根本的是：Claude 有**精确信号**没用——`PreToolUse` 的 `tool_name == "AskUserQuestion"` 就是"模型在等用户选"。应改用它进 `WaitingAnswer` 并挂 QUESTION 卡片（带选项），Notification 只作补充。

### C. 字段层（10 条，含 3 条带撇号的同族项）

**G1｜标题是从提示词自造的**
`TaskCatalog.cs:1135`：`Title = CodexEventReducer.Title(项目名, prompt)`——**用用户提示词前若干字当标题**。
opencode 明文"只取 session title 实时同步，创建时默认名忽略，**不用 prompt 自造标题**"；agy 同样"绝不从 prompt 自造"。Claude 的真实标题**现成可得**：转录本里的 `{"type":"ai-title","aiTitle":"Claude Code hooks 机制与适配"}`（实测，本会话就有）与 `{"type":"custom-title"}`，外加 `SessionStart`/`UserPromptSubmit` 每次都带的 `session_title`（自定义标题）。→ 走 agy 那条路（mtime 门控读转录本尾部 + 心跳同步），即可三家统一。

**G1'｜Token 恒空**
Claude 路径从不写 `TokenUsage`。数据现成：转录本 assistant 行的 `message.usage`（实测字段 `input_tokens` / `cache_read_input_tokens` / `cache_creation_input_tokens` / `output_tokens` / `output_tokens_details.thinking_tokens`），覆盖映射 `Input / CachedInput / Output / ReasoningOutput / Total`——与 opencode 的 `tokens` 口径一一对应。

**G1''｜模型灰标签恒空**
`ClaudeHookPayload.Model` 有字段，但 reducer 从不使用（`ModelName` 始终 null）→ 手机行内灰色模型标签对 Claude 永远回落成来源标签。来源：`SessionStart.model`（`claude-opus-5` 形态）；会话中途换模型在 2.1.236 无事件（`PostModelSwitch` 实测不存在），只能取转录本 assistant 行的 `message.model`（`deepseek/deepseek-v4.1-flash` 形态），按 opencode 规则取最后 `/` 段。

**G1'''｜`PromptQueued` 未实现**
Claude 无 busy 标志，但有两条旁证：转录本 `queue-operation`（`operation:"enqueue"` / `"remove"`，实测有）与 `sessions/<pid>.json` 的 `status:"busy"`。目标行为与 opencode 一致（第二行显示「你（排队中）：…」，被接单后清除）。

**G5｜计时不是本轮制**
`StartedAt` 取会话创建时间后不再更新；`UserPromptSubmit` 不重置。opencode/agy 都是**本轮制**（首个 prompt 起表、轮内插入不重置、终态冻结、下一轮重新起算）。Claude 现在会显示"会话总时长"，与另两家语义不同。

**G6｜新一轮不清第三行、也不认 `session_title`**
`UserPrompt()`（`TaskCatalog.cs:1127-1140`）只改 `Lifecycle/Activity/UserPrompt/CompletedAt/Capabilities`：既**不清空第三行**（opencode/agy 都规定"新一轮开聊时清空，轮内插入不清"），也不用每次 `UserPromptSubmit` 都随包携带的 `session_title`（实测构造式里有）去同步标题。副作用：上一轮的工具步会被带进新一轮，看起来像"这轮一开始就在跑那个命令"。

**G10｜子代理步骤字段用了 `SubagentStart` 根本不传的键**
`TaskCatalog.cs:1107` 用 `hook.TaskDescription` 填子代理步骤，但该字段实测只出现在 `TaskCreated` / `TaskCompleted` / `TeammateIdle` 的入参里，`SubagentStart` 只传 `agent_id` + `agent_type` → **恒空**。子代理步骤应改由"该 `agent_id` 名下的 PreToolUse/PostToolUse"驱动（与 opencode 的 `subagent` `tool` 相位同构）。

**G12｜`PostToolUseFailure` 的载荷没建模，丢掉唯一及时的中断信号**
`ClaudeHookPayload`（`ClaudeHooks.cs:11-31`）没有 `error` / `is_interrupt` / `duration_ms` / `tool_response` 字段，多余键被 `System.Text.Json` 静默丢弃。其中 `is_interrupt` 值 `true` 表示"这次工具失败是因为用户按了 Esc"——它是 Claude 侧**唯一能及时区分"用户中断"与"正常结束"**的信号（`Stop` 事件本身不区分）。缺了它，`Esc` 中断只能等 `Stop` 落成 `Succeeded`，手机上看不到"已中断"。

**G9｜缺"回复态"第三行**
第三行二态缺一半：只有工具步，没有正文预览。Claude 有两条可用通道：`MessageDisplay`（每批新完成行触发，带 `delta`/`final`；**注意 `forceSyncExecution:true` 且超时 10 s，逐帧拉 hook 有渲染侵入风险，`待实测`**）或读转录本尾部的 assistant 正文（agy 同构，无侵入但是块级刷新）。取舍：先走转录本（零侵入、与 agy 一套代码），`MessageDisplay` 留作可选。

**G18｜第三行没有"回显提示词"守卫**
`Summary()` 白名单里**含 `prompt` 键**（`TaskCatalog.cs:1077`），而 opencode 的同类白名单**刻意排除 prompt**（`TaskCatalog.cs:1175`），agy 也"严格排除 Prompt"。三家同一条规范，Claude 是唯一漏的——`Agent` 工具一类入参带 `prompt` 的调用会把提示词片段顶到第三行，与第二行重复。

### D. 工具映射层（3 条）

**G8｜测试判定失效（`Bash npm test` 落 `executing`）**
`ActivityFor`（`TaskCatalog.cs:927`）只看**工具名子串**，而 Claude 的命令文本在 `tool_input.command` 里。opencode/agy 共用的 `ShellTestRun`（`TaskCatalog.cs:842`，独立词 `\b(test|pytest|jest|vitest)\b`）在 Claude 路径上**从未被调用**。

**G19｜`TodoWrite` 误命中 `write` → 落 `editing`**
opencode 文档把这个坑写死过（"不得让 todowrite 误命中 write（先判 todo）"）；Claude 复用的是同一个 Codex 匹配器，**同一个坑原样复现**。同批还有 `TaskCreate` / `TaskList` / `AskUserQuestion` / `ExitPlanMode` 应归 `thinking`、`Glob` 应归 `searching`（现在落 executing）。

**G20｜`ToolStep.Label` 缺 Claude 专有工具名**
`TaskCatalog.cs:811-828` 的表里有 glob/grep/skill/todo/websearch/webfetch/read/edit/write/bash，但没有 `PowerShell`（Windows 下 Claude 的 shell 工具）、`AskUserQuestion`、`NotebookEdit`、`TaskCreate` 家族 → 第三行会原样打长名。

### E. 工程层（2 条）

**G24｜Claude 路径零测试覆盖**
`windows/tests/AgentPager.Core.Tests/Program.cs` 里 "Claude" 只出现 1 次，还是一句注释（共用格式化函数）；断言全部是 opencode / Codex / agy。opencode 有插件 TCP 单测、agy 有 reducer 与转录本解析断言——**Claude 是唯一没有测试资产的来源**，上面 B 段的三个终态复活缺陷正是测试缺失的直接后果。

**G21｜README 支持矩阵与代码矛盾**
`README.md:50` 写「Claude Code 手机端操作：暂不支持」，但代码里 `PermissionRequest` 会写 `Capabilities=[Approve,Deny]`（`TaskCatalog.cs:1015`）、`Perform` 校验能力位后调 `Resolve` 回写 hook stdout（`TaskCatalog.cs:558-586`）、Android 也按 `task.capabilities` 渲染按钮（`AgentGridScreen.kt:879`）——**功能在，文档说没有**。需要择一改掉（功能已通就改文档）。

### 附：不构成"不足"的差异（已核实是对的）

- `PermissionRequest` 用 24 h 超时并挂住连接：**不是缺陷**。二进制内置文档原文 summary 是 "When a permission dialog is displayed"——对话框照常弹出，hook 只是并行的第二个答案入口；本机用户不会被阻塞。
- `SessionEnd → Offline`、`Stop → Succeeded`（interrupted 保持）、`Capabilities` 恒空（除权限）：与另两家一致。
- 事件名清单里 14 个全部是本机 2.1.236 真实存在的事件（实测唯一构造点），**没有编造的钩子名**。

---

## 四、目标形态（设计建议，不含实现）

按"先统一、后增强"排：

1. **通道**：Claude 换轻量 Node 转发器（G22+G3 一并解决），保留权限事件的双向桥接；settings.json 指向脚本路径（顺带解决"换 vN 目录即失效"，G23 的注册表读取放 Bridge 侧）。
2. **守卫优先**：先加四道通用守卫（G17/G14/G16/G13/G15），这类改动**纯 reducer、风险最低、收益最直接**（消除假终态），且能立刻被测试固化。
3. **字段补齐**：标题（G1）、Token（G1'）、模型标签（G1''）三件都靠"读转录本尾部"一条通道，与 agy 的 `AgyTranscriptReader` 同构，可复用其 mtime 门控与"终稿落盘竞速补救"（`_agyPreviewRecheck`，`TaskCatalog.cs:202-207`）两个成熟机制。
4. **第三行回复态**（G9）：先走转录本；`MessageDisplay` 视实测开销再定。
5. **工具映射统一**（G8/G19/G20）：把 opencode 的 `ActivityFor(tool, state)` 与 `ShellTestRun` 直接复用到 Claude（判据 = 工具名 + `tool_input` 文本），一张表三家共用。
6. **提问态**（G7）：改认 `AskUserQuestion` 工具名。
7. **测试**（G24）：在 `windows/tests` 补 Claude reducer 断言（至少覆盖 B 段五条 + 工具映射表），风格照抄现有 opencode 段落。

---

## 五、证物与工具（复用处）

| 工具 / 路径 | 位置 | 用途 |
|---|---|---|
| **Claude Code 二进制** | `C:\Users\alord\AppData\Roaming\npm\node_modules\@anthropic-ai\claude-code\bin\claude.exe` | 唯一一手源码（bun 打包，内嵌 JS + 厂商 hook 参考文本）；`tr -c '[:print:]' '\n'` 后 `grep -o` 手术式提取 |
| 官方 hooks 文档 | `https://code.claude.com/docs/en/hooks` | 语义与退出码解释；**版本可能新于本机，冲突以二进制为准** |
| 转录本 | `~/.claude/projects/<slug>/<session_id>.jsonl` | 标题（`ai-title`/`custom-title`）、Token（`message.usage`）、正文、排队（`queue-operation`）、`system/turn_duration` |
| 会话注册表 | `~/.claude/sessions/<pid>.json` | `sessionId/name/nameSource/status(busy)/cwd/startedAt` —— 离线与排队的旁证 |
| 子代理元数据 | `…/<session_id>/subagents/agent-*.meta.json` | `agentType / description / toolUseId / parentAgentId / spawnDepth`（嵌套证据） |
| hook 配置 | `~/.claude/settings.json` | 托管项识别靠 `statusMessage: "Managed by AgentPager (Claude Code)"` 或命令含 `AgentPagerBridge` |
| reducer 单测 | `windows/tests/AgentPager.Core.Tests`（`dotnet run`） | 改 Claude reducer **前必须先补这里的断言**（G24） |
| 手机同款快照 | `%TEMP%\opencode\ap-verify-plugin\snapshot-now.mjs` | WS 连 bridge，读手机收到的 `state.snapshot` |
| 诊断插件/hook 探针 | 临时 `settings.json` 追加 `zz-*.js` hook | 抓真实 payload 字段（用完删除并还原 settings.json） |

---

## 六、判例（坑已写死，别再踩）

1. **2026-10-08 二进制是唯一真相**：官方文档已覆盖到比本机更新的版本——文档里的 `PreModelSwitch` / `PostModelSwitch` / `scratchpad_dir` 在本机 2.1.236 中 `grep -c` **为 0**。任何"文档说有"的功能，先在二进制里确认构造点存在，再写入映射表。反之，`agent_id`/`last_assistant_message`/`session_title`/`MessageDisplay`/`token` 相关的字段与事件**都在二进制里实测存在**，可以放心依赖。
2. **`task_description` 不属于 `SubagentStart`**：它只出现在 `TaskCreated` / `TaskCompleted` / `TeammateIdle` 的入参里。现有代码用它填子代理步骤（`TaskCatalog.cs:1107`）在真机上恒空——**类型定义里有这个字段 ≠ 该事件会传**。凡"字段是否真会来"必须回二进制看该事件的构造式。
3. **权限对话框不是"被 hook 拦住"的**：内置参考文本 summary 原文 "When a permission dialog is displayed"，终端对话框照常显示。不要为了"避免卡住终端"去削 24 h 超时或改成同步短超时——那只会让手机批准在慢网络下失败。
4. **换 bridge 目录 = Codex hook 失效**（本轮起 Claude 不再受影响）：Codex 的命令里存的是 exe 绝对路径，构建到新 `vN` 目录后必须重装一次。Claude 的 hook 指向 `~/.claude/agentpager-claude.js` 固定路径，与 bridge 目录无关——**这是本轮通道改造的附带收益**，也让"待更新"三态成为必要：脚本内容会落后于内置版本，而 bool 表达不了这一态。
5. **`TodoWrite` / prompt 白名单两个坑，另两家都写过**：一家踩过并把结论写进文档，不代表复用它代码的第三家就自动免疫——Claude 走的正是同一个 `CodexEventReducer.ActivityFor` / `Summary`，两个坑原样复现（G18/G19）。**"共用函数"是风险源不是保险**。
6. **临时研究产物要清**：本次研究在 `F:\Git\scratch\claude-hook-recon\` 落了 44 MB 字符串转储（含二进制内的产品源码片段）。结论入档后删除，不留在仓库、不提交。
7. **2026-10-08 exec 形式是 Windows 上唯一干净的命令钩子写法**：2.1.236 的 hook schema 原文——`args` 存在时 "`command` is resolved as an executable and spawned directly with these arguments — no shell. Path placeholders… are substituted per-element as plain strings"；`args` 缺省时 "`command` runs through a shell (**bash on POSIX, PowerShell on Windows without Git Bash**)"。也就是说默认路径下命令要经 bash/PowerShell 解析一遍，路径里的空格、反斜杠、`$`、反引号全是雷（agy 判例 #4 的同类问题）。用 `command:"node"` + `args:[脚本绝对路径]` 整体绕开。**副作用**：`node` 必须在该进程的 PATH 里（本机已满足，opencode/agy 的胶水脚本同样依赖）。
8. **`last_assistant_message` 的确切裁剪语义没静态读出来**：Stop 的构造式里它是 `Rd(content, <模板>)` 再 `.map(trim).filter(Boolean).at(-1)`，可能是"整段"也可能是"最后一行"（`Rd` 在打包产物里有多份同名定义，无法从静态文本断定）。因此实现里**只把它当兜底**（转录本拿不到正文时才用），要当主力必须先实测。
9. **2026-10-08 实测三连（用户看手机当场发现的）**——三条都是"静态读代码看不出来、只有真机跑才现形"的类型，务必记住判据：
    - **`/context` 这类本地斜杠命令会写 `queue-operation: enqueue` 却永远不写配对的 `remove`**。按"最后一条队列操作说了算"会永久停在"排队中"，并把 `/context` 顶到第二行冒充用户提示词。→ 排队判定必须排除斜杠命令与 `<...>` 系统注入项；且排空后必须**还原** UserPrompt（否则排队文本一去不回）。
    - **孤立 `SubagentStop` 真会出现**（那条的 `agent_type` 为空），若允许它新建行，手机上就凭空多出一行"已完成"的幽灵子代理。→ 只有 `SubagentStart` 能新建行。
    - **`AskUserQuestion` 会同时发 `PermissionRequest`**（它是 "requires user interaction" 工具）。按普通工具走审批路径的后果：手机上是 **APPROVAL 卡片 + 允许/拒绝按钮**（选择题用那个答不了），第三行被换成工具名 `Question` 而不是问题文本，已经进入的"等待回答"还会被压成"等待批准"。→ 提问工具的权限询问必须走提问路径（用 `IsTerminal` 而非 `CanAdvance` 做守卫，否则它进不去 WaitingAnswer）。
    - **Claude 的 payload `cwd` 跟着 Bash 的 `cd` 漂**——而**这是期望行为，不是缺陷**：这一行反映"会话此刻在哪个目录干活"（用户裁定，见判例 10）。曾按"opencode 用会话目录、agy 用 `workspacePaths[0]`，都稳定"把它冻结成首值，**已被撤销**。**不要再冻回去。**
    - ~~附带确认：**被杀掉的子代理会正常发 `SubagentStop`**，子代理行干净收场~~ —— **2026-10-11 探针实测推翻**：一次"起子代理 → `TaskStop` 掐掉"的完整回合里，`SubagentStop` 钩子**一次都没触发**（钩子日志只有 `SubagentStart` + 主会话自己的 `Stop`），那条子代理行会永远停在 `running`、父任务永远 `delegating`。当时那句是把"子代理自然跑完"误当成了"被杀"。**改用 `TaskStop` 的入参收敛**（见 G47）。

10. **别按"另两家怎么做"去改 Claude 的语义（2026-10-08，用户裁定撤销）**：我看到项目名跟着 Bash 的 `cd` 漂，就按"opencode 用会话目录、agy 用 `workspacePaths[0]`，都稳定"把它冻结成首次取值。用户看到后说："之前我看是会跟随你的 cd 变动的，感觉是没问题的"——**跟随 cd 恰恰是有用信息**（一眼看出这个会话此刻在哪个目录干活），而"稳定"只是我自己推出来的偏好。已全部回退并加断言锁住。
    **教训**：跨来源的"统一"只该用在**规范已经写死**的地方（标题只取真实标题、本轮计时、第三行二态、守卫…）；凡是**没有规范**的行为差异，先当成"这条来源的特性"，要么问，要么留着——不要用"另两家不是这样"当作改动理由。判断标准：这个差异会让用户看到错的信息吗？会 → 修；只是不一样 → 不动或先问。

11. **2026-10-08 签名的转义必须与 App 端 kotlinx 逐字节一致（本轮最难找的一个）**：手机作答"点了没反应"。排查链——桥日志显示 `挂起等待` 后**没有** `作答已回填`（说明 `ResolveAnswer` 从没被调用）、按钮能显示（说明能力位与挂起卡都对）→ 于是往"控制消息根本没进 `Perform`"查，落到签名：桥端用 STJ 默认编码，`"有按钮…"` 会变成 `"有按钮…"`；App 端 kotlinx 原样输出中文 → 两侧 HMAC 必然不同 → `InvalidSignature`。
    **为什么以前没暴露**：批准/拒绝/静音/置顶的 `value` 都是 `null`，`canonicalPayload` 走的是不带 value 的短形式，压根不涉及字符串转义。
    **教训两条**：① 跨语言算签名时，"字符串怎么转义"是协议的一部分，必须**逐字节对齐并写进断言**（现在有一条断言直接比对 App 端会算出的那个 payload 串）；② **协议层的拒绝不能静默**——这次失败此前只在 App 侧体现为"没反应"，桥日志一片空白，白花一整轮。现在被拒的控制消息会记 WARN（含原因）。

12. **守卫写出"一刀切"是本轮唯一一次翻车**：`CanAdvance` 最初把 `WaitingApproval`/`WaitingAnswer` 一并锁死，结果 `PostToolUse`（等待的解除信号）也被挡，任务会卡死在等待态、手机卡片永远不消失——`windows/tests` 的 `claude: question answered -> running` 当场报红。教训：**锁"推进"与锁"解除"必须分开写**（现为 `CanAdvance` 与 `IsTerminalState` 两个判据）。给新来源加守卫时，先问一句"谁是解除信号"。

13. **2026-10-11 "开机那一刻，标题不可能存在"——别在那一刻建行（G46）**：`SessionStart` 是**进程启动**事件而不是"会话开始干活"事件。实测它的载荷只有 `session_id` / `transcript_path` / `cwd` / `hook_event_name` / `source` 五个键，**没有 `session_title`**；而 `transcript_path` 指向的文件**此刻还不存在**（探针实测 `exists=false`，111 ms 后才随第一轮写盘）。也就是说标题的三条来源在同一瞬间**全部为空**——这不是"标题来晚了"的竞态，是"这一刻不可能有标题"。那时的新行唯一能挂的标题就是 `cwd` 目录名，用户看到的就是"每次启动 100% 冒出一个没有 session title 的幽灵任务"。**判据（可推广到任何来源）**：某个事件到达时，**这条来源的标题在这一刻是否可能存在**——不可能，就别让它在那一刻建行（改为只复位已知行）。**注意"进程启动"≠"用户开始干活"**：agy 的事件表里根本没有"会话创建"这类事件，行随第一个回合诞生，所以 agy 结构上不会出这个幽灵——这次的差异**不是**"另两家怎么写"，而是"我们多接了一个出生时刻太早的事件"（与判例 10 不冲突）。

---

## 七、一句话的验收模板（交付时照抄）

> 产物：bridge `dist\opencode-main-baseline-vN\AgentPagerBridge.exe`；Claude 转发脚本 `~/.claude/agentpager-claude.js`（由「安装 Claude Code Hook」部署）；APK 已拷 `…\AgentPager-debug.apk`（SHA 一致，本轮 app 无改动）。
> 你要做：① 退出旧 bridge → 启动 vN；② 托盘点「安装 Claude Code Hook」（**本轮必须点，命令形态变了**）；③ 重开一个 `claude` 会话（hook 对已开会话是否即时生效未实测）；④ 首次触发 hook 时 Claude Code 若问信任，选信任。
> 验证点：会话刚打开显示"空闲"而非"启动中"；标题是 AI 生成的标题（不是提示词）；耗时是本轮时长、从 0 起算；`Bash npm test` 是测试动画；`TodoWrite` 是四角轮流不是书写波；**回合进行中模型写正文时第三行滚动正文预览、工具一开跑就换成工具步**；回合结束后第三行保留终稿且**不回显**你的提示词；`AskUserQuestion` 出现带题号全文与选项的 QUESTION 卡片**并且有输入框 + 「发送回答」**——在手机上作答应让终端**不弹对话框**、模型直接收到答案；子代理干活时父任务显示"正在协作"、第三行不被子代理步骤顶掉；按 `Esc` 中断后显示"已中断"（不是"任务完成"）；权限对话框弹出时手机置顶且**终端仍可照常作答**；关闭 `claude` 后任务变"离线"。

---

## 八、专题：手机作答 Claude 的提问（2026-10-08 研究 → v36 实现，行为待实测）

**问题**：AgentPager 的 App 端早就画好了"作答"交互（输入框 + 黄色「发送回答」按钮，`AgentGridScreen.kt:889-904`，协议侧 `ControlAction.Answer` / `TaskCapability.Answer` / wire 名 `"answer"` 一应俱全），但**桥端从来没接**：`grep ControlAction.Answer windows/src` 零命中、`git log -S` 为空、`Perform` 落 `default → Unsupported`（macOS `TaskCatalog.swift:205` 同样显式 unsupported）。所以那道 UI 永远不会出现。**问题是：Claude 这条来源能不能接上？**

### 结论：能，走 `PermissionRequest` 的 `updatedInput`（**v36 已实现**；代码级已确证，行为待实测）

`AskUserQuestion` 是 `requiresUserInteraction(){return!0}` 的工具（二进制实测），它的权限询问**就是提问本身**。而权限路径对这类工具有一条专门的放行分支（原文）：

```js
if(g.behavior==="allow"){
  let y = g.updatedInput ?? f ?? t;
  if(!g.updatedInput && e.requiresUserInteraction?.()) return null;   // 没带 updatedInput 直接丢弃
  if(g.updatedInput){
    let _ = ZVr(await c5e(e, y, {...r,toolUseId:o}, {hookUpdatedInput:g.updatedInput}), e.name);
    if(_?.behavior==="deny") return …; if(_?.behavior==="ask") return {reprompted:_, finalInput:y};
  }
  return this.handleHookAllow(y, g.updatedPermissions ?? [], m);      // → buildAllow，不再弹对话框
}
```

配套三处佐证：

1. **工具的输入 schema 本来就带答案字段**：
   `answers: …optional().describe("User answers collected by the permission component")`，另有 `response`（自由文本）、`annotations`、`metadata`。
2. **`call()` 原样透传**：`async call(e,t){let{questions:r,answers:n={},annotations:o}=e,…; return {data:{questions:r,answers:n,…}}}` —— 入参里的答案直接变成工具结果。
3. **输入校验不拦**：`QTf={check:e=>{…只查问题文本唯一、每题选项 label 唯一…}}`，**不检查答案是否已存在**。

结果形状（`sdk-tools.d.ts` 的 `AskUserQuestionOutput`，权威）：
```ts
{ questions: [...]; answers: { [问题文本]: "答案" };   // 多选逗号分隔
  response?: string;                                    // 用户手打的自由文本
  annotations?: {...}; afkTimeoutMs?: number }
```

### 落到 AgentPager 的方案

| 步 | 做什么 |
|---|---|
| 1 | Bridge 收到 `PermissionRequest` 且 `tool_name == AskUserQuestion`：**原样保存 `tool_input`**（回填时必须完整带回 `questions`），下发 QUESTION 卡片 |
| 2 | `Capabilities` 打开 `Answer`（现在是恒空 → App 的输入框才会出现）。**不要**再给 `Approve/Deny`：选择题用"允许"答不了 |
| 3 | App 点「发送回答」→ `ControlAction.Answer` + 文本 |
| 4 | Bridge 把答案写回该 hook 的 stdout：`{"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"allow","updatedInput":{"questions":<原样>,"response":"<文本>"}}}}` |
| 5 | Claude Code 校验 → `handleHookAllow` → 工具带答案运行 → 模型收到答案，**终端不弹对话框** |

**与现有 UI 的契合度**：App 现在只有一个自由文本框（选项按设计不渲染），而 `response` 字段的语义正是"用户手打的自由文本，不选结构化选项"——**一对一对应，UI 不用改**。将来若要支持点选项，改成 `answers: {"<问题文本>": "<label>"}` 即可（同一机制）。

### v36 落地要点（实现侧）

- **只有 `PermissionRequest` 那条路能作答**：我们持有 hook 连接才写得回答案。只走 `PreToolUse`（说明权限没触发询问）时**不给** `Answer` 能力位——否则 App 会画出一个送不出去的输入框。
- **原始 `tool_input` 整份存下来**（`_claudeQuestionInputs[会话]`）再回填；拿不到原始入参同样收回能力位。
- App 端**零改动**：作答 UI 早就画好了（`AgentGridScreen.kt:889-904`），wire 名 `"answer"` 两端一致（`TaskCapability.@SerialName("answer")`）。

### 必须记住的约束与未实测项

- **只有权限询问这条路上成立**：不是"任何时候都能替用户回答"，而是"趁 `PermissionRequest` 挂着的时候答"。窗口 = 我们注册的 `timeout: 86400`，但**用户随时可能在终端抢先作答**——那之后 hook 的答案就废了（正常现象，不是 bug）。
- **`updatedInput` 必须带完整 `questions`**：它是"改写工具入参"，不是"补丁"；只传 answers 会把问题本身抹掉。
- **实测记录（2026-10-08，v42 + 选项按钮版 APK）**：手机点选项 → 桥日志 `作答已回填（10 字）` → 转录本的 `toolResult` 为 `answers{"<问题全文>":"<label 原样>"}`、`response` 为空 → **终端全程没弹对话框**，模型直接拿到答案。也就是说 ① 带 `answers` 的入参确实能跳过终端对话框、② `answers` 的 key 用问题全文精确匹配 ✓ 都实锤了。那条被攥住 13 秒的 hook 也正常退场（无残留 node 进程）✓
- **仍未实测**：与"用户同时在终端作答"的竞态下 UI 表现（罕见，且两条路都只是"谁先到谁生效"，不是缺陷）。
- **另一条路（原生外部客户端）存在但不是给我们的**：Claude Code 有 `toolUseContext.requestDialog` + `hasExternalRacer` 的外部抢答架构（`permission_bridge_relay`、`answered elsewhere — dismissing`），但判据是 `e.bridgeCallbacks || (e.channelCallbacks && !tool.requiresUserInteraction?.())` —— **requiresUserInteraction 的工具只认 `bridgeCallbacks`**，那是 SDK / 远程客户端形态，AgentPager 不是那种客户端，接进去代价远大于 hook 方案。
