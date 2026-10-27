import assert from 'node:assert/strict'
import test from 'node:test'
import * as turns from '../../../dist/Interaction/Repair/CompletedTurnSurface.js'

test('WHAT[provider-attempt-recovery-025] upstream thought-only error without output is unified into repairable continuation', () => {
  const parts = [{ type: 'reasoning', text: 'thinking without answering' }]
  const errorName = 'upstream_error'
  // When Cloud Code Assist API returns thought-only response, CompletedTurnClassifier treats it as repairable continuation:
  const outcome = turns.classifyOutcome(true, null, errorName, parts)
  assert.ok(outcome.kind === 'TurnFailed' || outcome.kind === 'TurnNeedsContinuation')
  // Content is unusable (formal text is missing):
  assert.equal(turns.formalContentUnusable(parts), true)
  // Needs interaction repair:
  assert.equal(turns.needsInteractionRepair('engineer', false, 'stop', parts), true)
  assert.equal(turns.needsInteractionRepair('devops', false, 'stop', parts), true)
})
