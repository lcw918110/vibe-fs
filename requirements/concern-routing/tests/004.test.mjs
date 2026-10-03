import assert from 'node:assert/strict'
import test from 'node:test'
import * as concern from '../../../dist/Interaction/Concern/Surface.js'

test('WHAT[concern-routing-004] pure prepare is non-consuming and successful placement updates coverage', () => {
  let state = concern.subscribe('owner-a', 'gen-1', 'build', 'build health', concern.empty()).state
  state = concern.publish('sender', 'msg-1', 'build', 'failure found', state).state

  const preparedA = concern.prepare('owner-a', state)
  const preparedB = concern.prepare('owner-a', state)
  assert.deepEqual(preparedA, preparedB, 'preparation from identical facts is stable and non-consuming')
  assert.deepEqual(preparedA.messages, [{ id: 'build', message: 'failure found' }])

  state = concern.place('owner-a', preparedA.announcedGenerations, preparedA.deliveredMessages, state).state
  assert.deepEqual(concern.prepare('owner-a', state).messages, [])
})

const { withExecutablePlugin } = await import('../../verification-system/tests/support/plugin-fixture.mjs')
const { admit, context, user, toolBatch, transform, hints } = await import('./support/plugin.mjs')

test('WHAT[concern-routing-004] actual plugin freezes an old Pair Hint and delivers new messages only in a later occurrence', async () => {
  await withExecutablePlugin(async (hooks, directory, created, runtime) => {
    const owner = 'delivery-owner'
    const sender = 'delivery-sender'
    await admit(runtime, owner, 'engineer', hooks)
    await admit(runtime, sender, 'engineer', hooks)
    await hooks.tool.subscribe.execute({ id: 'build', concern: 'BUILD-CONCERN-MARKER' }, context(owner, 'subscription'))
    const initial = await transform(hooks, owner, [user(owner)])
    const frozen = hints(initial)
    assert.ok(frozen.length > 0)
    assert.match(JSON.stringify(frozen), /BUILD-CONCERN-MARKER/)
    await hooks.tool.publish.execute({ id: 'build', message: 'NEW-MESSAGE-MARKER' }, context(sender, 'publication'))
    const replay = await transform(hooks, owner, initial)
    assert.deepEqual(hints(replay), frozen)
    assert.doesNotMatch(JSON.stringify(replay), /NEW-MESSAGE-MARKER/)
    const next = await transform(hooks, owner, [...replay, ...toolBatch(owner, 'next')])
    assert.deepEqual(hints(next).slice(0, frozen.length), frozen)
    const newPairs = hints(next).slice(frozen.length)
    assert.match(JSON.stringify(newPairs), /NEW-MESSAGE-MARKER/)
    assert.doesNotMatch(JSON.stringify(newPairs), /BUILD-CONCERN-MARKER/)
    const final = await transform(hooks, owner, [...next, ...toolBatch(owner, 'final')])
    assert.ok(hints(final).length > hints(next).length)
    assert.doesNotMatch(JSON.stringify(hints(final).slice(hints(next).length)), /NEW-MESSAGE-MARKER|BUILD-CONCERN-MARKER/)
    assert.equal(runtime.prompts.some(prompt => (prompt.path?.id ?? prompt.sessionID) === owner), false)
    assert.equal(runtime.abortedIds.includes(owner), false)
  })
})

test('WHAT[concern-routing-004] invalid delivery rejects the entire placement including otherwise-valid announcement coverage', () => {
  let state = concern.subscribe('owner', 'generation', 'address', 'health', concern.empty()).state
  state = concern.publish('sender', 'message', 'address', 'evidence', state).state
  const pending = concern.prepare('owner', state)
  for (const [recipient, announcements, messages] of [
    ['owner', pending.announcedGenerations, ['message', 'missing']],
    ['other', pending.announcedGenerations, pending.deliveredMessages],
    ['owner', ['unknown-generation'], pending.deliveredMessages],
  ]) {
    const rejected = concern.place(recipient, announcements, messages, state)
    assert.equal(rejected.ok, false)
    assert.deepEqual(concern.prepare('owner', rejected.state), pending)
  }
})

test.todo('WHAT[concern-routing-004] failed or abandoned real placement leaves both coverages pending across journal reopen and crash (GAP-155)')
