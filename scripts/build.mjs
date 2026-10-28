#!/usr/bin/env node
import crypto from 'node:crypto'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { execFileSync } from 'node:child_process'
import { buildSourceDependency } from './wanxiangli-source.mjs'
import { residentSpawn, requestCompiler } from './compiler/client.mjs'
import { toolchain } from './compiler/toolchain.mjs'
import { claim, release } from './compiler/ownership.mjs'
import { readReferences, currentReferences, referenceKey } from './compiler/references.mjs'

import { run as runSurfaceManifest } from './checks/js-surface-manifest.mjs'
import { run as runModuleLinkage } from './checks/js-module-linkage.mjs'
import {
  compileIncremental,
  resetOutputDirectory,
  planImpactCompile,
} from './lib/owner-compile.mjs'
import {
  MANIFEST_SCHEMA,
  collectCompilerInputs,
  collectArtifactInputs,
  collectOutputs,
  computeDigest,
  readManifest,
  writeManifest,
} from './lib/build-state.mjs'

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')

// ── Diagnostics & Output ─────────────────────────────────────────────────────

function formatBanner(title, color = '\x1b[31m') {
  const line = '═'.repeat(80)
  return `${color}${line}\n  ${title}\n${line}\x1b[0m`
}

function fail(message, details = null) {
  console.error(formatBanner('BUILD FAILED'))
  if (message) console.error(message)
  if (details) console.error(`\n${details}`)
  process.exit(1)
}

function logInfo(msg) {
  console.log(`\x1b[36m[build]\x1b[0m ${msg}`)
}

async function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms))
}

// ── Build Serialization ──────────────────────────────────────────────────────

export class CrossProcessMutex {
  constructor(lockPath, name = 'lock') {
    this.lockPath = lockPath
    this.name = name
    this.owner = null
  }

  async acquire(waitTimeoutMs = 180_000) {
    const deadline = Date.now() + waitTimeoutMs
    while (Date.now() < deadline) {
      this.owner = claim(this.lockPath + '.ownership')
      if (this.owner) {
        return true
      }
      await sleep(100)
    }

    throw new Error(`Failed to acquire ${this.name} after ${waitTimeoutMs}ms (lock at ${this.lockPath})`)
  }

  release() {
    if (!this.owner) return
    release(this.lockPath + '.ownership', this.owner)
    this.owner = null
  }
}

// ── Resource & Artifact Verification ─────────────────────────────────────────

async function verifyArtifacts(targetRoot = root) {
  // Fable 在 Linux 容器内可能生成权限异常（--w-------）的 0 字节产物，
  // 导致后续 surface/linkage 遍历 readFileSync 报 EACCES/ENOENT。
  // 所有检查之前先清理这些异常产物。
  try {
    const { execFileSync } = await import('node:child_process')
    execFileSync('find', [path.join(targetRoot, 'dist'), '-type', 'f', '-size', '0', '-exec', 'rm', '-f', '{}', '+'], { stdio: 'pipe' })
  } catch {}

  const entry = path.join(targetRoot, 'dist/OpenCode/Plugin/Plugin.js')
  if (!fs.existsSync(entry)) throw new Error(`missing entry artifact: ${entry}`)

  const sphinxEntry = path.join(targetRoot, 'dist/Sphinx/V2/ServeEntry.js')
  if (!fs.existsSync(sphinxEntry)) throw new Error(`missing sphinx entry artifact: ${sphinxEntry}`)

  const providerRoles = [
    'manager',
    'engineer',
    'devops',
    'orchestrator',
    'plan',
    'advisor',
    'bookkeeper',
  ]
  for (const name of providerRoles) {
    for (const locale of ['en.md', 'zh-CN.md']) {
      const rolePath = path.join(targetRoot, 'resources/provider/role', name, locale)
      if (!fs.existsSync(rolePath)) throw new Error(`missing Role Law: ${rolePath}`)
    }
  }

  for (const leaf of ['world/common-law', 'library/ingress', 'library/closing']) {
    for (const locale of ['en.md', 'zh-CN.md']) {
      const asset = path.join(targetRoot, 'resources/provider', leaf, locale)
      if (!fs.existsSync(asset)) throw new Error(`missing provider asset: ${asset}`)
    }
  }

  // js-semantic-surface-003/005: dist surface manifest validation (post-compile).
  if (runSurfaceManifest({ root: targetRoot }) !== 0) {
    throw new Error('js-surface-manifest: dist surface manifest validation failed')
  }
  if ((await runModuleLinkage({ root: targetRoot })) !== 0) {
    throw new Error('js-module-linkage: emitted ESM graph is not package-closed')
  }
}

