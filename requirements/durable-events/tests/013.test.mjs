import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { existsSync, readFileSync, mkdirSync, mkdtempSync, rmSync, writeFileSync } = await import("node:fs");
const { readFile } = await import("node:fs/promises");
const { default: path } = await import("node:path");
const { tmpdir } = await import("node:os");
const { join } = await import("node:path");
const { default: test } = await import("node:test");
const eventStore = await import("../../../dist/Persistence/EventStore/Surface.js");
const journal = await import("../../../dist/Persistence/Journal/Surface.js");

const id = (n) => n.toString(16).padStart(40, '0')
const canonicalize = (value) => {
  if (Array.isArray(value)) return value.map(canonicalize)
  if (value && typeof value === 'object') {
    return Object.fromEntries(Object.keys(value).sort().map((key) => [key, canonicalize(value[key])]))
  }
  return value
}
const canonicalEventLine = (event) => `${JSON.stringify(canonicalize(event))}\n`
const withTemp = (fn) => {
  const base = mkdtempSync(join(tmpdir(), 'wxs-event-store-append-'))
  return fn(base)
}
const event = (n, parents = [], type = 'JobRequested', payload = { n }) => ({
  id: id(n),
  stream: 'append/proof',
  type,
  parents: parents.map((p) => id(p)),
  payload,
  payloadRefs: [],
})

test('WHAT[durable-events-013] before-append directory failure leaves bytes event and structural Current unchanged', async () => {
  const dir = withTemp((base) => base)
  const store = eventStore.create(dir, 'append-failure-proof')
  try {
    const wanxiangDir = path.join(dir, 'wanxiangshu')
    mkdirSync(wanxiangDir, { recursive: true })
    const eventsPath = path.join(wanxiangDir, 'events')
    const beforeBytes = Buffer.from('not a directory', 'utf8')
    writeFileSync(eventsPath, beforeBytes)
    const incoming = event(3)

    const rejected = await eventStore.append(store, [incoming])

    assert.equal(rejected.ok, false)
    const { cause, ...failure } = rejected.error
    assert.deepEqual(failure, {
      code: 'AppendNotAttempted', phase: 'BeforePhysicalAppend', cleanupFailures: [],
      requested: [incoming], prepared: { durableEvents: [incoming], cuts: [] }, priorRejection: null,
    })
    assert.ok(cause instanceof Error)
    assert.equal(cause.code, 'EEXIST')
    assert.equal(cause.syscall, 'mkdir')
    assert.equal(cause.path, eventsPath)
    assert.deepEqual(readFileSync(eventsPath), beforeBytes, 'the blocking ordinary file must remain byte-identical')
    assert.equal(existsSync(path.join(eventsPath, 'append-failure-proof.ndjson')), false)
    assert.equal(existsSync(path.join(dir, 'wanxiangshu.lock')), false, 'the acquired physical gate is released')
    assert.equal(eventStore.read(store, id(3)), null, 'the unattempted append must not publish its prepared event')
    assert.equal(eventStore.head(store, 'append/proof'), null, 'the unattempted append must not advance Current')
    assert.deepEqual(eventStore.heads(store, 'append/proof'), [])
  } finally {
    eventStore.dispose(store)
    rmSync(dir, { recursive: true, force: true })
  }
})
}

