import test from 'node:test'
import { join } from 'node:path'

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const HandleSurface = await import("../../../dist/Execution/Delegation/Handle/Surface.js");
const HandleJournalSurface = await import("../../../dist/Execution/Delegation/Handle/JournalSurface.js");
const { withAdmittedChildren } = await import('./support/admitted-child-work.mjs')

const observed = (action) => HandleSurface.scenario(action)

test('WHAT[managed-session-lifecycle-013] abandonment projection remains parent-visible until consumption', async () => {
  await withAdmittedChildren('abandon-horizon', 'ses_parent', [{ agentId: 'h1', sessionId: 'ses_child', role: 'engineer' }], async (journal, profiles, directory, reopen) => {
    const root = profiles.get('h1').authorityRoot
    const settled = await HandleJournalSurface.recordAbandon(journal, 'ses_parent', 'h1', 'ses_child', root, 'ParentCancelled')
    assert.equal(settled.ok, true)
    const result = HandleJournalSurface.snapshot(journal, 'ses_parent', 'h1', 'ses_child', root)
    assert.equal(result.record.lifecycle, 'Abandoned')
    assert.equal(result.record.child, 'ses_child')
    assert.equal(result.horizonVisible, 1, 'unconsumed scoped abandonment remains visible to the parent horizon')
    const reopened = await reopen()
    assert.deepEqual(HandleJournalSurface.snapshot(reopened, 'ses_parent', 'h1', 'ses_child', root).record, result.record)
  })
})
test('WHAT[managed-session-lifecycle-013] retirement projection removes the handle from parent horizon', () => {
  const result = observed('retire')
  assert.equal(result.ok, true)
  assert.equal(result.record.lifecycle, 'Retired')
  assert.equal(result.horizonVisible, 0, 'join-retired handle may finally leave the parent horizon')
})
test('WHAT[managed-session-lifecycle-013] active projection retains its child identity', () => {
  const result = observed('active')
  assert.equal(result.record.lifecycle, 'Active')
  assert.equal(result.record.child, 'ses_child')
})
test.todo('WHAT[managed-session-lifecycle-013] actual restart re-enlists durable visible handles and a failed recovery commit blocks readiness (GAP-133)')
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const {
  childTurn,
  coldWorkSnapshot,
  forkCall,
  joinCall,
  observeTerminals,
  withRealFork,
} = await import('../../delegation/tests/support/real-fork.mjs');
const forkTool = await import("../../../dist/Execution/Delegation/Fork/OpenCode/ToolSurface.js");

/**
 * The cold recovery surface: a fresh writer re-opens the durable event log of
 * the same workspace and settles against the durable facts alone. It never
 * writes a fabricated fact.
 */
const coldConsume = (directory, owner, root, commitment) =>
  forkTool.coldConsumeWorkWithOutcome(join(directory, '.Herdchestra'), owner, root, commitment)

