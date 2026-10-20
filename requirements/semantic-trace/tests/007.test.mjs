import test from 'node:test'
import { withReopenableJournal } from './support/journal.mjs'

test('WHAT[semantic-trace-007] durable materialization survives reopening with exact user and tool text', async () => {
  const assert = (await import('node:assert/strict')).default
  const trace = await import('../../../dist/Context/Trace/SemanticTraceSurface.js')
  await withReopenableJournal(async (handle, reopen) => {
    const messages = [
      { role: 'user', parts: [trace.semanticText('原话\r\n"task" <tag> /path')] },
      { role: 'assistant', parts: [trace.semanticToolCall('todowrite-for-engineer', '{"program":"await this.cat(\'a\')"}'), trace.semanticToolResult('line 1\nline 2\n')] },
    ]
    assert.equal((await trace.captureProjection(handle, 'raw-history', { messages })).ok, true)
    const before = await trace.currentProjection(handle, 'raw-history')
    assert.deepEqual(before.messages, messages)
    const next = await reopen()
    assert.deepEqual(await trace.currentProjection(next, 'raw-history'), before)
    assert.equal((await trace.captureProjection(next, 'raw-history', { messages })).capturedPartCount, 0)
  })
})

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

test('WHAT[semantic-trace-007] projection capture is idempotent and reports owner receipts', async () => {
  await withJournal(async (handle) => {
    const value = projection([
      { role: 'user', parts: [trace.semanticText('task')] },
      { role: 'assistant', parts: [trace.semanticText('work'), trace.semanticReasoning('considered')] },
    ])
    const first = await trace.captureProjection(handle, SESSION, value)
    const second = await trace.captureProjection(handle, SESSION, value)
    assert.equal(first.ok, true, first.ok ? '' : first.error)
    assert.equal(first.capturedPartCount, 3)
    assert.equal(second.capturedPartCount, 0)
    assert.equal(trace.orderedSemanticParts(trace.snapshot(handle, SESSION)).length, 3)
  })
})
test('WHAT[semantic-trace-007] materialization reads canonical durable semantics', async () => {
  await withJournal(async (handle) => {
    await trace.captureProjection(handle, SESSION, projection([
      { role: 'user', parts: [trace.semanticText('raw opening')] },
      { role: 'assistant', parts: [trace.semanticText('raw answer')] },
    ]))
    const first = await trace.currentProjection(handle, SESSION)
    assert.deepEqual(first.messages, [
      { role: 'user', parts: [{ kind: 'text', text: 'raw opening' }] },
      { role: 'assistant', parts: [{ kind: 'text', text: 'raw answer' }] },
    ])
    assert.deepEqual(await trace.currentProjectionBetween(
      handle,
      SESSION,
      trace.createRange(trace.cursor(2), trace.cursor(3)),
    ), { messages: [{ role: 'assistant', parts: [{ kind: 'text', text: 'raw answer' }] }] })
  })
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const trace = await import("../../../dist/Context/Trace/SemanticTraceSurface.js");


test('WHAT[semantic-trace-007] canonical flatten preserves semantic part and role order', () => {
  const flat = trace.flatten([
    { role: 'user', parts: [trace.semanticText('task'), trace.semanticToolCall('todowrite-for-engineer', '{}')] },
    { role: 'assistant', parts: [trace.semanticReasoning('considered'), trace.semanticText('done')] },
  ])
  assert.deepEqual(flat.map((entry) => entry.role), ['user', 'user', 'assistant', 'assistant'])
  assert.deepEqual(flat.map((entry) => entry.part.kind), ['text', 'tool-call', 'reasoning', 'text'])
})
}

test.todo('WHAT[semantic-trace-007] actual prefix, view and Casebook consumers share the same bounded durable history and exclude request presentation; GAP-101')
