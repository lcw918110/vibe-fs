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

test('WHAT[semantic-trace-001] terminal capture returns explicit completion evidence', async () => {
  await withJournal(async (handle) => {
    const first = await trace.captureTerminalText(handle, SESSION, 'completed', 'terminal-run')
    const second = await trace.captureTerminalText(handle, SESSION, 'completed', 'terminal-run')
    assert.equal(first.terminalCaptured, true)
    assert.equal(second.terminalCaptured, false)
    assert.equal(trace.latestTerminalEvidence(trace.snapshot(handle, SESSION)).providerRun, 'terminal-run')
  })
})
}
