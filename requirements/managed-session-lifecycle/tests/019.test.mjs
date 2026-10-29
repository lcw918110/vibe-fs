import assert from 'node:assert/strict'
import test from 'node:test'
import { mkdtempSync, readdirSync, readFileSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { setImmediate } from 'node:timers/promises'
import * as recovery from '../../../dist/Execution/Session/ChatExecution/RecoveryRuntimeSurface.js'
import * as recoveryHost from '../../../dist/OpenCode/Host/SessionRecoveryHostSurface.js'
import * as routing from '../../../dist/OpenCode/Host/ModelRoutingSurface.js'
import { startPluginIncarnation } from '../../verification-system/tests/support/plugin-fixture.mjs'



test('WHAT[managed-session-lifecycle-019] recovery interpreter requests exact reconciliation for held terminal resources', async () => {
  const result = await recovery.recoverScenarios([
    'TerminalResourceHeld',
    'TerminalResourceReleased',
  ])

  assert.deepEqual(result.decisions, ['ReconcilePhysical', 'Ignore'])
  assert.deepEqual(result.effects, ['ReconcilePhysical:ReleaseTerminalResource'])
})

const ndjsonFiles = (directory) => {
  const found = []
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    const path = join(directory, entry.name)
    if (entry.isDirectory()) found.push(...ndjsonFiles(path))
    else if (entry.name.endsWith('.ndjson')) found.push(path)
  }
  return found
}

// Oracle: count durable Terminal lines on the real disk, not an observer.
const terminalLineCount = (directory) =>
  ndjsonFiles(directory)
    .flatMap((path) => readFileSync(path, 'utf8').split('\n'))
    .filter((line) => line.includes('["ChatExecution",["Terminal"')).length

// Oracle: the real shared capacity snapshot, exact owner only.
const executionCount = (session, physical) =>
  routing.sharedCapacitySnapshot().executions.filter(
    (execution) => execution.sessionId === session && execution.physicalUserMessageId === physical,
  ).length

const acquireLease = (session, physical) =>
  routing.acquireSharedExecutionAdmission(session, physical, 'engineer', 'engineer', null, 'normal',
    { model: 'fixture/engineer-model', reasoning: '' })

const withPlugin = async (action) => {
  const workspace = mkdtempSync(join(tmpdir(), 'wxs-lifecycle-drain-'))
  const incarnation = await startPluginIncarnation(workspace)
  try {
    await action()
  } finally {
    await incarnation.hooks.dispose()
    rmSync(workspace, { recursive: true, force: true })
  }
}