{
const { default: assert } = await import("node:assert/strict");
const { execFileSync } = await import("node:child_process");
const { existsSync, mkdirSync, mkdtempSync, rmSync, writeFileSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join } = await import("node:path");
const { default: test } = await import("node:test");
const journal = await import("../../../dist/Persistence/Journal/Surface.js");

const CLOSED = {
  family: 'Companion',
  case: 'TerminalOutputCaptured',
  payload: { SessionId: 'ses_es_boot', TextRef: 'blob-boot', TextDigest: 'sha-boot', ProviderRun: 'run-boot' },
}
const mustOk = (result, label) => {
  assert.equal(result.ok, true, `${label}: ${JSON.stringify(result.error)}`)
  return result
}
const withRepo = (fn) => {
  const repo = mkdtempSync(join(tmpdir(), 'wxs-journal-boot-'))
  execFileSync('git', ['init', '--quiet', repo])
  const commonDir = join(repo, '.Herdchestra')
  return fn(commonDir)
    .finally(() => rmSync(repo, { recursive: true, force: true }))
}

test('WHAT[durable-events-013] restart_replays_prior_writer_files_then_fresh_runtime_starts_LocalSeq_at_1', async () => {
  await withRepo(async (commonDir) => {
    const first = mustOk(await journal.JournalSurface_bootWithWriterId(commonDir, 'boot-writer-a', 'rt_before', 4242, '2026-04-01T00:00:00Z'), 'first boot')

    assert.equal(Number(first.localSeq), 1)
    const firstAppend = mustOk(
      await journal.JournalSurface_appendAgent(first.journal, { kind: 'Session', session: 'ses_es_boot' }, null, CLOSED),
      'first append',
    )
    assert.ok(journal.JournalSurface_hasSession(first.journal, 'ses_es_boot'))
    journal.JournalSurface_dispose(first.journal)

    const restarted = mustOk(await journal.JournalSurface_bootWithWriterId(commonDir, 'boot-writer-b', 'rt_after', 5252, '2026-04-02T00:00:00Z'), 'reboot')
    assert.equal(Number(restarted.localSeq), 1, 'fresh RuntimeId owns a fresh LocalSeq domain')
    assert.ok(journal.JournalSurface_hasSession(restarted.journal, 'ses_es_boot'), 'prior journal fact is rebuilt only through Integrator boot replay')
    journal.JournalSurface_dispose(restarted.journal)
  })
})
test('WHAT[durable-events-013] boot_and_live_use_one_CanonicalIntegrator_program', async () => {
  const { readFile } = await import('node:fs/promises')
  const integrator = await readFile(new URL('../../../src/Wanxiangshu/Persistence/EventStore/IntegratorEngine.fs', import.meta.url), 'utf8')
  const writer = await readFile(new URL('../../../src/Wanxiangshu/Persistence/Journal/EventStoreJournalWriter.fs', import.meta.url), 'utf8')
  assert.match(integrator, /EventKWayMerge\.merge/)
  assert.match(integrator, /integrateOne/)
  assert.match(integrator, /prepareLive/)
  assert.doesNotMatch(writer, /readStreams|loadEvent|Fold\.apply|OpenSnapshot/)
})
}

{
const { default: assert } = await import("node:assert/strict");
const { execFileSync } = await import("node:child_process");
const { existsSync, mkdtempSync, readFileSync, rmSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join } = await import("node:path");
const { default: test } = await import("node:test");
const journal = await import("../../../dist/Persistence/Journal/Surface.js");

const CLOSED_AGENT = {
  family: 'Companion',
  case: 'TerminalOutputCaptured',
  payload: { SessionId: 'ses_es_writer', TextRef: 'blob-writer', TextDigest: 'sha-writer', ProviderRun: 'run-writer' },
}
const mustOk = (result, label) => {
  assert.equal(result.ok, true, `${label}: ${JSON.stringify(result.error)}`)
  return result
}
const withRepo = (writerId, fn) => {
  const repo = mkdtempSync(join(tmpdir(), 'wxs-journal-'))
  execFileSync('git', ['init', '--quiet', repo])
  const commonDir = join(repo, '.Herdchestra')
  return fn(commonDir)
    .finally(() => rmSync(repo, { recursive: true, force: true }))
}

test('WHAT[durable-events-013] journal_surface_does_not_mint_terminal_proof_from_forged_strings', () => {
  assert.equal(Object.hasOwn(journal, 'JournalSurface_recordTerminalCompletion'), false)
})
}

