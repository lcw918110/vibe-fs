import assert from 'node:assert/strict'
import test from 'node:test'
import * as prefix from '../../../dist/Context/Prefix/Surface.js'
import * as surface from '../../../dist/Mission/Planning/Surface.js'
import {
  assistantText,
  cursors,
  ids,
  msgId,
  msgRole,
  msgText,
  syntheticLwr,
  toolResult,
  userText,
} from './support/plan-messages.mjs'

const assemble = (rawMessages, tenure, materializePrev, messageCursors) =>
  surface.PlanningSurface.assembleTenureMessages(rawMessages, tenure, materializePrev, messageCursors)

test('WHAT[planning-017] initial tenure (S1) assembles U + current messages with no LWR_prev and requests reanchor on first turn', () => {
  const rawMessages = [
    userText('u1', 'Initial user prompt'),
    userText('u2', 'Second user instruction'),
  ]

  const tenure = {
    workId: 'work-1',
    incumbencyId: 'inc-1',
    stage: 'S1',
    openingCursor: 0n,
    previousRange: null,
    isFreshHandover: true,
  }

  const matPrev = () => {
    throw new Error('materializePrev must not be called for initial tenure')
  }

  // 1. First turn: no assistant/tool messages yet, isFreshHandover = true -> reanchorRequested = true
  const firstTurn = assemble(rawMessages, tenure, matPrev, {})
  assert.equal(firstTurn.reanchorRequested, true, 'a fresh handover must request a reanchor once')
  assert.equal(firstTurn.messages.length, 2)
  assert.equal(msgId(firstTurn.messages[0]), 'u1')
  assert.equal(msgId(firstTurn.messages[1]), 'u2')

  // 2. Subsequent turn: current tenure assistant/tool messages exist -> reanchorRequested stays false
  const subsequentMessages = [
    ...rawMessages,
    assistantText('a1', 'working on S1', 'u2'),
    toolResult('t1', 'js-plan', 'draft'),
  ]
  const tenureContinuing = { ...tenure, isFreshHandover: false }
  const secondTurn = assemble(
    subsequentMessages,
    tenureContinuing,
    matPrev,
    cursors({ a1: 5n, t1: 6n }),
  )
  assert.equal(secondTurn.reanchorRequested, false, 'a continuing tenure must not re-request reanchor')
  assert.equal(secondTurn.messages.length, 4)
  assert.deepEqual(
    ids(secondTurn.messages),
    ['u1', 'u2', 'a1', 't1'],
  )
})

test('WHAT[planning-017] non-initial tenure strips prior assistant/tool messages, injects LWR_prev before the first current interaction, and preserves the current user reply after it', () => {
  // Real Host rows: historical U, prior S1 assistant+tool, a current user
  // reply (feedback) and a current assistant row. The canonical cursor map
  // carries first-observation evidence for every physical row, including the
  // current user reply, so LWR_prev is injected before that reply — never
  // moving it ahead of an older current assistant.
  const rawMessages = [
    userText('u1', 'Initial user prompt'),
    assistantText('a-old', 'S1 assistant work', 'u1'),
    toolResult('t-old', 'js-plan', 'S1 tool work'),
    userText('u2', 'Feedback from user'),
    assistantText('a-current', 'S2 assistant fresh', 'u2'),
    toolResult('t-current', 'js-plan', 'S2 tool result'),
  ]

  const tenureS2 = {
    workId: 'work-1',
    incumbencyId: 'inc-2',
    stage: 'S2',
    openingCursor: 20n,
    previousRange: [0n, 15n],
    isFreshHandover: false,
  }

  let matRangeCalledWith = null
  const matPrev = (rangeObj) => {
    matRangeCalledWith = rangeObj
    return '# Previous Incumbency Work Record\nCompleted S1 diagnosis.'
  }

  const result = assemble(
    rawMessages,
    tenureS2,
    matPrev,
    cursors({ 'a-old': 5n, 't-old': 8n, u2: 22n, 'a-current': 25n, 't-current': 26n }),
  )

  // Verify previous range was passed to materialize
  assert.ok(matRangeCalledWith != null)
  assert.equal(matRangeCalledWith.start, 0n)
  assert.equal(matRangeCalledWith.end, 15n)

  // Assembled sequence: U1, LWR_prev, U2(current reply), a-current, t-current.
  // a-old and t-old are stripped; LWR_prev is injected before the first
  // retained current physical message whose first observation exceeds the
  // opening cursor — here the current user reply u2 (cursor 22 > 20). The
  // reply stays in chronological position before the newer current assistant.
  assert.deepEqual(
    ids(result.messages),
    ['u1', 'lwr-prev-inc-2', 'u2', 'a-current', 't-current'],
  )

  const lwrMsg = result.messages[1]
  assert.equal(msgId(lwrMsg), 'lwr-prev-inc-2')
  assert.equal(msgRole(lwrMsg), 'user')
  assert.equal(msgText(lwrMsg), '# Previous Incumbency Work Record\nCompleted S1 diagnosis.')
  assert.equal(lwrMsg.info.synthetic, true)

  // Prior assistant/tool messages must be completely gone
  const assembledIds = ids(result.messages)
  assert.ok(!assembledIds.includes('a-old'), 'prior assistant message must be stripped')
  assert.ok(!assembledIds.includes('t-old'), 'prior tool message must be stripped')
})

