import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { readFileSync, readdirSync } = await import("node:fs");
const { join } = await import("node:path");
const journalSurface = await import("../../../dist/Persistence/Journal/Surface.js");
const { acceptAuthorityRoot, withExecutablePlugin } = await import("../../verification-system/tests/support/plugin-fixture.mjs");
const { integrationTest } = await import("../../verification-system/tests/support/tier-gate.mjs");
const { countFactCase } = await import("../../verification-system/tests/e2e/support/journal-observer.js");

// Count the durable TodoCheckpointCommitted facts in the plugin workspace's
// event log — the projection deduping alone cannot prove append idempotence.
const countCheckpointFacts = (directory) => {
  const eventsDir = join(directory, '.git', 'wanxiangshu', 'events')
  let count = 0
  for (const file of readdirSync(eventsDir).filter((file) => file.endsWith('.ndjson'))) {
    const content = readFileSync(join(eventsDir, file), 'utf8')
    assert.ok(content === '' || content.endsWith('\n'), 'durable facts must be complete NDJSON lines')
    const events = content.split('\n').filter(Boolean).map((line) => JSON.parse(line))
    count += countFactCase(events, 'TodoCheckpointCommitted')
  }
  return count
}

const terminalEvent = (sessionID, callID, status) => ({
  event: {
    type: 'message.part.updated',
    properties: {
      sessionID,
      part: { type: 'tool', tool: 'todowrite', callID, state: { status } },
    },
  },
})

const todoCall = (hooks, sessionID, callID) => {
  const output = { args: { todos: [{ content: 'native todo work', status: 'in_progress', priority: 'high' }] } }
  return hooks['tool.execute.before']({ tool: 'todowrite', sessionID, callID }, output).then(() => output)
}

integrationTest('WHAT[obligation-ledger-005] no checkpoint before the exact completed terminal evidence', async () => {
  await withExecutablePlugin(async (hooks, _directory, _createdIds, runtime) => {
    const sessionID = 'ol005-pending'
    const callID = 'ol005-call-pending'
    await acceptAuthorityRoot(runtime, sessionID, 'engineer')

    // before: admission alone commits nothing.
    await todoCall(hooks, sessionID, callID)
    assert.deepEqual(journalSurface.JournalSurface_snapshot(runtime.journal).todoCheckpoints, [], 'before admission commits no checkpoint')

    // after: the Host tool part is still running, so no checkpoint either.
    await hooks['tool.execute.after'](
      { tool: 'todowrite', sessionID, callID, args: { todos: [] } },
      { title: 'todowrite', output: 'Todos updated', metadata: {} },
    )
    assert.deepEqual(journalSurface.JournalSurface_snapshot(runtime.journal).todoCheckpoints, [], 'after with the part still running commits no checkpoint')

    // A non-terminal part update (running status) is not terminal evidence.
    await hooks.event(terminalEvent(sessionID, callID, 'running'))
    assert.deepEqual(journalSurface.JournalSurface_snapshot(runtime.journal).todoCheckpoints, [], 'a running part update commits no checkpoint')

    // A part update for a different tool is not terminal evidence either.
    await hooks.event({
      event: {
        type: 'message.part.updated',
        properties: { sessionID, part: { type: 'tool', tool: 'read', callID, state: { status: 'completed' } } },
      },
    })
    assert.deepEqual(journalSurface.JournalSurface_snapshot(runtime.journal).todoCheckpoints, [], 'a completed part for another tool commits no checkpoint')
  })
})

integrationTest('WHAT[obligation-ledger-005] the exact completed terminal commits exactly one durable checkpoint', async () => {
  await withExecutablePlugin(async (hooks, directory, _createdIds, runtime) => {
    const sessionID = 'ol005-TodoCheckpointCommitted-complete'
    const callID = 'ol005-call-complete'
    await acceptAuthorityRoot(runtime, sessionID, 'engineer')
    await todoCall(hooks, sessionID, callID)
    assert.equal(countCheckpointFacts(directory), 0, 'a fact name inside an identity is not a checkpoint fact')

    await hooks.event(terminalEvent(sessionID, callID, 'completed'))

    const snapshot = journalSurface.JournalSurface_snapshot(runtime.journal)
    assert.deepEqual(snapshot.todoCheckpoints, [
      { sessionId: sessionID, checkpoints: [{ callId: callID }] },
    ], 'the exact completed terminal commits the checkpoint')

    // Repeated completed events for the same exact call must not append a
    // second durable fact — counted in the event log, not just the projection.
    await hooks.event(terminalEvent(sessionID, callID, 'completed'))
    await hooks.event(terminalEvent(sessionID, callID, 'completed'))
    assert.equal(countCheckpointFacts(directory), 1, 'the durable log carries exactly one checkpoint fact for the call')
    assert.deepEqual(
      journalSurface.JournalSurface_snapshot(runtime.journal).todoCheckpoints,
      snapshot.todoCheckpoints,
      'the projection is unchanged by the replays',
    )
  })
})

