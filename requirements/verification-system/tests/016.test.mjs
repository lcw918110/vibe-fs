// Verification inputs collection and mid-run perturbation detection tests.

import assert from 'node:assert/strict'
import { execFileSync } from 'node:child_process'
import { createHash } from 'node:crypto'
import { gzipSync } from 'node:zlib'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import test from 'node:test'
import { Header } from 'tar'
import { createNpmInstallFixture } from './support/npm-install-fixture.mjs'
import { registerNpmInstallSetupTests } from './support/npm-install-setup-tests.mjs'
import { registerNodeToolCandidateTests } from './support/node-tool-candidate-tests.mjs'
import { createAbortedNpmAdmissionFixture, registerNpmToolArchiveTests } from './support/npm-tool-archive-tests.mjs'
import { assertParentReplacementInvalidatesVerification } from './support/parent-replacement.mjs'
import { repositoryNpmInstallTest } from './support/repository-npm-install-tests.mjs'
import { registerDotnetSdkTests, repositoryDotnetSdkTest } from './support/dotnet-sdk-tests.mjs'
import { registerDotnetToolRestoreTests } from './support/dotnet-tool-restore-tests.mjs'
import { repositoryDotnetToolsTest } from './support/repository-dotnet-tools-tests.mjs'
import { registerNugetProjectTests } from './support/nuget-project-tests.mjs'
import { repositoryNugetProjectTest } from './support/repository-nuget-project-tests.mjs'
import { registerFableProjectTests } from './support/fable-project-tests.mjs'
import { repositoryFableProjectTest, repositoryReadonlyFableProjectTest } from './support/repository-fable-project-tests.mjs'
import { integrationTest } from './support/tier-gate.mjs'
import { registerArchiveNamespaceTests } from './support/archive-namespace-tests.mjs'
import { registerReadonlyInputTests } from './support/readonly-input-tests.mjs'

process.env.WXS_VERIFICATION_TOOL_DIAGNOSTICS = '1'

registerReadonlyInputTests()
registerArchiveNamespaceTests()
registerNodeToolCandidateTests()
registerNpmToolArchiveTests()
registerNpmInstallSetupTests()
registerDotnetSdkTests()
registerDotnetToolRestoreTests()
registerNugetProjectTests()
registerFableProjectTests()
const hasExplicitReadonlyContext = () => Boolean(
  process.env.WXS_VERIFICATION_DOTNET_ROOT &&
  process.env.WXS_VERIFICATION_NUGET_PROJECT_TREE_ID &&
  process.env.WXS_VERIFICATION_NPM_CLI
)

integrationTest('WHAT[verification-system-016] exact repository Git inputs install through a selected npm 11.12.1 bundle and real JavaScript consumers', { skip: !process.env.WXS_VERIFICATION_NPM_CLI }, repositoryNpmInstallTest)
integrationTest('WHAT[verification-system-016] a complete selected SDK archive resolves the captured repository global.json and reclaims its owned roots', { skip: !process.env.WXS_VERIFICATION_DOTNET_ROOT }, repositoryDotnetSdkTest)
integrationTest('WHAT[verification-system-016] selected complete SDK restores original repository local tools from explicitly identified NuGet archives', { skip: !process.env.WXS_VERIFICATION_DOTNET_ROOT }, repositoryDotnetToolsTest)
integrationTest('WHAT[verification-system-016] selected complete Git source and SDK restore the original foundation identity project through a fresh-cache locked NuGet graph', { skip: !hasExplicitReadonlyContext() }, repositoryNugetProjectTest)
integrationTest('WHAT[verification-system-016] four selected input owners compile the original foundation identity project with Fable into isolated outputs', { skip: !hasExplicitReadonlyContext() }, repositoryFableProjectTest)
integrationTest('WHAT[verification-system-016] mac readonly views protect actual original-project Fable inputs and restore all prepared owners', { skip: process.platform !== 'darwin' || !hasExplicitReadonlyContext() }, repositoryReadonlyFableProjectTest)

test('WHAT[verification-system-016] the actual verification run binds its evidence to the same immutable candidate snapshot', async () => {
  const fixture = setupFixtureRepo()
  const tmpLogs = fs.mkdtempSync(path.join(os.tmpdir(), 'verify-candidate-logs-'))
  let buf = ''
  const sink = { write(chunk) { buf += chunk } }

  try {
    const expectedInputs = collectVerificationInputs(fixture)
    const expectedDigest = computeDigest(expectedInputs)

    const cleanStep = async ({ label }) => ({ label, ok: true, exitCode: 0, signal: null, durationMs: 2 })
    const result = await verify({
      root: fixture,
      release: false,
      runStep: cleanStep,
      output: sink,
      logDirectory: tmpLogs,
    })

    assert.equal(result.exitCode, 0)
    assert.equal(result.outcome, 'pass')
    assert.equal(typeof result.candidateDigest, 'string', 'verification result must carry candidateDigest evidence')
    assert.equal(result.candidateDigest, expectedDigest, 'candidateDigest must match the exact immutable snapshot digest')
  } finally {
    fs.rmSync(fixture, { recursive: true, force: true })
    fs.rmSync(tmpLogs, { recursive: true, force: true })
  }
})
import { collectGeneratedInputs, collectVerificationInputs, computeDigest, diffVerificationInputs } from '../../../scripts/lib/build-state.mjs'
import { verify } from '../../../scripts/verify.mjs'

function setupFixtureRepo(objectFormat = 'sha1') {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'verify-inputs-fixture-'))

  fs.mkdirSync(path.join(dir, 'src'), { recursive: true })
  fs.mkdirSync(path.join(dir, 'scripts'), { recursive: true })
  fs.mkdirSync(path.join(dir, 'requirements/p/tests'), { recursive: true })
  fs.mkdirSync(path.join(dir, 'resources'), { recursive: true })
  fs.mkdirSync(path.join(dir, '.github/workflows'), { recursive: true })

  fs.writeFileSync(path.join(dir, 'src/Foo.fs'), 'module Foo\nlet x = 1\n')
  fs.writeFileSync(path.join(dir, 'scripts/x.mjs'), 'console.log("x")\n')
  fs.writeFileSync(path.join(dir, 'requirements/p/tests/p.test.mjs'), '// test\n')
  fs.writeFileSync(path.join(dir, 'requirements/p/WHAT.md'), '# WHAT\n')
  fs.writeFileSync(path.join(dir, 'resources/r.txt'), 'resource\n')
  fs.writeFileSync(path.join(dir, '.github/workflows/ci.yml'), 'name: CI\n')
  fs.writeFileSync(path.join(dir, 'package.json'), '{"name":"fixture"}\n')
  fs.writeFileSync(path.join(dir, 'package-lock.json'), '{"lockfileVersion":3}\n')
  execFileSync('git', ['init', '--quiet', `--object-format=${objectFormat}`, dir])

  return dir
}

const prepareSource = async options => (await import('../../../scripts/lib/verification-source-candidate.mjs')).prepareGitSourceCandidate(options)
const fixtureGit = (root, ...args) => execFileSync('git', ['-C', root, ...args]).toString().trim()

function dependencyArchive(root, members) {
  const blocks = []
  for (const member of members) {
    const bytes = Buffer.from(member.bytes ?? '')
    const header = new Header({ path: member.path, type: member.type ?? 'File', mode: member.mode ?? 0o644, size: bytes.length, linkpath: member.target })
    header.encode()
    blocks.push(header.block, bytes, Buffer.alloc((512 - bytes.length % 512) % 512))
  }
  blocks.push(Buffer.alloc(1024))
  const archivePath = path.join(root, 'dependencies.tar')
  fs.writeFileSync(archivePath, Buffer.concat(blocks))
  return { archivePath, archiveSha256: computeFileDigest(fs.readFileSync(archivePath)) }
}

function computeFileDigest(bytes) {
  return createHash('sha256').update(bytes).digest('hex')
}

const prepareDependencies = async options => (await import('../../../scripts/lib/verification-dependency-candidate.mjs')).prepareVerificationDependencies(options)
const installDependencies = async options => (await import('../../../scripts/lib/verification-npm-candidate.mjs')).installVerificationDependencies(options)

