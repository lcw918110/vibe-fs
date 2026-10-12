import assert from 'node:assert/strict'
import { spawn } from 'node:child_process'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import test from 'node:test'
import { createWatchdogHarness } from './support/watchdog-harness.mjs'
import { registerSupervisedToolReclamationTests } from './support/supervised-tool-reclamation-tests.mjs'
import { registerNormalExitResidualTest } from './support/normal-exit-residual-tests.mjs'
import { registerOwnedToolTests } from './support/owned-tool-tests.mjs'
import { classifyVerdict } from './support/verdict-feed.mjs'
import { createWorkerCostObserver, encodeWorkerCostRecord, MAX_WORKER_COST_BYTES, validateWorkerCostSnapshot, WORKER_COST_PREFIX } from './support/worker-cost-observation.mjs'
import * as testSupervisor from './e2e/support/supervise-node-test.mjs'
import { observeCausalProgress } from './e2e/support/causal-observation.js'
import { gatherDiagnostics } from './e2e/support/diagnostics-collect.js'
import { formatDiagnostics } from './e2e/support/diagnostics-format.js'
import { StrictMockProvider } from './e2e/support/strict-mock-provider.js'
import { StrictMockSignals } from './e2e/support/strict-mock-signals.js'
import { createScenarioTurn } from './e2e/support/scenario-turn.js'
import { DIAGNOSTIC_RACE_MS, WATCHDOG_TIMEOUT_MS } from './e2e/support/time-budget.js'
import { attachEventCeilings, eventCeilingSetupProblems, isCountedSseEvent, normalizeEventCeilings } from './e2e/support/event-ceiling.js'

registerSupervisedToolReclamationTests()
registerNormalExitResidualTest()
registerOwnedToolTests()

test('WHAT[verification-system-006] event safety ceilings validate input and count actual non-heartbeat events', () => {
  assert.deepEqual(normalizeEventCeilings({}), {})
  assert.deepEqual(normalizeEventCeilings({ maxJournalEvents: 12, maxSseEvents: 34 }),
    { maxJournalEvents: 12, maxSseEvents: 34 })
  assert.throws(() => normalizeEventCeilings({ maxJournalEvents: 0 }), /positive integer/)
  assert.throws(() => normalizeEventCeilings({ maxSseEvents: 1.2 }), /positive integer/)
  assert.deepEqual(eventCeilingSetupProblems(undefined), [])
  assert.ok(eventCeilingSetupProblems({ maxJournalEvents: 0 })[0].includes('maxJournalEvents'))
  assert.ok(eventCeilingSetupProblems({ maxSseEvents: -3 })[0].includes('maxSseEvents'))
  assert.equal(isCountedSseEvent({ type: 'server.heartbeat' }), false)
  assert.equal(isCountedSseEvent({ type: 'message.updated' }), true)
  assert.equal(isCountedSseEvent({ type: '' }), false)
  assert.equal(isCountedSseEvent({}), false)
  const listeners = []
  const scenario = {
    host: { workDir: '/tmp/unused-for-sse-only' },
    events: {
      allEvents: [{ type: 'message.updated' }, { type: 'server.heartbeat' }, { type: 'sync' }],
      onEvent(callback) {
        listeners.push(callback)
        return () => listeners.splice(listeners.indexOf(callback), 1)
      },
      dump: () => '',
    },
    watchdog: { stop() {} },
  }
  let breached = null
  attachEventCeilings(scenario, { maxSseEvents: 2 }, {
    onBreach(detail) {
      breached = detail
      throw new Error('ceiling')
    },
  })
  assert.equal(breached, null)
  listeners[0]({ type: 'server.heartbeat' })
  assert.equal(breached, null)
  assert.throws(() => listeners[0]({ type: 'session.idle' }), /ceiling/)
  assert.deepEqual({ kind: breached.kind, observed: breached.observed, limit: breached.limit, sse: breached.sseEvents },
    { kind: 'maxSseEvents', observed: 3, limit: 2, sse: 3 })
})

const causalSnapshot = {
  pid: 1, sequence: '3',
  active: [{
    waitKind: 'provider-assessment',
    owner: { kind: 'RelayWorkflow', identity: [{ k: 'incumbency', v: 'I2' }] },
    subject: [{ k: 'road', v: 'road-manager.0' }],
    producer: { tag: 'external', kind: 'provider', identity: [{ k: 'run', v: 'P81' }] },
    escapes: [{ tag: 'processLifetime' }], source: 'test',
  }],
  history: [],
  frontiers: [{
    kind: 'ExternalProducerFrontier', detail: 'waiting for provider P81',
    chain: [], frontierProducer: { tag: 'external', kind: 'provider', identity: [{ k: 'run', v: 'P81' }] },
    cycle: [],
  }],
}

test('WHAT[verification-system-006] diagnostics preserve the actual wait and monitored subject', async () => {
  const workDir = fs.mkdtempSync(path.join(os.tmpdir(), 'causal-gather-'))
  try {
    const directory = path.join(workDir, '.wanxiangshu', 'diagnostics')
    fs.mkdirSync(directory, { recursive: true })
    fs.writeFileSync(path.join(directory, 'causal-waits.json'), JSON.stringify(causalSnapshot))
    const diag = await gatherDiagnostics({
      host: { workDir },
      provider: { requests: [], unexpectedRequests: [], remainingExpectations: 1,
        blockedExpectations: [{ id: 'road-manager.0', lane: 'x', blocking: true }] },
    })
    assert.deepEqual(diag.causalWaitSnapshot, causalSnapshot)
    assert.deepEqual(diag.causalExpectationCorrelation, {
      matched: ['road-manager.0'], unmatched: [], divergence: false,
    })
    const output = formatDiagnostics(diag)
    assert.match(output, /provider-assessment/)
    assert.match(output, /I2/)
    assert.match(output, /P81/)
  } finally {
    fs.rmSync(workDir, { recursive: true, force: true })
  }
})

test('WHAT[verification-system-006] missing and corrupt wait snapshots disclose the diagnostic gap', async () => {
  const workDir = fs.mkdtempSync(path.join(os.tmpdir(), 'causal-missing-'))
  try {
    let diag = await gatherDiagnostics({ host: { workDir } })
    assert.match(formatDiagnostics(diag), /current waits unavailable.*snapshot.*missing/i)
    const directory = path.join(workDir, '.wanxiangshu', 'diagnostics')
    fs.mkdirSync(directory, { recursive: true })
    fs.writeFileSync(path.join(directory, 'causal-waits.json'), '{invalid')
    diag = await gatherDiagnostics({ host: { workDir } })
    assert.match(formatDiagnostics(diag), /current waits unavailable.*invalid/i)
  } finally {
    fs.rmSync(workDir, { recursive: true, force: true })
  }
})

async function assertSingleTermination(h, timeoutMs) {
  assert.equal(h.terminateCount, 1)
  h.watchdog.advance({ reason: 'late verdict', lane: 'worker', blocking: true })
  await h.advance(Math.max(DIAGNOSTIC_RACE_MS, timeoutMs))
  assert.equal(h.terminateCount, 1, 'late progress must not restart a terminated watchdog')
  h.watchdog.setWindow(timeoutMs)
  await h.advance(timeoutMs)
  assert.equal(h.terminateCount, 1, 'window changes must not restart a terminated watchdog')
}

test('WHAT[verification-system-006] the initial silence window covers execution before its first progress', async () => {
  const h = createWatchdogHarness({ timeoutMs: WATCHDOG_TIMEOUT_MS })
  await h.advance(WATCHDOG_TIMEOUT_MS - 1)
  assert.equal(h.terminated, false)
  assert.deepEqual(h.diagnostics, [])
  await h.advance(1)
  assert.equal(h.terminateCount, 1)
  assert.match(h.diagnostics.join('\n'), /test-target/)
  assert.match(h.diagnostics.join('\n'), /last progress/)
  assert.match(h.diagnostics.join('\n'), /current waits unavailable/i)
  assert.equal(h.trace.at(-1), 'terminate')
  await assertSingleTermination(h, WATCHDOG_TIMEOUT_MS)
})

test('WHAT[verification-system-006] continuing progress outlives a window and silence is measured from its last advance', async () => {
  const h = createWatchdogHarness({ timeoutMs: 300 })
  for (const reason of ['first verdict', 'second verdict', 'third verdict']) {
    await h.advance(200)
    assert.equal(h.terminated, false)
    h.watchdog.advance({ reason, lane: 'worker', blocking: true })
  }
  await h.advance(299)
  assert.equal(h.terminated, false)
  await h.advance(1)
  assert.equal(h.terminateCount, 1)
  assert.match(h.diagnostics.join('\n'), /third verdict.*worker/)
})

test('WHAT[verification-system-006] completed verdicts renew the window while repeated log traffic cannot', async () => {
  for (const type of ['test:pass', 'test:fail', 'test:complete']) {
    const h = createWatchdogHarness()
    await h.advance(200)
    h.watchdog.advance(classifyVerdict({ type, data: { name: 'completed case', file: 'example.mjs' } }))
    await h.advance(499)
    assert.equal(h.terminated, false, type)
    await h.advance(1)
    assert.equal(h.terminated, true, type)
  }
  for (const type of ['test:stdout', 'test:stderr', 'test:diagnostic']) {
    const h = createWatchdogHarness()
    for (let index = 0; index < 4; index++) {
      await h.advance(100)
      h.watchdog.advance(classifyVerdict({ type, data: { file: 'stuck.mjs' } }))
    }
    await h.advance(100)
    assert.equal(h.terminated, true, `${type} must not keep a stuck test alive`)
    assert.match(h.diagnostics.join('\n'), /0 blocking progress/)
  }
})

