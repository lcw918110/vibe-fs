import assert from 'node:assert/strict'
import { execFileSync, spawnSync } from 'node:child_process'
import { existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { integrationTest } from '../../verification-system/tests/support/tier-gate.mjs'
import * as change from '../../../dist/Change/Surface.js'
import * as host from '../../../dist/Change/Host/Surface.js'

const git = (cwd, ...args) => execFileSync('git', args, { cwd, encoding: 'utf8' }).trim()

const realRunner = async command => {
  const result = spawnSync(command.fileName, command.args, {
    cwd: command.workingDirectory,
    encoding: 'utf8',
  })
  if (result.error) throw result.error
  return [result.status ?? 1, result.stdout ?? '', result.stderr ?? '']
}

integrationTest('real candidate preparation is repeatable and its fixed commit is published into the target worktree', async () => {
  const root = mkdtempSync(join(tmpdir(), 'wxs-git-publication-'))
  const repo = join(root, 'repo')
  const candidate = join(root, 'candidate')
  const previousGlobalConfig = process.env.GIT_CONFIG_GLOBAL
  const previousNoSystem = process.env.GIT_CONFIG_NOSYSTEM
  try {
    const emptyConfig = join(root, 'empty-git-config')
    writeFileSync(emptyConfig, '')
    process.env.GIT_CONFIG_GLOBAL = emptyConfig
    process.env.GIT_CONFIG_NOSYSTEM = '1'
    execFileSync('git', ['init', '--quiet', '--initial-branch=main', repo])
    git(repo, 'config', 'user.name', 'publication fixture')
    git(repo, 'config', 'user.email', 'publication@example.invalid')
    writeFileSync(join(repo, 'draft.txt'), 'before\n')
    git(repo, 'add', 'draft.txt')
    git(repo, 'commit', '--quiet', '-m', 'seed')

    const port = change.createGit(repo, realRunner)
    const target = await change.gitFreezeTargetBranch(port)
    assert.deepEqual(target, { ok: true, value: 'main' })
    const expected = await change.gitGetTargetHead(port, target.value)
    assert.equal(expected.ok, true, expected.error)
    const created = await change.gitCreateWorktree(port, 'publication-job', candidate, 'refs/heads/main')
    assert.equal(created.ok, true, created.error)

    const draft = Buffer.from('prepared draft — 准备完成\r\n', 'utf8')
    const added = Buffer.from('new candidate file\n', 'utf8')
    writeFileSync(join(candidate, 'draft.txt'), draft)
    writeFileSync(join(candidate, 'added.txt'), added)
    const prepared = await host.finalizeWorktreeWithGit('manager-publication', candidate)
    assert.deepEqual(prepared, { ok: true, error: null })
    const pin = await change.gitReadHead(port, candidate)
    assert.equal(pin.ok, true, pin.error)
    assert.notEqual(pin.value, expected.value)
    assert.equal(git(candidate, 'log', '-1', '--format=%s'), 'candidate: manager-publication')
    assert.equal(git(candidate, 'status', '--porcelain'), '')

    const repeated = await host.finalizeWorktreeWithGit('manager-publication', candidate)
    assert.deepEqual(repeated, { ok: true, error: null })
    assert.deepEqual(await change.gitReadHead(port, candidate), pin, 'nothing to commit keeps the same candidate pin')
    assert.equal(readFileSync(join(repo, 'draft.txt'), 'utf8'), 'before\n')
    assert.equal(existsSync(join(repo, 'added.txt')), false)

    const published = await change.gitFfMerge(port, candidate, target.value, expected.value, pin.value)
    assert.deepEqual(published, { ok: true, value: pin.value })
    assert.deepEqual(await change.gitGetTargetHead(port, target.value), pin)
    assert.equal(git(repo, 'rev-parse', 'HEAD'), pin.value)
    assert.deepEqual(readFileSync(join(repo, 'draft.txt')), draft)
    assert.deepEqual(readFileSync(join(repo, 'added.txt')), added)
    assert.deepEqual(readFileSync(join(candidate, 'draft.txt')), draft)
  } finally {
    if (previousGlobalConfig === undefined) delete process.env.GIT_CONFIG_GLOBAL
    else process.env.GIT_CONFIG_GLOBAL = previousGlobalConfig
    if (previousNoSystem === undefined) delete process.env.GIT_CONFIG_NOSYSTEM
    else process.env.GIT_CONFIG_NOSYSTEM = previousNoSystem
    rmSync(root, { recursive: true, force: true })
  }
})
