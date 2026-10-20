import test from 'node:test'
import assert from 'node:assert/strict'
import { admit, assertCold, forkTool, withForkRuntime } from '../../delegation/tests/support/scoped-work.mjs'

for (const commitment of ['before', 'after']) {
  test('WHAT[managed-session-lifecycle-008] exact consume with ' + commitment + ' commit failure never releases an unconfirmed payload', async () => {
    const owner = 'scope-consume-' + commitment
    await withForkRuntime(owner, async (runtime, directory) => {
      const a = await admit(runtime, owner, 1, 'FIRST')
      assert.equal(await forkTool.settle(runtime, owner, 'FIRST-ANSWER', 'provider-a'), true)
      const withheld = await forkTool.consumeWorkWithOutcome(runtime, owner, a.root, commitment)
      assert.equal(withheld.ok, false)
      assert.equal(Object.hasOwn(withheld, 'view'), false)
      const state = forkTool.workSnapshot(runtime, owner)[0]
      assert.equal(state.lifecycle, commitment === 'before' ? 'CompletedAwaitingJoin' : 'Retired')
      await assertCold(runtime, directory, owner)
      const next = await forkTool.consumeWorkWithOutcome(runtime, owner, a.root, 'confirmed')
      assert.equal(next.ok, commitment === 'before')
      if (next.ok) assert.match(next.view, /FIRST-ANSWER/)
      assert.equal((await forkTool.consumeWorkWithOutcome(runtime, owner, a.root, 'confirmed')).ok, false)
      await assertCold(runtime, directory, owner)
    })
  })
}

test('WHAT[managed-session-lifecycle-008] concurrent exact consumers deliver a work at most once', async () => {
  const owner = 'scope-concurrent-consume'
  await withForkRuntime(owner, async runtime => {
    const a = await admit(runtime, owner, 1, 'FIRST')
    assert.equal(await forkTool.settle(runtime, owner, 'FIRST-ANSWER', 'provider-a'), true)
    const outcomes = await Promise.all([
      forkTool.consumeWorkWithOutcome(runtime, owner, a.root, 'confirmed'),
      forkTool.consumeWorkWithOutcome(runtime, owner, a.root, 'confirmed'),
    ])
    assert.equal(outcomes.filter(outcome => outcome.ok).length, 1)
    assert.match(outcomes.find(outcome => outcome.ok).view, /FIRST-ANSWER/)
  })
})

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const HandleSurface = await import("../../../dist/Execution/Delegation/Handle/Surface.js");
const HandleFoldSurface = await import("../../../dist/Execution/Delegation/Handle/FoldSurface.js");

const blobRef = (value) => String(value)
const blobDigest = (value) => String(value)
const sessionId = (value) => String(value)
const roles = { of: (value) => String(value) }
const handleOwnership = {
  durableParentHandle: () => 'DurableParentHandle',
  hostOwnedHidden: () => 'HostOwnedHidden',
}
const completionKind = { of: (value) => String(value) }
const isSome = (value) => value !== undefined && value !== null
const fact = (caseName, payload) => ({ case: caseName, payload })
const envelope = ({ seq, stream, fact: value }) => ({ seq, stream, fact: value })
const stream = { session: (value) => `session:${value}` }
const handleId = {
  agent: HandleSurface.handleIdAgent,
  pty: HandleSurface.handleIdPty,
  managerJob: HandleSurface.handleIdManagerJob,
  describe: HandleSurface.handleIdDescribe,
  tryAgent: HandleSurface.handleIdTryAgent,
}
const PARENT = sessionId('ses_p')
const CHILD = sessionId('ses_c')
const HANDLE = handleId.agent('h1')
const linkOn = (state, { handle = HANDLE, child = CHILD, agent = 'coder', role = 'Coder' } = {}) => {
  const applied = HandleSurface.apply(state, { op: 'link', handle, child, agent, role })
  assert.equal(applied.ok, true, applied.ok ? '' : `link refused: ${JSON.stringify(applied.error)}`)
  return applied.state
}
const completeOn = (state, { handle = HANDLE, kind = 'Terminal', ref, digest } = {}) => {
  const command = { op: 'complete', handle, kind }
  if (ref !== undefined) command.ref = ref
  if (digest !== undefined) command.digest = digest
  const applied = HandleSurface.apply(state, command)
  assert.equal(applied.ok, true, applied.ok ? '' : `complete refused: ${JSON.stringify(applied.error)}`)
  return applied.state
}
const abandonOn = (state, { handle = HANDLE, reason = 'ParentCancelled' } = {}) => {
  const applied = HandleSurface.apply(state, { op: 'abandon', handle, reason })
  assert.equal(applied.ok, true, applied.ok ? '' : `abandon refused: ${JSON.stringify(applied.error)}`)
  return applied.state
}
const retireOn = (state, { handle = HANDLE } = {}) => {
  const applied = HandleSurface.apply(state, { op: 'retire', handle })
  assert.equal(applied.ok, true, applied.ok ? '' : `retire refused: ${JSON.stringify(applied.error)}`)
  return applied.state
}
const stateOf = (state, handle = HANDLE) => HandleSurface.read(state, handle)
const views = (state) => {
  const v = HandleSurface.views(state)
  return { listable: [...v.listable].sort(), joinable: [...v.joinable].sort(), active: [...v.active].sort() }
}
const handleFact = {
  linked: fact('HandleLinked', {
    ParentSessionId: PARENT,
    ChildSessionId: CHILD,
    Handle: HANDLE,
    TargetAgent: 'coder',
    CanonicalRole: roles.of('Coder'),
    Ownership: handleOwnership.durableParentHandle(),
  }),
  completed: fact('HandleCompleted', {
    ParentSessionId: PARENT,
    Handle: HANDLE,
    Kind: completionKind.of('Terminal'),
    CompletionRef: undefined,
    CompletionDigest: undefined,
  }),
  completedWithBlob: fact('HandleCompleted', {
    ParentSessionId: PARENT,
    Handle: HANDLE,
    Kind: completionKind.of('Terminal'),
    CompletionRef: blobRef('blobs/completion-h1'),
    CompletionDigest: blobDigest('sha-completion-h1'),
  }),
  retired: fact('HandleRetired', { ParentSessionId: PARENT, Handle: HANDLE }),
}
const foldFacts = (facts) =>
  HandleFoldSurface.foldApply(
    HandleFoldSurface.foldEmpty(),
    facts.map((value, index) => envelope({ seq: index + 1, stream: stream.session(PARENT), fact: value })),
  )
