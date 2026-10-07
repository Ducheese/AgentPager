# Antigravity → AgentPager 状态映射表（v1 基线）

本文档定义 Google Antigravity（`agy`，闭源 CLI / 运行时，基于 `hooks.json` 与转录本日志）到 AgentPager `TaskSnapshot` 的完整映射规范。
范围：Windows Bridge + Android。macOS 遵循对应事件逻辑（忽略差异）。

---

## 一、通道机制与架构推演

### 1. 闭源环境下的事实来源与可观测界面
Antigravity（`agy`）为 Google Deepmind 研发的闭源 Native 二进制程序（Go / Native 构建），没有源码可直接查阅。但通过其内置文档、系统级生命周期钩子规范及运行期落盘日志，可确立三大权威可观测界面（Ground Truth）：

1. **生命周期钩子（`~/.gemini/config/hooks.json`）**：
   - 官方提供的生命周期扩展总线，支持 `PreInvocation`、`PreToolUse`、`PostToolUse`、`PostInvocation`、`Stop`。
   - 钩子以独立子进程（`type: "command"`）执行，上下文通过 `stdin` 以 camelCase JSON 注入，执行决策通过 `stdout` 以 JSON 返回。
2. **转录本文件（`transcript.jsonl`）**：
   - 每次会话在 `<appDataDir>/brain/<conversationId>/.system_generated/logs/transcript.jsonl` 持久化结构化行式 JSON。
   - 完整记录 `USER_INPUT`（用户原始输入）、`PLANNER_RESPONSE`（模型思考、正文回答、Token 用量与工具调用列表）以及 `GENERIC`（工具执行输出结果）。
3. **系统提示词与工具调用契约**：
   - 工具调用统一遵循标准 JSON Schema，原生工具（`run_command`、`view_file`、`write_to_file`、`replace_file_content`、`ask_question`、`invoke_subagent` 等）均携带结构化的 `toolAction` 与 `toolSummary` 元数据。

### 2. 通道设计：轻量级 Node.js 胶水脚本 + TCP 49361
不同于 OpenCode 可在单一 Node.js 进程内常驻加载插件，Antigravity 的钩子为外部命令调用机制。为杜绝进程开销引发的终端卡顿与时序挂起，架构确立如下分工：

```
Antigravity (agy.exe)
      │
      ├─► cmd /c node ~/.gemini/config/agentpager-antigravity.js <Event> (stdin 管道)
      │         │
      │         ├─► [首帧 ≤1ms] stdout: {"decision":"allow"} 立即放行 agy 进程
      │         │
      │         └─► [异步 TCP] 写入 127.0.0.1:49361 (HookTcpServer)
      │                   │
      ▼                   ▼
(继续执行模型/工具)     AgentPager Bridge (C# / TaskCatalog)
                          │
                          ├─► 异步读取 transcript.jsonl 尾部 (获取 Prompt / Token / 文本预览)
                          └─► 归约投影 TaskSnapshot ──wire──► 手机端像素网格
```

- **载体**：由 Bridge 部署并托管在 `~/.gemini/config/agentpager-antigravity.js` 的轻量 Node.js 脚本。
- **传输**：脚本将事件封装为统一信封格式写入 TCP `127.0.0.1:49361`：
  ```json
  {
    "hook_source": "antigravity",
    "payload": {
      "event": "PreToolUse",
      "conversationId": "c5ab9fca-dcb5-42c5-a493-f3629fc96434",
      "workspacePaths": ["F:/Git"],
      "transcriptPath": "C:/Users/alord/.gemini/antigravity-cli/brain/c5ab9fca-dcb5-42c5-a493-f3629fc96434/.system_generated/logs/transcript.jsonl",
      "artifactDirectoryPath": "C:/Users/alord/.gemini/antigravity-cli/brain/c5ab9fca-dcb5-42c5-a493-f3629fc96434",
      "modelName": "auto",
      "stepIdx": 19,
      "toolCall": {
        "name": "run_command",
        "args": {
          "CommandLine": "npm test",
          "toolSummary": "Run test suite",
          "toolAction": "Running tests"
        }
      },
      "error": null,
      "terminationReason": null,
      "fullyIdle": true
    }
  }
  ```
- **核心红线（Fail-Open 与首帧放行）**：
  1. `PreToolUse` 是 Antigravity 内部的同步门禁钩子，**必须在首帧立即向 stdout 返回 `{"decision":"allow"}\n`**，绝不阻塞 `agy` 工具执行。
  2. 若 Bridge 未启动（TCP 49361 连接被拒绝），脚本捕获异常并在 5ms 内以 exit code 0 退出（Fail-Open），严禁向 Antigravity 抛出错误阻断开发流程。
  3. 耗时解析（如转录本读取、Token 提取、字符串规整）**全部交由 Bridge 端后台异步完成**，脚本自身只负责最快速度转发。

