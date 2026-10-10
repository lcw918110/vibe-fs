import assert from 'node:assert/strict'
import test from 'node:test'
import * as surface from '../../../dist/Mission/Planning/Surface.js'

const facts = { PlanEvents: surface.PlanEvents, PlanStage: surface.PlanStage }
const fold = { PlanFold: surface.PlanFold }

test('WHAT[planning-007] handoff transitions stage S1 to S2 and opens next incumbency without blocker', async () => {
  let state = fold.PlanFold.initialWorkState

  // Setup opened work and S1 incumbency
  const evOpen = facts.PlanEvents.workOpened('work-h1', '/repo/root')
  state = fold.PlanFold.applyWorkEventJs(evOpen, state).state

  const evInc1 = facts.PlanEvents.incumbencyOpened('work-h1', 'inc-1', facts.PlanStage.S1, 0n)
  state = fold.PlanFold.applyWorkEventJs(evInc1, state).state

  // Mock store holding current state and accumulating appended events
  const appended = []
  const mockStore = {
    TryCurrent: (key) => (key === 'Plan' ? state : null),
    TryHead: () => null,
    Append: async (envs) => {
      appended.push(...envs)
      // apply into state
      for (const env of envs) {
        const decoded = surface.PlanningSurface.tryDecodeEnvelopeJson(JSON.stringify(env))
        if (decoded.ok) {
          state = fold.PlanFold.applyWorkEventJs(decoded.event, state).state
        }
      }
      return { tag: 0 }
    },
  }

  // 1. S1 Handoff
  const res1 = await surface.PlanningSurface.executeHandoff(
    mockStore,
    'work-h1',
    'inc-1',
    false,
    ['S1 complete note'],
    [10n],
  )
  assert.equal(res1.ok, true)
  assert.equal(res1.retiredIncumbencyId, 'inc-1')
  assert.equal(res1.nextStage, 'S2')
  assert.ok(typeof res1.nextIncumbencyId === 'string' && res1.nextIncumbencyId.startsWith('inc-'))

  // State in store now has S2 active
  assert.equal(state.Active.Stage.name, 'S2')
  assert.equal(surface.retiredCount(state), 1)

  // 2. S2 Handoff to S3
  const res2 = await surface.PlanningSurface.executeHandoff(
    mockStore,
    'work-h1',
    res1.nextIncumbencyId,
    false,
    ['S2 complete note'],
    [20n],
  )
  assert.equal(res2.ok, true, `res2 failed: ${res2.error}`)
  assert.equal(res2.nextStage, 'S3')
  assert.equal(state.Active.Stage.name, 'S3')
  assert.equal(surface.retiredCount(state), 2)

  // 3. S3 Handoff is forbidden
  const res3 = await surface.PlanningSurface.executeHandoff(
    mockStore,
    'work-h1',
    res2.nextIncumbencyId,
    false,
    ['S3 attempt handoff'],
    [30n],
  )
  assert.equal(res3.ok, false)
  assert.match(res3.error, /Handoff is forbidden; deliver only|cannot hand off/i)
})

test('WHAT[planning-007] handoff is blocked when active blocker resources exist or wrong incumbency id is provided', async () => {
  let state = fold.PlanFold.initialWorkState
  state = fold.PlanFold.applyWorkEventJs(facts.PlanEvents.workOpened('work-h2', '/repo/root'), state).state
  state = fold.PlanFold.applyWorkEventJs(
    facts.PlanEvents.incumbencyOpened('work-h2', 'inc-1', facts.PlanStage.S1, 0n),
    state,
  ).state

  const mockStore = {
    TryCurrent: (key) => (key === 'Plan' ? state : null),
    TryHead: () => null,
    Append: async () => ({ tag: 0 }),
  }

  // Blocker presence rejects handoff
  const resBlocked = await surface.PlanningSurface.executeHandoff(mockStore, 'work-h2', 'inc-1', true, null, [10n])
  assert.equal(resBlocked.ok, false)
  assert.match(resBlocked.error, /blocked by live resources/i)

  // Wrong incumbency ID rejects handoff
  const resWrongId = await surface.PlanningSurface.executeHandoff(mockStore, 'work-h2', 'wrong-inc', false, null, [10n])
  assert.equal(resWrongId.ok, false)
  assert.match(resWrongId.error, /does not match invocation target/i)
})