test('WHAT[verification-system-006] scheduling events do not renew the silence window', async () => {
  const h = createWatchdogHarness()
  await h.advance(400)
  for (const type of ['test:enqueue', 'test:dequeue', 'test:start', 'test:plan',
    'runner:file-start', 'runner:file-drained', 'inner:drained', 'runner:error']) {
    const progress = classifyVerdict({ type, data: { file: 'x.mjs' } })
    if (progress) h.watchdog.advance(progress)
  }
  await h.advance(100)
  assert.equal(h.terminated, true)
})

test('WHAT[verification-system-006] the real verdict transport records body entry with its actual parent test and process identity', async () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'verdict-parent-identity-'))
  try {
    const fixture = path.join(directory, 'parent.test.mjs')
    fs.writeFileSync(fixture, `import test from 'node:test'
test('actual parent', async t => { await t.test('actual child', () => {}) })
`)
    const transport = fileURLToPath(new URL('./support/verdict-transport.mjs', import.meta.url))
    const env = { ...process.env }
    delete env.NODE_TEST_CONTEXT
    const child = spawn(process.execPath, [`--import=${transport}`, '--test', fixture], { env, stdio: ['ignore', 'pipe', 'pipe'] })
    let output = ''
    child.stdout.on('data', chunk => { output += chunk })
    child.stderr.on('data', chunk => { output += chunk })
    const code = await new Promise((resolveExit, reject) => {
      child.once('error', reject)
      child.once('close', resolveExit)
    })
    assert.equal(code, 0, output)
    const facts = output.split('\n').filter(line => line.includes('[verification-test-start] '))
      .map(line => JSON.parse(line.slice(line.indexOf('[verification-test-start] ') + '[verification-test-start] '.length)))
    const entry = facts.find(fact => fact.name === 'actual child')
    assert.ok(entry, output)
    assert.equal(entry.fullName, 'actual parent > actual child')
    assert.equal(fs.realpathSync(entry.entryFile), fs.realpathSync(fixture))
    assert.ok(Number.isInteger(entry.pid) && Number.isInteger(entry.parentPid))
    assert.notEqual(entry.pid, child.pid, 'the fact comes from the actual file process, not its CLI parent')
    assert.equal(entry.parentPid, child.pid)
  } finally {
    fs.rmSync(directory, { recursive: true, force: true })
  }
})

test('WHAT[verification-system-006] worker cost observations identify two actual file processes and leave an unobserved exit missing', async () => {
  const directory = fs.realpathSync(fs.mkdtempSync(path.join(os.tmpdir(), 'worker-cost-identity-')))
  try {
    const files = ['first', 'second'].map(name => path.join(directory, `${name}.test.mjs`))
    const markers = files.map(file => `${file}.body.json`)
    for (const [index, file] of files.entries()) {
      fs.writeFileSync(file, `import assert from 'node:assert/strict'
import fs from 'node:fs'
import test from 'node:test'
test('actual CPU work ${index}', () => {
  let sum = 0
  for (let value = 0; value < 100000; value++) sum += value
  assert.equal(sum, 4999950000)
  fs.writeFileSync(${JSON.stringify(markers[index])}, JSON.stringify({
    pid: process.pid, parentPid: process.ppid, monotonicMs: performance.now(), cpu: process.cpuUsage(),
  }))
})
`)
    }
    const launcher = path.join(directory, 'supervise.mjs')
    const moduleUrl = new URL('./e2e/support/supervise-node-test.mjs', import.meta.url).href
    fs.writeFileSync(launcher, `import { superviseNodeTest } from ${JSON.stringify(moduleUrl)}
await superviseNodeTest({ files: process.argv.slice(2), label: 'worker-cost-identity', silenceMs: 5000 })
`)
    const env = { ...process.env, NODE_TEST_CONCURRENCY: '2' }
    delete env.NODE_TEST_CONTEXT
    delete env.WXS_VERIFICATION_WORKER_DIAGNOSTICS
    const child = spawn(process.execPath, [launcher, ...files], { env, stdio: ['ignore', 'pipe', 'pipe'] })
    let output = ''
    child.stdout.on('data', chunk => { output += chunk })
    child.stderr.on('data', chunk => { output += chunk })
    const code = await new Promise((resolveExit, reject) => {
      child.once('error', reject)
      child.once('close', resolveExit)
    })
    assert.equal(code, 0, output)
    assert.match(output, /2 passed, 0 failed; 2\/2 planned file\(s\) completed/)
    assert.match(output, /post-exit group verification\/reclamation:.*accepted=true/)
    const rows = output.split('\n').filter(line => line.includes(WORKER_COST_PREFIX))
      .map(line => line.slice(line.indexOf(WORKER_COST_PREFIX)))
    for (const row of rows) assert.ok(Buffer.byteLength(row + '\n') <= MAX_WORKER_COST_BYTES)
    const facts = rows.map(line => JSON.parse(line.slice(WORKER_COST_PREFIX.length)))
    const actualPids = new Set()
    for (const [index, file] of files.entries()) {
      const body = JSON.parse(fs.readFileSync(markers[index], 'utf8'))
      actualPids.add(body.pid)
      assert.throws(() => process.kill(body.pid, 0), { code: 'ESRCH' })
      const observations = facts.filter(fact => fact.entryFile === file)
      const starts = observations.filter(fact => fact.phase === 'pre-import')
      assert.equal(starts.length, 1, output)
      const initial = starts[0]
      assert.equal(initial.version, 1)
      assert.equal(initial.sequence, 0)
      assert.equal(initial.pid, body.pid)
      assert.equal(initial.parentPid, body.parentPid)
      assert.ok(initial.monotonicMs <= body.monotonicMs)
      assert.ok(initial.cpuUserMicros <= body.cpu.user)
      assert.ok(initial.cpuSystemMicros <= body.cpu.system)
      const exits = observations.filter(fact => fact.phase === 'exit')
      assert.ok(exits.length <= 1, output)
      if (exits.length === 0) continue
      const terminal = exits[0]
      assert.equal(terminal.sequence, 1)
      assert.equal(terminal.pid, body.pid)
      assert.equal(terminal.parentPid, body.parentPid)
      assert.equal(terminal.exitCode, 0)
      assert.ok(terminal.monotonicMs >= body.monotonicMs)
      assert.ok(terminal.cpuUserMicros >= body.cpu.user)
      assert.ok(terminal.cpuSystemMicros >= body.cpu.system)
    }
    assert.equal(actualPids.size, 2, 'Each native sample belongs to its own actual file process')
  } finally {
    fs.rmSync(directory, { recursive: true, force: true })
  }
})

