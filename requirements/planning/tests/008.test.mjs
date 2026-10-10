import assert from 'node:assert/strict'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import test from 'node:test'
import * as surface from '../../../dist/Mission/Planning/Surface.js'

const facts = {
  PlanEvents: surface.PlanEvents,
  PlanStage: surface.PlanStage,
  PlanRetirementOutcome: surface.PlanRetirementOutcome,
}
const fold = { PlanFold: surface.PlanFold }

test('WHAT[planning-008] deliver successfully finalizes planning artifact and records PlanDelivered', async () => {
  const tmpRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'plan-deliver-test-'))

  try {
    const workId = 'work-d1'

    // Write a valid plan artifact first
    const content = '# Final Delivered Plan\n1. Step A\n2. Step B'
    const written = surface.PlanningSurface.rewritePlanAtomic(tmpRoot, workId, content)
    assert.equal(written.ok, true)

    // Setup state up to S3 active
    let state = fold.PlanFold.initialWorkState
    state = fold.PlanFold.applyWorkEventJs(facts.PlanEvents.workOpened(workId, tmpRoot), state).state
    state = fold.PlanFold.applyWorkEventJs(
      facts.PlanEvents.incumbencyOpened(workId, 'inc-1', facts.PlanStage.S1, 0n),
      state,
    ).state
    state = fold.PlanFold.applyWorkEventJs(
      facts.PlanEvents.incumbencyRetired('inc-1', facts.PlanRetirementOutcome.Continue, 10n),
      state,
    ).state
    state = fold.PlanFold.applyWorkEventJs(
      facts.PlanEvents.incumbencyOpened(workId, 'inc-2', facts.PlanStage.S2, 11n),
      state,
    ).state
    state = fold.PlanFold.applyWorkEventJs(
      facts.PlanEvents.incumbencyRetired('inc-2', facts.PlanRetirementOutcome.Continue, 20n),
      state,
    ).state
    state = fold.PlanFold.applyWorkEventJs(
      facts.PlanEvents.incumbencyOpened(workId, 'inc-3', facts.PlanStage.S3, 21n),
      state,
    ).state

    const mockStore = {
      TryCurrent: (key) => (key === 'Plan' ? state : null),
      TryHead: () => null,
      Append: async (envs) => {
        for (const env of envs) {
          const decoded = surface.PlanningSurface.tryDecodeEnvelopeJson(JSON.stringify(env))
          if (decoded.ok) {
            state = fold.PlanFold.applyWorkEventJs(decoded.event, state).state
          }
        }
        return { tag: 0 }
      },
    }

    const res = await surface.PlanningSurface.executeDeliver(
      mockStore,
      tmpRoot,
      workId,
      'inc-3',
      false,
      ['Delivery summary note'],
      [30n],
    )

    assert.equal(res.ok, true)
    assert.equal(res.delivered, true)
    assert.equal(res.digest, written.digest)
    assert.ok(typeof res.path === 'string' && res.path.endsWith('plan.md'))

    // State reflects delivered
    assert.equal(state.Active, undefined)
    assert.equal(surface.retiredCount(state), 3)
    assert.ok(state.Delivered != null)
    assert.equal(state.Delivered.Digest, written.digest)
  } finally {
    fs.rmSync(tmpRoot, { recursive: true, force: true })
  }
})

test('WHAT[planning-008] deliver is rejected when plan artifact is empty or missing, or when in stage S1', async () => {
  const tmpRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'plan-deliver-fail-'))

  try {
    const workId = 'work-d2'

    // 1. In S1: deliver is forbidden by ActionGate
    let stateS1 = fold.PlanFold.initialWorkState
    stateS1 = fold.PlanFold.applyWorkEventJs(facts.PlanEvents.workOpened(workId, tmpRoot), stateS1).state
    stateS1 = fold.PlanFold.applyWorkEventJs(
      facts.PlanEvents.incumbencyOpened(workId, 'inc-1', facts.PlanStage.S1, 0n),
      stateS1,
    ).state

    const mockStoreS1 = {
      TryCurrent: () => stateS1,
      TryHead: () => null,
      Append: async () => ({ tag: 0 }),
    }

    const resS1 = await surface.PlanningSurface.executeDeliver(mockStoreS1, tmpRoot, workId, 'inc-1', false, null, [10n])
    assert.equal(resS1.ok, false)
    assert.match(resS1.error, /Direct delivery is forbidden in stage S1/i)

    // 2. In S3: but plan artifact is missing on disk
    let stateS3 = fold.PlanFold.initialWorkState
    stateS3 = fold.PlanFold.applyWorkEventJs(facts.PlanEvents.workOpened('work-missing', tmpRoot), stateS3).state
    stateS3 = fold.PlanFold.applyWorkEventJs(
      facts.PlanEvents.incumbencyOpened('work-missing', 'inc-1', facts.PlanStage.S1, 0n),
      stateS3,
    ).state
    stateS3 = fold.PlanFold.applyWorkEventJs(
      facts.PlanEvents.incumbencyRetired('inc-1', facts.PlanRetirementOutcome.Continue, 10n),
      stateS3,
    ).state
    stateS3 = fold.PlanFold.applyWorkEventJs(
      facts.PlanEvents.incumbencyOpened('work-missing', 'inc-2', facts.PlanStage.S2, 11n),
      stateS3,
    ).state
    stateS3 = fold.PlanFold.applyWorkEventJs(
      facts.PlanEvents.incumbencyRetired('inc-2', facts.PlanRetirementOutcome.Continue, 20n),
      stateS3,
    ).state
    stateS3 = fold.PlanFold.applyWorkEventJs(
      facts.PlanEvents.incumbencyOpened('work-missing', 'inc-3', facts.PlanStage.S3, 21n),
      stateS3,
    ).state

    const mockStoreS3 = {
      TryCurrent: () => stateS3,
      TryHead: () => null,
      Append: async () => ({ tag: 0 }),
    }

    const resMissing = await surface.PlanningSurface.executeDeliver(mockStoreS3, tmpRoot, 'work-missing', 'inc-3', false, null, [30n])
    assert.equal(resMissing.ok, false)
    assert.match(resMissing.error, /empty or missing|not found/i)
  } finally {
    fs.rmSync(tmpRoot, { recursive: true, force: true })
  }
})
