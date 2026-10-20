import assert from 'node:assert/strict'
import test from 'node:test'
import * as child from '../../../dist/Execution/Delegation/Fork/ChildRecoverySurface.js'
import * as codec from '../../../dist/Execution/Delegation/Fork/CleanBreakSurface.js'

const event = (kind, extra = {}) => ({ kind, ...extra })
const proof = event('TerminalProofIssued', { agent: 'a' })
const committed = event('HandleCompletionCommitted', { agent: 'a' })
const returned = event('JoinReturned', { agent: 'a' })
const aborted = event('RawAbortObserved', { session: 'child' })
const started = event('ChildRecoveryStarted', { session: 'child' })

test('WHAT[crash-reconciliation-009] abort-only evidence is incomplete and terminal proof requires a body', () => {
  assert.equal(child.resolve('active', 'missing', ['aborted:transport', 'restore'], '').result, 'RecoveryIncomplete')
  assert.equal(child.resolve('active', 'terminal', [], 'delivered-view').result, 'RecoveredTerminal')
  assert.deepEqual(child.provenTerminal('delivered-view'), { ok: true, finality: 'Succeeded', body: 'delivered-view' })
  assert.equal(child.provenTerminal('').ok, false)
})

test('WHAT[crash-reconciliation-009] actual durable completion decoder rejects legacy aborted material as joinable completion', () => {
  const legacy = JSON.stringify({ status: 'aborted', run_id: 'run', code: 'CANCELLED', message: 'Host abort', child_session_id: 'child' })
  assert.deepEqual(codec.decode(legacy), { case: 'LegacyFalseAbort' })
  assert.equal(codec.tryDecode('handle', legacy).ok, false)
  assert.deepEqual(codec.decode('{broken'), { case: 'Invalid' })
})

test('WHAT[crash-reconciliation-009] trace checker distinguishes supplied proof commit return order and identities', () => {
  for (const legal of [[], [aborted], [proof, committed, returned], [aborted, started, proof, committed, returned]]) {
    assert.equal(child.trace(legal), true)
  }
  for (const illegal of [
    [started, committed, returned], [proof, returned], [committed, proof, returned],
    [aborted, committed, proof, returned], [proof, committed, aborted, returned],
    [event('TerminalProofIssued', { agent: 'other' }), committed, returned],
  ]) assert.equal(child.trace(illegal), false)
})

test.todo('WHAT[crash-reconciliation-009] real child recovery and join consume decoded completion while abort alone cannot publish child terminal (GAP-149)')
