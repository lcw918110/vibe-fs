import assert from 'node:assert/strict'
import { createHash } from 'node:crypto'
import { execFileSync } from 'node:child_process'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import test from 'node:test'
import { create } from 'tar'

const repositoryRoot = path.resolve(import.meta.dirname, '../../../..')
const sha256 = bytes => createHash('sha256').update(bytes).digest('hex')
const prepareSdk = async options => (await import('../../../../scripts/lib/verification-dotnet-sdk.mjs')).prepareVerificationDotnetSdk(options)

function fixture() {
  const allocatedRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'verification-dotnet-sdk-test-'))
  const root = fs.realpathSync(allocatedRoot)
  const sourceRoot = path.join(root, 'source')
  const parentDirectory = path.join(root, 'candidates')
  const selectedRoot = path.join(root, 'selected')
  fs.mkdirSync(sourceRoot)
  fs.mkdirSync(parentDirectory)
  fs.mkdirSync(path.join(selectedRoot, 'dotnet-sdk'), { recursive: true })
  const globalBytes = fs.readFileSync(path.join(repositoryRoot, 'global.json'))
  fs.writeFileSync(path.join(sourceRoot, 'global.json'), globalBytes)
  return { root, sourceRoot, parentDirectory, selectedRoot, globalBytes }
}

async function archive(fixture) {
  const archivePath = path.join(fixture.root, 'sdk.tar')
  await create({ cwd: fixture.selectedRoot, file: archivePath, portable: true, noMtime: true }, ['dotnet-sdk'])
  return {
    sourceRoot: fixture.sourceRoot,
    archivePath,
    archiveSha256: sha256(fs.readFileSync(archivePath)),
    parentDirectory: fixture.parentDirectory,
    expectedSdkVersion: '10.0.302',
  }
}

function writeExecutable(fixture, body) {
  const executable = path.join(fixture.selectedRoot, 'dotnet-sdk/dotnet')
  fs.writeFileSync(executable, `#!${process.execPath}\n${body}\n`, { mode: 0o755 })
  return executable
}

function assertReleased(fixture) {
  assert.deepEqual(fs.readdirSync(fixture.parentDirectory), [])
  assert.deepEqual(fs.readFileSync(path.join(fixture.sourceRoot, 'global.json')), fixture.globalBytes)
}

async function rejectedPreparation(options, predicate) {
  let published
  try {
    const [outcome] = await Promise.allSettled([prepareSdk(options)])
    if (outcome.status === 'fulfilled') published = outcome.value
    assert.equal(outcome.status, 'rejected', 'An invalid SDK must not publish a prepared bundle')
    assert.ok(predicate(outcome.reason), `Unexpected preparation failure: ${String(outcome.reason)}`)
  } finally {
    published?.dispose()
  }
}

function capturedInventory(root) {
  const entries = []
  function inspect(relative) {
    const file = path.join(root, relative)
    const stat = fs.lstatSync(file)
    if (stat.isSymbolicLink()) {
      entries.push({ path: relative, type: 'SymbolicLink', target: fs.readlinkSync(file) })
    } else if (stat.isDirectory()) {
      entries.push({ path: relative, type: 'Directory', mode: stat.mode & 0o777 })
      for (const name of fs.readdirSync(file)) inspect(`${relative}/${name}`)
    } else {
      assert.equal(stat.isFile(), true, 'The selected SDK must contain ordinary files, directories and internal links')
      entries.push({ path: relative, type: 'File', mode: stat.mode & 0o777, size: stat.size, sha256: sha256(fs.readFileSync(file)) })
    }
  }
  inspect('dotnet-sdk')
  return entries.sort((left, right) => left.path < right.path ? -1 : left.path > right.path ? 1 : 0)
}