function getToolchainIdentity(targetRoot = root, identity = toolchain(targetRoot)) {
  return `resident ${identity.sdk} / ${identity.key} / ${referenceKey(currentReferences(readReferences(targetRoot)))}`
}

// ── Build Mode Decision ──────────────────────────────────────────────────────

export function checkOutputsValid(existingManifest, targetDist) {
  if (!fs.existsSync(targetDist)) return false
  const recordedOutputs = existingManifest?.outputs ?? {}
  const currentOutputs = collectOutputs(targetDist)
  const recordedKeys = Object.keys(recordedOutputs)
  const currentKeys = Object.keys(currentOutputs)

  return (
    recordedKeys.length > 0 &&
    recordedKeys.length === currentKeys.length &&
    recordedKeys.every(
      (k) => currentOutputs[k] && currentOutputs[k][0] === recordedOutputs[k][0],
    )
  )
}

/**
 * The compilation snapshot proves the sources did not change; it does not
 * prove the emitted artifacts are still on disk. A missing manifest output
 * must force a full recompile, or the build reports "up-to-date" while
 * shipping a tree whose products are gone.
 */
export function checkArtifactsPresent(existingManifest, targetDist) {
  if (!fs.existsSync(targetDist)) return false

  const recordedOutputs = existingManifest?.outputs ?? {}
  const recordedKeys = Object.keys(recordedOutputs)
  if (recordedKeys.length === 0) return false

  for (const rel of recordedKeys) {
    if (!fs.existsSync(path.join(targetDist, rel))) return false
  }

  return true
}

export function determineBuildDecision({
  clean = false,
  existingManifest,
  resolvedRoot,
  targetDist,
  compilerInputs,
  compilerInputDigest,
  artifactInputDigest,
  currentToolchain,
}) {
  const missingDist = !fs.existsSync(targetDist)

  const hasValidManifest = Boolean(
    existingManifest && existingManifest.schema === MANIFEST_SCHEMA,
  )

  const outputsValid = hasValidManifest && checkOutputsValid(existingManifest, targetDist)

  // The snapshot can be perfectly consistent while the artifacts it names are
  // gone; missing artifacts must force a recompile.
  const artifactsPresent = hasValidManifest && checkArtifactsPresent(existingManifest, targetDist)

  const recordedToolIdentity = existingManifest?.compiler?.toolIdentity
  // A missing manifest, missing artifact, or toolchain change does not
  // authorize a full compile. Full compilation is forbidden. The changed
  // inputs below are the impact set; compiling anything outside it hides
  // a planner bug instead of fixing it.
  const toolchainChanged = hasValidManifest && recordedToolIdentity !== currentToolchain

  const oldCompilerInputs = existingManifest?.compiler?.inputs ?? []
  const oldMap = new Map(oldCompilerInputs.map((e) => [e.path, e]))
  const currentMap = new Map(compilerInputs.map((e) => [e.path, e]))

  const changedPaths = []
  let topologyChanged = false
  let hasNonFsChange = false

  for (const curr of compilerInputs) {
    const old = oldMap.get(curr.path)
    if (!old) {
      topologyChanged = true
      changedPaths.push(path.resolve(resolvedRoot, curr.path))
    } else if (old.sha256 !== curr.sha256) {
      changedPaths.push(path.resolve(resolvedRoot, curr.path))
      const ext = path.extname(curr.path).toLowerCase()
      if (ext !== '.fs' && ext !== '.fsi') {
        hasNonFsChange = true
      }
    }
  }

  for (const old of oldCompilerInputs) {
    if (!currentMap.has(old.path)) {
      topologyChanged = true
      changedPaths.push(path.resolve(resolvedRoot, old.path))
    }
  }

  if (oldCompilerInputs.length !== compilerInputs.length) {
    topologyChanged = true
  }

  const uniqueChangedPaths = [...new Set(changedPaths)]
  if (toolchainChanged) uniqueChangedPaths.push(path.join(resolvedRoot, '.config/dotnet-tools.json'))

  if (!hasValidManifest || !outputsValid || !artifactsPresent || toolchainChanged || topologyChanged || hasNonFsChange || uniqueChangedPaths.length > 0) {
    const reason = !hasValidManifest
      ? 'manifest-missing-or-invalid'
      : !artifactsPresent
        ? (missingDist ? 'dist-missing' : 'artifacts-missing')
        : !outputsValid
          ? 'output-hash-mismatch'
          : toolchainChanged
            ? 'toolchain-mismatch'
            : topologyChanged
              ? 'compiler-inputs-topology-changed'
              : hasNonFsChange
                ? 'toolchain-or-project-change'
                : 'focused-impact'
    return {
      mode: 'focused',
      reason,
      changedPaths: uniqueChangedPaths.length > 0
        ? uniqueChangedPaths
        : compilerInputs.map((e) => path.resolve(resolvedRoot, e.path)),
      isClean: false,
      outputsValid,
      missingDist,
    }
  }

  const artifactMatch =
    artifactInputDigest === existingManifest.artifacts?.inputDigest

  if (artifactMatch) {
    return {
      mode: 'no-op',
      reason: 'build up-to-date',
      changedPaths: [],
      isClean: false,
      outputsValid: true,
      missingDist: false,
    }
  }

  return {
    mode: 'focused',
    reason: 'non-compiler-inputs-changed',
    changedPaths: [],
    isClean: false,
    outputsValid: true,
    missingDist: false,
  }
}