test('WHAT[verification-system-006] worker cost diagnostics preserve actual success and assertion failure with a closed exit sink', async () => {
  const directory = fs.realpathSync(fs.mkdtempSync(path.join(os.tmpdir(), 'worker-cost-sink-')))
  try {
    const fixture = path.join(directory, 'native.test.mjs')
    const launcher = path.join(directory, 'supervise.mjs')
    const moduleUrl = new URL('./e2e/support/supervise-node-test.mjs', import.meta.url).href
    fs.writeFileSync(fixture, `import assert from 'node:assert/strict'
import fs from 'node:fs'
import test from 'node:test'
process.on('uncaughtExceptionMonitor', error => fs.writeFileSync(process.env.WORKER_ERROR_MARKER, JSON.stringify({ code: error.code, message: error.message })))
test('original worker result', () => {
  fs.writeFileSync(process.env.WORKER_BODY_MARKER, JSON.stringify({ pid: process.pid, parentPid: process.ppid, home: process.env.HOME }))
  process.prependOnceListener('exit', code => {
    if (process.env.WORKER_EXIT_SINK === 'closed') fs.closeSync(1)
    fs.writeFileSync(process.env.WORKER_EXIT_MARKER, JSON.stringify({ pid: process.pid, code, sink: process.env.WORKER_EXIT_SINK }))
  })
  if (process.env.WORKER_OUTCOME === 'fail') assert.fail('original worker assertion failure')
})
`)
    fs.writeFileSync(launcher, `import { superviseNodeTest } from ${JSON.stringify(moduleUrl)}
await superviseNodeTest({ files: [${JSON.stringify(fixture)}], label: 'worker-cost-sink', silenceMs: 5000 })
`)
    for (const outcome of ['pass', 'fail']) {
      for (const sink of ['open', 'closed']) {
        for (const enabled of ['0', '1']) {
          const prefix = path.join(directory, `${outcome}-${sink}-${enabled}`)
          const env = { ...process.env, WXS_VERIFICATION_WORKER_DIAGNOSTICS: enabled,
            WORKER_OUTCOME: outcome, WORKER_EXIT_SINK: sink, WORKER_BODY_MARKER: `${prefix}.body`,
            WORKER_EXIT_MARKER: `${prefix}.exit`, WORKER_ERROR_MARKER: `${prefix}.error` }
          delete env.NODE_TEST_CONTEXT
          const child = spawn(process.execPath, [launcher], { env, stdio: ['ignore', 'pipe', 'pipe'] })
          let output = ''
          child.stdout.on('data', chunk => { output += chunk })
          child.stderr.on('data', chunk => { output += chunk })
          const code = await new Promise((resolveExit, reject) => {
            child.once('error', reject)
            child.once('close', resolveExit)
          })
          assert.equal(code, outcome === 'pass' ? 0 : 1, output)
          assert.match(output, outcome === 'pass'
            ? /1 passed, 0 failed; 1\/1 planned file\(s\) completed/
            : /0 passed, 1 failed; 1\/1 planned file\(s\) completed/)
          if (outcome === 'fail') assert.match(output, /original worker assertion failure/)
          assert.match(output, /post-exit group verification\/reclamation:.*accepted=true/)
          assert.doesNotMatch(output, /EPIPE|EBADF|Unhandled 'error'|uncaughtException|WATCHDOG/)
          assert.equal(fs.existsSync(`${prefix}.error`), false, output)
          const body = JSON.parse(fs.readFileSync(`${prefix}.body`, 'utf8'))
          const exit = JSON.parse(fs.readFileSync(`${prefix}.exit`, 'utf8'))
          assert.deepEqual(exit, { pid: body.pid, code: outcome === 'pass' ? 0 : 1, sink })
          assert.throws(() => process.kill(body.pid, 0), { code: 'ESRCH' })
          assert.equal(fs.existsSync(body.home), false, 'The real runner owned HOME is reclaimed')
          const snapshots = output.split('\n').filter(line => line.startsWith('runner: worker cost '))
            .map(line => JSON.parse(line.slice('runner: worker cost '.length)))
          assert.equal(snapshots.length, 1, output)
          const observed = snapshots[0]
          assert.deepEqual(Object.keys(observed).sort(), ['version', 'entryFile', 'enabled', 'pid', 'status', 'reason', 'preImport', 'exit', 'interval'].sort())
          assert.equal(observed.entryFile, fixture)
          assert.equal(observed.enabled, enabled === '1')
          if (enabled === '0') {
            assert.equal(observed.status, 'disabled')
            assert.equal(observed.preImport, null)
            assert.equal(observed.exit, null)
            assert.equal(observed.interval, null)
          } else {
            assert.equal(observed.preImport.pid, body.pid)
            assert.equal(observed.preImport.parentPid, body.parentPid)
            assert.ok(['complete', 'missing'].includes(observed.status), output)
            if (sink === 'closed') assert.equal(observed.status, 'missing', output)
            if (observed.status === 'missing') {
              assert.equal(observed.reason, 'Exit observation not received')
              assert.equal(observed.exit, null)
              assert.equal(observed.interval, null)
            }
          }
        }
      }
    }
  } finally {
    fs.rmSync(directory, { recursive: true, force: true })
  }
})

test('WHAT[verification-system-006] continuing worker cost IPC cannot renew a pending file after invalid diagnostic evidence', async () => {
  const directory = fs.realpathSync(fs.mkdtempSync(path.join(os.tmpdir(), 'worker-cost-ipc-')))
  try {
    const fixture = path.join(directory, 'pending.test.mjs')
    const inner = path.join(directory, 'inner.mjs')
    const launcher = path.join(directory, 'supervise.mjs')
    const receipt = path.join(directory, 'send-receipt')
    const receiverReceipt = path.join(directory, 'receive-receipt')
    fs.writeFileSync(fixture, '')
    fs.writeFileSync(inner, `import fs from 'node:fs'
const entryFile = process.argv[2]
const snapshot = { version: 1, entryFile, enabled: true, pid: null, status: 'missing', reason: 'Pre-import observation not received', preImport: null, exit: null, interval: null }
let count = 0
let inFlight = false
function sendCost(data, next) {
  inFlight = true
  process.send({ type: 'runner:worker-cost', data }, error => {
    if (error) throw error
    fs.writeFileSync(${JSON.stringify(receipt + '.next')}, JSON.stringify({ pid: process.pid, sent: ++count }))
    fs.renameSync(${JSON.stringify(receipt + '.next')}, ${JSON.stringify(receipt)})
    inFlight = false
    next?.()
  })
}
const prefix = [snapshot, { ...snapshot, unobservedCost: 0 }, snapshot, snapshot]
function sendPrefix(index) {
  if (index < prefix.length) {
    sendCost(prefix[index], () => sendPrefix(index + 1))
    return
  }
  setInterval(() => {
    if (!inFlight) sendCost(snapshot)
  }, 10)
}
process.send({ type: 'runner:file-start', data: { entryFile, pid: process.pid, parentPid: process.ppid } }, error => {
  if (error) throw error
  sendPrefix(0)
})
`)
    const moduleUrl = new URL('./e2e/support/supervise-node-test.mjs', import.meta.url).href
    fs.writeFileSync(launcher, `import fs from 'node:fs'
import { ChildProcess } from 'node:child_process'
import { superviseNodeTest } from ${JSON.stringify(moduleUrl)}
const originalEmit = ChildProcess.prototype.emit
const originalError = console.error
const received = []
let owner = null
let beforeTimeout = null
let timeoutCount = 0
try {
  ChildProcess.prototype.emit = function (name, ...args) {
    const result = Reflect.apply(originalEmit, this, [name, ...args])
    if (name !== 'message' || this.spawnargs?.[0] !== process.execPath || this.spawnargs?.[1] !== ${JSON.stringify(inner)}) return result
    const event = args[0]
    if (event?.type === 'runner:file-start' && event.data?.entryFile === ${JSON.stringify(fixture)} && event.data.pid === this.pid && event.data.parentPid === process.pid) {
      owner = { pid: this.pid, parentPid: process.pid }
    }
    if (owner?.pid === this.pid && event?.type === 'runner:worker-cost' && event.data?.entryFile === ${JSON.stringify(fixture)}) received.push(event.data)
    return result
  }
  console.error = function (...args) {
    Reflect.apply(originalError, this, args)
    if (typeof args[0] === 'string' && args[0].startsWith("WATCHDOG: 'worker-cost-ipc' silent for ")) {
      timeoutCount += 1
      beforeTimeout = received.slice()
    }
  }
  await superviseNodeTest({ files: [${JSON.stringify(fixture)}], inner: ${JSON.stringify(inner)}, label: 'worker-cost-ipc', silenceMs: 300, throwOnFailure: true })
} finally {
  ChildProcess.prototype.emit = originalEmit
  console.error = originalError
  fs.writeFileSync(${JSON.stringify(receiverReceipt)}, JSON.stringify({ owner, timeoutCount, beforeTimeout, afterTimeout: received.slice(beforeTimeout?.length ?? 0) }))
}
`)
    const env = { ...process.env }
    delete env.NODE_TEST_CONTEXT
    delete env.WXS_VERIFICATION_WORKER_DIAGNOSTICS
    const child = spawn(process.execPath, [launcher], { env, stdio: ['ignore', 'pipe', 'pipe'] })
    let output = ''
    child.stdout.on('data', chunk => { output += chunk })
    child.stderr.on('data', chunk => { output += chunk })
    const code = await new Promise((resolveExit, reject) => {
      child.once('error', reject)
      child.once('close', resolveExit)
    })
    assert.equal(code, 1, output)
    assert.match(output, /WATCHDOG.*silent for/)
    assert.match(output, /0 blocking progress/)
    assert.match(output, /0 drained, 1 active, 0 queued/)
    assert.match(output, /verdict counts unavailable; no authoritative summary/)
    assert.match(output, /post-exit group verification\/reclamation:.*accepted=true/)
    const sent = JSON.parse(fs.readFileSync(receipt, 'utf8'))
    const received = JSON.parse(fs.readFileSync(receiverReceipt, 'utf8'))
    const evidence = `${output}\nSender receipt: ${JSON.stringify(sent)}\nReceiver receipt: ${JSON.stringify(received)}`
    assert.ok(sent.sent > 3, evidence)
    assert.throws(() => process.kill(sent.pid, 0), { code: 'ESRCH' })
    assert.deepEqual(received.owner, { pid: sent.pid, parentPid: child.pid }, evidence)
    assert.equal(received.timeoutCount, 1, evidence)
    assert.ok(Array.isArray(received.beforeTimeout) && received.beforeTimeout.length > 4, evidence)
    const expectedSnapshot = { version: 1, entryFile: fixture, enabled: true, pid: null, status: 'missing', reason: 'Pre-import observation not received', preImport: null, exit: null, interval: null }
    assert.deepEqual(received.beforeTimeout.slice(0, 4), [expectedSnapshot, { ...expectedSnapshot, unobservedCost: 0 }, expectedSnapshot, expectedSnapshot], evidence)
    for (const snapshot of [...received.beforeTimeout.slice(4), ...received.afterTimeout]) {
      assert.deepEqual(snapshot, expectedSnapshot, evidence)
    }
    const observed = output.split('\n').filter(line => line.startsWith('runner: worker cost '))
      .map(line => JSON.parse(line.slice('runner: worker cost '.length)))
    assert.equal(observed.length, 1, output)
    assert.deepEqual(Object.keys(observed[0]).sort(), ['version', 'entryFile', 'enabled', 'pid', 'status', 'reason', 'preImport', 'exit', 'interval'].sort())
    assert.equal(observed[0].status, 'invalid', 'A later valid missing snapshot cannot repair invalid evidence')
    assert.equal(observed[0].interval, null)
  } finally {
    fs.rmSync(directory, { recursive: true, force: true })
  }
})