test('WHAT[verification-system-016] npm installation uses the selected real CLI and two locked registry packages without lifecycle scripts or ambient launcher configuration', async () => {
  const fixture = await createNpmInstallFixture()
  let candidate
  const poisonedHome = path.join(fixture.root, 'foreign-home')
  const poisonedCache = path.join(fixture.root, 'foreign-cache')
  const poisonedPrefix = path.join(fixture.root, 'foreign-prefix')
  for (const directory of [poisonedHome, poisonedCache, poisonedPrefix]) fs.mkdirSync(directory)
  const preload = path.join(fixture.root, 'poison.cjs')
  const npmrc = path.join(fixture.root, 'foreign.npmrc')
  fs.writeFileSync(preload, 'throw new Error("ambient Node launcher was inherited")\n')
  fs.writeFileSync(npmrc, 'dry-run=true\nignore-scripts=false\nregistry=http://127.0.0.1:1/poison\n')
  const poison = {
    HOME: poisonedHome,
    XDG_CONFIG_HOME: poisonedHome,
    NODE_OPTIONS: `--require=${preload}`,
    NODE_PATH: poisonedHome,
    npm_config_userconfig: npmrc,
    NPM_CONFIG_GLOBALCONFIG: npmrc,
    npm_config_registry: 'http://127.0.0.1:1/poison',
    npm_config_cache: poisonedCache,
    npm_config_prefix: poisonedPrefix,
    npm_config_dry_run: 'true',
    npm_config_ignore_scripts: 'false',
  }
  const previous = new Map(Object.keys(poison).map(name => [name, process.env[name]]))
  const packageJson = fs.readFileSync(path.join(fixture.sourceRoot, 'package.json'))
  const lockfile = fs.readFileSync(path.join(fixture.sourceRoot, 'package-lock.json'))
  try {
    try {
      Object.assign(process.env, poison)
      candidate = await installDependencies(fixture.options)
    } finally {
      for (const [name, value] of previous) {
        if (value === undefined) delete process.env[name]
        else process.env[name] = value
      }
    }
    assert.deepEqual(candidate.installation, {
      npmVersion: fixture.options.expectedNpmVersion,
      nodeSha256: fixture.options.nodeSha256,
      npmCliSha256: fixture.options.npmCliSha256,
      platform: process.platform,
      arch: process.arch,
      lifecycleScripts: 'disabled',
      identityScope: 'bootstrap-admission',
    })
    assert.equal(candidate.packageJsonSha256, computeFileDigest(packageJson))
    assert.equal(candidate.lockfileSha256, computeFileDigest(lockfile))
    assert.match(candidate.archiveSha256, /^[a-f0-9]{64}$/)
    assert.match(candidate.dependencyDigest, /^[a-f0-9]{64}$/)
    for (const name of [fixture.parentName, fixture.leafName]) {
      assert.ok(fixture.requests.some(request => request.path === `/${name}/-/${name}-1.0.0.tgz`))
      assert.ok(candidate.entries.some(entry => entry.path === `node_modules/${name}/index.js`))
    }
    assert.deepEqual(fs.readFileSync(path.join(fixture.sourceRoot, 'package.json')), packageJson)
    assert.deepEqual(fs.readFileSync(path.join(fixture.sourceRoot, 'package-lock.json')), lockfile)
    assert.deepEqual(fs.readdirSync(poisonedHome), [])
    assert.deepEqual(fs.readdirSync(poisonedCache), [])
    assert.deepEqual(fs.readdirSync(poisonedPrefix), [])
    assert.deepEqual(fs.readdirSync(fixture.parentDirectory), [path.basename(candidate.dependencyRoot)])
    const answer = execFileSync(process.execPath, ['--input-type=module', '-e', `console.log((await import('${fixture.parentName}')).default)`], {
      cwd: candidate.dependencyRoot,
      env: { PATH: process.env.PATH, NODE_PATH: '', NODE_OPTIONS: '' },
      encoding: 'utf8',
    })
    assert.equal(answer, '42\n')
    candidate.dispose()
    assert.deepEqual(fs.readdirSync(fixture.parentDirectory), [])
  } finally {
    candidate?.dispose()
    await fixture.dispose()
  }
})

test('WHAT[verification-system-016] npm installation rejects real registry bytes that violate the selected lock integrity and reclaims all installation resources', async () => {
  const fixture = await createNpmInstallFixture()
  try {
    fixture.corruptLeaf()
    await assert.rejects(installDependencies(fixture.options), error => {
      assert.equal(typeof error.exitCode, 'number')
      assert.notEqual(error.exitCode, 0)
      assert.match(String(error.stderr), /EINTEGRITY|integrity checksum failed/)
      return true
    })
    assert.ok(fixture.requests.some(request => request.path === `/${fixture.leafName}/-/${fixture.leafName}-1.0.0.tgz`))
    assert.deepEqual(fs.readdirSync(fixture.parentDirectory), [])
  } finally {
    await fixture.dispose()
  }
})

test('WHAT[verification-system-016] npm installation fails through real npm ci when a required transitive package is missing from the lock inventory', async () => {
  const fixture = await createNpmInstallFixture()
  try {
    fixture.updateLock(lock => { delete lock.packages[`node_modules/${fixture.leafName}`] })
    await assert.rejects(installDependencies(fixture.options), error => {
      assert.equal(typeof error.exitCode, 'number')
      assert.notEqual(error.exitCode, 0)
      assert.match(String(error.stderr), /Missing: wxs-fixture-leaf@1\.0\.0 from lock file/)
      return true
    })
    assert.deepEqual(fs.readdirSync(fixture.parentDirectory), [])
  } finally {
    await fixture.dispose()
  }
})

test('WHAT[verification-system-016] npm installation refuses file git link and workspace dependency authorities before making registry requests', async (t) => {
  const fixture = await createNpmInstallFixture()
  const packagePath = path.join(fixture.sourceRoot, 'package.json')
  const lockPath = path.join(fixture.sourceRoot, 'package-lock.json')
  const originalPackage = fs.readFileSync(packagePath)
  const originalLock = fs.readFileSync(lockPath)
  try {
    for (const dependency of ['file:../foreign', 'git+https://example.invalid/foreign.git', 'link:../foreign', 'workspace:*', '.', '..', 'foreign.tgz', 'foreign.tar.gz', 'foreign.tar']) {
      await t.test(`WHAT[verification-system-016] npm installation refuses ${dependency}`, async () => {
        fs.writeFileSync(packagePath, originalPackage)
        fs.writeFileSync(lockPath, originalLock)
        const manifest = JSON.parse(originalPackage)
        manifest.dependencies[fixture.parentName] = dependency
        fs.writeFileSync(packagePath, JSON.stringify(manifest))
        fixture.updateLock(lock => { lock.packages[''].dependencies[fixture.parentName] = dependency })
        let outputStarted = false
        await assert.rejects(installDependencies({ ...fixture.options, output: { write() { outputStarted = true } } }), { code: 'verification-npm-lock-invalid' })
        assert.equal(outputStarted, false)
        assert.deepEqual(fixture.requests, [])
        assert.deepEqual(fs.readdirSync(fixture.parentDirectory), [])
      })
    }
  } finally {
    await fixture.dispose()
  }
})

test('WHAT[verification-system-016] cancelled actual npm installation preserves a replacement root and its original cancellation cause', async () => {
  const fixture = await createNpmInstallFixture()
  const controller = new AbortController()
  const reason = new Error('cancel npm after owned root replacement')
  let settled
  try {
    fixture.holdLeaf()
    const installing = installDependencies({ ...fixture.options, signal: controller.signal })
    settled = Promise.allSettled([installing])
    await Promise.race([fixture.leafRequest, installing.then(() => { throw new Error('npm published before held input') })])
    const names = fs.readdirSync(fixture.parentDirectory)
    assert.equal(names.length, 1)
    const installation = path.join(fixture.parentDirectory, names[0])
    const parked = path.join(fixture.root, 'parked-installation')
    fs.renameSync(installation, parked)
    fs.cpSync(parked, installation, { recursive: true })
    fs.writeFileSync(path.join(installation, 'foreign-marker'), 'replacement')
    controller.abort(reason)
    const [outcome] = await settled
    assert.equal(outcome.status, 'rejected')
    assert.ok(outcome.reason instanceof AggregateError)
    assert.equal(outcome.reason.cause, reason)
    assert.ok(outcome.reason.errors.some(error => error.code === 'verification-npm-lock-invalid'))
    await fixture.leafClosed
    assert.equal(fs.readFileSync(path.join(installation, 'foreign-marker'), 'utf8'), 'replacement')
  } finally {
    controller.abort(reason)
    await settled
    await fixture.dispose()
  }
})

