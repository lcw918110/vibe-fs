import assert from 'node:assert/strict'
import test from 'node:test'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { promisify } from 'node:util'
import { execFile } from 'node:child_process'
import { integrationTest } from '../../verification-system/tests/support/tier-gate.mjs'
import * as recoveryHost from '../../../dist/OpenCode/Host/SessionRecoveryHostSurface.js'
import * as routing from '../../../dist/OpenCode/Host/ModelRoutingSurface.js'
import {
  acceptAuthorityRoot,
  startPluginIncarnation,
  withExecutablePlugin,
} from '../../verification-system/tests/support/plugin-fixture.mjs'

// WHAT[managed-chat-execution-015]: when the transform refuses the provider
// start boundary for an exact execution (attempt plan freeze failure), the
// rejection must be decided as a typed pre-provider Failed terminal and the
// exact capacity fence must be returned after the durable commit.
//
// Failure-path questions:
//   1. what fails: the provider start boundary is refused while the exact
//      execution is Accepted and has never reached ProviderStarted.
//   2. which result must hold afterwards: durable Terminal/Failed for the
//      exact key.
//   3. which cleanup must happen: the exact capacity fence leaves the shared
//      snapshot only after the durable terminal is committed.
//   4. which side effect must never happen: no settlement for a key without
//      an accepted projection, no fabricated manual registration for it, no
//      session-wide release, and no touching of unrelated executions.
//
// The driving face is the registered recovery Surface (the same shape as the
// 023 session-idle sweep): the signal carries the exact key plus the typed
// reason code; settlement, terminal fold and release stay owned by the
// production SessionRecoveryHost / ManagedChat / ModelRouting modules.

const executionCount = (session, physical) =>
  routing.sharedCapacitySnapshot().executions.filter(
    (execution) => execution.sessionId === session && execution.physicalUserMessageId === physical,
  ).length

const acquireLease = (session, physical) =>
  routing.acquireSharedExecutionAdmission(session, physical, 'engineer', 'engineer', null, 'normal')

const withPlugin = async (action) => {
  const workspace = mkdtempSync(join(tmpdir(), 'wxs-chat015-'))
  const incarnation = await startPluginIncarnation(workspace)
  try {
    await action()
  } finally {
    await incarnation.hooks.dispose()
    rmSync(workspace, { recursive: true, force: true })
  }
}

const withHost = async (action) => {
  const directory = mkdtempSync(join(tmpdir(), 'wxs-chat015-journal-'))
  const host = await recoveryHost.bootRecoveryHost(directory, 'absent')
  try {
    await action(host)
  } finally {
    recoveryHost.disposeRecoveryHost(host)
    rmSync(directory, { recursive: true, force: true })
  }
}

test('WHAT[managed-chat-execution-015] a provider-start-boundary rejection terminalizes the exact accepted execution as Failed and returns its exact capacity', async () => {
  await withPlugin(async () => {
    await withHost(async (host) => {
      assert.equal(
        typeof recoveryHost.signalProviderStartBoundaryRejected,
        'function',
        'WHAT[managed-chat-execution-015]: the boundary-rejection signal must be registered on the recovery Surface',
      )

      const session = 'ses-par015-rejected'
      const physical = 'msg-par015-rejected'
      const decoySession = 'ses-par015-decoy'
      const decoyPhysical = 'msg-par015-decoy'

      await recoveryHost.seedAccepted(host, session, physical)
      await acquireLease(session, physical)
      await recoveryHost.seedAccepted(host, decoySession, decoyPhysical)
      await acquireLease(decoySession, decoyPhysical)

      assert.equal(executionCount(session, physical), 1, 'construction: the rejected execution holds its exact lease')
      assert.equal(executionCount(decoySession, decoyPhysical), 1, 'construction: the decoy execution holds its exact lease')

      const outcome = await recoveryHost.signalProviderStartBoundaryRejected(
        host,
        session,
        physical,
        'frozen-attempt-plan-missing',
      )

      assert.equal(outcome.result, 'terminalized', 'the rejection must decide the exact accepted execution')
      const status = recoveryHost.executionStatus(host, session, physical)
      assert.equal(status.phase, 'Terminal', 'the rejected execution must carry a durable terminal')
      assert.equal(status.disposition, 'Failed', 'the pre-provider rejection disposition is Failed, never Cancelled')
      assert.equal(executionCount(session, physical), 0, 'the exact capacity fence must be returned after the durable terminal')
      assert.equal(executionCount(decoySession, decoyPhysical), 1, 'an unrelated execution must keep its exact lease')
      assert.equal(
        recoveryHost.executionStatus(host, decoySession, decoyPhysical).phase,
        'Accepted',
        'an unrelated execution must not be terminalized',
      )

      const repeated = await recoveryHost.signalProviderStartBoundaryRejected(
        host,
        session,
        physical,
        'frozen-attempt-plan-missing',
      )
      assert.equal(repeated.result, 'already-terminal', 'a repeated rejection is idempotent')
      assert.equal(executionCount(session, physical), 0, 'a repeated rejection must not double-release')
    })
  })
})

test('WHAT[managed-chat-execution-015] a boundary rejection without an accepted projection reports no-execution and fabricates nothing', async () => {
  await withPlugin(async () => {
    await withHost(async (host) => {
      assert.equal(
        typeof recoveryHost.signalProviderStartBoundaryRejected,
        'function',
        'WHAT[managed-chat-execution-015]: the boundary-rejection signal must be registered on the recovery Surface',
      )

      const session = 'ses-par015-no-accepted'
      const physical = 'msg-par015-no-accepted'

      assert.equal(
        recoveryHost.executionStatus(host, session, physical),
        null,
        'construction: no durable execution exists for this key',
      )

      const outcome = await recoveryHost.signalProviderStartBoundaryRejected(
        host,
        session,
        physical,
        'accepted-execution-missing',
      )

      assert.equal(outcome.result, 'no-execution', 'a key without an accepted projection must not be settled')
      assert.equal(recoveryHost.executionStatus(host, session, physical), null, 'no durable execution may be fabricated')
      assert.deepEqual(outcome.manuals, [], 'a key outside recovery ownership must not register a manual intervention')
    })
  })
})

integrationTest('WHAT[managed-chat-execution-015] a real Host admission failure reaches and settles the real parent join', { timeout: 120000 }, async () => {
  const script = fileURLToPath(new URL('./support/run-pre-provider-join-smoke.mjs', import.meta.url))
  await promisify(execFile)(process.execPath, [script], {
    timeout: 110000, maxBuffer: 4 * 1024 * 1024,
  })
})