// ── Full Rebuild Staged Swap ─────────────────────────────────────────────────

export function stagedBackupDirFor(targetDist) {
  return path.join(path.dirname(targetDist), '.fable-build', 'dist.staged-backup')
}

// Move the current dist aside (same-filesystem rename) so a full rebuild
// compiles into a blank dist: outputs whose sources were removed cannot
// survive into the next manifest snapshot, while the prior dist stays
// recoverable until the rebuild commits.
export function stageDistForFullRebuild(targetDist) {
  const backupDir = stagedBackupDirFor(targetDist)
  if (fs.existsSync(backupDir)) {
    fs.rmSync(backupDir, { recursive: true, force: true })
  }
  if (fs.existsSync(targetDist)) {
    fs.mkdirSync(path.dirname(backupDir), { recursive: true })
    fs.renameSync(targetDist, backupDir)
  }
  fs.mkdirSync(targetDist, { recursive: true })
  return backupDir
}

// A failed full rebuild must leave the prior dist intact (fail-safe,
// structured-workflow-012): drop the half-built dist and rename the staged
// backup back into place. Without a backup (dist was already missing before
// staging), dropping the half-built dist restores the prior state.
export function restoreStagedDist(targetDist, backupDir) {
  fs.rmSync(targetDist, { recursive: true, force: true })
  if (fs.existsSync(backupDir)) {
    fs.renameSync(backupDir, targetDist)
  }
}

function matchesCommittedOutputs(targetDist) {
  try {
    const manifest = readManifest({ root: path.dirname(targetDist) })
    return manifest?.schema === MANIFEST_SCHEMA && checkOutputsValid(manifest, targetDist)
  } catch {
    return false
  }
}

// Copying outputs can be interrupted before the manifest's atomic commit.
// Only a complete match with committed output bytes authorizes dropping the
// backup; otherwise restore the prior dist without changing the manifest.
export function recoverStaleStagedDist(targetDist) {
  const backupDir = stagedBackupDirFor(targetDist)
  if (!fs.existsSync(backupDir)) return
  if (matchesCommittedOutputs(targetDist)) {
    fs.rmSync(backupDir, { recursive: true, force: true })
  } else {
    restoreStagedDist(targetDist, backupDir)
  }
}

// ── Run Build Orchestrator ───────────────────────────────────────────────────

