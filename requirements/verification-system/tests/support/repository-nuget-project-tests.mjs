import assert from 'node:assert/strict'
import { createHash } from 'node:crypto'
import { execFileSync } from 'node:child_process'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { create } from 'tar'
import { prepareGitSourceCandidate } from '../../../../scripts/lib/verification-source-candidate.mjs'
import { prepareVerificationDotnetSdk } from '../../../../scripts/lib/verification-dotnet-sdk.mjs'

const repositoryRoot = path.resolve(import.meta.dirname, '../../../..')
const selectedProjectPath = 'src/Wanxiangshu/Wanxiangshu.Owner.dispatch-protocol.foundation-identity.fsproj'
const expectedPackages = [
  { id: 'fable.core', name: 'Fable.Core', version: '5.0.0', contentHash: 'pJOXJjfhjvNa1cgtF+klgR3DxQI7x9D/fFzZbjUBZLBJyYIioPsTFsKUnIOqNdCq1Oby9lINUSwnL4TqspMQOA==' },
  { id: 'fsharp.core', name: 'FSharp.Core', version: '10.1.203', contentHash: 'I2MFk72yHqfgI594gKFigq8PVIso0Mh1ZUgG9R4n8gyYQ4IwpBxmvecfjnKA7iMLdVHYULB70PsLwYW/K76gew==' },
  { id: 'fstoolkit.errorhandling', name: 'FsToolkit.ErrorHandling', version: '5.2.0', contentHash: 'Y7NfN1gDJ5eOANmi8/aEjGHTWrJOYyqwmgtdA3BIZggd0Ph0IIsbewtgNaLkDYFGgkKw6mGlk0iw4RZ2ZRKZhA==' },
  { id: 'thoth.json', name: 'Thoth.Json', version: '10.5.1', contentHash: 'HMh+zRyOhz7ASGNLgcSQFsrZWv4mcMZfqIsya0aQ2ixFWZpav9RRvYR5C9tL33qmRDX4n+73Bpo6brSfUQ4nQA==' },
]
const hash = (algorithm, bytes, encoding = 'hex') => createHash(algorithm).update(bytes).digest(encoding)

function selectedArchives() {
  assert.ok(process.env.WXS_VERIFICATION_NUGET_PROJECT_PACKAGES_JSON, 'Project restore requires explicit NuGet archive paths and raw SHA512 identities')
  const archives = JSON.parse(process.env.WXS_VERIFICATION_NUGET_PROJECT_PACKAGES_JSON)
  assert.ok(Array.isArray(archives))
  assert.deepEqual(archives.map(({ id, version }) => `${id}/${version}`).sort(), expectedPackages.map(({ id, version }) => `${id}/${version}`))
  return archives.map(({ id, version, archivePath, sha512 }) => {
    assert.ok(path.isAbsolute(archivePath))
    assert.ok(fs.lstatSync(archivePath).isFile())
    const bytes = fs.readFileSync(archivePath)
    assert.equal(hash('sha512', bytes, 'base64'), sha512)
    return { id, version, archivePath, sha512, bytes }
  })
}

function actualInventory(root, source = false) {
  const entries = []
  function visit(relative) {
    const absolute = path.join(root, relative)
    const stat = fs.lstatSync(absolute)
    if (stat.isDirectory()) {
      entries.push({ path: relative, type: 'Directory', mode: stat.mode & 0o777 })
      for (const name of fs.readdirSync(absolute).sort()) {
        if (source && relative === '.' && name === '.git') continue
        visit(relative === '.' ? name : `${relative}/${name}`)
      }
    } else {
      assert.ok(stat.isFile(), `Restore must not introduce nonordinary members: ${relative}`)
      const bytes = fs.readFileSync(absolute)
      entries.push({ path: relative, type: 'File', mode: stat.mode & 0o777, size: bytes.length, sha256: hash('sha256', bytes) })
    }
  }
  visit('.')
  return entries
}

function ownedFile(root, relative) {
  assert.ok(typeof relative === 'string' && relative.length > 0)
  assert.equal(path.isAbsolute(relative), false)
  assert.ok(relative.split('/').every(segment => segment && segment !== '.' && segment !== '..' && !segment.includes('\\')))
  const absolute = path.join(root, relative)
  assert.ok(fs.lstatSync(absolute).isFile())
  assert.equal(fs.realpathSync(absolute), absolute)
  return absolute
}