test('WHAT[verification-system-016] npm installation cancellation closes the actual held tarball request preserves its original reason and reclaims partial installation', async () => {
  const fixture = await createNpmInstallFixture()
  const controller = new AbortController()
  const reason = new Error('controlled npm preparation cancellation')
  let settled
  try {
    fixture.holdLeaf()
    const installing = installDependencies({ ...fixture.options, signal: controller.signal })
    settled = Promise.allSettled([installing])
    await Promise.race([
      fixture.leafRequest,
      installing.then(() => { throw new Error('npm published before the held tarball completed') }),
    ])
    assert.ok(fs.readdirSync(fixture.parentDirectory).length > 0)
    controller.abort(reason)
    await assert.rejects(installing, error => error === reason)
    await fixture.leafClosed
    assert.deepEqual(fs.readdirSync(fixture.parentDirectory), [])
  } finally {
    controller.abort(reason)
    await settled
    await fixture.dispose()
  }
})

test('WHAT[verification-system-016] npm installation null reason cancellation preserves the exact reason after a real tarball request starts', async () => {
  const fixture = await createNpmInstallFixture()
  const controller = new AbortController()
  let settled
  try {
    fixture.holdLeaf()
    const installing = installDependencies({ ...fixture.options, signal: controller.signal })
    settled = Promise.allSettled([installing])
    await Promise.race([
      fixture.leafRequest,
      installing.then(() => { throw new Error('npm published before the held tarball completed') }),
    ])
    assert.ok(fixture.requests.some(request => request.path.includes(fixture.leafName) && request.path.endsWith('.tgz')))
    assert.ok(fs.readdirSync(fixture.parentDirectory).length > 0)
    controller.abort(null)
    const [outcome] = await settled
    assert.equal(outcome.status, 'rejected')
    assert.equal(outcome.reason, null)
    await fixture.leafClosed
    assert.deepEqual(fs.readdirSync(fixture.parentDirectory), [])
  } finally {
    controller.abort(null)
    await settled
    await fixture.dispose()
  }
})

test('WHAT[verification-system-016] npm installation refuses source overrides with foreign dependency authority before launching npm', async () => {
  const fixture = await createNpmInstallFixture()
  try {
    const packagePath = path.join(fixture.sourceRoot, 'package.json')
    const manifest = JSON.parse(fs.readFileSync(packagePath, 'utf8'))
    manifest.overrides = { [fixture.leafName]: 'file:../foreign' }
    fs.writeFileSync(packagePath, JSON.stringify(manifest))
    await assert.rejects(installDependencies(fixture.options), { code: 'verification-npm-lock-invalid' })
    assert.deepEqual(fixture.requests, [])
    assert.deepEqual(fs.readdirSync(fixture.parentDirectory), [])
  } finally {
    await fixture.dispose()
  }
})

test('WHAT[verification-system-016] npm installation rejects registry escapes linked lock entries and source workspace authority before network or allocation', async (t) => {
  const fixture = await createNpmInstallFixture()
  const packagePath = path.join(fixture.sourceRoot, 'package.json')
  const lockPath = path.join(fixture.sourceRoot, 'package-lock.json')
  const originalPackage = fs.readFileSync(packagePath)
  const originalLock = fs.readFileSync(lockPath)
  const cases = [
    { name: 'different registry origin', lock: lock => { lock.packages[`node_modules/${fixture.leafName}`].resolved = `${fixture.registry.replace('127.0.0.1', 'localhost')}/foreign.tgz` } },
    { name: 'registry path prefix sibling', options: { registry: `${fixture.registry}/selected/` }, lock: lock => { for (const [entryPath, entry] of Object.entries(lock.packages)) if (entryPath) entry.resolved = `${fixture.registry}/selected-other/package.tgz` } },
    { name: 'registry credentials', options: { registry: fixture.registry.replace('http://', 'http://user:password@') } },
    { name: 'registry query', options: { registry: `${fixture.registry}/?registry=foreign` } },
    { name: 'registry fragment', options: { registry: `${fixture.registry}/#foreign` } },
    { name: 'nonlocal plaintext registry', options: { registry: 'http://registry.example.invalid/' } },
    { name: 'locked tarball credentials', lock: lock => { lock.packages[`node_modules/${fixture.leafName}`].resolved = lock.packages[`node_modules/${fixture.leafName}`].resolved.replace('http://', 'http://user:password@') } },
    { name: 'locked tarball query', lock: lock => { lock.packages[`node_modules/${fixture.leafName}`].resolved += '?foreign=true' } },
    { name: 'locked tarball fragment', lock: lock => { lock.packages[`node_modules/${fixture.leafName}`].resolved += '#foreign' } },
    { name: 'linked lock member', lock: lock => { lock.packages[`node_modules/${fixture.leafName}`].link = true } },
    { name: 'source workspaces', manifest: manifest => { manifest.workspaces = ['packages/*'] } },
    { name: 'locked root workspaces', lock: lock => { lock.packages[''].workspaces = ['packages/*'] } },
    { name: 'nested git override', manifest: manifest => { manifest.overrides = { [fixture.parentName]: { [fixture.leafName]: 'git+https://example.invalid/foreign.git' } } } },
    { name: 'link override', manifest: manifest => { manifest.overrides = { [fixture.leafName]: 'link:../foreign' } } },
    { name: 'workspace override', manifest: manifest => { manifest.overrides = { [fixture.leafName]: 'workspace:*' } } },
    ...['.', '..', 'foreign.tgz', 'foreign.tar.gz', 'foreign.tar'].map(spec => ({
      name: `local archive or directory override ${spec}`,
      manifest: manifest => { manifest.overrides = { [fixture.leafName]: spec } },
    })),
  ]
  try {
    for (const entry of cases) {
      await t.test(`WHAT[verification-system-016] npm installation refuses ${entry.name}`, async () => {
        fs.writeFileSync(packagePath, originalPackage)
        fs.writeFileSync(lockPath, originalLock)
        if (entry.manifest) {
          const manifest = JSON.parse(originalPackage)
          entry.manifest(manifest)
          fs.writeFileSync(packagePath, JSON.stringify(manifest))
        }
        if (entry.lock) fixture.updateLock(entry.lock)
        let outputStarted = false
        await assert.rejects(installDependencies({ ...fixture.options, ...entry.options, output: { write() { outputStarted = true } } }), { code: 'verification-npm-lock-invalid' })
        assert.equal(outputStarted, false)
        assert.deepEqual(fixture.requests, [])
        assert.deepEqual(fs.readdirSync(fixture.parentDirectory), [])
      })
    }
  } finally {
    await fixture.dispose()
  }
})