export async function runBuild({
  targetRoot = root,
  stdio = 'inherit',
  skipSourceDependency = path.resolve(targetRoot) !== root,
  jobs = 1,
} = {}) {
  if (!Number.isInteger(jobs) || jobs < 1 || jobs > 4) throw new Error('--jobs must be an integer from 1 to 4')
  const resolvedRoot = path.resolve(targetRoot)
  const targetDist = path.join(resolvedRoot, 'dist')
  const lockFile = path.join(resolvedRoot, '.fable-build/build.lock')
  const mutex = new CrossProcessMutex(lockFile, 'build lock')
  await mutex.acquire()

  let stagedBackupDir = null
  let buildCommitted = false

  try {
    // 使用源码依赖的当前产物；不能让旧 node_modules tarball 替新 submodule 报绿。
    if (!skipSourceDependency) {
      buildSourceDependency(resolvedRoot, { jobs })
    }
    // A previously interrupted full rebuild may have left a staged backup
    // behind; settle it before reading any build state.
    recoverStaleStagedDist(targetDist)

    const existingManifest = readManifest({ root: resolvedRoot })

    const compilerInputs = collectCompilerInputs(resolvedRoot, null)
    const compilerInputDigest = computeDigest(compilerInputs)

    const artifactInputs = collectArtifactInputs(resolvedRoot)
    const artifactInputDigest = computeDigest(artifactInputs)
    const compilerIdentity = toolchain(resolvedRoot)
    const toolIdentity = getToolchainIdentity(resolvedRoot, compilerIdentity)
    const referenceInputs = currentReferences(readReferences(resolvedRoot))

    const decision = determineBuildDecision({
      existingManifest,
      resolvedRoot,
      targetDist,
      compilerInputs,
      compilerInputDigest,
      artifactInputDigest,
      currentToolchain: toolIdentity,
    })

    let buildMode = decision.mode
    const changedCompilerPaths = decision.changedPaths

    if (buildMode === 'no-op') {
      logInfo('build up-to-date (no-op)')
      return {
        ok: true,
        mode: 'no-op',
        generation: existingManifest.generation ?? 1,
        reused: true,
      }
    }

    // Full compilation is forbidden. A missing manifest or a wide change is
    // still the impact set of the files that differ, never the whole tree.
    // Wiping dist and recompiling everything hides an impact-set bug.
    let compileResult = null
    const backup = stagedBackupDirFor(targetDist)
    const copying = backup + '.copying'
    fs.rmSync(copying, { recursive: true, force: true })
    if (fs.existsSync(targetDist)) fs.cpSync(targetDist, copying, { recursive: true })
    else fs.mkdirSync(copying, { recursive: true })
    fs.renameSync(copying, backup)
    stagedBackupDir = backup

    if (changedCompilerPaths.length > 0) {
      logInfo(`Compiling F# (${decision.reason})...`)
      compileResult = await compileIncremental({
        changedPaths: changedCompilerPaths,
        isClean: false,
        root: resolvedRoot,
        outputDir: targetDist,
        scratchRoot: path.join(resolvedRoot, '.fable-build/owner-compile'),
        rootPropsPath: path.join(resolvedRoot, 'Directory.Build.props'),
        stdio,
        spawn: residentSpawn(resolvedRoot),
      })
      if (!compileResult.ok) {
        throw new Error(
          `Fable compilation failed${compileResult.signal ? ` by signal ${compileResult.signal}` : ` with exit code ${compileResult.code}`}`,
        )
      }
      logInfo(`compiled ${compileResult.mode} impact (${compileResult.compileItems?.length ?? 0} items in ${compileResult.elapsedMs}ms)`)
    }

    // 编译后先清理这些异常产物，避免后续 verifyArtifacts / collectOutputs 遍历失败。
    try {
      const { execFileSync } = await import('node:child_process')
      execFileSync('find', [targetDist, '-type', 'f', '-size', '0', '-exec', 'rm', '-f', '{}', '+'], { stdio: 'pipe' })
    } catch {}

    // Envelope & artifact verification
    await verifyArtifacts(resolvedRoot)

    // Recheck input snapshot inside lock
    const finalCompilerIdentity = toolchain(resolvedRoot)
    if (finalCompilerIdentity.key !== compilerIdentity.key || referenceKey(currentReferences(referenceInputs)) !== referenceKey(referenceInputs)
      || referenceKey(currentReferences(readReferences(resolvedRoot))) !== referenceKey(readReferences(resolvedRoot))) {
      throw new Error('Toolchain changed during compilation; run the build again')
    }
    const finalCompilerInputs = collectCompilerInputs(resolvedRoot, null)
    const finalCompilerDigest = computeDigest(finalCompilerInputs)
    if (finalCompilerDigest !== compilerInputDigest) {
      throw new Error('Mid-build mutation detected: compiler inputs changed during compilation')
    }

    const finalArtifactInputs = collectArtifactInputs(resolvedRoot)
    const finalArtifactDigest = computeDigest(finalArtifactInputs)
    if (finalArtifactDigest !== artifactInputDigest) {
      throw new Error('Mid-build mutation detected: artifact inputs changed during compilation')
    }

    // Collect final outputs
    const outputs = collectOutputs(targetDist)
    const nextGeneration = (existingManifest?.generation ?? 0) + 1

    const newManifest = {
      schema: MANIFEST_SCHEMA,
      rootIdentity: resolvedRoot,
      shardInventoryDirectory: 'src/Wanxiangshu',
      outputDir: path.relative(resolvedRoot, targetDist).replace(/\\/g, '/'),
      generation: nextGeneration,
      compiler: {
        configuration: 'Debug',
        toolIdentity: getToolchainIdentity(resolvedRoot, finalCompilerIdentity),
        inputDigest: finalCompilerDigest,
        inputs: finalCompilerInputs,
      },
      artifacts: {
        inputDigest: finalArtifactDigest,
        inputs: finalArtifactInputs,
      },
      outputs,
    }

    writeManifest({ root: resolvedRoot, manifest: newManifest })
    buildCommitted = true
    logInfo(`build ok (generation ${nextGeneration})`)
    ensureHostSnapshotDisabled()

    return {
      ok: true,
      mode: compileResult?.mode && buildMode !== 'clean' ? compileResult.mode : buildMode,
      generation: nextGeneration,
      reused: false,
    }
  } finally {
    if (stagedBackupDir) {
      try {
        if (buildCommitted) {
          fs.rmSync(stagedBackupDir, { recursive: true, force: true })
        } else {
          restoreStagedDist(targetDist, stagedBackupDir)
        }
      } catch (swapErr) {
        logInfo(`warning: staged dist swap finalize failed: ${swapErr.message}`)
      }
    }
    mutex.release()
  }
}

