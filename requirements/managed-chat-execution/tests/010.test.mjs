import assert from 'node:assert/strict'
import test from 'node:test'
import { mkdtempSync, readdirSync, readFileSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { setImmediate } from 'node:timers/promises'
import * as recoveryHost from '../../../dist/OpenCode/Host/SessionRecoveryHostSurface.js'
import * as status from '../../../dist/Execution/Session/ChatExecution/StatusSurface.js'
import * as routing from '../../../dist/OpenCode/Host/ModelRoutingSurface.js'
import { withExecutablePlugin, startPluginIncarnation } from '../../verification-system/tests/support/plugin-fixture.mjs'

const withHost = async (action) => {
  const directory = mkdtempSync(join(tmpdir(), 'wxs-exact-cancel-'))
  const host = await recoveryHost.bootRecoveryHost(directory, 'absent')
  try {
    await action(host)
  } finally {
    recoveryHost.disposeRecoveryHost(host)
    rmSync(directory, { recursive: true, force: true })
  }
}

test('WHAT[managed-chat-execution-010] actual recovery Host cancellation settles one exact execution and preserves its neighbours', async () => {
  await withHost(async (host) => {
    await recoveryHost.seedAccepted(host, 'ses-cancel', 'msg-before-provider')
    await recoveryHost.seedProviderStarted(host, 'ses-cancel', 'msg-started', 'provider-started')
    await recoveryHost.seedAccepted(host, 'ses-other', 'msg-before-provider')

    const before = recoveryHost.executionStatus(host, 'ses-cancel', 'msg-started')
    const other = recoveryHost.executionStatus(host, 'ses-other', 'msg-before-provider')
    assert.equal(before.phase, 'ProviderStarted')
    assert.equal(other.phase, 'Accepted')
    const cancelled = await recoveryHost.signalCancelled(host, 'ses-cancel', 'msg-before-provider')
    assert.equal(cancelled.lifecycle, 'Terminal')
    assert.equal(cancelled.disposition, 'Cancelled')
    assert.deepEqual(recoveryHost.executionStatus(host, 'ses-cancel', 'msg-started'), before)
    assert.deepEqual(recoveryHost.executionStatus(host, 'ses-other', 'msg-before-provider'), other)

    const replayed = await recoveryHost.signalCancelled(host, 'ses-cancel', 'msg-before-provider')
    assert.deepEqual(replayed, cancelled)
    const afterStart = await recoveryHost.signalCancelled(host, 'ses-cancel', 'msg-started')
    assert.equal(afterStart.lifecycle, 'Terminal')
    assert.equal(afterStart.disposition, 'Cancelled')
    assert.deepEqual(recoveryHost.executionStatus(host, 'ses-other', 'msg-before-provider'), other)
  })
})

// chat010 held-append oracles: the durable Terminal line count on the real
// disk and the exact capacity count from the real shared capacity snapshot.
const ndjsonFiles = (directory) => {
  const found = []
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    const path = join(directory, entry.name)
    if (entry.isDirectory()) found.push(...ndjsonFiles(path))
    else if (entry.name.endsWith('.ndjson')) found.push(path)
  }
  return found
}

const terminalLineCount = (directory) =>
  ndjsonFiles(directory)
    .flatMap((path) => readFileSync(path, 'utf8').split('\n'))
    .filter((line) => line.includes('["ChatExecution",["Terminal"')).length

const executionCount = (session, physical) =>
  routing.sharedCapacitySnapshot().executions.filter(
    (execution) => execution.sessionId === session && execution.physicalUserMessageId === physical,
  ).length

const acquireLease = (session, physical) =>
  routing.acquireSharedExecutionAdmission(session, physical, 'engineer', 'engineer', null, 'normal',
    { model: 'fixture/engineer-model', reasoning: '' })

const withPlugin = async (action) => {
  const workspace = mkdtempSync(join(tmpdir(), 'wxs-chat-drain-'))
  const incarnation = await startPluginIncarnation(workspace)
  try {
    await action()
  } finally {
    await incarnation.hooks.dispose()
    rmSync(workspace, { recursive: true, force: true })
  }
}

