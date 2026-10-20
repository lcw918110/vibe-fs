import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { readFileSync } = await import("node:fs");
const { default: test } = await import("node:test");
const journalCodec = await import("../../../dist/Persistence/Journal/CodecSurface.js");
const factCodec = await import("../../../dist/Persistence/Journal/FactCodecSurface.js");

const SESSION = 'ses_a'
const ROOT_ACCEPTED = {
  family: 'Prompt',
  case: 'AuthorityRootAccepted',
  payload: {
    SchemaVersion: 2,
    SessionId: SESSION,
    LogicalRunId: 'run_a',
    AuthorityRootUserMessageId: 'msg_root_a',
    AuthorityKind: 'HumanRoot',
    IdentitySeed: {
      kind: 'RootSelection',
      ownerSession: null,
      ownerLogicalRun: null,
      ownerAuthorityRoot: null,
      participantIdentity: { participant: 'engineer', role: 'engineer', persona: 'Engineer', personaCatalogVersion: 1, origin: 'ResolvedAtRoot' },
    },
  },
}
const env = (overrides = {}) => ({
  runtime: 'rt_a',
  seq: 1,
  observedAt: '2026-01-02T03:04:05Z',
  id: 'a'.repeat(32),
  stream: { kind: 'Session', id: SESSION },
  providerRun: null,
  fact: ROOT_ACCEPTED,
  ...overrides,
})
const readEnvelope = (value) => ({
  runtime: value.runtime,
  seq: Number(value.seq),
  event: value.id,
  stream: value.stream,
  providerRun: value.providerRun,
  fact: value.fact.case,
})
const mustOk = (result, label = 'result') => {
  assert.equal(result.ok, true, `${label} should be Ok: ${JSON.stringify(result.error)}`)
  return result.value
}

