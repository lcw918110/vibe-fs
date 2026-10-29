import test from 'node:test'
import assert from 'node:assert/strict'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { z } from 'zod'
import { createRegistered, execute } from '../../../dist/Repository/Programming/Js/OpenCode/ToolHostSurface.js'

test('WHAT[repository-programming-006] todowrite can continue after waiting longer than ten seconds', async () => {
  const directory = mkdtempSync(join(tmpdir(), 'wxs-006-long-wait-'))
  const tool = definition => definition
  tool.schema = z
  try {
    const registered = createRegistered({ tool }, 'DevOps', 'en', directory, null)
    const result = await execute(registered, {
      name: 'long-wait',
      program: `class Js extends JsProgram {
        async run() {
          await this.call("node -e 'setTimeout(() => {}, 10500)'");
          return { continued: true };
        }
      }`,
      intent: 'wait for the running operation '.repeat(20),
    }, { sessionID: 'ses-006-long-wait' })
    assert.equal(result.startsWith('# ok'), true, result)
    assert.match(result, /continued = true/)
  } finally {
    rmSync(directory, { recursive: true, force: true })
  }
})

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

test('WHAT[repository-programming-006] JS011_sandbox_program_uses_bindings_end_to_end', async () => {
  const { dir, cleanup } = sandbox()
  try {
    writeFileSync(join(dir, 'a.txt'), 'hello world', 'utf8')
    const binding = createApi(dir)
    const surface = coderSurface()
    const program = `class Js extends JsProgram {
  async run() {
    const text = await this.cat('a.txt');
    const [begin, end] = await this.where(text, 'hello');
    this.write('a.txt', text.slice(0, begin) + 'goodbye' + text.slice(end));
    return { before: text };
  }
}`
    const result = await run(surface.baseClassSource, program, apiOf(binding), 2000, Date.now() + 60_000, 1 << 20)
    assert.equal(result.ok, true)
    assert.deepEqual(JSON.parse(result.value), { before: 'hello world' })
    assert.equal(stagedCount(binding), 1, 'rewrite recorded in the as-is ledger')
    const disk = apiOf(binding).cat('a.txt')
    assert.equal(disk.text, 'goodbye world')
  } finally {
    cleanup()
  }
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { run } = await import("../../../dist/Repository/Programming/Js/RuntimeSurface.js");

const BASE = `class JsProgram {
  constructor(api) { this._api = api; }
  cat(path) { return this._api.cat(path); }
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

test('WHAT[repository-programming-006] JS011_program_runs_and_returns_json', async () => {
  const api = { cat: async (path) => ({ path, text: 'hello' }) }
  const result = await runWrapped(PROGRAM, api)
  assert.equal(result.ok, true)
  assert.deepEqual(JSON.parse(result.value), { sum: 3, text: 'hello' })
})
test('WHAT[repository-programming-006] JS011_api_is_the_only_authority_in_the_context', async () => {
  // process / require / fs / globalThis.process must be undefined inside the vm.
  const probe = `class Js extends JsProgram {
  async run() {
    return {
      process: typeof process,
      require: typeof require,
      fs: typeof fs,
      globalProcess: typeof globalThis.process,
    };
  }
}`
  const result = await runWrapped(probe, { js: {} })
  assert.equal(result.ok, true)
  assert.deepEqual(JSON.parse(result.value), {
    process: 'undefined',
    require: 'undefined',
    fs: 'undefined',
    globalProcess: 'undefined',
  })
})
test('WHAT[repository-programming-006] JS054_1_sync_infinite_loop_is_killed_by_vm_timeout', async () => {
  const loop = `class Js extends JsProgram {
  async run() { while (true) {} }
}`
  const result = await runWrapped(loop, { js: {} }, { deadlineMs: 200 })
  assert.equal(result.ok, false)
  assert.equal(failureCode(result), 'PROGRAM_TIMEOUT')
})
test('WHAT[repository-programming-006] retired absolute API deadlines cannot override the admitted request window', async () => {
  const result = await runWrapped('class Js extends JsProgram { async run(){ return await this.cat("a.txt"); } }',
    { cat: async () => ({ text: 'x' }) }, { deadlineEpochMs: Date.now() - 1000 })
  assert.equal(result.ok, true)
  assert.deepEqual(JSON.parse(result.value), { text: 'x' })
})

test('WHAT[repository-programming-006] JS054_2_output_bound_rejects_oversized_results', async () => {
  const big = `class Js extends JsProgram {
  async run() { return { data: 'x'.repeat(1000) }; }
}`
  const result = await runWrapped(big, { js: {} }, { outputBound: 100 })
  assert.equal(result.ok, false)
  assert.equal(failureCode(result), 'RESULT_TOO_LARGE')
})

test('WHAT[repository-programming-006] output budget measures UTF-8 bytes and accepts the exact boundary', async () => {
  const value = { text: '中文🙂' }
  const bytes = Buffer.byteLength(JSON.stringify(value), 'utf8')
  const program = `class Js extends JsProgram { async run() { return ${JSON.stringify(value)}; } }`
  assert.ok(bytes > JSON.stringify(value).length)
  const exact = await runWrapped(program, { js: {} }, { outputBound: bytes })
  assert.equal(exact.ok, true)
  assert.deepEqual(JSON.parse(exact.value), value)
  const over = await runWrapped(program, { js: {} }, { outputBound: bytes - 1 })
  assert.equal(over.ok, false)
  assert.equal(failureCode(over), 'RESULT_TOO_LARGE')
})
}

// Real child-process execution contains computation; host replies cross as data.

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { run } = await import("../../../dist/Repository/Programming/Js/RuntimeSurface.js");

const BASE = `class JsProgram {
  constructor(api) { this._api = api; }
  cat(path) { return this._api.cat(path); }
}`
const runWrapped = async (
  program,
  api,
  { deadlineMs = 2000, outputBound = 1 << 20, deadlineEpochMs = Date.now() + 60_000 } = {},
) => run(BASE, program, api, deadlineMs, deadlineEpochMs, outputBound)
const failureCode = (result) => result.code

test('WHAT[repository-programming-006] JS054_3_steady_allocation_loop_is_contained_by_deadline_and_host_survives_for_subsequent_programs', async () => {
  const api = { cat: async () => ({ text: 'x' }) }
  const allocating = `class Js extends JsProgram {
  async run() {
    const chunks = [];
    while (true) {
      chunks.push('x'.repeat(1024));
      if (chunks.length > 64) chunks.shift();
    }
  }
}`
  // A bounded working set isolates execution-budget containment from host memory.
  const contained = await runWrapped(allocating, api, { deadlineMs: 200 })
  assert.equal(contained.ok, false)
  assert.equal(failureCode(contained), 'PROGRAM_TIMEOUT')

  // The Host process is not exhausted by the contained program: the next
  // program in the SAME process still runs and still succeeds.
  const healthy = await runWrapped(`class Js extends JsProgram {
  async run() { return { sum: 1 + 2 }; }
}`, api)
  assert.equal(healthy.ok, true, 'the Host survives the contained program and remains functional')
  assert.deepEqual(JSON.parse(healthy.value), { sum: 3 })
})
}

// Moved from tests/006-cut25-escapes.test.mjs (CUT-25 membrane escapes): clause [006]; the requirement tree only admits NNN.test.mjs.
{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { mkdtempSync, writeFileSync, existsSync, rmSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join } = await import("node:path");
const { run } = await import("../../../dist/Repository/Programming/Js/RuntimeSurface.js");

const BASE = `class JsProgram {
  constructor(api) { this._api = api; }
  cat(path) { return this._api.cat(path); }
  ls(path) { return this._api.ls(path); }
}`
const runWrapped = (program, api, opts = {}) =>
  run(BASE, program, api, opts.deadlineMs ?? 2000, opts.deadlineEpochMs ?? Date.now() + 60_000, opts.outputBound ?? (1 << 20))

const reach = (expr) =>
  `const p = (${expr}); return { pid: typeof (p && p.pid), canFs: typeof (p && p.getBuiltinModule) };`

test('WHAT[repository-programming-006] CUT25_escape_via_global_api_binding_is_denied', async () => {
  const program = `class Js extends JsProgram { async run() { ${reach(`api.constructor.constructor('return process')()`)} } }`
  const result = await runWrapped(program, { ls: async () => [] })
  assert.equal(result.ok, false)
  assert.match(String(result.reason || result.code), /process is not defined|PROGRAM_FAILED|not defined/i)
})

test('WHAT[repository-programming-006] CUT25_escape_via_deadline_ref_global_is_denied', async () => {
  const program = `class Js extends JsProgram { async run() { ${reach(`__deadlineRef.constructor.constructor('return process')()`)} } }`
  const result = await runWrapped(program, { ls: async () => [] })
  assert.equal(result.ok, false)
})

test('WHAT[repository-programming-006] CUT25_escape_via_globalThis_constructor_is_denied', async () => {
  const program = `class Js extends JsProgram { async run() { ${reach(`globalThis.constructor.constructor('return process')()`)} } }`
  const result = await runWrapped(program, { ls: async () => [] })
  assert.equal(result.ok, false)
  assert.match(String(result.reason || ''), /process is not defined|Code generation from strings disallowed/i)
})

test('WHAT[repository-programming-006] CUT25_escape_via_returned_array_constructor_is_denied', async () => {
  const program = `class Js extends JsProgram {
  async run() {
    const r = await this.ls('.');
    ${reach(`r.constructor.constructor('return process')()`)}
  }
}`
  const result = await runWrapped(program, { ls: async () => ['a.txt'] })
  assert.equal(result.ok, false)
  assert.match(String(result.reason || ''), /process is not defined|Code generation from strings disallowed/i)
})

test('WHAT[repository-programming-006] CUT25_escape_cannot_write_outside_workspace', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'cut25-reg-'))
  writeFileSync(join(dir, 'a.txt'), 'x')
  const outside = join(dir, '..', 'cut25-reg-escaped-' + process.pid)
  const program = `class Js extends JsProgram {
  async run() {
    const r = await this.ls('.');
    const p = r.constructor.constructor('return process')();
    if (p && p.getBuiltinModule) p.getBuiltinModule('fs').writeFileSync(${JSON.stringify(outside)}, 'x');
    return { wrote: !!(p && p.getBuiltinModule) };
  }
}`
  try {
    const result = await runWrapped(program, { ls: async () => ['a.txt'] })
    assert.equal(result.ok, false)
    assert.equal(existsSync(outside), false, 'escaped write must not land outside the workspace')
  } finally {
    rmSync(outside, { force: true })
    rmSync(dir, { recursive: true, force: true })
  }
})

test('WHAT[repository-programming-006] pending async work and post-await computation terminate within the execution budget', async () => {
  // An outer process also bounds the test if the execution protection regresses.
  const { spawnSync } = await import('node:child_process')
  const script = `
    import { run } from ${JSON.stringify(new URL('../../../dist/Repository/Programming/Js/RuntimeSurface.js', import.meta.url).pathname)};
    const BASE = \`class JsProgram { constructor(api) { this._api = api; } cat(path) { return this._api.cat(path); } }\`;
    const program = \`class Js extends JsProgram { async run() { await this.cat('a.txt'); while (true) {} } }\`;
    const t0 = Date.now();
    const result = await run(BASE, program, { cat: async () => ({ text: 'x' }) }, 200, Date.now() + 2000, 1 << 20);
    const elapsed = Date.now() - t0;
    if (!result || result.ok !== false || result.code !== 'PROGRAM_TIMEOUT' || elapsed >= 5000) {
      console.error(JSON.stringify({ result, elapsed }));
      process.exit(2);
    }
  `
  const r = spawnSync(process.execPath, ['--input-type=module', '-e', script], { encoding: 'utf8', timeout: 10000 })
  assert.equal(r.status, 0, r.stderr || r.stdout || 'child failed')
})