{
const { default: assert } = await import("node:assert/strict");
const { execFileSync } = await import("node:child_process");
const { mkdtempSync, rmSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join } = await import("node:path");
const { default: test } = await import("node:test");
const journal = await import("../../../dist/Persistence/Journal/Surface.js");
const revisions = await import("../../../dist/Persistence/Journal/RevisionSurface.js");

const lifeOpened = (session) => ({
  case: 'LifeOpened',
  payload: {
    SessionId: session,
    LifeId: `life-${session}`,
    OpeningUserMessageId: `msg-${session}`,
    OpeningTextRef: 'blobs/placeholder',
    OpeningTextDigest: 'sha-placeholder',
    OpeningCursorSequence: 1,
  },
})
const openJournal = async (writerId) => {
  const repo = mkdtempSync(join(tmpdir(), `wxs-jrev-${writerId}-`))
  execFileSync('git', ['init', '--quiet', repo])
  const commonDir = join(repo, '.Herdchestra')
  const opened = await journal.JournalSurface_bootWithWriterId(commonDir, writerId, `rt-${writerId}`, 4242, '2026-04-01T00:00:00Z')
  assert.equal(opened.ok, true, JSON.stringify(opened.error))
  return {
    handle: opened.journal,
    close: () => {
      journal.JournalSurface_dispose(opened.journal)
      rmSync(repo, { recursive: true, force: true })
    },
  }
}
const withJournal = async (writerId, fn) => {
  const opened = await openJournal(writerId)
  try {
    await fn(opened.handle)
  } finally {
    opened.close()
  }
}
const appendLife = (handle, session) =>
  journal.JournalSurface_appendManagerLifecycle(handle, { kind: 'Session', session }, lifeOpened(session))
const revisionOf = (handle) => Number(revisions.revision(handle))

test('WHAT[durable-events-013] EXEC_journal_revision_advances_only_on_successful_fold', async (context) => {
  const opened = await openJournal('revision-advance')
  context.after(opened.close)
  const before = Number(revisions.revision(opened.handle))
  assert.equal(before, 0, 'pure load writes no RuntimeStarted and starts at revision 0')

  const linked = await appendLife(opened.handle, 'ses_p')
  assert.equal(linked.ok, true, JSON.stringify(linked.error))

  const after = Number(revisions.revision(opened.handle))
  assert.equal(after, 2, 'first business append lazily writes RuntimeStarted#1 then publishes business#2')
})
test('WHAT[durable-events-013] EXEC_AwaitChangeFrom_after_append_returns_promptly', async () => {
  await withJournal('revision-prompt', async (handle) => {
    const from = revisionOf(handle)
    const linked = await appendLife(handle, 'ses_prompt')
    assert.equal(linked.ok, true, JSON.stringify(linked.error))

    const started = Date.now()
    const change = await revisions.awaitChangeFrom(from, handle)
    const elapsed = Date.now() - started

    assert.ok(elapsed < 500, `must not wait full budget; elapsed=${elapsed}`)
    assert.ok(change.revision > from)
    assert.equal(change.revision, revisionOf(handle))
  })
})
test('WHAT[durable-events-013] EXEC_AwaitChangeFrom_before_append_waits_then_completes', async () => {
  await withJournal('revision-wait', async (handle) => {
    const from = revisionOf(handle)
    const pending = revisions.awaitChangeFrom(from, handle)

    setTimeout(() => {
      void appendLife(handle, 'ses_wait').then((linked) => {
        assert.equal(linked.ok, true, JSON.stringify(linked.error))
      })
    }, 30)

    const change = await pending
    assert.ok(change.revision > from)
    assert.equal(typeof change.envelope, 'string')
    assert.ok(change.envelope.length > 0)
  })
})
test('WHAT[durable-events-013] EXEC_cancelled_revision_subscription_unregisters_without_a_fact', async () => {
  await withJournal('revision-cancel', async (handle) => {
    const from = revisionOf(handle)
    assert.equal(await revisions.awaitCancelled(from, handle), true)
    assert.equal(revisionOf(handle), from, 'cancellation is not a durable fact')
  })
})
}

