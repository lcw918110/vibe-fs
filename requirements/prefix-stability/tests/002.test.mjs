import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const prefix = await import("../../../dist/Context/Prefix/Surface.js");

const reanchor = (state, { previousEpoch, nextEpoch, observedRun = 'msg_compaction' }) =>
  prefix.applyReanchor({ previousEpoch, nextEpoch, observedRun }, state)

test('WHAT[prefix-stability-002] COMPANION_009_initial_epoch_has_no_promotable_prefix', () => {
  assert.equal(prefix.epochOf(prefix.empty), 0n)
  assert.deepEqual(prefix.reanchoredRuns(prefix.empty), [])
})

test('WHAT[prefix-stability-002] CTX_012_a_reanchor_advances_the_epoch_once_and_records_the_run', () => {
  const result = reanchor(prefix.empty, { previousEpoch: 0, nextEpoch: 1 })

  assert.equal(result.ok, true, result.ok ? '' : result.error)
  assert.equal(prefix.epochOf(result.value), 1n)
  assert.deepEqual(prefix.reanchoredRuns(result.value), ['msg_compaction'])
})

test('WHAT[prefix-stability-002] CTX_012_a_replayed_reanchor_is_absorbed_so_recovery_is_idempotent', () => {
  const committed = reanchor(prefix.empty, { previousEpoch: 0, nextEpoch: 1 }).value
  const replayed = reanchor(committed, { previousEpoch: 0, nextEpoch: 1 })

  // The projection reports the typed refusal; the fold absorbs it as the
  // expected steady state, so recovery stays idempotent.
  assert.deepEqual(replayed, { ok: false, error: 'CompactionAlreadyReanchored' })
  assert.equal(prefix.epochOf(committed), 1n)
  assert.deepEqual(prefix.reanchoredRuns(committed), ['msg_compaction'])
})

test('WHAT[prefix-stability-002] CTX_012_a_non_sequential_epoch_is_refused', () => {
  const refused = reanchor(prefix.empty, { previousEpoch: 0, nextEpoch: 3 })

  assert.equal(refused.ok, false)
  assert.equal(refused.error, 'NonSequentialPrefixEpoch')
})

test('WHAT[prefix-stability-002] CTX_012_a_tenure_reanchor_advances_once_and_records_the_incumbency', () => {
  const result = prefix.applyTenureReanchor(
    { previousEpoch: 0, nextEpoch: 1, incumbencyId: 'incumbent-1' },
    prefix.empty,
  )

  assert.equal(result.ok, true, result.ok ? '' : result.error)
  assert.equal(prefix.epochOf(result.value), 1n)
  assert.equal(prefix.isTenureReanchored('incumbent-1', result.value), true)
  assert.deepEqual(prefix.reanchoredTenures(result.value), ['incumbent-1'])
})
}
