#!/usr/bin/env node
import crypto from 'node:crypto'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { execFileSync } from 'node:child_process'

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
  collectGeneratedInputs,
  collectArtifactInputs,
  collectOutputs,
  computeDigest,
  readManifest,
  writeManifest,
} from './lib/build-state.mjs'
import {
  loopDetectorEnvelopeDistPath,
  loopDetectorEnvelopeRepositoryPath,
} from './lib/loop-detector-envelope-paths.mjs'

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const dist = path.join(root, 'dist')
const buildStateDir = path.join(root, '.fable-build')
const buildLockFile = path.join(buildStateDir, 'build.lock')

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

function isPidRunning(pid) {
  if (!pid || typeof pid !== 'number' || isNaN(pid) || pid <= 0) return false
  try {
    process.kill(pid, 0)
    return true
  } catch (err) {
    return err.code === 'EPERM'
  }
}

export class CrossProcessMutex {
  constructor(lockPath, name = 'lock') {
    this.lockPath = lockPath
    this.name = name
    this.held = false
  }

  async acquire(waitTimeoutMs = 180_000) {
    fs.mkdirSync(path.dirname(this.lockPath), { recursive: true })
    const deadline = Date.now() + waitTimeoutMs
    while (Date.now() < deadline) {
      try {
        const payload = JSON.stringify({ pid: process.pid })
        fs.writeFileSync(this.lockPath, payload, { flag: 'wx', encoding: 'utf8' })
        this.held = true
        return true
      } catch (err) {
        if (err.code !== 'EEXIST') throw err

        // Check if existing lock is dead or stale
        try {
          const raw = fs.readFileSync(this.lockPath, 'utf8')
          const info = JSON.parse(raw)
          const isDead = !isPidRunning(info.pid)

          if (isDead) {
            try {
              fs.unlinkSync(this.lockPath)
              continue
            } catch {}
          }
        } catch {
          try {
            fs.unlinkSync(this.lockPath)
            continue
          } catch {}
        }

        await sleep(100)
      }
    }

    throw new Error(`Failed to acquire ${this.name} after ${waitTimeoutMs}ms (lock at ${this.lockPath})`)
  }

  release() {
    if (!this.held) return
    try {
      if (fs.existsSync(this.lockPath)) {
        const raw = fs.readFileSync(this.lockPath, 'utf8')
        const info = JSON.parse(raw)
        if (info.pid === process.pid) {
          fs.unlinkSync(this.lockPath)
        }
      }
    } catch {}
    this.held = false
  }
}

// ── Resource & Artifact Verification ─────────────────────────────────────────

export function materializeLoopDetectorEnvelope(targetRoot = root) {
  // degeneration-guard-004: the envelope is derived manually into the tracked
  // repository artifact. The build never auto-derives it and never checks
  // whether it changed; it copies the repository artifact verbatim into dist.
  const source = path.join(targetRoot, loopDetectorEnvelopeRepositoryPath)
  const target = path.join(targetRoot, loopDetectorEnvelopeDistPath)
  if (!fs.existsSync(source)) {
    throw new Error(
      `missing repository loop detector envelope artifact: ${source}\n` +
        'Run `node scripts/derive-envelope.mjs` to derive it from the repository corpus.',
    )
  }
  fs.mkdirSync(path.dirname(target), { recursive: true })
  fs.copyFileSync(source, target)
}

