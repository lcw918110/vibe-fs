import test from 'node:test'

{
  const {default:assert}=await import('node:assert/strict')
  const {sandbox,runProgram,caseName,parseData}=await import('./support/executor.mjs')
  test('WHAT[process-execution-010] a completed call reports separate raw streams and actual exit code',async()=>{
    const {dir,cleanup}=sandbox('wxs-proc010-')
    try {
      const outcome=await runProgram(dir,"return await this.call(\"printf 'hello-stdout'; printf 'hello-stderr' >&2\");")
      assert.equal(caseName(outcome),'Succeeded')
      const result=parseData(outcome)
      assert.equal(result.exitCode,0);assert.equal(result.stdout,'hello-stdout');assert.equal(result.stderr,'hello-stderr')
    } finally { cleanup() }
  })
  test('WHAT[process-execution-010] nonzero shell exit remains command data',async()=>{
    const {dir,cleanup}=sandbox('wxs-proc010-nonzero-')
    try {
      const result=await runProgram(dir,'return await this.call("exit 3");')
      assert.equal(caseName(result),'Succeeded');assert.equal(parseData(result).exitCode,3)
    } finally { cleanup() }
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

test('WHAT[process-execution-010] CUT_02_non_pty_host_wires_runtime_process_session_and_retires_direct_child_process_spawn', async () => {
  const root = join(dirname(fileURLToPath(import.meta.url)), '../../..')
  const hostSrc = readFileSync(join(root, 'src/Wanxiangshu/Process/NodeProcessHost.fs'), 'utf8')
  assert.match(hostSrc, /Import\("ProcessSession",\s*"wanxiangli\/runtime"\)/)
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

test('WHAT[process-execution-010] a real stdin pipe failure cannot become a successful incomplete run', async () => {
  const outcome = await run(
    command(process.execPath, ['-e', 'require("node:fs").closeSync(0); setTimeout(() => process.exit(0), 100)'], process.cwd(), 'x'.repeat(2 * 1024 * 1024)),
    estimate(10, 4096, 'medium'),
    context(process.cwd(), 60_000),
    createCancellationToken(false),
  )
  assert.equal(outcome.ok, false)
  assert.equal(outcome.error.kind, 'ExecutionFailed')
  assert.match(outcome.error.reason, /EPIPE|pipe|closed|write/i)
})
}