---

## 二、生命周期（Lifecycle）映射总表

| # | Antigravity 事件 | 触发时机 | Lifecycle | Activity | 附带字段与请求 | 备注 |
|---|---|---|---|---|---|---|
| 1 | `PreInvocation`（首轮或会话新建） | 用户提交 Prompt，模型即将调用 | `running` | `thinking` | `UserPrompt`（从 `transcript.jsonl` 尾部提取并剔除 `<USER_REQUEST>` 标签）、`StartedAt=now`（本轮起表）、清空第三行与完成标记 | 任务进入工作状态；新一轮清空第三行 |
| 2 | `PreInvocation`（轮内继续） | 工具执行完毕后模型继续思考 | `running` | `thinking` | 保持本轮 `StartedAt` 不重置；第三行保持工具粘滞或清空 | 不冲掉本轮计时 |
| 3 | `PreToolUse`（普通工具） | 任意工具即将执行 | `running` | 工具活动映射（见第三节） | `LatestStep=ToolStep.Compose(toolName, summary)`；粘滞显示 | 覆盖 `run_command`、`view_file`、`write_to_file` 等 |
| 4 | `PreToolUse`（`ask_question`） | 模型主动提问或等待确认 | `waitingAnswer` | `thinking` | `PendingRequest(question, questionText, options)`、第三行=`问题全文`、Capabilities=∅ | 手机端触发强辉光与提示音，卡片只读显示 |
| 5 | `PreToolUse`（`invoke_subagent`） | 模型派生子代理任务 | `running` | `delegating` | 更新父任务 `Subagents` 列表（见下方子代理映射） | 手机端自动展开子代理抽屉 |
| 6 | `PostToolUse`（工具正常结束） | 工具执行成功 | `running` | `thinking` | 保持上一工具标题（粘滞至下一事件） | 等待后续模型生成或下一工具 |
| 7 | `PostToolUse`（工具返回错误） | 工具执行失败（`error` 非空） | `running`（若已为 `interrupted` 则保持） | `thinking` | `LatestStep="✗ " + 错误摘要` | 报错粘滞提示；不打断会话（等待模型自我修复） |
| 8 | `PostInvocation` | 单轮模型输出与工具调度结束 | 不变（等待后续或 `Stop`） | `thinking` | `TokenUsage`（从 transcript 当前轮最新 `PLANNER_RESPONSE` 提取）；保持当前步骤不变（正文预览延至 `Stop` 写入，防工具步践踏） | 保持当前活跃工具或思考状态 |
| 9 | `Stop`（`terminationReason == "model_stop"`） | 回合正常完成（模型完成作答） | `succeeded`（当前为 `interrupted` 时保持） | 清空 | `CompletedAt=now`、`IsUnread=true`、`LatestStep` 置为纯文本正文预览（≤120 字，仅限本轮未调用工具的纯回复）或保留最终工具步 | 触发爆闪与提示音，本轮计时冻结 |
| 10 | `Stop`（`error` 或异常终止） | 回合异常中止或用户强行中断 | `interrupted` | 清空 | `CompletedAt=now`、`IsUnread=true`、`LatestStep="✗ " + (error ?? reason)` | 红色断口定格 |
| 11 | 会话超时 / CLI 退出 | 进程关闭或长久失联（无 Stop 到达） | 终态保持 30 分钟后由 Maintain 回收；非终态因缺失 Stop 保持在 running（见第六节已知限制） | 清空 | — | 终态 30 分钟回收，非终态不回收 |

> **通用竞态与乱序守卫（Antigravity 专有）**：
> 1. **`WaitingAnswer` 状态锁**：一旦 `ask_question` 触发进入 `WaitingAnswer`，在用户回答前，后续任何并发工具回调不得将其压回 `Running`。
> 2. **终态不可逆守卫**：会话进入 `Succeeded` 或 `Interrupted` 终态后，迟到的 `PostToolUse` 或 `PostInvocation` 绝对不得将任务复活为 `Running`。
> 3. **工具执行期正文覆盖守卫**：正文预览仅在 `Stop` 且本轮为纯文本回复时写入，运行期间（`PostInvocation`/`PreToolUse`/`PostToolUse`）绝不以旧正文覆盖工具步骤。
> 4. **UserPrompt 回显守卫**：若候选的 `LatestStep` 文本与当前 `UserPrompt` 相同或整段包含（≥4 字），判定为模型回显提示词，予以丢弃并保留上一值。