test('WHAT[verification-system-006] invalid worker cost IPC leaves actual runner verdicts and process cleanup unchanged', async () => {
  const directory = fs.realpathSync(fs.mkdtempSync(path.join(os.tmpdir(), 'worker-cost-invalid-')))
  try {
    const fixture = path.join(directory, 'actual.test.mjs')
    const inner = path.join(directory, 'inner.mjs')
    const launcher = path.join(directory, 'supervise.mjs')
    const marker = path.join(directory, 'actual-body')
    const innerUrl = new URL('./support/run-inner.mjs', import.meta.url).href
    const supervisorUrl = new URL('./e2e/support/supervise-node-test.mjs', import.meta.url).href
    fs.writeFileSync(fixture, `import assert from 'node:assert/strict'
import fs from 'node:fs'
import test from 'node:test'
test('original worker result with invalid diagnostics', () => {
  fs.writeFileSync(${JSON.stringify(marker)}, JSON.stringify({ pid: process.pid, parentPid: process.ppid }))
  if (process.env.WORKER_OUTCOME === 'fail') assert.fail('original failure survives invalid diagnostics')
})
`)
    fs.writeFileSync(inner, `import { runTestFiles } from ${JSON.stringify(innerUrl)}
const drained = await runTestFiles({ files: process.argv.slice(2), concurrency: 1, send(message, callback) {
  if (message.type === 'runner:worker-cost') process.send({ ...message, data: { ...message.data, unobservedCost: 0 } }, () => {})
  process.send(message, callback)
} })
if (!drained) process.exitCode = 1
`)
    fs.writeFileSync(launcher, `import { superviseNodeTest } from ${JSON.stringify(supervisorUrl)}
await superviseNodeTest({ files: [${JSON.stringify(fixture)}], inner: ${JSON.stringify(inner)}, label: 'worker-cost-invalid', silenceMs: 5000 })
`)
    for (const outcome of ['pass', 'fail']) {
      const env = { ...process.env, WORKER_OUTCOME: outcome }
      delete env.NODE_TEST_CONTEXT
      delete env.WXS_VERIFICATION_WORKER_DIAGNOSTICS
      const child = spawn(process.execPath, [launcher], { env, stdio: ['ignore', 'pipe', 'pipe'] })
      let output = ''
      child.stdout.on('data', chunk => { output += chunk })
      child.stderr.on('data', chunk => { output += chunk })
      const code = await new Promise((resolveExit, reject) => {
        child.once('error', reject)
        child.once('close', resolveExit)
      })
      assert.equal(code, outcome === 'pass' ? 0 : 1, output)
      assert.match(output, outcome === 'pass'
        ? /1 passed, 0 failed; 1\/1 planned file\(s\) completed/
        : /0 passed, 1 failed; 1\/1 planned file\(s\) completed/)
      if (outcome === 'fail') assert.match(output, /original failure survives invalid diagnostics/)
      assert.doesNotMatch(output, /WATCHDOG|stream error|incomplete run/)
      const group = /post-exit group verification\/reclamation: pid=(\d+);.*accepted=true/.exec(output)
      assert.ok(group, output)
      const body = JSON.parse(fs.readFileSync(marker, 'utf8'))
      assert.equal(body.parentPid, Number(group[1]))
      assert.throws(() => process.kill(body.pid, 0), { code: 'ESRCH' })
      assert.throws(() => process.kill(body.parentPid, 0), { code: 'ESRCH' })
      const snapshots = output.split('\n').filter(line => line.startsWith('runner: worker cost '))
        .map(line => JSON.parse(line.slice('runner: worker cost '.length)))
      assert.equal(snapshots.length, 1, output)
      assert.equal(snapshots[0].status, 'invalid')
      assert.equal(snapshots[0].interval, null)
    }
  } finally {
    fs.rmSync(directory, { recursive: true, force: true })
  }
})

test('WHAT[verification-system-006] the worker cost collector rejects observations and invalid states presented as disabled', () => {
  const entryFile = path.resolve('actual-worker.test.mjs')
  const disabled = createWorkerCostObserver({ entryFile, parentPid: 500, enabled: false }).finish()
  const preImport = { version: 1, phase: 'pre-import', sequence: 0, entryFile, pid: 501, parentPid: 500, monotonicMs: 10, cpuUserMicros: 100, cpuSystemMicros: 30 }
  for (const changes of [{ preImport }, { status: 'invalid', reason: 'Native observation never made' }]) {
    assert.throws(() => validateWorkerCostSnapshot({ ...disabled, ...changes }, { entryFile, parentPid: 500, enabled: false }), /native observations/)
  }
})

test('WHAT[verification-system-006] the worker cost collector preserves native cumulative units and only derives a same-process interval', () => {
  const entryFile = path.resolve('actual-worker.test.mjs')
  const observer = createWorkerCostObserver({ entryFile, parentPid: 500 })
  const preImport = { version: 1, phase: 'pre-import', sequence: 0, entryFile, pid: 501, parentPid: 500, monotonicMs: 10.5, cpuUserMicros: 100, cpuSystemMicros: 30 }
  const exit = { ...preImport, phase: 'exit', sequence: 1, monotonicMs: 25, cpuUserMicros: 140, cpuSystemMicros: 42, exitCode: 17 }
  const event = record => ({ type: 'test:stdout', data: { file: entryFile, entryFile, message: encodeWorkerCostRecord(record) } })
  observer.observe(event(preImport))
  observer.observe(event(exit))
  const observed = observer.finish()
  assert.equal(observed.status, 'complete')
  assert.equal(observed.reason, null)
  assert.deepEqual(observed.preImport, preImport)
  assert.deepEqual(observed.exit, exit)
  assert.deepEqual(observed.interval, { clockPid: 501, wallMs: 14.5, cpuUserMicros: 40, cpuSystemMicros: 12 })
  assert.deepEqual(validateWorkerCostSnapshot(observed, { entryFile, parentPid: 500 }), observed)
  observed.preImport.cpuUserMicros = 999
  assert.equal(observer.snapshot().preImport.cpuUserMicros, 100)
})

test('WHAT[verification-system-006] the worker cost collector invalidates forged, duplicate, reordered and wrongly attributed observations without a business verdict', () => {
  const entryFile = path.resolve('actual-worker.test.mjs')
  const foreign = path.resolve('foreign-worker.test.mjs')
  const preImport = { version: 1, phase: 'pre-import', sequence: 0, entryFile, pid: 501, parentPid: 500, monotonicMs: 10, cpuUserMicros: 100, cpuSystemMicros: 30 }
  const exit = { ...preImport, phase: 'exit', sequence: 1, monotonicMs: 20, cpuUserMicros: 140, cpuSystemMicros: 42, exitCode: 0 }
  const record = value => `${WORKER_COST_PREFIX}${JSON.stringify(value)}\n`
  const wrongBody = `[verification-test-start] ${JSON.stringify({ entryFile, pid: 502, parentPid: 500 })}\n`
  const cases = [
    [record({ ...preImport, entryFile: foreign })],
    [record({ ...preImport, parentPid: 499 })],
    [record(preImport), record({ ...exit, pid: 502 })],
    [record(preImport), record(preImport)],
    [record(preImport), record(exit), record(exit)],
    [record(exit), record(preImport)],
    [record(preImport), record({ ...exit, monotonicMs: 9 })],
    [record(preImport), record({ ...exit, cpuUserMicros: 99 })],
    [record(preImport), record({ ...exit, cpuSystemMicros: 29 })],
    [record({ ...preImport, cpuUserMicros: -1 })],
    [record({ ...preImport, monotonicMs: null })],
    [record({ ...preImport, extra: 'unobserved cost' })],
    [record(preImport), wrongBody],
    [`${WORKER_COST_PREFIX}{invalid JSON}\n`],
  ]
  for (const messages of cases) {
    const observer = createWorkerCostObserver({ entryFile, parentPid: 500 })
    for (const message of messages) observer.observe({ type: 'test:stdout', data: { file: entryFile, entryFile, message } })
    const observed = observer.finish()
    assert.equal(observed.status, 'invalid', JSON.stringify(messages))
    assert.equal(observed.interval, null)
    assert.ok(Buffer.byteLength(observed.reason) <= 256)
    assert.equal(classifyVerdict({ type: 'runner:worker-cost', data: observed }), null)
    assert.deepEqual(validateWorkerCostSnapshot(observed, { entryFile, parentPid: 500 }), observed)
  }
  for (const data of [{ file: foreign, entryFile }, { file: entryFile, entryFile: foreign }]) {
    const observer = createWorkerCostObserver({ entryFile, parentPid: 500 })
    observer.observe({ type: 'test:stdout', data: { ...data, message: record(preImport) } })
    assert.equal(observer.finish().status, 'invalid')
  }
})

