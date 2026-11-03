import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { mkdtempSync, readFileSync, realpathSync, rmSync, writeFileSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join } = await import("node:path");
const { caseName, failureCode, failureReason, rewritten, run, runObserved } = await import("../../../dist/Repository/Programming/Js/WorkflowSurface.js");
const { generate } = await import("../../../dist/Repository/Programming/Js/GeneratorSurface.js");

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

test('WHAT[repository-programming-014] JS_EDIT_target_read_is_observed_and_external_change_after_edit_is_a_separate_fact', async () => {
  const { dir, cleanup } = sandbox()
  try {
    writeFileSync(join(dir, 'a.txt'), 'alpha\n', 'utf8')
    const outcome = await runObserved(
      dir,
      'Coder',
      'en',
      program(`    const text = await this.cat('a.txt');
    const [begin, end] = await this.where(text, 'alpha');
    this.write('a.txt', text.slice(0, begin) + 'beta' + text.slice(end));`),
      2000,
      Date.now() + 60_000,
      1 << 20,
      null,
      async (readPaths, effectPaths) => {
        assert.deepEqual(readPaths, [{ path: realpathSync(join(dir, 'a.txt')), resultBytes: 'alpha\n' }])
        assert.deepEqual(effectPaths, ['a.txt'])
        // The edit landed as-is at call time; an external write after it is a
        // later, separate fact and does not fail the already-succeeded edit.
        writeFileSync(join(dir, 'a.txt'), 'external\n', 'utf8')
      },
    )

    assert.equal(caseName(outcome), 'Succeeded')
    assert.equal(readFileSync(join(dir, 'a.txt'), 'utf8'), 'external\n')
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

test('WHAT[repository-programming-014] JS014_commitPlan_rejects_a_create_race_without_overwriting', () => {
  const { dir, cleanup } = sandbox()
  try {
    writeFileSync(join(dir, 'new.txt'), 'external', 'utf8')
    const result = commitPlan(dir, [{ kind: 'create', path: 'new.txt', newText: 'tool' }])
    assert.equal(codeOf(result), 'FILE_CHANGED')
    assert.equal(readFileSync(join(dir, 'new.txt'), 'utf8'), 'external')
  } finally {
    cleanup()
  }
})
test('WHAT[repository-programming-014] JS014_commitPlan_rejects_a_stale_rewrite_without_overwriting', () => {
  const { dir, cleanup } = sandbox()
  try {
    writeFileSync(join(dir, 'a.txt'), 'external', 'utf8')
    const result = commitPlan(dir, [
      { kind: 'rewrite', path: 'a.txt', expectedCurrent: 'snapshot', newText: 'tool' },
    ])
    assert.equal(codeOf(result), 'FILE_CHANGED')
    assert.equal(readFileSync(join(dir, 'a.txt'), 'utf8'), 'external')
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

test('WHAT[repository-programming-014] JS014_stale_rewrite_is_a_conflict_with_no_retry', () => {
  const fresh = [rewrite('a.txt', 'current', 'new')]
  const stale = [rewrite('a.txt', 'old', 'new')]
  assert.equal(ok(validateFreshness(current, fresh)), true)
  assert.equal(codeOf(validateFreshness(current, stale)), 'FILE_CHANGED')
  // create targets are not freshness-checked
  assert.equal(ok(validateFreshness(current, [create('b.txt', 'n')])), true)
})
test('WHAT[repository-programming-014] JS014_preflight_covers_read_only_snapshots_and_create_absence', () => {
  const changedRead = preflight(
    ['dependency.txt'],
    { 'dependency.txt': 'external' },
    [{ path: 'dependency.txt', text: 'snapshot' }],
    [],
  )
  assert.equal(codeOf(changedRead), 'FILE_CHANGED')
  assert.equal(codeOf(preflight(['new.txt'], { 'new.txt': 'external' }, [], [create('new.txt', 'tool')])), 'FILE_CHANGED')
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { mkdtempSync, mkdirSync, readFileSync, realpathSync, rmSync, writeFileSync, existsSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join } = await import("node:path");
const { randomUUID } = await import("node:crypto");
const { parse: parseToml } = await import("smol-toml");
const { generate } = await import("../../../dist/Repository/Programming/Js/GeneratorSurface.js");
const { run, runObserved, caseName, rewritten, created, failureCode, render } = await import("../../../dist/Repository/Programming/Js/WorkflowSurface.js");
const { create: createEventStore, dispose: disposeEventStore } = await import("../../../dist/Persistence/EventStore/Surface.js");
const { pending } = await import("../../../dist/Repository/Programming/Js/TransactionSurface.js");

const sandbox = () => {
  const dir = mkdtempSync(join(tmpdir(), 'wxs-workflow-'))
  return { dir, cleanup: () => rmSync(dir, { recursive: true, force: true }) }
}
const localStore = () => {
  const owned = mkdtempSync(join(tmpdir(), 'wxs-workflow-events-'))
  const handle = createEventStore(owned, randomUUID().replaceAll('-', ''))
  return { handle, close: () => { disposeEventStore(handle); rmSync(owned, { recursive: true, force: true }) } }
}
const coderSurface = () => generate('Coder', ['Read', 'Write', 'Edit', 'Glob', 'Grep'], 'en')
const runWorkflow = async (dir, program, { deadlineMs = 2000, store = null } = {}) => ({
  outcome: await run(dir, 'Coder', 'en', program, deadlineMs, Date.now() + 60_000, 1 << 20, store),
  surface: coderSurface(),
})

test('WHAT[repository-programming-014] JS014_as_is_external_change_after_write_does_not_undo_the_write', async () => {
  const { dir, cleanup } = sandbox()
  try {
    writeFileSync(join(dir, 'dependency.txt'), 'snapshot', 'utf8')
    const program = `class Js extends JsProgram {
  async run() {
    const dependency = await this.cat('dependency.txt');
    this.write('output.txt', dependency);
    return { ok: true };
  }
}`
    let observations = 0
    const outcome = await runObserved(
      dir,
      'Coder',
      'en',
      program,
      2000,
      4_102_444_800_000,
      1 << 20,
      null,
      async (readPaths, effectPaths) => {
        observations += 1
        assert.deepEqual(readPaths, [{ path: realpathSync(join(dir, 'dependency.txt')), resultBytes: 'snapshot' }])
        assert.deepEqual(effectPaths, ['output.txt'])
        // The write already landed as-is; an external change after it is a
        // later fact and does not roll the write back.
        writeFileSync(join(dir, 'dependency.txt'), 'external', 'utf8')
      },
    )

    assert.equal(caseName(outcome), 'Succeeded')
    assert.equal(readFileSync(join(dir, 'output.txt'), 'utf8'), 'snapshot')
    assert.equal(readFileSync(join(dir, 'dependency.txt'), 'utf8'), 'external')
    assert.equal(observations, 1)
  } finally {
    cleanup()
  }
})
test('WHAT[repository-programming-014] JS014_as_is_create_lands_immediately_and_later_external_write_wins', async () => {
  const { dir, cleanup } = sandbox()
  try {
    const program = `class Js extends JsProgram {
  async run() {
    this.write('new.txt', 'tool');
    return { ok: true };
  }
}`
    let observed
    const outcome = await runObserved(
      dir,
      'Coder',
      'en',
      program,
      2000,
      4_102_444_800_000,
      1 << 20,
      null,
      async (readPaths, effectPaths) => {
        observed = { readPaths, effectPaths }
        writeFileSync(join(dir, 'new.txt'), 'external', 'utf8')
      },
    )

    // The create landed at call time; the observation callback's external
    // write is a later, separate fact and does not fail the program.
    assert.equal(caseName(outcome), 'Succeeded')
    assert.deepEqual(observed, { readPaths: [], effectPaths: ['new.txt'] })
    assert.equal(readFileSync(join(dir, 'new.txt'), 'utf8'), 'external')
  } finally {
    cleanup()
  }
})
test('WHAT[repository-programming-014] JS014_as_is_grep_snapshot_does_not_block_a_later_write_to_another_path', async () => {
  const { dir, cleanup } = sandbox()
  try {
    writeFileSync(join(dir, 'source.txt'), 'needle', 'utf8')
    const program = `class Js extends JsProgram {
  async run() {
    const entries = await this.ls('.');
    const txtFiles = entries.filter((entry) => entry.endsWith('.txt'));
    this.write('output.txt', String(txtFiles.length));
    return { count: txtFiles.length };
  }
}`
    const outcome = await runObserved(
      dir,
      'Coder',
      'en',
      program,
      2000,
      4_102_444_800_000,
      1 << 20,
      null,
      async (readPaths) => {
        assert.deepEqual(readPaths, [], 'grep retains freshness snapshots without explicit file-read observations')
        writeFileSync(join(dir, 'source.txt'), 'external', 'utf8')
      },
    )

    // As-is writes check the target's own freshness at call time; the write
    // target (output.txt) is absent and created successfully. The grep
    // snapshot of source.txt does not participate in another path's write.
    assert.equal(caseName(outcome), 'Succeeded')
    assert.equal(readFileSync(join(dir, 'output.txt'), 'utf8'), '1')
    assert.equal(readFileSync(join(dir, 'source.txt'), 'utf8'), 'external')
  } finally {
    cleanup()
  }
})
}