integrationTest('WHAT[obligation-ledger-005] an error terminal closes the candidate without a checkpoint and without blocking another call', async () => {
  await withExecutablePlugin(async (hooks, directory, _createdIds, runtime) => {
    const sessionID = 'ol005-error'
    const failedCall = 'ol005-call-error'
    const goodCall = 'ol005-call-after-error'
    await acceptAuthorityRoot(runtime, sessionID, 'engineer')

    await todoCall(hooks, sessionID, failedCall)
    await hooks.event(terminalEvent(sessionID, failedCall, 'error'))
    assert.deepEqual(journalSurface.JournalSurface_snapshot(runtime.journal).todoCheckpoints, [], 'an error terminal commits no checkpoint')
    assert.equal(countCheckpointFacts(directory), 0, 'the durable log carries no checkpoint fact for the error call')

    // A repeated error terminal for the same call stays deduplicated.
    await hooks.event(terminalEvent(sessionID, failedCall, 'error'))
    assert.equal(countCheckpointFacts(directory), 0, 'the repeated error terminal stays deduplicated')

    // The failed call does not block a later legal call from completing.
    await todoCall(hooks, sessionID, goodCall)
    await hooks.event(terminalEvent(sessionID, goodCall, 'completed'))
    assert.deepEqual(
      journalSurface.JournalSurface_snapshot(runtime.journal).todoCheckpoints,
      [{ sessionId: sessionID, checkpoints: [{ callId: goodCall }] }],
      'a later legal call completes normally',
    )
    assert.equal(countCheckpointFacts(directory), 1, 'the durable log carries exactly the later call\'s fact')
  })
})

integrationTest('WHAT[obligation-ledger-005] distinct sessions and calls never confuse checkpoint identities', async () => {
  await withExecutablePlugin(async (hooks, directory, _createdIds, runtime) => {
    const first = 'ol005-s1'
    const second = 'ol005-s2'
    await acceptAuthorityRoot(runtime, first, 'engineer')
    await acceptAuthorityRoot(runtime, second, 'engineer')

    // Same call id in two sessions: two independent checkpoints.
    await todoCall(hooks, first, 'shared-call')
    await todoCall(hooks, second, 'shared-call')
    await hooks.event(terminalEvent(first, 'shared-call', 'completed'))
    await hooks.event(terminalEvent(second, 'shared-call', 'completed'))
    assert.equal(countCheckpointFacts(directory), 2, 'the same call id in two sessions appends two facts')

    // Concatenation-collision counterexample: session "ab" + call "c" and
    // session "a" + call "bc" produce the same string when concatenated
    // without a separator. The dedup key must keep them distinct.
    const collideA = 'ol005-ab'
    const collideB = 'ol005-a'
    await acceptAuthorityRoot(runtime, collideA, 'engineer')
    await acceptAuthorityRoot(runtime, collideB, 'engineer')
    await todoCall(hooks, collideA, 'c')
    await todoCall(hooks, collideB, 'bc')
    await hooks.event(terminalEvent(collideA, 'c', 'completed'))
    await hooks.event(terminalEvent(collideB, 'bc', 'completed'))

    const snapshot = journalSurface.JournalSurface_snapshot(runtime.journal)
    const windowA = snapshot.todoCheckpoints.find((entry) => entry.sessionId === collideA)
    const windowB = snapshot.todoCheckpoints.find((entry) => entry.sessionId === collideB)
    assert.ok(windowA, 'the colliding first session keeps its checkpoint')
    assert.ok(windowB, 'the colliding second session keeps its checkpoint')
    assert.deepEqual(windowA.checkpoints, [{ callId: 'c' }])
    assert.deepEqual(windowB.checkpoints, [{ callId: 'bc' }])
    assert.equal(countCheckpointFacts(directory), 4, 'all four identities append their own fact')

    const colonA = 'ol005-colon:a'
    const colonB = 'ol005-colon'
    await acceptAuthorityRoot(runtime, colonA, 'engineer')
    await acceptAuthorityRoot(runtime, colonB, 'engineer')
    await todoCall(hooks, colonA, 'b')
    await todoCall(hooks, colonB, 'a:b')
    await hooks.event(terminalEvent(colonA, 'b', 'completed'))
    await hooks.event(terminalEvent(colonB, 'a:b', 'completed'))
    const colonWindows = journalSurface.JournalSurface_snapshot(runtime.journal).todoCheckpoints
    assert.deepEqual(colonWindows.find((entry) => entry.sessionId === colonA)?.checkpoints, [{ callId: 'b' }])
    assert.deepEqual(colonWindows.find((entry) => entry.sessionId === colonB)?.checkpoints, [{ callId: 'a:b' }])
    assert.equal(countCheckpointFacts(directory), 6, 'identities containing the old separator remain independent')
  })
})
const { renameSync, writeFileSync } = await import("node:fs");

