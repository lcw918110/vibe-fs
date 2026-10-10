import assert from 'node:assert/strict'
import test from 'node:test'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import * as recoveryHost from '../../../dist/OpenCode/Host/SessionRecoveryHostSurface.js'
import * as routing from '../../../dist/OpenCode/Host/ModelRoutingSurface.js'
import * as ownership from '../../verification-system/tests/support/blogger-ownership.mjs'
import { startPluginIncarnation } from '../../verification-system/tests/support/plugin-fixture.mjs'

// WHAT[managed-session-lifecycle-027]: closing a session scope must cancel the
// continuation-input retention the scope still owns. When an exact credit is
// held only by a retained continuation input (the `HeldForInput` shape of
// execution-model-routing-006), the close must run the retention's own
// cleanup — cancel the retained input, then complete the delayed exact
// release — instead of leaving the credit in shared capacity forever.
//
// Driving face: recoveryHost.clearSession is the same delete-drain owner the
// runtime's DisposeSession awaits (SessionRecoveryHostSurface.clearSession).
// Both the Main close (linked-leaf cascade, WHAT[managed-session-lifecycle-026])
// and the leaf's own close go through PluginSessionScope.ClearSession, so the
// retention cleanup must be observable through it.
//
// Construction: the retained-input window is created through the exact
// ModelRouting surface operation the production HostSignalBootstrap path uses
// (`retainContinuationInput`, EMR-006). The shared runtime is the one
// clearSession releases against, so the construction calls the shared wrapper
// (`sharedRetainContinuationInput`): the isolated wrapper only reaches an
// isolated runtime and cannot build this window on the process-shared runtime
// that clearSession owns. This shared wrapper is the test-constructible face of
// the same operation already executed on the shared runtime in production.
//
// Oracle: the real shared capacity snapshot, exact owner only (mirrors 026).
// `heldPhysicalReleases` / `continuationInputs` are process-internal, so the
// observable consequence is the exact credit's presence in shared capacity.
const executionCount = (session, physical) =>
  routing.sharedCapacitySnapshot().executions.filter(
    (execution) => execution.sessionId === session && execution.physicalUserMessageId === physical,
  ).length

const withPlugin = async (action) => {
  const workspace = mkdtempSync(join(tmpdir(), 'wxs-lifecycle-leaf-retained-'))
  const incarnation = await startPluginIncarnation(workspace)
  try {
    await action()
  } finally {
    await incarnation.hooks.dispose()
    rmSync(workspace, { recursive: true, force: true })
  }
}

const withHost = async (action) => {
  const directory = mkdtempSync(join(tmpdir(), 'wxs-lifecycle-leaf-retained-journal-'))
  const host = await recoveryHost.bootRecoveryHost(directory, 'absent')
  try {
    await action(host)
  } finally {
    recoveryHost.disposeRecoveryHost(host)
    rmSync(directory, { recursive: true, force: true })
  }
}

// Durable Main -> leaf link (the production CompanionBloggerLinked fact) plus
// one admitted execution holding an exact committed lease on the shared
// runtime. Returns the opaque lease token used to retain a continuation input.
const seedLinkedLeafExecution = async (host, main, leaf, physical) => {
  await ownership.linkBlogger(host.Journal, main, leaf)
  await recoveryHost.seedAccepted(host, leaf, physical)

  const acquisition = await routing.acquireSharedExecutionAdmission(
    leaf,
    physical,
    'engineer',
    'engineer',
    null,
    'normal',
  )
  assert.equal(acquisition.kind, 'Acquired', 'construction: the leaf execution must acquire its exact lease')

  const target = routing.sharedExecutionAdmissionTarget(acquisition.lease)
  const committed = routing.commitSharedExecutionAdmission(acquisition.lease, {
    sessionId: leaf,
    physicalUserMessageId: physical,
    role: 'engineer',
    participant: 'engineer',
    target,
  })
  assert.ok(
    ['Applied', 'AlreadyApplied'].includes(committed.kind),
    'construction: the leaf exact lease must commit before the retention is built',
  )
  return acquisition.lease
}