test('WHAT[verification-system-016] npm installation admits only the selected Node CLI hashes version and declared package manager', async (t) => {
  const fixture = await createNpmInstallFixture()
  const packagePath = path.join(fixture.sourceRoot, 'package.json')
  const originalPackage = fs.readFileSync(packagePath)
  const cases = [
    { name: 'Node byte identity mismatch', options: { nodeSha256: '0'.repeat(64) }, code: 'verification-npm-tool-invalid' },
    { name: 'npm CLI byte identity mismatch', options: { npmCliSha256: '0'.repeat(64) }, code: 'verification-npm-tool-invalid' },
    { name: 'actual npm version mismatch', options: { expectedNpmVersion: '0.0.0' }, manifest: manifest => { manifest.packageManager = 'npm@0.0.0' }, code: 'verification-npm-tool-invalid' },
    { name: 'declared package manager mismatch', manifest: manifest => { manifest.packageManager = 'npm@0.0.0' }, code: 'verification-npm-lock-invalid' },
  ]
  try {
    for (const entry of cases) {
      await t.test(`WHAT[verification-system-016] npm installation refuses ${entry.name}`, async () => {
        fs.writeFileSync(packagePath, originalPackage)
        if (entry.manifest) {
          const manifest = JSON.parse(originalPackage)
          entry.manifest(manifest)
          fs.writeFileSync(packagePath, JSON.stringify(manifest))
        }
        await assert.rejects(installDependencies({ ...fixture.options, ...entry.options }), { code: entry.code })
        assert.deepEqual(fixture.requests, [])
        assert.deepEqual(fs.readdirSync(fixture.parentDirectory), [])
      })
    }
  } finally {
    await fixture.dispose()
  }
})

test('WHAT[verification-system-016] npm installation already aborted admission preserves Error and null reasons before reading missing source files', async (t) => {
  const fixture = await createAbortedNpmAdmissionFixture()
  try {
    fixture.assertUnchanged()
    for (const reason of [new Error('cancelled before npm admission'), null]) {
      await t.test(`WHAT[verification-system-016] npm installation already aborted with ${reason === null ? 'null' : 'Error'}`, async () => {
        const controller = new AbortController()
        controller.abort(reason)
        const [outcome] = await Promise.allSettled([installDependencies({ ...fixture.options, signal: controller.signal })])
        assert.equal(outcome.status, 'rejected')
        assert.equal(outcome.reason, reason)
        fixture.assertUnchanged()
      })
    }
  } finally {
    await fixture.dispose()
  }
})

const dependencyMembers = () => [
  { path: 'node_modules/', type: 'Directory', mode: 0o755 },
  { path: 'node_modules/.package-lock.json', bytes: '{"hidden":true}\n' },
  { path: 'node_modules/demo/', type: 'Directory', mode: 0o755 },
  { path: 'node_modules/demo/package.json', bytes: '{"type":"module","exports":"./index.js"}' },
  { path: 'node_modules/demo/index.js', bytes: 'export default 42\n' },
  { path: 'node_modules/demo/cli.js', bytes: '#!/usr/bin/env node\n', mode: 0o755 },
  { path: 'node_modules/.bin/', type: 'Directory', mode: 0o755 },
  { path: 'node_modules/.bin/demo', type: 'SymbolicLink', target: '../demo/cli.js' },
]

test('WHAT[verification-system-016] explicit dependency archive identity preserves complete bytes modes and internal links independently of its origin', async () => {
  const fixture = setupFixtureRepo()
  const parent = fs.mkdtempSync(path.join(os.tmpdir(), 'dependency-candidate-parent-'))
  const candidates = []
  try {
    const archive = dependencyArchive(fixture, dependencyMembers())
    for (let i = 0; i < 2; i++) candidates.push(await prepareDependencies({ sourceRoot: fixture, parentDirectory: parent, ...archive }))
    assert.equal(candidates[0].dependencyDigest, candidates[1].dependencyDigest)
    assert.notEqual(candidates[0].dependencyRoot, candidates[1].dependencyRoot)
    fs.unlinkSync(archive.archivePath)
    fs.writeFileSync(path.join(fixture, 'package-lock.json'), 'changed after selection')
    const candidate = candidates[0]
    assert.equal(candidate.lockfileSha256, computeFileDigest(Buffer.from('{"lockfileVersion":3}\n')))
    assert.equal(candidate.archiveSha256, archive.archiveSha256)
    assert.equal(candidate.entries.length, 8)
    assert.equal(fs.readFileSync(path.join(candidate.dependencyRoot, 'node_modules/.package-lock.json'), 'utf8'), '{"hidden":true}\n')
    assert.deepEqual(fs.readFileSync(path.join(candidate.dependencyRoot, 'node_modules/demo/index.js')), Buffer.from('export default 42\n'))
    assert.equal(fs.statSync(path.join(candidate.dependencyRoot, 'node_modules/demo/cli.js')).mode & 0o777, 0o755)
    assert.equal(fs.readlinkSync(path.join(candidate.dependencyRoot, 'node_modules/.bin/demo')), '../demo/cli.js')
    assert.equal(fs.realpathSync(path.join(candidate.dependencyRoot, 'node_modules/.bin/demo')), path.join(candidate.dependencyRoot, 'node_modules/demo/cli.js'))
    const result = execFileSync(process.execPath, ['--input-type=module', '-e', 'console.log((await import("demo")).default)'], { cwd: candidate.dependencyRoot, env: { ...process.env, NODE_PATH: '' } }).toString()
    assert.equal(result, '42\n')
    candidate.dispose()
    candidate.dispose()
    assert.equal(fs.existsSync(candidate.dependencyRoot), false)
  } finally {
    for (const candidate of candidates) candidate.dispose()
    fs.rmSync(fixture, { recursive: true, force: true })
    fs.rmSync(parent, { recursive: true, force: true })
  }
})

test('WHAT[verification-system-016] dependency preparation rejects an unselected archive and binds lockfile and executable bytes to identity', async () => {
  const fixture = setupFixtureRepo()
  const parent = fs.mkdtempSync(path.join(os.tmpdir(), 'dependency-identity-parent-'))
  const candidates = []
  try {
    const archive = dependencyArchive(fixture, dependencyMembers())
    await assert.rejects(prepareDependencies({ sourceRoot: fixture, parentDirectory: parent, ...archive, archiveSha256: '0'.repeat(64) }), { code: 'dependency-candidate-integrity-invalid' })
    assert.deepEqual(fs.readdirSync(parent), [])
    candidates.push(await prepareDependencies({ sourceRoot: fixture, parentDirectory: parent, ...archive }))
    fs.writeFileSync(path.join(fixture, 'package-lock.json'), '{"lockfileVersion":3,"changed":true}\n')
    candidates.push(await prepareDependencies({ sourceRoot: fixture, parentDirectory: parent, ...archive }))
    assert.notEqual(candidates[0].dependencyDigest, candidates[1].dependencyDigest)
    const changed = dependencyMembers()
    changed.find(member => member.path === 'node_modules/demo/index.js').bytes = 'export default 43\n'
    candidates.push(await prepareDependencies({ sourceRoot: fixture, parentDirectory: parent, ...dependencyArchive(fixture, changed) }))
    assert.notEqual(candidates[1].dependencyDigest, candidates[2].dependencyDigest)
    const modeChanged = dependencyMembers()
    modeChanged.find(member => member.path === 'node_modules/demo/index.js').mode = 0o755
    candidates.push(await prepareDependencies({ sourceRoot: fixture, parentDirectory: parent, ...dependencyArchive(fixture, modeChanged) }))
    assert.notEqual(candidates[1].dependencyDigest, candidates[3].dependencyDigest)
  } finally {
    for (const candidate of candidates) candidate.dispose()
    fs.rmSync(fixture, { recursive: true, force: true })
    fs.rmSync(parent, { recursive: true, force: true })
  }
})

test('WHAT[verification-system-016] dependency archives reject external dangling cyclic aliasing and special entries without publishing or escaping their owned root', async () => {
  const fixture = setupFixtureRepo()
  const parent = fs.mkdtempSync(path.join(os.tmpdir(), 'dependency-invalid-parent-'))
  try {
    const cases = [
      { path: 'node_modules/escape', type: 'SymbolicLink', target: '../../outside' },
      { path: 'node_modules/absolute', type: 'SymbolicLink', target: path.join(fixture, 'resources/r.txt') },
      { path: 'node_modules/missing', type: 'SymbolicLink', target: 'absent' },
      { path: 'node_modules/cycle', type: 'SymbolicLink', target: 'cycle' },
      { path: 'node_modules/demo/index.js', bytes: 'duplicate' },
      { path: 'node_modules/../outside', bytes: 'escaped' },
      { path: 'node_modules/demo/hard', type: 'Link', target: 'node_modules/demo/index.js' },
      { path: 'node_modules/pipe', type: 'FIFO' },
      { path: 'node_modules/device', type: 'CharacterDevice' },
      { path: 'node_modules/ambiguous', type: 'SymbolicLink', target: 'demo/../demo/index.js' },
      { path: 'node_modules/suid', mode: 0o4644, bytes: 'special permissions' },
      { path: 'node_modules/.bin/demo/nested', bytes: 'symlink parent' },
    ]
    for (const invalid of cases) {
      await assert.rejects(prepareDependencies({ sourceRoot: fixture, parentDirectory: parent, ...dependencyArchive(fixture, [...dependencyMembers(), invalid]) }), { code: 'dependency-candidate-entry-invalid' }, JSON.stringify(invalid))
      assert.deepEqual(fs.readdirSync(parent), [])
      assert.equal(fs.readFileSync(path.join(fixture, 'resources/r.txt'), 'utf8'), 'resource\n')
    }
  } finally {
    fs.rmSync(fixture, { recursive: true, force: true })
    fs.rmSync(parent, { recursive: true, force: true })
  }
})

