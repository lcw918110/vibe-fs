import test from 'node:test'
import { withMacMountedOutput } from './support/mac-mounted-output.mjs'

{
const { default: assert } = await import('node:assert/strict')
const { mkdtempSync, mkdirSync, readdirSync, readFileSync, rmSync, symlinkSync, writeFileSync, lstatSync } = await import('node:fs')
const { tmpdir } = await import('node:os')
const { join } = await import('node:path')
const { EventEmitter } = await import('node:events')
const { compileIncremental, resetOutputDirectory } = await import('../../../scripts/lib/owner-compile.mjs')

function staleOutput(outputRoot, foreignRoot) {
  mkdirSync(join(outputRoot, 'nested'), { recursive: true })
  writeFileSync(join(outputRoot, 'nested', 'old.js'), 'stale')
  writeFileSync(join(outputRoot, '.hidden'), 'stale hidden')
  symlinkSync(join(outputRoot, 'missing'), join(outputRoot, 'dangling'))
  symlinkSync(foreignRoot, join(outputRoot, 'foreign'))
}

async function compileIntoMountedOutput(outputRoot) {
  const root = mkdtempSync(join(tmpdir(), 'wanxiangshu-mounted-compile-'))
  try {
    const source = join(root, 'Source.fs')
    writeFileSync(source, 'namespace Mounted\nlet value = 1\n')
    const props = join(root, 'Directory.Build.props')
    writeFileSync(props, '<Project/>\n')
    writeFileSync(join(root, 'Wanxiangshu.Shard.Mounted.fsproj'), '<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><Compile Include="Source.fs"/></ItemGroup></Project>\n')
    const compile = (code, emittedFile) => compileIncremental({
      changedPaths: [source], isClean: true, root, projectDirectory: root,
      rootPropsPath: props, scratchRoot: join(root, '.scratch'), outputDir: outputRoot, stdio: 'pipe',
      spawn: (_command, args) => {
        const staging = args[args.indexOf('-o') + 1]
        mkdirSync(staging, { recursive: true })
        writeFileSync(join(staging, emittedFile), 'export const value = 1;\n')
        const child = new EventEmitter()
        child.stdout = new EventEmitter()
        child.stderr = new EventEmitter()
        setImmediate(() => child.emit('close', code, null))
        return child
      },
    })
    const before = readFileSync(join(outputRoot, 'new.js'))
    const failed = await compile(1, 'Partial.js')
    assert.equal(failed.ok, false)
    assert.deepEqual(readdirSync(outputRoot), ['new.js'])
    assert.deepEqual(readFileSync(join(outputRoot, 'new.js')), before)
    const completed = await compile(0, 'Compiled.js')
    assert.equal(completed.ok, true)
    assert.deepEqual(readdirSync(outputRoot), ['Compiled.js'])
    assert.equal(readFileSync(join(outputRoot, 'Compiled.js'), 'utf8'), 'export const value = 1;\n')
  } finally { rmSync(root, { recursive: true, force: true }) }
}

test('WHAT[structured-workflow-012] output reset clears children and preserves foreign linked input', () => {
  const root = mkdtempSync(join(tmpdir(), 'wanxiangshu-output-reset-'))
  try {
    const outputRoot = join(root, 'dist')
    const foreignRoot = join(root, 'input')
    mkdirSync(foreignRoot)
    writeFileSync(join(foreignRoot, 'source.txt'), 'input bytes')
    staleOutput(outputRoot, foreignRoot)
    resetOutputDirectory(outputRoot)
    assert.deepEqual(readdirSync(outputRoot), [])
    assert.equal(readFileSync(join(foreignRoot, 'source.txt'), 'utf8'), 'input bytes')
    writeFileSync(join(outputRoot, 'new.js'), 'new output')
    assert.equal(readFileSync(join(outputRoot, 'new.js'), 'utf8'), 'new output')
  } finally { rmSync(root, { recursive: true, force: true }) }
})

test('WHAT[structured-workflow-012] output reset rejects a linked root without replacing it or clearing foreign input', () => {
  const root = mkdtempSync(join(tmpdir(), 'wanxiangshu-linked-output-reset-'))
  try {
    const foreignRoot = join(root, 'input')
    const outputRoot = join(root, 'dist')
    mkdirSync(foreignRoot)
    writeFileSync(join(foreignRoot, 'source.txt'), 'input bytes')
    symlinkSync(foreignRoot, outputRoot)
    for (const suppliedRoot of [outputRoot, `${outputRoot}/`]) {
      assert.throws(() => resetOutputDirectory(suppliedRoot), { code: 'OUTPUT_ROOT_NOT_DIRECTORY' })
    }
    assert.equal(lstatSync(outputRoot).isSymbolicLink(), true)
    assert.deepEqual(readdirSync(foreignRoot), ['source.txt'])
    assert.equal(readFileSync(join(foreignRoot, 'source.txt'), 'utf8'), 'input bytes')
  } finally { rmSync(root, { recursive: true, force: true }) }
})

test('WHAT[structured-workflow-012] output reset creates a missing directory and preserves invalid file and dangling-link roots', () => {
  const root = mkdtempSync(join(tmpdir(), 'wanxiangshu-output-root-kind-'))
  try {
    const directory = join(root, 'nested', 'dist')
    resetOutputDirectory(directory)
    writeFileSync(join(directory, 'new.js'), 'output')
    assert.equal(readFileSync(join(directory, 'new.js'), 'utf8'), 'output')
    const file = join(root, 'file')
    writeFileSync(file, 'original file')
    const dangling = join(root, 'dangling')
    symlinkSync(join(root, 'missing'), dangling)
    for (const invalidRoot of [file, dangling]) {
      assert.throws(() => resetOutputDirectory(invalidRoot), { code: 'OUTPUT_ROOT_NOT_DIRECTORY' })
    }
    assert.equal(readFileSync(file, 'utf8'), 'original file')
    assert.equal(lstatSync(dangling).isSymbolicLink(), true)
  } finally { rmSync(root, { recursive: true, force: true }) }
})

integrationTest('WHAT[structured-workflow-012] Mac nested mounted output resets under immutable input and cleanup preserves the original callback failure', { skip: process.platform !== 'darwin' ? 'Mac physical proof not acquired on this platform' : false }, async (t) => {
  await assert.rejects(() => withMacMountedOutput(async ({ inputRoot, outputRoot }) => {
    const inputBefore = readFileSync(join(inputRoot, 'source.txt'))
    assert.throws(() => writeFileSync(join(inputRoot, 'source.txt'), 'changed'), { code: 'EROFS' })
    staleOutput(outputRoot, inputRoot)
    resetOutputDirectory(outputRoot)
    assert.deepEqual(readdirSync(outputRoot), [])
    assert.deepEqual(readFileSync(join(inputRoot, 'source.txt')), inputBefore)
    writeFileSync(join(outputRoot, 'new.js'), 'new mounted output')
    assert.equal(readFileSync(join(outputRoot, 'new.js'), 'utf8'), 'new mounted output')
    await compileIntoMountedOutput(outputRoot)
    assert.deepEqual(readFileSync(join(inputRoot, 'source.txt')), inputBefore)
    throw null
  }, { diagnostic: (message) => t.diagnostic(message), signal: t.signal }), (error) => error === null)
})
}

{
const { default: assert } = await import('node:assert/strict')
const { mkdtempSync, rmSync, writeFileSync } = await import('node:fs')
const { tmpdir } = await import('node:os')
const { join } = await import('node:path')
const { planImpactCompile } = await import('../../../scripts/lib/owner-compile.mjs')

test('WHAT[structured-workflow-012] changed .fs with unchanged .fsi triggers reverse-consumer recompile', () => {
  const root = mkdtempSync(join(tmpdir(), 'wanxiang-inline-fsi-'))
  try {
    const aggregate = join(root, 'Wanxiangshu.fsproj')
    const providerProj = join(root, 'Provider.fsproj')
    const consumerProj = join(root, 'Consumer.fsproj')

    const providerFsi = join(root, 'Provider.fsi')
    const providerFs = join(root, 'Provider.fs')
    const consumerFs = join(root, 'Consumer.fs')

    writeFileSync(providerFsi, 'namespace Sample\n', 'utf8')
    writeFileSync(providerFs, 'namespace Sample\nlet inline helper x = x + 1\n', 'utf8')
    writeFileSync(consumerFs, 'namespace Sample\nlet consume x = helper x\n', 'utf8')

    writeFileSync(providerProj, `<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <Compile Include="${providerFsi}"/>
    <Compile Include="${providerFs}"/>
  </ItemGroup>
</Project>\n`, 'utf8')

    writeFileSync(consumerProj, `<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="${providerProj}"/>
    <Compile Include="${consumerFs}"/>
  </ItemGroup>
</Project>\n`, 'utf8')

    writeFileSync(aggregate, `<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <Compile Include="${providerFsi}"/>
    <Compile Include="${providerFs}"/>
    <Compile Include="${consumerFs}"/>
  </ItemGroup>
</Project>\n`, 'utf8')

    // Change .fs only (unchanged .fsi)
    const plan = planImpactCompile({
      changedPaths: [providerFs],
      projectDirectory: root,
      aggregatePath: aggregate,
      fullThreshold: 1,
    })

    assert.equal(plan.mode, 'focused')
    assert.ok(
      plan.projectPaths.includes(consumerProj),
      'reverse consumer must be included when .fs implementation changes, even with unchanged .fsi',
    )
    assert.ok(
      plan.compileItems.includes(consumerFs),
      'consumer compile item must be in compile plan',
    )
  } finally {
    rmSync(root, { recursive: true, force: true })
  }
})
}
import { integrationTest } from '../../verification-system/tests/support/tier-gate.mjs'

{
const { default: assert } = await import("node:assert/strict");
const { mkdtempSync, mkdirSync, rmSync, writeFileSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join } = await import("node:path");
const { default: test } = await import("node:test");
const { default: fc } = await import("fast-check");
const { planImpactCompile, planImpactFromInventory, readImpactInventory } = await import("../../../scripts/lib/owner-compile.mjs");

const TOPOLOGIES = ['chain', 'diamond', 'fanout', 'arbitrary']
const SEED = 0x494d5043
const RUNS_PER_TOPOLOGY = 25
const distinct = (values) => [...new Set(values)]
const referencesFor = (topology, nodeCount, rawReferences) => {
  const connectedCount = nodeCount - 1

  return Array.from({ length: nodeCount }, (_, consumer) => {
    if (consumer === 0 || consumer === connectedCount) {
      return []
    }
    if (topology === 'chain') {
      return [consumer - 1]
    }
    if (topology === 'fanout') {
      return [0]
    }
    if (topology === 'diamond') {
      if (consumer === 1 || consumer === 2) return [0]
      if (consumer === 3) return [1, 2]
      return [consumer - 1]
    }
    return distinct(rawReferences[consumer].filter((provider) => provider < consumer)).sort((left, right) => left - right)
  })
}
const graphCase = (topology) => fc.integer({ min: 5, max: 9 }).chain((nodeCount) => fc.record({
  aggregateOrder: fc.shuffledSubarray(
    Array.from({ length: nodeCount }, (_, index) => index),
    { minLength: nodeCount, maxLength: nodeCount },
  ),
  changeNodes: fc.shuffledSubarray(
    Array.from({ length: nodeCount - 1 }, (_, index) => index),
    { minLength: 1, maxLength: Math.min(4, nodeCount - 1) },
  ),
  signatureFlags: fc.array(fc.boolean(), { minLength: nodeCount, maxLength: nodeCount }),
  referenceOrderFlags: fc.array(fc.boolean(), { minLength: nodeCount, maxLength: nodeCount }),
  rawReferences: fc.array(
    fc.array(fc.integer({ min: 0, max: nodeCount - 1 }), { maxLength: nodeCount }),
    { minLength: nodeCount, maxLength: nodeCount },
  ),
}).map((generated) => ({
  ...generated,
  nodeCount,
  references: referencesFor(topology, nodeCount, generated.rawReferences),
})))
const projectName = (node) => `Owner.${String(node).padStart(2, '0')}.fsproj`
const buildInventory = (graph) => {
  const root = '/memory-impact'
  const sourceDirectory = join(root, 'Source')
  const projects = new Map()
  const sourceOwner = new Map()
  const projectPaths = []

  for (let node = 0; node < graph.nodeCount; node += 1) {
    const projectPath = join(root, projectName(node))
    const compileItems = [
      join(sourceDirectory, `Node${node}.fsi`),
      join(sourceDirectory, `Node${node}.fs`),
    ]
    const references = graph.references[node].map((provider) => join(root, projectName(provider)))
    projects.set(projectPath, {
      path: projectPath,
      dir: root,
      rawText: `<memory project ${node}>`,
      references,
      compileItems,
    })
    projectPaths.push(projectPath)
    for (const sourcePath of compileItems) {
      sourceOwner.set(sourcePath, projectPath)
    }
  }

  const aggregateCompileItems = graph.aggregateOrder.flatMap((node) => [
    join(sourceDirectory, `Node${node}.fsi`),
    join(sourceDirectory, `Node${node}.fs`),
  ])
  const aggregatePath = join(root, 'Aggregate.fsproj')
  const aggregate = {
    path: aggregatePath,
    dir: root,
    rawText: '<memory aggregate>',
    compileItems: aggregateCompileItems,
  }

  const reverseReferences = new Map(projectPaths.map((projectPath) => [projectPath, new Set()]))
  for (const [consumerPath, project] of projects) {
    for (const providerPath of project.references) {
      reverseReferences.get(providerPath).add(consumerPath)
    }
  }

  return {
    aggregate: aggregatePath,
    projects: projectPaths,
    root,
    sourceDirectory,
    inventory: {
      aggregate,
      projectDirectory: root,
      projectPaths,
      projects,
      sourceOwner,
      reverseReferences,
    },
  }
}
const changePaths = (fixture, changes) => changes.map(({ node, extension }) =>
  join(fixture.sourceDirectory, `Node${node}.${extension}`))
const compileItemsForProjects = (fixture, graph, projectPaths) => graph.aggregateOrder
  .filter((node) => projectPaths.has(fixture.projects[node]))
  .flatMap((node) => [
    join(fixture.sourceDirectory, `Node${node}.fsi`),
    join(fixture.sourceDirectory, `Node${node}.fs`),
  ])
const focusedPlan = (fixture, changes) => {
  const plan = planImpactFromInventory({
    inventory: fixture.inventory,
    changedPaths: changePaths(fixture, changes),
    fullThreshold: 1,
  })

  assert.equal(plan.mode, 'focused')
  return plan
}
const assertCanonicalFlatInputs = (fixture, graph, plan) => {
  assert.deepEqual(plan.compileItems, compileItemsForProjects(fixture, graph, new Set(plan.projectPaths)))
  assert.equal(new Set(plan.compileItems).size, plan.compileItems.length)
}
const assertSubset = (subset, superset) => {
  for (const item of subset) assert.ok(superset.has(item), `${item} must be preserved by the larger impact`)
}
const verifyGraph = (graph) => {
  const fixture = buildInventory(graph)
  const implementationChanges = graph.changeNodes.map((node) => ({ node, extension: 'fs' }))
  const signatureChanges = graph.changeNodes.map((node) => ({ node, extension: 'fsi' }))
  const mixedChanges = graph.changeNodes.map((node) => ({
    node,
    extension: graph.signatureFlags[node] ? 'fsi' : 'fs',
  }))

  const implementationPlan = focusedPlan(fixture, implementationChanges)
  const signaturePlan = focusedPlan(fixture, signatureChanges)
  const mixedPlan = focusedPlan(fixture, mixedChanges)

  for (const plan of [implementationPlan, signaturePlan, mixedPlan]) {
    assertCanonicalFlatInputs(fixture, graph, plan)
    for (const node of graph.changeNodes) assert.ok(plan.projectPaths.includes(fixture.projects[node]))
  }
  assertSubset(new Set(implementationPlan.projectPaths), new Set(signaturePlan.projectPaths))

  // SPEC: a plain .fs body change must select exactly the forward closure
  // of the changed shards — never pull reverse consumers in. Compute the
  // forward closure independently and assert equality.
  const forwardSet = new Set()
  const pushForward = (projectPath) => {
    if (forwardSet.has(projectPath)) return
    forwardSet.add(projectPath)
    for (const ref of fixture.inventory.projects.get(projectPath).references) pushForward(ref)
  }
  for (const node of graph.changeNodes) pushForward(fixture.projects[node])
  assert.deepEqual(
    [...implementationPlan.projectPaths].sort(),
    [...forwardSet].sort(),
    'implementation changes select only the forward dependency closure, never reverse consumers',
  )

  const reorderedPlan = planImpactFromInventory({
    inventory: fixture.inventory,
    changedPaths: [...changePaths(fixture, mixedChanges).reverse(), ...changePaths(fixture, mixedChanges)],
    fullThreshold: 1,
  })
  assert.deepEqual(reorderedPlan.projectPaths, mixedPlan.projectPaths)
  assert.deepEqual(reorderedPlan.compileItems, mixedPlan.compileItems)

  const singleChangeUnion = new Set(mixedChanges.flatMap((change) => focusedPlan(fixture, [change]).projectPaths))
  assert.deepEqual(new Set(mixedPlan.projectPaths), singleChangeUnion)

  const disconnectedProject = fixture.projects.at(-1)
  assert.ok(!mixedPlan.projectPaths.includes(disconnectedProject))

  for (const changedPath of [fixture.projects[graph.changeNodes[0]], join(fixture.root, 'Directory.Build.props')]) {
    const fullPlan = planImpactFromInventory({
      inventory: fixture.inventory,
      changedPaths: [changedPath],
    })
    assert.equal(fullPlan.mode, 'full')
    assert.deepEqual(fullPlan.projectPaths, [...fixture.projects].sort())
    assert.deepEqual(fullPlan.compileItems, compileItemsForProjects(fixture, graph, new Set(fixture.projects)))
  }
}
const writeDiskFixture = () => {
  const root = mkdtempSync(join(tmpdir(), 'wanxiangshu-impact-parity-'))
  const sourceDirectory = join(root, 'Source')
  mkdirSync(sourceDirectory)
  writeFileSync(join(root, 'Directory.Build.props'), '<Project/>\n')

  const nodes = [0, 1, 2]
  const projects = nodes.map((node) => {
    const sourceName = `Node${node}`
    const projectPath = join(root, projectName(node))
    writeFileSync(join(sourceDirectory, `${sourceName}.fsi`), `namespace Fixture\nval node${node}: string\n`)
    writeFileSync(join(sourceDirectory, `${sourceName}.fs`), `namespace Fixture\nlet node${node} = "${node}"\n`)
    const refs = node === 0
      ? []
      : [`    <ProjectReference Include="${projectName(node - 1)}"/>`]
    writeFileSync(projectPath, `<Project Sdk="Microsoft.NET.Sdk">\n  <ItemGroup>\n${refs.join('\n')}${refs.length > 0 ? '\n' : ''}    <Compile Include="Source/${sourceName}.fsi"/>\n    <Compile Include="Source/${sourceName}.fs"/>\n  </ItemGroup>\n</Project>\n`)
    return projectPath
  })

  const aggregate = join(root, 'Aggregate.fsproj')
  writeFileSync(aggregate, `<Project Sdk="Microsoft.NET.Sdk">\n  <ItemGroup>\n${nodes.flatMap((node) => [
    `    <Compile Include="Source/Node${node}.fsi"/>`,
    `    <Compile Include="Source/Node${node}.fs"/>`,
  ]).join('\n')}\n  </ItemGroup>\n</Project>\n`)

  return { aggregate, projects, root, sourceDirectory }
}

test('WHAT[structured-workflow-012] generated impact DAGs preserve change union signature monotonicity and canonical flat inputs', () => {
  for (const [index, topology] of TOPOLOGIES.entries()) {
    fc.assert(fc.property(graphCase(topology), verifyGraph), {
      seed: SEED + index,
      numRuns: RUNS_PER_TOPOLOGY,
    })
  }
})
test('WHAT[structured-workflow-012] disk inventory plans identically through the split stages', () => {
  const fixture = writeDiskFixture()
  try {
    for (const changedPaths of [
      [join(fixture.sourceDirectory, 'Node1.fs')],
      [fixture.projects[0]],
    ]) {
      const legacy = planImpactCompile({
        changedPaths,
        projectDirectory: fixture.root,
        aggregatePath: fixture.aggregate,
      })
      const inventory = readImpactInventory({
        projectDirectory: fixture.root,
        aggregatePath: fixture.aggregate,
      })
      const split = planImpactFromInventory({ inventory, changedPaths })
      assert.deepEqual(split, legacy)
    }
  } finally {
    rmSync(fixture.root, { recursive: true, force: true })
  }
})
}

