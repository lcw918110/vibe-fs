import test from 'node:test'
import { integrationTest } from '../../verification-system/tests/support/tier-gate.mjs'

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { existsSync, readdirSync, readFileSync } = await import("node:fs");
const { join, resolve } = await import("node:path");
const eventCodec = await import("../../../dist/Persistence/EventStore/CodecSurface.js");
const { readCompileShardInventory } = await import("../../../scripts/lib/compile-shards.mjs");
const { buildSubsystemInventory } = await import("../../../scripts/checks/subsystems.mjs");

const ROOT = resolve(import.meta.dirname, '../../..')
const SOURCE_ROOT = join(ROOT, 'src/Wanxiangshu')
const event = ({
  id = '1111111111111111111111111111111111111111',
  payload = { state: 'open' },
} = {}) => ({
  id,
  stream: 'proof/canonical-codec-slice',
  type: 'JobRequested',
  parents: [],
  payload,
  payloadRefs: [],
})
function collectSourceFiles(directory) {
  const found = []
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    const path = join(directory, entry.name)
    if (entry.isDirectory()) found.push(...collectSourceFiles(path))
    else if (/\.[fs]i?$/.test(entry.name) || entry.name.endsWith('.fs')) found.push(path)
  }
  return found
}

test('WHAT[durable-events-023] canonical codec surface keeps encode decode UTF-8 identity and merge in one fail-closed protocol', () => {
  const left = event()
  const same = event()
  const collision = event({ payload: { state: 'closed' } })
  const distinct = event({ id: '2222222222222222222222222222222222222222' })
  const canonical = eventCodec.encode(left)

  assert.deepEqual(eventCodec.decode(canonical), { ok: true, event: left })
  assert.deepEqual(eventCodec.decodeUtf8Text(Buffer.from(canonical)), { ok: true, text: canonical })
  assert.deepEqual(eventCodec.decodeUtf8(Buffer.from(canonical)), { ok: true, event: left })

  assert.deepEqual(eventCodec.checkIdentity(left, same), { ok: true })
  assert.deepEqual(eventCodec.checkIdentity(left, collision), {
    ok: false,
    error: { code: 'IdentityCollision', eventId: left.id },
  })
  assert.deepEqual(eventCodec.checkIdentity(left, distinct), { ok: true })

  assert.deepEqual(eventCodec.mergeByIdentity([left, same, distinct]), {
    ok: true,
    events: [left, distinct],
  })
  assert.deepEqual(eventCodec.mergeByIdentity([left, collision, distinct]), {
    ok: false,
    error: { code: 'IdentityCollision', eventId: left.id },
  })

  const nonCanonical = canonical.replace('"event_id"', '"stream_id"').replace(/,"stream_id":"[^"]+"/, `,"event_id":"${left.id}"`)
  assert.deepEqual(eventCodec.decode(nonCanonical), {
    ok: false,
    error: { code: 'NonCanonical', reason: 'event bytes are not §5.0 canonical' },
  })
  const invalidUtf8 = Buffer.from([0xc3, 0x20])
  const invalidUtf8Error = {
    ok: false,
    error: { code: 'NonCanonical', reason: 'event bytes are not valid UTF-8' },
  }
  assert.deepEqual(eventCodec.decodeUtf8Text(invalidUtf8), invalidUtf8Error)
  assert.deepEqual(eventCodec.decodeUtf8(invalidUtf8), invalidUtf8Error)
})
test('WHAT[durable-events-023] single-field family folds own their slice and declare no aggregate dependency', () => {
  const shardInventory = readCompileShardInventory({ repositoryRoot: ROOT })
  const subsystemInventory = buildSubsystemInventory({ compileInventory: shardInventory })
  assert.ok(subsystemInventory.ok, subsystemInventory.violations.join('\n'))
  const projects = [...subsystemInventory.projects.values()]

  // These four families only ever wrote their own top-level field, so their
  // `AgentProjectionSet -> ... -> AgentProjectionSet` wrappers are gone and the
  // slice write lives in composition (`ProjectionUpdate.apply*`).
  for (const source of [
    'Execution/Fission/Fold.fs',
    'Interaction/Concern/Fold.fs',
    'Interaction/Attention/Fold.fs',
  ])
    assert.equal(
      existsSync(join(SOURCE_ROOT, source)),
      false,
      `${source} must not come back as an aggregate-typed wrapper`,
    )

  // The Change family keeps the fold but reads its own slice: the shard declares
  // no reference to the aggregate projection and the fold names neither the
  // aggregate nor the durable fail-closed report.
  const changeFold = projects.filter((project) => project.shard === 'change-fold')
  assert.equal(changeFold.length, 1, 'change-fold must resolve to exactly one compile shard')
  assert.ok(
    !changeFold[0].references.some((reference) => reference.endsWith('composition-durable-projection.fsproj')),
    'change-fold must not declare composition-durable-projection',
  )
  const changeFoldSources = ['Change/Fold.fs', 'Change/Fold.fsi']
    .map((source) => readFileSync(join(SOURCE_ROOT, source), 'utf8'))
    .join('\n')
  assert.doesNotMatch(changeFoldSources, /\bAgentProjectionSet\b|\bFoldRejection\b/)
})
test('WHAT[durable-events-023] prompt provider companion and context folds decide on their own slices while composition owns the aggregate write', () => {
  // These families span more than one slice, so the fold stays and returns a
  // change list over the slices it owns; the bridge writes it back.
  for (const source of [
    'Interaction/Authority/Fold.fs',
    'Participant/Provider/Attempt/Fallback/ProviderFailureFactFold.fs',
    'Context/Companion/CompanionFactFold.fs',
  ]) {
    const text = readFileSync(join(SOURCE_ROOT, source), 'utf8')
    assert.doesNotMatch(
      text,
      /\bAgentProjection(?:Set|s)?\b|\bAgentProjection\.|\bFoldRejection\b|\bProjectionUpdate\b|\bComposition\.Durable\b/,
      `${source} is a domain fold and must not name the aggregate projection, the write algebra, or the spine's rejection`,
    )
  }

  // The session-scoped write helpers are composition's; a domain fold that calls
  // them again would re-invert the dependency this boundary exists to prevent.
  const writeHelper =
    /ProjectionUpdate\.(?:updateSession|updateAuthority|updateCompanion|retireAuxiliaryInjectionVisibility)\b/
  for (const file of collectSourceFiles(SOURCE_ROOT)) {
    const relative = file.slice(SOURCE_ROOT.length + 1)
    if (relative.startsWith('Composition/')) continue
    assert.doesNotMatch(
      readFileSync(file, 'utf8'),
      writeHelper,
      `${relative} is outside composition and must not write the aggregate through ProjectionUpdate`,
    )
  }
})
}

