// scripts/verify-package.mjs
// distribution-007 real pack+extract+consume proof.
//
// Performs real `npm pack --json --ignore-scripts --pack-destination <runDir>`
// from a temporary staging directory containing only the selected byte closure.
// This materializes bundled file: dependencies; raw npm pack in the development
// checkout is not the delivery entry point.
// streams members via `tar`, strictly validates normalized archive paths,
// compares complete closure (manifest outputs dist + git tracked resources + root files)
// and content digests, extracts into an isolated temporary directory,
// and executes an isolated external consumer test without borrowing repo node_modules.
//
// The member boundary enforced here is the normative contract of
// requirements/distribution/WHAT.md [003]/[004]/[007]:
//   1. payload          -> the manifest `files` whitelist prefixes (dist/, resources/)
//   2. package metadata -> required: package.json; optional: README.md, LICENSE
//   3. explicit bundles -> node_modules/<bundleDependencies entry>/**
// The constants and verdicts below implement that contract; they are not an
// independent whitelist of convenience.

import { execFileSync, spawn } from 'node:child_process'
import crypto from 'node:crypto'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { createRequire } from 'node:module'
import * as tar from 'tar'
import { checkCandidateDependency } from './wanxiangli-source.mjs'

import {
  assertBuildFresh,
  collectArtifactInputs,
  collectOutputs,
  readManifest,
} from './lib/build-state.mjs'

const MODULE_DIR = path.dirname(fileURLToPath(import.meta.url))
export const REPO_ROOT = path.resolve(MODULE_DIR, '..')
export const DEFAULT_PACK_RUN_DIR = path.join(REPO_ROOT, '.fable-build/verify-logs/package')

/** WHAT[distribution-003]: root members that must exist in the source tree and the archive. */
export const ROOT_REQUIRED_FILES = ['package.json']

/** WHAT[distribution-004]: root members the contract allows, shipped only when present. */
export const ROOT_OPTIONAL_FILES = ['README.md', 'LICENSE']

/** Every package-root member the member contract permits. */
export const ROOT_FILE_WHITELIST = [...ROOT_REQUIRED_FILES, ...ROOT_OPTIONAL_FILES]

export const BANNED_PREFIXES = [
  'src/',
  'src',
  'tests/',
  'tests',
  'scripts/',
  'scripts',
  'requirements/',
  'requirements',
  '.fable-build/',
  '.fable-build',
  '.git/',
  '.git',
  '.github/',
  '.github',
]

export const BANNED_PATTERNS = [/\.log$/i]
export const ALLOWED_BUNDLED_PREFIXES = ['node_modules/wanxiangli']

export function createVerificationError(code, filePath, reason) {
  const err = new Error(reason)
  err.code = code
  err.path = filePath
  err.reason = reason
  return err
}

/**
 * Normalizes an archive entry path:
 * Must begin with 'package/', strips it, normalizes slashes, strips leading './'.
 */
const TAR_PACKAGE_PREFIX = 'package/'

export function normalizeMemberPath(entryPath) {
  let normalized = String(entryPath).replace(/\\/g, '/').trim()
  if (normalized.startsWith('./')) {
    normalized = normalized.slice(2)
  }
  if (normalized === 'package' || normalized === TAR_PACKAGE_PREFIX) {
    return ''
  }
  if (normalized.startsWith(TAR_PACKAGE_PREFIX)) {
    return normalized.slice(TAR_PACKAGE_PREFIX.length)
  }
  return normalized
}

export function checkPathTraversal(member) {
  const raw = String(member).replace(/\\/g, '/')
  if (path.isAbsolute(raw) || raw.startsWith('/') || raw.startsWith('\\')) {
    throw createVerificationError(
      'path-traversal',
      member,
      `Archive member has absolute path: ${member}`,
    )
  }
  const parts = raw.split('/')
  if (parts.includes('..')) {
    throw createVerificationError(
      'path-traversal',
      member,
      `Archive member contains directory traversal '..': ${member}`,
    )
  }
}

/**
 * Contract verdict for one archive member path (WHAT[distribution-004]).
 *
 * Structural part of the boundary — package-root metadata, banned prefixes and
 * declared bundles. Payload membership (dist/**, resources/**) is decided by the
 * declared byte closure in `validateArchiveEntries`, so members outside the
 * structural rules but outside the payload are reported there as `extra-member`.
 *
 * @returns {{ allowed: true, kind: 'package-root' | 'root-required' | 'root-optional' | 'bundle' | 'other' }
 *          | { allowed: false, code: string }}
 */
export function classifyMember(member) {
  const norm = normalizeMemberPath(member)
  if (!norm) {
    // The tarball root prefix itself ('package/'), not a member.
    return { allowed: true, kind: 'package-root' }
  }

  const isRootLevel = !norm.includes('/')
  if (isRootLevel) {
    if (ROOT_REQUIRED_FILES.includes(norm)) return { allowed: true, kind: 'root-required' }
    if (ROOT_OPTIONAL_FILES.includes(norm)) return { allowed: true, kind: 'root-optional' }
  }

  if (norm === 'node_modules' || norm.startsWith('node_modules/')) {
    const allowed = ALLOWED_BUNDLED_PREFIXES.some(
      (prefix) => norm === prefix || norm.startsWith(`${prefix}/`),
    )
    return allowed
      ? { allowed: true, kind: 'bundle' }
      : { allowed: false, code: 'infiltrated-member' }
  }

  for (const banned of BANNED_PREFIXES) {
    const cleanBanned = banned.endsWith('/') ? banned.slice(0, -1) : banned
    if (norm === cleanBanned || norm.startsWith(`${cleanBanned}/`)) {
      return { allowed: false, code: 'infiltrated-member' }
    }
  }

  for (const pat of BANNED_PATTERNS) {
    if (pat.test(norm)) {
      return { allowed: false, code: 'infiltrated-member' }
    }
  }

  // Root-level name outside the metadata contract, e.g. SECURITY.md or
  // CHANGELOG.md: rejected even though npm may auto-include it when present.
  if (isRootLevel) {
    return { allowed: false, code: 'non-contract-root-member' }
  }

  return { allowed: true, kind: 'other' }
}

export function isBannedMember(member) {
  return !classifyMember(member).allowed
}

/**
 * Derives expected package member closure.
 * expected = (payload: manifest.outputs || disk dist + git tracked resources)
 *          U (root metadata: [003] required files, [004] optional files present on disk)
 *          U (declared bundles, per-file)
 * Returns Map<relativeMemberPath, { sha256, size, fullSourcePath }>
 */
