// AgentPager opencode 插件单测
// 运行：node windows/tests/opencode-plugin.test.mjs
// 做法：读仓库插件源码、把端口补丁到测试端口、写入临时目录后 import，
// 用本地 TCP 假桥驱动插件的转发路径并断言。（2026-10-08 随插件 v8 重写）
import net from "node:net"
import { readFileSync, writeFileSync, rmSync } from "node:fs"
import { tmpdir } from "node:os"
import { dirname, join } from "node:path"
import { fileURLToPath, pathToFileURL } from "node:url"

const TEST_PORT = 49371
const here = dirname(fileURLToPath(import.meta.url))
const source = readFileSync(join(here, "..", "src", "AgentPager.Core", "Resources", "agentpager-opencode.js"), "utf8")
const copy = join(tmpdir(), `agentpager-opencode-test-${process.pid}.mjs`)
writeFileSync(copy, source.replace("const PORT = 49361", `const PORT = ${TEST_PORT}`))
const { AgentPagerOpenCode } = await import(pathToFileURL(copy).href)

const failures = []
function check(condition, name) {
  if (condition) console.log(`PASS  ${name}`)
  else { failures.push(name); console.log(`FAIL  ${name}`) }
}

const received = []
// hold 帧（插件挂住等手机的那种）由假桥回一行 JSON；普通帧回裸换行（即发即走）。
// 审批回 `{decision}`、提问回 `{answers}`——与真桥的 OpenCodeHookOutput 同一形状。
let replyDecision = "allow"
let questionAnswers = [["A"]]
const holds = () => received.filter((i) => i.payload?.hold)
const server = net.createServer((socket) => {
  let buffer = ""
  socket.on("data", (chunk) => {
    buffer += chunk.toString()
    let index
    while ((index = buffer.indexOf("\n")) >= 0) {
      const line = buffer.slice(0, index)
      buffer = buffer.slice(index + 1)
      if (line.trim()) {
        const envelope = JSON.parse(line)
        received.push(envelope)
        if (envelope.payload?.hold === "question") socket.write(JSON.stringify({ answers: questionAnswers }) + "\n")
        else if (envelope.payload?.hold) socket.write(JSON.stringify({ decision: replyDecision }) + "\n")
        else socket.write("\n")
      }
    }
  })
})
await new Promise((resolve) => server.listen(TEST_PORT, "127.0.0.1", resolve))

const clientCalls = []
// 装好的 1.18.35 给插件的是 SDK 生成的那版客户端：方法名 postSessionIdPermissionsPermissionId。
// （新版封装是 permission.reply —— 插件按"运行时可用的那个"挑，两条路各测一次。）
const lowLevelCalls = []
const fakeClient = {
  postSessionIdPermissionsPermissionId: async (args) => {
    clientCalls.push(args)
    if (process.env.AP_TEST_REPLY_THROWS === "1") throw new Error("模板未替换：/session/{sessionID}/...")
  },
  // 1.18.35 的插件 client 上**没有** question 分组（二进制实测：内部类 R7 只有 permission 那个
  // 扁平方法 + 一堆分组属性），所以作答走它自己的底层请求器——每个生成方法体都是
  // `this._client.post({url, path, body, headers})`，插件照抄这个调用约定。
  _client: {
    post: async (args) => { lowLevelCalls.push(args) },
  },
}
const plugin = await AgentPagerOpenCode({ client: fakeClient, directory: "F:/Git/demo" })
const emit = (type, properties) => plugin.event({ event: { type, properties } })
const flush = () => new Promise((resolve) => setTimeout(resolve, 150))
const events = (name) => received.filter((item) => item.payload?.event === name)

// 1. chat.message 转发 + 会话/工具 part 转发
await plugin["chat.message"]({ sessionID: "ses_t" }, { parts: [{ type: "text", text: "你好" }] })
await emit("session.created", { info: { id: "ses_t", directory: "F:/Git/demo", time: { created: 1 } } })
await emit("message.updated", { info: { id: "msg_1", sessionID: "ses_t", role: "assistant", time: { created: 1 } } })
await emit("message.part.updated", { part: { id: "prt_tool1", sessionID: "ses_t", callID: "call_1", type: "tool", tool: "bash", state: { status: "running", input: { command: "npm test" } } } })
await flush()
check(events("chat.message").length === 1, "chat.message forwarded")
check(events("message.part.updated").length === 1, "tool part forwarded")

// 2. 同签名工具 part 去重
await emit("message.part.updated", { part: { id: "prt_tool1", sessionID: "ses_t", callID: "call_1", type: "tool", tool: "bash", state: { status: "running", input: { command: "npm test" } } } })
await flush()
check(events("message.part.updated").length === 1, "duplicate tool part deduped")

