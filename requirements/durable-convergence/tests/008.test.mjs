import assert from 'node:assert/strict'
import { execFileSync, spawn } from 'node:child_process'
import { chmodSync, existsSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import test from 'node:test'
import { fileURLToPath } from 'node:url'
import * as hook from '../../../dist/Git/Hook/Surface.js'
import { createBareWorkspace, readRemoteStoreOid } from '../../verification-system/tests/support/dumb-remote.mjs'
import { integrationTest } from '../../verification-system/tests/support/tier-gate.mjs'
import { event } from './support/events.mjs'
import { appendFact, assertFacts, runHook } from './support/hooks.mjs'

integrationTest('WHAT[durable-convergence-008] reference-transaction imports the observed snapshot and publishes independent local truth in one hook process', async () => {
  const workspace = createBareWorkspace(['left', 'right'])
  try {
    const left = workspace.client('left')
    const right = workspace.client('right')
    const a = event('a'.repeat(40), [], { writer: 'left' })
    const b = event('b'.repeat(40), [], { writer: 'right' })
    await appendFact(left, 'writer-left', a)
    await appendFact(right, 'writer-right', b)
    runHook(left)
    const before = readRemoteStoreOid(workspace.bare)
    execFileSync('git', ['-C', right, 'fetch', '-q', 'origin', '+refs/wanxiang/store:refs/wanxiang/remotes/origin/store'])
    const update = `${'0'.repeat(40)} ${before} refs/wanxiang/remotes/origin/store\n`
    runHook(right, 'reference-transaction', 'committed', update)
    const after = readRemoteStoreOid(workspace.bare)
    assert.notEqual(after, before, 'local independent truth must also be published')
    assertFacts(right, [a, b])
    runHook(left)
    assertFacts(left, [a, b])
    runHook(right, 'reference-transaction', 'committed', update)
    assert.equal(readRemoteStoreOid(workspace.bare), after, 'stale observed input cannot replace newer union')
  } finally {
    workspace.cleanup()
  }
})

const storeLine = remote => `+refs/wanxiang/store:refs/wanxiang/remotes/${remote}/store`
const headsLine = remote => `+refs/heads/*:refs/remotes/${remote}/*`
const git = (repo, ...args) => execFileSync('git', ['-C', repo, ...args], { encoding: 'utf8' })
const fetchSpecs = (repo, remote) => git(repo, 'config', '--get-all', `remote.${remote}.fetch`).trim().split('\n')

test('WHAT[durable-convergence-008] ensure adds both fetch mappings to every remote while preserving custom order and remaining idempotent', () => {
  const repo = mkdtempSync(join(tmpdir(), 'wxs-fetch-baseline-'))
  try {
    git(repo, 'init', '--quiet')
    for (const remote of ['origin', 'upstream']) {
      git(repo, 'remote', 'add', remote, `https://example.com/${remote}.git`)
      git(repo, 'config', '--replace-all', `remote.${remote}.fetch`, '+refs/tags/*:refs/tags/*')
      git(repo, 'config', '--add', `remote.${remote}.fetch`, `+refs/review/*:refs/remotes/${remote}/review/*`)
    }
    const before = new Map(['origin', 'upstream'].map(remote => [remote, fetchSpecs(repo, remote)]))
    assert.equal(hook.ensure(repo), true)
    const after = new Map()
    for (const remote of ['origin', 'upstream']) {
      const specs = fetchSpecs(repo, remote)
      assert.deepEqual(specs.slice(0, 2), before.get(remote))
      assert.equal(specs.length, 4)
      assert.ok(specs.includes(storeLine(remote)))
      assert.ok(specs.includes(headsLine(remote)))
      after.set(remote, specs)
    }
    assert.equal(hook.ensure(repo), true)
    for (const remote of ['origin', 'upstream']) assert.deepEqual(fetchSpecs(repo, remote), after.get(remote))
  } finally {
    rmSync(repo, { recursive: true, force: true })
  }
})

test('WHAT[durable-convergence-008] ensure repairs a store-only remote without replacing its existing line', () => {
  const repo = mkdtempSync(join(tmpdir(), 'wxs-fetch-store-only-'))
  try {
    git(repo, 'init', '--quiet')
    git(repo, 'remote', 'add', 'origin', 'https://example.com/repo.git')
    git(repo, 'config', '--replace-all', 'remote.origin.fetch', storeLine('origin'))
    assert.deepEqual(fetchSpecs(repo, 'origin'), [storeLine('origin')])
    assert.equal(hook.ensure(repo), true)
    assert.deepEqual(fetchSpecs(repo, 'origin'), [storeLine('origin'), headsLine('origin')])
    assert.equal(hook.ensure(repo), true)
    assert.deepEqual(fetchSpecs(repo, 'origin'), [storeLine('origin'), headsLine('origin')])
  } finally {
    rmSync(repo, { recursive: true, force: true })
  }
})

test.todo('WHAT[durable-convergence-008] real plugin load leaves Git configuration unchanged and first durability activation installs hooks without starting synchronization (GAP-151)')
test.todo('WHAT[durable-convergence-008] controlled CAS competition and replacement crash cuts preserve all facts while clean no-op leaves unseen remote progress untouched (GAP-151)')

// ── WP-024: the store lock covers local bytes only; network stays outside ──

const hookRunner = fileURLToPath(new URL('../../../resources/git/wanxiang-hook.mjs', import.meta.url))

const waitFor = async (predicate, timeoutMs = 8000) => {
  const start = Date.now()
  while (Date.now() - start < timeoutMs) {
    if (predicate()) return true
    await new Promise(resolve => setTimeout(resolve, 25))
  }
  return predicate()
}

// A slow `git` shim: ls-remote/fetch/push stamp a marker file and sleep, so the
// hook's network window is observable from this test process. Every other git
// verb passes straight through.
const makeSlowGit = () => {
  const shim = mkdtempSync(join(tmpdir(), 'wxs-slow-git-'))
  const marker = join(shim, 'network-started')
  const realGit = execFileSync('which', ['git'], { encoding: 'utf8' }).trim()
  const script = [
    '#!/bin/sh',
    'case " $* " in',
    '  *" ls-remote "*) echo started >> ' + JSON.stringify(marker) + '; sleep 2.5 ;;',
    '  *" fetch "*) echo started >> ' + JSON.stringify(marker) + '; sleep 2.5 ;;',
    '  *" push "*) echo started >> ' + JSON.stringify(marker) + '; sleep 2.5 ;;',
    'esac',
    'exec ' + JSON.stringify(realGit) + ' "$@"',
    '',
  ].join(String.fromCharCode(10))
  const gitPath = join(shim, 'git')
  writeFileSync(gitPath, script, 'utf8')
  chmodSync(gitPath, 0o755)
  return { shim, marker }
}

const spawnHook = (repo, kind, arg, shim) =>
  spawn(process.execPath, [hookRunner, kind, arg], {
    cwd: repo,
    env: { ...process.env, PATH: shim + ':' + process.env.PATH, WANXIANG_GIT_SYNC_ACTIVE: '' },
    stdio: 'ignore',
  })

integrationTest('WHAT[durable-convergence-008] a network command in flight never blocks local append', async () => {
  const workspace = createBareWorkspace(['client'])
  try {
    const repo = workspace.client('client')
    await appendFact(repo, 'writer-a', event('a'.repeat(40), [], { writer: 'writer-a' }))
    const { shim, marker } = makeSlowGit()
    const child = spawnHook(repo, 'pre-push', 'origin', shim)
    const exit = new Promise(resolve => child.on('exit', code => resolve(code)))

    assert.ok(await waitFor(() => existsSync(marker)), 'the slow git shim was never invoked')
    const started = Date.now()
    await appendFact(repo, 'writer-probe', event('b'.repeat(40), [], { writer: 'writer-probe' }))
    const waited = Date.now() - started

    assert.ok(waited < 1000, 'append waited ' + waited + ' ms behind the hook network window')
    assert.equal(await exit, 0)
  } finally {
    workspace.cleanup()
  }
})

integrationTest('WHAT[durable-convergence-008] a hung network command still converges the full union without holding the lock', async () => {
  const workspace = createBareWorkspace(['left', 'right'])
  try {
    const left = workspace.client('left')
    const right = workspace.client('right')
    const a = event('a'.repeat(40), [], { writer: 'left' })
    const b = event('b'.repeat(40), [], { writer: 'right' })
    await appendFact(left, 'writer-left', a)
    runHook(left)
    await appendFact(right, 'writer-right', b)
    runHook(right)

    const { shim, marker } = makeSlowGit()
    const child = spawnHook(left, 'pre-push', 'origin', shim)
    const exit = new Promise(resolve => child.on('exit', code => resolve(code)))

    assert.ok(await waitFor(() => existsSync(marker)), 'the slow git shim was never invoked')
    const started = Date.now()
    const c = event('c'.repeat(40), [], { writer: 'writer-probe' })
    await appendFact(left, 'writer-probe', c)
    const waited = Date.now() - started
    assert.ok(waited < 1000, 'append waited ' + waited + ' ms behind the hung network window')
    assert.equal(await exit, 0)

    assertFacts(left, [a, b, c])
    assert.ok(readRemoteStoreOid(workspace.bare), 'the converged union reached the remote')
  } finally {
    workspace.cleanup()
  }
})

integrationTest('WHAT[durable-convergence-008] a lease race refetches and republishes the union instead of overwriting unseen remote progress', async () => {
  const workspace = createBareWorkspace(['left', 'right'])
  try {
    const left = workspace.client('left')
    const right = workspace.client('right')
    const a = event('a'.repeat(40), [], { writer: 'left' })
    const b = event('b'.repeat(40), [], { writer: 'right' })
    const c = event('c'.repeat(40), [], { writer: 'left' })
    await appendFact(left, 'writer-left', a)
    runHook(left)
    await appendFact(right, 'writer-right', b)
    runHook(right)
    await appendFact(left, 'writer-left', c)
    runHook(left)

    assertFacts(left, [a, b, c])
    assert.ok(readRemoteStoreOid(workspace.bare), 'the race left the remote published')

    runHook(right)
    assertFacts(right, [a, b, c])
    runHook(left)
    assertFacts(left, [a, b, c])
  } finally {
    workspace.cleanup()
  }
})
