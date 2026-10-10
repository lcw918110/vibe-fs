import assert from 'node:assert/strict'
import test from 'node:test'
import * as surface from '../../../dist/Mission/Planning/Surface.js'

const facts = {
  PlanEvents: surface.PlanEvents,
  PlanStage: surface.PlanStage,
  PlanRetirementOutcome: surface.PlanRetirementOutcome,
}
const fold = { PlanFold: surface.PlanFold }

test('WHAT[planning-009] ask returns waiting-for-user record immediately without blocking', () => {
  let state = fold.PlanFold.initialWorkState
  state = fold.PlanFold.applyWorkEventJs(facts.PlanEvents.workOpened('work-ask-1', '/root'), state).state
  state = fold.PlanFold.applyWorkEventJs(
    facts.PlanEvents.incumbencyOpened('work-ask-1', 'inc-1', facts.PlanStage.S1, 0n),
    state,
  ).state

  const mockStore = {
    TryCurrent: () => state,
  }

  // 1. Valid question returns waiting marker immediately
  const qText = 'Should the plan support PostgreSQL or SQLite?'
  const res = surface.PlanningSurface.executeAsk(mockStore, 'work-ask-1', qText)

  assert.equal(res.ok, true)
  assert.equal(res.kind, 'waiting_for_user')
  assert.equal(res.question, qText)
  assert.equal(res.status, 'pending')

  // 2. Empty question is rejected
  const resEmpty = surface.PlanningSurface.executeAsk(mockStore, 'work-ask-1', '   ')
  assert.equal(resEmpty.ok, false)
  assert.match(resEmpty.error, /cannot be empty/i)
})

test('WHAT[planning-009] ask is rejected when planning artifact has already been delivered', () => {
  let state = fold.PlanFold.initialWorkState
  state = fold.PlanFold.applyWorkEventJs(facts.PlanEvents.workOpened('work-ask-2', '/root'), state).state
  state = fold.PlanFold.applyWorkEventJs(
    facts.PlanEvents.incumbencyOpened('work-ask-2', 'inc-1', facts.PlanStage.S1, 0n),
    state,
  ).state
  state = fold.PlanFold.applyWorkEventJs(
    facts.PlanEvents.incumbencyRetired('inc-1', facts.PlanRetirementOutcome.Delivered, 10n),
    state,
  ).state
  state = fold.PlanFold.applyWorkEventJs(
    facts.PlanEvents.delivered('inc-1', 'work-ask-2', 'sha256-hash', 'path.md'),
    state,
  ).state

  const mockStore = {
    TryCurrent: () => state,
  }

  const res = surface.PlanningSurface.executeAsk(mockStore, 'work-ask-2', 'Any question?')
  assert.equal(res.ok, false)
  assert.match(res.error, /already been delivered/i)
})
