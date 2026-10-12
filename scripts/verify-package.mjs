// scripts/verify-package.mjs
// distribution-007 real pack+extract+consume proof.
//
// Performs real `npm pack --json --ignore-scripts --pack-destination <runDir>`,
// streams members via `tar`, strictly validates normalized archive paths,
// compares complete closure (manifest outputs dist + git tracked resources + root files)
// and content digests, extracts into an isolated temporary directory,
// and executes an isolated external consumer test without borrowing repo node_modules.

import { execFileSync, spawn } from 'node:child_process'
import crypto from 'node:crypto'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import * as tar from 'tar'

import {
  assertBuildFresh,
  collectArtifactInputs,
  collectOutputs,
  readManifest,
} from './lib/build-state.mjs'

const MODULE_DIR = path.dirname(fileURLToPath(import.meta.url))
export const REPO_ROOT = path.resolve(MODULE_DIR, '..')
export const DEFAULT_PACK_RUN_DIR = path.join(REPO_ROOT, '.fable-build/verify-logs/package')

export const ROOT_FILE_WHITELIST = ['package.json', 'README.md', 'LICENSE']

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

export function isBannedMember(member) {
  const norm = normalizeMemberPath(member)
  if (!norm) return false

  for (const banned of BANNED_PREFIXES) {
    const cleanBanned = banned.endsWith('/') ? banned.slice(0, -1) : banned
    if (norm === cleanBanned || norm.startsWith(`${cleanBanned}/`)) {
      return true
    }
  }

  for (const pat of BANNED_PATTERNS) {
    if (pat.test(norm)) {
      return true
    }
  }

  return false
}

/**
 * Derives expected package member closure.
 * expected = (manifest.outputs || disk dist) U (git tracked resources) U (root whitelist present)
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

  // 3. Root whitelist files (only if they exist on disk)
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

  return expected
}

/**
 * Validates tarball stream entries directly without extracting first.
 * Enforces:
 * - paths must start with 'package/'
 * - no path traversal
 * - no duplicate entries
 * - ordinary files only (reject links, special entries, directories with content)
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

      if (isBannedMember(member)) {
        issues.push({
          code: 'infiltrated-member',
          path: member,
          message: `Archive contains infiltrated/banned member: ${member}`,
        })
        return
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

    // Locate wanxiangshu in node_modules
    const pkgRoot = path.resolve('node_modules/wanxiangshu');

    const enLaw = path.join(pkgRoot, 'resources/provider/world/common-law/en.md');
    const zhLaw = path.join(pkgRoot, 'resources/provider/world/common-law/zh-CN.md');
    const roleEn = path.join(pkgRoot, 'resources/provider/role/manager/en.md');

    assert.ok(fs.existsSync(enLaw), 'resources/provider/world/common-law/en.md missing in consumed package');
    assert.ok(fs.existsSync(zhLaw), 'resources/provider/world/common-law/zh-CN.md missing in consumed package');
    assert.ok(fs.existsSync(roleEn), 'resources/provider/role/manager/en.md missing in consumed package');

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
    'blogger',
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

  // Phase 1: assertBuildFresh before pack
  const initialFresh = assertBuildFresh({ root: resolvedRoot })
  const generation = initialFresh.generation

  // Derive expected closure before pack
  const expectedClosure = deriveExpectedClosure({ root: resolvedRoot })

  // Phase 2: npm pack --json --ignore-scripts --pack-destination <runDir>
  let packResult
  try {
    packResult = await spawnProcess(
      'npm',
      ['pack', '--json', '--ignore-scripts', '--pack-destination', resolvedRunDir],
      { cwd: resolvedRoot },
    )
  } catch (err) {
    throw createVerificationError(
      'pack-spawn-failed',
      'npm',
      `npm pack failed to spawn: ${err.message}`,
    )
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
  if (finalFresh.generation !== initialFresh.generation) {
    throw createVerificationError(
      'generation-changed',
      resolvedRoot,
      `Build generation changed mid-verification: was ${initialFresh.generation}, now ${finalFresh.generation}`,
    )
  }

  if (
    finalFresh.compilerInputDigest !== initialFresh.compilerInputDigest ||
    finalFresh.generatedInputDigest !== initialFresh.generatedInputDigest ||
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
