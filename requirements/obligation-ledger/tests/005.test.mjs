import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { readFileSync, readdirSync, renameSync, mkdirSync, mkdtempSync: mkStash, rmSync: rmStash } = await import("node:fs");
const osModule = await import("node:os");
const { join } = await import("node:path");
const journalSurface = await import("../../../dist/Persistence/Journal/Surface.js");
const { acceptAuthorityRoot, withExecutablePlugin } = await import("../../verification-system/tests/support/plugin-fixture.mjs");
const { integrationTest } = await import("../../verification-system/tests/support/tier-gate.mjs");
const { countFactCase } = await import("../../verification-system/tests/e2e/support/journal-observer.js");

const countCheckpointFacts = (directory) => {
  const eventsDir = join(directory, '.Herdchestra', 'wanxiangshu', 'events')
  let count = 0
  for (const file of readdirSync(eventsDir).filter((file) => file.endsWith('.ndjson'))) {
    const content = readFileSync(join(eventsDir, file), 'utf8')
    assert.ok(content === '' || content.endsWith('\n'), 'durable facts must be complete NDJSON lines')
    const events = content.split('\n').filter(Boolean).map((line) => JSON.parse(line))
    count += countFactCase(events, 'ViewCheckpointCommitted')
  }
  return count
}

const terminalEvent = (sessionID, callID, status) => ({
  event: {
    type: 'message.part.updated',
    properties: {
      sessionID,
      part: {
        type: 'tool',
        tool: 'todowrite-for-engineer',
        callID,
        state: {
          status,
          input: {
            program: 'class Js extends JsProgram { async run() { return {}; } }',
          },
        },
      },
    },
  },
})

const todoCall = (hooks, sessionID, callID) => {
  const output = {
    args: {
      program: 'class Js extends JsProgram { async run() { return {}; } }',
    },
  }
  return hooks['tool.execute.before']({ tool: 'todowrite-for-engineer', sessionID, callID }, output).then(() => output)
}

integrationTest('WHAT[obligation-ledger-005] no todo or checkpoint fact is emitted in before, after, or non-terminal events', async () => {
  await withExecutablePlugin(async (hooks, directory, _createdIds, runtime) => {
    const sessionID = 'ol005-pending'
    const callID = 'ol005-call-pending'
    await acceptAuthorityRoot(runtime, sessionID, 'engineer')

    await todoCall(hooks, sessionID, callID)
    assert.equal(journalSurface.JournalSurface_snapshot(runtime.journal).viewCheckpoints, undefined, 'before admission commits no checkpoint')

    await hooks['tool.execute.after'](
      { tool: 'todowrite-for-engineer', sessionID, callID, args: { program: 'class Js extends JsProgram { async run() { return {}; } }' } },
      { title: 'todowrite-for-engineer', output: 'ok', metadata: {} },
    )
    assert.equal(journalSurface.JournalSurface_snapshot(runtime.journal).viewCheckpoints, undefined, 'after commits no checkpoint')

    await hooks.event(terminalEvent(sessionID, callID, 'running'))
    assert.equal(journalSurface.JournalSurface_snapshot(runtime.journal).viewCheckpoints, undefined, 'a running part update commits no checkpoint')
    assert.equal(countCheckpointFacts(directory), 0, 'no durable todo/checkpoint fact is appended')
  })
})

integrationTest('WHAT[obligation-ledger-005] repeated and concurrent completed or error terminal events emit no durable todo/checkpoint facts', async () => {
  await withExecutablePlugin(async (hooks, directory, _createdIds, runtime) => {
    const sessionID = 'ol005-terminal-idempotent'
    const callID = 'ol005-call-complete'
    await acceptAuthorityRoot(runtime, sessionID, 'engineer')
    await todoCall(hooks, sessionID, callID)

    await Promise.all([
      hooks.event(terminalEvent(sessionID, callID, 'completed')),
      hooks.event(terminalEvent(sessionID, callID, 'completed')),
    ])
    await hooks.event(terminalEvent(sessionID, callID, 'completed'))
    await hooks.event(terminalEvent(sessionID, 'ol005-call-error', 'error'))
    await hooks.event(terminalEvent(sessionID, 'ol005-call-error', 'error'))

    const snapshot = journalSurface.JournalSurface_snapshot(runtime.journal)
    assert.equal(snapshot.viewCheckpoints, undefined, 'no checkpoint projection is created')
    assert.equal(countCheckpointFacts(directory), 0, 'no ViewCheckpointCommitted fact is appended on repeated or error terminals')
  })
})

const withBlockedEvents = async (directory, action) => {
  const eventsDir = join(directory, '.Herdchestra', 'wanxiangshu', 'events')
  const stash = mkStash(join(osModule.tmpdir(), 'wxs-005-stash-'))
  const writers = readdirSync(eventsDir).filter(name => name.endsWith('.ndjson'))
  const stashed = writers.map(name => ({ live: join(eventsDir, name), held: join(stash, name) }))
  for (const writer of stashed) renameSync(writer.live, writer.held)
  for (const writer of stashed) mkdirSync(writer.live)
  try {
    return await action()
  } finally {
    for (const writer of stashed) rmStash(writer.live, { recursive: true, force: true })
    for (const writer of stashed) renameSync(writer.held, writer.live)
    rmStash(stash, { recursive: true, force: true })
  }
}

integrationTest('WHAT[obligation-ledger-005] durable append failure is never reported as committed and poisons the writer against silent retry', async () => {
  await withExecutablePlugin(async (_hooks, directory, _createdIds, runtime) => {
    const sessionID = 'ol005-append-failure'
    await acceptAuthorityRoot(runtime, sessionID, 'engineer')

    await withBlockedEvents(directory, async () => {
      const blockedAppend = await journalSurface.JournalSurface_appendAgent(
        runtime.journal,
        { kind: 'Session', session: sessionID },
        null,
        {
          family: 'Companion',
          case: 'TerminalOutputCaptured',
          payload: { SessionId: sessionID, TextRef: 'blob-ol005', TextDigest: 'digest-ol005', ProviderRun: 'run-ol005' },
        },
      )
      assert.equal(blockedAppend.ok, false, 'blocked durable append must not report committed')
      // The history read fails in Preparation, before any byte is written, so the
      // outcome is a known non-attempt that poisons the writer — not an unknown
      // physical result. Lying "unknown" here would understate what is known.
      assert.match(String(blockedAppend.error), /WriterUnavailable/i, 'blocked append reports a non-attempt outcome')
      assert.match(String(blockedAppend.error), /WriterPoisoned/i, 'the blocked writer is poisoned')
    })

    const afterPoison = await journalSurface.JournalSurface_appendAgent(
      runtime.journal,
      { kind: 'Session', session: sessionID },
      null,
      {
        family: 'Companion',
        case: 'TerminalOutputCaptured',
        payload: { SessionId: sessionID, TextRef: 'blob-ol005-2', TextDigest: 'digest-ol005-2', ProviderRun: 'run-ol005-2' },
      },
    )
    assert.equal(afterPoison.ok, false, 'poisoned writer must refuse subsequent append')
    assert.match(String(afterPoison.error), /writer poisoned|WriterUnavailable/i, 'poisoned writer reports NotAttempted / writer poisoned')
  })
})

}