export function deriveExpectedClosure({ root = REPO_ROOT } = {}) {
  const resolvedRoot = path.resolve(root)
  const manifest = readManifest({ root: resolvedRoot })

  const distOutputs = manifest?.outputs ?? collectOutputs(path.join(resolvedRoot, 'dist'))
  const resourceInputs = collectArtifactInputs(resolvedRoot)

  const expected = new Map()

  // 1. Dist outputs: relative paths under dist/
  for (const [relPathUnderDist, meta] of Object.entries(distOutputs)) {
    const normRel = path.posix.join('dist', relPathUnderDist.replace(/\\/g, '/'))
    const sha256 = Array.isArray(meta) ? meta[0] : meta?.sha256
    const size = Array.isArray(meta) ? meta[1] : meta?.size
    const fullSourcePath = path.join(resolvedRoot, normRel)
    expected.set(normRel, { sha256, size, fullSourcePath })
  }

  // 2. Resources inputs: paths already relative to resolvedRoot ('resources/...')
  for (const res of resourceInputs) {
    const normRel = res.path.replace(/\\/g, '/')
    const fullSourcePath = path.join(resolvedRoot, normRel)
    expected.set(normRel, {
      sha256: res.sha256,
      size: res.size,
      fullSourcePath,
    })
  }

  // 3. Package-root metadata (WHAT[distribution-003]/[004]). Contract-required
  // root files must exist: their absence is a contract violation, not a smaller
  // closure. Allowed optional root files join the closure only when present.
  for (const rootFile of ROOT_REQUIRED_FILES) {
    if (!fs.existsSync(path.join(resolvedRoot, rootFile))) {
      throw createVerificationError(
        'missing-root-member',
        rootFile,
        `Contract-required package-root member is missing from the source tree: ${rootFile}`,
      )
    }
  }

  for (const rootFile of ROOT_FILE_WHITELIST) {
    const fullSourcePath = path.join(resolvedRoot, rootFile)
    if (fs.existsSync(fullSourcePath)) {
      const stat = fs.statSync(fullSourcePath)
      const buf = fs.readFileSync(fullSourcePath)
      const sha256 = crypto.createHash('sha256').update(buf).digest('hex')
      expected.set(rootFile, {
        sha256,
        size: stat.size,
        fullSourcePath,
      })
    }
  }

  // 4. Explicit bundled runtime dependencies. These are part of the shipped
  // byte closure, not development node_modules leakage. Derive them from the
  // exact installed package that npm pack will bundle so package verification
  // cannot accidentally pass by borrowing a top-level dependency later.
  const packageJson = JSON.parse(fs.readFileSync(path.join(resolvedRoot, 'package.json'), 'utf8'))
  const bundled = packageJson.bundleDependencies ?? packageJson.bundledDependencies ?? []

  // 运行依赖跟随实际解析的安装内容，归档中嵌入所属 bundle，不能借主体顶层副本。
  const addRuntimeDependencies = (ownerRoot, ownerMember, ancestors = []) => {
    const owner = JSON.parse(fs.readFileSync(path.join(ownerRoot, 'package.json'), 'utf8'))
    const require = createRequire(path.join(ownerRoot, 'package.json'))
    for (const name of Object.keys(owner.dependencies ?? {})) {
      const packagePath = require.resolve(`${name}/package.json`)
      const dependencyRoot = fs.realpathSync(path.dirname(packagePath))
      if (ancestors.includes(dependencyRoot)) {
        throw createVerificationError('bundled-dependency-cycle', name, 'Cannot materialize a cyclic bundled dependency tree')
      }
      const dependencyPackage = JSON.parse(fs.readFileSync(packagePath, 'utf8'))
      const memberRoot = path.posix.join(ownerMember, 'node_modules', name)
      const listing = execFileSync(process.platform === 'win32' ? 'npm.cmd' : 'npm', [
        'pack', '--dry-run', '--ignore-scripts', '--json',
        '--cache', path.join(os.tmpdir(), 'wanxiangshu-pack-cache'),
      ], { cwd: dependencyRoot, env: npmEnvironment(), encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] })
      const files = parsePackResult(listing, dependencyPackage.name).files
      if (!Array.isArray(files)) throw createVerificationError('bundled-dependency-files-missing', name, 'npm did not return dependency files')
      for (const file of files) {
        checkPathTraversal(file.path)
        const source = path.join(dependencyRoot, file.path)
        if (!fs.lstatSync(source).isFile()) {
          throw createVerificationError('bundled-dependency-non-regular', source, 'Dependency pack input is not a regular file')
        }
        const bytes = fs.readFileSync(source)
        expected.set(path.posix.join(memberRoot, file.path), {
          sha256: crypto.createHash('sha256').update(bytes).digest('hex'),
          size: bytes.length,
          fullSourcePath: source,
        })
      }
      addRuntimeDependencies(dependencyRoot, memberRoot, [...ancestors, dependencyRoot])
    }
  }

  for (const dependency of bundled) {
    const dependencyRoot = path.join(resolvedRoot, 'node_modules', dependency)
    if (!fs.existsSync(dependencyRoot)) {
      throw createVerificationError(
        'bundled-dependency-missing',
        dependency,
        `Bundled dependency is not installed: ${dependency}`,
      )
    }

    const dependencyRealRoot = fs.realpathSync(dependencyRoot)
    const dependencyPkgPath = path.join(dependencyRealRoot, 'package.json')
    if (!fs.existsSync(dependencyPkgPath)) {
      throw createVerificationError(
        'bundled-dependency-missing',
        dependency,
        `Bundled dependency package.json is missing: ${dependency}`,
      )
    }

    const dependencyPkg = JSON.parse(fs.readFileSync(dependencyPkgPath, 'utf8'))
    const addBundledFile = (full) => {
      const stat = fs.lstatSync(full)
      if (!stat.isFile()) {
        throw createVerificationError(
          'bundled-dependency-non-regular',
          path.relative(resolvedRoot, full),
          `Bundled dependency contains a non-regular entry: ${dependency}`,
        )
      }
      const relative = path.relative(dependencyRealRoot, full).replace(/\\/g, '/')
      const member = path.posix.join('node_modules', dependency, relative)
      const bytes = fs.readFileSync(full)
      expected.set(member, {
        sha256: crypto.createHash('sha256').update(bytes).digest('hex'),
        size: bytes.length,
        fullSourcePath: full,
      })
    }

    const walkBundledDir = (dir) => {
      const pending = [dir]
      while (pending.length > 0) {
        const current = pending.pop()
        for (const entry of fs.readdirSync(current, { withFileTypes: true })) {
          const full = path.join(current, entry.name)
          if (entry.isDirectory()) {
            pending.push(full)
          } else {
            addBundledFile(full)
          }
        }
      }
    }

    if (Array.isArray(dependencyPkg.files)) {
      addBundledFile(dependencyPkgPath)
      const licensePath = path.join(dependencyRealRoot, 'LICENSE')
      if (fs.existsSync(licensePath)) addBundledFile(licensePath)
      for (const item of dependencyPkg.files) {
        const cleanRel = String(item).replace(/\\/g, '/').replace(/^\/+|\/+$/g, '')
        if (!cleanRel) continue
        const full = path.join(dependencyRealRoot, cleanRel)
        if (!fs.existsSync(full)) {
          throw createVerificationError(
            'bundled-dependency-file-missing',
            path.relative(resolvedRoot, full),
            `Declared file in bundled dependency ${dependency} does not exist: ${item}`,
          )
        }
        const stat = fs.lstatSync(full)
        if (stat.isDirectory()) {
          walkBundledDir(full)
        } else {
          addBundledFile(full)
        }
      }
    } else {
      walkBundledDir(dependencyRealRoot)
    }
    addRuntimeDependencies(dependencyRealRoot, path.posix.join('node_modules', dependency), [dependencyRealRoot])
  }

  return expected
}

/**
 * Validates tarball stream entries directly without extracting first.
 * Enforces:
 * - paths must start with 'package/'
 * - no path traversal
 * - no duplicate entries
 * - ordinary files only (reject links, special entries, directories with content)
 * - member contract (WHAT[distribution-004]): only root metadata, payload and
 *   declared bundles; non-contract root members and banned prefixes are rejected
 * - content sha256 match against expected closure
 * - no missing, no extra members
 *
 * @param {string} tarballPath
 * @param {Map<string, {sha256: string, size: number}>} expectedClosure
 * @returns {Promise<{ memberCount: number, totalBytes: number, issues: Array<{code: string, path: string, message: string}>, members: Map<string, {sha256: string, size: number}> }>}
 */
export async function validateArchiveEntries(tarballPath, expectedClosure) {
  const issues = []
  const seenMembers = new Map()

  await tar.t({
    file: tarballPath,
    onReadEntry: (entry) => {
      const rawPath = entry.path

      // Directory entries in tarball are allowed if harmless, but we only track files
      if (entry.type === 'Directory') {
        return
      }

      if (entry.type !== 'File') {
        issues.push({
          code: 'non-regular-entry',
          path: rawPath,
          message: `Archive entry is not a regular file (${entry.type}): ${rawPath}`,
        })
        return
      }

      const normalizedRaw = String(rawPath).replace(/\\/g, '/')
      if (!normalizedRaw.startsWith(TAR_PACKAGE_PREFIX)) {
        issues.push({
          code: 'invalid-prefix',
          path: rawPath,
          message: `Archive entry does not start with '${TAR_PACKAGE_PREFIX}': ${rawPath}`,
        })
        return
      }

      const member = normalizeMemberPath(rawPath)
      if (!member) {
        return
      }

      try {
        checkPathTraversal(member)
      } catch (err) {
        issues.push({
          code: err.code,
          path: member,
          message: err.reason,
        })
        return
      }

      const verdict = classifyMember(member)
      if (!verdict.allowed) {
        issues.push({
          code: verdict.code,
          path: member,
          message: `Archive member violates the [004] member contract (${verdict.code}): ${member}`,
        })
        // A root member outside the contract is also reported by the closure
        // comparison below, which never contains it.
        if (verdict.code !== 'non-contract-root-member') {
          return
        }
      }

      if (seenMembers.has(member)) {
        issues.push({
          code: 'duplicate-member',
          path: member,
          message: `Duplicate member in archive: ${member}`,
        })
        return
      }

      const bufs = []
      entry.on('data', (chunk) => bufs.push(chunk))
      entry.on('end', () => {
        const buf = Buffer.concat(bufs)
        const digest = crypto.createHash('sha256').update(buf).digest('hex')
        seenMembers.set(member, {
          size: buf.length,
          sha256: digest,
        })
      })
    },
  })

  // Compare against expected closure
  if (expectedClosure) {
    for (const [expectedPath, expectedMeta] of expectedClosure.entries()) {
      if (!seenMembers.has(expectedPath)) {
        issues.push({
          code: 'missing-member',
          path: expectedPath,
          message: `Expected member missing from archive: ${expectedPath}`,
        })
      } else {
        const actualMeta = seenMembers.get(expectedPath)
        if (actualMeta.sha256 !== expectedMeta.sha256) {
          issues.push({
            code: 'digest-mismatch',
            path: expectedPath,
            message: `Digest mismatch for ${expectedPath}: expected ${expectedMeta.sha256}, got ${actualMeta.sha256}`,
          })
        }
      }
    }

    for (const [actualPath] of seenMembers.entries()) {
      if (!expectedClosure.has(actualPath)) {
        issues.push({
          code: 'extra-member',
          path: actualPath,
          message: `Unexpected extra member in archive: ${actualPath}`,
        })
      }
    }
  }

  let totalBytes = 0
  for (const meta of seenMembers.values()) {
    totalBytes += meta.size
  }

  return {
    memberCount: seenMembers.size,
    totalBytes,
    issues,
    members: seenMembers,
  }
}

/**
 * Public validateArtifact helper for unit testing.
 */
export async function validateArtifact(tarballPath, expectedClosure) {
  const result = await validateArchiveEntries(tarballPath, expectedClosure)
  return {
    ok: result.issues.length === 0,
    ...result,
  }
}

export function parsePackResult(jsonOutput, expectedName = 'wanxiangshu') {
  let parsed
  try {
    parsed = JSON.parse(jsonOutput)
  } catch (err) {
    throw createVerificationError(
      'pack-json-invalid',
      'npm pack',
      `Failed to parse npm pack JSON output: ${err.message}`,
    )
  }

  // npm <=11 prints an array of entries; npm >=12 prints an object keyed by
  // package name. Both shapes must yield exactly one pack result.
  const entries =
    Array.isArray(parsed)
      ? parsed
      : parsed !== null && typeof parsed === 'object'
        ? Object.values(parsed)
        : null

  if (entries === null || entries.length !== 1) {
    throw createVerificationError(
      'pack-result-count',
      'npm pack',
      `Expected exactly 1 pack result entry, got ${entries === null ? typeof parsed : entries.length}`,
    )
  }

  const entry = entries[0]
  if (!entry || typeof entry !== 'object') {
    throw createVerificationError(
      'pack-result-invalid',
      'npm pack',
      'Pack result entry is not an object',
    )
  }

  if (expectedName && entry.name !== expectedName) {
    throw createVerificationError(
      'pack-name-mismatch',
      entry.name ?? 'unknown',
      `Pack result name '${entry.name}' does not match expected '${expectedName}'`,
    )
  }

  if (!entry.filename || typeof entry.filename !== 'string') {
    throw createVerificationError(
      'pack-filename-missing',
      'npm pack',
      'Pack result is missing filename',
    )
  }

  return entry
}

