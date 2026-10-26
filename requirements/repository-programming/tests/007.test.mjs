import { closePrograms } from '../../../dist/Repository/Programming/Js/WorkflowSurface.js'
import test from 'node:test'

test('WHAT[repository-programming-007] an acquired view remains unchanged after rewrite and commit', async () => {
  const { default: assert } = await import('node:assert/strict')
  const { mkdtempSync, readFileSync, writeFileSync, rmSync } = await import('node:fs')
  const { tmpdir } = await import('node:os')
  const { join } = await import('node:path')
  const { parse } = await import('smol-toml')
  const { run, caseName, render } = await import('../../../dist/Repository/Programming/Js/WorkflowSurface.js')
  const dir = mkdtempSync(join(tmpdir(), 'wxs-immutable-view-'))
  try {
    writeFileSync(join(dir, 'a.txt'), 'original')
    const outcome = await run(dir, 'Engineer', 'en', `class Js extends JsProgram {
      async run() {
        const snapshot = await this.cat('a.txt');
        const before = snapshot;
        this.write('a.txt', 'mutated');
        return { before, after: snapshot };
      }
    }`, 2000, Date.now() + 60_000, 1 << 20, null)
    assert.equal(caseName(outcome), 'Succeeded')
    assert.deepEqual(parse(render(outcome)).data, { before: 'original', after: 'original' })
    assert.equal(readFileSync(join(dir, 'a.txt'), 'utf8'), 'mutated')
  } finally {
    rmSync(dir, { recursive: true, force: true })
  }
})

// Superseded note: `FileToolsSurface` (`read`/`write`/`edit`) was retired with the direct
// filesystem tools; `cat`/`where`/`write`/`ls`/`mv`/`rm` on the js programming surface are
// the contract now.
{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { failureCatalog, validateAnchorDeclaration: validateDeclaration, validateAnchorOccurrence: validateOccurrence } = await import("../../../dist/Repository/Programming/Js/TransactionSurface.js");

const declaration = (spec, occurrence) => ({ ...spec, occurrence })
const ok = (result) => result.ok
const exact = (text) => ({ kind: 'exact', text })
const regex = (text) => ({ kind: 'regex', text })

test('WHAT[repository-programming-007] JS006_empty_anchor_declaration_is_refused', () => {
  assert.equal(ok(validateDeclaration(declaration(exact(''), undefined))), false)
  assert.equal(ok(validateDeclaration(declaration(regex(''), undefined))), false)
  assert.equal(ok(validateDeclaration(declaration(exact('hello'), undefined))), true)
  assert.equal(ok(validateDeclaration(declaration(regex('^\\s*$'), undefined))), true)
})
test('WHAT[repository-programming-007] JS006_non_positive_occurrence_is_refused', () => {
  assert.equal(ok(validateOccurrence(declaration(exact('x'), 0))), false)
  assert.equal(ok(validateOccurrence(declaration(exact('x'), -1))), false)
  assert.equal(ok(validateOccurrence(declaration(exact('x'), 1))), true)
  assert.equal(ok(validateOccurrence(declaration(exact('x'), undefined))), true)
})
}

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

