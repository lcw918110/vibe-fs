import crypto from 'node:crypto'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { execFileSync } from 'node:child_process'

import { collectTrackedInputs, computeFileHash } from './owner-compile.mjs'
import { rejectSymbolicVerificationInput } from './verification-input-path.mjs'

const MODULE_DIR = path.dirname(fileURLToPath(import.meta.url))
export const REPO_ROOT = path.resolve(MODULE_DIR, '../..')
export const MANIFEST_SCHEMA = 'build-manifest-v1'
export const DEFAULT_MANIFEST_REL = '.fable-build/build-manifest.json'

function norm(filePath) {
  return path.resolve(filePath).replace(/\\/g, '/')
}

function relPath(root, filePath) {
  return path.relative(root, filePath).replace(/\\/g, '/')
}

export function computeDigest(entries) {
  const sorted = [...entries].sort((a, b) => a.path.localeCompare(b.path))
  const hasher = crypto.createHash('sha256')
  for (const entry of sorted) {
    const hash = entry.sha256 ?? entry.hash
    hasher.update(`${entry.path}:${hash}\n`)
  }
  return hasher.digest('hex')
}

const REQUIRED_VERIFICATION_DIRS = ['src', 'scripts', 'requirements', 'resources']
const OPTIONAL_VERIFICATION_DIRS = ['.github', '.config', 'wanxiangtong']
const SKIP_DIR_NAMES = new Set([
  'node_modules',
  '.git',
  'dist',
  '.fable-build',
  'artifacts',
  'coverage',
  '.wireit',
  'bin',
  'obj',
  '.reasonix',
  '.nuget',
])

function isIgnoredVerificationFile(fileName) {
  if (fileName === '.DS_Store') return true
  if (/^Wanxiangshu\.Impact\.[0-9a-f]{64}\.fsproj$/.test(fileName)) return true
  if (fileName === '.env.example') return false
  if (fileName.startsWith('.env')) return true
  if (fileName.endsWith('.pem') || fileName.endsWith('.key') || fileName.endsWith('.tgz')) return true
  return false
}

export function collectVerificationInputs(root = REPO_ROOT) {
  const resolvedRoot = path.resolve(root)
  rejectSymbolicVerificationInput(resolvedRoot)

  for (const dirName of REQUIRED_VERIFICATION_DIRS) {
    const dirPath = path.join(resolvedRoot, dirName)
    if (fs.existsSync(dirPath)) rejectSymbolicVerificationInput(dirPath)
    if (!fs.existsSync(dirPath) || !fs.statSync(dirPath).isDirectory()) {
      const err = new Error(`Verification inputs required root directory missing: ${dirPath}`)
      err.code = 'verification-inputs-root-missing'
      err.path = dirPath
      throw err
    }
  }

  const collectedMap = new Map()

  const rootEntries = fs.readdirSync(resolvedRoot, { withFileTypes: true })
  for (const entry of rootEntries) {
    if (entry.isSymbolicLink() && !SKIP_DIR_NAMES.has(entry.name) && !isIgnoredVerificationFile(entry.name)) {
      rejectSymbolicVerificationInput(path.join(resolvedRoot, entry.name))
    }
    if (entry.isFile() && !isIgnoredVerificationFile(entry.name)) {
      const absPath = path.join(resolvedRoot, entry.name)
      const rel = relPath(resolvedRoot, absPath)
      const stat = fs.statSync(absPath)
      const sha256 = computeFileHash(absPath)
      collectedMap.set(rel, {
        path: rel,
        sha256,
        size: stat.size,
        mtimeMs: stat.mtimeMs,
      })
    }
  }

  function walk(currentDir) {
    const entries = fs.readdirSync(currentDir, { withFileTypes: true })
    for (const entry of entries) {
      const fullPath = path.join(currentDir, entry.name)
      if (entry.isSymbolicLink()) {
        if (isIgnoredVerificationFile(entry.name)) continue
        rejectSymbolicVerificationInput(fullPath)
      }
      if (entry.isDirectory()) {
        if (SKIP_DIR_NAMES.has(entry.name)) continue
        walk(fullPath)
      } else if (entry.isFile()) {
        if (isIgnoredVerificationFile(entry.name)) continue
        const rel = relPath(resolvedRoot, fullPath)
        const stat = fs.statSync(fullPath)
        const sha256 = computeFileHash(fullPath)
        collectedMap.set(rel, {
          path: rel,
          sha256,
          size: stat.size,
          mtimeMs: stat.mtimeMs,
        })
      }
    }
  }

  for (const dirName of REQUIRED_VERIFICATION_DIRS) {
    walk(path.join(resolvedRoot, dirName))
  }

  for (const dirName of OPTIONAL_VERIFICATION_DIRS) {
    const dirPath = path.join(resolvedRoot, dirName)
    if (fs.existsSync(dirPath)) rejectSymbolicVerificationInput(dirPath)
    if (fs.existsSync(dirPath) && fs.statSync(dirPath).isDirectory()) {
      walk(dirPath)
    }
  }

  return Array.from(collectedMap.values()).sort((a, b) => a.path.localeCompare(b.path))
}

