import assert from 'node:assert/strict'
import test from 'node:test'
import * as compression from '../../../dist/Context/Companion/CompressionSurface.js'
import * as prefix from '../../../dist/Context/Prefix/Surface.js'
import * as failureOwner from '../../../dist/Participant/Provider/Attempt/Fallback/ProviderFailureSurface.js'

const requestKind = prefix.requestKind

const budget = failureOwner.budget

test('WHAT[context-compression-006] role is fixed across retry attempts and consecutive failures', () => {
  const planner = compression.attemptPlanner

  const plan0 = planner.plan({ kind: requestKind.workMain, role: 'engineer' })
  const plan1 = planner.plan({ kind: requestKind.workMain, role: 'engineer' })
  const plan2 = planner.plan({ kind: requestKind.workMain, role: 'engineer' })

  assert.equal(plan0.participant.toLowerCase(), 'engineer')
  assert.equal(plan1.participant.toLowerCase(), 'engineer')
  assert.equal(plan2.participant.toLowerCase(), 'engineer')

  assert.equal(plan0.participantIdentity.canonicalRole, 'engineer')
  assert.equal(plan1.participantIdentity.canonicalRole, 'engineer')
  assert.equal(plan2.participantIdentity.canonicalRole, 'engineer')

  for (const plan of [plan0, plan1, plan2]) {
    assert.equal('budget' in plan, false, 'plans carry no budget snapshot')
    assert.equal('agent' in plan, false, 'plans carry no agent field')
    assert.equal('projectionChoice' in plan, false, 'plans carry no projectionChoice')
  }
})

test('WHAT[context-compression-006] consecutive failures consume the failure budget and exhaustion halts auto-retry', () => {
  let b = budget.initial
  assert.deepEqual(b, { failures: 0 })
  assert.equal(budget.verdict(budget.defaultBudget, b), 'MayRetry')

  for (let i = 1; i < budget.defaultBudget; i++) {
    b = budget.recordFailure(b)
    assert.equal(b.failures, i)
    assert.equal(budget.verdict(budget.defaultBudget, b), 'MayRetry')
  }

  // Budget reached
  b = budget.recordFailure(b)
  assert.equal(b.failures, budget.defaultBudget)
  assert.equal(budget.verdict(budget.defaultBudget, b), 'Exhausted')

  // Business main success clears failure count
  const cleared = budget.recordSuccess(b)
  assert.deepEqual(cleared, { failures: 0 })
  assert.equal(budget.verdict(budget.defaultBudget, cleared), 'MayRetry')
})

test.todo('WHAT[context-compression-006] actual repeated dispatch binds a fresh physical identity and consumes each confirmed failure once; GAP-104')