const withControlledHost = async (mode, action) => {
  const directory = mkdtempSync(join(tmpdir(), 'wxs-chat-drain-journal-'))
  const host = await recoveryHost.bootControlledRecoveryHost(directory, 'absent', mode)
  const pendingDrains = []
  let barrierReleased = false
  let actionFailure
  const terminalWriter = {
    trackDrain: (drain) => {
      pendingDrains.push(drain)
      return drain
    },
    releaseBarrier: () => {
      if (!barrierReleased) {
        recoveryHost.releaseTerminalBarrier(host)
        barrierReleased = true
      }
    },
  }
  try {
    await action(host, directory, terminalWriter)
  } catch (error) {
    actionFailure = { error }
    throw error
  } finally {
    try {
      if (mode === 'held') terminalWriter.releaseBarrier()
      const settled = await Promise.allSettled(pendingDrains)
      const failures = settled.filter((drain) => drain.status === 'rejected').map((drain) => drain.reason)
      if (!actionFailure && failures.length) throw new AggregateError(failures, 'Held terminal drain failed', { cause: failures[0] })
    } finally {
      recoveryHost.disposeRecoveryHost(host)
      rmSync(directory, { recursive: true, force: true })
    }
  }
}

// chat010 same-session multi-key fixture: the drained session carries one
// pre-provider Accepted key and one provider-started key; the decoy is a
// neighbouring execution in its own session.
const seedSessionKeys = async (host, session) => {
  await recoveryHost.seedAccepted(host, session, `msg-${session}-before-provider`)
  await recoveryHost.seedProviderStarted(host, session, `msg-${session}-started`, `provider-${session}`)
}

const acquireSessionLeases = async (session) => {
  await acquireLease(session, `msg-${session}-before-provider`)
  await acquireLease(session, `msg-${session}-started`)
}

const assertHeldSettlement = (session, directory, host) => {
  // The held terminal append parks the settlement: no durable terminal line
  // exists yet, the projection keeps the provider-started phase, and the
  // exact lease is still held.
  // routing [006]: a same-session newer physical atomically supersedes the
  // older admission, so only the started key holds a live lease.
  assert.equal(executionCount(session, `msg-${session}-before-provider`), 0)
  assert.equal(executionCount(session, `msg-${session}-started`), 1)
  assert.equal(terminalLineCount(directory), 0)
  assert.equal(recoveryHost.executionStatus(host, session, `msg-${session}-started`).phase, 'ProviderStarted')
}

const assertSettledSession = (session, directory, host) => {
  for (const physical of [`msg-${session}-before-provider`, `msg-${session}-started`]) {
    const settled = recoveryHost.executionStatus(host, session, physical)
    assert.equal(settled.phase, 'Terminal')
    assert.equal(settled.disposition, 'Cancelled')
    assert.equal(executionCount(session, physical), 0)
  }
  assert.equal(terminalLineCount(directory), 2)
}