const withHost = async (mode, action) => {
  const directory = mkdtempSync(join(tmpdir(), 'wxs-lifecycle-drain-journal-'))
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
const seedDrainedSession = async (host, session) => {
  await recoveryHost.seedAccepted(host, session, `msg-${session}-before-provider`)
  await recoveryHost.seedProviderStarted(host, session, `msg-${session}-started`, `provider-${session}`)
}

const acquireSessionLeases = async (session) => {
  await acquireLease(session, `msg-${session}-before-provider`)
  await acquireLease(session, `msg-${session}-started`)
}

test('WHAT[managed-session-lifecycle-019] actual session delete waits for every exact terminal commit and capacity release before completing (GAP-126)', async () => {
  await withPlugin(async () => {
    await withHost('held', async (host, directory, terminalWriter) => {
      await seedDrainedSession(host, 'ses-delete')
      await recoveryHost.seedProviderStarted(host, 'ses-preserved', 'msg-preserved', 'provider-preserved')
      await acquireSessionLeases('ses-delete')
      await acquireLease('ses-preserved', 'msg-preserved')

      let completed = false
      const drained = terminalWriter.trackDrain(recoveryHost.clearSession(host, 'ses-delete'))
      drained.then(() => { completed = true }, () => { completed = true })
      await recoveryHost.awaitTerminalBarrier(host)
      await setImmediate()

      // The held terminal append parks the drain writer: the delete
      // completion is not published, no exact lease is returned early, and
      // no durable terminal line exists yet.
      assert.equal(completed, false, 'the delete completion promise must not publish while a terminal commit is held')
      // routing [006]: a same-session newer physical atomically supersedes the
      // older admission, so only the started key holds a live lease.
      assert.equal(executionCount('ses-delete', 'msg-ses-delete-before-provider'), 0)
      assert.equal(executionCount('ses-delete', 'msg-ses-delete-started'), 1)
      assert.equal(terminalLineCount(directory), 0)
      assert.equal(recoveryHost.executionStatus(host, 'ses-delete', 'msg-ses-delete-started').phase, 'ProviderStarted')
      assert.equal(executionCount('ses-preserved', 'msg-preserved'), 1)

      terminalWriter.releaseBarrier()
      await drained
      assert.equal(completed, true)

      // Every key of the deleted session now carries a durable Cancelled
      // terminal and its exact capacity is back; the neighbouring session
      // keeps its lease and its facts untouched.
      for (const physical of ['msg-ses-delete-before-provider', 'msg-ses-delete-started']) {
        const status = recoveryHost.executionStatus(host, 'ses-delete', physical)
        assert.equal(status.phase, 'Terminal')
        assert.equal(status.disposition, 'Cancelled')
        assert.equal(executionCount('ses-delete', physical), 0)
      }
      assert.equal(terminalLineCount(directory), 2)
      assert.equal(executionCount('ses-preserved', 'msg-preserved'), 1)
      assert.equal(recoveryHost.executionStatus(host, 'ses-preserved', 'msg-preserved').phase, 'ProviderStarted')
    })
  })
})

test('WHAT[managed-session-lifecycle-019] actual logical cancel waits for every key settlement and exact capacity release (GAP-126)', async () => {
  await withPlugin(async () => {
    await withHost('held', async (host, directory, terminalWriter) => {
      await seedDrainedSession(host, 'ses-cancel')
      await recoveryHost.seedProviderStarted(host, 'ses-preserved', 'msg-preserved', 'provider-preserved')
      await acquireSessionLeases('ses-cancel')
      await acquireLease('ses-preserved', 'msg-preserved')

      let completed = false
      const cancelled = terminalWriter.trackDrain(recoveryHost.signalSessionCancelled(host, 'ses-cancel'))
      cancelled.then(() => { completed = true }, () => { completed = true })
      await recoveryHost.awaitTerminalBarrier(host)
      await setImmediate()

      // The held terminal append parks the first settlement: the logical
      // cancel is not complete, no lease is returned, no terminal line.
      assert.equal(completed, false, 'the logical cancel must not complete while a terminal commit is held')
      // routing [006]: same-session supersession — see the delete case above.
      assert.equal(executionCount('ses-cancel', 'msg-ses-cancel-before-provider'), 0)
      assert.equal(executionCount('ses-cancel', 'msg-ses-cancel-started'), 1)
      assert.equal(terminalLineCount(directory), 0)
      assert.equal(recoveryHost.executionStatus(host, 'ses-cancel', 'msg-ses-cancel-started').phase, 'ProviderStarted')

      terminalWriter.releaseBarrier()
      await cancelled
      assert.equal(completed, true)

      for (const physical of ['msg-ses-cancel-before-provider', 'msg-ses-cancel-started']) {
        const status = recoveryHost.executionStatus(host, 'ses-cancel', physical)
        assert.equal(status.phase, 'Terminal')
        assert.equal(status.disposition, 'Cancelled')
        assert.equal(executionCount('ses-cancel', physical), 0)
      }
      assert.equal(terminalLineCount(directory), 2)
      assert.equal(executionCount('ses-preserved', 'msg-preserved'), 1)
      assert.equal(recoveryHost.executionStatus(host, 'ses-preserved', 'msg-preserved').phase, 'ProviderStarted')
    })
  })
})

test('WHAT[managed-session-lifecycle-019] uncertain terminal commit keeps cancel and delete fail closed without release (GAP-126)', async () => {
  await withPlugin(async () => {
    await withHost('commitUnknown', async (host, directory) => {
      await seedDrainedSession(host, 'ses-uncertain')
      await acquireSessionLeases('ses-uncertain')

      await assert.rejects(recoveryHost.clearSession(host, 'ses-uncertain'))

      // Unknown stays unknown: no release, no terminal line, no fabricated
      // terminal disposition in the durable projection.
      // routing [006]: same-session supersession — see the delete case above.
      assert.equal(executionCount('ses-uncertain', 'msg-ses-uncertain-before-provider'), 0)
      assert.equal(executionCount('ses-uncertain', 'msg-ses-uncertain-started'), 1)
      assert.equal(terminalLineCount(directory), 0)
      assert.equal(recoveryHost.executionStatus(host, 'ses-uncertain', 'msg-ses-uncertain-started').phase, 'ProviderStarted')
    })

    await withHost('commitUnknown', async (host, directory) => {
      await seedDrainedSession(host, 'ses-uncertain')
      await acquireSessionLeases('ses-uncertain')

      await assert.rejects(recoveryHost.signalSessionCancelled(host, 'ses-uncertain'))

      assert.equal(executionCount('ses-uncertain', 'msg-ses-uncertain-started'), 1)
      assert.equal(terminalLineCount(directory), 0)
      assert.equal(recoveryHost.executionStatus(host, 'ses-uncertain', 'msg-ses-uncertain-started').phase, 'ProviderStarted')
    })
  })
})