{
const { default: assert } = await import("node:assert/strict");
const { execFileSync } = await import("node:child_process");
const { mkdtempSync, rmSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join } = await import("node:path");
const { default: test } = await import("node:test");
const surface = await import("../../../dist/Verification/JournalPortObservationSurface.js");

const withJournalDir = async (tag, scenario) => {
  const dir = mkdtempSync(join(tmpdir(), `wxs-portobs-${tag}-`))
  execFileSync('git', ['init', '--quiet', dir])
  try {
    return await scenario(join(dir, '.Herdchestra'), tag)
  } finally {
    rmSync(dir, { recursive: true, force: true })
  }
}

test('WHAT[durable-events-023] EXEC_port_members_read_journal_at_call_time', () =>
  withJournalDir('live-read', (commonDir, tag) =>
    surface.liveReadScenario(commonDir, tag).then((result) => {
      assert.equal(result.folded, true, 'the seeded fact must fold')
      assert.equal(result.openedOk, true, 'the session-opening fact must fold')
      assert.equal(result.pendingBefore, 0, 'no deferred work before the append')
      assert.equal(result.pendingAfter, 1, 'a port built before the append must still read live')
      assert.equal(result.freshAfter, 1, 'a port built after the append reads the same live state')
      assert.equal(result.poisonedBefore, false)
      assert.equal(result.poisonedAfter, false, 'a healthy journal is never poisoned')
      assert.equal(result.stateBefore, false, 'no session state before the opening append')
      assert.equal(result.stateAfter, true, 'ReadView must observe the XTrace slice the opening wrote')
    }),
  ))
test('WHAT[durable-events-023] EXEC_one_commit_moves_every_related_view_together', () =>
  withJournalDir('same-commit', (commonDir, tag) =>
    surface.sameCommitViewScenario(commonDir, tag).then((result) => {
      assert.equal(result.outcome, 'Ok', `commit must succeed, got ${result.outcome}`)
      assert.equal(result.preMember, false)
      assert.equal(result.preLinked, false)
      assert.equal(result.preState, false)
      // While the physical append is parked mid-commit, every affected member
      // must still read the pre-commit projection — a view that tears would
      // already show one slice advanced.
      assert.equal(result.midMember, false, 'mid-commit member must not see the parked handle')
      assert.equal(result.midLinked, false, 'mid-commit view must not see the parked link')
      assert.equal(result.midState, false, 'mid-commit canonical handle state must stay absent')
      assert.equal(result.midCompanion, false)
      assert.equal(result.postMember, true, 'after release both handle slices are visible')
      assert.equal(result.postLinked, true)
      assert.equal(result.postState, true, 'the canonical parent handle state must appear with both derived views')
      assert.equal(result.advancedOnce, true, 'only the parked handle commit advances the accepted setup revision')
    }),
  ))
test('WHAT[durable-events-023] EXEC_revision_waiter_wakes_on_next_commit', { timeout: 8000 }, () =>
  withJournalDir('wait', (commonDir, tag) =>
    surface.revisionWaitScenario(commonDir, tag).then((result) => {
      assert.equal(result.committed, 'Ok')
      assert.equal(result.resolved, true, 'the registered waiter must resolve through the commit, not a poll')
      assert.ok(result.changeRevision > 0, 'the woken waiter must carry the new revision')
      assert.equal(result.changeRevision, result.currentRevision, 'the wake revision is the live revision')
      assert.equal(result.observedHandle, true, 'the member reads the committed state after the wake')
      assert.equal(result.advancedOnce, true, 'the waiter observes exactly the next commit after physical admission setup')
    }),
  ))
test('WHAT[durable-events-023] EXEC_cancelled_waiter_releases_without_stealing_a_commit', () =>
  withJournalDir('cancel', (commonDir, tag) =>
    surface.cancelWaiterScenario(commonDir, tag).then((result) => {
      assert.equal(result.cancelledToNone, true, 'a cancelled waiter resolves to None')
      assert.equal(result.committed, 'Ok')
      assert.equal(result.revisionAdvanced, true, 'the commit still lands and advances revision')
    }),
  ))
test('WHAT[durable-events-023] EXEC_missing_payload_is_known_not_attempted_and_poisons_with_the_original_rejection', () =>
  withJournalDir('poison', (commonDir, tag) =>
    surface.rejectedPayloadPoisonsWriterScenario(commonDir, tag).then((result) => {
      assert.equal(result.seededOk, true)
      assert.ok(result.failedOutcome.startsWith('Poisoned:'), `the known missing-payload rejection must not become Unknown, got ${result.failedOutcome}`)
      assert.equal(result.missingPayloadRef, 'f'.repeat(64), 'the writer maps the blob address to its canonical payload handle')
      assert.equal(result.failedFactAbsent, true, 'the rejected fact must not exist in canonical Current')
      assert.equal(result.poisoned, true, 'the port must observe the poisoned writer')
      assert.ok(result.afterOutcome.startsWith('Poisoned:'), `a poisoned writer must refuse later appends, got ${result.afterOutcome}`)
      assert.equal(result.afterOutcome, result.failedOutcome, 'later refusal renders the original typed rejection')
      assert.equal(result.laterFactAbsent, true, 'the poisoned writer must not publish a later fact')
      assert.equal(result.originalPoisonPreserved, true, 'later refusal retains the first event identity and original AppendError object')
      assert.equal(result.revisionAfterFailure, result.revisionBefore, 'known storage rejection publishes no revision')
      assert.equal(result.revisionAfterPoisoned, result.revisionBefore, 'poison refusal publishes no revision')
    }),
  ))
integrationTest('WHAT[durable-events-023] isolated compilation rejects physical-store authority in the codec closure', async () => {
  const { cpSync, mkdirSync, mkdtempSync, readFileSync, rmSync, statSync, writeFileSync } = await import('node:fs')
  const { createHash } = await import('node:crypto')
  const { dirname: dirnameOf, join: joinPath, relative: relativeOf, resolve: resolveRoot } = await import('node:path')
  const { tmpdir } = await import('node:os')
  const { compileOwnerProject, planOwnerCompile } = await import('../../../scripts/lib/owner-compile.mjs')
  const ROOT = resolveRoot(import.meta.dirname, '../../..')
  const SOURCE_ROOT = joinPath(ROOT, 'src/Wanxiangshu')
  const CODEC_SHARD = joinPath(SOURCE_ROOT, 'Wanxiangshu.Owner.durable-events.persistence-eventstore-canonicalcodec.fsproj')
  const STORE_SHARD = joinPath(SOURCE_ROOT, 'Wanxiangshu.Owner.durable-convergence.persistence-eventstore-processeventlog.fsproj')

  // The probe references the real physical-store factory
  // (Wanxiangshu.Persistence.EventStore.EventStore.createLocal, a
  // RequireQualifiedAccess module member). The same probe source is used in
  // both closures so the only variable is which dependencies are in scope.
  const PROBE_SOURCE = [
    'namespace Wanxiangshu.Probe',
    '',
    'open Wanxiangshu.Persistence.EventStore',
    '',
    'module ProbeStoreUsage =',
    '    let factory = EventStore.createLocal',
    '',
  ].join('\n')

  // Source isolation: every compile input resolves to a copy under a temp
  // root, so the real workspace is never written and a crash mid-test cannot
  // leave the tree mutated. compileOwnerProject's scratchRoot only isolates
  // outputs; the compile items themselves are remapped here.
  const isolate = (plan) => {
    const iso = mkdtempSync(joinPath(tmpdir(), 'wxs-023-iso-'))
    const items = plan.compileItems.map((item) => {
      const dest = joinPath(iso, 'src', relativeOf(SOURCE_ROOT, item))
      mkdirSync(dirnameOf(dest), { recursive: true })
      cpSync(item, dest)
      return dest
    })
    const probe = joinPath(iso, 'probe-store-usage.fs')
    writeFileSync(probe, PROBE_SOURCE)
    return { plan: { ...plan, compileItems: [...items, probe] }, iso }
  }

  // Isolation evidence: the real codec source keeps its bytes and mtime, and
  // the generated project references only isolated copies.
  const realCodec = joinPath(SOURCE_ROOT, 'Persistence/EventStore/CanonicalEventCodec.fs')
  const before = { hash: createHash('sha256').update(readFileSync(realCodec)).digest('hex'), mtime: statSync(realCodec).mtimeMs }

  const scratch = mkdtempSync(joinPath(tmpdir(), 'wxs-023-compile-'))
  const positiveIso = isolate(planOwnerCompile({ projectPath: STORE_SHARD }))
  const negativeIso = isolate(planOwnerCompile({ projectPath: CODEC_SHARD }))
  try {
    // Positive: the store closure legitimately contains Store.fs, so the
    // probe compiles — the symbol and its qualified usage are valid.
    const positive = await compileOwnerProject({
      projectPath: STORE_SHARD,
      scratchRoot: scratch,
      stdio: 'pipe',
      compilePlan: positiveIso.plan,
    })
    assert.equal(positive.ok, true, 'probe compiles in the store closure (symbol and usage are valid): ' + String(positive.stdout ?? '').slice(-200))

    // Negative: the codec closure has no Store.fs, so the same probe fails.
    // The diagnostic must name the target symbol, not just any error.
    const negative = await compileOwnerProject({
      projectPath: CODEC_SHARD,
      scratchRoot: scratch,
      stdio: 'pipe',
      compilePlan: negativeIso.plan,
    })
    assert.equal(negative.ok, false, 'the same probe must fail in the codec closure')
    assert.match(
      String(negative.stdout ?? '') + String(negative.stderr ?? ''),
      /'EventStore' is not defined/,
      'the diagnostic names the physical-store module',
    )

    // Isolation evidence: the real source was never touched.
    const after = { hash: createHash('sha256').update(readFileSync(realCodec)).digest('hex'), mtime: statSync(realCodec).mtimeMs }
    assert.deepEqual(after, before, 'the real codec source is untouched (bytes and mtime)')
  } finally {
    rmSync(scratch, { recursive: true, force: true })
    rmSync(positiveIso.iso, { recursive: true, force: true })
    rmSync(negativeIso.iso, { recursive: true, force: true })
  }

  // Static boundary supplement (read-only): the codec shard declares no
  // reference to the physical store family.
  const { readCompileShardInventory } = await import('../../../scripts/lib/compile-shards.mjs')
  const inventory = readCompileShardInventory({ repositoryRoot: ROOT })
  const codec = [...inventory.projects.values()].find((p) => p.explicitCompileShard === 'eventstore-canonical-codec')
  assert.ok(codec, 'codec shard resolves in the inventory')
  const forbidden = codec.references.filter((reference) =>
    /eventstore-(store|merge-runtime|integrator-engine|process-log|git|writer|handle)\b/i.test(reference))
  assert.deepEqual(forbidden, [], 'codec shard declares no physical-store reference')
})

// B4 domain-fold dimension (GAP-149): the codec closure proof above does not
// cover domain folds, so this probe targets the real Delegation fact fold
// (the card names it explicitly). Positive: the fold's declared closure
// flattens into one zero-ProjectReference project and compiles green — a
// domain fold owns its facts without the aggregate. Negative: the same fold
// closure must reject the aggregate outer union (Fact.AgentFact), the
// composition journal append entry (AgentJournalPortAdapter.fromAgentJournal,
// delegation-029's composition-only wrapping point) and the physical
// ProcessEventLog with not-defined diagnostics. Each probe first compiles
// green in the closure that legitimately owns the symbol, so the only
// remaining cause of each negative failure is the domain-fold boundary —
// not probe syntax, not a missing reference, not toolchain drift.
integrationTest('WHAT[durable-events-023] isolated compilation rejects aggregate authority in domain folds', async () => {
  const { cpSync, mkdirSync, mkdtempSync, readFileSync, rmSync, statSync, writeFileSync } = await import('node:fs')
  const { createHash } = await import('node:crypto')
  const { dirname: dirnameOf, join: joinPath, relative: relativeOf, resolve: resolveRoot } = await import('node:path')
  const { tmpdir } = await import('node:os')
  const { compileOwnerProject, materializeOwnerCompile, planOwnerCompile } = await import('../../../scripts/lib/owner-compile.mjs')
  const ROOT = resolveRoot(import.meta.dirname, '../../..')
  const SOURCE_ROOT = joinPath(ROOT, 'src/Wanxiangshu')

  // The domain fold under probe: the real Delegation fact fold. Its shard
  // compiles DelegationFactFold (DelegationFactCases ->
  // DelegationProjectionChange list / DelegationFoldRejection) and
  // ExecutionFactFold — owner-owned fact cases, fold state and closed
  // rejection only.
  const FOLD_SHARD = joinPath(
    SOURCE_ROOT, 'Wanxiangshu.Owner.delegation.execution-delegation-handle-surface.fsproj')

  // Positive-control closures, one per probe symbol.
  const AGGREGATE_OWNER_SHARD = joinPath(
    SOURCE_ROOT, 'Wanxiangshu.Owner.durable-events.composition-durable-fact.fsproj')
  const JOURNAL_APPEND_OWNER_SHARD = joinPath(
    SOURCE_ROOT, 'Wanxiangshu.Owner.durable-events.durable-journal-port-adapter.fsproj')
  const STORE_SHARD = joinPath(
    SOURCE_ROOT, 'Wanxiangshu.Owner.durable-convergence.persistence-eventstore-processeventlog.fsproj')

  const AGGREGATE_PROBE = [
    'namespace Wanxiangshu.Probe',
    '',
    'open Wanxiangshu.Composition.Durable',
    '',
    'module ProbeAggregateUsage =',
    '    let kindOf (fact: Fact.AgentFact) = "agent-fact"',
    '',
  ].join('\n')

  const JOURNAL_APPEND_PROBE = [
    'namespace Wanxiangshu.Probe',
    '',
    'open Wanxiangshu.Composition.Durable',
    '',
    'module ProbeJournalAppendUsage =',
    '    let appendPort = AgentJournalPortAdapter.fromAgentJournal',
    '',
  ].join('\n')

  const PROCESS_EVENT_LOG_PROBE = [
    'namespace Wanxiangshu.Probe',
    '',
    'open Wanxiangshu.Persistence.EventStore',
    '',
    'module ProbeProcessEventLogUsage =',
    '    let describe (log: ProcessEventLog) = "process-event-log"',
    '',
  ].join('\n')

  // Source isolation: every compile input resolves to a copy under a temp
  // root, so the real workspace is never written and a crash mid-test cannot
  // leave the tree mutated (same pattern as the codec closure proof above).
  const isolate = (projectPath, probeSource) => {
    const plan = planOwnerCompile({ projectPath, aggregatePath: null })
    const iso = mkdtempSync(joinPath(tmpdir(), 'wxs-de023-fold-iso-'))
    const items = plan.compileItems.map((item) => {
      const dest = joinPath(iso, 'src', relativeOf(SOURCE_ROOT, item))
      mkdirSync(dirnameOf(dest), { recursive: true })
      cpSync(item, dest)
      return dest
    })
    const probe = joinPath(iso, 'probe-domain-fold-boundary.fs')
    writeFileSync(probe, probeSource)
    return { projectPath, plan: { ...plan, compileItems: [...items, probe] }, iso }
  }

  // Isolation evidence: the real fold, aggregate, adapter and store sources
  // keep their bytes and mtime across the whole probe matrix.
  const realFold = joinPath(SOURCE_ROOT, 'Execution/Delegation/DelegationFactFold.fs')
  const realAggregate = joinPath(SOURCE_ROOT, 'Composition/Durable/Fact.fs')
  const realAdapter = joinPath(SOURCE_ROOT, 'Composition/Durable/AgentJournalPortAdapter.fs')
  const realStore = joinPath(SOURCE_ROOT, 'Persistence/EventStore/ProcessEventLog.fs')
  const digestOf = (file) => ({
    hash: createHash('sha256').update(readFileSync(file)).digest('hex'),
    mtime: statSync(file).mtimeMs,
  })
  const before = [digestOf(realFold), digestOf(realAggregate), digestOf(realAdapter), digestOf(realStore)]

  const PROBES = [
    {
      name: 'the aggregate outer union',
      ownerPath: AGGREGATE_OWNER_SHARD,
      source: AGGREGATE_PROBE,
      symbol: /AgentFact|Composition|Durable/,
    },
    {
      name: 'the composition journal append entry',
      ownerPath: JOURNAL_APPEND_OWNER_SHARD,
      source: JOURNAL_APPEND_PROBE,
      symbol: /AgentJournalPortAdapter|Composition|Durable/,
    },
    {
      name: 'the physical process event log',
      ownerPath: STORE_SHARD,
      source: PROCESS_EVENT_LOG_PROBE,
      symbol: /ProcessEventLog|Persistence|EventStore/,
    },
  ]

  const scratch = mkdtempSync(joinPath(tmpdir(), 'wxs-de023-fold-compile-'))
  const isolated = PROBES.map((probe) => ({
    ...probe,
    ownerIso: isolate(probe.ownerPath, probe.source),
    foldIso: isolate(FOLD_SHARD, probe.source),
  }))
  try {
    // Positive: the real domain fold compiles as one flat project over its
    // own declared closure — fold own fact needs no aggregate, no journal
    // append, no physical store.
    const foldPlan = planOwnerCompile({ projectPath: FOLD_SHARD, aggregatePath: null })
    const materialized = materializeOwnerCompile(foldPlan, { scratchRoot: scratch })
    const flatXml = readFileSync(materialized.projectPath, 'utf8')
    assert.ok(
      !flatXml.includes('<ProjectReference'),
      'the delegation fold flat project must carry zero ProjectReference',
    )
    assert.equal(
      (flatXml.match(/<Compile Include=/g) ?? []).length,
      foldPlan.compileItems.length,
      'the delegation fold flat project must carry the full closure compile list',
    )
    const foldCompile = await compileOwnerProject({
      projectPath: FOLD_SHARD,
      aggregatePath: null,
      scratchRoot: scratch,
      stdio: 'pipe',
      compilePlan: foldPlan,
    })
    assert.equal(
      foldCompile.ok,
      true,
      'the real delegation fold closure compiles green (fold own fact): '
        + String(foldCompile.stdout ?? '').slice(-400),
    )

    // Negative: every probe compiles green in its owner closure and fails
    // inside the domain fold closure with a not-defined diagnostic naming
    // the target symbol.
    for (const { name, symbol, ownerIso, foldIso } of isolated) {
      const ownerCompile = await compileOwnerProject({
        projectPath: ownerIso.projectPath,
        aggregatePath: null,
        scratchRoot: scratch,
        stdio: 'pipe',
        compilePlan: ownerIso.plan,
      })
      assert.equal(
        ownerCompile.ok,
        true,
        `${name}: the probe compiles in the owner closure (symbol and usage are valid): `
          + String(ownerCompile.stdout ?? '').slice(-400),
      )

      const foldCompileProbe = await compileOwnerProject({
        projectPath: foldIso.projectPath,
        aggregatePath: null,
        scratchRoot: scratch,
        stdio: 'pipe',
        compilePlan: foldIso.plan,
      })
      assert.equal(
        foldCompileProbe.ok,
        false,
        `${name}: the same probe must fail in the domain fold closure`,
      )
      const output = String(foldCompileProbe.stdout ?? '') + String(foldCompileProbe.stderr ?? '')
      assert.match(
        output,
        symbol,
        `${name}: the diagnostic names the target symbol`,
      )
      assert.match(
        output,
        /is not defined/,
        `${name}: the diagnostic is a not-defined rejection, not a syntax or toolchain error`,
      )
    }

    // Isolation evidence: the real sources were never touched.
    const after = [digestOf(realFold), digestOf(realAggregate), digestOf(realAdapter), digestOf(realStore)]
    assert.deepEqual(
      after,
      before,
      'the real fold, aggregate, adapter and store sources are untouched (bytes and mtime)',
    )
  } finally {
    rmSync(scratch, { recursive: true, force: true })
    for (const { ownerIso, foldIso } of isolated) {
      rmSync(ownerIso.iso, { recursive: true, force: true })
      rmSync(foldIso.iso, { recursive: true, force: true })
    }
  }
})
}
