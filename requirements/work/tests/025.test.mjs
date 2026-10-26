import assert from 'node:assert/strict'
import test from 'node:test'
import { Work } from 'wanxiangli/work'

const review = (work, id, updates, extra = {}) =>
  work.execute(id, {
    type: 'Review',
    office: 'Manager',
    providerRunId: 'run',
    toolCallId: id,
    updates,
    ...extra,
  })

const confirm = (work, effect) => {
  const reply = work.execute(`confirm-${effect.id}`, { type: 'Confirm', effectId: effect.id, outcome: 'Sent' })
  assert.equal(reply.status, 'Applied', reply.reason)
}

test('WHAT[work-025] clearing the ledger relays at the same milestone', () => {
  const work = new Work('work-25')
  const recorded = review(work, 'r1', [{ key: 'job', value: 'do' }])
  assert.equal(recorded.result.kind, 'Reviewed')
  assert.equal(recorded.result.milestone, '执行')
  const kept = review(work, 'r2', [])
  assert.equal(kept.result.kind, 'Reviewed')
  assert.equal(work.inspect().active.id, recorded.result.incumbencyId)
  const relayed = review(work, 'r3', [{ key: 'job', value: null }], { blockers: ['live-child'] })
  assert.equal(relayed.result.kind, 'Relayed')
  assert.equal(relayed.result.milestone, '执行')
  assert.notEqual(relayed.result.successorId, recorded.result.incumbencyId)
  const events = work.history().at(-1).events
  assert.equal(events[0].outcome, 'Relayed')
  assert.equal(events.some((event) => event.type === 'DeliveryRequested'), false)
  assert.equal(events.some((event) => event.type === 'IncumbencyRetired'), true)
  assert.equal(events.some((event) => event.type === 'IncumbencyOpened'), true)
  confirm(work, relayed.result.effect)
  const filled = review(work, 'r4', [{ key: 'job', value: 'again' }])
  assert.equal(filled.result.kind, 'Reviewed')
  assert.equal(filled.result.milestone, '执行')
  const second = review(work, 'r5', [{ key: 'job', value: null }])
  assert.equal(second.result.kind, 'Relayed')
  assert.equal(second.result.milestone, '执行')
})

test('WHAT[work-025] an effect that is still pending refuses the next review', () => {
  const work = new Work('work-25-pending')
  review(work, 'r1', [{ key: 'job', value: 'do' }])
  const relayed = review(work, 'r2', [{ key: 'job', value: null }])
  assert.equal(relayed.result.kind, 'Relayed')
  const before = work.records().length
  const blocked = review(work, 'r3', [])
  assert.equal(blocked.status, 'Conflict')
  assert.equal(work.records().length, before)
  confirm(work, relayed.result.effect)
  assert.equal(review(work, 'r3', []).result.kind, 'Advanced')
})
