import assert from 'node:assert/strict'
import test from 'node:test'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import * as recoveryHost from '../../../dist/OpenCode/Host/SessionRecoveryHostSurface.js'
import * as routing from '../../../dist/OpenCode/Host/ModelRoutingSurface.js'
import * as ownership from '../../verification-system/tests/support/blogger-ownership.mjs'
import { startPluginIncarnation } from '../../verification-system/tests/support/plugin-fixture.mjs'

// WHAT[managed-session-lifecycle-026]: closing a Main session must also settle
// every admitted execution of its durable linked Attached InternalLeaf (Blogger)
// with a typed Cancelled terminal and exact capacity release.
//
// Driving face: recoveryHost.clearSession is the same delete-drain owner the
// runtime's DisposeSession awaits (SessionRecoveryHostSurface.clearSession —
// "the same PluginSessionScope.ClearSession the runtime's DisposeSession
// awaits"). It is the public lifecycle completion promise of the Main close,
// so the linked-leaf settlement must be observable through it.
//
// Oracle: the real shared capacity snapshot, exact owner only (mirrors 019).
const executionCount = (session, physical) =>
  routing.sharedCapacitySnapshot().executions.filter(
    (execution) => execution.sessionId === session && execution.physicalUserMessageId === physical,
  ).length

// Exact lease acquisition through the registered ModelRouting surface. The
// lease role is not the addressing axis of this proposition: the durable
// CompanionBloggerLinked fact marks the session as an Attached InternalLeaf,
// and both settlement and release address the exact execution key
// (session + physical).
const acquireLease = (session, physical) =>
  routing.acquireSharedExecutionAdmission(session, physical, 'engineer', 'engineer', null, 'normal')

const withPlugin = async (action) => {
  const workspace = mkdtempSync(join(tmpdir(), 'wxs-lifecycle-leaf-drain-'))
  const incarnation = await startPluginIncarnation(workspace)
  try {
    await action()
  } finally {
    await incarnation.hooks.dispose()
    rmSync(workspace, { recursive: true, force: true })
  }
}

const withHost = async (action) => {
  const directory = mkdtempSync(join(tmpdir(), 'wxs-lifecycle-leaf-drain-journal-'))
  const host = await recoveryHost.bootRecoveryHost(directory, 'absent')
  try {
    await action(host)
  } finally {
    recoveryHost.disposeRecoveryHost(host)
    rmSync(directory, { recursive: true, force: true })
  }
}

// Durable Main -> leaf link (the production CompanionBloggerLinked fact) plus
// one admitted execution holding an exact lease. `started` selects the
// after-provider-start shape; otherwise the execution stays pre-provider.
const seedLinkedLeaf = async (host, main, leaf, physical, { started }) => {
  await ownership.linkBlogger(host.Journal, main, leaf)
  if (started) await recoveryHost.seedProviderStarted(host, leaf, physical, `provider-${leaf}`)
  else await recoveryHost.seedAccepted(host, leaf, physical)
  await acquireLease(leaf, physical)
}

test('WHAT[managed-session-lifecycle-026] a provider-started execution under a linked Attached InternalLeaf loses its exact capacity when its Main session closes', async () => {
  await withPlugin(async () => {
    await withHost(async (host) => {
      const main = 'ses-main-leaf-started'
      const leaf = 'ses-blogger-leaf-started'
      const physical = 'msg-blogger-leaf-started'
      const decoyMain = 'ses-main-leaf-decoy'
      const decoyLeaf = 'ses-blogger-leaf-decoy'
      const decoyPhysical = 'msg-blogger-leaf-decoy'

      await seedLinkedLeaf(host, main, leaf, physical, { started: true })
      await seedLinkedLeaf(host, decoyMain, decoyLeaf, decoyPhysical, { started: true })

      // Construction guard: both exact leases must exist before the close, so a
      // red below is about the close, not about a failed fixture.
      assert.equal(executionCount(leaf, physical), 1, 'construction: the leaf execution must hold its exact lease before the Main closes')
      assert.equal(executionCount(decoyLeaf, decoyPhysical), 1, 'construction: the decoy leaf execution must hold its exact lease')

      await recoveryHost.clearSession(host, main)

      assert.equal(
        executionCount(leaf, physical),
        0,
        'WHAT[managed-session-lifecycle-026]: closing the Main must return the linked leaf exact capacity; the execution must not remain in the shared capacity snapshot',
      )
      const status = recoveryHost.executionStatus(host, leaf, physical)
      assert.equal(status.phase, 'Terminal', 'the linked leaf execution must carry a durable terminal after the Main close')
      assert.equal(status.disposition, 'Cancelled', 'the linked leaf terminal disposition must be Cancelled')
      assert.equal(
        executionCount(decoyLeaf, decoyPhysical),
        1,
        'an unrelated Main close must not touch another linked leaf lease',
      )
    })
  })
})

