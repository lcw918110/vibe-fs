import assert from 'node:assert/strict'
import test from 'node:test'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import * as recoveryHost from '../../../dist/OpenCode/Host/SessionRecoveryHostSurface.js'
import * as routing from '../../../dist/OpenCode/Host/ModelRoutingSurface.js'
import * as ownership from '../../verification-system/tests/support/blogger-ownership.mjs'
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
  routing.acquireSharedExecutionAdmission(session, physical, 'blogger', 'blogger', null, 'normal')

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

// The emission side of the same contract: the REAL registered transform is
// driven through its public hook with a deterministic freeze refusal, and the
// refusal must report itself to the settlement owner so the exact execution
// settles and its exact capacity returns. The accepted execution is created by
// the real chat.message admission (HumanRoot); the durable main->Blogger link
// then makes the transform's request-kind path a companion path, so with no
// durable open request the plan freeze refuses with blogger-request-missing.
// The signal, the durable terminal and the shared-capacity release all stay
// production-owned.
test('WHAT[managed-chat-execution-015] the real transform refusal settles the exact execution and returns its capacity', async () => {
  await withExecutablePlugin(async (hooks, directory, createdIds, runtime) => {
    const main = 'ses-par015-emit-main'
    const blogger = 'ses-par015-emit-blogger'
    const physical = 'msg-par015-emit-blogger'

    // Real admission: the registered chat.message establishes the durable
    // Accepted execution and the exact capacity lease for this physical id.
    await acceptAuthorityRoot(runtime, blogger, 'blogger', physical)
    const opening = {
      message: { id: physical, sessionID: blogger, role: 'user', agent: 'blogger', time: { created: 3 } },
      parts: [{ type: 'text', text: '# Call the chronicle tool exactly once.' }],
    }
    await hooks['chat.message']({ sessionID: blogger, messageID: physical, agent: 'blogger' }, opening)

    const status = recoveryHost.journalExecutionStatus(runtime.journal, blogger, physical)
    assert.equal(status?.phase, 'Accepted', 'construction: chat.message must leave an accepted execution')

    const leaseCount = () =>
      routing.sharedCapacitySnapshot().executions.filter(
        (execution) => execution.sessionId === blogger && execution.physicalUserMessageId === physical,
      ).length
    assert.equal(leaseCount(), 1, 'construction: the accepted execution must hold its exact lease')

    // Durable main->Blogger link (the production CompanionBloggerLinked fact).
    await ownership.linkBlogger(runtime.journal, main, blogger)

    const messages = [
      { info: opening.message, parts: opening.parts },
      {
        info: {
          id: 'msg-par015-emit-blogger-run',
          sessionID: blogger,
          parentID: physical,
          role: 'assistant',
          agent: 'blogger',
          providerID: 'provider',
          modelID: 'blogger-model',
          time: { created: 4 },
        },
        parts: [],
      },
    ]
    await assert.rejects(
      () =>
        hooks['experimental.chat.messages.transform']({ sessionID: blogger }, { messages: structuredClone(messages) }),
      (error) => /blogger-request-missing/.test(String(error?.message ?? error)),
      'the refused start boundary must still surface the original hook failure',
    )

    assert.deepEqual(
      recoveryHost.journalExecutionStatus(runtime.journal, blogger, physical),
      { phase: 'Terminal', disposition: 'Failed' },
      'WHAT[managed-chat-execution-015]: the real refusal must settle the exact execution as a pre-provider Failed terminal',
    )
    assert.equal(leaseCount(), 0, 'WHAT[managed-chat-execution-015]: the exact capacity must return after the durable terminal')
  })
})
