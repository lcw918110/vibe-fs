import assert from 'node:assert/strict'
import test from 'node:test'
import * as surface from '../../../dist/Mission/Planning/Surface.js'

const facts = {
  PlanEvents: surface.PlanEvents,
  PlanStage: surface.PlanStage,
  PlanRetirementOutcome: surface.PlanRetirementOutcome,
}
const fold = { PlanFold: surface.PlanFold }

test('WHAT[planning-010] resume dispatches single-flight DevOps and binds target if unbound', async () => {
  let state = fold.PlanFold.initialWorkState
  state = fold.PlanFold.applyWorkEventJs(facts.PlanEvents.workOpened('work-res-1', '/root'), state).state
  state = fold.PlanFold.applyWorkEventJs(
    facts.PlanEvents.incumbencyOpened('work-res-1', 'inc-1', facts.PlanStage.S1, 0n),
    state,
  ).state

  const appended = []
  const mockStore = {
    TryCurrent: () => state,
    TryHead: () => null,
    Append: async (envs) => {
      appended.push(...envs)
      for (const env of envs) {
        const decoded = surface.PlanningSurface.tryDecodeEnvelopeJson(JSON.stringify(env))
        if (decoded.ok) {
          state = fold.PlanFold.applyWorkEventJs(decoded.event, state).state
        }
      }
      return { tag: 0 }
    },
  }

  // 1. Initial resume dispatches and creates DevOps binding
  const chargeText = 'Investigate whether build artifact paths match expectation'
  const res = await surface.PlanningSurface.executeResume(mockStore, 'work-res-1', chargeText, null)

  assert.equal(res.ok, true)
  assert.equal(res.status, 'dispatched')
  assert.equal(res.target, 'devops')
  assert.equal(res.charge, chargeText)

  // Verify DevOps is now bound in state
  assert.ok(state.BoundDevOps != null)
  assert.equal(state.BoundDevOps[0], 'devops')

  // 2. Empty charge is rejected
  const resEmpty = await surface.PlanningSurface.executeResume(mockStore, 'work-res-1', '   ', null)
  assert.equal(resEmpty.ok, false)
  assert.match(resEmpty.error, /cannot be empty/i)
})

test('WHAT[planning-010] resume is rejected when planning artifact has already been delivered', async () => {
  let state = fold.PlanFold.initialWorkState
  state = fold.PlanFold.applyWorkEventJs(facts.PlanEvents.workOpened('work-res-2', '/root'), state).state
  state = fold.PlanFold.applyWorkEventJs(
    facts.PlanEvents.incumbencyOpened('work-res-2', 'inc-1', facts.PlanStage.S1, 0n),
    state,
  ).state
  state = fold.PlanFold.applyWorkEventJs(
    facts.PlanEvents.incumbencyRetired('inc-1', facts.PlanRetirementOutcome.Delivered, 10n),
    state,
  ).state
  state = fold.PlanFold.applyWorkEventJs(
    facts.PlanEvents.delivered('inc-1', 'work-res-2', 'sha256-hash', 'path.md'),
    state,
  ).state

  const mockStore = {
    TryCurrent: () => state,
    TryHead: () => null,
    Append: async () => ({ tag: 0 }),
  }

  const res = await surface.PlanningSurface.executeResume(mockStore, 'work-res-2', 'Investigate something', null)
  assert.equal(res.ok, false)
  assert.match(res.error, /already been delivered/i)
})