// Refuse physical appends in the fixture workspace, restoring its events
// directory even when the tested operation fails.
const withBlockedEvents = async (directory, action) => {
  const eventsDir = join(directory, '.git', 'wanxiangshu', 'events')
  const { mkdtempSync: mkStash, mkdirSync, readdirSync, rmSync: rmStash } = await import("node:fs")
  const osModule = await import('node:os')
  const stash = mkStash(join(osModule.tmpdir(), 'wxs-005-stash-'))
  // Replace each existing writer file with a same-named directory: the events
  // directory stays usable, so the fault lands in PhysicalAppend
  // (AppendAllText against a directory) and reports an unknown outcome.
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

integrationTest('WHAT[obligation-ledger-005] concurrent duplicate terminals share one append outcome instead of an early success', async () => {
  await withExecutablePlugin(async (hooks, directory, _createdIds, runtime) => {
    const sessionID = 'ol005-concurrent'
    const callID = 'ol005-concurrent-call'
    await acceptAuthorityRoot(runtime, sessionID, 'engineer')
    await todoCall(hooks, sessionID, callID)

    await withBlockedEvents(directory, async () => {
      // Two completed events for the same exact call race on the same
      // blocked append. Both callers must observe the append outcome —
      // an early fulfilled duplicate would claim a checkpoint that was
      // never committed.
      const first = hooks.event(terminalEvent(sessionID, callID, 'completed'))
      const second = hooks.event(terminalEvent(sessionID, callID, 'completed'))
      const outcomes = await Promise.allSettled([first, second])
      const statuses = outcomes.map((outcome) => outcome.status)

      // The blocked append fails; both callers must see that failure.
      assert.deepEqual(statuses, ['rejected', 'rejected'], 'both duplicate terminals observe the append failure')
      for (const outcome of outcomes) {
        assert.match(String(outcome.reason), /append outcome unknown/i, 'the rejection carries the unknown append outcome')
      }
      assert.equal(String(outcomes[1].reason), String(outcomes[0].reason), 'both callers observe the same failed event identity and outcome')
    })

    // After the blockage is removed the durable log carries no checkpoint
    // fact for the call — nothing was silently committed.
    assert.equal(countCheckpointFacts(directory), 0, 'no checkpoint fact was committed while the append was blocked')
  })
})

integrationTest('WHAT[obligation-ledger-005] concurrent duplicates of a successful terminal share the one append and commit exactly one fact', async () => {
  await withExecutablePlugin(async (hooks, directory, _createdIds, runtime) => {
    const sessionID = 'ol005-concurrent-ok'
    const callID = 'ol005-concurrent-ok-call'
    await acceptAuthorityRoot(runtime, sessionID, 'engineer')
    await todoCall(hooks, sessionID, callID)

    // Two completed events for the same exact call race on the same append;
    // both must observe the success and the durable log must carry exactly
    // one checkpoint fact.
    const first = hooks.event(terminalEvent(sessionID, callID, 'completed'))
    const second = hooks.event(terminalEvent(sessionID, callID, 'completed'))
    await Promise.all([first, second])

    assert.equal(countCheckpointFacts(directory), 1, 'the shared append commits exactly one durable fact')
    assert.deepEqual(
      journalSurface.JournalSurface_snapshot(runtime.journal).todoCheckpoints,
      [{ sessionId: sessionID, checkpoints: [{ callId: callID }] }],
      'the projection carries the single checkpoint',
    )
  })
})

integrationTest('WHAT[obligation-ledger-005] a WriteUnknown append outcome is never retried for the same call', async () => {
  await withExecutablePlugin(async (hooks, directory, _createdIds, runtime) => {
    const sessionID = 'ol005-write-unknown'
    const callID = 'ol005-write-unknown-call'
    await acceptAuthorityRoot(runtime, sessionID, 'engineer')
    await todoCall(hooks, sessionID, callID)

    await withBlockedEvents(directory, async () => {
      // The blocked append reports an unknown outcome: the event may already
      // be durable, so repeating it is unsafe. The first caller observes the
      // failure...
      let firstFailure
      await assert.rejects(
        () => hooks.event(terminalEvent(sessionID, callID, 'completed')),
        (failure) => {
          firstFailure = String(failure)
          assert.match(firstFailure, /append outcome unknown/i)
          return true
        },
        'the first terminal observes the WriteUnknown failure',
      )
      // ...and a duplicate terminal for the same call observes the same
      // failure instead of silently succeeding or re-attempting the append.
      await assert.rejects(
        () => hooks.event(terminalEvent(sessionID, callID, 'completed')),
        (failure) => {
          assert.equal(String(failure), firstFailure, 'the retry preserves the original uncertain event identity and failure')
          return true
        },
        'the duplicate terminal observes the same unknown outcome, never an early success',
      )
    })

    // After the blockage is removed no fact was committed and no re-attempt
    // happened: the unknown outcome stays settled for the call.
    assert.equal(countCheckpointFacts(directory), 0, 'no fact was committed or re-attempted for the unknown outcome')
  })
})

integrationTest('WHAT[obligation-ledger-005] a WriterUnavailable outcome releases the call so a retry reaches a fresh writer admission', async () => {
  await withExecutablePlugin(async (hooks, directory, _createdIds, runtime) => {
    const sessionID = 'ol005-writer-unavailable'
    const firstCall = 'ol005-wu-call-1'
    await acceptAuthorityRoot(runtime, sessionID, 'engineer')
    await todoCall(hooks, sessionID, firstCall)

    // The first blocked append poisons the writer with WriteUnknown; after
    // the blockage is removed the writer stays poisoned, so a later call's
    // append is explicitly NotAttempted (WriterUnavailable).
    await withBlockedEvents(directory, async () => {
      await assert.rejects(
        () => hooks.event(terminalEvent(sessionID, firstCall, 'completed')),
        /append outcome unknown/i,
        'the blocked append reports an unknown outcome',
      )
    })

    const secondCall = 'ol005-wu-call-2'
    await todoCall(hooks, sessionID, secondCall)
    // The poisoned writer refuses the new call's append as NotAttempted.
    let firstRefusedEvent
    await assert.rejects(
      () => hooks.event(terminalEvent(sessionID, secondCall, 'completed')),
      (failure) => {
        const matched = String(failure).match(/append not attempted for ([^:]+): writer poisoned/i)
        assert.ok(matched, 'the first refusal includes the attempted admission identity')
        firstRefusedEvent = matched[1]
        return true
      },
      'the poisoned writer reports the append as not attempted',
    )
    // A new refusal identity distinguishes fresh writer admission from
    // replaying a cached error. The poisoned writer performs no new write.
    await assert.rejects(
      () => hooks.event(terminalEvent(sessionID, secondCall, 'completed')),
      (failure) => {
        const matched = String(failure).match(/append not attempted for ([^:]+): writer poisoned/i)
        assert.ok(matched, 'the retry is explicitly refused before physical append')
        assert.notEqual(matched[1], firstRefusedEvent, 'the retry reaches writer admission with a new event identity')
        return true
      },
      'the retry gets a fresh writer refusal, not a cached failure',
    )
    assert.equal(countCheckpointFacts(directory), 0, 'no fact was committed through the poisoned writer')
  })
})

}
