import assert from 'node:assert/strict'
import test from 'node:test'
import * as attention from '../../../dist/Interaction/Attention/Surface.js'
import { recordingPort, context } from './support/attention-port.mjs'
import * as tools from '../../../dist/OpenCode/Tools/AttentionToolSurface.js'

const pendingOf = (fixture, session) => attention.pending(session, fixture.state)

test('WHAT[attention-regulation-005] manager retirement consumes the pending list and replay cannot resurrect it', async () => {
  const fixture = recordingPort()
  await tools.execute(fixture.tools, 'defer', { new_work: 'first' }, context('session-manager', 'call-m1'))
  await tools.execute(fixture.tools, 'defer', { new_work: 'second' }, context('session-manager', 'call-m2'))
  // The first suicide returns the still-unconsumed list for review.
  assert.deepEqual(pendingOf(fixture, 'session-manager'), [
    { occurrence: 'call-m1', text: 'first' },
    { occurrence: 'call-m2', text: 'second' },
  ])
  // The completed retirement consumes the whole list and leaves receipts.
  fixture.state = attention.consume('session-manager', ['call-m1', 'call-m2'], fixture.state)
  assert.deepEqual(pendingOf(fixture, 'session-manager'), [])
  // Replaying the same records (a journal rebuild) cannot resurrect them.
  fixture.state = attention.record('session-manager', 'call-m1', 'first', fixture.state)
  fixture.state = attention.record('session-manager', 'call-m2', 'second', fixture.state)
  assert.deepEqual(pendingOf(fixture, 'session-manager'), [])
  // Consuming the same ids again is idempotent.
  fixture.state = attention.consume('session-manager', ['call-m1', 'call-m2'], fixture.state)
  assert.deepEqual(pendingOf(fixture, 'session-manager'), [])
})

test('WHAT[attention-regulation-005] engineer natural terminal consumes its pending work exactly once', async () => {
  const fixture = recordingPort()
  await tools.execute(fixture.tools, 'defer', { new_work: 'follow up' }, context('session-engineer', 'call-e1'))
  const work = pendingOf(fixture, 'session-engineer').map((item) => item.occurrence)
  fixture.state = attention.consume('session-engineer', work, fixture.state)
  assert.deepEqual(pendingOf(fixture, 'session-engineer'), [])
  // A second terminal observation finds nothing pending: no second prompt.
  assert.deepEqual(pendingOf(fixture, 'session-engineer'), [])
  // Replaying the record after the receipt stays suppressed.
  fixture.state = attention.record('session-engineer', 'call-e1', 'follow up', fixture.state)
  assert.deepEqual(pendingOf(fixture, 'session-engineer'), [])
})

test('WHAT[attention-regulation-005] orchestrator finish follows the same consumption contract', async () => {
  const fixture = recordingPort()
  await tools.execute(fixture.tools, 'defer', { new_work: 'hand over' }, context('session-orchestrator', 'call-o1'))
  assert.equal(pendingOf(fixture, 'session-orchestrator').length, 1)
  fixture.state = attention.consume('session-orchestrator', ['call-o1'], fixture.state)
  assert.deepEqual(pendingOf(fixture, 'session-orchestrator'), [])
  fixture.state = attention.record('session-orchestrator', 'call-o1', 'hand over', fixture.state)
  assert.deepEqual(pendingOf(fixture, 'session-orchestrator'), [])
})

test('WHAT[attention-regulation-005] a receipt recorded before its work stays order-independent across replay', () => {
  let state = attention.empty()
  state = attention.consume('session-a', ['late'], state)
  state = attention.record('session-a', 'late', 'arrived after the receipt', state)
  assert.deepEqual(attention.pending('session-a', state), [])
})

test('WHAT[attention-regulation-005] closing a life consumes its remaining work without leaking into a reused session', async () => {
  const fixture = recordingPort()
  await tools.execute(fixture.tools, 'defer', { new_work: 'old life' }, context('session-reuse', 'call-r1'))
  fixture.state = attention.closeLife('session-reuse', fixture.state)
  assert.deepEqual(pendingOf(fixture, 'session-reuse'), [])
  // Replaying the old record cannot resurrect it into the reused session.
  fixture.state = attention.record('session-reuse', 'call-r1', 'old life', fixture.state)
  assert.deepEqual(pendingOf(fixture, 'session-reuse'), [])
  // A fresh life under the same session records new work normally.
  fixture.state = attention.record('session-reuse', 'call-r2', 'new life work', fixture.state)
  assert.deepEqual(pendingOf(fixture, 'session-reuse'), [{ occurrence: 'call-r2', text: 'new life work' }])
})
