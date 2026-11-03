import test from 'node:test'

{
const { default: assert } = await import('node:assert/strict')
const { mkdtempSync, mkdirSync, rmSync, writeFileSync } = await import('node:fs')
const { tmpdir } = await import('node:os')
const { join } = await import('node:path')
const { assertProductionSourcesAssigned } = await import('../../../scripts/lib/compile-shards.mjs')

test('WHAT[structured-workflow-011] untracked new production source fails compile-shard inventory', () => {
  const root = mkdtempSync(join(tmpdir(), 'wanxiang-untracked-source-'))
  try {
    const sourceRoot = join(root, 'src/Wanxiangshu')
    mkdirSync(sourceRoot, { recursive: true })
    const aggregate = join(sourceRoot, 'Wanxiangshu.fsproj')
    const trackedSource = join(sourceRoot, 'Tracked.fs')
    const untrackedSource = join(sourceRoot, 'Untracked.fs')

    writeFileSync(trackedSource, 'module Tracked\n', 'utf8')
    writeFileSync(untrackedSource, 'module Untracked\n', 'utf8')

    writeFileSync(aggregate, `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <WanxiangshuEmitProject>true</WanxiangshuEmitProject>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Tracked.fs"/>
  </ItemGroup>
</Project>\n`, 'utf8')

    assert.throws(
      () => assertProductionSourcesAssigned({
        repositoryRoot: root,
        sourceRoot,
        aggregatePath: aggregate,
        discoveredSources: new Set([trackedSource, untrackedSource]),
        shardImplementations: new Set([trackedSource]),
      }),
      /production source coverage mismatch/,
    )
  } finally {
    rmSync(root, { recursive: true, force: true })
  }
})
}