test('WHAT[repository-programming-007] JS005_bindings_file_reads_utf8', () => {
  const { dir, cleanup } = sandbox()
  try {
    writeFileSync(join(dir, 'a.txt'), 'hello', 'utf8')
    const binding = createApi(dir)
    const result = apiOf(binding).cat('a.txt')
    assert.equal(result.ok, true)
    assert.equal(result.text, 'hello')
    const missing = apiOf(binding).cat('nope.txt')
    assert.equal(missing.ok, false)
    assert.equal(missing.code, 'FILE_NOT_FOUND')
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

test('WHAT[repository-programming-007] JS005_readUtf8_reads_and_classifies', () => {
  const { dir, cleanup } = sandbox()
  try {
    const file = join(dir, 'a.txt')
    writeFileSync(file, 'hello', 'utf8')
    assert.equal(unwrap(readUtf8(file)), 'hello')
    assert.equal(codeOf(readUtf8(join(dir, 'missing.txt'))), 'FILE_NOT_FOUND')
    // invalid UTF-8 bytes → INVALID_UTF8, never silent replacement
    writeFileSync(join(dir, 'bad.bin'), Buffer.from([0xff, 0xfe, 0xfd]))
    assert.equal(codeOf(readUtf8(join(dir, 'bad.bin'))), 'INVALID_UTF8')
  } finally {
    cleanup()
  }
})
test('WHAT[repository-programming-007] JS006_findAnchor_ordered_string_and_regex', () => {
  const text = 'a b a b a'
  // exact, occurrence 1/2/3
  assert.deepEqual(unwrap(findAnchor(text, exact('a'), 1)), [0, 1])
  assert.deepEqual(unwrap(findAnchor(text, exact('a'), 2)), [4, 5])
  assert.deepEqual(unwrap(findAnchor(text, exact('a'), 3)), [8, 9])
  assert.equal(codeOf(findAnchor(text, exact('a'), 4)), 'ANCHOR_NOT_FOUND')
  // regex
  assert.deepEqual(unwrap(findAnchor(text, regex('b a'), 1)), [2, 5])
  assert.deepEqual(unwrap(findAnchor(text, regex('b a'), 2)), [6, 9])
  // zero-width: ^ anchors at absolute file start
  assert.deepEqual(unwrap(findAnchor(text, regex('^'), 1)), [0, 0])
  assert.equal(codeOf(findAnchor(text, regex('('), 1)), 'INVALID_ANCHOR_PATTERN')
})
test('WHAT[repository-programming-007] JS006_requireUnique_refuses_ambiguous_anchors', () => {
  const text = 'x y x'
  assert.deepEqual(unwrap(requireUnique(text, exact('y'))), [2, 3])
  assert.equal(codeOf(requireUnique(text, exact('x'))), 'ANCHOR_NOT_UNIQUE')
  assert.equal(codeOf(requireUnique(text, exact('z'))), 'ANCHOR_NOT_FOUND')
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { mkdtempSync, mkdirSync, readFileSync, rmSync, writeFileSync, existsSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join } = await import("node:path");
const { randomUUID } = await import("node:crypto");
const { parse: parseToml } = await import("smol-toml");
const { generate } = await import("../../../dist/Repository/Programming/Js/GeneratorSurface.js");
const { run, execute, runObserved, caseName, rewritten, created, failureCode, parkedPayload, render } = await import("../../../dist/Repository/Programming/Js/WorkflowSurface.js");
const { create: createEventStore, dispose: disposeEventStore } = await import("../../../dist/Persistence/EventStore/Surface.js");
const { pending } = await import("../../../dist/Repository/Programming/Js/TransactionSurface.js");

const sandbox = () => {
  const dir = mkdtempSync(join(tmpdir(), 'wxs-workflow-'))
  return { dir, cleanup: () => rmSync(dir, { recursive: true, force: true }) }
}
const localStore = () => {
  const owned = mkdtempSync(join(tmpdir(), 'wxs-workflow-events-'))
  const commonDir = join(owned, '.Herdchestra')
  mkdirSync(commonDir, { recursive: true })
  const handle = createEventStore(commonDir, randomUUID().replaceAll('-', ''))
  return { handle, close: () => { disposeEventStore(handle); rmSync(owned, { recursive: true, force: true }) } }
}
const coderSurface = () => generate('Coder', ['Read', 'Write', 'Edit', 'Glob', 'Grep'], 'en')
const runWorkflow = async (dir, program, { deadlineMs = 2000, store = null } = {}) => ({
  outcome: await run(dir, 'Coder', 'en', program, deadlineMs, Date.now() + 60_000, 1 << 20, store),
  surface: coderSurface(),
})

test('WHAT[repository-programming-007] JS006_missing_anchor_reason_names_declaration_path_and_pattern', async () => {
  const { dir, cleanup } = sandbox()
  try {
    writeFileSync(join(dir, 'a.txt'), 'hello world', 'utf8')
    const program = `class Js extends JsProgram {
  async run() {
    const text = await this.cat('a.txt');
    await this.where(text, '## JS-007 FileView.text()');
    return { ok: true };
  }
}`
    const outcome = await execute(dir, 'Coder', 'en', 'sess-007', { name: "missing-anchor-007", program, intent: "Wait for the anchor lookup and preserve its original pause." }, 2000, Date.now() + 60_000, 1 << 20, null)
    assert.equal(caseName(outcome), 'Parked')
    const payload = parkedPayload(outcome).payload
    assert.equal(payload.needle, '## JS-007 FileView.text()')
    assert.deepEqual(payload.candidates, [])
    assert.equal(payload.nearest.text, 'hello world')
  } finally {
    await closePrograms()
    cleanup()
  }
})
test('WHAT[repository-programming-007] JS005_offset_anchor_clips_to_closed_file_range', async () => {
  const { dir, cleanup } = sandbox()
  try {
    writeFileSync(join(dir, 'a.txt'), 'hello world', 'utf8')
    const program = `class Js extends JsProgram {
  async run() {
    const text = await this.cat('a.txt');
    const [h, hend] = await this.where(text, 'hello');
    const clip = (n) => Math.min(Math.max(0, n), text.length);
    return {
      window: text.slice(h, clip(h + 6)),
      before: text.slice(clip(hend - 5), hend),
      clippedEnd: text.slice(h, clip(h + 1000)),
      clippedStart: text.slice(clip(0 - 1000), h),
      eof: text.slice(clip(text.length + 100), clip(text.length + 200)).length,
    };
  }
}`
    const { outcome } = await runWorkflow(dir, program)
    const doc = parseToml(render(outcome))
    assert.equal(doc.data.window, 'hello ')
    assert.equal(doc.data.before, 'hello')
    assert.equal(doc.data.clippedEnd, 'hello world')
    assert.equal(doc.data.clippedStart, '')
    assert.equal(doc.data.eof, 0)
  } finally {
    await closePrograms()
    cleanup()
  }
})
test('WHAT[repository-programming-007] JS005_offset_N_is_string_index_not_line_number', async () => {
  const { dir, cleanup } = sandbox()
  try {
    writeFileSync(join(dir, 'a.txt'), 'ab\ncd\nef', 'utf8')
    const program = `class Js extends JsProgram {
  async run() {
    const text = await this.cat('a.txt');
    const [cdBegin] = await this.where(text, 'cd');
    return {
      twoUnits: text.slice(0, 2),
      threeLen: text.slice(0, 3).length,
      cdBegin,
    };
  }
}`
    const { outcome } = await runWorkflow(dir, program)
    const doc = parseToml(render(outcome))
    assert.equal(doc.data.twoUnits, 'ab')
    assert.equal(doc.data.threeLen, 3)
    assert.equal(doc.data.cdBegin, 3)
  } finally {
    await closePrograms()
    cleanup()
  }
})
}