const foldStateOf = (folded, handle = HANDLE) => {
  const handles = HandleFoldSurface.foldSession(folded.state, 'ses_p')
  return HandleSurface.read(handles, handle)
}
const foldViews = (folded) => {
  const handles = HandleFoldSurface.foldSession(folded.state, 'ses_p')
  return views(handles)
}

test('WHAT[managed-session-lifecycle-008] EXEC_004_join_may_only_retire_a_handle_that_actually_completed', () => {
  // `retire` IS join's write. Retiring an active handle would discard a child
  // that is still running and leave its completion with nowhere to land.
  const active = linkOn(HandleSurface.empty())

  assert.deepEqual(
    HandleSurface.apply(active, { op: 'retire', handle: HANDLE }),
    { ok: false, error: { kind: 'TransitionRejected', reason: 'NotCompleted' } },
  )
  assert.deepEqual(
    HandleSurface.apply(active, { op: 'retire', handle: handleId.agent('never') }),
    { ok: false, error: { kind: 'TransitionRejected', reason: 'UnknownHandle' } },
  )
})
test('WHAT[managed-session-lifecycle-008] EXEC_009_a_replayed_completion_or_retirement_is_absorbed', () => {
  // The tombstone makes both idempotent, and a journal written across a restart
  // contains exactly these repeats. Rejecting them would refuse to boot.
  const folded = foldFacts([
    handleFact.linked,
    handleFact.completed,
    handleFact.completed,
    handleFact.retired,
    handleFact.retired,
  ])

  assert.equal(folded.ok, true, folded.ok ? '' : JSON.stringify(folded.error))
  assert.equal(foldStateOf(folded).lifecycle, 'Retired')
})
test('WHAT[managed-session-lifecycle-008] EXEC_004_a_retirement_without_a_completion_stops_the_replay', () => {
  // `retire` is join's write, and join consumes a completion. A tombstone with no
  // completion means a handle was discarded while its child was still running.
  const folded = foldFacts([handleFact.linked, handleFact.retired])

  assert.equal(folded.ok, false)
  assert.equal(folded.error.Fact, 'HandleRetired')
  assert.equal(folded.error.Reason, 'join retired a handle that had no completion (EXEC-004)')

  // The two fatal reasons must read differently: one sends an operator looking
  // for a missing link, the other for a missing completion.
  assert.notEqual(folded.error.Reason, foldFacts([handleFact.completed]).error.Reason)
})
test('WHAT[managed-session-lifecycle-008] fold_refuses_unknown_fact_case', () => {
  const folded = foldFacts([fact('HandleExploded', { ParentSessionId: PARENT, Handle: HANDLE })])
  assert.equal(folded.ok, false)
  assert.equal(folded.error.kind, 'UnknownFactCase')
  assert.equal(folded.error.value, 'HandleExploded')
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const HandleFoldSurface = await import("../../../dist/Execution/Delegation/Handle/FoldSurface.js");
const HandleSurface = await import("../../../dist/Execution/Delegation/Handle/Surface.js");

const makeActive = () => {
  const result = HandleSurface.apply(HandleSurface.empty(), {
    op: 'link', handle: 'agent:c1', child: 'ses_child', agent: 'coder', role: 'Coder',
  })
  assert.equal(result.ok, true)
  return result.state
}
const complete = (state) => HandleSurface.apply(state, { op: 'complete', handle: 'agent:c1', kind: 'Terminal' })
const retire = (state) => HandleSurface.apply(state, { op: 'retire', handle: 'agent:c1' })

test('WHAT[managed-session-lifecycle-008] retirement removes completed handle from all parent views', () => {
  const active = makeActive()
  const completed = complete(active)
  const retired = retire(completed.state)
  assert.equal(retired.ok, true)
  assert.deepEqual(HandleSurface.views(retired.state), { listable: [], joinable: [], active: [] })
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const HandleSurface = await import("../../../dist/Execution/Delegation/Handle/Surface.js");

const link = (projection, agentId, child, targetAgent = 'coder') => {
  const result = HandleSurface.apply(projection, {
    op: 'link', handle: `agent:${agentId}`, child, agent: targetAgent, role: 'Coder',
  })
  assert.equal(result.ok, true)
  return result.state
}

test('WHAT[managed-session-lifecycle-008] EXEC_009_consume_abandoned_writes_HandleRetired_second_AlreadyRetired', () => {
  let projection = link(HandleSurface.empty(), 'h1', 'ses_c')
  projection = HandleSurface.apply(projection, { op: 'abandon', handle: 'agent:h1', reason: 'ParentCancelled' }).state
  assert.equal(HandleSurface.reportableAbandonedCount(projection), 1)
  const consumed = HandleSurface.apply(projection, { op: 'retire', handle: 'agent:h1' })
  assert.equal(consumed.ok, true)
  projection = consumed.state
  assert.equal(HandleSurface.isRetired(projection, 'agent:h1'), true)
  assert.equal(HandleSurface.reportableAbandonedCount(projection), 0)
  assert.deepEqual(HandleSurface.apply(projection, { op: 'retire', handle: 'agent:h1' }).error, { kind: 'TransitionRejected', reason: 'HandleIsRetired' })
})
}

test('WHAT[managed-session-lifecycle-008] actual join withholds payload until retirement append confirms and survives each crash cut with one delivery (GAP-133)', async () => {
  const owner = 'scope-consume-crash-cut'
  await withForkRuntime(owner, async (runtime, directory) => {
    const a = await admit(runtime, owner, 1, 'CRASH-CUT-A')
    assert.equal(await forkTool.settle(runtime, owner, 'CRASH-ANSWER-A', 'provider-a'), true)
    const b = await admit(runtime, owner, 2, 'CRASH-CUT-B')
    assert.equal(await forkTool.settle(runtime, owner, 'CRASH-ANSWER-B', 'provider-b'), true)
    let deliveries = 0

    // Crash cut before the retirement append: the dying caller attempted a
    // consume whose append never committed, so it received nothing and no
    // tombstone reached the disk. A cold-reopened journal (fresh writer, full
    // replay — the shape a restarted process takes) still finds the work
    // unconsumed and must deliver exactly once, only after its own append
    // confirms.
    const dyingBefore = await forkTool.coldConsumeWorkWithOutcome(directory, owner, a.root, 'before')
    assert.equal(dyingBefore.ok, false)
    assert.equal(Object.hasOwn(dyingBefore, 'view'), false)
    assert.equal((await forkTool.coldWorkSnapshot(directory, owner)).find(work => work.root === a.root).lifecycle, 'CompletedAwaitingJoin')
    const recovered = await forkTool.coldConsumeWorkWithOutcome(directory, owner, a.root, 'confirmed')
    assert.equal(recovered.ok, true)
    assert.match(recovered.view, /CRASH-ANSWER-A/)
    deliveries += 1
    assert.equal((await forkTool.coldWorkSnapshot(directory, owner)).find(work => work.root === a.root).lifecycle, 'Retired')
    const repeat = await forkTool.coldConsumeWorkWithOutcome(directory, owner, a.root, 'confirmed')
    assert.equal(repeat.ok, false)
    assert.match(repeat.error, /AlreadyRetired/)

    // Crash cut after the retirement append: this time the tombstone did
    // commit before the caller died, but the receipt never came back and the
    // payload was never delivered. The unknown commit stays conservative — no
    // fabricated re-claim — and every later cold consumer meets the durable
    // tombstone instead of a second delivery.
    const dyingAfter = await forkTool.coldConsumeWorkWithOutcome(directory, owner, b.root, 'after')
    assert.equal(dyingAfter.ok, false)
    assert.equal(Object.hasOwn(dyingAfter, 'view'), false)
    assert.equal((await forkTool.coldWorkSnapshot(directory, owner)).find(work => work.root === b.root).lifecycle, 'Retired')
    const reclaimer = await forkTool.coldConsumeWorkWithOutcome(directory, owner, b.root, 'confirmed')
    assert.equal(reclaimer.ok, false)
    assert.match(reclaimer.error, /AlreadyRetired/)

    assert.equal(deliveries, 1, 'exactly one payload delivery reaches an independent caller across both crash cuts')
  })
})
