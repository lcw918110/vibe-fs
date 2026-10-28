import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const planner = await import("../../../dist/Participant/Provider/Attempt/PlannerSurface.js");

test('WHAT[context-compression-008] work session plans use the ordinary work request kind', () => {
  const plan = planner.plan({ role: 'Engineer', kind: 'WorkMain' })
  assert.equal(plan.requestKind, 'work-main')
  for (const input of [{ role: 'blogger' }, { role: 'predictor' }, { role: 'chronicle' }]) {
    const result = planner.plan(input)
    assert.equal(result.ok, false)
  }
})
}

test('WHAT[context-compression-008] compaction advances the wire epoch without resetting scope progress', async () => {
  const assert = (await import('node:assert/strict')).default
  const prefix = await import('../../../dist/Context/Prefix/Surface.js')
  const { ScopeProgress } = await import('wanxiangli/scope-progress')
  const progress = ScopeProgress()
  const session = 'scope-compaction-008'
  const scopeId = (state) => String(prefix.reanchoredScopes(state).length)
  let state = prefix.empty
  assert.equal(progress.record(session, scopeId(state), 'r1', 't1', 4).ok, true)
  assert.equal(progress.settle(session, scopeId(state), 'r1').estimate, 4)
  const compacted = prefix.applyReanchor({ previousEpoch: 0, nextEpoch: 1, observedRun: 'compact-1' }, state)
  assert.equal(compacted.ok, true)
  state = compacted.value
  assert.equal(Number(prefix.epochOf(state)), 1)
  assert.equal(scopeId(state), '0')
  assert.equal(progress.record(session, scopeId(state), 'r2', 't2', 3).ok, true)
  assert.equal(progress.settle(session, scopeId(state), 'r2').advisor, false)
  const scope = prefix.applyScopeReanchor({ previousEpoch: 1, nextEpoch: 2, callId: 'real-reset' }, state)
  assert.equal(scope.ok, true)
  state = scope.value
  assert.equal(scopeId(state), '1')
  progress.reset(session, scopeId(state))
  const tenure = prefix.applyTenureReanchor({ previousEpoch: 2, nextEpoch: 3, incumbencyId: 'next-tenure' }, state)
  assert.equal(tenure.ok, true)
  state = tenure.value
  assert.equal(scopeId(state), '1')
  assert.equal(progress.record(session, scopeId(state), 'r3', 't3', 2).ok, true)
  assert.equal(progress.settle(session, scopeId(state), 'r3').advisor, false)
})