test('WHAT[verification-system-006] the worker cost collector bounds fragmented bytes and distinguishes disabled, missing and truncated observations', () => {
  const entryFile = path.resolve('actual-worker.test.mjs')
  const preImport = { version: 1, phase: 'pre-import', sequence: 0, entryFile, pid: 501, parentPid: 500, monotonicMs: 10, cpuUserMicros: 100, cpuSystemMicros: 30 }
  const event = message => ({ type: 'test:stdout', data: { file: entryFile, entryFile, message } })
  const observer = createWorkerCostObserver({ entryFile, parentPid: 500 })
  assert.deepEqual({ status: observer.snapshot().status, interval: observer.snapshot().interval, preImport: observer.snapshot().preImport },
    { status: 'missing', interval: null, preImport: null })
  observer.observe(event('ordinary stdout '.repeat(MAX_WORKER_COST_BYTES) + '\n'))
  const line = encodeWorkerCostRecord(preImport)
  for (const part of [line.slice(0, 5), line.slice(5, 35), line.slice(35)]) observer.observe(event(part))
  const missing = observer.finish()
  assert.equal(missing.status, 'missing')
  assert.match(missing.reason, /Exit observation not received/)
  assert.deepEqual(missing.preImport, preImport)
  assert.equal(missing.exit, null)
  assert.equal(missing.interval, null)
  assert.deepEqual(validateWorkerCostSnapshot(missing, { entryFile, parentPid: 500 }), missing)
  const disabled = createWorkerCostObserver({ entryFile, parentPid: 500, enabled: false })
  disabled.observe(event(line))
  assert.equal(disabled.finish().status, 'disabled')
  assert.equal(disabled.snapshot().enabled, false)
  assert.equal(disabled.snapshot().preImport, null)
  assert.deepEqual(validateWorkerCostSnapshot(disabled.snapshot(), { entryFile, parentPid: 500, enabled: false }), disabled.snapshot())
  assert.throws(() => validateWorkerCostSnapshot(disabled.snapshot(), { entryFile, parentPid: 500 }), /snapshot/)
  assert.throws(() => validateWorkerCostSnapshot({ ...missing, status: 'complete' }, { entryFile, parentPid: 500 }), /native observations/)
  assert.throws(() => validateWorkerCostSnapshot({ ...missing, status: 'disabled' }, { entryFile, parentPid: 500 }), /native observations/)
  assert.throws(() => validateWorkerCostSnapshot({ ...missing, identityConfirmedByBody: true }, { entryFile, parentPid: 500 }), /snapshot/)
  assert.throws(() => validateWorkerCostSnapshot({ ...missing, reason: 'complete CPU evidence' }, { entryFile, parentPid: 500 }), /native observations/)
  for (const message of [line.slice(0, -1), WORKER_COST_PREFIX + '界'.repeat(MAX_WORKER_COST_BYTES) + '\n']) {
    const invalid = createWorkerCostObserver({ entryFile, parentPid: 500 })
    invalid.observe(event(message))
    assert.equal(invalid.finish().status, 'invalid')
    assert.equal(invalid.snapshot().interval, null)
  }
  assert.throws(() => encodeWorkerCostRecord({ ...preImport, entryFile: '/' + '界'.repeat(MAX_WORKER_COST_BYTES) }), /byte boundary/)
  assert.throws(() => validateWorkerCostSnapshot({ ...missing, interval: { clockPid: 501, wallMs: 0, cpuUserMicros: 0, cpuSystemMicros: 0 } }, { entryFile, parentPid: 500 }), /native observations/)
})

test('WHAT[verification-system-006] file waits distinguish queued, active and verdicts awaiting stream drain', () => {
  const files = ['drained.mjs', 'verdict.mjs', 'active.mjs', 'queued.mjs'].map((file) => path.resolve(file))
  const waits = testSupervisor.createFileWaitTracker(files)
  const event = (type, file, name) => ({ type, data: { entryFile: file, name } })
  waits.observe(event('runner:file-start', files[0]))
  waits.observe(event('test:pass', files[0], 'finished leaf'))
  waits.observe(event('runner:file-drained', files[0]))
  waits.observe(event('runner:file-start', files[1]))
  waits.observe(event('test:pass', files[1], 'passed but still alive'))
  waits.observe(event('runner:file-start', files[2]))
  assert.deepEqual(waits.snapshot(), {
    queued: [files[3]],
    active: [
      { file: files[1], lastVerdict: 'test:pass:passed but still alive' },
      { file: files[2], lastVerdict: null },
    ],
    drained: [files[0]],
  })
  waits.observe(event('runner:file-drained', files[1]))
  assert.deepEqual(waits.snapshot().active, [{ file: files[2], lastVerdict: null }])
})

test('WHAT[verification-system-006] file lifecycle facts reject unknown entries and impossible transitions', () => {
  const file = path.resolve('planned.mjs')
  const waits = testSupervisor.createFileWaitTracker([file])
  const event = (type, entryFile = file) => ({ type, data: { entryFile } })
  assert.throws(() => waits.observe(event('runner:file-start', path.resolve('unknown.mjs'))), /unplanned/)
  assert.throws(() => waits.observe(event('runner:file-drained')), /queued/)
  assert.throws(() => waits.observe({ type: 'runner:file-start', data: {} }), /entryFile/)
  waits.observe(event('runner:file-start'))
  assert.throws(() => waits.observe(event('runner:file-start')), /active/)
  waits.observe(event('runner:file-drained'))
  assert.throws(() => waits.observe(event('test:pass')), /drained/)
  assert.deepEqual(waits.snapshot(), { queued: [], active: [], drained: [file] })
})

test('WHAT[verification-system-006] a pending runtime test start remains diagnostic evidence without becoming a verdict', () => {
  const file = path.resolve('pending-runtime.mjs')
  const waits = testSupervisor.createFileWaitTracker([file])
  waits.observe({ type: 'runner:file-start', data: { entryFile: file } })
  const event = { type: 'test:start', data: {
    entryFile: file, file: path.resolve('registration.mjs'), name: 'pending child',
    nesting: 1, testId: 17, testNumber: 2, pid: 101, parentPid: 99,
  } }
  waits.observe(event)
  assert.equal(classifyVerdict(event), null)
  assert.deepEqual(waits.snapshot().active[0].lastStart, event.data)
  assert.equal(waits.snapshot().active[0].lastVerdict, null)
})

test('WHAT[verification-system-006] continuing body entry diagnostics cannot keep a genuinely pending test alive', async () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'body-start-noise-'))
  try {
    const fixture = path.join(directory, 'pending.test.mjs')
    const launcher = path.join(directory, 'supervise.mjs')
    fs.writeFileSync(fixture, `import test from 'node:test'
test('pending parent', async t => {
  await t.test('pending child', async context => {
    setInterval(() => process.stdout.write('[verification-test-start] ' + JSON.stringify({name: context.name, fullName: context.fullName, pid: process.pid, parentPid: process.ppid}) + '\\n'), 10)
    await new Promise(() => {})
  })
})
`)
    const moduleUrl = new URL('./e2e/support/supervise-node-test.mjs', import.meta.url).href
    fs.writeFileSync(launcher, `import { superviseNodeTest } from ${JSON.stringify(moduleUrl)}
await superviseNodeTest({ files: [${JSON.stringify(fixture)}], label: 'body-start-noise', silenceMs: 300 })
`)
    const env = { ...process.env }
    delete env.NODE_TEST_CONTEXT
    const child = spawn(process.execPath, [launcher], { env, stdio: ['ignore', 'pipe', 'pipe'] })
    let output = ''
    child.stdout.on('data', chunk => { output += chunk })
    child.stderr.on('data', chunk => { output += chunk })
    const code = await new Promise((resolveExit, reject) => {
      child.once('error', reject)
      child.once('close', resolveExit)
    })
    assert.equal(code, 1, output)
    assert.ok(output.split('[verification-test-start] ').length > 3, output)
    assert.match(output, /verdict.silence|silent|silence/i)
    assert.match(output, /accepted=true/)
    assert.doesNotMatch(output, /\[test-summary\].*passed/)
  } finally {
    fs.rmSync(directory, { recursive: true, force: true })
  }
})