export function registerDotnetSdkTests() {
  for (const replaced of ['archive', 'probe']) {
    test(`WHAT[verification-system-016] successful SDK probe replacing its ${replaced} root cannot start another selected executable`, async () => {
      const selected = fixture()
      try {
        const calls = path.join(selected.root, 'calls')
        writeExecutable(selected, `
const fs = require('node:fs')
const path = require('node:path')
fs.appendFileSync(${JSON.stringify(calls)}, JSON.stringify(process.argv.slice(2)) + '\\n')
if (process.argv[2] === '--version') {
  const owned = ${replaced === 'archive' ? "path.resolve(__dirname, '..')" : 'path.dirname(process.env.HOME)'}
  fs.renameSync(owned, ${JSON.stringify(path.join(selected.root, 'parked'))})
  fs.cpSync(${JSON.stringify(path.join(selected.root, 'parked'))}, owned, { recursive: true })
  fs.writeFileSync(path.join(owned, 'foreign-marker'), 'replacement')
  console.log('10.0.302')
} else { console.log('10.0.302 [' + path.join(__dirname, 'sdk') + ']') }
`)
        const [outcome] = await Promise.allSettled([prepareSdk(await archive(selected))])
        assert.equal(outcome.status, 'rejected')
        assert.deepEqual(fs.readFileSync(calls, 'utf8').trim().split('\n').map(line => JSON.parse(line)), [['--version']], 'changed ownership stops before the next physical probe')
        assert.ok(outcome.reason instanceof AggregateError)
        assert.equal(outcome.reason.cause.code, 'verification-dotnet-sdk-entry-invalid', JSON.stringify(outcome.reason.cause))
        const remaining = fs.readdirSync(selected.parentDirectory)
        assert.equal(remaining.length, 1)
        assert.equal(fs.readFileSync(path.join(selected.parentDirectory, remaining[0], 'foreign-marker'), 'utf8'), 'replacement')
      } finally {
        fs.rmSync(selected.root, { recursive: true, force: true })
      }
    })
  }
  test('WHAT[verification-system-016] failed SDK probe preserves a copied replacement of its private probe root', async () => {
    const selected = fixture()
    try {
      writeExecutable(selected, `
const fs = require('node:fs')
const path = require('node:path')
const root = path.dirname(process.env.HOME)
fs.renameSync(root, ${JSON.stringify(path.join(selected.root, 'parked-probe'))})
fs.cpSync(${JSON.stringify(path.join(selected.root, 'parked-probe'))}, root, {recursive:true})
fs.writeFileSync(path.join(root, 'foreign-marker'), 'replacement')
process.exit(73)
`)
      const [outcome] = await Promise.allSettled([prepareSdk(await archive(selected))])
      assert.equal(outcome.status, 'rejected')
      assert.ok(outcome.reason instanceof AggregateError)
      assert.equal(outcome.reason.cause.exitCode, 73)
      assert.ok(outcome.reason.errors.some(error => error.code === 'verification-dotnet-sdk-entry-invalid'))
      const remaining = fs.readdirSync(selected.parentDirectory)
      assert.equal(remaining.length, 1)
      assert.equal(fs.readFileSync(path.join(selected.parentDirectory, remaining[0], 'foreign-marker'), 'utf8'), 'replacement')
    } finally {
      fs.rmSync(selected.root, { recursive: true, force: true })
    }
  })
  test('WHAT[verification-system-016] selected SDK archive failures reclaim private roots and preserve exact global.json bytes', async t => {
    const selected = fixture()
    try {
      fs.writeFileSync(path.join(selected.selectedRoot, 'dotnet-sdk/README.txt'), 'This archive intentionally contains no SDK')
      let options = await archive(selected)
      await t.test('WHAT[verification-system-016] a missing selected SDK executable is not borrowed from ambient dotnet', async () => {
        await rejectedPreparation(options, error => error.code === 'verification-dotnet-sdk-entry-invalid')
        assertReleased(selected)
      })
      await t.test('WHAT[verification-system-016] a changed SDK archive digest publishes no candidate', async () => {
        await rejectedPreparation({ ...options, archiveSha256: '0'.repeat(64) }, error => error.code === 'verification-dotnet-sdk-integrity-invalid')
        assertReleased(selected)
      })
      for (const reason of [new Error('SDK preparation cancelled before admission'), null]) {
        await t.test(`WHAT[verification-system-016] early SDK cancellation preserves ${reason === null ? 'null' : 'Error'} before reading missing inputs`, async () => {
          const controller = new AbortController()
          controller.abort(reason)
          await rejectedPreparation({ ...options, sourceRoot: path.join(selected.root, 'missing-source'), archivePath: path.join(selected.root, 'missing.tar'), signal: controller.signal }, error => error === reason)
          assertReleased(selected)
        })
      }
      const executable = writeExecutable(selected, 'process.stderr.write("not an SDK\\n"); process.exit(73)')
      options = await archive(selected)
      await t.test('WHAT[verification-system-016] a failing actual executable probe does not publish its archive identity', async () => {
        await rejectedPreparation(options, error => error.code === 'verification-tool-probe-failed' && error.exitCode === 73)
        assertReleased(selected)
      })
      await t.test('WHAT[verification-system-016] dotnet roles cannot use traversal or a symbolic executable alias', async () => {
        fs.symlinkSync('dotnet', path.join(selected.selectedRoot, 'dotnet-sdk/alias'))
        options = await archive(selected)
        for (const dotnetPath of ['dotnet-sdk/../dotnet-sdk/dotnet', 'dotnet-sdk/alias', 'other/dotnet']) {
          await rejectedPreparation({ ...options, dotnetPath }, error => error.code === 'verification-dotnet-sdk-entry-invalid')
          assertReleased(selected)
        }
      })
      await t.test('WHAT[verification-system-016] a non-executable selected dotnet role cannot launch a probe', async () => {
        fs.chmodSync(executable, 0o644)
        options = await archive(selected)
        await rejectedPreparation(options, error => error.code === 'verification-dotnet-sdk-entry-invalid')
        assertReleased(selected)
      })
      await t.test('WHAT[verification-system-016] an escaping SDK archive link cannot capture a foreign runtime', async () => {
        const foreign = path.join(selected.root, 'foreign-runtime')
        fs.mkdirSync(foreign)
        fs.writeFileSync(path.join(foreign, 'retained.txt'), 'foreign bytes')
        fs.symlinkSync(foreign, path.join(selected.selectedRoot, 'dotnet-sdk/foreign'))
        options = await archive(selected)
        await rejectedPreparation(options, error => error.code === 'verification-dotnet-sdk-entry-invalid')
        assertReleased(selected)
        assert.equal(fs.readFileSync(path.join(foreign, 'retained.txt'), 'utf8'), 'foreign bytes')
      })
    } finally {
      fs.rmSync(selected.root, { recursive: true, force: true })
    }
  })

  test('WHAT[verification-system-016] SDK version and provenance come from the selected executable rather than its role name', async t => {
    const selected = fixture()
    try {
      writeExecutable(selected, 'process.stdout.write("9.0.999\\n")')
      let options = await archive(selected)
      await t.test('WHAT[verification-system-016] actual selected version must match the explicitly expected SDK', async () => {
        await rejectedPreparation(options, error => error.code === 'verification-dotnet-sdk-entry-invalid')
        assertReleased(selected)
      })
      const liveRoot = process.env.WXS_VERIFICATION_DOTNET_ROOT
      await t.test('WHAT[verification-system-016] a selected wrapper cannot borrow SDK Base Path or runtimes from a live installation', { skip: liveRoot ? false : 'No explicitly selected live SDK is available for this physical provenance counterexample' }, async () => {
        const liveDotnet = path.join(fs.realpathSync(liveRoot), 'dotnet')
        writeExecutable(selected, `require('node:child_process').spawnSync(${JSON.stringify(liveDotnet)}, process.argv.slice(2), { stdio: 'inherit' });`)
        options = await archive(selected)
        await rejectedPreparation(options, error => error.code === 'verification-dotnet-sdk-entry-invalid')
        assertReleased(selected)
        assert.equal(fs.statSync(liveDotnet).isFile(), true)
      })
    } finally {
      fs.rmSync(selected.root, { recursive: true, force: true })
    }
  })

  for (const reason of [new Error('cancel held selected SDK process'), null]) {
    test(`WHAT[verification-system-016] held SDK probe preserves ${reason === null ? 'null' : 'Error'} cancellation and drains its actual process group`, { skip: process.platform === 'win32' }, async () => {
      const selected = fixture()
      const marker = path.join(selected.root, 'started.json')
      const fallback = path.join(selected.root, 'fallback-cleanup.txt')
      const controller = new AbortController()
      let watcher
      let physical
      let settled
      try {
        const childProgram = `require('node:fs').watch(${JSON.stringify(selected.root)}, () => {}); process.stdout.write('ready\\n')`
        writeExecutable(selected, `
const fs = require('node:fs')
const child = require('node:child_process').spawn(process.execPath, ['-e', ${JSON.stringify(childProgram)}], { stdio: ['ignore', 'pipe', 'inherit'] })
child.stdout.once('data', () => {
  fs.writeFileSync(${JSON.stringify(`${marker}.tmp`)}, JSON.stringify({ pid: process.pid, childPid: child.pid }))
  fs.renameSync(${JSON.stringify(`${marker}.tmp`)}, ${JSON.stringify(marker)})
})
process.on('SIGTERM', () => {
  fs.writeFileSync(${JSON.stringify(fallback)}, 'caller fallback used')
  child.once('close', () => process.exit(89))
  child.kill('SIGKILL')
})`)
        const options = await archive(selected)
        const started = Promise.withResolvers()
        watcher = fs.watch(selected.root, (_event, filename) => {
          if (filename !== path.basename(marker) || !fs.existsSync(marker)) return
          try {
            started.resolve(JSON.parse(fs.readFileSync(marker, 'utf8')))
          } catch (error) {
            started.reject(error)
          }
        })
        const preparing = prepareSdk({ ...options, signal: controller.signal })
        settled = Promise.allSettled([preparing])
        physical = await Promise.race([started.promise, preparing.then(() => { throw new Error('Held SDK probe unexpectedly published') })])
        assert.ok(Number.isInteger(physical.pid) && Number.isInteger(physical.childPid))
        assert.ok(fs.readdirSync(selected.parentDirectory).length > 0)
        controller.abort(reason)
        // EOF cancellation drains asynchronously; a second signal is only valid after settlement.
        const [outcome] = await settled
        assert.equal(outcome.status, 'rejected')
        assert.equal(outcome.reason, reason)
        assert.throws(() => process.kill(physical.pid, 'SIGTERM'), error => error.code === 'ESRCH')
        assert.equal(fs.existsSync(fallback), false, 'The preparation owner must drain the process group before returning')
        for (const pid of [physical.pid, physical.childPid]) {
          assert.throws(() => process.kill(pid, 0), error => error.code === 'ESRCH')
        }
        assertReleased(selected)
      } finally {
        watcher?.close()
        controller.abort(reason)
        if (physical) {
          try {
            process.kill(-physical.pid, 'SIGKILL')
          } catch (error) {
            if (error.code !== 'ESRCH') throw error
          }
        }
        if (settled) await settled
        fs.rmSync(selected.root, { recursive: true, force: true })
      }
    })
  }
}

