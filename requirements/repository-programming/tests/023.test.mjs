import assert from 'node:assert/strict'
import test from 'node:test'
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import {
  caseName,
  failureCode,
  failureReason,
  rewritten,
  run,
  runObserved,
} from '../../../dist/Repository/Programming/Js/WorkflowSurface.js'
import { generate } from '../../../dist/Repository/Programming/Js/GeneratorSurface.js'
import {
  api as runtimeApi,
  createApi as createRuntimeApi,
  run as runRuntime,
} from '../../../dist/Repository/Programming/Js/RuntimeSurface.js'

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

test('WHAT[repository-programming-023] JS_EDIT_exact_batch_replaces_inserts_and_deletes_with_one_rewrite', async () => {
  const { dir, cleanup } = sandbox()
  try {
    writeFileSync(join(dir, 'a.js'), 'const alpha = 1;\nconst beta = 2;\nconst obsolete = true;\n', 'utf8')
    const outcome = await execute(dir, `    let text = await this.cat('a.js');
    const edits = [
      { span: await this.where(text, 'const alpha = 1;'), put: 'const alpha = 10;' },
      { span: await this.where(text, 'const beta = 2;'), put: 'const beta = 2;\\nconst gamma = 3;' },
      { span: await this.where(text, 'const obsolete = true;'), put: '' },
    ];
    for (const { span: [begin, end], put } of edits.sort((a, b) => b.span[0] - a.span[0])) {
      const tail = put === '' && text[end] === '\\n' ? end + 1 : end;
      text = text.slice(0, begin) + put + text.slice(tail);
    }
    this.write('a.js', text);`)

    assert.equal(caseName(outcome), 'Succeeded')
    assert.deepEqual(rewritten(outcome), ['a.js'], 'one write call produces one rewritten path')
    assert.equal(
      readFileSync(join(dir, 'a.js'), 'utf8'),
      'const alpha = 10;\nconst beta = 2;\nconst gamma = 3;\n',
    )
  } finally {
    cleanup()
  }
})

test('WHAT[repository-programming-023] JS_EDIT_only_surface_has_private_snapshot_read_without_public_file_member', async () => {
  const { dir, cleanup } = sandbox()
  try {
    writeFileSync(join(dir, 'a.txt'), 'alpha\n', 'utf8')
    assert.equal(generate('Coder', ['Edit'], 'en'), null, 'Edit permission alone projects no surface')
    const surface = generate('Coder', ['Write'], 'en')
    assert.deepEqual(surface.members.map(member => member.memberName), ['write', 'mkdir'])
    assert.equal(surface.baseClassSource.includes('file(path'), false)
    assert.equal(surface.baseClassSource.includes('cat(path'), false)

    const handle = createRuntimeApi(dir)
    const outcome = await runRuntime(
      surface.baseClassSource,
      program(`    if (typeof this.file !== 'undefined' || typeof this.cat !== 'undefined') throw new Error('public Read leaked');
    this.write('a.txt', 'beta\\n');`),
      runtimeApi(handle),
      2000,
      Date.now() + 60_000,
      1 << 20,
    )

    assert.equal(outcome.ok, true)
    assert.equal(readFileSync(join(dir, 'a.txt'), 'utf8'), 'beta\n', 'runtime surface persists as-is')
  } finally {
    cleanup()
  }
})

test('WHAT[repository-programming-023] JS_EDIT_accepts_single_object_and_unambiguous_common_aliases', async () => {
  const { dir, cleanup } = sandbox()
  try {
    writeFileSync(join(dir, 'a.txt'), 'before\n', 'utf8')
    const first = await execute(dir, `    const text = await this.cat('a.txt');
    const [b, e] = await this.where(text, 'before');
    this.write('a.txt', text.slice(0, b) + 'middle' + text.slice(e));`)
    assert.equal(caseName(first), 'Succeeded')
    assert.equal(readFileSync(join(dir, 'a.txt'), 'utf8'), 'middle\n')

    const second = await execute(dir, `    const text = await this.cat('a.txt');
    const [b, e] = await this.where(text, 'middle');
    this.write('a.txt', text.slice(0, b) + 'after' + text.slice(e));`)
    assert.equal(caseName(second), 'Succeeded')
    assert.equal(readFileSync(join(dir, 'a.txt'), 'utf8'), 'after\n')
  } finally {
    cleanup()
  }
})

test('WHAT[repository-programming-023] JS_EDIT_all_applies_every_non_overlapping_string_or_regexp_match', async () => {
  const { dir, cleanup } = sandbox()
  try {
    writeFileSync(join(dir, 'a.js'), 'oldApi();\noldApi();\nconst oldApiValue = 1;\n', 'utf8')
    const outcome = await execute(dir, `    let text = await this.cat('a.js');
    for (const [begin, end] of [...this.whereAll(text, 'oldApi()')].reverse()) {
      text = text.slice(0, begin) + 'newApi()' + text.slice(end);
    }
    this.write('a.js', text);`)

    assert.equal(caseName(outcome), 'Succeeded')
    assert.equal(readFileSync(join(dir, 'a.js'), 'utf8'), 'newApi();\nnewApi();\nconst oldApiValue = 1;\n')
  } finally {
    cleanup()
  }
})

