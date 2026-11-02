import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { readCompileShardInventory } from '../../../scripts/lib/compile-shards.mjs'
import { buildSubsystemInventory } from '../../../scripts/checks/subsystems.mjs'
import { join, resolve } from 'node:path'
import test from 'node:test'
import { planOwnerCompile } from '../../../scripts/lib/owner-compile.mjs'

const ROOT = resolve(import.meta.dirname, '../../..')

const SOURCE_ROOT = join(ROOT, 'src/Wanxiangshu')

const shardInventory = readCompileShardInventory({ repositoryRoot: ROOT })

const subsystemInventory = buildSubsystemInventory({ compileInventory: shardInventory })

assert.ok(subsystemInventory.ok, subsystemInventory.violations.join('\n'))

const projects = [...subsystemInventory.projects.values()]

const SHARD_SUBSYSTEM_OVERRIDE = new Map([
  ['delegation-sync-runtime', 'application-composition'],
  ['delegation-host-adapter', 'application-composition'],
  ['delegation-pty-adapter', 'application-composition'],
  ['delegation-ledger', 'durable-composition'],
  ['delegation-recovery-runtime', 'application-composition'],
])

const requireShard = (locality) => {
  const matches = projects.filter((project) => project.shard === locality)
  assert.equal(matches.length, 1, `${locality} must resolve to exactly one compile shard`)
  const expected = SHARD_SUBSYSTEM_OVERRIDE.get(locality) ?? 'delegation'
  assert.equal(matches[0].subsystem, expected, `${locality} belongs to the ${expected} subsystem`)
  return matches[0]
}

const inspectShard = (locality) => {
  const project = requireShard(locality)
  const plan = planOwnerCompile({ projectPath: project.projectPath, aggregatePath: null })
  const sources = plan.compileItems
    .filter((path) => path.endsWith('.fs'))
    .map((path) => path.slice(SOURCE_ROOT.length + 1).replaceAll('\\', '/'))
  return { project, sources }
}

const rel = (file) => file.slice(SOURCE_ROOT.length + 1).replaceAll('\\', '/')

const IMPLEMENTATION_SOURCES = [
  /(?:^|\/)(?:Composition|Persistence|Process|OpenCode|Plugin|Sphinx|Host)\//,
  /Execution\/Delegation\/Fork\/Host\//,
  /Execution\/Delegation\/SyncDelegate\/(?:Wait|Store|Prompt|Workflow|Runtime)\.f[si]$/,
  /Execution\/Delegation\/Handle\/(?:Controller|JoinInterruptRegistry|CompletionCodec|JoinDrain)\.f[si]$/,
  /Execution\/Delegation\/ChildRecoveryWorkflow\.f[si]$/,
  /Execution\/Delegation\/.*\/OpenCode\//,
  /Execution\/Agent\/(?:Program|Errors)\.f[si]$/,
]

const visitClosure = (inventory, project, perProject) => {
  const seen = new Set()
  const visit = (path) => {
    if (seen.has(path)) return
    seen.add(path)
    const provider = inventory.projects.get(path)
    assert.ok(provider, `referenced project must exist in inventory: ${path}`)
    perProject(provider)
    for (const reference of provider.references) visit(reference)
  }
  visit(project.projectPath)
}

const closureSources = (inventory, project) => {
  const sources = []
  visitClosure(inventory, project, (provider) => sources.push(...provider.implementationFiles.map(rel)))
  return sources
}

const assertVocabularyClosure = (inventory, project, label) =>
  visitClosure(inventory, project, (provider) => {
    for (const file of provider.implementationFiles) {
      const relative = rel(file)
      for (const forbidden of IMPLEMENTATION_SOURCES) {
        assert.ok(!forbidden.test(relative), `${label} must not transitively compile implementation source ${relative}`)
      }
    }
  })

const SOURCE_BUDGETS = new Map([
  ['delegation-contract', 100],
  ['delegation-linkage-projection', 100],
  ['delegation-journal-port', 100],
  ['delegation-sync-contract', 100],
  ['delegation-fold', 185],
  ['delegation-fork-runtime', 185],
])

