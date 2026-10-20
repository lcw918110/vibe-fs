import assert from 'node:assert/strict'
import test from 'node:test'
import * as toolResultBound from '../../../dist/Host/Contract/ToolResultBound.js'
import { sandbox, runProgram, caseName, render, nodeCommand } from './support/executor.mjs'

// Host-native `run` tool is retired; `js.call` supersedes it (repository-programming [050]-[052])
// and complete provider results are bounded to the UTF-8 window by ToolResultBound.

for (const offset of [0, 1, 2, 3, 4, 7]) {
  test(`WHAT[process-execution-015] UTF-8 tail bound at alignment offset ${offset} fits the host byte ceiling without splitting a code point`, () => {
    const original = 'a'.repeat(offset) + '中文🔥'.repeat(6000)
    assert.ok(Buffer.byteLength(original, 'utf8') > toolResultBound.HostMaxBytes())
    const bounded = toolResultBound.bound(original)
    assert.ok(bounded.startsWith(toolResultBound.Marker))
    const tail = bounded.slice(toolResultBound.Marker.length)
    assert.equal(tail.includes('\uFFFD'), false)
    assert.ok(Buffer.byteLength(tail, 'utf8') <= toolResultBound.ContentMaxBytes())
    assert.ok(Buffer.byteLength(bounded, 'utf8') <= toolResultBound.HostMaxBytes())
    assert.ok(original.endsWith(tail))
    assert.equal(new TextDecoder('utf-8', { fatal: true }).decode(Buffer.from(bounded)), bounded)
    assert.equal(toolResultBound.fitsInWindow(bounded), true)
  })
}

test('WHAT[process-execution-015] the complete visible result bounded by ToolResultBound fits the host byte and line budget', async () => {
  const { dir, cleanup } = sandbox('wxs-proc-015-')
  try {
    const cmd = nodeCommand("process.stdout.write(('中文🔥line\\n').repeat(3000))")
    const outcome = await runProgram(
      dir,
      `    return await js.call(class extends js.Callable {
      maxDuration = 5000;
      interval = 50;
      next(ctx) { return ctx.step === 0 ? ${JSON.stringify(cmd)} : null; }
      check() { return true; }
      watch(ctx) { if (ctx.exited && ctx.exitCode !== 0) return { abort: "command failed" }; return true; }
      sanity() { return { nextDone: () => [{ root: '/', step: 1 }], watchVeto: () => [{ root: '/', step: 0, stdout: '', stderr: '', exited: true, exitCode: 2, backgrounds: [], elapsedMs: 0, remainingMs: 60000 }] }; }
    });`,
    )
    assert.equal(caseName(outcome), 'Succeeded')
    const wire = toolResultBound.bound(render(outcome))
    assert.ok(Buffer.byteLength(wire, 'utf8') <= toolResultBound.HostMaxBytes())
    assert.ok(wire.split('\n').length <= toolResultBound.HostMaxLines())
    assert.equal(toolResultBound.fitsInWindow(wire), true)
  } finally {
    cleanup()
  }
})
