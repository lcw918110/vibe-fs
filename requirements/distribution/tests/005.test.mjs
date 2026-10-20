import assert from 'node:assert/strict'
import { spawnSync } from 'node:child_process'
import { existsSync, readFileSync, readdirSync } from 'node:fs'
import path from 'node:path'
import test from 'node:test'

{
const { existsSync, mkdtempSync, mkdirSync, rmSync, writeFileSync } = await import('node:fs')
const { tmpdir } = await import('node:os')
const { join } = await import('node:path')
const { assertBuildFresh, collectOutputs, readManifest, writeManifest, MANIFEST_SCHEMA, collectCompilerInputs, collectGeneratedInputs, collectArtifactInputs, computeDigest } = await import('../../../scripts/lib/build-state.mjs')
const { resetOutputDirectory } = await import('../../../scripts/lib/owner-compile.mjs')
const { runBuild, checkOutputsValid, determineBuildDecision, stagedBackupDirFor, stageDistForFullRebuild, restoreStagedDist, recoverStaleStagedDist } = await import('../../../scripts/build.mjs')
const { execFileSync } = await import('node:child_process')

const toolchainIdentity = () => {
  let dotnetVer = 'unknown'
  let fableVer = 'unknown'
  try {
    dotnetVer = execFileSync('dotnet', ['--version'], { encoding: 'utf8' }).trim()
  } catch {}
  try {
    fableVer = execFileSync('dotnet', ['tool', 'run', 'fable', '--version'], { encoding: 'utf8' }).trim()
  } catch {}
  return `dotnet ${dotnetVer} / fable ${fableVer}`
}

const writeOrphanScenario = (root) => {
  spawnSync('git', ['-C', root, 'init'])
  mkdirSync(join(root, 'src', 'Wanxiangshu'), { recursive: true })
  const distDir = join(root, 'dist')
  const orphan = join(distDir, 'Orphan', 'Legacy.js')
  mkdirSync(join(distDir, 'Orphan'), { recursive: true })
  writeFileSync(orphan, 'export const legacy = 1\n', 'utf8')
  return { distDir, orphan }
}

test('WHAT[distribution-005] assertBuildFresh succeeds on current repository build', () => {
  const root = mkdtempSync(join(tmpdir(), 'wanxiang-fresh-build-'))
  try {
    const { distDir } = writeOrphanScenario(root)
    const compilerInputs = collectCompilerInputs(root, null)
    const compilerInputDigest = computeDigest(compilerInputs)
    const generatedInputs = collectGeneratedInputs(root)
    const generatedInputDigest = computeDigest(generatedInputs)
    const artifactInputs = collectArtifactInputs(root)
    const artifactInputDigest = computeDigest(artifactInputs)

    writeManifest({
      root,
      manifest: {
        schema: MANIFEST_SCHEMA,
        rootIdentity: root,
        outputDir: 'dist',
        generation: 1,
        compiler: { inputDigest: compilerInputDigest, inputs: compilerInputs },
        generated: { inputDigest: generatedInputDigest, inputs: generatedInputs },
        artifacts: { inputDigest: artifactInputDigest, inputs: artifactInputs },
        outputs: collectOutputs(distDir),
      },
    })

    const freshness = assertBuildFresh({ root })
    assert.ok(freshness.generation >= 1)
    assert.ok(typeof freshness.compilerInputDigest === 'string')
    assert.ok(typeof freshness.generatedInputDigest === 'string')
    assert.ok(typeof freshness.artifactInputDigest === 'string')
  } finally {
    rmSync(root, { recursive: true, force: true })
  }
})

test('WHAT[distribution-005] no-op mode preserves manifest and generation', async () => {
  const root = mkdtempSync(join(tmpdir(), 'wanxiang-noop-build-'))
  try {
    const { distDir } = writeOrphanScenario(root)
    const compilerInputs = collectCompilerInputs(root, null)
    const generatedInputs = collectGeneratedInputs(root)
    const artifactInputs = collectArtifactInputs(root)
    writeManifest({
      root,
      manifest: {
        schema: MANIFEST_SCHEMA,
        rootIdentity: root,
        outputDir: 'dist',
        generation: 7,
        compiler: {
          configuration: 'Debug',
          toolIdentity: toolchainIdentity(),
          inputDigest: computeDigest(compilerInputs),
          inputs: compilerInputs,
        },
        generated: { inputDigest: computeDigest(generatedInputs), inputs: generatedInputs },
        artifacts: { inputDigest: computeDigest(artifactInputs), inputs: artifactInputs },
        outputs: collectOutputs(distDir),
      },
    })

    const manifestBefore = readManifest({ root })
    assert.ok(manifestBefore !== null)
    const manifestBytesBefore = readFileSync(join(root, '.fable-build/build-manifest.json'))

    const result = await runBuild({ targetRoot: root, clean: false })
    assert.equal(result.ok, true)
    assert.equal(result.mode, 'no-op')
    assert.equal(result.reused, true)
    assert.equal(result.generation, manifestBefore.generation)

    const manifestAfter = readManifest({ root })
    assert.equal(manifestAfter.generation, manifestBefore.generation)
    assert.deepEqual(readFileSync(join(root, '.fable-build/build-manifest.json')), manifestBytesBefore)
  } finally {
    rmSync(root, { recursive: true, force: true })
  }
})

test('WHAT[distribution-005] manifest corruption causes assertBuildFresh to throw with code', () => {
  const root = mkdtempSync(join(tmpdir(), 'wanxiang-corrupt-manifest-'))
  try {
    const buildStateDir = join(root, '.fable-build')
    mkdirSync(buildStateDir, { recursive: true })
    writeFileSync(join(buildStateDir, 'build-manifest.json'), '{ not valid json', 'utf8')

    assert.throws(
      () => assertBuildFresh({ root }),
      (err) => {
        assert.equal(err.code, 'manifest-corrupt')
        return true
      },
    )
  } finally {
    rmSync(root, { recursive: true, force: true })
  }
})

test('WHAT[distribution-005] missing or stale output entry causes assertBuildFresh to throw', () => {
  const root = mkdtempSync(join(tmpdir(), 'wanxiang-stale-output-'))
  try {
    const buildStateDir = join(root, '.fable-build')
    const distDir = join(root, 'dist')
    mkdirSync(buildStateDir, { recursive: true })
    mkdirSync(distDir, { recursive: true })

    const outputA = join(distDir, 'A.js')
    writeFileSync(outputA, 'export const a = 1\n', 'utf8')
    const outputs = collectOutputs(distDir)

    const manifest = {
      schema: MANIFEST_SCHEMA,
      rootIdentity: root,
      outputDir: 'dist',
      generation: 1,
      compiler: { inputDigest: 'test', inputs: [] },
      generated: { inputDigest: 'test', inputs: [] },
      artifacts: { inputDigest: 'test', inputs: [] },
      outputs,
    }
    writeManifest({ root, manifest })

    // If an output file is deleted:
    rmSync(outputA)
    assert.throws(
      () => assertBuildFresh({ root }),
      (err) => {
        assert.ok(err.code === 'output-missing' || err.code === 'output-empty')
        return true
      },
    )

    // If an output file is modified with stale hash:
    writeFileSync(outputA, 'export const a = 2\n', 'utf8')
    const outputB = join(distDir, 'B.js')
    writeFileSync(outputB, 'export const b = 2\n', 'utf8')
    // Reset outputs in manifest to old A.js
    manifest.outputs = outputs
    writeManifest({ root, manifest })

    assert.throws(
      () => assertBuildFresh({ root }),
      (err) => {
        assert.ok(err.code === 'output-stale' || err.code === 'output-extra')
        return true
      },
    )
  } finally {
    rmSync(root, { recursive: true, force: true })
  }
})

test('WHAT[distribution-005] release output reset physically removes stale artifacts', () => {
  const root = mkdtempSync(join(tmpdir(), 'wanxiang-release-output-'))
  const output = join(root, 'dist')
  const stale = join(output, 'src', 'Wanxiangshu', 'Stale.js')
  try {
    mkdirSync(join(output, 'src', 'Wanxiangshu'), { recursive: true })
    writeFileSync(stale, 'export const stale = true\n', 'utf8')

    resetOutputDirectory(output)

    assert.equal(existsSync(stale), false)
    assert.equal(existsSync(output), true)
  } finally {
    rmSync(root, { recursive: true, force: true })
  }
})

test('WHAT[distribution-005] full rebuild staged swap clears orphans, restores prior dist on failure, and settles stale backups', async () => {
  const root = mkdtempSync(join(tmpdir(), 'wanxiang-orphan-staged-'))
  try {
    const { distDir, orphan } = writeOrphanScenario(root)
    const backupDir = stagedBackupDirFor(distDir)

    const resetOrphan = () => {
      rmSync(distDir, { recursive: true, force: true })
      mkdirSync(join(distDir, 'Orphan'), { recursive: true })
      writeFileSync(orphan, 'export const legacy = 1\n', 'utf8')
    }

    // Stage: the prior dist (orphan included) moves aside and the compile
    // step sees a blank dist, so orphaned outputs cannot survive a
    // successful full rebuild into the next manifest snapshot.
    stageDistForFullRebuild(distDir)
    assert.equal(existsSync(orphan), false, 'staged full rebuild must compile into a blank dist')
    assert.equal(existsSync(join(backupDir, 'Orphan', 'Legacy.js')), true, 'prior dist must be recoverable at the staged backup location')

    // Restore (failed rebuild): half-built outputs are dropped and the prior
    // dist — orphan included — comes back intact. The end-to-end
    // compile-failure path through runBuild is owned by structured-workflow-012.
    writeFileSync(join(distDir, 'Halfbuilt.js'), 'export const half = 1\n', 'utf8')
    restoreStagedDist(distDir, backupDir)
    assert.equal(existsSync(orphan), true, 'failed full rebuild must restore the prior dist')
    assert.equal(existsSync(join(distDir, 'Halfbuilt.js')), false, 'half-built outputs must be dropped on restore')
    assert.equal(existsSync(backupDir), false, 'restore must consume the staged backup')

    // Recover, interrupted-build residue: dist with committed content is
    // authoritative and the stale backup is dropped.
    resetOrphan()
    stageDistForFullRebuild(distDir)
    writeFileSync(join(distDir, 'Fresh.js'), 'export const fresh = 1\n', 'utf8')
    writeManifest({ root, manifest: { schema: MANIFEST_SCHEMA, generation: 2, outputs: collectOutputs(distDir) } })
    const committedManifestBytes = readFileSync(join(root, '.fable-build/build-manifest.json'))
    recoverStaleStagedDist(distDir)
    assert.deepEqual(readdirSync(distDir), ['Fresh.js'])
    assert.equal(readFileSync(join(distDir, 'Fresh.js'), 'utf8'), 'export const fresh = 1\n', 'committed bytes must stay authoritative')
    assert.equal(existsSync(backupDir), false, 'stale backup must be dropped when dist has committed content')
    assert.deepEqual(readFileSync(join(root, '.fable-build/build-manifest.json')), committedManifestBytes)

    // Recover, missing dist: the stale backup is the last known-good dist
    // and is restored.
    resetOrphan()
    stageDistForFullRebuild(distDir)
    rmSync(distDir, { recursive: true, force: true })
    recoverStaleStagedDist(distDir)
    assert.equal(existsSync(orphan), true, 'missing dist must be restored from the stale backup')
    assert.equal(existsSync(backupDir), false, 'recovery must consume the stale backup')

    // Wiring: runBuild settles stale backups on entry. The manifest below
    // matches the restored dist exactly, so the build is a no-op and the
    // only dist mutation available is the entry-time stale recovery.
    resetOrphan()
    stageDistForFullRebuild(distDir)
    writeFileSync(join(distDir, 'Uncommitted.js'), 'export const incomplete = true\n', 'utf8')
    const compilerInputs = collectCompilerInputs(root, null)
    const generatedInputs = collectGeneratedInputs(root)
    const artifactInputs = collectArtifactInputs(root)
    writeManifest({
      root,
      manifest: {
        schema: MANIFEST_SCHEMA,
        generation: 1,
        compiler: {
          toolIdentity: toolchainIdentity(),
          inputDigest: computeDigest(compilerInputs),
          inputs: compilerInputs,
        },
        generated: { inputDigest: computeDigest(generatedInputs) },
        artifacts: { inputDigest: computeDigest(artifactInputs) },
        outputs: collectOutputs(backupDir),
      },
    })
    const result = await runBuild({ targetRoot: root })
    assert.equal(result.mode, 'no-op')
    assert.equal(existsSync(orphan), true, 'entry recovery must restore the prior dist before the no-op decision')
    assert.equal(existsSync(join(distDir, 'Uncommitted.js')), false)
    assert.equal(existsSync(backupDir), false, 'runBuild must consume the stale backup on entry')
  } finally {
    rmSync(root, { recursive: true, force: true })
  }
})

test('WHAT[distribution-005] staged recovery restores committed bytes after process exit with uncommitted partial output', () => {
  const root = mkdtempSync(join(tmpdir(), 'wanxiang-recovery-exit-'))
  const distDir = join(root, 'dist')
  const manifestPath = join(root, '.fable-build/build-manifest.json')
  const baseline = new Map([
    ['A.js', 'export const a = "last-known-good A"\n'],
    ['B.js', 'export const b = "last-known-good B"\n'],
  ])
  try {
    mkdirSync(distDir)
    for (const [name, bytes] of baseline) writeFileSync(join(distDir, name), bytes)
    const manifest = {
      schema: MANIFEST_SCHEMA,
      generation: 7,
      compiler: { toolIdentity: 'controlled-toolchain', inputs: [], inputDigest: 'compiler' },
      generated: { inputDigest: 'generated' },
      artifacts: { inputDigest: 'artifacts' },
      outputs: collectOutputs(distDir),
    }
    writeManifest({ root, manifest })
    const manifestBytes = readFileSync(manifestPath)
    assert.equal(checkOutputsValid(manifest, distDir), true)

    // Exercise a real process exit after staging and partial target emission;
    // this is the recovery boundary, not a Fable-internal crash injection.
    const buildModule = new URL('../../../scripts/build.mjs', import.meta.url).href
    const child = spawnSync(process.execPath, ['--input-type=module', '-e', `
      import { writeFileSync } from 'node:fs'
      import { join } from 'node:path'
      import { stageDistForFullRebuild } from ${JSON.stringify(buildModule)}
      const dist = process.argv[1]
      stageDistForFullRebuild(dist)
      writeFileSync(join(dist, 'A.js'), 'export const a = "UNCOMMITTED"\\n')
      process.exit(23)
    `, distDir], { encoding: 'utf8' })
    assert.equal(child.error, undefined)
    assert.equal(child.status, 23, child.stderr)
    assert.equal(child.signal, null)
    assert.deepEqual(readdirSync(distDir), ['A.js'])
    assert.equal(checkOutputsValid(manifest, distDir), false)
    assert.equal(checkOutputsValid(manifest, stagedBackupDirFor(distDir)), true)
    assert.deepEqual(readFileSync(manifestPath), manifestBytes)

    recoverStaleStagedDist(distDir)

    assert.deepEqual(readdirSync(distDir).sort(), [...baseline.keys()].sort())
    for (const [name, bytes] of baseline) assert.equal(readFileSync(join(distDir, name), 'utf8'), bytes)
    assert.equal(existsSync(stagedBackupDirFor(distDir)), false)
    assert.deepEqual(readFileSync(manifestPath), manifestBytes)
    const decision = determineBuildDecision({
      existingManifest: readManifest({ root }),
      resolvedRoot: root,
      targetDist: distDir,
      compilerInputs: [],
      compilerInputDigest: 'compiler',
      generatedInputDigest: 'generated',
      artifactInputDigest: 'artifacts',
      currentToolchain: 'controlled-toolchain',
    })
    assert.equal(decision.mode, 'no-op')
    assert.equal(decision.outputsValid, true)
  } finally {
    rmSync(root, { recursive: true, force: true })
  }
})

test('WHAT[distribution-005] staged recovery rejects unavailable manifest evidence and incomplete output identities', async t => {
  for (const scenario of ['missing manifest', 'corrupt manifest', 'unsupported schema', 'empty outputs', 'missing output', 'extra output', 'changed bytes']) {
    await t.test('WHAT[distribution-005] restores backup for ' + scenario, () => {
      const root = mkdtempSync(join(tmpdir(), 'wanxiang-recovery-evidence-'))
      const distDir = join(root, 'dist')
      const manifestPath = join(root, '.fable-build/build-manifest.json')
      try {
        mkdirSync(distDir)
        writeFileSync(join(distDir, 'A.js'), 'export const a = 1\n')
        writeFileSync(join(distDir, 'B.js'), 'export const b = 2\n')
        writeManifest({ root, manifest: { schema: MANIFEST_SCHEMA, generation: 1, outputs: collectOutputs(distDir) } })
        stageDistForFullRebuild(distDir)
        writeFileSync(join(distDir, 'A.js'), 'export const a = 9\n')
        writeFileSync(join(distDir, 'B.js'), 'export const b = 2\n')
        const manifest = { schema: MANIFEST_SCHEMA, generation: 2, outputs: collectOutputs(distDir) }
        writeManifest({ root, manifest })
        if (scenario === 'missing manifest') rmSync(manifestPath)
        if (scenario === 'corrupt manifest') writeFileSync(manifestPath, '{ invalid json')
        if (scenario === 'unsupported schema') writeManifest({ root, manifest: { ...manifest, schema: 'unsupported' } })
        if (scenario === 'empty outputs') writeManifest({ root, manifest: { ...manifest, outputs: {} } })
        if (scenario === 'missing output') rmSync(join(distDir, 'B.js'))
        if (scenario === 'extra output') writeFileSync(join(distDir, 'Extra.js'), 'export const extra = 3\n')
        if (scenario === 'changed bytes') writeFileSync(join(distDir, 'A.js'), 'export const a = 8\n')
        const manifestBytes = existsSync(manifestPath) ? readFileSync(manifestPath) : null

        recoverStaleStagedDist(distDir)

        assert.deepEqual(readdirSync(distDir).sort(), ['A.js', 'B.js'])
        assert.equal(readFileSync(join(distDir, 'A.js'), 'utf8'), 'export const a = 1\n')
        assert.equal(readFileSync(join(distDir, 'B.js'), 'utf8'), 'export const b = 2\n')
        assert.equal(existsSync(stagedBackupDirFor(distDir)), false)
        assert.deepEqual(existsSync(manifestPath) ? readFileSync(manifestPath) : null, manifestBytes)
      } finally {
        rmSync(root, { recursive: true, force: true })
      }
    })
  }
})

test('WHAT[distribution-005] focused rebuild without compiler changes keeps dist intact', async () => {
  const root = mkdtempSync(join(tmpdir(), 'wanxiang-orphan-focused-'))
  try {
    const { orphan } = writeOrphanScenario(root)

    const manifest = {
      schema: MANIFEST_SCHEMA,
      generation: 1,
      compiler: { toolIdentity: toolchainIdentity(), inputDigest: 'recorded', inputs: [] },
      generated: { inputDigest: 'recorded' },
      artifacts: { inputDigest: 'recorded' },
      outputs: collectOutputs(join(root, 'dist')),
    }
    writeManifest({ root, manifest })

    await assert.rejects(runBuild({ targetRoot: root }))

    assert.equal(existsSync(orphan), true, 'focused rebuild must not clear dist outputs')
  } finally {
    rmSync(root, { recursive: true, force: true })
  }
})
}
import { fileURLToPath, pathToFileURL } from 'node:url'

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../..')

