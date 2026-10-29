// Real OpenCode host repro: an orchestrator commissions a Manager child; the child's first
// provider request stalls past the latency threshold. Expect: watchdog interrupt, then the
// continuation prompt reaches the provider for the same child session.
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { ProcessHost } from '../../../verification-system/tests/e2e/support/process-host.js'
import { initGitWorkspace } from '../../../verification-system/tests/e2e/support/process-host-utils.js'
import { resolvePluginPath } from '../../../verification-system/tests/e2e/support/scenario-paths.js'
import { startHttpServer, stopHttpServer, readRequestBody } from '../../../verification-system/tests/e2e/support/strict-mock-server.js'
import { buildTextChunks, buildToolCallChunks, sendJSON, sendSSE } from '../../../verification-system/tests/e2e/support/strict-mock-sse.js'

const CONT = 'took far longer than this session'
const MODE = process.env.REPRO_MODE ?? 'stream'
let engineerCount = 0
let childCount = 0
const WARMUP = Number(process.env.REPRO_WARMUP ?? 0)
const NEED = Number(process.env.REPRO_CONTINUATIONS ?? 1)
const sleep = ms => new Promise(r => setTimeout(r, ms))
const textOf = body => (body?.messages ?? []).map(m => typeof m.content === 'string' ? m.content
  : Array.isArray(m.content) ? m.content.map(i => i?.text ?? '').join('\n') : '').join('\n---\n')
const lastUser = body => { const u = (body?.messages ?? []).filter(m => m.role === 'user').at(-1); return u ? textOf({ messages: [u] }) : '' }
const sys = body => textOf({ messages: (body?.messages ?? []).filter(m => m.role === 'system') })

