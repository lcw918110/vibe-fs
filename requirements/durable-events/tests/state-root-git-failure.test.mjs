import assert from 'node:assert/strict'
import test from 'node:test'
import { chmodSync, existsSync, mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { stateRoot } from '../../../scripts/lib/state-root.mjs'
import * as runtimePath from '../../../dist/Persistence/Journal/RuntimePath.js'

for (const [entry, locate] of [
  ['diagnostics', workspace => stateRoot(workspace)],
  ['async runtime', workspace => runtimePath.stateRoot(workspace)],
  ['sync runtime', async workspace => runtimePath.stateRootSync(workspace)],
]) {
  test(`strict ${entry} state lookup propagates a Git permission failure without choosing a fallback`, async () => {
    const root = mkdtempSync(join(tmpdir(), 'state-root-git-failure-'))
    const workspace = join(root, 'workspace')
    const executableDirectory = join(root, 'bin')
    mkdirSync(workspace)
    mkdirSync(executableDirectory)
    const executable = join(executableDirectory, 'git')
    writeFileSync(executable, '#!/bin/sh\nprintf "%s\\n" "fatal: permission denied while reading worktree metadata" >&2\nexit 128\n')
    chmodSync(executable, 0o755)
    const previousPath = process.env.PATH
    try {
      process.env.PATH = executableDirectory
      await assert.rejects(() => locate(workspace), /permission denied while reading worktree metadata/)
      assert.equal(existsSync(join(workspace, '.Herdchestra')), false)
      assert.equal(existsSync(join(root, '.Herdchestra')), false)
    } finally {
      if (previousPath === undefined) delete process.env.PATH
      else process.env.PATH = previousPath
      rmSync(root, { recursive: true, force: true })
    }
  })
}
