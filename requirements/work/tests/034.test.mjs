import assert from 'node:assert/strict'
import test from 'node:test'
import { Work } from 'wanxiangli/work'
import * as prefix from '../../../dist/Context/Prefix/Surface.js'
import * as surface from '../../../dist/Mission/Planning/Surface.js'
import {
  assistantText,
  cursors,
  ids,
  msgId,
  msgText,
  syntheticPredecessor,
  toolResult,
  userText,
} from './support/plan-messages.mjs'

const assemble = (rawMessages, tenure, messageCursors) =>
  surface.PlanningSurface.assembleTenureMessages(rawMessages, tenure, messageCursors)

const planReview = (work, id, updates) =>
  work.execute(id, {
    type: 'Review',
    office: 'Plan',
    providerRunId: 'run',
    toolCallId: id,
    updates,
  })

test('WHAT[work-034] initial tenure (S1) assembles U + current messages with no synthetic record and requests reanchor on first turn', () => {
  const rawMessages = [
    userText('u1', 'Initial user prompt'),
    userText('u2', 'Second user instruction'),
  ]

  const tenure = {
    workId: 'work-1',
    incumbencyId: 'inc-1',
    stage: 'S1',
    openingCursor: 0n,
    isFreshHandover: true,
  }

  // 1. First turn: no assistant/tool messages yet, isFreshHandover = true -> reanchorRequested = true
  const firstTurn = assemble(rawMessages, tenure, {})
  assert.equal(firstTurn.reanchorRequested, true, 'a fresh handover must request a reanchor once')
  assert.equal(firstTurn.messages.length, 2)
  assert.equal(msgId(firstTurn.messages[0]), 'u1')
  assert.equal(msgId(firstTurn.messages[1]), 'u2')

  // 2. Subsequent turn: current tenure assistant/tool messages exist -> reanchorRequested stays false
  const subsequentMessages = [
    ...rawMessages,
    assistantText('a1', 'working on S1', 'u2'),
    toolResult('t1', 'todowrite-for-plan', 'draft'),
  ]
  const tenureContinuing = { ...tenure, isFreshHandover: false }
  const secondTurn = assemble(
    subsequentMessages,
    tenureContinuing,
    cursors({ a1: 5n, t1: 6n }),
  )
  assert.equal(secondTurn.reanchorRequested, false, 'a continuing tenure must not re-request reanchor')
  assert.equal(secondTurn.messages.length, 4)
  assert.deepEqual(
    ids(secondTurn.messages),
    ['u1', 'u2', 'a1', 't1'],
  )
})

const mark = (message, fields) => Object.assign(message, { toolCallId: null, resultFor: null, tenureId: null, ...fields })

const barrierTenure = (extra = {}) => ({
  workId: 'work-1',
  incumbencyId: 'next',
  stage: '执行',
  openingCursor: 0n,
  isFreshHandover: false,
  barrierToolCallId: 'review-1',
  successorTenureId: 'next',
  ...extra,
})

test('WHAT[work-034] a committed barrier keeps the user messages and the triggering review turn and hides the rest before it', () => {
  const rawMessages = [
    mark(userText('u-old', 'Initial user prompt'), { tenureId: 'old' }),
    mark(assistantText('a-old', 'previous tenure', 'u-old'), { tenureId: 'old' }),
    mark(assistantText('call', 'review'), { toolCallId: 'review-1', tenureId: 'old' }),
    mark(toolResult('result', 'review', 'recorded'), { resultFor: 'review-1', tenureId: 'old' }),
    mark(userText('u-next', 'Feedback from user'), { tenureId: 'next' }),
    mark(assistantText('a-current', 'successor work', 'u-next'), { tenureId: 'next' }),
  ]
  const before = JSON.stringify(rawMessages)
  const result = assemble(rawMessages, barrierTenure(), {})
  assert.equal(JSON.stringify(rawMessages), before)
  assert.deepEqual(ids(result.messages), ['u-old', 'call', 'result', 'u-next', 'a-current'])
  assert.equal(result.messages[0], rawMessages[0])
  assert.equal(ids(result.messages).includes('a-old'), false)
})

