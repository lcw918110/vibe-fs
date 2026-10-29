import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const prefix = await import("../../../dist/Context/Prefix/Surface.js");

const reanchor = (state, { previousEpoch, nextEpoch, observedRun = 'msg_compaction' }) =>
  prefix.applyReanchor({ previousEpoch, nextEpoch, observedRun }, state)

test('WHAT[prefix-stability-006] epochs beyond the JS safe range retain exact scope and compaction identities', () => {
  const epoch = 9007199254740993n
  const state = { ...prefix.empty, epoch }
  const scoped = prefix.applyScopeReanchor({ previousEpoch: epoch, nextEpoch: epoch + 1n, callId: 'exact-reset' }, state)
  assert.equal(scoped.ok, true)
  assert.equal(prefix.epochOf(scoped.value), epoch + 1n)
  assert.deepEqual(prefix.reanchoredScopes(scoped.value), ['exact-reset'])
  const compacted = reanchor(scoped.value, { previousEpoch: epoch + 1n, nextEpoch: epoch + 2n, observedRun: 'exact-compaction' })
  assert.equal(compacted.ok, true)
  assert.equal(prefix.epochOf(compacted.value), epoch + 2n)
  assert.deepEqual(prefix.reanchoredScopes(compacted.value), ['exact-reset'])
  assert.deepEqual(prefix.reanchoredRuns(compacted.value), ['exact-compaction'])
})

test('WHAT[prefix-stability-006] HOST_006_reanchor_advances_the_epoch_and_records_the_compaction', () => {
  const result = reanchor(prefix.empty, { previousEpoch: 0, nextEpoch: 1 })
  assert.equal(result.ok, true, result.ok ? '' : result.error)

  // This is a real cold boundary — the provider-visible prefix changed and the
  // seal barrier broke — and COMPANION-009's byte-stability guarantee is scoped
  // to one epoch, so staying put would state something false.
  assert.equal(prefix.epochOf(result.value), 1n)

  // The compaction that caused it is recorded, so the same observation cannot act
  // twice.
  assert.deepEqual(prefix.reanchoredRuns(result.value), ['msg_compaction'])
})
test('WHAT[prefix-stability-006] HOST_006_reanchoring_a_session_that_never_promoted_still_advances', () => {
  // A manual /compact on a session with no committed prefix. Nothing to retire,
  // but the cold boundary is just as real, and the epoch is what the frame
  // projection's coverage reset is paired with under one fact.
  const result = reanchor(prefix.empty, { previousEpoch: 0, nextEpoch: 1 })

  assert.equal(result.ok, true, result.ok ? '' : result.error)
  assert.equal(prefix.epochOf(result.value), 1n)
})
test('WHAT[prefix-stability-006] PERSIST_010_reanchor_epoch_must_be_the_successor', () => {
  for (const nextEpoch of [0, 3, 9]) {
    assert.deepEqual(
      reanchor(prefix.empty, { previousEpoch: 0, nextEpoch }),
      { ok: false, error: 'NonSequentialPrefixEpoch' },
      `nextEpoch ${nextEpoch} must be refused after epoch 0`,
    )
  }
})
test('WHAT[prefix-stability-006] HOST_006_the_same_compaction_is_never_reanchored_twice', () => {
  // Two observations of one pseudo-run must produce one retirement.
  const once = reanchor(prefix.empty, { previousEpoch: 0, nextEpoch: 1 }).value

  const replay = reanchor(once, { previousEpoch: 0, nextEpoch: 1 })
  assert.deepEqual(replay, { ok: false, error: 'CompactionAlreadyReanchored' })

  assert.equal(prefix.epochOf(once), 1n, 'the epoch did not move twice')
})
test('WHAT[prefix-stability-006] HOST_006_a_recorded_compaction_stays_refused_after_the_epoch_moves_on', () => {
  // The failure the recorded-run set exists for, and the reason the epoch check alone
  // is not enough.
  //
  // A compaction message stays in the Host transcript forever, so every later
  // reconcile observes it again. Once the epoch has advanced for an UNRELATED reason —
  // a tenure handover here — a freshly decided reanchor for that old compaction would
  // carry a `PreviousEpochId` that matches the current epoch. The epoch check would
  // accept it, the epoch would advance again, and the coverage the session had
  // legitimately rebuilt would be zeroed.
  const reanchored = reanchor(prefix.empty, { previousEpoch: 0, nextEpoch: 1, observedRun: 'msg_c1' }).value

  const handedOver =
    prefix.applyTenureReanchor(
      { previousEpoch: 1, nextEpoch: 2, incumbencyId: 'incumbent-1' },
      reanchored,
    ).value

  assert.equal(prefix.epochOf(handedOver), 2n)

  // A well-formed line for the OLD compaction, correct against the current epoch.
  const stale = reanchor(handedOver, { previousEpoch: 2, nextEpoch: 3, observedRun: 'msg_c1' })

  assert.deepEqual(stale, { ok: false, error: 'CompactionAlreadyReanchored' })
  assert.equal(prefix.epochOf(handedOver), 2n, 'the unrelated advance survives')
})
test('WHAT[prefix-stability-006] HOST_006_a_genuinely_new_compaction_reanchors_again', () => {
  // A second, different pseudo-run on an already-reanchored session. It must be
  // accepted, or a second manual /compact would leave the session pointing at a
  // numbering the transcript no longer has.
  const first = reanchor(prefix.empty, { previousEpoch: 0, nextEpoch: 1, observedRun: 'msg_c1' }).value

  const second = reanchor(first, { previousEpoch: 1, nextEpoch: 2, observedRun: 'msg_c2' })
  assert.equal(second.ok, true, second.ok ? '' : second.error)
  assert.equal(prefix.epochOf(second.value), 2n)

  // Both compactions are now recorded, so neither can act again.
  assert.deepEqual(prefix.reanchoredRuns(second.value), ['msg_c1', 'msg_c2'])
  assert.equal(prefix.isReanchored('msg_c1', second.value), true)
  assert.equal(prefix.isReanchored('msg_c2', second.value), true)
  assert.equal(prefix.isReanchored('msg_c3', second.value), false)
})
}

