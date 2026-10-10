import assert from 'node:assert/strict'
import { createHash } from 'node:crypto'
import { execFileSync } from 'node:child_process'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { stripVTControlCharacters } from 'node:util'
import { create } from 'tar'
import { prepareVerificationDotnetSdk } from '../../../../scripts/lib/verification-dotnet-sdk.mjs'
import { prepareVerificationDotnetTools } from '../../../../scripts/lib/verification-dotnet-tools.mjs'
import { runVerificationToolProbe } from '../../../../scripts/lib/verification-tool-probe.mjs'

const repositoryRoot = path.resolve(import.meta.dirname, '../../../..')
const hash = (algorithm, bytes, encoding = 'hex') => createHash(algorithm).update(bytes).digest(encoding)

function selectedArchives(manifest) {
  assert.ok(process.env.WXS_VERIFICATION_DOTNET_TOOLS_PACKAGES_JSON, 'WXS_VERIFICATION_DOTNET_TOOLS_PACKAGES_JSON must explicitly identify the selected NuGet archives and SHA512 bytes')
  const archives = JSON.parse(process.env.WXS_VERIFICATION_DOTNET_TOOLS_PACKAGES_JSON)
  assert.ok(Array.isArray(archives))
  const expected = Object.entries(manifest.tools).map(([id, tool]) => `${id}/${tool.version}`).sort()
  assert.deepEqual(archives.map(entry => `${entry.id}/${entry.version}`).sort(), expected)
  return archives.map(({ id, version, archivePath, sha512 }) => {
    assert.ok(path.isAbsolute(archivePath), 'The external package archive must be selected by an explicit absolute path')
    assert.ok(fs.lstatSync(archivePath).isFile())
    const bytes = fs.readFileSync(archivePath)
    assert.equal(hash('sha512', bytes, 'base64'), sha512)
    return { id, version, archivePath, sha512, bytes }
  })
}

function actualInventory(root) {
  const entries = []
  function visit(relative) {
    const file = path.join(root, relative)
    const stat = fs.lstatSync(file)
    if (stat.isDirectory()) {
      entries.push({ path: relative, type: 'Directory', mode: stat.mode & 0o777 })
      for (const name of fs.readdirSync(file).sort()) visit(relative === '.' ? name : `${relative}/${name}`)
    } else {
      assert.ok(stat.isFile(), `Unexpected restored member: ${relative}`)
      const bytes = fs.readFileSync(file)
      entries.push({ path: relative, type: 'File', mode: stat.mode & 0o777, size: bytes.length, sha256: hash('sha256', bytes) })
    }
  }
  visit('.')
  return entries
}

function consumerEnvironment(root, sdkRoot, executable) {
  const roles = ['home', 'cli', 'config', 'cache', 'data', 'state', 'tmp', 'packages', 'nuget-scratch', 'nuget-plugins']
  for (const role of roles) assert.ok(fs.lstatSync(path.join(root, role)).isDirectory())
  return {
    CI: 'true',
    HOME: path.join(root, 'home'),
    DOTNET_CLI_HOME: path.join(root, 'cli'),
    DOTNET_ROOT: sdkRoot,
    DOTNET_HOST_PATH: executable,
    DOTNET_CLI_UI_LANGUAGE: 'en-US',
    DOTNET_CLI_TELEMETRY_OPTOUT: '1',
    DOTNET_NOLOGO: '1',
    DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE: 'true',
    XDG_CONFIG_HOME: path.join(root, 'config'),
    XDG_CACHE_HOME: path.join(root, 'cache'),
    XDG_DATA_HOME: path.join(root, 'data'),
    XDG_STATE_HOME: path.join(root, 'state'),
    TMPDIR: path.join(root, 'tmp'),
    TMP: path.join(root, 'tmp'),
    TEMP: path.join(root, 'tmp'),
    NUGET_PACKAGES: path.join(root, 'packages'),
    NUGET_HTTP_CACHE_PATH: path.join(root, 'cache'),
    NUGET_SCRATCH: path.join(root, 'nuget-scratch'),
    NUGET_PLUGINS_CACHE_PATH: path.join(root, 'nuget-plugins'),
    PATH: sdkRoot,
    LANG: 'C',
    LC_ALL: 'C',
  }
}