---

### 子代理映射（`invoke_subagent` → `SubagentSnapshot`）

Antigravity 拥有原生首类子代理系统（`invoke_subagent` / `manage_subagents`）。其派发结构映射至父任务的 `Subagents` 列表中：

| Antigravity 子代理动作 | 触发源 | `SubagentSnapshot` 字段与状态 | 视觉与行为 |
|---|---|---|---|
| 派生子代理（`invoke_subagent`） | `PreToolUse` 的 `toolCall.args.Subagents[]` | `Id = TypeName + "-" + Role.HashCode`<br>`DisplayName = Role ?? TypeName`<br>`Path = "/" + TypeName`<br>`Lifecycle = Running`<br>`Activity = Executing`<br>`LatestStep = Prompt` 摘要 | 父任务进入 `Activity = Delegating`；手机端自动展开面板展示 `ANTIGRAVITY (N)` |
| 子代理运行完成 | 转录本中 `invoke_subagent` 返回或会话轮次结束 | `Lifecycle = Succeeded`<br>`Activity = null` | 子代理打勾完成，约 4 秒后从列表淡出 |
| 子代理执行出错 | 转录本报错或 `manage_subagents` 报告 errored | `Lifecycle = Interrupted`<br>`LatestStep = "✗ 出错"` | 红色中断提示 |

---

## 三、工具 → Activity 映射

依据 Antigravity 原生工具名及 MCP 协议工具命名规则进行映射：

| 工具名 / 特征 | 映射 Activity | 说明 |
|---|---|---|
| `view_file`、`read_url_content`、`read_resource`、`list_resources` | `reading` | 文件只读查阅、文档抓取与静态资源读取 |
| `search_web` | `browsing` | 网页搜索与外部网络检索 |
| `write_to_file`、`replace_file_content` | `editing` | 源码创建、块替换与文件写入 |
| `run_command`（命中 `test`/`pytest`/`jest`/`cargo test`/`go test`/`dotnet test`/`npm test`） | `testing` | 测试套件与自动化单元测试执行 |
| `run_command`（其他命令行操作） | `executing` | 构建、工具运行与系统 Shell 执行 |
| `ask_question` | `thinking` | 触发 `waitingAnswer` 交互态 |
| `invoke_subagent`、`send_message`、`manage_subagents` | `delegating` | 子代理派生、消息调度与团队协同 |
| `schedule`、`manage_task` | `executing` | 定时器管理与后台守护进程管控 |
| MCP 工具：`*search*`、`*find*`、`*query*` | `searching` | 数据库/结构体/符号检索（如 idalib MCP） |
| MCP 工具：`*read*`、`*view*`、`*dump*` | `reading` | 外部只读接口与内存结构读取 |
| MCP 工具：`*write*`、`*edit*`、`*patch*` | `editing` | 补丁生成与外部配置修改 |
| 其他未识别工具 / MCP 工具 | `executing` | 统一回落至通用执行动画 |

---

## 四、字段映射

