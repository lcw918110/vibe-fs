import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { mkdtempSync, readFileSync, rmSync, writeFileSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join } = await import("node:path");
const { createRegistered, execute } = await import("../../../dist/Repository/Programming/Js/OpenCode/ToolHostSurface.js");

// Superseded note: `annotate`, `validateRecommendation`, and `builtinTools` were retired from
// `ToolHostSurface` alongside the direct Host filesystem tools; the js programming surface
// (`cat`/`where`/`write`/`ls`/`mv`/`rm`) is the sole contract now.
const sandbox = () => {
  const dir = mkdtempSync(join(tmpdir(), 'wxs-host-'))
  return { dir, cleanup: () => rmSync(dir, { recursive: true, force: true }) }
}
// The fake host schema builders mirror the real ones: every builder can be
// marked optional, so a registered argument can say "this may be absent" the
// way the zod-based host schema does.
const schemaOf = (state) => ({
  ...state,
  optional: () => schemaOf({ ...state, optional: true }),
  nullable: () => schemaOf({ ...state, nullable: true }),
  describe: (text) => schemaOf({ ...state, description: text }),
  int: () => schemaOf({ ...state, integer: true }),
  nonnegative: () => schemaOf({ ...state, nonnegative: true }),
  min: bound => schemaOf({ ...state, min: bound }),
})
const toolModule = () => {
  const tool = (definition) => definition
  tool.schema = {
    string: () => schemaOf({ type: 'string' }),
    any: () => schemaOf({ type: 'any' }),
    number: () => schemaOf({ type: 'number' }),
    array: (item) => schemaOf({ type: 'array', item }),
    record: (key, value) => schemaOf({ type: 'record', key, value }),
    object: (shape) => schemaOf({ type: 'object', shape }),
  }
  return { tool }
}

