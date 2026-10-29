import test from 'node:test'
import assert from 'node:assert/strict'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import * as routing from '../../../dist/OpenCode/Host/ModelRoutingSurface.js'
import * as sessions from '../../../dist/OpenCode/Host/SessionsSurface.js'
import * as managerWorkflow from '../../../dist/Mission/Manager/WorkflowSurface.js'
import * as quiescence from '../../../dist/OpenCode/Host/QuiescenceSurface.js'
import * as dispatch from '../../../dist/Interaction/Dispatch/DispatchSurface.js'
import * as journal from '../../../dist/Persistence/Journal/Surface.js'
import * as lifecycle from '../../../dist/Mission/Lifecycle/Surface.js'

// Field incident 2026-10-29 05:23:50 JST (Manager ses_e7ed7cdb2ffe…, OpenCode 1.19.4):
// the Manager ended a turn with prose (msg_1842ec5e0, finish=stop). The exact completed
// assistant minted the idle permit, so the ManagerGuard continuation msg_1842eecaa was
// posted 87 ms after `exiting loop` while the Host runner was still finishing that run.
// OpenCode's runner joins a prompt that arrives in that window to the finishing run: the
// user message is stored, no new loop starts (no `loop step=0` ever followed), and the
// later session.idle is deduped against the already-claimed nudge. The Manager stood
// still for 1.5 h.
const pending = Symbol('pending')
const settledWithin = (promise, ms) =>
  Promise.race([promise.then(() => 'settled'), new Promise(resolve => setTimeout(() => resolve(pending), ms))])

test('WHAT[host-boundary-037] the SDK port reads runner busy from Host session status', async () => {
  const calls = []
  let statuses = { 'ses-busy': { type: 'busy' }, 'ses-retry': { type: 'retry', attempt: 1 } }
  const port = routing.createSdkClientPort({ session: {
    promptAsync: async () => ({}),
    status: async (args) => { calls.push(args); return { data: statuses } },
  } })
  assert.deepEqual(await routing.runnerBusy(port, 'ses-busy', '/work'), { ok: true, busy: true, error: null })
  assert.deepEqual(await routing.runnerBusy(port, 'ses-retry', '/work'), { ok: true, busy: true, error: null })
  assert.deepEqual(await routing.runnerBusy(port, 'ses-idle', '/work'), { ok: true, busy: false, error: null },
    'the Host status map lists only non-idle sessions')
  assert.equal(calls[0].query.directory, '/work', 'status is read from the session workspace instance')
  statuses = { 'ses-busy': { type: 'idle' } }
  assert.equal((await routing.runnerBusy(port, 'ses-busy', '/work')).busy, false)

  const rejected = routing.createSdkClientPort({ session: {
    promptAsync: async () => ({}), status: async () => ({ error: { name: 'BadRequest' } }),
  } })
  assert.equal((await routing.runnerBusy(rejected, 'ses-busy', '/work')).ok, false)

  const noStatus = routing.createSdkClientPort({ session: { promptAsync: async () => ({}) } })
  assert.deepEqual(await routing.runnerBusy(noStatus, 'ses-busy', '/work'), { ok: true, busy: false, error: null },
    'a Host without a status API is treated as idle: the send keeps its old immediacy')
})

test('WHAT[host-boundary-037] the Host session port waits until the runner has left its run', async () => {
  let busy = true
  const reads = []
  const waiting = sessions.awaitRunnerIdle((sessionId) => { reads.push(sessionId); return busy }, 'ses-finishing')
  assert.equal(await settledWithin(waiting, 150), pending, 'a busy runner holds the idle continuation')
  assert.ok(reads.length >= 2, 'the wait keeps reading Host status')
  assert.ok(reads.every(id => id === 'ses-finishing'))
  busy = false
  assert.equal(await settledWithin(waiting, 2000), 'settled', 'runner idle releases the continuation')

  assert.equal(await settledWithin(sessions.awaitRunnerIdle(() => false, 'ses-idle'), 50), 'settled')
  assert.equal(await settledWithin(sessions.awaitRunnerIdle(() => { throw new Error('status down') }, 'ses-x'), 200),
    'settled', 'an unreadable status degrades to the immediate send')
})

test('WHAT[host-boundary-037] a Manager idle continuation is sent only after the Host runner is idle', async () => {
  const base = mkdtempSync(join(tmpdir(), 'wxs-runner-idle-037-'))
  let opened
  try {
    opened = await journal.JournalSurface_bootWithWriterId(base, 'writer-037', 'rt-037', 4242, '2026-01-01T00:00:00Z')
    assert.equal(opened.ok, true, JSON.stringify(opened.error))
    const session = 'ses-manager-runner-idle-037'
    const rootPhysical = 'msg-manager-root-037'
    const root = await dispatch.acceptHumanRoot(opened.journal, session, rootPhysical, 'manager')
    assert.equal(root.ok, true, root.error)
    const surface = lifecycle.LifecycleSurface ?? lifecycle
    const opening = await surface.submitReview(base, session, 'open-' + session, 'Manager', 'run', 'open',
      [{ key: 'opened', value: 'do' }], [], '')
    assert.equal(opening.kind, 'Reviewed', opening.reason)

    const gate = quiescence.create()
    quiescence.beginAttempt(gate, session)
    const permit = quiescence.observeIdle(gate, session)
    const order = []
    const runnerIdle = Promise.withResolvers()
    const port = {
      SubscribeTerminal: () => ({ Dispose() {} }),
      AwaitRunnerIdle: (sessionId) => { order.push(`await:${sessionId}`); return runnerIdle.promise },
      SendPrompt: async (sessionId) => {
        order.push(`send:${sessionId}`)
        return dispatch.admittedWithReceipt('receipt-037')
      },
    }
    const observed = managerWorkflow.observeIdle(port, opened.journal, gate, permit, session, rootPhysical,
      rootPhysical, 'provider-run-037', base)
    assert.equal(await settledWithin(observed, 150), pending)
    assert.deepEqual(order, [`await:${session}`], 'the nudge is not posted into a run that is still finishing')
    runnerIdle.resolve()
    await observed
    assert.deepEqual(order, [`await:${session}`, `send:${session}`], 'runner idle lets the same nudge start a new run')
    assert.deepEqual(quiescence.tryConsume(gate, permit), { accepted: false, failure: 'AlreadyConsumed' })
  } finally {
    try {
      if (opened?.ok) journal.JournalSurface_dispose(opened.journal)
    } finally {
      rmSync(base, { recursive: true, force: true })
    }
  }
})