| `TaskSnapshot` 字段 | 来源与解析规则 |
|---|---|
| `Id` | `conversationId`（UUID 原样，来自 Hook 输入 payload） |
| `Source` | `AgentSource.Antigravity` |
| `ProjectName` | `workspacePaths[0]` 的叶子目录名；若空回落为 `"Antigravity"` |
| `Title` | 严格对齐 opencode 规范：只取 `conversation_summaries.db` 的会话真实生成标题（`项目名 · <db.title>`）；若无真实标题则显示 `项目名`。**绝不从 prompt 自造标题**。支持双通道实时同步：① 每次 Hook 事件到达时实时拉取最新标题；② 后台心跳（`Maintain`，每 3 秒）侦测 `conversation_summaries.db` 文件修改时间戳（`LastWriteTimeUtc`），用户无论是在 CLI 还是 Web 界面自改标题、或闲置/终态期间重命名，3 秒内自动同步并广播更新至 App 任务卡片 |
| `UserPrompt` | 从 `transcript.jsonl` 尾部抓取最近一条 `USER_INPUT`，正则剥离 `<USER_REQUEST>`、`<ADDITIONAL_METADATA>` 与 `<USER_SETTINGS_CHANGE>` 包装，截取 1000 字符 |
| `LatestStep` | 第三行=统一 `工具显示名 内容` 前缀（`Edit src/x.cs`、`Write test.txt`、`Bash git log`、`Read hooks.json`、`WebSearch ...`；命令与文件类均带工具名，不再裸露动词）。内容优先取特定操作参数白名单（`CommandLine`、`TargetFile`、`AbsolutePath`、`query`、`Url`、`Action` 等，**严格排除 `Prompt`**），运行中与完成时都显示，**粘滞到下一条正文/工具**、**新一轮开聊时清空**（轮内插入不清，由 lifecycle 文案兜底）；**回复**=正文预览（≤120 字单行截断，回合结束保留）。<br>① **回合边界硬隔离**：倒序扫描转录本提取正文时遇 `USER_INPUT` 即停止，绝不跨越到历史回合提取旧正文；<br>② **多工具执行免覆写**：当前回合内最新步骤若为工具调用（正文为空），正文候选保持 `null`，工具执行与完成步骤粘滞显示，绝不被之前的正文反向盖回；<br>③ **回显守卫**：候选与当前 `UserPrompt` 相同或整段包含它（≥4 字）时丢弃保留上一值——第三行永不重复用户 prompt |
| `TokenUsage` | 从转录本最新 `PLANNER_RESPONSE` 提取 `input_tokens`、`cache_read_tokens`、`output_tokens` 组合生成 |
| `Subagents` | 通过 `conversation_summaries.db` 的 `parent_conversation_id` 自动识别子代理会话并路由进父任务的 `Subagents` 列表中（绝不建立幽灵顶层任务）。子代理活跃时父任务保持 `Activity = Delegating`，手机端自动展开青色 `AGY (N)` 子面板，终态后约 4 秒淡出 |
| `Lifecycle` | 遵从映射表：`idle` → `running` → `waitingAnswer` → `succeeded` / `interrupted` |
| `Activity` | 详见第三节工具映射表；子代理活跃时为 `delegating` |
| `StartedAt` | 本轮计时起点：新一轮首个 prompt 接收时置为当前时间；轮内插入不重置；完成/中断时冻结为本轮耗时 |
| `CompletedAt` | 终态接收时间 |
| `IsUnread` | 回合完成或中断置为 true |
| `Capabilities` | 基线恒空（手机端只读展示） |
| `ModelName` | 取自 Hook payload 中的 `modelName`（如 `gemini-3.8-flash-high`） |
| `PromptQueued` | 严格对齐 opencode 规范：通过尾部扫描 `~/.gemini/antigravity-cli/history.jsonl`，若在当前回合运行期间用户在终端新提交了指令（时间戳 > `StartedAt`），自动标记 `PromptQueued = true`，并将第二行提示词切换为排队文本；App 第二行同步渲染「你（排队中）：…」。在新一轮开聊接单（`PreInvocation`）或会话结束（`Stop`）时清除 |
| `updatedAt` | Bridge 接收时间 |

---

## 五、手机控制（本基线范围）

- **`ask_question` 卡片展示**：
  - 手机端接收 `PendingRequestKind.Question`，置顶渲染带有 `QUESTION` 徽标的卡片，展示问题内容与多项选择分支。
  - **只读展示**：由于 Antigravity CLI 的交互为终端控制台本地阻塞式选择器（Terminal Checkbox/Radio List），v1 基线在手机端仅作为即时提醒器（响铃 + 强辉光），作答仍在本地 CLI 终端中完成。
- **权限拦截机制**：
  - `PreToolUse` 门禁默认首帧放行，所有真实权限管控交由用户既有的 Antigravity `settings.json`（如 `accept-edits`、`always-proceed` 或 `command-guard`）在本地判定。

---

## 六、已知限制

1. **强杀进程无 Stop 触发**：若用户直接关闭终端窗口或通过任务管理器杀掉 `agy.exe`，Antigravity 不会触发 `Stop` 钩子，任务在 Bridge 侧将保持在 `running`，直到新会话到达或 Bridge 重启。
2. **转录本文件并发读取**：Bridge 读取 `transcript.jsonl` 时必须显式声明 `FileShare.ReadWrite` 共享读锁，防止与正在高速落盘的 `agy` 进程产生 Windows 文件锁冲突。
3. **正文预览非打字机流式**：因 Antigravity 仅在 Invocation 结束或工具发起时落盘转录本，手机端正文预览为块级阶段性刷新，而非逐字打字机流式。
4. **多工作区路径聚合**：当 CLI 带有多个 `--add-dir` 时，`ProjectName` 优先取主工作区（`workspacePaths[0]`）。

---

## 七、验证清单（用户实测用）

