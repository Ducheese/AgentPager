// AgentPager agy 钩子脚本单测（脚本 v3：**只对 ask_question 挂起**，其余一律立刻放行）
// 运行：node windows/tests/agy-hook.test.mjs
// 做法：读仓库里的脚本源码、把端口补丁到测试端口、写入临时文件后 spawn，
// 用本地 TCP 假桥驱动它的四种收场路径并断言 stdout。
// 为什么值得测：这条链的失败模式全是"卡死 agy"——2026-10-14 曾把**所有** PreToolUse
// 都攥住等回包，agy 在每一把普通工具上被卡住；边界与超时必须钉死。
import net from "node:net"
import { readFileSync, writeFileSync, rmSync } from "node:fs"
import { tmpdir } from "node:os"
import { dirname, join } from "node:path"
import { fileURLToPath } from "node:url"
import { spawn } from "node:child_process"

const TEST_PORT = 49372
const here = dirname(fileURLToPath(import.meta.url))
const source = readFileSync(join(here, "..", "src", "AgentPager.Core", "Resources", "agentpager-antigravity.js"), "utf8")
const script = join(tmpdir(), `agentpager-agy-test-${process.pid}.js`)
writeFileSync(script, source.replace("const PORT = 49361", `const PORT = ${TEST_PORT}`))

const failures = []
function check(condition, name) {
  if (condition) console.log(`PASS  ${name}`)
  else { failures.push(name); console.log(`FAIL  ${name}`) }
}

const QUESTION_ARGS = { questions: [{ question: "选哪个？", options: ["A", "B"], is_multi_select: false }] }
const questionPayload = { conversationId: "agy_test", workspacePaths: ["F:/Git/demo"], toolCall: { name: "ask_question", args: QUESTION_ARGS } }
const commandPayload = { conversationId: "agy_test", workspacePaths: ["F:/Git/demo"], toolCall: { name: "run_command", args: { CommandLine: "echo x" } } }

// 起一个只服务一条连接的假桥；onFrame 决定回什么（回 null = 直接关掉，不回不闭则挂住）
function startBridge(onFrame) {
  const frames = []
  const server = net.createServer((socket) => {
    let buffer = ""
    socket.on("data", (chunk) => {
      buffer += chunk.toString()
      let index
      while ((index = buffer.indexOf("\n")) >= 0) {
        const line = buffer.slice(0, index)
        buffer = buffer.slice(index + 1)
        if (!line.trim()) continue
        const envelope = JSON.parse(line)
        frames.push(envelope)
        onFrame(envelope, socket)
      }
    })
  })
  return new Promise((resolve) => server.listen(TEST_PORT, "127.0.0.1", () => resolve({ server, frames })))
}

function runHook(event, payload) {
  return new Promise((resolve) => {
    const child = spawn(process.execPath, [script, event], { stdio: ["pipe", "pipe", "pipe"] })
    let out = ""
    let err = ""
    const timer = setTimeout(() => child.kill(), 8000)   // 卡死即视为失败
    child.stdout.on("data", (chunk) => { out += chunk })
    child.stderr.on("data", (chunk) => { err += chunk })
    child.on("close", (code) => {
      clearTimeout(timer)
      resolve({ out: out.trim(), err: err.trim(), code })
    })
    child.stdin.end(JSON.stringify(payload))
  })
}

const close = (server) => new Promise((resolve) => server.close(resolve))

// ① 提问：桥回"带答案的拒绝"，脚本原样转给 agy（这就是手机作答的落地形态）
{
  const { server, frames } = await startBridge((_envelope, socket) => {
    socket.write(JSON.stringify({ decision: "deny", reason: "用户已在 AgentPager 手机端作答：\nQ1: 选哪个？\nA1: A" }) + "\n")
  })
  const result = await runHook("PreToolUse", questionPayload)
  check(result.out === '{"decision":"deny","reason":"用户已在 AgentPager 手机端作答：\\nQ1: 选哪个？\\nA1: A"}',
    `agy hook: question answer is passed through verbatim (got '${result.out}')`)
  check(frames.length === 1 && frames[0].hook_source === "antigravity" && frames[0].payload.toolCall.name === "ask_question",
    `agy hook: the question frame reaches the bridge (got ${JSON.stringify(frames[0]?.payload?.toolCall?.name)})`)
  await close(server)
}

// ② 提问：桥**没挂住**（没有手机连着时就是这条路径：回一个空行就关）→ 放行，终端自己弹框
{
  const { server } = await startBridge((_envelope, socket) => { socket.write("\n"); socket.end() })
  const result = await runHook("PreToolUse", questionPayload)
  check(result.out === '{"decision":"allow"}', `agy hook: bridge without a hold (blank line) falls back to allow (got '${result.out}')`)
  await close(server)
}

// ②b 提问：桥撤了等待（直接关连接不给任何字节）→ 同样放行
{
  const { server } = await startBridge((_envelope, socket) => socket.end())
  const result = await runHook("PreToolUse", questionPayload)
  check(result.out === '{"decision":"allow"}', `agy hook: closed wait falls back to allow (got '${result.out}')`)
  await close(server)
}

// ③ 提问：桥根本没起 → Fail-Open 放行，绝不阻断 agy
{
  const result = await runHook("PreToolUse", questionPayload)
  check(result.out === '{"decision":"allow"}', `agy hook: no bridge -> allow (got '${result.out}')`)
}

// ④ 非提问的 PreToolUse：**立刻**放行，且照样转发给桥（这是"卡死 agy"那条红线的回归守卫）
{
  const { server, frames } = await startBridge((_envelope, socket) => socket.write("\n"))
  const started = Date.now()
  const result = await runHook("PreToolUse", commandPayload)
  const elapsed = Date.now() - started
  check(result.out === '{"decision":"allow"}', `agy hook: ordinary tool is allowed (got '${result.out}')`)
  check(elapsed < 3000, `agy hook: ordinary tool does not wait (took ${elapsed}ms)`)
  check(frames.length === 1 && frames[0].payload.toolCall.name === "run_command",
    "agy hook: ordinary tool frame is still forwarded")
  await close(server)
}

// ⑤ 非 PreToolUse：契约是空对象
{
  const { server } = await startBridge(() => {})
  const result = await runHook("Stop", { conversationId: "agy_test" })
  check(result.out === "{}", `agy hook: non-PreToolUse event answers {} (got '${result.out}')`)
  await close(server)
}

rmSync(script, { force: true })
console.log()
if (failures.length === 0) console.log("ALL CHECKS PASSED")
else console.log(`FAILURES: ${failures.join(", ")}`)
process.exit(failures.length === 0 ? 0 : 1)
