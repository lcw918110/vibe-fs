import assert from 'node:assert/strict'
import test from 'node:test'
import { mkdtempSync, readdirSync, readFileSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { setTimeout as sleep } from 'node:timers/promises'
import * as recoveryHost from '../../../dist/OpenCode/Host/SessionRecoveryHostSurface.js'
import * as routing from '../../../dist/OpenCode/Host/ModelRoutingSurface.js'
import { startPluginIncarnation } from '../../verification-system/tests/support/plugin-fixture.mjs'

// WHAT[managed-session-lifecycle-030]: a session-scoped cancel settlement must
// return the exact credit a retained continuation input holds, so the cancel
// path's drain converges. Field shape (2026-10-30): a delegated child was
// cancelled right after its provider started; the parent stayed in join and the
// next admission stalled behind a cancel/recovery pipeline whose drain could
// never observe a settled session — the terminal execution's credit stayed
// HeldForInput because its retained continuation input was only ever released
// by session deletion, never by the cancel settlement.

const ndjsonFiles = (directory) => {
  const found = []
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    const path = join(directory, entry.name)
    if (entry.isDirectory()) found.push(...ndjsonFiles(path))
    else if (entry.name.endsWith('.ndjson')) found.push(path)
  }
  return found
}

// Oracle: durable Terminal lines on the real disk — the settlement must stay
// exactly the one cancellation terminal; [030] invents no terminal.
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
  const workspace = mkdtempSync(join(tmpdir(), 'wxs-lifecycle-cancel-drain-'))
  const incarnation = await startPluginIncarnation(workspace)
  try {
    await action()
  } finally {
    await incarnation.hooks.dispose()
    rmSync(workspace, { recursive: true, force: true })
  }
}

const withHost = async (action) => {
  const directory = mkdtempSync(join(tmpdir(), 'wxs-lifecycle-cancel-drain-journal-'))
  const host = await recoveryHost.bootControlledRecoveryHost(directory, 'absent', 'committed')
  try {
    await action(host, directory)
  } finally {
    recoveryHost.disposeRecoveryHost(host)
    rmSync(directory, { recursive: true, force: true })
  }
}

// The cancelled session carries one accepted (pre-provider) execution whose
// credit a retained continuation input holds under a never-admitted next
// physical — the delayed exact return nothing but a settlement would replay.
const seedRetainedSession = async (host, session) => {
  await recoveryHost.seedAccepted(host, session, `msg-${session}-old`)
  const leased = await acquireLease(session, `msg-${session}-old`)
  routing.sharedRetainContinuationInput(leased.lease, `msg-${session}-new`)
}

test('WHAT[managed-session-lifecycle-030] logical cancel returns the retained exact credit and the drain converges', async () => {
  await withPlugin(async () => {
    await withHost(async (host, directory) => {
      await seedRetainedSession(host, 'ses-cancel')
      // An unrelated live session must stay untouched: the release is exact to
      // the cancelled session's own retentions, never session-wide.
      await recoveryHost.seedProviderStarted(host, 'ses-keep', 'msg-keep', 'provider-keep')
      await acquireLease('ses-keep', 'msg-keep')

      // The production drain await — the same one `drainTermination` and the
      // delete chain hang on — registered before the settlement.
      let settled = false
      const drained = recoveryHost.drainSession(host, 'ses-cancel')
      drained.then(() => { settled = true }, () => { settled = true })
      await sleep(50)
      assert.equal(settled, false, 'an unsettled session must not report drained')

      await recoveryHost.signalSessionCancelled(host, 'ses-cancel')

      const verdict = await Promise.race([
        drained.then(() => 'drained'),
        sleep(1500).then(() => 'hung'),
      ])
      assert.equal(verdict, 'drained', 'the cancel drain must converge after the session-scoped settlement')

      // The settlement is exactly the durable cancellation; the retained input
      // is gone and its held credit returned to the pool.
      const status = recoveryHost.executionStatus(host, 'ses-cancel', 'msg-ses-cancel-old')
      assert.equal(status.phase, 'Terminal')
      assert.equal(status.disposition, 'Cancelled')
      assert.equal(executionCount('ses-cancel', 'msg-ses-cancel-old'), 0)
      assert.equal(executionCount('ses-cancel', 'msg-ses-cancel-new'), 0)
      assert.equal(terminalLineCount(directory), 1)

      // A drain awaited after the fact takes the same fast path.
      await recoveryHost.drainSession(host, 'ses-cancel')

      // The unrelated live session keeps its lease and its facts.
      assert.equal(executionCount('ses-keep', 'msg-keep'), 1)
      assert.equal(recoveryHost.executionStatus(host, 'ses-keep', 'msg-keep').phase, 'ProviderStarted')
    })
  })
})