test('WHAT[planning-017] user messages U preserve original bytes and order verbatim without modification', () => {
  const originalUser1 = userText('u1', 'Exact bytes line 1\nLine 2\tTabs and spaces   ')
  const originalUser2 = userText('u2', 'Another user prompt with emojis: 💡🚀')

  const tenure = {
    workId: 'work-1',
    incumbencyId: 'inc-1',
    stage: 'S1',
    openingCursor: 0n,
    previousRange: null,
    isFreshHandover: true,
  }

  const result = assemble([originalUser1, originalUser2], tenure, () => '', {})

  assert.equal(result.messages.length, 2)
  // Strict identity and byte-for-byte content equality
  assert.equal(msgText(result.messages[0]), msgText(originalUser1))
  assert.equal(msgText(result.messages[1]), msgText(originalUser2))
  assert.deepEqual(result.messages[0], originalUser1)
  assert.deepEqual(result.messages[1], originalUser2)
})

test('WHAT[planning-017] the first part immediately after the opening cursor survives tenure assembly', () => {
  // The very first assistant/tool row whose first observation is exactly the
  // opening cursor + 1 must survive — the boundary is strict `>` so the first
  // current interaction is retained.
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
    previousRange: [0n, 19n],
    isFreshHandover: false,
  }
  const result = assemble(
    rawMessages,
    tenure,
    () => '# prev',
    cursors({ 'a-first': 21n, u2: 22n }),
  )
  const assembledIds = ids(result.messages)
  assert.ok(assembledIds.includes('a-first'), 'the first part right after opening must survive')
  assert.ok(assembledIds.includes('u2'), 'the follow-up user reply must survive after LWR')
  // LWR_prev is injected before the first retained current interaction (a-first).
  assert.deepEqual(assembledIds, ['u1', 'lwr-prev-inc-2', 'a-first', 'u2'])
})

test('WHAT[planning-017] a current assistant row with no canonical cursor and semantic parts rejects assembly (fail-closed)', () => {
  const rawMessages = [
    userText('u1', 'go'),
    assistantText('a-no-cursor', 'current work', 'u1'),
  ]
  const tenure = {
    workId: 'work-1',
    incumbencyId: 'inc-2',
    stage: 'S2',
    openingCursor: 20n,
    previousRange: [0n, 19n],
    isFreshHandover: false,
  }
  // No cursor entry for a-no-cursor: an assistant/tool row carrying semantic
  // parts must never be silently dropped for lack of evidence.
  assert.throws(
    () => assemble(rawMessages, tenure, () => '# prev', {}),
    /no stable XTrace message identity/i,
  )
})

test('WHAT[planning-017] synthetic previous LWR rows are stripped and exactly one current record is injected', () => {
  const rawMessages = [
    userText('u1', 'go'),
    syntheticLwr('inc-old', 'work-1', 'stale synthesized row'),
    assistantText('a-current', 'current work', 'u1'),
  ]
  const tenure = {
    workId: 'work-1',
    incumbencyId: 'inc-2',
    stage: 'S2',
    openingCursor: 20n,
    previousRange: [0n, 19n],
    isFreshHandover: false,
  }
  const result = assemble(
    rawMessages,
    tenure,
    () => '# Fresh record',
    cursors({ 'a-current': 25n }),
  )
  const lwrRows = result.messages.filter((m) => msgId(m).startsWith('lwr-prev-'))
  assert.equal(lwrRows.length, 1, 'exactly one current LWR_prev is injected')
  assert.equal(msgId(lwrRows[0]), 'lwr-prev-inc-2')
  assert.ok(!result.messages.some((m) => msgId(m) === 'lwr-prev-inc-old'))
})

test('WHAT[planning-017] durable Plan active state assembles U + current interactions in place', () => {
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
    previousRange: [0n, 15n],
    isFreshHandover: false,
  }
  const result = assemble(rawMessages, tenure, () => '# prev', cursors({ a1: 25n, u2: 26n }))
  assert.deepEqual(
    ids(result.messages),
    ['u1', 'lwr-prev-inc-2', 'a1', 'u2'],
    'U, LWR_prev and current interactions must be assembled in place with LWR injected before the first current interaction',
  )
  assert.equal(result.reanchorRequested, false, 'a continuing durable tenure must not re-request reanchor')
})

test('WHAT[planning-017] the Plan store read path is reachable as a pure function', () => {
  assert.equal(typeof surface.PlanningSurface.getPlanWorkStateFromIntegrator, 'function')
})