// The retained-input window: an accepted-but-not-yet-selected material key
// owns the old exact credit. Retaining it then releasing the old physical
// execution must produce `HeldForInput` (the delayed return), never a release.
const retainGuidance = (token, guidance) => {
  assert.equal(
    typeof routing.sharedRetainContinuationInput,
    'function',
    'the shared runtime retention wrapper must exist: production retains on the process-shared runtime (HostSignalBootstrap PublishInput), and clearSession releases against that same runtime',
  )
  routing.sharedRetainContinuationInput(token, guidance)
}

test('WHAT[managed-session-lifecycle-027] closing the Main cancels the linked leaf retention and returns the held exact credit', async () => {
  await withPlugin(async () => {
    await withHost(async (host) => {
      const main = 'ses-main-retained'
      const leaf = 'ses-blogger-retained'
      const physical = 'msg-blogger-retained'
      const guidance = 'msg-blogger-retained-guidance'
      const decoyMain = 'ses-main-retained-decoy'
      const decoyLeaf = 'ses-blogger-retained-decoy'
      const decoyPhysical = 'msg-blogger-retained-decoy'
      const decoyGuidance = 'msg-blogger-retained-decoy-guidance'

      const token = await seedLinkedLeafExecution(host, main, leaf, physical)
      retainGuidance(token, guidance)
      assert.deepEqual(
        routing.releasePhysical(leaf, physical),
        { kind: 'HeldForInput' },
        'construction: with a retained continuation input the old exact release must be delayed, never applied',
      )
      assert.equal(
        executionCount(leaf, physical),
        1,
        'construction: the held credit must still be visible in shared capacity before the Main closes',
      )

      const decoyToken = await seedLinkedLeafExecution(host, decoyMain, decoyLeaf, decoyPhysical)
      retainGuidance(decoyToken, decoyGuidance)
      assert.deepEqual(
        routing.releasePhysical(decoyLeaf, decoyPhysical),
        { kind: 'HeldForInput' },
        'construction: the decoy retained input must also delay its old exact release',
      )
      assert.equal(executionCount(decoyLeaf, decoyPhysical), 1, 'construction: the decoy held credit must be visible')

      await recoveryHost.clearSession(host, main)

      assert.equal(
        executionCount(leaf, physical),
        0,
        'WHAT[managed-session-lifecycle-027]: closing the Main must cancel the linked leaf retention and return the delayed exact credit; a HeldForInput credit must not remain in shared capacity',
      )
      assert.equal(
        executionCount(decoyLeaf, decoyPhysical),
        1,
        'an unrelated Main close must not touch another held leaf retention',
      )
    })
  })
})

test('WHAT[managed-session-lifecycle-027] closing the leaf itself cancels its own retention and returns the held exact credit', async () => {
  await withPlugin(async () => {
    await withHost(async (host) => {
      const main = 'ses-main-retained-leaf-close'
      const leaf = 'ses-blogger-retained-leaf-close'
      const physical = 'msg-blogger-retained-leaf-close'
      const guidance = 'msg-blogger-retained-leaf-close-guidance'

      const token = await seedLinkedLeafExecution(host, main, leaf, physical)
      retainGuidance(token, guidance)
      assert.deepEqual(
        routing.releasePhysical(leaf, physical),
        { kind: 'HeldForInput' },
        'construction: the leaf retention must delay the old exact release',
      )

      // The leaf itself is recursively deleted: no Main cascade runs here, so
      // the scope close on the leaf must cancel its own retention.
      await recoveryHost.clearSession(host, leaf)

      assert.equal(
        executionCount(leaf, physical),
        0,
        'WHAT[managed-session-lifecycle-027]: closing the leaf scope must cancel its own retention and return the delayed exact credit',
      )
    })
  })
})

