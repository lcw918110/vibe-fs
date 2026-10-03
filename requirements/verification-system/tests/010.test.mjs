import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import test from 'node:test'
import { attachEventCeilings, eventCeilingSetupProblems, isCountedSseEvent, normalizeEventCeilings } from './e2e/support/event-ceiling.js'
import { compileScenario } from './e2e/support/scenario-schema.js'

test('WHAT[verification-system-010] frozen event budgets reject invalid limits and count only substantive SSE events', () => {
  assert.equal(isCountedSseEvent({ type: 'server.heartbeat' }), false)
  assert.equal(isCountedSseEvent({ type: 'message.updated' }), true)
  assert.equal(isCountedSseEvent({ type: '' }), false)
  assert.equal(isCountedSseEvent({}), false)
  assert.deepEqual(normalizeEventCeilings({}), {})
  assert.deepEqual(normalizeEventCeilings({ maxJournalEvents: 12, maxSseEvents: 34 }), { maxJournalEvents: 12, maxSseEvents: 34 })
  assert.throws(() => normalizeEventCeilings({ maxJournalEvents: 0 }), /positive integer/)
  assert.throws(() => normalizeEventCeilings({ maxSseEvents: 1.2 }), /positive integer/)
  assert.deepEqual(eventCeilingSetupProblems(undefined), [])
  assert.ok(eventCeilingSetupProblems({ maxJournalEvents: 0 })[0].includes('maxJournalEvents'))
  assert.ok(eventCeilingSetupProblems({ maxSseEvents: -3 })[0].includes('maxSseEvents'))
})

test('WHAT[verification-system-010] exceeding an event budget fails instead of extending its limit', () => {
  let listener
  let breached = null
  const scenario = {
    host: { workDir: '/unused-sse-only' },
    events: {
      allEvents: [{ type: 'message.updated' }, { type: 'server.heartbeat' }, { type: 'sync' }],
      onEvent(callback) { listener = callback; return () => {} },
      dump: () => '',
    },
    watchdog: { stop() {} },
  }
  attachEventCeilings(scenario, { maxSseEvents: 2 }, {
    onBreach(detail) { breached = detail; throw new Error('budget exceeded') },
  })
  assert.equal(breached, null)
  listener({ type: 'server.heartbeat' })
  assert.equal(breached, null)
  assert.throws(() => listener({ type: 'session.idle' }), /budget exceeded/)
  assert.deepEqual({ kind: breached.kind, observed: breached.observed, limit: breached.limit, sseEvents: breached.sseEvents },
    { kind: 'maxSseEvents', observed: 3, limit: 2, sseEvents: 3 })
})

test('WHAT[verification-system-010] the Long Stroke retains its existing acceptance event ceilings', () => {
  const result = compileScenario(readFileSync(new URL('./e2e/scenarios/long-stroke.toml', import.meta.url), 'utf8'), { name: 'long-stroke.toml' })
  assert.equal(result.ok, true, result.ok ? '' : result.problems.join('\n'))
  assert.equal(result.scenario.setup.maxJournalEvents, 550)
  assert.equal(result.scenario.setup.maxSseEvents, 2500)
})
