import assert from 'node:assert/strict'
import test from 'node:test'
import { withReview, scores } from './support/plugin.mjs'
import { lifecycleWorkView } from '../../verification-system/tests/support/plugin-fixture.mjs'
import * as lifecycle from '../../../dist/Mission/Lifecycle/Surface.js'
import * as journalSurface from '../../../dist/Persistence/Journal/Surface.js'

const life = lifecycle.LifecycleSurface ?? lifecycle
const retirementIdOf = (bytes) =>
  JSON.parse(bytes).events.find((event) => event.type === 'IncumbencyRetired').retirementId
const incumbencyIdOf = (text) =>
  JSON.parse(text.split('\n').find((line) => line.includes('IncumbencyOpened'))).events.find(
    (event) => event.type === 'IncumbencyOpened',
  ).incumbencyId
const retirementsOf = (work) => Array.from(work.Retirements ?? [])

const call = (sessionID, callID, messageID) => ({ sessionID, callID, messageID, agent: 'manager' })
const accepted = receipt => {
  assert.ok(['Committed', 'Replayed', 'Observed'].includes(receipt.Status), JSON.stringify(receipt))
  return receipt
}

test('WHAT[work-007] the retirement identity is recomputed from the same fence and does not merge the two confirmation counts', () => {
  const plan = life.encodeOpening('plan', 'work-007-identity', '/repo', 7n)
  const oneshot = life.deliverProbe(plan, 'run-007', 'call-007', 9n)
  assert.equal(oneshot.decision, 'Write')

  // Same fence, same work: recomputation from the real append bytes is stable.
  const recomputed = life.deliverProbe(life.encodeOpening('plan', 'work-007-identity', '/repo', 7n), 'run-007', 'call-007', 9n)
  assert.equal(retirementIdOf(recomputed.bytes), retirementIdOf(oneshot.bytes))

  // The same fence under the other confirmation count carries the other marker.
  const s4 = life.encodeOpening('S4', 'work-007-identity', '/repo', 4n)
  const reviewed = life.reviewProbe(s4, incumbencyIdOf(s4), 'Stayed')
  const confirmed = life.deliverProbe(s4 + reviewed.bytes, 'run-007', 'call-confirm', 9n)
  assert.equal(confirmed.names, 'ConfirmationCommitted')
  const twoshot = life.deliverProbe(s4 + reviewed.bytes + confirmed.bytes, 'run-007', 'call-007', 9n)
  assert.equal(twoshot.decision, 'Write')
  assert.notEqual(
    retirementIdOf(twoshot.bytes),
    retirementIdOf(oneshot.bytes),
    'oneshot-retirement-v1 and retirement-v1 must not merge into one identity',
  )
})

test('WHAT[work-007] a settled fence replays its recorded state instead of the tenure the retirement removed', async () => {
  await withReview(async ({ hooks, runtime, session, execute }) => {
    await execute(scores('REVISE'))
    assert.match(await hooks.tool.deliver.execute({}, call(session, 'deliver-confirm', 'run-confirm')), /confirmation_required = true/)
    assert.match(await hooks.tool.deliver.execute({}, call(session, 'deliver-call', 'run-retire')), /Finished\. Do not continue work\./)

    const commonDir = await journalSurface.JournalSurface_initializeStateRoot(runtime.directory)
    const settled = life.inspected(commonDir, session)
    const settledRetirements = retirementsOf(settled)
    assert.equal(settledRetirements.length, 1)
    assert.equal(settledRetirements[0].ProjectionCut, true)
    assert.equal(typeof settledRetirements[0].Fence.ToolCallId, 'string')
    assert.equal(settledRetirements[0].Expected, settled.Authority)
    const successor = settled.ActiveId
    const dispatched = runtime.prompts.length

    // Algorithm E1.1: the fence is looked up on the fold before Algorithm E2
    // reads an active tenure and before Algorithm E3 reads the review position.
    // The relayed successor is unreviewed, so answering for it would report a
    // review gap the retirement already settled.
    const replay = await hooks.tool.deliver.execute({}, call(session, 'deliver-call', 'run-retire'))
    assert.match(replay, /Finished\. Do not continue work\./)
    assert.doesNotMatch(replay, /assessment_required/)

    const after = life.inspected(commonDir, session)
    assert.equal(after.ActiveId, successor)
    assert.equal(retirementsOf(after).length, 1)
    assert.equal(runtime.prompts.length, dispatched, 'a replay dispatches no second successor')
    assert.equal((await lifecycleWorkView(runtime, session)).active, true)
    assert.equal((await lifecycleWorkView(runtime, session)).delivered, false)
  }, { session: 'replay-relayed' })
})

