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
  claimBloggerRequest,
  configureManagedPlugin,
  startPluginIncarnation,
  withRestartablePlugin,
} from '../../verification-system/tests/support/plugin-fixture.mjs'

// WHAT[provider-attempt-recovery-024]: when the load phase abandons a stale
// Blogger open request (no live flight in this process), the same-source
// executions that are Accepted without ProviderStarted must be decided with a
// typed pre-provider Failed terminal, and their exact capacity must be returned
// (a boot-time release is an idempotent no-op and is still requested).
//
// Failure-path questions:
//   1. what fails: the previous runtime left an open Blogger request and an
//      accepted execution that never reached the provider.
//   2. which result must hold afterwards: the same-source execution carries a
//      durable Terminal/Failed; the stale request is abandoned by the load
//      owner (crash-reconciliation-018/020, outside this Surface).
//   3. which cleanup must happen: the exact capacity lease leaves the shared
//      snapshot after the durable terminal; a repeated load is a no-op.
//   4. which side effect must never happen: no terminal or release for a
//      provider-started execution, no touching of unrelated sessions, no
//      fabricated execution or manual for a key without an accepted projection.
//
// The driving face is the registered recovery Surface (the same shape as the
// 023 idle sweep): the load phase enumerates the same-source keys and the
// production SessionRecoveryHost / ManagedChat / ModelRouting modules decide,
// commit and release.

const executionCount = (session, physical) =>
  routing.sharedCapacitySnapshot().executions.filter(
    (execution) => execution.sessionId === session && execution.physicalUserMessageId === physical,
  ).length

const acquireLease = (session, physical) =>
  routing.acquireSharedExecutionAdmission(session, physical, 'blogger', 'blogger', null, 'normal')

const withPlugin = async (action) => {
  const workspace = mkdtempSync(join(tmpdir(), 'wxs-par024-'))
  const incarnation = await startPluginIncarnation(workspace)
  try {
    await action()
  } finally {
    await incarnation.hooks.dispose()
    rmSync(workspace, { recursive: true, force: true })
  }
}

const withHost = async (action) => {
  const directory = mkdtempSync(join(tmpdir(), 'wxs-par024-journal-'))
  const host = await recoveryHost.bootRecoveryHost(directory, 'absent')
  try {
    await action(host)
  } finally {
    recoveryHost.disposeRecoveryHost(host)
    rmSync(directory, { recursive: true, force: true })
  }
}

const assertSweepSurface = () =>
  assert.equal(
    typeof recoveryHost.settleStaleBloggerAcceptedExecutions,
    'function',
    'WHAT[provider-attempt-recovery-024]: the stale-load same-source sweep must be registered on the recovery Surface',
  )

test('WHAT[provider-attempt-recovery-024] the stale-load sweep settles only the same-source accepted execution and returns its exact capacity', async () => {
  await withPlugin(async () => {
    await withHost(async (host) => {
      assertSweepSurface()

      const blogger = 'ses-par024-blogger'
      const physical = 'msg-par024-blogger'
      const decoy = 'ses-par024-decoy'
      const decoyPhysical = 'msg-par024-decoy'

      await recoveryHost.seedAccepted(host, blogger, physical)
      await acquireLease(blogger, physical)
      await recoveryHost.seedAccepted(host, decoy, decoyPhysical)
      await acquireLease(decoy, decoyPhysical)

      assert.equal(executionCount(blogger, physical), 1, 'construction: the same-source execution holds its exact lease')
      assert.equal(executionCount(decoy, decoyPhysical), 1, 'construction: the decoy execution holds its exact lease')

      const outcome = await recoveryHost.settleStaleBloggerAcceptedExecutions(host, blogger)

      assert.equal(outcome.settled, 1, 'the stale-load sweep must decide exactly the same-source accepted execution')
      assert.equal(outcome.alreadyTerminal, 0, 'the first sweep must not count an already-terminal execution')
      assert.deepEqual(
        recoveryHost.executionStatus(host, blogger, physical),
        { phase: 'Terminal', disposition: 'Failed' },
        'WHAT[provider-attempt-recovery-024]: the same-source execution must carry a durable Terminal/Failed',
      )
      assert.equal(executionCount(blogger, physical), 0, 'the exact capacity must return after the durable terminal')
      assert.deepEqual(
        recoveryHost.executionStatus(host, decoy, decoyPhysical),
        { phase: 'Accepted', disposition: null },
        'an unrelated execution must not be terminalized',
      )
      assert.equal(executionCount(decoy, decoyPhysical), 1, 'an unrelated execution must keep its exact lease')

      const repeated = await recoveryHost.settleStaleBloggerAcceptedExecutions(host, blogger)

      assert.equal(repeated.settled, 0, 'a repeated load must not settle a second time')
      assert.equal(repeated.alreadyTerminal, 1, 'a repeated load observes the existing terminal')
      assert.deepEqual(
        recoveryHost.executionStatus(host, blogger, physical),
        { phase: 'Terminal', disposition: 'Failed' },
        'a repeated load must not produce a second terminal fact',
      )
      assert.equal(executionCount(blogger, physical), 0, 'a repeated load must not double-release')
    })
  })
})

