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
