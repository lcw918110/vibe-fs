import assert from 'node:assert/strict'
import test from 'node:test'
import * as planner from '../../../dist/Participant/Provider/Attempt/PlannerSurface.js'
import * as turns from '../../../dist/Interaction/Repair/CompletedTurnSurface.js'

test('WHAT[provider-attempt-recovery-016] actual RequestKind algebra clears only business-main success', () => {
  for (const [role, kind, clears] of [
    ['engineer', 'work-main', true],
    ['engineer', 'interaction-repair', false],
  ]) {
    const plan = planner.plan({ role, kind })
    assert.equal(plan.ok, true)
    assert.equal(plan.requestKind, kind)
    assert.equal(plan.clearsFailureCountOnSuccess, clears, kind)
  }

  for (const retiredKind of ['blogger-main', 'strength-replica', 'blogger-squash']) {
    const plan = planner.plan({ role: 'engineer', kind: retiredKind })
    assert.equal(plan.ok, false)
    assert.equal(plan.error, 'unknown request kind')
  }
  for (const retiredRole of ['blogger', 'predictor']) {
    const plan = planner.plan({ role: retiredRole, kind: 'work-main' })
    assert.equal(plan.ok, false)
    assert.equal(plan.error, 'unknown role')
  }
})

test('WHAT[provider-attempt-recovery-016] tool-calls finish is a successful provider attempt while the turn continues', () => {
  assert.deepEqual(turns.classifyOutcome(true, 'tool-calls', null, []), { kind: 'TurnInProgress', reason: null })
})

test.todo('WHAT[provider-attempt-recovery-016] real ordinary completion uses durable kind evidence and writes SuccessRecorded only for eligible successes (GAP-139)')