test('WHAT[repository-programming-006] CUT25_escape_via_patched_WeakMap_has_is_denied', async () => {
  const program = `class Js extends JsProgram {
  async run() {
    const isProc = (p) => !!(p && typeof p.pid === 'number' && typeof p.getBuiltinModule === 'function');
    let leaked = false;
    const original = WeakMap.prototype.has;
    WeakMap.prototype.has = function (key) {
      try { const p = key && key.constructor && key.constructor.constructor('return process')(); if (isProc(p)) leaked = true; } catch (_) {}
      return original.call(this, key);
    };
    try { await this.ls('.'); } finally { WeakMap.prototype.has = original; }
    return { leaked };
  }
}`
  const result = await runWrapped(program, { ls: async () => ['a.txt'] })
  assert.equal(result.ok, true, JSON.stringify(result))
  const value = typeof result.value === 'string' ? JSON.parse(result.value) : result.value
  assert.equal(value.leaked, false, 'patched WeakMap.prototype.has must not observe a host value')
})

test('WHAT[repository-programming-006] CUT25_escape_via_patched_WeakMap_set_is_denied', async () => {
  const program = `class Js extends JsProgram {
  async run() {
    const isProc = (p) => !!(p && typeof p.pid === 'number' && typeof p.getBuiltinModule === 'function');
    let leaked = false;
    const original = WeakMap.prototype.set;
    WeakMap.prototype.set = function (key, value) {
      try { const p = key && key.constructor && key.constructor.constructor('return process')(); if (isProc(p)) leaked = true; } catch (_) {}
      return original.call(this, key, value);
    };
    try { await this.ls('.'); } finally { WeakMap.prototype.set = original; }
    return { leaked };
  }
}`
  const result = await runWrapped(program, { ls: async () => ['a.txt'] })
  assert.equal(result.ok, true, JSON.stringify(result))
  const value = typeof result.value === 'string' ? JSON.parse(result.value) : result.value
  assert.equal(value.leaked, false, 'patched WeakMap.prototype.set must not observe a host value')
})
}
