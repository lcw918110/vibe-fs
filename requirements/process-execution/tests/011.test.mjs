import assert from 'node:assert/strict'
import test from 'node:test'
import { existsSync } from 'node:fs'
import { join } from 'node:path'
import { sandbox, runProgram, caseName, render, nodeCommand } from './support/executor.mjs'

const { contextDecode, contextView } = await import('../../../dist/OpenCode/Codec/ToolHostSurface.js')

// Host-native `run` tool is retired; `js.call` supersedes it (repository-programming [050]-[052]).

test('WHAT[process-execution-011] RUN_host_context_codec_exposes_plain_snapshot', () => {
  const decoded = contextDecode({ sessionID: 'ses-exec', agent: 'devops' })
  assert.deepEqual(contextView(decoded), {
    sessionId: 'ses-exec',
    agent: 'devops',
    toolCallId: null,
    providerRunId: null,
    promptText: null,
  })
})

test('WHAT[process-execution-011] js.call rejects incomplete Runnable specs before spawning', async () => {
  const { dir, cleanup } = sandbox('wxs-proc-011a-')
  try {
    const marker = join(dir, 'spawned')
    const cmd = nodeCommand(`require('node:fs').writeFileSync(${JSON.stringify(marker)}, 'ran')`)
    const incompleteSpecs = [
      `class { next() { return ${JSON.stringify(cmd)}; } check() { return true; } }`,
      `class { maxDuration = 0; next() { return ${JSON.stringify(cmd)}; } check() { return true; } }`,
      `class { maxDuration = Infinity; next() { return ${JSON.stringify(cmd)}; } check() { return true; } }`,
      `class { maxDuration = 1000; check() { return true; } }`,
      `class { maxDuration = 1000; next() { return ${JSON.stringify(cmd)}; } }`,
    ]
    for (const spec of incompleteSpecs) {
      const outcome = await runProgram(dir, `    return await js.call(${spec});`)
      assert.equal(caseName(outcome), 'Failed')
      assert.match(render(outcome), /CALLABLE_SPEC_INCOMPLETE/)
      assert.equal(existsSync(marker), false, 'incomplete Runnable spec must not spawn the command')
    }
  } finally {
    cleanup()
  }
})

test('WHAT[process-execution-011] js.call rejects unsupported command descriptors with CALL_COMMAND_UNSUPPORTED', async () => {
  const { dir, cleanup } = sandbox('wxs-proc-011b-')
  try {
    const outcome = await runProgram(
      dir,
      `    return await js.call(class extends js.Callable {
      maxDuration = 2000;
      next(ctx) { return ctx.step === 0 ? { unsupported: true } : null; }
      check() { return true; }
      watch(ctx) { if (ctx.exited && ctx.exitCode !== 0) return { abort: "command failed" }; return true; }
      sanity() { return { nextDone: () => [{ root: '/', step: 1 }], watchVeto: () => [{ root: '/', step: 0, stdout: '', stderr: '', exited: true, exitCode: 2, backgrounds: [], elapsedMs: 0, remainingMs: 60000 }] }; }
    });`,
    )
    assert.equal(caseName(outcome), 'Failed')
    assert.match(render(outcome), /CALL_COMMAND_UNSUPPORTED/)
  } finally {
    cleanup()
  }
})

test('WHAT[process-execution-011] js.call maxDuration overrun terminates the foreground command and reports timeout', async () => {
  const { dir, cleanup } = sandbox('wxs-proc-011c-')
  try {
    const marker = join(dir, 'late-marker')
    const outcome = await runProgram(
      dir,
      `    return await js.call(class extends js.Callable {
      maxDuration = 150;
      interval = 30;
      next(ctx) { return ctx.step === 0 ? "sleep 5 && touch late-marker" : null; }
      check() { return true; }
      watch(ctx) { if (ctx.exited && ctx.exitCode !== 0) return { abort: "command failed" }; return true; }
      sanity() { return { nextDone: () => [{ root: '/', step: 1 }], watchVeto: () => [{ root: '/', step: 0, stdout: '', stderr: '', exited: true, exitCode: 2, backgrounds: [], elapsedMs: 0, remainingMs: 60000 }] }; }
    });`,
    )
    assert.equal(caseName(outcome), 'Succeeded')
    assert.match(render(outcome), /status = "timeout"/)
    assert.equal(existsSync(marker), false)
  } finally {
    cleanup()
  }
})
