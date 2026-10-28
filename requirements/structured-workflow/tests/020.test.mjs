import test from 'node:test'
import assert from 'node:assert/strict'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { spawn, execFileSync } from 'node:child_process'
import { pathToFileURL, fileURLToPath } from 'node:url'
import { requestCompiler } from '../../../scripts/compiler/client.mjs'
import { createUsage } from '../../../scripts/compiler/usage.mjs'
import { integrationTest } from '../../verification-system/tests/support/tier-gate.mjs'

const repository = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../..')
const client = pathToFileURL(path.join(repository, 'scripts/compiler/client.mjs')).href
const snapshot = directory => Object.fromEntries(fs.readdirSync(directory, { recursive: true })
  .filter(name => name.endsWith('.js')).sort().map(name => [name, fs.readFileSync(path.join(directory, name), 'utf8')]))

function fixture() {
  const root = fs.realpathSync(fs.mkdtempSync(path.join(os.tmpdir(), 'wxs-checker-')))
  fs.mkdirSync(path.join(root, '.config'))
  for (const name of ['.config/dotnet-tools.json', 'global.json', 'package-lock.json']) fs.copyFileSync(path.join(repository, name), path.join(root, name))
  fs.writeFileSync(path.join(root, 'package.json'), '{"name":"checker-fixture","type":"module","exports":"./dist/Value.js"}')
  fs.writeFileSync(path.join(root, 'Value.fs'), 'module Value\nlet answer = 10\n')
  fs.writeFileSync(path.join(root, 'Test.fsproj'), '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems><DisableImplicitFSharpCoreReference>true</DisableImplicitFSharpCoreReference></PropertyGroup><ItemGroup><PackageReference Include="FSharp.Core" Version="10.1.203"/><PackageReference Include="Fable.Core" Version="5.0.0"/></ItemGroup><ItemGroup><Compile Include="Value.fs"/></ItemGroup></Project>')
  return root
}
function call(root, op = 'compile', output = 'dist') {
  const command = { op, project: path.join(root, 'Test.fsproj'), output: path.join(root, output), noRestore: false, noCache: true }
  return new Promise((resolve, reject) => {
    const source = `import {requestCompiler} from ${JSON.stringify(client)}; console.log(JSON.stringify(await requestCompiler(${JSON.stringify(root)},${JSON.stringify(command)})))`
    const child = spawn(process.execPath, ['--input-type=module', '-e', source], { cwd: root, stdio: ['ignore', 'pipe', 'pipe'] })
    let stdout = ''
    let stderr = ''
    child.stdout.on('data', text => { stdout += text })
    child.stderr.on('data', text => { stderr += text })
    child.on('error', reject)
    child.on('close', code => {
      if (code) reject(new Error(stderr))
      else {
        try { resolve(JSON.parse(stdout)) }
        catch (error) { reject(error) }
      }
    })
  })
}
async function value(root) {
  return Number(execFileSync(process.execPath, ['--input-type=module', '-e', `import {answer} from 'checker-fixture'; console.log(answer)`], { cwd: root, encoding: 'utf8' }).trim())
}
async function close(root) {
  await requestCompiler(root, { op: 'stop' })
  fs.rmSync(root, { recursive: true, force: true })
}

integrationTest('WHAT[structured-workflow-020] independent commands reuse checks, read new bytes and match the CLI', { timeout: 180000 }, async () => {
  const root = fixture()
  try {
    const first = await call(root)
    assert.equal(first.ok, true, JSON.stringify(first))
    assert.equal(first.reused, false)
    assert.equal(await value(root), 10)
    const file = path.join(root, 'Value.fs')
    const time = fs.statSync(file)
    fs.writeFileSync(file, 'module Value\nlet answer = 11\n')
    fs.utimesSync(file, time.atime, time.mtime)
    const second = await call(root)
    assert.equal(second.ok, true, JSON.stringify(second))
    assert.equal(second.pid, first.pid)
    assert.equal(second.reused, true)
    assert.equal(await value(root), 11)
    const expected = snapshot(path.join(root, 'dist'))
    fs.rmSync(path.join(root, 'dist'), { recursive: true })
    execFileSync('dotnet', ['tool', 'run', 'fable', '--', 'Test.fsproj', '-c', 'Debug', '-o', 'dist', '--noGitignore', '--noCache'], { cwd: root, stdio: 'pipe' })
    assert.deepEqual(snapshot(path.join(root, 'dist')), expected)
    fs.writeFileSync(file, 'module Value\nlet answer: int = "wrong"\n')
    const bad = await call(root)
    assert.equal(bad.ok, false)
    assert.equal(bad.retired, true)
    fs.writeFileSync(file, 'module Value\nlet answer = 12\n')
    const repaired = await call(root)
    assert.equal(repaired.ok, true, JSON.stringify(repaired))
    assert.notEqual(repaired.pid, first.pid)
    assert.equal(await value(root), 12)
    const usage = JSON.parse(fs.readFileSync(path.join(root, '.fable-build/compiler/usage.json')))
    assert.equal(usage.totals.attempts, 4)
    assert.equal(usage.totals.success, 3)
    assert.equal(usage.totals.failure, 1)
    assert.equal(usage.totals.created, 2)
    assert.equal(usage.totals.reusedAcrossBuilds, 2)
    assert.equal(usage.samples[1].intervalMs, usage.samples[1].startedAt - usage.samples[0].startedAt)
    assert.equal(usage.samples[1].idleMs, usage.samples[1].startedAt - usage.samples[0].finishedAt)
    assert.ok(usage.samples[1].workerMemory.rss > 0)
    assert.ok(usage.samples[1].workerMemory.managedHeapBytes > 0)
  } finally { await close(root) }
})

