import assert from 'node:assert/strict'
import fs from 'node:fs'
import http from 'node:http'
import os from 'node:os'
import path from 'node:path'
import { execFileSync, spawnSync } from 'node:child_process'
import { ProcessHost } from '../../../verification-system/tests/e2e/support/process-host.js'
import { OPENCODE_BIN, initGitWorkspace } from '../../../verification-system/tests/e2e/support/process-host-utils.js'
import { buildTextChunks, buildToolCallChunks, sendJSON, sendSSE } from '../../../verification-system/tests/e2e/support/strict-mock-sse.js'
import { stopHttpServer } from '../../../verification-system/tests/e2e/support/strict-mock-server.js'
import { renderInterrupted } from '../../../../dist/Execution/Delegation/Fork/OpenCode/JoinSurface.js'
import { coldWorkSnapshot } from '../../../../dist/Execution/Delegation/Fork/OpenCode/ToolSurface.js'

const root = path.resolve(import.meta.dirname, '../../../..')
const capacityOne = process.argv.includes('--capacity-one')
const opencodeVersion = execFileSync(OPENCODE_BIN, ['--version'], { encoding: 'utf8' }).trim()
assert.equal(opencodeVersion, '1.18.29')
const phases = ['STREAMING', 'TOOL']
const phaseArgument = process.argv.indexOf('--phase')
const phase = phaseArgument < 0 ? undefined : process.argv[phaseArgument + 1]
if (phase === undefined) {
  const results = []
  for (const selected of phases) {
    const args = [import.meta.filename, '--phase', selected]
    if (capacityOne) args.push('--capacity-one')
    const launched = spawnSync(process.execPath, args, { encoding: 'utf8', timeout: 120000 })
    assert.equal(launched.status, 0, `${launched.error ?? ''}\n${launched.stdout}\n${launched.stderr}`)
    const result = JSON.parse(launched.stdout.trim())
    assert.equal(result.physicalCleanup, true)
    results.push(result.results[0])
  }
  process.stdout.write(`${JSON.stringify({ opencode: opencodeVersion, capacityOne, results, physicalCleanup: true })}\n`)
  process.exit(0)
}
assert.ok([...phases, 'GUIDANCE', 'GUIDANCE_STOP'].includes(phase))
const guidanceMode = phase === 'GUIDANCE' || phase === 'GUIDANCE_STOP'
const childStops = phase === 'GUIDANCE_STOP'
const observations = []
const waiters = new Set()
const releases = new Map()
const released = new Set()
const publish = (kind, value) => {
  const observation = { kind, value }
  observations.push(observation)
  for (const waiter of waiters) {
    if (kind === 'fixture.failure') waiter.reject(new Error(value.error))
    else if (waiter.matches(observation)) waiter.resolve(observation)
  }
}
const waitFor = (matches) => {
  const failure = observations.find(({ kind }) => kind === 'fixture.failure')
  if (failure) return Promise.reject(new Error(failure.value.error))
  const existing = observations.find(matches)
  if (existing) return Promise.resolve(existing)
  return new Promise((resolve, reject) => {
    const waiter = {
      matches,
      resolve: value => {
        clearTimeout(timer)
        waiters.delete(waiter)
        resolve(value)
      },
      reject: error => {
        clearTimeout(timer)
        waiters.delete(waiter)
        reject(error)
      },
    }
    const timer = setTimeout(() => waiter.reject(new Error(`Missing observation: ${matches}`)), 25000)
    waiters.add(waiter)
  })
}
const listen = async server => {
  await new Promise((resolve, reject) => {
    server.once('error', reject)
    server.listen(0, '127.0.0.1', resolve)
  })
  return `http://127.0.0.1:${server.address().port}`
}
const bodyOf = async request => {
  const chunks = []
  for await (const chunk of request) chunks.push(chunk)
  return JSON.parse(Buffer.concat(chunks).toString('utf8'))
}
const collector = http.createServer(async (request, response) => {
  if (request.method === 'GET') {
    const key = decodeURIComponent(request.url.slice('/release/'.length))
    if (released.has(key)) response.writeHead(204).end()
    else releases.set(key, response)
    return
  }
  const observation = await bodyOf(request)
  publish(observation.kind, observation.value)
  response.writeHead(204).end()
})
const collectorUrl = await listen(collector)
const release = key => {
  released.add(key)
  releases.get(key)?.writeHead(204).end()
  releases.delete(key)
}
let heldStream
let streamFinished = false
let activeResponseClosedEarly = false
let heldChild
let childFinished = false
let childSessionID
let childRequestsBeforeGuidance = 0
let managerStep = 0
let humanStep = 0
const rootID = `msg_root_${phase}`
const humanID = `msg_human_${phase}`
const handleProvider = async (request, response) => {
  if (request.method === 'GET') {
    sendJSON(response, 200, { object: 'list', data: [{ id: 'test-model', object: 'model' }] })
    return
  }
  const body = await bodyOf(request)
  const names = (body.tools ?? []).map(tool => tool.function?.name ?? tool.name)
  const messageID = request.headers['x-wxs-canary-message']
  if (guidanceMode && names.includes('todowrite-for-engineer')) {
    const text = JSON.stringify(body.messages)
    if (!text.includes('USER_INPUT_GUIDANCE_ONLY')) {
      const firstRequest = childRequestsBeforeGuidance++ === 0
      assert.ok(childRequestsBeforeGuidance <= 2, 'a request prepared after visible guidance must include it')
      childSessionID = request.headers['x-wxs-canary-session']
      let responseFinished = false
      response.writeHead(200, { 'Content-Type': 'text/event-stream', 'Cache-Control': 'no-cache', Connection: 'keep-alive' })
      const [prefix] = buildTextChunks('child', 'CHILD_PREFIX_', 1)
      response.write(`data: ${JSON.stringify(prefix)}\n\n`)
      response.once('close', () => {
        if (!responseFinished) activeResponseClosedEarly = true
      })
      const finishChildRequest = () => {
        assert.equal(response.destroyed, false, 'Manager guidance must not abort the running child')
        const suffix = { ...prefix, choices: [{ index: 0, delta: { content: 'CHILD_SUFFIX' }, finish_reason: null }] }
        response.write(`data: ${JSON.stringify(suffix)}\n\n`)
        if (childStops) {
          response.write(`data: ${JSON.stringify({ ...prefix, choices: [{ index: 0, delta: {}, finish_reason: 'stop' }] })}\n\n`)
        } else {
          for (const chunk of buildToolCallChunks('child-tool', 'todowrite-for-engineer', JSON.stringify({
            program: 'class Js extends JsProgram { async run() { return "CHILD_TOOL_RESULT"; } }', estimated_readonly_rounds: 0,
          }), 1)) response.write(`data: ${JSON.stringify(chunk)}\n\n`)
        }
        childFinished = true
        responseFinished = true
        response.end('data: [DONE]\n\n')
      }
      heldChild = finishChildRequest
      publish('child.held', { sessionID: childSessionID, messageID })
      if (!firstRequest) {
        const guidance = observations.find(({ kind, value }) =>
          kind === 'message.accepted' && value.sessionID === childSessionID && value.origin === 'BusyAgentNudge')
        assert.ok(guidance, 'only a request already preparing before Host storage may omit new guidance')
        await waitFor(({ kind, value }) =>
          kind === 'event.completed' && value.type === 'message.updated'
          && value.properties.info?.id === guidance.value.messageID && value.properties.info?.role === 'user')
        finishChildRequest()
      }
    } else {
      assert.ok(text.includes('USER_INPUT_CHILD_WORK'))
      assert.ok(text.includes('CHILD_PREFIX_CHILD_SUFFIX'))
      if (!childStops) assert.ok(text.includes('CHILD_TOOL_RESULT'))
      publish('child.guided.provider', { sessionID: request.headers['x-wxs-canary-session'], messageID })
      sendSSE(response, buildTextChunks('child-guided', 'CHILD_WORK_DONE', 1))
    }
    return
  }
  if (!names.includes('todowrite-for-manager')) {
    sendSSE(response, buildTextChunks('auxiliary', 'Auxiliary response.', 1))
    return
  }
  if (guidanceMode && messageID === rootID) {
    if (managerStep++ === 0) {
      sendSSE(response, buildToolCallChunks('fork-child', 'fork', JSON.stringify({
        calling: 'engineer', name: 'Ada', charge: 'USER_INPUT_CHILD_WORK',
      }), 1))
    } else sendSSE(response, buildToolCallChunks('join-child', 'join', '{}', 1))
    return
  }
  if (guidanceMode && messageID === humanID) {
    assert.ok(JSON.stringify(body.messages).includes('USER_INPUT_HUMAN'))
    const step = humanStep++
    if (step === 0) {
      sendSSE(response, buildToolCallChunks('guide-child', 'resume', JSON.stringify({
        name: 'Ada', charge: 'USER_INPUT_GUIDANCE_ONLY',
      }), 1))
    } else if (step === 1) sendSSE(response, buildToolCallChunks('join-guided-child', 'join', '{}', 1))
    else {
      assert.ok(JSON.stringify(body.messages).includes('Ada has returned'))
      sendSSE(response, buildTextChunks('human', 'HUMAN_ANSWER', 1))
    }
    return
  }
  if (messageID === humanID) {
    assert.ok(JSON.stringify(body.messages).includes('USER_INPUT_HUMAN'), 'the next actual provider request must contain the fresh input')
    assert.ok(JSON.stringify(body.messages).includes(phase === 'STREAMING' ? 'OLD_PREFIX_OLD_SUFFIX' : 'USER_INPUT_HELD_TOOL'), 'the old output/tool result must survive in the next request')
    publish('human.provider', { messageID })
    sendSSE(response, buildTextChunks('human', 'HUMAN_ANSWER', 1))
  } else if (messageID === rootID) {
    if (phase === 'TOOL') {
      sendSSE(response, buildToolCallChunks('held-tool', 'todowrite-for-manager', JSON.stringify({
        program: 'class Js extends JsProgram { async run() { return "USER_INPUT_HELD_TOOL"; } }',
      }), 1))
    } else {
      response.writeHead(200, { 'Content-Type': 'text/event-stream', 'Cache-Control': 'no-cache', Connection: 'keep-alive' })
      const [prefix, terminal] = buildTextChunks('old', 'OLD_PREFIX_', 1)
      response.write(`data: ${JSON.stringify(prefix)}\n\n`)
      response.once('close', () => {
        if (!streamFinished) activeResponseClosedEarly = true
      })
      heldStream = () => {
        assert.equal(response.destroyed, false, 'new user input must not close the current provider stream')
        const suffix = { ...prefix, choices: [{ index: 0, delta: { content: 'OLD_SUFFIX' }, finish_reason: null }] }
        response.write(`data: ${JSON.stringify(suffix)}\n\n`)
        response.write(`data: ${JSON.stringify(terminal)}\n\ndata: [DONE]\n\n`)
        streamFinished = true
        response.end()
      }
      publish('stream.held', { messageID })
    }
  }
}
const provider = http.createServer((request, response) => {
  handleProvider(request, response).catch(error => {
    publish('fixture.failure', { error: String(error) })
    sendJSON(response, 500, { error: String(error) })
  })
})
const providerUrl = await listen(provider)
const scenarioDir = fs.mkdtempSync(path.join(os.tmpdir(), 'wxs-user-input-'))
const workspace = path.join(scenarioDir, 'workspace')
fs.mkdirSync(workspace)
await initGitWorkspace(workspace)
const host = new ProcessHost()
let sessionID
const request = async (method, pathname, body) => {
  const response = await fetch(host.baseUrl + pathname, {
    method, headers: { 'content-type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
    signal: AbortSignal.timeout(25000),
  })
  const text = await response.text()
  assert.equal(response.ok, true, `${method} ${pathname}: HTTP ${response.status} ${text}`)
  return text ? JSON.parse(text) : null
}
const prompt = (messageID, text) => ({
  messageID, agent: 'manager', model: { providerID: 'test', modelID: 'test-model' }, parts: [{ type: 'text', text }],
})
const results = []
const waitForManagerJoin = () => new Promise((resolve, reject) => {
  const snapshot = path.join(workspace, '.Herdchestra/diagnostics/causal-waits.json')
  const observe = () => {
    if (!fs.existsSync(snapshot)) return
    // The plugin rewrites this snapshot in place, so a watch event can fire
    // while the file holds a half-written document. A partial read is not an
    // answer: wait for the next event instead of killing the canary.
    let waits
    try {
      waits = JSON.parse(fs.readFileSync(snapshot, 'utf8'))
    } catch {
      return
    }
    if (waits.active?.some(wait => wait.waitKind === 'agent-join' && JSON.stringify(wait.owner).includes(sessionID))) {
      clearTimeout(timer)
      watcher.close()
      resolve()
    }
  }
  const watcher = fs.watch(workspace, { recursive: true }, observe)
  const timer = setTimeout(() => {
    watcher.close()
    reject(new Error('Manager never entered an active Join wait'))
  }, 25000)
  observe()
})
try {
  await host.start({
    scenarioDir, providerUrl: `${providerUrl}/v1`,
    pluginPaths: [path.join(import.meta.dirname, 'user-input-canary-plugin.mjs')],
    extraEnv: { WXS_USER_INPUT_PLUGIN: path.join(root, 'dist/OpenCode/Plugin/Plugin.js'), WXS_USER_INPUT_COLLECTOR: collectorUrl },
    ...(capacityOne ? { routingSource: `export const routingProtocol = 2
export const hasTheoreticalCapacity = () => true
export default function route(role, running) {
  if (role !== 'manager') return { model: 'test/test-model-b', reasoning: 'none' }
  return running.filter(target => target.model === 'test/test-model').length < 1
    ? { model: 'test/test-model', reasoning: 'none' }
    : null
}
` } : {}),
  })
  sessionID = (await request('POST', '/session', { title: `User input ${phase}` })).id
  const initial = request('POST', `/session/${sessionID}/message`, prompt(rootID, 'USER_INPUT_ROOT'))
  initial.catch(error => publish('fixture.failure', { error: String(error) }))
  if (guidanceMode) {
    await waitFor(({ kind }) => kind === 'child.held')
    await waitForManagerJoin()
    const human = request('POST', `/session/${sessionID}/message`, prompt(humanID, 'USER_INPUT_HUMAN'))
    human.catch(error => publish('fixture.failure', { error: String(error) }))
    const join = await waitFor(({ kind, value }) => kind === 'join.result' && value.sessionID === sessionID)
    assert.equal(join.value.result, renderInterrupted('english', 'UserMessageArrived'))
    assert.equal(childFinished, false, 'the user input only releases Manager Join, not the child task')
    const guidance = await waitFor(({ kind, value }) => kind === 'message.accepted' && value.sessionID === childSessionID && value.origin === 'BusyAgentNudge')
    assert.equal(observations.filter(({ kind, value }) => kind === 'message.accepted' && value.sessionID === childSessionID && value.origin === 'AgentOwnerRoot').length, 1)
    assert.equal(observations.some(({ kind, value }) =>
      kind === 'host.abort' && [sessionID, childSessionID].includes(value.sessionID)), false)
    heldChild()
    const [answer] = await Promise.all([human, initial])
    assert.equal(answer.info.parentID, humanID)
    assert.equal(answer.info.finish, 'stop')
    assert.equal(answer.info.error, undefined)
    assert.ok(answer.parts.some(part => part.type === 'text' && part.text === 'HUMAN_ANSWER'))
    const followed = await waitFor(({ kind }) => kind === 'child.guided.provider')
    assert.equal(followed.value.sessionID, childSessionID)
    assert.equal(followed.value.messageID, guidance.value.messageID)
    await waitFor(({ kind, value }) => kind === 'event.completed' && value.type === 'message.updated'
      && value.properties.info.sessionID === childSessionID && value.properties.info.parentID === guidance.value.messageID
      && value.properties.info.time?.completed !== undefined)
    const childMessages = await request('GET', `/session/${childSessionID}/message`)
    const old = childMessages.find(row => row.info.role === 'assistant' && row.info.parentID !== guidance.value.messageID)
    assert.equal(old.info.error, undefined)
    assert.equal(old.info.finish, childStops ? 'stop' : 'tool-calls')
    assert.ok(old.parts.some(part => part.type === 'text' && part.text === 'CHILD_PREFIX_CHILD_SUFFIX'))
    const completed = childMessages.find(row => row.info.role === 'assistant' && row.info.parentID === guidance.value.messageID)
    assert.equal(completed.info.error, undefined)
    assert.ok(completed.parts.some(part => part.type === 'text' && part.text === 'CHILD_WORK_DONE'))
    assert.equal(activeResponseClosedEarly, false)
    assert.equal(observations.some(({ kind, value }) =>
      kind === 'host.abort' && [sessionID, childSessionID].includes(value.sessionID)), false)
    const works = await coldWorkSnapshot(path.join(workspace, '.Herdchestra'), sessionID)
    assert.equal(works.length, 1, 'guidance must not create another logical work unit')
    const originalRoot = observations.find(({ kind, value }) => kind === 'message.accepted' && value.sessionID === childSessionID && value.origin === 'AgentOwnerRoot')
    assert.equal(works[0].root, originalRoot.value.messageID)
    assert.equal(works[0].lifecycle, 'Retired', 'Manager Join must consume the original completed work')
    results.push({ phase, joinInterrupted: true, childInterrupted: false, childRoots: 1, guidanceInNextRequest: true, answerParent: answer.info.parentID })
  } else {
    await waitFor(({ kind }) => kind === (phase === 'STREAMING' ? 'stream.held' : 'tool.held'))
    const human = request('POST', `/session/${sessionID}/message`, prompt(humanID, 'USER_INPUT_HUMAN'))
    human.catch(error => publish('fixture.failure', { error: String(error) }))
    await waitFor(({ kind, value }) => kind === (capacityOne ? 'message.received' : 'message.accepted') && value.messageID === humanID)
    assert.equal(observations.some(({ kind }) => kind === 'host.abort'), false, 'new user input must issue no Host abort')
    assert.equal(observations.some(({ kind }) => kind === 'human.provider'), false, 'the new request must wait for the current stream/tool to finish')
    if (phase === 'STREAMING') heldStream()
    else release('tool')
    const [answer] = await Promise.all([human, initial])
    await waitFor(({ kind, value }) => kind === 'event.completed' && value.type === 'message.updated'
      && value.properties.info.id === answer.info.id && value.properties.info.time?.completed !== undefined)
    assert.equal(answer.info.parentID, humanID)
    assert.equal(answer.info.finish, 'stop')
    assert.equal(answer.info.error, undefined)
    assert.ok(answer.parts.some(part => part.type === 'text' && part.text === 'HUMAN_ANSWER'))
    const messages = await request('GET', `/session/${sessionID}/message`)
    const previous = messages.find(row => row.info.role === 'assistant' && row.info.parentID === rootID)
    assert.equal(previous.info.error, undefined, 'the old physical assistant must finish normally')
    assert.ok(previous.info.time.completed !== undefined)
    if (phase === 'STREAMING') {
      assert.ok(previous.parts.some(part => part.type === 'text' && part.text === 'OLD_PREFIX_OLD_SUFFIX'))
    } else {
      const tool = previous.parts.find(part => part.type === 'tool' && part.tool === 'todowrite-for-manager')
      assert.equal(tool.state.status, 'completed', JSON.stringify(tool))
      assert.ok(tool.state.output.includes('USER_INPUT_HELD_TOOL'))
    }
    assert.equal(activeResponseClosedEarly, false)
    assert.equal(observations.some(({ kind }) => kind === 'host.abort'), false)
    results.push({ phase, answerParent: answer.info.parentID, human: humanID, oldFinish: previous.info.finish, interrupted: false })
  }
} catch (error) {
  console.error(error)
  console.error(JSON.stringify({ observations, results }, null, 2))
  console.error(`Host stdout:\n${host.stdoutLog}\nHost stderr:\n${host.stderrLog}`)
  process.exitCode = 1
} finally {
  if (process.env.WXS_USER_INPUT_TRACE) {
    fs.writeFileSync(process.env.WXS_USER_INPUT_TRACE, JSON.stringify({ observations, results }))
  }
  release('tool')
  if (sessionID) {
    try {
      await request('POST', `/session/${sessionID}/abort`, {})
    } catch (error) {
      console.error(error)
      process.exitCode = 1
    }
  }
  try {
    await host.stop()
  } catch (error) {
    console.error(error)
    process.exitCode = 1
  }
  if (process.exitCode) {
    const logDirectory = path.join(scenarioDir, 'xdg/data/opencode/log')
    if (fs.existsSync(logDirectory)) {
      for (const name of fs.readdirSync(logDirectory).filter(name => name.endsWith('.log'))) {
        console.error(`Host log ${name}:\n${fs.readFileSync(path.join(logDirectory, name), 'utf8')}`)
      }
    }
  }
  const cleanups = await Promise.allSettled([stopHttpServer(provider), stopHttpServer(collector)])
  for (const cleanup of cleanups) {
    if (cleanup.status === 'rejected') {
      console.error(cleanup.reason)
      process.exitCode = 1
    }
  }
  fs.rmSync(scenarioDir, { recursive: true, force: true })
}
if (!process.exitCode) process.stdout.write(`${JSON.stringify({ opencode: opencodeVersion, capacityOne, results, physicalCleanup: true })}\n`)