test('WHAT[work-007] a retried delivered fence is answered by the fold, not by the missing tenure', async () => {
  await withReview(async ({ hooks, runtime, session, execute }) => {
    await execute(scores('PERFECT'))
    assert.match(await hooks.tool.deliver.execute({}, call(session, 'deliver-confirm', 'run-confirm')), /confirmation_required = true/)
    assert.match(await hooks.tool.deliver.execute({}, call(session, 'deliver-call', 'run-retire')), /Finished\. Do not continue work\./)

    const commonDir = await journalSurface.JournalSurface_initializeStateRoot(runtime.directory)
    const settled = life.inspected(commonDir, session)
    assert.equal(settled.Delivered, true)
    assert.equal(Boolean(settled.ActiveId), false, 'a final delivery has no successor tenure')
    assert.equal(runtime.prompts.length, 0)

    // Algorithm E1.1 / E2: the recorded retirement still names this call.
    const replay = await hooks.tool.deliver.execute({}, call(session, 'deliver-call', 'run-retire'))
    assert.match(replay, /Finished\. Do not continue work\./)
    assert.doesNotMatch(replay, /not available right now/)
    const after = life.inspected(commonDir, session)
    assert.equal(retirementsOf(after).length, 1)
    assert.equal(after.Delivered, true)

    // A fence the fold never settled keeps Algorithm E2's refusal.
    const unsettled = await hooks.tool.deliver.execute({}, call(session, 'deliver-other', 'run-other'))
    assert.doesNotMatch(unsettled, /Finished\. Do not continue work\./)
  }, { session: 'replay-delivered' })
})

test('WHAT[work-007] a settled fence whose expected advance moved is refused with no write', async () => {
  await withReview(async ({ hooks, runtime, session, execute }) => {
    await execute(scores('REVISE'))
    await hooks.tool.deliver.execute({}, call(session, 'deliver-confirm', 'run-confirm'))
    await hooks.tool.deliver.execute({}, call(session, 'deliver-call', 'run-retire'))

    const commonDir = await journalSurface.JournalSurface_initializeStateRoot(runtime.directory)
    const successor = life.inspected(commonDir, session).ActiveId
    accepted(
      life.execute(commonDir, session, 'review-successor', {
        type: 'Review',
        incumbencyId: successor,
        reviewerId: 'reviewer',
        findings: [],
      }),
    )
    const before = life.inspected(commonDir, session)
    accepted(
      life.execute(commonDir, session, 'advance-after-replay', {
        type: 'Advance',
        incumbencyId: successor,
        expected: before.Authority,
        next: 'authority-after-replay',
      }),
    )
    assert.equal(life.inspected(commonDir, session).Authority, 'authority-after-replay')

    // Algorithm E1.1's closed set includes the expected advance the entry read,
    // so a retirement recorded under another authority is not replayed.
    await assert.rejects(
      () => hooks.tool.deliver.execute({}, call(session, 'deliver-call', 'run-retire')),
      /RetirementReplayConflict/,
    )
    const after = life.inspected(commonDir, session)
    assert.equal(retirementsOf(after).length, 1)
    assert.equal(after.ActiveId, successor)
  }, { session: 'replay-authority-moved' })
})
