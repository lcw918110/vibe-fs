import assert from 'node:assert/strict'
import test from 'node:test'
import { existsSync, mkdirSync, mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { stateRoot } from '../../../scripts/lib/state-root.mjs'

test('diagnostic state root preserves the native startup error when Git is missing', async () => {
  const root = mkdtempSync(join(tmpdir(), 'state-root-no-git-'))
  const workspace = join(root, 'workspace')
  const emptyPath = join(root, 'empty-bin')
  mkdirSync(workspace)
  mkdirSync(emptyPath)
  const previousPath = process.env.PATH
  try {
    process.env.PATH = emptyPath
    let failure
    try {
      await stateRoot(workspace)
    } catch (error) {
      failure = error
    }
    assert.equal(existsSync(join(workspace, '.Herdchestra')), false)
    assert.ok(failure, 'a missing Git executable must fail rather than select a state root')
    assert.equal(failure.code, 'ENOENT')
    assert.equal(failure.path, 'git')
    assert.equal(failure.syscall, 'spawn git')
  } finally {
    if (previousPath === undefined) delete process.env.PATH
    else process.env.PATH = previousPath
    rmSync(root, { recursive: true, force: true })
  }
})
