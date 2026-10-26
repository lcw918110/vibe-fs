import assert from 'node:assert/strict'
import test from 'node:test'
import { Work } from 'wanxiangli/work'

const review = (work, id, updates) =>
  work.execute(id, {
    type: 'Review',
    office: 'Plan',
    providerRunId: 'run',
    toolCallId: id,
    updates,
  })

test('WHAT[work-005] an empty successor review advances the milestone in one record', () => {
  const work = new Work('work-005')
  review(work, 'r1', [{ key: 'need', value: 'a plan' }])
  const relayed = review(work, 'r2', [{ key: 'need', value: null }])
  assert.equal(relayed.result.kind, 'Relayed')
  const relayEvents = work.history().at(-1).events.map((event) => event.type)
  assert.deepEqual(relayEvents, ['IncumbencyRetired', 'ContextBarrier', 'IncumbencyOpened', 'PromptObliged'])
  assert.equal(relayEvents.includes('Delivered'), false)
  const sent = work.execute('confirm-relay', { type: 'Confirm', effectId: relayed.result.effect.id, outcome: 'Sent' })
  assert.equal(sent.status, 'Applied', sent.reason)

  const advanced = review(work, 'r3', [])
  assert.equal(advanced.result.kind, 'Advanced')
  assert.equal(advanced.result.milestone, '理解与路径')
  const advanceEvents = work.history().at(-1).events.map((event) => event.type)
  assert.deepEqual(advanceEvents, ['MilestoneAdvanced', 'PromptObliged'])
  assert.equal(advanceEvents.includes('IncumbencyOpened'), false)
  assert.equal(work.inspect().active.id, relayed.result.successorId)
})
