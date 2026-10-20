import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { failureCatalog, validateAnchorDeclaration: validateDeclaration, validateAnchorOccurrence: validateOccurrence } = await import("../../../dist/Repository/Programming/Js/TransactionSurface.js");

const declaration = (spec, occurrence) => ({ ...spec, occurrence })
const ok = (result) => result.ok
const exact = (text) => ({ kind: 'exact', text })
const regex = (text) => ({ kind: 'regex', text })

test('WHAT[repository-programming-018] JS019_failure_codes_are_stable_and_unique', () => {
  const expectedCodes = [
    'INVALID_PROGRAM',
    'PROGRAM_FAILED',
    'PROGRAM_TIMEOUT',
    'PROGRAM_RESOURCE_LIMIT',
    'FILE_NOT_FOUND',
    'FILE_ALREADY_EXISTS',
    'INVALID_UTF8',
    'ANCHOR_NOT_FOUND',
    'ANCHOR_NOT_UNIQUE',
    'INVALID_EDIT',
    'EDIT_NOT_FOUND',
    'EDIT_AMBIGUOUS',
    'EDIT_OVERLAP',
    'DUPLICATE_MUTATION_TARGET',
    'RESULT_TOO_LARGE',
    'INVALID_RETURN_VALUE',
    'FILE_CHANGED',
    'FILE_READ_FAILED',
    'DIRECTORY_NOT_EMPTY',
    'DIRECTORY_EXISTS',
    'PARENT_DIRECTORY_MISSING',
    'CROSS_DEVICE_MOVE',
    'INVALID_ARGUMENT',
    'TRANSACTION_PREPARE_FAILED',
    'TRANSACTION_COMMIT_FAILED',
    'TRANSACTION_RECOVERY_REQUIRED',
    'UNKNOWN_MEMBER',
    'CALLABLE_SPEC_INCOMPLETE',
    'CALL_COMMAND_UNSUPPORTED',
    'CALL_WAIT_TIMEOUT',
    'CALL_CHILD_UNKNOWN',
    'CALL_PARK_UNSUPPORTED',
    'CALL_HOST_UNAVAILABLE',
    'ASK_REFUSED',
    'DELEGATION_UNAVAILABLE',
  ]
  const cases = failureCatalog()
  assert.equal(cases.length, expectedCodes.length)
  const seen = new Set()
  for (const expected of expectedCodes) {
    const entry = cases.find(({ code }) => code === expected)
    assert.ok(entry, `missing code ${expected}`)
    assert.equal(seen.has(entry.code), false, `duplicate code ${entry.code}`)
    seen.add(entry.code)
    assert.equal(typeof entry.reason, 'string')
    assert.ok(entry.reason.length > 0)
  }
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { run } = await import("../../../dist/Repository/Programming/Js/RuntimeSurface.js");

const BASE = `class JsProgram {
  constructor(api) { this._api = api; }
  cat(path) { return this._api.js.cat(path); }
}`
const runWrapped = async (
  program,
  api,
  { deadlineMs = 2000, outputBound = 1 << 20, deadlineEpochMs = Date.now() + 60_000 } = {},
) => run(BASE, program, api, deadlineMs, deadlineEpochMs, outputBound)
const failureCode = (result) => result.code
const PROGRAM = `class Js extends JsProgram {
  async run() {
    return { sum: 1 + 2, text: (await this.cat('a.txt')).text };
  }
}`

test('WHAT[repository-programming-018] JS019_invalid_javascript_is_invalid_program', async () => {
  const bad = `class Js extends JsProgram { async run() { return { broken: } } }`
  const result = await runWrapped(bad, { js: {} })
  assert.equal(result.ok, false)
  assert.equal(failureCode(result), 'INVALID_PROGRAM')
})
test('WHAT[repository-programming-018] JS019_program_throw_is_program_failed', async () => {
  const throwing = `class Js extends JsProgram {
  async run() { throw new Error('boom'); }
}`
  const result = await runWrapped(throwing, { js: {} })
  assert.equal(result.ok, false)
  assert.equal(failureCode(result), 'PROGRAM_FAILED')
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

test('WHAT[repository-programming-018] JS085_workflow_file_missing_fails_the_program', async () => {
  const { dir, cleanup } = sandbox()
  try {
    const program = `class Js extends JsProgram {
  async run() {
    await this.cat('missing.txt');
    return { ok: true };
  }
}`
    const { outcome } = await runWorkflow(dir, program)
    assert.equal(caseName(outcome), 'Failed')
    const failed = parseToml(render(outcome))
    assert.equal(failed.code, 'FILE_NOT_FOUND')
    assert.equal(failed.reason.includes('missing.txt'), true)
  } finally {
    cleanup()
  }
})
test('WHAT[repository-programming-018] JS019_missing_anchor_uses_stable_code', async () => {
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
    const outcome = await execute(dir, 'Coder', 'en', 'sess-018', { program }, 2000, Date.now() + 60_000, 1 << 20, null)
    assert.equal(caseName(outcome), 'Parked')
    const payload = parkedPayload(outcome)
    assert.equal(payload.needle, '## JS-007 FileView.text()')
    assert.deepEqual(payload.candidates, [])
  } finally {
    cleanup()
  }
})
}
