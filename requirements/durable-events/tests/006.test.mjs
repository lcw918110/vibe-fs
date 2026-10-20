import test from 'node:test'

{
  const { default: assert } = await import('node:assert/strict')
  const { mkdtempSync, readFileSync, realpathSync, rmSync } = await import('node:fs')
  const { tmpdir } = await import('node:os')
  const { default: path } = await import('node:path')
  const { randomUUID } = await import('node:crypto')
  const { fileURLToPath } = await import('node:url')
  const { runVerificationToolProbe } = await import('../../../scripts/lib/verification-tool-probe.mjs')
  const codec = await import('../../../dist/Persistence/EventStore/CodecSurface.js')
  const child = fileURLToPath(new URL('./support/append-lock-acquire-child.mjs', import.meta.url))
  for (const scenario of ['mkdir-eacces', 'mkdir-eio', 'busy-success', 'busy-eacces']) {
    test(`WHAT[durable-events-006] actual_lock_${scenario}_distinguishes_contention_from_unattempted_io`, async t => {
      const root = realpathSync(mkdtempSync(path.join(tmpdir(), 'append-lock-acquire-')))
      const commonDir = path.join(root, '.Herdchestra')
      const sourceWriterId = randomUUID()
      const sourceFile = path.join(commonDir, 'wanxiangshu', 'events', `${sourceWriterId}-0000.ndjson`)
      const seed = { id: 'e'.repeat(40), stream: 'proof/lock-acquire/old', type: 'JobRequested',
        parents: [], payload: { text: '原有事实' }, payloadRefs: [] }
      const incoming = { id: 'f'.repeat(40), stream: 'proof/lock-acquire/new', type: 'JobRequested',
        parents: [], payload: { text: '新事实-é' }, payloadRefs: [] }
      const request = { sourceWriterId, seed, incoming }
      const env = { ...process.env }
      delete env.NODE_TEST_CONTEXT
      const probe = async (mode, writer) => {
        try {
          return JSON.parse(await runVerificationToolProbe(process.execPath,
            [child, mode, commonDir, writer, scenario, JSON.stringify(request)], { cwd: root, env, signal: t.signal }))
        } catch (error) {
          if (typeof error?.stderr === 'string' && typeof error.message === 'string') error.message += `\n${error.stderr}`
          throw error
        }
      }
      try {
        const measured = await probe('measure', sourceWriterId)
        const cold = await probe('cold', randomUUID())
        assert.ok(Number.isInteger(measured.pid) && measured.pid > 0)
        assert.ok(Number.isInteger(cold.pid) && cold.pid > 0 && cold.pid !== measured.pid)
        assert.deepEqual(cold.views, measured.views)
        assert.equal(cold.bytesBase64, measured.bytesBase64)
        assert.equal(cold.writerCreated, false)
        t.diagnostic(JSON.stringify({ scenario, measurePid: measured.pid, coldPid: cold.pid,
          faultKind: scenario === 'busy-success' ? null : 'controlled one-hop native mkdir error', ...measured.observed }))
        const succeeds = scenario === 'busy-success'
        assert.deepEqual(measured.result, succeeds ? { ok: true, cuts: [] } : {
          ok: false, error: {
            code: 'AppendNotAttempted', phase: 'GateAcquire', causeIsInjected: true,
            causeCode: scenario === 'mkdir-eio' ? 'EIO' : 'EACCES', causeSyscall: 'mkdir',
            causePath: path.join(commonDir, 'wanxiangshu.lock'),
            requested: [incoming], prepared: null, cleanupFailures: [], priorRejection: null,
          },
        })
        const expectedBytes = Buffer.from(codec.encode(seed) + (succeeds ? codec.encode(incoming) : ''), 'utf8')
        assert.deepEqual(Buffer.from(measured.beforeBase64, 'base64'), Buffer.from(codec.encode(seed), 'utf8'))
        assert.deepEqual(Buffer.from(measured.bytesBase64, 'base64'), expectedBytes)
        assert.deepEqual(readFileSync(sourceFile), expectedBytes)
        assert.deepEqual(measured.views, [
          { event: seed, head: seed.id, heads: [seed.id] },
          succeeds ? { event: incoming, head: incoming.id, heads: [incoming.id] } : { event: null, head: null, heads: [] },
        ])
        assert.deepEqual({ append: measured.observed.append, fsync: measured.observed.fsync,
          close: measured.observed.close, release: measured.observed.release, injected: measured.observed.injected },
        { append: succeeds ? 1 : 0, fsync: succeeds ? 1 : 0, close: succeeds ? 1 : 0,
          release: succeeds ? 1 : 0, injected: succeeds ? 0 : 1 })
        assert.equal(measured.appendBeforeRelease, false)
        if (scenario.startsWith('busy-')) {
          assert.ok(measured.observed.busy > 0, 'Observe the original proper-lockfile ELOCKED without changing retry decisions')
          assert.deepEqual(measured.busyBeforeRelease, {
            settled: false, append: 0, lockExists: true, bytesBase64: measured.beforeBase64,
          })
        } else {
          assert.equal(measured.busyBeforeRelease, null)
        }
      } finally {
        rmSync(root, { recursive: true, force: true })
      }
    })
  }
}