test('WHAT[managed-session-lifecycle-027] repeated scope close remains idempotent for the cancelled retention', async () => {
  await withPlugin(async () => {
    await withHost(async (host) => {
      const main = 'ses-main-retained-repeat'
      const leaf = 'ses-blogger-retained-repeat'
      const physical = 'msg-blogger-retained-repeat'
      const guidance = 'msg-blogger-retained-repeat-guidance'

      const token = await seedLinkedLeafExecution(host, main, leaf, physical)
      retainGuidance(token, guidance)
      assert.deepEqual(routing.releasePhysical(leaf, physical), { kind: 'HeldForInput' }, 'construction: the retention must delay the old exact release')

      await recoveryHost.clearSession(host, main)
      assert.equal(executionCount(leaf, physical), 0, 'the first Main close must return the held exact credit')

      // The repeated close must resolve cleanly: the retention is already
      // cancelled, so the second pass is a no-op rather than a second release
      // side effect or a thrown ownership violation.
      await recoveryHost.clearSession(host, main)
      assert.equal(executionCount(leaf, physical), 0, 'the repeated close must not leave a second held credit')
    })
  })
})

// A Conflict on the delayed return: the old execution settles on the
// pre-provider path while its material is still retained, so the replay's
// physical-completion release meets the opposite terminal. The ownership
// violation must surface as a raised error, never be swallowed as "handled"
// while the credit is still held.
test('WHAT[managed-session-lifecycle-027] a rejected delayed release surfaces as an ownership violation instead of silently dropping the credit', async () => {
  await withPlugin(async () => {
    await withHost(async (host) => {
      const main = 'ses-main-retained-conflict'
      const leaf = 'ses-blogger-retained-conflict'
      const physical = 'msg-blogger-retained-conflict'
      const guidance = 'msg-blogger-retained-conflict-guidance'

      await ownership.linkBlogger(host.Journal, main, leaf)
      await recoveryHost.seedAccepted(host, leaf, physical)

      const acquisition = await routing.acquireSharedExecutionAdmission(
        leaf,
        physical,
        'engineer',
        'engineer',
        null,
        'normal',
      )
      assert.equal(acquisition.kind, 'Acquired', 'construction: the conflict fixture must hold its exact lease')

      // Pending lease: the pre-provider settlement below only accepts the
      // pending shape, so this fixture deliberately skips the commit.
      const observed = {
        sessionId: leaf,
        physicalUserMessageId: physical,
        role: 'engineer',
        participant: 'engineer',
        target: routing.sharedExecutionAdmissionTarget(acquisition.lease),
      }
      retainGuidance(acquisition.lease, guidance)
      assert.deepEqual(
        routing.releasePhysical(leaf, physical),
        { kind: 'HeldForInput' },
        'construction: the retention must delay the old exact release',
      )

      // The old execution settles pre-provider while the material is retained:
      // the capacity lifecycle moves to Releasing(BeforeProvider) -> Released.
      const settled = routing.releaseSharedExecutionAdmissionBeforeProvider(acquisition.lease, observed)
      assert.equal(settled.kind, 'Applied', 'construction: the pending old execution must settle on the before-provider path')

      // Cancelling the retention replays the delayed return as a
      // physical-completion release against that opposite terminal: the
      // ownership violation must be raised, not silently dropped.
      assert.throws(
        () => routing.releasePhysical(leaf, guidance),
        /managed-session-lifecycle-027: retained continuation input release was rejected/,
        'WHAT[managed-session-lifecycle-027]: a Conflict on the delayed return must be raised, not silently dropped',
      )

      // The cancellation itself completed (the retention key is gone), so the
      // repeated call has no retention left to replay; the guidance key's own
      // release is an idempotent stale-fence no-op.
      assert.deepEqual(
        routing.releasePhysical(leaf, guidance),
        { kind: 'StaleFence' },
        'the repeated cancellation of an already-cancelled retention is idempotent',
      )
    })
  })
})