/**
 * Spawns a command using argv array, returns promise with stdout, stderr, code.
 */
function npmEnvironment() {
  const env = { ...process.env }
  delete env.npm_config_allow_scripts
  return env
}

function spawnProcess(cmd, args, options = {}) {
  return new Promise((resolve, reject) => {
    const child = spawn(cmd, args, {
      ...options,
      stdio: ['ignore', 'pipe', 'pipe'],
    })

    let stdout = ''
    let stderr = ''

    child.stdout.on('data', (d) => {
      stdout += d
    })
    child.stderr.on('data', (d) => {
      stderr += d
    })

    child.on('error', (err) => {
      reject(err)
    })

    child.on('close', (code, signal) => {
      resolve({ code, signal, stdout, stderr })
    })
  })
}

/**
 * Runs an isolated external consumer test against the extracted package.
 * Prepares runtime dependencies in an outside directory via npm ci --omit=dev,
 * places the extracted package under node_modules/wanxiangshu,
 * and executes consumer script with bare `import('wanxiangshu')` asserting default export shape.
 */
export async function runExternalConsumer({
  root = REPO_ROOT,
  tarballPath,
  extractedPackageDir,
  scratchDir,
}) {
  const resolvedRoot = path.resolve(root)

  // 1. Prepare isolated runtime root
  const isolatedRoot = path.join(scratchDir, 'runtime-root')
  fs.mkdirSync(isolatedRoot, { recursive: true })
  // Write clean empty npmrc files to prevent parent or user configuration pollution
  const emptyNpmrc = path.join(isolatedRoot, '.empty-npmrc')
  fs.writeFileSync(emptyNpmrc, '')
  const emptyGlobalNpmrc = path.join(isolatedRoot, '.empty-global-npmrc')
  fs.writeFileSync(emptyGlobalNpmrc, '')

  fs.copyFileSync(path.join(resolvedRoot, 'package.json'), path.join(isolatedRoot, 'package.json'))
  fs.copyFileSync(
    path.join(resolvedRoot, 'package-lock.json'),
    path.join(isolatedRoot, 'package-lock.json'),
  )

  // file: dependencies used by the source checkout must exist for the isolated
  // npm ci as well. Copy only the exact tracked artifact, never the sibling
  // source repository.
  const rootPackage = JSON.parse(fs.readFileSync(path.join(resolvedRoot, 'package.json'), 'utf8'))
  const rootLock = JSON.parse(fs.readFileSync(path.join(resolvedRoot, 'package-lock.json'), 'utf8'))
  const lockedLink = rootLock.packages?.['node_modules/wanxiangli']
  const lifecycleVersion =
    lockedLink?.version ?? (lockedLink?.resolved ? rootLock.packages?.[lockedLink.resolved]?.version : undefined)
  if (typeof lifecycleVersion !== 'string' || lifecycleVersion.length === 0) {
    throw createVerificationError(
      'consumer-lifecycle-version-missing',
      'package-lock.json',
      'The external consumer requires an exact locked wanxiangli version',
    )
  }
  for (const specifier of Object.values(rootPackage.dependencies ?? {})) {
    if (typeof specifier !== 'string' || !specifier.startsWith('file:')) continue
    const relative = specifier.slice('file:'.length)
    const source = path.resolve(resolvedRoot, relative)
    const destination = path.resolve(isolatedRoot, relative)
    if (!source.startsWith(resolvedRoot + path.sep) || !destination.startsWith(isolatedRoot + path.sep)) {
      throw createVerificationError(
        'consumer-file-dependency-outside-root',
        relative,
        `Runtime file dependency must stay inside the repository: ${relative}`,
      )
    }
    fs.mkdirSync(path.dirname(destination), { recursive: true })
    if (fs.statSync(source).isDirectory()) {
      fs.cpSync(source, destination, { recursive: true })
    } else {
      fs.copyFileSync(source, destination)
    }
  }


  // Run npm ci --omit=dev --ignore-scripts --no-audit --no-fund
  try {
    const cleanEnv = { ...process.env, npm_config_userconfig: emptyNpmrc, npm_config_globalconfig: emptyGlobalNpmrc }
    delete cleanEnv.npm_config_allow_scripts
    execFileSync(
      process.platform === 'win32' ? 'npm.cmd' : 'npm',
      ['ci', '--omit=dev', '--ignore-scripts', '--no-audit', '--no-fund'],
      {
        cwd: isolatedRoot,
        env: cleanEnv,
        stdio: 'pipe',
      },
    )
  } catch (err) {
    throw createVerificationError(
      'consumer-ci-failed',
      isolatedRoot,
      `npm ci --omit=dev failed in isolated runtime root: ${err.message}`,
    )
  }

  // 2. Put extracted package under isolated node_modules/wanxiangshu
  const targetPkgDir = path.join(isolatedRoot, 'node_modules', 'wanxiangshu')
  if (fs.existsSync(targetPkgDir)) {
    fs.rmSync(targetPkgDir, { recursive: true, force: true })
  }
  fs.cpSync(extractedPackageDir, targetPkgDir, { recursive: true })

  const bundledWanxiangli = path.join(targetPkgDir, 'node_modules', 'wanxiangli', 'package.json')
  if (!fs.existsSync(bundledWanxiangli)) {
    throw createVerificationError(
      'bundled-wanxiangli-missing',
      bundledWanxiangli,
      'Extracted wanxiangshu package does not contain its bundled wanxiangli runtime dependency',
    )
  }

  // Do not let a separately installed top-level dependency mask a missing or
  // broken bundled dependency during the actual consumer proof.
  const topLevelWanxiangli = path.join(isolatedRoot, 'node_modules', 'wanxiangli')
  if (fs.existsSync(topLevelWanxiangli)) {
    fs.rmSync(topLevelWanxiangli, { recursive: true, force: true })
  }

  // 复用所选库候选的公开消费者，导入仍从实际主体包内解析。
  fs.copyFileSync(path.join(REPO_ROOT, 'wanxiangli/scripts/tests/fission-lifecycle-consumer.mjs'),
    path.join(targetPkgDir, '.verify-fission-lifecycle.mjs'))
  for (const name of ['prefix-history', 'child-run-set', 'durable-handles', 'change']) {
    fs.copyFileSync(path.join(REPO_ROOT, `wanxiangli/scripts/tests/${name}-consumer.mjs`),
      path.join(targetPkgDir, `.verify-${name}.mjs`))
  }

  // 探针位于实际主体包内，裸导入只能使用随包交付的库。
  fs.writeFileSync(path.join(targetPkgDir, '.verify-library-entries.mjs'), [
    "import { mapBounded } from 'wanxiangli/bounded-map'",
    "import { isValid } from './dist/Participant/Provider/Attempt/TerminalValidity.js'",
    "import { encode, validationError } from 'wanxiangli/returned-material'",
    "import { KeyedSerial } from 'wanxiangli/keyed-serial'",
    "import { acquire } from 'wanxiangli/file-lock'",
    "import { primaryDirectory, primaryDirectorySync, checkStateDirectory, initializeStateDirectory } from 'wanxiangli/workspace-state'",
    "import { GitWorktrees, GitPublication } from 'wanxiangli/git'",
    "import { ResultMailbox } from 'wanxiangli/result-mailbox'",
    "import { TaskScope } from 'wanxiangli/task-scope'",
    "import { FileBaseline, FileDiff } from 'wanxiangli/file-baseline'",
    "import { BookkeeperStaging, BookkeeperLedger, BookkeeperTransaction, CasebookState, CasebookIndex, SessionDraftStore, CasebookService, CasebookSettlementOwner } from 'wanxiangli/casebook'",
    "import { SegmentedWriter, SegmentedReader } from 'wanxiangli/segments'",
    "import { CanonicalEventCodec } from 'wanxiangli/events'",
    "import { CompletionRecord } from 'wanxiangli/handle-lifecycle'",
    "import { AttachmentLeaseRegistry, AttachmentSessions } from 'wanxiangli/attachment-lease'",
    "import { ProgramInvocation, ProgramSession } from 'wanxiangli/program'",
    "import { DispatchAttempt, PromptPhysicalAcceptance } from 'wanxiangli/dispatch'",
    "import { ExecutionRouting } from 'wanxiangli/routing'",
    "import { TraceHistory, TraceCapture } from 'wanxiangli/trace'",
    "import { PairGuidanceHistory, PairGuidanceOccurrences } from 'wanxiangli/pair-hint'",
    "import { ProviderRecovery } from 'wanxiangli/failure'",
    "import { DurableProviderBudget, ProviderFailureBudget } from 'wanxiangli/failure-budget'",
    "import { DelegateInvocationRuntime } from 'wanxiangli/delegate-batch'",
    "import { runProcess } from 'wanxiangli/runtime'",
    "import { createHash } from 'node:crypto'",
    "import { execFile } from 'node:child_process'",
    "import assert from 'node:assert/strict'",
    "import { createRequire } from 'node:module'",
    "import fs from 'node:fs'",
    "import os from 'node:os'",
    "import path from 'node:path'",
    'const fileDiff = new FileDiff()',
    'const packedProcess = await runProcess({ command: { fileName: process.execPath, args: ["-e", "process.stdout.write(process.argv[1])", "packed process"] }, estimate: { runtimeSeconds: 5, outputBytes: 65536n, memory: "medium" }, context: { hardLimitMs: 5000 } })',
    'assert.equal(packedProcess.ok, true)',
    'assert.equal(packedProcess.value.kind, "Completed")',
    'assert.equal(packedProcess.value.stdout, "packed process")',
    'const packedLeases = new AttachmentLeaseRegistry()',
    'const packedAttachments = new AttachmentSessions(packedLeases)',
    'const attachmentOrder = []',
    'const attachmentSpec = { owner: "packed-owner", kind: "packed-role", agent: "original-agent", directory: null }',
    'const attachmentOps = {',
    '  isUsable: sessionId => sessionId === "packed-child",',
    '  observe: async () => { attachmentOrder.push("observe"); return { ok: true, value: null } },',
    '  create: async () => { attachmentOrder.push("create"); return { ok: true, value: "packed-child" } },',
    '  link: async ({ sessionId }) => { assert.equal(sessionId, "packed-child"); attachmentOrder.push("link"); return { ok: true, value: undefined } },',
    '}',
    'const packedAttachment = await packedAttachments.getOrCreate(attachmentSpec, attachmentOps)',
    'assert.deepEqual(packedAttachment, { ok: true, value: { sessionId: "packed-child", origin: "Created", agent: "original-agent" } })',
    'const reusedAttachment = await packedAttachments.getOrCreate({ ...attachmentSpec, agent: "later-agent" }, attachmentOps)',
    'assert.equal(reusedAttachment.ok, true)',
    'assert.equal(reusedAttachment.value.sessionId, "packed-child")',
    'assert.equal(reusedAttachment.value.agent, "original-agent")',
    'assert.deepEqual(attachmentOrder, ["observe", "create", "link"])',
    'const retiredAttachment = await packedAttachments.retireAssociated(attachmentSpec.owner, attachmentSpec.kind, {',
    '  abort: async ({ sessionId }) => { assert.equal(sessionId, "packed-child"); attachmentOrder.push("abort"); return { ok: true, value: undefined } },',
    '  close: async ({ owner }) => { assert.equal(owner, "packed-owner"); attachmentOrder.push("close"); return { ok: true, value: undefined } },',
    '})',
    'assert.equal(retiredAttachment.ok, true)',
    'assert.deepEqual(attachmentOrder, ["observe", "create", "link", "abort", "close"])',
    'assert.equal(packedLeases.tryFind(attachmentSpec.owner, attachmentSpec.kind), undefined)',
    'const packedAcceptance = new PromptPhysicalAcceptance()',
    'const packedDispatch = new DispatchAttempt(packedAcceptance)',
    'const dispatchOrder = []',
    'const dispatchLeases = []',
    'try {',
    '  const dispatchResult = await packedDispatch.sendInitial({ key: "packed-prompt", mode: "await", observer: {',
    '    notify: physical => { assert.equal(physical, "packed-physical"); dispatchOrder.push("notify") },',
    '    own: lease => dispatchLeases.push(lease), budget: 1000,',
    '  } }, {',
    '    subscribe: () => { dispatchOrder.push("subscribe"); return { dispose: () => dispatchOrder.push("terminal-dispose") } },',
    '    send: async late => { assert.equal(late, null); dispatchOrder.push("send"); return { kind: "AdmittedWithReceipt", receipt: "packed-receipt" } },',
    '    submitted: async receipt => { assert.equal(receipt, "packed-receipt"); dispatchOrder.push("submitted"); packedAcceptance.accepted("packed-prompt", "packed-physical"); return { ok: true, value: undefined } },',
    '    acceptPhysical: async () => { dispatchOrder.push("acceptPhysical"); return { ok: true, value: undefined } },',
    '    abandon: async () => { assert.fail("packed dispatch unexpectedly abandoned its claim") },',
    '    diagnostic: () => assert.fail("packed dispatch unexpectedly reported a failure"),',
    '  })',
    '  assert.deepEqual(dispatchResult, { kind: "Sent", key: "packed-prompt" })',
    '  assert.deepEqual(dispatchOrder, ["subscribe", "send", "submitted", "notify", "terminal-dispose"])',
    '} finally {',
    '  for (const lease of dispatchLeases) lease.dispose()',
    '  packedAcceptance.clear()',
    '}',
    'const guidance = new PairGuidanceHistory().append({ ordinal: 1n, callId: "packed-guidance", markerText: "original\\0", placementKey: "s|a:x", metadata: "x" })',
    'assert.equal(guidance.ok, true)',
    'assert.equal(guidance.value.reanchor().visible().length, 0)',
    'assert.equal(guidance.value.history()[0].markerText, "original\\0")',
    'let appendedGuidance',
    'const occurrence = await new PairGuidanceOccurrences().occur({ history: [], visible: [], occupied: [], syntheticCallIds: [], placement: { ok: true, value: { callGapKey: "Start", resultGapKey: "After:x", metadata: "x" } }, skipNew: false, sessionKey: "packed", markerText: "original\\0" }, rows => ({ ok: true, value: rows }), async row => { appendedGuidance = row; return { ok: true, value: null } })',
    'assert.equal(occurrence.ok, true)',
    'assert.equal(occurrence.value.wire[0], appendedGuidance)',
    'const budgetLaw = new ProviderFailureBudget()',
    'let recoveryBudget = budgetLaw.forAuthority("logical", "root")',
    'const durableBudget = new DurableProviderBudget({ current: () => recoveryBudget, append: async event => { recoveryBudget = budgetLaw.applyFailure({ sessionId: event.sessionId, logicalRun: event.logicalRun, authorityRoot: event.authorityRoot, providerRun: event.providerRun }, event.count, recoveryBudget).state; return { ok: true } } })',
    'const recovery = new ProviderRecovery()',
    'let recoverySends = 0',
    'const recoveryResult = await recovery.attempt({ resolution: "RetryFreshAttempt", authorization: { logicalRun: "logical", providerRun: "run" } }, { admit: auth => durableBudget.recordAuthorizedFailure("packed", auth, "failure"), redispatch: auth => recovery.redispatch("root", auth, { alreadyAdmitted: () => false, observeAcceptance: () => {}, waitForStop: async () => "Stopped", currentAuthority: () => ({ root: "root", value: null }), sendRetry: async () => { recoverySends++; return { kind: "Sent" } } }, "failure") }, "failure")',
    'assert.equal(recoveryResult.kind, "Dispatched")',
    'assert.equal(recoveryBudget.consecutiveFailureCount, 1)',
    'assert.equal(recoverySends, 1)',
    'const delegateRuntime = new DelegateInvocationRuntime()',
    'let markDelegateSent; let delegateDisposals = 0',
    'const delegateSent = new Promise(resolve => { markDelegateSent = resolve })',
    'const delegateEffects = { attach: async () => ({ ok: true, session: "packed-child", agent: "engineer" }), cleanupDraft() {}, replaceEstimate: async () => {}, fallbackPrompt: text => text, combinePrompts: parts => parts.join("\\n"), resolveAgent: () => null, subscribe: () => () => { delegateDisposals++ }, send: async call => { call.invocations[0].cursor = 0n; call.accept("input", () => { call.acceptedPhysical = "input"; call.acceptedRoot = "root"; call.admit("root") }); call.invocations[0].request.observeAdmission({ kind: "accepted", physical: "input" }); markDelegateSent(); return { ok: true } }, notePrompt() {}, awaitView: (_, answer) => answer, awaitInvocation: (_, answer) => answer }',
    'const delegated = delegateRuntime.invokeObserved({ owner: "packed-owner", scope: "packed-owner", role: "Engineer", charge: "work", estimate: null, batch: null, prepare: async () => "work", observed: true, isCancelled: () => false, noteDraft: true }, delegateEffects, reason => ({ kind: "refused", reason }))',
    'await delegateSent',
    'assert.equal((await delegated.admission).physical, "input")',
    'assert.equal(await delegateRuntime.handleTurn({ session: "packed-child", root: "root", physical: "input", outcome: "completed", hasFailure: false, hasParts: true, naturalMaterial: "material", response: { text: "answer" } }, { readDelivery: () => null, noteAnswer() {}, captureTerminal: async () => ({ kind: "captured" }), retry: async () => ({ ok: false, error: "exhausted" }) }), true)',
    'assert.deepEqual(await delegated.completion, { ok: true, value: { text: "answer" } })',
    'assert.equal(delegateDisposals, 1)',
    'delegateRuntime.clear()',
    'const packedCapture = new TraceCapture(); let captureHistory = new TraceHistory("packed-capture"); const captureIdentities = []; const captureWrites = []',
    'const captureStore = { read: () => ({ history: captureHistory, identities: captureIdentities }), writeBody: async body => { captureWrites.push(body); return { ok: true, value: { reference: "body", digest: "digest" } } }, append: async fact => { const next = captureHistory.appendPart({ ...fact.part, sequence: fact.sequence, bodyRef: fact.bodyRef, bodyDigest: fact.bodyDigest, descriptor: fact.part.key }); assert.equal(next.ok, true); captureHistory = next.value; captureIdentities.push({ key: fact.part.key, secondaryKey: null, compatibility: "same" }) } }',
    'const capturePart = { key: "one", secondaryKey: null, compatibility: "same", exactKey: true, legacyKey: null, role: "user", kind: "text", toolName: null, body: "original\\0", metadata: null }',
    'await Promise.all([packedCapture.run("session", () => packedCapture.captureParts(captureStore, [capturePart], "stable")), packedCapture.run("session", () => packedCapture.captureParts(captureStore, [capturePart], "stable"))])',
    'assert.deepEqual(captureWrites, ["original\\0"])',
    'assert.equal(captureHistory.headSequence(), 1n)',
    'const packedRouting = new ExecutionRouting({ scheduler: null })',
    'const packedAdmission = await packedRouting.acquire({ sessionId: "packed-session", physicalUserMessageId: "packed-input", role: "worker", participant: "worker", purpose: "normal", hostTarget: { model: "provider/model", reasoning: "normal" } })',
    'assert.equal(packedAdmission.kind, "Admitted")',
    'assert.equal(packedRouting.commit(packedAdmission.lease, packedAdmission.lease.identity), "Applied")',
    'await packedRouting.enterStep({ sessionId: "packed-session", physicalUserMessageId: "packed-input", visibleProviderRuns: [], requestKey: "packed-request" })',
    'assert.equal(packedRouting.snapshot().inFlightCount, 1)',
    'packedRouting.endStep("packed-session", "packed-input", "packed-run")',
    'assert.equal(packedRouting.snapshot().idleCount, 1)',
    'assert.deepEqual(packedRouting.releasePhysical("packed-session", "packed-input"), { kind: "Released", outcome: "Applied" })',
    'assert.deepEqual(packedRouting.occupied(), [])',
    'const historyMetadata = { provenance: "packed origin" }',
    'const historyPart = { sequence: 1n, role: "user", kind: "text", toolName: null, bodyRef: "packed-body", bodyDigest: "packed-digest", descriptor: "user|packed origin|text||packed-body|packed-digest", metadata: historyMetadata }',
    'const firstHistory = new TraceHistory("packed-history").appendPart(historyPart)',
    'assert.equal(firstHistory.ok, true)',
    'const openedHistory = firstHistory.value.appendOpening({ assignment: "packed task", requirements: [] })',
    'assert.equal(openedHistory.ok, true)',
    'const fixedHistory = openedHistory.value',
    'const fixedRange = fixedHistory.rangeFrom(0n)',
    'const laterHistory = fixedHistory.appendPart({ ...historyPart, sequence: 2n, bodyRef: "later-body", descriptor: "user|later origin|text||later-body|packed-digest" })',
    'assert.equal(laterHistory.ok, true)',
    'const readHistoryBody = async ref => { assert.equal(ref, "packed-body"); return { ok: true, value: "原文\\r\\n\\uFEFF\\u0000" } }',
    'const fixedMaterial = await fixedHistory.materialize(fixedRange, readHistoryBody)',
    'assert.equal(fixedMaterial.ok, true)',
    'assert.deepEqual(fixedMaterial.value, [{ sequence: 1n, role: "user", part: { kind: "text", text: "原文\\r\\n\\uFEFF\\u0000" }, metadata: historyMetadata }])',
    'assert.equal(fixedMaterial.value[0].metadata, historyMetadata)',
    'assert.deepEqual(await laterHistory.value.materialize(fixedRange, readHistoryBody), fixedMaterial)',
    'assert.equal(fixedHistory.entries().length, 1)',
    'assert.equal(laterHistory.value.entries().length, 2)',
    'const { LocalEventStore, LocalIntegrator } = await import("wanxiangli/local-eventstore")',
    'const localStoreRoot = fs.mkdtempSync(path.join(os.tmpdir(), "wx-packed-local-store-"))',
    'try {',
    '  const directory = () => path.join(localStoreRoot, "events")',
    '  const localOptions = { directory, lockTarget: () => path.join(localStoreRoot, "owner"), writerId: "packed-a", segmentCap: 67108864, integrator: new LocalIntegrator({ rules: [], isKnownEventType: type => type === "PackedEvent" }) }',
    '  const packedStore = new LocalEventStore(localOptions)',
    '  const stagedBody = await packedStore.writePayload(Buffer.from("packed original"))',
    '  assert.equal(stagedBody.ok, true)',
    '  assert.deepEqual(await packedStore.append([{ id: "packed-local-event", stream: "packed", type: "PackedEvent", parents: [], payload: {}, payloadRefs: [stagedBody.value] }]), { ok: true, value: { cuts: [] } })',
    '  const reopenedStore = new LocalEventStore({ ...localOptions, writerId: "packed-b", integrator: new LocalIntegrator({ rules: [], isKnownEventType: type => type === "PackedEvent" }) })',
    '  assert.deepEqual(reopenedStore.heads("packed"), ["packed-local-event"])',
    '  const reopenedBody = await reopenedStore.readPayload(stagedBody.value)',
    '  assert.equal(reopenedBody.ok, true)',
    '  assert.equal(Buffer.from(reopenedBody.value).toString("utf8"), "packed original")',
    '} finally { fs.rmSync(localStoreRoot, { recursive: true, force: true }) }',
    'const { JournalWriter, JournalSession } = await import("wanxiangli/journal-session")',
    'const journalRoot = fs.mkdtempSync(path.join(os.tmpdir(), "wx-packed-journal-session-"))',
    'try {',
    '  const journalStore = new LocalEventStore({ directory: () => path.join(journalRoot, "events"), lockTarget: () => path.join(journalRoot, "owner"), writerId: "packed-journal", segmentCap: 67108864, integrator: new LocalIntegrator({ rules: [], isKnownEventType: type => type === "PackedJournal" }) })',
    '  let journalEventId = 0',
    '  const journalWriter = new JournalWriter({ initialization: { eventId: "packed-init", envelope: { eventId: "packed-init", sequence: 1n, request: "boot" } }, newEventId: () => "packed-journal-" + (++journalEventId), now: () => Date.now(), makeEnvelope: input => input, commitEnvelope: async envelope => {',
    '    const appended = await journalStore.append([{ id: envelope.eventId, stream: "journal", type: "PackedJournal", parents: journalStore.heads("journal"), payload: { sequence: String(envelope.sequence), request: envelope.request }, payloadRefs: [] }])',
    '    return appended.ok ? appended : { ok: false, error: { ...appended.error, original: appended.error } }',
    '  } })',
    '  const journalSession = new JournalSession({ writer: journalWriter, initialRevision: 0n, readCurrent: () => journalStore.head("journal"), refreshCurrent: () => journalStore.reload() })',
    '  assert.equal(fs.existsSync(path.join(journalRoot, "events")), false)',
    '  const journalChange = journalSession.awaitChange(0n)',
    '  const journalAppend = await journalSession.append("packed-business")',
    '  assert.equal(journalAppend.kind, "Committed")',
    '  assert.equal(journalAppend.sequence, 2n)',
    '  assert.equal(journalAppend.current, "packed-journal-1")',
    '  assert.equal((await journalChange).revision, 2n)',
    '  assert.deepEqual(journalSession.snapshotWithRevision(), { current: "packed-journal-1", revision: 2n })',
    '  const journalClose = journalSession.releaseAsync()',
    '  assert.equal(journalSession.releaseAsync(), journalClose)',
    '  await journalClose',
    '} finally { fs.rmSync(journalRoot, { recursive: true, force: true }) }',
    'const invocationRoot = fs.mkdtempSync(path.join(os.tmpdir(), "wx-packed-invocation-"))',
    'const packedProgramSession = ProgramSession("packed-invocation-" + process.pid)',
    'try {',
    '  const invocationOrder = []',
    '  const invocationToken = { source: "packed-invocation" }',
    '  let invocationObservation',
    '  const invocation = new ProgramInvocation({ root: invocationRoot, ledger: {',
    '    prepared: ({ root, mutation }) => { assert.equal(root, invocationRoot); assert.equal(mutation.kind, "create"); invocationOrder.push("prepared"); return invocationToken },',
    '    committed: token => { assert.equal(token, invocationToken); invocationOrder.push("committed") },',
    '    settle: async () => { invocationOrder.push("settle"); return null },',
    '  }, observe: observation => { invocationObservation = observation; invocationOrder.push("observe") },',
    '    checkReturn: ({ value }) => { assert.deepEqual(value, { written: true }); invocationOrder.push("check"); return null },',
    '  })',
    '  const invocationResult = await invocation.execute(packedProgramSession, {',
    '    name: "write-packed-file", intent: "Write the package fixture and wait for its file records.",',
    '    program: `class Js extends JsProgram { async run() { this.write("effect.txt", "packed invocation"); return { written: true }; } }`,',
    '    baseClass: "class JsProgram { constructor(api) { Object.assign(this, api); } }",',
    '    root: invocationRoot, capabilities: ["Write"], executionBudgetMs: 2000, outputBoundBytes: 4096,',
    '  }, invocation.files)',
    '  assert.equal(invocationResult.receipt.state, "completed")',
    '  assert.equal(invocationResult.settlementFailure, null)',
    '  assert.deepEqual(invocationResult.receipt.effects, { rewritten: [], created: ["effect.txt"], removed: [], moved: [] })',
    '  assert.deepEqual(invocationObservation, { reads: [], effectPaths: ["effect.txt"] })',
    '  assert.deepEqual(invocationOrder, ["prepared", "committed", "observe", "settle", "check"])',
    '  assert.equal(fs.readFileSync(path.join(invocationRoot, "effect.txt"), "utf8"), "packed invocation")',
    '} finally {',
    '  try { await packedProgramSession.close() }',
    '  finally { fs.rmSync(invocationRoot, { recursive: true, force: true }) }',
    '}',
    'const emptyCases = new CasebookState()',
    'const capturedCases = emptyCases.apply({ kind: "captured", case: { identity: "packed-case", sourceTrace: "packed-trace", q: "packed question", a: "packed answer", relatedPaths: [], completionFileState: "baseline", maintenanceFileState: "baseline", accessOrder: 99n, observations: [] } })',
    'assert.deepEqual(emptyCases.visible(0).cases, [])',
    'assert.equal(capturedCases.visible(0).cases[0].a, "packed answer")',
    'assert.equal(capturedCases.visible(0).cases[0].accessOrder, 0n)',
    'const editStaging = new BookkeeperStaging()',
    'editStaging.beginTransaction("packed-edit", "original question", "original answer")',
    'const editResult = editStaging.edit("packed-edit", kind => kind)',
    'assert.equal(editResult.tag, 0)',
    'const packedEdit = editResult.fields[0]',
    'assert.equal(packedEdit.api.bookkeeper.question(), "original question")',
    'assert.equal(packedEdit.api.bookkeeper.setAnswer("packed answer").ok, true)',
    'assert.equal(packedEdit.commit().tag, 0)',
    'assert.deepEqual(editStaging.take("packed-edit").fields[0], ["original question", "packed answer"])',
    'const packedLedger = new BookkeeperLedger()',
    'const packedTransaction = new BookkeeperTransaction(editStaging, packedLedger)',
    'let completePackedTransaction',
    'const transactionResult = await packedTransaction.run({ txId: "packed-transaction", ownerId: "packed-owner", question: "question", answer: "answer" }, {',
    '  open: async () => ({ ok: true, value: "packed-bookkeeper" }),',
    '  subscribe: (_child, complete) => { completePackedTransaction = complete; return () => {} },',
    '  prepare: () => ({ ok: true, value: { agent: "bookkeeper", text: "packed prompt" } }),',
    '  send: async (child, agent, text) => { assert.equal(packedLedger.tryConsume(child, agent, text), true); assert.equal(editStaging.apply("packed-transaction", "revised", "answer").tag, 0); completePackedTransaction({ ok: true }); return { ok: true } },',
    '  abort: async child => { assert.equal(packedLedger.isAttached(child), true) },',
    '})',
    'assert.deepEqual(transactionResult, { ok: true, value: { question: "revised", answer: "answer" } })',
    'assert.equal(packedLedger.isAttached("packed-bookkeeper"), false)',
    'let serviceWorld = new CasebookState()',
    'const serviceDrafts = new SessionDraftStore()',
    'const serviceIndex = new CasebookIndex()',
    'const packedService = new CasebookService(serviceDrafts, serviceIndex, {',
    '  current: () => ({ ok: true, value: serviceWorld }),',
    '  appendCaptured: async value => { serviceWorld = serviceWorld.apply({ kind: "captured", case: value }); return { ok: true } },',
    '  appendRefreshed: async value => { serviceWorld = serviceWorld.apply({ kind: "refreshed", ...value }); return { ok: true } },',
    '  appendAccessed: async identity => { serviceWorld = serviceWorld.apply({ kind: "accessed", identity }); return { ok: true } },',
    '}, { capture: async () => ({ ok: true, value: "baseline" }), compare: async () => ({ ok: true, value: { refresh: false, targetState: "baseline", diffSummary: "" } }) },',
    '  async request => ({ ok: true, value: { question: request.question, answer: request.answer } }), () => [], () => true)',
    'serviceDrafts.setQ("packed-service", "service question")',
    'serviceDrafts.setA("packed-service", "service answer")',
    'assert.deepEqual(await packedService.finalizeDraft("packed-service"), { kind: "finalized", identity: "packed-service" })',
    'assert.deepEqual(await packedService.fetch(serviceIndex.shelfmarkFor("packed-service", "service question"), () => assert.fail("unexpected package append failure")), { kind: "fresh", answer: "service answer" })',
    'const completionRecord = new CompletionRecord()',
    'const decodedCompletion = completionRecord.decode(JSON.stringify({ schemaVersion: 4, finality: "failed", run_id: "packed-run", code: "FAILED", message: "packed failure", child_session_id: "packed-child" }))',
    'assert.equal(decodedCompletion.kind, "current")',
    'assert.equal(decodedCompletion.payload.childSessionId, "packed-child")',
    "assert.equal(fileDiff.fileDiff('unchanged', 'same', 'same'), '')",
    "assert.equal(fileDiff.truncate('short', 20).text, 'short')",
    'const reportedConflicts = []',
    'const settlementOwner = new CasebookSettlementOwner(incident => reportedConflicts.push(incident))',
    "const conflict = { message: 'package conflict' }",
    "assert.equal(settlementOwner.Observe('event', 'SemanticCut', conflict), 'CaseUnavailable')",
    "assert.equal(settlementOwner.Redeliver('event', conflict), 'CaseUnavailableKnown')",
    'assert.deepEqual(reportedConflicts, [conflict])',
    "const writerRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'wx-packed-segmented-writer-'))",
    'try {',
    '  let directoryCalls = 0',
    "  const writerDirectory = path.join(writerRoot, 'caller-events')",
    "  const writer = new SegmentedWriter({ directory: () => { directoryCalls++; return writerDirectory }, writerId: 'package-writer', segmentCap: 4 })",
    '  assert.equal(directoryCalls, 0)',
    "  assert.deepEqual(writer.append(''), { ok: true, completion: 'no-append' })",
    '  assert.equal(directoryCalls, 0)',
    "  assert.deepEqual(writer.append('α\\n'), { ok: true, completion: 'durable' })",
    "  const firstSegment = path.join(writerDirectory, 'package-writer-0000.ndjson')",
    '  assert.equal(writer.filePath(), firstSegment)',
    "  assert.deepEqual(fs.readFileSync(firstSegment), Buffer.from('α\\n'))",
    "  assert.deepEqual(writer.append('z\\n'), { ok: true, completion: 'durable' })",
    "  assert.equal(writer.filePath(), path.join(writerDirectory, 'package-writer-0001.ndjson'))",
    "  assert.deepEqual(fs.readFileSync(writer.filePath()), Buffer.from('z\\n'))",
    "  assert.deepEqual(fs.readFileSync(firstSegment), Buffer.from('α\\n'))",
    '} finally { fs.rmSync(writerRoot, { recursive: true, force: true }) }',
    "const readerRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'wx-packed-segmented-reader-'))",
    'try {',
    "  const directory = () => path.join(readerRoot, 'events')",
    "  const writer = new SegmentedWriter({ directory, writerId: 'read-package', segmentCap: 1024 })",
    "  const event = { id: 'a'.repeat(40), stream: 'package/read', type: 'JobRequested', parents: [], payload: { text: '原文' }, payloadRefs: [] }",
    "  assert.equal(writer.append(new CanonicalEventCodec().encode(event)).ok, true)",
    '  const reader = new SegmentedReader()',
    '  const loaded = reader.readStreams(directory, Date.now())',
    '  assert.equal(loaded.ok, true)',
    "  assert.deepEqual(loaded.streams.map(stream => [stream.writerId, stream.events.map(value => value.id)]), [['read-package', [event.id]]])",
    '  assert.deepEqual(loaded.streams[0].events[0].payload, event.payload)',
    "  assert.match(reader.physicalInventory(directory), /read-package-0000\\.ndjson=/)",
    '} finally { fs.rmSync(readerRoot, { recursive: true, force: true }) }',
    "const baselineRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'wx-packed-file-baseline-'))",
    'try {',
    '  const baselinePayloads = new Map()',
    '  const baselineService = new FileBaseline({',
    '    resolvePath: relativeOrAbsolute => path.resolve(baselineRoot, relativeOrAbsolute),',
    '    payloads: {',
    '      async read(reference) { return { ok: true, value: baselinePayloads.has(reference) ? Uint8Array.from(baselinePayloads.get(reference)) : null } },',
    '      async write(bytes) {',
    "        const reference = 'sha256:' + createHash('sha256').update(bytes).digest('hex')",
    '        baselinePayloads.set(reference, Uint8Array.from(bytes))',
    '        return { ok: true, value: reference }',
    '      },',
    '    },',
    '  })',
    "  const baselineFile = path.join(baselineRoot, 'subject.txt')",
    "  const baselineOriginal = Buffer.from('\\uFEFFbefore\\r\\n')",
    '  fs.writeFileSync(baselineFile, baselineOriginal)',
    "  const baselinePaths = ['subject.txt', 'missing.txt']",
    '  const captured = await baselineService.capture(baselinePaths)',
    '  assert.equal(captured.ok, true, captured.error)',
    '  const initialState = JSON.parse(captured.value)',
    "  assert.deepEqual(initialState['missing.txt'], { kind: 'Missing' })",
    "  assert.deepEqual(Buffer.from(baselinePayloads.get(initialState['subject.txt'].payloadRef)), baselineOriginal)",
    "  const baselineTarget = Buffer.from('after\\n')",
    '  fs.writeFileSync(baselineFile, baselineTarget)',
    '  const compared = await baselineService.compare(baselinePaths, captured.value)',
    '  assert.equal(compared.ok, true, compared.error)',
    '  assert.equal(compared.value.refresh, true)',
    '  assert.ok(compared.value.diffSummary.length > 0)',
    '  const targetState = JSON.parse(compared.value.targetState)',
    "  assert.deepEqual(Buffer.from(baselinePayloads.get(targetState['subject.txt'].payloadRef)), baselineTarget)",
    '} finally { fs.rmSync(baselineRoot, { recursive: true, force: true }) }',
    'const callbackScope = new TaskScope()',
    "const foregroundError = new Error('package foreground failure')",
    'await assert.rejects(callbackScope.run(() => Promise.reject(foregroundError)), cause => cause === foregroundError)',
    'assert.equal((await callbackScope.waitIdle()).ok, true)',
    'let callbackStarted = false',
    'let rejectBackground',
    'const backgroundWork = callbackScope.background(() => {',
    '  callbackStarted = true',
    '  return new Promise((_, reject) => { rejectBackground = reject })',
    '})',
    'assert.equal(callbackStarted, true)',
    'let callbacksClosed = false',
    'const closingCallbacks = callbackScope.close()',
    'closingCallbacks.then(() => { callbacksClosed = true })',
    'await Promise.resolve()',
    'assert.equal(callbacksClosed, false)',
    'let lateCallbackRan = false',
    'await callbackScope.background(async () => { lateCallbackRan = true })',
    'await callbackScope.run(async () => { lateCallbackRan = true })',
    'assert.equal(lateCallbackRan, false)',
    "const backgroundError = new Error('package background failure')",
    'rejectBackground(backgroundError)',
    'await backgroundWork',
    'const closedCallbacks = await closingCallbacks',
    'assert.equal(closedCallbacks.ok, false)',
    'assert.equal(closedCallbacks.error, backgroundError)',
    'const mailbox = new ResultMailbox()',
    'mailbox.start()',
    'let rejectInterrupt',
    'const failedInterrupt = new Promise((_, reject) => { rejectInterrupt = reject })',
    'const firstJoin = mailbox.beginJoin(1, failedInterrupt)',
    "assert.equal(firstJoin.kind, 'pending')",
    "const interruptError = new Error('package interrupt failure')",
    'const rejectedJoin = assert.rejects(firstJoin.completion, cause => cause === interruptError)',
    'rejectInterrupt(interruptError)',
    'await rejectedJoin',
    'assert.equal(mailbox.hasActive, true)',
    'const secondJoin = mailbox.beginJoin(1, new Promise(() => {}))',
    "assert.equal(secondJoin.kind, 'pending')",
    "const mailboxPayload = { value: 'package result' }",
    'mailbox.publish(mailboxPayload)',
    'const mailboxResult = await secondJoin.completion',
    "assert.equal(mailboxResult.kind, 'items')",
    'assert.equal(mailboxResult.items.length, 1)',
    'assert.equal(mailboxResult.items[0], mailboxPayload)',
    'assert.equal(mailbox.pendingCount, 0)',
    'assert.equal(mailbox.hasActive, false)',
    "assert.deepEqual(mailbox.beginJoin(1, new Promise(() => {})), { kind: 'ready', result: { kind: 'empty' } })",
    "const gitRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'wx-packed-worktrees-'))",
    "const repository = path.join(gitRoot, 'repository')",
    "const worktreePath = path.join(gitRoot, 'worktree')",
    'fs.mkdirSync(repository)',
    'const runGit = ({ cwd = repository, args }) => new Promise((resolve, reject) => {',
    "  execFile('git', args, { cwd, encoding: 'utf8', env: { ...process.env, LC_ALL: 'C', GIT_OPTIONAL_LOCKS: '0' } }, (error, stdout, stderr) => {",
    "    if (error && typeof error.code !== 'number') reject(error)",
    '    else resolve({ code: error?.code ?? 0, stdout, stderr })',
    '  })',
    '})',
    'try {',
    '  for (const args of [',
    "    ['init', '--quiet', '--initial-branch=main'],",
    "    ['config', 'user.name', 'Package fixture'],",
    "    ['config', 'user.email', 'package@example.invalid'],",
    '  ]) { const result = await runGit({ args }); assert.equal(result.code, 0, result.stderr) }',
    "  const draftPath = path.join(repository, 'draft.txt')",
    "  fs.writeFileSync(draftPath, 'base\\n')",
    "  for (const args of [['add', 'draft.txt'], ['-c', 'commit.gpgsign=false', 'commit', '--quiet', '-m', 'base']]) {",
    '    const result = await runGit({ args }); assert.equal(result.code, 0, result.stderr)',
    '  }',
    "  fs.writeFileSync(draftPath, 'uncommitted draft\\n')",
    '  const worktrees = new GitWorktrees(repository, runGit)',
    "  const created = await worktrees.create('package-worktree', worktreePath, 'HEAD')",
    '  assert.equal(created.ok, true, created.error)',
    '  const listed = await worktrees.list()',
    '  assert.equal(listed.ok, true, listed.error)',
    "  assert.ok(listed.value.some(row => row.identity === 'refs/heads/package-worktree'))",
    '  const resource = created.value',
    "  assert.equal(resource.identity, 'package-worktree')",
    '  assert.equal(fs.realpathSync(resource.path), fs.realpathSync(worktreePath))',
    '  await resource.markDurable()',
    '  await resource.dispose()',
    '  assert.ok(fs.existsSync(worktreePath))',
    '  const released = await resource.release()',
    '  assert.equal(released.ok, true, released.error)',
    '  assert.equal(fs.existsSync(worktreePath), false)',
    "  const branches = await worktrees.listBranches('package-worktree')",
    '  assert.equal(branches.ok, true, branches.error)',
    '  assert.deepEqual(branches.value, [])',
    "  const publicationTargetPath = path.join(gitRoot, 'publication-target')",
    "  const publicationCandidatePath = path.join(gitRoot, 'publication-candidate')",
    "  const publicationTarget = await worktrees.create('package-target', publicationTargetPath, 'HEAD')",
    '  assert.equal(publicationTarget.ok, true, publicationTarget.error)',
    "  const publicationCandidate = await worktrees.create('package-candidate', publicationCandidatePath, 'HEAD')",
    '  assert.equal(publicationCandidate.ok, true, publicationCandidate.error)',
    '  const publication = new GitPublication(publicationTargetPath, runGit)',
    "  const expectedHead = await publication.targetHead('package-target')",
    '  assert.equal(expectedHead.ok, true, expectedHead.error)',
    "  fs.writeFileSync(path.join(publicationCandidatePath, 'candidate.txt'), 'package publication\\n')",
    "  const prepared = await publication.prepare(publicationCandidatePath, 'package candidate')",
    '  assert.equal(prepared.ok, true, prepared.error)',
    '  const candidateHead = await publication.readHead(publicationCandidatePath)',
    '  assert.equal(candidateHead.ok, true, candidateHead.error)',
    "  const published = await publication.fastForward({ worktree: publicationCandidatePath, target: 'package-target', expectedHead: expectedHead.value, candidate: candidateHead.value })",
    '  assert.equal(published.ok, true, published.error)',
    '  assert.equal(published.value, candidateHead.value)',
    "  assert.equal(fs.readFileSync(path.join(publicationTargetPath, 'candidate.txt'), 'utf8'), 'package publication\\n')",
    '  for (const item of [publicationCandidate.value, publicationTarget.value]) {',
    '    const cleanup = await item.release(); assert.equal(cleanup.ok, true, cleanup.error)',
    '  }',
    "  assert.equal(fs.readFileSync(draftPath, 'utf8'), 'uncommitted draft\\n')",
    '} finally { fs.rmSync(gitRoot, { recursive: true, force: true }) }',
    "const workspaceRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'wx-packed-workspace-'))",
    'try {',
    '  const primary = fs.realpathSync(workspaceRoot)',
    '  assert.equal(await primaryDirectory(workspaceRoot), primary)',
    '  assert.equal(primaryDirectorySync(workspaceRoot), primary)',
    "  const stateRoot = path.join(primary, 'host-chosen-state')",
    '  assert.equal(checkStateDirectory(stateRoot), stateRoot)',
    '  assert.equal(fs.existsSync(stateRoot), false)',
    '  assert.equal(initializeStateDirectory(stateRoot), stateRoot)',
    '  assert.ok(fs.statSync(stateRoot).isDirectory())',
    "  assert.equal(fs.readFileSync(path.join(stateRoot, '.gitignore'), 'utf8'), '*\\n')",
    '} finally { fs.rmSync(workspaceRoot, { recursive: true, force: true }) }',
    "export const lockDependency = createRequire(import.meta.resolve('wanxiangli/file-lock')).resolve('proper-lockfile')",
    "const lockRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'wx-packed-file-lock-'))",
    "const lockPath = path.join(lockRoot, 'probe')",
    'export let fileLockHeld = false',
    'export let fileLockReleased = false',
    'try {',
    "  const lease = await acquire(lockPath, { kind: 'Retry', retries: 0, minTimeoutMs: 1, maxTimeoutMs: 1, factor: 1 })",
    "  fileLockHeld = fs.existsSync(lockPath + '.lock')",
    '  await lease.release()',
    "  fileLockReleased = !fs.existsSync(lockPath + '.lock')",
    '} finally { fs.rmSync(lockRoot, { recursive: true, force: true }) }',
    'const serial = new KeyedSerial()',
    'const order = []',
    "const first = serial.run('key', async () => { order.push('first'); throw new Error('expected') })",
    "const second = serial.run('key', async () => { order.push('second'); return 2 })",
    'await first.catch(() => {})',
    'export const serialResult = await second',
    'export const serialOrder = order',
    "export const material = encode(['report'], ['raw material'])",
    'export const materialError = validationError(material)',
    'export const values = await mapBounded(1, async value => value + 1, [1, 2])',
    "export const validTerminals = [null, 'before <tool_call> after', 'plain answer'].map(isValid)",
  ].join('\n'))

  // 3. Create consumer/ directory outside with separate package.json
  const consumerDir = path.join(scratchDir, 'consumer')
  fs.mkdirSync(consumerDir, { recursive: true })

  fs.writeFileSync(
    path.join(consumerDir, 'package.json'),
    JSON.stringify(
      {
        name: 'external-test-consumer',
        version: '1.0.0',
        type: 'module',
      },
      null,
      2,
    ),
    'utf8',
  )

  // Link isolatedRoot/node_modules into consumerDir
  const consumerNodeModules = path.join(consumerDir, 'node_modules')
  if (!fs.existsSync(consumerNodeModules)) {
    fs.symlinkSync(path.join(isolatedRoot, 'node_modules'), consumerNodeModules, 'junction')
  }

  const consumerScript = `
    import pkg from 'wanxiangshu';
    import assert from 'node:assert/strict';

    if (!pkg || typeof pkg !== 'object') {
      console.error('wanxiangshu default export is not an object:', pkg);
      process.exit(2);
    }

    if (pkg.id !== 'wanxiangshu-next') {
      console.error('wanxiangshu default export id mismatch:', pkg.id);
      process.exit(3);
    }

    if (typeof pkg.server !== 'function') {
      console.error('wanxiangshu default export server is not a function:', typeof pkg.server);
      process.exit(4);
    }

    // Read a runtime resource via package export or package structure
    import fs from 'node:fs';
    import path from 'node:path';
    import { pathToFileURL } from 'node:url';

    // Locate wanxiangshu in node_modules
    const pkgRoot = path.resolve('node_modules/wanxiangshu');
    const lifecycleDependency = path.join(pkgRoot, 'node_modules/wanxiangli/package.json');

    const enLaw = path.join(pkgRoot, 'resources/provider/world/common-law/en.md');
    const zhLaw = path.join(pkgRoot, 'resources/provider/world/common-law/zh-CN.md');
    const roleEn = path.join(pkgRoot, 'resources/provider/role/manager/en.md');

    assert.ok(fs.existsSync(enLaw), 'resources/provider/world/common-law/en.md missing in consumed package');
    assert.ok(fs.existsSync(zhLaw), 'resources/provider/world/common-law/zh-CN.md missing in consumed package');
    assert.ok(fs.existsSync(roleEn), 'resources/provider/role/manager/en.md missing in consumed package');
    assert.ok(fs.existsSync(lifecycleDependency), 'bundled wanxiangli dependency missing in consumed package');
    assert.equal(JSON.parse(fs.readFileSync(lifecycleDependency, 'utf8')).version, ${JSON.stringify(lifecycleVersion)});

    // 从实际主体适配加载公开库入口，不能借消费者顶层的另一份库。
    const text = await import(pathToFileURL(path.join(pkgRoot, 'dist/Foundation/LlmFacing.js')).href);
    const document = text.withData(text.Data_rootStructuredValue(text.Data_ofJs({ source: 'bundled' })), text.instruction('probe'));
    assert.equal(text.render(document), '# probe\\n\\nsource = "bundled"\\n');

    const entryProbe = path.join(pkgRoot, '.verify-library-entries.mjs');
    try {
      const entries = await import(pathToFileURL(entryProbe).href);
      assert.deepEqual(entries.values, [2, 3]);
      assert.deepEqual(entries.validTerminals, [false, false, true]);
      assert.equal(entries.material, '{"body":["report"],"carry":["raw material"]}');
      assert.equal(entries.materialError, null);
      assert.deepEqual(entries.serialOrder, ['first', 'second']);
      assert.equal(entries.serialResult, 2);
      await import(pathToFileURL(path.join(pkgRoot, '.verify-fission-lifecycle.mjs')).href);
      for (const name of ['prefix-history', 'child-run-set', 'durable-handles', 'change']) {
        await import(pathToFileURL(path.join(pkgRoot, '.verify-' + name + '.mjs')).href);
      }
      assert.equal(entries.fileLockHeld, true);
      assert.equal(entries.fileLockReleased, true);
      assert.ok(entries.lockDependency.startsWith(fs.realpathSync(path.dirname(lifecycleDependency)) + path.sep), 'file-lock must resolve its shipped runtime dependency');
      const material = await import(pathToFileURL(path.join(pkgRoot, 'dist/Foundation/ReturnedMaterial.js')).href);
      assert.equal(material.renderBody(entries.material), '# report\\n');
    } finally {
      fs.unlinkSync(entryProbe);
      fs.unlinkSync(path.join(pkgRoot, '.verify-fission-lifecycle.mjs'));
      for (const name of ['prefix-history', 'child-run-set', 'durable-handles', 'change']) {
        fs.unlinkSync(path.join(pkgRoot, '.verify-' + name + '.mjs'));
      }
    }

    assert.ok(fs.readFileSync(enLaw, 'utf8').trim().length > 0, 'en common law is empty');
    assert.ok(fs.readFileSync(zhLaw, 'utf8').trim().length > 0, 'zh common law is empty');
    assert.ok(fs.readFileSync(roleEn, 'utf8').trim().length > 0, 'role en law is empty');

    process.exit(0);
  `

  fs.writeFileSync(path.join(consumerDir, 'consume.mjs'), consumerScript, 'utf8')

  const isolatedEnv = {
    PATH: process.env.PATH,
    NODE_PATH: undefined,
    NODE_OPTIONS: undefined,
    HOME: path.join(scratchDir, 'home'),
    USERPROFILE: path.join(scratchDir, 'home'),
  }
  fs.mkdirSync(isolatedEnv.HOME, { recursive: true })

  const runResult = await spawnProcess(process.execPath, ['consume.mjs'], {
    cwd: consumerDir,
    env: isolatedEnv,
  })

  if (runResult.code !== 0) {
    throw createVerificationError(
      'external-consumer-failed',
      consumerDir,
      `External consumer failed with code ${runResult.code}: ${runResult.stderr || runResult.stdout}`,
    )
  }

  return true
}