test('WHAT[provider-attempt-recovery-024] the stale-load sweep leaves a provider-started execution untouched', async () => {
  await withPlugin(async () => {
    await withHost(async (host) => {
      assertSweepSurface()

      const started = 'ses-par024-started'
      const startedPhysical = 'msg-par024-started'

      await recoveryHost.seedProviderStarted(host, started, startedPhysical, 'run-par024')
      await acquireLease(started, startedPhysical)

      const outcome = await recoveryHost.settleStaleBloggerAcceptedExecutions(host, started)

      assert.equal(outcome.settled, 0, 'a provider-started execution is not part of the pre-provider sweep')
      const status = recoveryHost.executionStatus(host, started, startedPhysical)
      assert.equal(status.phase, 'ProviderStarted', 'WHAT[provider-attempt-recovery-024]: a provider-started execution stays in the provider phase')
      assert.equal(executionCount(started, startedPhysical), 1, 'a provider-started execution keeps its exact lease')
    })
  })
})

test('WHAT[provider-attempt-recovery-024] the stale-load sweep fabricates nothing for a session without accepted executions', async () => {
  await withPlugin(async () => {
    await withHost(async (host) => {
      assertSweepSurface()

      const session = 'ses-par024-no-execution'
      const physical = 'msg-par024-no-execution'

      assert.equal(recoveryHost.executionStatus(host, session, physical), null, 'construction: no durable execution exists')

      const outcome = await recoveryHost.settleStaleBloggerAcceptedExecutions(host, session)

      assert.equal(outcome.settled, 0, 'a session without accepted executions has nothing to settle')
      assert.equal(outcome.alreadyTerminal, 0, 'nothing becomes terminal')
      assert.equal(recoveryHost.executionStatus(host, session, physical), null, 'no durable execution may be fabricated')
      assert.deepEqual(outcome.manuals, [], 'a key outside recovery ownership must not register a manual intervention')
    })
  })
})

// The load-phase wiring itself: incarnation A leaves a stale Blogger open
// request plus its same-source accepted execution; incarnation B's load
// normalization abandons the request (no live flight) and must decide the
// execution through the same production recovery path. The wait below is a
// bounded read of the durable projection (the load callback is a background
// task), not a timing decision of the product.
const waitForProjection = async (read, attempts = 400) => {
  for (let index = 0; index < attempts; index += 1) {
    const value = read()
    if (value !== undefined) return value
    await new Promise((resolve) => setTimeout(resolve, 10))
  }
  return undefined
}

test('WHAT[provider-attempt-recovery-024] the real load phase decides the same-source accepted execution of an abandoned stale Blogger request', async () => {
  await withRestartablePlugin(async (start, directory, runtime) => {
    const blogger = 'ses-par024-wire-blogger'
    const physical = 'msg-par024-wire-blogger'
    const decoy = 'ses-par024-wire-decoy'
    const decoyPhysical = 'msg-par024-wire-decoy'

    // --- Incarnation A: leave the stale open request and its accepted execution.
    const hooksA = await start()
    await configureManagedPlugin(hooksA)
    await runtime.withRuntime(async (journalRuntime) => {
      const all = { ...runtime, ...journalRuntime }

      const profile = await ownership.rootBlogger(all.journal, blogger, physical)
      const opening = {
        message: { id: physical, sessionID: blogger, role: 'user', agent: 'blogger', time: { created: 3 } },
        parts: [{ type: 'text', text: '# Call the chronicle tool exactly once.' }],
      }
      await hooksA['chat.message']({ sessionID: blogger, messageID: physical, agent: 'blogger' }, opening)

      const status = recoveryHost.journalExecutionStatus(all.journal, blogger, physical)
      assert.equal(status?.phase, 'Accepted', 'construction: incarnation A must leave an accepted execution')

      await ownership.rootBlogger(all.journal, decoy, decoyPhysical)
      const decoyOpening = {
        message: { id: decoyPhysical, sessionID: decoy, role: 'user', agent: 'blogger', time: { created: 3 } },
        parts: [{ type: 'text', text: 'decoy' }],
      }
      await hooksA['chat.message']({ sessionID: decoy, messageID: decoyPhysical, agent: 'blogger' }, decoyOpening)

      const claim = await claimBloggerRequest({
        runtime: all,
        mainSession: 'ses-par024-wire-main',
        bloggerSession: blogger,
        profile,
        dispatchPhysical: 'msg-par024-wire-dispatch',
        requestId: 'req-par024-wire',
      })
      // Release the process-local flight: the durable open request stays, exactly
      // the stale shape a dead runtime leaves behind.
      claim.dispose()
    })
    await runtime.stop(hooksA)

    // --- Incarnation B: the load normalization abandons the request and decides.
    const hooksB = await start()
    await configureManagedPlugin(hooksB)
    await runtime.withRuntime(async (journalRuntime) => {
      const all = { ...runtime, ...journalRuntime }

      const fresh = 'ses-par024-wire-activate'
      const freshPhysical = 'msg-par024-wire-activate'
      await acceptAuthorityRoot(all, fresh, 'manager', freshPhysical)
      await hooksB['chat.message'](
        { sessionID: fresh, messageID: freshPhysical, agent: 'manager' },
        {
          message: { id: freshPhysical, sessionID: fresh, role: 'user', agent: 'manager', time: { created: 3 } },
          parts: [{ type: 'text', text: 'activate durability' }],
        },
      )

      const settled = await waitForProjection(() => {
        const status = recoveryHost.journalExecutionStatus(all.journal, blogger, physical)
        return status?.phase === 'Terminal' ? status : undefined
      })

      assert.deepEqual(
        settled,
        { phase: 'Terminal', disposition: 'Failed' },
        'WHAT[provider-attempt-recovery-024]: the load phase must decide the same-source accepted execution of the abandoned stale request',
      )
      assert.deepEqual(
        recoveryHost.journalExecutionStatus(all.journal, decoy, decoyPhysical),
        { phase: 'Accepted', disposition: null },
        'an unrelated accepted execution must not be terminalized',
      )
    })
  })
})
