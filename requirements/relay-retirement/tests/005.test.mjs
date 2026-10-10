import assert from 'node:assert/strict'
import test from 'node:test'
import {JournalSurface_snapshot} from '../../../dist/Persistence/Journal/Surface.js'
import {withReview, scores} from '../../relay-assessment/tests/support/plugin.mjs'

const suicide = ({hooks, session}, call, run = call) =>
  hooks.tool.suicide.execute({}, {sessionID: session, callID: call, messageID: run, agent: 'manager'})

test('WHAT[relay-retirement-005] the first suicide returns review commitments and deferred work without retiring', async () => {
  await withReview(async fixture => {
    assert.match(await fixture.execute(scores('REVISE')), /recorded = true/)
    const first = await suicide(fixture, 'confirm-call', 'confirm-run')
    assert.match(first, /finished = false/)
    assert.match(first, /confirmation_required = true/)
    assert.match(first, /commitments/)
  })
})

test('WHAT[relay-retirement-005] a second suicide call with a new tool call retires normally', async () => {
  await withReview(async fixture => {
    assert.match(await fixture.execute(scores('REVISE')), /recorded = true/)
    assert.match(await suicide(fixture, 'confirm-call', 'confirm-run'), /confirmation_required = true/)
    assert.match(await suicide(fixture, 'retire-call', 'retire-run'), /finished = true/)
  })
})

test('WHAT[relay-retirement-005] replaying the exact first confirmation is idempotent and does not retire', async () => {
  await withReview(async fixture => {
    assert.match(await fixture.execute(scores('REVISE')), /recorded = true/)
    const first = await suicide(fixture, 'confirm-call', 'confirm-run')
    assert.match(first, /confirmation_required = true/)
    const replay = await suicide(fixture, 'confirm-call', 'confirm-run')
    assert.equal(replay, first)
  })
})

test('WHAT[relay-retirement-005] confirmation does not check blockers and the second call blocks on an owned child', async () => {
  await withReview(async fixture => {
    assert.match(await fixture.execute(scores('REVISE')), /recorded = true/)
    assert.match(
      await fixture.hooks.tool.fork.execute(
        {calling: 'engineer', name: 'Ada', charge: 'Implement the requested behavior.'},
        {sessionID: fixture.session, callID: 'fork-child', messageID: 'work-run', agent: 'manager'},
      ),
      /Ada/,
    )
    const first = await suicide(fixture, 'confirm-call', 'confirm-run')
    assert.match(first, /confirmation_required = true/)
    assert.doesNotMatch(first, /blocker_count/)
    const second = await suicide(fixture, 'retire-call', 'retire-run')
    assert.match(second, /finished = false/)
    assert.match(second, /blocker_count = [1-9]/)
  })
})

test('WHAT[relay-retirement-005] the same tool call from a different provider run is rejected as RetirementConfirmationReplayConflict without retiring', async () => {
  await withReview(async fixture => {
    assert.match(await fixture.execute(scores('REVISE')), /recorded = true/)
    const first = await suicide(fixture, 'confirm-call', 'confirm-run')
    assert.match(first, /confirmation_required = true/)
    await assert.rejects(
      () => suicide(fixture, 'confirm-call', 'other-run'),
      /RetirementConfirmationReplayConflict/,
    )
    const relay = JournalSurface_snapshot(fixture.runtime.journal).sessionProjections[fixture.session].relay
    assert.equal(relay.roads.length, 1)
    assert.equal(relay.roads[0].latestRetirementPresent, false)
    assert.equal(await suicide(fixture, 'confirm-call', 'confirm-run'), first)
  })
})