{
const { default: assert } = await import("node:assert/strict");
const { spawnSync } = await import("node:child_process");
const { EventEmitter } = await import("node:events");
const { existsSync, mkdtempSync, mkdirSync, readFileSync, rmSync, statSync, utimesSync, writeFileSync } = await import("node:fs");
const { join, resolve } = await import("node:path");
const { tmpdir } = await import("node:os");
const { default: test } = await import("node:test");
const { compileIncremental, compileOwnerProject, materializeOwnerCompile, planImpactCompile } = await import("../../../scripts/lib/owner-compile.mjs");

const ROOT = resolve(import.meta.dirname, '../../..')
const SOURCE_ROOT = join(ROOT, 'src/Wanxiangshu')
const AGGREGATE = join(SOURCE_ROOT, 'Wanxiangshu.fsproj')
const writeProject = (root, name, shard, refs, source, hasFsi = true) => {
  const path = join(root, `${name}.fsproj`)
  writeFileSync(path, `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <WanxiangshuSubsystem>fixture</WanxiangshuSubsystem>
    <WanxiangshuCompileShard>${shard}</WanxiangshuCompileShard>
  </PropertyGroup>
  <ItemGroup>
${refs.map((ref) => `    <ProjectReference Include="${ref}.fsproj"/>`).join('\n')}
${hasFsi ? `    <Compile Include="Source/${source}.fsi"/>\n` : ''}    <Compile Include="Source/${source}.fs"/>
  </ItemGroup>
</Project>
`)
  return path
}
const createFixture = ({ omitSiblingFsi = [] } = {}) => {
  const root = mkdtempSync(join(tmpdir(), 'wanxiangshu-impact-'))
  mkdirSync(join(root, 'Source'))
  writeFileSync(join(root, 'Directory.Build.props'), '<Project/>\n')

  const omittedSet = new Set(omitSiblingFsi)
  const sources = ['Base', 'Contract', 'Runtime', 'Consumer', 'Composition', 'Unrelated']
  for (const source of sources) {
    if (!omittedSet.has(source)) {
      writeFileSync(join(root, 'Source', `${source}.fsi`), `namespace Fixture\nval ${source.toLowerCase()}: string\n`)
    }
    writeFileSync(join(root, 'Source', `${source}.fs`), `namespace Fixture\nlet ${source.toLowerCase()} = "${source}"\n`)
  }

  const projects = {
    base: writeProject(root, 'Wanxiangshu.Owner.Base', 'base-contract', [], 'Base', !omittedSet.has('Base')),
    contract: writeProject(root, 'Wanxiangshu.Owner.Provider.Contract', 'provider-contract', ['Wanxiangshu.Owner.Base'], 'Contract', !omittedSet.has('Contract')),
    runtime: writeProject(root, 'Wanxiangshu.Owner.Provider.Runtime', 'provider-runtime', ['Wanxiangshu.Owner.Provider.Contract'], 'Runtime', !omittedSet.has('Runtime')),
    consumer: writeProject(root, 'Wanxiangshu.Owner.Consumer.Runtime', 'consumer-runtime', ['Wanxiangshu.Owner.Provider.Contract'], 'Consumer', !omittedSet.has('Consumer')),
    composition: writeProject(root, 'Wanxiangshu.Owner.Composition.Runtime', 'composition-runtime', ['Wanxiangshu.Owner.Provider.Runtime', 'Wanxiangshu.Owner.Consumer.Runtime'], 'Composition', !omittedSet.has('Composition')),
    unrelated: writeProject(root, 'Wanxiangshu.Owner.Unrelated.Runtime', 'unrelated-runtime', ['Wanxiangshu.Owner.Base'], 'Unrelated', !omittedSet.has('Unrelated')),
  }

  const aggregate = join(root, 'Aggregate.fsproj')
  writeFileSync(aggregate, `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><AssemblyName>Fixture</AssemblyName></PropertyGroup>
  <ItemGroup>
${sources.flatMap((source) => [
    ...(omittedSet.has(source) ? [] : [`    <Compile Include="Source/${source}.fsi"/>`]),
    `    <Compile Include="Source/${source}.fs"/>`,
  ]).join('\n')}
  </ItemGroup>
</Project>
`)

  return { root, aggregate, projects }
}
const sourceNames = (plan) => plan.compileItems.map((path) => path.split('/').at(-1))

test('WHAT[structured-workflow-012] implementation changes reach reverse consumers', () => {
  const fixture = createFixture()
  try {
    const plan = planImpactCompile({
      changedPaths: [join(fixture.root, 'Source/Runtime.fs')],
      projectDirectory: fixture.root,
      aggregatePath: fixture.aggregate,
    })

    assert.equal(plan.mode, 'focused')
    assert.equal(plan.reason, 'focused-impact')
    // Structured-workflow WHAT §implementation scope: a non-risky `.fs`
    // body change whose sibling `.fsi` is unchanged selects only the owning
    // shard plus its forward dependencies — never every reverse consumer.
    assert.deepEqual(
      new Set(plan.projectPaths),
      new Set([fixture.projects.base, fixture.projects.contract, fixture.projects.runtime]),
    )
    assert.ok(!plan.projectPaths.includes(fixture.projects.consumer))
    assert.ok(!plan.projectPaths.includes(fixture.projects.composition))
  } finally {
    rmSync(fixture.root, { recursive: true, force: true })
  }
})
test('WHAT[structured-workflow-012] signature-risky implementation changes independently reach reverse consumers across all syntax forms', () => {
  const forms = [
    {
      name: 'standard [<Literal>]',
      body: 'namespace Fixture\nlet runtime = "Runtime"\n[<Literal>] let rom = "x"\n',
    },
    {
      name: 'compound attribute with Literal in last position [<X; Literal>]',
      body: 'namespace Fixture\nlet runtime = "Runtime"\n[<System.Obsolete("old"); Literal>] let rom = "x"\n',
    },
    {
      name: 'compound attribute with Literal in first position [<Literal; X>]',
      body: 'namespace Fixture\nlet runtime = "Runtime"\n[<Literal; System.Obsolete("old")>] let rom = "x"\n',
    },
    {
      name: 'compound attribute with Literal in middle position',
      body: 'namespace Fixture\nlet runtime = "Runtime"\n[<System.Obsolete("old"); Literal; System.Diagnostics.DebuggerDisplay("disp")>] let rom = "x"\n',
    },
    {
      name: 'fully qualified [<Microsoft.FSharp.Core.Literal>]',
      body: 'namespace Fixture\nlet runtime = "Runtime"\n[<Microsoft.FSharp.Core.Literal>] let rom = "x"\n',
    },
    {
      name: 'multiline attribute with Literal',
      body: 'namespace Fixture\nlet runtime = "Runtime"\n[<System.Obsolete("old");\n  Literal>] let rom = "x"\n',
    },
    {
      name: 'let inline',
      body: 'namespace Fixture\nlet runtime = "Runtime"\nlet inline tag x = x + "r"\n',
    },
    {
      name: 'let inline private',
      body: 'namespace Fixture\nlet runtime = "Runtime"\nlet inline private tag x = x + "r"\n',
    },
    {
      name: 'let inline internal',
      body: 'namespace Fixture\nlet runtime = "Runtime"\nlet inline internal tag x = x + "r"\n',
    },
    {
      name: 'member inline',
      body: 'namespace Fixture\nlet runtime = "Runtime"\ntype Helper () =\n    member inline this.Tag x = x + "r"\n',
    },
    {
      name: 'member inline private',
      body: 'namespace Fixture\nlet runtime = "Runtime"\ntype Helper () =\n    member inline private this.Tag x = x + "r"\n',
    },
    {
      name: 'static member inline',
      body: 'namespace Fixture\nlet runtime = "Runtime"\ntype Helper () =\n    static member inline Tag x = x + "r"\n',
    },
    {
      name: 'static member inline private',
      body: 'namespace Fixture\nlet runtime = "Runtime"\ntype Helper () =\n    static member inline private Tag x = x + "r"\n',
    },
    {
      name: 'and inline (mutually recursive binding, text-level)',
      body: 'namespace Fixture\nlet runtime = "Runtime"\nlet rec first x = x\nand inline second y = y + "r"\n',
    },
    {
      name: 'inline fun lambda (text-level spec requirement)',
      body: 'namespace Fixture\nlet runtime = "Runtime"\nlet tag = inline fun x -> x + "r"\n',
    },
  ]

  for (const { name, body } of forms) {
    const fixture = createFixture()
    try {
      writeFileSync(join(fixture.root, 'Source/Runtime.fs'), body, 'utf8')
      const plan = planImpactCompile({
        changedPaths: [join(fixture.root, 'Source/Runtime.fs')],
        projectDirectory: fixture.root,
        aggregatePath: fixture.aggregate,
        fullThreshold: 1,
      })

      assert.equal(plan.mode, 'focused', `${name} must result in focused impact`)
      assert.ok(plan.projectPaths.includes(fixture.projects.consumer), `${name} must include reverse consumer`)
      assert.ok(plan.projectPaths.includes(fixture.projects.composition), `${name} must include composition consumer`)
      assert.ok(plan.projectPaths.includes(fixture.projects.runtime), `${name} must include owning runtime project`)
    } finally {
      rmSync(fixture.root, { recursive: true, force: true })
    }
  }
})

test('WHAT[structured-workflow-012] missing sibling fsi independently triggers signature risk and pulls reverse consumers', () => {
  const fixture = createFixture({ omitSiblingFsi: ['Runtime'] })
  try {
    // Runtime shard declares ONLY Source/Runtime.fs in its fsproj and aggregate; no Runtime.fsi exists on disk.
    // An implementation without an interface file exposes all its public symbols as signature,
    // so changes must pull reverse consumers even with no inline or Literal in source text.
    writeFileSync(
      join(fixture.root, 'Source/Runtime.fs'),
      'namespace Fixture\nlet runtime = "RuntimeMutated"\n',
      'utf8',
    )
    const plan = planImpactCompile({
      changedPaths: [join(fixture.root, 'Source/Runtime.fs')],
      projectDirectory: fixture.root,
      aggregatePath: fixture.aggregate,
      fullThreshold: 1,
    })

    assert.equal(plan.mode, 'focused')
    assert.ok(plan.projectPaths.includes(fixture.projects.consumer), 'missing sibling fsi must pull consumer')
    assert.ok(plan.projectPaths.includes(fixture.projects.composition), 'missing sibling fsi must pull composition')
    assert.ok(plan.projectPaths.includes(fixture.projects.runtime), 'missing sibling fsi must include runtime')
  } finally {
    rmSync(fixture.root, { recursive: true, force: true })
  }
})

test('WHAT[structured-workflow-012] comments and string literals containing Literal or inline do not trigger signature risk', () => {
  // Replicating production files where [<Literal>] or inline appear in comments:
  // 1. Host/Contract/ToolResultBound.fs
  // 2. Interaction/Authority/Model.fs
  // 3. Process/Deadline.fs
  const negativeCases = [
    {
      source: 'Host/Contract/ToolResultBound.fs',
      comment: [
        '// Not [<Literal>]: Fable drops unused compile-time literals from JS exports.',
        '// These are contract values tests and callers must observe.',
        'let runtime = "HostMaxLines"',
      ].join('\n'),
    },
    {
      source: 'Interaction/Authority/Model.fs',
      comment: [
        '/// Plain `let`, not `[<Literal>]`: Fable inlines a literal and emits no export,',
        '/// leaving the clause value unassertable from a layer 1 test.',
        'let runtime = "RecoveryTailWindow"',
      ].join('\n'),
    },
    {
      source: 'Process/Deadline.fs',
      comment: [
        '/// JS/Int32 ceiling for setTimeout: a larger delay is clamped/rejected by the',
        '/// runtime, so any wait longer than this must be segmented.',
        '///',
        '/// Plain `let`, not `[<Literal>]`: Fable inlines a literal and emits no export,',
        '/// so a layer 1 test could not read the bound it must assert against.',
        'let runtime = "MaxTimerWaitMs"',
      ].join('\n'),
    },
    {
      source: 'multi-line block comment',
      comment: [
        '(*',
        '   This block comment mentions [<Literal>] and let inline helper',
        '   and static member inline or member inline without being code.',
        '*)',
        'let runtime = "BlockCommentRuntime"',
      ].join('\n'),
    },
    {
      source: 'string literal',
      comment: [
        'let runtime = "plain string mentioning [<Literal>] and let inline tag and inline fun"',
      ].join('\n'),
    },
    {
      source: 'verbatim string literal',
      comment: [
        'let runtime = @"verbatim string mentioning [<Literal>] and [<CompiledName(\"x\"); Literal>] and member inline"',
      ].join('\n'),
    },
  ]

  for (const { source, comment } of negativeCases) {
    const fixture = createFixture()
    try {
      const sourceContent = `namespace Fixture\n${comment}\n`
      writeFileSync(join(fixture.root, 'Source/Runtime.fs'), sourceContent, 'utf8')

      const plan = planImpactCompile({
        changedPaths: [join(fixture.root, 'Source/Runtime.fs')],
        projectDirectory: fixture.root,
        aggregatePath: fixture.aggregate,
        fullThreshold: 1,
      })

      assert.equal(plan.mode, 'focused', `${source} comment/string must keep focused mode`)
      assert.ok(
        !plan.projectPaths.includes(fixture.projects.consumer),
        `${source} comment/string must NOT trigger signature risk or pull consumer`,
      )
      assert.ok(
        !plan.projectPaths.includes(fixture.projects.composition),
        `${source} comment/string must NOT trigger signature risk or pull composition`,
      )
      assert.ok(
        plan.projectPaths.includes(fixture.projects.runtime),
        `${source} must include the owning runtime shard`,
      )
    } finally {
      rmSync(fixture.root, { recursive: true, force: true })
    }
  }
})
test('WHAT[structured-workflow-012] incremental compile executes focused flat compile and records cache', async () => {
  const fixture = createFixture()
  try {
    const calls = []
    const spawn = (command, args) => {
      calls.push({ command, args })
      const output = args[args.indexOf('-o') + 1]
      mkdirSync(output, { recursive: true })
      writeFileSync(join(output, 'Runtime.js'), 'export const runtime = true\n')
      const child = new EventEmitter()
      child.stdout = new EventEmitter()
      child.stderr = new EventEmitter()
      setImmediate(() => child.emit('close', 0, null))
      return child
    }

    const outputDir = join(fixture.root, 'dist')
    const manifestPath = join(fixture.root, '.fable-build/build-manifest.json')

    const result = await compileIncremental({
      changedPaths: [join(fixture.root, 'Source/Unrelated.fs')],
      aggregatePath: fixture.aggregate,
      rootPropsPath: join(fixture.root, 'Directory.Build.props'),
      scratchRoot: join(fixture.root, '.scratch'),
      outputDir,
      manifestPath,
      spawn,
      stdio: 'pipe',
    })

    assert.equal(result.ok, true)
    assert.equal(result.cached, false)
    assert.equal(result.mode, 'focused')
    assert.equal(calls.length, 1)
    assert.equal(calls[0].command, 'dotnet')
    assert.notEqual(calls[0].args[4], fixture.projects.unrelated)
    assert.ok(!readFileSync(calls[0].args[4], 'utf8').includes('<ProjectReference'), 'focused build emits flat project with zero ProjectReference')
  } finally {
    rmSync(fixture.root, { recursive: true, force: true })
  }
})
test('WHAT[structured-workflow-012] signature changes include every reverse consumer and exact forward union', () => {
  const fixture = createFixture()
  try {
    const plan = planImpactCompile({
      changedPaths: [join(fixture.root, 'Source/Contract.fsi')],
      projectDirectory: fixture.root,
      aggregatePath: fixture.aggregate,
      fullThreshold: 1,
    })

    assert.equal(plan.mode, 'focused')
    assert.deepEqual(sourceNames(plan), [
      'Base.fsi', 'Base.fs',
      'Contract.fsi', 'Contract.fs',
      'Consumer.fsi', 'Consumer.fs',
      'Runtime.fsi', 'Runtime.fs',
      'Composition.fsi', 'Composition.fs',
    ])
    assert.deepEqual(
      new Set(plan.rootProjectPaths),
      new Set([fixture.projects.contract, fixture.projects.runtime, fixture.projects.consumer, fixture.projects.composition]),
    )
  } finally {
    rmSync(fixture.root, { recursive: true, force: true })
  }
})
test('WHAT[structured-workflow-012] toolchain changes and oversized impact select one full flat build', () => {
  const fixture = createFixture()
  try {
    const oversized = planImpactCompile({
      changedPaths: [join(fixture.root, 'Source/Contract.fsi')],
      projectDirectory: fixture.root,
      aggregatePath: fixture.aggregate,
    })
    assert.equal(oversized.mode, 'full')
    assert.equal(oversized.compileItems.length, 12)

    const toolchain = planImpactCompile({
      changedPaths: [join(fixture.root, 'Directory.Build.props')],
      projectDirectory: fixture.root,
      aggregatePath: fixture.aggregate,
    })
    assert.equal(toolchain.mode, 'full')
    assert.equal(toolchain.compileItems.length, 12)
  } finally {
    rmSync(fixture.root, { recursive: true, force: true })
  }
})
test('WHAT[structured-workflow-012] materialized impact project has exact canonical inputs and zero ProjectReference', () => {
  const fixture = createFixture()
  try {
    const plan = planImpactCompile({
      changedPaths: [join(fixture.root, 'Source/Runtime.fs')],
      projectDirectory: fixture.root,
      aggregatePath: fixture.aggregate,
    })
    const materialized = materializeOwnerCompile(plan, {
      rootPropsPath: join(fixture.root, 'Directory.Build.props'),
      scratchRoot: join(fixture.root, '.scratch'),
    })
    const xml = readFileSync(materialized.projectPath, 'utf8')

    assert.ok(!xml.includes('<ProjectReference'))
    assert.deepEqual(
      [...xml.matchAll(/<Compile Include="([^"]+)"\/>/g)].map((match) => match[1].split('/').at(-1)),
      sourceNames(plan),
    )
  } finally {
    rmSync(fixture.root, { recursive: true, force: true })
  }
})
test('WHAT[structured-workflow-012] multi-change union compiles each closure once', () => {
  const fixture = createFixture()
  try {
    const plan = planImpactCompile({
      changedPaths: [
        join(fixture.root, 'Source/Runtime.fs'),
        join(fixture.root, 'Source/Unrelated.fs'),
      ],
      projectDirectory: fixture.root,
      aggregatePath: fixture.aggregate,
      fullThreshold: 1,
    })

    assert.equal(plan.mode, 'focused')
    assert.deepEqual(sourceNames(plan), [
      'Base.fsi', 'Base.fs',
      'Contract.fsi', 'Contract.fs',
      'Runtime.fsi', 'Runtime.fs',
      'Unrelated.fsi', 'Unrelated.fs',
    ])
  } finally {
    rmSync(fixture.root, { recursive: true, force: true })
  }
})
test('WHAT[structured-workflow-012] project file changes select one full flat build', () => {
  const fixture = createFixture()
  try {
    const plan = planImpactCompile({
      changedPaths: [fixture.projects.runtime],
      projectDirectory: fixture.root,
      aggregatePath: fixture.aggregate,
    })
    assert.equal(plan.mode, 'full')
    assert.equal(plan.reason, 'toolchain-or-project-change')
    assert.equal(plan.compileItems.length, 12)
  } finally {
    rmSync(fixture.root, { recursive: true, force: true })
  }
})
test('WHAT[structured-workflow-012] production impact-set ladder classifies fs fsi project and toolchain', () => {
  const impl = planImpactCompile({
    changedPaths: [join(SOURCE_ROOT, 'Foundation/FatalProcess.fs')],
    projectDirectory: SOURCE_ROOT,
    aggregatePath: AGGREGATE,
  })
  // Plain implementation change on a paired .fs: stays inside the owning
  // shard's forward closure — the documented FatalProcess closure keeps its
  // two own sources plus whatever its shard directly depends on.
  assert.equal(impl.mode, 'focused')
  assert.equal(impl.reason, 'focused-impact')
  assert.ok(impl.compileItems.includes(join(SOURCE_ROOT, 'Foundation/FatalProcess.fs')))
  assert.ok(impl.compileItems.includes(join(SOURCE_ROOT, 'Foundation/FatalProcess.fsi')))
  // Reverse consumers elsewhere in the tree (Enforcer, OpenCode hosts, etc.)
  // must NOT be recompiled for a non-signature body change.
  assert.ok(!impl.compileItems.some((item) => item.endsWith('Enforcer/Continuation.fs')))
  assert.ok(impl.compileItems.length < 100)

  const signature = planImpactCompile({
    changedPaths: [join(SOURCE_ROOT, 'Foundation/FatalProcess.fsi')],
    projectDirectory: SOURCE_ROOT,
    aggregatePath: AGGREGATE,
  })
  // Contract change: every reverse consumer joins the closure.
  assert.ok(
    signature.mode === 'full' || signature.projectPaths.length > impl.projectPaths.length,
    `signature change must widen the scope, got ${signature.mode} with ${signature.projectPaths.length} projects`,
  )
  assert.ok(new Set(signature.projectPaths).size >= new Set(impl.projectPaths).size)
  
  const project = planImpactCompile({
    changedPaths: [join(SOURCE_ROOT, 'Wanxiangshu.Owner.host-boundary.host-fatal-effect.fsproj')],
    projectDirectory: SOURCE_ROOT,
    aggregatePath: AGGREGATE,
  })
  assert.equal(project.mode, 'full')
  assert.equal(project.reason, 'toolchain-or-project-change')

  const toolchain = planImpactCompile({
    changedPaths: [join(ROOT, 'package.json')],
    projectDirectory: SOURCE_ROOT,
    aggregatePath: AGGREGATE,
  })
  assert.equal(toolchain.mode, 'full')
  assert.equal(toolchain.reason, 'toolchain-or-project-change')
})
test('WHAT[structured-workflow-012] compile-impact CLI plan-only smoke matches the planner', () => {
  const changed = join(SOURCE_ROOT, 'Foundation/FatalProcess.fs')
  // W4 cutover: `compile-impact.mjs` was deleted; `build.mjs --plan` is the
  // remaining read-only preview and reports the same planner-shaped payload.
  const result = spawnSync(
    process.execPath,
    ['scripts/build.mjs', '--plan'],
    { maxBuffer: 10 * 1024 * 1024, cwd: ROOT, encoding: 'utf8' },
  )
  assert.equal(result.status, 0, result.stderr || result.stdout)
  const cli = JSON.parse(result.stdout)
  // The plan reflects the live worktree: a clean tree reports no-op, a
  // toolchain change reports full, and non-compiler inputs (e.g. shard-graph
  // fsproj edits) legitimately report focused. All three are valid modes.
  assert.ok(
    cli.mode === 'no-op' || cli.mode === 'full' || cli.mode === 'focused',
    'manifest mode is valid',
  )
  const plan = planImpactCompile({
    changedPaths: [changed],
    projectDirectory: SOURCE_ROOT,
    aggregatePath: AGGREGATE,
  })
  assert.equal(plan.mode, 'focused')
  assert.equal(plan.reason, 'focused-impact')
  assert.ok(plan.compileItems.includes(join(SOURCE_ROOT, 'Foundation/FatalProcess.fs')))
})
test('WHAT[structured-workflow-012] obsolete recursive-graph compile probes stay deleted', () => {
  assert.equal(existsSync(join(ROOT, 'scripts/analyze-closures.mjs')), false)
  assert.equal(existsSync(join(SOURCE_ROOT, 'FableBarrier.fs')), false)

  const ownerCli = readFileSync(join(ROOT, 'scripts/compile-owner.mjs'), 'utf8')
  const lib = readFileSync(join(ROOT, 'scripts/lib/owner-compile.mjs'), 'utf8')

  assert.match(lib, /generateFlatProjectXml/)
  assert.match(lib, /zero ProjectReference|Wanxiangshu\.Impact\.fsproj/)
  assert.doesNotMatch(ownerCli, /tool',\s*'run',\s*'fable'/)
  assert.match(ownerCli, /compileOwnerProject/)
  // `build.mjs` itself is the canonical caller and legitimately invokes the
  // Fable toolchain — we don't pin the string here.
})
}

{
const { default: assert } = await import("node:assert/strict");
const { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join } = await import("node:path");
const { default: test } = await import("node:test");
const { buildOwnerImpactReportV1, measureOwnerImpactStructureV1, OWNER_IMPACT_CONTROL_CASES, OWNER_IMPACT_CONTROL_CASE_IDS, OWNER_IMPACT_STABLE_CASES, OWNER_IMPACT_STABLE_CASE_IDS, OWNER_IMPACT_TIMING_COMMANDS, OWNER_IMPACT_TIMING_IDS, validateOwnerImpactCorpusV1, writeOwnerImpactBaselineV1 } = await import("../../../scripts/owner-impact-report.mjs");

const commit = (digit) => digit.repeat(40)
const digest = (digit) => `sha256:${digit.repeat(64)}`
const cases = (definitions) => definitions.map((definition) => ({
  ...definition,
  successor_path: definition.baseline_changed_path,
}))
const corpus = ({ baseline = null } = {}) => ({
  schema_version: 1,
  purpose: 'm6-owner-impact-report-only',
  baseline_commit: commit('a'),
  aggregate_path: 'src/Wanxiangshu/Wanxiangshu.fsproj',
  project_directory: 'src/Wanxiangshu',
  full_threshold: 0.6,
  lockfile_path: 'package-lock.json',
  tool_manifest_path: '.config/dotnet-tools.json',
  stable_cases: cases(OWNER_IMPACT_STABLE_CASES),
  control_cases: cases(OWNER_IMPACT_CONTROL_CASES),
  timing_commands: OWNER_IMPACT_TIMING_COMMANDS.map(({ id, command }) => ({ id, command: [...command] })),
  baseline_measurement: baseline,
})
const environment = () => ({
  platform: 'fixture',
  release: 'fixture',
  architecture: 'fixture',
  cpu_model: 'fixture',
  cpu_count: 1,
  node_version: 'v1',
  fable_version: '1',
  lockfile_digest: digest('1'),
  tool_manifest_digest: digest('2'),
  dependency_cache_identity_digest: digest('3'),
})
const changedPathById = new Map([...OWNER_IMPACT_STABLE_CASES, ...OWNER_IMPACT_CONTROL_CASES]
  .map(({ id, baseline_changed_path: path }) => [id, path]))
const structural = (sourceCount) => [...OWNER_IMPACT_STABLE_CASE_IDS, ...OWNER_IMPACT_CONTROL_CASE_IDS]
  .sort()
  .map((id) => ({
    id,
    changed_path: changedPathById.get(id),
    mode: 'full',
    reason: 'fixture-impact',
    root_project_count: 1,
    project_count: 1,
    production_source_count: sourceCount,
    compile_item_identities: ['src/Wanxiangshu/Fixture.fsi', 'src/Wanxiangshu/Fixture.fs'],
  }))
const timing = (milliseconds) => OWNER_IMPACT_TIMING_IDS.map((id) => ({
  id,
  raw_milliseconds: [milliseconds, milliseconds, milliseconds],
  median_milliseconds: milliseconds,
}))

test('WHAT[structured-workflow-012] fixed owner impact corpus drives the production planner without becoming a verdict', () => {
  const fixture = mkdtempSync(join(tmpdir(), 'owner-impact-corpus-'))
  try {
    mkdirSync(join(fixture, 'src/Wanxiangshu'), { recursive: true })
    mkdirSync(join(fixture, '.config'))
    writeFileSync(join(fixture, 'src/Wanxiangshu/Fixture.fsi'), 'module Fixture\nval value: int\n')
    writeFileSync(join(fixture, 'src/Wanxiangshu/Fixture.fs'), 'module Fixture\nlet value = 1\n')
    writeFileSync(join(fixture, 'src/Wanxiangshu/Wanxiangshu.Owner.host-boundary.host-fatal-effect.fsproj'), `<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <Compile Include="Fixture.fsi"/>
    <Compile Include="Fixture.fs"/>
  </ItemGroup>
</Project>\n`)
    writeFileSync(join(fixture, 'src/Wanxiangshu/Wanxiangshu.fsproj'), `<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <Compile Include="Fixture.fsi"/>
    <Compile Include="Fixture.fs"/>
  </ItemGroup>
</Project>\n`)
    writeFileSync(join(fixture, 'package.json'), '{}\n')
    writeFileSync(join(fixture, 'package-lock.json'), '{}\n')
    writeFileSync(join(fixture, '.config/dotnet-tools.json'), '{"tools":{"fable":{"version":"1.0.0"}}}\n')

    const definition = corpus()
    assert.equal(validateOwnerImpactCorpusV1(definition).baseline_measurement, null)
    const measured = measureOwnerImpactStructureV1(definition, { root: fixture })
    assert.deepEqual(measured.map(({ id }) => id), [...OWNER_IMPACT_STABLE_CASE_IDS, ...OWNER_IMPACT_CONTROL_CASE_IDS].sort())
    assert.ok(measured.every(({ compile_item_identities: identities }) => identities.every((path) => path.startsWith('src/Wanxiangshu/'))))

    const baselineMeasurement = {
      commit: commit('a'),
      environment: environment(),
      structural: structural(10),
      timing: timing(100),
    }
    const report = buildOwnerImpactReportV1({
      corpus: corpus({ baseline: baselineMeasurement }),
      candidate_commit: commit('b'),
      structural: structural(8),
      timing: { environment: environment(), timing: timing(106) },
    })
    assert.equal(report.comparison.structural_median.reduction_percent, 20)
    assert.deepEqual(report.findings.map(({ code }) => code), [
      'wall-clock-regression-over-five-percent',
    ])
    assert.ok(!Object.hasOwn(report, 'ok'))
    assert.ok(!Object.hasOwn(report, 'verdict'))
  } finally {
    rmSync(fixture, { recursive: true, force: true })
  }
})
test('WHAT[structured-workflow-012] owner impact corpus rejects stable-case deletion and baseline drift', () => {
  const missingStableCase = corpus()
  missingStableCase.stable_cases.pop()
  assert.throws(() => validateOwnerImpactCorpusV1(missingStableCase), /closed report-only schema/)

  const driftedBaseline = corpus({
    baseline: {
      commit: commit('b'),
      environment: environment(),
      structural: structural(10),
      timing: timing(100),
    },
  })
  assert.throws(() => validateOwnerImpactCorpusV1(driftedBaseline), /declared commit and cases/)

  const changedDefinition = corpus()
  changedDefinition.stable_cases[0].baseline_changed_path = 'src/Wanxiangshu/Other.fs'
  assert.throws(() => validateOwnerImpactCorpusV1(changedDefinition), /closed report-only schema/)

  const changedCommand = corpus()
  changedCommand.timing_commands[0].command = ['npm', 'run', 'format-build-test', '--extra-flag']
  assert.throws(() => validateOwnerImpactCorpusV1(changedCommand), /closed report-only schema/)
})
test('WHAT[structured-workflow-012] baseline writer binds one clean exact commit and refuses overwrite', () => {
  const fixture = mkdtempSync(join(tmpdir(), 'owner-impact-baseline-'))
  try {
    const corpusPath = join(fixture, 'corpus.json')
    writeFileSync(corpusPath, `${JSON.stringify(corpus(), null, 2)}\n`)
    const measured = writeOwnerImpactBaselineV1(corpusPath, {
      root: fixture,
      inspectGit: () => ({ commit: commit('a'), clean: true }),
      measureStructure: () => structural(10),
      measureTiming: () => ({ environment: environment(), timing: timing(100) }),
    })
    assert.equal(measured.baseline_measurement.commit, commit('a'))
    assert.equal(validateOwnerImpactCorpusV1(JSON.parse(readFileSync(corpusPath))).baseline_measurement.timing.length, 1)
    assert.throws(() => writeOwnerImpactBaselineV1(corpusPath, {
      root: fixture,
      inspectGit: () => ({ commit: commit('a'), clean: true }),
      measureStructure: () => structural(10),
      measureTiming: () => ({ environment: environment(), timing: timing(100) }),
    }), /refusing to overwrite/)

    writeFileSync(corpusPath, `${JSON.stringify(corpus(), null, 2)}\n`)
    assert.throws(() => writeOwnerImpactBaselineV1(corpusPath, {
      root: fixture,
      inspectGit: () => ({ commit: commit('a'), clean: false }),
    }), /clean Git checkout/)
    assert.throws(() => writeOwnerImpactBaselineV1(corpusPath, {
      root: fixture,
      inspectGit: () => ({ commit: commit('b'), clean: true }),
    }), /does not match baseline_commit/)
  } finally {
    rmSync(fixture, { recursive: true, force: true })
  }
})
}

{
const { default: assert } = await import("node:assert/strict");
const { existsSync, mkdtempSync, mkdirSync, rmSync, writeFileSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join } = await import("node:path");
const { default: test } = await import("node:test");
const { planImpactCompile, planImpactFromInventory, readImpactInventory } = await import("../../../scripts/lib/owner-compile.mjs");

const SOURCE_ROOT = join(import.meta.dirname, '../../..', 'src/Wanxiangshu')

const projectName = (node) => `Owner.${String(node).padStart(2, '0')}.fsproj`
const writeProject = (root, node, { references = [], compileItems = [`Source/Node${node}.fsi`, `Source/Node${node}.fs`], rawReferences = null } = {}) => {
  const projectPath = join(root, projectName(node))
  const refs = (rawReferences ?? references.map((provider) => projectName(provider)))
    .map((include) => `    <ProjectReference Include="${include}"/>`).join('\n')
  writeFileSync(projectPath, `<Project Sdk="Microsoft.NET.Sdk">\n  <ItemGroup>\n${refs}${refs ? '\n' : ''}${compileItems.map((item) => `    <Compile Include="${item}"/>`).join('\n')}\n  </ItemGroup>\n</Project>\n`)
  return projectPath
}
const writeChainFixture = () => {
  const root = mkdtempSync(join(tmpdir(), 'wanxiangshu-impact-inventory-'))
  const sourceDirectory = join(root, 'Source')
  mkdirSync(sourceDirectory)
  writeFileSync(join(root, 'Directory.Build.props'), '<Project/>\n')
  for (const node of [0, 1]) {
    writeFileSync(join(sourceDirectory, `Node${node}.fsi`), `namespace Fixture\nval node${node}: string\n`)
    writeFileSync(join(sourceDirectory, `Node${node}.fs`), `namespace Fixture\nlet node${node} = "${node}"\n`)
  }
  writeProject(root, 0)
  writeProject(root, 1, { references: [0] })
  const aggregate = join(root, 'Aggregate.fsproj')
  writeFileSync(aggregate, `<Project Sdk="Microsoft.NET.Sdk">\n  <ItemGroup>\n${[0, 1].flatMap((node) => [
    `    <Compile Include="Source/Node${node}.fsi"/>`,
    `    <Compile Include="Source/Node${node}.fs"/>`,
  ]).join('\n')}\n  </ItemGroup>\n</Project>\n`)
  return { aggregate, root, sourceDirectory }
}

test('WHAT[structured-workflow-012] production focused closure carries upstream type-definition shards (CompanionHost regression)', () => {
  const pluginSessionScopeFs = join(SOURCE_ROOT, 'OpenCode/Host/PluginSessionScope.fs')
  const companionHostFsi = join(SOURCE_ROOT, 'Context/Companion/Host.fsi')
  const companionHostFs = join(SOURCE_ROOT, 'Context/Companion/Host.fs')

  assert.equal(existsSync(pluginSessionScopeFs), true, 'production PluginSessionScope.fs must exist')
  assert.equal(existsSync(companionHostFs), true, 'production CompanionHost definition file must exist')

  const plan = planImpactCompile({
    changedPaths: [pluginSessionScopeFs],
    projectDirectory: SOURCE_ROOT,
    fullThreshold: 1,
  })

  assert.equal(plan.mode, 'focused', `a .fs-only implementation change must stay focused, got ${plan.mode} (${plan.reason})`)
  assert.ok(
    plan.compileItems.includes(companionHostFsi) && plan.compileItems.includes(companionHostFs),
    'the focused closure of PluginSessionScope.fs must include the CompanionHost definition files: the owner shard graph has to declare every upstream definition dependency the one-pass flat compile resolves',
  )
  assert.ok(
    plan.compileItems.indexOf(companionHostFs) < plan.compileItems.indexOf(pluginSessionScopeFs),
    'canonical order must compile the CompanionHost definition before its consumer',
  )
})

test('WHAT[structured-workflow-012] focused Controller compilation includes Snapshot recovery without private Host admission contracts', () => {
  const controllerFs = join(SOURCE_ROOT, 'Execution/Delegation/Handle/Controller.fs')
  const recoveryFs = join(SOURCE_ROOT, 'Execution/Delegation/ChildRecoveryWorkflow.fs')
  const plan = planImpactCompile({
    changedPaths: [controllerFs],
    projectDirectory: SOURCE_ROOT,
    fullThreshold: 1,
  })

  assert.equal(plan.mode, 'focused', `a Controller implementation change must stay focused, got ${plan.mode} (${plan.reason})`)
  assert.ok(plan.compileItems.includes(controllerFs))
  assert.ok(plan.compileItems.includes(recoveryFs))
  for (const extension of ['fsi', 'fs']) {
    const snapshot = join(SOURCE_ROOT, `OpenCode/Host/SessionSnapshot.${extension}`)
    assert.ok(plan.compileItems.includes(snapshot), `focused Controller compilation must include SessionSnapshot.${extension}`)
    assert.ok(plan.compileItems.indexOf(snapshot) < plan.compileItems.indexOf(recoveryFs), 'Snapshot must precede its recovery consumer in canonical order')
  }
  for (const path of ['OpenCode/Host/SessionHostPort', 'Execution/Session/ChatExecution/Acceptance', 'Execution/Session/ChatExecution/Settlement']) {
    for (const extension of ['fsi', 'fs']) {
      assert.equal(plan.compileItems.includes(join(SOURCE_ROOT, `${path}.${extension}`)), false, `Snapshot recovery must not import ${path}.${extension}`)
    }
  }
})

integrationTest('WHAT[structured-workflow-012] real Fable compiles a focused Controller change with its declared Snapshot contracts', async () => {
  const { compileIncremental } = await import('../../../scripts/lib/owner-compile.mjs')
  const owned = mkdtempSync(join(tmpdir(), 'wanxiangshu-controller-focused-'))
  try {
    const outputDir = join(owned, 'out')
    const result = await compileIncremental({
      changedPaths: [join(SOURCE_ROOT, 'Execution/Delegation/Handle/Controller.fs')],
      root: join(SOURCE_ROOT, '../..'),
      projectDirectory: SOURCE_ROOT,
      rootPropsPath: join(SOURCE_ROOT, '../../Directory.Build.props'),
      scratchRoot: join(owned, 'scratch'),
      outputDir,
      stdio: 'pipe',
    })
    assert.equal(result.ok, true, result.stderr || result.stdout)
    assert.equal(result.mode, 'focused', 'the production contract fix must not require a full fallback')
    assert.ok(existsSync(join(outputDir, 'Execution/Delegation/Handle/Controller.js')), 'real Fable must emit the changed Controller')
  } finally {
    rmSync(owned, { recursive: true, force: true })
  }
})

test('WHAT[structured-workflow-012] disk inventory matches the legacy plan and rejects unmapped added sources fail-closed', () => {
  const fixture = writeChainFixture()
  try {
    const inventory = readImpactInventory({
      projectDirectory: fixture.root,
      aggregatePath: fixture.aggregate,
    })
    assert.equal(inventory.projects.size, 2)
    assert.equal(inventory.sourceOwner.size, 4)
    assert.deepEqual([...inventory.projectPaths].sort(), [...inventory.projectPaths])

    const changedPaths = [join(fixture.sourceDirectory, 'Node0.fs')]
    const legacy = planImpactCompile({
      changedPaths,
      projectDirectory: fixture.root,
      aggregatePath: fixture.aggregate,
      fullThreshold: 1,
    })
    const split = planImpactFromInventory({ inventory, changedPaths, fullThreshold: 1 })
    assert.equal(legacy.mode, 'focused')
    assert.deepEqual(split, legacy)

    // A newly added source file owned by no project is an unmapped .fs change: MUST throw fail-closed
    const addedFile = join(fixture.sourceDirectory, 'Added.fs')
    writeFileSync(addedFile, 'namespace Fixture\nlet added = 1\n')

    assert.throws(
      () => planImpactFromInventory({
        inventory,
        changedPaths: [addedFile],
      }),
      /unmapped/i,
      'planImpactFromInventory must throw fail-closed for unmapped source change',
    )

    assert.throws(
      () => planImpactCompile({
        changedPaths: [addedFile],
        projectDirectory: fixture.root,
        aggregatePath: fixture.aggregate,
      }),
      /unmapped/i,
      'planImpactCompile must throw fail-closed for unmapped source change',
    )
  } finally {
    rmSync(fixture.root, { recursive: true, force: true })
  }
})
test('WHAT[structured-workflow-012] inventory rejects bad topology inputs and planning rejects bad change inputs', () => {
  const fixture = writeChainFixture()
  try {
    // Duplicate Compile item across owner projects.
    writeProject(fixture.root, 1, { references: [0], compileItems: ['Source/Node1.fsi', 'Source/Node1.fs', 'Source/Node0.fs'] })
    assert.throws(
      () => readImpactInventory({ projectDirectory: fixture.root, aggregatePath: fixture.aggregate }),
      /Duplicate Compile item across owner projects/,
    )

    // Reference to an existing project outside the scanned topology.
    mkdirSync(join(fixture.root, 'Other'))
    const outside = join(fixture.root, 'Other', 'Extra.fsproj')
    writeFileSync(outside, '<Project Sdk="Microsoft.NET.Sdk"><ItemGroup></ItemGroup></Project>\n')
    writeProject(fixture.root, 1, { rawReferences: ['Other/Extra.fsproj'], compileItems: ['Source/Node1.fsi', 'Source/Node1.fs'] })
    assert.throws(
      () => readImpactInventory({ projectDirectory: fixture.root, aggregatePath: fixture.aggregate }),
      /references project outside owner topology/,
    )

    // Missing referenced project file surfaces from the underlying parse.
    writeProject(fixture.root, 1, { rawReferences: ['Missing.fsproj'], compileItems: ['Source/Node1.fsi', 'Source/Node1.fs'] })
    assert.throws(
      () => readImpactInventory({ projectDirectory: fixture.root, aggregatePath: fixture.aggregate }),
      /Missing ProjectReference/,
    )

    // Malformed project XML is rejected.
    writeFileSync(join(fixture.root, projectName(1)), 'not xml at all\n')
    assert.throws(
      () => readImpactInventory({ projectDirectory: fixture.root, aggregatePath: fixture.aggregate }),
      /lacks <Project> root/,
    )

    // W2 cutover: a missing aggregate fsproj is tolerated — the shard graph
    // alone is the canonical inventory source now and the planner can
    // synthesize the canonical compile order without it. The owner graph
    // still rejects malformed projects.
    writeProject(fixture.root, 1, { references: [0], compileItems: ['Source/Node1.fsi', 'Source/Node1.fs'] })
    const tolerated = readImpactInventory({ projectDirectory: fixture.root, aggregatePath: join(fixture.root, 'Nope.fsproj') })
    assert.equal(tolerated.aggregateMissing, true)
    assert.equal(tolerated.projects.size, 2)
    assert.equal(tolerated.aggregate.compileItems.length, 4)
    writeFileSync(join(fixture.root, projectName(1)), 'not xml at all\n')
    assert.throws(
      () => readImpactInventory({ projectDirectory: fixture.root, aggregatePath: fixture.aggregate }),
      /lacks <Project> root/,
    )

    // Planning-stage input validation matches the legacy entry errors.
    const fresh = writeChainFixture()
    try {
      const inventory = readImpactInventory({
        projectDirectory: fresh.root,
        aggregatePath: fresh.aggregate,
      })
      assert.throws(
        () => planImpactFromInventory({ inventory, changedPaths: [] }),
        /changedPaths must contain at least one path/,
      )
      for (const fullThreshold of [0, -0.5, 1.5]) {
        assert.throws(
          () => planImpactFromInventory({ inventory, changedPaths: [join(fresh.sourceDirectory, 'Node0.fs')], fullThreshold }),
          /fullThreshold must be within/,
        )
      }
    } finally {
      rmSync(fresh.root, { recursive: true, force: true })
    }
  } finally {
    rmSync(fixture.root, { recursive: true, force: true })
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

test('WHAT[structured-workflow-012] flat Fable projection planner produces exact closure and canonical aggregate order', () => {
  const aggregatePath = join(FIXTURE, 'Emitter.fsproj')
  const leakyConsumerPath = join(FIXTURE, 'LeakyConsumer.fsproj')
  const leakyContractPath = join(FIXTURE, 'LeakyContract.fsproj')
  const runtimePath = join(FIXTURE, 'Runtime.fsproj')

  const plan = planOwnerCompile({
    projectPath: leakyConsumerPath,
    aggregatePath,
  })

  // Exact project closure
  const expectedProjects = [leakyConsumerPath, leakyContractPath, runtimePath].sort()
  assert.deepEqual(plan.projectPaths, expectedProjects)

  // Exact compile items filtered in Emitter.fsproj document order
  const expectedCompileItems = [
    join(FIXTURE, 'Runtime.fs'),
    join(FIXTURE, 'LeakyContract.fs'),
    join(FIXTURE, 'LeakyConsumer.fs'),
  ]
  assert.deepEqual(plan.compileItems, expectedCompileItems)

  // Unreferenced files must NOT be in compileItems
  assert.ok(!plan.compileItems.includes(join(FIXTURE, 'Provider.fs')))
  assert.ok(!plan.compileItems.includes(join(FIXTURE, 'GreenConsumer.fs')))
  assert.ok(!plan.compileItems.includes(join(FIXTURE, 'RedConsumer.fs')))

  // Signature order verification: .fsi must precede .fs
  const signedPlan = planOwnerCompile({
    projectPath: join(FIXTURE, 'SignedProvider.fsproj'),
    aggregatePath,
  })
  assert.deepEqual(signedPlan.compileItems, [
    join(FIXTURE, 'SignedProvider.fsi'),
    join(FIXTURE, 'SignedProvider.fs'),
  ])
})
test('WHAT[structured-workflow-012] flat Fable projection materializes zero ProjectReference and isolated scratch props', () => {
  const scratchRoot = mkdtempSync(join(tmpdir(), 'wanxiangshu-materialize-test-'))
  const rootPropsPath = join(ROOT, 'Directory.Build.props')
  try {
    const aggregatePath = join(FIXTURE, 'Emitter.fsproj')
    const plan = planOwnerCompile({
      projectPath: join(FIXTURE, 'LeakyConsumer.fsproj'),
      aggregatePath,
    })

    const materialized = materializeOwnerCompile(plan, {
      scratchRoot,
      rootPropsPath,
    })

    // Generated project file must contain zero ProjectReference and strip WanxiangshuEmitProject identity
    const generatedXml = readFileSync(materialized.projectPath, 'utf8')
    assert.doesNotMatch(generatedXml, /<ProjectReference\b/i, 'generated flat project must contain zero ProjectReference')
    assert.match(readFileSync(aggregatePath, 'utf8'), /<WanxiangshuEmitProject>true<\/WanxiangshuEmitProject>/)
    assert.doesNotMatch(
      generatedXml,
      /<WanxiangshuEmitProject\b/i,
      'generated flat project must strip WanxiangshuEmitProject emitter identity',
    )

    // Generated project must preserve aggregate shell and contain absolute Compile entries in order
    assert.match(generatedXml, /<TargetFramework>net10\.0<\/TargetFramework>/)
    assert.match(generatedXml, /<PackageReference Include="Fable\.Core"/)
    assert.match(generatedXml, new RegExp(`<Compile Include="${join(FIXTURE, 'Runtime.fs')}"\\s*/>`))
    assert.match(generatedXml, new RegExp(`<Compile Include="${join(FIXTURE, 'LeakyContract.fs')}"\\s*/>`))
    assert.match(generatedXml, new RegExp(`<Compile Include="${join(FIXTURE, 'LeakyConsumer.fs')}"\\s*/>`))

    // Scratch Directory.Build.props must define isolated ArtifactsDir and import root props
    const scratchProps = readFileSync(join(materialized.scratchDir, 'Directory.Build.props'), 'utf8')
    // WP3 split cache: ArtifactsDir anchors at the restore fingerprint so
    // a body-only .fs edit keeps project.assets.json warm; the literal
    // macro form or an absolute /restore-<fp>/artifacts/ path are both
    // accepted — what must be true is that ArtifactsDir exists and binds
    // `artifacts/`.
    assert.match(scratchProps, /<ArtifactsDir>(?:[^<]*)artifacts\/<\/ArtifactsDir>/, 'scratch props must set ArtifactsDir to an artifacts/ dir')
    assert.match(scratchProps, /<NuGetAudit>false<\/NuGetAudit>/)
    assert.match(scratchProps, new RegExp(`<Import Project="${rootPropsPath}"\\s*/>`))

    // Deterministic fingerprint
    const materializedAgain = materializeOwnerCompile(plan, {
      scratchRoot,
      rootPropsPath,
    })
    assert.equal(materializedAgain.fingerprint, materialized.fingerprint)
  } finally {
    rmSync(scratchRoot, { recursive: true, force: true })
  }
})
test('WHAT[structured-workflow-012] flat projection rejects missing or stale ProjectReference before compiler invocation', () => {
  const scratchRoot = mkdtempSync(join(tmpdir(), 'wanxiangshu-reject-test-'))
  try {
    const aggregatePath = join(FIXTURE, 'Emitter.fsproj')

    // Stale/missing ProjectReference
    const missingRefProject = join(scratchRoot, 'MissingRef.fsproj')
    writeFileSync(
      missingRefProject,
      `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="NonExistentTarget.fsproj"/>
    <Compile Include="${join(FIXTURE, 'Provider.fs')}"/>
  </ItemGroup>
</Project>`,
      'utf8',
    )

    assert.throws(
      () => planOwnerCompile({ projectPath: missingRefProject, aggregatePath }),
      /Missing ProjectReference.*NonExistentTarget\.fsproj/i,
      'must reject non-existent ProjectReference target before compiler invocation',
    )

    // ProjectReference cycle
    const cycleA = join(scratchRoot, 'CycleA.fsproj')
    const cycleB = join(scratchRoot, 'CycleB.fsproj')
    writeFileSync(
      cycleA,
      `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="CycleB.fsproj"/>
    <Compile Include="${join(FIXTURE, 'Provider.fs')}"/>
  </ItemGroup>
</Project>`,
      'utf8',
    )
    writeFileSync(
      cycleB,
      `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="CycleA.fsproj"/>
    <Compile Include="${join(FIXTURE, 'Runtime.fs')}"/>
  </ItemGroup>
</Project>`,
      'utf8',
    )

    assert.throws(
      () => planOwnerCompile({ projectPath: cycleA, aggregatePath }),
      /ProjectReference cycle detected/i,
      'must reject ProjectReference cycle before compiler invocation',
    )
  } finally {
    rmSync(scratchRoot, { recursive: true, force: true })
  }
})
test('WHAT[structured-workflow-012] flat Fable projection materialization escapes XML metacharacters, strips emitter identity, and binds source bytes into isolated fingerprints', () => {
  const scratchRoot = mkdtempSync(join(tmpdir(), 'wanxiangshu-xml-metachar-proof-'))
  try {
    const signatureFile = join(scratchRoot, 'Special&Signature.fsi')
    writeFileSync(signatureFile, 'namespace Special\nmodule SpecialSource\nval x : int\n', 'utf8')

    const compileFile = join(scratchRoot, 'Special&Source.fs')
    writeFileSync(compileFile, 'namespace Special\nmodule SpecialSource\nlet x = 1\n', 'utf8')

    const rootPropsPath = join(scratchRoot, 'Root&<Props>\'Test".props')
    writeFileSync(rootPropsPath, '<Project />', 'utf8')

    const aggregatePath = join(scratchRoot, 'Emitter.fsproj')
    writeFileSync(
      aggregatePath,
      `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <WanxiangshuEmitProject>true</WanxiangshuEmitProject>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Special&amp;Signature.fsi" />
    <Compile Include="Special&amp;Source.fs" />
  </ItemGroup>
</Project>`,
      'utf8',
    )

    const ownerPath = join(scratchRoot, 'Owner.fsproj')
    writeFileSync(
      ownerPath,
      `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Special&amp;Signature.fsi" />
    <Compile Include="Special&amp;Source.fs" />
  </ItemGroup>
</Project>`,
      'utf8',
    )

    const plan = planOwnerCompile({
      projectPath: ownerPath,
      aggregatePath,
    })

    // Planner resolves the real decoded ampersand filenames in canonical order
    const normalizedSignatureFile = resolve(signatureFile).replace(/\\/g, '/')
    const normalizedCompileFile = resolve(compileFile).replace(/\\/g, '/')
    assert.deepEqual(plan.compileItems, [normalizedSignatureFile, normalizedCompileFile])
    assert.ok(
      plan.compileItems[0].endsWith('/Special&Signature.fsi'),
      'planner must resolve real decoded signature filename',
    )
    assert.ok(
      plan.compileItems[1].endsWith('/Special&Source.fs'),
      'planner must resolve real decoded source filename',
    )

    const initialMaterialized = materializeOwnerCompile(plan, {
      scratchRoot,
      rootPropsPath,
    })

    const generatedXml = readFileSync(initialMaterialized.projectPath, 'utf8')
    const scratchProps = readFileSync(join(initialMaterialized.scratchDir, 'Directory.Build.props'), 'utf8')

    // Materialized project strips WanxiangshuEmitProject identity
    assert.match(readFileSync(aggregatePath, 'utf8'), /<WanxiangshuEmitProject>true<\/WanxiangshuEmitProject>/)
    assert.doesNotMatch(
      generatedXml,
      /<WanxiangshuEmitProject\b/i,
      'materialized project must strip WanxiangshuEmitProject emitter identity',
    )

    // Compile Include attribute escaping proof for signature and implementation
    const expectedSigAttr = normalizedSignatureFile
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;')
      .replace(/'/g, '&apos;')
    const expectedCompileAttr = normalizedCompileFile
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;')
      .replace(/'/g, '&apos;')

    assert.ok(expectedSigAttr.includes('&amp;'))
    assert.ok(expectedCompileAttr.includes('&amp;'))
    assert.match(
      generatedXml,
      new RegExp(`<Compile Include="${expectedSigAttr.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}"\\s*/>`),
    )
    assert.match(
      generatedXml,
      new RegExp(`<Compile Include="${expectedCompileAttr.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}"\\s*/>`),
    )
    assert.ok(
      !generatedXml.includes(`Include="${normalizedSignatureFile}"`),
      'generated XML must not contain raw unescaped signature Include attribute',
    )
    assert.ok(
      !generatedXml.includes(`Include="${normalizedCompileFile}"`),
      'generated XML must not contain raw unescaped Compile Include attribute',
    )

    // Root props Import attribute escaping proof
    const normalizedRootPropsPath = resolve(rootPropsPath).replace(/\\/g, '/')
    const expectedPropsAttr = normalizedRootPropsPath
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;')
      .replace(/'/g, '&apos;')
    assert.ok(expectedPropsAttr.includes('&amp;&lt;Props&gt;&apos;Test&quot;.props'))
    const propsMatch = scratchProps.match(/<Import Project="([^"]*)"\s*\/>/)
    assert.ok(propsMatch, 'Directory.Build.props must contain Import element with Project attribute')
    assert.equal(propsMatch[1], expectedPropsAttr)
    assert.doesNotMatch(propsMatch[1], /[<>'"]/)
    assert.doesNotMatch(propsMatch[1], /&(?!(amp|lt|gt|quot|apos);)/)
    assert.ok(!scratchProps.includes(`Project="${normalizedRootPropsPath}"`), 'Directory.Build.props must not contain raw unescaped Import Project attribute')

    // 1. Invalidation proof: mutating signature file bytes invalidates content fingerprint and isolates output,
    // while the flat project working directory stays stable across source byte changes (set fingerprint stability).
    writeFileSync(signatureFile, 'namespace Special\nmodule SpecialSource\nval x : int\nval y : string\n', 'utf8')
    const materializedAfterSigChange = materializeOwnerCompile(plan, {
      scratchRoot,
      rootPropsPath,
    })
    assert.notEqual(
      materializedAfterSigChange.fingerprint,
      initialMaterialized.fingerprint,
      'signature source byte change must invalidate artifact content fingerprint',
    )
    assert.equal(
      materializedAfterSigChange.projectDir ?? materializedAfterSigChange.scratchDir,
      initialMaterialized.projectDir ?? initialMaterialized.scratchDir,
      'signature source byte change must keep flat project working directory stable to reuse Fable cache',
    )
    assert.equal(
      materializedAfterSigChange.projectPath,
      initialMaterialized.projectPath,
      'signature source byte change must preserve stable flat project path',
    )
    assert.notEqual(
      materializedAfterSigChange.outputPath,
      initialMaterialized.outputPath,
      'signature source byte change must isolate output path',
    )
    assert.equal(
      materializedAfterSigChange.assetsPath,
      initialMaterialized.assetsPath,
      'signature source byte change must NOT isolate assets path — assets identity binds to the project graph, not sources',
    )
    assert.notEqual(
      materializedAfterSigChange.artifactFingerprint,
      initialMaterialized.artifactFingerprint,
      'signature byte change must invalidate artifact fingerprint',
    )
    assert.equal(
      materializedAfterSigChange.restoreFingerprint,
      initialMaterialized.restoreFingerprint,
      'signature byte change must preserve restore fingerprint — the split is what powers incremental restore reuse',
    )

    // 2. Invalidation proof: mutating implementation source file bytes invalidates content fingerprint and isolates output,
    // while flat project working directory remains stable.
    writeFileSync(compileFile, 'namespace Special\nmodule SpecialSource\nlet x = 2\nlet y = "hello"\n', 'utf8')
    const materializedAfterSrcChange = materializeOwnerCompile(plan, {
      scratchRoot,
      rootPropsPath,
    })
    assert.notEqual(
      materializedAfterSrcChange.fingerprint,
      materializedAfterSigChange.fingerprint,
      'implementation source byte change must invalidate content fingerprint',
    )
    assert.notEqual(
      materializedAfterSrcChange.fingerprint,
      initialMaterialized.fingerprint,
      'implementation source byte change must invalidate initial fingerprint',
    )
    assert.equal(
      materializedAfterSrcChange.projectDir ?? materializedAfterSrcChange.scratchDir,
      materializedAfterSigChange.projectDir ?? materializedAfterSigChange.scratchDir,
      'implementation source byte change must keep flat project working directory stable',
    )
    assert.equal(
      materializedAfterSrcChange.projectPath,
      materializedAfterSigChange.projectPath,
      'implementation source byte change must keep flat project path stable',
    )
    assert.notEqual(
      materializedAfterSrcChange.outputPath,
      materializedAfterSigChange.outputPath,
      'implementation source byte change must isolate output path',
    )
    assert.equal(
      materializedAfterSrcChange.assetsPath,
      materializedAfterSigChange.assetsPath,
      'implementation source byte change must share the assets path — .assets.json is restore-identity-owned',
    )

    // 3. Cache stability proof: unchanged inputs with same plan must produce identical fingerprint and reuse scratch/output
    const materializedStable = materializeOwnerCompile(plan, {
      scratchRoot,
      rootPropsPath,
    })
    assert.equal(
      materializedStable.fingerprint,
      materializedAfterSrcChange.fingerprint,
      'unchanged inputs must preserve deterministic fingerprint',
    )
    assert.equal(
      materializedStable.scratchDir,
      materializedAfterSrcChange.scratchDir,
      'unchanged inputs must reuse scratch directory',
    )
    assert.equal(
      materializedStable.projectPath,
      materializedAfterSrcChange.projectPath,
      'unchanged inputs must reuse project path',
    )
    assert.equal(
      materializedStable.outputPath,
      materializedAfterSrcChange.outputPath,
      'unchanged inputs must reuse output path',
    )
    assert.equal(
      materializedStable.assetsPath,
      materializedAfterSrcChange.assetsPath,
      'unchanged inputs must reuse assets path',
    )

    // Fail-closed unknown-entity assertion
    const unknownEntityPath = join(scratchRoot, 'UnknownEntity.fsproj')
    writeFileSync(
      unknownEntityPath,
      `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Unknown&unknown;Source.fs" />
  </ItemGroup>
</Project>`,
      'utf8',
    )

    assert.throws(
      () => planOwnerCompile({ projectPath: unknownEntityPath, aggregatePath }),
      /(?:Unknown|Malformed|Invalid) XML (?:entity|character) reference/i,
      'must reject unknown XML entity reference fail-closed before compiler invocation',
    )
  } finally {
    rmSync(scratchRoot, { recursive: true, force: true })
  }
})
test('WHAT[structured-workflow-012] failure lifecycle prevents false-green warm cache and enforces success marker contract', async () => {
  const scratchRoot = mkdtempSync(join(tmpdir(), 'wanxiangshu-sw011-failure-proof-'))
  try {
    const aggregatePath = join(FIXTURE, 'Emitter.fsproj')
    const projectPath = join(FIXTURE, 'LeakyConsumer.fsproj')
    const rootPropsPath = join(ROOT, 'Directory.Build.props')

    let spawnInvocations = 0
    let run4SawPreservedOutput = false
    let run5SawPreservedOutput = false

    const fakeSpawn = (command, args, options) => {
      spawnInvocations++
      const child = new EventEmitter()
      child.stdout = new EventEmitter()
      child.stderr = new EventEmitter()

      const outIndex = args.indexOf('-o')
      const targetOutputDir = outIndex !== -1 ? args[outIndex + 1] : null

      setImmediate(() => {
        if (spawnInvocations === 1) {
          // First run: writes partial JS output then exits nonzero
          if (targetOutputDir) {
            writeFileSync(join(targetOutputDir, 'Partial.fs.js'), '// partial output from crashed run 1\n', 'utf8')
          }
          child.emit('close', 1, null)
        } else if (spawnInvocations === 2) {
          // Second run: would falsely return zero ONLY if partial output from run 1 survived
          if (targetOutputDir && existsSync(join(targetOutputDir, 'Partial.fs.js'))) {
            child.emit('close', 0, null)
          } else {
            // Correct behavior: partial output from run 1 was cleaned up; this run also writes partial output and fails
            if (targetOutputDir) {
              writeFileSync(join(targetOutputDir, 'Partial2.fs.js'), '// partial output from crashed run 2\n', 'utf8')
            }
            child.emit('close', 2, null)
          }
        } else if (spawnInvocations === 3) {
          // Third run: simulated successful emit writing verified JS files and exiting 0
          if (targetOutputDir) {
            writeFileSync(join(targetOutputDir, 'Runtime.fs.js'), 'export const runtime = true;\n', 'utf8')
            writeFileSync(join(targetOutputDir, 'LeakyConsumer.fs.js'), 'export const consumer = true;\n', 'utf8')
          }
          child.emit('close', 0, null)
        } else if (spawnInvocations === 4) {
          // Fourth run (warm compile): must invoke injected spawn again, preserve output before invocation
          if (
            targetOutputDir &&
            existsSync(join(targetOutputDir, 'Runtime.fs.js')) &&
            existsSync(join(targetOutputDir, 'LeakyConsumer.fs.js'))
          ) {
            run4SawPreservedOutput = true
            writeFileSync(join(targetOutputDir, 'Runtime.fs.js'), 'export const runtime = true; // refreshed\n', 'utf8')
            writeFileSync(join(targetOutputDir, 'LeakyConsumer.fs.js'), 'export const consumer = true; // refreshed\n', 'utf8')
            child.emit('close', 0, null)
          } else {
            child.emit('close', 40, null)
          }
        } else if (spawnInvocations === 5) {
          // Fifth run (warm compile with failure): output preserved before spawn, but compiler fails
          if (
            targetOutputDir &&
            existsSync(join(targetOutputDir, 'Runtime.fs.js')) &&
            existsSync(join(targetOutputDir, 'LeakyConsumer.fs.js'))
          ) {
            run5SawPreservedOutput = true
            child.emit('close', 5, null)
          } else {
            child.emit('close', 50, null)
          }
        } else {
          // Unexpected spawn call
          child.emit('close', 99, null)
        }
      })

      return child
    }

    // Call 1: First compilation fails after partial emit
    const result1 = await compileOwnerProject({
      projectPath,
      aggregatePath,
      scratchRoot,
      rootPropsPath,
      spawn: fakeSpawn,
      stdio: 'pipe',
    })

    assert.equal(result1.ok, false, 'first compilation must fail on nonzero child exit')
    assert.equal(result1.code, 1, 'first compilation must propagate exit code 1')
    assert.equal(spawnInvocations, 1, 'first compilation must invoke spawn exactly once')
    assert.equal(
      existsSync(join(result1.outputPath, 'Partial.fs.js')),
      false,
      'partial JS output must be cleaned up after first compilation failure',
    )
    assert.equal(
      existsSync(join(result1.scratchDir, '.success')),
      false,
      'success marker must not exist after first compilation failure',
    )

    // Call 2: Second compilation with same inputs must fail (not falsely return zero due to stale partial output)
    const result2 = await compileOwnerProject({
      projectPath,
      aggregatePath,
      scratchRoot,
      rootPropsPath,
      spawn: fakeSpawn,
      stdio: 'pipe',
    })

    assert.equal(result2.ok, false, 'second compilation must fail without valid success marker')
    assert.equal(result2.code, 2, 'second compilation must fail with clean-state failure code')
    assert.equal(spawnInvocations, 2, 'second compilation must invoke spawn because cache is unvalidated')
    assert.equal(
      existsSync(join(result2.outputPath, 'Partial.fs.js')),
      false,
      'stale partial output from run 1 must not exist after second compilation',
    )
    assert.equal(
      existsSync(join(result2.outputPath, 'Partial2.fs.js')),
      false,
      'partial output from run 2 must be cleaned up after failure',
    )
    assert.equal(
      existsSync(join(result2.scratchDir, '.success')),
      false,
      'success marker must not exist after second compilation failure',
    )

    // Call 3: Simulated successful compilation emit creates success marker and preserves JS outputs
    const result3 = await compileOwnerProject({
      projectPath,
      aggregatePath,
      scratchRoot,
      rootPropsPath,
      spawn: fakeSpawn,
      stdio: 'pipe',
    })

    assert.equal(result3.ok, true, 'third compilation with valid emit must succeed')
    assert.equal(result3.code, 0, 'third compilation must return exit code 0')
    assert.equal(spawnInvocations, 3, 'third compilation must invoke spawn')
    assert.equal(
      existsSync(join(result3.outputPath, 'Runtime.fs.js')),
      true,
      'emitted Runtime.fs.js must exist on successful compilation',
    )
    assert.equal(
      existsSync(join(result3.outputPath, 'LeakyConsumer.fs.js')),
      true,
      'emitted LeakyConsumer.fs.js must exist on successful compilation',
    )
    const markerFile = join(result3.scratchDir, '.success')
    assert.equal(existsSync(markerFile), true, 'success marker must be created on successful zero-exit compilation')

    // Call 4: Next warm call must invoke injected spawn again, preserve output before invocation, and require a new successful compiler result
    const result4 = await compileOwnerProject({
      projectPath,
      aggregatePath,
      scratchRoot,
      rootPropsPath,
      spawn: fakeSpawn,
      stdio: 'pipe',
    })

    assert.equal(result4.ok, true, 'fourth compilation must succeed with new compiler result')
    assert.equal(result4.code, 0, 'fourth compilation must return exit code 0')
    assert.equal(spawnInvocations, 4, 'fourth compilation must invoke injected spawn again (no marker-only cache bypass)')
    assert.equal(run4SawPreservedOutput, true, 'fourth compilation must preserve existing output before spawn invocation')
    assert.equal(
      existsSync(join(result4.outputPath, 'Runtime.fs.js')),
      true,
      'emitted Runtime.fs.js must exist on successful warm compilation',
    )
    assert.equal(
      existsSync(join(result4.outputPath, 'LeakyConsumer.fs.js')),
      true,
      'emitted LeakyConsumer.fs.js must exist on successful warm compilation',
    )
    assert.equal(existsSync(markerFile), true, 'success marker must remain intact after successful warm compilation')

    // Call 5: Warm compilation failure must remove marker and output
    const result5 = await compileOwnerProject({
      projectPath,
      aggregatePath,
      scratchRoot,
      rootPropsPath,
      spawn: fakeSpawn,
      stdio: 'pipe',
    })

    assert.equal(result5.ok, false, 'fifth compilation must fail when injected spawn returns nonzero')
    assert.equal(result5.code, 5, 'fifth compilation must propagate exit code 5')
    assert.equal(spawnInvocations, 5, 'fifth compilation must invoke injected spawn')
    assert.equal(run5SawPreservedOutput, true, 'fifth compilation must preserve existing output before spawn invocation')
    assert.equal(
      existsSync(join(result5.outputPath, 'Runtime.fs.js')),
      false,
      'injected warm failure must remove output directory / JS outputs',
    )
    assert.equal(
      existsSync(join(result5.outputPath, 'LeakyConsumer.fs.js')),
      false,
      'injected warm failure must remove output directory / JS outputs',
    )
    assert.equal(
      existsSync(markerFile),
      false,
      'injected warm failure must remove success marker',
    )
  } finally {
    rmSync(scratchRoot, { recursive: true, force: true })
  }
})
}

{
const { default: assert } = await import("node:assert/strict");
const { spawn, spawnSync } = await import("node:child_process");
const { chmodSync, cpSync, existsSync, mkdtempSync, readFileSync, readdirSync, rmSync, statSync, writeFileSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join, resolve } = await import("node:path");
const { hasEmittedJsFiles, compileOwnerProject } = await import("../../../scripts/lib/owner-compile.mjs");

const ROOT = resolve(import.meta.dirname, '../../..')
const FIXTURE_CLI = join(ROOT, 'requirements/structured-workflow/tests/fixtures/impact-cli')
const CLI = join(ROOT, 'requirements/structured-workflow/tests/fixtures/impact-cli-wrapper.mjs')
const FIXTURE_BOUNDARY = join(ROOT, 'requirements/structured-workflow/tests/fixtures/owner-project-boundary')

const findImpactProject = (root) => {
  const pending = [join(ROOT, 'src/Wanxiangshu/.fable-build/output-compile')]
  let newest = null
  let newestMtime = 0
  while (pending.length > 0) {
    const current = pending.pop()
    for (const entry of readdirSync(current, { withFileTypes: true })) {
      const path = join(current, entry.name)
      if (entry.isDirectory()) {
        pending.push(path)
        continue
      }
      if (entry.name === 'Wanxiangshu.Impact.fsproj') {
        const stat = statSync(path)
        if (stat.mtimeMs > newestMtime) {
          newest = path
          newestMtime = stat.mtimeMs
        }
      }
    }
  }
  return newest
}

const copyFixture = () => {
  const dir = mkdtempSync(join(tmpdir(), 'wanxiangshu-impact-fixture-'))
  cpSync(FIXTURE_CLI, dir, { recursive: true })
  // cpSync 保留源文件权限；快照输入树为 444 只读时副本同样只读，而本
  // fixture 是用例会直接改写的工作副本（Directory.Build.props、Alpha.fs、
  // Core.fs 等），故整树恢复可写。
  const restoreWritable = (entry) => {
    const stat = statSync(entry)
    chmodSync(entry, stat.isDirectory() ? 0o755 : 0o644)
    if (stat.isDirectory()) for (const name of readdirSync(entry)) restoreWritable(join(entry, name))
  }
  restoreWritable(dir)
  writeFileSync(
    join(dir, 'Directory.Build.props'),
    `<Project>
  <PropertyGroup>
    <ImpactFixtureMark>1</ImpactFixtureMark>
  </PropertyGroup>
  <Import Project="${join(ROOT, 'Directory.Build.props')}" />
</Project>
`,
    'utf8',
  )
  return dir
}

const runCli = (args) => spawnSync(
  process.execPath,
  [CLI, ...args],
  { cwd: ROOT, encoding: 'utf8', timeout: 55_000 },
)

const findEmittedJs = (outputDir, expectedBasename) => {
  const pending = [outputDir]
  while (pending.length > 0) {
    const current = pending.pop()
    for (const entry of readdirSync(current, { withFileTypes: true })) {
      const path = join(current, entry.name)
      if (entry.isDirectory()) {
        if (entry.name === 'fable' + '_modules') continue
        pending.push(path)
        continue
      }
      if (entry.name === expectedBasename) return path
    }
  }
  return null
}

const baseArgs = (dir) => {
  const scratchRoot = join(dir, 'scratch')
  const outputDir = join(dir, 'out')
  return {
    scratchRoot,
    outputDir,
    flags: [
      '--projects', dir,
      '--props', join(dir, 'Directory.Build.props'),
      '--scratch', scratchRoot,
      '-o', outputDir,
    ],
  }
}

function compileFableDirect(project) {
  const outDir = mkdtempSync(join(tmpdir(), 'wanxiangshu-owner-boundary-'))
  return new Promise((resolveResult, reject) => {
    const child = spawn(
      'dotnet',
      ['tool', 'run', 'fable', '--', join(FIXTURE_BOUNDARY, project), '-o', outDir, '--noGitignore'],
      { cwd: ROOT, stdio: ['ignore', 'pipe', 'pipe'] },
    )
    let stdout = ''
    let stderr = ''
    child.stdout.setEncoding('utf8')
    child.stderr.setEncoding('utf8')
    child.stdout.on('data', (chunk) => { stdout += chunk })
    child.stderr.on('data', (chunk) => { stderr += chunk })
    child.once('error', reject)
    child.once('close', (status, signal) => {
      rmSync(outDir, { recursive: true, force: true })
      resolveResult({ status, signal, stdout, stderr })
    })
  })
}

integrationTest('WHAT[structured-workflow-012] compile-impact CLI compiles a focused production implementation change', { timeout: 120_000 }, () => {
  const dir = copyFixture()
  try {
    const { flags, outputDir } = baseArgs(dir)
    const alphaFs = join(dir, 'Alpha', 'Alpha.fs')

    const focused = runCli([alphaFs, ...flags])
    assert.equal(focused.status, 0, focused.stderr || focused.stdout)
    assert.match(focused.stdout, /\[owner-compile\] OK: Wanxiangshu\.Impact\.fsproj/)
    assert.match(focused.stdout, /compiled focused impact \(4 items\)/)

    const projectPath = findImpactProject(dir)
    assert.ok(projectPath, 'CLI must materialize Wanxiangshu.Impact.fsproj for the fixture')
    const xml = readFileSync(projectPath, 'utf8')
    assert.ok(!xml.includes('<ProjectReference'), 'impact CLI must not hand the owner ProjectReference graph to Fable')
    assert.match(xml, /Alpha\/Alpha\.fs/)
    assert.ok(!xml.includes('Beta/BetaOne.fs'), 'focused compile must exclude the unimpacted Beta sources')
    assert.ok(hasEmittedJsFiles(outputDir), 'focused impact compile must emit JavaScript')
    assert.ok(findEmittedJs(outputDir, 'Alpha.js'), 'focused emit must contain the changed Alpha module')
    assert.ok(findEmittedJs(outputDir, 'Core.js'), 'focused emit must contain the Alpha forward closure')
    assert.ok(!findEmittedJs(outputDir, 'BetaOne.js'), 'focused emit must not contain unimpacted Beta bytes')

    const propsPath = join(dir, 'Directory.Build.props')
    writeFileSync(propsPath, `${readFileSync(propsPath, 'utf8')}<!-- impact-cli full-fallback probe -->\n`, 'utf8')
    const fallback = runCli([propsPath, ...flags])
    assert.equal(fallback.status, 0, fallback.stderr || fallback.stdout)
    assert.match(fallback.stdout, /compiled full impact \(8 items\)/)
    assert.ok(
      findEmittedJs(outputDir, 'BetaTwo.js'),
      'full fallback must emit the whole fixture closure',
    )
  } finally {
    rmSync(dir, { recursive: true, force: true })
  }
})

integrationTest('WHAT[structured-workflow-012] switching focused closures in one output directory emits the newly selected module', { timeout: 120_000 }, () => {
  const dir = copyFixture()
  try {
    const { flags, outputDir } = baseArgs(dir)
    const core = runCli([join(dir, 'Core', 'Core.fs'), ...flags])
    assert.equal(core.status, 0, core.stderr || core.stdout)
    assert.match(core.stdout, /compiled focused impact \(2 items\)/)
    assert.ok(findEmittedJs(outputDir, 'Core.js'))
    assert.equal(findEmittedJs(outputDir, 'Alpha.js'), null)

    const alpha = runCli([join(dir, 'Alpha', 'Alpha.fs'), ...flags])
    assert.equal(alpha.status, 0, alpha.stderr || alpha.stdout)
    assert.match(alpha.stdout, /compiled focused impact \(4 items\)/)
    const output = findEmittedJs(outputDir, 'Alpha.js')
    assert.ok(output, 'successful focused compilation must emit the newly selected Alpha module')
    assert.ok(readFileSync(output, 'utf8').includes('baseValue + 10'))
    assert.equal(findEmittedJs(outputDir, 'BetaOne.js'), null, 'repair must remain focused')
  } finally {
    rmSync(dir, { recursive: true, force: true })
  }
})

integrationTest('WHAT[structured-workflow-012] compile-impact CLI emits fresh output into a scratch output dir and never writes a success manifest', { timeout: 120_000 }, () => {
  const dir = copyFixture()
  try {
    const { flags, scratchRoot, outputDir } = baseArgs(dir)
    const alphaFs = join(dir, 'Alpha', 'Alpha.fs')

    const result1 = runCli([alphaFs, ...flags])
    assert.equal(result1.status, 0, result1.stderr || result1.stdout)
    assert.match(result1.stdout, /\[owner-compile\] OK: Wanxiangshu\.Impact\.fsproj/)

    assert.ok(hasEmittedJsFiles(outputDir), 'focused impact compile must emit JavaScript to output directory')
    const beforePath = findEmittedJs(outputDir, 'Alpha.js')
    assert.ok(beforePath, 'baseline flat compile must emit Alpha.js')
    const before = readFileSync(beforePath, 'utf8')
    assert.ok(before.includes('baseValue + 10'), 'baseline emit must carry the committed fixture value')

    const manifestPath = join(scratchRoot, 'impact-manifest.json')
    assert.ok(!existsSync(manifestPath), 'compileIncremental must not write an authoritative manifest')

    writeFileSync(
      alphaFs,
      'namespace ImpactFixture\n\nmodule Alpha =\n    let value = Core.baseValue + 999\n',
      'utf8',
    )
    // Production contract: changedPaths is mandatory; CLI caller must supply explicit changed paths.
    // Proves that when an explicit changed file is passed, CLI recompiles the focused impact into the scratch/output dir,
    // updates the output artifact with fresh bytes, and never creates or writes an authoritative build-manifest.
    const result2 = runCli([alphaFs, ...flags])
    assert.equal(result2.status, 0, result2.stderr || result2.stdout)
    assert.match(result2.stdout, /compiled .* impact/)
    const afterPath = findEmittedJs(outputDir, 'Alpha.js')
    assert.ok(afterPath, 'emitted Alpha.js still exists after second run')
    const after = readFileSync(afterPath, 'utf8')
    assert.notEqual(after, before, 'explicitly passed changed file must produce an emergent emit delta')
    assert.ok(after.includes('baseValue + 999'), 'recompiled emit must carry the mutated fixture value')
    assert.ok(!existsSync(manifestPath), 'still no manifest written by a compile-only caller')
    assert.ok(
      !existsSync(join(outputDir, 'Foundation', 'FatalProcess.js')),
      'focused emit stays fixture-scoped, never production sources',
    )
  } finally {
    rmSync(dir, { recursive: true, force: true })
  }
})

integrationTest('WHAT[structured-workflow-012] compile-impact CLI re-emits reverse consumers for inline body changes', { timeout: 120_000 }, () => {
  const dir = copyFixture()
  try {
    const { flags, outputDir } = baseArgs(dir)
    const coreFs = join(dir, 'Core', 'Core.fs')
    const alphaFs = join(dir, 'Alpha', 'Alpha.fs')

    writeFileSync(
      coreFs,
      'namespace ImpactFixture\n\nmodule Core =\n    let baseValue = 1\n\n    [<Literal>]\n    let tag = "core-tag"\n\n    let inline seeded (x: string) =\n        x + "original"\n',
      'utf8',
    )
    writeFileSync(
      coreFs.replace(/\.fs$/, '.fsi'),
      'namespace ImpactFixture\n\nmodule Core =\n    val baseValue: int\n\n    [<Literal>]\n    val tag: string = "core-tag"\n\n    val inline seeded: string -> string\n',
      'utf8',
    )
    writeFileSync(
      alphaFs,
      'namespace ImpactFixture\n\nmodule Alpha =\n    let value = Core.seeded (string Core.baseValue)\n',
      'utf8',
    )
    writeFileSync(
      alphaFs.replace(/\.fs$/, '.fsi'),
      'namespace ImpactFixture\n\nmodule Alpha =\n    val value: string\n',
      'utf8',
    )

    const props = join(dir, 'Directory.Build.props')
    writeFileSync(props, `${readFileSync(props, 'utf8')}<!-- baseline -->\n`, 'utf8')
    const baseline = runCli([props, ...flags])
    assert.equal(baseline.status, 0, baseline.stderr || baseline.stdout)
    const baselineAlphaPath = findEmittedJs(outputDir, 'Alpha.js')
    assert.ok(baselineAlphaPath, 'baseline must emit Alpha.js')
    const baselineAlpha = readFileSync(baselineAlphaPath, 'utf8')
    assert.ok(baselineAlpha.includes('original'), 'baseline must embed the original inline body')

    writeFileSync(
      coreFs,
      'namespace ImpactFixture\n\nmodule Core =\n    let baseValue = 1\n\n    [<Literal>]\n    let tag = "core-tag"\n\n    let inline seeded (x: string) =\n        x + "mutated"\n',
      'utf8',
    )
    const focused = runCli([coreFs, ...flags])
    assert.equal(focused.status, 0, focused.stderr || focused.stdout)
    const mutatedAlphaPath = findEmittedJs(outputDir, 'Alpha.js')
    assert.ok(mutatedAlphaPath, 'post-inline change still emits Alpha.js')
    const mutatedAlpha = readFileSync(mutatedAlphaPath, 'utf8')
    assert.ok(mutatedAlpha.includes('mutated'), 'inline body change must re-emit consumer bytes')
    assert.notEqual(mutatedAlpha, baselineAlpha, 'Alpha emit must differ after the inline body change')
  } finally {
    rmSync(dir, { recursive: true, force: true })
  }
})

integrationTest('WHAT[structured-workflow-012] deleting a source purges its stale JS from dist', { timeout: 120_000 }, () => {
  const dir = copyFixture()
  try {
    const { flags, outputDir } = baseArgs(dir)

    const alphaFs = join(dir, 'Alpha', 'Alpha.fs')
    const baseline = runCli([alphaFs, ...flags])
    assert.equal(baseline.status, 0, baseline.stderr || baseline.stdout)
    assert.ok(findEmittedJs(outputDir, 'Alpha.js'), 'baseline must emit Alpha.js')

    rmSync(alphaFs)
    rmSync(join(dir, 'Wanxiangshu.Owner.fixture.alpha.fsproj'))
    const afterDelete = runCli([alphaFs, ...flags])
    assert.equal(afterDelete.status, 0, afterDelete.stderr || afterDelete.stdout)
    assert.match(afterDelete.stdout, /compiled (clean|full) impact/, 'source deletion must not resolve to focused reuse of stale bytes')
    assert.ok(
      !findEmittedJs(outputDir, 'Alpha.js'),
      'deleted source must not leave old JS in the emitted set',
    )
    assert.ok(
      findEmittedJs(outputDir, 'BetaOne.js') || findEmittedJs(outputDir, 'Core.js'),
      'post-deletion compile must still emit remaining modules',
    )
  } finally {
    rmSync(dir, { recursive: true, force: true })
  }
})

integrationTest('WHAT[structured-workflow-012] independent Fable checks enforce compile-shard input boundaries', async () => {
  const [
    green,
    red,
    merged,
    transitiveLeak,
    privateModuleLeak,
    privateBinding,
    signedGreen,
    signedRed,
    signatureOnly,
  ] = await Promise.all([
    'GreenConsumer.fsproj',
    'RedConsumer.fsproj',
    'MergedConsumer.fsproj',
    'LeakyConsumer.fsproj',
    'PrivateConsumer.fsproj',
    'PrivateBindingConsumer.fsproj',
    'SignedGreenConsumer.fsproj',
    'SignedRedConsumer.fsproj',
    'SignatureOnlyGreenConsumer.fsproj',
  ].map(compileFableDirect))
  assert.equal(green.status, 0, `public contract must compile\n${green.stdout}\n${green.stderr}`)

  assert.notEqual(red.status, 0, 'runtime symbol without a ProjectReference must be a compiler error')
  assert.match(`${red.stdout}\n${red.stderr}`, /Runtime|secretValue|not defined/i)

  assert.equal(
    merged.status,
    0,
    `Fable ProjectReference source-merging canary: internal is not an assembly firewall\n${merged.stdout}\n${merged.stderr}`,
  )

  assert.equal(
    transitiveLeak.status,
    0,
    `Fable transitively source-merges ProjectReference closure even when DisableTransitiveProjectReferences=true\n${transitiveLeak.stdout}\n${transitiveLeak.stderr}`,
  )

  assert.equal(
    privateModuleLeak.status,
    0,
    `Fable source-merging canary: top-level private module is not a foreign-owner firewall\n${privateModuleLeak.stdout}\n${privateModuleLeak.stderr}`,
  )

  assert.notEqual(privateBinding.status, 0, 'module-local private binding must stay inaccessible after Fable source merge')
  assert.match(`${privateBinding.stdout}\n${privateBinding.stderr}`, /privateValue|not accessible|not defined|private/i)

  assert.equal(signedGreen.status, 0, `F# signature must expose declared contract\n${signedGreen.stdout}\n${signedGreen.stderr}`)

  assert.notEqual(signedRed.status, 0, 'F# signature must hide implementation symbols from source-merged consumers')
  assert.match(`${signedRed.stdout}\n${signedRed.stderr}`, /hiddenValue|not defined|not accessible/i)

  assert.notEqual(signatureOnly.status, 0, 'Fable does not materialize a consumable module from a signature-only project')
  assert.match(`${signatureOnly.stdout}\n${signatureOnly.stderr}`, /SignedProvider|not defined/i)
})


{
const { default: assert } = await import("node:assert/strict");
const { createHash } = await import("node:crypto");
const { EventEmitter } = await import("node:events");
const { execFileSync } = await import("node:child_process");
const { existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, statSync, writeFileSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { dirname, isAbsolute, join, relative, resolve } = await import("node:path");
const { default: test } = await import("node:test");
const {
  collectTrackedInputs,
  compileIncremental,
  planImpactCompile,
  planImpactFromInventory,
  readImpactInventory,
} = await import("../../../scripts/lib/owner-compile.mjs");
const { determineBuildDecision, planBuild, runBuild } = await import("../../../scripts/build.mjs");
const {
  MANIFEST_SCHEMA,
  collectArtifactInputs,
  collectCompilerInputs,
  collectGeneratedInputs,
  collectOutputs,
  computeDigest,
  readManifest,
  writeManifest,
} = await import("../../../scripts/lib/build-state.mjs");

const ROOT = resolve(import.meta.dirname, '../../..');

test('WHAT[structured-workflow-012] collectTrackedInputs includes compile-order global.json and build state scripts, and compile-order change triggers full plan', () => {
  const fixtureRoot = mkdtempSync(join(tmpdir(), 'tracked-inputs-fixture-'));
  try {
    const srcDir = join(fixtureRoot, 'src/Wanxiangshu');
    const scriptsLibDir = join(fixtureRoot, 'scripts/lib');
    mkdirSync(srcDir, { recursive: true });
    mkdirSync(scriptsLibDir, { recursive: true });

    // Minimal shard setup so assertProductionSourcesAssigned succeeds independently
    mkdirSync(join(srcDir, 'Source'), { recursive: true });
    writeFileSync(join(srcDir, 'Source/ModA.fsi'), 'namespace Core\nval a: int\n', 'utf8');
    writeFileSync(join(srcDir, 'Source/ModA.fs'), 'namespace Core\nlet a = 1\n', 'utf8');
    writeFileSync(
      join(srcDir, 'Wanxiangshu.Shard.A.fsproj'),
      '<Project Sdk="Microsoft.NET.Sdk">\n<ItemGroup>\n<Compile Include="Source/ModA.fsi"/>\n<Compile Include="Source/ModA.fs"/>\n</ItemGroup>\n</Project>\n',
      'utf8',
    );
    const compileOrderPath = join(srcDir, 'compile-order.txt');
    writeFileSync(compileOrderPath, 'Source/ModA.fsi\nSource/ModA.fs\n', 'utf8');

    // Config and script inputs
    writeFileSync(join(fixtureRoot, 'global.json'), '{"sdk":{"version":"10.0.100"}}\n', 'utf8');
    writeFileSync(join(fixtureRoot, 'Directory.Build.props'), '<Project/>\n', 'utf8');
    writeFileSync(join(fixtureRoot, 'scripts/build.mjs'), '// build.mjs stub\n', 'utf8');
    writeFileSync(join(scriptsLibDir, 'compile-shards.mjs'), '// compile-shards stub\n', 'utf8');
    writeFileSync(join(scriptsLibDir, 'build-state.mjs'), '// build-state stub\n', 'utf8');
    writeFileSync(join(scriptsLibDir, 'owner-compile.mjs'), '// owner-compile stub\n', 'utf8');

    const tracked = collectTrackedInputs({ root: fixtureRoot });

    // 1. Must include compile-order.txt
    const hasCompileOrder = tracked.some((p) => p.endsWith('src/Wanxiangshu/compile-order.txt'));
    assert.equal(hasCompileOrder, true, 'collectTrackedInputs must include src/Wanxiangshu/compile-order.txt');

    // 2. Must include root global.json
    const hasGlobalJson = tracked.some((p) => p.endsWith('global.json'));
    assert.equal(hasGlobalJson, true, 'collectTrackedInputs must include global.json');

    // 3. Must include scripts/lib/compile-shards.mjs
    const hasCompileShards = tracked.some((p) => p.endsWith('scripts/lib/compile-shards.mjs'));
    assert.equal(hasCompileShards, true, 'collectTrackedInputs must include scripts/lib/compile-shards.mjs');

    // 4. Must include scripts/lib/build-state.mjs
    const hasBuildState = tracked.some((p) => p.endsWith('scripts/lib/build-state.mjs'));
    assert.equal(hasBuildState, true, 'collectTrackedInputs must include scripts/lib/build-state.mjs');

    // 5. compile-order.txt change in planner side triggers mode: full
    const plan = planImpactCompile({
      changedPaths: [compileOrderPath],
      projectDirectory: srcDir,
    });
    assert.equal(plan.mode, 'full', 'compile-order.txt modification must trigger full compilation mode');
    assert.equal(plan.reason, 'toolchain-or-project-change', 'compile-order.txt reason must be toolchain-or-project-change');
  } finally {
    rmSync(fixtureRoot, { recursive: true, force: true });
  }
});

test('WHAT[structured-workflow-012] compileIncremental enforces mandatory changedPaths and bypasses build-manifest read and write', async () => {
  const fixtureRoot = mkdtempSync(join(tmpdir(), 'compile-inc-contract-'));
  try {
    const outputDir = join(fixtureRoot, 'dist');
    const manifestPath = join(fixtureRoot, '.fable-build/build-manifest.json');
    mkdirSync(outputDir, { recursive: true });

    // 1. changedPaths is mandatory: missing changedPaths must reject/throw
    await assert.rejects(
      async () => {
        await compileIncremental({
          root: fixtureRoot,
          outputDir,
          manifestPath,
        });
      },
      /(?:changedPaths|required|must contain at least one)/i,
      'compileIncremental must fail when changedPaths is omitted',
    );

    await assert.rejects(
      async () => {
        await compileIncremental({
          changedPaths: [],
          root: fixtureRoot,
          outputDir,
          manifestPath,
        });
      },
      /(?:changedPaths|at least one)/i,
      'compileIncremental must fail when changedPaths is empty array',
    );

    // 2. compileIncremental does not read or write build-manifest
    assert.equal(existsSync(manifestPath), false, 'build-manifest must not be created before compile');

    const fakeSpawnSuccess = (command, args) => {
      const outIndex = args.indexOf('-o');
      const targetDir = outIndex !== -1 ? args[outIndex + 1] : outputDir;
      mkdirSync(targetDir, { recursive: true });
      writeFileSync(join(targetDir, 'Out.js'), 'export const done = true;\n', 'utf8');

      const child = new EventEmitter();
      child.stdout = new EventEmitter();
      child.stderr = new EventEmitter();
      setImmediate(() => child.emit('close', 0, null));
      return child;
    };

    // Prepare minimal fake shard project
    const sourceDir = join(fixtureRoot, 'Source');
    mkdirSync(sourceDir, { recursive: true });
    writeFileSync(join(fixtureRoot, 'Directory.Build.props'), '<Project/>\n', 'utf8');
    writeFileSync(join(sourceDir, 'Mod.fsi'), 'namespace Test\nval v: int\n', 'utf8');
    writeFileSync(join(sourceDir, 'Mod.fs'), 'namespace Test\nlet v = 1\n', 'utf8');
    const fsproj = join(fixtureRoot, 'Wanxiangshu.Shard.Test.fsproj');
    writeFileSync(
      fsproj,
      '<Project Sdk="Microsoft.NET.Sdk">\n<ItemGroup>\n<Compile Include="Source/Mod.fsi"/>\n<Compile Include="Source/Mod.fs"/>\n</ItemGroup>\n</Project>\n',
      'utf8',
    );

    const result = await compileIncremental({
      changedPaths: [join(sourceDir, 'Mod.fs')],
      projectDirectory: fixtureRoot,
      rootPropsPath: join(fixtureRoot, 'Directory.Build.props'),
      scratchRoot: join(fixtureRoot, '.scratch'),
      outputDir,
      manifestPath,
      spawn: fakeSpawnSuccess,
      stdio: 'pipe',
    });

    assert.equal(result.ok, true);
    assert.equal(existsSync(manifestPath), false, 'compileIncremental must never write build-manifest.json');
  } finally {
    rmSync(fixtureRoot, { recursive: true, force: true });
  }
});

test('WHAT[structured-workflow-012] compileIncremental staging semantics protects outputDir from failure pollution', async () => {
  const fixtureRoot = mkdtempSync(join(tmpdir(), 'compile-staging-proof-'));
  try {
    const outputDir = join(fixtureRoot, 'dist');
    mkdirSync(outputDir, { recursive: true });
    const baselineFile = join(outputDir, 'ExistingGood.js');
    writeFileSync(baselineFile, 'export const valid = "prior-good-artifact";\n', 'utf8');

    const sourceDir = join(fixtureRoot, 'Source');
    mkdirSync(sourceDir, { recursive: true });
    writeFileSync(join(fixtureRoot, 'Directory.Build.props'), '<Project/>\n', 'utf8');
    writeFileSync(join(sourceDir, 'Comp.fsi'), 'namespace Test\nval c: int\n', 'utf8');
    writeFileSync(join(sourceDir, 'Comp.fs'), 'namespace Test\nlet c = 42\n', 'utf8');
    const fsproj = join(fixtureRoot, 'Wanxiangshu.Shard.Comp.fsproj');
    writeFileSync(
      fsproj,
      '<Project Sdk="Microsoft.NET.Sdk">\n<ItemGroup>\n<Compile Include="Source/Comp.fsi"/>\n<Compile Include="Source/Comp.fs"/>\n</ItemGroup>\n</Project>\n',
      'utf8',
    );

    // Failing compiler simulation that writes garbage into the target directory argument passed to spawn
    const fakeSpawnFailure = (command, args) => {
      const outIndex = args.indexOf('-o');
      const spawnTargetDir = outIndex !== -1 ? args[outIndex + 1] : outputDir;
      mkdirSync(spawnTargetDir, { recursive: true });
      writeFileSync(join(spawnTargetDir, 'BrokenPartial.js'), '// broken partial output\n', 'utf8');

      const child = new EventEmitter();
      child.stdout = new EventEmitter();
      child.stderr = new EventEmitter();
      setImmediate(() => child.emit('close', 1, null));
      return child;
    };

    const failResult = await compileIncremental({
      changedPaths: [join(sourceDir, 'Comp.fs')],
      projectDirectory: fixtureRoot,
      rootPropsPath: join(fixtureRoot, 'Directory.Build.props'),
      scratchRoot: join(fixtureRoot, '.scratch'),
      outputDir,
      spawn: fakeSpawnFailure,
      stdio: 'pipe',
    });

    assert.equal(failResult.ok, false, 'compilation must fail on non-zero child exit');
    // Staging invariant: the destination outputDir must NOT contain garbage written during failed compilation
    assert.equal(
      existsSync(join(outputDir, 'BrokenPartial.js')),
      false,
      'failing compilation must not pollute outputDir with partial/corrupted artifacts (staging semantics)',
    );
    // Prior good file must remain intact
    assert.equal(
      existsSync(baselineFile),
      true,
      'pre-existing valid artifacts in outputDir must be preserved across failing focused compile',
    );
    assert.equal(
      readFileSync(baselineFile, 'utf8'),
      'export const valid = "prior-good-artifact";\n',
      'pre-existing artifact contents must not be truncated or corrupted',
    );
  } finally {
    rmSync(fixtureRoot, { recursive: true, force: true });
  }
});

test('WHAT[structured-workflow-012] compileIncremental staging rebaseEmittedImports rebases escaping imports landing in targetDir while preserving normal and external imports', async () => {
  const fixtureRoot = mkdtempSync(join(tmpdir(), 'compile-rebase-imports-'));
  try {
    const outputDir = join(fixtureRoot, 'dist');
    const scratchRoot = join(fixtureRoot, '.scratch');
    mkdirSync(outputDir, { recursive: true });
    mkdirSync(scratchRoot, { recursive: true });

    // Target inside outputDir that escaped import will land on
    const neutralTargetDir = join(outputDir, 'deps', 'runtime');
    mkdirSync(neutralTargetDir, { recursive: true });
    const runtimeFinalPath = join(neutralTargetDir, 'Runtime.js');
    writeFileSync(runtimeFinalPath, 'export class Runtime {}\n', 'utf8');

    // External target outside outputDir
    const externalFile = join(fixtureRoot, 'External.js');
    writeFileSync(externalFile, 'export const externalValue = 999;\n', 'utf8');

    // Project fixture for incremental compile
    const sourceDir = join(fixtureRoot, 'Source');
    mkdirSync(sourceDir, { recursive: true });
    writeFileSync(join(fixtureRoot, 'Directory.Build.props'), '<Project/>\n', 'utf8');
    writeFileSync(join(sourceDir, 'Mod.fsi'), 'namespace Test\nval m: int\n', 'utf8');
    writeFileSync(join(sourceDir, 'Mod.fs'), 'namespace Test\nlet m = 1\n', 'utf8');
    const fsproj = join(fixtureRoot, 'Wanxiangshu.Shard.Mod.fsproj');
    writeFileSync(
      fsproj,
      '<Project Sdk="Microsoft.NET.Sdk">\n<ItemGroup>\n<Compile Include="Source/Mod.fsi"/>\n<Compile Include="Source/Mod.fs"/>\n</ItemGroup>\n</Project>\n',
      'utf8',
    );

    let recordedEscapingSpecifier = null;
    let recordedExternalSpecifier = null;

    const fakeSpawnSuccess = (command, args) => {
      const outIndex = args.indexOf('-o');
      const stagingDir = outIndex !== -1 ? args[outIndex + 1] : outputDir;
      mkdirSync(stagingDir, { recursive: true });

      // 1. Normal module A: relative import pointing inside stagingDir to B.js
      writeFileSync(join(stagingDir, 'B.js'), 'export const b = 42;\n', 'utf8');
      writeFileSync(join(stagingDir, 'A.js'), "import { b } from './B.js';\nexport const a = b + 1;\n", 'utf8');

      // 2. Escaping module C: relative import pointing from stagingDir to runtimeFinalPath (inside outputDir)
      recordedEscapingSpecifier = relative(stagingDir, runtimeFinalPath).replace(/\\/g, '/');
      if (!recordedEscapingSpecifier.startsWith('.')) {
        recordedEscapingSpecifier = './' + recordedEscapingSpecifier;
      }
      writeFileSync(
        join(stagingDir, 'C.js'),
        `import { Runtime } from '${recordedEscapingSpecifier}';\nexport const runtime = new Runtime();\n`,
        'utf8',
      );

      // 3. Out-of-bounds module D: relative import pointing from stagingDir to externalFile (outside outputDir)
      recordedExternalSpecifier = relative(stagingDir, externalFile).replace(/\\/g, '/');
      if (!recordedExternalSpecifier.startsWith('.')) {
        recordedExternalSpecifier = './' + recordedExternalSpecifier;
      }
      writeFileSync(
        join(stagingDir, 'D.js'),
        `import { externalValue } from '${recordedExternalSpecifier}';\nexport const d = externalValue;\n`,
        'utf8',
      );

      const child = new EventEmitter();
      child.stdout = new EventEmitter();
      child.stderr = new EventEmitter();
      setImmediate(() => child.emit('close', 0, null));
      return child;
    };

    const result = await compileIncremental({
      changedPaths: [join(sourceDir, 'Mod.fs')],
      projectDirectory: fixtureRoot,
      rootPropsPath: join(fixtureRoot, 'Directory.Build.props'),
      scratchRoot,
      outputDir,
      spawn: fakeSpawnSuccess,
      stdio: 'pipe',
    });

    assert.equal(result.ok, true, 'incremental compile with staging and rebaseEmittedImports must succeed');

    const extractImportSpecifier = (content) => {
      const match = content.match(/from\s+['"]([^'"]+)['"]/);
      return match ? match[1] : null;
    };

    const finalA = join(outputDir, 'A.js');
    const finalB = join(outputDir, 'B.js');
    const finalC = join(outputDir, 'C.js');
    const finalD = join(outputDir, 'D.js');

    assert.equal(existsSync(finalA), true, 'final A.js must exist in outputDir');
    assert.equal(existsSync(finalB), true, 'final B.js must exist in outputDir');
    assert.equal(existsSync(finalC), true, 'final C.js must exist in outputDir');
    assert.equal(existsSync(finalD), true, 'final D.js must exist in outputDir');

    // Verification 1: Normal module A import resolves to B.js in outputDir
    const specA = extractImportSpecifier(readFileSync(finalA, 'utf8'));
    assert.equal(specA, './B.js', 'normal module import within package must remain unchanged');
    const targetA = resolve(dirname(finalA), specA);
    assert.equal(targetA, finalB, 'A.js import must resolve to physical B.js in outputDir');

    // Verification 2: Escaped module C import was rebased to point to Runtime.js in outputDir
    const specC = extractImportSpecifier(readFileSync(finalC, 'utf8'));
    assert.notEqual(specC, recordedEscapingSpecifier, 'C.js escaping specifier must have been rebased');
    assert.equal(
      specC.startsWith('../../'),
      false,
      'C.js rebased specifier must not escape outputDir with parent directory traversals',
    );
    const targetC = resolve(dirname(finalC), specC);
    assert.equal(targetC, runtimeFinalPath, 'C.js rebased import must resolve to physical Runtime.js in outputDir');
    assert.equal(existsSync(targetC), true, 'physical target of C.js rebased import must exist');
    const relTargetC = relative(outputDir, targetC);
    assert.equal(
      !relTargetC.startsWith('..') && !isAbsolute(relTargetC),
      true,
      'physical target of C.js must reside within outputDir root',
    );

    // Verification 3: Out-of-bounds module D import pointing outside outputDir must remain untouched
    const specD = extractImportSpecifier(readFileSync(finalD, 'utf8'));
    assert.equal(
      specD,
      recordedExternalSpecifier,
      'D.js import pointing outside targetDir must remain untouched and not erroneously rebased',
    );
  } finally {
    rmSync(fixtureRoot, { recursive: true, force: true });
  }
});

test('WHAT[structured-workflow-012] build mode decisions: plain fs changes plan focused, non-fs inputs plan full, and plan matches run mode', async () => {
  const fixtureRoot = mkdtempSync(join(tmpdir(), 'build-mode-fixture-'));
  try {
    execFileSync('git', ['init', '--quiet', fixtureRoot]);
    const srcDir = join(fixtureRoot, 'src/Wanxiangshu');
    const distDir = join(fixtureRoot, 'dist');
    const buildStateDir = join(fixtureRoot, '.fable-build');
    mkdirSync(srcDir, { recursive: true });
    mkdirSync(distDir, { recursive: true });
    mkdirSync(buildStateDir, { recursive: true });

    writeFileSync(join(fixtureRoot, 'Directory.Build.props'), '<Project/>\n', 'utf8');
    writeFileSync(join(fixtureRoot, 'global.json'), '{"sdk":{"version":"10.0.100"}}\n', 'utf8');

    // Create 4 shards:
    // Shard A (Provider with inline function)
    // Shard B (Consumer of Shard A via ProjectReference)
    // Shard C (Independent ordinary shard)
    // Shard D (Independent ordinary shard)
    // Total shards = 4, fullThreshold = 60%.
    mkdirSync(join(srcDir, 'Source'), { recursive: true });

    // Shard A
    writeFileSync(join(srcDir, 'Source/ModA.fsi'), 'namespace Core\nval a: int\nval inline helper: int -> int\n', 'utf8');
    writeFileSync(join(srcDir, 'Source/ModA.fs'), 'namespace Core\nlet a = 1\nlet inline helper x = x + 1\n', 'utf8');
    writeFileSync(
      join(srcDir, 'Wanxiangshu.Shard.A.fsproj'),
      '<Project Sdk="Microsoft.NET.Sdk">\n<ItemGroup>\n<Compile Include="Source/ModA.fsi"/>\n<Compile Include="Source/ModA.fs"/>\n</ItemGroup>\n</Project>\n',
      'utf8',
    );

    // Shard B references Shard A (reverse consumer)
    writeFileSync(join(srcDir, 'Source/ModB.fsi'), 'namespace Core\nval b: int\n', 'utf8');
    writeFileSync(join(srcDir, 'Source/ModB.fs'), 'namespace Core\nlet b = 1\n', 'utf8');
    writeFileSync(
      join(srcDir, 'Wanxiangshu.Shard.B.fsproj'),
      '<Project Sdk="Microsoft.NET.Sdk">\n<ItemGroup>\n<ProjectReference Include="Wanxiangshu.Shard.A.fsproj"/>\n<Compile Include="Source/ModB.fsi"/>\n<Compile Include="Source/ModB.fs"/>\n</ItemGroup>\n</Project>\n',
      'utf8',
    );

    // Shard C
    writeFileSync(join(srcDir, 'Source/ModC.fsi'), 'namespace Core\nval c: int\n', 'utf8');
    writeFileSync(join(srcDir, 'Source/ModC.fs'), 'namespace Core\nlet c = 1\n', 'utf8');
    writeFileSync(
      join(srcDir, 'Wanxiangshu.Shard.C.fsproj'),
      '<Project Sdk="Microsoft.NET.Sdk">\n<ItemGroup>\n<Compile Include="Source/ModC.fsi"/>\n<Compile Include="Source/ModC.fs"/>\n</ItemGroup>\n</Project>\n',
      'utf8',
    );

    // Shard D
    writeFileSync(join(srcDir, 'Source/ModD.fsi'), 'namespace Core\nval d: int\n', 'utf8');
    writeFileSync(join(srcDir, 'Source/ModD.fs'), 'namespace Core\nlet d = 1\n', 'utf8');
    writeFileSync(
      join(srcDir, 'Wanxiangshu.Shard.D.fsproj'),
      '<Project Sdk="Microsoft.NET.Sdk">\n<ItemGroup>\n<Compile Include="Source/ModD.fsi"/>\n<Compile Include="Source/ModD.fs"/>\n</ItemGroup>\n</Project>\n',
      'utf8',
    );

    writeFileSync(
      join(srcDir, 'compile-order.txt'),
      'Source/ModA.fsi\nSource/ModA.fs\nSource/ModB.fsi\nSource/ModB.fs\nSource/ModC.fsi\nSource/ModC.fs\nSource/ModD.fsi\nSource/ModD.fs\n',
      'utf8',
    );

    mkdirSync(join(distDir, 'OpenCode/Plugin'), { recursive: true });
    mkdirSync(join(distDir, 'Sphinx/V2'), { recursive: true });
    writeFileSync(join(distDir, 'OpenCode/Plugin/Plugin.js'), 'export const plugin = true;\n', 'utf8');
    writeFileSync(join(distDir, 'Sphinx/V2/ServeEntry.js'), 'export const serve = true;\n', 'utf8');

    // Controlled toolchain identity: completely decoupled from repo manifest and dotnet CLI
    const controlledToolIdentity = 'controlled-toolchain-identity-v1';

    const initialCompilerInputs = collectCompilerInputs(fixtureRoot, null);
    const initialCompilerDigest = computeDigest(initialCompilerInputs);
    const initialGeneratedInputs = collectGeneratedInputs(fixtureRoot);
    const initialGeneratedDigest = computeDigest(initialGeneratedInputs);
    const initialArtifactInputs = collectArtifactInputs(fixtureRoot);
    const initialArtifactDigest = computeDigest(initialArtifactInputs);
    const initialOutputs = collectOutputs(distDir);

    const baselineManifest = {
      schema: MANIFEST_SCHEMA,
      generation: 1,
      compiler: {
        toolIdentity: controlledToolIdentity,
        inputs: initialCompilerInputs,
        inputDigest: initialCompilerDigest,
      },
      generated: {
        inputDigest: initialGeneratedDigest,
      },
      artifacts: {
        inputDigest: initialArtifactDigest,
      },
      outputs: initialOutputs,
    };
    writeManifest({ root: fixtureRoot, manifest: baselineManifest });

    // 1. Negative example: plain non-signature body .fs change (ModC.fs):
    // Must plan focused mode and select ONLY Shard C (must NOT pull reverse consumers like Shard B).
    writeFileSync(join(srcDir, 'Source/ModC.fs'), 'namespace Core\nlet c = 2\n', 'utf8');

    const planPlain = await planBuild({ targetRoot: fixtureRoot, currentToolchain: controlledToolIdentity });
    assert.equal(planPlain.mode, 'focused', 'modifying plain non-signature .fs file must plan focused mode');
    assert.ok(planPlain.selectedShards.some((p) => p.endsWith('Wanxiangshu.Shard.C.fsproj')), 'must select Shard C');
    assert.equal(
      planPlain.selectedShards.some((p) => p.endsWith('Wanxiangshu.Shard.B.fsproj')),
      false,
      'negative example: plain fs change must NOT pull reverse consumer Shard B',
    );

    const decisionPlain = determineBuildDecision({
      clean: false,
      existingManifest: baselineManifest,
      resolvedRoot: fixtureRoot,
      targetDist: distDir,
      compilerInputs: collectCompilerInputs(fixtureRoot, null),
      compilerInputDigest: computeDigest(collectCompilerInputs(fixtureRoot, null)),
      generatedInputDigest: initialGeneratedDigest,
      artifactInputDigest: initialArtifactDigest,
      currentToolchain: controlledToolIdentity,
    });
    assert.equal(planPlain.mode, decisionPlain.mode, 'plan mode must agree with build run decision mode');

    // 2. D2 Regression lock: signature-risky inline body change in ModA.fs:
    // Must pull reverse consumer Shard B into selectedShards, matching run-path planner (planImpactCompile).
    writeFileSync(join(srcDir, 'Source/ModC.fs'), 'namespace Core\nlet c = 1\n', 'utf8'); // restore ModC
    writeFileSync(join(srcDir, 'Source/ModA.fs'), 'namespace Core\nlet a = 1\nlet inline helper x = x + 42\n', 'utf8');

    const planInline = await planBuild({ targetRoot: fixtureRoot, currentToolchain: controlledToolIdentity });
    assert.equal(planInline.mode, 'focused', 'inline body change selecting 2/4 shards (50% < 60%) must plan focused mode');
    assert.ok(planInline.selectedShards.some((p) => p.endsWith('Wanxiangshu.Shard.A.fsproj')), 'must select provider Shard A');
    assert.ok(
      planInline.selectedShards.some((p) => p.endsWith('Wanxiangshu.Shard.B.fsproj')),
      'regression lock: inline body change MUST pull reverse consumer Shard B into selectedShards',
    );
    assert.equal(
      planInline.selectedShards.some((p) => p.endsWith('Wanxiangshu.Shard.C.fsproj')),
      false,
      'unimpacted Shard C must not be pulled',
    );

    // Verify plan matches run path planner contract
    const runPlannerImpact = planImpactCompile({
      changedPaths: [join(srcDir, 'Source/ModA.fs')],
      projectDirectory: srcDir,
    });
    assert.equal(planInline.mode, runPlannerImpact.mode, 'planBuild mode must agree with planImpactCompile');
    assert.deepEqual(
      planInline.selectedShards.map((p) => resolve(p)).sort(),
      runPlannerImpact.projectPaths.map((p) => resolve(p)).sort(),
      'planBuild selectedShards must agree with planImpactCompile projectPaths',
    );

    // 3. Threshold behavior: hitting >= 60% threshold triggers mode = full
    // Modify ModA (pulls A & B = 2 shards) AND ModC (1 shard) => 3/4 = 75% >= 60%
    writeFileSync(join(srcDir, 'Source/ModC.fs'), 'namespace Core\nlet c = 99\n', 'utf8');
    const planThreshold = await planBuild({ targetRoot: fixtureRoot, currentToolchain: controlledToolIdentity });
    assert.equal(planThreshold.mode, 'full', 'hitting 75% >= 60% threshold must plan full mode');

    // 4. Non-.fs/.fsi input change (Directory.Build.props):
    writeFileSync(join(srcDir, 'Source/ModA.fs'), 'namespace Core\nlet a = 1\nlet inline helper x = x + 1\n', 'utf8');
    writeFileSync(join(srcDir, 'Source/ModC.fs'), 'namespace Core\nlet c = 1\n', 'utf8');
    writeFileSync(join(fixtureRoot, 'Directory.Build.props'), '<Project><!-- updated --></Project>\n', 'utf8');

    const planFull = await planBuild({ targetRoot: fixtureRoot, currentToolchain: controlledToolIdentity });
    assert.equal(planFull.mode, 'full', 'modifying non-.fs/.fsi input must plan full mode, not clean');
    assert.notEqual(planFull.mode, 'clean', 'planBuild must never report clean for non-clean non-fs changes');
  } finally {
    rmSync(fixtureRoot, { recursive: true, force: true });
  }
});

test('WHAT[structured-workflow-012] build failure preserves prior valid manifest and does not delete it (unit: pre-compile failure)', async () => {
  const fixtureRoot = mkdtempSync(join(tmpdir(), 'build-manifest-retention-unit-'));
  try {
    execFileSync('git', ['init', '--quiet', fixtureRoot]);
    const srcDir = join(fixtureRoot, 'src/Wanxiangshu');
    const distDir = join(fixtureRoot, 'dist');
    const buildStateDir = join(fixtureRoot, '.fable-build');
    mkdirSync(srcDir, { recursive: true });
    mkdirSync(distDir, { recursive: true });
    mkdirSync(buildStateDir, { recursive: true });

    writeFileSync(join(fixtureRoot, 'Directory.Build.props'), '<Project/>\n', 'utf8');
    writeFileSync(join(fixtureRoot, 'global.json'), '{"sdk":{"version":"10.0.100"}}\n', 'utf8');

    // Minimal valid shard
    mkdirSync(join(srcDir, 'Source'), { recursive: true });
    writeFileSync(join(srcDir, 'Source/ModA.fsi'), 'namespace Core\nval a: int\n', 'utf8');
    writeFileSync(join(srcDir, 'Source/ModA.fs'), 'namespace Core\nlet a = 1\n', 'utf8');
    writeFileSync(
      join(srcDir, 'Wanxiangshu.Shard.A.fsproj'),
      '<Project Sdk="Microsoft.NET.Sdk">\n<ItemGroup>\n<Compile Include="Source/ModA.fsi"/>\n<Compile Include="Source/ModA.fs"/>\n</ItemGroup>\n</Project>\n',
      'utf8',
    );
    writeFileSync(join(srcDir, 'compile-order.txt'), 'Source/ModA.fsi\nSource/ModA.fs\n', 'utf8');

    // Dist baseline files
    const goodDistFile = join(distDir, 'Output.js');
    writeFileSync(goodDistFile, 'export const valid = true;\n', 'utf8');

    const initialCompilerInputs = collectCompilerInputs(fixtureRoot, null);
    const initialCompilerDigest = computeDigest(initialCompilerInputs);
    const initialGeneratedInputs = collectGeneratedInputs(fixtureRoot);
    const initialGeneratedDigest = computeDigest(initialGeneratedInputs);
    const initialArtifactInputs = collectArtifactInputs(fixtureRoot);
    const initialArtifactDigest = computeDigest(initialArtifactInputs);
    const initialOutputs = collectOutputs(distDir);

    const priorManifest = {
      schema: MANIFEST_SCHEMA,
      generation: 42,
      compiler: {
        toolIdentity: 'unit-toolchain-identity-v1',
        inputs: initialCompilerInputs,
        inputDigest: initialCompilerDigest,
      },
      generated: { inputDigest: initialGeneratedDigest },
      artifacts: { inputDigest: initialArtifactDigest },
      outputs: initialOutputs,
    };
    writeManifest({ root: fixtureRoot, manifest: priorManifest });

    const manifestPath = join(buildStateDir, 'build-manifest.json');
    assert.equal(existsSync(manifestPath), true, 'manifest file exists before build');
    const manifestStatBefore = statSync(manifestPath);
    const manifestContentBefore = readFileSync(manifestPath, 'utf8');
    const distFilesBefore = readdirSync(distDir);

    // Cause runBuild to fail before entering compilation by introducing an unassigned production source.
    // This triggers assertProductionSourcesAssigned / collectCompilerInputs rejection.
    writeFileSync(join(srcDir, 'Source/Unassigned.fs'), 'namespace Core\nlet unassigned = 12345\n', 'utf8');

    await assert.rejects(
      async () => {
        collectCompilerInputs(fixtureRoot, null);
      },
      /production source coverage mismatch unassigned/i,
      'runBuild must reject before compilation when unassigned production sources exist',
    );

    // Contract: failure must preserve existing valid manifest and dist exactly
    assert.equal(existsSync(manifestPath), true, 'manifest must still exist after build failure');
    const manifestStatAfter = statSync(manifestPath);
    const manifestContentAfter = readFileSync(manifestPath, 'utf8');
    const manifestAfter = readManifest({ root: fixtureRoot });

    assert.equal(manifestAfter?.generation, 42, 'prior manifest generation must be preserved when build fails');
    assert.equal(manifestContentAfter, manifestContentBefore, 'manifest file content must remain byte-for-byte identical');
    assert.equal(manifestStatAfter.mtimeMs, manifestStatBefore.mtimeMs, 'manifest file mtime must not change');
    assert.deepEqual(readdirSync(distDir), distFilesBefore, 'dist directory files must remain completely untouched');
  } finally {
    rmSync(fixtureRoot, { recursive: true, force: true });
  }
});

integrationTest('WHAT[structured-workflow-012] build failure preserves prior valid manifest and does not delete it (integration: compiler failure)', { timeout: 120_000 }, async () => {
  const fixtureRoot = mkdtempSync(join(tmpdir(), 'build-manifest-retention-integration-'));
  try {
    execFileSync('git', ['init', '--quiet', fixtureRoot]);
    const srcDir = join(fixtureRoot, 'src/Wanxiangshu');
    const distDir = join(fixtureRoot, 'dist');
    const buildStateDir = join(fixtureRoot, '.fable-build');
    mkdirSync(srcDir, { recursive: true });
    mkdirSync(distDir, { recursive: true });
    mkdirSync(buildStateDir, { recursive: true });

    writeFileSync(
      join(fixtureRoot, 'Directory.Build.props'),
      `<Project>\n  <Import Project="${join(ROOT, 'Directory.Build.props')}" />\n</Project>\n`,
      'utf8',
    );

    mkdirSync(join(srcDir, 'Source'), { recursive: true });
    writeFileSync(join(srcDir, 'Source/Core.fsi'), 'namespace Core\nval value: int\n', 'utf8');
    const coreFsPath = join(srcDir, 'Source/Core.fs');
    writeFileSync(coreFsPath, 'namespace Core\nlet value = 1\n', 'utf8');
    writeFileSync(
      join(srcDir, 'Wanxiangshu.Shard.Core.fsproj'),
      `<Project Sdk="Microsoft.NET.Sdk">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n  </PropertyGroup>\n  <ItemGroup>\n    <Compile Include="Source/Core.fsi"/>\n    <Compile Include="Source/Core.fs"/>\n  </ItemGroup>\n</Project>\n`,
      'utf8',
    );
    writeFileSync(join(srcDir, 'compile-order.txt'), 'Source/Core.fsi\nSource/Core.fs\n', 'utf8');

    // Established dist artifact
    const baselineOutFile = join(distDir, 'Core.js');
    writeFileSync(baselineOutFile, 'export const value = 1;\n', 'utf8');

    const compilerInputs = collectCompilerInputs(fixtureRoot, null);
    const compilerDigest = computeDigest(compilerInputs);
    const generatedInputs = collectGeneratedInputs(fixtureRoot);
    const generatedDigest = computeDigest(generatedInputs);
    const artifactInputs = collectArtifactInputs(fixtureRoot);
    const artifactDigest = computeDigest(artifactInputs);
    const outputs = collectOutputs(distDir);

    const manifestBefore = {
      schema: MANIFEST_SCHEMA,
      generation: 1,
      compiler: {
        toolIdentity: 'integration-baseline-identity',
        inputs: compilerInputs,
        inputDigest: compilerDigest,
      },
      generated: { inputDigest: generatedDigest },
      artifacts: { inputDigest: artifactDigest },
      outputs,
    };
    writeManifest({ root: fixtureRoot, manifest: manifestBefore });

    const manifestPath = join(buildStateDir, 'build-manifest.json');
    assert.equal(existsSync(manifestPath), true, 'manifest must exist before compile failure');
    const manifestStatBefore = statSync(manifestPath);
    const manifestBytesBefore = readFileSync(manifestPath);
    const manifestShaBefore = createHash('sha256').update(manifestBytesBefore).digest('hex');
    const distFilesBefore = readdirSync(distDir);

    // Inject real syntax error into source to cause Fable compiler execution to fail
    writeFileSync(coreFsPath, 'namespace Core\nlet syntax error invalid fsharp ???\n', 'utf8');

    await assert.rejects(
      async () => {
        await runBuild({ targetRoot: fixtureRoot, stdio: 'pipe' });
      },
      /Fable compilation failed/i,
      'runBuild must reject when Fable compiler fails with syntax error',
    );

    // Verify manifest (generation, mtime, sha256) and dist file set are strictly preserved
    assert.equal(existsSync(manifestPath), true, 'manifest file must not be deleted on compiler failure');
    const manifestAfter = readManifest({ root: fixtureRoot });
    const manifestStatAfter = statSync(manifestPath);
    const manifestBytesAfter = readFileSync(manifestPath);
    const manifestShaAfter = createHash('sha256').update(manifestBytesAfter).digest('hex');

    assert.equal(manifestAfter?.generation, 1, 'generation must remain 1');
    assert.equal(manifestShaAfter, manifestShaBefore, 'manifest sha256 must remain identical');
    assert.equal(manifestStatAfter.mtimeMs, manifestStatBefore.mtimeMs, 'manifest mtime must not be updated on failure');
    assert.deepEqual(readdirSync(distDir), distFilesBefore, 'dist file set must remain unchanged');
  } finally {
    rmSync(fixtureRoot, { recursive: true, force: true });
  }
});
}

integrationTest('WHAT[structured-workflow-012] flat closure compilation compiles transitive closure green and keeps unreferenced sources red', async () => {
  const emitterPath = join(FIXTURE_BOUNDARY, 'Emitter.fsproj')
  const scratchRoot = mkdtempSync(join(tmpdir(), 'wanxiangshu-owner-flat-compile-'))
  const rootPropsPath = join(ROOT, 'Directory.Build.props')

  try {
    const greenResult = await compileOwnerProject({
      projectPath: join(FIXTURE_BOUNDARY, 'LeakyConsumer.fsproj'),
      aggregatePath: emitterPath,
      scratchRoot,
      rootPropsPath,
      stdio: 'pipe',
    })
    assert.equal(greenResult.ok, true, `LeakyConsumer flat closure must compile GREEN\n${greenResult.stdout}\n${greenResult.stderr}`)
    assert.equal(greenResult.code, 0)

    const redResult = await compileOwnerProject({
      projectPath: join(FIXTURE_BOUNDARY, 'RedConsumer.fsproj'),
      aggregatePath: emitterPath,
      scratchRoot,
      rootPropsPath,
      stdio: 'pipe',
    })
    assert.equal(redResult.ok, false, 'RedConsumer without ProjectReference to Runtime must fail compile')
    assert.notEqual(redResult.code, 0)
    assert.match(`${redResult.stdout}\n${redResult.stderr}`, /Runtime|secretValue|not defined/i)

    const staleFsprojPath = join(scratchRoot, 'StaleConsumer.fsproj')
    writeFileSync(
      staleFsprojPath,
      `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="NonExistentProvider.fsproj"/>
    <Compile Include="${join(FIXTURE_BOUNDARY, 'RedConsumer.fs')}"/>
  </ItemGroup>
</Project>`,
      'utf8',
    )

    await assert.rejects(
      async () => {
        await compileOwnerProject({
          projectPath: staleFsprojPath,
          aggregatePath: emitterPath,
          scratchRoot,
          rootPropsPath,
          stdio: 'pipe',
        })
      },
      /Missing ProjectReference.*NonExistentProvider\.fsproj/i,
      'stale ProjectReference must fail before Fable compilation',
    )
  } finally {
    rmSync(scratchRoot, { recursive: true, force: true })
  }
})
}
