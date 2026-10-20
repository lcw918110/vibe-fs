import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { mkdtempSync, rmSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { dirname, join, resolve } = await import("node:path");
const { fileURLToPath } = await import("node:url");
const { assertOpaque } = await import("../../verification-system/tests/support/js-contract.mjs");

const causal = await import('../../../dist/Execution/Session/Wait/Surface.js')
const boundaryGate = await import('../../../scripts/checks/causal-wait-boundary.mjs')
const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '../../..')
const descriptor = causal.createWait({
  waitKind: 'boundary-observation',
  owner: causal.owner('workflow', { id: 'decision-decoy' }),
  subject: { journalFact: 'lexical-decoy' },
  producer: causal.externalProducer('provider', { id: 'producer-1' }),
  escapes: [causal.escape('processLifetime')],
  source: 'boundary-observation.test',
})
const CLEAN_FILES = [
  {
    rel: 'Execution/Session/Wait/Registry.fs',
    text: 'let reader: IWaitSnapshotReader = registry :> IWaitSnapshotReader\n',
  },
  {
    rel: 'Persistence/Journal/EventStoreJournalCodec.fs',
    text: 'let payload = "CausalWait WaitKind IWaitSnapshotReader CausalAwait"\n',
  },
  {
    rel: 'Change/Fact.fs',
    text: 'let payload = @"DiagnosticWait DiagnosticWaitSnapshot"\n',
  },
  {
    rel: 'Interaction/Dispatch/NewDecision.fs',
    text: [
      '// IWaitSnapshotReader and CausalWaitHub.snapshot are lexical decoys',
      'let ordinary = "IWaitSnapshotReader CausalWaitHub.snapshot"',
      'let verbatim = @"DiagnosticWaitSnapshot CausalWaitSurface"',
      'let triple = """CausalWaitBridge.toPlainObject"""',
      '',
    ].join('\n'),
  },
]
const mutate = (relativePath, addition) => {
  const mutated = CLEAN_FILES.map((file) =>
    file.rel === relativePath ? { ...file, text: file.text + addition } : file,
  )
  assert.notDeepEqual(mutated, CLEAN_FILES, 'target mutation must change the legal fixture')
  return mutated
}

