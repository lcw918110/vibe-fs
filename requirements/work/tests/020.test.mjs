import assert from 'node:assert/strict'
import test from 'node:test'
import * as surface from '../../../dist/Mission/Planning/Surface.js'
import { isAllowed } from '../../../dist/Participant/Persona/OfficeCapabilitySurface.js'
import {
  assistantText,
  cursors,
  ids,
  msgId,
  msgRole,
  msgText,
  syntheticPredecessor,
  userText,
} from './support/plan-messages.mjs'

// WHAT[work-020] fixes the closed input set a Plan runner receives:
// U (verbatim, original order), current-tenure interactions, and the draft
// (the only writable surface, reached through todowrite-for-plan only); no
// synthetic predecessor message is ever injected.

const assemble = (rawMessages, tenure, messageCursors) =>
  surface.PlanningSurface.assembleTenureMessages(rawMessages, tenure, messageCursors)

const mark = (message, fields) => Object.assign(message, { toolCallId: null, resultFor: null, tenureId: null, ...fields })

const barrierTenure = (extra = {}) => ({
  workId: 'w-001-inputs',
  incumbencyId: 'next',
  stage: '执行',
  openingCursor: 0n,
  isFreshHandover: false,
  barrierToolCallId: 'review-1',
  successorTenureId: 'next',
  ...extra,
})

test('WHAT[work-020] a current-tenure user message keeps its original bytes', () => {
  const current = mark(userText('u-2', '补充约束: no cache invalidation'), { tenureId: 'next' })
  const rawMessages = [
    mark(userText('u-1', '设计缓存架构\n第二行'), { tenureId: 'old' }),
    mark(assistantText('call', 'review', 'u-1'), { toolCallId: 'review-1', tenureId: 'old' }),
    mark(assistantText('result', 'recorded'), { resultFor: 'review-1', tenureId: 'old' }),
    current,
    mark(assistantText('a-new', 'current tenure work', 'u-2'), { tenureId: 'next' }),
  ]
  const result = assemble(rawMessages, barrierTenure(), {})
  assert.equal(result.messages[0], current)
  assert.equal(msgText(result.messages[0]), '补充约束: no cache invalidation')
  assert.equal(ids(result.messages).includes('u-1'), false)
})

test('WHAT[work-020] neither an open tenure nor a successor view injects a synthetic-prev-* message', () => {
  const firstTenure = {
    workId: 'w-001-inputs',
    incumbencyId: 'inc-1',
    stage: '事实与取舍',
    openingCursor: 0n,
    isFreshHandover: true,
  }
  const laterMessages = [
    mark(userText('u-1', 'go'), { tenureId: 'old' }),
    mark(assistantText('call', 'review'), { toolCallId: 'review-1', tenureId: 'old' }),
    mark(assistantText('result', 'recorded'), { resultFor: 'review-1', tenureId: 'old' }),
    mark(assistantText('a-new', 'current work', 'u-1'), { tenureId: 'next' }),
  ]
  const firstResult = assemble([userText('u-1', 'go')], firstTenure, {})
  const laterResult = assemble(laterMessages, barrierTenure({ isFreshHandover: true }), {})
  assert.ok(!firstResult.messages.some((m) => msgId(m).startsWith('synthetic-prev-') || m.info.synthetic === true))
  assert.ok(!laterResult.messages.some((m) => msgId(m).startsWith('synthetic-prev-') || m.info.synthetic === true))
  assert.deepEqual(ids(firstResult.messages), ['u-1'])
  assert.deepEqual(ids(laterResult.messages), ['a-new'])
})

test('WHAT[work-020] assembled messages come only from U and current-tenure interactions', () => {
  const rawMessages = [
    userText('u-1', 'go'),
    assistantText('a-1', 'current work', 'u-1'),
  ]
  const tenure = {
    workId: 'w-001-inputs',
    incumbencyId: 'inc-2',
    stage: 'S2',
    openingCursor: 20n,
    isFreshHandover: false,
  }
  const result = assemble(
    rawMessages,
    tenure,
    cursors({ 'a-1': 30n }),
  )
  const inputIds = new Set(rawMessages.map(msgId))
  for (const m of result.messages) {
    assert.ok(
      inputIds.has(msgId(m)) && !msgId(m).startsWith('synthetic-prev-'),
      `assembled message id ${msgId(m)} must originate from the physical input messages`,
    )
  }
  // The draft is reachable only through the todowrite-for-plan tool surface; the
  // assembled request never carries its content as an injected message.
  assert.equal(result.messages.length, rawMessages.length)
})

test('WHAT[work-020] the plan runner holds no write capability beyond the draft', () => {
  for (const perm of ['Write', 'Edit', 'Move', 'Remove', 'Exec', 'Pty']) {
    assert.equal(isAllowed('plan', perm), false, `plan must be denied ${perm}`)
  }
  // The draft itself is reached through the todowrite-for-plan programming surface.
  assert.equal(isAllowed('plan', 'JsPlan'), true)
})

test('WHAT[work-020] a negative openingCursor rejects assembly fail-closed', () => {
  const rawMessages = [userText('u-1', 'go')]
  const tenure = {
    workId: 'w-001-inputs',
    incumbencyId: 'inc-2',
    stage: 'S2',
    openingCursor: -1n,
    isFreshHandover: false,
  }
  assert.throws(
    () => assemble(rawMessages, tenure, {}),
    /nonnegative bigint|cannot be negative/i,
  )
})

test('WHAT[work-020] synthetic rows are dropped by the barrier and never re-injected', () => {
  const rawMessages = [
    mark(userText('u-1', 'go'), { tenureId: 'old' }),
    mark(assistantText('call', 'review'), { toolCallId: 'review-1', tenureId: 'old' }),
    mark(assistantText('result', 'recorded'), { resultFor: 'review-1', tenureId: 'old' }),
    mark(syntheticPredecessor('inc-old', 'w-001', 'old synthesized previous row'), { tenureId: 'next' }),
    mark(assistantText('a-1', 'current work', 'u-1'), { tenureId: 'next' }),
  ]
  const result = assemble(rawMessages, barrierTenure(), {})
  assert.equal(result.messages.filter((message) => msgId(message).startsWith('synthetic-prev-')).length, 0)
  assert.deepEqual(ids(result.messages), ['a-1'])
})