const ADAPTER_RATCHET = new Map([
  // 312/312 on 2026-09-25 — the durable spine and prefix shards now declare the
  // context-prefix-epoch vocabulary explicitly (Persistence/Journal, Composition/Durable and
  // Context/Prefix/Wire consume ActivePrefixEpoch/PhaseWindow/PrefixEpochProjection), which
  // carries Context/Prefix/{ProbeSelection,PhaseWindow,Projection,Epoch,Surface}.fs into this
  // adapter closure; ratchet raised 307 → 312 as the explicit accounting required by
  // WHAT[delegation-028]. The earlier 306 → 307 raise was the cognitive runtime surface.
  ['delegation-host-adapter', 312],
  ['delegation-pty-adapter', 305],
  // 47 on 2026-09-14 — this batch hoisted Runtime.fs settleCompletedFromParts
  // into a module-internal SyncDelegateInternals module (namespace-scoped files
  // cannot hold bare top-level `let`); ratchet raised one slot to hold the
  // same dependency surface.
  // 48 on 2026-09-30 — managed-session-lifecycle-001: SyncDelegate's Runtime.fs
  // now consumes AttachedRuntime.fs (the Sync kind's adapter over AttachmentLeaseCore),
  // which itself consumes AttachmentLeaseCore.fs; the two files join this adapter
  // closure, one slot above the previous 47.
  // 2026-10-04: declaring linkage-projection's real authority-model dependency
  // adds eight production sources to this recovery closure (48 -> 56).
  // Keep the measured bound explicit; the added files remain pure contracts.
  // The real SessionHostPort contract also consumes ChatExecutionKey and
  // ManagedChatAcceptanceWitness. Declaring that missing edge adds ten .fs
  // sources (56 -> 66): Context/Prefix/Candidate; Foundation/Parallel,
  // ParallelSurface, AsyncSupport; Execution/Session/ChatExecution/Facts,
  // Projection, Acceptance, Settlement; Execution/Failure/Model; and
  // Interaction/Authority/Run. These are the existing typed contracts and
  // supporting modules, not concrete Host or I/O adapters. No runtime
  // capability is introduced by recording the actual compile closure.
  ['delegation-recovery-runtime', 66],
])

test('WHAT[delegation-028] Delegation contract excludes workflow Host PTY and recovery sources', () => {
  const { sources } = inspectShard('delegation-contract')

  const forbidden = [
    /Execution\/Delegation\/SyncDelegate\/(?:Wait|Store|Prompt|Workflow|Runtime)\.fs$/,
    /Execution\/Delegation\/Fork\/Host\//,
    /Execution\/Delegation\/Handle\/(?:Controller|JoinInterruptRegistry|CompletionCodec|JoinDrain)\.fs$/,
    /Execution\/Delegation\/ChildRecoveryWorkflow\.fs$/,
    /Execution\/Delegation\/.*\/OpenCode\//,
    /Execution\/Agent\/(?:Program|Errors)\.fs$/,
    /Process\//,
  ]

  for (const pattern of forbidden) {
    assert.ok(!sources.some((source) => pattern.test(source)), `delegation contract leaks ${pattern}`)
  }
})

test('WHAT[delegation-028] recovery Snapshot consumer excludes full Host admission capabilities', () => {
  const { sources } = inspectShard('delegation-recovery-runtime')
  assert.ok(sources.includes('Execution/Delegation/ChildRecoveryWorkflow.fs'))
  assert.ok(sources.includes('Foundation/HostSessionSnapshot.fs'))
  for (const forbidden of [
    'Foundation/HostSession.fs',
    'Foundation/HostSessionPort.fs',
    'OpenCode/Host/SessionRuntimeOwner.fs',
    'Execution/Session/ChatExecution/Acceptance.fs',
    'Execution/Session/ChatExecution/Settlement.fs',
  ]) {
    assert.ok(!sources.includes(forbidden), `Snapshot recovery closure must not contain ${forbidden}`)
  }
})

