# TOOL 行（工具输出）规则（2026-10-20 定）

本文档定义任务卡片展开面板里 TOOL 行（`LatestToolOutput`）的语义、三家来源与已知限制。
三家统一以 **opencode 为基准**：TOOL 行展示的是**非结构化的工具结果文本**。

---

## 一、标准

| 项 | 规定 |
|---|---|
| 内容 | 本轮**最近一次**工具执行的结果；非结构化文本 |
| 清空时机 ① | **新一轮对话**开始（用户重新开口 / `PreInvocation` 判定新回合） |
| 清空时机 ② | 工具结束，但**没有可显示的内容**（与成功/失败无关） |
| 工具执行期间 | **不清空**，保持上一条；不按工具逐个清 |
| 不可用来源 | Claude 的 `tool_response`（结构化）、agy 的 `steps/<n>/output.txt`（快路径） |
| 持久化 | `Persistence.Save` 不落盘该字段；桥重启后为空 |

"清空" = 字段置 null → Android 端 `showToolOutput` 为 false → TOOL 行不渲染（不占位）。

失败不算"没有内容"：错误文本本身就是这次工具的输出。只有**成功无输出**与**失败且无错误文本**才清空。

---

## 二、三家来源

| Agent | 来源 | 更新时机 | 结构化程度 |
|---|---|---|---|
| **opencode** | `message.part.updated` 的 `state.output` / `state.error` | 工具完成事件当下 | 非结构文本（基准） |
| **Claude Code** | 转录本 `.jsonl` 的 `tool_result` 块 | 心跳 ≤3 秒（mtime 门控）；或 `SessionStart` / `UserPromptSubmit` / `Stop` / `StopFailure` | 非结构文本 |
| **agy** | `transcript_full.jsonl` 的 `GENERIC.content` | 仅 `PreInvocation` / `PostInvocation` / `Stop` | 非结构文本（需剥前缀，见 §四.1） |

`PostToolUse` 对 Claude 与 agy 都**不写 TOOL 行**：钩子首帧转录本里还没有本次结果，读到的是上一条（实测）。

---

## 三、防回潮（per-tool floor）

转录本只能给出"本轮最新一条结果"，无法区分"本次工具"与"上一个工具"。因此在 `PreToolUse` 记下本次工具起点，转录本回填的时间戳必须 ≥ 该起点，否则拒绝写入。

| 实现 | 位置 |
|---|---|
| Claude | `TaskCatalog._claudeToolStartAt`，心跳与事件路径都传 `toolOutputFloor` |
| agy | `TaskCatalog._agyToolStartAt` → `AgyEventReducer.Reduce(..., toolOutputFloor)` |

**生命周期**（2026-10-20 修正）：

| 事件 | floor |
|---|---|
| `PreToolUse` | 设为收包时刻 |
| `PostToolUse`（成功） | **撤销** |
| `PostToolUse`（带 error）/ `PostToolUseFailure` | **保留** |

撤销的理由：floor 记的是**桥的收包时间**，hook 稍一延迟就会晚于结果时间，留着会把整轮后续回填全部拒掉——实测 agy 的 TOOL 行因此冻结了一整轮（错误文本挂了 2 分钟，期间 10+ 个工具的结果都没上来）。
保留失败分支的理由：那一刻转录本里只有**上一个工具**的成功结果，放行会用旧成功冲掉错误原文。

字典在任务被回收（`Normalize` 的 `IsPurged`）时清理。
拿不到结果时间戳时**放行**（宁可显示可能过期的结果，也不因缺元数据挡掉正常结果）。

---

## 四、agy 的已知限制

### 1. 元数据前缀必须剥离

agy 给 `GENERIC.content` 加的两行不是工具输出：

```
Created At: 2026-10-20T01:32:48+08:00
Completed At: 2026-10-20T01:32:49+08:00
<工具实际输出>
```

读取时剥掉开头连续的 `Created At: …` / `Completed At: …` 行（`AgyTranscriptReader.StripGenericMetadata`）。

### 2. "无输出清空"只覆盖元数据为空的情形

若一条 `GENERIC` 剥完前缀后为空串，按标准清空 TOOL 行。
但 agy 的 `GENERIC` 通常会写入自己的说明文本（实测大转录本 5130 条无一为空），那种文本算"有内容"，**无法表达"工具没有输出"**。

**这是接受下来的例外**，不做额外处理（不恢复 `steps/<n>/output.txt`）。

### 3. 没有周期刷新

agy 的转录本只在 invocation 边界/工具发起时落盘，轮询收益低，因此**不加心跳**。
代价：一串连续工具调用期间，TOOL 行可能停在旧值，直到 `PostInvocation` 或 `Stop`。

---

## 五、Claude 的前提

"无输出清空"依赖转录本里存在一个 **`tool_result` 块且其 content 为空**：

| `ReadToolResult` 返回 | 含义 | 行为 |
|---|---|---|
| `null` | 该行没有 `tool_result` 块 | 不动 TOOL 行 |
| `""` | 有结果但内容为空 | 清空 TOOL 行 |
| 非空串 | 正常文本 | 写入 TOOL 行 |

如果某个工具结束后**完全不写** `tool_result` 块，TOOL 行会保持上一条——这一点尚未实测，属于已知未验证前提。

---

## 六、相关代码

| 文件 | 位置 |
|---|---|
| `windows/src/AgentPager.Core/TaskCatalog.cs` | `ClaudeEventReducer.Reduce` / `ApplyTranscript` / `OpenCodeEventReducer.PartUpdated` |
| `windows/src/AgentPager.Core/ClaudeTranscript.cs` | `ReadToolResult`（三态） |
| `windows/src/AgentPager.Core/AgyHooks.cs` | `AgyTranscriptReader.Read` / `StripGenericMetadata` / `AgyEventReducer.Reduce` |
| `windows/tests/AgentPager.Core.Tests/Program.cs` | `*tool*` 断言 |