{
  const { default: assert } = await import('node:assert/strict')
  const { existsSync, mkdtempSync, readFileSync, realpathSync, rmSync } = await import('node:fs')
  const { tmpdir } = await import('node:os')
  const { default: path } = await import('node:path')
  const { randomUUID } = await import('node:crypto')
  const { fileURLToPath } = await import('node:url')
  const { runVerificationToolProbe } = await import('../../../scripts/lib/verification-tool-probe.mjs')
  const child = fileURLToPath(new URL('./support/append-boundary-child.mjs', import.meta.url))
  const seed = {
    id: 'a'.repeat(40), stream: 'proof/append-boundary/old', type: 'JobRequested',
    parents: [], payload: { text: '独立旧事实' }, payloadRefs: [],
  }
  const canonical = value => {
    if (Array.isArray(value)) return value.map(canonical)
    if (value !== null && typeof value === 'object') {
      return Object.fromEntries(Object.keys(value).sort().map(key => [key, canonical(value[key])]))
    }
    return value
  }
  const eventLine = event => JSON.stringify(canonical({
    event_id: event.id, event_type: event.type, parents: event.parents,
    payload: event.payload, payload_refs: event.payloadRefs, stream_id: event.stream,
  })) + '\n'

  for (const scenario of ['gate-directory', 'before-directory', 'mixed-release', 'cut-release', 'validation-release', 'preparation-random']) {
    test(`WHAT[durable-events-006] actual_${scenario}_preserves_preparation_rejection_and_independent_cold_facts`, async t => {
      const root = realpathSync(mkdtempSync(path.join(tmpdir(), 'append-boundary-')))
      const commonDir = path.join(root, '.Herdchestra')
      const writerId = randomUUID()
      const sourceFile = path.join(commonDir, 'wanxiangshu', 'events', `${writerId}-0000.ndjson`)
      const cut = scenario === 'cut-release'
      const validation = scenario === 'validation-release'
      const preparation = scenario === 'preparation-random'
      const noWrite = scenario === 'gate-directory' || scenario === 'before-directory' || validation || preparation
      const incoming = {
        id: (cut || preparation ? 'c' : validation ? 'd' : 'b').repeat(40),
        stream: 'proof/append-boundary/new',
        type: cut || preparation ? 'InspectorCaseCaptured' : validation ? 'UnregisteredAppendBoundaryType' : 'JobRequested',
        parents: [], payload: cut || preparation ? {} : { text: '新事实-é', detail: { ready: true } }, payloadRefs: [],
      }
      const request = { incoming, seed, sourceWriterId: writerId }
      const env = { ...process.env }
      delete env.NODE_TEST_CONTEXT
      const probe = async (mode, writer, input) => {
        try {
          return JSON.parse(await runVerificationToolProbe(process.execPath,
            [child, mode, commonDir, writer, scenario, JSON.stringify(input)], { cwd: root, env, signal: t.signal }))
        } catch (error) {
          if (typeof error?.stderr === 'string' && typeof error.message === 'string') error.message += `\n${error.stderr}`
          throw error
        }
      }
      try {
        const measured = await probe('measure', writerId, request)
        assert.ok(Number.isInteger(measured.pid) && measured.pid > 0)
        assert.deepEqual(measured.observed, {
          directoryFailures: scenario.endsWith('directory') ? 1 : 0,
          random: preparation ? 1 : 0,
          append: noWrite ? 0 : 1, fsync: noWrite ? 0 : 1, close: noWrite ? 0 : 1,
          release: scenario === 'gate-directory' ? 0 : 1, injected: 1,
        })
        assert.equal(measured.writerCreated, !noWrite)
        assert.equal(existsSync(sourceFile), !noWrite)
        if (noWrite) {
          assert.deepEqual(measured.facts, [])
        } else if (cut) {
          assert.equal(measured.facts.length, 2)
          assert.deepEqual(measured.facts[0], incoming)
          const reset = measured.facts[1]
          assert.equal(reset.type, 'ProjectionCutTail')
          assert.deepEqual(reset.parents, [incoming.id])
          assert.equal(reset.payload.failed_event_id, incoming.id)
          assert.equal(reset.payload.rule, 'Casebook')
          assert.ok(reset.payload.reason.length > 0)
          assert.equal(typeof reset.payload.reset_json, 'string')
        } else {
          assert.deepEqual(measured.facts, [seed, incoming])
        }
        const expectedBytes = Buffer.from(measured.facts.map(eventLine).join(''), 'utf8')
        assert.deepEqual(Buffer.from(measured.bytesBase64, 'base64'), expectedBytes)
        assert.deepEqual(Buffer.from(measured.beforeBase64, 'base64'),
          scenario === 'mixed-release' ? Buffer.from(eventLine(seed), 'utf8') : Buffer.alloc(0))
        assert.deepEqual(measured.views, noWrite ? [{ event: null, head: null, heads: [] }]
          : measured.facts.map(fact => ({ event: fact, head: fact.id, heads: [fact.id] })))
        if (!noWrite) assert.deepEqual(readFileSync(sourceFile), expectedBytes)

        const cold = await probe('cold', randomUUID(), { ...request, facts: measured.facts, bytesBase64: measured.bytesBase64 })
        assert.ok(Number.isInteger(cold.pid) && cold.pid > 0 && cold.pid !== measured.pid)
        assert.equal(cold.writerCreated, false)
        assert.deepEqual(cold.views, measured.views)
        assert.deepEqual(Buffer.from(cold.bytesBase64, 'base64'), expectedBytes)
        assert.equal(existsSync(sourceFile), !noWrite)
        if (!noWrite) assert.deepEqual(readFileSync(sourceFile), expectedBytes)
        t.diagnostic(JSON.stringify({ scenario, measurePid: measured.pid, coldPid: cold.pid, physicalPreconditions: true, ...measured.observed }))

        assert.equal(measured.settled, true, `Physical facts and independent cold replay passed: ${JSON.stringify(measured)}`)
        const cuts = cut ? [{
          failedEventId: incoming.id, rule: 'Casebook',
          cutEventId: measured.facts[1].id, reason: measured.facts[1].payload.reason,
        }] : []
        const prepared = scenario === 'gate-directory' || validation || preparation ? null
          : { durableEvents: cut ? measured.facts : [incoming], cuts }
        assert.deepEqual(measured.result, {
          ok: false,
          error: {
            code: noWrite ? 'AppendNotAttempted' : 'CommitUnknown',
            phase: scenario === 'gate-directory' ? 'GateAcquire'
              : scenario === 'before-directory' ? 'BeforePhysicalAppend' : preparation ? 'Preparation' : 'StoreRelease',
            causeIsInjected: true, cleanupFailures: [],
            requested: scenario === 'mixed-release' ? [seed, incoming] : [incoming],
            prepared,
            priorRejection: validation ? {
              code: 'StorageInvalid', error: { code: 'UnknownEventType', eventType: incoming.type },
            } : null,
          },
        })
      } finally {
        rmSync(root, { recursive: true, force: true })
      }
    })
  }
}

