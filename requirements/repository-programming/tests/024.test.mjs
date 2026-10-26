import assert from 'node:assert/strict'
import test from 'node:test'
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import {
  caseName,
  execute as executeWorkflow,
  failureCode,
  failureReason,
  parkedPayload as fullParkedPayload,
  rewritten,
  run,
  runObserved,
} from '../../../dist/Repository/Programming/Js/WorkflowSurface.js'
import { generate } from '../../../dist/Repository/Programming/Js/GeneratorSurface.js'
import {
  api as runtimeApi,
  createApi as createRuntimeApi,
  readPaths as runtimeReadPaths,
  run as runRuntime,
  stagedCount,
  stagedKinds,
} from '../../../dist/Repository/Programming/Js/RuntimeSurface.js'

import { closeAll } from '../../../dist/Repository/Programming/Js/Programs.js'
test.after(async () => { await closeAll() })

const parkedPayload = outcome => fullParkedPayload(outcome).payload

const sandbox = () => {
  const dir = mkdtempSync(join(tmpdir(), 'wxs-edit-'))
  return { dir, cleanup: () => rmSync(dir, { recursive: true, force: true }) }
}

const program = (body) => `class Js extends JsProgram {
  async run() {
${body}
    return { done: true };
  }
}`

const execute = (dir, body, language = 'en') =>
  run(dir, 'Coder', language, program(body), 2000, Date.now() + 60_000, 1 << 20, null)

const executeWithPark = (dir, body, sessionId = 'sess-024', language = 'en') =>
  executeWorkflow(dir, 'Coder', language, sessionId, { name: 'anchor-' + sessionId, program: program(body), intent: 'Wait for this anchor operation and preserve ambiguity for explicit recovery.' }, 2000, Date.now() + 60_000, 1 << 20, null)

test('WHAT[repository-programming-024] JS_EDIT_near_match_is_copy_ready_diagnostic_but_never_write_authority', async () => {
  const { dir, cleanup } = sandbox()
  try {
    writeFileSync(join(dir, 'a.js'), 'const timeout = 1000;\nstart(timeout);\n', 'utf8')
    const outcome = await executeWithPark(dir, `    const text = await this.cat('a.js');
    const [begin, end] = await this.where(text, 'const timout = 1000;');
    this.write('a.js', text.slice(0, begin) + 'const timeout = 5000;' + text.slice(end));`, 'sess-024-near')

    assert.equal(caseName(outcome), 'Parked')
    assert.equal(readFileSync(join(dir, 'a.js'), 'utf8'), 'const timeout = 1000;\nstart(timeout);\n')
    const payload = parkedPayload(outcome)
    assert.equal(payload.needle, 'const timout = 1000;')
    assert.deepEqual(payload.candidates, [])
    assert.equal(payload.nearest.text, 'const timeout = 1000;')
  } finally {
    cleanup()
  }
})

test('WHAT[repository-programming-024] JS_EDIT_ambiguous_match_returns_candidates_and_two_safe_next_moves', async () => {
  const { dir, cleanup } = sandbox()
  try {
    writeFileSync(join(dir, 'a.txt'), 'section A\nvalue = 1\nsection B\nvalue = 1\n', 'utf8')
    const outcome = await executeWithPark(dir, `    const text = await this.cat('a.txt');
    const [begin, end] = await this.where(text, 'value = 1');
    this.write('a.txt', text.slice(0, begin) + 'value = 2' + text.slice(end));`, 'sess-024-ambig')

    assert.equal(caseName(outcome), 'Parked')
    assert.equal(readFileSync(join(dir, 'a.txt'), 'utf8'), 'section A\nvalue = 1\nsection B\nvalue = 1\n')
    const payload = parkedPayload(outcome)
    assert.equal(payload.needle, 'value = 1')
    assert.equal(payload.candidates.length, 2)
    assert.deepEqual(payload.candidates[0].span, [10, 19])
    assert.deepEqual(payload.candidates[1].span, [30, 39])
  } finally {
    cleanup()
  }
})

test('WHAT[repository-programming-024] JS_EDIT_overlap_and_invalid_shape_have_stable_codes_and_zero_commit', async () => {
  const { dir, cleanup } = sandbox()
  try {
    writeFileSync(join(dir, 'a.txt'), 'alpha beta gamma\n', 'utf8')
    const text = readFileSync(join(dir, 'a.txt'), 'utf8')
    const empty = await execute(dir, `    const spans = this.whereAll(${JSON.stringify(text)}, '   ');
    if (spans.length !== 0) throw new Error('whitespace needle must return empty spans');`)
    assert.equal(caseName(empty), 'Succeeded')
    assert.equal(readFileSync(join(dir, 'a.txt'), 'utf8'), 'alpha beta gamma\n')

    const invalid = await executeWithPark(dir, `    const src = await this.cat('a.txt');
    await this.where(src, '');`, 'sess-024-empty')
    assert.equal(caseName(invalid), 'Parked')
    assert.equal(readFileSync(join(dir, 'a.txt'), 'utf8'), 'alpha beta gamma\n')
    assert.deepEqual(parkedPayload(invalid).candidates, [])
  } finally {
    cleanup()
  }
})