test('WHAT[work-034] user messages U preserve original bytes and order verbatim without modification', () => {
  const originalUser1 = userText('u1', 'Exact bytes line 1\nLine 2\tTabs and spaces   ')
  const originalUser2 = userText('u2', 'Another user prompt with emojis: 💡🚀')

  const tenure = {
    workId: 'work-1',
    incumbencyId: 'inc-1',
    stage: 'S1',
    openingCursor: 0n,
    isFreshHandover: true,
  }

  const result = assemble([originalUser1, originalUser2], tenure, {})

  assert.equal(result.messages.length, 2)
  // Strict identity and byte-for-byte content equality
  assert.equal(msgText(result.messages[0]), msgText(originalUser1))
  assert.equal(msgText(result.messages[1]), msgText(originalUser2))
  assert.deepEqual(result.messages[0], originalUser1)
  assert.deepEqual(result.messages[1], originalUser2)
})

test('WHAT[work-034] the first part immediately after the opening cursor survives tenure assembly', () => {
  const rawMessages = [
    userText('u1', 'go'),
    assistantText('a-first', 'first current assistant part', 'u1'),
    userText('u2', 'follow-up'),
  ]
  const tenure = {
    workId: 'work-1',
    incumbencyId: 'inc-2',
    stage: 'S2',
    openingCursor: 20n,
    isFreshHandover: false,
  }
  const result = assemble(
    rawMessages,
    tenure,
    cursors({ 'a-first': 21n, u2: 22n }),
  )
  const assembledIds = ids(result.messages)
  assert.ok(assembledIds.includes('a-first'), 'the first part right after opening must survive')
  assert.ok(assembledIds.includes('u2'), 'the follow-up user reply must survive in order')
  assert.deepEqual(assembledIds, ['u1', 'a-first', 'u2'])
})

test('WHAT[work-034] a current assistant row with a Host id but no first-observation entry stays', () => {
  const rawMessages = [
    userText('u1', 'go'),
    assistantText('a-no-cursor', 'current work', 'u1'),
  ]
  const tenure = {
    workId: 'work-1',
    incumbencyId: 'inc-2',
    stage: 'S2',
    openingCursor: 20n,
    isFreshHandover: false,
  }
  const result = assemble(rawMessages, tenure, {})
  assert.ok(ids(result.messages).includes('a-no-cursor'))
  assert.equal(result.messages.filter((m) => msgId(m).startsWith('synthetic-prev-')).length, 0)
})

test('WHAT[work-034] assembly does not invent a host id or a synthetic predecessor', () => {
  const rawMessages = [
    userText('u1', 'go'),
    {
      info: { role: 'assistant' },
      parts: [{ type: 'text', text: 'current work without a stable identity' }],
    },
  ]
  const tenure = {
    workId: 'work-1',
    incumbencyId: 'inc-2',
    stage: '执行',
    openingCursor: 0n,
    isFreshHandover: false,
  }
  const result = assemble(rawMessages, tenure, {})
  assert.equal(result.messages.some((message) => String(msgId(message) ?? '').startsWith('synthetic-prev-')), false)
  assert.ok(ids(result.messages).includes('u1'))
})

test('WHAT[work-034] a barrier drops synthetic rows and does not inject a predecessor', () => {
  const rawMessages = [
    mark(userText('u1', 'go'), { tenureId: 'old' }),
    mark(assistantText('call', 'review'), { toolCallId: 'review-1', tenureId: 'old' }),
    mark(toolResult('result', 'review', 'recorded'), { resultFor: 'review-1', tenureId: 'old' }),
    mark(syntheticPredecessor('inc-old', 'work-1', 'stale synthesized row'), { tenureId: 'next' }),
    mark(assistantText('a-current', 'current work', 'u1'), { tenureId: 'next' }),
  ]
  const result = assemble(rawMessages, barrierTenure(), {})
  assert.equal(result.messages.some((message) => msgId(message).startsWith('synthetic-prev-')), false)
  assert.deepEqual(ids(result.messages), ['u1', 'call', 'result', 'a-current'])
})

