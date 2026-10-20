import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { mkdtempSync, writeFileSync, rmSync, mkdirSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join } = await import("node:path");
const { createApi, api: apiOf, stagedCount, stagedKinds, run } = await import("../../../dist/Repository/Programming/Js/RuntimeSurface.js");
const { generate } = await import("../../../dist/Repository/Programming/Js/GeneratorSurface.js");

const sandbox = () => {
  const dir = mkdtempSync(join(tmpdir(), 'wxs-bindings-'))
  return { dir, cleanup: () => rmSync(dir, { recursive: true, force: true }) }
}
const coderSurface = () => generate('Coder', ['Read', 'Write', 'Edit', 'Glob', 'Grep'], 'en')

test('WHAT[repository-programming-009] JS010_bindings_grep_returns_matches', async () => {
  const { dir, cleanup } = sandbox()
  try {
    writeFileSync(join(dir, 'a.txt'), 'one two one', 'utf8')
    const api = apiOf(createApi(dir))
    assert.equal(api.js.grep, undefined)
    const listed = api.js.ls('.')
    assert.equal(listed.ok, true)
    const hits = []
    for (const entry of listed.entries.filter((e) => e.endsWith('.txt'))) {
      const read = api.js.cat(entry)
      assert.equal(read.ok, true)
      for (const [begin, end] of api.js.whereAll(read.text, 'one').spans) {
        hits.push({ path: entry, text: read.text.slice(begin, end) })
      }
    }
    assert.deepEqual(hits.map((m) => m.text), ['one', 'one'])
    assert.deepEqual(hits.map((m) => m.path), ['a.txt', 'a.txt'])
  } finally {
    cleanup()
  }
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { mkdtempSync, rmSync, writeFileSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join } = await import("node:path");
const { createApi, api: apiOf } = await import("../../../dist/Repository/Programming/Js/RuntimeSurface.js");

test('WHAT[repository-programming-009] rg binding returns a promise', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'wxs-rg-promise-'))
  try {
    writeFileSync(join(dir, 'a.ts'), 'targetNeedle\n', 'utf8')
    const pending = apiOf(createApi(dir)).js.rg(['targetNeedle', '.'])
    assert.equal(typeof pending.then, 'function')
    const found = await pending
    assert.equal(found.ok, true)
    assert.equal(found.exitCode, 0)
    assert.equal(Array.isArray(found.messages), true)
  } finally {
    rmSync(dir, { recursive: true, force: true })
  }
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join } = await import("node:path");
const { readUtf8, glob, findAnchor, requireUnique, grep, commitPlan, rollbackPlan } = await import("../../../dist/Repository/Programming/Js/FilesystemSurface.js");

const exact = (text) => ({ kind: 'exact', text })
const regex = (text) => ({ kind: 'regex', text })
const sandbox = () => {
  const dir = mkdtempSync(join(tmpdir(), 'wxs-jstools-'))
  return { dir, cleanup: () => rmSync(dir, { recursive: true, force: true }) }
}
const ok = (result) => result.ok
const codeOf = (result) => result.code
const unwrap = (result) => {
  assert.equal(result.ok, true, `expected Ok, got ${JSON.stringify(result.error)}`)
  return result.value
}

test('WHAT[repository-programming-009] JS020_grep_returns_line_column_and_skips_ignored', async () => {
  const { dir, cleanup } = sandbox()
  try {
    mkdirSync(join(dir, 'src'))
    mkdirSync(join(dir, 'dist'))
    writeFileSync(join(dir, 'src', 'a.fs'), 'alpha\nTODO: one\n', 'utf8')
    writeFileSync(join(dir, 'dist', 'skip.js'), 'TODO: hidden\n', 'utf8')
    writeFileSync(join(dir, '.gitignore'), '/dist/\n', 'utf8')

    const listing = unwrap(await grep(dir, regex('TODO:.+'), 'src/**/*.fs'))
    const hits = listing.matches
    assert.equal(hits.length, 1)
    assert.equal(hits[0].path, 'src/a.fs')
    assert.equal(hits[0].line, 2)
    assert.equal(hits[0].column, 1)
    assert.equal(hits[0].text, 'TODO: one')
    assert.equal('truncated' in listing, false)
  } finally {
    cleanup()
  }
})
}
