import assert from 'node:assert/strict'
import test from 'node:test'
import { END_TOOL, EVENTS, RESULT_NAMES, REVIEW_OUTCOMES } from './support/contract.mjs'
import { Work } from 'wanxiangli/work'

test('WHAT[work-001] real review records use current milestones and results without legacy qualification states', () => {
  const work = new Work('current-vocabulary')
  const review = (id, updates) => work.execute(id, {
    type: 'Review', office: 'Manager', providerRunId: 'run', toolCallId: id, updates,
  })
  assert.equal(review('open', [{ key: 'goal', value: 'finish' }]).result.kind, 'Reviewed')
  const relayed = review('relay', [{ key: 'goal', value: null }]).result
  assert.equal(relayed.kind, 'Relayed')
  assert.equal(work.execute('sent-relay', { type: 'Confirm', effectId: relayed.effect.id, outcome: 'Sent' }).status, 'Applied')
  const advanced = review('advance', []).result
  assert.equal(advanced.kind, 'Advanced')
  assert.equal(work.execute('sent-advance', { type: 'Confirm', effectId: advanced.effect.id, outcome: 'Sent' }).status, 'Applied')
  assert.equal(review('finish', []).result.kind, 'Completed')
  const records = work.records()
  assert.doesNotMatch(records.join('\n'), /"(?:Promoted|Stayed|S[1-4])"/)
  assert.deepEqual(new Work('current-vocabulary', records).records(), records)
  const events = work.history().flatMap(record => record.events)
  assert.ok(events.some(event => event.type === 'Reviewed'))
  assert.ok(events.some(event => event.type === 'ContextBarrier'))
  assert.ok(events.some(event => event.type === 'StopObliged'))
})

test('WHAT[work-001] live names are the review vocabulary', () => {
  assert.deepEqual(RESULT_NAMES, ['Asked', 'Reviewed', 'Relayed', 'Advanced', 'Launched', 'Completed', 'Reopened', 'Blocked', 'Ignored'])
  assert.deepEqual(REVIEW_OUTCOMES, RESULT_NAMES)
  assert.equal(END_TOOL, 'review')
  assert.equal(EVENTS.includes('WorkOpened'), true)
  assert.equal(EVENTS.includes('QualityCertificateInvalidated'), false)
  assert.equal(EVENTS.includes('PlanDelivered'), false)
  assert.equal(END_TOOL === 'suicide', false)
})