{
  const { default: assert } = await import('node:assert/strict')
  const { mkdtempSync, readFileSync, realpathSync, rmSync } = await import('node:fs')
  const { tmpdir } = await import('node:os')
  const { default: path } = await import('node:path')
  const { randomUUID } = await import('node:crypto')
  const { fileURLToPath } = await import('node:url')
  const { runVerificationToolProbe } = await import('../../../scripts/lib/verification-tool-probe.mjs')
  await import('../../../dist/Verification/JournalPortObservationSurface.js')
  const child = fileURLToPath(new URL('./support/journal-settlement-child.mjs', import.meta.url))
  for (const scenario of ['initialization-release', 'business-release', 'initialization-fsync', 'business-fsync']) {
    test(`WHAT[durable-events-006] actual_journal_${scenario}_keeps_business_fate_poison_and_waiters_separate`, async t => {
      const root = realpathSync(mkdtempSync(path.join(tmpdir(), 'journal-settlement-')))
      const commonDir = path.join(root, '.Herdchestra')
      const writerId = randomUUID()
      const sourceFile = path.join(commonDir, 'wanxiangshu', 'events', `${writerId}-0000.ndjson`)
      const env = { ...process.env }
      delete env.NODE_TEST_CONTEXT
      const probe = async (mode, writer, request) => JSON.parse(await runVerificationToolProbe(process.execPath,
        [child, mode, commonDir, writer, scenario, JSON.stringify(request)], { cwd: root, env, signal: t.signal }))
      try {
        const measured = await probe('measure', writerId, {})
        const initialization = scenario.startsWith('initialization-')
        const fsync = scenario.endsWith('-fsync')
        const count = initialization ? 1 : 2
        assert.deepEqual(measured.observed, { append: count, fsync: count, close: count, release: count, injected: 1 })
        assert.equal(measured.lockReleased, true)
        assert.equal(measured.ids.length, count)
        assert.equal(measured.ids[0], measured.initial.initEventId)
        assert.equal(measured.liveInit, !(initialization && fsync))
        assert.equal(measured.liveBusiness, !initialization && !fsync)
        assert.equal(measured.initial.revision, '0')
        assert.equal(measured.afterFirst.revision, '0', 'Unknown must not publish a journal revision')
        assert.equal(measured.afterSecond.revision, '0')
        assert.equal(measured.afterFirst.waitCompleted, false, 'Unknown must not wake a committed-change waiter')
        assert.equal(measured.afterSecond.waitCompleted, false)
        assert.equal(measured.afterFirst.poisoned, true)
        assert.equal(measured.afterFirst.localSeq, '2')
        assert.equal(measured.afterFirst.lastCommittedLocalSeq, initialization ? '0' : '1')
        const cold = await probe('cold', randomUUID(), { ids: measured.ids, sourceFile })
        assert.notEqual(cold.pid, measured.pid)
        assert.deepEqual(cold.present, Array(count).fill(true))
        assert.equal(cold.writerCreated, false)
        assert.equal(cold.sourceBytes, measured.bytesBase64)
        assert.equal(readFileSync(sourceFile, 'base64'), measured.bytesBase64)
        t.diagnostic(JSON.stringify({ scenario, measurePid: measured.pid, coldPid: cold.pid, physicalPreconditions: true, ...measured.observed }))
        assert.equal(measured.first.kind, initialization ? 'NotAttempted' : 'CommitUnknown')
        assert.equal(measured.first.error.code, 'CommitUnknown')
        assert.equal(measured.first.error.phase, fsync ? 'DurabilityBarrier' : 'StoreRelease')
        assert.equal(measured.firstCauseSame, true)
        assert.equal(measured.secondCauseSame, true)
        const failedId = initialization ? measured.initial.initEventId : measured.first.error.eventId
        assert.equal(measured.first.error.failedEventId, failedId)
        assert.deepEqual(measured.first.error.requestedIds, [failedId])
        assert.deepEqual(measured.first.error.preparedIds, [failedId])
        assert.deepEqual(measured.first.error.cutIds, [])
        assert.deepEqual(measured.first.error.cleanupFailures, [])
        assert.equal(measured.second.kind, 'NotAttempted')
        assert.equal(measured.second.error.failedEventId, failedId)
        assert.notEqual(measured.second.error.eventId, measured.first.error.eventId)
        assert.notEqual(measured.first.error.eventId, measured.initial.initEventId)
        if (!initialization) assert.equal(measured.ids[1], measured.first.error.eventId)
      } finally {
        rmSync(root, { recursive: true, force: true })
      }
    })
  }
}