export async function repositoryNugetProjectTest(t) {
  const allocatedRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'verification-repository-nuget-project-'))
  const root = fs.realpathSync(allocatedRoot)
  const parentDirectory = path.join(root, 'candidates')
  const selectedRoot = path.join(root, 'selected')
  const archiveRoot = path.join(root, 'archives')
  let source
  let sdk
  let project
  let originalInventory
  let originalInputs
  let originalArchives
  let packageArchives
  let sdkArchivePath
  let prepared = false
  let restored = false
  let actionFailure
  const assertSource = () => {
    assert.deepEqual(actualInventory(source.sourceRoot, true), originalInventory, 'Restore must preserve the entire selected source inventory, bytes, and modes')
    for (const [relative, bytes] of originalInputs) assert.deepEqual(fs.readFileSync(path.join(source.sourceRoot, relative)), bytes)
    source.revalidate()
  }
  const assertArchives = () => {
    for (const selected of originalArchives) assert.deepEqual(fs.readFileSync(selected.archivePath), selected.bytes, 'Caller-selected archives must remain unchanged')
  }
  try {
    fs.mkdirSync(parentDirectory)
    fs.mkdirSync(selectedRoot)
    fs.mkdirSync(archiveRoot)
    await t.test('WHAT[verification-system-016] project restore captures an explicit complete Git tree, selected SDK, and four package archive identities', async () => {
      const treeId = process.env.WXS_VERIFICATION_NUGET_PROJECT_TREE_ID
      assert.match(treeId ?? '', /^(?:[0-9a-f]{40}|[0-9a-f]{64})$/, 'An explicit immutable source tree must be selected independently of the current dirty workspace')
      assert.ok(process.env.WXS_VERIFICATION_DOTNET_ROOT, 'The complete SDK installation must be selected explicitly')
      source = prepareGitSourceCandidate({ repositoryRoot, treeId, parentDirectory })
      assert.equal(source.treeId, treeId)
      originalInventory = actualInventory(source.sourceRoot, true)
      assert.deepEqual(originalInventory.filter(entry => entry.type === 'File').map(({ path: file, mode, size, sha256 }) => ({ path: file, mode, size, sha256 })).sort((a, b) => a.path.localeCompare(b.path, 'en')), source.entries.map(({ path: file, mode, size, sha256 }) => ({ path: file, mode: mode === '100755' ? 0o755 : 0o644, size, sha256 })).sort((a, b) => a.path.localeCompare(b.path, 'en')))
      originalInputs = ['global.json', 'Directory.Build.props', 'src/Wanxiangshu/Directory.Build.props', selectedProjectPath].map(relative => [relative, fs.readFileSync(path.join(source.sourceRoot, relative))])
      originalArchives = selectedArchives()
      packageArchives = originalArchives.map(({ id, version, sha512, bytes }) => {
        const archivePath = path.join(archiveRoot, `${id}.${version}.nupkg`)
        fs.writeFileSync(archivePath, bytes, { flag: 'wx' })
        return { id, version, archivePath, sha512 }
      })
      const sdkDirectory = fs.realpathSync(process.env.WXS_VERIFICATION_DOTNET_ROOT)
      fs.cpSync(sdkDirectory, path.join(selectedRoot, 'dotnet-sdk'), { recursive: true, verbatimSymlinks: true })
      const sdkVersion = execFileSync(path.join(selectedRoot, 'dotnet-sdk/dotnet'), ['--version'], { encoding: 'utf8' }).trim()
      sdkArchivePath = path.join(archiveRoot, 'sdk.tar')
      await create({ cwd: selectedRoot, file: sdkArchivePath, portable: true, noMtime: true }, ['dotnet-sdk'])
      sdk = await prepareVerificationDotnetSdk({ sourceRoot: source.sourceRoot, parentDirectory, archivePath: sdkArchivePath, archiveSha256: hash('sha256', fs.readFileSync(sdkArchivePath)), expectedSdkVersion: sdkVersion, signal: t.signal })
      fs.rmSync(selectedRoot, { recursive: true, force: true })
      assertSource()
      assertArchives()
      t.diagnostic(JSON.stringify({ treeId, sourceDigest: source.sourceDigest, sdkDigest: sdk.sdkDigest, sdkArchiveSha256: sdk.archiveSha256, projectPath: selectedProjectPath, packages: packageArchives.map(({ id, version, sha512 }) => ({ id, version, sha512 })), provenance: 'explicit caller-selected archive bytes; this fixture does not authenticate the publisher' }))
      prepared = true
    })
    if (!prepared) return
    await t.test('WHAT[verification-system-016] actual original project restore binds the four-package assets and derived lock in separate owned outputs', async () => {
      const { prepareVerificationNugetProject } = await import('../../../../scripts/lib/verification-nuget-project.mjs')
      project = await prepareVerificationNugetProject({ source, sdk, projectPath: selectedProjectPath, packageArchives, parentDirectory, signal: t.signal })
      assert.equal(project.identityScope, 'selected-nuget-project-restore')
      assert.equal(project.projectPath, selectedProjectPath)
      assert.equal(project.targetFramework, 'net10.0')
      assert.equal(project.treeId, source.treeId)
      assert.equal(project.sourceDigest, source.sourceDigest)
      assert.equal(project.sdkDigest, sdk.sdkDigest)
      assert.equal(fs.realpathSync(project.projectRoot), project.projectRoot)
      assert.equal(path.dirname(project.projectRoot), parentDirectory)
      const expectedAssetsPath = 'artifacts/obj/Wanxiangshu.Owner.dispatch-protocol.foundation-identity/project.assets.json'
      assert.equal(project.assetsPath, expectedAssetsPath)
      assert.equal(project.lockPath, 'artifacts/packages.lock.json')
      const assets = JSON.parse(fs.readFileSync(ownedFile(project.projectRoot, project.assetsPath), 'utf8'))
      const lock = JSON.parse(fs.readFileSync(ownedFile(project.projectRoot, project.lockPath), 'utf8'))
      const expectedIdentities = expectedPackages.map(({ name, version }) => `${name}/${version}`).sort()
      assert.deepEqual(Object.keys(assets.targets), ['net10.0'])
      assert.deepEqual(Object.keys(assets.libraries).sort(), expectedIdentities)
      assert.deepEqual(Object.keys(assets.targets['net10.0']).sort(), expectedIdentities)
      assert.deepEqual(Object.keys(lock.dependencies), ['net10.0'])
      assert.deepEqual(Object.keys(lock.dependencies['net10.0']).sort(), expectedPackages.map(({ name }) => name).sort())
      assert.deepEqual(Object.keys(assets.project.restore.sources), [path.join(project.projectRoot, 'feed')])
      assert.deepEqual(Object.keys(assets.packageFolders), [path.join(project.projectRoot, 'packages')])
      assert.deepEqual(assets.project.restore.configFilePaths, [path.join(project.projectRoot, 'NuGet.Config')])
      assert.deepEqual(assets.project.restore.fallbackFolders ?? [], [])
      assert.equal(assets.project.restore.projectPath, path.join(source.sourceRoot, selectedProjectPath))
      assert.deepEqual(assets.project.restore.frameworks['net10.0'].projectReferences, {})
      const actualCacheIdentities = fs.readdirSync(path.join(project.projectRoot, 'packages')).sort().flatMap(id => fs.readdirSync(path.join(project.projectRoot, 'packages', id)).sort().map(version => `${id}/${version}`))
      assert.deepEqual(actualCacheIdentities, expectedPackages.map(({ id, version }) => `${id}/${version}`))
      assert.deepEqual(project.packages, packageArchives.map(({ id, version, sha512 }) => ({ id, version, rawSha512: sha512 })).sort((a, b) => `${a.id}/${a.version}`.localeCompare(`${b.id}/${b.version}`, 'en')))
      for (const expected of expectedPackages) {
        const selected = packageArchives.find(({ id }) => id === expected.id)
        const library = assets.libraries[`${expected.name}/${expected.version}`]
        const locked = lock.dependencies['net10.0'][expected.name]
        assert.equal(library.type, 'package')
        assert.equal(library.path, `${expected.id}/${expected.version}`)
        assert.equal(library.sha512, expected.contentHash)
        assert.equal(locked.type, 'Direct')
        assert.equal(locked.resolved, expected.version)
        assert.equal(locked.requested, `[${expected.version}, )`)
        assert.equal(locked.contentHash, expected.contentHash)
        const packageDirectory = `packages/${expected.id}/${expected.version}`
        const cacheArchive = ownedFile(project.projectRoot, `${packageDirectory}/${expected.id}.${expected.version}.nupkg`)
        assert.equal(hash('sha512', fs.readFileSync(cacheArchive), 'base64'), selected.sha512)
        assert.equal(fs.readFileSync(ownedFile(project.projectRoot, `${packageDirectory}/${expected.id}.${expected.version}.nupkg.sha512`), 'utf8').trim(), selected.sha512)
        const metadata = JSON.parse(fs.readFileSync(ownedFile(project.projectRoot, `${packageDirectory}/.nupkg.metadata`), 'utf8'))
        assert.equal(metadata.contentHash, expected.contentHash)
        assert.equal(metadata.source, path.join(project.projectRoot, 'feed'))
        assert.notEqual(selected.sha512, expected.contentHash, 'Raw signed archive SHA512 and NuGet contentHash are distinct identities')
        for (const kind of ['compile', 'runtime']) {
          const target = assets.targets['net10.0'][`${expected.name}/${expected.version}`]
          assert.equal(target.type, 'package')
          assert.ok(Object.keys(target[kind]).length > 0)
          for (const relative of Object.keys(target[kind])) ownedFile(project.projectRoot, `${packageDirectory}/${relative}`)
        }
      }
      assert.deepEqual(project.graph, { targetFramework: 'net10.0', targets: assets.targets, libraries: assets.libraries, dependencies: lock.dependencies })
      assert.equal(project.graphDigest, hash('sha256', JSON.stringify(project.graph)))
      assert.deepEqual(project.entries, actualInventory(project.projectRoot))
      assert.equal(project.entriesDigest, hash('sha256', JSON.stringify(project.entries)))
      assert.equal(project.projectDigest, hash('sha256', JSON.stringify({ sourceDigest: project.sourceDigest, treeId: project.treeId, sdkDigest: project.sdkDigest, projectPath: project.projectPath, packages: project.packages, graphDigest: project.graphDigest, entriesDigest: project.entriesDigest, identityScope: project.identityScope })))
      assert.deepEqual(fs.readdirSync(parentDirectory).sort(), [source.sourceRoot, sdk.toolRoot, project.projectRoot].map(directory => path.basename(directory)).sort())
      project.revalidate()
      assertSource()
      assertArchives()
      sdk.revalidate()
      t.diagnostic(JSON.stringify({ projectDigest: project.projectDigest, graphDigest: project.graphDigest, entries: project.entries.length, packages: project.packages, contentHashes: expectedPackages.map(({ id, version, contentHash }) => ({ id, version, contentHash })), derivedLock: true, sourceLockProvided: false, fableCompilation: false, readOnlyMounted: false, actualVerifyBound: false }))
      restored = true
    })
    if (!restored) return
    await t.test('WHAT[verification-system-016] project receipt survives removal of fixture archives and disposes outputs without owning source or SDK', () => {
      for (const selected of packageArchives) fs.rmSync(selected.archivePath)
      fs.rmSync(sdkArchivePath)
      assert.deepEqual(fs.readdirSync(archiveRoot), [])
      project.revalidate()
      assertSource()
      assertArchives()
      sdk.revalidate()
      project.dispose()
      assert.equal(fs.existsSync(project.projectRoot), false)
      assert.equal(fs.existsSync(source.sourceRoot), true)
      assert.equal(fs.existsSync(sdk.toolRoot), true)
      assertSource()
      sdk.revalidate()
      assert.deepEqual(fs.readdirSync(parentDirectory).sort(), [source.sourceRoot, sdk.toolRoot].map(directory => path.basename(directory)).sort())
      sdk.dispose()
      assertSource()
      source.dispose()
      assert.deepEqual(fs.readdirSync(parentDirectory), [])
    })
  } catch (error) {
    actionFailure = { error }
    throw error
  } finally {
    const failures = []
    for (const candidate of [project, sdk, source]) {
      try { candidate?.dispose() } catch (error) { failures.push(error) }
    }
    try { assert.deepEqual(fs.readdirSync(parentDirectory), []) } catch (error) { failures.push(error) }
    try { fs.rmSync(allocatedRoot, { recursive: true, force: true }) } catch (error) { failures.push(error) }
    if (failures.length) throw new AggregateError(actionFailure ? [actionFailure.error, ...failures] : failures, 'Repository NuGet project cleanup failed', { cause: actionFailure ? actionFailure.error : failures[0] })
  }
}