async function verifyArtifacts(targetRoot = root) {
  materializeLoopDetectorEnvelope(targetRoot)

  const entry = path.join(targetRoot, 'dist/OpenCode/Plugin/Plugin.js')
  if (!fs.existsSync(entry)) throw new Error(`missing entry artifact: ${entry}`)

  const sphinxEntry = path.join(targetRoot, 'dist/Sphinx/V2/ServeEntry.js')
  if (!fs.existsSync(sphinxEntry)) throw new Error(`missing sphinx entry artifact: ${sphinxEntry}`)

  const enforcerRoot = path.join(targetRoot, 'resources/enforcer')
  if (!fs.existsSync(enforcerRoot)) throw new Error(`missing rulebook root: ${enforcerRoot}`)
  const ruleDirs = fs
    .readdirSync(enforcerRoot, { withFileTypes: true })
    .filter((entry) => entry.isDirectory())
    .map((entry) => entry.name)
  if (ruleDirs.length < 1) throw new Error(`enforcer rulebook has no rule directories under ${enforcerRoot}`)
  const catalogJson = path.join(enforcerRoot, 'catalog.json')
  if (fs.existsSync(catalogJson)) throw new Error(`catalog.json must be removed after folder cutover: ${catalogJson}`)

  for (const name of ['primitive-obsession', ruleDirs[0]]) {
    const enforcerMd = path.join(enforcerRoot, name, 'enforcer.md')
    const mainMd = path.join(enforcerRoot, name, 'main.md')
    if (!fs.existsSync(enforcerMd)) throw new Error(`missing rulebook file: ${enforcerMd}`)
    if (!fs.existsSync(mainMd)) throw new Error(`missing rulebook file: ${mainMd}`)
  }

  const providerRoles = [
    'manager',
    'engineer',
    'devops',
    'orchestrator',
    'blogger',
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

function getToolchainIdentity() {
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

function computeCorpusDigest(generatedInputs, targetRoot) {
  const hasher = crypto.createHash('sha256')
  for (const entry of generatedInputs) {
    const abs = path.resolve(targetRoot, entry.path)
    if (fs.existsSync(abs)) {
      hasher.update(fs.readFileSync(abs))
    }
  }
  return hasher.digest('hex')
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

export function determineBuildDecision({
  clean = false,
  existingManifest,
  resolvedRoot,
  targetDist,
  compilerInputs,
  compilerInputDigest,
  generatedInputDigest,
  artifactInputDigest,
  currentToolchain,
}) {
  const missingDist = !fs.existsSync(targetDist)

  if (clean) {
    return {
      mode: 'clean',
      reason: 'clean-requested',
      changedPaths: compilerInputs.map((e) => path.resolve(resolvedRoot, e.path)),
      isClean: true,
      outputsValid: false,
      missingDist,
    }
  }

  const hasValidManifest = Boolean(
    existingManifest && existingManifest.schema === MANIFEST_SCHEMA,
  )

  const outputsValid = hasValidManifest && checkOutputsValid(existingManifest, targetDist)

  if (!hasValidManifest || !outputsValid) {
    const reason = missingDist
      ? 'dist-missing'
      : !hasValidManifest
        ? 'manifest-missing-or-invalid'
        : 'output-hash-mismatch'
    return {
      mode: 'full',
      reason,
      changedPaths: compilerInputs.map((e) => path.resolve(resolvedRoot, e.path)),
      isClean: false,
      outputsValid,
      missingDist,
    }
  }

  const recordedToolIdentity = existingManifest.compiler?.toolIdentity
  if (recordedToolIdentity !== currentToolchain) {
    return {
      mode: 'full',
      reason: 'toolchain-mismatch',
      changedPaths: compilerInputs.map((e) => path.resolve(resolvedRoot, e.path)),
      isClean: false,
      outputsValid: true,
      missingDist: false,
    }
  }

  const oldCompilerInputs = existingManifest.compiler?.inputs ?? []
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

  if (topologyChanged || hasNonFsChange) {
    return {
      mode: 'full',
      reason: topologyChanged ? 'compiler-inputs-topology-changed' : 'toolchain-or-project-change',
      changedPaths: uniqueChangedPaths.length > 0
        ? uniqueChangedPaths
        : compilerInputs.map((e) => path.resolve(resolvedRoot, e.path)),
      isClean: false,
      outputsValid: true,
      missingDist: false,
    }
  }

  if (uniqueChangedPaths.length > 0) {
    return {
      mode: 'focused',
      reason: 'focused-impact',
      changedPaths: uniqueChangedPaths,
      isClean: false,
      outputsValid: true,
      missingDist: false,
    }
  }

  const generatedMatch =
    generatedInputDigest === existingManifest.generated?.inputDigest
  const artifactMatch =
    artifactInputDigest === existingManifest.artifacts?.inputDigest

  if (generatedMatch && artifactMatch) {
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
  clean = false,
  stdio = 'inherit',
} = {}) {
  const resolvedRoot = path.resolve(targetRoot)
  const targetDist = path.join(resolvedRoot, 'dist')
  const lockFile = path.join(resolvedRoot, '.fable-build/build.lock')
  const mutex = new CrossProcessMutex(lockFile, 'build lock')
  await mutex.acquire()

  let stagedBackupDir = null
  let buildCommitted = false

  try {
    // A previously interrupted full rebuild may have left a staged backup
    // behind; settle it before reading any build state.
    recoverStaleStagedDist(targetDist)

    const existingManifest = readManifest({ root: resolvedRoot })

    const compilerInputs = collectCompilerInputs(resolvedRoot, null)
    const compilerInputDigest = computeDigest(compilerInputs)

    const generatedInputs = collectGeneratedInputs(resolvedRoot)
    const generatedInputDigest = computeDigest(generatedInputs)

    const artifactInputs = collectArtifactInputs(resolvedRoot)
    const artifactInputDigest = computeDigest(artifactInputs)

    const decision = determineBuildDecision({
      clean,
      existingManifest,
      resolvedRoot,
      targetDist,
      compilerInputs,
      compilerInputDigest,
      generatedInputDigest,
      artifactInputDigest,
      currentToolchain: getToolchainIdentity(),
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

    let compileResult = null

    if (buildMode === 'clean') {
      logInfo('Compiling F# (clean)...')
      resetOutputDirectory(targetDist)
      compileResult = await compileIncremental({
        changedPaths: compilerInputs.map((e) => path.resolve(resolvedRoot, e.path)),
        isClean: true,
        root: resolvedRoot,
        outputDir: targetDist,
        stdio,
      })
      if (!compileResult.ok) {
        throw new Error(
          `Fable compilation failed${compileResult.signal ? ` by signal ${compileResult.signal}` : ` with exit code ${compileResult.code}`}`,
        )
      }
      logInfo(`compiled clean impact (${compileResult.compileItems?.length ?? 0} items in ${compileResult.elapsedMs}ms)`)
    } else if (buildMode === 'full') {
      logInfo('Compiling F# (full)...')
      // Full mode rebuilds from a blank slate via a staged swap: the prior
      // dist is renamed aside so outputs whose sources were removed (orphaned
      // JS) cannot survive into the next manifest snapshot, while a failed
      // rebuild restores the prior dist (structured-workflow-012 fail-safe).
      stagedBackupDir = stageDistForFullRebuild(targetDist)
      compileResult = await compileIncremental({
        changedPaths: changedCompilerPaths.length > 0
          ? changedCompilerPaths
          : compilerInputs.map((e) => path.resolve(resolvedRoot, e.path)),
        isClean: false,
        root: resolvedRoot,
        outputDir: targetDist,
        stdio,
      })
      if (!compileResult.ok) {
        throw new Error(
          `Fable compilation failed${compileResult.signal ? ` by signal ${compileResult.signal}` : ` with exit code ${compileResult.code}`}`,
        )
      }
      logInfo(`compiled full impact (${compileResult.compileItems?.length ?? 0} items in ${compileResult.elapsedMs}ms)`)
    } else if (buildMode === 'focused' && changedCompilerPaths.length > 0) {
      logInfo('Compiling F# (focused)...')
      compileResult = await compileIncremental({
        changedPaths: changedCompilerPaths,
        isClean: false,
        root: resolvedRoot,
        outputDir: targetDist,
        stdio,
      })
      if (!compileResult.ok) {
        throw new Error(
          `Fable compilation failed${compileResult.signal ? ` by signal ${compileResult.signal}` : ` with exit code ${compileResult.code}`}`,
        )
      }
      logInfo(`compiled focused impact (${compileResult.compileItems?.length ?? 0} items in ${compileResult.elapsedMs}ms)`)
    }

    // Envelope & artifact verification
    await verifyArtifacts(resolvedRoot)

    // Recheck input snapshot inside lock
    const finalCompilerInputs = collectCompilerInputs(resolvedRoot, null)
    const finalCompilerDigest = computeDigest(finalCompilerInputs)
    if (finalCompilerDigest !== compilerInputDigest) {
      throw new Error('Mid-build mutation detected: compiler inputs changed during compilation')
    }

    const finalGeneratedInputs = collectGeneratedInputs(resolvedRoot)
    const finalGeneratedDigest = computeDigest(finalGeneratedInputs)
    if (finalGeneratedDigest !== generatedInputDigest) {
      throw new Error('Mid-build mutation detected: generated inputs changed during compilation')
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
        toolIdentity: getToolchainIdentity(),
        inputDigest: finalCompilerDigest,
        inputs: finalCompilerInputs,
      },
      generated: {
        inputDigest: finalGeneratedDigest,
        corpusPathList: finalGeneratedInputs.map((e) => e.path),
        corpusDigest: computeCorpusDigest(finalGeneratedInputs, resolvedRoot),
        generatorIdentity: 'derive-loop-detector-envelope.mjs@v1',
        tokenizerIdentity: 'gpt-tokenizer@4.0.0',
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
  currentToolchain = getToolchainIdentity(),
} = {}) {
  const resolvedRoot = path.resolve(targetRoot)
  const targetDist = path.join(resolvedRoot, 'dist')
  const buildStateDirectory = path.join(resolvedRoot, '.fable-build')
  const manifestPath = path.join(buildStateDirectory, 'build-manifest.json')
  const manifest = readManifest({ root: resolvedRoot })
  const compilerInputs = collectCompilerInputs(resolvedRoot, null)
  const compilerInputDigest = computeDigest(compilerInputs)
  const generatedInputs = collectGeneratedInputs(resolvedRoot)
  const generatedInputDigest = computeDigest(generatedInputs)
  const artifactInputs = collectArtifactInputs(resolvedRoot)
  const artifactInputDigest = computeDigest(artifactInputs)

  const decision = determineBuildDecision({
    clean: false,
    existingManifest: manifest,
    resolvedRoot,
    targetDist,
    compilerInputs,
    compilerInputDigest,
    generatedInputDigest,
    artifactInputDigest,
    currentToolchain,
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
      generatedInputDigest,
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
      generatedInputDigest,
      artifactInputDigest,
      missingDist: decision.missingDist,
    }
  }

  const plan = planImpactCompile({
    changedPaths: decision.changedPaths.length > 0
      ? decision.changedPaths
      : compilerInputs.map((entry) => path.resolve(resolvedRoot, entry.path)),
    isClean: decision.isClean,
    projectDirectory: path.join(resolvedRoot, 'src/Wanxiangshu'),
    fullThreshold: 0.6,
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
    generatedInputDigest,
    artifactInputDigest,
    missingDist: decision.missingDist,
  }
}

// ── Clean Signal & Exit Handlers ─────────────────────────────────────────────

function registerSignalHandlers() {
  const cleanup = () => {
    try {
      if (fs.existsSync(buildLockFile)) {
        const raw = fs.readFileSync(buildLockFile, 'utf8')
        const info = JSON.parse(raw)
        if (info.pid === process.pid) fs.unlinkSync(buildLockFile)
      }
    } catch {}
  }

  process.on('SIGINT', () => {
    cleanup()
    process.exit(130)
  })
  process.on('SIGTERM', () => {
    cleanup()
    process.exit(143)
  })
  process.on('exit', cleanup)
}

// ── Main Entrypoint ──────────────────────────────────────────────────────────

async function main() {
  registerSignalHandlers()

  const knownOptions = new Set(['--clean', '--plan', '--help', '-h'])
  const unknown = process.argv.slice(2).filter((arg) => arg.startsWith('-') && !knownOptions.has(arg))
  if (unknown.length > 0) {
    console.error(`unknown option(s): ${unknown.join(', ')}`)
    process.exit(1)
  }

  if (process.argv.includes('--help') || process.argv.includes('-h')) {
    console.log(`
Usage: node scripts/build.mjs [options]

Options:
  --clean      Force clean full rebuild and invalidate manifest
  --plan       Compute and print the next build plan as JSON; no compile,
               resource emit, manifest, or dist write is performed
  --help, -h   Show this help message
`)
    process.exit(0)
  }

  const clean = process.argv.includes('--clean')
  const plan = process.argv.includes('--plan')

  if (plan && clean) {
    console.error('--plan is a read-only preview and cannot be combined with --clean')
    process.exit(1)
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
    await runBuild({ targetRoot: root, clean })
  } catch (err) {
    fail(err.message)
  }
}

const isDirectRun = process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)
if (isDirectRun) {
  await main()
}
