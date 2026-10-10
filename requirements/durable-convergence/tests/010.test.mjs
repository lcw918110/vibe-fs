import assert from 'node:assert/strict'
import { createHash } from 'node:crypto'
import { chmodSync, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs'
import { execFileSync, spawnSync } from 'node:child_process'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import test from 'node:test'
import { ensure } from '../../../dist/Git/Hook/Surface.js'
import { createBareWorkspace, readRemoteStoreOid } from '../../verification-system/tests/support/dumb-remote.mjs'
import { integrationTest } from '../../verification-system/tests/support/tier-gate.mjs'
import { event } from './support/events.mjs'
import { appendFact, assertFacts, runHook } from './support/hooks.mjs'

const shellQuote = value => `'${value.replaceAll("'", "'\\''")}'`

integrationTest('WHAT[durable-convergence-010] actual clean pre-push does no transport despite unseen remote progress and local change resumes convergence', async () => {
  const workspace = createBareWorkspace(['left', 'right'])
  try {
    const left = workspace.client('left')
    const right = workspace.client('right')
    const a = event('a'.repeat(40))
    const b = event('b'.repeat(40))
    const c = event('c'.repeat(40))
    await appendFact(left, 'writer-left', a)
    runHook(left)
    const first = readRemoteStoreOid(workspace.bare)
    execFileSync('git', ['-C', left, 'update-ref', 'refs/wanxiang/remotes/origin/store', first])
    await appendFact(right, 'writer-right', b)
    runHook(right)
    const unseen = readRemoteStoreOid(workspace.bare)
    assert.notEqual(unseen, first)

    const bin = join(workspace.root, 'bin')
    const calls = join(workspace.root, 'git-calls')
    const realGit = execFileSync('which', ['git'], { encoding: 'utf8' }).trim()
    mkdirSync(bin)
    writeFileSync(join(bin, 'git'), `#!/usr/bin/env node
const fs = require('node:fs')
const { spawnSync } = require('node:child_process')
const args = process.argv.slice(2)
fs.appendFileSync(process.env.WXS_GIT_CALLS, JSON.stringify(args) + '\\n')
const result = spawnSync(process.env.WXS_REAL_GIT, args, { stdio: 'inherit', env: process.env })
process.exit(result.status ?? 1)
`)
    chmodSync(join(bin, 'git'), 0o755)
    const environment = { PATH: `${bin}:${process.env.PATH}`, WXS_GIT_CALLS: calls, WXS_REAL_GIT: realGit }
    const transportCalls = () => readFileSync(calls, 'utf8').trim().split('\n').filter(Boolean)
      .map(line => JSON.parse(line)).filter(args => args.some(arg => ['push', 'fetch', 'ls-remote'].includes(arg)))
    runHook(left, 'pre-push', 'origin', '', environment)
    assert.deepEqual(transportCalls(), [])
    assert.equal(readRemoteStoreOid(workspace.bare), unseen)

    await appendFact(left, 'writer-left-next', c)
    writeFileSync(calls, '')
    runHook(left, 'pre-push', 'origin', '', environment)
    assert.ok(transportCalls().some(args => args.includes('push')), 'positive control: the same observer sees actual transport')
    assertFacts(left, [a, b, c])
  } finally {
    workspace.cleanup()
  }
})

test.todo('WHAT[durable-convergence-010] actual changed-file sync reads and validates only changed writer and payload bytes including retention expiry (GAP-151)')

test('WHAT[durable-convergence-010] hook installer enables repo-local SSH multiplex without clobbering ssh identity options', () => {
  const repo = mkdtempSync(join(tmpdir(), 'wxs-hook-ssh-mux-'))

  try {
    execFileSync('git', ['init', '--quiet', repo])
    const commonDir = execFileSync('git', ['-C', repo, 'rev-parse', '--path-format=absolute', '--git-common-dir'], { encoding: 'utf8' }).trim()
    const wrapper = join(commonDir, 'wanxiangshu', 'ssh-command')
    const base = 'ssh -F /dev/null -i /tmp/wxs-test-key'
    execFileSync('git', ['-C', repo, 'config', '--local', 'core.sshCommand', base])

    assert.equal(ensure(repo), true, 'hook ensure failed')
    const configured = execFileSync('git', ['-C', repo, 'config', '--local', '--get', 'core.sshCommand'], { encoding: 'utf8' }).trim()
    const wrapperBody = readFileSync(wrapper, 'utf8')

    assert.match(configured, /wanxiangshu\/ssh-command/)
    assert.doesNotMatch(configured, /ControlMaster|ControlPath/)
    assert.match(wrapperBody, /ssh -F \/dev\/null -i \/tmp\/wxs-test-key\b/)
    assert.match(wrapperBody, /ControlMaster=auto/)
    assert.match(wrapperBody, /ControlPath=.*wanxiang-ssh-[0-9a-f]{12}\/ssh-%C/)
    assert.match(wrapperBody, /mkdir -p/)

    assert.equal(ensure(repo), true, 'second hook ensure failed')
    const configuredAgain = execFileSync('git', ['-C', repo, 'config', '--local', '--get', 'core.sshCommand'], { encoding: 'utf8' }).trim()
    assert.equal(configuredAgain, configured, 'repeated ensure must not stack SSH multiplex options')
    assert.equal(readFileSync(wrapper, 'utf8'), wrapperBody, 'repeated ensure must keep the owned SSH wrapper stable')
  } finally {
    rmSync(repo, { recursive: true, force: true })
  }
})

test('WHAT[durable-convergence-010] hook installer migrates the obsolete long repo-local control socket path', () => {
  const repo = mkdtempSync(join(tmpdir(), 'wxs-hook-ssh-migrate-'))

  try {
    execFileSync('git', ['init', '--quiet', repo])
    const commonDir = execFileSync('git', ['-C', repo, 'rev-parse', '--path-format=absolute', '--git-common-dir'], { encoding: 'utf8' }).trim()
    const base = 'ssh -F /dev/null -i /tmp/wxs-test-key'
    const legacy = `${base} -o ControlMaster=auto -o ControlPersist=15s -o 'ControlPath=${join(commonDir, 'wanxiang', 'ssh-%C')}'`
    execFileSync('git', ['-C', repo, 'config', '--local', 'core.sshCommand', legacy])

    assert.equal(ensure(repo), true, 'hook ensure failed')
    const configured = execFileSync('git', ['-C', repo, 'config', '--local', '--get', 'core.sshCommand'], { encoding: 'utf8' }).trim()
    const wrapper = join(commonDir, 'wanxiangshu', 'ssh-command')
    const wrapperBody = readFileSync(wrapper, 'utf8')
    assert.match(configured, /wanxiangshu\/ssh-command/)
    assert.match(wrapperBody, /^#!\/bin\/sh/m)
    assert.match(wrapperBody, /ssh -F \/dev\/null -i \/tmp\/wxs-test-key\b/)
    assert.match(wrapperBody, /ControlPath=.*wanxiang-ssh-[0-9a-f]{12}\/ssh-%C/)
    assert.doesNotMatch(configured, new RegExp(`${commonDir.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}/wanxiang/ssh-%C`))
  } finally {
    rmSync(repo, { recursive: true, force: true })
  }
})

test('WHAT[durable-convergence-010] hook installer migrates the ephemeral tmp-directory path and recreates it at SSH invocation', () => {
  const repo = mkdtempSync(join(tmpdir(), 'wxs-hook-ssh-ephemeral-migrate-'))
  let socketDir

  try {
    execFileSync('git', ['init', '--quiet', repo])
    const commonDir = execFileSync('git', ['-C', repo, 'rev-parse', '--path-format=absolute', '--git-common-dir'], { encoding: 'utf8' }).trim()
    const repoKey = createHash('sha256').update(commonDir).digest('hex').slice(0, 12)
    socketDir = join(tmpdir(), `wanxiang-ssh-${repoKey}`)
    const observedArgs = join(repo, 'ssh-args')
    const fakeSsh = join(repo, 'fake-ssh')
    writeFileSync(fakeSsh, `#!/bin/sh\nprintf '%s\\n' "$@" > ${shellQuote(observedArgs)}\n`)
    chmodSync(fakeSsh, 0o755)
    const base = fakeSsh
    const ephemeral = `${base} -o ControlMaster=auto -o ControlPersist=15s -o 'ControlPath=${join(tmpdir(), `wanxiang-ssh-${repoKey}`, 'ssh-%C')}'`
    execFileSync('git', ['-C', repo, 'config', '--local', 'core.sshCommand', ephemeral])

    assert.equal(ensure(repo), true, 'hook ensure failed')
    const configured = execFileSync('git', ['-C', repo, 'config', '--local', '--get', 'core.sshCommand'], { encoding: 'utf8' }).trim()
    const wrapper = join(commonDir, 'wanxiangshu', 'ssh-command')
    assert.match(configured, /wanxiangshu\/ssh-command/)
    assert.doesNotMatch(configured, /wanxiang-ssh-[0-9a-f]{12}\/ssh-%C/)

    rmSync(socketDir, { recursive: true, force: true })
    assert.equal(existsSync(socketDir), false)
    const invoked = spawnSync(wrapper, ['example.test', 'git-receive-pack repo.git'], { encoding: 'utf8' })
    assert.equal(invoked.status, 0, invoked.stderr || invoked.stdout)
    assert.equal(existsSync(socketDir), true, 'SSH wrapper must recreate its private multiplex directory at invocation time')
    assert.equal(statSync(socketDir).mode & 0o777, 0o700)
    const args = readFileSync(observedArgs, 'utf8')
    assert.match(args, /ControlMaster=auto/)
    assert.match(args, /ControlPersist=\d+s/)
    assert.match(args, new RegExp(`ControlPath=${socketDir.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}/ssh-%C`))
    assert.match(args, /example\.test/)
  } finally {
    if (socketDir) rmSync(socketDir, { recursive: true, force: true })
    rmSync(repo, { recursive: true, force: true })
  }
})

test('WHAT[durable-convergence-010] hook installer drops the deleted legacy wrapper from the chain and rewrites a stale owned wrapper', () => {
  const repo = mkdtempSync(join(tmpdir(), 'wxs-hook-ssh-legacy-wrapper-'))

  try {
    execFileSync('git', ['init', '--quiet', repo])
    const commonDir = execFileSync('git', ['-C', repo, 'rev-parse', '--path-format=absolute', '--git-common-dir'], { encoding: 'utf8' }).trim()
    const wrapper = join(commonDir, 'wanxiangshu', 'ssh-command')
    const legacyWrapper = join(commonDir, 'wanxiang', 'ssh-command')
    const legacySuffix = `-o ControlMaster=auto -o ControlPersist=15s -o 'ControlPath=${join(commonDir, 'wanxiang', 'ssh-%C')}'`

    execFileSync('git', ['-C', repo, 'config', '--local', 'core.sshCommand', `${legacyWrapper} ${legacySuffix}`])
    assert.equal(ensure(repo), true, 'hook ensure failed')
    const configured = execFileSync('git', ['-C', repo, 'config', '--local', '--get', 'core.sshCommand'], { encoding: 'utf8' }).trim()
    const wrapperBody = readFileSync(wrapper, 'utf8')
    assert.match(configured, /wanxiangshu\/ssh-command/)
    assert.doesNotMatch(wrapperBody, /wanxiang\/ssh-command/)
    assert.doesNotMatch(wrapperBody, /wanxiang\/ssh-%C/)
    assert.match(wrapperBody, /^ssh -o ControlMaster=auto/m)

    writeFileSync(wrapper, `#!/bin/sh\n# wanxiang-hook-dispatcher ssh-command\nset -eu\nexec '${legacyWrapper}' -o ControlMaster=auto -o ControlPersist=15s -o 'ControlPath=${join(commonDir, 'wanxiang', 'ssh-%C')}' "$@"\n`)
    assert.equal(ensure(repo), true, 'second hook ensure failed')
    const rewritten = readFileSync(wrapper, 'utf8')
    assert.doesNotMatch(rewritten, /wanxiang\/ssh-command/)
    assert.doesNotMatch(rewritten, /wanxiang\/ssh-%C/)
    assert.match(rewritten, /^ssh -o ControlMaster=auto/m)
  } finally {
    rmSync(repo, { recursive: true, force: true })
  }
})

test('WHAT[durable-convergence-010] hook installer respects user-owned SSH multiplex configuration', () => {
  const repo = mkdtempSync(join(tmpdir(), 'wxs-hook-ssh-user-owned-'))

  try {
    execFileSync('git', ['init', '--quiet', repo])
    const userOwned = 'ssh -o ControlMaster=yes -o ControlPath=/tmp/user-owned-%C'
    execFileSync('git', ['-C', repo, 'config', '--local', 'core.sshCommand', userOwned])
    assert.equal(ensure(repo), true, 'hook ensure failed')
    const configured = execFileSync('git', ['-C', repo, 'config', '--local', '--get', 'core.sshCommand'], { encoding: 'utf8' }).trim()
    assert.equal(configured, userOwned)
  } finally {
    rmSync(repo, { recursive: true, force: true })
  }
})
