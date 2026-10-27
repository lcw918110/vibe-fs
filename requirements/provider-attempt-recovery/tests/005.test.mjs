import assert from 'node:assert/strict'
import test from 'node:test'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import * as journal from '../../../dist/Persistence/Journal/Surface.js'
import * as owner from '../../../dist/Participant/Provider/Attempt/Fallback/ProviderFailureSurface.js'
import { current, run } from './support/retry.mjs'

const { budget, providerFailureProjection: projection } = owner

test('WHAT[provider-attempt-recovery-005] finite budget boundaries stop automatic permission at the configured count', () => {
  assert.equal(budget.defaultBudget, 10)
  for (const limit of [1, 3, budget.defaultBudget]) {
    assert.equal(budget.verdict(limit, { failures: limit - 1 }), 'MayRetry')
    assert.equal(budget.verdict(limit, { failures: limit }), 'Exhausted')
    assert.equal(budget.verdict(limit, { failures: limit + 1 }), 'Exhausted')
  }
  assert.equal(budget.verdict(0, budget.initial), 'Exhausted')
})

test('WHAT[provider-attempt-recovery-005] durable exhaustion forbids retry even if a caller asks with a larger limit', () => {
  const exhausted = projection.applyExhausted(current())
  assert.equal(projection.read(exhausted).exhausted, true)
  assert.equal(projection.mayRetry(budget.defaultBudget, exhausted), false)
  assert.equal(projection.mayRetry(budget.defaultBudget + 1, exhausted), false)
})

test('WHAT[provider-attempt-recovery-005] real retry engine calls neither admission nor redispatch after exhaustion', async () => {
  const attempt = run('ProviderTransient', projection.applyExhausted(current()))
  assert.equal(await attempt.completed, 'Terminal')
  assert.deepEqual(attempt.events, [])
})

test('WHAT[provider-attempt-recovery-005] ledger records exhaustion exactly at its current finite limit and preserves it on reopen', async () => {
  const directory = mkdtempSync(join(tmpdir(), 'wxs-retry-limit-'))
  let opened = await journal.JournalSurface_bootWithWriterId(directory, 'writer-limit', 'runtime-limit', 1, '2026-01-01T00:00:00Z')
  assert.equal(opened.ok, true)
  try {
    assert.equal((await owner.acceptHumanRoot(opened.journal, 'session-limit', 'physical-root', 'engineer')).ok, true)
    for (let count = 1; count <= budget.defaultBudget; count++) {
      const recorded = await owner.recordConfirmedFailure(opened.journal, budget.defaultBudget, 'session-limit', `run-${count}`, 'provider failure')
      assert.deepEqual(recorded, { ok: true, outcome: count === budget.defaultBudget ? 'RetryExhausted' : 'RetryAuthorized' })
    }
    const before = owner.snapshot(opened.journal, 'session-limit')
    assert.equal(before.failures, budget.defaultBudget)
    assert.equal(before.exhausted, true)
    journal.JournalSurface_dispose(opened.journal)
    opened = null
    opened = await journal.JournalSurface_bootWithWriterId(directory, 'writer-reopened-limit', 'runtime-limit-reopened', 2, '2026-01-01T00:00:01Z')
    assert.equal(opened.ok, true)
    assert.deepEqual(owner.snapshot(opened.journal, 'session-limit'), before)
    assert.deepEqual(await owner.recordConfirmedFailure(opened.journal, budget.defaultBudget, 'session-limit', 'later-run', 'provider failure'), { ok: true, outcome: 'RetryExhausted' })
    assert.deepEqual(owner.snapshot(opened.journal, 'session-limit'), before)
  } finally {
    if (opened?.ok) journal.JournalSurface_dispose(opened.journal)
    rmSync(directory, { recursive: true, force: true })
  }
})

test.todo('WHAT[provider-attempt-recovery-005] full recovery workflow reaches exhaustion without an extra physical Host send (GAP-139)')
