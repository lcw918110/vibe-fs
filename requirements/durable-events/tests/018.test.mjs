import assert from 'node:assert/strict'
import { execFileSync } from 'node:child_process'
import { existsSync, mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import test from 'node:test'
import * as eventStore from '../../../dist/Persistence/EventStore/Surface.js'
import * as journal from '../../../dist/Persistence/Journal/Surface.js'

const retiredHook = new URL('../../../resources/git/wanxiang-hook.mjs', import.meta.url)
const hookSource = new URL('../../../src/Wanxiangshu/Git/Hook/Dispatcher.fs', import.meta.url)

test('WHAT[durable-events-018] activation writes only the local ignore file and never installs Git hooks or refspecs', async () => {
  const repo = mkdtempSync(join(tmpdir(), 'wxs-no-hook-'))
  const foreign = '#!/bin/sh\necho foreign\n'
  try {
    execFileSync('git', ['init', '--quiet', repo])
    execFileSync('git', ['-C', repo, 'remote', 'add', 'origin', 'https://example.com/repo.git'])
    const hooks = join(repo, '.git', 'hooks')
    writeFileSync(join(hooks, 'pre-push'), foreign)
    const configBefore = execFileSync('git', ['-C', repo, 'config', '--local', '--list'], { encoding: 'utf8' })
    const excludeBefore = existsSync(join(repo, '.git', 'info', 'exclude'))
      ? readFileSync(join(repo, '.git', 'info', 'exclude'), 'utf8')
      : null

    const root = await journal.JournalSurface_initializeStateRoot(repo)
    const store = eventStore.create(root, 'writer-no-hook')
    const appended = await eventStore.append(store, [{
      id: 'a'.repeat(40),
      stream: 'durable-events-018/local',
      type: 'JobRequested',
      parents: [],
      payload: { n: 1 },
      payloadRefs: [],
    }])
    eventStore.dispose(store)

    assert.equal(appended.ok, true)
    assert.equal(readFileSync(join(root, '.gitignore'), 'utf8').trim(), '*')
    assert.equal(readFileSync(join(hooks, 'pre-push'), 'utf8'), foreign, 'an existing foreign hook must stay untouched')
    assert.equal(existsSync(join(hooks, 'reference-transaction')), false)
    assert.equal(execFileSync('git', ['-C', repo, 'config', '--local', '--list'], { encoding: 'utf8' }), configBefore)
    assert.equal(
      existsSync(join(repo, '.git', 'info', 'exclude'))
        ? readFileSync(join(repo, '.git', 'info', 'exclude'), 'utf8')
        : null,
      excludeBefore,
    )
    assert.equal(existsSync(retiredHook), false, 'the retired sync runner must not return')
    assert.equal(existsSync(hookSource), false, 'the retired hook installer must not return')
    assert.equal(readdirSync(join(root, 'wanxiangshu', 'events')).some(name => name.endsWith('.ndjson')), true)
  } finally {
    rmSync(repo, { recursive: true, force: true })
  }
})