{
const { default: assert } = await import("node:assert/strict");
const { readFile, readdir } = await import("node:fs/promises");
const { mkdtempSync, rmSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join } = await import("node:path");
const { default: test } = await import("node:test");
const eventStore = await import("../../../dist/Persistence/EventStore/Surface.js");

const id = (n) => n.toString(16).padStart(40, '0')
const event = (id, parents = []) => ({
  id,
  stream: 'append/law',
  type: 'JobRequested',
  parents,
  payload: { id },
  payloadRefs: [],
})
const withTemp = (fn) => {
  const base = mkdtempSync(join(tmpdir(), 'wxs-append-law-'))
  return fn(base)
}

test('WHAT[durable-events-006] duplicate_same_identity_is_idempotent_but_collision_is_rejected', async () => {
  const dir = withTemp((base) => base)
  const store = eventStore.create(dir, 'collision-law')
  try {
    const same = event(id(1))
    assert.equal((await eventStore.append(store, [same])).ok, true)
    assert.equal((await eventStore.append(store, [same])).ok, true)
    const conflict = { ...same, payload: { id: 'different' } }
    assert.equal((await eventStore.append(store, [conflict])).ok, false)
  } finally {
    eventStore.dispose(store)
    rmSync(dir, { recursive: true, force: true })
  }
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
  payload: { SessionId: 'ses_es_writer', TextRef: 'blob-term', TextDigest: 'sha-term', ProviderRun: 'run-term' },
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

test('WHAT[durable-events-006] append_adds_one_local_line_and_Current_is_already_integrated', async () => {
  await withRepo('journal-append-proof', async (commonDir) => {
    const booted = mustOk(await journal.JournalSurface_bootWithWriterId(commonDir, 'journal-append-proof', 'rt_es_append', 4242, '2026-04-01T00:00:00Z'), 'boot')
    const file = join(commonDir, 'wanxiangshu', 'events', 'journal-append-proof-0000.ndjson')

    assert.equal(existsSync(file), false)
    const appended = mustOk(
      await journal.JournalSurface_appendAgent(
        booted.journal,
        { kind: 'Session', session: 'ses_es_writer' },
        null,
        CLOSED_AGENT,
      ),
      'append',
    )

    const after = readFileSync(file, 'utf8')
    assert.equal(after.trim().split('\n').length, 2, 'first business append writes RuntimeStarted then the business fact')
    assert.ok(appended.projection)
    journal.JournalSurface_dispose(booted.journal)
  })
})
}

{
const { default: assert } = await import('node:assert/strict')
const { mkdtempSync, readFileSync, realpathSync, rmSync } = await import('node:fs')
const { tmpdir } = await import('node:os')
const { default: path } = await import('node:path')
const { randomUUID } = await import('node:crypto')
const { fileURLToPath } = await import('node:url')
const { runVerificationToolProbe } = await import('../../../scripts/lib/verification-tool-probe.mjs')
const childPath = fileURLToPath(new URL('./support/append-settlement-child.mjs', import.meta.url))
const incoming = {
  id: '7'.repeat(40),
  stream: 'proof/append-settlement',
  type: 'JobRequested',
  parents: [],
  payload: { text: '真实落盘-é', detail: { ready: true } },
  payloadRefs: [],
}
const canonicalLine = '{"event_id":"7777777777777777777777777777777777777777","event_type":"JobRequested","parents":[],"payload":{"detail":{"ready":true},"text":"真实落盘-é"},"payload_refs":[],"stream_id":"proof/append-settlement"}\n'

async function settlementChild(mode, commonDir, writerId, scenario, request, signal) {
  const env = { ...process.env }
  delete env.NODE_TEST_CONTEXT
  try {
    return JSON.parse(await runVerificationToolProbe(process.execPath, [
      childPath, mode, commonDir, writerId, scenario, JSON.stringify(request),
    ], { cwd: path.dirname(commonDir), env, signal }))
  } catch (error) {
    if (error?.stderr && typeof error.message === 'string') error.message += `\n${error.stderr}`
    throw error
  }
}

for (const scenario of ['normal', 'fresh-release', 'duplicate-release', 'empty-release', 'append', 'open', 'fsync', 'close', 'current-before', 'current-after', 'fsync-close-release']) {
  test(`WHAT[durable-events-006] actual_append_settlement_preserves_${scenario}_physical_facts_and_cause`, async t => {
    const root = realpathSync(mkdtempSync(path.join(tmpdir(), 'append-settlement-')))
    const commonDir = path.join(root, '.Herdchestra')
    const writerId = randomUUID()
    const writerFile = path.join(commonDir, 'wanxiangshu', 'events', `${writerId}-0000.ndjson`)
    const request = { incoming, canonicalLine, sourceWriterId: writerId }
    const expectedBytes = Buffer.from(canonicalLine, 'utf8')
    const noNewWrite = scenario === 'duplicate-release' || scenario === 'empty-release'
    const currentFault = scenario === 'current-before' || scenario === 'current-after'
    const multipleFaults = scenario === 'fsync-close-release'
    const beforeBarrier = scenario === 'append' || scenario === 'open'
    const currentUnchanged = beforeBarrier || scenario === 'fsync' || scenario === 'close' || scenario === 'current-before' || multipleFaults
    try {
      const measured = await settlementChild('measure', commonDir, writerId, scenario, request, t.signal)
      assert.ok(Number.isInteger(measured.pid) && measured.pid > 0)
      assert.equal(measured.scenario, scenario)
      assert.deepEqual(measured.observed, {
        appendCalls: noNewWrite ? 0 : 1,
        fsyncCalls: noNewWrite || beforeBarrier ? 0 : 1,
        syncedCloseCalls: noNewWrite || beforeBarrier ? 0 : 1,
        releaseCalls: 1,
        injectedCalls: multipleFaults ? 3 : scenario === 'normal' || currentFault ? 0 : 1,
      })
      assert.deepEqual(Buffer.from(measured.bytesBase64, 'base64'), expectedBytes)
      assert.deepEqual(readFileSync(writerFile), expectedBytes)
      assert.deepEqual(Buffer.from(measured.beforeBase64, 'base64'), noNewWrite ? expectedBytes : Buffer.alloc(0))
      assert.deepEqual(measured.event, currentUnchanged ? null : incoming)
      assert.equal(measured.head, currentUnchanged ? null : incoming.id)
      assert.deepEqual(measured.heads, currentUnchanged ? [] : [incoming.id])
      assert.equal(measured.lockReleased, true)

      const coldWriterId = randomUUID()
      const cold = await settlementChild('cold', commonDir, coldWriterId, scenario, request, t.signal)
      assert.ok(Number.isInteger(cold.pid) && cold.pid > 0 && cold.pid !== measured.pid)
      assert.equal(cold.writerId, coldWriterId)
      assert.deepEqual(cold.event, incoming)
      assert.equal(cold.head, incoming.id)
      assert.deepEqual(cold.heads, [incoming.id])
      assert.deepEqual(Buffer.from(cold.bytesBase64, 'base64'), expectedBytes)
      assert.deepEqual(readFileSync(writerFile), expectedBytes)
      t.diagnostic(JSON.stringify({ scenario, measurePid: measured.pid, coldPid: cold.pid, physicalPreconditions: true, ...measured.observed }))

      assert.equal(measured.settled, true, `Physical preconditions passed; append must return a typed settlement: ${JSON.stringify(measured)}`)
      if (scenario === 'normal') {
        assert.deepEqual(measured.result, { ok: true, cuts: [] })
      } else {
        assert.deepEqual(measured.result, {
          ok: false,
          error: {
            code: noNewWrite ? 'NoNewWriteReleaseFailed' : 'CommitUnknown',
            phase: currentFault ? 'CurrentCommit' : scenario === 'append' ? 'PhysicalAppend'
              : scenario === 'open' ? 'DurabilityOpen' : scenario === 'close' ? 'DurabilityClose'
                : scenario === 'fsync' || multipleFaults ? 'DurabilityBarrier' : 'StoreRelease',
            causeIsInjected: true,
            cleanupFailures: multipleFaults ? [
              { phase: 'DurabilityClose', causeIsInjected: true },
              { phase: 'StoreRelease', causeIsInjected: true },
            ] : [],
            requested: scenario === 'empty-release' ? [] : [incoming],
            prepared: noNewWrite ? null : { durableEvents: [incoming], cuts: [] },
            priorRejection: null,
          },
        })
      }
    } finally {
      rmSync(root, { recursive: true, force: true })
    }
  })
}
}