test('WHAT[managed-session-lifecycle-026] a pre-provider execution under a linked Attached InternalLeaf loses its exact capacity when its Main session closes', async () => {
  await withPlugin(async () => {
    await withHost(async (host) => {
      const main = 'ses-main-leaf-pre'
      const leaf = 'ses-blogger-leaf-pre'
      const physical = 'msg-blogger-leaf-pre'

      await seedLinkedLeaf(host, main, leaf, physical, { started: false })

      assert.equal(executionCount(leaf, physical), 1, 'construction: the pre-provider leaf execution must hold its exact lease before the Main closes')

      await recoveryHost.clearSession(host, main)

      assert.equal(
        executionCount(leaf, physical),
        0,
        'WHAT[managed-session-lifecycle-026]: closing the Main must settle the pre-provider linked leaf and return its exact capacity',
      )
      const status = recoveryHost.executionStatus(host, leaf, physical)
      assert.equal(status.phase, 'Terminal', 'the pre-provider linked leaf must be settled before its capacity is returned')
      assert.equal(status.disposition, 'Cancelled', 'the pre-provider linked leaf terminal disposition must be Cancelled')
    })
  })
})

test('WHAT[managed-session-lifecycle-026] repeated Main session close is idempotent for the linked leaf settlement', async () => {
  await withPlugin(async () => {
    await withHost(async (host) => {
      const main = 'ses-main-leaf-repeat'
      const leaf = 'ses-blogger-leaf-repeat'
      const physical = 'msg-blogger-leaf-repeat'

      await seedLinkedLeaf(host, main, leaf, physical, { started: true })
      assert.equal(executionCount(leaf, physical), 1, 'construction: the leaf execution must hold its exact lease before the first close')

      await recoveryHost.clearSession(host, main)
      assert.equal(
        executionCount(leaf, physical),
        0,
        'WHAT[managed-session-lifecycle-026]: the first Main close must return the linked leaf exact capacity',
      )
      const first = recoveryHost.executionStatus(host, leaf, physical)
      assert.equal(first.phase, 'Terminal')
      assert.equal(first.disposition, 'Cancelled')

      // The repeated close must resolve cleanly and be a no-op: no second
      // terminal, no duplicated release side effect.
      await recoveryHost.clearSession(host, main)

      assert.equal(executionCount(leaf, physical), 0, 'the repeated close must not leave a second lease')
      assert.deepEqual(
        recoveryHost.executionStatus(host, leaf, physical),
        first,
        'the repeated close must not rewrite the durable terminal or duplicate side effects',
      )
    })
  })
})

test('WHAT[managed-session-lifecycle-026] a terminal linked leaf execution whose exact custody is still held is released when its Main session closes', async () => {
  await withPlugin(async () => {
    await withHost(async (host) => {
      const main = 'ses-main-leaf-held'
      const leaf = 'ses-blogger-leaf-held'
      const physical = 'msg-blogger-leaf-held'
      const providerRun = `provider-${leaf}`

      await ownership.linkBlogger(host.Journal, main, leaf)
      await recoveryHost.seedProviderStarted(host, leaf, physical, providerRun)

      // Produce a real durable terminal first; its normal settlement returns
      // the lease. Re-acquiring the same exact key then represents the
      // contract's `terminal but custody still held` shape (019's
      // TerminalResourceHeld): settlement is done, the physical resource is not.
      await recoveryHost.signalExactTerminal(host, leaf, physical, providerRun, 'Completed')
      assert.equal(recoveryHost.executionStatus(host, leaf, physical).phase, 'Terminal', 'construction: the leaf must carry a durable terminal')
      await acquireLease(leaf, physical)
      assert.equal(executionCount(leaf, physical), 1, 'construction: the held custody must be visible in shared capacity')

      await recoveryHost.clearSession(host, main)

      assert.equal(
        executionCount(leaf, physical),
        0,
        'WHAT[managed-session-lifecycle-026]: a terminal linked leaf execution whose custody is still held must be released on the Main close',
      )
    })
  })
})
