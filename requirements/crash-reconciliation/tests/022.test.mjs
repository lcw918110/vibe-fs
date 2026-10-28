import assert from 'node:assert/strict'
import test from 'node:test'
import * as recovery from '../../../dist/OpenCode/Host/LoadRecoverySurface.js'
import * as recoveryHost from '../../../dist/OpenCode/Host/SessionRecoveryHostSurface.js'
import { fold, link, acceptRun, canonical } from './support/load-projection.mjs'
import * as codec from '../../../dist/Persistence/Journal/FactCodecSurface.js'
import { integrationTest } from '../../verification-system/tests/support/tier-gate.mjs'

// WHAT[crash-reconciliation-022] a pre-provider child failure settles as a
// durable Failed terminal, and load settlement still voids the orphaned child
// work: the parent sees Abandoned (Voided) while the child's Failed fact
// stays retrievable by child identity across reopen.
//
// 语义取舍：本用例的失败传递方向依据《指南》「用户侧 LLM 均无感」一律维持
// 现状（结算作废、子方 Failed 事实各自保留）；该方向若日后另行裁定，再
// 另开账目，本测试不预设。
//
// Coverage, stated precisely:
// - Tier 1 (integration): the production SessionRecoveryHost signal path
//   persists a pre-provider Failed terminal on a real on-disk journal, and a
//   reopen of the same journal still observes Failed by child identity
//   (session + physical message). The notifyTerminal callback is hard-wired
//   to None on both public boot surfaces, so this is the "persisted but not
//   yet notified" state: a stand-in for the notification gap, NOT a real
//   crash and NOT the normally-wired interrupt.
// - Tier 2 (projection fixture): the same durable pre-provider Failed fact
//   folded into the LoadRecoverySurface fixture. Step 1 mirrors the contract
//   020 established for physical terminals: a terminal does not clear the
//   child's active logical run and the handle stays Active (confirmed by an
//   actual run: activeRun kept the child logical run). Step 2 pins the
//   settled load contract: settlement produces ChildWorkVoided, the parent's
//   view of the child becomes Abandoned (Voided), and a repeated settlement
//   appends nothing more.
// - NOT covered with current public surfaces: the parent Join wire output.
//   drainCompletions' fixture port cannot read completion bodies and its
//   return carries only run/agent names, never Join status; there is no
//   public entry that reconstructs a drain state from a reopened journal.
//   "The parent sees voided on the Join wire" is asserted nowhere here.
//
// Run: node --test requirements/crash-reconciliation/tests/022.test.mjs
// (single-file command shape unverified; DevOps confirms by running it).
// Integration tier: WXS_TIER_INTEGRATION=1 per tests/support/tier-gate.mjs.

const session = 'child-failed-persist'
const physical = 'message-child-failed-persist'

integrationTest('WHAT[crash-reconciliation-022] production signal path persists a pre-provider Failed terminal and reopen keeps it Failed', async () => {
  const { mkdtempSync, rmSync } = await import('node:fs')
  const { tmpdir } = await import('node:os')
  const { join } = await import('node:path')
  const directory = mkdtempSync(join(tmpdir(), 'wxs-preprovider-failed-'))
  try {
    // Step 1 (boot): production recovery host over a real journal. portOutcome
    // 'reject' means no resume capability takes the accepted material. The
    // notifyTerminal port is None on this boot surface (persisted-but-unnotified
    // stand-in).
    const handle = await recoveryHost.bootRecoveryHost(directory, 'reject')
    try {
      // Step 2 (seed + signal): the accepted execution belongs to the linked
      // child session by construction of the host surface (SessionId =
      // sessionId). The boundary rejection settles it pre-provider.
      await recoveryHost.seedAccepted(handle, session, physical)
      const decision = await recoveryHost.signalProviderStartBoundaryRejected(
        handle, session, physical, 'regression-022: provider never started')
      assert.equal(decision.result, 'terminalized', 'step 2 (signal): boundary rejection settles the accepted execution')

      const status = recoveryHost.journalExecutionStatus(handle.Journal, session, physical)
      assert.ok(status, 'step 2 (signal): the terminal left a projection')
      assert.equal(status.phase, 'Terminal', 'step 2 (signal): execution is terminal')
      assert.equal(status.disposition, 'Failed', 'step 2 (signal): pre-provider disposition is Failed, not voided or abandoned')
    } finally {
      recoveryHost.disposeRecoveryHost(handle)
    }

    // Step 3: reopen the same on-disk journal (same-process reopen; not an
    // OS crash) and observe the terminal again.
    const reopened = await recoveryHost.bootRecoveryHost(directory, 'reject')
    try {
      const after = recoveryHost.journalExecutionStatus(reopened.Journal, session, physical)
      assert.ok(after, 'step 3 (reopen): the terminal projection survives reopen')
      assert.equal(after.phase, 'Terminal', 'step 3 (reopen): still terminal')
      assert.equal(after.disposition, 'Failed', 'step 3 (reopen): the pre-provider Failed disposition survives reopen unchanged')
    } finally {
      recoveryHost.disposeRecoveryHost(reopened)
    }
  } finally {
    rmSync(directory, { recursive: true, force: true })
  }
})

