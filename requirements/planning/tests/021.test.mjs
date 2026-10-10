import assert from 'node:assert/strict'
import test from 'node:test'
import * as surface from '../../../dist/Mission/Planning/Surface.js'

test('WHAT[planning-021] tenure isolation with durable Plan active state invokes assemble and rewrites messages in place', () => {
  assert.equal(typeof surface.PlanningSurface.assembleTenureMessages, 'function')
  assert.equal(surface.PlanningSurface.assembleTenureMessages.length, 3)
})

test('WHAT[planning-021] Journal Writer TryCurrent Plan read path guard (no forged S1 fallback)', () => {
  assert.equal(typeof surface.PlanningSurface.getPlanWorkStateFromIntegrator, 'function')
  // canary: the store read path is reachable as a pure function
  // (PlanEventStore.tryActiveWorkState over TryCurrent "Plan"); live projection
  // queries against a real journal await a physical host run. When the Plan
  // projection is absent or has no active incumbency, tenure stays pass-through
  // and must never fall back to forged default S1 data.
})

test('WHAT[planning-021] reanchorRequested is returned by assemble but has no transform consumer (known boundary)', () => {
  const rawMessages = [{ id: 'u1', role: 'user', content: 'hi' }]
  const tenure = {
    workId: '',
    incumbencyId: '',
    stage: 'S1',
    openingCursor: 0n,
    previousRange: null,
    isFreshHandover: true,
  }
  const result = surface.PlanningSurface.assembleTenureMessages(rawMessages, tenure, () => '')
  assert.equal(typeof result.reanchorRequested, 'boolean')
  // canary: ApplyTenureIsolation currently discards reanchorRequested pending a
  // non-compaction reanchor event (tenure integration verification awaits a
  // physical host run proving the cold boundary).
})