// 3. 乱序残帧守卫（v8）：工具事件先到，旧正文结束帧必须被源头丢弃
await emit("message.part.updated", { part: { id: "prt_text_old", sessionID: "ses_t", messageID: "msg_1", type: "text", text: "旧正文残帧", time: { end: 2 } } })
await flush()
check(events("reply").length === 0, "stale end frame after tool is dropped (v8 guard)")

// 3b. **v11 关键回归**：正文写完接着调工具是常规时序，它的结束帧晚到但内容**是新的**，必须收下。
//     旧实现按到达顺序一律丢弃 → 手机上那行只剩流式期节流发出的开头两三个字，后面整段不更新。
await emit("message.part.updated", {
  part: { id: "prt_tool_ts", sessionID: "ses_t", type: "tool", tool: "bash", state: { status: "running", title: "ls", input: { command: "ls" }, time: { start: 5_000 } } },
})
await emit("message.part.updated", { part: { id: "prt_text_fresh", sessionID: "ses_t", messageID: "msg_1", type: "text", text: "工具之后写完的完整一段话", time: { end: 9_000 } } })
await flush()
const freshReplies = events("reply").filter((i) => i.payload.properties.text === "工具之后写完的完整一段话")
check(freshReplies.length === 1, `a text that ended AFTER the tool started is forwarded (got ${events("reply").length} replies)`)

// 3c. 同一判据的反面：结束帧确实早于工具开始 → 仍是残帧
await emit("message.part.updated", {
  part: { id: "prt_tool_ts2", sessionID: "ses_t", type: "tool", tool: "bash", state: { status: "running", title: "pwd", input: { command: "pwd" }, time: { start: 20_000 } } },
})
await emit("message.part.updated", { part: { id: "prt_text_stale2", sessionID: "ses_t", messageID: "msg_1", type: "text", text: "工具之前那段", time: { end: 19_000 } } })
await flush()
check(!events("reply").some((i) => i.payload.properties.text === "工具之前那段"), "a text that ended BEFORE the tool started is still dropped")

// 4. 新正文恢复流动后，预览正常转发（按增量断言，不吃前面用例的 reply 计数）
const repliesBeforeResume = events("reply").length
await emit("message.part.updated", { part: { id: "prt_text_new", sessionID: "ses_t", messageID: "msg_1", type: "text", text: "" } })
await emit("message.part.delta", { sessionID: "ses_t", messageID: "msg_1", partID: "prt_text_new", field: "text", delta: "新正文" })
await flush()
const resumed = events("reply").slice(repliesBeforeResume)
check(resumed.length === 1 && resumed[0].payload.properties.text === "新正文" && resumed[0].payload.properties.fullText === "新正文", `fresh preview and fullText forwarded after text resumed (got ${JSON.stringify(resumed.map((i) => i.payload.properties))})`)

// 4b. 超过 120 字符的长正文结束帧：即使 preview 已经截断饱和，结束帧也必须把权威全文完整转发（v18 修复）
const longMarkdown = "# 标题\n" + "A".repeat(200) + "\n| col1 | col2 |\n|---|---|\n| 1 | 2 |"
await emit("message.part.updated", {
  part: {
    id: "prt_text_long",
    sessionID: "ses_t",
    messageID: "msg_1",
    type: "text",
    text: longMarkdown,
    time: { start: 21_000, end: 25_000 },
  },
})
await flush()
const lastReply = events("reply").at(-1)
check(lastReply?.payload?.properties?.fullText === longMarkdown, "authoritative long fullText forwarded at end frame regardless of preview limit saturation")

// 4c. 流式末尾在节流窗口内到达，结束帧必须补发末尾切片（v19 修复）：
//     delta 累积了最后 22 字符但在 300ms 内未发，结束帧必须判定 sentLength < fullLength 强行补发。
const chunk1 = "前段文字已超长".repeat(15) // > 100 字符
const chunk2 = ">>> 节流尾巴第二行"
await emit("message.part.updated", { part: { id: "prt_stream_tail", sessionID: "ses_t", messageID: "msg_1", type: "text", text: "" } })
// 第一块触发发送
await emit("message.part.delta", { sessionID: "ses_t", messageID: "msg_1", partID: "prt_stream_tail", field: "text", delta: chunk1 })
await flush()
const replyAfterChunk1 = events("reply").at(-1)
check(replyAfterChunk1?.payload?.properties?.fullText === chunk1, "chunk 1 emitted via delta")