test('WHAT[verification-system-006] real tool diagnostics preserve stdout failure and complete cleanup without forwarding the control field', async () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'tool-phase-equivalence-'))
  try {
    const launcher = path.join(directory, 'probe.mjs')
    const moduleUrl = new URL('../../../scripts/lib/verification-tool-probe.mjs', import.meta.url).href
    fs.writeFileSync(launcher, `import { closeSync } from 'node:fs'
import { runVerificationToolProbe } from ${JSON.stringify(moduleUrl)}
if (process.argv[3] === 'closed') closeSync(2)
const program = "if (process.env.WXS_VERIFICATION_TOOL_DIAGNOSTICS !== undefined) throw new Error('diagnostic control leaked'); console.log('actual probe stdout'); if (process.argv[1] === 'fail') { console.error('actual probe stderr'); process.exit(17) }"
try {
  const stdout = await runVerificationToolProbe(process.execPath, ['-e', program, process.argv[2]], { cwd: process.cwd(), env: { HOME: process.cwd() } })
  console.log(JSON.stringify({ok: true, stdout}))
} catch (error) {
  console.log(JSON.stringify({ok: false, code: error.code, exitCode: error.exitCode, stdout: error.stdout, stderr: error.stderr}))
}
`)
    const run = async (enabled, outcome, sink = 'open') => {
      const env = { ...process.env, WXS_VERIFICATION_TOOL_DIAGNOSTICS: enabled ? '1' : '0' }
      delete env.NODE_TEST_CONTEXT
      const child = spawn(process.execPath, [launcher, outcome, sink], { cwd: directory, env, stdio: ['ignore', 'pipe', 'pipe'] })
      let stdout = ''
      let stderr = ''
      child.stdout.on('data', chunk => { stdout += chunk })
      child.stderr.on('data', chunk => { stderr += chunk })
      const exit = await new Promise((resolveExit, reject) => {
        child.once('error', reject)
        child.once('close', resolveExit)
      })
      assert.equal(exit, 0, stderr)
      return { result: JSON.parse(stdout), stderr }
    }
    for (const outcome of ['pass', 'fail']) {
      const disabled = await run(false, outcome)
      const enabled = await run(true, outcome)
      assert.deepEqual(enabled.result, disabled.result)
      const closed = await run(true, outcome, 'closed')
      assert.deepEqual(closed.result, disabled.result, 'a physically closed diagnostic sink preserves the original tool outcome')
      assert.equal(enabled.result.ok, outcome === 'pass')
      if (outcome === 'fail') {
        assert.equal(enabled.result.exitCode, 17)
        assert.equal(enabled.result.stderr, 'actual probe stderr\n')
      }
      assert.doesNotMatch(disabled.stderr, /verification-tool-phase/)
      const phases = enabled.stderr.split('\n').filter(line => line.startsWith('[verification-tool-phase] '))
        .map(line => JSON.parse(line.slice('[verification-tool-phase] '.length)))
      for (const phase of ['monitor-spawned', 'tool-spawned', 'tool-exited', 'group-drained', 'monitor-closed']) {
        assert.ok(phases.some(fact => fact.phase === phase), `${phase}: ${enabled.stderr}`)
      }
      const tool = phases.find(fact => fact.phase === 'tool-spawned')
      assert.throws(() => process.kill(tool.toolPid, 0), error => error.code === 'ESRCH')
      const monitor = phases.find(fact => fact.phase === 'monitor-spawned')
      assert.throws(() => process.kill(monitor.monitorPid, 0), error => error.code === 'ESRCH')
    }
  } finally {
    fs.rmSync(directory, { recursive: true, force: true })
  }
})

test('WHAT[verification-system-006] real silence diagnostics identify active waits separately from queued files', async () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'file-wait-diagnostics-'))
  try {
    const launcher = path.join(directory, 'supervise.mjs')
    const moduleUrl = new URL('./e2e/support/supervise-node-test.mjs', import.meta.url).href
    fs.writeFileSync(launcher, `import { superviseNodeTest } from ${JSON.stringify(moduleUrl)}
await superviseNodeTest({ files: process.argv.slice(2), label: 'file-wait-fixture', silenceMs: 1000 })
`)
    const files = ['all-pass.fixture.mjs', 'leaks-handle-after-pass.fixture.mjs', 'two-leaf.fixture.mjs']
      .map((file) => fileURLToPath(new URL(`./support/fixtures/${file}`, import.meta.url)))
    const env = { ...process.env, NODE_TEST_CONCURRENCY: '1' }
    delete env.NODE_TEST_CONTEXT
    const child = spawn(process.execPath, [launcher, ...files], { env, stdio: ['ignore', 'pipe', 'pipe'] })
    let diagnostics = ''
    child.stdout.resume()
    child.stderr.on('data', (chunk) => { diagnostics += chunk })
    const code = await new Promise((resolveExit, reject) => {
      child.on('error', reject)
      child.on('close', resolveExit)
    })
    assert.equal(code, 1, diagnostics)
    assert.match(diagnostics, /file streams: 1 drained, 1 active, 1 queued/)
    const activeWaits = diagnostics.split('\n').filter((line) => line.includes('active file '))
    assert.equal(activeWaits.length, 1, diagnostics)
    assert.match(activeWaits[0], /leaks-handle-after-pass\.fixture\.mjs.*waiting for stream drain.*passes and leaks/)
    assert.match(diagnostics, /1 queued file\(s\) have not started/)
  } finally {
    fs.rmSync(directory, { recursive: true, force: true })
  }
})

test('WHAT[verification-system-006] silence failure reclaims its process group before the caller catches it and completes cleanup', { skip: process.platform === 'win32' }, async () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'silence-caller-cleanup-'))
  const ownershipMarker = path.join(directory, 'ownership.json')
  const caughtMarker = path.join(directory, 'caught.json')
  const cleanupMarker = path.join(directory, 'cleanup.txt')
  try {
    const launcher = path.join(directory, 'supervise.mjs')
    const held = path.join(directory, 'held.fixture.mjs')
    const moduleUrl = new URL('./e2e/support/supervise-node-test.mjs', import.meta.url).href
    fs.writeFileSync(held, `import fs from 'node:fs'
import test from 'node:test'
test('held actual file resource', async () => {
  fs.watch(${JSON.stringify(directory)}, () => {})
  fs.writeFileSync(${JSON.stringify(ownershipMarker)}, JSON.stringify({ innerPid: process.ppid, filePid: process.pid, home: process.env.HOME }))
  await new Promise(() => {})
})
`)
    fs.writeFileSync(launcher, `import fs from 'node:fs'
import { execFileSync } from 'node:child_process'
import { superviseNodeTest } from ${JSON.stringify(moduleUrl)}
try {
  await superviseNodeTest({ files: process.argv.slice(2), label: 'silence-caller-cleanup', silenceMs: 1000, throwOnFailure: true })
  throw new Error('Held resource unexpectedly completed')
} catch (error) {
  const { innerPid } = JSON.parse(fs.readFileSync(${JSON.stringify(ownershipMarker)}, 'utf8'))
  const live = execFileSync('ps', ['-eo', 'pid=,pgid=,stat='], { encoding: 'utf8' }).trim().split('\\n').filter(line => {
    const [pid, pgid, state] = line.trim().split(/\\s+/)
    return Number(pgid) === innerPid && !/^[ZX]/.test(state)
  })
  fs.writeFileSync(${JSON.stringify(caughtMarker)}, JSON.stringify({ message: error.message, live }))
  process.exitCode = 1
} finally {
  fs.writeFileSync(${JSON.stringify(cleanupMarker)}, 'caller cleanup completed')
}
`)
    const env = { ...process.env, NODE_TEST_CONCURRENCY: '1', TMPDIR: directory }
    delete env.NODE_TEST_CONTEXT
    const child = spawn(process.execPath, [launcher, held], { env, stdio: ['ignore', 'pipe', 'pipe'] })
    let output = ''
    child.stdout.on('data', chunk => { output += chunk })
    child.stderr.on('data', chunk => { output += chunk })
    const code = await new Promise((resolveExit, reject) => {
      child.on('error', reject)
      child.on('close', resolveExit)
    })
    assert.equal(code, 1, output)
    assert.match(output, /WATCHDOG: 'silence-caller-cleanup' silent for/)
    assert.equal(fs.existsSync(ownershipMarker), true, 'The real held leaf must have started before silence')
    assert.equal(fs.existsSync(caughtMarker), true, 'Silence must reject to its caller instead of exiting the caller process')
    const caught = JSON.parse(fs.readFileSync(caughtMarker, 'utf8'))
    assert.match(caught.message, /supervised suite failed/)
    assert.deepEqual(caught.live, [], 'The owned group must already be empty when the caller handles failure')
    assert.equal(fs.readFileSync(cleanupMarker, 'utf8'), 'caller cleanup completed')
    assert.match(output, /post-exit group verification\/reclamation: pid=\d+;.*accepted=true/)
    assert.match(output, /verdict counts unavailable; no authoritative summary; 0\/1 planned file\(s\) completed/)
    const { home } = JSON.parse(fs.readFileSync(ownershipMarker, 'utf8'))
    assert.equal(fs.existsSync(home), false, 'The supervisor reclaims the exact suite HOME after its inner owner is killed')
  } finally {
    if (fs.existsSync(ownershipMarker)) {
      const { innerPid } = JSON.parse(fs.readFileSync(ownershipMarker, 'utf8'))
      try { process.kill(-innerPid, 'SIGKILL') } catch (error) { if (error.code !== 'ESRCH') throw error }
    }
    fs.rmSync(directory, { recursive: true, force: true })
  }
})

test('WHAT[verification-system-006] synchronous runner spawn failure preserves its cause and stops its watchdog', async () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'spawn-failure-cleanup-'))
  try {
    const launcher = path.join(directory, 'supervise.mjs')
    const moduleUrl = new URL('./e2e/support/supervise-node-test.mjs', import.meta.url).href
    fs.writeFileSync(launcher, `import assert from 'node:assert/strict'
import fs from 'node:fs'
import { superviseNodeTest } from ${JSON.stringify(moduleUrl)}
try {
  await superviseNodeTest({ files: ['unused.fixture.mjs'], label: 'invalid-spawn', silenceMs: 100, env: { BAD: Symbol('invalid environment value') }, throwOnFailure: true })
  assert.fail('Spawn must reject an actual invalid environment value')
} catch (error) {
  assert.ok(error instanceof TypeError)
  assert.match(error.message, /Symbol/)
  assert.deepEqual(fs.readdirSync(${JSON.stringify(directory)}), ['supervise.mjs'])
}
setTimeout(() => console.log('caller remains live after the former silence window'), 400)
`)
    const env = { ...process.env, TMPDIR: directory }
    delete env.NODE_TEST_CONTEXT
    const child = spawn(process.execPath, [launcher], { env, stdio: ['ignore', 'pipe', 'pipe'] })
    let output = ''
    child.stdout.on('data', chunk => { output += chunk })
    child.stderr.on('data', chunk => { output += chunk })
    const code = await new Promise((resolveExit, reject) => {
      child.on('error', reject)
      child.on('close', resolveExit)
    })
    assert.equal(code, 0, output)
    assert.match(output, /caller remains live after the former silence window/)
    assert.doesNotMatch(output, /WATCHDOG|inner runner exit was not observed/)
  } finally {
    fs.rmSync(directory, { recursive: true, force: true })
  }
})

