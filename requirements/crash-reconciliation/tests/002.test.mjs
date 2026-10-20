import assert from 'node:assert/strict'
import test from 'node:test'
import * as child from '../../../dist/Execution/Delegation/Fork/ChildRecoverySurface.js'
import * as handles from '../../../dist/Execution/Delegation/Handle/Surface.js'

test('WHAT[crash-reconciliation-002] child resolver uses durable completion or current terminal evidence while missing evidence remains incomplete', () => {
  assert.equal(child.resolve('active', 'missing', [], '').result, 'RecoveryIncomplete')
  assert.equal(child.resolve('completed', 'missing', [], '').result, 'RecoveredTerminal')
  assert.equal(child.resolve('active', 'terminal', [], 'delivered-view').result, 'RecoveredTerminal')
})

test('WHAT[crash-reconciliation-002] local handle projection preserves completion across a duplicate completion transition', () => {
  for (const scenario of ['completed', 'replayed-completed']) {
    const state = handles.crashScenario(scenario)
    assert.equal(state.lifecycle, 'CompletedAwaitingJoin')
    assert.deepEqual(state.completion, { kind: 'Terminal' })
    assert.equal(state.joinable, 1)
  }
})

test.todo('WHAT[crash-reconciliation-002] fresh process reconstructs completion from committed journal and physical snapshot without cache clock or prose evidence (GAP-149)')