{
const { default: assert } = await import("node:assert/strict");
const { readFileSync } = await import("node:fs");
const { default: test } = await import("node:test");
const { CANONICAL_EVENT_READER_OWNER_PATHS, DUAL_WRITE_ALLOWLIST, GIT_BYPASS_ALLOWLIST, NON_STORE_SCHEMA_VERSION_SITES, PHYSICAL_HISTORY_OBSERVER_PATHS, SCANNER_IDS, collectProductionEntries, scanCanonicalSharedProgram, scanDualWrite, scanFeatureHistoryLoop, scanFeatureRef, scanFiles, scanGitBypass, scanPrivateDurableSubstrate, scanSchemaVersionInStoreContext, scanText } = await import("../../../scripts/checks/unified-store-gate.mjs");

const readFixture = (name) =>
  readFileSync(new URL(`./fixtures/${name}`, import.meta.url), 'utf8')

test('WHAT[durable-events-013] canonical shape requires one-envelope rule and shared boot live program', () => {
  const kernelFile = 'src/Wanxiangshu/Persistence/EventStore/IntegrationKernel.fs'
  const canonicalFile = 'src/Wanxiangshu/Persistence/EventStore/IntegratorEngine.fs'
  const kernel = [
    'type IntegrationRule =',
    '    { Name: string',
    '      Integrate: obj -> EventEnvelope -> Result<obj, string> }',
    'type PreparedIntegration = { Commit: unit -> unit }',
  ].join('\n')
  const goodCanonical = [
    'let private integrateOne state envelope = Ok state',
    'let private replay streams =',
    '    integrateOne empty (List.head streams)',
    'let private fullReplayGate = obj ()',
    'let prepareLive state events =',
    '    integrateOne state (List.head events)',
    '{ new ICanonicalIntegrator with',
    '    member _.ReloadLocal(commonDir) = replay streams',
    '    member _.PrepareLive(events) = prepareLive state events',
    '    member _.IsEventTypeKnown(eventType) = true',
    '    member _.TryCurrent(key) = None }',
  ].join('\n')

  assert.deepEqual(
    scanCanonicalSharedProgram([
      { file: kernelFile, text: kernel },
      { file: canonicalFile, text: goodCanonical },
    ]),
    [],
  )

  const collectionRule = kernel.replace(
    'Integrate: obj -> EventEnvelope -> Result<obj, string>',
    'Integrate: obj -> EventEnvelope list -> Result<obj, string>',
  )
  const collectionHits = scanCanonicalSharedProgram([
    { file: kernelFile, text: collectionRule },
    { file: canonicalFile, text: goodCanonical },
  ])
  assert.deepEqual(
    collectionHits.map(({ file, line, token }) => ({ file, line, token })),
    [{ file: kernelFile, line: 1, token: 'IntegrationRule.Integrate(EventEnvelope)' }],
  )

  const missingSharedCalls = goodCanonical
    .replace('integrateOne empty (List.head streams)', 'Ok empty')
    .replace('integrateOne state (List.head events)', 'Ok state')
  const hits = scanCanonicalSharedProgram([
    { file: kernelFile, text: kernel },
    { file: canonicalFile, text: missingSharedCalls },
  ])
  assert.deepEqual(
    hits.map(({ file, line, token }) => ({ file, line, token })),
    [
      { file: canonicalFile, line: 2, token: 'replay->integrateOne' },
      { file: canonicalFile, line: 5, token: 'PrepareLive->integrateOne' },
    ],
  )

  const bypassCanonical = goodCanonical
    .replace('integrateOne empty (List.head streams)', 'alternateReplay empty streams\n    let _ = integrateOne')
    .replace('integrateOne state (List.head events)', 'alternateLive state events\n    let _ = integrateOne')
    .replace('member _.ReloadLocal(commonDir) = replay streams', 'member _.ReloadLocal(commonDir) = alternateReplay empty streams')
    .replace('member _.PrepareLive(events) = prepareLive state events', 'member _.PrepareLive(events) = alternateLive state events')
  const bypassHits = scanCanonicalSharedProgram([
    { file: kernelFile, text: kernel },
    { file: canonicalFile, text: bypassCanonical },
  ])
  assert.deepEqual(
    bypassHits.map(({ token }) => token),
    [
      'ReloadLocal->replay',
      'replay->integrateOne',
      'PrepareLive->integrateOne',
      'PrepareLive->integrateOne',
    ],
    'alternate reducers and unused integrateOne references must not satisfy any call-graph edge',
  )
})
test('WHAT[durable-events-013] production CanonicalIntegrator has the shared one-envelope program shape', () => {
  const entries = collectProductionEntries()
  const violations = scanCanonicalSharedProgram(entries)
  assert.deepEqual(
    violations,
    [],
    violations.map((v) => `[${v.id}] ${v.file}:${v.line} ${v.token} ${v.label}`).join('\n'),
  )
})
}
