# OpenCode ↔ AgentPager 集成协作备忘

> 本文记录一套已被验证有效的协作节奏，以及本轮 opencode 集成工作的决策与产物清单。
> 用途：后续会话 / 新 agent 按同样方式工作，避免重复踩坑。
> 定稿日期：2026-10-06

## 一、协作节奏（核心，务必保持）

用户提要求 → agent 改 → 构建 → **明确告知哪些要重启** → 用户验证 → 反馈 → 下一轮。
小步推进，一次只解决一件被点名的事。

**Agent 侧固定动作：**

1. **先读现状再开口**：任何关于代码的断言前，先 Read 相关文件（跨会话 / 用户可能手动改过，旧快照不可信）；涉及行为先 `git status` + `git diff` 体检。
2. **范围最小**：只动用户点到的事。用户只是"描述现象 / 提问"时**不要动代码**；没被要求的"顺手改"一律不做，最多列出来问。
3. **每改必验**：先跑隔离验证（见第四节），全绿再构建；构建失败如实报，不静默放过。
4. **构建后给"重启清单"**：明确说清这次要重启什么、不用重启什么、步骤是什么（见第二节矩阵）。这是节奏里最被重视的一环。
5. **产物路径写到底**：bridge 的 exe 全路径、APK 的同步路径、插件落盘位置；不要只说"构建好了"。
6. **诊断拿证据**：数据库查询 / 临时插桩 / WS 观察，不靠猜。临时物**用完即还原**（插件插桩后必须从仓库还原并哈希校验）。
7. **诚实汇报**：没验证的说没验证；有残余风险的主动列（如"片段式回显拦不住"）。

**用户沟通风格（适应）**：极简、命令式、一次一个点；"别改"即停；验证后会给出精确的现象描述（这是最有价值的输入，直接拿它当测试用例）。

## 二、重启矩阵（改哪里 → 要做什么）

| 改动类型 | 需要做的动作 |
|---|---|
| bridge（C#） | 退出旧 bridge → 启动 `dist\opencode-main-baseline-v<N>\AgentPagerBridge.exe`。（运行中的 exe 被占用，所以每次构建到新 vN 文件夹） |
| opencode 插件（`agentpager-opencode.js`） | bridge 面板点「安装或修复」→ **重启 opencode**（插件只在进程启动时加载）。版本头会显示"待更新 vX → vN" |
| Android App | 重装 APK（每次构建后拷到 `F:\FTP Client Available\ducheese\同步空间\AgentPager-debug.apk`，SHA-256 核对） |
| 仅 bridge 内部逻辑（数据格式不变） | 只换 bridge，插件 / APK / opencode 都不用动 |
| 仅文档 / 测试脚本 | 无需重启 |

## 三、已定稿的集成决策（改动前必须先读，别回退）

映射与协议细节以 `docs/opencode-state-mapping.md` 为准，这里只锁关键结论：

- **通道**：插件 → TCP `127.0.0.1:49361`，信封 `{hook_source:"opencode", payload:{event, properties}}`；合成事件 `chat.message` / `reply` / `plugin.dispose`。
- **状态**：`session.created`→idle；prompt→running；`session.status idle` / `session.idle`→succeeded（当前为 interrupted 时保持）；`session.error` / 拒绝→interrupted（迟到 idle 不复活）；`plugin.dispose`→offline（终态保持）。
- **标题**：只取 opencode 的 **session title 实时同步**；默认名（`New session -` / `Child session -` + ISO）忽略；**不用 prompt 自造标题**。
- **计时**：**本轮制**——第一个 prompt 起表（StartedAt=now），轮内插入 prompt 不重置，idle / error / 拒绝停表冻结为本轮时长，下一轮 prompt 重新从 0 起算。
- **Token**：**永远跟随最新一条完成的 assistant 消息**（不取最大值，压缩后如实回落）；右侧小号 `total` 常驻（上下文监视）；`IN/CACHE/OUT/TOTAL` 明细在**点开任务行**的展开区（任何状态可点，与生命周期无关）。
- **第三行（laststep）二态**：**工具调用**（运行中标题/摘要，完成后保留标题，粘滞）与 **回复正文预览**（≤120 字、单行、静态截断、节流 300ms、回合结束保留）。**无"思考"占位**；`busy` / `chat.message` / 工具完成 / 权限放行 / 问题放行 **绝不写第三行**（防盖掉正文预览）。空值时才回落 lifecycle 文案（空闲/正在运行/任务完成…）。
- **第三行守卫**：候选文本与当前 UserPrompt 相同或整段包含它（prompt ≥4 字）→ 丢弃、保留上一值。**laststep 永不回显用户 prompt**（第二行已经展示）。
- **模型标签**：行内灰色标签 = 模型名（取最后一个 `/` 段：`ccproxy/deepseek-flash` → `deepseek-flash`），缺失时回落来源标签；彩色徽标（Codex/Claude/OpenCode）不动。
- **插件版本化**：首行 `// agentpager-opencode vN`；bridge 三态识别：未安装 / 已写入 vN（内容与内置逐字一致）/ 待更新 vX → vN。
- **UI 形态**：无"切换式"——完成后不再用 TOKEN 行替换第二/三行；第二行恒为「你：prompt」。
- **App 展示分层**：自动层（问题/批准卡片 + 子代理面板：出现即显示、结束即自动收起）与手动层（TOKEN 明细：仅点击展开）相互独立——TOKEN 永不被自动内容带出，紧急态不占用行展开位。