export async function repositoryDotnetToolsTest(t) {
  const allocatedRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'verification-repository-dotnet-tools-'))
  const root = fs.realpathSync(allocatedRoot)
  const sourceRoot = path.join(root, 'source')
  const parentDirectory = path.join(root, 'candidates')
  const selectedRoot = path.join(root, 'selected')
  const archiveRoot = path.join(root, 'archives')
  let sdk
  let tools
  let sourceArchives
  let packageArchives
  let sdkArchivePath
  let globalBytes
  let manifestBytes
  let captured = false
  let restored = false
  let actionFailure
  const assertInputs = () => {
    assert.deepEqual(fs.readFileSync(path.join(sourceRoot, 'global.json')), globalBytes)
    assert.deepEqual(fs.readFileSync(path.join(sourceRoot, '.config', 'dotnet-tools.json')), manifestBytes)
    for (const selected of sourceArchives) assert.deepEqual(fs.readFileSync(selected.archivePath), selected.bytes, 'The original caller-selected package archive remains unchanged')
  }
  try {
    fs.mkdirSync(path.join(sourceRoot, '.config'), { recursive: true })
    fs.mkdirSync(parentDirectory)
    fs.mkdirSync(selectedRoot)
    fs.mkdirSync(archiveRoot)
    await t.test('WHAT[verification-system-016] the repository tool manifest and complete SDK use explicit selected archive identities', async () => {
      assert.ok(process.env.WXS_VERIFICATION_DOTNET_ROOT, 'WXS_VERIFICATION_DOTNET_ROOT must explicitly select the complete SDK installation')
      globalBytes = fs.readFileSync(path.join(repositoryRoot, 'global.json'))
      manifestBytes = fs.readFileSync(path.join(repositoryRoot, '.config', 'dotnet-tools.json'))
      const manifest = JSON.parse(manifestBytes)
      assert.deepEqual(manifest.tools, {
        fantomas: { version: '6.3.0', commands: ['fantomas'], rollForward: false },
        fable: { version: '5.13.0', commands: ['fable'], rollForward: false },
      })
      sourceArchives = selectedArchives(manifest)
      fs.writeFileSync(path.join(sourceRoot, 'global.json'), globalBytes, { flag: 'wx' })
      fs.writeFileSync(path.join(sourceRoot, '.config', 'dotnet-tools.json'), manifestBytes, { flag: 'wx' })
      packageArchives = sourceArchives.map(({ id, version, sha512, bytes }) => {
        const archivePath = path.join(archiveRoot, `${id}.${version}.nupkg`)
        fs.writeFileSync(archivePath, bytes, { flag: 'wx' })
        return { id, version, archivePath, sha512 }
      })
      const selectedSdk = fs.realpathSync(process.env.WXS_VERIFICATION_DOTNET_ROOT)
      fs.cpSync(selectedSdk, path.join(selectedRoot, 'dotnet-sdk'), { recursive: true, verbatimSymlinks: true })
      const sdkVersion = execFileSync(path.join(selectedRoot, 'dotnet-sdk/dotnet'), ['--version'], { encoding: 'utf8' }).trim()
      sdkArchivePath = path.join(archiveRoot, 'sdk.tar')
      await create({ cwd: selectedRoot, file: sdkArchivePath, portable: true, noMtime: true }, ['dotnet-sdk'])
      sdk = await prepareVerificationDotnetSdk({ sourceRoot, parentDirectory, archivePath: sdkArchivePath, archiveSha256: hash('sha256', fs.readFileSync(sdkArchivePath)), expectedSdkVersion: sdkVersion, signal: t.signal })
      fs.rmSync(selectedRoot, { recursive: true, force: true })
      assert.equal(sdk.globalJsonSha256, hash('sha256', globalBytes))
      assert.equal(sdk.sdk.version, sdkVersion)
      assert.ok(sdk.entries.some(entry => entry.path.startsWith('dotnet-sdk/packs/')))
      assertInputs()
      t.diagnostic(JSON.stringify({ sdkDigest: sdk.sdkDigest, sdkArchiveSha256: sdk.archiveSha256, toolManifestSha256: hash('sha256', manifestBytes), packages: packageArchives.map(({ id, version, sha512 }) => ({ id, version, sha512 })), provenance: 'explicit caller-selected archive bytes; this test does not authenticate the publisher' }))
      captured = true
    })
    if (!captured) return
    await t.test('WHAT[verification-system-016] the selected SDK rejects a public root redirected to an existing foreign directory', () => {
      const capturedSdkRoot = sdk.toolRoot
      const foreignRoot = path.join(root, 'foreign-sdk')
      fs.mkdirSync(foreignRoot)
      const marker = path.join(foreignRoot, 'caller-owned.txt')
      fs.writeFileSync(marker, 'external root is not selected SDK evidence\n', { flag: 'wx' })
      try {
        sdk.toolRoot = foreignRoot
        assert.throws(() => sdk.revalidate(), { code: 'verification-dotnet-sdk-entry-invalid' })
      } finally {
        sdk.toolRoot = capturedSdkRoot
      }
      assert.equal(fs.readFileSync(marker, 'utf8'), 'external root is not selected SDK evidence\n')
      assert.equal(fs.existsSync(capturedSdkRoot), true)
      sdk.revalidate()
      assert.deepEqual(fs.readdirSync(parentDirectory), [path.basename(capturedSdkRoot)])
      assertInputs()
    })
    await t.test('WHAT[verification-system-016] actual private NuGet tool restore binds the complete package inventory and owned resolver paths', async () => {
      tools = await prepareVerificationDotnetTools({ sdk, sourceRoot, packageArchives, parentDirectory, signal: t.signal })
      assert.equal(tools.identityScope, 'selected-dotnet-tool-restore')
      assert.equal(tools.sdkDigest, sdk.sdkDigest)
      assert.equal(tools.globalJsonSha256, hash('sha256', globalBytes))
      assert.equal(tools.toolManifestSha256, hash('sha256', manifestBytes))
      assert.deepEqual(tools.packages, packageArchives.map(({ id, version, sha512 }) => ({ id, version, sha512 })).sort((a, b) => `${a.id}/${a.version}`.localeCompare(`${b.id}/${b.version}`, 'en')))
      assert.deepEqual(tools.entries, actualInventory(tools.toolRoot))
      assert.equal(tools.entriesDigest, hash('sha256', JSON.stringify(tools.entries)))
      assert.equal(tools.toolsDigest, hash('sha256', JSON.stringify({ sdkDigest: tools.sdkDigest, globalJsonSha256: tools.globalJsonSha256, toolManifestSha256: tools.toolManifestSha256, packages: tools.packages, entriesDigest: tools.entriesDigest, identityScope: tools.identityScope })))
      assert.deepEqual(tools.tools.map(({ id, version, command }) => ({ id, version, command })), [{ id: 'fable', version: '5.13.0', command: 'fable' }, { id: 'fantomas', version: '6.3.0', command: 'fantomas' }])
      for (const tool of tools.tools) {
        const resolverPath = path.join(tools.toolRoot, 'cli', '.dotnet', 'toolResolverCache', '1', tool.command)
        const [resolver] = JSON.parse(fs.readFileSync(resolverPath, 'utf8'))
        assert.equal(resolver.Name, tool.command)
        assert.equal(resolver.Version, tool.version)
        assert.equal(resolver.Runner, 'dotnet')
        assert.equal(resolver.PathToExecutable, path.join(tools.toolRoot, tool.executable))
        assert.ok(fs.lstatSync(resolver.PathToExecutable).isFile())
        const packagePath = path.join(tools.toolRoot, 'packages', tool.id, tool.version, `${tool.id}.${tool.version}.nupkg`)
        assert.equal(hash('sha512', fs.readFileSync(packagePath), 'base64'), tools.packages.find(pkg => pkg.id === tool.id).sha512)
      }
      assert.deepEqual(fs.readdirSync(parentDirectory).sort(), [sdk.toolRoot, tools.toolRoot].map(directory => path.basename(directory)).sort())
      sdk.revalidate()
      tools.revalidate()
      assertInputs()
      t.diagnostic(JSON.stringify({ toolsDigest: tools.toolsDigest, entries: tools.entries.length, tools: tools.tools, projectRestore: false, fableCompilation: false, readOnlyMounted: false, actualVerifyBound: false }))
      restored = true
    })
    if (!restored) return
    for (const replacement of ['root', 'parent-with-missing-root']) {
      await t.test(`WHAT[verification-system-016] actually restored tools reject ${replacement} namespace substitution and preserve foreign data`, () => {
        const target = replacement === 'root' ? tools.toolRoot : parentDirectory
        const parked = path.join(root, 'parked-tools-namespace')
        fs.renameSync(target, parked)
        try {
          if (replacement === 'root') {
            fs.cpSync(parked, target, { recursive: true, preserveTimestamps: true })
            for (const entry of tools.entries) fs.chmodSync(path.join(target, entry.path), entry.mode)
          } else fs.mkdirSync(target)
          if (replacement === 'root') assert.deepEqual(actualInventory(target), tools.entries)
          assert.throws(() => tools.revalidate(), { code: 'verification-dotnet-tools-entry-invalid' })
          const marker = path.join(target, 'foreign-marker')
          fs.writeFileSync(marker, 'foreign namespace must survive')
          assert.throws(() => tools.dispose(), { code: 'verification-dotnet-tools-entry-invalid' })
          assert.equal(fs.readFileSync(marker, 'utf8'), 'foreign namespace must survive')
        } finally {
          fs.rmSync(target, { recursive: true, force: true })
          fs.renameSync(parked, target)
        }
        tools.revalidate()
        sdk.revalidate()
        assertInputs()
      })
    }
    await t.test('WHAT[verification-system-016] real restored Fable and Fantomas execute after external archives are removed and release only their owned roots', async () => {
      for (const selected of packageArchives) fs.rmSync(selected.archivePath)
      fs.rmSync(sdkArchivePath)
      assert.deepEqual(fs.readdirSync(archiveRoot), [])
      sdk.revalidate()
      tools.revalidate()
      const executable = path.join(sdk.toolRoot, sdk.dotnet.path)
      const env = consumerEnvironment(tools.toolRoot, path.join(sdk.toolRoot, 'dotnet-sdk'), executable)
      for (const tool of tools.tools) {
        const output = stripVTControlCharacters(await runVerificationToolProbe(executable, [path.join(tools.toolRoot, tool.executable), '--version'], { cwd: tools.toolRoot, env, signal: t.signal })).trim()
        if (tool.command === 'fable') assert.equal(output, '5.13.0')
        else assert.match(output, /^Fantomas v6\.3\.0\+[0-9a-f]+$/)
        t.diagnostic(JSON.stringify({ command: tool.command, actualVersionOutput: output, selectedExecutable: tool.executable }))
      }
      tools.revalidate()
      assertInputs()
      tools.dispose()
      assert.equal(fs.existsSync(tools.toolRoot), false)
      assert.equal(fs.existsSync(sdk.toolRoot), true, 'Tool disposal must retain the caller-owned SDK')
      sdk.revalidate()
      assert.deepEqual(fs.readdirSync(parentDirectory), [path.basename(sdk.toolRoot)])
      sdk.dispose()
      assert.deepEqual(fs.readdirSync(parentDirectory), [])
    })
  } catch (error) {
    actionFailure = { error }
    throw error
  } finally {
    const failures = []
    for (const candidate of [tools, sdk]) {
      try { candidate?.dispose() } catch (error) { failures.push(error) }
    }
    try { assert.deepEqual(fs.readdirSync(parentDirectory), []) } catch (error) { failures.push(error) }
    try { fs.rmSync(allocatedRoot, { recursive: true, force: true }) } catch (error) { failures.push(error) }
    if (failures.length) throw new AggregateError(actionFailure ? [actionFailure.error, ...failures] : failures, 'Repository tool fixture cleanup failed', { cause: actionFailure ? actionFailure.error : failures[0] })
  }
}
