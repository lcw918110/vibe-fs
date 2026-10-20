import assert from 'node:assert/strict'
import test from 'node:test'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { execute, caseName, render } from '../../../dist/Repository/Programming/Js/WorkflowSurface.js'

const sandbox = () => {
  const dir = mkdtempSync(join(tmpdir(), 'wxs-run-send-'))
  return { dir, cleanup: () => rmSync(dir, { recursive: true, force: true }) }
}

const program = (body) => `class Js extends JsProgram {
  async run() {
${body}
  }
}`

const runProgram = (dir, body, sessionId = 'sess-058') =>
  execute(dir, 'DevOps', 'en', sessionId, { program: program(body) }, 20_000, Date.now() + 120_000, 1 << 20, null)

const sanity = (nextStep) => `sanity() {
        return {
          nextDone: () => [{ root: "/", step: ${nextStep} }],
          watchVeto: () => [{ root: "/", step: 0, stdout: "", stderr: "", exited: true, exitCode: 2, backgrounds: [{ name: "echoer", exited: true, exitCode: 1 }], elapsedMs: 0, remainingMs: 60000 }],
        };
      }`

test('WHAT[repository-programming-058] js.call drives an interactive child through send and observes the deltas', async () => {
  const { dir, cleanup } = sandbox()
  try {
    // A child that echoes each stdin line back, prefixed.
    const child = `node -e "process.stdin.setEncoding('utf8'); process.stdin.on('data', (d) => process.stdout.write('echo:' + d.trim() + '\\\\n'))"`
    const outcome = await runProgram(
      dir,
      `    const seen = [];
    const out = await js.call(class extends js.Callable {
      maxDuration = 15000;
      interval = 100;
      next(ctx) {
        if (ctx.step === 0) return { spawn: ${JSON.stringify(child)}, name: "echoer" };
        if (ctx.step === 1) return { send: { name: "echoer", input: "ping" } };
        if (ctx.step === 2) return "sleep 0.4";
        if (ctx.step === 3) return { signal: "SIGKILL", name: "echoer" };
        return null;
      }
      watch(ctx) {
        const echoer = ctx.backgrounds.find((b) => b.name === "echoer");
        if (echoer && echoer.exited && ctx.step < 3) return { abort: "child died mid-session" };
        if (ctx.stdout && ctx.stdout.length > 0) seen.push(ctx.stdout);
        return true;
      }
      ${sanity(4)}
    });
    return { out, seen };`,
    )
    assert.equal(caseName(outcome), 'Succeeded')
    const doc = render(outcome)
    assert.match(doc, /echo:ping/, 'the interactive child echoed the injected stdin line')
    assert.match(doc, /status = "completed"/)
  } finally {
    cleanup()
  }
})

test('WHAT[repository-programming-058] send to an unknown child fails with CALL_CHILD_UNKNOWN', async () => {
  const { dir, cleanup } = sandbox()
  try {
    const outcome = await runProgram(
      dir,
      `    return await js.call(class extends js.Callable {
      maxDuration = 3000;
      interval = 100;
      next(ctx) { return ctx.step === 0 ? { send: { name: "nobody", input: "hi" } } : null; }
      watch(ctx) {
        if (ctx.exited && ctx.exitCode !== 0) return { abort: "command failed" };
        return true;
      }
      ${sanity(1)}
    });`,
    )
    assert.equal(caseName(outcome), 'Failed')
    assert.match(render(outcome), /CALL_CHILD_UNKNOWN/)
  } finally {
    cleanup()
  }
})
