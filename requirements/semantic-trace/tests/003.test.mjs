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

test('WHAT[semantic-trace-003] concurrent same-session captures serialize before allocating durable cursors', async () => {
  await withJournal(async (handle) => {
    const capture = (hostMessageId, text) => trace.captureObservedMessages(handle, SESSION, [
      {
        hostMessageId,
        message: {
          info: { id: `${hostMessageId}-run`, role: 'assistant' },
          parts: [{ id: `${hostMessageId}-part`, type: 'text', text }],
        },
      },
    ])

    const [first, second] = await Promise.all([
      capture('concurrent-a', 'first concurrent semantic part'),
      capture('concurrent-b', 'second concurrent semantic part'),
    ])

    assert.equal(first.ok, true, first.ok ? '' : first.error)
    assert.equal(second.ok, true, second.ok ? '' : second.error)
    assert.deepEqual(first.receipt.currentHead, second.receipt.previousHead)
    const parts = trace.orderedSemanticParts(trace.snapshot(handle, SESSION))
    assert.equal(parts.length, 2)
    assert.deepEqual(parts.map((part) => part.cursor.sequence), [1, 2])
    assert.deepEqual(parts.map((part) => part.provenance), [
      'g:0/msg:concurrent-a/host-part:concurrent-a-part',
      'g:0/msg:concurrent-b/host-part:concurrent-b-part',
    ])
    const expectedTexts = ['first concurrent semantic part', 'second concurrent semantic part']
    for (let index = 0; index < parts.length; index += 1) {
      const payload = await journal.JournalSurface_readPayload(handle, parts[index].textRef)
      assert.equal(payload.ok, true, payload.ok ? '' : payload.error)
      assert.equal(payload.content, expectedTexts[index])
    }
  })
})

test('WHAT[semantic-trace-003] stable tool-part identity remains idempotent without a provider-run identity', async () => {
  await withJournal(async (handle) => {
    const observation = (hostMessageId) => ({
      hostMessageId,
      message: {
        info: { id: hostMessageId, role: 'tool' },
        parts: [{
          id: 'same-host-tool-part', type: 'tool', tool: 'todowrite-for-engineer', callID: 'same-tool-call',
          state: { status: 'completed', input: { program: "class Js extends JsProgram { async run() { return await this.cat('sample'); } }" }, output: 'stable tool output' },
        }],
      },
    })
    const first = await trace.captureObservedMessages(handle, SESSION, [observation('first-host-message')])
    assert.equal(first.ok, true, first.ok ? '' : first.error)
    assert.equal(first.receipt.capturedPartCount, 1)
    const before = trace.orderedSemanticParts(trace.snapshot(handle, SESSION))
    assert.equal(before[0].providerRun, null)

    const replay = await trace.captureObservedMessages(handle, SESSION, [observation('reanchored-host-message')])
    assert.equal(replay.ok, true, replay.ok ? '' : replay.error)
    assert.equal(replay.receipt.capturedPartCount, 0)
    assert.deepEqual(trace.orderedSemanticParts(trace.snapshot(handle, SESSION)), before)
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

test('WHAT[semantic-trace-003] duplicate and retreating cursors are rejected', () => {
  const projection = unwrap(trace.appendPart(trace.emptyProjection(), part(5)))
  assert.equal(trace.appendPart(projection, part(5)).ok, false)
  assert.equal(trace.appendPart(projection, part(3)).ok, false)
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const trace = await import("../../../dist/Context/Trace/SemanticTraceSurface.js");


test('WHAT[semantic-trace-003] owner cursor operations are monotonic', () => {
  const origin = trace.originCursor
  const second = trace.next(origin)
  const third = trace.next(second)
  assert.deepEqual([origin.sequence, second.sequence, third.sequence], [0, 1, 2])
  assert.equal(trace.isAfter(second, origin), true)
  assert.equal(trace.isAtOrAfter(second, second), true)
  assert.equal(trace.isBefore(origin, second), true)
})
}