// Failure isolation on the close chain. Four questions:
// - What fails: the Main's own retained-input replay meets the
//   OppositeTerminalConflict (same construction as the case above, on `main`),
//   so PluginSessionScope.ClearSession raises at the self cancellation step.
// - Which state must hold after: the linked leaf's admitted execution must
//   still be settled (durable Cancelled terminal) and its exact capacity
//   returned (executionCount(leaf, physical) === 0); one step's failure must
//   not skip the remaining scope obligations.
// - Which cleanup must happen: the leaf settle -> retention cancel -> exact
//   release chain. The unconditional per-session registry teardown has no
//   public observation face yet, so this test covers the observable scope
//   obligations and the registry gap is recorded instead of faked.
// - Which side effect must never happen: the Conflict must not be swallowed
//   (clearSession must reject) and the leaf credit must not remain held.
test('WHAT[managed-session-lifecycle-027] a failed self cancellation does not skip the linked leaf obligations and still surfaces the conflict', async () => {
  await withPlugin(async () => {
    await withHost(async (host) => {
      const main = 'ses-main-isolation'
      const mainPhysical = 'msg-main-isolation'
      const mainGuidance = 'msg-main-isolation-guidance'
      const leaf = 'ses-blogger-isolation'
      const leafPhysical = 'msg-blogger-isolation'
      const leafGuidance = 'msg-blogger-isolation-guidance'

      await ownership.linkBlogger(host.Journal, main, leaf)

      // Main conflict fixture: pending lease + retention + before-provider
      // settlement, so the self cancellation replay meets the opposite terminal.
      await recoveryHost.seedAccepted(host, main, mainPhysical)
      const mainAcquisition = await routing.acquireSharedExecutionAdmission(
        main,
        mainPhysical,
        'engineer',
        'engineer',
        null,
        'normal',
      )
      assert.equal(mainAcquisition.kind, 'Acquired', 'construction: the Main conflict fixture must hold its exact lease')

      const mainObserved = {
        sessionId: main,
        physicalUserMessageId: mainPhysical,
        role: 'engineer',
        participant: 'engineer',
        target: routing.sharedExecutionAdmissionTarget(mainAcquisition.lease),
      }
      retainGuidance(mainAcquisition.lease, mainGuidance)
      assert.deepEqual(
        routing.releasePhysical(main, mainPhysical),
        { kind: 'HeldForInput' },
        'construction: the Main retention must delay the old exact release',
      )
      assert.equal(
        routing.releaseSharedExecutionAdmissionBeforeProvider(mainAcquisition.lease, mainObserved).kind,
        'Applied',
        'construction: the pending Main execution must settle on the before-provider path',
      )

      // The linked leaf obligation is still outstanding when the close starts.
      const leafToken = await seedLinkedLeafExecution(host, main, leaf, leafPhysical)
      retainGuidance(leafToken, leafGuidance)
      assert.deepEqual(
        routing.releasePhysical(leaf, leafPhysical),
        { kind: 'HeldForInput' },
        'construction: the leaf retention must delay the old exact release',
      )
      assert.equal(executionCount(leaf, leafPhysical), 1, 'construction: the leaf credit must still be held before the close')

      // The close: the self step raises (the Conflict stays surfaced), and the
      // failure must not skip the linked leaf settle/cancel/release obligations.
      await assert.rejects(
        () => recoveryHost.clearSession(host, main),
        /managed-session-lifecycle-027: retained continuation input release was rejected/,
        'WHAT[managed-session-lifecycle-027]: the Conflict must still be surfaced after the remaining obligations run',
      )

      assert.equal(
        executionCount(leaf, leafPhysical),
        0,
        'WHAT[managed-session-lifecycle-027]: a failed self step must not skip the linked leaf exact release; the leaf credit must not remain held',
      )
      const leafStatus = recoveryHost.executionStatus(host, leaf, leafPhysical)
      assert.equal(leafStatus.phase, 'Terminal', 'the linked leaf execution must still be settled despite the self failure')
      assert.equal(leafStatus.disposition, 'Cancelled', 'the linked leaf terminal must be Cancelled')
    })
  })
})
