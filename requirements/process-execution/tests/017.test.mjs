import assert from 'node:assert/strict'
import test from 'node:test'
import { existsSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { sandbox, runProgram, caseName, parseData } from './support/executor.mjs'

// Host-native `run` tool and its 10-second `deadline_seconds` policy (along with
// the four PTY tools `open-terminal`, `send-terminal`, `read-terminal`, `signal-terminal`)
// are retired and superseded by `js.call` with explicit `maxDuration` and sliced `check(ctx)`
// (repository-programming [050]-[052]).

const root = join(dirname(fileURLToPath(import.meta.url)), '../../..')

test('WHAT[process-execution-017] retired host run and PTY tool resources and ExecutorTool are absent', () => {
  assert.equal(existsSync(join(root, 'src/Wanxiangshu/OpenCode/Tools/ExecutorTool.fs')), false)
  assert.equal(existsSync(join(root, 'src/Wanxiangshu/OpenCode/Tools/ExecutorToolSurface.fs')), false)
  assert.equal(existsSync(join(root, 'resources/provider/tool/run')), false)
})

test('WHAT[process-execution-017] js.call accepts multi-minute maxDuration without an artificial 10-second cap and completes fast commands', async () => {
  const { dir, cleanup } = sandbox('wxs-proc-017-')
  try {
    const outcome = await runProgram(
      dir,
      `    return await js.call(class extends js.Callable {
      maxDuration = 60000;
      interval = 100;
      next(ctx) { return ctx.step === 0 ? "printf 'ok'" : null; }
      check() { return true; }
      watch(ctx) { if (ctx.exited && ctx.exitCode !== 0) return { abort: "command failed" }; return true; }
      sanity() { return { nextDone: () => [{ root: '/', step: 1 }], watchVeto: () => [{ root: '/', step: 0, stdout: '', stderr: '', exited: true, exitCode: 2, backgrounds: [], elapsedMs: 0, remainingMs: 60000 }] }; }
    });`,
    )
    assert.equal(caseName(outcome), 'Succeeded')
    const parsed = parseData(outcome)
    assert.equal(parsed.status, 'completed')
    assert.equal(parsed.exitCode, 0)
    assert.equal(parsed.stdout, 'ok')
  } finally {
    cleanup()
  }
})
