# OpenCode → AgentPager 状态映射表（v1 基线）

本文档定义 opencode（1.18.x，插件事件总线）到 AgentPager `TaskSnapshot` 的完整映射。
范围：Windows Bridge + Android。macOS 不在本次范围内（忽略）。

> TOOL 行（工具输出）的规则见 [tool-output-row.md](tool-output-row.md)；opencode 是该行的**基准实现**。

## 一、通道机制

- 载体：opencode 插件（JS），安装到 `~/.config/opencode/plugins/agentpager-opencode.js`（或 `$XDG_CONFIG_HOME` / `$OPENCODE_CONFIG_DIR` 对应目录），由 opencode 启动时自动加载。
- 传输：插件把事件以 JSON 行写入 TCP `127.0.0.1:49361`（Bridge 的 Hook 通道），信封与 Codex/Claude 一致：

```json
{ "hook_source": "opencode", "payload": { "event": "<事件名>", "properties": { ... } } }
```

- Bridge 立即回一个 `\n` 应答；Bridge 不在线时插件静默忽略（fail-open，不阻塞 opencode）。
- `payload.event` 取值 = opencode 总线事件名原样（`session.created` / `session.status` / …），另外由插件合成四类事件：
  - `chat.message`：用户提交 prompt 时（插件钩子）携带文本、模型与 `queued`（提交时会话仍在跑 = 排队）；
  - `prompt.active`：排队的 prompt 被接单（新的 assistant 消息开始生成）时发送，用于清除排队标记；
  - `reply`：assistant 正文预览——插件监听 text part 的 `message.part.delta`/结束帧，节流转发（首包 + 结束帧 + 间隔 ≥300ms 的增量），单行截断 ≤120 字；
  - `plugin.dispose`：opencode 退出/插件卸载时按会话发送。
- 子会话（`info.parentID` 非空，即 task 工具派发的内部子会话）不建独立任务；其事件由插件转换成 `subagent` 事件映射进父任务的 Subagents（见第二节末「子代理映射」）。

## 二、生命周期（Lifecycle）映射总表

