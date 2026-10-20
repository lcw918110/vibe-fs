import assert from 'node:assert/strict'
import test from 'node:test'
import { capabilityToolNames, rolePredicate } from '../../../dist/OpenCode/Tools/ToolRegistrySurface.js'
import { plan } from '../../../dist/Participant/Provider/Attempt/PlannerSurface.js'

test('WHAT[capability-enforcement-005] retired request kinds and strength-replica yield empty capability set and fail closed', () => {
  for (const kind of ['strength-replica', 'blogger-main', 'speculative-investigation', 'unknown-kind']) {
    for (const role of ['engineer', 'devops', 'manager', 'orchestrator', 'plan']) {
      const tools = capabilityToolNames(role, kind)
      assert.deepEqual(tools, [], `${role}/${kind} capability tool set must be empty`)

      const planned = plan({ role, kind })
      assert.equal(planned.ok, false, `plan for retired request kind '${kind}' must fail closed`)
    }
  }
})

test('WHAT[capability-enforcement-005] retired todowrite-for-predictor and chronicle fail closed in rolePredicate', () => {
  for (const role of ['engineer', 'devops', 'manager', 'orchestrator', 'plan', 'blogger', 'predictor']) {
    assert.equal(rolePredicate('todowrite-for-predictor', role), false)
    assert.equal(rolePredicate('chronicle', role), false)
    assert.equal(rolePredicate('todowrite-for-blogger', role), false)
  }
})