{
const { test } = await import("node:test");
const { default: assert } = await import("node:assert/strict");
const { mkdtempSync, mkdirSync, rmSync, writeFileSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join, dirname } = await import("node:path");
const { fileURLToPath } = await import("node:url");

const ROOT = dirname(fileURLToPath(import.meta.url))
const ReP = join(ROOT, '../../..')
const { check } = await import(join(ReP, 'scripts/checks/aggregate-retired.mjs'))
const fixtureRoot = () => {
  const dir = mkdtempSync(join(tmpdir(), 'aggregate-retired-fixture-'))
  mkdirSync(join(dir, 'src', 'Wanxiangshu'), { recursive: true })
  mkdirSync(join(dir, 'scripts'), { recursive: true })
  return dir
}
const writeShimShard = (dir, extraSources = []) => {
  const sources = ['Shim', ...extraSources]
  writeFileSync(
    join(dir, 'src/Wanxiangshu/Wanxiangshu.Owner.test.shim.fsproj'),
    `<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
${sources.flatMap((s) => [`    <Compile Include="${s}.fsi"/>`, `    <Compile Include="${s}.fs"/>`]).join('\n')}
  </ItemGroup>
</Project>`,
  )
  for (const s of sources) {
    writeFileSync(join(dir, 'src/Wanxiangshu', `${s}.fsi`), `namespace ${s}\n`)
    writeFileSync(join(dir, 'src/Wanxiangshu', `${s}.fs`), `namespace ${s}\nlet ${s.toLowerCase()} = 0\n`)
  }
}
const writeManifest = (dir, sources) => {
  writeFileSync(join(dir, 'src/Wanxiangshu/compile-order.txt'), sources.flatMap((s) => [`${s}.fsi`, `${s}.fs`]).join('\n') + '\n')
}

test('WHAT[structured-workflow-011] RETIRED-GATE: aggregate deleted → check stays green on minimal fixture', () => {
  const dir = fixtureRoot()
  try {
    writeShimShard(dir)
    writeManifest(dir, ['Shim'])
    writeFileSync(join(dir, 'scripts/noop.mjs'), '// nothing\n')

    const result = check({ root: dir })
    assert.deepEqual(result.issues, [])
  } finally {
    rmSync(dir, { recursive: true, force: true })
  }
})
test('WHAT[structured-workflow-011] RETIRED-GATE: resurrecting wrapper fsproj → red', () => {
  const dir = fixtureRoot()
  try {
    writeFileSync(join(dir, 'src/Wanxiangshu/Wanxiangshu.fsproj'), '<Project/>\n')
    writeFileSync(join(dir, 'scripts/noop.mjs'), '// nothing\n')
    writeFileSync(join(dir, 'src/Wanxiangshu/compile-order.txt'), '\n')

    const result = check({ root: dir })
    const resurrected = result.issues.find((issue) => issue.code === 'aggregate-resurrected')
    assert.ok(resurrected, 're-introducing the wrapper file must fail the gate')
  } finally {
    rmSync(dir, { recursive: true, force: true })
  }
})
test('WHAT[structured-workflow-011] RETIRED-GATE: a script that hard-codes the aggregate path → red', () => {
  const dir = fixtureRoot()
  try {
    writeFileSync(
      join(dir, 'scripts/build.mjs'),
      `// Script probes the retired path — the gate must flag the call.
import { existsSync } from 'node:fs'
const DEPRECATED = 'src/Wanxiangshu/Wanxiangshu.fsproj'
if (existsSync(DEPRECATED)) process.exit(2)
`,
    )
    writeFileSync(join(dir, 'src/Wanxiangshu/compile-order.txt'), '')

    const result = check({ root: dir })
    const leaked = result.issues.filter((issue) => issue.code === 'aggregate-access')
    assert.ok(leaked.length > 0, 'a script hard-coding the wrapper path must fail the gate')
    assert.ok(
      leaked.some((issue) => (issue.path ?? '').includes('build.mjs')),
      'aggregate-access must name the offending script',
    )
  } finally {
    rmSync(dir, { recursive: true, force: true })
  }
})
test('WHAT[structured-workflow-011] RETIRED-GATE: compile-order manifest missing or drifting → red', () => {
  const dir = fixtureRoot()
  try {
    writeFileSync(join(dir, 'scripts/noop.mjs'), '// nothing\n')
    writeShimShard(dir, ['Orphan'])
    // Manifest declares only Shim — Orphan.fs must flag drift.
    writeManifest(dir, ['Shim'])

    const result = check({ root: dir })
    const drift = result.issues.find((issue) => issue.code === 'order-manifest-drift')
    assert.ok(drift, 'manifest that silently drops a shard source must fail the gate')
    assert.match(drift.message, /Orphan\./)
  } finally {
    rmSync(dir, { recursive: true, force: true })
  }
})
test('WHAT[structured-workflow-011] RETIRED-GATE: unconditional resetOutputDirectory in build.mjs → red', () => {
  const dir = fixtureRoot()
  try {
    writeShimShard(dir)
    writeManifest(dir, ['Shim'])
    // Wiping dist is a full rebuild. The gate rejects the call itself;
    // there is no flag that makes it legal.
    writeFileSync(
      join(dir, 'scripts/build.mjs'),
      `const unknown = process.argv.slice(2).filter((a) => !['--plan', '--help', '-h'].includes(a))
if (unknown.length > 0) { console.error('unknown option(s)'); process.exit(1) }
import { resetOutputDirectory } from './x.mjs'
resetOutputDirectory('dist')
console.log('build done')
`,
    )

    const result = check({ root: dir })
    const red = result.issues.find((issue) => issue.code === 'full-compile-forbidden')
    assert.ok(red, 'resetOutputDirectory must fail the gate')
  } finally {
    rmSync(dir, { recursive: true, force: true })
  }
})
test('WHAT[structured-workflow-011] RETIRED-GATE: build.mjs that accepts unknown argv → red', () => {
  const dir = fixtureRoot()
  try {
    writeShimShard(dir)
    writeManifest(dir, ['Shim'])
    // A build.mjs that swallows argv entirely — the unknown-arg check must
    // catch the missing strict parse.
    writeFileSync(
      join(dir, 'scripts/build.mjs'),
      `const argv = process.argv.slice(2)
console.log('build done', argv.length)
`,
    )

    const result = check({ root: dir })
    const red = result.issues.find((issue) => issue.code === 'unknown-arg-silent')
    assert.ok(red, 'build.mjs that silently accepts unknown argv must fail the gate')
  } finally {
    rmSync(dir, { recursive: true, force: true })
  }
})
}