| # | opencode 事件 / 钩子 | 触发时机 | Lifecycle | Activity | 附带字段与请求 | 备注 |
|---|---|---|---|---|---|---|
| 1 | `session.created` | 会话建立 | `idle` | 无 | ProjectName/StartedAt（`info.directory`、`info.time.created`） | 新建任务；不置 Starting，避免空会话误报 |
| 2 | `chat.message`（合成） | 用户提交 prompt | `running` | `thinking` | UserPrompt；`queued` 标记（提交时会话仍在跑 = 排队）；本轮计时起点（仅上一状态为 idle/offline/succeeded/interrupted 时重置 StartedAt=now，轮内插入不重置）；新一轮清空第三行（轮内插入不清，lifecycle 文案兜底） | 清除 CompletedAt/挂起请求；不产生标题 |
| 3 | `session.status` `busy` | 回合开始/继续 | `running` | 保持现值（空则 `thinking`） | — | 不动第三行（避免盖掉正文预览） |
| 4 | `session.status` `retry` | 上游重试 | `running` | `thinking` | LatestStep=`重试 #<attempt>: <message>`（message 缺失时=`重试中`；`TaskCatalog.cs:1274`） | 重试耗尽会转为 `session.error` |
| 5 | `session.status` `idle` / `session.idle` | 回合结束 | `succeeded`（当前为 `interrupted` 时保持 `interrupted`） | 清空 | CompletedAt=now、IsUnread=true | 两者的去重/收敛规则相同，等价处理 |
| 6 | `session.error` | 回合异常/中止 | `interrupted` | 清空 | CompletedAt=now、IsUnread=true、LatestStep=错误摘要 | 中止（MessageAbortedError）同映射；随后到达的 `idle` 被第 5 条守卫拦下 |
| 7 | `message.updated`（assistant 且带 `error`） | 助手消息失败 | `interrupted` | 同上 | 同上 | 与 6 等价（双保险），仅在终态换算去重 |
| 8 | `message.updated`（assistant 且 `time.completed`） | 助手消息完成 | 不变（等 5 收敛） | 不变 | TokenUsage 覆盖为最新消息值 | 只转发带 completed/error 的消息，避免流式噪声；不取最大值，压缩后如实回落 |
| 9 | `message.part.updated` tool `pending`/`running` | 工具开始 | `running` | 工具活动映射（见第三节） | LatestStep=标题/摘要 | 同一 part 的 (status,title,error) 不变时插件去重 |
| 10 | `message.part.updated` tool `completed` | 工具完成 | `running` | `thinking` | LatestStep=完成标题（工具调用粘滞显示至下一事件） | 回合未结束，等待 5 |
| 11 | `message.part.updated` tool `error` | 工具失败 | 当前为 `interrupted` 时保持，否则 `running` | `thinking` | LatestStep=`✗ <错误首行>` | 权限拒绝后的工具报错不得覆盖 interrupted |
| 12 | `permission.asked` | 等待权限批准 | `waitingApproval` | `executing` | PendingRequest(approval, `permission · patterns`)、Capabilities=∅ | 置顶+提示音；本基线不支持手机批准 |
| 13 | `permission.replied` reply=`once`/`always` | 权限已放行 | `running` | `thinking` | 移除挂起请求 | 工具继续执行 |
| 14 | `permission.replied` reply=`reject` | 权限被拒 | `interrupted` | 清空 | CompletedAt=now、IsUnread=true、移除请求 | 与 Claude PermissionDenied 语义对齐 |
| 15 | `question.asked` | 等待用户回答 | `waitingAnswer` | `thinking` | PendingRequest(question, 卡片摘要=问题全文 `question ?? header`)、第三行=header ?? question、options=各问题选项 label（协议携带，app 暂不渲染）、Capabilities=∅ | 只在 TUI 可作答；手机为只读展示 |
| 16 | `question.replied` | 已回答 | `running` | `thinking` | 移除挂起请求 | — |
| 21 | `reply`（合成，assistant 正文流） | 模型写回复 | 不变 | 不变 | LatestStep=正文预览（≤120 字单行截断；回显整段 prompt 时被守卫丢弃） | 插件节流：首包 + 结束帧 + ≥300ms 增量；不做生命周期迁移；工具活动期间整条忽略（乱序守卫，见上） |
| 22 | `prompt.active`（合成） | 排队的 prompt 被接单（新 assistant 消息开始生成） | 不变 | 不变 | PromptQueued=false | 排队窗口结束 |
| 17 | `question.rejected` | 问题被放弃 | `interrupted` | 清空 | CompletedAt=now、IsUnread=true、移除请求 | 工具以 RejectedError 结束 |
| 18 | `session.updated`（标题变化） | 自动生成摘要标题 | 不变 | 不变 | Title=`项目 · <标题>`（默认名 `New session - *` 时忽略） | 其余 updated（touch）只透传忽略 |
| 19 | `session.deleted` | 会话删除 | 终态保持；否则 `offline` | 清空 | — | 与 Claude SessionEnd 对齐；未终态子代理级联扫为 interrupted（防残留行落盘） |
| 20 | `plugin.dispose`（合成） | opencode 退出 | 终态保持；否则 `offline` | 清空 | — | 每个已知会话发一条；强杀不触发；未终态子代理级联扫为 interrupted |

不在上表的事件一律忽略（`todo.updated`、`session.compacted`、`file.edited`、`tui.*`、`installation.*`、`lsp.*`、`server.connected` 等）。

> **通用竞态守卫**（乱序防御，bridge v26 / 插件 v8 起）：① `WaitingApproval` / `WaitingAnswer` 期间，工具 part 更新一律忽略（实测 opencode 会在 `question.asked` / `permission.asked` 之后补发该工具 part 的 running/标题更新，不拦会把等待状态压回 running 并清掉挂起卡片）；② `Succeeded` / `Interrupted` / `Offline` 终态后，迟到的工具 part 一律忽略（不得把已完成任务复活回 running）；③ 插件 v8 源头守卫：正文结束帧若晚于工具 part 到达（toolSinceText 标记）由插件直接丢弃，残帧不出插件；④ 桥侧兜底：工具 part 处于 `pending`/`running` 期间（按状态判定、不依赖 activity，无时间窗），`reply` 整条忽略——覆盖 todowrite 类 thinking 工具，且不吞工具结束后的合法短终稿。

### 子代理映射（child session → SubagentSnapshot）

子会话（task 工具派生）不建独立任务；插件把它的关键事件转换成合成的 `subagent` 事件（`phase` 字段区分），父任务的 Subagents 行随之更新：

| 子会话事件 | subagent phase | 效果 |
|---|---|---|
| `session.created`（带 parentID） | `started` | 新建 SubagentSnapshot：名字与路径取自标题里的 `(@agent subagent)`，状态 Running / activity delegating |
| 工具 part（pending/running/completed/error） | `tool` | activity=工具活动映射，LatestStep=工具步骤（与主任务同规则：统一 `工具名 内容` 前缀，标题优先；回显整段用户 prompt 时丢弃；与主任务同款 part 去重） |
| assistant `message.updated` 完成 | `tokens` | TokenUsage 跟随该子会话最新一条完成消息 |
| `session.idle` / `session.status idle` | `done` | Succeeded（终态的 subagent 行约 4 秒后从列表淡出） |
| `session.error` | `failed` | Interrupted + `✗ 错误摘要` |

