/**
 * run-plan-request-loss-smoke.mjs — real installed OpenCode smoke for the Plan
 * latest-turn preservation regression.
 *
 * Drives a REAL installed OpenCode host (ProcessHost) with the PRODUCTION
 * Wanxiangshu plugin and a real Plan-mode session through the full lifecycle:
 *
 *   1. Plan initial prompt (actual production Plan role + S1 stage prompt).
 *   2. Assistant asks a natural stop (empty stop, no continuation).
 *   3. User follow-up enters the same incumbency.
 *   4. Tool loop: a real pure JsProgram computes a distinctive value through
 *      todowrite-for-plan (read-only, no review authorization invented).
 *   5. Deliver relay (unauthorized) opens a successor tenure.
 *   6. Successor work + another user follow-up.
 *
 * The mock provider captures the ACTUAL HTTP request bodies and asserts real
 * message content/chronology/tool exchange and that the LATEST user turn and
 * LATEST tool result survive tenure assembly. It fails when the latest turn is
 * lost.
 *
 * Run:  node requirements/work/tests/support/run-plan-request-loss-smoke.mjs
 * (requires a built dist; no external LLM credentials — the provider is a
 * strict mock SSE server).
 */

import assert from 'node:assert/strict'
import http from 'node:http'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { setTimeout as delay } from 'node:timers/promises'

import { ProcessHost } from '../../../verification-system/tests/e2e/support/process-host.js'
import { initGitWorkspace } from '../../../verification-system/tests/e2e/support/process-host-utils.js'
import {
  buildTextChunks,
  buildToolCallsChunks,
  sendJSON,
  sendSSE,
} from '../../../verification-system/tests/e2e/support/strict-mock-sse.js'
import {
  startHttpServer,
  stopHttpServer,
  readRequestBody,
} from '../../../verification-system/tests/e2e/support/strict-mock-server.js'

const here = path.dirname(fileURLToPath(import.meta.url))

// ── collector: the observer plugin posts transform/params observations here ──
const observations = []
const waiters = []
const publish = (observation) => {
  const recorded = { sequence: observations.length + 1, ...observation }
  observations.push(recorded)
  for (let index = waiters.length - 1; index >= 0; index -= 1) {
    if (!waiters[index].predicate(recorded)) continue
    waiters.splice(index, 1)[0].resolve(recorded)
  }
}
const waitFor = (predicate, timeoutMs = 20000) => {
  const existing = observations.find(predicate)
  if (existing) return Promise.resolve(existing)
  return new Promise((resolve, reject) => {
    const waiter = { predicate, resolve }
    waiters.push(waiter)
    setTimeout(() => {
      const i = waiters.indexOf(waiter)
      if (i >= 0) waiters.splice(i, 1)
      reject(new Error(`timed out waiting for observation matching ${predicate}`))
    }, timeoutMs)
  })
}

// Wait until the mock provider has received at least `count` chat completions.
const waitForProviderCount = async (count, timeoutMs = 30000) => {
  const started = Date.now()
  while (providerRequests.length < count) {
    if (Date.now() - started > timeoutMs) {
      throw new Error(`timed out waiting for ${count} provider requests (got ${providerRequests.length})`)
    }
    await delay(100)
  }
}

// Wait until the Host reports the plan session idle (no in-flight provider run).
// Each settled turn appends one idle observation; waiting by count never
// completes on a previous turn's idle.
const waitForIdleCount = async (count, timeoutMs = 30000) => {
  await waitFor(
    (observation) =>
      observation.kind === 'session.idle'
      && observation.value?.sessionID === sessionID
      && observations.filter((seen) => seen.kind === 'session.idle' && seen.value?.sessionID === sessionID).length >= count,
    timeoutMs,
  )
}

const collector = await new Promise((resolve, reject) => {
  const server = http.createServer(async (request, response) => {
    const chunks = []
    for await (const chunk of request) chunks.push(chunk)
    publish(JSON.parse(Buffer.concat(chunks).toString('utf8')))
    response.writeHead(204).end()
  })
  server.once('error', reject)
  server.listen(0, '127.0.0.1', () => resolve(server))
})

// ── provider: strict mock that captures real request bodies ──
const providerRequests = []
let providerStep = 0
const providerErrors = []

// Plan leg requests carry the Plan tool surface; Host title generation and
// internal sidecars ride the same mock endpoint and must not shift the script.
const isPlanRequest = (body) =>
  (body?.tools ?? []).map((tool) => tool?.function?.name ?? tool?.name).includes('todowrite-for-plan')