## 四、证物与工具（复用处）

| 工具 | 位置 | 用途 |
|---|---|---|
| C# reducer 单测 | `%TEMP%\opencode\ap-verify-core` | 三态/计时/token/守卫/插件安装器全量断言，改 bridge 先跑 |
| 插件 TCP 单测 | `%TEMP%\opencode\ap-verify-plugin\test.mjs` | 用打补丁副本 + 独立端口隔离验证插件转发与节流 |
| 手机同款快照 | `ap-verify-plugin\snapshot-now.mjs` | WS 连 bridge，读手机收到的 state.snapshot |
| opencode 数据 | `~/.local/share/opencode/opencode.db`（SQLite，只读） | durable 事件/part/message/session 取证；`message.part.delta` 等非持久事件**不落库**，只能事件层抓 |
| 临时诊断插件 | `~/.config/opencode/plugins/zz-*.js` | 记录事件类型/字段；用完删除 |
| 探针实例法 | `opencode run`（临时目录） | 主实例不能重启时的端到端验证；新实例会加载全局插件，验证完即退 |

**插桩纪律**：对"已安装"的插件文件插桩后，必须从仓库源（`windows/src/AgentPager.Core/Resources/agentpager-opencode.js`）还原并哈希校验一致；bridge 的"待更新"识别就是靠内容逐字比对。

## 五、判例（坑已写死，别再踩）

1. **2026-10-06 正文预览被盖**：插件确实发出了 reply，但回合收尾又来了一个 `session.status busy`，当时的桥把 busy 写成"思考中…"直接盖掉预览。→ 结论：busy 不写第三行；"思考"占位废弃。
2. **`session.next.prompt.*` / `session_input` 是另一条路**：当前 TUI 不走 V2 输入队列（表为空），"queued"是 TUI 客户端自算的显示（消息下标 > 最后一条未完成 assistant）。插件想做 queued 标记要靠 `message.updated(user)` + assistant 出生/parentID 自行推导。
3. **工具标题会回显用户的话**（bash 命令、websearch query、task description 都是模型写的）：这就是 laststep 守卫存在的原因；仅"整段原样包含"可拦，改写/片段拦不住（已知残余）。
4. **中文数据不要用 PowerShell 直接看**：控制台编码（GBK/UTF-8）会把内容显示成乱码导致误判（曾把 title 看错）。查中文一律用 node + UTF-8。
5. **Windows 上运行中的 exe 不可覆盖**：所以 bridge 每次构建到新的 `vN` 文件夹，交付时明确说"启动哪个新路径"。
6. **"你别改"教训**：用户问"对不对 / 为什么"时只是信息交流，代码一个字都不要动；拿不准就先问，宁可慢半拍。
7. **2026-10-06 等待态被工具 part 压回（实测竞态）**：`question.asked` 正确推送 waitingAnswer + 卡片后，8ms 内又被该工具补发的 part running 更新压回 running（并把挂起请求清掉）。修复：WaitingApproval / WaitingAnswer 期间忽略工具 part 更新（bridge v11）。诊断手法：WS 行车记录仪（watch-git.mjs）+ DB part 序列双证。
8. **2026-10-07 工具执行期第三行被迟到正文盖回（实测乱序 ~200ms）**：`message.part.updated`（工具 running，+9ms）与正文结束帧（→ reply，+198ms）在插件侧到达顺序与语义相反——命令开始 9ms 就写对了第三行，198ms 后被上一段正文盖回，整个构建期显示旧正文，直到 completed 事件恢复。修复（bridge v15）：工具活动期间（activity 非空且非 thinking）整条忽略 `reply`；同时把工具 part 的忽略守卫扩到终态（Succeeded/Offline），防迟到 part 复活已完成任务。诊断手法：行车记录仪三连快照 + `ap-git-watch.log` 时间线（+9ms / +198ms / +12.2s），一次抓全。2026-10-08 终稿（bridge v28 + 插件 v8）：④ 源头守卫——插件对「结束帧晚于工具 part」的乱序残帧直接丢弃（toolSinceText，替代 v26 的 1 秒时间窗，不再吞合法短终稿）；⑤ 桥侧按「工具 pending/running」状态集忽略 `reply`（兜底，覆盖 todowrite 类 thinking 工具）；⑥ 退出/删除时未终态子代理级联扫为 interrupted；⑦ 插件无界 map 封顶 + session.deleted 清理（v8）。

