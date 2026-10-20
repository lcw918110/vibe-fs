import assert from 'node:assert/strict'
import test from 'node:test'
import { execFileSync } from 'node:child_process'
import { existsSync, mkdirSync, mkdtempSync, readFileSync, realpathSync, rmSync, symlinkSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import path from 'node:path'
import { stateRoot, initializeStateRoot } from '../../../scripts/lib/state-root.mjs'
import * as journal from '../../../dist/Persistence/Journal/Surface.js'
import * as store from '../../../dist/Persistence/EventStore/Surface.js'
import * as retention from '../../../dist/Persistence/EventStore/RetentionSurface.js'
import * as lifecycle from '../../../dist/Mission/Lifecycle/Surface.js'

function git(directory, ...args) {
  return execFileSync('git', ['-c', 'core.hooksPath=/dev/null', '-c', 'commit.gpgsign=false', '-C', directory, ...args], {
    encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'],
  }).trim()
}

function fixture(t) {
  const base = realpathSync(mkdtempSync(path.join(tmpdir(), 'wxs-local-state-')))
  t.after(() => rmSync(base, { recursive: true, force: true }))
  const repo = path.join(base, 'repo')
  mkdirSync(repo)
  git(repo, 'init', '--initial-branch=main')
  git(repo, '-c', 'user.name=State test', '-c', 'user.email=state@example.invalid', 'commit', '--allow-empty', '-m', 'fixture')
  return { base, repo }
}

test('WHAT[durable-events-028] runtime and diagnostics use the primary worktree state without editing Git configuration', async t => {
  const { base, repo } = fixture(t)
  const linked = path.join(base, 'linked')
  git(repo, 'worktree', 'add', '--detach', linked)
  const nested = path.join(linked, 'nested')
  mkdirSync(nested)
  const config = path.join(repo, '.git', 'config')
  const before = readFileSync(config)
  const expected = path.join(repo, '.Herdchestra')
  for (const entry of [repo, linked, nested]) {
    assert.equal(await stateRoot(entry), expected)
    assert.equal(await journal.JournalSurface_initializeStateRoot(entry), expected)
  }
  assert.equal(readFileSync(path.join(expected, '.gitignore'), 'utf8'), '*\n')
  assert.deepEqual(readFileSync(config), before)
  assert.equal(existsSync(path.join(linked, '.Herdchestra')), false)
  assert.equal(git(repo, 'status', '--porcelain'), '')
})

test('WHAT[durable-events-028] non-Git directories and independent repositories keep separate state', async t => {
  const { base, repo } = fixture(t)
  const plain = path.join(base, 'plain')
  const other = path.join(base, 'other')
  mkdirSync(plain)
  mkdirSync(other)
  git(other, 'init')
  for (const entry of [repo, plain, other]) {
    assert.equal(await stateRoot(entry), path.join(entry, '.Herdchestra'))
  }
})

test('WHAT[durable-events-028] bare repositories and direct Git paths are not state roots', async t => {
  const { base, repo } = fixture(t)
  const bare = path.join(base, 'bare')
  mkdirSync(bare)
  git(bare, 'init', '--bare')
  await assert.rejects(() => stateRoot(bare), /bare repository/)
  const metadata = path.join(repo, '.git')
  await assert.rejects(() => stateRoot(metadata), /inside .git/)
  await assert.rejects(() => journal.JournalSurface_initializeStateRoot(metadata), /inside .git/)
  assert.throws(() => lifecycle.journalPath(metadata), /inside .git/)
  assert.throws(() => retention.retainedWriterIdsAt(metadata, Date.now()), /inside .git/)
  assert.throws(() => store.create(metadata, 'forbidden'), /inside .git/)
  assert.equal(existsSync(path.join(metadata, 'wanxiangshu')), false)
})

test('WHAT[durable-events-028] corrupted retired files cannot influence a fresh local event stream', async t => {
  const { repo } = fixture(t)
  const legacy = path.join(repo, '.git', 'wanxiangshu', 'events')
  mkdirSync(legacy, { recursive: true })
  const evidence = path.join(legacy, 'old-0000.ndjson')
  writeFileSync(evidence, 'deliberately invalid retired bytes\n')
  const root = await journal.JournalSurface_initializeStateRoot(repo)
  const current = store.create(root, 'fresh')
  try {
    const id = 'a'.repeat(40)
    assert.equal((await store.append(current, [{ id, stream: 'new/state', type: 'JobRequested', parents: [], payload: { id }, payloadRefs: [] }])).ok, true)
    assert.deepEqual(store.heads(current, 'new/state'), [id])
    assert.equal(readFileSync(evidence, 'utf8'), 'deliberately invalid retired bytes\n')
    assert.equal(existsSync(path.join(root, 'wanxiangshu', 'events', 'fresh-0000.ndjson')), true)
  } finally {
    store.dispose(current)
  }
})

test('WHAT[durable-events-028] symlink state and ignore files cannot redirect persistence into Git', async t => {
  const { repo } = fixture(t)
  const target = path.join(repo, '.Herdchestra')
  symlinkSync(path.join(repo, '.git'), target, 'junction')
  await assert.rejects(() => stateRoot(repo), /real directory/)
  await assert.rejects(() => journal.JournalSurface_initializeStateRoot(repo), /real directory/)
  assert.throws(() => lifecycle.journalPath(target), /into .git/)
  rmSync(target)
  mkdirSync(target)
  const exclude = path.join(repo, '.git', 'info', 'exclude')
  const before = readFileSync(exclude)
  symlinkSync(exclude, path.join(target, '.gitignore'), 'file')
  await assert.rejects(() => initializeStateRoot(repo), /regular file/)
  await assert.rejects(() => journal.JournalSurface_initializeStateRoot(repo), /regular file/)
  assert.deepEqual(readFileSync(exclude), before)
})