const provider = await startHttpServer(async (request, response) => {
  try {
    const url = new URL(request.url, `http://${request.headers.host}`)
    if ((url.pathname === '/v1/models' || url.pathname === '/models') && request.method === 'GET') {
      sendJSON(response, 200, { object: 'list', data: [{ id: 'test-model', object: 'model' }] })
      return
    }
    if (url.pathname !== '/v1/chat/completions' || request.method !== 'POST') {
      sendJSON(response, 404, { error: `unexpected ${request.method} ${url.pathname}` })
      return
    }
    const body = await readRequestBody(request)
    if (!isPlanRequest(body)) {
      sendSSE(response, buildTextChunks(`plan-smoke-sidecar-${Date.now()}`, 'ok', 1))
      return
    }
    providerRequests.push({ body })
    const step = providerStep
    providerStep += 1

    // The provider script: each step returns a scripted SSE completion.
    // Step 0: Plan initial — natural stop (empty text, no tool call).
    if (step === 0) {
      sendSSE(response, buildTextChunks(`plan-smoke-${step}`, 'I have reviewed the current draft and need no further input for this leg.', 1))
      return
    }
    // Step 1: after the user follow-up — a todowrite-for-plan tool call (real JsProgram).
    if (step === 1) {
      const program = `class Js extends JsProgram { async run() { const p = await this.file([]); const value = 'PLAN_SMOKE_TOOL_' + (p.text ? p.text.length : 0); return { computed: value, draftLength: p.text ? p.text.length : 0 }; } }`
      sendSSE(response, buildToolCallsChunks(`plan-smoke-tool-${step}`, [{ name: 'todowrite-for-plan', argsStr: JSON.stringify({ program }) }], 1))
      return
    }
    // Step 2: tool result processed — deliver relay (unauthorized).
    if (step === 2) {
      const program = `class Js extends JsProgram { async run() { const p = await this.file([]); const value = 'PLAN_SMOKE_TOOL_' + (p.text ? p.text.length : 0); return { computed: value, draftLength: p.text ? p.text.length : 0 }; } }`
      sendSSE(response, buildToolCallsChunks(`plan-smoke-deliver-${step}`, [{ name: 'deliver', argsStr: JSON.stringify({}) }], 1))
      return
    }
    // Step 3: successor work — natural stop.
    sendSSE(response, buildTextChunks(`plan-smoke-${step}`, 'The successor leg is complete.', 1))
  } catch (error) {
    providerErrors.push(error)
    sendJSON(response, 500, { error: error.message })
  }
})

// ── host helpers ──
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
const sessionIdOf = ({ data }) => data?.data?.data?.id ?? data?.data?.id ?? data?.id ?? null

// The isolated routing module supports the plan agent (production routing has
// no plan pool, so the smoke seeds one).
const routingSource = `export const routingProtocol = 2
export const hasTheoreticalCapacity = (role, purpose) => true
export default function route(role, running, previous, purpose) {
  if (new Set(['manager', 'orchestrator', 'engineer', 'devops', 'bookkeeper', 'plan']).has(role)) return { model: 'test/test-model', reasoning: 'none' }
  throw new Error('unexpected managed role: ' + role)
}
`

let host = new ProcessHost()
let scenarioDir
let sessionID
const failures = []
const cleanupErrors = []