9. **2026-10-11 判例 8 的 ④ 是错的：残帧判定不能比"到达顺序"**（用户报出"中途 reply 经常只有前两三个字，后面整段话都没了"）。④ 原文"插件对『结束帧晚于工具 part』的乱序残帧直接丢弃（toolSinceText）"——**"晚到"不等于"内容旧"**：模型写完一段正文**接着**调工具是常规时序，那段正文的结束帧同样晚到，但内容是**新的** ✗。于是整段小结/结尾正文被源头丢掉，那行只剩流式期节流发出的开头两三个字（用户看到的就是"什么数据库""关键"这种残句）✗✗。
   → **修法（插件 v11）**：按 opencode 自己的时间戳判残——`packages/schema/src/v1/session.ts` 里 `TextPart.time = { start, end }`、`ToolStateRunning/Completed/Error.time.start` 都是毫秒时间戳；**结束帧的 `time.end <= 最近一次工具 time.start` 才算残帧**，其余照收。工具 part 没有 `time.start` 时用到达时刻兜底（保持旧行为）。
   → **判据**：凡是"乱序/迟到"的守卫，**要比内容自身的时间戳，不能比到达顺序**——到达顺序只说明"谁先被看见"，不说明"谁更新"。
   → 测试两条钉住正反两面：`a text that ended AFTER the tool started is forwarded` / `a text that ended BEFORE the tool started is still dropped`。
   → **未复现的部分**：我自己那轮实测第三行是**正常生长**的（`工作` 2 字 → 102 字 → 120 字，走的是有增量 deltas 的路径，与守卫无关）。用户遇到的是"没有后续增量、只有结束帧"的路径（很可能是该 provider/model 不逐字流式），这条只能靠用户在重启后再验一次。

