import assert from 'node:assert/strict'
import { basename, join, resolve } from 'node:path'
import test from 'node:test'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { readCompileShardInventory } from '../../../scripts/lib/compile-shards.mjs'
import { buildSubsystemInventory } from '../../../scripts/checks/subsystems.mjs'
import { planOwnerCompile, compileOwnerProject } from '../../../scripts/lib/owner-compile.mjs'
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

const productionSources = (plan) => plan.compileItems
  .filter((path) => path.endsWith('.fs'))
  .map((path) => path.slice(SOURCE_ROOT.length + 1).replaceAll('\\', '/'))

const CONTRACT_SHARDS = [
  'eventstore-model-contract',
  'eventstore-port-contract',
  'eventstore-event-vocabulary-contract',
  'eventstore-git-contract',
  'strength-event-vocabulary-contract',
  'casebook-event-vocabulary-contract',
  'js-transaction-event-vocabulary-contract',
]

const FOCUSED_RUNTIME_SHARDS = [
  'eventstore-core-runtime',
  'eventstore-git-runtime',
]

const ALLOWED_CONTRACT_CLOSURE_SHARDS = new Set([
  'eventstore-model-contract',
  'eventstore-port-contract',
  'eventstore-event-vocabulary-contract',
  'eventstore-git-contract',
  'strength-event-vocabulary-contract',
  'casebook-event-vocabulary-contract',
  'js-transaction-event-vocabulary-contract',
  'identity',
  // Pure vocabulary tier retagged during B-series: Foundation.Outcome lives under
  // runtime-platform (the term/outcome root namespace) and is reachable from the
  // canonical model contract. This is a contract-tier provider, not a runtime artifact.
  'outcome',
  'foundation-roles',
  'runtime-platform-language',
  'runtime-platform-assemblyinfo',
])

test('WHAT[durable-events-022] EventStore contracts exclude physical and Strength runtime closure', () => {
  for (const shard of CONTRACT_SHARDS) {
    const { project, plan } = planShard(shard)
    assert.ok(
      ['persistence', 'strength', 'knowledge', 'repository-programming'].includes(project.subsystem),
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
    'strength-event-vocabulary-contract',
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
    'strength-event-vocabulary-contract',
    'casebook-event-vocabulary-contract',
    'js-transaction-event-vocabulary-contract',
  ]) {
    assert.ok(
      assemblyShards.has(domainShard),
      `eventstore-authoritative-vocabulary must contain ${domainShard} in its transitive closure`,
    )
  }

  const assemblySources = productionSources(assemblyPlan)
  assert.ok(assemblySources.includes('Strength/EventVocabulary.fs'))
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
})

test.todo('WHAT[durable-events-022] each actual bounded locality compiles as one flat project and a reversed dependency is rejected')

integrationTest('WHAT[durable-events-022] real Fable compiles the journal observation owner using its declared closure', async () => {
  const scratchRoot = mkdtempSync(join(tmpdir(), 'wxs-journal-owner-compile-'))
  try {
    const result = await compileOwnerProject({
      projectPath: join(SOURCE_ROOT, 'Wanxiangshu.Owner.verification-system.verification-eventstorewritersurface.fsproj'),
      aggregatePath: null,
      scratchRoot,
      rootPropsPath: join(ROOT, 'Directory.Build.props'),
      stdio: 'pipe',
    })
    assert.equal(result.ok, true, `journal observation owner must compile without undeclared siblings\n${result.stdout}\n${result.stderr}`)
  } finally {
    rmSync(scratchRoot, { recursive: true, force: true })
  }
})
