import test from 'node:test'

{
// Host-native `run` tool is retired; `js.call` supersedes it (repository-programming [050]-[052]).
const { default: assert } = await import('node:assert/strict')
const { default: test } = await import('node:test')
const { sandbox, runProgram, caseName, render } = await import('./support/executor.mjs')

test('WHAT[process-execution-005] js.call rejects non-positive or non-finite maxDuration before spawning', async () => {
  const { dir, cleanup } = sandbox('wxs-proc-005-')
  try {
    for (const rawExpr of ['0', '-5', 'Number.NaN', 'Number.POSITIVE_INFINITY']) {
      const outcome = await runProgram(
        dir,
        `    return await js.call(class extends js.Callable {
      maxDuration = ${rawExpr};
      next() { return "true"; }
      check() { return true; }
    });`,
      )
      assert.equal(caseName(outcome), 'Failed', `maxDuration=${rawExpr} must fail`)
      assert.match(render(outcome), /CALLABLE_SPEC_INCOMPLETE/, `maxDuration=${rawExpr}`)
    }
  } finally {
    cleanup()
  }
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
  commandView,
  estimate,
  estimateView,
} = await import('../../../dist/Process/Surface.js')

test('WHAT[process-execution-005] EXEC_010_process_request_carries_all_fields', () => {
  const cmd = command('sh', ['-lc', 'echo hi'], '/tmp/wx', 'input')
  const cmdView = commandView(cmd)
  const estView = estimateView(estimate(42, 65536, 'large'))

  assert.equal(cmdView.fileName, 'sh')
  assert.deepEqual(cmdView.arguments, ['-lc', 'echo hi'])
  assert.equal(cmdView.workingDirectory, '/tmp/wx')
  assert.equal(cmdView.stdin, 'input')
  assert.equal(estView.runtimeSeconds, 42)
  assert.equal(estView.outputBytes, 65536)
  assert.equal(estView.memory, 'large')
})
}

{
  const assert = (await import('node:assert/strict')).default
  const runtime = await import('../../../dist/Process/Surface.js')
  test('WHAT[process-execution-005] estimate validation rejects nonfinite or nonpositive time and negative output', () => {
    assert.equal(runtime.validateEstimate(10, 0).ok, true)
    for (const invalid of [NaN, Infinity, -Infinity, 0, -5]) {
      const result = runtime.validateEstimate(invalid, 1024)
      assert.equal(result.ok, false)
      assert.match(result.error, /finite positive number/)
    }
    const result = runtime.validateEstimate(10, -1)
    assert.equal(result.ok, false)
    assert.match(result.error, /non-negative/)
  })
}