test('WHAT[repository-programming-023] JS_EDIT_preserves_sticky_regexp_as_write_authority', async () => {
  const { dir, cleanup } = sandbox()
  try {
    writeFileSync(join(dir, 'a.txt'), 'prefix target\n', 'utf8')
    const outcome = await execute(dir, `    const text = await this.cat('a.txt');
    const spans = this.whereAll(text, 'target', 8);
    if (spans.length === 0) throw new Error('no match from offset');
    this.write('a.txt', 'changed');`)

    assert.equal(caseName(outcome), 'Failed')
    assert.equal(failureCode(outcome), 'PROGRAM_FAILED')
    assert.equal(readFileSync(join(dir, 'a.txt'), 'utf8'), 'prefix target\n')
  } finally {
    cleanup()
  }
})

test('WHAT[repository-programming-023] JS_EDIT_preserves_a_consistent_CRLF_file_when_the_needle_is_written_as_is', async () => {
  const { dir, cleanup } = sandbox()
  try {
    writeFileSync(join(dir, 'a.txt'), 'alpha\r\nbeta\r\n', 'utf8')
    const outcome = await execute(dir, `    const text = await this.cat('a.txt');
    const [begin, end] = await this.where(text, 'alpha\\r\\nbeta');
    this.write('a.txt', text.slice(0, begin) + 'alpha\\r\\ngamma\\r\\nbeta' + text.slice(end));`)

    assert.equal(caseName(outcome), 'Succeeded')
    assert.equal(readFileSync(join(dir, 'a.txt'), 'utf8'), 'alpha\r\ngamma\r\nbeta\r\n')
  } finally {
    cleanup()
  }
})

test('WHAT[repository-programming-023] JS_EDIT_every_change_addresses_the_original_snapshot_and_failure_is_atomic', async () => {
  const { dir, cleanup } = sandbox()
  try {
    writeFileSync(join(dir, 'a.txt'), 'alpha\n', 'utf8')
    const outcome = await execute(dir, `    const text = await this.cat('a.txt');
    const first = this.whereAll(text, 'alpha');
    const second = this.whereAll(text, 'beta');
    if (first.length !== 1 || second.length !== 1) throw new Error('change 2 missing on original snapshot');
    this.write('a.txt', 'gamma\\n');`)

    assert.equal(caseName(outcome), 'Failed')
    assert.equal(failureCode(outcome), 'PROGRAM_FAILED')
    assert.equal(readFileSync(join(dir, 'a.txt'), 'utf8'), 'alpha\n')
    assert.match(String(failureReason(outcome)), /change 2/i)
  } finally {
    cleanup()
  }
})

test('WHAT[repository-programming-023] JS_EDIT_noop_succeeds_without_a_rewrite_intent', async () => {
  const { dir, cleanup } = sandbox()
  try {
    writeFileSync(join(dir, 'a.txt'), 'alpha\n', 'utf8')
    const outcome = await execute(dir, `    const text = await this.cat('a.txt');
    const [begin, end] = await this.where(text, 'alpha');
    const next = text.slice(0, begin) + 'alpha' + text.slice(end);
    if (next !== text) this.write('a.txt', next);`)

    assert.equal(caseName(outcome), 'Succeeded')
    assert.deepEqual(rewritten(outcome), [])
    assert.equal(readFileSync(join(dir, 'a.txt'), 'utf8'), 'alpha\n')
  } finally {
    cleanup()
  }
})

test('WHAT[repository-programming-023] JS_EDIT_later_file_failure_keeps_earlier_file_writes', async () => {
  const { dir, cleanup } = sandbox()
  try {
    writeFileSync(join(dir, 'a.txt'), 'alpha\n', 'utf8')
    writeFileSync(join(dir, 'b.txt'), 'bravo\n', 'utf8')
    const outcome = await execute(dir, `    const a = await this.cat('a.txt');
    const [ab, ae] = await this.where(a, 'alpha');
    this.write('a.txt', a.slice(0, ab) + 'changed' + a.slice(ae));
    await this.cat('missing.txt');`)

    assert.equal(caseName(outcome), 'Failed')
    assert.equal(failureCode(outcome), 'FILE_NOT_FOUND')
    assert.equal(readFileSync(join(dir, 'a.txt'), 'utf8'), 'changed\n', 'earlier write stays on disk')
    assert.equal(readFileSync(join(dir, 'b.txt'), 'utf8'), 'bravo\n')
  } finally {
    cleanup()
  }
})
