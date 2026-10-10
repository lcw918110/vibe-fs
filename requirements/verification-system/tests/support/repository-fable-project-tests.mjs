import assert from 'node:assert/strict'
import { createHash } from 'node:crypto'
import { execFileSync } from 'node:child_process'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { pathToFileURL } from 'node:url'
import { create } from 'tar'
import { prepareGitSourceCandidate } from '../../../../scripts/lib/verification-source-candidate.mjs'
import { prepareVerificationDotnetSdk } from '../../../../scripts/lib/verification-dotnet-sdk.mjs'
import { prepareVerificationDotnetTools } from '../../../../scripts/lib/verification-dotnet-tools.mjs'
import { prepareVerificationNugetProject } from '../../../../scripts/lib/verification-nuget-project.mjs'
import { runVerificationToolProbe } from '../../../../scripts/lib/verification-tool-probe.mjs'
import { consumeReadonlyVerificationInputs } from '../../../../scripts/lib/verification-readonly-inputs.mjs'

const repositoryRoot = path.resolve(import.meta.dirname, '../../../..')
const projectPath = 'src/Wanxiangshu/Wanxiangshu.Owner.dispatch-protocol.foundation-identity.fsproj'
const hash = (algorithm, bytes, encoding = 'hex') => createHash(algorithm).update(bytes).digest(encoding)

function selectedArchives(variable, identities) {
  assert.ok(process.env[variable], `${variable} must explicitly identify selected archives and raw SHA512 bytes`)
  const selected = JSON.parse(process.env[variable])
  assert.ok(Array.isArray(selected))
  assert.deepEqual(selected.map(({ id, version }) => `${id}/${version}`).sort(), [...identities].sort())
  return selected.map(({ id, version, archivePath, sha512 }) => {
    assert.ok(path.isAbsolute(archivePath))
    assert.ok(fs.lstatSync(archivePath).isFile())
    const bytes = fs.readFileSync(archivePath)
    assert.equal(hash('sha512', bytes, 'base64'), sha512)
    return { id, version, archivePath, sha512, bytes }
  })
}

function actualInventory(root, sdkLinks = false) {
  const entries = []
  function visit(relative) {
    const absolute = path.join(root, relative)
    const stat = fs.lstatSync(absolute)
    if (stat.isDirectory()) {
      entries.push({ path: relative, type: 'Directory', mode: stat.mode & 0o777 })
      for (const name of fs.readdirSync(absolute).sort()) visit(relative === '.' ? name : `${relative}/${name}`)
    } else if (sdkLinks && stat.isSymbolicLink()) {
      const resolved = fs.realpathSync(absolute)
      assert.ok(resolved.startsWith(root + path.sep), `SDK links must remain inside the selected bundle: ${relative}`)
      entries.push({ path: relative, type: 'SymbolicLink', mode: stat.mode & 0o777, target: fs.readlinkSync(absolute) })
    } else {
      assert.ok(stat.isFile(), `The compile inventory must contain ordinary members: ${relative}`)
      const bytes = fs.readFileSync(absolute)
      entries.push({ path: relative, type: 'File', mode: stat.mode & 0o777, size: bytes.length, sha256: hash('sha256', bytes) })
    }
  }
  visit('.')
  return entries
}

export const repositoryReadonlyFableProjectTest = t => repositoryFableProjectTest(t, { readonly: true })