export async function repositoryDotnetSdkTest(t) {
  const selected = fixture()
  let candidate
  let options
  let liveRoot
  let inventory
  let actionFailure
  let captured = false
  let prepared = false
  let sdkVersion
  let runtimeVersion
  try {
    await t.test('WHAT[verification-system-016] the complete explicitly selected SDK is captured with all files and internal links', async () => {
      assert.ok(process.env.WXS_VERIFICATION_DOTNET_ROOT, 'WXS_VERIFICATION_DOTNET_ROOT must explicitly select the complete SDK installation')
      liveRoot = fs.realpathSync(process.env.WXS_VERIFICATION_DOTNET_ROOT)
      const global = JSON.parse(selected.globalBytes)
      assert.deepEqual(global.sdk, { version: '10.0.100', rollForward: 'latestFeature' })
      fs.cpSync(liveRoot, path.join(selected.selectedRoot, 'dotnet-sdk'), { recursive: true, verbatimSymlinks: true })
      // 从真实 SDK 动态读取版本号，而非硬编码 10.0.302（CI 容器预装版本可能不同）
      const dotnetExe = path.join(selected.selectedRoot, 'dotnet-sdk/dotnet')
      sdkVersion = execFileSync(dotnetExe, ['--version'], { encoding: 'utf8' }).trim()
      const runtimesOutput = execFileSync(dotnetExe, ['--list-runtimes'], { encoding: 'utf8' })
      const coreAppLine = runtimesOutput.split(/\r?\n/).find(line => line.startsWith('Microsoft.NETCore.App '))
      runtimeVersion = coreAppLine ? coreAppLine.split(' ')[1] : sdkVersion
      t.diagnostic(`Real SDK version: ${sdkVersion}, runtime version: ${runtimeVersion}`)
      inventory = capturedInventory(selected.selectedRoot)
      options = await archive(selected)
      // 覆盖硬编码的 expectedSdkVersion，使用真实版本号
      options = { ...options, expectedSdkVersion: sdkVersion }
      t.diagnostic(JSON.stringify({ archiveSha256: options.archiveSha256, entries: inventory.length, fileBytes: inventory.filter(entry => entry.type === 'File').reduce((sum, entry) => sum + entry.size, 0), internalLinks: inventory.filter(entry => entry.type === 'SymbolicLink').length }))
      captured = true
    })
    if (!captured) return
    await t.test('WHAT[verification-system-016] captured global.json selects the actual in-bundle SDK and runtime paths without ambient inputs', async () => {
      candidate = await prepareSdk({ ...options, signal: t.signal })
      assert.equal(candidate.archiveSha256, options.archiveSha256)
      assert.equal(candidate.entriesDigest, sha256(JSON.stringify(candidate.entries)))
      assert.equal(candidate.globalJsonSha256, sha256(selected.globalBytes))
      assert.equal(candidate.identityScope, 'selected-dotnet-sdk-bundle')
      assert.deepEqual(candidate.dotnet, { path: 'dotnet-sdk/dotnet', sha256: sha256(fs.readFileSync(path.join(liveRoot, 'dotnet'))) })
      assert.deepEqual(candidate.sdk, { version: sdkVersion, basePath: `dotnet-sdk/sdk/${sdkVersion}` })
      assert.ok(candidate.runtimes.some(runtime => runtime.name === 'Microsoft.NETCore.App' && runtime.version === runtimeVersion && runtime.path === `dotnet-sdk/shared/Microsoft.NETCore.App/${runtimeVersion}`))
      assert.ok(candidate.runtimes.some(runtime => runtime.name === 'Microsoft.AspNetCore.App' && runtime.version === runtimeVersion && runtime.path === `dotnet-sdk/shared/Microsoft.AspNetCore.App/${runtimeVersion}`))
      const hostLibrary = process.platform === 'darwin' ? 'libhostfxr.dylib' : process.platform === 'win32' ? 'hostfxr.dll' : 'libhostfxr.so'
      for (const relative of [`sdk/${sdkVersion}/dotnet.dll`, `host/fxr/${runtimeVersion}/${hostLibrary}`, `shared/Microsoft.NETCore.App/${runtimeVersion}/System.Private.CoreLib.dll`]) {
        assert.ok(candidate.entries.some(entry => entry.path === `dotnet-sdk/${relative}` && entry.type === 'File'), `Complete SDK capture must retain ${relative}`)
      }
      assert.deepEqual(candidate.entries, inventory, 'The prepared inventory must retain every captured SDK file byte and internal link')
      for (const directory of ['packs', 'library-packs', 'sdk-manifests', 'templates']) {
        assert.ok(candidate.entries.some(entry => entry.path.startsWith(`dotnet-sdk/${directory}/`)), `Complete SDK capture must retain ${directory}`)
      }
      assert.equal(candidate.sdkDigest, sha256(JSON.stringify({ archiveSha256: candidate.archiveSha256, entriesDigest: candidate.entriesDigest, globalJsonSha256: candidate.globalJsonSha256, dotnet: candidate.dotnet, sdk: candidate.sdk, runtimes: candidate.runtimes, identityScope: candidate.identityScope })))
      assert.deepEqual(fs.readdirSync(selected.parentDirectory), [path.basename(candidate.toolRoot)])
      assert.deepEqual(fs.readFileSync(path.join(selected.sourceRoot, 'global.json')), selected.globalBytes)
      t.diagnostic(JSON.stringify({ sdk: candidate.sdk, runtimes: candidate.runtimes, sdkDigest: candidate.sdkDigest, archiveSha256: candidate.archiveSha256, entries: candidate.entries.length, internalLinks: candidate.entries.filter(entry => entry.type === 'SymbolicLink').length, nugetRun: false, fableRun: false, readOnlyMounted: false }))
      prepared = true
    })
    if (!prepared) return
    await t.test('WHAT[verification-system-016] the prepared SDK revalidates without its archive and releases every private root', () => {
      fs.rmSync(options.archivePath)
      candidate.revalidate()
      candidate.dispose()
      assertReleased(selected)
    })
  } catch (error) {
    actionFailure = { error }
    throw error
  } finally {
    const failures = []
    try {
      candidate?.dispose()
    } catch (error) {
      failures.push(error)
    }
    try {
      assertReleased(selected)
    } catch (error) {
      failures.push(error)
    }
    try {
      fs.rmSync(selected.root, { recursive: true, force: true })
    } catch (error) {
      failures.push(error)
    }
    if (failures.length) {
      const causes = actionFailure ? [actionFailure.error, ...failures] : failures
      throw new AggregateError(causes, 'SDK fixture cleanup failed', { cause: actionFailure ? actionFailure.error : failures[0] })
    }
  }
}
