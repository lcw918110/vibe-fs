import assert from 'node:assert/strict'
import test from 'node:test'
import * as surface from '../../../dist/Mission/Planning/Surface.js'

test('WHAT[planning-017] initial tenure (S1) assembles U + current messages with no LWR_prev and requests reanchor on first turn', () => {
  const rawMessages = [
    { id: 'u1', role: 'user', content: 'Initial user prompt' },
    { id: 'u2', role: 'user', content: 'Second user instruction' },
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
  const firstTurn = surface.PlanningSurface.assembleTenureMessages(rawMessages, tenure, matPrev)
  assert.equal(firstTurn.reanchorRequested, true)
  assert.equal(firstTurn.messages.length, 2)
  assert.equal(firstTurn.messages[0].id, 'u1')
  assert.equal(firstTurn.messages[1].id, 'u2')

  // 2. Subsequent turn: current tenure assistant/tool messages exist -> reanchorRequested = false
  const subsequentMessages = [
    ...rawMessages,
    { id: 'a1', role: 'assistant', content: 'working on S1', cursor: 5n },
    { id: 't1', role: 'tool', content: 'tool result', cursor: 6n },
  ]
  const tenureContinuing = { ...tenure, isFreshHandover: false }
  const secondTurn = surface.PlanningSurface.assembleTenureMessages(subsequentMessages, tenureContinuing, matPrev)
  assert.equal(secondTurn.reanchorRequested, false)
  assert.equal(secondTurn.messages.length, 4)
  assert.equal(secondTurn.messages[2].id, 'a1')
  assert.equal(secondTurn.messages[3].id, 't1')
})

test('WHAT[planning-017] non-initial tenure strips prior assistant/tool messages and injects LWR_prev after U', () => {
  const rawMessages = [
    { id: 'u1', role: 'user', content: 'Initial user prompt' },
    { id: 'a-old', role: 'assistant', content: 'S1 assistant work', cursor: 5n },
    { id: 't-old', role: 'tool', content: 'S1 tool work', cursor: 8n },
    { id: 'u2', role: 'user', content: 'Feedback from user' },
    { id: 'a-current', role: 'assistant', content: 'S2 assistant fresh', cursor: 25n },
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

  const result = surface.PlanningSurface.assembleTenureMessages(rawMessages, tenureS2, matPrev)

  // Verify previous range was passed to materialize
  assert.ok(matRangeCalledWith != null)
  assert.equal(matRangeCalledWith.start, 0n)
  assert.equal(matRangeCalledWith.end, 15n)

  // Verify assembled sequence: U1, U2, LWR_prev, current assistant
  // a-old and t-old must be stripped away
  assert.equal(result.messages.length, 4)
  assert.equal(result.messages[0].id, 'u1')
  assert.equal(result.messages[1].id, 'u2')

  // The 3rd message is the synthesized LWR_prev
  const lwrMsg = result.messages[2]
  assert.equal(lwrMsg.id, 'lwr-prev-inc-2')
  assert.equal(lwrMsg.content, '# Previous Incumbency Work Record\nCompleted S1 diagnosis.')

  // The 4th message is the current tenure assistant message
  assert.equal(result.messages[3].id, 'a-current')

  // Prior assistant/tool messages must be completely gone
  const ids = result.messages.map((m) => m.id)
  assert.ok(!ids.includes('a-old'), 'prior assistant message must be stripped')
  assert.ok(!ids.includes('t-old'), 'prior tool message must be stripped')
})

test('WHAT[planning-017] user messages U preserve original bytes and order verbatim without modification', () => {
  const originalUser1 = { id: 'u1', role: 'user', content: 'Exact bytes line 1\nLine 2\tTabs and spaces   ' }
  const originalUser2 = { id: 'u2', role: 'user', content: 'Another user prompt with emojis: 💡🚀' }

  const tenure = {
    workId: 'work-1',
    incumbencyId: 'inc-1',
    stage: 'S1',
    openingCursor: 0n,
    previousRange: null,
    isFreshHandover: true,
  }

  const result = surface.PlanningSurface.assembleTenureMessages([originalUser1, originalUser2], tenure, () => '')

  assert.equal(result.messages.length, 2)
  // Strict identity and byte-for-byte content equality
  assert.equal(result.messages[0].content, originalUser1.content)
  assert.equal(result.messages[1].content, originalUser2.content)
  assert.deepEqual(result.messages[0], originalUser1)
  assert.deepEqual(result.messages[1], originalUser2)
})
