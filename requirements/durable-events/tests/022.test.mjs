import assert from 'node:assert/strict'
import { createHash } from 'node:crypto'
import { basename, dirname, join, relative, resolve } from 'node:path'
import test from 'node:test'
import { cpSync, mkdirSync, mkdtempSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs'
import { availableParallelism, tmpdir } from 'node:os'
import { readCompileShardInventory } from '../../../scripts/lib/compile-shards.mjs'
import { buildSubsystemInventory } from '../../../scripts/checks/subsystems.mjs'
import { planOwnerCompile, materializeOwnerCompile, compileOwnerProject } from '../../../scripts/lib/owner-compile.mjs'
import { integrationTest } from '../../verification-system/tests/support/tier-gate.mjs'

const ROOT = resolve(import.meta.dirname, '../../..')

const SOURCE_ROOT = join(ROOT, 'src/Wanxiangshu')

const shardInventory = readCompileShardInventory({ repositoryRoot: ROOT })

const subsystemInventory = buildSubsystemInventory({ compileInventory: shardInventory })

assert.ok(subsystemInventory.ok, subsystemInventory.violations.join('\n'))

const projects = [...subsystemInventory.projects.values()]

const projectByPath = new Map(projects.map((project) => [resolve(project.projectPath), project]))

const requireShard = (shard) => {
  const matches = projects.filter((project) => project.shard === shard)
  assert.equal(matches.length, 1, `${shard} must resolve to exactly one compile shard`)
  return matches[0]
}

const planShard = (shard) => {
  const project = requireShard(shard)
  return {
    project,
    plan: planOwnerCompile({ projectPath: project.projectPath, aggregatePath: null }),
  }
}

// Three positive probes are compiled inside their locality's own flat
// project instead of in a second compile of the same closure. The shared
// compile keeps the locality's exact compile inputs — the same real closure
// source paths, in the same order, with the same props — and appends the
// probe file last. Both verdicts are therefore preserved:
//   * the closure's own verdict: a file later in the compile order cannot
//     make an earlier file's error disappear (F# has no forward
//     references), so the plain locality compile is green whenever the
//     shared compile is green;
//   * the probe's verdict: the probe file is part of that compile, so its
//     success is a real Fable typecheck of the probe against the closure.
// Negative probes keep their own isolated compile: there the failure IS
// the assertion, and a shared compile could mask a missing-reference
// failure by changing the exact closure under test.
const CAPABILITY_PROBE = [
  'namespace Wanxiangshu.ContractProbe',
  'open Wanxiangshu.Persistence.EventStore',
  'module ProbeStoreCapability =',
  '    let append (store: IEventStore) (events: EventEnvelope list) = store.Append(events)',
  '',
].join('\n')

const RESULT_PROBE = [
  'namespace Wanxiangshu.ContractProbe',
  'open Wanxiangshu.Foundation.Identity',
  'open Wanxiangshu.Persistence.EventStore',
  'module ProbeAppendResults =',
  '    let eventId = EventId.create "append-result-probe"',
  '    let streamId = EventStreamId.create "append-result-stream"',
  '    let invalid =',
  '        [ StorageInvalid.IdentityCollision eventId; StorageInvalid.NonCanonical "canonical";',
  '          StorageInvalid.MalformedEnvelope "envelope"; StorageInvalid.MissingParent eventId;',
  '          StorageInvalid.CyclicParents; StorageInvalid.MissingPayload(PayloadRef.create "payload");',
  '          StorageInvalid.UnknownEventType "unknown" ]',
  '    let classifyInvalid = function',
  '        | StorageInvalid.IdentityCollision _ -> 1',
  '        | StorageInvalid.NonCanonical _ -> 2',
  '        | StorageInvalid.MalformedEnvelope _ -> 3',
  '        | StorageInvalid.MissingParent _ -> 4',
  '        | StorageInvalid.CyclicParents -> 5',
  '        | StorageInvalid.MissingPayload _ -> 6',
  '        | StorageInvalid.UnknownEventType _ -> 7',
  '    let invalidKinds = invalid |> List.map classifyInvalid',
  '    let conflict = DomainConflict.ConcurrentHeads(streamId, [eventId])',
  '    let heads = match conflict with DomainConflict.ConcurrentHeads(_, values) -> values',
  '    let cut: SemanticCut =',
  '        { Rule = "probe"; FailedEventId = eventId; Reason = "cut"; CutEventId = eventId }',
  '    let receipt: AppendReceipt = { Cuts = [cut] }',
  '    let found = AppendReceipt.cutFor eventId receipt',
  '    let missing = AppendReceipt.cutFor eventId AppendReceipt.empty',
  '    let envelope: EventEnvelope =',
  '        { EventId = eventId; StreamId = streamId; EventType = "probe";',
  '          Parents = []; Payload = Thoth.Json.Encode.object []; PayloadRefs = []; Payloads = Map.empty }',
  '    let prepared: PreparedAppend = { DurableEvents = [envelope]; Cuts = [cut] }',
  '    let cause = System.InvalidOperationException "native cause"',
  '    let phases =',
  '        [ AppendPhase.GateAcquire; AppendPhase.Preparation; AppendPhase.BeforePhysicalAppend;',
  '          AppendPhase.PhysicalAppend; AppendPhase.DurabilityOpen; AppendPhase.DurabilityBarrier;',
  '          AppendPhase.DurabilityClose; AppendPhase.CurrentCommit; AppendPhase.StoreRelease ]',
  '    let classifyPhase = function',
  '        | AppendPhase.GateAcquire -> 1',
  '        | AppendPhase.Preparation -> 2',
  '        | AppendPhase.BeforePhysicalAppend -> 3',
  '        | AppendPhase.PhysicalAppend -> 4',
  '        | AppendPhase.DurabilityOpen -> 5',
  '        | AppendPhase.DurabilityBarrier -> 6',
  '        | AppendPhase.DurabilityClose -> 7',
  '        | AppendPhase.CurrentCommit -> 8',
  '        | AppendPhase.StoreRelease -> 9',
  '    let phaseKinds = phases |> List.map classifyPhase',
  '    let fault: AppendFault = { Phase = AppendPhase.DurabilityBarrier; Cause = cause }',
  '    let release: AppendFault = { Phase = AppendPhase.StoreRelease; Cause = cause }',
  '    let notAttempted: AppendNotAttemptedEvidence =',
  '        { Requested = [envelope]; Prepared = Some prepared; Primary = fault;',
  '          CleanupFailures = [release]; PriorRejection = Some(AppendPreWriteRejection.StorageInvalid invalid.Head) }',
  '    let unknown: AppendCommitUnknownEvidence =',
  '        { Requested = [envelope]; Prepared = prepared; Primary = fault; CleanupFailures = [release] }',
  '    let noNewWrite: AppendNoNewWriteReleaseFailure =',
  '        { Requested = [envelope]; Prepared = None; Cause = cause }',
  '    let preparation = AppendPreWriteRejection.PreparationRejected "pure refusal"',
  '    let errors = [ AppendError.StorageInvalid invalid.Head; AppendError.SemanticCut cut;',
  '                   AppendError.AppendFailed "refused"; AppendError.AppendNotAttempted notAttempted;',
  '                   AppendError.CommitUnknown unknown; AppendError.NoNewWriteReleaseFailed noNewWrite ]',
  '    let classify = function',
  '        | AppendError.StorageInvalid _ -> 1',
  '        | AppendError.SemanticCut _ -> 2',
  '        | AppendError.AppendFailed _ -> 3',
  '        | AppendError.AppendNotAttempted _ -> 4',
  '        | AppendError.CommitUnknown _ -> 5',
  '        | AppendError.NoNewWriteReleaseFailed _ -> 6',
  '    let kinds = errors |> List.map classify',
  '    let diagnostics = errors |> List.map AppendError.describe',
  '    let causes = errors |> List.map AppendError.cause',
  '    let cuts = errors |> List.map AppendError.semanticCuts',
  '',
].join('\n')

const JOURNAL_OUTCOME_PROBE = [
  'namespace Wanxiangshu.Probe',
  'open Wanxiangshu.Foundation.Identity',
  'open Wanxiangshu.Persistence.EventStore',
  'open Wanxiangshu.Persistence.Journal.JournalOutcome',
  'module ProbeJournalOutcome =',
  '    let eventId = EventId.create "contract-probe"',
  '    let cause = System.InvalidOperationException "native cause"',
  '    let unknown: AppendCommitUnknownEvidence =',
  '        { Requested = []; Prepared = { DurableEvents = []; Cuts = [] };',
  '          Primary = { Phase = AppendPhase.StoreRelease; Cause = cause }; CleanupFailures = [] }',
  '    let noNewWrite: AppendNoNewWriteReleaseFailure =',
  '        { Requested = []; Prepared = None; Cause = cause }',
  '    let poison: JournalAppendPoison =',
  '        { FailedEventId = eventId; Error = AppendError.CommitUnknown unknown }',
  '    let outcomes: CommitResult<int> list =',
  '        [ Committed 1; Rejected(eventId, "cut");',
  '          NotAttempted(eventId, WriterClosing);',
  '          CommitUnknown(eventId, WriteFailed "write");',
  '          CommitUnknown(eventId, StoreAppendUnknown unknown);',
  '          CommitResult.NoNewWriteReleaseFailed(eventId, noNewWrite) ]',
  '    let failures =',
  '        [ WriteUnknown(eventId, WriteFailed "write");',
  '          WriteUnknown(eventId, FlushFailed "flush");',
  '          WriteUnknown(eventId, StoreAppendUnknown unknown);',
  '          JournalAppendFailure.NoNewWriteReleaseFailed(eventId, noNewWrite);',
  '          WriterUnavailable(eventId, WriterPoisoned poison);',
  '          WriterUnavailable(eventId, WriterClosing);',
  '          WriterUnavailable(eventId, WriterDisposed);',
  '          FactRejected(eventId, { Fact = "fact"; Reason = "cut" }) ]',
  '    let diagnostics = failures |> List.map JournalAppendFailure.describe',
  '',
].join('\n')

// shard -> the positive probe that shard's own locality compile carries.
const LOCALITY_CARRIED_PROBES = new Map([
  ['eventstore-port-contract', { name: 'capability', source: CAPABILITY_PROBE }],
  ['eventstore-append-result-contract', { name: 'result', source: RESULT_PROBE }],
  ['journal-outcome-contract', { name: 'journal-outcome', source: JOURNAL_OUTCOME_PROBE }],
])

// The carried probe for a shard, but only when the probe source is exactly
// the one that shard's shared compile carries. A negative probe never
// matches, so it keeps its own isolated compile.
const carriedProbeFor = (shard, source) => {
  const carried = LOCALITY_CARRIED_PROBES.get(shard)
  return carried && carried.source === source ? carried : null
}

const carriedCompileScratch = { dir: null }
const carriedLocalityCompiles = new Map()

// The real closure sources a carried compile reads are recorded before that
// compile starts, so the "the real workspace is never written" evidence
// still covers every file a carried compile touches.
const carriedSourceDigests = new Map()
const digestOfFile = (file) => ({
  hash: createHash('sha256').update(readFileSync(file)).digest('hex'),
  mtime: statSync(file).mtimeMs,
})

// One shared compile per carried locality: the locality's own closure
// sources, in their declared order, plus the probe file last. The promise
// is shared by the locality test and by the probe test that owns the
// probe, so exactly one Fable invocation covers both assertions.
const carriedLocalityCompile = (shard) => {
  const existing = carriedLocalityCompiles.get(shard)
  if (existing) return existing
  const carried = LOCALITY_CARRIED_PROBES.get(shard)
  assert.ok(carried, `${shard} is not a carried-probe locality`)
  if (carriedCompileScratch.dir === null) {
    carriedCompileScratch.dir = mkdtempSync(join(tmpdir(), 'wxs-de022-carried-'))
    process.once('exit', () => rmSync(carriedCompileScratch.dir, { recursive: true, force: true }))
  }
  const project = requireShard(shard)
  const basePlan = planOwnerCompile({ projectPath: project.projectPath, aggregatePath: null })
  for (const item of basePlan.compileItems) {
    if (!carriedSourceDigests.has(item)) carriedSourceDigests.set(item, digestOfFile(item))
  }
  const probePath = join(carriedCompileScratch.dir, `${carried.name}-probe.fs`)
  writeFileSync(probePath, carried.source)
  const plan = { ...basePlan, compileItems: [...basePlan.compileItems, probePath] }
  const materialized = materializeOwnerCompile(plan, { scratchRoot: carriedCompileScratch.dir })
  const promise = boundedCompile(project.projectPath, () => compileOwnerProject({
    projectPath: project.projectPath,
    aggregatePath: null,
    scratchRoot: carriedCompileScratch.dir,
    stdio: 'pipe',
    compilePlan: plan,
  }))
  const entry = { shard, project, basePlan, plan, materialized, probePath, promise }
  carriedLocalityCompiles.set(shard, entry)
  return entry
}

// The real Fable compiles in this file are launched together through one
// bounded pool and their verdicts are asserted afterwards in declaration
// order. Concurrency changes wall time only: every compile still runs, and
// every positive, negative and isolation assertion is the one the sequential
// form made. Compiles of the same owner project share its materialized
// project directory, so compiles keyed by the same project never overlap.
const COMPILE_CONCURRENCY = Math.max(2, Math.min(6, Math.floor(availableParallelism() / 2)))
const activeCompileKeys = new Set()
const queuedCompiles = []
const pumpCompiles = () => {
  for (let index = 0; index < queuedCompiles.length && activeCompileKeys.size < COMPILE_CONCURRENCY;) {
    const { key, run, resolve: settle, reject } = queuedCompiles[index]
    if (activeCompileKeys.has(key)) {
      index += 1
      continue
    }
    queuedCompiles.splice(index, 1)
    activeCompileKeys.add(key)
    Promise.resolve()
      .then(run)
      .then(settle, reject)
      .finally(() => {
        activeCompileKeys.delete(key)
        pumpCompiles()
      })
  }
}
// The returned promise is marked handled at once so a compile that fails
// before its assertion awaits it is still reported by that assertion, not as
// an unhandled rejection.
const boundedCompile = (key, run) => {
  const pending = new Promise((settle, reject) => {
    queuedCompiles.push({ key: resolve(key), run, resolve: settle, reject })
    pumpCompiles()
  })
  pending.catch(() => {})
  return pending
}

// The two whole-owner compiles are the longest single compiles in this file;
// the locality test queues them first so they overlap its batch, and each
// owner test awaits and asserts its own verdict (starting it itself when run
// alone). A prefetched compile whose test is filtered out is still reclaimed.
const ownerCompiles = new Map()
const ownerCompile = (fsproj, scratchPrefix) => {
  if (!ownerCompiles.has(fsproj)) {
    const scratchRoot = mkdtempSync(join(tmpdir(), scratchPrefix))
    process.once('exit', () => rmSync(scratchRoot, { recursive: true, force: true }))
    const projectPath = join(SOURCE_ROOT, fsproj)
    const result = boundedCompile(projectPath, () => compileOwnerProject({
      projectPath,
      aggregatePath: null,
      scratchRoot,
      rootPropsPath: join(ROOT, 'Directory.Build.props'),
      stdio: 'pipe',
    }))
    ownerCompiles.set(fsproj, { scratchRoot, result })
  }
  return ownerCompiles.get(fsproj)
}
const JOURNAL_OBSERVATION_OWNER = ['Wanxiangshu.Owner.verification-system.verification-eventstorewritersurface.fsproj', 'wxs-journal-owner-compile-']
const EVENTSTORE_COMPOSITION_OWNER = ['Wanxiangshu.Owner.durable-events.runtime.fsproj', 'wxs-eventstore-surface-compile-']

const productionSources = (plan) => plan.compileItems
  .filter((path) => path.endsWith('.fs'))
  .map((path) => path.slice(SOURCE_ROOT.length + 1).replaceAll('\\', '/'))

const CONTRACT_SHARDS = [
  'eventstore-model-contract',
  'eventstore-append-result-contract',
  'eventstore-port-contract',
  'eventstore-event-vocabulary-contract',
  'eventstore-git-contract',
  'casebook-event-vocabulary-contract',
  'js-transaction-event-vocabulary-contract',
]

const FOCUSED_RUNTIME_SHARDS = [
  'eventstore-core-runtime',
]

// B4 expansion (2026-10-04, volume-02 build direction): the locality
// flat-compile probe set extended beyond EventStore into shards the
// W1/W3/W4 construction cards actually touch. Selection: (a) entry
// shards of active construction areas — interaction-authority (C1/IA018
// ledger+fact, IA017 surface), dispatch-protocol (DP002/session008),
// durable journal (DE023), context companion, and the session-ontology
// sync delegate whose missing edge was repaired in the previous batch
// (the positive now guards that edge); (b) shards whose closure the
// lexical audit flags for namespaces opened from outside it
// (Wanxiangshu.Participant.Provider et al.); (c) closure size within
// the 022 budget tiers — contract kind ≤100, focused runtime kind ≤185.
// Shards surveyed but over the focused budget stay out of the set
// (delegation-runtime-surface 474, execution-delegation-
// hostturnobservedsurface 448, join-guard-surface 453,
// composition-turn-scheduler 239, opencode-host-chatadmission-
// transaction 375, context-compression-runtime-surface 281,
// persistence-journal-surface 303, persistence-journal-
// obligationsurface 305, durable-runtime-surface 352,
// opencode-host-workspaceeventstore 529, opencode-host-
// turnruntimepreparation 528) — recorded in the package README.
// authority-runtime-surface joined the set (2026-10-04,
// shard-regrouping card) after its five out-of-closure namespace opens
// were resolved as dead opens: Child.fs / CompletedTurn.fs reference no
// symbol from Change, Context.Trace, Enforcer (incl. Enforcer.Guidance),
// Execution.Fission, or Persistence.EventStore, so deleting those open
// lines is a zero-behavior structural fix. The closure stays at 118
// production sources — inside the 185 focused-runtime ratchet (delegation
// WHAT-028) — with no ProjectReference edge added.
const EXTENDED_CONTRACT_LOCALITIES = [
  'interaction-authority-fold',
  'execution-session-syncdelegaterole',
  'journal-outcome-contract',
  'repository-programming-js-capability',
]

const EXTENDED_FOCUSED_RUNTIME_LOCALITIES = [
  'interaction-authority-fact',
  'interaction-authority-ledger',
  'authority-runtime-surface',
  'dispatch-runtime',
  'context-companion-fold',
  'persistence-journal-agentjournal',
]

const ALLOWED_CONTRACT_CLOSURE_SHARDS = new Set([
  'eventstore-model-contract',
  'eventstore-append-result-contract',
  'eventstore-port-contract',
  'eventstore-event-vocabulary-contract',
  'eventstore-git-contract',
  'casebook-event-vocabulary-contract',
  'js-transaction-event-vocabulary-contract',
  'identity',
  // Other contract vocabularies may still depend on the neutral Outcome tier.
  'outcome',
  'foundation-roles',
  'runtime-platform-language',
  'runtime-platform-assemblyinfo',
])

test('WHAT[durable-events-022] EventStore contracts exclude physical and Strength runtime closure', () => {
  for (const shard of CONTRACT_SHARDS) {
    const { project, plan } = planShard(shard)
    assert.ok(
      ['persistence', 'knowledge', 'repository-programming'].includes(project.subsystem),
      `${shard} belongs to expected contract subsystem`,
    )

    for (const projectPath of plan.projectPaths) {
      const provider = projectByPath.get(resolve(projectPath))
      assert.ok(
        ALLOWED_CONTRACT_CLOSURE_SHARDS.has(provider?.shard),
        `${shard} contract closure contains non-contract shard ${provider?.shardKey ?? basename(projectPath)}`,
      )
    }
  }

  const portSources = productionSources(planShard('eventstore-port-contract').plan)
  for (const forbidden of [
    'Persistence/EventStore/GitObjectDatabase.fs',
    'Persistence/EventStore/ProcessGitRawStore.fs',
    'Persistence/EventStore/ProcessEventLog.fs',
    'Persistence/EventStore/Store.fs',
    'Persistence/EventStore/CanonicalIntegrator.fs',
    'OpenCode/Host/WorkspaceEventStore.fs',
  ]) {
    assert.ok(!portSources.includes(forbidden), `EventStore.Port.Contract leaks ${forbidden}`)
  }

  const contractPlan = planShard('eventstore-event-vocabulary-contract').plan
  const contractShards = new Set(
    contractPlan.projectPaths.map((projectPath) => projectByPath.get(resolve(projectPath))?.shard),
  )
  for (const domainShard of [
    'casebook-event-vocabulary-contract',
    'js-transaction-event-vocabulary-contract',
  ]) {
    assert.ok(
      !contractShards.has(domainShard),
      `eventstore-event-vocabulary-contract must not contain ${domainShard} in its transitive closure`,
    )
  }

  const contractSources = productionSources(contractPlan)
  assert.ok(!contractSources.includes('Strength/EventVocabulary.fs'))
  assert.ok(!contractSources.includes('Repository/Knowledge/Casebook/EventVocabulary.fs'))
  assert.ok(!contractSources.includes('Repository/Programming/Js/EventVocabulary.fs'))
  assert.ok(!contractSources.includes('Strength/Events.fs'))
  assert.ok(!contractSources.includes('Strength/Runtime.fs'))

  const assemblyPlan = planShard('eventstore-authoritative-vocabulary').plan
  const assemblyShards = new Set(
    assemblyPlan.projectPaths.map((projectPath) => projectByPath.get(resolve(projectPath))?.shard),
  )
  for (const domainShard of [
    'casebook-event-vocabulary-contract',
    'js-transaction-event-vocabulary-contract',
  ]) {
    assert.ok(
      assemblyShards.has(domainShard),
      `eventstore-authoritative-vocabulary must contain ${domainShard} in its transitive closure`,
    )
  }

  const assemblySources = productionSources(assemblyPlan)
  assert.ok(!assemblySources.includes('Strength/EventVocabulary.fs'))
  assert.ok(assemblySources.includes('Repository/Knowledge/Casebook/EventVocabulary.fs'))
  assert.ok(assemblySources.includes('Repository/Programming/Js/EventVocabulary.fs'))
  assert.ok(!assemblySources.includes('Strength/Events.fs'))
  assert.ok(!assemblySources.some((path) => path.startsWith('Strength/Prediction/')))
  assert.ok(!assemblySources.some((path) => path.startsWith('Strength/Replica/')))
  assert.ok(!assemblySources.includes('Strength/Runtime.fs'))
})

test('WHAT[durable-events-022] declared EventStore compile plans stay within source-count budgets', () => {
  for (const shard of CONTRACT_SHARDS) {
    const { plan } = planShard(shard)
    assert.ok(
      productionSources(plan).length <= 100,
      `${shard} contract closure exceeds 100 production sources`,
    )
  }

  for (const shard of FOCUSED_RUNTIME_SHARDS) {
    const { project, plan } = planShard(shard)
    assert.equal(project.subsystem, 'persistence', `${shard} must belong to persistence subsystem`)
    assert.ok(
      productionSources(plan).length <= 185,
      `${shard} runtime closure exceeds 185 production sources`,
    )
  }

  // Extended localities reuse the same 022 budget tiers per declared
  // LocalityKind (contract ≤100, focused runtime ≤185) — the same
  // numbers the EventStore tiers above enforce, no loosening. The
  // EventStore-specific closure whitelist and persistence-subsystem
  // assertions do not apply to the extended tiers; the flat-compile
  // positive and these budget bounds are what carry the 022 contract
  // for the extended set.
  for (const shard of EXTENDED_CONTRACT_LOCALITIES) {
    const { plan } = planShard(shard)
    assert.ok(
      productionSources(plan).length <= 100,
      `${shard} extended contract closure exceeds 100 production sources`,
    )
  }

  for (const shard of EXTENDED_FOCUSED_RUNTIME_LOCALITIES) {
    const { plan } = planShard(shard)
    assert.ok(
      productionSources(plan).length <= 185,
      `${shard} extended focused-runtime closure exceeds 185 production sources`,
    )
  }
})

test('WHAT[durable-events-022] Journal outcome contract excludes the store capability and physical journal', () => {
  const { project, plan } = planShard('journal-outcome-contract')
  assert.equal(project.subsystem, 'persistence')
  const sources = productionSources(plan)
  assert.ok(sources.includes('Persistence/Journal/Outcome.fs'))
  for (const source of [
    'Persistence/EventStore/Port.fs',
    'Persistence/EventStore/Store.fs',
    'Persistence/EventStore/ProcessEventLog.fs',
    'Persistence/Journal/Writer.fs',
    'Persistence/Journal/EventStoreJournalWriter.fs',
    'Persistence/Journal/AgentJournal.fs',
    'Foundation/HostSessionPort.fs',
  ]) {
    assert.ok(!sources.includes(source), `Journal outcome contract must not compile ${source}`)
  }
  const foundation = planShard('outcome').plan
  assert.ok(!productionSources(foundation).includes('Persistence/Journal/Outcome.fs'))
})

test('WHAT[durable-events-022] append results have one pure compile owner without acquiring the store capability', () => {
  const { project, plan } = planShard('eventstore-append-result-contract')
  assert.equal(project.subsystem, 'persistence')
  assert.equal(project.legacyKind, 'contract')
  const storeTypes = ['Persistence/EventStore/StoreTypes.fsi', 'Persistence/EventStore/StoreTypes.fs']
  assert.deepEqual(project.compileItems, storeTypes.map(source => join(SOURCE_ROOT, source)))
  for (const source of project.compileItems) {
    assert.deepEqual(projects.filter(owner => owner.compileItems.includes(source)).map(owner => owner.projectPath),
      [project.projectPath], `${source} must have exactly one Compile owner`)
  }
  assert.deepEqual(productionSources(plan), [
    'Foundation/Identity.fs', 'Foundation/Quiescence.fs',
    'Persistence/EventStore/Model.fs', 'Persistence/EventStore/StoreTypes.fs',
  ])
  const port = requireShard('eventstore-port-contract')
  assert.deepEqual(port.compileItems, [
    'Persistence/EventStore/Port.fsi', 'Persistence/EventStore/Port.fs',
    'Persistence/EventStore/EventStoreHandle.fsi', 'Persistence/EventStore/EventStoreHandle.fs',
  ].map(source => join(SOURCE_ROOT, source)))
  assert.ok(port.references.includes(project.projectPath))
  const journal = planShard('journal-outcome-contract').plan
  assert.ok(journal.projectPaths.includes(project.projectPath), 'Journal outcomes carry the pure append settlement evidence')
  assert.ok(!journal.projectPaths.includes(port.projectPath), 'Journal outcomes must not acquire the store capability')
  assert.ok(!productionSources(planShard('outcome').plan).some(source => source.startsWith('Persistence/')))
})

// B4 compile isolation for durable-events-022: the durable-events-023 probe
// pattern applied to the locality dimension. Positive: every bounded
// locality the WHAT-022 budgets name — the eight EventStore contract
// localities (100 production sources), the two focused EventStore runtime
// localities (185), plus the extended locality tiers (2026-10-04:
// contract tier interaction-authority-fold and
// execution-session-syncdelegaterole; focused tier
// interaction-authority-fact, interaction-authority-ledger,
// authority-runtime-surface, dispatch-runtime, context-companion-fold,
// persistence-journal-agentjournal) — flattens its declared ProjectReference
// closure into exactly one
// zero-ProjectReference project and compiles under a single Fable
// invocation. Negative: a temporary consumer that references the real Git
// runtime implementation or the real Host adapter must fail inside a
// contract locality's closure, because that reversed dependency is not part
// of the closure. Each probe first compiles green in the closure that
// legitimately owns the symbol, so the only remaining cause of each negative
// failure is the locality boundary — not probe syntax, not a missing
// reference, not toolchain drift.
integrationTest('WHAT[durable-events-022] each actual bounded locality compiles as one flat project and a reversed dependency is rejected', async () => {

  const LOCALITIES = [
    ...CONTRACT_SHARDS,
    ...FOCUSED_RUNTIME_SHARDS,
    // Extended tiers keep their own budget assertions above; they are not
    // recompiled here to bound the suite's Fable invocation count.
  ]

  ownerCompile(...JOURNAL_OBSERVATION_OWNER)
  ownerCompile(...EVENTSTORE_COMPOSITION_OWNER)

  const scratch = mkdtempSync(join(tmpdir(), 'wxs-de022-compile-'))
  try {
    // Positive: one flat project per locality. planOwnerCompile derives the
    // ordered compile list purely from the declared ProjectReference closure;
    // materializeOwnerCompile emits the flat fsproj; compileOwnerProject runs
    // one `dotnet tool run fable` invocation against it. A locality whose
    // closure is missing a declared edge fails here for real — that is a
    // shard-graph gap, not a test defect, and it must surface as red.
    const localityCompiles = LOCALITIES.map((shard) => {
      const project = requireShard(shard)
      const carried = LOCALITY_CARRIED_PROBES.has(shard) ? carriedLocalityCompile(shard) : null
      const plan = carried
        ? carried.basePlan
        : planOwnerCompile({ projectPath: project.projectPath, aggregatePath: null })
      const materialized = materializeOwnerCompile(plan, { scratchRoot: scratch })
      const flatXml = readFileSync(materialized.projectPath, 'utf8')
      assert.ok(
        !flatXml.includes('<ProjectReference'),
        `${shard} flat project must carry zero ProjectReference`,
      )
      assert.equal(
        (flatXml.match(/<Compile Include=/g) ?? []).length,
        plan.compileItems.length,
        `${shard} flat project must carry the full closure compile list`,
      )
      if (carried) {
        // The carried compile is this locality's own closure compile list,
        // in the same order, plus the positive probe file last. The closure
        // verdict is identical to the plain locality compile: a file that
        // comes later in the compile order cannot remove an error raised
        // by an earlier file, so the closure is green exactly when the
        // carried compile is green.
        assert.deepEqual(
          carried.plan.compileItems.slice(0, plan.compileItems.length),
          plan.compileItems,
          `${shard} carried compile must keep the full closure compile list, in order`,
        )
        const carriedXml = readFileSync(carried.materialized.projectPath, 'utf8')
        assert.ok(
          !carriedXml.includes('<ProjectReference'),
          `${shard} carried flat project must carry zero ProjectReference`,
        )
        assert.equal(
          (carriedXml.match(/<Compile Include=/g) ?? []).length,
          carried.plan.compileItems.length,
          `${shard} carried flat project must carry the full closure compile list`,
        )
      }
      return {
        shard,
        result: carried
          ? carried.promise
          : boundedCompile(project.projectPath, () => compileOwnerProject({
            projectPath: project.projectPath,
            aggregatePath: null,
            scratchRoot: scratch,
            stdio: 'pipe',
            compilePlan: plan,
          })),
      }
    })

    // Negative probes (WHAT-022: a business consumer's transitive compile
    // input must contain neither the Git object/ref implementation nor the
    // Host adapter; IGitRawStore stays in the physical-port contract and is
    // never exposed through EventStore.Port.Contract). Each probe names a
    // real public member of the higher layer.
    const HOST_ADAPTER_PROBE = [
      'namespace Wanxiangshu.Probe',
      '',
      'open Wanxiangshu.OpenCode',
      '',
      'module ProbeHostAdapterUsage =',
      '    let program = WorkspaceEventStore.programWithoutRegistration',
      '',
    ].join('\n')

    // Source isolation: every compile input resolves to a copy under a temp
    // root, so the real workspace is never written and a crash mid-test
    // cannot leave the tree mutated. compileOwnerProject's scratchRoot only
    // isolates outputs; the compile items themselves are remapped here.
    const isolate = (shard, probeSource) => {
      const plan = planOwnerCompile({ projectPath: requireShard(shard).projectPath, aggregatePath: null })
      const iso = mkdtempSync(join(tmpdir(), 'wxs-de022-iso-'))
      const items = plan.compileItems.map((item) => {
        const dest = join(iso, 'src', relative(SOURCE_ROOT, item))
        mkdirSync(dirname(dest), { recursive: true })
        cpSync(item, dest)
        return dest
      })
      const probe = join(iso, 'probe-reversed-dependency.fs')
      writeFileSync(probe, probeSource)
      return { shard, plan: { ...plan, compileItems: [...items, probe] }, iso }
    }

    const digestOf = (file) => ({
      hash: createHash('sha256').update(readFileSync(file)).digest('hex'),
      mtime: statSync(file).mtimeMs,
    })

    // Isolation evidence: the real owner sources keep their bytes and mtime.
    const realStoreTypes = join(SOURCE_ROOT, 'Persistence/EventStore/StoreTypes.fs')
    const realModel = join(SOURCE_ROOT, 'Persistence/EventStore/Model.fs')
    const realHostAdapter = join(SOURCE_ROOT, 'OpenCode/Host/WorkspaceEventStore.fs')
    const before = [digestOf(realStoreTypes), digestOf(realModel), digestOf(realHostAdapter)]

    // durable-events [009]/[011]: the Git runtime implementation and its
    // shard are retired, so only the host-adapter boundary is probed here.
    // Negative B: the model contract must not admit the Host adapter.
    // Positive control: the same probe compiles in the host workspace
    // event-store closure that owns the adapter.
    const hostAdapterPositive = isolate('opencode-host-workspaceeventstore', HOST_ADAPTER_PROBE)
    const hostAdapterNegative = isolate('eventstore-model-contract', HOST_ADAPTER_PROBE)
    const compileIsolated = ({ shard, plan }) => boundedCompile(requireShard(shard).projectPath, () => compileOwnerProject({
      projectPath: requireShard(shard).projectPath,
      aggregatePath: null,
      scratchRoot: scratch,
      stdio: 'pipe',
      compilePlan: plan,
    }))
    const hostPositiveCompile = compileIsolated(hostAdapterPositive)
    const hostNegativeCompile = compileIsolated(hostAdapterNegative)
    try {
      for (const { shard, result: pending } of localityCompiles) {
        const result = await pending
        assert.equal(
          result.ok,
          true,
          `${shard} locality must compile as one flat project\n${result.stdout}\n${result.stderr}`,
        )
      }

      const hostPositive = await hostPositiveCompile
      assert.equal(
        hostPositive.ok,
        true,
        'probe compiles in the host workspace-event-store closure (symbol and usage are valid): '
          + String(hostPositive.stdout ?? '').slice(-400),
      )

      const hostNegative = await hostNegativeCompile
      assert.equal(
        hostNegative.ok,
        false,
        'the host-adapter probe must fail in the eventstore-model-contract closure',
      )
      const hostOutput = String(hostNegative.stdout ?? '') + String(hostNegative.stderr ?? '')
      assert.match(hostOutput, /OpenCode|WorkspaceEventStore/, 'the diagnostic names the host adapter namespace or module')
      assert.match(hostOutput, /is not defined/, 'the diagnostic is a not-defined rejection, not a syntax or toolchain error')

      // Isolation evidence: the real owner sources were never touched.
      const after = [digestOf(realStoreTypes), digestOf(realModel), digestOf(realHostAdapter)]
      assert.deepEqual(after, before, 'the real owner sources are untouched (bytes and mtime)')
    } finally {
      await Promise.allSettled([
        ...localityCompiles.map(({ result }) => result),
        hostPositiveCompile,
        hostNegativeCompile,
      ])
      rmSync(hostAdapterPositive.iso, { recursive: true, force: true })
      rmSync(hostAdapterNegative.iso, { recursive: true, force: true })
    }
  } finally {
    rmSync(scratch, { recursive: true, force: true })
  }
})

integrationTest('WHAT[durable-events-022] real Fable compiles the journal observation owner using its declared closure', async () => {
  const { scratchRoot, result: pending } = ownerCompile(...JOURNAL_OBSERVATION_OWNER)
  try {
    const result = await pending
    assert.equal(result.ok, true, `journal observation owner must compile without undeclared siblings\n${result.stdout}\n${result.stderr}`)
  } finally {
    await Promise.allSettled([pending])
    rmSync(scratchRoot, { recursive: true, force: true })
  }
})

integrationTest('WHAT[durable-events-022] actual EventStore composition surface compiles its registered Sphinx rule dependency', async () => {
  const { scratchRoot, result: pending } = ownerCompile(...EVENTSTORE_COMPOSITION_OWNER)
  try {
    const result = await pending
    assert.equal(result.ok, true, `actual composition surface must compile without undeclared Sphinx siblings\n${result.stdout}\n${result.stderr}`)
  } finally {
    await Promise.allSettled([pending])
    rmSync(scratchRoot, { recursive: true, force: true })
  }
})

integrationTest('WHAT[durable-events-022] pure append results compile every settlement case and reject the identical store capability probe', async (t) => {
  const scratchRoot = mkdtempSync(join(tmpdir(), 'wxs-append-result-compile-'))
  const copies = []
  const sourceDigests = new Map()
  const digestOf = file => ({
    hash: createHash('sha256').update(readFileSync(file)).digest('hex'),
    mtime: statSync(file).mtimeMs,
  })
  const capabilityProbe = CAPABILITY_PROBE
  const resultProbe = RESULT_PROBE
  const compileProbe = async (shard, source) => {
    const { project, plan } = planShard(shard)
    const copyRoot = mkdtempSync(join(tmpdir(), 'wxs-append-result-input-'))
    copies.push(copyRoot)
    const items = plan.compileItems.map(file => {
      if (!sourceDigests.has(file)) sourceDigests.set(file, digestOf(file))
      const destination = join(copyRoot, 'src', relative(SOURCE_ROOT, file))
      mkdirSync(dirname(destination), { recursive: true })
      cpSync(file, destination)
      return destination
    })
    const probe = join(copyRoot, 'probe.fs')
    writeFileSync(probe, source)
    const isolatedPlan = { ...plan, compileItems: [...items, probe] }
    const flat = materializeOwnerCompile(isolatedPlan, { scratchRoot })
    const xml = readFileSync(flat.projectPath, 'utf8')
    assert.ok(!xml.includes('<ProjectReference'), `${shard} must compile one flat project`)
    assert.equal((xml.match(/<Compile Include=/g) ?? []).length, isolatedPlan.compileItems.length)
    return boundedCompile(project.projectPath, () => compileOwnerProject({
      projectPath: project.projectPath,
      aggregatePath: null,
      scratchRoot,
      stdio: 'pipe',
      compilePlan: isolatedPlan,
    }))
  }
  // Each probe is launched once, before the subtests, and each subtest awaits
  // and asserts its own verdict. A positive probe that is already carried by
  // its locality's own flat compile reuses that compile instead of starting
  // a second one; the flat-project shape it asserts is asserted on the
  // carried project below, so no assertion is dropped.
  const launched = new Map()
  const launch = (shard, source) => {
    const key = `${shard}\0${source}`
    if (!launched.has(key)) {
      const carried = carriedProbeFor(shard, source)
      const pending = carried
        ? (() => {
            const entry = carriedLocalityCompile(shard)
            const xml = readFileSync(entry.materialized.projectPath, 'utf8')
            assert.ok(!xml.includes('<ProjectReference'), `${shard} must compile one flat project`)
            assert.equal((xml.match(/<Compile Include=/g) ?? []).length, entry.plan.compileItems.length)
            return entry.promise
          })()
        : Promise.resolve().then(() => compileProbe(shard, source))
      pending.catch(() => {})
      launched.set(key, pending)
    }
    return launched.get(key)
  }
  launch('eventstore-port-contract', capabilityProbe)
  launch('eventstore-append-result-contract', resultProbe)
  launch('eventstore-append-result-contract', capabilityProbe)
  try {
    await t.test('WHAT[durable-events-022] the original port closure compiles its real append capability', async () => {
      const result = await launch('eventstore-port-contract', capabilityProbe)
      assert.equal(result.ok, true, `the original capability probe must compile\n${result.stdout}\n${result.stderr}`)
    })
    await t.test('WHAT[durable-events-022] the pure result closure compiles all settlement cases and evidence', async () => {
      const result = await launch('eventstore-append-result-contract', resultProbe)
      assert.equal(result.ok, true, `all settlement cases and evidence must compile\n${result.stdout}\n${result.stderr}`)
    })
    await t.test('WHAT[durable-events-022] the pure result closure rejects the identical append capability', async () => {
      const result = await launch('eventstore-append-result-contract', capabilityProbe)
      assert.equal(result.ok, false, 'a pure result consumer must not acquire IEventStore')
      const rejection = String(result.stdout ?? '') + String(result.stderr ?? '')
      assert.match(rejection, /IEventStore/)
      assert.match(rejection, /is not defined/, 'the failure must name the missing capability, not probe syntax or the toolchain')
    })
  } finally {
    await Promise.allSettled(launched.values())
    for (const copyRoot of copies) rmSync(copyRoot, { recursive: true, force: true })
    rmSync(scratchRoot, { recursive: true, force: true })
    for (const [file, digest] of sourceDigests) {
      assert.deepEqual(digestOf(file), digest, `${file} must retain its original bytes and mtime`)
    }
    for (const [file, digest] of carriedSourceDigests) {
      assert.deepEqual(digestOfFile(file), digest, `${file} must retain its original bytes and mtime`)
    }
  }
})

integrationTest('WHAT[durable-events-022] Snapshot recovery flat compile preserves bounded Journal outcomes and the full Host effect owner', async (t) => {
  const scratchRoot = mkdtempSync(join(tmpdir(), 'wxs-snapshot-boundary-'))
  const copies = []
  const sourceDigests = new Map()

  const SNAPSHOT_PROBE = [
    'namespace Wanxiangshu.Probe',
    'open Wanxiangshu.Foundation.Identity',
    'open Wanxiangshu.Foundation.HostSession',
    'module ProbeSnapshotConsumer =',
    '    let read (port: ISessionSnapshotPort) (session: SessionId) =',
    '        port.GetMessages(session)',
    '',
  ].join('\n')

  const HOST_EFFECT_PROBE = [
    'namespace Wanxiangshu.Probe',
    'open Wanxiangshu.Foundation.Identity',
    'open Wanxiangshu.Foundation.HostSession',
    'module ProbeHostEffect =',
    '    let send (port: ISessionHostPort) (session: SessionId)',
    '             (text: string) (options: SessionPromptOptions) =',
    '        port.SendPrompt(session, text, options)',
    '',
  ].join('\n')


  const digestOf = (file) => ({
    hash: createHash('sha256').update(readFileSync(file)).digest('hex'),
    mtime: statSync(file).mtimeMs,
  })

  const compileProbe = async (shard, probeSource) => {
    const { project, plan } = planShard(shard)
    const copyRoot = mkdtempSync(join(tmpdir(), 'wxs-snapshot-input-'))
    copies.push(copyRoot)
    const items = plan.compileItems.map((file) => {
      if (!sourceDigests.has(file)) sourceDigests.set(file, digestOf(file))
      const destination = join(copyRoot, 'src', relative(SOURCE_ROOT, file))
      mkdirSync(dirname(destination), { recursive: true })
      cpSync(file, destination)
      return destination
    })
    const probe = join(copyRoot, 'probe.fs')
    writeFileSync(probe, probeSource)
    const isolatedPlan = { ...plan, compileItems: [...items, probe] }
    const flat = materializeOwnerCompile(isolatedPlan, { scratchRoot })
    const flatXml = readFileSync(flat.projectPath, 'utf8')
    assert.ok(!flatXml.includes('<ProjectReference'), `${shard} must compile one flat project`)
    assert.equal((flatXml.match(/<Compile Include=/g) ?? []).length, isolatedPlan.compileItems.length)
    return boundedCompile(project.projectPath, () => compileOwnerProject({
      projectPath: project.projectPath,
      aggregatePath: null,
      scratchRoot,
      stdio: 'pipe',
      compilePlan: isolatedPlan,
    }))
  }
  // Each probe is launched once, before the subtests, and each subtest awaits
  // and asserts its own verdict. A positive probe that is already carried by
  // its locality's own flat compile reuses that compile instead of starting
  // a second one; the flat-project shape it asserts is asserted on the
  // carried project, so no assertion is dropped.
  const launched = new Map()
  const launch = (shard, source) => {
    const key = `${shard}\0${source}`
    if (!launched.has(key)) {
      const carried = carriedProbeFor(shard, source)
      const pending = carried
        ? (() => {
            const entry = carriedLocalityCompile(shard)
            const xml = readFileSync(entry.materialized.projectPath, 'utf8')
            assert.ok(!xml.includes('<ProjectReference'), `${shard} must compile one flat project`)
            assert.equal((xml.match(/<Compile Include=/g) ?? []).length, entry.plan.compileItems.length)
            return entry.promise
          })()
        : Promise.resolve().then(() => compileProbe(shard, source))
      pending.catch(() => {})
      launched.set(key, pending)
    }
    return launched.get(key)
  }

  launch('host-session-contract', HOST_EFFECT_PROBE)
  launch('delegation-recovery-runtime', SNAPSHOT_PROBE)
  launch('delegation-recovery-runtime', HOST_EFFECT_PROBE)
  launch('host-session-snapshot-contract', SNAPSHOT_PROBE)
  launch('journal-outcome-contract', JOURNAL_OUTCOME_PROBE)
  launch('journal-outcome-contract', HOST_EFFECT_PROBE)
  try {
    await t.test('WHAT[durable-events-022] full Host contract compiles with its real SendPrompt capability', async () => {
      const result = await launch('host-session-contract', HOST_EFFECT_PROBE)
      assert.equal(result.ok, true, `the full Host owner must retain its real SendPrompt capability\n${result.stdout}\n${result.stderr}`)
    })

    await t.test('WHAT[durable-events-022] actual recovery compiles with only its Snapshot read capability', async () => {
      const result = await launch('delegation-recovery-runtime', SNAPSHOT_PROBE)
      assert.equal(result.ok, true, `actual recovery must retain the Snapshot read capability\n${result.stdout}\n${result.stderr}`)
    })

    await t.test('WHAT[durable-events-022] actual recovery rejects the identical full Host effect probe', async () => {
      const result = await launch('delegation-recovery-runtime', HOST_EFFECT_PROBE)
      assert.equal(result.ok, false, 'the identical full Host effect probe must fail in the actual recovery closure')
      const rejection = String(result.stdout ?? '') + String(result.stderr ?? '')
      assert.match(rejection, /ISessionHostPort|SessionPromptOptions/)
      assert.match(rejection, /is not defined/, 'the real boundary must reject the Host symbol, not probe syntax or the toolchain')
    })

    await t.test('WHAT[durable-events-022] standalone Snapshot contract compiles its real read capability', async () => {
      const result = await launch('host-session-snapshot-contract', SNAPSHOT_PROBE)
      assert.equal(result.ok, true, `the standalone Snapshot contract must compile without full Host admission\n${result.stdout}\n${result.stderr}`)
    })

    await t.test('WHAT[durable-events-022] standalone Journal outcome contract compiles every original result and diagnostic case', async () => {
      const result = await launch('journal-outcome-contract', JOURNAL_OUTCOME_PROBE)
      assert.equal(result.ok, true, `the pure Journal contract must retain its original typed results\n${result.stdout}\n${result.stderr}`)
    })

    await t.test('WHAT[durable-events-022] standalone Journal outcome contract rejects the full Host effect probe', async () => {
      const result = await launch('journal-outcome-contract', HOST_EFFECT_PROBE)
      assert.equal(result.ok, false, 'the pure Journal result contract must reject the identical full Host effect capability')
      const rejection = String(result.stdout ?? '') + String(result.stderr ?? '')
      assert.match(rejection, /ISessionHostPort|SessionPromptOptions/)
      assert.match(rejection, /is not defined/)
    })
  } finally {
    await Promise.allSettled(launched.values())
    for (const copyRoot of copies) rmSync(copyRoot, { recursive: true, force: true })
    rmSync(scratchRoot, { recursive: true, force: true })
    for (const [file, digest] of sourceDigests) {
      assert.deepEqual(digestOf(file), digest, `${file} must retain its original bytes and mtime`)
    }
    for (const [file, digest] of carriedSourceDigests) {
      assert.deepEqual(digestOfFile(file), digest, `${file} must retain its original bytes and mtime`)
    }
  }
})
