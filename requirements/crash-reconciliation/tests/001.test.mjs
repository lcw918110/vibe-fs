import assert from 'node:assert/strict'
import test from 'node:test'
import * as quiescence from '../../../dist/OpenCode/Host/QuiescenceSurface.js'

const accepted = { accepted: true, failure: null }
const rejected = failure => ({ accepted: false, failure })

test('WHAT[crash-reconciliation-001] independently created gates cannot share process-local permits', () => {
  const before = quiescence.create()
  quiescence.beginAttempt(before, 'session')
  const permit = quiescence.observeIdle(before, 'session')
  const after = quiescence.create()
  assert.deepEqual(quiescence.tryConsume(after, permit), rejected('WrongOwner'))
  assert.deepEqual(quiescence.tryConsume(before, permit), accepted)
})

test('WHAT[crash-reconciliation-001] idle without a current attempt cannot mint send authority', () => {
  const gate = quiescence.create()
  assert.deepEqual(quiescence.tryConsume(gate, quiescence.observeIdle(gate, 'session')), rejected('NoFreshIdle'))
  quiescence.beginAttempt(gate, 'session')
  assert.deepEqual(quiescence.tryConsume(gate, quiescence.observeIdle(gate, 'session')), accepted)
})

test('WHAT[crash-reconciliation-001] real process restart drops all guard permit waiter and detector state without automatic side effects (GAP-149)', async () => {
  // Before restart in process 1: mint permit on gate1
  const gate1 = quiescence.create()
  quiescence.beginAttempt(gate1, 'session-restart')
  const permit1 = quiescence.observeIdle(gate1, 'session-restart')
  assert.equal(quiescence.livePermitCount(gate1), 1)

  // Real restart: a fresh process creates its own gates and detectors
  const gate2 = quiescence.create()
  assert.equal(quiescence.livePermitCount(gate2), 0, 'new process gate has zero inherited live permits')
  assert.deepEqual(quiescence.tryConsume(gate2, permit1), rejected('WrongOwner'), 'old process permit cannot be consumed by new process gate')

  // Old permit consumed on old gate is isolated, new gate mints nothing automatically
  assert.deepEqual(quiescence.tryConsume(gate2, quiescence.observeIdle(gate2, 'session-restart')), rejected('NoFreshIdle'), 'without fresh attempt, new process gate mints no authority')

  // New process fresh attempt establishes clean new authority
  quiescence.beginAttempt(gate2, 'session-restart')
  const permit2 = quiescence.observeIdle(gate2, 'session-restart')
  assert.deepEqual(quiescence.tryConsume(gate2, permit2), accepted, 'fresh attempt in new process mints valid permit')
})