export function diffVerificationInputs(before, after) {
  const beforeMap = new Map(before.map((e) => [e.path, e]))
  const afterMap = new Map(after.map((e) => [e.path, e]))

  if (beforeMap.size !== afterMap.size) {
    return { equal: false, reason: 'file-set-changed' }
  }

  for (const [p, bEntry] of beforeMap.entries()) {
    const aEntry = afterMap.get(p)
    if (!aEntry) {
      return { equal: false, reason: 'file-set-changed' }
    }
    if (bEntry.sha256 !== aEntry.sha256) {
      return { equal: false, reason: `content-changed:${p}` }
    }
  }

  return { equal: true }
}

export function collectCompilerInputs(root = REPO_ROOT, aggregatePath) {
  const resolvedRoot = path.resolve(root)

  const tracked = collectTrackedInputs({
    root: resolvedRoot,
    aggregatePath: aggregatePath ? path.resolve(aggregatePath) : null,
  })

  const results = []
  for (const abs of tracked) {
    if (fs.existsSync(abs)) {
      const stat = fs.statSync(abs)
      const sha256 = computeFileHash(abs)
      results.push({
        path: relPath(resolvedRoot, abs),
        sha256,
        mtimeMs: stat.mtimeMs,
        size: stat.size,
      })
    }
  }
  return results.sort((a, b) => a.path.localeCompare(b.path))
}

function walkDir(dir) {
  let list = []
  if (!fs.existsSync(dir)) return list
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name)
    if (entry.isDirectory()) {
      list.push(...walkDir(full))
    } else if (entry.isFile()) {
      list.push(full)
    }
  }
  return list
}

export function collectArtifactInputs(root = REPO_ROOT) {
  const resolvedRoot = path.resolve(root)
  let files = []
  try {
    files = execFileSync('git', ['-C', resolvedRoot, 'ls-files', '--cached', '-z', 'resources'])
      .toString('utf8')
      .split('\0')
      .filter(Boolean)
      .map((rel) => path.resolve(resolvedRoot, rel))
  } catch {
    const resourcesDir = path.resolve(resolvedRoot, 'resources')
    files = walkDir(resourcesDir)
  }

  const results = []
  for (const abs of files) {
    if (fs.existsSync(abs)) {
      const stat = fs.statSync(abs)
      const content = fs.readFileSync(abs)
      const sha256 = crypto.createHash('sha256').update(content).digest('hex')
      results.push({
        path: relPath(resolvedRoot, abs),
        sha256,
        mtimeMs: stat.mtimeMs,
        size: stat.size,
      })
    }
  }
  return results.sort((a, b) => a.path.localeCompare(b.path))
}

export function readManifest({ root = REPO_ROOT, manifestPath } = {}) {
  const resolvedPath = manifestPath
    ? path.resolve(manifestPath)
    : path.resolve(root, DEFAULT_MANIFEST_REL)

  if (!fs.existsSync(resolvedPath)) {
    return null
  }
  const raw = fs.readFileSync(resolvedPath, 'utf8')
  return JSON.parse(raw)
}

export function writeManifest({ root = REPO_ROOT, manifest, manifestPath } = {}) {
  const resolvedPath = manifestPath
    ? path.resolve(manifestPath)
    : path.resolve(root, DEFAULT_MANIFEST_REL)

  const dir = path.dirname(resolvedPath)
  fs.mkdirSync(dir, { recursive: true })

  const tmpPath = `${resolvedPath}.${process.pid}.${Date.now()}.${Math.random().toString(36).slice(2)}.tmp`
  const content = JSON.stringify(manifest, null, 2)
  fs.writeFileSync(tmpPath, content, 'utf8')
  fs.renameSync(tmpPath, resolvedPath)
}

export function invalidateManifest({ root = REPO_ROOT, manifestPath } = {}) {
  const resolvedPath = manifestPath
    ? path.resolve(manifestPath)
    : path.resolve(root, DEFAULT_MANIFEST_REL)

  if (fs.existsSync(resolvedPath)) {
    try {
      fs.rmSync(resolvedPath, { force: true })
    } catch {
      // ignore
    }
  }
}