- 父任务在任一子代理未终态时显示 activity=`delegating`（「正在协作」），app 会自动展开该行并显示子代理面板（标题按来源显示，如 `OPENCODE (1)`）。
- 孙会话（父不是已存在任务的会话 id）丢弃，不建幽灵任务；子代理终态后迟到的 tool 事件不复活。
- 子会话的 chat.message / 正文预览 / 权限请求不上报（其提示词与正文不进入手机）。

## 三、工具 → Activity 映射

按 opencode 内置工具 id 匹配（MCP 工具按其工具名子串匹配，默认 `executing`）：

| 工具 | Activity |
|---|---|
| `read`、`list` | `reading` |
| `glob`、`grep`、含 `search` | `searching` |
| `edit`、`write`、`apply_patch`、`patch` | `editing` |
| `webfetch`、`websearch`、`mcp-websearch` | `browsing` |
| `task` | `delegating` |
| `bash`/`shell`（命令含独立词 `test`/`pytest`/`jest`/`vitest`，词边界匹配；`latest` 等子串不误报、`gradlew build` 不算测试） | `testing` |
| `bash`/`shell`（其余） | `executing` |
| `todowrite`/`todoread` | `thinking` |
| `skill`、`lsp` | `reading` |
| `question` | `thinking` |
| 其他 / MCP 工具 | `executing` |

匹配顺序：先精确/前缀匹配上表前段，再落到默认值；不得让 `todowrite` 误命中 `write`（先判 todo）。

## 四、字段映射

| TaskSnapshot 字段 | 来源 |
|---|---|
| `Id` | `sessionID`（`ses_*`） |
| `Source` | 常量 `opencode` |
| `ProjectName` | `info.directory` 的叶子目录名；空则 `opencode` |
| `Title` | 只取 opencode 的 session title，`session.created`/`session.updated` 实时同步：`项目名 · <info.title>`；默认名（`New session - `/`Child session - ` + ISO，与 `Session.isDefaultTitle` 同规则）忽略；无真实标题时显示项目名。不从 prompt 自造标题 |
| `UserPrompt` | `chat.message` 文本部分（过滤 `synthetic`）拼接，截 1000 字 |
| `LatestStep` | 第三行=二态：**工具**=统一 `工具显示名 内容` 前缀（`Edit src/App.kt`、`Write .gitignore`、`Bash git log`、`Read src/x.ts`、`Grep TODO`…；命令类工具同样带工具名、不再裸命令；标题已含工具名时不重复。格式化函数 `ToolStep.Compose` 同时供 Codex/Claude 行使用）。内容取 tool `state.title` 或输入摘要白名单（command/description/filePath/path/pattern/query/url/subagent_type/agent/skill，不含 prompt），运行中与完成时都显示，粘滞到下一条正文/工具、**新一轮开聊时清空**（轮内插入不清，由 lifecycle 文案兜底）；**回复**=正文预览（≤120 字单行截断，回合结束保留）。无“思考”占位（`busy` 等不写第三行，避免盖掉正文预览）。截 220 字。**守卫：候选与当前 UserPrompt 相同或整段包含它（≥4 字）时丢弃、保留上一值——第三行永不重复用户 prompt** |
| `TokenUsage` | assistant `tokens`：`input`→Input、`cache.read`→CachedInput、`output`→Output、`reasoning`→ReasoningOutput、`total`→Total（缺省时求和）。每条完成的助手消息直接覆盖为最新值（不取最大值）——永远跟随最新消息，上下文压缩后如实回落 |
| `Subagents` | opencode 子会话映射（见第二节末「子代理映射」）：名字=子会话标题里的 `(@agent subagent)` 解析、状态=子会话生命周期、步骤=子会话工具 part、token=子会话 assistant 用量 |
| `StartedAt` | 本轮计时起点：一轮的第一个 prompt 到达时置为接收时间；轮内插入 prompt 不重置；`idle`/`error`/拒绝后由 CompletedAt 冻结为本轮耗时；下一轮 prompt 重新从 0 开始。首轮之前由 `session.created` 的 `info.time.created`（毫秒）初始化 |
| `CompletedAt` | `idle`/`error`/拒绝时的接收时间 |
| `IsUnread` | 成功/失败/拒绝置 true |
| `Capabilities` | 基线恒空（无手机控制） |
| `ModelName` | `chat.message` 的 `model.modelID`（其次 assistant `message.updated` 的 `modelID`）取最后一个 `/` 段：`ccproxy/deepseek-flash` → `deepseek-flash`；app 灰色标签优先显示它，缺失时回落原来源标签 |
| `PromptQueued` | `chat.message` 的 `queued`（提交时会话 busy=排队）；`prompt.active`（新 assistant 开始）、下一条 chat.message、session.deleted、plugin.dispose 时清除。app 第二行显示「你（排队中）：…」 |
| `updatedAt` | Bridge 接收时间 |