test('WHAT[crash-reconciliation-022] fixture: a pre-provider Failed terminal preserves the child active run (contract of 020) and load settlement voids the already-failed child', () => {
  // Step 1: fold the same durable facts the production path persists.
  const state = recovery.create()
  link(state, { parent: 'parent', child: session, handle: 'work-failed', agent: 'engineer' })
  const child = acceptRun(state, { parent: 'parent', child: session, agent: 'engineer' })
  const before = recovery.childView(state, 'parent', session)
  assert.equal(before.activeRun, child.logicalRun, 'step 1 setup: the accepted run is the active logical run')
  // Mirror 020's terminal(): extract the accepted evidence from the encoded
  // root fact so IdentitySeed uses the exact durable shape, not a hand-built one.
  const root = JSON.parse(codec.encode({
    family: 'Prompt',
    case: 'AuthorityRootAccepted',
    payload: {
      SchemaVersion: 2,
      SessionId: session,
      LogicalRunId: child.logicalRun,
      AuthorityRootUserMessageId: 'root-' + session,
      AuthorityKind: 'AgentOwnerRoot',
      IdentitySeed: child.identitySeed,
    },
  }))[1][1][1]
  fold(state, canonical('ChatExecution', 'Accepted', {
    SchemaVersion: 1,
    Key: { SessionId: ['SessionId', session], PhysicalUserMessageId: ['PhysicalUserMessageId', physical] },
    Evidence: {
      SessionId: ['SessionId', session],
      LogicalRunId: ['LogicalRunId', child.logicalRun],
      AuthorityRootUserMessageId: ['AuthorityRootUserMessageId', 'root-' + session],
      AuthorityKind: root.AuthorityKind,
      IdentitySeed: root.IdentitySeed,
      PhysicalUserMessageId: ['PhysicalUserMessageId', physical],
      Origin: ['AuthorityRoot', 'AgentOwnerRoot'],
    },
  }))
  fold(state, canonical('ChatExecution', 'Terminal', {
    SchemaVersion: 1,
    Key: { SessionId: ['SessionId', session], PhysicalUserMessageId: ['PhysicalUserMessageId', physical] },
    Evidence: ['PreProvider', {
      SessionId: ['SessionId', session],
      LogicalRunId: ['LogicalRunId', child.logicalRun],
      AuthorityRootUserMessageId: ['AuthorityRootUserMessageId', 'root-' + session],
      AuthorityKind: root.AuthorityKind,
      IdentitySeed: root.IdentitySeed,
      PhysicalUserMessageId: ['PhysicalUserMessageId', physical],
      Origin: ['AuthorityRoot', 'AgentOwnerRoot'],
    }],
    Disposition: 'Failed',
  }))
  // Contract mirror of 020: a terminal — pre-provider Failed included — does
  // not clear the child view; the active run stays. Confirmed by an actual
  // run where activeRun kept the child logical run.
  assert.deepEqual(recovery.childView(state, 'parent', session), before,
    'step 1 (contract mirror of 020): the pre-provider Failed terminal preserves the child view')
  // Step 2: the settled load contract. Two fact layers coexist after
  // settlement: the child's durable pre-provider Failed terminal stays in the
  // journal (the integration test above proves it across reopen), and the
  // parent-side load settlement voids the orphaned child work exactly once —
  // the parent sees Abandoned (Voided), never a completion.
  const settlements = recovery.childSettlements(state)
  assert.equal(settlements.length, 1,
    'step 2 (settlement): load settlement voids the already-failed child exactly once')
  assert.equal(JSON.parse(settlements[0])[1][1][0], 'ChildWorkVoided',
    'step 2 (settlement): the void fact is ChildWorkVoided')
  fold(state, settlements[0])
  assert.deepEqual(recovery.childView(state, 'parent', session), {
    activeRun: '', lifecycle: 'Abandoned', joinable: 0, horizonVisible: 1,
  }, 'step 2 (settlement): the parent sees the child work as Abandoned (Voided)')
  assert.deepEqual(recovery.childSettlements(state), [],
    'step 2 (settlement): a repeated settlement appends nothing more')
})