test('WHAT[verification-system-016] dependency preparation fully consumes compressed archives and refuses malformed or truncated input and linked lockfiles', async () => {
  const fixture = setupFixtureRepo()
  const parent = fs.mkdtempSync(path.join(os.tmpdir(), 'dependency-parser-parent-'))
  let candidate
  try {
    const archive = dependencyArchive(fixture, dependencyMembers())
    const tarBytes = fs.readFileSync(archive.archivePath)
    const compressed = gzipSync(tarBytes)
    fs.writeFileSync(archive.archivePath, compressed)
    candidate = await prepareDependencies({ sourceRoot: fixture, parentDirectory: parent, archivePath: archive.archivePath, archiveSha256: computeFileDigest(compressed) })
    assert.equal(candidate.archiveSha256, computeFileDigest(compressed))
    candidate.dispose()
    for (const invalid of [Buffer.from('not tar'), compressed.subarray(0, compressed.length - 12), tarBytes.subarray(0, 1900), tarBytes.subarray(0, tarBytes.length - 1024), Buffer.concat([tarBytes, Buffer.alloc(512, 1)]), Buffer.concat([tarBytes, tarBytes])]) {
      fs.writeFileSync(archive.archivePath, invalid)
      await assert.rejects(prepareDependencies({ sourceRoot: fixture, parentDirectory: parent, archivePath: archive.archivePath, archiveSha256: computeFileDigest(invalid) }))
      assert.deepEqual(fs.readdirSync(parent), [])
    }
    fs.writeFileSync(archive.archivePath, tarBytes)
    fs.unlinkSync(path.join(fixture, 'package-lock.json'))
    fs.symlinkSync('resources/r.txt', path.join(fixture, 'package-lock.json'))
    await assert.rejects(prepareDependencies({ sourceRoot: fixture, parentDirectory: parent, ...archive }), { code: 'verification-inputs-symbolic-link' })
    assert.deepEqual(fs.readdirSync(parent), [])
  } finally {
    candidate?.dispose()
    fs.rmSync(fixture, { recursive: true, force: true })
    fs.rmSync(parent, { recursive: true, force: true })
  }
})

test('WHAT[verification-system-016] a selected Git tree preserves complete raw source bytes and its own corpus inventory despite later workspace and index edits', async () => {
  const fixture = setupFixtureRepo()
  const parent = fs.mkdtempSync(path.join(os.tmpdir(), 'source-candidate-parent-'))
  let candidate
  try {
    fs.mkdirSync(path.join(fixture, 'proposals'))
    const specialPath = 'proposals/中文 space\tline\n.md'
    const binary = Buffer.from([0, 10, 255, 128, 0])
    fs.writeFileSync(path.join(fixture, specialPath), binary)
    fs.writeFileSync(path.join(fixture, '.gitattributes'), 'proposals/** export-ignore\nresources/r.txt filter=broken\n')
    fixtureGit(fixture, 'add', '.')
    fixtureGit(fixture, 'update-index', '--chmod=+x', 'scripts/x.mjs')
    const treeId = fixtureGit(fixture, 'write-tree')
    const originalSource = fs.readFileSync(path.join(fixture, 'src/Foo.fs'))
    fixtureGit(fixture, 'config', 'filter.broken.smudge', 'false')
    fs.writeFileSync(path.join(fixture, 'src/Foo.fs'), 'module Changed\n')
    fixtureGit(fixture, 'rm', '--cached', 'resources/r.txt')
    fs.writeFileSync(path.join(fixture, 'untracked.md'), '# Not selected\n')
    fs.unlinkSync(path.join(fixture, specialPath))

    candidate = await prepareSource({ repositoryRoot: fixture, treeId, parentDirectory: parent })
    assert.equal(candidate.treeId, treeId)
    assert.deepEqual(fs.readFileSync(path.join(candidate.sourceRoot, 'src/Foo.fs')), originalSource)
    assert.deepEqual(fs.readFileSync(path.join(candidate.sourceRoot, specialPath)), binary)
    assert.equal(fs.readFileSync(path.join(candidate.sourceRoot, 'resources/r.txt'), 'utf8'), 'resource\n')
    assert.equal(fs.existsSync(path.join(candidate.sourceRoot, 'untracked.md')), false)
    assert.equal(fs.statSync(path.join(candidate.sourceRoot, 'scripts/x.mjs')).mode & 0o111, 0o111)
    assert.equal(fixtureGit(candidate.sourceRoot, 'write-tree'), treeId)
    assert.ok(collectGeneratedInputs(candidate.sourceRoot).some(entry => entry.path === specialPath))
    assert.equal(fs.existsSync(path.join(candidate.sourceRoot, '.git/objects/info/alternates')), false)
    assert.ok(candidate.entries.some(entry => entry.path === specialPath && entry.size === binary.length))
    assert.match(candidate.sourceDigest, /^[0-9a-f]{64}$/)
  } finally {
    candidate?.dispose()
    assert.deepEqual(fs.readdirSync(parent), [])
    fs.rmSync(fixture, { recursive: true, force: true })
    fs.rmSync(parent, { recursive: true, force: true })
  }
})

for (const objectFormat of ['sha1', 'sha256']) {
test(`WHAT[verification-system-016] ${objectFormat} source candidate identity is independent of location and binds executable mode`, async () => {
  const fixture = setupFixtureRepo(objectFormat)
  const parent = fs.mkdtempSync(path.join(os.tmpdir(), 'source-identity-parent-'))
  const candidates = []
  try {
    fixtureGit(fixture, 'add', '.')
    const treeId = fixtureGit(fixture, 'write-tree')
    for (let i = 0; i < 2; i++) candidates.push(await prepareSource({ repositoryRoot: fixture, treeId, parentDirectory: parent }))
    assert.notEqual(candidates[0].sourceRoot, candidates[1].sourceRoot)
    assert.equal(candidates[0].objectFormat, objectFormat)
    assert.equal(candidates[0].sourceDigest, candidates[1].sourceDigest)
    fixtureGit(fixture, 'update-index', '--chmod=+x', 'scripts/x.mjs')
    candidates.push(await prepareSource({ repositoryRoot: fixture, treeId: fixtureGit(fixture, 'write-tree'), parentDirectory: parent }))
    assert.notEqual(candidates[0].sourceDigest, candidates[2].sourceDigest)
    candidates[0].dispose()
    candidates[0].dispose()
    assert.equal(fs.existsSync(candidates[0].sourceRoot), false)
  } finally {
    for (const candidate of candidates) candidate.dispose()
    fs.rmSync(fixture, { recursive: true, force: true })
    fs.rmSync(parent, { recursive: true, force: true })
  }
})
}