test('WHAT[planning-017] reanchorRequested is returned by assemble and is true only on a fresh handover', () => {
  const rawMessages = [userText('u1', 'hi')]
  const tenure = {
    workId: 'w-25b',
    incumbencyId: 'inc-1',
    stage: 'S1',
    openingCursor: 0n,
    previousRange: null,
    isFreshHandover: true,
  }
  const fresh = assemble(rawMessages, tenure, () => '', {})
  assert.equal(typeof fresh.reanchorRequested, 'boolean')
  assert.equal(fresh.reanchorRequested, true, 'a fresh handover must request a reanchor')

  const continuing = assemble(rawMessages, { ...tenure, isFreshHandover: false }, () => '', {})
  assert.equal(continuing.reanchorRequested, false, 'a continuing tenure must not re-request reanchor on an empty stop')
})

const openWork = (workId) => surface.PlanningSurface.workOpened(workId, '/tmp/plan-test')
const openInc = (workId, incId, stage, cursor) =>
  surface.PlanningSurface.incumbencyOpened(workId, incId, stage, cursor)
const retireInc = (incId, outcome, cursor) =>
  surface.PlanningSurface.incumbencyRetired(incId, outcome, cursor)

const firstStateOf = (events) => {
  let st = surface.PlanningSurface.empty()
  for (const ev of events) {
    const res = surface.PlanningSurface.applyWorkEvent(ev, st)
    assert.equal(res.ok, true, JSON.stringify(res))
    st = res.state
  }
  return st
}

const tenureFor = (overrides) => ({
  workId: 'w1',
  incumbencyId: 'inc-2',
  stage: 'S2',
  openingCursor: 20n,
  previousRange: [7n, 25n],
  isFreshHandover: false,
  ...overrides,
})

test('WHAT[planning-017] S1 with no retirement carries no previous range', () => {
  const st = firstStateOf([openWork('w1'), openInc('w1', 'inc-1', 'S1', 0n)])
  assert.equal(surface.PlanningSurface.latestRetirementRange(st), undefined)
})

test('WHAT[planning-017] S1 retirement preserves its opening range for the next tenure', () => {
  const st = firstStateOf([
    openWork('w1'),
    openInc('w1', 'inc-1', 'S1', 7n),
    retireInc('inc-1', 'Continue', 25n),
  ])
  assert.deepEqual(surface.PlanningSurface.latestRetirementRange(st), [7n, 25n])
})

test('WHAT[planning-017] tenure assembly consumes PreviousRange into LWR_prev', () => {
  const rawMessages = [
    userText('u1', 'go'),
    assistantText('a-old', 'old work', 'u1'),
    assistantText('a-new', 'new work', 'u1'),
  ]
  let seen = null
  const result = surface.PlanningSurface.assembleTenureMessages(
    rawMessages,
    tenureFor(),
    (range) => {
      seen = range
      return 'prev record'
    },
    cursors({ 'a-old': 9n, 'a-new': 30n }),
  )
  assert.equal(seen.start, 7n)
  assert.equal(seen.end, 25n)
  const assembledIds = ids(result.messages)
  assert.ok(!assembledIds.includes('a-old'))
  assert.ok(assembledIds.includes('a-new'))
  assert.ok(assembledIds.some((id) => id.startsWith('lwr-prev-')))
  // LWR_prev is injected before the first current interaction (a-new).
  assert.deepEqual(assembledIds, ['u1', 'lwr-prev-inc-2', 'a-new'])
})

test('WHAT[planning-017] tenure reanchor event shape carries session, epochs, work and incumbency', () => {
  assert.equal(typeof prefix.applyTenureReanchor, 'function')
  assert.equal(typeof prefix.isTenureReanchored, 'function')
  assert.equal(typeof prefix.reanchoredTenures, 'function')
})

test('WHAT[planning-017] duplicate incumbencyId is rejected on second tenure reanchor', () => {
  const empty = prefix.empty
  const first = prefix.applyTenureReanchor({ previousEpoch: 0n, nextEpoch: 1n, incumbencyId: 'inc-1' }, empty)
  assert.equal(first.ok, true)
  const second = prefix.applyTenureReanchor({ previousEpoch: 1n, nextEpoch: 2n, incumbencyId: 'inc-1' }, first.value)
  assert.equal(second.ok, false)
  assert.equal(second.error, 'TenureAlreadyReanchored')
})

test('WHAT[planning-017] reanchorRequested is true on fresh handover and deduplicated via isTenureReanchored', () => {
  const rawMessages = [userText('u1', 'hi')]
  const tenure = {
    workId: 'w-27',
    incumbencyId: 'inc-1',
    stage: 'S1',
    openingCursor: 0n,
    previousRange: null,
    isFreshHandover: true,
  }
  const result = surface.PlanningSurface.assembleTenureMessages(rawMessages, tenure, () => '', {})
  assert.equal(result.reanchorRequested, true)

  const continuing = surface.PlanningSurface.assembleTenureMessages(
    rawMessages,
    { ...tenure, isFreshHandover: false },
    () => '',
    {},
  )
  assert.equal(continuing.reanchorRequested, false)
})