function ensureHostSnapshotDisabled() {
  try {
    const configDir = process.env.XDG_CONFIG_HOME
      ? path.resolve(process.env.XDG_CONFIG_HOME, 'opencode')
      : path.join(process.env.HOME || os.homedir(), '.config', 'opencode')
    const configPath = path.join(configDir, 'opencode.json')

    if (!fs.existsSync(configPath)) return

    const raw = fs.readFileSync(configPath, 'utf8')
    const parsed = JSON.parse(raw)
    if (parsed.snapshot !== false) {
      parsed.snapshot = false
      fs.writeFileSync(configPath, JSON.stringify(parsed, null, 2), 'utf8')
      logInfo(`ensured ${configPath} has snapshot: false (anti-concurrency crash guard)`)
    }
  } catch (err) {
    // Non-blocking diagnostic warning
    logInfo(`warning: could not inspect host snapshot configuration: ${err.message}`)
  }
}

export const buildEntrypoint = runBuild

/**
 * WP4: read-only preview of what the next `npm run build` would do. Uses the
 * same manifest/digest path the build itself reads — no compiler spawn, no
 * manifest write, no dist write. Returns the structured plan; callers can
 * print or inspect `plan.fableCompileInvocations` and `selectedShards`.
 */
export async function planBuild({
  targetRoot = root,
  currentToolchain,
} = {}) {
  const resolvedRoot = path.resolve(targetRoot)
  const targetDist = path.join(resolvedRoot, 'dist')
  const buildStateDirectory = path.join(resolvedRoot, '.fable-build')
  const manifestPath = path.join(buildStateDirectory, 'build-manifest.json')
  const manifest = readManifest({ root: resolvedRoot })
  const compilerInputs = collectCompilerInputs(resolvedRoot, null)
  const compilerInputDigest = computeDigest(compilerInputs)
  const artifactInputs = collectArtifactInputs(resolvedRoot)
  const artifactInputDigest = computeDigest(artifactInputs)

  const decision = determineBuildDecision({
    clean: false,
    existingManifest: manifest,
    resolvedRoot,
    targetDist,
    compilerInputs,
    compilerInputDigest,
    artifactInputDigest,
    currentToolchain: currentToolchain ?? getToolchainIdentity(resolvedRoot),
  })

  if (decision.mode === 'no-op') {
    return {
      mode: 'no-op',
      reason: decision.reason,
      changedInputs: [],
      selectedShards: [],
      compileItems: [],
      fableCompileInvocations: 0,
      manifestPath,
      compilerInputDigest,
      artifactInputDigest,
    }
  }

  if (decision.mode === 'focused' && decision.changedPaths.length === 0) {
    return {
      mode: 'focused',
      reason: decision.reason,
      changedInputs: [],
      selectedShards: [],
      compileItems: [],
      fableCompileInvocations: 0,
      manifestPath,
      compilerInputDigest,
      artifactInputDigest,
      missingDist: decision.missingDist,
    }
  }

  const plan = planImpactCompile({
    changedPaths: decision.changedPaths.length > 0
      ? decision.changedPaths
      : compilerInputs.map((entry) => path.resolve(resolvedRoot, entry.path)),
    projectDirectory: path.join(resolvedRoot, 'src/Wanxiangshu'),
  })

  return {
    mode: plan.mode,
    reason: plan.reason,
    changedInputs: decision.changedPaths.map((abs) => path.relative(resolvedRoot, abs).replace(/\\/g, '/')),
    selectedShards: plan.projectPaths,
    compileItems: plan.compileItems,
    fableCompileInvocations: 1,
    manifestPath,
    compilerInputDigest,
    artifactInputDigest,
    missingDist: decision.missingDist,
  }
}

