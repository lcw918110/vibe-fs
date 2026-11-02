import assert from 'node:assert/strict'
import test from 'node:test'
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import {
  createApi,
  api as apiOf,
} from '../../../dist/Repository/Programming/Js/RuntimeSurface.js'

const sandbox = () => {
  const dir = mkdtempSync(join(tmpdir(), 'wxs-primitives-'))
  return { dir, cleanup: () => rmSync(dir, { recursive: true, force: true }) }
}

test('WHAT[repository-programming-045] cat returns strict UTF-8 content and registers a read snapshot', () => {
  const { dir, cleanup } = sandbox()
  try {
    writeFileSync(join(dir, 'a.txt'), 'alpha beta', 'utf8')
    const binding = createApi(dir)
    const result = apiOf(binding).cat('a.txt')
    assert.equal(result.ok, true)
    assert.equal(result.text, 'alpha beta')

    const changed = apiOf(binding).write('a.txt', 'changed')
    assert.equal(changed.ok, true, 'the registered snapshot is fresh before the write')
    assert.equal(readFileSync(join(dir, 'a.txt'), 'utf8'), 'changed')
  } finally {
    cleanup()
  }
})

test('WHAT[repository-programming-045] cat reports FILE_NOT_FOUND for a missing path', () => {
  const { dir, cleanup } = sandbox()
  try {
    const result = apiOf(createApi(dir)).cat('missing.txt')
    assert.equal(result.ok, false)
    assert.equal(result.code, 'FILE_NOT_FOUND')
  } finally {
    cleanup()
  }
})

test('WHAT[repository-programming-045] cat refuses a snapshot changed after the read', () => {
  const { dir, cleanup } = sandbox()
  try {
    writeFileSync(join(dir, 'a.txt'), 'first', 'utf8')
    const binding = createApi(dir)
    assert.equal(apiOf(binding).cat('a.txt').ok, true)
    writeFileSync(join(dir, 'a.txt'), 'third-party', 'utf8')
    const changed = apiOf(binding).write('a.txt', 'attempt')
    assert.equal(changed.ok, false)
    assert.equal(changed.code, 'FILE_CHANGED')
    assert.equal(readFileSync(join(dir, 'a.txt'), 'utf8'), 'third-party')
  } finally {
    cleanup()
  }
})

test('WHAT[repository-programming-045] ls lists one directory sorted with trailing slash on directories', () => {
  const { dir, cleanup } = sandbox()
  try {
    mkdirSync(join(dir, 'sub'))
    writeFileSync(join(dir, 'b.txt'), 'b', 'utf8')
    writeFileSync(join(dir, 'a.txt'), 'a', 'utf8')
    const result = apiOf(createApi(dir)).ls('.')
    assert.equal(result.ok, true)
    assert.deepEqual(result.entries, ['a.txt', 'b.txt', 'sub/'])
  } finally {
    cleanup()
  }
})

test('WHAT[repository-programming-045] stat reports absence without failing and metadata when present', () => {
  const { dir, cleanup } = sandbox()
  try {
    const binding = createApi(dir)
    const missing = apiOf(binding).stat('missing.txt')
    assert.equal(missing.ok, true)
    assert.equal(missing.stat.exists, false)

    writeFileSync(join(dir, 'a.txt'), '12345', 'utf8')
    const file = apiOf(binding).stat('a.txt')
    assert.equal(file.ok, true)
    assert.equal(file.stat.exists, true)
    assert.equal(file.stat.size, 5)
    assert.equal(file.stat.isDir, false)
    assert.equal(typeof file.stat.mtime, 'string')

    mkdirSync(join(dir, 'sub'))
    const directory = apiOf(binding).stat('sub')
    assert.equal(directory.stat.isDir, true)
  } finally {
    cleanup()
  }
})

test('WHAT[repository-programming-045] mkdir creates one directory and refuses collisions or missing parents', () => {
  const { dir, cleanup } = sandbox()
  try {
    const binding = createApi(dir)
    const created = apiOf(binding).mkdir('generated')
    assert.equal(created.ok, true)
    assert.equal(existsSync(join(dir, 'generated')), true)

    const again = apiOf(binding).mkdir('generated')
    assert.equal(again.ok, false)
    assert.equal(again.code, 'DIRECTORY_EXISTS')

    const orphan = apiOf(binding).mkdir('missing-parent/child')
    assert.equal(orphan.ok, false)
    assert.equal(orphan.code, 'PARENT_DIRECTORY_MISSING')
  } finally {
    cleanup()
  }
})

test('WHAT[repository-programming-045] write creates a missing file in a created directory', () => {
  const { dir, cleanup } = sandbox()
  try {
    const binding = createApi(dir)
    assert.equal(apiOf(binding).mkdir('generated').ok, true)
    const written = apiOf(binding).write('generated/version.txt', '1.2.3\n')
    assert.equal(written.ok, true)
    assert.equal(readFileSync(join(dir, 'generated', 'version.txt'), 'utf8'), '1.2.3\n')
  } finally {
    cleanup()
  }
})
