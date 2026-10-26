import assert from 'node:assert/strict'
import test from 'node:test'
import { planSourceWriteAllowed, devopsDispatchAllowed } from '../../../dist/OpenCode/Tools/ToolRegistrySurface.js'
import { admitAction } from './support/contract.mjs'

const open = { active: true, reviewed: false, sequence: ['S4'], deliveryAuthorized: false }

test('WHAT[work-004] unreviewed tenure still admits read and observe, and refuses source writes and DevOps dispatch', () => {
  assert.equal(admitAction(open, 'read').admitted, true)
  assert.equal(admitAction(open, 'observe').admitted, true)
  assert.equal(admitAction(open, 'write-source').admitted, false)
  assert.equal(admitAction(open, 'dispatch-devops').admitted, false)
  assert.equal(admitAction(open, 'review').admitted, true)
  const refused = ['read', 'observe', 'review'].filter((action) => !admitAction(open, action).admitted)
  assert.deepEqual(refused, [])
})

test('WHAT[work-004] the shipped write and DevOps predicates refuse while a question is unanswered', () => {
  assert.equal(planSourceWriteAllowed(true), false)
  assert.equal(devopsDispatchAllowed(true), false)
  assert.equal(planSourceWriteAllowed(false), true)
  assert.equal(devopsDispatchAllowed(false), true)
})