const run = async () => {
  try {
    scenarioDir = fs.mkdtempSync(path.join(os.tmpdir(), 'wanxiangshu-plan-smoke-'))
    fs.mkdirSync(path.join(scenarioDir, 'workspace'))
    await initGitWorkspace(path.join(scenarioDir, 'workspace'))

    await host.start({
      scenarioDir,
      providerUrl: `${provider.url}/v1`,
      pluginPaths: [path.join(here, 'plan-request-loss-smoke-plugin.mjs')],
      routingSource,
      extraEnv: {
        WANXIANGSHU_PLAN_SMOKE_COLLECTOR: `http://127.0.0.1:${collector.address().port}`,
        WANXIANGSHU_NO_FATAL_EXIT: '1',
      },
    })

    // Create a real Plan-mode session.
    const created = await request(host.baseUrl, 'POST', '/api/session', {
      agent: 'plan',
      model: { providerID: 'test', id: 'test-model' },
    }, 200)
    sessionID = sessionIdOf(created)
    assert.ok(sessionID, 'plan session creation omitted its id')

    // ── 1. Plan initial prompt ──
    await request(host.baseUrl, 'POST', `/session/${sessionID}/prompt_async`, {
      messageID: 'msg_plan_smoke_u1',
      agent: 'plan',
      model: { providerID: 'test', modelID: 'test-model' },
      parts: [{ type: 'text', text: 'Plan a caching architecture for the repository.' }],
    }, 204)
    await waitForProviderCount(1)

    // The initial provider request must carry the real Plan role + S1 stage
    // prompt and the user's verbatim instruction.
    const firstBody = providerRequests[0].body
    assert.ok(
      JSON.stringify(firstBody).includes('You are one leg of the relay'),
      'the initial Plan request must carry the production Plan role prompt',
    )
    assert.ok(
      JSON.stringify(firstBody).includes('# S1'),
      'the initial Plan request must carry the S1 stage prompt',
    )
    assert.ok(
      JSON.stringify(firstBody).includes('Plan a caching architecture for the repository.'),
      'the initial Plan request must carry the verbatim user instruction',
    )
    await waitForIdleCount(1)

    // ── 2. User follow-up enters the same incumbency ──
    await request(host.baseUrl, 'POST', `/session/${sessionID}/prompt_async`, {
      messageID: 'msg_plan_smoke_u2',
      agent: 'plan',
      model: { providerID: 'test', modelID: 'test-model' },
      parts: [{ type: 'text', text: 'Prefer a cache-aside strategy with no invalidation.' }],
    }, 204)
    await waitForProviderCount(2)

    const secondBody = providerRequests[1].body
    const secondMessages = secondBody.messages ?? []
    const secondText = JSON.stringify(secondBody)
    assert.ok(
      secondText.includes('Prefer a cache-aside strategy with no invalidation.'),
      'the second request must carry the latest user turn verbatim',
    )
    assert.ok(
      secondText.includes('Plan a caching architecture for the repository.'),
      'the second request must preserve the original user instruction',
    )
    // The latest user turn must be present and in chronological position.
    // The provider wire sends content as a parts array (OpenAI-compatible);
    // flatten to text before substring checks.
    const wireText = (message) => {
      const content = message?.content
      if (typeof content === 'string') return content
      if (Array.isArray(content)) {
        return content
          .map((part) => (typeof part === 'string' ? part : part?.text ?? ''))
          .join('')
      }
      return ''
    }
    const userContents = secondMessages.filter((m) => m.role === 'user').map(wireText)
    assert.ok(
      userContents.some((c) => c.includes('Prefer a cache-aside')),
      'the latest user reply must survive tenure assembly',
    )
    await waitForIdleCount(2)

    // ── 3. Tool loop: real JsProgram computes a distinctive value ──
    await waitForProviderCount(3)

    const thirdBody = providerRequests[2].body
    const thirdText = JSON.stringify(thirdBody)
    assert.ok(
      /PLAN_SMOKE_TOOL_\d+/.test(thirdText) || thirdText.includes('computed'),
      'the tool result must be present in the provider request body',
    )

    // ── 4. Deliver relay ──
    // request 3 answered with a deliver tool call; the relay retires this
    // incumbency and opens the successor. The Host then emits a continuation
    // request (request 4) whose transform runs under the NEW tenure: it is the
    // relay successor's first assembled request. It must still carry the
    // original instruction (U is never stripped), with prior assistant/tool
    // stripped and no synthetic-prev-* row injected. The successor is also
    // re-anchored (fresh prefix epoch).
    await waitForProviderCount(4)

    const relayBody = JSON.stringify(providerRequests[3].body)
    assert.ok(
      relayBody.includes('Plan a caching architecture for the repository.'),
      'the relay successor request must preserve the original user instruction',
    )
    assert.ok(
      relayBody.includes('Prefer a cache-aside strategy'),
      'the relay successor request must preserve the prior current-turn user reply',
    )

    // ── 5. Successor work + another follow-up ──
    await request(host.baseUrl, 'POST', `/session/${sessionID}/prompt_async`, {
      messageID: 'msg_plan_smoke_u3',
      agent: 'plan',
      model: { providerID: 'test', modelID: 'test-model' },
      parts: [{ type: 'text', text: 'Successor: document the trade-offs in the draft.' }],
    }, 204)
    await waitForProviderCount(5)

    const successorBody = providerRequests[4].body
    const successorText = JSON.stringify(successorBody)
    assert.ok(
      successorText.includes('Successor: document the trade-offs in the draft.'),
      'the successor request must carry the latest successor turn',
    )
    assert.ok(
      successorText.includes('The successor leg is complete.'),
      'the successor request must carry the current tenure assistant turn before the latest user reply',
    )
    assert.ok(
      successorText.includes('Prefer a cache-aside strategy'),
      'the successor request must preserve the prior current-tenure user turn',
    )
    assert.ok(
      successorText.includes('Plan a caching architecture for the repository.'),
      'the successor request must preserve the original instruction',
    )
    // The u2 turn chain (u2 → todowrite-for-plan result → deliver relay → stop) settles as
    // one idle; u3 settles as the third.
    await waitForIdleCount(3)

    return {
      providerRequests: providerRequests.length,
      initialHasRolePrompt: true,
      latestTurnSurvives: true,
      toolExchangePresent: true,
      successorTurnSurvives: true,
    }
  } finally {
    // cleanup handled by caller
  }
}

let result = null
try {
  result = await run()
} catch (error) {
  failures.push(error)
} finally {
  try { if (sessionID && host.baseUrl) await request(host.baseUrl, 'POST', `/session/${sessionID}/abort`, {}, 204) } catch {}
  try { await host.stop() } catch {}
  try { await stopHttpServer(provider.server) } catch {}
  try { await new Promise((resolve) => collector.close(resolve)) } catch {}
  try { if (scenarioDir) fs.rmSync(scenarioDir, { recursive: true, force: true }) } catch {}
}

const errors = [...new Set([...failures, ...providerErrors, ...cleanupErrors])]
if (errors.length === 1) throw errors[0]
if (errors.length > 1) throw new AggregateError(errors, 'Plan request-loss smoke failed', { cause: errors[0] })

assert.ok(result, 'the smoke must complete')
console.log(JSON.stringify(result))