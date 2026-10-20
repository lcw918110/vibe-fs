import assert from 'node:assert/strict'
import test from 'node:test'
import * as failureOwner from '../../../dist/Participant/Provider/Attempt/Fallback/ProviderFailureSurface.js'

const budget = failureOwner.budget

test('WHAT[context-compression-023] retry is event-driven by failure budget and independent of wall clock', () => {
  // WHAT [023]: Provider retry correctness path is purely event-driven with no clock dependency.
  // The failure budget dictates auto-retry availability directly from durable failure records.
  let b = budget.initial
  assert.deepEqual(b, { failures: 0 })
  assert.equal(budget.verdict(budget.defaultBudget, b), 'MayRetry')

  // Record failure sequentially without delays or timestamps
  for (let i = 1; i < budget.defaultBudget; i++) {
    b = budget.recordFailure(b)
    assert.equal(b.failures, i)
    assert.equal(budget.verdict(budget.defaultBudget, b), 'MayRetry')
  }

  b = budget.recordFailure(b)
  assert.equal(b.failures, budget.defaultBudget)
  assert.equal(budget.verdict(budget.defaultBudget, b), 'Exhausted')

  const cleared = budget.recordSuccess(b)
  assert.deepEqual(cleared, { failures: 0 })
  assert.equal(budget.verdict(budget.defaultBudget, cleared), 'MayRetry')
})

test.todo('WHAT[context-compression-023] actual retry waits on committed open-request changes and ends only on new coverage or exact settlement, with no clock dependency; GAP-104')
