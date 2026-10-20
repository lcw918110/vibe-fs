import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { mkdirSync, mkdtempSync, rmSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join } = await import("node:path");
const { default: test } = await import("node:test");
const eventStore = await import("../../../dist/Persistence/EventStore/Surface.js");
const journal = await import("../../../dist/Persistence/Journal/Surface.js");
const casebook = await import("../../../dist/Repository/Knowledge/Casebook/Surface.js");
const transaction = await import("../../../dist/Repository/Programming/Js/TransactionSurface.js");

const workspace = await import("../../../dist/OpenCode/Host/WorkspaceEventStoreSurface.js");

const id = (n) => n.toString(16).padStart(40, '0')

const structuralEvent = {
  id: id(1),
  stream: 'canonical-integrator/proof',
  type: 'JobRequested',
  parents: [],
  payload: { proof: 'structural' },
  payloadRefs: [],
}
const mustOk = (result, label) => {
  assert.equal(result.ok, true, `${label}: ${JSON.stringify(result.error)}`)
  return result
}

test('WHAT[durable-events-019] every registered business oracle changes its production Current', async () => {
  const root = mkdtempSync(join(tmpdir(), 'wxs-integrator-registration-'))
  const commonDir = join(root, '.Herdchestra')
  mkdirSync(commonDir, { recursive: true })

  try {
    const store = eventStore.create(commonDir, 'business-registration')

    try {
      mustOk(await eventStore.append(store, [structuralEvent]), 'append Structural fact')

      mustOk(
        await casebook.archive(store, {
          sessionId: 'canonical-case',
          q: 'Q',
          a: 'A',
          observations: [],
          lastAccessOrder: 0,
        }),
        'append Casebook fact',
      )

      mustOk(
        await transaction.appendPrepared(store, {
          transactionId: 'canonical-transaction',
          workspaceRoot: '/canonical-workspace',
          mutation: { path: 'a.txt', originalText: 'before', newText: 'after' },
        }),
        'append JsTransaction fact',
      )

      const fetched = mustOk(await casebook.fetchCase(store, 10, 'canonical-case'), 'read Casebook Current')

      assert.deepEqual(
        {
          structuralHead: eventStore.head(store, structuralEvent.stream),
          structuralEvent: eventStore.read(store, structuralEvent.id)?.payload?.proof ?? null,
          caseAnswer: fetched.value?.a ?? null,
          pendingTransactions: transaction.pending(store).map(({ transactionId }) => transactionId).sort(),
        },
        {
          structuralHead: structuralEvent.id,
          structuralEvent: 'structural',
          caseAnswer: 'A',
          pendingTransactions: ['canonical-transaction'],
        },
      )
    } finally {
      eventStore.dispose(store)
    }

    const reopened = eventStore.create(commonDir, 'business-registration-reopen')
    try {
      assert.equal(eventStore.head(reopened, structuralEvent.stream), structuralEvent.id)
      assert.equal(eventStore.read(reopened, structuralEvent.id)?.payload?.proof, 'structural')
      assert.equal(mustOk(await casebook.fetchCase(reopened, 10, 'canonical-case'), 'replayed Casebook Current').value?.a, 'A')
      assert.deepEqual(transaction.pending(reopened).map(({ transactionId }) => transactionId), ['canonical-transaction'])
    } finally {
      eventStore.dispose(reopened)
    }

    const booted = mustOk(
      await journal.JournalSurface_bootWithWriterId(
        commonDir,
        'journal-registration',
        'runtime-journal-registration',
        4242,
        '9999-01-01T00:00:00Z',
      ),
      'boot Journal',
    )

    try {
      const payload = mustOk(
        await journal.JournalSurface_writePayload(booted.journal, 'canonical opening text'),
        'write Journal payload',
      )
      mustOk(
        await journal.JournalSurface_appendManagerLifecycle(
          booted.journal,
          { kind: 'Session', session: 'canonical-session' },
          {
            case: 'LifeOpened',
            payload: {
              SessionId: 'canonical-session',
              LifeId: 'canonical-life',
              OpeningUserMessageId: 'canonical-message',
              OpeningTextRef: payload.blobRef,
              OpeningTextDigest: payload.blobDigest,
              OpeningCursorSequence: 1,
            },
          },
        ),
        'append Journal fact',
      )
      mustOk(
        await journal.JournalSurface_appendAgent(
          booted.journal,
          { kind: 'Session', session: 'canonical-session' },
          null,
          {
            family: 'Companion',
            case: 'TerminalOutputCaptured',
            payload: {
              SessionId: 'canonical-session',
              TextRef: 'blob-term',
              TextDigest: 'sha-term',
              ProviderRun: 'run-term',
            },
          },
        ),
        'append Journal fact',
      )
      assert.equal(journal.JournalSurface_hasSession(booted.journal, 'canonical-session'), true)
    } finally {
      journal.JournalSurface_dispose(booted.journal)
    }
    const reopenedJournal = mustOk(await journal.JournalSurface_bootWithWriterId(
      commonDir, 'journal-reopen', 'runtime-journal-reopen', 4343, '9999-01-02T00:00:00Z',
    ), 'reopen Journal')
    try {
      assert.equal(journal.JournalSurface_hasSession(reopenedJournal.journal, 'canonical-session'), true)
    } finally {
      journal.JournalSurface_dispose(reopenedJournal.journal)
    }
  } finally {
    rmSync(root, { recursive: true, force: true })
  }
})