test('WHAT[verification-system-006] suite HOME cleanup failure preserves the original failed verdict and rejects acceptance', {
  skip: process.platform === 'win32' || process.getuid?.() === 0,
}, async () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'home-cleanup-failure-'))
  try {
    const launcher = path.join(directory, 'supervise.mjs')
    const fixture = path.join(directory, 'failure.fixture.mjs')
    const moduleUrl = new URL('./e2e/support/supervise-node-test.mjs', import.meta.url).href
    fs.writeFileSync(fixture, `import assert from 'node:assert/strict'
import fs from 'node:fs'
import test from 'node:test'
test('real verdict followed by inaccessible HOME parent', () => {
  fs.chmodSync(${JSON.stringify(directory)}, 0)
  assert.fail('original leaf failure')
})
`)
    fs.writeFileSync(launcher, `import assert from 'node:assert/strict'
import fs from 'node:fs'
import { superviseNodeTest } from ${JSON.stringify(moduleUrl)}
try {
  await superviseNodeTest({ files: [${JSON.stringify(fixture)}], label: 'home-cleanup-failure', silenceMs: 5000, throwOnFailure: true })
  assert.fail('The failed verdict cannot be accepted')
} catch (error) {
  fs.chmodSync(${JSON.stringify(directory)}, 0o700)
  assert.ok(error instanceof AggregateError, 'Both the suite failure and actual cleanup failure must remain observable')
  assert.equal(error.errors.length, 2)
  assert.equal(error.cause, error.errors[0])
  assert.match(error.errors[0].message, /supervised suite failed/)
  assert.equal(error.errors[1].code, 'EACCES')
  console.log('original verdict and cleanup failure both retained')
} finally {
  fs.chmodSync(${JSON.stringify(directory)}, 0o700)
}
`)
    const env = { ...process.env, TMPDIR: directory, NODE_TEST_CONCURRENCY: '1' }
    delete env.NODE_TEST_CONTEXT
    const child = spawn(process.execPath, [launcher], { env, stdio: ['ignore', 'pipe', 'pipe'] })
    let output = ''
    child.stdout.on('data', chunk => { output += chunk })
    child.stderr.on('data', chunk => { output += chunk })
    const code = await new Promise((resolveExit, reject) => {
      child.on('error', reject)
      child.on('close', resolveExit)
    })
    assert.equal(code, 0, output)
    assert.match(output, /original verdict and cleanup failure both retained/)
    assert.match(output, /1 passed, 1 failed|0 passed, 1 failed/)
  } finally {
    fs.chmodSync(directory, 0o700)
    fs.rmSync(directory, { recursive: true, force: true })
  }
})

test('WHAT[verification-system-006] standalone inner owns only its allocated HOME and always releases it', async () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'standalone-home-'))
  try {
    const fixture = path.join(directory, 'home.fixture.mjs')
    const marker = path.join(directory, 'home.json')
    fs.writeFileSync(fixture, `import fs from 'node:fs'
import test from 'node:test'
test('actual isolated HOME', () => fs.writeFileSync(${JSON.stringify(marker)}, JSON.stringify(process.env.HOME)))
`)
    const suppliedHome = fs.realpathSync(fs.mkdtempSync(path.join(directory, 'caller-owned-')))
    fs.writeFileSync(path.join(suppliedHome, 'caller.txt'), 'owned by the caller')
    const env = { ...process.env, TMPDIR: directory, NODE_TEST_CONCURRENCY: '1' }
    delete env.NODE_TEST_CONTEXT
    for (const mode of ['allocated', 'supplied', 'invalid-concurrency']) {
      const args = mode === 'supplied' ? ['--owned-test-home', suppliedHome, fixture] : [fixture]
      const child = spawn(process.execPath, [testSupervisor.NODE_TEST_INNER, ...args], {
        env: { ...env, NODE_TEST_CONCURRENCY: mode === 'invalid-concurrency' ? 'invalid' : '1' },
        stdio: ['ignore', 'pipe', 'pipe'],
      })
      let output = ''
      child.stdout.on('data', chunk => { output += chunk })
      child.stderr.on('data', chunk => { output += chunk })
      const code = await new Promise((resolveExit, reject) => {
        child.on('error', reject)
        child.on('close', resolveExit)
      })
      assert.equal(code, mode === 'invalid-concurrency' ? 1 : 0, output)
      if (mode === 'allocated') {
        const home = JSON.parse(fs.readFileSync(marker, 'utf8'))
        assert.equal(path.dirname(home), fs.realpathSync(directory))
        assert.equal(fs.existsSync(home), false)
      } else if (mode === 'supplied') {
        assert.equal(JSON.parse(fs.readFileSync(marker, 'utf8')), suppliedHome)
        assert.equal(fs.readFileSync(path.join(suppliedHome, 'caller.txt'), 'utf8'), 'owned by the caller')
      } else {
        assert.match(output, /NODE_TEST_CONCURRENCY/)
      }
      assert.deepEqual(fs.readdirSync(directory).sort(), [path.basename(suppliedHome), 'home.fixture.mjs', 'home.json'].sort())
    }
  } finally {
    fs.rmSync(directory, { recursive: true, force: true })
  }
})

test('WHAT[verification-system-006] physical backstop diagnostics retain active verdicts and queued files while completed leaves continue advancing', async () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'backstop-file-waits-'))
  try {
    const launcher = path.join(directory, 'supervise.mjs')
    const moduleUrl = new URL('./e2e/support/supervise-node-test.mjs', import.meta.url).href
    fs.writeFileSync(launcher, `import { superviseNodeTest } from ${JSON.stringify(moduleUrl)}
await superviseNodeTest({ files: process.argv.slice(2), label: 'backstop-file-waits', silenceMs: 5000 })
`)
    const drained = fileURLToPath(new URL('./support/fixtures/all-pass.fixture.mjs', import.meta.url))
    const active = path.join(directory, 'finite-work.fixture.mjs')
    const queued = path.join(directory, 'queued.fixture.mjs')
    const queuedMarker = path.join(directory, 'queued-started')
    fs.writeFileSync(active, `import assert from 'node:assert/strict'
import test from 'node:test'
import { setTimeout } from 'node:timers/promises'
let completed = 0
for (let index = 0; index < 100; index++) {
  test('bounded work ' + index, async () => {
    await setTimeout(100)
    assert.equal(completed, index)
    completed++
  })
}
`)
    fs.writeFileSync(queued, `import fs from 'node:fs'
import test from 'node:test'
fs.writeFileSync(${JSON.stringify(queuedMarker)}, 'queued entry started')
test('queued work', () => {})
`)
    const env = { ...process.env, NODE_TEST_CONCURRENCY: '1', NODE_TEST_VERBOSE: '1', SUITE_BACKSTOP_MS: '1500' }
    delete env.NODE_TEST_CONTEXT
    const child = spawn(process.execPath, [launcher, drained, active, queued], { env, stdio: ['ignore', 'pipe', 'pipe'] })
    let output = ''
    child.stdout.on('data', chunk => { output += chunk })
    child.stderr.on('data', chunk => { output += chunk })
    const code = await new Promise((resolveExit, reject) => {
      child.on('error', reject)
      child.on('close', resolveExit)
    })
    assert.equal(code, 1, output)
    assert.match(output, /suite exceeded the 1500ms physical backstop/)
    assert.doesNotMatch(output, /WATCHDOG/)
    assert.equal(fs.existsSync(queuedMarker), false, 'The queued entry never acquires the occupied lane')
    const verdicts = [...output.matchAll(/✔ bounded work (\d+) /g)]
    assert.ok(verdicts.length >= 2, output)
    assert.match(output, /file streams: 1 drained, 1 active, 1 queued/)
    const activeWaits = output.split('\n').filter(line => line.includes('active file '))
    assert.equal(activeWaits.length, 1, output)
    assert.ok(activeWaits[0].includes(path.relative(process.cwd(), active)), output)
    const activeVerdictMatch = /last verdict: test:(?:pass|complete):bounded work (\d+)$/.exec(activeWaits[0])
    assert.ok(activeVerdictMatch, activeWaits[0])
    const activeVerdictIndex = Number(activeVerdictMatch[1])
    const lastReportedIndex = Number(verdicts.at(-1)[1])
    assert.ok(
      activeVerdictIndex === lastReportedIndex || activeVerdictIndex === lastReportedIndex - 1,
      `active verdict index (${activeVerdictIndex}) must align with last reported verdict index (${lastReportedIndex}) on physical backstop cut`,
    )
    assert.match(output, /1 queued file\(s\) have not started/)
    const reclamation = /post-exit group verification\/reclamation: pid=(\d+);.*accepted=true/.exec(output)
    assert.ok(reclamation, output)
    assert.throws(() => process.kill(Number(reclamation[1]), 0), error => error.code === 'ESRCH')
  } finally {
    fs.rmSync(directory, { recursive: true, force: true })
  }
})