integrationTest('WHAT[structured-workflow-020] changing staging paths still reuses the checker with fresh package sources', { timeout: 180000 }, async () => {
  const root = fixture()
  try {
    const project = path.join(root, 'Test.fsproj')
    fs.writeFileSync(project, fs.readFileSync(project, 'utf8').replace('<PackageReference Include="Fable.Core"', '<PackageReference Include="Thoth.Json" Version="10.5.1"/><PackageReference Include="Fable.Core"'))
    const first = await call(root)
    assert.equal(first.ok, true, JSON.stringify(first))
    fs.writeFileSync(path.join(root, 'Value.fs'), 'module Value\nlet answer = 23\n')
    const second = await call(root, 'compile', 'next-dist')
    assert.equal(second.ok, true, JSON.stringify(second))
    assert.equal(second.pid, first.pid)
    assert.equal(second.reused, true)
    const expected = snapshot(path.join(root, 'next-dist'))
    fs.rmSync(path.join(root, 'next-dist'), { recursive: true })
    execFileSync('dotnet', ['tool', 'run', 'fable', '--', 'Test.fsproj', '-c', 'Debug', '-o', 'next-dist', '--noGitignore', '--noCache'], { cwd: root, stdio: 'pipe' })
    assert.deepEqual(snapshot(path.join(root, 'next-dist')), expected)
    fs.writeFileSync(path.join(root, 'package-lock.json'), '{}\n')
    const changed = await call(root)
    assert.equal(changed.ok, true, JSON.stringify(changed))
    assert.notEqual(changed.pid, first.pid)
    assert.equal(await value(root), 23)
  } finally { await close(root) }
})

integrationTest('WHAT[structured-workflow-020] concurrent first commands share one service and recover after it is killed', { timeout: 180000 }, async () => {
  const root = fixture()
  try {
    const results = await Promise.all([call(root), call(root), call(root), call(root)])
    assert.ok(results.every(result => result.ok), JSON.stringify(results))
    assert.equal(new Set(results.map(result => result.servicePid)).size, 1)
    assert.equal(new Set(results.map(result => result.pid)).size, 1)
    assert.equal(await value(root), 10)
    process.kill(results[0].servicePid, 'SIGKILL')
    await new Promise(resolve => setTimeout(resolve, 1000))
    const recovered = await call(root)
    assert.equal(recovered.ok, true, JSON.stringify(recovered))
    assert.notEqual(recovered.servicePid, results[0].servicePid)
    assert.notEqual(recovered.pid, results[0].pid)
    assert.equal(await value(root), 10)
  } finally { await close(root) }
})

integrationTest('WHAT[structured-workflow-020] project files, order and conditional options are read again', { timeout: 180000 }, async () => {
  const root = fixture()
  try {
    assert.equal((await call(root)).ok, true)
    const project = path.join(root, 'Test.fsproj')
    const initial = fs.readFileSync(project, 'utf8')
    fs.writeFileSync(path.join(root, 'Other.fs'), 'module Other\nlet value = 17\n')
    fs.writeFileSync(path.join(root, 'Value.fs'), 'module Value\nlet answer = Other.value\n')
    fs.writeFileSync(project, initial.replace('<Compile Include="Value.fs"/>', '<Compile Include="Other.fs"/><Compile Include="Value.fs"/>'))
    const added = await call(root)
    assert.equal(added.ok, true, JSON.stringify(added))
    assert.equal(await value(root), 17)
    fs.writeFileSync(project, initial.replace('<Compile Include="Value.fs"/>', '<Compile Include="Value.fs"/><Compile Include="Other.fs"/>'))
    const reversed = await call(root)
    assert.equal(reversed.ok, false)
    fs.rmSync(path.join(root, 'Other.fs'))
    fs.writeFileSync(project, initial)
    fs.writeFileSync(path.join(root, 'Value.fs'), 'module Value\n#if CHECKER_CASE\nlet answer = 21\n#else\nlet answer = 22\n#endif\n')
    assert.equal((await call(root)).ok, true)
    assert.equal(await value(root), 22)
    fs.writeFileSync(project, initial.replace('</PropertyGroup>', '<DefineConstants>CHECKER_CASE</DefineConstants></PropertyGroup>'))
    assert.equal((await call(root)).ok, true)
    assert.equal(await value(root), 21)
  } finally { await close(root) }
})

