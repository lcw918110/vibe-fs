import assert from 'node:assert/strict'
import { readdirSync, readFileSync } from 'node:fs'
import { join } from 'node:path'
import test from 'node:test'
import * as relay from '../../../dist/Mission/Relay/Surface.js'
import { JournalSurface_snapshot } from '../../../dist/Persistence/Journal/Surface.js'

// 语言锚定：下面两条断言比较英文指令资源的渲染结果；显式设为英文使断言不随宿主环境语言漂移。
process.env.WANXIANGSHU_PROVIDER_LANGUAGE = 'en'

const gap = [{ acceptance_criteria: 'the delivery still misses part of the target state', work_plan: 'close the remaining gap' }]

const open = (state, snapshot = 'snapshot-1') =>
  relay.openIncumbency(state, 'road-1', 'inc-1', snapshot, 'authority-1')

test('WHAT[relay-assessment-002] second assessment in one iteration is rejected without overwriting the first', () => {
  const opened = open(relay.empty())
  const assessed = relay.assess(opened.state, 'road-1', 'inc-1', 'assessment-1', 'snapshot-1', 'authority-1', gap)
  assert.equal(assessed.ok, true)

  const replayed = relay.assess(
    assessed.state,
    'road-1',
    'inc-1',
    'assessment-1',
    'snapshot-1',
    'authority-1',
    gap,
  )
  assert.equal(replayed.ok, true)

  const conflicted = relay.assess(
    assessed.state,
    'road-1',
    'inc-1',
    'assessment-1',
    'snapshot-1',
    'authority-1',
    [],
  )
  assert.deepEqual(conflicted, { ok: false, error: 'AssessmentReplayConflict' })

  const second = relay.assess(
    assessed.state,
    'road-1',
    'inc-1',
    'assessment-2',
    'snapshot-1',
    'authority-1',
    [],
  )
  assert.deepEqual(second, { ok: false, error: 'AssessmentAlreadySubmitted' })
})

test('WHAT[relay-assessment-002] cross-iteration replay of another iteration assessment is rejected', () => {
  const opened = open(relay.empty())
  const assessed = relay.assess(opened.state, 'road-1', 'inc-1', 'assessment-1', 'snapshot-1', 'authority-1', gap)
  assert.equal(assessed.ok, true)
  const retired = relay.retireContinue(assessed.state, 'road-1', 'inc-1', 'ret-1', 'run-1', 'tool-1', 'snapshot-1')
  assert.equal(retired.ok, true)
  const next = relay.openIncumbency(retired.state, 'road-1', 'inc-2', 'snapshot-2', 'authority-1')
  assert.equal(next.ok, true)
  const replayed = relay.assess(
    next.state,
    'road-1',
    'inc-2',
    'assessment-1',
    'snapshot-2',
    'authority-1',
    gap,
  )
  assert.deepEqual(replayed, { ok: false, error: 'AssessmentReplayConflict' })
})

const {withReview, scores: reviewScores} = await import('./support/plugin.mjs')
const {withSuccessor} = await import('../../relay-context-projection/tests/support/cut.mjs')

test('WHAT[relay-assessment-002] actual tool exact replay returns the accepted result', async () => {
  await withReview(async ({execute, hooks, session}) => {
    const input = reviewScores('REVISE')
    const first = await execute(input)
    assert.match(first, /recorded = true/)
    const replay = await hooks.tool.review.execute(input, {sessionID: session, callID: 'review-call', messageID: 'review-run', agent: 'manager'})
    assert.equal(replay, first)
  })
})