test('WHAT[work-034] durable Plan active state assembles U + current interactions in place', () => {
  const rawMessages = [
    userText('u1', 'go'),
    assistantText('a1', 'current work', 'u1'),
    userText('u2', 'feedback'),
  ]
  const tenure = {
    workId: 'w-25a',
    incumbencyId: 'inc-2',
    stage: 'S2',
    openingCursor: 20n,
    isFreshHandover: false,
  }
  const result = assemble(rawMessages, tenure, cursors({ a1: 25n, u2: 26n }))
  assert.deepEqual(
    ids(result.messages),
    ['u1', 'a1', 'u2'],
    'U and current interactions must be assembled in place in chronological order',
  )
  assert.equal(result.reanchorRequested, false, 'a continuing durable tenure must not re-request reanchor')
})

test('WHAT[work-034] a fresh Plan review opens at 事实与取舍 and carries no retirement', () => {
  const work = new Work('w-25a')
  const reviewed = planReview(work, 'r1', [{ key: 'need', value: 'scope' }])
  assert.equal(reviewed.status, 'Applied', reviewed.reason)
  assert.equal(reviewed.result.kind, 'Reviewed')
  assert.equal(reviewed.result.milestone, '事实与取舍')
  const events = work.history().flatMap((frame) => frame.events)
  assert.equal(events.some((event) => event.type === 'WorkOpened'), true)
  assert.equal(events.some((event) => event.type === 'IncumbencyRetired'), false)
})

test('WHAT[work-034] reanchorRequested is returned by assemble and is true only on a fresh handover', () => {
  const rawMessages = [userText('u1', 'hi')]
  const tenure = {
    workId: 'w-25b',
    incumbencyId: 'inc-1',
    stage: 'S1',
    openingCursor: 0n,
    isFreshHandover: true,
  }
  const fresh = assemble(rawMessages, tenure, {})
  assert.equal(typeof fresh.reanchorRequested, 'boolean')
  assert.equal(fresh.reanchorRequested, true, 'a fresh handover must request a reanchor')

  const continuing = assemble(rawMessages, { ...tenure, isFreshHandover: false }, {})
  assert.equal(continuing.reanchorRequested, false, 'a continuing tenure must not re-request reanchor on an empty stop')
})

test('WHAT[work-034] clearing the plan ledger relays in one record', () => {
  const work = new Work('w1')
  const reviewed = planReview(work, 'r1', [{ key: 'need', value: 'scope' }])
  assert.equal(reviewed.result.kind, 'Reviewed')
  assert.equal(reviewed.result.milestone, '事实与取舍')
  assert.equal(work.history().some((frame) => frame.events.some((event) => event.type === 'IncumbencyRetired')), false)
  const relayed = planReview(work, 'call-034', [{ key: 'need', value: null }])
  assert.equal(relayed.status, 'Applied', relayed.reason)
  assert.equal(relayed.result.kind, 'Relayed')
  const record = work.history().at(-1)
  assert.deepEqual(record.events.map((event) => event.type), ['IncumbencyRetired', 'ContextBarrier', 'IncumbencyOpened', 'PromptObliged'])
  assert.equal(record.events.filter((event) => event.type === 'IncumbencyRetired').length, 1)
  assert.equal(record.events.find((event) => event.type === 'IncumbencyRetired').outcome, 'Relayed')
  assert.equal(work.history().filter((frame) => frame.events.some((event) => event.type === 'IncumbencyRetired')).length, 1)
})

test('WHAT[work-034] tenure assembly keeps the user request and the triggering review turn and does not materialize a previous record', () => {
  const rawMessages = [
    mark(userText('u1', 'go'), { tenureId: 'old' }),
    mark(assistantText('call', 'review'), { toolCallId: 'review-1', tenureId: 'old' }),
    mark(toolResult('result', 'review', 'recorded'), { resultFor: 'review-1', tenureId: 'old' }),
    mark(assistantText('a-new', 'new work', 'u1'), { tenureId: 'next' }),
  ]
  const result = surface.PlanningSurface.assembleTenureMessages(rawMessages, barrierTenure(), {})
  const assembledIds = ids(result.messages)
  assert.ok(!assembledIds.some((id) => id.startsWith('synthetic-prev-')))
  assert.deepEqual(assembledIds, ['u1', 'call', 'result', 'a-new'])
})