integrationTest('WHAT[structured-workflow-020] workspaces, build limits and idle exit have separate lifetimes', { timeout: 180000 }, async () => {
  const first = fixture()
  const second = fixture()
  const oldLimit = process.env.WXS_CHECKER_BUILD_LIMIT
  const oldIdle = process.env.WXS_CHECKER_IDLE_MS
  try {
    const left = await call(first)
    const right = await call(second)
    assert.equal(left.ok, true)
    assert.equal(right.ok, true)
    assert.notEqual(left.pid, right.pid)
    assert.notEqual(left.servicePid, right.servicePid)
    process.env.WXS_CHECKER_BUILD_LIMIT = '1'
    process.env.WXS_CHECKER_IDLE_MS = '400'
    const retired = await call(first)
    assert.equal(retired.ok, true, JSON.stringify(retired))
    assert.equal(retired.retired, true)
    await new Promise(resolve => setTimeout(resolve, 800))
    assert.equal((await call(first, 'status')).running, false)
    const restarted = await call(first)
    assert.equal(restarted.ok, true)
    assert.notEqual(restarted.pid, retired.pid)
  } finally {
    if (oldLimit === undefined) delete process.env.WXS_CHECKER_BUILD_LIMIT
    else process.env.WXS_CHECKER_BUILD_LIMIT = oldLimit
    if (oldIdle === undefined) delete process.env.WXS_CHECKER_IDLE_MS
    else process.env.WXS_CHECKER_IDLE_MS = oldIdle
    await close(first)
    await close(second)
  }
})

test('WHAT[structured-workflow-020] build publishers serialize and a dead owner can be replaced', { timeout: 30000 }, async () => {
  const root = fixture()
  const build = pathToFileURL(path.join(repository, 'scripts/build.mjs')).href
  const lock = path.join(root, 'build.lock')
  const occupied = path.join(root, 'occupied')
  const code = `import fs from 'node:fs'; import {CrossProcessMutex} from ${JSON.stringify(build)};
    const lock = new CrossProcessMutex(${JSON.stringify(lock)});
    await lock.acquire(); fs.writeFileSync(${JSON.stringify(occupied)}, String(process.pid), {flag:'wx'});
    await new Promise(resolve=>setTimeout(resolve,100)); fs.unlinkSync(${JSON.stringify(occupied)}); lock.release();`
  const run = source => new Promise((resolve, reject) => {
    const child = spawn(process.execPath, ['--input-type=module', '-e', source], { stdio: ['ignore', 'pipe', 'pipe'] })
    let errors = ''
    child.stderr.on('data', text => { errors += text })
    child.on('error', reject)
    child.on('close', code => code ? reject(new Error(errors)) : resolve())
  })
  try {
    await Promise.all([run(code), run(code), run(code), run(code)])
    await run(`import {CrossProcessMutex} from ${JSON.stringify(build)}; await new CrossProcessMutex(${JSON.stringify(lock)}).acquire(); process.exit(0);`)
    await Promise.all([run(code), run(code), run(code)])
  } finally { await close(root) }
})