test('WHAT[repository-programming-024] JS_EDIT_rejects_invalid_path_unknown_fields_and_exotic_change_objects', async () => {
  const { dir, cleanup } = sandbox()
  const outsideFile = join(dir, '..', `outside-${Date.now()}-${Math.random().toString(36).slice(2)}.txt`)
  try {
    writeFileSync(join(dir, 'a.txt'), 'alpha\n', 'utf8')

    const invalidPath = await execute(dir, `    await this.cat('${outsideFile}');`)
    assert.equal(caseName(invalidPath), 'Failed')
    assert.equal(failureCode(invalidPath), 'FILE_NOT_FOUND', 'missing target returns FILE_NOT_FOUND')

    const retiredEdit = await execute(dir, `    if (typeof this.edit !== 'undefined' || typeof this.rewrite !== 'undefined' || typeof this.file !== 'undefined') {
      throw new Error('legacy member still present');
    }`)
    assert.equal(caseName(retiredEdit), 'Succeeded')
    assert.equal(readFileSync(join(dir, 'a.txt'), 'utf8'), 'alpha\n')

    assert.equal(generate('Coder', ['Edit'], 'en'), null)
    const writeOnly = generate('Coder', ['Write'], 'en')
    const handle = createRuntimeApi(dir)
    const writeOutcome = await runRuntime(
      writeOnly.baseClassSource,
      program(`    this.write('${outsideFile}', 'beta');`),
      runtimeApi(handle),
      2000,
      Date.now() + 60_000,
      1 << 20,
    )
    assert.equal(writeOutcome.ok, true)
    assert.equal(stagedCount(handle), 1)
  } finally {
    try { rmSync(outsideFile, { force: true }) } catch {}
    cleanup()
  }
})

test('WHAT[repository-programming-024] JS_EDIT_diagnostics_are_bounded_and_echo_the_attempted_find', async () => {
  const { dir, cleanup } = sandbox()
  try {
    const longLine = `${'x'.repeat(6000)} const timeout = 1000; ${'y'.repeat(6000)}`
    writeFileSync(join(dir, 'a.js'), `${longLine}\n${longLine}\n`, 'utf8')
    const outcome = await executeWithPark(dir, `    const text = await this.cat('a.js');
    await this.where(text, 'const timeout = 1000;');`, 'sess-024-bounded')

    assert.equal(caseName(outcome), 'Parked')
    const payload = parkedPayload(outcome)
    assert.equal(payload.needle, 'const timeout = 1000;')
    assert.equal(payload.candidates.length, 2)
    for (const candidate of payload.candidates) {
      assert.ok(candidate.context.length <= 200, `candidate context must be bounded, got ${candidate.context.length} chars`)
    }
    assert.equal(readFileSync(join(dir, 'a.js'), 'utf8'), `${longLine}\n${longLine}\n`)
  } finally {
    cleanup()
  }
})

test('WHAT[repository-programming-024] JS_EDIT_copy_ready_fix_uses_the_exact_candidate_subspan', async () => {
  const { dir, cleanup } = sandbox()
  try {
    writeFileSync(join(dir, 'a.js'), 'prefix const timeout = 1000; suffix\n', 'utf8')
    const outcome = await execute(dir, `    const text = await this.cat('a.js');
    const [begin, end] = await this.where(text, 'const timeout = 1000;');
    return { subspan: text.slice(begin, end) };`)

    assert.equal(caseName(outcome), 'Succeeded')
    assert.equal(readFileSync(join(dir, 'a.js'), 'utf8'), 'prefix const timeout = 1000; suffix\n')
  } finally {
    cleanup()
  }
})

test('WHAT[repository-programming-024] JS_EDIT_failure_control_language_is_localized', async () => {
  const { dir, cleanup } = sandbox()
  try {
    writeFileSync(join(dir, 'a.txt'), 'value = 1\nvalue = 1\n', 'utf8')
    const ambiguous = await executeWithPark(dir, `    const text = await this.cat('a.txt');
    await this.where(text, 'value = 1');`, 'sess-024-zh', 'zh-CN')

    assert.equal(caseName(ambiguous), 'Parked')
    const payload = parkedPayload(ambiguous)
    assert.equal(payload.needle, 'value = 1')
    assert.equal(payload.candidates.length, 2)
  } finally {
    cleanup()
  }
})