10. **2026-10-11 opencode 远程审批打通（插件 v13 + 桥 v68）**：opencode 的权限询问可以在手机上答复，落地形态与最初设想**不同**，两次踩坑都记在这里。
    → **坑一：`permission.ask` 插件钩子在 1.18.35 上根本不触发**。它的**类型**早在 2026-08-24 就进了 SDK（`(input: Permission, output: {status:"ask"|"deny"|"allow"}) => Promise<void>`），但它只存在于**更新的版本**里 —— 实测：插件注册了、真机一次没触发（桥日志零 `[OpenCode] 挂起等待`），安装版二进制里也**没有** `trigger("permission.ask")`（对照：`trigger("tool.execute.before")` / `trigger("shell.env")` 都在）。**结论：不要据 clone 的 HEAD 写代码**——clone 比用户装的版本新。
    → **坑二：`permission.asked` 是事件，不是可挂住的钩子**。所以通道反过来：插件在**事件回调里**（不能 await ✗，会把事件总线按分钟级卡住）`void holdApproval(...)` **游离地**另挂一条连接给桥（`hold:"approval"`）→ 桥攥住 → 手机允许/拒绝 → 桥回 `{"decision":"allow"|"deny"}` → 插件**用 opencode 自己的接口替它答复**。
    → **答复接口**（这是本条的真正关键）：`POST /session/{sessionID}/permissions/{permissionID}`，body `{ response: "once" | "always" | "reject" }` —— **opencode 自己的 TUI 在 auto 模式下就是这么答复的**，所以这条路必然能关掉终端那个对话框（Claude 那边做不到，见 claude-code notes G55 第 2 条）。SDK 生成的方法是 `postSessionIdPermissionsPermissionId`（v1 SDK 里就有 ✓）；新版封装叫 `permission.reply({requestID, reply})` —— 插件**运行时可用的那个**优先，两个都没有就 console.log 出 client 上的相关键（不猜）。
    → **桥侧**：带 `hold` 的帧按"挂住"处理（`_pending[sessionID] = (client, HookSource.OpenCode)` ✓），`Resolve` 回 `OpenCodeHookOutput.Decision(...)`；**能力位只在攥着回话通道时才给**（`permission.asked` 带 hold → `Approve/Deny`，否则空）—— 没有它手机上就是一对点不动的假按钮（今天之前 opencode 正是这个状态 ✗）。另加 `AcceptOpenCodeHook` 包装：等待若在终端那边结束了，就放掉挂住的连接（与 Claude 的 `AcceptClaudeHook` 同一个目的）。
    → **超时 10 分钟**（与桥的挂起上限、Claude 的 hook timeout 三家同值 ✓，见 claude-code notes G58）：挂住期间 opencode 就停在这次询问上，超时必须把询问交回终端（桥侧同时撤卡 + 放连接 ✓），绝不把它卡死。
    → **坑三（真机踩到）：两帧打架把能力位冲空**。第一版是"照旧 enqueue 普通事件 + 另发 hold 帧"——两帧走**两条连接**，谁后到谁说了算 ✗；而 `enqueue` 走的是排队链（会被前面积压的事件拖后 ✗）、hold 走新连接（快 ✓）→ "hold 先到、普通帧后到"成了常态 ✗ → 普通帧把刚给上的 `Approve/Deny` 清成空 ✗ = **手机上只剩卡片没有按钮**（用户实测报出）。**修法两层**：① 根因——插件**只发 hold 一帧**（它自带卡片所需的 sessionID/id/permission/patterns，桥侧单收它就建卡 + 给能力位 ✓）；② 桥侧兜底——`permission.asked` 的能力位**只升不降**（已在等审批的行不许被后续普通询问冲空）。<br>**复现手法（复用处）**：直接往桥的 49361 打合成帧就能验桥这半，不必启动 opencode —— 先普通后 hold → `["approve","deny"]` ✓；先 hold 后普通 → `[]` ✗（两组一跑，坑就现形 ✓）。
    → **坑四（真机踩到）：回复接口的 path 参数名各家版本不同 → URL 坏掉 → "点了没反应"**。按钮出来之后用户反馈"点了没反应" ✗：插件调的是 `postSessionIdPermissionsPermissionId`，而**参数键**在装好的 1.18.35 里是 **`{sessionID}`**（`url:"/session/{sessionID}/permissions/{permissionID}"`，params `key:"sessionID"`）、在更新的那版里是 **`{id}`** —— 我照 clone（=更新的那版）写了 `path:{id:…}` ✗ → 装好的那版模板**不被替换** → URL 直接坏掉 → 404 ✓✓。**修法：两个键都给**（生成器只读它声明过的那个 ✓，多余键被忽略 ✓）。<br>**教训**：这套 SDK 是**按生成的**，同一个方法在相邻版本里参数名就能变 ✗ —— 一律以**装好的那份二进制里的生成代码**为准（`grep -aoE 'url:"/session/\{[a-zA-Z]+\}/permissions' 装好的exe` 一句话就能看出来 ✓）。
    → **配套：插件自诊断回桥**。这次"点了没反应"一开始**查不到任何痕迹** ✗（插件 console.log 在 opencode 里看不到 ✗）。现在插件出错会发一条 `plugin.note` 事件 → 桥记 `[OpenCode] 插件报告：…` ✓（`AcceptOpenCodeHook` 里短路 → **不进 catalog**，否则会凭空建一行任务 ✗）。
    → **验证**：插件测试 8 条（**一次权限只发一帧且必是 hold** ✓ allow→`response=once` / deny→`response=reject` ✓ **两个 path 键都发** ✓ **回复失败会发 plugin.note** ✓ 子会话不上报不挂起 ✓）；Core 断言 10 条（能力位有/无 hold 两态、**只升不降**、提问两态、回包字节形状、新载荷字段）。真机侧：合成 hold 帧在手机上确认过**卡片 + 可用按钮** ✓。
    → **待真机确认**：① 手机上出现"允许/拒绝"并能真的放行 ✓ ② **opencode 自己的 TUI 会不会因此不再弹它自己的对话框**（这条决定了手机审批能否真正替掉终端 ✗）。

## 六、一句话的验收模板（交付时照抄）

> 产物：bridge `dist\opencode-main-baseline-vN\AgentPagerBridge.exe`；插件 vN（需要的话给"待更新"提示）；APK 已拷 `…\AgentPager-debug.apk`（SHA 一致）。
> 你要做：① 退出旧 bridge → 启动 vN；② 点「安装或修复」→ 重启 opencode（如涉及插件）；③ 重装 APK（如涉及 app）。验证点：……
