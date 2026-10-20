import assert from 'node:assert/strict'
import test from 'node:test'
import { sandbox, runProgram, caseName, parseData, nodeCommand } from './support/executor.mjs'
import * as toolResultBound from '../../../dist/Host/Contract/ToolResultBound.js'

// Host-native `run` tool is retired; `js.call` supersedes it (repository-programming [050]-[052]).

test('WHAT[process-execution-013] js.call bounds large output deterministically to head+tail without model distillation', async () => {
  const { dir, cleanup } = sandbox('wxs-proc-013a-')
  try {
    const cmd1 = nodeCommand(`process.stdout.write('HEAD-MARK-' + 'a'.repeat(200000))`)
    const cmd2 = nodeCommand(`process.stdout.write('b'.repeat(200000) + '-TAIL-MARK')`)
    const outcome = await runProgram(
      dir,
      `    const r = await js.call(class extends js.Callable {
      maxDuration = 10000;
      interval = 100;
      next(ctx) {
        if (ctx.step === 0) return ${JSON.stringify(cmd1)};
        if (ctx.step === 1) return ${JSON.stringify(cmd2)};
        return null;
      }
      check() { return true; }
      watch(ctx) { if (ctx.exited && ctx.exitCode !== 0) return { abort: "command failed" }; return true; }
      sanity() { return { nextDone: () => [{ root: '/', step: 2 }], watchVeto: () => [{ root: '/', step: 0, stdout: '', stderr: '', exited: true, exitCode: 2, backgrounds: [], elapsedMs: 0, remainingMs: 60000 }] }; }
    });
    return {
      exitCode: r.exitCode,
      truncated: r.truncated,
      bytes: r.bytes,
      keptLength: r.stdout.length,
      startsWithHead: r.stdout.startsWith("HEAD-MARK-"),
      endsWithTail: r.stdout.endsWith("-TAIL-MARK"),
    };`,
    )
    assert.equal(caseName(outcome), 'Succeeded')
    const parsed = parseData(outcome)
    assert.equal(parsed.exitCode, 0)
    assert.equal(parsed.truncated, true)
    assert.equal(parsed.bytes, 10 + 200000 + 200000 + 10)
    assert.equal(parsed.keptLength, 262144)
    assert.equal(parsed.startsWithHead, true)
    assert.equal(parsed.endsWithTail, true)
  } finally {
    cleanup()
  }
})

test('WHAT[process-execution-013] small output preserves separate stdout and stderr in onExit without truncation', async () => {
  const { dir, cleanup } = sandbox('wxs-proc-013b-')
  try {
    const cmd = nodeCommand("process.stdout.write('hello'); process.stderr.write('warning')")
    const outcome = await runProgram(
      dir,
      `    let exited = null;
    const r = await js.call(class extends js.Callable {
      maxDuration = 5000;
      interval = 50;
      next(ctx) { return ctx.step === 0 ? ${JSON.stringify(cmd)} : null; }
      check() { return true; }
      onExit(res) { exited = { stdout: res.stdout, stderr: res.stderr }; }
      watch(ctx) { if (ctx.exited && ctx.exitCode !== 0) return { abort: "command failed" }; return true; }
      sanity() { return { nextDone: () => [{ root: '/', step: 2 }], watchVeto: () => [{ root: '/', step: 0, stdout: '', stderr: '', exited: true, exitCode: 2, backgrounds: [], elapsedMs: 0, remainingMs: 60000 }] }; }
    });
    return { exitCode: r.exitCode, truncated: r.truncated, stdout: exited.stdout, stderr: exited.stderr };`,
    )
    assert.equal(caseName(outcome), 'Succeeded')
    const parsed = parseData(outcome)
    assert.equal(parsed.exitCode, 0)
    assert.equal(parsed.truncated, false)
    assert.equal(parsed.stdout, 'hello')
    assert.equal(parsed.stderr, 'warning')
  } finally {
    cleanup()
  }
})

test('WHAT[process-execution-013] ToolResultBound keeps deterministic raw tail when output exceeds host window', () => {
  const original = 'EARLY-ERROR-\n' + 'x'.repeat(60000) + '\nLATEST-RAW-OUTPUT'
  const bounded = toolResultBound.bound(original)
  assert.ok(bounded.startsWith(toolResultBound.Marker))
  assert.ok(bounded.endsWith('\nLATEST-RAW-OUTPUT'))
  assert.equal(toolResultBound.fitsInWindow(bounded), true)
})