// ── Main Entrypoint ──────────────────────────────────────────────────────────

export function parseParallelOptions(supplied) {
  const args = []
  let jobs = 1
  let parallel = false
  for (let index = 0; index < supplied.length; index++) {
    const arg = supplied[index]
    if (arg === '--parallel' || arg === '--jobs' || arg.startsWith('--jobs=')) {
      if (parallel) throw new Error('Specify --parallel or --jobs only once')
      parallel = true
      const value = arg === '--parallel' ? '2' : arg === '--jobs' ? supplied[++index] : arg.slice(7)
      if (!/^[1-4]$/.test(value ?? '')) throw new Error('--jobs must be an integer from 1 to 4')
      jobs = Number(value)
    } else args.push(arg)
  }
  if (parallel && args.some(arg => ['--plan', '--checker-status', '--checker-stop'].includes(arg))) {
    throw new Error('Parallel compilation cannot be combined with plan, status or stop')
  }
  return { args, jobs }
}

async function main() {
  let options
  try {
    options = parseParallelOptions(process.argv.slice(2))
  } catch (error) {
    fail(error.message)
    return
  }
  const { args, jobs } = options

  // Full compilation is forbidden. A flag that wipes dist and compiles every
  // source is rejected with the other unknown flags; fix the impact set.
  const knownOptions = new Set(['--plan', '--help', '-h', '--checker-status', '--checker-stop'])
  const unknown = args.filter((arg) => arg.startsWith('-') && !knownOptions.has(arg))
  if (unknown.length > 0) {
    console.error(`unknown option(s): ${unknown.join(', ')}`)
    console.error('full compilation is forbidden. Fix the impact set; do not rebuild the tree.')
    process.exit(1)
  }

  if (process.argv.includes('--help') || process.argv.includes('-h')) {
    console.log(`
Usage: node scripts/build.mjs [options]

Options:
  --parallel   Compile independent wanxiangli modules in 2 processes
  --jobs N     Use 1–4 library compiler processes (default 1); extra processes exit after the build
  --checker-status  Show the resident compiler process
  --checker-stop    Stop the resident compiler after pending work
  --plan       Compute and print the next build plan as JSON; no compile,
               resource emit, manifest, or dist write is performed
  --help, -h   Show this help message
`)
    process.exit(0)
  }

  const plan = process.argv.includes('--plan')
  if (['--plan', '--checker-status', '--checker-stop'].filter(option => process.argv.includes(option)).length > 1) {
    console.error('Choose one of --plan, --checker-status, or --checker-stop')
    process.exit(1)
  }
  const control = process.argv.includes('--checker-stop') ? 'stop' : process.argv.includes('--checker-status') ? 'status' : null
  if (control) {
    const result = await requestCompiler(root, { op: control })
    console.log(JSON.stringify(result))
    process.exitCode = result.ok ? 0 : 1
    return
  }

  if (plan) {
    try {
      const report = await planBuild({ targetRoot: root })
      process.stdout.write(JSON.stringify(report, null, 2) + '\n')
      return
    } catch (err) {
      console.error(`[build:plan] ${err.message}`)
      process.exit(1)
    }
  }

  try {
    await runBuild({ targetRoot: root, jobs })
  } catch (err) {
    fail(err.message)
  }
}

const isDirectRun = process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)
if (isDirectRun) {
  await main()
}
