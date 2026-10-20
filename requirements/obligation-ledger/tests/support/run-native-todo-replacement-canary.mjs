// requirements/obligation-ledger/tests/support/run-native-todo-replacement-canary.mjs
//
// Canary for WHAT[obligation-ledger-002]: on the installed OpenCode Host,
// programming-surface calls (`todowrite-for-engineer`) that declare a non-empty
// `view` argument commit one durable `ViewCheckpointCommitted` fact per session,
// while calls without `view` commit no checkpoint, and retired native `todowrite`
// is not advertised on the Engineer provider wire.

import assert from 'node:assert/strict'
import { execFileSync } from 'node:child_process'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { setTimeout as delay } from 'node:timers/promises'
import { fileURLToPath } from 'node:url'

import { ProcessHost } from '../../../verification-system/tests/e2e/support/process-host.js'
import { OPENCODE_BIN, initGitWorkspace } from '../../../verification-system/tests/e2e/support/process-host-utils.js'
import { resolvePluginPath } from '../../../verification-system/tests/e2e/support/scenario-paths.js'
import { buildTextChunks, buildToolCallChunks, sendJSON, sendSSE } from '../../../verification-system/tests/e2e/support/strict-mock-sse.js'
import { readRequestBody, startHttpServer, stopHttpServer } from '../../../verification-system/tests/e2e/support/strict-mock-server.js'

const here = path.dirname(fileURLToPath(import.meta.url))
const repoRoot = path.resolve(here, '../../../..')
const opencodeVersion = execFileSync(OPENCODE_BIN, ['--version'], { encoding: 'utf8' }).trim().replace(/^v/, '')
const pluginVersion = JSON.parse(fs.readFileSync(path.join(repoRoot, 'node_modules/@opencode-ai/plugin/package.json'), 'utf8')).version

const messageText = (body) => (body?.messages ?? [])
  .flatMap((message) => {
    if (typeof message?.content === 'string') return [message.content]
    if (Array.isArray(message?.content)) return message.content.map((item) => item?.text ?? '').filter(Boolean)
    return []
  })
  .join('\n')

const hasToolResult = (body) => (body?.messages ?? []).some((message) => message?.role === 'tool')
const isTitleRequest = (body) => {
  const messages = Array.isArray(body?.messages) ? body.messages : []
  return messages.some(
    (message) =>
      typeof message?.content === 'string' &&
      (message.content.startsWith('Generate a title for this conversation:') ||
        message.content.includes('title generator')),
  )
}

const jsViewCall = (id, view) =>
  buildToolCallChunks(
    id,
    'todowrite-for-engineer',
    JSON.stringify({
      program: 'class Js extends JsProgram { async run() { return { ok: true }; } }',
      ...(view === undefined ? {} : { view }),
    }),
    1,
  )

const providerRequests = []
const advertisedToolNames = new Set()
const provider = await startHttpServer(async (request, response) => {
  const url = new URL(request.url, `http://${request.headers.host}`)
  if ((url.pathname === '/v1/models' || url.pathname === '/models') && request.method === 'GET') {
    sendJSON(response, 200, { object: 'list', data: [{ id: 'test-model', object: 'model' }] })
    return
  }
  if (url.pathname === '/v1/chat/completions' && request.method === 'POST') {
    const body = await readRequestBody(request)
    providerRequests.push(body)
    for (const tool of body?.tools ?? []) {
      const name = tool?.function?.name ?? tool?.name
      if (typeof name === 'string') advertisedToolNames.add(name)
    }
    if (isTitleRequest(body)) {
      sendSSE(response, buildTextChunks(`title_${Date.now()}`, 'OL002 Title', 1))
      return
    }
    if (hasToolResult(body)) {
      sendSSE(response, buildTextChunks(`ol002_round_${providerRequests.length}`, 'OL002_DONE', 1))
      return
    }
    const text = messageText(body)
    if (text.includes('OL002_A_NO_VIEW')) {
      sendSSE(response, jsViewCall('ol002-a-no-view', undefined))
      return
    }
    if (text.includes('OL002_A_VIEW')) {
      sendSSE(response, jsViewCall('ol002-a-view', { prefixUntil: 100, thenRemove: {} }))
      return
    }
    if (text.includes('OL002_B_VIEW')) {
      sendSSE(response, jsViewCall('ol002-b-view', { prefixUntil: 100 }))
      return
    }
    sendJSON(response, 500, { error: `unexpected prompt text: ${text.slice(0, 200)}` })
    return
  }
  sendJSON(response, 404, { error: `unexpected ${request.method} ${url.pathname}` })
})