{
const { default: assert } = await import("node:assert/strict");
const { EventEmitter } = await import("node:events");
const { existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join, relative, resolve } = await import("node:path");
const { default: test } = await import("node:test");
const { checkSubsystems } = await import("../../../scripts/checks/subsystems.mjs");
const { planOwnerCompile, materializeOwnerCompile, compileOwnerProject } = await import("../../../scripts/lib/owner-compile.mjs");

const ROOT = resolve(import.meta.dirname, '../../..')
const SRC = join(ROOT, 'src/Wanxiangshu')
const FIXTURE = join(ROOT, 'requirements/structured-workflow/tests/fixtures/owner-project-boundary')
const inventory = checkSubsystems()
assert.ok(inventory.ok, inventory.violations.join('\n'))
function productionProject(projectName) {
  const project = inventory.projects.get(join(SRC, projectName))
  assert.ok(project, `missing production compile shard ${projectName}`)
  return project
}
function compileItems(projectName) {
  const project = productionProject(projectName)
  return [...project.signatureFiles, ...project.implementationFiles].map((path) => relative(SRC, path))
}
function references(projectName) {
  return productionProject(projectName).references.map((path) => relative(SRC, path))
}

test('WHAT[structured-workflow-011] flattened Fable emitter mirrors compile-shard source coverage', () => {
  // W5 cutover: the wrapper aggregate is gone. The canonical compile-order
  // manifest is the final answer to "what does one real Fable call compile";
  // the compile-shard inventory already enforces coverage + DAG shape.
  const order = readFileSync(join(SRC, 'compile-order.txt'), 'utf8')
  const orderedSources = order.split(/\r?\n/).map((line) => line.trim()).filter((line) => line.endsWith('.fs'))
  assert.ok(orderedSources.length > 0)
  const inventorySources = [...inventory.compileInventory.projects.values()].flatMap((entry) => entry.implementationFiles)
  assert.equal(
    new Set(orderedSources.map((line) => join(SRC, line))).size,
    new Set(inventorySources).size,
    'manifest must cover exactly the shard-declared production sources',
  )

  const props = readFileSync(join(SRC, 'Directory.Build.props'), 'utf8')
  assert.match(props, /<DisableTransitiveProjectReferences>true<\/DisableTransitiveProjectReferences>/)
})
test('WHAT[structured-workflow-011] subsystem ownership and compile-shard graph are complete and acyclic', () => {
  const result = inventory
  assert.equal(result.ok, true, result.violations.join('\n'))
  assert.ok(result.sourceCount > 0, 'compile-shard graph must cover production sources')
  const shardKeys = [...result.projects.values()].map((entry) => entry.shardKey)
  assert.equal(new Set(shardKeys).size, shardKeys.length, 'compile shard keys must be unique')
  for (const entry of result.compileInventory.projects.values()) {
    for (const file of entry.implementationFiles) {
      assert.equal(
        result.compileInventory.sourceProject.get(file)?.projectPath,
        entry.projectPath,
        'every production source must be owned by exactly its compiling shard',
      )
    }
  }
})
}

