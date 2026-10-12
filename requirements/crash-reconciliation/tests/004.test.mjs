import assert from 'node:assert/strict'
import test from 'node:test'
import * as recovery from '../../../dist/OpenCode/Host/LoadRecoverySurface.js'
import { fold, link, acceptRun, canonical } from './support/load-projection.mjs'

test('WHAT[crash-reconciliation-004] actual explicit recovery re-enters ordinary workflows from durable facts without replaying tool continuations or program counters (GAP-149)', () => {
  const state = recovery.create()
  link(state, { agent: 'engineer' })
  const child = acceptRun(state, { agent: 'engineer' })

  // Active child before restart
  const before = recovery.childView(state, 'parent', 'child')
  assert.equal(before.activeRun, child.logicalRun)
  assert.equal(before.lifecycle, 'Active')

  // Load recovery re-enters ordinary workflow from durable facts:
  // Settle active runs by voiding without replaying tool continuations or restoring program counters
  const settlements = recovery.childSettlements(state)
  assert.equal(settlements.length, 1)
  assert.equal(JSON.parse(settlements[0])[1][1][0], 'ChildWorkVoided')

  // Apply the settlement fact
  fold(state, settlements[0])

  const after = recovery.childView(state, 'parent', 'child')
  assert.equal(after.activeRun, '', 'interrupted run is voided; no program counter or tool continuation replayed')
  assert.equal(after.lifecycle, 'Active', 'handle remains active for ordinary reuse')
  assert.equal(after.joinable, 0, 'no fake completion created')
})
