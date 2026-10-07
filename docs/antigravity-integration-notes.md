# Antigravity ↔ AgentPager 集成协作备忘

> 本文记录 Antigravity（`agy`，Google Deepmind 闭源开发平台）与 AgentPager 的对接技术决策、协作节奏与判例清单。
> 用途：后续会话 / 新 agent 按同样方式工作，避免重复踩坑。
> 定稿日期：2026-10-07

---

## 一、协作节奏（核心，务必保持）

用户提要求 → agent 改 → 构建 → **明确告知哪些要重启** → 用户验证 → 反馈 → 下一轮。
小步推进，一次只解决一件被点名的事。

**Agent 侧固定动作：**

1. **先读现状再开口**：任何关于代码的断言前，先 Read 相关文件（跨会话 / 用户可能手动改过，旧快照不可信）；涉及行为先 `git status` + `git diff` 体检。
2. **范围最小**：只动用户点到的事。用户只是"描述现象 / 提问"时**不要动代码**；没被要求的"顺手改"一律不做，最多列出来问。
3. **每改必验**：先跑隔离验证（见第四节），全绿再构建；构建失败如实报，不静默放过。
4. **构建后给"重启清单"**：明确说清这次要重启什么、不用重启什么、步骤是什么（见第二节矩阵）。这是节奏里最被重视的一环。
5. **产物路径写到底**：bridge 的 exe 全路径、APK 的同步路径、脚本落盘位置；不要只说"构建好了"。
6. **诊断拿证据**：转录本查询 / 临时插桩 / WS 观察，不靠猜。临时物**用完即还原**。
7. **诚实汇报**：没验证的说没验证；有残余风险的主动列。

**用户沟通风格（适应）**：极简、命令式、一次一个点；"别改"即停；验证后会给出精确的现象描述（这是最有价值的输入，直接拿它当测试用例）。

---

## 二、重启矩阵（改哪里 → 要做什么）

| 改动类型 | 需要做的动作 | 说明 |
|---|---|---|
| bridge（C#） | 退出旧 bridge → 启动 `dist\opencode-main-baseline-v<N>\AgentPagerBridge.exe` | 运行中的 exe 被占用，每次构建至新 vN 目录 |
| Antigravity 钩子脚本（`agentpager-antigravity.js`） | bridge 面板点「安装或修复」→ **无需重启 agy** | 钩子由 agy 按需以独立进程执行，下次触发立即生效 |
| 钩子配置（`~/.gemini/config/hooks.json`） | bridge 面板点「安装或修复」→ **即时生效** | 严格遵循 UTF-8 No-BOM 写入规范 |
| Android App | 重装 APK（同步至指定目录并核对 SHA-256） | 涉及来源徽标或模型标签展示时需要 |
| 仅 bridge 内部逻辑（数据格式不变） | 只换 bridge，脚本 / APK / agy 都不用动 | 纯 reducer 修复逻辑 |
| 仅文档 / 测试脚本 | 无需重启 | 纯文档与静态资产 |

---

## 三、已定稿的集成决策（改动前必须先读，别回退）

映射与协议细节以 `docs/antigravity-state-mapping.md` 为准，这里只锁关键结论：

- **通道机制**：
  - 核心载体为轻量级 Node.js 胶水脚本 `agentpager-antigravity.js`（内存占用 <20MB，冷启 ~25ms），严禁直接执行 220MB 的 Bridge 原生 exe。
  - 通过 TCP `127.0.0.1:49361` 发送统一信封 `{hook_source: "antigravity", payload: {event, conversationId, ...}}`。
- **Fail-Open 与首帧放行**：
  - `PreToolUse` 是 Antigravity 的同步前置门禁钩子，**必须在首帧立即向 stdout 返回 `{"decision":"allow"}\n`**，彻底杜绝命令阻塞与界面卡顿。
  - Bridge 未在线时，脚本捕获 TCP 连接异常并在 5ms 内以状态码 0 静默退出，绝不阻断开发流程。
- **转录本尾部异步读取**：
  - 所有耗时解析（用户真实 Prompt 剥离、Token 提取、正文预览）全部由 Bridge 异步打开 `<appDataDir>/brain/<id>/.system_generated/logs/transcript.jsonl` 完成。
  - 读取必须使用 `FileShare.ReadWrite` 共享锁，防止与正在写盘的 `agy` 产生冲突。
- **标题**：严格对齐 OpenCode 规范：只取 SQLite `conversation_summaries.db` 的会话真实生成标题（`项目名 · <db.title>`），缺失时显示项目名，绝不从 prompt 自造。
- **计时**：**本轮制**——以 `PreInvocation`（新一轮提交）作为起点（`StartedAt=now`），轮内工具调用不重置，`Stop` 到达时冻结为本轮耗时，下一轮重新从 0 起算。
- **Token**：从转录本当前轮最新 `PLANNER_RESPONSE` 提取 `input_tokens`、`cache_read_tokens`、`output_tokens`，总和为 Total。右侧小号 `total` 常驻，点击展开显示 `IN/CACHE/OUT/TOTAL` 明细。
- **第三行（LastStep）二态与粘滞**：
  - **工具态**：统一 `工具名 内容` 前缀（`Bash git log`、`Read hooks.md`、`Edit app.js`）；
  - **回复态**：正文预览 ≤120 字单行截断，仅在 `Stop`（且本轮无工具调用）写入，回合结束保留；
  - **守卫**：候选文本若原样包含用户当前 Prompt（≥4 字）直接丢弃并保留上一值，杜绝第三行回显第二行。