test('WHAT[verification-system-016] source preparation ignores replacement refs and inherited Git repository redirection', async () => {
  const fixture = setupFixtureRepo()
  const hostile = setupFixtureRepo()
  const parent = fs.mkdtempSync(path.join(os.tmpdir(), 'source-environment-parent-'))
  const names = ['GIT_DIR', 'GIT_INDEX_FILE', 'GIT_OBJECT_DIRECTORY', 'GIT_ALTERNATE_OBJECT_DIRECTORIES']
  const previous = names.map(name => process.env[name])
  let candidate
  try {
    fixtureGit(fixture, 'add', '.')
    fixtureGit(hostile, 'add', '.')
    const treeId = fixtureGit(fixture, 'write-tree')
    const original = fs.readFileSync(path.join(fixture, 'src/Foo.fs'))
    const originalIndex = fs.readFileSync(path.join(hostile, '.git/index'))
    const replacement = execFileSync('git', ['-C', fixture, 'hash-object', '-w', '--stdin'], { input: 'replacement bytes\n' }).toString().trim()
    fixtureGit(fixture, 'replace', fixtureGit(fixture, 'rev-parse', ':src/Foo.fs'), replacement)
    process.env.GIT_DIR = path.join(hostile, '.git')
    process.env.GIT_INDEX_FILE = path.join(hostile, '.git/index')
    process.env.GIT_OBJECT_DIRECTORY = path.join(hostile, '.git/objects')
    process.env.GIT_ALTERNATE_OBJECT_DIRECTORIES = path.join(hostile, '.git/objects')
    candidate = await prepareSource({ repositoryRoot: fixture, treeId, parentDirectory: parent })
    assert.deepEqual(fs.readFileSync(path.join(candidate.sourceRoot, 'src/Foo.fs')), original)
    assert.deepEqual(fs.readFileSync(path.join(hostile, '.git/index')), originalIndex)
  } finally {
    names.forEach((name, index) => {
      if (previous[index] === undefined) delete process.env[name]
      else process.env[name] = previous[index]
    })
    candidate?.dispose()
    fs.rmSync(fixture, { recursive: true, force: true })
    fs.rmSync(hostile, { recursive: true, force: true })
    fs.rmSync(parent, { recursive: true, force: true })
  }
})

test('WHAT[verification-system-016] a tree topology that cannot be faithfully materialized fails and reclaims its partial source root', async () => {
  const fixture = setupFixtureRepo()
  const parent = fs.mkdtempSync(path.join(os.tmpdir(), 'source-topology-parent-'))
  try {
    const emptyTree = execFileSync('git', ['-C', fixture, 'mktree'], { input: '' }).toString().trim()
    const treeId = execFileSync('git', ['-C', fixture, 'mktree'], {
      input: `040000 tree ${emptyTree}\tempty\n`,
    }).toString().trim()
    await assert.rejects(() => prepareSource({ repositoryRoot: fixture, treeId, parentDirectory: parent }),
      error => error.code === 'source-candidate-tree-invalid')
    assert.deepEqual(fs.readdirSync(parent), [])
    assert.equal(fs.readFileSync(path.join(fixture, 'src/Foo.fs'), 'utf8'), 'module Foo\nlet x = 1\n')
  } finally {
    fs.rmSync(fixture, { recursive: true, force: true })
    fs.rmSync(parent, { recursive: true, force: true })
  }
})

for (const mode of ['120000', '160000']) {
  test(`WHAT[verification-system-016] source preparation rejects unsupported Git entry ${mode} without publishing a candidate`, async () => {
    const fixture = setupFixtureRepo()
    const parent = fs.mkdtempSync(path.join(os.tmpdir(), 'source-rejection-parent-'))
    try {
      fixtureGit(fixture, 'add', '.')
      const objectId = mode === '120000'
        ? fixtureGit(fixture, 'rev-parse', ':src/Foo.fs')
        : fixtureGit(fixture, '-c', 'user.name=Fixture', '-c', 'user.email=fixture@example.invalid', 'commit-tree', fixtureGit(fixture, 'write-tree'), '-m', 'fixture')
      fixtureGit(fixture, 'update-index', '--add', '--cacheinfo', `${mode},${objectId},linked`)
      await assert.rejects(() => prepareSource({ repositoryRoot: fixture, treeId: fixtureGit(fixture, 'write-tree'), parentDirectory: parent }),
        error => error.code === 'source-candidate-entry-unsupported')
      assert.deepEqual(fs.readdirSync(parent), [])
    } finally {
      fs.rmSync(fixture, { recursive: true, force: true })
      fs.rmSync(parent, { recursive: true, force: true })
    }
  })
}

test('WHAT[verification-system-016] missing selected blobs fail source preparation without falling back to working files', async () => {
  const fixture = setupFixtureRepo()
  const parent = fs.mkdtempSync(path.join(os.tmpdir(), 'source-missing-parent-'))
  try {
    fixtureGit(fixture, 'add', '.')
    const treeId = fixtureGit(fixture, 'write-tree')
    const blobId = fixtureGit(fixture, 'rev-parse', ':src/Foo.fs')
    fs.unlinkSync(path.join(fixture, '.git/objects', blobId.slice(0, 2), blobId.slice(2)))
    await assert.rejects(() => prepareSource({ repositoryRoot: fixture, treeId, parentDirectory: parent }),
      error => error.code === 'source-candidate-blob-invalid')
    assert.deepEqual(fs.readdirSync(parent), [])
  } finally {
    fs.rmSync(fixture, { recursive: true, force: true })
    fs.rmSync(parent, { recursive: true, force: true })
  }
})

test('WHAT[verification-system-016] source preparation rejects repositories that may implicitly fetch promised objects', async () => {
  const fixture = setupFixtureRepo()
  const parent = fs.mkdtempSync(path.join(os.tmpdir(), 'source-promisor-parent-'))
  try {
    fixtureGit(fixture, 'add', '.')
    const treeId = fixtureGit(fixture, 'write-tree')
    fixtureGit(fixture, 'config', 'remote.origin.promisor', 'true')
    await assert.rejects(() => prepareSource({ repositoryRoot: fixture, treeId, parentDirectory: parent }),
      error => error.code === 'source-candidate-promisor-unsupported')
    assert.deepEqual(fs.readdirSync(parent), [])
  } finally {
    fs.rmSync(fixture, { recursive: true, force: true })
    fs.rmSync(parent, { recursive: true, force: true })
  }
})

for (const relativePath of ['src', '.github', 'package.json', 'resources/linked.txt', 'requirements/p/linked', 'resources/obj']) {
  test(`WHAT[verification-system-016] verification rejects an unsealed symbolic input at ${relativePath} before running stages`, async () => {
    const fixture = setupFixtureRepo()
    const external = fs.mkdtempSync(path.join(os.tmpdir(), 'verify-external-input-'))
    const stages = []
    try {
      const target = path.join(fixture, relativePath)
      const isDirectory = ['src', '.github', 'requirements/p/linked'].includes(relativePath)
      const source = path.join(external, isDirectory ? 'directory' : 'file')
      if (isDirectory) {
        fs.mkdirSync(source)
        fs.writeFileSync(path.join(source, 'input.fs'), 'module External\n')
      } else {
        fs.writeFileSync(source, 'external input\n')
      }
      fs.rmSync(target, { recursive: true, force: true })
      fs.symlinkSync(source, target, isDirectory ? 'dir' : 'file')

      const result = await verify({
        root: fixture,
        logDirectory: path.join(external, 'logs'),
        output: { write() {} },
        runStep: async ({ label }) => {
          stages.push(label)
          return { label, ok: true, exitCode: 0 }
        },
      })
      assert.equal(result.exitCode, 1)
      assert.match(result.failureReason, /symbolic verification input/)
      assert.deepEqual(stages, [])
      assert.ok(result.steps.every(step => step.status === 'not-run'))
    } finally {
      fs.rmSync(fixture, { recursive: true, force: true })
      fs.rmSync(external, { recursive: true, force: true })
    }
  })
}

test('WHAT[verification-system-016] output directory names do not exclude ordinary input files, while root output links stay outside the closure', () => {
  const fixture = setupFixtureRepo()
  const external = fs.mkdtempSync(path.join(os.tmpdir(), 'verify-output-link-'))
  try {
    fs.writeFileSync(path.join(fixture, 'scripts/obj'), 'ordinary input\n')
    for (const name of ['dist', 'node_modules', '.fable-build']) {
      fs.symlinkSync(external, path.join(fixture, name), 'dir')
    }
    const inputs = collectVerificationInputs(fixture)
    assert.ok(inputs.some(entry => entry.path === 'scripts/obj'))
    assert.ok(inputs.every(entry => !['dist', 'node_modules', '.fable-build'].includes(entry.path.split('/')[0])))
  } finally {
    fs.rmSync(fixture, { recursive: true, force: true })
    fs.rmSync(external, { recursive: true, force: true })
  }
})