test('WHAT[work-034] tenure reanchor event shape carries session, epochs, work and incumbency', () => {
  assert.equal(typeof prefix.applyTenureReanchor, 'function')
  assert.equal(typeof prefix.isTenureReanchored, 'function')
  assert.equal(typeof prefix.reanchoredTenures, 'function')
})

test('WHAT[work-034] duplicate incumbencyId is rejected on second tenure reanchor', () => {
  const empty = prefix.empty
  const first = prefix.applyTenureReanchor({ previousEpoch: 0n, nextEpoch: 1n, incumbencyId: 'inc-1' }, empty)
  assert.equal(first.ok, true)
  const second = prefix.applyTenureReanchor({ previousEpoch: 1n, nextEpoch: 2n, incumbencyId: 'inc-1' }, first.value)
  assert.equal(second.ok, false)
  assert.equal(second.error, 'TenureAlreadyReanchored')
})

test('WHAT[work-034] reanchorRequested is true on fresh handover and deduplicated via isTenureReanchored', () => {
  const rawMessages = [userText('u1', 'hi')]
  const tenure = {
    workId: 'w-27',
    incumbencyId: 'inc-1',
    stage: 'S1',
    openingCursor: 0n,
    isFreshHandover: true,
  }
  const result = surface.PlanningSurface.assembleTenureMessages(rawMessages, tenure, {})
  assert.equal(result.reanchorRequested, true)

  const continuing = surface.PlanningSurface.assembleTenureMessages(
    rawMessages,
    { ...tenure, isFreshHandover: false },
    {},
  )
  assert.equal(continuing.reanchorRequested, false)
})

test('WHAT[work-034] successor projection hides earlier rows and never inserts a synthetic predecessor', () => {
  const rawMessages = [
    mark(userText('u-history', 'Original user request'), { tenureId: 'old' }),
    mark(assistantText('call', 'review'), { toolCallId: 'review-1', tenureId: 'old' }),
    mark(toolResult('result', 'review', 'recorded'), { resultFor: 'review-1', tenureId: 'old' }),
    mark(assistantText('a-current', 'this tenure answer'), { tenureId: 'next' }),
    mark(assistantText('a-uncaptured', 'this request, not captured yet'), { tenureId: 'next' }),
  ]
  const result = assemble(rawMessages, barrierTenure(), {})
  assert.equal(result.messages.filter((message) => msgId(message).startsWith('synthetic-prev-')).length, 0)
  assert.deepEqual(ids(result.messages), ['u-history', 'call', 'result', 'a-current', 'a-uncaptured'])
})

test('WHAT[work-034] the barrier hides every non-user row before the review and keeps earlier user text', () => {
  const rawMessages = [
    mark(userText('u-history', 'Original user request'), { tenureId: 'old' }),
    mark(assistantText('a-s1', 'earlier assistant'), { tenureId: 'old' }),
    mark(assistantText('call', 'review'), { toolCallId: 'review-1', tenureId: 'old' }),
    mark(toolResult('result', 'review', 'recorded'), { resultFor: 'review-1', tenureId: 'old' }),
    mark(userText('u-command', 'Please continue.'), { tenureId: 'next' }),
    mark(assistantText('a-post', 'successor assistant', 'u-command'), { tenureId: 'next' }),
  ]
  const result = assemble(rawMessages, barrierTenure({ workId: 'work-post', incumbencyId: 'next' }), {})
  const kept = ids(result.messages)
  assert.equal(result.messages.filter((message) => msgId(message).startsWith('synthetic-prev-')).length, 0)
  assert.ok(!kept.includes('a-s1'))
  assert.deepEqual(kept, ['u-history', 'call', 'result', 'u-command', 'a-post'])
})