{

// CUT-15 counterexample: the durable fold must commit an epoch-advancing line through
// the prefix owner. If the fold only clears checkpoints, the epoch never leaves 0,
// HostCompactionGate re-proposes the same compaction on every reconcile, and each
// replayed line clears the checkpoints the session rebuilt since.
const { default: assert } = await import('node:assert/strict')
const { default: test } = await import('node:test')
const contextFold = await import('../../../dist/Context/Companion/FoldSurface.js')
const policy = await import('../../../dist/Host/Contract/CompactionPolicySurface.js')

const session = 'ses_prefix_016'
let seq = 0
const envelope = (caseName, payload) => {
  seq += 1
  return {
    runtime: 'rt_prefix_016',
    seq,
    observedAt: '2026-10-20T00:00:00Z',
    id: `prefix-016-${seq}`,
    session,
    run: null,
    fact: { family: 'Context', case: caseName, payload: { SessionId: session, ...payload } },
  }
}
const reanchored = (previous, next, run) =>
  envelope('ContextReanchored', { PreviousEpochId: previous, NextEpochId: next, ObservedCompactionRun: run })
const tenure = (previous, next, incumbency) =>
  envelope('TenureReanchored', { PreviousEpochId: previous, NextEpochId: next, WorkId: 'work-1', IncumbencyId: incumbency })
const checkpoint = callId => envelope('ViewCheckpointCommitted', { ToolCallId: callId })

const folded = lines => {
  const result = contextFold.fold(lines)
  assert.equal(result.ok, true, result.ok ? '' : JSON.stringify(result.error))
  return result.value
}
const epochOf = value => value.sessions[session]?.PrefixEpoch ?? null

test('WHAT[prefix-stability-006] CUT_15_a_committed_compaction_line_advances_the_durable_epoch', () => {
  const value = folded([reanchored(0, 1, 'msg_c1')])
  const epoch = epochOf(value)
  assert.notEqual(epoch, null, 'the fold must record the prefix epoch')
  assert.equal(epoch.EpochId, 1n)
  assert.deepEqual(epoch.ReanchoredRuns, ['msg_c1'])
})

test('WHAT[prefix-stability-006] CUT_15_the_gate_candidate_reads_the_committed_projection', () => {
  const value = folded([reanchored(0, 1, 'msg_c1')])
  const handled = new Set(epochOf(value).ReanchoredRuns)
  assert.equal(policy.nextReanchor(['msg_c1'], run => handled.has(run)), null, 'no second reanchor for one compaction')
  assert.equal(policy.nextReanchor(['msg_c1', 'msg_c2'], run => handled.has(run)), 'msg_c2')
})


test('WHAT[prefix-stability-006] CUT_15_a_non_successor_epoch_line_is_refused_by_the_fold', () => {
  const result = contextFold.fold([reanchored(0, 3, 'msg_c1')])
  assert.equal(result.ok, false)
  assert.match(JSON.stringify(result.error), /successor/)
})
}
