import assert from 'node:assert/strict'
import test from 'node:test'
import { Work } from 'wanxiangli/work'
import { planSourceWriteAllowed, devopsDispatchAllowed } from '../../../dist/OpenCode/Tools/ToolRegistrySurface.js'

test('WHAT[work-004] an unanswered ask writes no review and the shipped gates refuse source writes and DevOps dispatch', () => {
  const work = new Work('work-004')
  const asked = work.execute('r1', {
    type: 'Review',
    office: 'Plan',
    providerRunId: 'run',
    toolCallId: 'r1',
    updates: [],
  })
  assert.equal(asked.status, 'Applied', asked.reason)
  assert.equal(asked.result.kind, 'Asked')
  assert.equal(work.history()[0].events.some((event) => event.type === 'Reviewed'), false)
  const wait = work.recover().wait
  assert.equal(wait === 'ConfirmEffect' || wait === 'AwaitOperatorAnswer', true)
  assert.equal(planSourceWriteAllowed(true), false)
  assert.equal(devopsDispatchAllowed(true), false)
})

test('WHAT[work-004] the shipped write and DevOps predicates refuse while a question is unanswered', () => {
  assert.equal(planSourceWriteAllowed(true), false)
  assert.equal(devopsDispatchAllowed(true), false)
  assert.equal(planSourceWriteAllowed(false), true)
  assert.equal(devopsDispatchAllowed(false), true)
})