const log = []
const t0 = Date.now()
const note = (kind, extra) => { const r = { t: Date.now() - t0, kind, ...extra }; log.push(r); if (log.length < 40 || kind !== 'provider-request') console.error(JSON.stringify(r).slice(0, 700)) }
let n = 0
const held = []
const provider = await startHttpServer(async (req, res) => {
  const url = new URL(req.url, `http://${req.headers.host}`)
  if (req.method === 'GET' && /models$/.test(url.pathname)) return sendJSON(res, 200, { object: 'list', data: [{ id: 'test-model', object: 'model' }, { id: 'test-model-b', object: 'model' }] })
  if (req.method !== 'POST') return sendJSON(res, 404, {})
  const body = await readRequestBody(req)
  n += 1
  fs.appendFileSync('/tmp/degen-reqs.jsonl', JSON.stringify(body) + '\n')
  const s = sys(body), u = lastUser(body), all = textOf(body)
  const role = /orchestrator/i.test(s.slice(0, 4000)) && !/You are the Manager|manager/i.test(s.slice(0, 300)) ? 'orch?' : 'other'
  note('provider-request', { n, role, tools: (body.tools ?? []).map(t => t.function?.name).slice(0, 40), lastUser: u.slice(0, 200), sysHead: s.slice(0, 160) })
  const conts = all.split(CONT).length - 1
  if (conts > 0 && !(body.tools ?? []).some(t => t.function?.name === 'todowrite-for-orchestrator')) {
    note('continuation-reached-provider', { n, conts })
    if (conts >= NEED) return sendSSE(res, buildTextChunks(`r${n}`, 'CONTINUED_OK', 1))
  }
  const toolNames = (body.tools ?? []).map(t => t.function?.name)
  if (MODE === 'slowtool' && !toolNames.includes('todowrite-for-orchestrator') && toolNames.includes('negotiate')) {
    if (conts > 0) { note('manager-continued', { n, conts }); return sendSSE(res, buildTextChunks(`r${n}`, 'MANAGER_CONTINUED', 1)) }
    childCount += 1
    if (childCount <= WARMUP) return sendSSE(res, buildToolCallChunks(`r${n}`, 'negotiate', JSON.stringify({ questions: 'warm ' + childCount }), 1))
    if (!JSON.stringify(body.messages).includes('SLOW-TOOL')) { note('manager-slow-tool', { n, childCount }); return sendSSE(res, buildToolCallChunks(`r${n}`, 'negotiate', JSON.stringify({ questions: 'SLOW-TOOL: which file?' }), 1)) }
    note('manager-after-slow-tool', { n, lastUser: u.slice(0, 200) })
    return sendSSE(res, buildTextChunks(`r${n}`, 'MANAGER_IDLE', 1))
  }
  if (MODE === 'join' && !toolNames.includes('todowrite-for-orchestrator')) {
    if (toolNames.includes('todowrite-for-manager')) {
      if (conts > 0) { note('manager-continued', { n, conts }); return sendSSE(res, buildTextChunks(`r${n}`, 'MANAGER_CONTINUED', 1)) }
      if (!JSON.stringify(body.messages).includes('fork-degeneration')) {
        const program = 'class Js extends JsProgram { async run() { return await this.fork({"calling":"engineer","name":"Proof Writer","charge":"Write proof.txt."}, []); } }'
        note('manager-forks', { n })
        return sendSSE(res, buildToolCallChunks(`r${n}`, 'todowrite-for-manager', JSON.stringify({ name: 'fork-degeneration', intent: 'Fork one engineer.', program }), 1))
      }
      note('manager-joins', { n, lastUser: u.slice(0, 160) })
      return sendSSE(res, buildToolCallChunks(`r${n}`, 'join', '{}', 1))
    }
    engineerCount += 1
    note('engineer-request', { n, engineerCount, lastUser: u.slice(0, 160) })
    await sleep(15000)
    return sendSSE(res, buildTextChunks(`r${n}`, engineerCount >= 3 ? 'ENGINEER_DONE' : 'working', 1))
  }
  if (MODE === 'negotiate') {
    if (!toolNames.includes('todowrite-for-orchestrator') && !all.includes('Q-DEGEN')) {
      note('child-negotiates', { n })
      return sendSSE(res, buildToolCallChunks(`r${n}`, 'negotiate', JSON.stringify({ questions: 'Q-DEGEN: which file name?' }), 1))
    }
    if (toolNames.includes('todowrite-for-orchestrator') && u.includes('Q-DEGEN')) {
      note('parent-answer-delayed', { n })
      await sleep(Number(process.env.REPRO_ANSWER_DELAY_MS ?? 40000))
      note('parent-answers', { n })
      return sendSSE(res, buildTextChunks(`r${n}`, 'Use proof.txt.', 1))
    }
    if (!toolNames.includes('todowrite-for-orchestrator')) { note('child-after-negotiate', { n, lastUser: u.slice(0, 300) }); return sendSSE(res, buildTextChunks(`r${n}`, 'CHILD_DONE', 1)) }
  }
  if (toolNames.includes('todowrite-for-orchestrator') && !all.includes('commission-degeneration')) {
    const program = 'class Js extends JsProgram { async run() { return await this.commission({"calling":"lead","name":"Degeneration Road","charge":"Write proof.txt."}, []); } }'
    return sendSSE(res, buildToolCallChunks(`r${n}`, 'todowrite-for-orchestrator', JSON.stringify({ name: 'commission-degeneration', intent: 'Commission one road and wait for it.', program }), 1))
  }
  if (toolNames.includes('todowrite-for-orchestrator')) return sendSSE(res, buildToolCallChunks(`r${n}`, 'join', '{}', 1))
  // Any other session (the managed child): stall without completing.
  note('holding', { n })
  res.writeHead(200, { 'content-type': 'text/event-stream' })
  res.write(`data: ${JSON.stringify({ id: `r${n}`, object: 'chat.completion.chunk', created: 1, model: 'test-model', choices: [{ index: 0, delta: { role: 'assistant', content: 'thinking...' }, finish_reason: null }] })}\n\n`)
  held.push(res)
  req.on('close', () => note('held-closed', { n }))
})