test('WHAT[delegation-028] current compile plans meet declared budgets and source-placement audit', () => {
  // W5 cutover: the aggregate fsproj is gone. Count total .fs from the
  // compile-order manifest — the canonical declaration of what the build
  // actually compiles.
  const aggregateSources = readFileSync(join(SOURCE_ROOT, 'compile-order.txt'), 'utf8')
    .split(/\r?\n/).map((line) => line.trim()).filter((line) => line.endsWith('.fs')).length
  assert.ok(aggregateSources > 0, 'compile-order manifest must declare production sources')
  const fullFallbackCeiling = Math.floor(aggregateSources * 0.6)

  for (const [locality, budget] of SOURCE_BUDGETS) {
    const inspected = inspectShard(locality)
    assert.ok(inspected.sources.length <= budget, `${locality} exceeds its production source budget ${budget}`)
  }
  for (const [locality, ratchet] of ADAPTER_RATCHET) {
    const inspected = inspectShard(locality)
    assert.ok(
      inspected.sources.length <= fullFallbackCeiling,
      `${locality} exceeds the 60% full-fallback ceiling (${inspected.sources.length} > ${fullFallbackCeiling})`,
    )
    assert.ok(
      inspected.sources.length <= ratchet,
      `${locality} grew beyond its recorded ratchet ${ratchet} — revise WHAT[delegation-028] or shrink the closure`,
    )
  }

  const foldSources = inspectShard('delegation-fold').sources
  assert.ok(foldSources.includes('Execution/Delegation/DelegationFactFold.fs'))
  assert.ok(!foldSources.includes('Execution/Delegation/DelegatedToolEstimateLedger.fs'))
  assert.ok(inspectShard('delegation-ledger').sources.includes('Execution/Delegation/DelegatedToolEstimateLedger.fs'))
  const linkageOwner = projects.find(
    (project) => project.implementationFiles.includes(join(SOURCE_ROOT, 'Execution/Delegation/LinkageProjection.fs')),
  )
  assert.equal(linkageOwner?.subsystem, 'delegation', 'delegation subsystem must own LinkageProjection')
  assert.equal(linkageOwner?.shard, 'delegation-linkage-projection', 'delegation-linkage-projection shard must own LinkageProjection')
  // The vocabulary role is proven by transitive source membership, not by a declared
  // kind: an implementation source anywhere in this shard's closure is forbidden.
  assertVocabularyClosure(subsystemInventory, linkageOwner, 'delegation-linkage-projection')
  const spineProject = projects.find((project) => project.shard === 'composition-durable-projection')
  assert.ok(spineProject, 'shard composition-durable-projection must exist')
  assert.ok(
    linkageOwner && spineProject.references.includes(linkageOwner.projectPath),
    'composition-durable-projection must declare a ProjectReference to delegation-linkage-projection',
  )
  assert.ok(inspectShard('delegation-fork-runtime').sources.includes('Execution/Delegation/Fork/Runtime.fs'))
  assert.ok(inspectShard('delegation-host-adapter').sources.includes('Execution/Delegation/Fork/Host/Runtime.fs'))
  assert.ok(inspectShard('delegation-pty-adapter').sources.includes('Execution/Delegation/Fork/Host/Pty.fs'))
  assert.ok(inspectShard('delegation-recovery-runtime').sources.includes('Execution/Delegation/ChildRecoveryWorkflow.fs'))

  // DELEG-029 / DURABLE-EVENTS-023: the delegation fold owns linkage/estimate decisions and
  // emits pure changes; durable composition is the only place that assembles them back into
  // the aggregate projection. A runtime fold therefore must not name the aggregate, must not
  // close authority runs itself, and may only declare contract references.
  const foldProject = requireShard('delegation-fold')
  for (const source of foldProject.implementationFiles) {
    const text = readFileSync(source, 'utf8')
    assert.doesNotMatch(text, /\bAgentProjectionSet\b/, `${source} must not name the aggregate projection`)
    assert.doesNotMatch(text, /PromptAuthority/, `${source} must not close authority runs itself`)
    assert.doesNotMatch(text, /\bFoldRejection\b/, `${source} must not speak the durable fail-closed report`)
  }
  for (const reference of foldProject.references) {
    const provider = projects.find((project) => project.projectPath === reference)
    assert.ok(provider, `referenced project must exist in inventory: ${reference}`)
    if (provider.subsystem === 'delegation') continue
    assertVocabularyClosure(
      subsystemInventory,
      provider,
      `delegation fold foreign provider ${provider.shardKey}`,
    )
  }
  for (const spineSource of [
    'Composition/Durable/Projection.fs',
    'Composition/Durable/FoldRejection.fs',
    'Composition/Durable/Fold.fs',
  ]) {
    assert.ok(
      !foldSources.includes(spineSource),
      `delegation fold closure must not pull ${spineSource} out of the durable spine`,
    )
  }

  // The fold vocabulary is delegation-owned; applying it to the aggregate is composition's
  // single assembly point.
  const delegationProjectionOwner = projects.find((project) =>
    project.implementationFiles.includes(join(SOURCE_ROOT, 'Execution/Delegation/DelegationProjection.fs')),
  )
  assert.equal(delegationProjectionOwner?.subsystem, 'delegation', 'delegation must own its fold state vocabulary')
  assert.equal(
    delegationProjectionOwner?.shard,
    'delegation-linkage-projection',
    'delegation-linkage-projection must compile the delegation fold state vocabulary',
  )
  assertVocabularyClosure(subsystemInventory, delegationProjectionOwner, 'the delegation fold state vocabulary')
  const bridgeOwner = projects.find((project) =>
    project.implementationFiles.includes(join(SOURCE_ROOT, 'Composition/Durable/DelegationProjectionBridge.fs')),
  )
  assert.equal(bridgeOwner?.shard, 'composition-durable-fold', 'composition-durable-fold must compile the delegation bridge')
  // The bridge is the assembly point only if its shard really closes the aggregate
  // projection over the delegation fold vocabulary: its own source must name the
  // aggregate, and its closure must reach Composition/Durable/Projection.fs.
  const bridgeText = readFileSync(join(SOURCE_ROOT, 'Composition/Durable/DelegationProjectionBridge.fs'), 'utf8')
  assert.match(bridgeText, /\bAgentProjectionSet\b/, 'the delegation bridge must consume the aggregate projection')
  assert.ok(
    closureSources(subsystemInventory, bridgeOwner).includes('Composition/Durable/Projection.fs'),
    'composition-durable-fold must transitively compile the aggregate projection',
  )
})