export async function repositoryFableProjectTest(t, { readonly = false } = {}) {
  const allocatedRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'verification-repository-fable-project-'))
  const root = fs.realpathSync(allocatedRoot)
  const parentDirectory = path.join(root, 'candidates')
  const selectedRoot = path.join(root, 'selected')
  const archiveRoot = path.join(root, 'archives')
  let source
  let sdk
  let tools
  let project
  let compiled
  let originalSource
  let originalSdk
  let originalTools
  let originalProject
  let originalInputs
  let callerArchives
  let toolArchives
  let projectArchives
  let sdkArchivePath
  let sdkExpectedVersion
  let prepared = false
  let restored = false
  let emitted = false
  let actionFailure
  let readonlyInputs
  let readonlyRestored = true
  const assertSource = () => {
    assert.deepEqual(actualInventory(source.sourceRoot), originalSource, 'The complete original Git input, including .git, remains unchanged')
    for (const [relative, bytes] of originalInputs) assert.deepEqual(fs.readFileSync(path.join(source.sourceRoot, relative)), bytes)
    source.revalidate()
  }
  const assertArchives = () => {
    for (const selected of callerArchives) assert.deepEqual(fs.readFileSync(selected.archivePath), selected.bytes)
  }
  const assertOwners = () => {
    if (readonlyInputs) {
      readonlyInputs.revalidate()
      return
    }
    assertSource()
    assert.deepEqual(actualInventory(sdk.toolRoot, true), originalSdk)
    sdk.revalidate()
    assert.deepEqual(actualInventory(tools.toolRoot), originalTools)
    tools.revalidate()
    assert.deepEqual(actualInventory(project.projectRoot), originalProject, 'Fable design-time writes must stay outside the prepared restore owner')
    project.revalidate()
    assertArchives()
  }
  try {
    fs.mkdirSync(parentDirectory)
    fs.mkdirSync(selectedRoot)
    fs.mkdirSync(archiveRoot)
    await t.test('WHAT[verification-system-016] real Fable preparation selects a complete immutable Git tree and SDK archive without rewriting the original project', async () => {
      const treeId = process.env.WXS_VERIFICATION_NUGET_PROJECT_TREE_ID
      assert.match(treeId ?? '', /^(?:[0-9a-f]{40}|[0-9a-f]{64})$/)
      if (!readonly) assert.ok(process.env.WXS_VERIFICATION_DOTNET_ROOT)
      source = prepareGitSourceCandidate({ repositoryRoot, treeId, parentDirectory })
      originalSource = actualInventory(source.sourceRoot)
      originalInputs = ['global.json', 'Directory.Build.props', 'src/Wanxiangshu/Directory.Build.props', projectPath].map(relative => [relative, fs.readFileSync(path.join(source.sourceRoot, relative))])
      const selectedTools = selectedArchives('WXS_VERIFICATION_DOTNET_TOOLS_PACKAGES_JSON', ['fable/5.13.0', 'fantomas/6.3.0'])
      const selectedPackages = selectedArchives('WXS_VERIFICATION_NUGET_PROJECT_PACKAGES_JSON', ['fsharp.core/10.1.203', 'fable.core/5.0.0', 'fstoolkit.errorhandling/5.2.0', 'thoth.json/10.5.1'])
      callerArchives = [...selectedTools, ...selectedPackages]
      const copyArchive = ({ id, version, sha512, bytes }) => {
        const archivePath = path.join(archiveRoot, `${id}.${version}.nupkg`)
        fs.writeFileSync(archivePath, bytes, { flag: 'wx' })
        return { id, version, archivePath, sha512 }
      }
      toolArchives = selectedTools.map(copyArchive)
      projectArchives = selectedPackages.map(copyArchive)
      sdkArchivePath = path.join(archiveRoot, 'sdk.tar')
      let sdkArchiveSha256
      if (readonly) {
        const archive = process.env.WXS_VERIFICATION_READONLY_SDK_ARCHIVE
        const sha256 = process.env.WXS_VERIFICATION_READONLY_SDK_ARCHIVE_SHA256
        assert.ok(typeof archive === 'string' && path.isAbsolute(archive), 'Readonly compilation requires an explicit SDK archive absolute path')
        assert.match(sha256 ?? '', /^[0-9a-f]{64}$/, 'Readonly compilation requires the selected raw SDK archive SHA256')
        assert.ok(fs.lstatSync(archive).isFile())
        assert.equal(hash('sha256', fs.readFileSync(archive)), sha256)
        fs.copyFileSync(archive, sdkArchivePath, fs.constants.COPYFILE_EXCL)
        assert.equal(hash('sha256', fs.readFileSync(sdkArchivePath)), sha256)
        sdkArchiveSha256 = sha256
      } else {
        fs.cpSync(fs.realpathSync(process.env.WXS_VERIFICATION_DOTNET_ROOT), path.join(selectedRoot, 'dotnet-sdk'), { recursive: true, verbatimSymlinks: true })
        const sdkVersion = execFileSync(path.join(selectedRoot, 'dotnet-sdk/dotnet'), ['--version'], { encoding: 'utf8' }).trim()
        await create({ cwd: selectedRoot, file: sdkArchivePath, portable: true, noMtime: true }, ['dotnet-sdk'])
        sdkArchiveSha256 = hash('sha256', fs.readFileSync(sdkArchivePath))
        sdkExpectedVersion = sdkVersion
      }
      sdk = await prepareVerificationDotnetSdk({ sourceRoot: source.sourceRoot, parentDirectory, archivePath: sdkArchivePath, archiveSha256: sdkArchiveSha256, expectedSdkVersion: readonly ? '10.0.302' : sdkExpectedVersion, signal: t.signal })
      fs.rmSync(selectedRoot, { recursive: true, force: true })
      originalSdk = actualInventory(sdk.toolRoot, true)
      assertSource()
      assertArchives()
      t.diagnostic(JSON.stringify({ treeId, sourceDigest: source.sourceDigest, sdkDigest: sdk.sdkDigest, projectPath, provenance: 'explicit selected archive bytes; this fixture does not authenticate their publisher' }))
      prepared = true
    })
    if (!prepared) return
    await t.test('WHAT[verification-system-016] real selected Fable tools and original project restore produce independent prepared input owners', async () => {
      tools = await prepareVerificationDotnetTools({ sourceRoot: source.sourceRoot, sdk, packageArchives: toolArchives, parentDirectory, signal: t.signal })
      originalTools = actualInventory(tools.toolRoot)
      project = await prepareVerificationNugetProject({ source, sdk, projectPath, packageArchives: projectArchives, parentDirectory, signal: t.signal })
      originalProject = actualInventory(project.projectRoot)
      assertOwners()
      assert.deepEqual(fs.readdirSync(parentDirectory).sort(), [source.sourceRoot, sdk.toolRoot, tools.toolRoot, project.projectRoot].map(directory => path.basename(directory)).sort())
      restored = true
    })
    if (!restored) return
    const compileAndConsume = async () => {
      if (readonly) {
        const files = [
          path.join(source.sourceRoot, projectPath),
          path.join(sdk.toolRoot, sdk.dotnet.path),
          path.join(tools.toolRoot, tools.tools.find(tool => tool.command === 'fable').executable),
          path.join(project.projectRoot, 'packages/fable.core/5.0.0/fable.core.5.0.0.nupkg'),
        ]
        for (const file of files) {
          const original = fs.readFileSync(file)
          assert.throws(() => fs.writeFileSync(file, original), { code: 'EROFS' }, `actual compiler input must be read-only: ${file}`)
          assert.throws(() => fs.writeFileSync(path.join(path.dirname(file), 'readonly-new-member'), 'new'), { code: 'EROFS' })
          assert.throws(() => fs.unlinkSync(file), { code: 'EROFS' })
        }
        assert.throws(() => source.revalidate(), /owned identity/, 'the original inode owner must not pretend to validate a mounted namespace')
      }
      await t.test('WHAT[verification-system-016] actual Fable compiles the original project into an owned seed and binds the complete output receipt', async () => {
        const { compileVerificationFableProject } = await import('../../../../scripts/lib/verification-fable-project.mjs')
        const compileParent = readonly ? path.join(root, 'outputs') : parentDirectory
        if (readonly) fs.mkdirSync(compileParent)
        compiled = await compileVerificationFableProject({ source, sdk, tools, project, parentDirectory: compileParent, readonlyInputs, signal: t.signal })
        assert.equal(compiled.identityScope, 'selected-fable-project-compile')
        assert.equal(compiled.projectPath, projectPath)
        assert.equal(compiled.outputPath, 'js')
        assert.equal(compiled.treeId, source.treeId)
        assert.equal(compiled.sourceDigest, source.sourceDigest)
        assert.equal(compiled.sdkDigest, sdk.sdkDigest)
        assert.equal(compiled.toolsDigest, tools.toolsDigest)
        assert.equal(compiled.projectDigest, project.projectDigest)
        assert.equal(fs.realpathSync(compiled.compileRoot), compiled.compileRoot)
        assert.equal(path.dirname(compiled.compileRoot), compileParent)
        assert.deepEqual(compiled.entries, actualInventory(compiled.compileRoot))
        assert.equal(compiled.entriesDigest, hash('sha256', JSON.stringify(compiled.entries)))
        assert.deepEqual(compiled.fable, tools.tools.find(tool => tool.command === 'fable'))
        assert.equal(compiled.compileDigest, hash('sha256', JSON.stringify({ sourceDigest: compiled.sourceDigest, treeId: compiled.treeId, sdkDigest: compiled.sdkDigest, toolsDigest: compiled.toolsDigest, projectDigest: compiled.projectDigest, projectPath: compiled.projectPath, fable: compiled.fable, entriesDigest: compiled.entriesDigest, identityScope: compiled.identityScope })))
        for (const name of ['Identity', 'Quiescence']) {
          const file = `js/Foundation/${name}.js`
          const entry = compiled.entries.find(entry => entry.path === file)
          assert.equal(entry?.type, 'File')
          assert.ok(entry.size > 0)
          assert.equal(entry.sha256, hash('sha256', fs.readFileSync(path.join(compiled.compileRoot, file))))
        }
        compiled.revalidate()
        assertOwners()
        emitted = true
      })
      if (!emitted) return
      await t.test('WHAT[verification-system-016] actual generated JavaScript preserves public Identity behavior and all prepared owner lifetimes', async () => {
        const identityUrl = pathToFileURL(path.join(compiled.compileRoot, compiled.outputPath, 'Foundation/Identity.js')).href
        const quiescenceUrl = pathToFileURL(path.join(compiled.compileRoot, compiled.outputPath, 'Foundation/Quiescence.js')).href
        const program = `
import assert from 'node:assert/strict'
const identity = await import(${JSON.stringify(identityUrl)})
const quiescence = await import(${JSON.stringify(quiescenceUrl)})
assert.equal(identity.SessionIdModule_value(identity.SessionIdModule_create('selected-session')), 'selected-session')
assert.equal(identity.JournalRevisionModule_value(identity.JournalRevisionModule_next(identity.JournalRevisionModule_create(7n))), 8n)
assert.equal(typeof quiescence.QuiescencePermitFailure, 'function')
assert.equal(typeof quiescence.QuiescencePermitFailure_$reflection, 'function')
console.log(JSON.stringify({session:'selected-session',revision:'8',quiescenceLoaded:true,nodeVersion:process.version}))
`
        const env = { HOME: root, PATH: path.dirname(process.execPath), TMPDIR: root, LANG: 'C', LC_ALL: 'C' }
        const consume = signal => runVerificationToolProbe(process.execPath, ['--experimental-default-type=module', '--input-type=module', '-e', program], { cwd: root, env, signal })
        const consumed = JSON.parse(await (readonlyInputs ? consumeReadonlyVerificationInputs(readonlyInputs, signal => consume(AbortSignal.any([t.signal, signal]))) : consume(t.signal)))
        assert.deepEqual(consumed, { session: 'selected-session', revision: '8', quiescenceLoaded: true, nodeVersion: process.version })
        for (const selected of [...toolArchives, ...projectArchives]) fs.rmSync(selected.archivePath)
        fs.rmSync(sdkArchivePath)
        assert.deepEqual(fs.readdirSync(archiveRoot), [])
        compiled.revalidate()
        assertOwners()
        t.diagnostic(JSON.stringify({ compileDigest: compiled.compileDigest, entries: compiled.entries.length, fable: compiled.fable, actualJavaScriptConsumer: consumed, readOnlyMounted: readonly, actualVerifyBound: false }))
        compiled.dispose()
        assert.equal(fs.existsSync(compiled.compileRoot), false)
        assertOwners()
      })
    }
    if (readonly) {
      const { withReadonlyVerificationInputs } = await import('../../../../scripts/lib/verification-readonly-inputs.mjs')
      readonlyRestored = false
      await withReadonlyVerificationInputs({ source, sdk, tools, project, parentDirectory: root, signal: t.signal }, async view => {
        readonlyInputs = view
        try {
          await compileAndConsume()
        } catch (error) {
          try { compiled?.dispose() }
          catch (cleanupError) { throw new AggregateError([error, cleanupError], 'Readonly compilation and output cleanup failed', { cause: error }) }
          throw error
        }
        compiled?.dispose()
      })
      readonlyRestored = true
      readonlyInputs = undefined
      if (compiled) assert.throws(() => compiled.revalidate(), /not active/, 'Compiled receipts cannot outlive their readonly input view')
      assertOwners()
      assert.equal(fs.readdirSync(path.join(root, 'outputs')).length, 0)
    } else await compileAndConsume()
    assert.deepEqual(fs.readdirSync(parentDirectory).sort(), [source.sourceRoot, sdk.toolRoot, tools.toolRoot, project.projectRoot].map(directory => path.basename(directory)).sort())
    project.dispose()
    tools.dispose()
    sdk.dispose()
    assertSource()
    source.dispose()
    assert.deepEqual(fs.readdirSync(parentDirectory), [])
  } catch (error) {
    const describe = failure => failure instanceof Error ? { name: failure.name, message: failure.message, code: failure.code, killed: failure.killed, signal: failure.signal, stderr: failure.stderr, errors: failure.errors?.map(describe), cause: failure.cause === undefined ? undefined : describe(failure.cause) } : failure
    t.diagnostic(JSON.stringify({ actualFailure: describe(error) }))
    actionFailure = { error }
    throw error
  } finally {
    const failures = []
    for (const candidate of readonlyRestored ? [compiled, project, tools, sdk, source] : [compiled]) {
      try { candidate?.dispose() } catch (error) { failures.push(error) }
    }
    if (readonlyRestored) {
      try { assert.deepEqual(fs.readdirSync(parentDirectory), []) } catch (error) { failures.push(error) }
      try { fs.rmSync(allocatedRoot, { recursive: true, force: true }) } catch (error) { failures.push(error) }
    } else t.diagnostic(`Readonly handoff did not complete; retained resources at ${root}`)
    if (failures.length) throw new AggregateError(actionFailure ? [actionFailure.error, ...failures] : failures, 'Repository Fable fixture cleanup failed', { cause: actionFailure ? actionFailure.error : failures[0] })
  }
}