test('WHAT[durable-events-009] PERSIST_005_legacy_fallback_counters_and_model_ids_are_fatal', () => {
  const markers = [
    'FailuresOnCurrentSide',
    'IsDead',
    'TotalFailures',
    'BaseModelID',
    'BaseProviderID',
    'EffectiveModelID',
    'EffectiveProviderID',
  ]

  for (const marker of markers) {
    const line = JSON.stringify({ LocalSeq: 1, [marker]: 3 })
    assert.equal(factCodec.containsLegacyFallbackFields(line), true, `${marker} must be recognised as pre-0.5.0`)
    const decoded = factCodec.decode(line)
    assert.equal(decoded.ok, false)
    assert.equal(decoded.error, factCodec.pre050MigrationMessage)
  }
})
test('WHAT[durable-events-009] PERSIST_005_replaced_fact_names_produce_the_migration_message_not_a_codec_error', () => {
  const retired = [
    'PluginPromptAccepted',
    'HumanPromptAccepted',
    'GuardPromptAccepted',
    'InteractionRepairClaimed',
    'ReviewConfirmedIdle',
    'AgentLinked',
    'AgentForked',
    'AgentUnlinked',
    'OrchestratorCandidateRegistered',
    'OrchestratorRebased',
    'OrchestratorPublishClaimed',
    'DurableEffectRequested',
    'DurableEffectAccepted',
  ]

  for (const name of retired) {
    const line = JSON.stringify({ Fact: ['Agent', [name, {}]] })
    assert.equal(factCodec.decode(line).error, factCodec.pre050MigrationMessage, `${name} must be diagnosed by name`)
  }
})
test('WHAT[durable-events-009] PERSIST_005_the_migration_message_tells_the_operator_what_to_do', () => {
  assert.equal(
    factCodec.pre050MigrationMessage,
    'Wanxiangshu 0.5.0 does not support pre-0.5.0 runtime journals.\n' +
      'Archive or remove the old Wanxiangshu runtime journal before starting.',
  )
})
test('WHAT[durable-events-009] PERSIST_005_a_current_fact_is_not_mistaken_for_a_legacy_one', () => {
  const line = journalCodec.serialize(env({ seq: 1 }))
  assert.equal(factCodec.containsLegacyFallbackFields(line), false)

  for (const current of [
    'HandleLinked',
    'HandleCompleted',
    'HandleAbandoned',
    'HandleRetired',
    'HostTurnObserved',
    'CandidateReady',
    'PublishClaimed',
  ]) {
    assert.equal(
      factCodec.containsLegacyFallbackFields(JSON.stringify({ Fact: ['Agent', [current, {}]] })),
      false,
      `${current} is a current fact and must not trip the pre-0.5.0 check`,
    )
  }
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const factCodec = await import("../../../dist/Persistence/Journal/FactCodecSurface.js");

const runtimeStarted = (startedAt = '2026-01-01T00:00:00Z') => ({
  family: 'Runtime',
  case: 'RuntimeStarted',
  payload: { RuntimeId: 'rt_fact', ProcessId: 42, StartedAt: startedAt },
})
const handleAbandoned = (abandonedAt = '2026-01-01T00:00:00Z') => ({
  family: 'Execution',
  case: 'HandleAbandoned',
  payload: {
    ParentSessionId: 'ses_pin',
    Handle: 'h-pin',
    Reason: 'ParentCancelled',
    AbandonedAt: abandonedAt,
  },
})
const handleCompleted = (overrides = {}) => ({
  family: 'Execution',
  case: 'HandleCompleted',
  payload: {
    ParentSessionId: 'ses_hc',
    Handle: 'h-hc',
    Kind: 'Terminal',
    CompletionRef: null,
    CompletionDigest: null,
    ...overrides,
  },
})
const handleLinked = (overrides = {}) => ({
  family: 'Execution',
  case: 'HandleLinked',
  payload: {
    ParentSessionId: 'ses_hl',
    ChildSessionId: 'ses_hl_child',
    Handle: 'h-hl',
    TargetAgent: 'coder',
    Byname: 'Rhea',
    CanonicalRole: 'Engineer',
    Ownership: 'DurableParentHandle',
    ...overrides,
  },
})

test('WHAT[durable-events-009] PERSIST_005_modern_json_has_no_legacy_markers', () => {
  assert.equal(factCodec.containsLegacyFallbackFields('{"RuntimeStarted":{"Runtime":"rt"}}'), false)
})
test('WHAT[durable-events-009] historical_unanchored_guideline_is_refused_without_rewrite', () => {
  const legacy = JSON.stringify({ PairProgrammingGuidelineAppended: { Ordinal: 1, MarkerText: 'legacy' } })
  const decoded = factCodec.decode(legacy)
  assert.equal(decoded.ok, false)
})
test('WHAT[durable-events-009] Fact_codec_reports_migration_markers_as_data_errors', () => {
  const markers = [
    'FailuresOnCurrentSide',
    'IsDead',
    'TotalFailures',
    'BaseModelID',
    'BaseProviderID',
    'EffectiveModelID',
    'EffectiveProviderID',
  ]

  for (const marker of markers) {
    const line = JSON.stringify({ Fact: ['Agent', ['FallbackCursorAdvanced', { [marker]: 1 }]] })
    assert.equal(factCodec.containsLegacyFallbackFields(line), true, `${marker} must be refused`)
    const decoded = factCodec.decode(line)
    assert.equal(decoded.ok, false)
    assert.equal(decoded.error, factCodec.pre050MigrationMessage)
  }
})
}

{
const { default: assert } = await import("node:assert/strict");
const { readFileSync } = await import("node:fs");
const { default: test } = await import("node:test");
const { CANONICAL_EVENT_READER_OWNER_PATHS, DUAL_WRITE_ALLOWLIST, GIT_BYPASS_ALLOWLIST, NON_STORE_SCHEMA_VERSION_SITES, PHYSICAL_HISTORY_OBSERVER_PATHS, SCANNER_IDS, collectProductionEntries, scanCanonicalSharedProgram, scanDualWrite, scanFeatureHistoryLoop, scanFeatureRef, scanFiles, scanGitBypass, scanPrivateDurableSubstrate, scanSchemaVersionInStoreContext, scanText } = await import("../../../scripts/checks/unified-store-gate.mjs");

const readFixture = (name) =>
  readFileSync(new URL(`./fixtures/${name}`, import.meta.url), 'utf8')

test('WHAT[durable-events-009] fixture unified-store-dual-write.fs is RED for dual-write', () => {
  const source = readFixture('unified-store-dual-write.fs')
  const hits = scanDualWrite(source, 'src/Wanxiangshu/Application/DualWriteBridge.fs')
  assert.ok(hits.length >= 1, 'expected dual-write violation')
  assert.equal(hits[0].id, 'dual-write')
})
test('WHAT[durable-events-009] Journal-only or EventStore-only modules are not dual-write', () => {
  const journalOnly = [
    'module RuntimePath',
    'let root = joinPath common "wanxiangshu"',
    'let file = sprintf "%s.ndjson" runtimeId',
    'type AgentJournal(writer: JournalWriter) =',
    '    member _.AppendAgent fact = writer.Append fact',
  ].join('\n')
  assert.equal(scanDualWrite(journalOnly, 'src/Wanxiangshu/Journal/AgentJournal.fs').length, 0)

  const eventStoreOnly = [
    'module EventStore',
    'let append (store: IEventStore) candidate =',
    '    store.Append candidate.NewEvents',
    'let create store = EventStore.create store',
  ].join('\n')
  assert.equal(
    scanDualWrite(eventStoreOnly, 'src/Wanxiangshu/Persistence/EventStore/Store.fs').length,
    0,
  )
})
test('WHAT[durable-events-009] dual-write allowlist is empty (no parked bridges)', () => {
  assert.deepEqual([...DUAL_WRITE_ALLOWLIST], [
    'src/Wanxiangshu/Verification/JournalPortObservationSurface.fs',
  ])
})
test('WHAT[durable-events-009] production scan has no dual-write residue', () => {
  const entries = collectProductionEntries()
  const violations = scanFiles(entries)
  const own = violations.filter((v) => v.id === 'dual-write')
  assert.deepEqual(
    own,
    [],
    own.map((v) => `[${v.id}] ${v.file}:${v.line} ${v.label}`).join('\n'),
  )
})
}

{
const { default: assert } = await import("node:assert/strict");
const { createHash } = await import("node:crypto");
const { execFileSync } = await import("node:child_process");
const { mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, statSync, writeFileSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join } = await import("node:path");
const { default: test } = await import("node:test");
const workspaceHost = await import("../../../dist/OpenCode/Host/WorkspaceSharedJournal.js");
const journalSurface = await import("../../../dist/Persistence/Journal/Surface.js");

const POISON = 'LEAVE_UNREAD_POISON_SENTINEL_NEVER_PARSE\n{not-a-journal-envelope\n'
const fingerprint = (path) => {
  const st = statSync(path)
  return { size: st.size, mtimeMs: st.mtimeMs, ino: st.ino, sha256: createHash('sha256').update(readFileSync(path)).digest('hex') }
}
const mustOk = (result) => {
  assert.equal(result.ok, true, result.ok ? '' : JSON.stringify(result.error))
  return result
}
const openRepo = (name) => {
  const workspace = mkdtempSync(join(tmpdir(), `wxs-host-es-${name}-`))
  execFileSync('git', ['init', '--quiet', workspace])
  return {
    workspace,
    commonDir: join(workspace, '.Herdchestra'),
    close: () => rmSync(workspace, { recursive: true, force: true }),
  }
}
const withRepo = async (name, fn) => {
  const opened = openRepo(name)
  try {
    await fn(opened.workspace, opened.commonDir)
  } finally {
    opened.close()
  }
}

test('WHAT[durable-events-009] SharedAgentJournal_cache_hit_returns_same_instance_without_rereading_retired_path', async (context) => {
  const opened = openRepo('cache')
  context.after(opened.close)
  const retiredDir = join(opened.commonDir, 'wanxiangshu-next', 'runtimes')
  mkdirSync(retiredDir, { recursive: true })
  const stale = join(retiredDir, 'old.ndjson')
  writeFileSync(stale, POISON)
  const before = fingerprint(stale)

  const first = mustOk(await workspaceHost.acquire(retiredDir, opened.commonDir, process.pid, '2026-04-01T00:00:00Z'))
  const second = mustOk(await workspaceHost.acquire(retiredDir, opened.commonDir, process.pid, '2026-04-01T00:00:01Z'))
  assert.equal(workspaceHost.same(first.journal, second.journal), true)
  assert.deepEqual(fingerprint(stale), before)

  workspaceHost.release(first.journal)
  workspaceHost.release(second.journal)
})
}

{
const { default: assert } = await import("node:assert/strict");
const { createHash } = await import("node:crypto");
const { execFileSync } = await import("node:child_process");
const { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, statSync, writeFileSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join } = await import("node:path");
const eventStore = await import("../../../dist/Persistence/EventStore/Surface.js");
const { integrationTest } = await import("../../verification-system/tests/support/tier-gate.mjs");
const POISON = 'LEAVE_UNREAD_POISON_SENTINEL_NEVER_PARSE\n{not-a-journal-envelope\n'

const fingerprint = (p) => {
  const s = statSync(p)
  return {
    size: s.size,
    mtimeMs: s.mtimeMs,
    ino: s.ino,
    sha256: createHash('sha256').update(readFileSync(p)).digest('hex'),
  }
}

const plant = (commonDir) => {
  const oldRuntime = join(commonDir, 'wanxiangshu-next', 'runtimes')
  const oldUnified = join(commonDir, 'objects-for-old-universal-store')
  mkdirSync(oldRuntime, { recursive: true })
  mkdirSync(oldUnified, { recursive: true })
  const a = join(oldRuntime, 'abandoned.ndjson')
  const b = join(oldUnified, 'legacy.poison')
  writeFileSync(a, POISON)
  writeFileSync(b, POISON)
  return [
    [a, fingerprint(a)],
    [b, fingerprint(b)],
  ]
}

integrationTest('WHAT[durable-events-009] local_EventStore_never_reads_or_rewrites_any_legacy_layout', async () => {
  const repo = mkdtempSync(join(tmpdir(), 'wxs-leave-unread-'))
  try {
    execFileSync('git', ['init', '--quiet', repo])
    const commonDir = join(repo, '.Herdchestra')
    const planted = plant(commonDir)
    const local = eventStore.create(commonDir, 'fresh-writer')
    const event = {
      id: 'a'.repeat(40),
      stream: 'leave-unread/new',
      type: 'JobRequested',
      parents: [],
      payload: { ok: true },
      payloadRefs: [],
    }

    const result = await eventStore.append(local, [event])
    eventStore.dispose(local)
    assert.equal(result.ok, true, `append failed: ${JSON.stringify(result.error)}`)

    for (const [path, before] of planted) {
      assert.deepEqual(fingerprint(path), before)
      assert.equal(readFileSync(path, 'utf8'), POISON)
    }
  } finally {
    rmSync(repo, { recursive: true, force: true })
  }
})

integrationTest('WHAT[durable-events-009] shock_cut_source_has_no_legacy_shape_detection_migration_or_reset', async () => {
  const { readFile } = await import('node:fs/promises')
  const eventStore = await readFile(
    new URL('../../../src/Wanxiangshu/Persistence/EventStore/Store.fs', import.meta.url),
    'utf8',
  )
  const log = await readFile(
    new URL('../../../src/Wanxiangshu/Persistence/EventStore/ProcessEventLog.fs', import.meta.url),
    'utf8',
  )
  const legacySources = [
    new URL('../../../src/Wanxiangshu/Infrastructure/Persist/GitRawStore.fs', import.meta.url),
    new URL('../../../src/Wanxiangshu/Infrastructure/Persist/UniversalGitRawStore.fs', import.meta.url),
  ]

  assert.doesNotMatch(
    eventStore + log,
    /LegacyEventsDir|isLegacy|migrat|reset.*root|wanxiangshu-next/i,
  )
  for (const legacy of legacySources) {
    assert.equal(existsSync(legacy), false, `${legacy.pathname}: shock-cut legacy source must stay deleted`)
  }
})
}