test('WHAT[causal-wait-009] analyzer rejects a global observer hub in a business workflow', () => {
  const violations = boundaryGate.analyzeObservationBoundary(
    mutate('Interaction/Dispatch/NewDecision.fs', 'let observer = CausalWaitHub.observer\n'),
  )
  assert.deepEqual(violations, [
    'Interaction/Dispatch/NewDecision.fs: diagnostics read capability "CausalWaitHub" is confined to Execution/Session/Wait',
  ])
})
test('WHAT[causal-wait-009] analyzer rejects the Node diagnostic adapter outside composition', () => {
  const violations = boundaryGate.analyzeObservationBoundary(
    mutate('Interaction/Dispatch/NewDecision.fs', 'let sink = CausalWaitBridge.target workspace\n'),
  )
  assert.deepEqual(violations, [
    'Interaction/Dispatch/NewDecision.fs: diagnostics read capability "CausalWaitBridge" is confined to Execution/Session/Wait',
  ])
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { default: fs } = await import("node:fs");
const { default: os } = await import("node:os");
const { default: path } = await import("node:path");

const causal = await import('../../../dist/Execution/Session/Wait/Surface.js')
const mkWait = (waitKind, ownerKind, subject, producer) =>
  causal.createWait({
    waitKind,
    owner: causal.owner(ownerKind, { id: ownerKind }),
    subject,
    producer,
    escapes: [causal.escape('processLifetime')],
    source: 'causal-wait-bridge.test',
  })
const externalProducer = (kind, identity) => causal.externalProducer(kind, identity)
const readDiagnostic = (workspace) =>
  JSON.parse(fs.readFileSync(path.join(workspace, '.Herdchestra', 'diagnostics', 'causal-waits.json'), 'utf8'))

const waitFor = async (predicate, timeoutMs = 2000) => {
  const start = Date.now()
  while (Date.now() - start < timeoutMs) {
    try {
      if (predicate()) return
    } catch {}
    await new Promise((resolve) => setTimeout(resolve, 10))
  }
  assert.fail('condition did not settle within the observation window')
}

test('WHAT[causal-wait-009] CAUSAL_BRIDGE_first_binding_is_stable_and_refreshes_on_lifecycle', async () => {
  const workspace = fs.mkdtempSync(path.join(os.tmpdir(), 'causal-runtime-'))
  const redirect = fs.mkdtempSync(path.join(os.tmpdir(), 'causal-redirect-'))
  const registry = causal.createRegistry()
  assert.equal(causal.bindDiagnosticWorkspace(registry, workspace), true)
  assert.equal(causal.bindDiagnosticWorkspace(registry, redirect), false)
  const wait = mkWait(
    'manager-job',
    'OrchestratorWorkflow',
    { job: 'OJ7', manager: 'M4' },
    causal.workflowProducer(causal.owner('ManagerWorkflow', { session: 'M4' })),
  )
  const lease = causal.enter(registry, wait)
  try {
    const filePath = path.join(workspace, '.Herdchestra', 'diagnostics', 'causal-waits.json')
    await waitFor(() => fs.existsSync(filePath) && readDiagnostic(workspace).active.some((value) => value.waitKind === 'manager-job'))
    assert.equal(fs.existsSync(filePath), true)
    assert.ok(readDiagnostic(workspace).active.some((value) => value.waitKind === 'manager-job'))
    assert.equal(fs.existsSync(path.join(redirect, '.Herdchestra', 'diagnostics', 'causal-waits.json')), false)
  } finally {
    causal.dispose(lease)
    await waitFor(() => readDiagnostic(workspace).active.length === 0)
    assert.equal(readDiagnostic(workspace).active.length, 0)
    fs.rmSync(workspace, { recursive: true, force: true })
    fs.rmSync(redirect, { recursive: true, force: true })
  }
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { default: path } = await import("node:path");
const { fileURLToPath } = await import("node:url");
const { readCompileShardInventory } = await import("../../../scripts/lib/compile-shards.mjs");
const { buildSubsystemInventory } = await import("../../../scripts/checks/subsystems.mjs");

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..', '..')
const requireShard = (projects, shardId) => {
  const matches = [...projects.values()].filter((candidate) => candidate.shard === shardId)
  assert.equal(matches.length, 1, `${shardId} must resolve to exactly one production compile shard`)
  return matches[0]
}
const relSources = (project) => project.implementationFiles.map((p) => path.relative(ROOT, p)).sort()
const refShards = (project, projects) => project.references.map((refPath) => projects.get(refPath).shard).sort()

test('WHAT[causal-wait-009] production inventory separates contract runtime adapter mailbox and proof surface', () => {
  const shardInventory = readCompileShardInventory({ repositoryRoot: ROOT })
  const subsystemInventory = buildSubsystemInventory({ compileInventory: shardInventory })
  assert.ok(subsystemInventory.ok, subsystemInventory.violations.join('\n'))
  const projects = subsystemInventory.projects

  const contract = requireShard(projects, 'execution-session-wait-contract')
  const runtime = requireShard(projects, 'execution-session-wait-runtime')
  const adapter = requireShard(projects, 'execution-session-wait-diagnostic-adapter')
  const mailbox = requireShard(projects, 'execution-session-wait-completion-mailbox')
  const proof = requireShard(projects, 'execution-session-wait-proof-surface')

  assert.equal(contract.subsystem, 'session-lifecycle')
  assert.equal(runtime.subsystem, 'session-lifecycle')
  assert.equal(adapter.subsystem, 'session-lifecycle')
  // B03/B07: CompletionMailbox reads delegation completion vocabulary and belongs
  // to the delegation subsystem, not pure session foundation.
  assert.equal(mailbox.subsystem, 'delegation')
  assert.equal(proof.subsystem, 'application-composition',
    'proof surface is a JS-side Surface; it sits in application-composition per B09/B10')

  assert.deepEqual(relSources(contract), ['src/Wanxiangshu/Execution/Session/Wait/CausalWait.fs'])
  assert.deepEqual(relSources(runtime), [
    'src/Wanxiangshu/Execution/Session/Wait/Await.fs',
    'src/Wanxiangshu/Execution/Session/Wait/Registry.fs',
  ])
  assert.deepEqual(relSources(adapter), ['src/Wanxiangshu/Execution/Session/Wait/Bridge.fs'])
  assert.deepEqual(relSources(mailbox), ['src/Wanxiangshu/Execution/Session/Wait/CompletionMailbox.fs'])
  assert.deepEqual(relSources(proof), ['src/Wanxiangshu/Execution/Session/Wait/Surface.fs'])

  assert.deepEqual(refShards(contract, projects), [])
  assert.deepEqual(refShards(adapter, projects), [
    'execution-session-wait-contract',
    'persistence-runtime-path',
  ])
  assert.deepEqual(refShards(runtime, projects), [
    'execution-session-wait-contract',
    'foundation-temporal-contract',
  ])

  for (const id of [
    'delegation-runtime-surface',
    'git-integrationgate',
    'opencode-tools-toolruntimescope',
  ]) {
    const composition = requireShard(projects, id)
    assert.ok(refShards(composition, projects).includes(mailbox.shard), `${id} must declare its physical mailbox provider`)
  }
  assert.equal(
    refShards(requireShard(projects, 'delegation-host-adapter'), projects).includes(mailbox.shard),
    false,
    'the Host adapter must receive a mailbox factory instead of constructing a foreign runtime',
  )
  assert.equal(
    refShards(requireShard(projects, 'delegation-fork-runtime'), projects).includes(mailbox.shard),
    false,
    'the Fork runtime must consume only the injected mailbox capability',
  )
  assert.equal([...projects.values()].some((p) => p.shard === 'execution-session-wait-causalwait'), false)
  assert.deepEqual(
    [...projects.values()].filter((p) => refShards(p, projects).includes(proof.shard)),
    [],
    'proof surface must not provide production capability',
  )
})
test.todo('WHAT[causal-wait-009] actual negative compilation and plugin wiring enforce injected wait capabilities and first process binding')
}
