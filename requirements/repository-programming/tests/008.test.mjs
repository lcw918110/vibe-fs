import test from 'node:test'

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

test('WHAT[repository-programming-008] JS007_bindings_path_supports_navigation_outside_root', () => {
  const { dir, cleanup } = sandbox()
  const outsideFile = join(dir, '..', `outside-${Date.now()}-${Math.random().toString(36).slice(2)}.txt`)
  try {
    const binding = createApi(dir)
    const missing = apiOf(binding).cat(outsideFile)
    assert.equal(missing.ok, false)
    assert.equal(missing.code, 'FILE_NOT_FOUND')

    const written = apiOf(binding).write(outsideFile, 'x')
    assert.equal(written.ok, true)
    assert.equal(readFileSync(outsideFile, 'utf8'), 'x')

    const read = apiOf(binding).cat(outsideFile)
    assert.equal(read.ok, true)
    assert.equal(read.text, 'x')
  } finally {
    try { rmSync(outsideFile, { force: true }) } catch {}
    cleanup()
  }
})
test('WHAT[repository-programming-008] JS007_bindings_glob_lists_matching_paths', async () => {
  const { dir, cleanup } = sandbox()
  try {
    mkdirSync(join(dir, 'src'))
    writeFileSync(join(dir, 'src', 'a.fs'), 'x', 'utf8')
    writeFileSync(join(dir, 'src', 'b.txt'), 'y', 'utf8')
    const api = apiOf(createApi(dir))
    assert.equal(api.glob, undefined)
    const result = api.ls('src')
    assert.equal(result.ok, true)
    assert.deepEqual(result.entries.filter((e) => e.endsWith('.fs')).map((e) => `src/${e}`), ['src/a.fs'])
    assert.equal('truncated' in result, false)
  } finally {
    cleanup()
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

test('WHAT[repository-programming-008] JS007_glob_deterministic_enumeration', async () => {
  const { dir, cleanup } = sandbox()
  try {
    mkdirSync(join(dir, 'src'))
    mkdirSync(join(dir, 'src', 'deep'))
    writeFileSync(join(dir, 'src', 'a.fs'), 'a', 'utf8')
    writeFileSync(join(dir, 'src', 'b.fs'), 'b', 'utf8')
    writeFileSync(join(dir, 'src', 'deep', 'c.fs'), 'c', 'utf8')
    writeFileSync(join(dir, 'readme.md'), 'r', 'utf8')

    const all = unwrap(await glob(dir, '**/*.fs'))
    assert.deepEqual(all.paths, ['src/a.fs', 'src/b.fs', 'src/deep/c.fs'])
    assert.equal('truncated' in all, false)
    const nested = unwrap(await glob(dir, '*.fs'))
    assert.deepEqual(nested.paths, ['src/a.fs', 'src/b.fs', 'src/deep/c.fs'])
    const shallow = unwrap(await glob(dir, 'src/*.fs'))
    assert.deepEqual(shallow.paths, ['src/a.fs', 'src/b.fs'])
    const zeroStar = unwrap(await glob(dir, 'src/**/*.fs'))
    assert.deepEqual(zeroStar.paths, ['src/a.fs', 'src/b.fs', 'src/deep/c.fs'])
  } finally {
    cleanup()
  }
})
test('WHAT[repository-programming-008] JS007_glob_gitignore_skips_git_and_ignored', async () => {
  const { dir, cleanup } = sandbox()
  try {
    mkdirSync(join(dir, '.git', 'objects'), { recursive: true })
    mkdirSync(join(dir, 'dist'))
    mkdirSync(join(dir, 'src'))
    writeFileSync(join(dir, '.git', 'HEAD'), 'ref', 'utf8')
    writeFileSync(join(dir, 'dist', 'out.js'), 'x', 'utf8')
    writeFileSync(join(dir, 'secret.txt'), 's', 'utf8')
    writeFileSync(join(dir, 'src', 'keep.fs'), 'k', 'utf8')
    writeFileSync(join(dir, 'readme.md'), 'r', 'utf8')
    writeFileSync(join(dir, '.gitignore'), 'secret.txt\n/dist/\n', 'utf8')

    const listing = unwrap(await glob(dir, '**/*'))
    const paths = listing.paths
    assert.equal(paths.some((p) => p.startsWith('.git/') || p === '.git'), false)
    assert.equal(paths.includes('secret.txt'), false)
    assert.equal(paths.includes('dist/out.js'), false)
    assert.equal(paths.includes('src/keep.fs'), true)
    assert.equal(paths.includes('readme.md'), true)
    assert.equal(paths.includes('.gitignore'), true)

    const braces = unwrap(await glob(dir, '**/*.{fs,md}'))
    assert.deepEqual(braces.paths, ['readme.md', 'src/keep.fs'])
  } finally {
    cleanup()
  }
})
}