test('WHAT[durable-events-019] removing one real hostProgram registration blocks its domain result', async () => {
  // Structural 与 Journal 是 spine 注册：移除时 CanonicalIntegrator 的
  // base-rule 前置检查直接拒绝构造。如实断言「构造被整体拒绝」这一
  // fail-closed 形态，不把它伪称为「观察到缺失 Current」。
  for (const ruleName of ['Structural', 'Journal']) {
    const root = mkdtempSync(join(tmpdir(), `wxs-integrator-without-${ruleName.toLowerCase()}-`))
    const commonDir = join(root, '.Herdchestra')
    mkdirSync(commonDir, { recursive: true })
    try {
      assert.throws(
        () => workspace.createIsolatedWithoutRegistration(commonDir, `without-${ruleName.toLowerCase()}`, ruleName),
        (error) => {
          const text = String((error && error.message) || error)
          assert.ok(
            text.includes(`missing required rule '${ruleName}'`),
            `expected a fail-closed construction refusal naming ${ruleName}, got: ${text}`,
          )
          return true
        },
        `removing ${ruleName} must fail closed at construction`,
      )
    } finally {
      rmSync(root, { recursive: true, force: true })
    }
  }

  // 三个域注册的隔离变异：变体程序来自真实 hostProgram 移除恰好一项、
  // 其余注册（含 Sphinx）保持生产顺序；每个变体落在自己的临时目录，
  // 不碰共享工作区。live fact 仍按已知词汇落盘，对应业务 Current 被阻止，
  // 保留注册的 Structural 结果不受牵连。
  const isolatedStore = (ruleName, writerId, root) => {
    const commonDir = join(root, '.Herdchestra')
    mkdirSync(commonDir, { recursive: true })
    return workspace.createIsolatedWithoutRegistration(commonDir, writerId, ruleName)
  }


  {
    const root = mkdtempSync(join(tmpdir(), 'wxs-integrator-without-casebook-'))
    try {
      const store = isolatedStore('Casebook', 'without-casebook', root)
      try {
        mustOk(await eventStore.append(store, [structuralEvent]), 'append Structural fact with Casebook removed')
        mustOk(
          await casebook.archive(store, {
            sessionId: 'isolated-case',
            q: 'Q',
            a: 'A',
            observations: [],
            lastAccessOrder: 0,
          }),
          'append Casebook fact with rule removed',
        )
        const fetched = mustOk(
          await casebook.fetchCase(store, 10, 'isolated-case'),
          'read Casebook Current with rule removed',
        )
        assert.equal(fetched.value, null, 'the Casebook Current no longer produces the case')
        assert.equal(
          eventStore.head(store, structuralEvent.stream),
          structuralEvent.id,
          'the kept Structural registration still folds its live fact',
        )
      } finally {
        eventStore.dispose(store)
      }
    } finally {
      rmSync(root, { recursive: true, force: true })
    }
  }

  {
    const root = mkdtempSync(join(tmpdir(), 'wxs-integrator-without-jstransaction-'))
    try {
      const store = isolatedStore('JsTransaction', 'without-jstransaction', root)
      try {
        mustOk(await eventStore.append(store, [structuralEvent]), 'append Structural fact with JsTransaction removed')
        mustOk(
          await transaction.appendPrepared(store, {
            transactionId: 'isolated-transaction',
            workspaceRoot: '/isolated-workspace',
            mutation: { path: 'a.txt', originalText: 'before', newText: 'after' },
          }),
          'append JsTransaction fact with rule removed',
        )
        assert.deepEqual(
          transaction.pending(store).map(({ transactionId }) => transactionId),
          [],
          'the JsTransaction Current no longer lists the pending transaction',
        )
        assert.equal(
          eventStore.head(store, structuralEvent.stream),
          structuralEvent.id,
          'the kept Structural registration still folds its live fact',
        )
      } finally {
        eventStore.dispose(store)
      }
    } finally {
      rmSync(root, { recursive: true, force: true })
    }
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

test('WHAT[durable-events-019] feature history loops report exact path line and token', () => {
  const fixtures = [
    ['ProcessEventLog.readStreams commonDir', 'ProcessEventLog.readStreams'],
    ['let loadEvents raw = raw', 'loadEvents'],
    ['let scanHistory stream = stream', 'scanHistory'],
    ['let readHistory stream = stream', 'readHistory'],
    ['let foldHistory state events = List.fold apply state events', 'foldHistory'],
    ['let replayEvents events = List.fold alternate empty events', 'replayEvents'],
    ['let event = (store: IEventStore).TryEvent eventId', 'TryEvent'],
    ['let heads = (store: IEventStore).TryHeads streamId', 'TryHeads'],
    ['let heads = (store: IEventStore).AllHeads()', 'AllHeads'],
    ['let event = EventStore.Surface.read(handle, eventId)', 'read'],
    ['let heads = Surface.heads(handle, streamId)', 'heads'],
  ]

  for (const [source, token] of fixtures) {
    const file = 'src/Wanxiangshu/Repository/Feature/History.fs'
    const hits = scanFeatureHistoryLoop(`module Feature\n${source}`, file)
    const hit = hits.find((candidate) => candidate.token === token)
    assert.deepEqual(
      hit && { file: hit.file, line: hit.line, token: hit.token },
      { file, line: 2, token },
    )
  }

  const manualMerge = [
    'module FeatureHistory',
    'let merge streams =',
    '    streams',
    '    |> List.collect snd',
    '    |> List.sortBy (fun envelope -> envelope.EventId)',
    '    |> List.fold apply empty',
  ].join('\n')
  const manualHit = scanFeatureHistoryLoop(
    manualMerge,
    'src/Wanxiangshu/Repository/Feature/ManualMerge.fs',
  ).find((hit) => hit.token === 'manual merge')
  assert.deepEqual(
    manualHit && { line: manualHit.line, token: manualHit.token },
    { line: 5, token: 'manual merge' },
  )
})
test('WHAT[durable-events-019] diagnostic history fields do not turn unrelated graph collection into durable replay', () => {
  const source = [
    'type Snapshot = { History: Transition list; Active: Wait list }',
    'let ownerKey owner = owner.Identity |> List.sortBy fst',
    'let branches snapshot =',
    '    snapshot.Active |> List.collect followProducer',
    'let orderedBranches snapshot =',
    '    branches snapshot |> List.sortBy ownerKey',
  ].join('\n')
  assert.deepEqual(
    scanFeatureHistoryLoop(source, 'src/Wanxiangshu/Feature/Diagnostics.fs'),
    [],
  )
})
test('WHAT[durable-events-019] history merge detection binds collection and ordering to the same function', () => {
  const source = [
    'let count history = List.length history',
    'let orderedPaths directories =',
    '    directories |> List.collect paths |> List.sortBy pathName',
    'let merge streams =',
    '    let events = streams |> List.collect snd',
    '    events |> List.sortBy eventKey',
  ].join('\n')
  assert.deepEqual(
    scanFeatureHistoryLoop(source, 'src/Wanxiangshu/Feature/Collections.fs')
      .map(({ line, token }) => ({ line, token })),
    [{ line: 6, token: 'manual merge' }],
  )
})
test('WHAT[durable-events-019] overrides default members and constructor effects cannot merge business history', () => {
  for (const binding of ['override _.BuildCurrent() =', 'default _.BuildCurrent() =', 'do']) {
    const source = [
      'type FeatureProjection(history) =',
      '    inherit BaseProjection()',
      `    ${binding}`,
      '        history |> List.collect snd |> List.sortBy eventKey |> consume',
    ].join('\n')
    assert.deepEqual(
      scanFeatureHistoryLoop(source, 'src/Wanxiangshu/Feature/Projection.fs')
        .map(({ line, token }) => ({ line, token })),
      [{ line: 4, token: 'manual merge' }],
      binding,
    )
  }
})
test('WHAT[durable-events-019] a called local collection helper does not hide a history merge', () => {
  const source = [
    'let flatten streams = List.collect snd streams',
    'let ordered streams = flatten streams |> List.sortBy eventKey',
  ].join('\n')
  assert.deepEqual(
    scanFeatureHistoryLoop(source, 'src/Wanxiangshu/Feature/Collections.fs')
      .map(({ line, token }) => ({ line, token })),
    [{ line: 2, token: 'manual merge' }],
  )
})
test('WHAT[durable-events-019] history merge follows transitive and mutually recursive named helpers', () => {
  const sources = [
    [
      'let flatten streams = List.collect snd streams',
      'let forward values = flatten values',
      'let ordered values = forward values |> List.sortBy eventKey',
    ],
    [
      'let rec ordered streams = flatten streams |> List.sortBy eventKey',
      'and flatten streams =',
      '    if List.isEmpty streams then ordered streams else List.collect snd streams',
    ],
  ]
  for (const [index, source] of sources.entries()) {
    assert.deepEqual(
      scanFeatureHistoryLoop(source.join('\n'), 'src/Wanxiangshu/Feature/Collections.fs')
        .map(({ line, token }) => ({ line, token })),
      [{ line: index === 0 ? 3 : 1, token: 'manual merge' }],
    )
  }
})
test('WHAT[durable-events-019] history merge follows explicitly called helpers in same-file modules', () => {
  for (const called of ['Helpers.flatten', 'Feature.Helpers.flatten']) {
    const source = [
      'module Feature',
      'module Helpers =',
      '    let flatten values = List.collect id values',
      `let ordered streams = ${called} streams |> List.sortBy eventKey`,
    ].join('\n')
    assert.deepEqual(
      scanFeatureHistoryLoop(source, 'src/Wanxiangshu/Feature/Collections.fs')
        .map(({ line, token }) => ({ line, token })),
      [{ line: 4, token: 'manual merge' }],
      called,
    )
  }
})
test('WHAT[durable-events-019] lambda parameters shadow helpers only inside their own expression', () => {
  const prefix = ['let flatten values = List.collect id values', 'let ordered streams =']
  for (const body of [
    ['    (fun flatten -> streams |> flatten |> List.sortBy eventKey) id'],
    ['    (fun flatten ->', '        streams |> flatten |> List.sortBy eventKey) id'],
  ]) {
    assert.deepEqual(
      scanFeatureHistoryLoop([...prefix, ...body].join('\n'), 'src/Wanxiangshu/Feature/Collections.fs'),
      [],
    )
  }
  const outside = [...prefix,
    '    ignore ((fun flatten -> flatten streams) id)',
    '    flatten streams |> List.sortBy eventKey',
  ].join('\n')
  assert.deepEqual(
    scanFeatureHistoryLoop(outside, 'src/Wanxiangshu/Feature/Collections.fs')
      .map(({ line, token }) => ({ line, token })),
    [{ line: 4, token: 'manual merge' }],
  )
})
test('WHAT[durable-events-019] a value parameter does not resolve to a same-named module helper', () => {
  for (const body of [
    'let ordered Helpers streams = Helpers.flatten streams |> List.sortBy eventKey',
    'let ordered streams = (fun Helpers -> Helpers.flatten streams |> List.sortBy eventKey) source',
  ]) {
    const source = [
      'module Helpers =',
      '    let flatten values = List.collect id values',
      body,
    ].join('\n')
    assert.deepEqual(scanFeatureHistoryLoop(source, 'src/Wanxiangshu/Feature/Collections.fs'), [])
  }
})
test('WHAT[durable-events-019] unreferenced and shadowed helpers do not supply history collection', () => {
  const sources = [
    [
      'let flatten streams = List.collect snd streams',
      'let ordered history = history |> List.sortBy eventKey',
    ],
    [
      'let flatten streams = List.collect snd streams',
      'let ordered flatten history = flatten history |> List.sortBy eventKey',
    ],
    [
      'let flatten streams = List.collect snd streams',
      'let ordered source history = source.flatten history |> List.sortBy eventKey',
    ],
    [
      'let flatten streams = List.collect snd streams',
      'let ordered history =',
      '    log "flatten" // flatten history',
      '    log (* flatten history *) "diagnostic"',
      '    history |> List.sortBy eventKey',
    ],
    [
      'let ordered history =',
      '    let flatten streams = List.collect snd streams',
      '    history |> List.sortBy eventKey',
    ],
    [
      'module Collections =',
      '    let flatten streams = List.collect snd streams',
      'module Diagnostics =',
      '    let flatten values = values',
      '    let ordered history = flatten history |> List.sortBy eventKey',
    ],
  ]
  for (const source of sources) {
    assert.deepEqual(
      scanFeatureHistoryLoop(source.join('\n'), 'src/Wanxiangshu/Feature/Collections.fs'),
      [],
      source.join('\n'),
    )
  }
})
test('WHAT[durable-events-019] history collection does not cross unrelated member boundaries', () => {
  for (const binding of ['override _.Ordered(history) =', 'default _.Ordered(history) =', 'do']) {
    const source = [
      'type FeatureProjection(history) =',
      '    member _.Flatten(streams) = List.collect snd streams',
      `    ${binding}`,
      '        history |> List.sortBy eventKey |> consume',
    ].join('\n')
    assert.deepEqual(scanFeatureHistoryLoop(source, 'src/Wanxiangshu/Feature/Projection.fs'), [], binding)
  }
})
test('WHAT[durable-events-019] feature-local NDJSON SQLite and private stores are forbidden', () => {
  const file = 'src/Wanxiangshu/Repository/Feature/PrivateStore.fs'
  const source = [
    'module FeatureStorage',
    'let journal = "feature-history.ndjson"',
    'let connection = SQLite.open "feature.sqlite"',
    'let store = PrivateEventStore(connection)',
  ].join('\n')
  const hits = scanPrivateDurableSubstrate(source, file)
  assert.deepEqual(
    hits.map(({ file, line, token }) => ({ file, line, token })),
    [
      { file, line: 2, token: '.ndjson' },
      { file, line: 3, token: 'SQLite' },
      { file, line: 4, token: 'PrivateEventStore' },
    ],
  )
})
test('WHAT[durable-events-019] durable file database and custom-store writer capabilities are owner-bound', () => {
  const file = 'src/Wanxiangshu/Repository/Feature/CustomHistory.fs'
  const fixtures = [
    ['System.IO.File.AppendAllText("feature-history.log", payload)', 'System.IO.File.AppendAllText'],
    ['File.WriteAllText("feature-history.log", payload)', 'File.WriteAllText'],
    ['use writer = StreamWriter(path)', 'StreamWriter('],
    ['let connection: DbConnection = openDatabase ()', 'DbConnection'],
    ['let store = CustomStore(path)', 'CustomStore'],
  ]

  for (const [source, token] of fixtures) {
    const hits = scanPrivateDurableSubstrate(source, file)
    assert.ok(hits.some((hit) => hit.token === token), `${token} must be RED outside owners`)
  }

  const owner = 'src/Wanxiangshu/Persistence/EventStore/RetentionSurface.fs'
  assert.equal(
    scanPrivateDurableSubstrate(fixtures.map(([source]) => source).join('\n'), owner).length,
    0,
    'the exact physical owner may use durable writer capabilities',
  )
})
test('WHAT[durable-events-019] canonical integrator and exact physical or proof readers are allowed', () => {
  const reader = [
    'let streams = ProcessEventLog.readStreams commonDir',
    'let event = (store: IEventStore).TryEvent eventId',
    'let streamHeads = (store: IEventStore).TryHeads streamId',
    'let allHeads = (store: IEventStore).AllHeads()',
    'let exposed = EventStore.Surface.read(handle, eventId)',
    'let exposedHeads = EventStore.Surface.heads(handle, streamId)',
  ].join('\n')
  assert.equal(
    scanFeatureHistoryLoop(
      reader,
      'src/Wanxiangshu/Persistence/EventStore/IntegratorEngine.fs',
    ).length,
    0,
  )

  assert.deepEqual([...PHYSICAL_HISTORY_OBSERVER_PATHS], [
    'src/Wanxiangshu/Persistence/EventStore/ProcessEventLog.fs',
    'src/Wanxiangshu/Persistence/EventStore/EventKWayMerge.fs',
    'src/Wanxiangshu/Persistence/EventStore/RetentionSurface.fs',
    'src/Wanxiangshu/Persistence/EventStore/MergeSurface.fs',
    'src/Wanxiangshu/Verification/TemporalSurface.fs',
    'src/Wanxiangshu/Verification/JournalPortObservationSurface.fs',
  ])
  for (const file of PHYSICAL_HISTORY_OBSERVER_PATHS) {
    assert.equal(scanFeatureHistoryLoop(reader, file).length, 0, file)
  }

  assert.deepEqual([...CANONICAL_EVENT_READER_OWNER_PATHS], [
    'src/Wanxiangshu/Persistence/EventStore/Store.fs',
    'src/Wanxiangshu/Persistence/EventStore/Surface.fs',
    'src/Wanxiangshu/OpenCode/Host/WorkspaceEventStore.fs',
    'src/Wanxiangshu/Persistence/Journal/EventStoreJournalWriter.fs',
    'src/Wanxiangshu/Verification/EventStoreWriterSurface.fs',
    'src/Wanxiangshu/Verification/JournalPortObservationSurface.fs',
  ])
  const eventStoreReaders = reader.split('\n').slice(1).join('\n')
  for (const file of CANONICAL_EVENT_READER_OWNER_PATHS) {
    assert.equal(scanFeatureHistoryLoop(eventStoreReaders, file).length, 0, file)
  }

  const substrate = 'let physicalLine = "writer.ndjson"'
  const substrateOwners = [
    'src/Wanxiangshu/Persistence/EventStore/RetentionSurface.fs',
    'src/Wanxiangshu/OpenCode/Host/WorkspaceEventStore.fs',
    'src/Wanxiangshu/Persistence/Journal/EventStoreJournalWriter.fs',
    'src/Wanxiangshu/Verification/EventStoreWriterSurface.fs',
  ]
  for (const file of substrateOwners) {
    assert.equal(scanPrivateDurableSubstrate(substrate, file).length, 0, file)
  }
  assert.ok(
    scanPrivateDurableSubstrate(
      substrate,
      'src/Wanxiangshu/Repository/Knowledge/Casebook/Surface.fs',
    ).length > 0,
    'the same substrate token remains forbidden outside exact physical/proof owners',
  )

  const physicalMerge = [
    'let merge streams =',
    '    streams',
    '    |> List.collect snd',
    '    |> List.sortBy eventKey',
  ].join('\n')
  assert.equal(
    scanFeatureHistoryLoop(
      physicalMerge,
      'src/Wanxiangshu/Persistence/EventStore/EventKWayMerge.fs',
    ).length,
    0,
  )
  assert.ok(
    scanFeatureHistoryLoop(
      physicalMerge,
      'src/Wanxiangshu/Repository/Feature/EventKWayMerge.fs',
    ).some((hit) => hit.token === 'manual merge'),
    'the exact physical merge path does not grant similarly named feature files ownership',
  )

  assert.ok(
    scanFeatureHistoryLoop(
      reader,
      'src/Wanxiangshu/Verification/AnotherProbe.fs',
    ).length > 0,
    'verification observation is granted to exact probes, not the whole directory',
  )
})
test('WHAT[durable-events-019] production tree has no feature history loop or private substrate', () => {
  const entries = collectProductionEntries()
  const violations = scanFiles(entries).filter(
    (v) => v.id === 'feature-history-loop' || v.id === 'private-durable-substrate',
  )
  assert.deepEqual(
    violations,
    [],
    violations.map((v) => `[${v.id}] ${v.file}:${v.line} ${v.token} ${v.label}`).join('\n'),
  )
})
}