{
const { default: assert } = await import("node:assert/strict");
const { mkdtempSync, readFileSync, rmSync, writeFileSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { basename, join, resolve } = await import("node:path");
const { default: test } = await import("node:test");
const { buildSubsystemInventory } = await import("../../../scripts/checks/subsystems.mjs");
const { readCompileShardInventory } = await import("../../../scripts/lib/compile-shards.mjs");

const ROOT = resolve(import.meta.dirname, '../../..')
const project = (inventory, name) => {
  const match = [...inventory.projects.values()].find((entry) => basename(entry.projectPath) === name)
  assert.ok(match, `missing compile shard ${name}`)
  return match
}
const sources = (entry) => entry.implementationFiles.map((path) => path.replace(ROOT.replace(/\\/g, '/') + '/', '').replace(/\\/g, '/')).sort()
const refs = (entry) => entry.references.map((path) => basename(path)).sort()
const fakeShard = (name, { subsystem = '', shard = '', owner = '', locality = '', refs = [] } = {}) => ({
  projectPath: `/fake/${name}`,
  projectRepoPath: `src/Wanxiangshu/${name}`,
  explicitSubsystem: subsystem,
  explicitCompileShard: shard,
  legacyOwner: owner,
  legacyLocality: locality,
  references: refs.map((ref) => `/fake/${ref}`),
})
const fakeCompileInventory = (entries) => ({
  projects: new Map(entries.map((entry) => [entry.projectPath, entry])),
  sourceCount: entries.length,
  projectReferenceCount: entries.reduce((sum, entry) => sum + entry.references.length, 0),
})
const fakePolicy = (ids, legacyPairs = []) => ({ ids: new Set(ids), legacyOwnerSubsystem: new Map(legacyPairs) })
const SHARD_XML = ({ owner, locality, refs = [], files = [] }) => `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <WanxiangshuSemanticOwner>${owner}</WanxiangshuSemanticOwner>
    <WanxiangshuOwnerLocality>${locality}</WanxiangshuOwnerLocality>
    <WanxiangshuOwnerLocalityKind>contract</WanxiangshuOwnerLocalityKind>
  </PropertyGroup>
  <ItemGroup>
${[...refs.map((ref) => `    <ProjectReference Include="${ref}" />`), ...files.map((file) => `    <Compile Include="${file}" />`)].join('\n')}
  </ItemGroup>
</Project>
`
const AGGREGATE_XML = (files) => `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
${files.map((file) => `    <Compile Include="${file}" />`).join('\n')}
  </ItemGroup>
</Project>
`
const makeShardRepo = (shards) => {
  const dir = mkdtempSync(join(tmpdir(), 'wanxiangshu-subsystem-boundaries-'))
  const aggregate = []
  for (const shard of shards) {
    const files = []
    for (const module of shard.modules) {
      writeFileSync(join(dir, `${module}.fsi`), `module ${module}\n`, 'utf8')
      writeFileSync(join(dir, `${module}.fs`), `module ${module}\n`, 'utf8')
      files.push(`${module}.fsi`, `${module}.fs`)
    }
    aggregate.push(...files)
    writeFileSync(join(dir, shard.name), SHARD_XML({ owner: shard.owner, locality: shard.locality, refs: shard.refs ?? [], files }), 'utf8')
  }
  writeFileSync(join(dir, 'Wanxiangshu.fsproj'), AGGREGATE_XML(aggregate), 'utf8')
  return dir
}
const readRepo = (dir) => readCompileShardInventory({ repositoryRoot: dir, sourceRoot: dir, aggregatePath: join(dir, 'Wanxiangshu.fsproj') })
const withShardRepo = (t, shards) => {
  const dir = makeShardRepo(shards)
  t.after(() => rmSync(dir, { recursive: true, force: true }))
  return dir
}

test('WHAT[structured-workflow-011] subsystem is the only semantic governance identity', (t) => {
  const inventory = buildSubsystemInventory()
  assert.equal(inventory.ok, true, inventory.violations.join('\n'))

  for (const entry of inventory.projects.values()) {
    assert.ok(entry.subsystem, `${entry.projectRepoPath}: shard must resolve to exactly one subsystem`)
    assert.ok(entry.shard, `${entry.projectRepoPath}: shard must carry a stable shard id`)
  }
  const shardKeys = [...inventory.projects.values()].map((entry) => entry.shardKey)
  assert.equal(new Set(shardKeys).size, shardKeys.length, 'compile shard keys must be unique')
  for (const entry of inventory.compileInventory.projects.values()) {
    for (const file of entry.implementationFiles) {
      assert.equal(
        inventory.compileInventory.sourceProject.get(file)?.projectPath,
        entry.projectPath,
        'every production source must be owned by exactly its compiling shard',
      )
    }
  }

  for (const component of inventory.cyclicComponents) {
    assert.ok(component.length > 1, 'reported subsystem cycles must be real cycles')
    for (const member of component) assert.ok(inventory.subsystemIds.has(member), `cycle member ${member} must be a known subsystem`)
  }
  assert.ok(
    inventory.largestSubsystemCycle.length === 0
      || inventory.cyclicComponents.some((component) => component.join('\0') === inventory.largestSubsystemCycle.join('\0')),
    'largest subsystem cycle must be one of the reported cycles',
  )
  for (const [consumer, provider] of inventory.subsystemEdges) {
    assert.ok(
      inventory.subsystemIds.has(consumer) && inventory.subsystemIds.has(provider),
      'subsystem edges must stay inside known subsystems',
    )
  }

  const growthDir = withShardRepo(t, [
    { name: 'Wanxiangshu.Owner.test-alpha.fsproj', owner: 'test-alpha', locality: 'alpha', modules: ['Alpha'] },
  ])
  const growthPolicy = fakePolicy(['alpha', 'beta'], [['test-alpha', 'alpha'], ['test-beta', 'beta']])
  const solo = readRepo(growthDir)
  assert.equal(solo.sourceCount, 1)
  assert.equal(buildSubsystemInventory({ compileInventory: solo, policyState: growthPolicy }).ok, true)
  writeFileSync(join(growthDir, 'Beta.fsi'), 'module Beta\n', 'utf8')
  writeFileSync(join(growthDir, 'Beta.fs'), 'module Beta\n', 'utf8')
  writeFileSync(
    join(growthDir, 'Wanxiangshu.Owner.test-beta.fsproj'),
    SHARD_XML({ owner: 'test-beta', locality: 'beta', files: ['Beta.fsi', 'Beta.fs'] }),
    'utf8',
  )
  writeFileSync(join(growthDir, 'Wanxiangshu.fsproj'), AGGREGATE_XML(['Alpha.fsi', 'Alpha.fs', 'Beta.fsi', 'Beta.fs']), 'utf8')
  const grown = readRepo(growthDir)
  assert.equal(grown.sourceCount, 2)
  assert.equal(
    buildSubsystemInventory({ compileInventory: grown, policyState: growthPolicy }).ok,
    true,
    'legal growth with one more shard and source must pass instead of pinning a count',
  )

  const cycleDir = withShardRepo(t, [
    { name: 'Wanxiangshu.Owner.test-alpha.fsproj', owner: 'test-alpha', locality: 'alpha', modules: ['Alpha'], refs: ['Wanxiangshu.Owner.test-beta.fsproj'] },
    { name: 'Wanxiangshu.Owner.test-beta.fsproj', owner: 'test-beta', locality: 'beta', modules: ['Beta'], refs: ['Wanxiangshu.Owner.test-alpha.fsproj'] },
  ])
  assert.throws(() => readRepo(cycleDir), /contains a cycle/, 'mutual ProjectReference must be rejected before any verdict')

  const ringDir = withShardRepo(t, [
    { name: 'Wanxiangshu.Owner.test-alpha-one.fsproj', owner: 'test-alpha-one', locality: 'a-one', modules: ['AlphaOne'], refs: ['Wanxiangshu.Owner.test-beta.fsproj'] },
    { name: 'Wanxiangshu.Owner.test-beta.fsproj', owner: 'test-beta', locality: 'b-one', modules: ['Beta'], refs: ['Wanxiangshu.Owner.test-alpha-two.fsproj'] },
    { name: 'Wanxiangshu.Owner.test-alpha-two.fsproj', owner: 'test-alpha-two', locality: 'a-two', modules: ['AlphaTwo'] },
  ])
  const ring = buildSubsystemInventory({
    compileInventory: readRepo(ringDir),
    policyState: fakePolicy(['alpha', 'beta'], [['test-alpha-one', 'alpha'], ['test-alpha-two', 'alpha'], ['test-beta', 'beta']]),
  })
  assert.equal(ring.ok, true, ring.violations.join('\n'))
  assert.deepEqual(ring.cyclicComponents, [['alpha', 'beta']])
  assert.deepEqual(ring.largestSubsystemCycle, ['alpha', 'beta'])

  const chainDir = withShardRepo(t, [
    { name: 'Wanxiangshu.Owner.test-alpha.fsproj', owner: 'test-alpha', locality: 'alpha', modules: ['Alpha'], refs: ['Wanxiangshu.Owner.test-beta.fsproj'] },
    { name: 'Wanxiangshu.Owner.test-beta.fsproj', owner: 'test-beta', locality: 'beta', modules: ['Beta'] },
  ])
  const chain = buildSubsystemInventory({
    compileInventory: readRepo(chainDir),
    policyState: fakePolicy(['alpha', 'beta'], [['test-alpha', 'alpha'], ['test-beta', 'beta']]),
  })
  assert.equal(chain.ok, true, chain.violations.join('\n'))
  assert.deepEqual(chain.cyclicComponents, [])
  assert.deepEqual(chain.largestSubsystemCycle, [])

  const duplicateDir = withShardRepo(t, [
    { name: 'Wanxiangshu.Owner.test-alpha.fsproj', owner: 'test-alpha', locality: 'alpha', modules: ['Alpha'] },
    { name: 'Wanxiangshu.Owner.test-beta.fsproj', owner: 'test-beta', locality: 'beta', modules: ['Alpha'] },
  ])
  assert.throws(() => readRepo(duplicateDir), /compiled by both/, 'a source compiled by two shards must be rejected')

  const missingDir = withShardRepo(t, [
    { name: 'Wanxiangshu.Owner.test-alpha.fsproj', owner: 'test-alpha', locality: 'alpha', modules: ['Alpha'] },
  ])
  rmSync(join(missingDir, 'Alpha.fsi'))
  assert.throws(() => readRepo(missingDir), /must carry its sibling \.fsi/, 'a source without its sibling signature must be rejected')

  const staleAggregateDir = withShardRepo(t, [
    { name: 'Wanxiangshu.Owner.test-alpha.fsproj', owner: 'test-alpha', locality: 'alpha', modules: ['Alpha'] },
    { name: 'Wanxiangshu.Owner.test-beta.fsproj', owner: 'test-beta', locality: 'beta', modules: ['Beta'] },
  ])
  writeFileSync(join(staleAggregateDir, 'Wanxiangshu.fsproj'), AGGREGATE_XML(['Alpha.fsi', 'Alpha.fs']), 'utf8')
  assert.throws(() => readRepo(staleAggregateDir), /aggregate \.fs compile set differs/, 'an aggregate missing a shard source must be rejected')

  const unassignedDir = withShardRepo(t, [
    { name: 'Wanxiangshu.Owner.test-alpha.fsproj', owner: 'test-alpha', locality: 'alpha', modules: ['Alpha'] },
  ])
  writeFileSync(join(unassignedDir, 'Stray.fsi'), 'module Stray\n', 'utf8')
  writeFileSync(join(unassignedDir, 'Stray.fs'), 'module Stray\n', 'utf8')
  assert.throws(() => readRepo(unassignedDir), /production source coverage mismatch/, 'a production source owned by no shard must be rejected')

  const stray = buildSubsystemInventory({
    compileInventory: fakeCompileInventory([
      fakeShard('Wanxiangshu.Owner.stray.fsproj', { owner: 'unknown-owner', locality: 'stray' }),
    ]),
    policyState: fakePolicy(['alpha']),
  })
  assert.equal(stray.ok, false, 'a shard with no valid subsystem must go red')
  assert.match(stray.violations.join('\n'), /has no valid subsystem|is not mapped to a subsystem/)

  const clash = buildSubsystemInventory({
    compileInventory: fakeCompileInventory([
      fakeShard('Wanxiangshu.Owner.a.fsproj', { subsystem: 'alpha', shard: 'same' }),
      fakeShard('Wanxiangshu.Owner.b.fsproj', { subsystem: 'alpha', shard: 'same' }),
    ]),
    policyState: fakePolicy(['alpha']),
  })
  assert.equal(clash.ok, false, 'a duplicated subsystem/shard identity must go red')
  assert.match(clash.violations.join('\n'), /duplicate compile shard/)
})
test('WHAT[structured-workflow-011] shared gravity wells are split by knowledge instead of copied ACLs', () => {
  const inventory = buildSubsystemInventory()
  const identity = project(inventory, 'Wanxiangshu.Owner.dispatch-protocol.foundation-identity.fsproj')
  const outcome = project(inventory, 'Wanxiangshu.Owner.dispatch-protocol.foundation-outcome.fsproj')
  const canonical = project(inventory, 'Wanxiangshu.Owner.dispatch-protocol.foundation-canonical-json.fsproj')
  const taskResult = project(inventory, 'Wanxiangshu.Owner.intra-participant-parallelism.foundation-taskresult.fsproj')
  const parallel = project(inventory, 'Wanxiangshu.Owner.intra-participant-parallelism.foundation-parallel.fsproj')
  const asyncSupport = project(inventory, 'Wanxiangshu.Owner.intra-participant-parallelism.foundation-async-support.fsproj')
  const fission = project(inventory, 'Wanxiangshu.Owner.intra-participant-parallelism.execution-fission-facts.fsproj')

  // Foundation.Quiescence used to live in its own shard; it was merged into the identity
  // vocabulary because role-free quiescence and prompt fields are still platform-tier knowledge, not a domain.
  const identitySources = sources(identity)
  assert.ok(identitySources.every((file) => file.startsWith('src/Wanxiangshu/Foundation/')), 'identity shard must contain only platform Foundation sources')
  assert.ok(identitySources.includes('src/Wanxiangshu/Foundation/Identity.fs'), 'identity shard must include Identity.fs')
  assert.ok(identitySources.includes('src/Wanxiangshu/Foundation/Quiescence.fs'), 'identity shard must include Quiescence.fs')
  assert.deepEqual(sources(outcome), ['src/Wanxiangshu/Foundation/Outcome.fs', 'src/Wanxiangshu/Foundation/OutcomeSurface.fs'])
  assert.deepEqual(sources(canonical), ['src/Wanxiangshu/Host/CanonicalJson.fs', 'src/Wanxiangshu/OpenCode/Codec/CanonicalJsonSurface.fs'])
  assert.deepEqual(sources(taskResult), ['src/Wanxiangshu/Foundation/FsToolkitFableCompat.fs', 'src/Wanxiangshu/Foundation/TaskResult.fs'])
  assert.deepEqual(sources(parallel), ['src/Wanxiangshu/Foundation/Defer.fs'])
  assert.deepEqual(sources(asyncSupport), ['src/Wanxiangshu/Foundation/AsyncSupport.fs'])
  assert.deepEqual(sources(fission), ['src/Wanxiangshu/Execution/Fission/Facts.fs'])

  assert.deepEqual(refs(canonical), [])
  assert.deepEqual(refs(taskResult), [])
  assert.deepEqual(refs(parallel), [])
  assert.deepEqual(refs(asyncSupport), [])
  assert.deepEqual(refs(fission), ['Wanxiangshu.Owner.dispatch-protocol.foundation-identity.fsproj'])
})
}
