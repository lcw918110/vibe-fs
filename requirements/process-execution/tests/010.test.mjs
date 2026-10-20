import test from 'node:test'

{
// Host-native `run` tool is retired; `js.call` supersedes it (repository-programming [050]-[052]).
const { default: assert } = await import('node:assert/strict')
const { default: test } = await import('node:test')
const { sandbox, runProgram, caseName, parseData } = await import('./support/executor.mjs')

test('WHAT[process-execution-010] js.call completed command reports exit code and streams via onExit and outcome', async () => {
  const { dir, cleanup } = sandbox('wxs-proc-010a-')
  try {
    const outcome = await runProgram(
      dir,
      `    let exitedWith = null;
    const result = await js.call(class extends js.Callable {
      maxDuration = 5000;
      interval = 50;
      next(ctx) { return ctx.step === 0 ? "printf 'hello-stdout'; printf 'hello-stderr' >&2" : null; }
      check() { return true; }
      watch(ctx) { if (ctx.exited && ctx.exitCode !== 0) return { abort: "command failed" }; return true; }
      sanity() { return { nextDone: () => [{ root: '/', step: 1 }], watchVeto: () => [{ root: '/', step: 0, stdout: '', stderr: '', exited: true, exitCode: 2, backgrounds: [], elapsedMs: 0, remainingMs: 60000 }] }; }
      onExit(r) { exitedWith = { exitCode: r.exitCode, stdout: r.stdout, stderr: r.stderr }; }
    });
    return { exitCode: result.exitCode, onExitStdout: exitedWith.stdout, onExitStderr: exitedWith.stderr };`,
    )
    assert.equal(caseName(outcome), 'Succeeded')
    const parsed = parseData(outcome)
    assert.equal(parsed.exitCode, 0)
    assert.equal(parsed.onExitStdout, 'hello-stdout')
    assert.equal(parsed.onExitStderr, 'hello-stderr')
  } finally {
    cleanup()
  }
})

test('WHAT[process-execution-010] js.call nonzero exit is reported as data rather than thrown', async () => {
  const { dir, cleanup } = sandbox('wxs-proc-010b-')
  try {
    const outcome = await runProgram(
      dir,
      `    return await js.call(class extends js.Callable {
      maxDuration = 5000;
      interval = 50;
      next(ctx) { return ctx.step === 0 ? "exit 3" : null; }
      check() { return true; }
      watch(ctx) { if (ctx.exited && ctx.exitCode !== 0 && ctx.exitCode !== 3) return { abort: "command failed" }; return true; }
      sanity() { return { nextDone: () => [{ root: '/', step: 1 }], watchVeto: () => [{ root: '/', step: 0, stdout: '', stderr: '', exited: true, exitCode: 2, backgrounds: [], elapsedMs: 0, remainingMs: 60000 }] }; }
    });`,
    )
    assert.equal(caseName(outcome), 'Succeeded')
    const parsed = parseData(outcome)
    assert.equal(parsed.status, 'completed')
    assert.equal(parsed.exitCode, 3)
  } finally {
    cleanup()
  }
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");

const { renderPtyCompletion } = await import('../../../dist/Process/Surface.js')

test('WHAT[process-execution-010] EXEC_004_pty_completion_is_natural_language_plus_exit_code', () => {
  const wire = renderPtyCompletion('shell', 'pty-9', 'ended', 0)
  assert.match(wire, /# shell has ended\./)
  assert.match(wire, /exit_code = 0/)
  assert.ok(!wire.includes('pty_id'))
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");

const {
  command,
  context,
  createCancellationToken,
  estimate,
  runWithLauncher,
  runWithHostLauncher,
} = await import('../../../dist/Process/Surface.js')
const CTX = context(undefined, 3_600_000)
const cmd = command('sh', ['-c', 'echo hi'], undefined, undefined)
const makeEstimate = (runtimeSeconds = 10, outputBytes = 1024, memory = 'medium') =>
  estimate(runtimeSeconds, outputBytes, memory)
const live = () => createCancellationToken(false)
const cancelled = () => createCancellationToken(true)
const okLauncher = (exitCode = 0, out = 'hello', err = '') => async (_command, _token) => [
  exitCode,
  new TextEncoder().encode(out),
  new TextEncoder().encode(err),
]

test('WHAT[process-execution-010] EXEC_011_successful_run_collects_stdout_and_exit_code', async () => {
  const result = await runWithLauncher(okLauncher(0, 'the output', ''), cmd, makeEstimate(), CTX, live())
  assert.equal(result.ok, true)
  assert.deepEqual(result.value, {
    kind: 'Completed',
    exitCode: 0,
    stdout: 'the output',
    stderr: '',
    spooled: false,
  })
})
test('WHAT[process-execution-010] EXEC_011_nonzero_exit_is_still_an_ok_outcome', async () => {
  const result = await runWithLauncher(okLauncher(3, '', 'boom'), cmd, makeEstimate(), CTX, live())
  assert.equal(result.ok, true)
  assert.equal(result.value.kind, 'Completed')
  assert.equal(result.value.exitCode, 3)
})
}

{
const { default: assert } = await import("node:assert/strict");
const { readFileSync } = await import("node:fs");
const { dirname, join } = await import("node:path");
const { default: test } = await import("node:test");
const { fileURLToPath } = await import("node:url");
const {
  command,
  context,
  createCancellationToken,
  estimate,
  run,
} = await import('../../../dist/Process/Surface.js')

test('WHAT[process-execution-010] CUT_02_non_pty_host_wires_wanxiangli_process_session_and_retires_direct_child_process_spawn', async () => {
  const root = join(dirname(fileURLToPath(import.meta.url)), '../../..')
  const hostSrc = readFileSync(join(root, 'src/Wanxiangshu/Process/NodeProcessHost.fs'), 'utf8')
  assert.match(hostSrc, /Import\("ProcessSession",\s*"wanxiangli\/process"\)/)
  assert.doesNotMatch(hostSrc, /node:child_process/)

  const outcome = await run(
    command(process.execPath, ['-e', 'process.stdout.write("via-process-session"); process.stderr.write("err-stream"); process.exit(5)'], process.cwd(), undefined),
    estimate(10, 4096, 'medium'),
    context(process.cwd(), 60_000),
    createCancellationToken(false),
  )
  assert.equal(outcome.ok, true)
  assert.deepEqual(outcome.value, {
    kind: 'Completed',
    exitCode: 5,
    stdout: 'via-process-session',
    stderr: 'err-stream',
    spooled: false,
  })
})
}