test('WHAT[relay-assessment-002] changed public narrative binding rejects replay without changing the accepted facts', async () => {
  await withReview(async ({execute, hooks, directory, runtime, session}) => {
    const input = reviewScores('REVISE')
    const first = await execute(input)
    assert.match(first, /recorded = true/)
    const messages = runtime.messages.filter(message => message.info?.id === 'review-run')
    assert.equal(messages.length, 1)
    const message = messages[0]
    const callIndex = message.parts.findIndex(part => part.type === 'tool' && part.callID === 'review-call')
    assert.ok(callIndex > 0)
    assert.deepEqual(message.parts[callIndex].state.input, input)
    const narrative = message.parts.slice(0, callIndex).find(part => part.type === 'text')
    assert.ok(narrative)
    const original = narrative.text
    const accepted = structuredClone(JournalSurface_snapshot(runtime.journal))
    const eventDirectory = join(directory, '.git', 'wanxiangshu', 'events')
    const facts = () => readdirSync(eventDirectory).filter(name => name.endsWith('.ndjson')).sort()
      .map(name => ({ name, bytes: readFileSync(join(eventDirectory, name)) }))
    const acceptedFacts = facts()
    assert.ok(acceptedFacts.length > 0)
    const context = {sessionID: session, callID: 'review-call', messageID: 'review-run', agent: 'manager'}
    try {
      narrative.text = 'The public assessment evidence changed after the accepted review.'
      const changed = await hooks.tool.review.execute(input, context)
      assert.match(changed, /recorded = false/)
      assert.deepEqual(JournalSurface_snapshot(runtime.journal), accepted)
      assert.deepEqual(facts(), acceptedFacts)
      narrative.text = original
      const exact = await hooks.tool.review.execute(input, context)
      assert.equal(exact, first)
      assert.deepEqual(JournalSurface_snapshot(runtime.journal), accepted)
      assert.deepEqual(facts(), acceptedFacts)
    } finally {
      narrative.text = original
    }
  })
})

test('WHAT[relay-assessment-002] same identity with changed input is a replay conflict and the accepted result stays', async () => {
  await withReview(async ({execute, hooks, session}) => {
    const input = reviewScores('REVISE')
    const first = await execute(input)
    assert.match(first, /recorded = true/)
    const changed = await hooks.tool.review.execute(reviewScores('PERFECT'), {sessionID: session, callID: 'review-call', messageID: 'review-run', agent: 'manager'})
    assert.match(changed, /recorded = false/)
    const exact = await hooks.tool.review.execute(input, {sessionID: session, callID: 'review-call', messageID: 'review-run', agent: 'manager'})
    assert.equal(exact, first)
  })
})

test('WHAT[relay-assessment-002] same input under a new call id is already-submitted', async () => {
  await withReview(async ({execute, hooks, session}) => {
    const input = reviewScores('REVISE')
    const first = await execute(input)
    assert.match(first, /recorded = true/)
    const second = await execute(input, {call: 'review-call-2', run: 'review-run-2'})
    assert.match(second, /recorded = false/)
    const exact = await hooks.tool.review.execute(input, {sessionID: session, callID: 'review-call', messageID: 'review-run', agent: 'manager'})
    assert.equal(exact, first)
  })
})

test('WHAT[relay-assessment-002] cross-incumbency replay of a retired review call is rejected', async () => {
  await withSuccessor(async ({execute, hooks, session}) => {
    const input = reviewScores('REVISE')
    // withSuccessor already recorded this exact call in the first incumbency and
    // drove the real suicide retirement chain; the successor manager prompt is the
    // owner-dispatched gate, so the next incumbency is open in AuditPending.
    const retired = await hooks.tool.review.execute(input, {sessionID: session, callID: 'review-call', messageID: 'review-run', agent: 'manager'})
    assert.match(retired, /recorded = false/)
    // The gate binds the retired call id, not the successor's review right: a fresh
    // call id in the next incumbency records its own independent assessment.
    const successor = await execute(input, {call: 'review-call-2', run: 'review-run-2'})
    assert.match(successor, /recorded = true/)
    // With the successor assessment accepted, the retired call id still cannot
    // replay: it never becomes an idempotent hit on the successor's result.
    const replayed = await hooks.tool.review.execute(input, {sessionID: session, callID: 'review-call', messageID: 'review-run', agent: 'manager'})
    assert.match(replayed, /recorded = false/)
  })
})