test('WHAT[managed-chat-execution-010] public logical cancel and session delete enumerate every key and await durable terminal plus real capacity release, including held append (GAP-126)', async () => {
  await withPlugin(async () => {
    // Public logical cancel: the chat-side drain owner
    // (SessionRecoveryHost.SignalSession, the entry the runtime routes
    // SignalChatRecoverySession through) settles every key of the session.
    await withControlledHost('held', async (host, directory, terminalWriter) => {
      await seedSessionKeys(host, 'ses-cancel')
      await recoveryHost.seedProviderStarted(host, 'ses-preserved', 'msg-preserved', 'provider-preserved')
      await acquireSessionLeases('ses-cancel')
      await acquireLease('ses-preserved', 'msg-preserved')

      let completed = false
      const cancelled = terminalWriter.trackDrain(recoveryHost.signalSessionCancelled(host, 'ses-cancel'))
      cancelled.then(() => { completed = true }, () => { completed = true })
      await recoveryHost.awaitTerminalBarrier(host)
      await setImmediate()

      // A held terminal append parks the settlement: the public logical
      // cancel is not complete, no lease is returned, no terminal line.
      assert.equal(completed, false, 'the logical cancel must not complete while a terminal commit is held')
      assertHeldSettlement('ses-cancel', directory, host)
      assert.equal(executionCount('ses-preserved', 'msg-preserved'), 1)

      terminalWriter.releaseBarrier()
      await cancelled
      assert.equal(completed, true)

      // Every enumerated key now carries a durable Cancelled terminal and
      // its exact capacity is back; the neighbouring session keeps its lease
      // and its facts untouched.
      assertSettledSession('ses-cancel', directory, host)
      assert.equal(executionCount('ses-preserved', 'msg-preserved'), 1)
      assert.equal(recoveryHost.executionStatus(host, 'ses-preserved', 'msg-preserved').phase, 'ProviderStarted')
    })

    // Session delete: the same drain semantics through the delete drain
    // owner the runtime's DisposeSession awaits (PluginSessionScope.ClearSession).
    await withControlledHost('held', async (host, directory, terminalWriter) => {
      await seedSessionKeys(host, 'ses-delete')
      await recoveryHost.seedProviderStarted(host, 'ses-preserved', 'msg-preserved', 'provider-preserved')
      await acquireSessionLeases('ses-delete')
      await acquireLease('ses-preserved', 'msg-preserved')

      let completed = false
      const drained = terminalWriter.trackDrain(recoveryHost.clearSession(host, 'ses-delete'))
      drained.then(() => { completed = true }, () => { completed = true })
      await recoveryHost.awaitTerminalBarrier(host)
      await setImmediate()

      assert.equal(completed, false, 'the session delete must not complete while a terminal commit is held')
      assertHeldSettlement('ses-delete', directory, host)
      assert.equal(executionCount('ses-preserved', 'msg-preserved'), 1)

      terminalWriter.releaseBarrier()
      await drained
      assert.equal(completed, true)

      assertSettledSession('ses-delete', directory, host)
      assert.equal(executionCount('ses-preserved', 'msg-preserved'), 1)
      assert.equal(recoveryHost.executionStatus(host, 'ses-preserved', 'msg-preserved').phase, 'ProviderStarted')
    })
  })
})

test('WHAT[managed-chat-execution-010] actual Host session deletion settles every admitted key and preserves another session', async () => {
  let deliverEvent
  await withExecutablePlugin(async (hooks, directory, createdIds, runtime) => {
    const admit = (sessionId, physical) => hooks['chat.message']({ sessionID: sessionId, messageID: physical, agent: 'engineer' }, {
      message: { id: physical, sessionID: sessionId, role: 'user', agent: 'engineer', model: { providerID: 'fixture', modelID: 'engineer-model' } }, parts: [],
    })
    await admit('ses-deleted', 'msg-deleted-first')
    await admit('ses-deleted', 'msg-deleted-next')
    await admit('ses-preserved', 'msg-preserved')
    const preserved = status.query(runtime.journal, 'ses-preserved', 'msg-preserved')
    assert.deepEqual(preserved, { accepted: true, providerStarted: false, terminal: false, disposition: null })

    deliverEvent({ type: 'session.deleted', properties: { sessionID: 'ses-deleted' } })
    await hooks.dispose()

    for (const physical of ['msg-deleted-first', 'msg-deleted-next']) {
      assert.deepEqual(status.query(runtime.journal, 'ses-deleted', physical), {
        accepted: true, providerStarted: false, terminal: true, disposition: 'Cancelled',
      })
    }
    assert.deepEqual(status.query(runtime.journal, 'ses-preserved', 'msg-preserved'), preserved)
    const capacity = routing.sharedCapacitySnapshot()
    assert.equal(capacity.executions.some((execution) => execution.sessionId === 'ses-deleted'), false)
    assert.equal(capacity.tokens.some((token) => token.owner.sessionId === 'ses-deleted'), false)
    assert.equal(capacity.waiters.some((waiter) => waiter.sessionId === 'ses-deleted'), false)
  }, { events: { listen: (callback) => { deliverEvent = callback; return () => {} } } })
})