## 五、手机控制（本基线不含）

- `permission.asked` / `question.asked` 照常显示（置顶、提示音、展示摘要与选项），但不提供批准/回答按钮。
- 问题卡片为只读展示：`QUESTION <问题全文>`（选项随协议携带但暂不渲染——无现成排版参照）；批准确认卡片同理只读。
- 后续扩展路径（已预留通道约束，不在本次实现）：插件对权限事件改为阻塞等待 Bridge 回包，桥端回传 allow/deny，插件调用 opencode SDK `permission.reply`；`question` 同理。届时再把 Capabilities 打开。

## 六、已知限制

1. opencode 被强杀（任务管理器/断电）时 `dispose` 不触发，任务停留在 `running`，直到 Bridge 重启后仍按持久化保留，需要下一次同会话事件收敛。
2. 一个 Bridge 聚合所有目录下的 opencode 实例；不同项目的会话按各自目录名显示。
3. 基线只同步“状态流”（生命周期/活动/第三行相位/用量/标题）；助手正文只同步单行预览（≤120 字、节流），全文不搬运。
4. `retry` 无独立状态，显示为 `running` + 重试文案。
5. 排队标记由插件自行推导（提交时会话 busy = 排队；新 assistant 消息开始生成 = 接单清除）。极端情况下（排队 prompt 始终未被处理）标记会保留到下一轮 prompt 或会话删除。

## 七、验证清单（用户实测用）

1. 启动 Bridge（新构建），托盘/窗口确认「OPENCODE 已写入」。
2. 打开 opencode（TUI）→ 手机出现「项目 · 空闲」行。
3. 发一条 prompt → 变「正在运行」，总耗时从 0 开始计，标题保持项目名；opencode 生成 session title 后（约数秒）实时变为「项目名 · 生成标题」。
4. 触发工具 → 第三行变为「工具名 内容」（如 `Edit src/x.kt`、`Bash git status`、`Read src/x.ts`）并粘滞显示；模型写回复后第三行滚动显示正文预览。
5. 模型写回复 → 第三行滚动显示正文预览（单行截断）；回合结束 → 「任务完成」+ 提示音，预览保留，总耗时冻结为本轮时长；点开任务行看 TOKEN 明细（一直可点）；下一轮 prompt 计时重新从 0 计。
6. `Esc` 中断 → 「已中断」。
7. 触发权限询问（如 write/bash 需要批准）→ 「等待批准」置顶，展示 `permission · pattern`；在 TUI 里放行后回到运行。
8. 关闭 opencode → 任务变「离线」。
9. 子代理（task 工具）→ 父任务显示「正在协作」，展开区出现子代理行（名字/状态/耗时/最新步骤/token），完成后约 4 秒淡出。
10. 任务运行中再发一条 prompt → 第二行显示「你（排队中）：…」；该条被接单（新的 assistant 消息开始生成）后标记消失。

## 八、三层映射总表（像素图 ↔ TaskSnapshot ↔ opencode）

> **性质：代码现状快照，不是设计稿。** 本章记录的是提交 `82c8fa6` 时点的实际代码行为（逐条读码校订），不是设计意图；像素动画名是对 `PixelMotionEngine`（`android/app/src/main/java/com/agentgrid/mobile/render/PixelMotionEngine.kt:181-433`）各代码分支的直观描述，如有出入以源码为准。

### 链路

```
opencode 事件 ──插件(TCP 49361)──► TaskSnapshot(Lifecycle + Activity + LatestStep) ──wire──► app 任务行 ──► 像素图(3×3 九格)
```

- 像素图输入 = `PixelRenderState(lifecycle, activity, revision)`（`AgentGridScreen.kt:602`）。`revision` 只对内置模拟器任务生效（:606）；真实任务的重播只来自 lifecycle/activity 真实变化（`PixelRenderState.kt:12`）。
- 帧率/辉光：`waitingApproval` / `waitingAnswer` / `succeeded` 前 1.2s 额外辉光（`PixelMotionEngine.kt:117`）；idle/offline 30fps，succeeded/interrupted 落定 1.2s 后停帧（:91）。
- 像素引擎对 `waitingApproval` / `waitingAnswer` **只用 lifecycle 分支**，这两个状态下写入的 activity（executing/thinking）不参与渲染（`PixelMotionEngine.kt:234/252` 不读 activity）。