integrationTest('WHAT[structured-workflow-020] replacing a referenced DLL at the same path and timestamp drops its checker', { timeout: 180000 }, async () => {
  const root = fixture()
  const { toolchain } = await import('../../../scripts/compiler/toolchain.mjs')
  const identity = toolchain(root)
  const reference = path.join(root, 'Contract.dll')
  const dllSource = path.join(root, 'Contract.fs')
  const core = path.resolve(identity.packageRoot, '../../fable.core/5.0.0/lib/netstandard2.0/Fable.Core.dll')
  const make = (type, expression) => {
    fs.writeFileSync(dllSource, `namespace Contract\nopen Fable.Core\n[<AbstractClass; Sealed>]\ntype Api =\n    [<Emit("${expression}")>]\n    static member Value: ${type} = failwith "JS only"\n`)
    execFileSync('dotnet', [path.join(identity.sdk, 'FSharp/fsc.dll'), '--target:library', '--targetprofile:netcore', '--noframework', '--nocopyfsharpcore', '--out:' + reference,
      ...fs.readdirSync(identity.references).filter(name => name.endsWith('.dll')).map(name => '-r:' + path.join(identity.references, name)),
      '-r:' + path.join(identity.payload, 'FSharp.Core.dll'), '-r:' + core, dllSource], { stdio: 'pipe' })
  }
  try {
    const project = path.join(root, 'Test.fsproj')
    fs.writeFileSync(project, fs.readFileSync(project, 'utf8').replace('<Compile Include="Value.fs"/>', '<Reference Include="Contract"><HintPath>Contract.dll</HintPath></Reference><Compile Include="Value.fs"/>'))
    fs.writeFileSync(path.join(root, 'Value.fs'), 'module Value\nlet answer: int = Contract.Api.Value\n')
    make('int', '41')
    const first = await call(root)
    assert.equal(first.ok, true, JSON.stringify(first))
    assert.equal(await value(root), 41)
    const same = await call(root)
    assert.equal(same.pid, first.pid)
    assert.equal(same.reused, true)
    const timestamp = fs.statSync(reference)
    make('string', "'changed'")
    fs.utimesSync(reference, timestamp.atime, timestamp.mtime)
    const changed = await call(root)
    assert.equal(changed.ok, false, JSON.stringify(changed))
    assert.equal(changed.reused, false)
    assert.notEqual(changed.pid, first.pid)
  } finally { await close(root) }
})

integrationTest('WHAT[structured-workflow-020] status polling does not renew idle time and usage survives service restart', { timeout: 180000 }, async () => {
  const root = fixture()
  const previous = process.env.WXS_CHECKER_IDLE_MS
  const alive = pid => {
    try { process.kill(pid, 0); return true }
    catch (error) { if (error.code === 'ESRCH') return false; throw error }
  }
  try {
    process.env.WXS_CHECKER_IDLE_MS = '1200'
    const first = await call(root)
    assert.equal(first.ok, true, JSON.stringify(first))
    const file = path.join(root, '.fable-build/compiler/usage.json')
    const before = fs.readFileSync(file, 'utf8')
    const deadline = Date.now() + 5000
    let queries = 0
    while (alive(first.servicePid) && Date.now() < deadline) {
      await requestCompiler(root, { op: 'status' }).catch(error => {
        assert.match(error.message, /disconnected|exited/)
      })
      queries++
      await new Promise(resolve => setTimeout(resolve, 50))
    }
    assert.ok(queries >= 2)
    assert.equal(alive(first.servicePid), false, 'status polling kept the service alive')
    assert.equal(alive(first.pid), false)
    assert.equal(fs.readFileSync(file, 'utf8'), before)
    const restarted = await call(root)
    assert.equal(restarted.ok, true, JSON.stringify(restarted))
    const usage = JSON.parse(fs.readFileSync(file))
    assert.equal(usage.totals.attempts, 2)
    assert.equal(usage.totals.created, 2)
    assert.equal(usage.totals.reused, 0)
    assert.ok(usage.samples[1].idleMs >= 1200)
  } finally {
    if (previous === undefined) delete process.env.WXS_CHECKER_IDLE_MS
    else process.env.WXS_CHECKER_IDLE_MS = previous
    await close(root)
  }
})

test('WHAT[structured-workflow-020] usage history is bounded, survives interruption and fails independently', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'checker-usage-'))
  const file = path.join(root, 'usage.json')
  try {
    createUsage(file).begin({ projects: 1 })
    const recorder = createUsage(file)
    for (let index = 0; index < 260; index++) {
      recorder.begin({ projects: 1 })({ outcome: 'success', reuse: { created: 0, reused: 1, reusedAcrossBuilds: 1 } })
    }
    const usage = JSON.parse(fs.readFileSync(file))
    assert.equal(usage.samples.length, 256)
    assert.equal(usage.totals.attempts, 261)
    assert.equal(usage.totals.unknown, 1)
    assert.equal(usage.totals.reusedAcrossBuilds, 260)
    assert.equal(usage.totals.missingReuseReports, 1)
    fs.writeFileSync(file, 'invalid record')
    assert.doesNotThrow(() => createUsage(file).begin({ projects: 1 })({ outcome: 'failure', reuse: null }))
    assert.equal(fs.readFileSync(file, 'utf8'), 'invalid record')
  } finally { fs.rmSync(root, { recursive: true, force: true }) }
})