- **子代理**：通过 `invoke_subagent` 派发时映射为 `SubagentSnapshot`，父任务显示 `delegating`，手机端自动展开 `ANTIGRAVITY (N)`。
- **提问态（`ask_question`）**：触发 `WaitingAnswer` 状态锁与顶部 `QUESTION` 卡片展示，手机端只读，作答在 CLI 完成。
- **Windows UTF-8 No-BOM 红线**：Bridge 对 `~/.gemini/config/hooks.json` 进行管理与注入时，必须强制 UTF-8 No-BOM，防止写入 `\ufeff` 导致 Antigravity 的 Go JSON 解析器崩溃。

---

## 四、证物与工具（复用处）

| 工具 / 路径 | 位置 | 用途 |
|---|---|---|
| 转录本日志（真理源） | `~/.gemini/antigravity-cli/brain/<id>/.system_generated/logs/transcript.jsonl` | 取证用户 Prompt、模型正文、Token 用量与工具输入输出 |
| 全局钩子配置文件 | `~/.gemini/config/hooks.json` | 挂载与调试 Antigravity 生命周期钩子 |
| 手机同款快照观察器 | `ap-verify-plugin\snapshot-now.mjs` | WS 连 bridge，直接读取手机收到的 `state.snapshot` |
| 胶水脚本本地单测 | 命令行执行 `node agentpager-antigravity.js PreToolUse` | 模拟管道输入，验证首帧放行与 TCP 组包 |
| Bridge Reducer 隔离测试 | `%TEMP%\antigravity\ap-verify-agy` | 针对 AgyEventReducer 进行纯内存状态机流转断言 |

---

## 五、判例（坑已写死，别再踩）

1. **2026-10-03 220MB Bridge.exe 进程冷启动雪崩**：最初尝试在 `hooks.json` 中配置直接调用 `AgentPagerBridge.exe --hook --source antigravity`。由于单轮对话中往往连续触发 5-10 次工具调用，每次调用都冷启动拉起一个 220MB 的 .NET Core 单文件可执行程序，单次启动耗时 300~800ms，引发严重的 CPU 飙升与磁盘 I/O 争抢，`agy` CLI 交互出现极强卡顿感。
   → **结论**：绝对禁止直接通过钩子拉起笨重原生 exe。改由常驻轻量环境的 Node.js 脚本分发，启动耗时仅 ~25ms，内存占用 <20MB。
2. **2026-10-03 PreToolUse 门禁缺失导致 agy 挂起**：Antigravity 将 `PreToolUse` 作为前置门禁拦截器，底层的 Go 运行时在读取 stdout 前会一直阻塞等待。若脚本在建立 TCP 或读取文件完成前才输出，或未输出 `decision: "allow"`，`agy` 会发生严重死锁或默认拒绝调用。
   → **结论**：脚本启动的第一行代码必须是向 stdout 立即写入 `{"decision":"allow"}\n` 并 flush，彻底消除挂起风险。
3. **PowerShell 5.1 BOM 头导致 Go JSON 解析崩溃**：在 Windows 下若使用 PowerShell 的 `Set-Content -Encoding UTF8` 写入 `hooks.json`，会强制携带 UTF-8 BOM（`\ufeff`）。Antigravity 底层采用 Go 语言的 `encoding/json`，该标准库遇到 BOM 会抛出 `invalid character '\ufeff' looking for beginning of value` 并彻底瘫痪。
   → **结论**：所有针对 `~/.gemini/` 的文件写入必须强制 `new UTF8Encoding(false)`（UTF-8 No-BOM）。
4. **Windows 路径反斜杠与 `cmd /c` 转义陷阱**：Antigravity 在 Windows 下通过 `cmd /c` 派生钩子命令。若命令路径中包含 Windows 传统反斜杠 `\` 且有空格，极易因 Shell 二次展开丢失转义。
   → **结论**：`hooks.json` 中的脚本路径一律统一转换为正斜杠 `/`。
5. **转录本文件并发读取冲突（`FileShare.ReadWrite`）**：`agy` 正在以高吞吐向 `transcript.jsonl` 追加写入。若 Bridge 使用默认的 `File.ReadAllText` 打开，在 Windows 上会抛出 `IOException: 进程无法访问文件，因为另一个程序正在使用它`。
   → **结论**：Bridge 读取转录本时必须使用带 `FileShare.ReadWrite` 的 `FileStream`。
6. **`<USER_REQUEST>` 与系统元数据污染**：Antigravity 在 `transcript.jsonl` 中记录的 `USER_INPUT` 不仅包含用户输入，还拼接了 `<USER_REQUEST>`、`<ADDITIONAL_METADATA>` 与 `<USER_SETTINGS_CHANGE>` 等提示词标签。
   → **结论**：解析 Prompt 时必须使用正则表达式精准截取 `<USER_REQUEST>(.*?)</USER_REQUEST>` 内部的正文，剥离系统冗余信息。
7. **`ask_question` 等待态被后续事件意外冲掉**：触发提问后，`WaitingAnswer` 卡片应常驻置顶。若后续伴随工具清理或状态事件，不可将任务压回 `Running`。
   → **结论**：生命周期处于 `WaitingAnswer` 期间，忽略普通工具更新与流式变动。

---

## 六、一句话的验收模板（交付时照抄）

> 产物：bridge `dist\opencode-main-baseline-vN\AgentPagerBridge.exe`；Antigravity 钩子脚本 `~/.gemini/config/agentpager-antigravity.js`；APK 已拷 `…\AgentPager-debug.apk`。
> 你要做：① 退出旧 bridge → 启动 vN；② 检查 bridge 面板「ANTIGRAVITY: 已写入 vN」；③ 启动 `agy` 发送提示词。验证点：任务行即时出现、耗时起算、工具步粘滞显示、`ask_question` 提问卡片置顶、Token 明细可点开查阅。
