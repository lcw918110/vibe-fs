import assert from 'node:assert/strict'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { setTimeout as delay } from 'node:timers/promises'
import { ProcessHost } from '../../../verification-system/tests/e2e/support/process-host.js'
import { initGitWorkspace } from '../../../verification-system/tests/e2e/support/process-host-utils.js'
import { buildTextChunks, buildToolCallsChunks, sendJSON, sendSSE } from '../../../verification-system/tests/e2e/support/strict-mock-sse.js'
import { startHttpServer, stopHttpServer, readRequestBody } from '../../../verification-system/tests/e2e/support/strict-mock-server.js'

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../../..')
const plugin = path.join(repoRoot, 'dist/OpenCode/Plugin/Plugin.js')
const scenario = fs.mkdtempSync(path.join(os.tmpdir(), 'wxs-pre-provider-join-'))
const host = new ProcessHost()
let managerRequests = 0
let childRequests = 0
let providerRequests = 0
let joinResult = null
let session
const routingSource = `export const routingProtocol = 2
export const hasTheoreticalCapacity = (role) => role !== 'engineer'
export default function route(role) {
  return role === 'engineer' ? null : { model: 'test/test-model', reasoning: 'none' }
}
`
const provider = await startHttpServer(async (request, response) => {
  if (request.method === 'GET') return sendJSON(response, 200, { data: [{ id: 'test-model', object: 'model' }] })
  const body = await readRequestBody(request)
  const requestNumber = ++providerRequests
  const names = (body.tools ?? []).map(tool => tool.function?.name ?? tool.name)
  if (names.includes('todowrite-for-engineer')) childRequests++
  const call = (name, args) => sendSSE(response, buildToolCallsChunks(`join-smoke-${requestNumber}`, [{ name, argsStr: JSON.stringify(args) }], 10))
  if (names.includes('fork')) {
    managerRequests++
    if (managerRequests === 1) return call('fork', { calling: 'engineer', name: 'Worker', charge: 'Do not modify files. Return one short report.' })
    if (managerRequests === 2) return call('join', {})
    joinResult = (body.messages ?? []).findLast(message => message.role === 'tool')
  }
  sendSSE(response, buildTextChunks(`join-smoke-text-${requestNumber}`, 'The isolated request was observed.', 10))
})
const api = async (method, pathname, body) => {
  const response = await fetch(host.baseUrl + pathname, {
    method, headers: { 'content-type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body), signal: AbortSignal.timeout(20000),
  })
  const text = await response.text()
  assert.ok(response.ok, `${method} ${pathname}: ${response.status} ${text}`)
  return text ? JSON.parse(text) : null
}
try {
  fs.mkdirSync(path.join(scenario, 'workspace'))
  fs.mkdirSync(path.join(scenario, 'xdg/config/opencode'), { recursive: true })
  fs.writeFileSync(path.join(scenario, 'xdg/config/opencode/opencode.jsonc'), JSON.stringify({ plugin: [plugin] }))
  await initGitWorkspace(path.join(scenario, 'workspace'))
  await host.start({ scenarioDir: scenario, providerUrl: `${provider.url}/v1`, pluginPaths: [plugin], routingSource })
  const created = await api('POST', '/api/session', { agent: 'manager', model: { providerID: 'test', id: 'test-model' } })
  session = created?.id ?? created?.data?.id ?? created?.data?.data?.id
  assert.ok(session)
  await api('POST', `/session/${session}/prompt_async`, {
    messageID: 'msg_pre_provider_join', agent: 'manager', model: { providerID: 'test', modelID: 'test-model' },
    parts: [{ type: 'text', text: 'Delegate once, then join the exact result. Do not modify files.' }],
  })
  const deadline = Date.now() + 45000
  while (!joinResult && Date.now() < deadline) await delay(50)
  assert.equal(childRequests, 0, 'rejected admission must never call the child provider')
  assert.ok(joinResult, `parent join did not return; manager requests=${managerRequests}`)
  assert.match(JSON.stringify(joinResult), /ERROR|failed|Failed|capacity|Capacity|never started/i)
  const eventsDirectory = path.join(scenario, 'workspace/.Herdchestra/wanxiangshu/events')
  const facts = fs.readdirSync(eventsDirectory).filter(name => name.endsWith('.ndjson')).flatMap(name =>
    fs.readFileSync(path.join(eventsDirectory, name), 'utf8').trim().split('\n').filter(Boolean).map(line => JSON.parse(line).payload?.Fact),
  ).filter(Boolean)
  const completions = facts.filter(fact => fact[0] === 'Agent' && fact[1]?.[0] === 'Execution' && fact[1][1]?.[0] === 'HandleWorkCompleted')
  const consumptions = facts.filter(fact => fact[0] === 'Agent' && fact[1]?.[0] === 'Execution' && fact[1][1]?.[0] === 'HandleWorkConsumed')
  assert.equal(completions.length, 1, 'one failed admission must produce exactly one durable completion')
  assert.equal(consumptions.length, 1, 'the real parent join must consume that completion')
  console.log(JSON.stringify({ managerRequests, childRequests, durableCompletions: completions.length, durableConsumptions: consumptions.length, joinResult }))
} catch (error) {
  console.error(JSON.stringify({ scenario, managerRequests, childRequests, hostStderr: host.stderrLog.slice(-8000) }))
  throw error
} finally {
  try { if (session && host.baseUrl) await api('POST', `/session/${session}/abort`, {}) } catch {}
  await host.stop()
  await stopHttpServer(provider.server)
  fs.rmSync(scenario, { recursive: true, force: true })
}
