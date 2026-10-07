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
const server = net.createServer((socket) => {
  let buffer = ""
  socket.on("data", (chunk) => {
    buffer += chunk.toString()
    let index
    while ((index = buffer.indexOf("\n")) >= 0) {
      const line = buffer.slice(0, index)
      buffer = buffer.slice(index + 1)
      if (line.trim()) received.push(JSON.parse(line))
      socket.write("\n")
    }
  })
})
await new Promise((resolve) => server.listen(TEST_PORT, "127.0.0.1", resolve))

const plugin = await AgentPagerOpenCode()
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

// 4. 新正文恢复流动（增量清除标记）后，预览正常转发
await emit("message.part.updated", { part: { id: "prt_text_new", sessionID: "ses_t", messageID: "msg_1", type: "text", text: "" } })
await emit("message.part.delta", { sessionID: "ses_t", messageID: "msg_1", partID: "prt_text_new", field: "text", delta: "新正文" })
await flush()
check(events("reply").length === 1 && events("reply")[0].payload.properties.text === "新正文", "fresh preview forwarded after text resumed")

// 5. dispose 只列自有会话
await plugin.dispose()
await flush()
check(events("plugin.dispose").some((item) => item.payload.properties.sessionID === "ses_t"), "dispose lists own session")

server.close()
rmSync(copy, { force: true })
console.log()
console.log(failures.length === 0 ? "ALL CHECKS PASSED" : `FAILURES: ${failures.join(", ")}`)
process.exit(failures.length === 0 ? 0 : 1)