1. 启动 AgentPager Bridge，托盘与主面板确认「ANTIGRAVITY: 已写入 vN / 未安装」。
2. 启动 `agy` CLI，会话就绪。
3. 输入一条指令（如「查看 hooks 文档」）并回车：
   - 手机端出现「项目名 · <db.title>」（或项目名）任务行；
   - 状态变为「正在运行」，耗时从 0 起算；
   - 像素图呈现「四角轮流 + 中心脉动」动画。
4. 工具执行阶段：
   - 调用 `view_file` 时，第三行显示 `Read docs/hooks.md`，像素图切换为横向扫描线（`reading`）；
   - 调用 `run_command` 执行测试时，第三行显示 `Bash npm test`，像素图切换为斜向扫线（`testing`）。
5. 触发子代理：
   - 派发 `invoke_subagent` 时，父任务显示「正在协作」，自动展开子代理抽屉 `ANTIGRAVITY (1)`，显示子代理角色与步骤。
6. 触发提问交互：
   - 触发 `ask_question` 时，手机端出现强辉光与提示音，顶部浮现 `QUESTION` 卡片。
7. 模型回答完毕：
   - 终端打印完毕后，`Stop` 事件到达；
   - 手机端切换为「任务完成」并伴随爆闪与打勾定格动画；
   - 总耗时冻结为本轮实际时长；第三行保留最终正文预览；
   - 点击任务行展开区，核对 `IN / CACHE / OUT / TOTAL` Token 明细与实际消耗吻合。
8. 再次发送新 Prompt：
   - 耗时重新从 0 起算，第三行清空并进入新一轮流转。

---

## 八、三层映射总表（像素图 ↔ TaskSnapshot ↔ Antigravity）

### 链路

```
Antigravity (hooks.json + transcript) ──脚本(TCP 49361)──► TaskSnapshot ──wire──► app 任务行 ──► 像素图(3×3 九格)
```

### 主表（事件 → 字段 → 像素图）

| Antigravity 实际动作 | Lifecycle | Activity | 第三行 LastStep | 像素图动画 |
|---|---|---|---|---|
| `PreInvocation`（新轮次开聊） | `running` | `thinking` | 新一轮清空（等待正文或工具） | 四角轮流 + 中心脉动 |
| `PreInvocation`（轮内模型继续） | `running` | `thinking` | 粘滞保留上一工具或清空 | 四角轮流 |
| 工具即将启动（`PreToolUse`） | `running` | 工具映射（见第三节） | 「工具名 内容」粘滞 | 对应工具细分动画（下表） |
| 工具执行成功（`PostToolUse`） | `running` | `thinking` | 工具名与内容继续粘滞 | 四角轮流 |
| 工具执行报错（`PostToolUse error`） | `running`（interrupted 则保持） | `thinking` | `✗ 错误摘要` | 四角轮流 |
| 模型正文生成完成（`PostInvocation`） | 不变 | 不变 | 保持当前工具步（正文预览延至 `Stop` 写入，防工具步践踏） | 保持当前（通常为四角轮流） |
| `Stop`（正常完成 `model_stop`） | `succeeded` | 清空 | 正文预览（若本轮无工具调用）或最终工具步 | 爆闪 + 打勾（1.2s 后定格） |
| `Stop`（报错中断 `error`） | `interrupted` | 清空 | 错误摘要 | 闪断 → 沉降为断口（定格） |
| 提问等待（`PreToolUse ask_question`） | `waitingAnswer` | `thinking` | 问题全文 | 中行三点省略号（强辉光） |
| 派生子代理（`invoke_subagent`） | 不变 | `delegating` | — | 中心脉冲 + 外圈分支扩散 |
| 进程离线 / 超时 | `offline` | 清空 | — | 暗十字间歇双闪 |

### 工具 → Activity → 像素图（RUNNING 细分）

| 工具（按第三节规则命中） | Activity | 像素图动画 |
|---|---|---|
| `view_file` / `read_url_content` / `read_resource` | `reading` | 横向扫描线（自上而下掠过） |
| MCP 检索工具（含 search/find/query） | `searching` | 外圈 8 格跑马灯 |
| `write_to_file` / `replace_file_content` | `editing` | 横向书写波 + 中列光标闪 |
| `run_command` 命中测试套件 | `testing` | 斜向扫线 + 末尾判定脉冲 |
| `run_command` 其他 / 通用 MCP | `executing` | 竖直流光柱（逐格下落） |
| `search_web` | `browsing` | 翻页带自上而下 |
| `invoke_subagent` | `delegating` | 中心脉冲 + 外圈分支扩散 |
| `ask_question` / 空闲思考 | `thinking` | 四角轮流 + 中心脉动 |