const request = async (baseUrl, method, pathname, body, expectedStatus) => {
  const response = await fetch(baseUrl + pathname, {
    method,
    headers: { 'content-type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  })
  const text = await response.text()
  if (expectedStatus !== undefined) assert.equal(response.status, expectedStatus, `${method} ${pathname}: ${text}`)
  return { status: response.status, data: text ? JSON.parse(text) : null }
}

const sessionIdOf = ({ data }) => data?.data?.data?.id ?? data?.data?.id ?? data?.id

const prompt = (text) => ({
  agent: 'engineer',
  model: { providerID: 'test', modelID: 'test-model' },
  parts: [{ type: 'text', text }],
})

const waitForToolResultRound = async (marker, host) => {
  const deadline = Date.now() + 30000
  const matches = () => providerRequests.some((body) => {
    const text = messageText(body)
    return text.includes(marker) && hasToolResult(body)
      && (marker !== 'OL002_A_VIEW' || !text.includes('OL002_A_NO_VIEW'))
  })
  while (Date.now() < deadline) {
    if (matches()) return
    await delay(200)
  }
  throw new Error(
    `timeout waiting for the tool-result round of ${marker};\n=== HOST STDOUT ===\n${host.stdoutLog}\n=== HOST STDERR ===\n${host.stderrLog}`,
  )
}

const readViewCheckpointsBySession = (workDir) => {
  const eventsDir = path.join(workDir, '.Herdchestra', 'wanxiangshu', 'events')
  const bySession = {}
  if (!fs.existsSync(eventsDir)) return bySession
  for (const name of fs.readdirSync(eventsDir).filter((file) => file.endsWith('.ndjson'))) {
    const lines = fs.readFileSync(path.join(eventsDir, name), 'utf8').split('\n').filter(Boolean)
    for (const line of lines) {
      try {
        const parsed = JSON.parse(line)
        const fact = parsed?.payload?.Fact ?? parsed?.fact ?? parsed?.Fact
        const findCheckpoint = (node) => {
          if (!node) return null
          if (Array.isArray(node)) {
            if (node.length >= 2 && node[0] === 'ViewCheckpointCommitted') {
              const p = node[1] ?? {}
              const sid = Array.isArray(p.SessionId) ? p.SessionId[1] : p.SessionId ?? p.sessionId
              const cid = Array.isArray(p.ToolCallId) ? p.ToolCallId[1] : p.ToolCallId ?? p.toolCallId
              return { sid, cid }
            }
            for (const child of node) {
              const found = findCheckpoint(child)
              if (found) return found
            }
          }
          return null
        }
        const checkpoint = findCheckpoint(fact)
        if (checkpoint?.sid && checkpoint?.cid) {
          bySession[checkpoint.sid] = bySession[checkpoint.sid] ?? []
          bySession[checkpoint.sid].push(checkpoint.cid)
        }
      } catch {}
    }
  }
  return bySession
}

const scenarioDir = fs.mkdtempSync(path.join(os.tmpdir(), 'wanxiangshu-native-todo-canary-'))
const workspace = path.join(scenarioDir, 'workspace')
fs.mkdirSync(workspace, { recursive: true })
await initGitWorkspace(workspace)

const host = new ProcessHost()
let sessionA
let sessionB
try {
  await host.start({
    scenarioDir,
    providerUrl: `${provider.url}/v1`,
    pluginPaths: [resolvePluginPath('opencode')],
  })

  sessionA = sessionIdOf(await request(host.baseUrl, 'POST', '/api/session', {
    agent: 'engineer',
    model: { providerID: 'test', id: 'test-model' },
  }, 200))
  sessionB = sessionIdOf(await request(host.baseUrl, 'POST', '/api/session', {
    agent: 'engineer',
    model: { providerID: 'test', id: 'test-model' },
  }, 200))
  assert.ok(sessionA && sessionB && sessionA !== sessionB, 'public session create responses omitted distinct ids')

  await request(host.baseUrl, 'POST', `/session/${sessionA}/prompt_async`, prompt('OL002_A_VIEW 为会话 A 提交带 view 的编程面调用'), 204)
  await waitForToolResultRound('OL002_A_VIEW', host)

  await request(host.baseUrl, 'POST', `/session/${sessionB}/prompt_async`, prompt('OL002_B_VIEW 为会话 B 提交带 view 的编程面调用'), 204)
  await waitForToolResultRound('OL002_B_VIEW', host)

  await request(host.baseUrl, 'POST', `/session/${sessionA}/prompt_async`, prompt('OL002_A_NO_VIEW 会话 A 无 view 调用不产生 checkpoint'), 204)
  await waitForToolResultRound('OL002_A_NO_VIEW', host)

  await delay(500)
  const checkpointsBySession = readViewCheckpointsBySession(host.workDir)

  process.stdout.write(`${JSON.stringify({
    schemaVersion: 1,
    launched: `${OPENCODE_BIN} serve --port 0 --hostname 127.0.0.1`,
    versions: { opencode: opencodeVersion, plugin: pluginVersion },
    sessions: { sessionA, sessionB },
    observed: {
      todowriteAdvertised: advertisedToolNames.has('todowrite'),
      programmingToolAdvertised: advertisedToolNames.has('todowrite-for-engineer'),
      checkpointsBySession,
    },
    hostStderrTail: host.stderrLog.slice(-4000),
    providerRequests: providerRequests.length,
  }, null, 2)}\n`)
} finally {
  if (sessionA && host.baseUrl) {
    try { await request(host.baseUrl, 'POST', `/session/${sessionA}/abort`, {}, 204) } catch {}
  }
  if (sessionB && host.baseUrl) {
    try { await request(host.baseUrl, 'POST', `/session/${sessionB}/abort`, {}, 204) } catch {}
  }
  try { await host.stop() } catch {}
  await stopHttpServer(provider.server)
  fs.rmSync(scenarioDir, { recursive: true, force: true })
}
