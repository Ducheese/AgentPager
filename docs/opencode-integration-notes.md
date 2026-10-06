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

## 六、一句话的验收模板（交付时照抄）

> 产物：bridge `dist\opencode-main-baseline-vN\AgentPagerBridge.exe`；插件 vN（需要的话给"待更新"提示）；APK 已拷 `…\AgentPager-debug.apk`（SHA 一致）。
> 你要做：① 退出旧 bridge → 启动 vN；② 点「安装或修复」→ 重启 opencode（如涉及插件）；③ 重装 APK（如涉及 app）。验证点：……
