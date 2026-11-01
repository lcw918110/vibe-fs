import test from 'node:test'

// Superseded note: `FileToolsSurface` (`read`/`write`/`edit`) was retired with the direct
// filesystem tools; `cat`/`where`/`write`/`ls`/`mv`/`rm` on the js programming surface are
// the contract now.
{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { mkdtempSync, writeFileSync, readFileSync, rmSync, mkdirSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join } = await import("node:path");
const { createApi, api: apiOf, run } = await import("../../../dist/Repository/Programming/Js/RuntimeSurface.js");
const { generate } = await import("../../../dist/Repository/Programming/Js/GeneratorSurface.js");

const sandbox = () => {
  const dir = mkdtempSync(join(tmpdir(), 'wxs-bindings-'))
  return { dir, cleanup: () => rmSync(dir, { recursive: true, force: true }) }
}
const coderSurface = () => generate('Coder', ['Read', 'Write', 'Edit', 'Glob', 'Grep'], 'en')

test('WHAT[repository-programming-010] JS008_012_bindings_rewrite_requires_existing_target', () => {
  const { dir, cleanup } = sandbox()
  try {
    writeFileSync(join(dir, 'a.txt'), 'old text', 'utf8')
    const binding = createApi(dir)
    assert.equal(apiOf(binding).edit, undefined, 'legacy this.edit is retired from bindings')
    assert.equal(apiOf(binding).cat('a.txt').ok, true)
    const result = apiOf(binding).write('a.txt', 'new text')
    assert.equal(result.ok, true)
    assert.equal(readFileSync(join(dir, 'a.txt'), 'utf8'), 'new text')
    writeFileSync(join(dir, 'a.txt'), 'concurrent change', 'utf8')
    const stale = apiOf(binding).write('a.txt', 'stale overwrite')
    assert.equal(stale.ok, false)
    assert.equal(stale.code, 'FILE_CHANGED')
  } finally {
    cleanup()
  }
})
test('WHAT[repository-programming-010] JS009_012_bindings_write_stages_create', () => {
  const { dir, cleanup } = sandbox()
  try {
    const binding = createApi(dir)
    const result = apiOf(binding).write('new.txt', 'fresh')
    assert.equal(result.ok, true)
    assert.equal(readFileSync(join(dir, 'new.txt'), 'utf8'), 'fresh')
  } finally {
    cleanup()
  }
})
test('WHAT[repository-programming-010] JS009_bindings_write_overwrites_an_existing_target_as_is', () => {
  const { dir, cleanup } = sandbox()
  try {
    writeFileSync(join(dir, 'existing.txt'), 'external', 'utf8')
    const binding = createApi(dir)
    const result = apiOf(binding).write('existing.txt', 'tool')
    assert.equal(result.ok, true)
    assert.equal(readFileSync(join(dir, 'existing.txt'), 'utf8'), 'tool')
  } finally {
    cleanup()
  }
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { validateSingleIntent, validateTargets, validateFreshness, preflight, commitPlan, rollbackPlan } = await import("../../../dist/Repository/Programming/Js/TransactionSurface.js");

const ok = (result) => result.ok
const codeOf = (result) => result.code
const rewrite = (path, originalText, newText) => ({ kind: 'rewrite', path, originalText, newText })
const create = (path, text) => ({ kind: 'create', path, text })
const current = { 'a.txt': 'current' }

test('WHAT[repository-programming-010] JS026_same_path_once_rejects_duplicate_mutation_targets', () => {
  const dup = [rewrite('a.txt', 'x', 'y'), create('a.txt', 'z')]
  const result = validateSingleIntent(dup)
  assert.equal(ok(result), false)
  assert.equal(codeOf(result), 'DUPLICATE_MUTATION_TARGET')

  const distinct = [rewrite('a.txt', 'x', 'y'), create('b.txt', 'z')]
  assert.equal(ok(validateSingleIntent(distinct)), true)
})
test('WHAT[repository-programming-010] JS008_009_rewrite_requires_existing_target_create_requires_missing', () => {
  const existing = ['a.txt']
  assert.equal(ok(validateTargets(existing, [rewrite('a.txt', 'x', 'y')])), true)
  assert.equal(codeOf(validateTargets(existing, [rewrite('missing.txt', 'x', 'y')])), 'FILE_NOT_FOUND')
  assert.equal(ok(validateTargets(existing, [create('new.txt', 'n')])), true)
  assert.equal(codeOf(validateTargets(existing, [create('a.txt', 'n')])), 'FILE_ALREADY_EXISTS')
})
}
