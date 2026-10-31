import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { mkdtempSync, readFileSync, rmSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join, resolve } = await import("node:path");
const journal = await import("../../../dist/Persistence/Journal/Surface.js");
const trace = await import("../../../dist/Context/Trace/SemanticTraceSurface.js");

const SESSION = 'ses_semantic_capture'
const withJournal = async (fn) => {
  const dir = mkdtempSync(join(tmpdir(), 'semantic-trace-'))
  const created = await journal.JournalSurface_boot(dir, 'rt_semantic_trace', 4242, '2026-01-01T00:00:00Z')
  assert.equal(created.ok, true, created.ok ? '' : JSON.stringify(created.error))
  try {
    return await fn(created.journal)
  } finally {
    journal.JournalSurface_dispose(created.journal)
    rmSync(dir, { recursive: true, force: true })
  }
}
const projection = (messages) => ({ messages })

test('WHAT[semantic-trace-010] opening capture reports idempotent evidence', async () => {
  await withJournal(async (handle) => {
    const first = await trace.captureOpening(handle, SESSION, 'Review the tree.', ['Ship it.', 'Add tests.'])
    const second = await trace.captureOpening(handle, SESSION, 'Review the tree.', ['Ship it.', 'Add tests.'])
    assert.equal(first.openingCaptured, true)
    assert.equal(second.openingCaptured, false)
    assert.deepEqual(trace.openingEvidence(trace.snapshot(handle, SESSION)).authoritativeRequirements, ['Ship it.', 'Add tests.'])
  })
})
test('WHAT[semantic-trace-010] durable capture refuses conflicting text and requirements without changing opening', async () => {
  await withJournal(async (handle) => {
    assert.equal((await trace.captureOpening(handle, SESSION, 'Original task', ['r1'])).ok, true)
    for (const [text, requirements] of [['Other task', ['r1']], ['Original task', ['r2']]]) {
      const result = await trace.captureOpening(handle, SESSION, text, requirements)
      assert.equal(result.ok, false, 'a conflicting capture must return a refusal, not an idempotent success')
      assert.match(result.error, /opening-already-captured/)
      assert.deepEqual(trace.openingEvidence(trace.snapshot(handle, SESSION)), {
        assignmentText: 'Original task', authoritativeRequirements: ['r1'], constitutiveBody: '',
      })
    }
  })
})

test('WHAT[semantic-trace-010] a refused concurrent opening does not block the next legal capture', async () => {
  await withJournal(async (handle) => {
    const session = `${SESSION}-opening-conflict`
    const assignment = 'Original task\r\nKeep source.'
    const raw = 'legal after refusal\r\n中文 😀'
    const firstOpening = trace.captureOpening(handle, session, assignment, ['r1'])
    const conflictingOpening = trace.captureOpening(handle, session, 'Other task', ['r2'])
    const nextCapture = trace.captureObservedMessages(handle, session, [{
      hostMessageId: 'after-refusal',
      message: {
        info: { id: 'after-refusal-run', role: 'assistant' },
        parts: [{ id: 'after-refusal-part', type: 'text', text: raw }],
      },
    }])
    const [first, conflict, captured] = await Promise.all([firstOpening, conflictingOpening, nextCapture])

    assert.equal(first.ok, true, first.ok ? '' : first.error)
    assert.equal(first.openingCaptured, true)
    assert.equal(conflict.ok, false)
    assert.match(conflict.error, /opening-already-captured/)
    assert.equal(captured.ok, true, captured.ok ? '' : captured.error)
    assert.equal(captured.receipt.capturedPartCount, 1)

    const snapshot = trace.snapshot(handle, session)
    assert.deepEqual(trace.openingEvidence(snapshot), {
      assignmentText: assignment, authoritativeRequirements: ['r1'], constitutiveBody: '',
    })
    const parts = trace.orderedSemanticParts(snapshot)
    assert.equal(parts.length, 1)
    assert.equal(parts[0].cursor.sequence, 1)
    assert.equal(parts[0].provenance, 'g:0/msg:after-refusal/host-part:after-refusal-part')
    const payload = await journal.JournalSurface_readPayload(handle, parts[0].textRef)
    assert.equal(payload.ok, true, payload.ok ? '' : payload.error)
    assert.equal(payload.content, raw)
  })
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const trace = await import("../../../dist/Context/Trace/SemanticTraceSurface.js");

const unwrap = (result) => {
  assert.equal(result.ok, true, result.ok ? '' : result.error)
  return result.projection
}
const part = (sequence, overrides = {}) => ({
  sequence,
  role: 'assistant',
  provenance: `g:0/msg:run-${sequence}/host-part:part-${sequence}`,
  turn: 0,
  partIndex: sequence - 1,
  kind: 'text',
  textRef: `blob-${sequence}`,
  textDigest: `digest-${sequence}`,
  providerRun: `run-${sequence}`,
  ...overrides,
})

test('WHAT[semantic-trace-010] conflicting opening is rejected', () => {
  const projection = unwrap(trace.appendOpening(trace.emptyProjection(), 'first task', []))
  const rejected = trace.appendOpening(projection, 'second task', [])
  assert.equal(rejected.ok, false)
  assert.equal(rejected.error, 'opening-already-captured')
})
}