test('WHAT[repository-programming-016] JS073_spec_executes_program_and_renders_result', async () => {
  const { dir, cleanup } = sandbox()
  try {
    writeFileSync(join(dir, 'a.txt'), 'hello world', 'utf8')
    const registered = createRegistered(toolModule(), 'Coder', 'en', dir, null)

    const program = `class Js extends JsProgram {
  async run() {
    const text = await this.cat('a.txt');
    const [begin, end] = await this.where(text, 'hello');
    this.write('a.txt', text.slice(0, begin) + 'goodbye' + text.slice(end));
    return { before: text };
  }
}`
    const result = await execute(registered, { name: "edit-016", program, intent: "Wait for this file operation and preserve the original effects." }, { sessionID: 'ses-test', agent: 'coder' })
    assert.equal(result.startsWith('# ok\n'), true)
    assert.equal(result.includes('[data]'), true)
    assert.equal(result.includes('before'), true)
    assert.equal(result.includes('[fs]'), true)
    assert.equal(result.includes('status ='), false)
    assert.equal(readFileSync(join(dir, 'a.txt'), 'utf8'), 'goodbye world', 'committed via workflow')
    const missing = await execute(registered, {}, { sessionID: 'ses-test', agent: 'coder' })
    assert.equal(missing.includes('error'), true)
    assert.match(missing, /name|program|intent/)
  } finally {
    cleanup()
  }
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
const { run, runObserved, caseName, rewritten, created, failureCode, render } = await import("../../../dist/Repository/Programming/Js/WorkflowSurface.js");
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

test('WHAT[repository-programming-016] JS016_result_renders_stable_toml_shapes', async () => {
  const { dir, cleanup } = sandbox()
  try {
    writeFileSync(join(dir, 'a.txt'), 'hello world', 'utf8')
    const program = `class Js extends JsProgram {
  async run() {
    const text = await this.cat('a.txt');
    const [begin, end] = await this.where(text, 'hello');
    this.write('a.txt', text.slice(0, begin) + 'goodbye' + text.slice(end));
    return { before: 'x' };
  }
}`
    const { outcome } = await runWorkflow(dir, program)
    const toml = render(outcome)
    assert.equal(toml.startsWith('# ok\n'), true)
    assert.equal(/(?:^|\n)status =/m.test(toml), false)
    assert.equal(/(?:^|\n)result =/m.test(toml), false)
    assert.equal(/(?:^|\n)written =/m.test(toml), false)
    const doc = parseToml(toml)
    assert.equal(doc.data.before, 'x')
    assert.deepEqual(doc.fs.rewritten, ['a.txt'])
    assert.equal(doc.fs.created, undefined)
    const failing = await run(dir, 'Coder', 'en', `class Js extends JsProgram {
  async run() { throw new Error('boom'); }
}`, 2000, Date.now() + 60_000, 1 << 20, null)
    const failedToml = render(failing)
    assert.equal(failedToml.startsWith('# failed\n'), true)
    assert.equal(failedToml.includes('status ='), false)
    const failed = parseToml(failedToml)
    assert.equal(failed.code, 'PROGRAM_FAILED')
    assert.equal(typeof failed.reason, 'string')
    assert.equal(failed.reason.includes('boom'), true)
    assert.equal(failed.data, undefined)
    assert.equal(failed.fs, undefined)
  } finally {
    cleanup()
  }
})
test('WHAT[repository-programming-016] JS010_016_query_object_has_data_and_no_fs', async () => {
  const { dir, cleanup } = sandbox()
  try {
    const program = `class Js extends JsProgram {
  async run() { return { paths: ['a.txt'] } }
}`
    const { outcome } = await runWorkflow(dir, program)
    const toml = render(outcome)
    const doc = parseToml(toml)
    assert.deepEqual(doc.data.paths, ['a.txt'])
    assert.equal('truncated' in doc.data, false)
    assert.equal(doc.fs, undefined)
    assert.equal(toml.includes('[fs]'), false)
  } finally {
    cleanup()
  }
})
test('WHAT[repository-programming-016] JS010_016_primitive_return_uses_data_field', async () => {
  const { dir, cleanup } = sandbox()
  try {
    const { outcome } = await runWorkflow(dir, `class Js extends JsProgram {
  async run() { return 42 }
}`)
    const toml = render(outcome)
    assert.equal(parseToml(toml).data, 42)
  } finally {
    cleanup()
  }
})
}

{
// Where the surface executes. The Host creates a Manager and her forked children
// rooted at the directory their owner registered — for a Change road that is the
// job's own worktree (`OrchestratorHost.directoryFor` → `HostForkRuntime.CreateChildSession`).
// The programming surface must execute in that same directory: writing into the
// plugin's project directory instead puts the road's deliverable into the target's
// own checkout, leaves the job worktree empty, and makes the candidate a no-op
// commit whose publish rebase cannot conflict.
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { existsSync, mkdtempSync, readFileSync, rmSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join } = await import("node:path");
const { createRegistered, execute } = await import("../../../dist/Repository/Programming/Js/OpenCode/ToolHostSurface.js");

// The fake host schema builders mirror the real ones: every builder can be
// marked optional, so a registered argument can say "this may be absent" the
// way the zod-based host schema does.
const schemaOf = (state) => ({
  ...state,
  optional: () => schemaOf({ ...state, optional: true }),
  nullable: () => schemaOf({ ...state, nullable: true }),
  describe: (text) => schemaOf({ ...state, description: text }),
  int: () => schemaOf({ ...state, integer: true }),
  nonnegative: () => schemaOf({ ...state, nonnegative: true }),
  min: bound => schemaOf({ ...state, min: bound }),
})
const toolModule = () => {
  const tool = (definition) => definition
  tool.schema = {
    string: () => schemaOf({ type: 'string' }),
    any: () => schemaOf({ type: 'any' }),
    number: () => schemaOf({ type: 'number' }),
    array: (item) => schemaOf({ type: 'array', item }),
    record: (key, value) => schemaOf({ type: 'record', key, value }),
    object: (shape) => schemaOf({ type: 'object', shape }),
  }
  return { tool }
}

const sandbox = (prefix) => {
  const dir = mkdtempSync(join(tmpdir(), prefix))
  return { dir, cleanup: () => rmSync(dir, { recursive: true, force: true }) }
}

const writeProgram = (target) => `class Js extends JsProgram {
  async run() {
    await this.write('${target}', 'Published by long-stroke canary\\n');
    return { written: true };
  }
}`

test('WHAT[repository-programming-016] the surface executes in the directory its session registered, not the plugin workspace', async () => {
  const project = sandbox('wxs-root-project-')
  const worktree = sandbox('wxs-root-worktree-')
  try {
    const registered = createRegistered(
      toolModule(), 'Coder', 'en', project.dir, null, { 'ses-road': worktree.dir },
    )
    const result = await execute(registered, { name: 'worktree-proof', intent: 'Wait for the worktree write and report the actual result.', program: writeProgram('publish_proof.txt') }, { sessionID: 'ses-road' })

    assert.equal(result.startsWith('# ok\n'), true, result)
    assert.equal(
      readFileSync(join(worktree.dir, 'publish_proof.txt'), 'utf8'),
      'Published by long-stroke canary\n',
      'the road session writes inside its own worktree',
    )
    assert.equal(
      existsSync(join(project.dir, 'publish_proof.txt')),
      false,
      'the target’s checkout must stay untouched by a road session',
    )
  } finally {
    project.cleanup()
    worktree.cleanup()
  }
})

test('WHAT[repository-programming-016] an unregistered session keeps the plugin workspace fallback', async () => {
  const project = sandbox('wxs-root-fallback-')
  try {
    const registered = createRegistered(
      toolModule(), 'Coder', 'en', project.dir, null, { 'ses-other': project.dir },
    )
    const result = await execute(registered, { name: 'workspace-proof', intent: 'Wait for the workspace write and report the actual result.', program: writeProgram('fallback.txt') }, { sessionID: 'ses-unregistered' })

    assert.equal(result.startsWith('# ok\n'), true, result)
    assert.equal(readFileSync(join(project.dir, 'fallback.txt'), 'utf8'), 'Published by long-stroke canary\n')
  } finally {
    project.cleanup()
  }
})
}