// 第二块紧接着到达（在 300ms 节流窗口内，delta 被节流阻断不发）
await emit("message.part.delta", { sessionID: "ses_t", messageID: "msg_1", partID: "prt_stream_tail", field: "text", delta: chunk2 })
// 立即收到结束帧
const streamFull = chunk1 + chunk2
await emit("message.part.updated", {
  part: {
    id: "prt_stream_tail",
    sessionID: "ses_t",
    messageID: "msg_1",
    type: "text",
    text: streamFull,
    time: { start: 30_000, end: 30_050 },
  },
})
await flush()
const finalReply = events("reply").at(-1)
check(finalReply?.payload?.properties?.fullText === streamFull, "throttled tail emitted via final updated frame (v19 fix)")

// 5. 子会话被删除必须先补终态（v9）：旧实现只删映射不发信号，桥上那条子代理行会永远 running
await emit("session.created", { info: { id: "ses_child", parentID: "ses_t", title: "explore (@explore subagent)" } })
await flush()
check(
  events("subagent").some((i) => i.payload.properties.id === "ses_child" && i.payload.properties.phase === "started"),
  "child session registered as a subagent row"
)
await emit("session.deleted", { info: { id: "ses_child", parentID: "ses_t" } })
await flush()
const childPhases = events("subagent").filter((i) => i.payload.properties.id === "ses_child").map((i) => i.payload.properties.phase)
check(childPhases.includes("failed"), `deleted child session sends a terminal phase (got ${JSON.stringify(childPhases)})`)
check(
  events("subagent").every((i) => i.payload.properties.sessionID === "ses_t"),
  "subagent frames always address the parent session"
)

// 6. 子代理的工具事件必须带命令文本（v10）：桥靠它判"是不是测试命令"，不带就会把 `npm test`
//    在子代理行上显示成"执行"而不是"测试"
await emit("session.created", { info: { id: "ses_child2", parentID: "ses_t", title: "build (@build subagent)" } })
await emit("message.part.updated", {
  part: { id: "prt_child_tool", sessionID: "ses_child2", type: "tool", tool: "bash", state: { status: "running", title: "npm test", input: { command: "npm test" } } },
})
await flush()
const childTool = events("subagent").filter((i) => i.payload.properties.id === "ses_child2" && i.payload.properties.phase === "tool").at(-1)
check(childTool?.payload.properties.command === "npm test", `child tool frame carries the command (got ${JSON.stringify(childTool?.payload.properties.command)})`)

// 5. 远程审批（v13）：权限询问事件 → 插件另挂一条连接给桥 → 桥上的决策 → 用 opencode 接口回复
replyDecision = "allow"
const beforeApproval = received.length
await emit("permission.asked", { sessionID: "ses_t", id: "per_1", permission: "bash", patterns: ["rm -rf build"] })
await flush()
// **只许发一帧**（实测踩过）：再照旧补一条普通 permission.asked 的话，两帧走两条连接、
// 谁后到谁说了算 —— 普通帧后到会把刚给上的能力位冲成空 ✗，手机上就只剩卡片没有按钮。
const approvalFrames = received.slice(beforeApproval).filter((i) => i.payload?.event === "permission.asked")
check(approvalFrames.length === 1 && approvalFrames[0].payload?.hold === "approval",
  `a permission sends exactly one frame, and it is the hold (got ${JSON.stringify(approvalFrames.map((i) => i.payload?.hold ?? "plain"))})`)
check(holds().some((h) => h.payload?.properties?.id === "per_1" && h.payload?.hold === "approval"),
  "permission.asked also arms a hold for the phone")
check(clientCalls.at(-1)?.body?.response === "once",
  `bridge allow is handed back as response=once (got ${JSON.stringify(clientCalls.at(-1)?.body)})`)
// 两个键都给：这条路由的 path 参数名各家版本不同（装好的 1.18.35 是 {sessionID}，新版是 {id}）
check(clientCalls.at(-1)?.path?.sessionID === "ses_t" && clientCalls.at(-1)?.path?.id === "ses_t" &&
  clientCalls.at(-1)?.path?.permissionID === "per_1",
  `both path key spellings are sent (got ${JSON.stringify(clientCalls.at(-1)?.path)})`)

replyDecision = "deny"
await emit("permission.asked", { sessionID: "ses_t", id: "per_2", permission: "edit" })
await flush()
check(clientCalls.at(-1)?.body?.response === "reject",
  `bridge deny is handed back as response=reject (got ${JSON.stringify(clientCalls.at(-1)?.body)})`)

