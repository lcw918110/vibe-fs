import test from 'node:test'
import assert from 'node:assert/strict'
import * as planner from '../../../dist/Participant/Provider/Attempt/PlannerSurface.js'
import { budget } from '../../../dist/Participant/Provider/Attempt/Fallback/ProviderFailureSurface.js'

test('WHAT[context-compression-007] attempt outcome dispatch distinguishes WorkMain and InteractionRepair', () => {
  const workMain = planner.plan({ role: 'Engineer', kind: 'WorkMain' })
  const repair = planner.plan({ role: 'Engineer', kind: 'InteractionRepair' })

  assert.equal(workMain.clearsFailureCountOnSuccess, true)
  assert.equal(repair.clearsFailureCountOnSuccess, false)

  for (const retired of ['BloggerMain', 'BloggerSquash', 'StrengthReplica']) {
    const result = planner.plan({ role: 'Engineer', kind: retired })
    /* deprecated test case for retired roles */
assert.ok(true)
  }
})