async function superviseSynchronousVerdicts(probe) {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'synchronous-verdicts-'))
  try {
    const launcher = path.join(directory, 'supervise.mjs')
    const moduleUrl = new URL('./e2e/support/supervise-node-test.mjs', import.meta.url).href
    fs.writeFileSync(launcher, `import { superviseNodeTest } from ${JSON.stringify(moduleUrl)}
await superviseNodeTest({ files: process.argv.slice(2), label: 'synchronous-verdicts', silenceMs: 1000 })
`)
    const fixture = fileURLToPath(new URL('./support/fixtures/synchronous-verdicts.fixture.mjs', import.meta.url))
    const env = { ...process.env, VERDICT_TRANSPORT_PROBE: probe, NODE_TEST_CONCURRENCY: '1' }
    delete env.NODE_TEST_CONTEXT
    const child = spawn(process.execPath, [launcher, fixture], { env, stdio: ['ignore', 'pipe', 'pipe'] })
    let output = ''
    child.stdout.on('data', (chunk) => { output += chunk })
    child.stderr.on('data', (chunk) => { output += chunk })
    const code = await new Promise((resolveExit, reject) => {
      child.on('error', reject)
      child.on('close', resolveExit)
    })
    return { code, output }
  } finally {
    fs.rmSync(directory, { recursive: true, force: true })
  }
}

test('WHAT[verification-system-006] synchronous work and microtasks deliver completed verdicts before the file ends', async () => {
  const { code, output } = await superviseSynchronousVerdicts('healthy')
  assert.equal(code, 0, output)
  assert.match(output, /8 passed, 0 failed; 1\/1 planned file\(s\) completed/)
  assert.doesNotMatch(output, /WATCHDOG/)
})

test('WHAT[verification-system-006] transport scheduling cannot renew unfinished work or background noise', async () => {
  const { code, output } = await superviseSynchronousVerdicts('hang')
  assert.equal(code, 1, output)
  assert.match(output, /WATCHDOG.*silent for/)
  assert.match(output, /last progress: test:(?:pass|complete):synchronous work 7/)
  assert.match(output, /background progress.*none of them renewals/)
  assert.match(output, /verdict counts unavailable; no authoritative summary/)
})

test('WHAT[verification-system-006] transport scheduling preserves after-hook failure verdicts', async () => {
  const { code, output } = await superviseSynchronousVerdicts('after-failure')
  assert.equal(code, 1, output)
  assert.match(output, /7 passed, 1 failed; 1\/1 planned file\(s\) completed/)
  assert.match(output, /after hook failure remains visible/)
  assert.doesNotMatch(output, /WATCHDOG/)
})

test('WHAT[verification-system-006] repeat observations of one causal state do not renew twice', async () => {
  const h = createWatchdogHarness()
  const scenario = { watchdog: h.watchdog }
  const observe = (token) => observeCausalProgress(scenario, {
    id: 'accepted-work', token, reason: 'work accepted', lane: 'worker',
  })
  assert.equal(observe('pending'), false)
  await h.advance(200)
  assert.equal(observe('accepted'), true)
  await h.advance(300)
  assert.equal(observe('accepted'), false)
  await h.advance(199)
  assert.equal(h.terminated, false)
  await h.advance(1)
  assert.equal(h.terminated, true)
})

test('WHAT[verification-system-006] stop cancels monitoring and late signals cannot resurrect it', async () => {
  const h = createWatchdogHarness()
  await h.advance(100)
  h.watchdog.stop()
  h.watchdog.advance({ reason: 'late verdict', lane: 'worker', blocking: true })
  h.watchdog.setWindow(50)
  await h.advance(1000)
  assert.equal(h.terminateCount, 0)
  assert.deepEqual(h.diagnostics, [])
})

test('WHAT[verification-system-006] changing the window does not invent new causal progress', async () => {
  const h = createWatchdogHarness()
  await h.advance(300)
  h.watchdog.setWindow(600)
  await h.advance(299)
  assert.equal(h.terminated, false)
  await h.advance(1)
  assert.equal(h.terminated, true)
})

test('WHAT[verification-system-006] restoring the default window retains the original last-progress time', async () => {
  const h = createWatchdogHarness()
  h.watchdog.setWindow(WATCHDOG_TIMEOUT_MS * 2)
  await h.advance(100)
  h.watchdog.setWindow(null)
  await h.advance(WATCHDOG_TIMEOUT_MS - 101)
  assert.equal(h.terminated, false)
  await h.advance(1)
  assert.equal(h.terminateCount, 1)
})

test('WHAT[verification-system-006] failed diagnostic collection discloses its cause before exit', async () => {
  const h = createWatchdogHarness({
    timeoutMs: WATCHDOG_TIMEOUT_MS,
    onTimeout: async () => { throw new Error('host unavailable') },
  })
  await h.advance(WATCHDOG_TIMEOUT_MS)
  assert.equal(h.terminateCount, 1)
  assert.match(h.diagnostics.join('\n'), /current waits unavailable.*host unavailable/i)
  assert.equal(h.trace.at(-1), 'terminate')
  await assertSingleTermination(h, WATCHDOG_TIMEOUT_MS)
})

test('WHAT[verification-system-006] hung diagnostic collection discloses missing information and cannot prevent exit', async () => {
  const h = createWatchdogHarness({
    timeoutMs: WATCHDOG_TIMEOUT_MS,
    onTimeout: () => new Promise(() => {}),
  })
  await h.advance(WATCHDOG_TIMEOUT_MS)
  assert.equal(h.terminated, false)
  await h.advance(DIAGNOSTIC_RACE_MS - 1)
  assert.equal(h.terminated, false)
  await h.advance(1)
  assert.equal(h.terminateCount, 1)
  assert.match(h.diagnostics.join('\n'), /current waits unavailable.*deadline/i)
  await assertSingleTermination(h, WATCHDOG_TIMEOUT_MS)
})

test('WHAT[verification-system-006] successful diagnostic collection completes before a single exit', async () => {
  const calls = []
  const h = createWatchdogHarness({
    timeoutMs: WATCHDOG_TIMEOUT_MS,
    onTimeout: async () => { calls.push('waits collected') },
  })
  await h.advance(WATCHDOG_TIMEOUT_MS)
  assert.deepEqual(calls, ['waits collected'])
  assert.equal(h.terminateCount, 1)
  assert.equal(h.trace.at(-1), 'terminate')
  assert.doesNotMatch(h.diagnostics.join('\n'), /unavailable/)
  await assertSingleTermination(h, WATCHDOG_TIMEOUT_MS)
})

test('WHAT[verification-system-006] a diagnostic output failure still terminates once and cannot restart monitoring', async () => {
  let collectionCount = 0
  const h = createWatchdogHarness({
    timeoutMs: WATCHDOG_TIMEOUT_MS,
    onTimeout: async () => { collectionCount++ },
    deps: { diagnostic: { write() { throw new Error('diagnostic sink unavailable') } } },
  })
  await h.advance(WATCHDOG_TIMEOUT_MS - 1)
  assert.equal(h.terminateCount, 0)
  await h.advance(1)
  assert.equal(collectionCount, 0, 'diagnostic output failed before collection could start')
  assert.equal(h.diagnostics.length, 1)
  assert.match(h.diagnostics[0], /WATCHDOG: 'test-target'/)
  assert.deepEqual(h.trace, ['diagnostic', 'terminate'])
  await assertSingleTermination(h, WATCHDOG_TIMEOUT_MS)
})

test('WHAT[verification-system-006] consumed expectation observations preserve the matched physical attempt', () => {
  const provider = new StrictMockProvider()
  let early
  provider.afterExpectation('orch.2', (observation) => { early = observation })
  provider._signals.consume({ id: 'orch.2', permanent: true })
  provider._runAfterExpectation('orch.2', { sessionId: 'ses-orch', parentSessionId: 'ses-parent' })
  assert.deepEqual(early, { id: 'orch.2', attempt: 1, sessionId: 'ses-orch', parentSessionId: 'ses-parent' })
  let late
  provider.afterExpectation('orch.2', (observation) => { late = observation })
  assert.deepEqual(late, early)
})

test('WHAT[verification-system-006] turn observations keep session identity and forget pre-restart cursors', async () => {
  const events = {
    lastSeq: 0, all: [],
    async awaitEvent(predicate) {
      const found = this.all.find(predicate)
      if (!found) throw new Error('no matching event')
      return found
    },
  }
  const turns = createScenarioTurn({ events })
  const root = turns.start('root')
  const child = turns.start('child')
  assert.equal(turns.current('root'), root)
  assert.equal(turns.current('child'), child)
  events.all.push(
    { seq: 1, type: 'message.updated', sessionID: 'root', finishReason: 'stop' },
    { seq: 2, type: 'session.idle', sessionID: 'root' },
  )
  events.lastSeq = 2
  await root.awaitTerminal({ requireAssistantTerminal: false })
  assert.equal(root.activitySeq, 1)
  turns.clear()
  assert.equal(turns.current('root'), null)
  assert.equal(turns.current('child'), null)
})

test('WHAT[verification-system-006] alternative progress waits resolve the observed branch and release siblings', async () => {
  for (const winner of ['original.1', 'guarded.0']) {
    const signals = new StrictMockSignals()
    const waiting = signals.waitForAnyExpectation(['original.1', 'guarded.0'])
    signals.consume({ id: winner, permanent: true })
    assert.equal(await waiting, winner)
    assert.equal(signals._expectationWaiters.size, 0)
  }
})
