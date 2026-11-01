import test from 'node:test'
import assert from 'node:assert/strict'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join, resolve } from 'node:path'
import { compileOwnerProject } from '../../../scripts/lib/owner-compile.mjs'
import { integrationTest } from '../../verification-system/tests/support/tier-gate.mjs'

integrationTest('WHAT[host-boundary-026] real Fable compiles Host diagnostics with only its declared dependencies', async () => {
  const root = resolve(import.meta.dirname, '../../..')
  const scratchRoot = mkdtempSync(join(tmpdir(), 'wxs-host-diagnostics-compile-'))
  try {
    const result = await compileOwnerProject({
      projectPath: join(root, 'src/Wanxiangshu/Wanxiangshu.Owner.host-boundary.host-diagnostics-runtime.fsproj'),
      aggregatePath: null,
      scratchRoot,
      rootPropsPath: join(root, 'Directory.Build.props'),
      stdio: 'pipe',
    })
    assert.equal(result.ok, true, `declared Host diagnostics closure must compile\n${result.stdout}\n${result.stderr}`)
  } finally {
    rmSync(scratchRoot, { recursive: true, force: true })
  }
})

{
const { default: assert } = await import("node:assert/strict");
const { basename, join, resolve } = await import("node:path");
const { default: test } = await import("node:test");
const { readCompileShardInventory } = await import("../../../scripts/lib/compile-shards.mjs");
const { buildSubsystemInventory } = await import("../../../scripts/checks/subsystems.mjs");
const { planOwnerCompile } = await import("../../../scripts/lib/owner-compile.mjs");

const ROOT = resolve(import.meta.dirname, '../../..')
const SOURCE_ROOT = join(ROOT, 'src/Wanxiangshu')
const shardInventory = readCompileShardInventory({ repositoryRoot: ROOT })
const subsystemInventory = buildSubsystemInventory({ compileInventory: shardInventory })
assert.ok(subsystemInventory.ok, subsystemInventory.violations.join('\n'))
const projectMetadata = [...subsystemInventory.projects.values()].map((project) => ({
  ...project,
  path: project.projectPath,
  name: basename(project.projectPath),
  compile: project.implementationFiles.map((path) => path.slice(SOURCE_ROOT.length + 1).replaceAll('\\', '/')),
}))
const requireShard = (shard) => {
  const matches = projectMetadata.filter((project) => project.shard === shard)
  assert.equal(matches.length, 1, `${shard} must resolve to exactly one compile shard, found ${matches.length}`)
  return matches[0]
}
const planShard = (shard) => {
  const project = requireShard(shard)
  return {
    project,
    plan: planOwnerCompile({ projectPath: project.path, aggregatePath: null }),
  }
}
const productionSources = (plan) => plan.compileItems
  .filter((path) => path.endsWith('.fs'))
  .map((path) => path.slice(SOURCE_ROOT.length + 1).replaceAll('\\', '/'))

test('WHAT[host-boundary-026] host session contract compiles independently without runtime or sphinx dependencies', () => {
  const { plan } = planShard('host-session-contract')
  const sources = productionSources(plan)

  assert.ok(sources.includes('Foundation/HostSession.fs'), 'host-session-contract must include SessionContract.fs')
  assert.ok(sources.includes('Foundation/HostSessionPort.fs'), 'host-session-contract must include SessionHostPort.fs')
  assert.ok(sources.includes('Foundation/HostSessionSnapshot.fs'), 'host-session-contract must include SessionSnapshot.fs')
  const forbidden = [
    'OpenCode/Codec/ToolHostCodec.fs',
    'OpenCode/Codec/ToolHostSurface.fs',
    'OpenCode/Codec/HostEventCodec.fs',
    'OpenCode/Signals/HostSignalAdapter.fs',
    'OpenCode/Signals/HostSignalSubscribe.fs',
    'OpenCode/Host/Events.fs',
    'OpenCode/Host/TerminalWiring.fs',
    'OpenCode/Host/TerminalWiring.fsi',
    'Foundation/Diagnostic.fs',
    'OpenCode/Host/ReliabilityDiagnostics.fs',
    'OpenCode/Host/ReliabilityDiagnosticsSurface.fs',
    'OpenCode/Host/HookPolicy.fs',
    'OpenCode/Host/HookPolicySurface.fs',
    'OpenCode/Host/Sessions.fs',
    'OpenCode/Host/SessionSnapshotPort.fs',
    'OpenCode/Host/SessionSnapshotSurface.fs',
    'OpenCode/Host/SessionQuiescenceGate.fs',
    'OpenCode/Host/QuiescenceSurface.fs',
    'OpenCode/Host/HostSessionContext.fs',
    'OpenCode/Host/HostBoundarySurface.fs',
    'OpenCode/Host/HostSessionContextSurface.fs',
  ]

  for (const item of forbidden) {
    assert.ok(!sources.includes(item), `host-session-contract closure must not contain ${item}`)
  }

  const forbiddenProcessRuntime = [
    'Process/ProcessRequest.fs',
    'Process/JsSandbox.fs',
    'OpenCode/Tools/PtyTool.fs',
  ]
  for (const item of forbiddenProcessRuntime) {
    assert.ok(!sources.includes(item), `host-session-contract closure must not contain process runtime ${item}`)
  }

  assert.ok(!sources.some((s) => s.startsWith('Sphinx/')), 'host-session-contract closure must not contain Sphinx runtime')

  for (const consumer of ['opencode-host-opencodeport', 'strength-policy']) {
    const consumerSources = productionSources(planShard(consumer).plan)
    assert.ok(consumerSources.includes('OpenCode/Codec/OpencodeTypes.fs'))
    for (const unrelated of ['Foundation/TerminalSignal.fs', 'Foundation/HostMessage.fs']) {
      assert.ok(!consumerSources.includes(unrelated), `${consumer} must not acquire ${unrelated}`)
    }
  }
  const portSources = productionSources(planShard('opencode-host-opencodeport').plan)
  assert.ok(!portSources.includes('Host/Digest.fs'), 'OpenCode port contract must not acquire Host/Digest.fs')

  for (const consumer of ['host-signal-contract', 'delegation-sync-runtime', 'host-diagnostics-runtime', 'opencode-host-messagevisibility']) {
    const consumerSources = productionSources(planShard(consumer).plan)
    for (const unrelated of ['Host/Digest.fs', 'OpenCode/Codec/OpencodeTypes.fs', 'Foundation/HostMessage.fs']) {
      assert.ok(!consumerSources.includes(unrelated), `${consumer} must not acquire ${unrelated}`)
    }
  }
  for (const consumer of ['host-diagnostics-runtime', 'opencode-host-messagevisibility']) {
    const consumerSources = productionSources(planShard(consumer).plan)
    assert.ok(!consumerSources.includes('Foundation/TerminalSignal.fs'), `${consumer} must not acquire terminal event vocabulary`)
  }

  const adapterSources = productionSources(planShard('host-signal-adapter').plan)
  for (const required of ['Execution/Failure/Model.fs', 'Execution/Session/ChatExecution/Facts.fs', 'Persistence/Journal/RuntimePath.fs']) {
    assert.ok(adapterSources.includes(required), `signal adapter must compile its actual dependency ${required}`)
  }
  for (const unrelated of ['OpenCode/Codec/OpencodeTypes.fs', 'Foundation/HostMessage.fs']) {
    assert.ok(!adapterSources.includes(unrelated), `signal adapter must not acquire ${unrelated}`)
  }
})
test('WHAT[host-boundary-026] Host source ownership follows subsystem inventory and physical boundaries', () => {
  const hostSources = [
    // TerminalSignal (formerly EventContract) was retagged to runtime-platform next to Digest/Quiescence; physical
    // host boundary ownership only applies to protocol/codec/adapter sources.
    'Foundation/HostMessage.fs',
    'OpenCode/Codec/ToolHostCodec.fs',
    'OpenCode/Codec/ToolHostSurface.fs',
    'Foundation/Diagnostic.fs',
    'OpenCode/Signals/HostSignalAdapter.fs',
    'OpenCode/Host/SessionQuiescenceGate.fs',
  ]
  for (const source of hostSources) {
    const owner = shardInventory.sourceProject.get(join(SOURCE_ROOT, source))
    assert.ok(owner, `${source} must have a unique production shard`)
    assert.equal(subsystemInventory.projects.get(owner.projectPath).subsystem, 'host', `${source} belongs to the Host subsystem`)
  }
  // SessionContract was retagged to provider because its shape carries provider request-kind
  // types and the dispatch layer consumes it through the boundary; keeping it in host would
  // drag provider-typed data down into the physical codec tier.
  const contractSource = shardInventory.sourceProject.get(join(SOURCE_ROOT, 'Foundation/HostSession.fs'))
  assert.ok(contractSource, 'SessionContract.fs must have a unique production shard')
  assert.equal(
    subsystemInventory.projects.get(contractSource.projectPath).subsystem,
    'provider',
    'SessionContract retagged to provider per layer direction'
  )
  const digestOwner = shardInventory.sourceProject.get(join(SOURCE_ROOT, 'Host/Digest.fs'))
  assert.ok(digestOwner, 'HostDigest must have a unique production shard')
  assert.equal(subsystemInventory.projects.get(digestOwner.projectPath).subsystem, 'runtime-platform')
  const eventContractSource = shardInventory.sourceProject.get(join(SOURCE_ROOT, 'Foundation/TerminalSignal.fs'))
  assert.ok(eventContractSource, 'TerminalSignal.fs (formerly OpenCode/Signals/EventContract.fs) must have a unique production shard')
  assert.equal(
    subsystemInventory.projects.get(eventContractSource.projectPath).subsystem,
    'runtime-platform',
    'EventContract retagged to runtime-platform',
  )
  // OpencodeTypes is a raw SDK decode record — retagged to provider because it's pure provider
  // vocabulary; physical host ownership only applies to the adapter surfaces around it.
  const opencodeTypesSource = shardInventory.sourceProject.get(join(SOURCE_ROOT, 'OpenCode/Codec/OpencodeTypes.fs'))
  assert.ok(opencodeTypesSource, 'OpencodeTypes.fs must have a unique production shard')
  assert.equal(
    subsystemInventory.projects.get(opencodeTypesSource.projectPath).subsystem,
    'provider',
    'OpencodeTypes retagged to provider',
  )

  const sessionContract = requireShard('host-session-contract')
  assert.deepEqual(
    sessionContract.compile.sort(),
    [
      'Foundation/HostSession.fs',
      'Foundation/HostSessionPort.fs',
      'OpenCode/Host/SessionRuntimeOwner.fs',
    ].sort(),
  )

  const snapshotContract = requireShard('host-session-snapshot-contract')
  assert.deepEqual(snapshotContract.compile, ['Foundation/HostSessionSnapshot.fs'])
  const snapshotOwner = shardInventory.sourceProject.get(join(SOURCE_ROOT, 'Foundation/HostSessionSnapshot.fs'))
  assert.equal(snapshotOwner.projectPath, snapshotContract.path, 'Snapshot has its own unique contract owner')
  assert.equal(snapshotContract.subsystem, 'provider')
  const snapshotSources = productionSources(planShard('host-session-snapshot-contract').plan)
  for (const unrelated of [
    'Foundation/HostSession.fs',
    'Foundation/HostSessionPort.fs',
    'OpenCode/Host/SessionRuntimeOwner.fs',
    'Execution/Session/ChatExecution/Acceptance.fs',
    'Execution/Session/ChatExecution/Settlement.fs',
  ]) {
    assert.ok(!snapshotSources.includes(unrelated), `Snapshot read contract must not acquire ${unrelated}`)
  }

  const diagnosticsRuntime = requireShard('host-diagnostics-runtime')
  assert.ok(diagnosticsRuntime.compile.includes('OpenCode/Host/HookPolicy.fs'))
  assert.ok(diagnosticsRuntime.compile.includes('OpenCode/Host/ReliabilityDiagnostics.fs'))
  assert.ok(diagnosticsRuntime.compile.includes('Foundation/Diagnostic.fs'))

  const signalAdapter = requireShard('host-signal-adapter')
  assert.ok(signalAdapter.compile.includes('OpenCode/Signals/HostSignalAdapter.fs'))
  assert.ok(signalAdapter.compile.includes('OpenCode/Signals/HostSignalSubscribe.fs'))
  assert.ok(signalAdapter.compile.includes('OpenCode/Host/Events.fs'))
  assert.ok(signalAdapter.compile.includes('OpenCode/Host/TerminalWiring.fs'))
  // .fsi is interface, compile list only includes implementation files
    assert.ok(signalAdapter.compile.includes('OpenCode/Host/TerminalWiring.fs'))
  assert.ok(!signalAdapter.compile.includes('OpenCode/Host/SharedTerminalBus.fs'))
  assert.ok(!signalAdapter.compile.includes('Execution/Session/TerminalRegistry.fs'))

  const sessionRuntime = requireShard('host-session-runtime')
  assert.ok(sessionRuntime.compile.includes('OpenCode/Host/SessionQuiescenceGate.fs'))
  assert.ok(sessionRuntime.compile.includes('OpenCode/Host/QuiescenceSurface.fs'))
  assert.ok(sessionRuntime.compile.includes('OpenCode/Host/HostSessionContext.fs'))


  // Unique production ownership, sibling signatures and aggregate coverage are
  // enforced by readCompileShardInventory for every shard, including explicit ones.

  // Verify delegation ref migration in host-boundary consumers
  const sharedStateSurface = projectMetadata.find((p) => p.name === 'Wanxiangshu.Owner.host-boundary.opencode-host-sharedstatesurface.fsproj')
  assert.ok(sharedStateSurface, 'opencode-host-sharedstatesurface.fsproj must exist')
  assert.ok(
    !sharedStateSurface.references.some((r) => r.includes('execution-delegation-handle-surface')),
    'opencode-host-sharedstatesurface must not reference old delegation-handle-surface',
  )

  const hostSignalBootstrap = projectMetadata.find((p) => p.name === 'Wanxiangshu.Owner.host-boundary.opencode-host-hostsignalbootstrap.fsproj')
  assert.ok(hostSignalBootstrap, 'opencode-host-hostsignalbootstrap.fsproj must exist')
  // Physical truth: the plugin composition chain (PluginHooks/PluginSessionWiring) consumes the
  // delegation ledger and now lives in opencode-plugin.plugin-composition (asserted below);
  // bootstrap still consumes SyncDelegateHostObservation (hostturnobservedsurface).
  // SyncDelegateRuntime Host integration lives in delegation-host-adapter while
  // Wait/Store/Prompt/Workflow stay in delegation-sync-runtime (see AGENTS delegation split).
  assert.ok(
    hostSignalBootstrap.references.some((r) => r.includes('execution-delegation-hostturnobservedsurface')),
    'opencode-host-hostsignalbootstrap must reference the delegation host-turn-observed surface',
  )
  // The plugin composition chain (PluginHooks/PluginSessionWiring) consumes the delegation
  // ledger and moved to the opencode-plugin owner; the consuming composition must reference it.
  const pluginComposition = projectMetadata.find((p) => p.name === 'Wanxiangshu.Owner.opencode-plugin.plugin-composition.fsproj')
  assert.ok(pluginComposition, 'opencode-plugin.plugin-composition.fsproj must exist')
  assert.ok(
    pluginComposition.references.some((r) => r.includes('execution-delegation-ledger')),
    'opencode-plugin.plugin-composition must reference the persistence-backed delegation ledger',
  )
  assert.ok(
    !hostSignalBootstrap.references.some((r) => r.includes('delegation-host-adapter')),
    'opencode-host-hostsignalbootstrap must not bypass plugin runtime composition to the delegation Host adapter',
  )
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { default: path } = await import("node:path");
const { fileURLToPath } = await import("node:url");
const { readCompileShardInventory } = await import("../../../scripts/lib/compile-shards.mjs");
const { buildSubsystemInventory } = await import("../../../scripts/checks/subsystems.mjs");
const HostSignalSurface = await import("../../../dist/OpenCode/Host/HostSignalSurface.js");
const { assertEffectIsInjected, assertFatalBoundary, assertOptionalObservationNoninterference, assertPureContract } = await import("../../structured-workflow/tests/support/m6-boundary-proof.mjs");

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..', '..')
const requireShard = (projects, shardId) => {
  const matches = [...projects.values()].filter((candidate) => candidate.shard === shardId)
  assert.equal(matches.length, 1, `${shardId} must resolve to exactly one production compile shard`)
  return matches[0]
}
const relSources = (project) => project.implementationFiles.map((p) => path.relative(ROOT, p)).sort()
const refShards = (project, projects) => project.references.map((refPath) => projects.get(refPath).shard).sort()
const closureSources = (root, projects) => {
  const closure = new Set()
  const pending = [root]
  while (pending.length > 0) {
    const project = pending.pop()
    if (closure.has(project)) continue
    closure.add(project)
    for (const refPath of project.references) {
      pending.push(projects.get(refPath))
    }
  }
  return new Set([...closure].flatMap(relSources))
}

test('WHAT[host-boundary-026] tool registration compiles without signal routing or terminal bus implementations', () => {
  const shardInventory = readCompileShardInventory({ repositoryRoot: ROOT })
  const subsystemInventory = buildSubsystemInventory({ compileInventory: shardInventory })
  assert.ok(subsystemInventory.ok, subsystemInventory.violations.join('\n'))
  const projects = subsystemInventory.projects

  const tool = [...projects.values()].find((entry) => relSources(entry).includes('src/Wanxiangshu/OpenCode/Codec/ToolHostCodec.fs'))
  assert.ok(tool, 'tool codec must have a production compile shard')

  const sources = closureSources(tool, projects)
  assert.ok(sources.has('src/Wanxiangshu/Host/Contract/ToolResultBound.fs'))
  for (const unrelated of [
    'src/Wanxiangshu/OpenCode/Codec/HostEventCodec.fs',
    'src/Wanxiangshu/OpenCode/Signals/HostSignal.fs',
    'src/Wanxiangshu/OpenCode/Signals/HostSignalAdapter.fs',
    'src/Wanxiangshu/OpenCode/Host/Events.fs',
    'src/Wanxiangshu/OpenCode/Host/SharedTerminalBus.fs',
    'src/Wanxiangshu/OpenCode/Host/TerminalWiring.fs',
    'src/Wanxiangshu/Execution/Failure/Model.fs',
    'src/Wanxiangshu/Persistence/Journal/RuntimePath.fs',
  ])
    assert.ok(!sources.has(unrelated), `tool adapter must not acquire ${unrelated}`)

  for (const id of [
    'opencode-tools-executortoolsurface',
    'opencode-tools-bookkeepertool',
    'opencode-tools-fetchtool',
  ]) {
    const consumerSources = closureSources(requireShard(projects, id), projects)
    assert.ok(consumerSources.has('src/Wanxiangshu/OpenCode/Codec/ToolHostCodec.fs'))
    for (const unrelated of [
      'src/Wanxiangshu/OpenCode/Codec/HostEventCodec.fs',
      'src/Wanxiangshu/OpenCode/Signals/HostSignal.fs',
      'src/Wanxiangshu/OpenCode/Host/Events.fs',
      'src/Wanxiangshu/OpenCode/Host/SharedTerminalBus.fs',
      'src/Wanxiangshu/OpenCode/Host/TerminalWiring.fs',
    ])
      assert.ok(!consumerSources.has(unrelated), `${id} must not acquire ${unrelated}`)
  }

  const signalSources = closureSources(requireShard(projects, 'host-signal-adapter'), projects)
  assert.ok(!signalSources.has('src/Wanxiangshu/OpenCode/Codec/ToolHostCodec.fs'), 'signal adapter must not acquire tool registration')
})
}