### 主表（事件 → 字段 → 像素图）

| opencode 实际事件/状态 | Lifecycle | Activity | 第三行 lastStep | 像素图动画 |
|---|---|---|---|---|
| `session.created` | idle | — | — | 中心慢呼吸（十字微光） |
| `chat.message`（提交 prompt） | running | thinking | 新一轮清空（轮内插入不清） | 四角轮流+中心脉动 |
| `session.status busy` | running | 保持现值（空→thinking） | 不动（防盖预览） | 保持当前 |
| `session.status retry` | running | thinking | `重试 #n: message`（缺省「重试中」） | 四角轮流 |
| 工具 pending/running | running | 工具映射（见第三节） | 「工具名 内容」粘滞 | 对应工具动画 |
| 工具 completed | running | thinking | 「工具名 内容」粘滞（不变） | 四角轮流 |
| 工具 error | running（interrupted 被 `TaskCatalog.cs:1364` 提前拦下，保持） | thinking | `✗ 错误首行` | 四角轮流 |
| `reply`（正文流） | 不变（waiting 期间、或工具处于 pending·running 期间整条忽略；残帧由插件 v8 源头丢弃） | 不变 | 正文预览 ≤120 字 | 保持当前（通常=四角轮流） |
| `session.status idle` / `session.idle` | succeeded（interrupted 保持，:1290） | 清空 | 预览保留 | 爆闪+打勾（1.2s 后定格） |
| `session.error` | interrupted | 清空 | 错误摘要 | 闪断→沉降为断口（落定定格） |
| `permission.asked` | waitingApproval | executing | — | 双拍心跳+中心冲击（强辉光） |
| `permission.replied` once/always | running | thinking | — | 四角轮流 |
| `permission.replied` reject | interrupted | 清空 | — | 闪断 |
| `question.asked` | waitingAnswer | thinking | 问题全文 | 中行三点省略号（强辉光） |
| `question.replied` | running | thinking | — | 四角轮流 |
| `question.rejected` | interrupted | 清空 | — | 闪断 |
| 子代理事件（改父任务） | 不变 | 父 running 时：任一子代理未终态→delegating，全终态→thinking；否则保持 | — | 分支脉冲 / 回落四角轮流 |
| `session.deleted` / `plugin.dispose` | offline（终态保持） | 清空 | — | 暗十字间歇双闪 |
| 排队 prompt（`chat.message` queued） | running | thinking | 第二行「你（排队中）」（第三行不动） | 四角轮流 |
| `prompt.active` | 不变 | 不变 | — | 不变 |

> 第三行工具步骤统一 `<工具显示名> <内容>`（`Edit/Write/Bash/Read/Grep/Glob/Task/WebFetch/…`）：改代码=`Edit <路径>`、命令=`Bash <命令>`；标题已含工具名时不重复。

### 工具 → Activity → 像素图（RUNNING 细分）

| 工具（按第三节规则命中） | Activity | 像素图动画 |
|---|---|---|
| read / list / skill / lsp | reading | 横向扫描线（自上而下掠过） |
| glob / grep / `*search*` | searching | 外圈 8 格跑马灯 |
| edit / write / patch | editing | 横向书写波 + 中列光标闪 |
| bash 含独立词 test/pytest/jest/vitest | testing | 斜向扫线 + 末尾判定脉冲 |
| bash 其余 / 未知 / MCP | executing | 竖直流光柱（逐格下落） |
| webfetch / websearch | browsing | 翻页带自上而下 |
| task | delegating | 中心脉冲 + 外圈分支扩散 |
| todowrite / question / null | thinking | 四角轮流 + 中心脉动 |

### 三个易误认为「设计」的代码事实

1. `session.created` 直接落 `idle`（`TaskCatalog.cs:1114`）——opencode 源**永不出现 `starting`**（螺旋点燃动画只被模拟器等来源使用）。
2. `waitingApproval` / `waitingAnswer` 的像素动画由 lifecycle 决定，activity 只写入不渲染；`reply` 流同样不改 activity，因此模型写回复时像素通常停留在 thinking 动画。
3. 同一 lifecycle+activity 的连续事件**不重播动画**（真实任务 revision 恒 0）；重播只发生在状态真实迁移时（模拟器除外，见 `AgentGridScreen.kt:606`）。