const packageResourcesUrl = pathToFileURL(
  path.join(root, 'dist/Resources/PackageResources.js'),
).href

const entryUrl = pathToFileURL(
  path.join(root, 'dist/OpenCode/Plugin/Plugin.js'),
).href

const RESOURCE_SAMPLES = [
  'provider/role/manager/en.md',
  'provider/role/manager/zh-CN.md',
  'provider/world/common-law/en.md',
  'provider/lifecycle/manager/work-activation/en.md',
  'provider/lifecycle/manager/work-activation/zh-CN.md',
]

test('WHAT[distribution-005] repository layout has one resources directory beside dist without a dist/resources copy', () => {
  // PackageResources 的 ../../../resources 必须恰好是仓库/安装根的 resources/。
  // 只核对目录布局；实际定位行为由002验证。
  const moduleDir = path.dirname(fileURLToPath(packageResourcesUrl))
  const resolvedResources = path.resolve(moduleDir, '../..', 'resources')
  const expected = path.join(root, 'resources')
  assert.equal(path.normalize(resolvedResources), path.normalize(expected))

  for (const relative of RESOURCE_SAMPLES) {
    const full = path.join(resolvedResources, relative)
    assert.ok(existsSync(full), `expected fixed path must exist: ${full}`)
    assert.ok(readFileSync(full, 'utf8').trim().length > 0, `expected fixed path non-empty: ${full}`)
  }

  // distribution-005：资源单份发布——resources/ 只存在于包根，不得复制进 dist/ 形成双副本。
  assert.equal(
    readdirSync(path.join(root, 'dist')).includes('resources'),
    false,
    'an exact lowercase resources entry must not be duplicated into dist/ (single-copy publish)',
  )
})

test.todo('WHAT[distribution-005] GAP-210: clean and incremental production outputs have identical bytes for identical inputs')