test.todo('WHAT[delegation-028] each bounded locality actually compiles independently with only its declared transitive inputs (GAP-153)')

test('WHAT[delegation-028] delegation-029 boundary ignores locality kind labels entirely', () => {
  // Positive: erasing every kind label keeps the boundary intact — it is decided by
  // transitive source membership alone.
  const kindless = new Map(projects.map((project) => [project.projectPath, { ...project, legacyKind: '' }]))
  const kindlessInventory = { ...subsystemInventory, projects: kindless }
  assertVocabularyClosure(
    kindlessInventory,
    requireShard('delegation-linkage-projection'),
    'delegation-linkage-projection',
  )
  for (const reference of requireShard('delegation-fold').references) {
    const provider = kindless.get(reference)
    if (provider.subsystem === 'delegation') continue
    assertVocabularyClosure(kindlessInventory, provider, `delegation fold foreign provider ${provider.shardKey}`)
  }

  // Negative: a real implementation shard labelled 'contract' inside the fold's
  // reference list is still adjudicated by source membership and rejected.
  for (const masquerading of ['persistence-journal-agentjournal', 'composition-durable-projection']) {
    const foreign = projects.find((project) => project.shard === masquerading)
    assert.ok(foreign, `real implementation provider must exist: ${masquerading}`)
    const mutated = new Map(kindless)
    mutated.set(foreign.projectPath, { ...foreign, legacyKind: 'contract' })
    const fold = requireShard('delegation-fold')
    mutated.set(fold.projectPath, { ...fold, references: [...fold.references, foreign.projectPath] })
    const mutatedInventory = { ...subsystemInventory, projects: mutated }
    assert.throws(
      () => assertVocabularyClosure(mutatedInventory, mutated.get(fold.projectPath), 'delegation-fold'),
      /must not transitively compile implementation source/,
      `a ${masquerading} shard masquerading as a contract must be rejected inside the fold closure`,
    )
  }

  // Indirect masquerade: an implementation shard hiding behind a same-subsystem
  // provider is equally rejected — the check walks transitive references.
  const journal = projects.find((project) => project.shard === 'persistence-journal-agentjournal')
  const intermediary = projects.find((project) => project.shard === 'delegation-journal-port')
  assert.ok(journal && intermediary, 'journal provider and delegation intermediary must exist')
  const throughDelegation = new Map(kindless)
  throughDelegation.set(intermediary.projectPath, {
    ...intermediary,
    references: [...intermediary.references, journal.projectPath],
  })
  assert.throws(
    () => assertVocabularyClosure({ ...subsystemInventory, projects: throughDelegation }, intermediary, 'delegation-journal-port'),
    /must not transitively compile implementation source Persistence\/Journal\//,
  )
})