export function assertActiveRegistrations({ extractedPackageDir, root }) {
  const roleDir = path.join(extractedPackageDir, 'resources/provider/role')
  if (!fs.existsSync(roleDir)) {
    throw new Error(`extracted package missing resources/provider/role: ${roleDir}`)
  }
  const actualRoles = fs
    .readdirSync(roleDir, { withFileTypes: true })
    .filter((d) => d.isDirectory())
    .map((d) => d.name)
    .sort()

  const expectedActiveRoles = [
    'bookkeeper',
    'devops',
    'engineer',
    'manager',
    'orchestrator',
    'plan',
  ].sort()

  if (JSON.stringify(actualRoles) !== JSON.stringify(expectedActiveRoles)) {
    throw new Error(
      `resources/provider/role in package must strictly equal active roles: expected ${expectedActiveRoles.join(',')}, got ${actualRoles.join(',')}`,
    )
  }
}

/**
 * Verifies package closure and integrity end-to-end.
 *
 * @param {{ root?: string, runDir?: string, skipConsumer?: boolean }} options
 * @returns {Promise<{ ok: boolean, artifactPath: string, tarballDigest: string, memberCount: number, totalBytes: number, issues: Array<{code: string, path: string, message: string}>, generation: number }>}
 */
export async function verifyPackage({
  root = REPO_ROOT,
  runDir = DEFAULT_PACK_RUN_DIR,
  skipConsumer = false,
} = {}) {
  const resolvedRoot = path.resolve(root)
  const resolvedRunDir = path.resolve(runDir)
  fs.mkdirSync(resolvedRunDir, { recursive: true })

  const candidate = checkCandidateDependency(resolvedRoot)

  // Phase 1: assertBuildFresh before pack
  const initialFresh = assertBuildFresh({ root: resolvedRoot })
  const generation = initialFresh.generation

  // Derive expected closure before pack
  const expectedClosure = deriveExpectedClosure({ root: resolvedRoot })

  // file: 链接的依赖会被 npm 放到链接源路径；仅在临时打包目录物化选定闭包。
  const staging = fs.mkdtempSync(path.join(os.tmpdir(), 'wx-package-stage-'))
  let packResult
  try {
    for (const [member, meta] of expectedClosure) {
      const target = path.join(staging, member)
      fs.mkdirSync(path.dirname(target), { recursive: true })
      fs.copyFileSync(meta.fullSourcePath, target)
    }
    packResult = await spawnProcess(
      'npm',
      ['pack', '--json', '--ignore-scripts', '--pack-destination', resolvedRunDir],
      { cwd: staging, env: npmEnvironment() },
    )
  } catch (err) {
    throw createVerificationError(
      'pack-spawn-failed',
      'npm',
      `npm pack failed to spawn: ${err.message}`,
    )
  } finally {
    fs.rmSync(staging, { recursive: true, force: true })
  }

  if (packResult.code !== 0) {
    throw createVerificationError(
      'pack-failed',
      'npm pack',
      `npm pack exited with code ${packResult.code}: ${packResult.stderr || packResult.stdout}`,
    )
  }

  const packEntry = parsePackResult(packResult.stdout, 'wanxiangshu')
  const tgzPath = path.join(resolvedRunDir, packEntry.filename)
  if (!fs.existsSync(tgzPath)) {
    throw createVerificationError(
      'tarball-missing',
      tgzPath,
      `Packed tarball not found at ${tgzPath}`,
    )
  }

  const tgzBuffer = fs.readFileSync(tgzPath)
  const tarballDigest = crypto.createHash('sha256').update(tgzBuffer).digest('hex')

  // Phase 3: Stream validate tarball entries against expected closure
  const validation = await validateArchiveEntries(tgzPath, expectedClosure)
  if (validation.issues.length > 0) {
    const firstIssue = validation.issues[0]
    const err = createVerificationError(
      firstIssue.code,
      firstIssue.path,
      `Archive validation failed: ${firstIssue.message}`,
    )
    err.issues = validation.issues
    throw err
  }

  // Phase 4: Extract to scratch directory for external consumer
  const scratchDir = fs.mkdtempSync(path.join(os.tmpdir(), 'wx-consumer-scratch-'))
  const extractedPackageDir = path.join(scratchDir, 'package')

  try {
    // Restricted extraction of validated tarball
    await tar.x({
      file: tgzPath,
      cwd: scratchDir,
      strict: true,
    })

    // distribution-010: assert active registrations and surface consistency in extracted artifact
    assertActiveRegistrations({ extractedPackageDir, root: resolvedRoot })

    if (!skipConsumer) {
      await runExternalConsumer({
        root: resolvedRoot,
        tarballPath: tgzPath,
        extractedPackageDir,
        scratchDir,
      })
    }
  } finally {
    try {
      fs.rmSync(scratchDir, { recursive: true, force: true })
    } catch {}
  }

  // Phase 5: Recheck build generation and freshness
  const finalFresh = assertBuildFresh({ root: resolvedRoot })
  const finalCandidate = checkCandidateDependency(resolvedRoot)
  if (finalCandidate.sha !== candidate.sha) {
    throw createVerificationError('candidate-changed', resolvedRoot, 'wanxiangli candidate changed during package verification')
  }
  if (finalFresh.generation !== initialFresh.generation) {
    throw createVerificationError(
      'generation-changed',
      resolvedRoot,
      `Build generation changed mid-verification: was ${initialFresh.generation}, now ${finalFresh.generation}`,
    )
  }

  if (
    finalFresh.compilerInputDigest !== initialFresh.compilerInputDigest ||
    finalFresh.artifactInputDigest !== initialFresh.artifactInputDigest
  ) {
    throw createVerificationError(
      'digest-changed',
      resolvedRoot,
      'Production inputs or artifacts digest changed mid-pack verification',
    )
  }

  return {
    ok: true,
    artifactPath: tgzPath,
    tarballDigest,
    memberCount: validation.memberCount,
    totalBytes: validation.totalBytes,
    issues: validation.issues,
    generation,
    libraryCandidate: candidate.sha,
  }
}

// CLI entrypoint
if (
  process.argv[1] &&
  path.resolve(process.argv[1]) === path.resolve(fileURLToPath(import.meta.url))
) {
  try {
    const result = await verifyPackage({ root: REPO_ROOT })
    console.log(`[verify-package] OK: package verified successfully`)
    console.log(`  generation:    ${result.generation}`)
    console.log(`  members:       ${result.memberCount}`)
    console.log(`  totalBytes:    ${result.totalBytes}`)
    console.log(`  tarballDigest: ${result.tarballDigest}`)
    console.log(`  artifactPath:  ${result.artifactPath}`)
    process.exit(0)
  } catch (err) {
    console.error(`[verify-package] FAILED: ${err.reason || err.message}`)
    if (err.code) console.error(`  code: ${err.code}`)
    if (err.path) console.error(`  path: ${err.path}`)
    if (Array.isArray(err.issues)) {
      console.error(`  total issues: ${err.issues.length}`)
      for (const issue of err.issues.slice(0, 10)) {
        console.error(`    [${issue.code}] ${issue.path}: ${issue.message}`)
      }
    }
    if (err.stack && !err.reason) console.error(err.stack)
    process.exit(1)
  }
}