export function collectOutputs(outputDir) {
  const outputs = {}
  const files = walkDir(outputDir)
  for (const abs of files) {
    const stat = fs.statSync(abs)
    const content = fs.readFileSync(abs)
    const sha256 = crypto.createHash('sha256').update(content).digest('hex')
    const rel = relPath(outputDir, abs)
    outputs[rel] = [sha256, stat.size, stat.mtimeMs]
  }
  return outputs
}

function createFreshnessError(code, filePath, reason) {
  const err = new Error(reason)
  err.code = code
  err.path = filePath
  err.reason = reason
  return err
}

export function assertBuildFresh({ root = REPO_ROOT, manifestPath } = {}) {
  const resolvedRoot = path.resolve(root)
  const resolvedManifestPath = manifestPath
    ? path.resolve(manifestPath)
    : path.resolve(resolvedRoot, DEFAULT_MANIFEST_REL)

  if (!fs.existsSync(resolvedManifestPath)) {
    throw createFreshnessError(
      'manifest-missing',
      resolvedManifestPath,
      `Build manifest not found at ${resolvedManifestPath}`,
    )
  }

  let manifest
  try {
    const raw = fs.readFileSync(resolvedManifestPath, 'utf8')
    manifest = JSON.parse(raw)
  } catch (err) {
    throw createFreshnessError(
      'manifest-corrupt',
      resolvedManifestPath,
      `Build manifest is corrupted: ${err.message}`,
    )
  }

  if (!manifest || manifest.schema !== MANIFEST_SCHEMA) {
    throw createFreshnessError(
      'schema-mismatch',
      resolvedManifestPath,
      `Manifest schema mismatch: expected '${MANIFEST_SCHEMA}', got '${manifest?.schema}'`,
    )
  }

  const outputDir = manifest.outputDir
    ? path.resolve(resolvedRoot, manifest.outputDir)
    : path.resolve(resolvedRoot, 'dist')

  if (!fs.existsSync(outputDir)) {
    throw createFreshnessError(
      'output-missing',
      outputDir,
      `Output directory does not exist: ${outputDir}`,
    )
  }

  // Check outputs against manifest.outputs
  const currentOutputs = collectOutputs(outputDir)
  const recordedOutputs = manifest.outputs ?? {}

  const currentKeys = Object.keys(currentOutputs)
  const recordedKeys = Object.keys(recordedOutputs)

  if (currentKeys.length === 0) {
    throw createFreshnessError(
      'output-empty',
      outputDir,
      `Output directory contains no emitted files: ${outputDir}`,
    )
  }

  for (const file of recordedKeys) {
    const recorded = recordedOutputs[file]
    const current = currentOutputs[file]
    if (!current) {
      throw createFreshnessError(
        'output-missing',
        path.join(outputDir, file),
        `Output file missing: ${file}`,
      )
    }
    const [recordedSha] = recorded
    const [currentSha] = current
    if (currentSha !== recordedSha) {
      throw createFreshnessError(
        'output-stale',
        path.join(outputDir, file),
        `Output file modified: ${file} (hash mismatch)`,
      )
    }
  }

  for (const file of currentKeys) {
    if (!recordedOutputs[file]) {
      throw createFreshnessError(
        'output-extra',
        path.join(outputDir, file),
        `Unrecorded extra output file: ${file}`,
      )
    }
  }

  // Verify compiler inputs
  const currentCompilerInputs = collectCompilerInputs(resolvedRoot)
  const currentCompilerDigest = computeDigest(currentCompilerInputs)
  if (currentCompilerDigest !== manifest.compiler?.inputDigest) {
    throw createFreshnessError(
      'compiler-input-stale',
      resolvedManifestPath,
      `Compiler inputs changed (digest mismatch: expected ${manifest.compiler?.inputDigest}, got ${currentCompilerDigest})`,
    )
  }

  // Verify artifact inputs
  const currentArtifactInputs = collectArtifactInputs(resolvedRoot)
  const currentArtifactDigest = computeDigest(currentArtifactInputs)
  if (currentArtifactDigest !== manifest.artifacts?.inputDigest) {
    throw createFreshnessError(
      'artifact-input-stale',
      resolvedManifestPath,
      `Artifact inputs changed (digest mismatch: expected ${manifest.artifacts?.inputDigest}, got ${currentArtifactDigest})`,
    )
  }

  return {
    generation: manifest.generation ?? 1,
    compilerInputDigest: currentCompilerDigest,
    artifactInputDigest: currentArtifactDigest,
  }
}