// 5b. 回复失败要把原因**报回桥日志**（真机踩过"手机上点了没反应、却查不到任何痕迹"的亏 ✗）
process.env.AP_TEST_REPLY_THROWS = "1"
const beforeNote = received.length
await emit("permission.asked", { sessionID: "ses_t", id: "per_3", permission: "bash" })
await flush()
delete process.env.AP_TEST_REPLY_THROWS
const noteFrame = received.slice(beforeNote).map((i) => i.payload).find((p) => p?.event === "plugin.note")
check(typeof noteFrame?.properties?.note === "string" && noteFrame.properties.note.includes("权限回复失败"),
  `a failed reply is reported back through the bridge (got ${JSON.stringify(noteFrame?.properties?.note)})`)

// 5c. 远程作答（v16）：提问事件 → 只发 hold 一帧 → 桥上拿到答案 → 用 opencode 接口回复
questionAnswers = [["A"], ["C", "E"]]
const beforeQuestion = received.length
await emit("question.asked", {
  sessionID: "ses_t",
  id: "que_1",
  questions: [
    { question: "先选哪个？", header: "第一", options: [{ label: "A", description: "甲" }, { label: "B" }] },
    { question: "再选哪些？", header: "第二", multiple: true, options: [{ label: "C" }, { label: "D" }, { label: "E" }] },
  ],
})
await flush()
const questionFrames = received.slice(beforeQuestion).filter((i) => i.payload?.event === "question.asked")
check(questionFrames.length === 1 && questionFrames[0].payload?.hold === "question",
  `a question sends exactly one frame, and it is the hold (got ${JSON.stringify(questionFrames.map((i) => i.payload?.hold ?? "plain"))})`)
check(questionFrames[0].payload?.request_id === "que_1", "the hold carries the request id the reply route needs")

const forwardedQuestions = questionFrames[0].payload.properties.questions
check(forwardedQuestions.length === 2, `both questions are forwarded (got ${forwardedQuestions.length})`)
check(forwardedQuestions[0].options[0].label === "A" && forwardedQuestions[0].options[0].description === "甲",
  `option annotations ride along, same shape as Claude (got ${JSON.stringify(forwardedQuestions[0].options[0])})`)
check(forwardedQuestions[1].multiple === true, "the per-question multiple flag is forwarded")

check(lowLevelCalls.at(-1)?.url === "/question/{requestID}/reply" &&
  lowLevelCalls.at(-1)?.path?.requestID === "que_1",
  `the answer is handed back through the client's own request wrapper (got ${JSON.stringify(lowLevelCalls.at(-1))})`)
check(JSON.stringify(lowLevelCalls.at(-1)?.body?.answers) === '[["A"],["C","E"]]',
  `the per-question / per-selection arrays pass through untouched (got ${JSON.stringify(lowLevelCalls.at(-1)?.body)})`)

// 5d. 一层接口都没有时，把可用键报回桥（不猜；真机踩过"点了没反应、却查不到任何痕迹"的亏）
const beforeNoInterface = received.length
const bare = await AgentPagerOpenCode({ client: { postSessionIdPermissionsPermissionId: async () => {} } })
await bare.event({ event: { type: "question.asked", properties: { sessionID: "ses_bare", id: "que_bare", questions: [] } } })
await flush()
const bareNote = received.slice(beforeNoInterface).map((i) => i.payload).find((p) => p?.event === "plugin.note")
check(typeof bareNote?.properties?.note === "string" && bareNote.properties.note.includes("作答接口"),
  `a missing reply interface is reported back through the bridge (got ${JSON.stringify(bareNote?.properties?.note)})`)

// 6. 子会话的询问不上报、也不挂起
const beforeChild = holds().length
await emit("session.created", { info: { id: "ses_child3", parentID: "ses_t", title: "x (@x subagent)" } })
await emit("permission.asked", { sessionID: "ses_child3", id: "per_child", permission: "bash" })
await emit("question.asked", { sessionID: "ses_child3", id: "que_child", questions: [{ question: "?" }] })
await flush()
check(holds().length === beforeChild, "child session permissions are never held")
check(!events("question.asked").some((i) => i.payload.properties.sessionID === "ses_child3"),
  "child session questions are never forwarded")

// 7. dispose 只列自有会话
await plugin.dispose()
await flush()
check(events("plugin.dispose").some((item) => item.payload.properties.sessionID === "ses_t"), "dispose lists own session")

server.close()
rmSync(copy, { force: true })
console.log()
console.log(failures.length === 0 ? "ALL CHECKS PASSED" : `FAILURES: ${failures.join(", ")}`)
process.exit(failures.length === 0 ? 0 : 1)