for (const [linkDirectory, dangling] of [[false, false], [true, false], [false, true], [true, true]]) {
  test(`WHAT[verification-system-016] tracked corpus rejects a ${dangling ? 'dangling ' : ''}symbolic ${linkDirectory ? 'ancestor' : 'file'} outside ordinary input roots`, () => {
    const fixture = setupFixtureRepo()
    const external = fs.mkdtempSync(path.join(os.tmpdir(), 'verify-external-corpus-'))
    try {
      fs.writeFileSync(path.join(external, 'decision.md'), '# External\n')
      if (linkDirectory) {
        fs.symlinkSync(dangling ? path.join(external, 'missing') : external, path.join(fixture, 'proposals'), 'dir')
        execFileSync('git', ['-C', fixture, 'update-index', '--add', '--cacheinfo',
          `100644,${execFileSync('git', ['-C', fixture, 'hash-object', '-w', path.join(external, 'decision.md')]).toString().trim()},proposals/decision.md`])
      } else {
        fs.mkdirSync(path.join(fixture, 'proposals'))
        fs.symlinkSync(path.join(external, dangling ? 'missing.md' : 'decision.md'), path.join(fixture, 'proposals/decision.md'))
        execFileSync('git', ['-C', fixture, 'add', 'proposals/decision.md'])
      }
      for (const collect of [collectGeneratedInputs, collectVerificationInputs]) {
        assert.throws(() => collect(fixture), error => error.code === 'verification-inputs-symbolic-link')
      }
    } finally {
      fs.rmSync(fixture, { recursive: true, force: true })
      fs.rmSync(external, { recursive: true, force: true })
    }
  })
}

test('WHAT[verification-system-016] tracked corpus proposals contribute their actual content to verification inputs', () => {
  const fixture = setupFixtureRepo()
  try {
    fs.mkdirSync(path.join(fixture, 'proposals'))
    const proposal = path.join(fixture, 'proposals', 'decision.md')
    fs.writeFileSync(proposal, '# Before\n')
    execFileSync('git', ['-C', fixture, 'add', 'proposals/decision.md'])

    const before = collectVerificationInputs(fixture)
    assert.ok(before.some(entry => entry.path === 'proposals/decision.md'))
    assert.ok(collectGeneratedInputs(fixture).some(entry => entry.path === 'proposals/decision.md'))
    fs.writeFileSync(proposal, '# Changed\n')
    const after = collectVerificationInputs(fixture)

    assert.deepEqual(diffVerificationInputs(before, after), {
      equal: false,
      reason: 'content-changed:proposals/decision.md',
    })
    assert.notEqual(computeDigest(before), computeDigest(after))
  } finally {
    fs.rmSync(fixture, { recursive: true, force: true })
  }
})

for (const initiallyTracked of [false, true]) {
  test(`WHAT[verification-system-016] corpus tracking ${initiallyTracked ? 'removal' : 'addition'} changes verification identity without changing file bytes`, () => {
    const fixture = setupFixtureRepo()
    try {
      if (initiallyTracked) execFileSync('git', ['-C', fixture, 'add', 'src/Foo.fs'])
      const original = fs.readFileSync(path.join(fixture, 'src/Foo.fs'))
      const before = collectVerificationInputs(fixture)
      const generatedBefore = collectGeneratedInputs(fixture)

      execFileSync('git', ['-C', fixture, ...(initiallyTracked ? ['rm', '--cached', 'src/Foo.fs'] : ['add', 'src/Foo.fs'])])
      const after = collectVerificationInputs(fixture)
      const generatedAfter = collectGeneratedInputs(fixture)

      assert.deepEqual(fs.readFileSync(path.join(fixture, 'src/Foo.fs')), original)
      assert.deepEqual(before.map(entry => entry.path), after.map(entry => entry.path))
      assert.notDeepEqual(generatedBefore.map(entry => entry.path), generatedAfter.map(entry => entry.path))
      assert.equal(diffVerificationInputs(before, after).equal, false)
      assert.notEqual(computeDigest(before), computeDigest(after))
    } finally {
      fs.rmSync(fixture, { recursive: true, force: true })
    }
  })
}

for (const collect of [collectGeneratedInputs, collectVerificationInputs]) {
  test(`WHAT[verification-system-016] ${collect.name} rejects failed Git corpus inventory`, () => {
    const fixture = setupFixtureRepo()
    try {
      fs.writeFileSync(path.join(fixture, '.git', 'index'), 'invalid index')
      assert.throws(() => collect(fixture), error => {
        assert.equal(error.status, 128)
        assert.match(String(error.stderr), /index/)
        return true
      })
    } finally {
      fs.rmSync(fixture, { recursive: true, force: true })
    }
  })
}

test('WHAT[verification-system-016] failed Git corpus inventory stops verification before any stage starts', async () => {
  const fixture = setupFixtureRepo()
  const stages = []
  try {
    fs.writeFileSync(path.join(fixture, '.git', 'index'), 'invalid index')
    const result = await verify({
      root: fixture,
      output: { write() {} },
      runStep: async ({ label }) => {
        stages.push(label)
        return { label, ok: true, exitCode: 0 }
      },
    })
    assert.equal(result.exitCode, 1)
    assert.equal(result.outcome, 'fail')
    assert.match(result.failureReason, /^input-collection-failed:.*git/s)
    assert.deepEqual(stages, [])
    assert.ok(result.steps.every(step => step.status === 'not-run'))
  } finally {
    fs.rmSync(fixture, { recursive: true, force: true })
  }
})

test('WHAT[verification-system-016] collectVerificationInputs collects expected relative paths and diff detects mutations', () => {
  const fixture = setupFixtureRepo()
  try {
    const initial = collectVerificationInputs(fixture)
    const paths = initial.map((e) => e.path).sort()
    const expected = [
      '.github/workflows/ci.yml',
      'package-lock.json',
      'package.json',
      'requirements/p/WHAT.md',
      'requirements/p/tests/p.test.mjs',
      'resources/r.txt',
      'scripts/x.mjs',
      'src/Foo.fs',
    ].sort()

    assert.deepEqual(paths, expected, 'collected input paths must match fixture exactly')

    // Same size + mtime content change
    const target = path.join(fixture, 'src/Foo.fs')
    const originalContent = fs.readFileSync(target, 'utf8')
    const statBefore = fs.statSync(target)
    // Replace "1" with "2" keeping length same
    const modifiedContent = originalContent.replace('1', '2')
    assert.equal(modifiedContent.length, originalContent.length)
    fs.writeFileSync(target, modifiedContent)
    fs.utimesSync(target, statBefore.atime, statBefore.mtime)

    const afterContentChange = collectVerificationInputs(fixture)
    const contentDiff = diffVerificationInputs(initial, afterContentChange)
    assert.equal(contentDiff.equal, false)
    assert.equal(contentDiff.reason, 'content-changed:src/Foo.fs')

    // Revert target content
    fs.writeFileSync(target, originalContent)

    // Add new file
    const newFile = path.join(fixture, 'requirements/p/tests/new.test.mjs')
    fs.writeFileSync(newFile, '// new test\n')
    const afterAdd = collectVerificationInputs(fixture)
    const addDiff = diffVerificationInputs(initial, afterAdd)
    assert.equal(addDiff.equal, false)
    assert.equal(addDiff.reason, 'file-set-changed')
    fs.unlinkSync(newFile)

    // Delete existing file
    const deletedFile = path.join(fixture, 'scripts/x.mjs')
    const deletedContent = fs.readFileSync(deletedFile, 'utf8')
    fs.unlinkSync(deletedFile)
    const afterDelete = collectVerificationInputs(fixture)
    const deleteDiff = diffVerificationInputs(initial, afterDelete)
    assert.equal(deleteDiff.equal, false)
    assert.equal(deleteDiff.reason, 'file-set-changed')
    fs.writeFileSync(deletedFile, deletedContent)

    // Writing to .fable-build directory is ignored
    const buildLog = path.join(fixture, '.fable-build/run.log')
    fs.mkdirSync(path.dirname(buildLog), { recursive: true })
    fs.writeFileSync(buildLog, 'log entry\n')
    const afterBuildLog = collectVerificationInputs(fixture)
    const ignoreDiff = diffVerificationInputs(initial, afterBuildLog)
    assert.equal(ignoreDiff.equal, true, '.fable-build writes must not affect input diff')

    // Missing required root directory throws verification-inputs-root-missing
    const emptyFixture = fs.mkdtempSync(path.join(os.tmpdir(), 'verify-empty-'))
    try {
      assert.throws(
        () => collectVerificationInputs(emptyFixture),
        (err) => err.code === 'verification-inputs-root-missing',
      )
    } finally {
      fs.rmSync(emptyFixture, { recursive: true, force: true })
    }
  } finally {
    fs.rmSync(fixture, { recursive: true, force: true })
  }
})

