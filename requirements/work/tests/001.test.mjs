import assert from 'node:assert/strict'
import test from 'node:test'
import { END_TOOL, EVENTS, RESULT_NAMES, REVIEW_OUTCOMES } from './support/contract.mjs'

test('WHAT[work-001] live names are the review vocabulary', () => {
  assert.deepEqual(RESULT_NAMES, ['Asked', 'Reviewed', 'Relayed', 'Advanced', 'Launched', 'Completed', 'Reopened', 'Blocked', 'Ignored'])
  assert.deepEqual(REVIEW_OUTCOMES, RESULT_NAMES)
  assert.equal(END_TOOL, 'review')
  assert.equal(EVENTS.includes('WorkOpened'), true)
  assert.equal(EVENTS.includes('QualityCertificateInvalidated'), false)
  assert.equal(EVENTS.includes('PlanDelivered'), false)
  assert.equal(END_TOOL === 'suicide', false)
})
