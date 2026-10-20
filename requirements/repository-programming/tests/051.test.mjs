import assert from 'node:assert/strict'
import test from 'node:test'
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { createServer } from 'node:net'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { execute, caseName, render } from '../../../dist/Repository/Programming/Js/WorkflowSurface.js'

const sandbox = () => {
  const dir = mkdtempSync(join(tmpdir(), 'wxs-run-multi-'))
  return { dir, cleanup: () => rmSync(dir, { recursive: true, force: true }) }
}

const freePort = () =>
  new Promise((resolve) => {
    const probe = createServer()
    probe.listen(0, '127.0.0.1', () => {
      const { port } = probe.address()
      probe.close(() => resolve(port))
    })
  })

const program = (body) => `class Js extends JsProgram {
  async run() {
${body}
  }
}`

const runProgram = (dir, body, sessionId = 'sess-051') =>
  execute(dir, 'DevOps', 'en', sessionId, { program: program(body) }, 20_000, Date.now() + 120_000, 1 << 20, null)

const sanity = (nextStep) => `sanity() {
        return {
          nextDone: () => [{ root: "/", step: ${nextStep} }],
          watchVeto: () => [{ root: "/", step: 0, stdout: "", stderr: "", exited: true, exitCode: 2, backgrounds: [], elapsedMs: 0, remainingMs: 60000 }],
        };
      }`

test('WHAT[repository-programming-051] spawn, waitFor and signal drive one background server', async () => {
  const { dir, cleanup } = sandbox()
  try {
    const port = await freePort()
    writeFileSync(
      join(dir, 'server.cjs'),
      `const http = require('node:http');\nconst port = Number(process.argv[2]);\nhttp.createServer((req, res) => { res.end('ok'); }).listen(port, '127.0.0.1');\n`,
      'utf8',
    )
    const outcome = await runProgram(
      dir,
      `    return await js.call(class extends js.Callable {
      maxDuration = 15000;
      interval = 100;
      next(ctx) {
        if (ctx.step === 0) return { spawn: "node server.cjs ${port}", name: "srv" };
        if (ctx.step === 1) return { waitFor: { port: ${port}, timeoutMs: 8000 } };
        if (ctx.step === 2) return "curl -s http://127.0.0.1:${port}/";
        if (ctx.step === 3) return { signal: "SIGTERM", name: "srv" };
        return null;
      }
      watch(ctx) {
        if (ctx.exited && ctx.exitCode !== 0) return { abort: "command failed" };
        return true;
      }
      ${sanity(4)}
    });`,
    )
    assert.equal(caseName(outcome), 'Succeeded')
    const doc = render(outcome)
    assert.match(doc, /status = "completed"/)
    assert.match(doc, /ok/)
  } finally {
    cleanup()
  }
})

test('WHAT[repository-programming-051] waitFor on a closed port fails with CALL_WAIT_TIMEOUT', async () => {
  const { dir, cleanup } = sandbox()
  try {
    const port = await freePort()
    const outcome = await runProgram(
      dir,
      `    return await js.call(class extends js.Callable {
      maxDuration = 5000;
      interval = 100;
      next(ctx) { return ctx.step === 0 ? { waitFor: { port: ${port}, timeoutMs: 300 } } : null; }
      watch(ctx) {
        if (ctx.exited && ctx.exitCode !== 0) return { abort: "command failed" };
        return true;
      }
      ${sanity(1)}
    });`,
    )
    assert.equal(caseName(outcome), 'Failed')
    assert.match(render(outcome), /CALL_WAIT_TIMEOUT/)
  } finally {
    cleanup()
  }
})

test('WHAT[repository-programming-051] signal on an unknown name fails with CALL_CHILD_UNKNOWN', async () => {
  const { dir, cleanup } = sandbox()
  try {
    const outcome = await runProgram(
      dir,
      `    return await js.call(class extends js.Callable {
      maxDuration = 5000;
      interval = 100;
      next(ctx) { return ctx.step === 0 ? { signal: "SIGTERM", name: "nope" } : null; }
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

test('WHAT[repository-programming-051] a crashed background is visible to watch and can abort the run', async () => {
  const { dir, cleanup } = sandbox()
  try {
    const port = await freePort()
    const outcome = await runProgram(
      dir,
      `    return await js.call(class extends js.Callable {
      maxDuration = 10000;
      interval = 100;
      next(ctx) {
        if (ctx.step === 0) return { spawn: "node -e 'process.exit(3)'", name: "die" };
        if (ctx.step === 1) return { waitFor: { port: ${port}, timeoutMs: 5000 } };
        return null;
      }
      watch(ctx) {
        const dead = ctx.backgrounds.find((entry) => entry.name === "die");
        return dead && dead.exited && dead.exitCode === 3 ? { abort: "background died" } : true;
      }
      sanity() {
        return {
          nextDone: () => [{ root: "/", step: 2 }],
          watchVeto: () => [{ root: "/", step: 1, stdout: "", stderr: "", exited: false, exitCode: null, backgrounds: [{ name: "die", exited: true, exitCode: 3 }], elapsedMs: 0, remainingMs: 60000 }],
        };
      }
    });`,
    )
    assert.equal(caseName(outcome), 'Succeeded')
    assert.match(render(outcome), /status = "aborted"/)
  } finally {
    cleanup()
  }
})