test('WHAT[verification-system-016] verify detects mid-flight inputs change via runStep perturbation and halts with fail', async () => {
  const fixture = setupFixtureRepo()
  const tmpLogs = fs.mkdtempSync(path.join(os.tmpdir(), 'verify-step-logs-'))
  let buf = ''
  const sink = { write(chunk) { buf += chunk } }

  try {
    const mutatingRunStep = async ({ label }) => {
      if (label === 'unit') {
        fs.appendFileSync(path.join(fixture, 'src/Foo.fs'), '// mid-run mutation\n')
      }
      return { label, ok: true, exitCode: 0, signal: null, durationMs: 5 }
    }

    const result = await verify({
      root: fixture,
      release: false,
      runStep: mutatingRunStep,
      output: sink,
      logDirectory: tmpLogs,
    })

    assert.equal(result.exitCode, 1, 'verify must return exitCode 1 when inputs change')
    assert.equal(result.outcome, 'fail', 'outcome must be fail')
    assert.ok(result.inputChanges, 'inputChanges must be present')
    assert.equal(result.inputChanges.equal, false)
    assert.ok(result.inputChanges.reason.includes('src/Foo.fs'))
    const integrationStep = result.steps.find((s) => s.label === 'integration')
    assert.equal(integrationStep?.status, 'not-run', 'subsequent integration step must not run after unit mutation')

    // Control group: clean run without mutations
    const cleanLogs = fs.mkdtempSync(path.join(os.tmpdir(), 'verify-clean-logs-'))
    let cleanBuf = ''
    const cleanSink = { write(chunk) { cleanBuf += chunk } }
    try {
      // Restore Foo.fs
      fs.writeFileSync(path.join(fixture, 'src/Foo.fs'), 'module Foo\nlet x = 1\n')
      const cleanStep = async ({ label }) => ({ label, ok: true, exitCode: 0, signal: null, durationMs: 5 })
      const cleanResult = await verify({
        root: fixture,
        release: false,
        runStep: cleanStep,
        output: cleanSink,
        logDirectory: cleanLogs,
      })
      assert.equal(cleanResult.exitCode, 0, 'clean verify must succeed with 0')
      assert.equal(cleanResult.outcome, 'pass')
    } finally {
      fs.rmSync(cleanLogs, { recursive: true, force: true })
    }
  } finally {
    fs.rmSync(fixture, { recursive: true, force: true })
    fs.rmSync(tmpLogs, { recursive: true, force: true })
  }
})

test('WHAT[verification-system-016] verify with isolated logDirectory creates run dir and latest link without touching repo root', async () => {
  const fixture = setupFixtureRepo()
  const isolatedLogs = fs.mkdtempSync(path.join(os.tmpdir(), 'verify-isolated-logs-'))
  let buf = ''
  const sink = { write(chunk) { buf += chunk } }

  try {
    const fakeStep = async ({ label }) => ({ label, ok: true, exitCode: 0, signal: null, durationMs: 1 })
    const result = await verify({
      root: fixture,
      release: false,
      runStep: fakeStep,
      output: sink,
      logDirectory: isolatedLogs,
    })

    assert.equal(result.exitCode, 0)
    const runDirs = fs.readdirSync(isolatedLogs).filter((name) => name !== 'latest')
    assert.ok(runDirs.length >= 1, 'must have at least one run directory in isolated logDirectory')
    const latestLink = path.join(isolatedLogs, 'latest')
    assert.ok(fs.existsSync(latestLink), 'latest link must exist in isolated logDirectory')
    assert.equal(fs.readlinkSync(latestLink), runDirs[0])

    // Verify fixture root has no .fable-build directory created
    assert.equal(fs.existsSync(path.join(fixture, '.fable-build')), false, 'fixture root must have no .fable-build')
  } finally {
    fs.rmSync(fixture, { recursive: true, force: true })
    fs.rmSync(isolatedLogs, { recursive: true, force: true })
  }
})

test('WHAT[verification-system-016] a detected step-boundary mutation prevents later steps', async () => {
  const fixture = setupFixtureRepo()
  const tmpLogs = fs.mkdtempSync(path.join(os.tmpdir(), 'verify-step-interrupt-logs-'))
  let buf = ''
  const sink = { write(chunk) { buf += chunk } }
  const executedSteps = []

  try {
    const mutatingRunStep = async ({ label }) => {
      executedSteps.push(label)
      if (label === 'check') {
        fs.writeFileSync(path.join(fixture, 'src/Foo.fs'), 'module Foo\nlet x = 999\n')
      }
      return { label, ok: true, exitCode: 0, signal: null, durationMs: 2 }
    }

    const result = await verify({
      root: fixture,
      release: false,
      runStep: mutatingRunStep,
      output: sink,
      logDirectory: tmpLogs,
    })

    assert.equal(result.exitCode, 1, 'must exit with non-zero status')
    assert.equal(result.outcome, 'fail', 'outcome must be fail')
    assert.ok(result.inputChanges, 'inputChanges must be recorded')
    assert.equal(result.inputChanges.equal, false)

    // Planned steps for daily: format:check, check, build, unit, integration
    // When check mutates input, subsequent steps build, unit, integration must NOT be executed!
    assert.deepEqual(executedSteps, ['format:check', 'check'], 'steps after check must not be executed')

    const stepStatuses = Object.fromEntries(result.steps.map((s) => [s.label, s.status]))
    assert.equal(stepStatuses['format:check'], 'ok')
    assert.equal(stepStatuses['check'], 'ok')
    assert.equal(stepStatuses['build'], 'not-run', 'subsequent step build must be not-run')
    assert.equal(stepStatuses['unit'], 'not-run', 'subsequent step unit must be not-run')
    assert.equal(stepStatuses['integration'], 'not-run', 'subsequent step integration must be not-run')
  } finally {
    fs.rmSync(fixture, { recursive: true, force: true })
    fs.rmSync(tmpLogs, { recursive: true, force: true })
  }
})

test('WHAT[verification-system-016] a mutation restored within one step still invalidates the run', {
  todo: 'D2: fixed isolated inputs selected; current runner still uses mutable inputs and step-boundary hashes miss this counterexample',
}, async () => {
  const fixture = setupFixtureRepo()
  try {
    const target = path.join(fixture, 'src/Foo.fs')
    const original = fs.readFileSync(target)
    const result = await verify({
      root: fixture,
      output: { write() {} },
      runStep: async ({ label }) => {
        if (label === 'check') {
          fs.writeFileSync(target, 'module Foo\nlet x = 999\n')
          fs.writeFileSync(target, original)
        }
        return { label, ok: true, exitCode: 0, signal: null, durationMs: 1 }
      },
    })
    assert.equal(result.exitCode, 1, 'changing and restoring an input is not a stable verification run')
    assert.equal(result.outcome, 'fail')
    await assertParentReplacementInvalidatesVerification({ verify, root: fixture })
  } finally {
    fs.rmSync(fixture, { recursive: true, force: true })
  }
})