test('WHAT[managed-session-lifecycle-013] a stale-projection consumer is arbitrated by the durable consumption tombstone, not its own fold', async () => {
  const owner = 'scope-cross-instance-consume'
  await withRealFork(owner, async ({ hooks, createdIds, runtime, directory }) => {
    const terminals = await observeTerminals(runtime)
    try {
      const first = await forkCall(hooks, owner, 'Ada', 'RACE-FIRST')
      assert.match(first, /Ada/)
      const child = createdIds[0]
      const root = runtime.messages.filter((message) => message.id?.startsWith(`msg-${child}-`)).at(-1).id
      await childTurn(hooks, runtime, child, `run-${child}-1`, 'PROSE', 'RACE-ANSWER', terminals.awaitRun)

      let deliveries = 0

      // A fresh writer with replay consumes before the old runtime reads again.
      const cold = await coldConsume(directory, owner, root, 'confirmed')
      assert.equal(cold.ok, true)
      assert.match(cold.view, /RACE-ANSWER/)
      deliveries += 1

      // The old runtime still holds the in-memory projection it folded before
      // the cold instance's append. WHAT-013 durable re-enlist: its consume
      // must be arbitrated by the durable tombstone, not by its own stale
      // fold — no second delivery, and no second consumption append written
      // from a projection that never saw the first tombstone.
      //
      // The arbitration is asserted on the durable surface, not on the error
      // text: the production join reports `nothing-to-join` where the old
      // harness reported `AlreadyRetired` — the same meaning (there is no
      // payload left to hand out), and the tombstone itself is read back from
      // the durable facts below.
      const stale = await joinCall(hooks, owner)
      assert.match(stale, /There is nothing away to receive|目前没有在外可接收的工作/)
      assert.doesNotMatch(stale, /RACE-ANSWER/)
      assert.doesNotMatch(stale, /RACE-ANSWER/)

      // Durable replay keeps a single terminal: a fresh cold reader sees the
      // work Retired and every later consumer meets the same tombstone.
      assert.equal((await coldWorkSnapshot(directory, owner)).find(work => work.root === root).lifecycle, 'Retired')
      const late = await coldConsume(directory, owner, root, 'confirmed')
      assert.equal(late.ok, false)
      assert.match(late.error, /AlreadyRetired/)
      assert.equal(deliveries, 1, 'sequential cold and stale consumers deliver only once')
    } finally {
      terminals.dispose()
    }
  })
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const RecoverySurface = await import("../../../dist/Execution/Session/Recovery/Surface.js");
const HandleSurface = await import("../../../dist/Execution/Delegation/Handle/Surface.js");


test('WHAT[managed-session-lifecycle-013] parent projection filters hidden handles before recovery selection', () => {
  let state = HandleSurface.empty()
  
  // Link durable public child
  const r1 = HandleSurface.apply(state, { op: 'link', handle: 'agent:engineer', child: 'ses_child_1', agent: 'engineer', role: 'Engineer', ownership: 'DurableParentHandle' })
  assert.equal(r1.ok, true)
  state = r1.state

  // Link host-owned hidden child (e.g. a host executor run leaf)
  const r2 = HandleSurface.apply(state, { op: 'link', handle: 'agent:executor', child: 'ses_executor_1', agent: 'executor', role: 'DevOps', ownership: 'HostOwnedHidden' })
  assert.equal(r2.ok, true)
  state = r2.state

  const listable = HandleSurface.views(state).listable
  assert.equal(listable.length, 1)
  assert.equal(listable[0], 'agent:engineer')
  assert.equal(HandleSurface.read(state, listable[0]).child, 'ses_child_1')
})
test('WHAT[managed-session-lifecycle-013] session_recovery_contract_authorizes_family_without_physical_handle_leaks', () => {
  const root = 'ses_root'
  const nodes = [
    { kind: 'child', parent: root, child: 'ses_child', handle: 'agent:h1' },
    { kind: 'companion', main: root, companion: 'ses_comp' }
  ]
  const closure = RecoverySurface.validateClosure(root, nodes)
  assert.equal(closure.ok, true)
  assert.equal(closure.members.length, 2)

  // Authorize with all recovered -> FamilyReady permit with members
  const authReady = RecoverySurface.authorize(root, 1, [
    { session: 'ses_child', state: 'Recovered' },
    { session: 'ses_comp', state: 'Recovered' }
  ])
  assert.equal(authReady.state, 'FamilyReady')
  assert.equal(authReady.root, root)
  assert.equal(authReady.sequence, 1)

  // Authorize with a waiting member -> FamilyWaiting without permit
  const authWaiting = RecoverySurface.authorize(root, 1, [
    { session: 'ses_child', state: 'Waiting' },
    { session: 'ses_comp', state: 'Recovered' }
  ])
  assert.equal(authWaiting.state, 'FamilyWaiting')

  // Authorize with a blocked member -> FamilyBlocked
  const authBlocked = RecoverySurface.authorize(root, 1, [
    { session: 'ses_child', state: 'Blocked' },
    { session: 'ses_comp', state: 'Recovered' }
  ])
  assert.equal(authBlocked.state, 'FamilyBlocked')
})

test('WHAT[managed-session-lifecycle-013] an unknown recovery member is refused instead of becoming a work session', () => {
  assert.throws(
    () => RecoverySurface.validateClosure('ses_root', [{ kind: 'future', session: 'ses_unknown' }]),
    /unknown recovery node: future/,
  )
  const work = RecoverySurface.validateClosure('ses_root', [{ kind: 'work', session: 'ses_work' }])
  assert.equal(work.ok, true)
})
}