const scenarioDir = fs.mkdtempSync(path.join(os.tmpdir(), 'wxs-degeneration-real-'))
fs.mkdirSync(path.join(scenarioDir, 'workspace'), { recursive: true })
await initGitWorkspace(path.join(scenarioDir, 'workspace'))
const host = new ProcessHost()
let root
const api = async (method, p, body) => {
  const r = await fetch(host.baseUrl + p, { method, headers: { 'content-type': 'application/json' }, body: body === undefined ? undefined : JSON.stringify(body) })
  const t = await r.text(); try { return JSON.parse(t) } catch { return t }
}
try {
  await host.start({
    scenarioDir, providerUrl: `${provider.url}/v1`, pluginPaths: [MODE === 'slowtool' ? new URL('./slow-tool-plugin.mjs', import.meta.url).pathname : resolvePluginPath('opencode')],
    extraEnv: { WANXIANGSHU_DIAG: '1', WANXIANGSHU_PROVIDER_LANGUAGE: 'en', WXS_SLOW_TOOL_PLUGIN: resolvePluginPath('opencode'), WXS_SLOW_TOOL_TRACE: '/tmp/slow-tool-trace.jsonl', WXS_SLOW_TOOL_HONOR_ABORT: process.env.WXS_SLOW_TOOL_HONOR_ABORT ?? '1', WXS_SLOW_TOOL_MS: process.env.WXS_SLOW_TOOL_MS ?? '40000' },
    routingSource: `export const routingProtocol = 2
export const hasTheoreticalCapacity = () => true
const LIMIT_B = ${Number(process.env.REPRO_LIMIT_B ?? 1)}
export default function route(role, running) {
  if (role === 'orchestrator') return { model: 'test/test-model', reasoning: 'none' }
  const busy = running.filter((item) => item.model === 'test/test-model-b').length
  return busy < LIMIT_B ? { model: 'test/test-model-b', reasoning: 'none' } : null
}
`,
  })
  ;(async () => {
    const r = await fetch(host.baseUrl + '/event')
    const reader = r.body.getReader(); let buf = ''
    for (;;) { const { value, done } = await reader.read(); if (done) break; buf += Buffer.from(value).toString()
      let i; while ((i = buf.indexOf('\n\n')) >= 0) { const chunk = buf.slice(0, i); buf = buf.slice(i + 2)
        const line = chunk.split('\n').find(l => l.startsWith('data:')); if (!line) continue
        try { const e = JSON.parse(line.slice(5)); fs.appendFileSync('/tmp/degen-events.jsonl', JSON.stringify({ t: Date.now() - t0, e }) + '\n') } catch {} } }
  })().catch(() => {})
  const created = await api('POST', '/session', { agent: 'orchestrator', title: 'degeneration repro' })
  root = created?.data?.data?.id ?? created?.data?.id ?? created?.id
  note('root', { root })
  await api('POST', `/session/${root}/prompt_async`, { agent: 'orchestrator', model: { providerID: 'test', modelID: 'test-model' }, parts: [{ type: 'text', text: 'Please commission the degeneration road.' }] })
  const deadline = Date.now() + Number(process.env.REPRO_MS ?? 90000)
  while (Date.now() < deadline && !log.some(r => r.kind === 'continuation-reached-provider' && r.conts >= NEED && MODE !== 'join') && !log.some(r => r.kind === 'manager-continued')) await sleep(250)
  const diag = (host.stderrLog + host.stdoutLog).split('\n').filter(l => /latency-degeneration|session-attempt-interrupt|session-abort|slow-tool|continuation/i.test(l))
  fs.writeFileSync('/tmp/degeneration-real-host.log', host.stderrLog + '\n=====STDOUT=====\n' + host.stdoutLog)
  console.log(JSON.stringify({ continued: log.some(r => r.kind === 'continuation-reached-provider' && r.conts >= NEED), providerRequests: n, diag: diag.slice(0, 80) }, null, 1))
} finally {
  for (const r of held) { try { r.end() } catch {} }
  try { await host.stop() } catch {}
  await stopHttpServer(provider.server)
}
