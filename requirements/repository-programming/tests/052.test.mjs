import assert from 'node:assert/strict'
import test from 'node:test'
import { existsSync, mkdtempSync, rmSync, statSync, writeFileSync } from 'node:fs'
import { createServer } from 'node:net'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import {
  execute,
  caseName,
  parkedHandle,
  parkedPayload,
  render,
} from '../../../dist/Repository/Programming/Js/WorkflowSurface.js'

const sandbox = () => {
  const dir = mkdtempSync(join(tmpdir(), 'wxs-run-wget-'))
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

const runProgram = (dir, body, sessionId) =>
  execute(dir, 'DevOps', 'en', sessionId, { program: program(body) }, 30_000, Date.now() + 180_000, 1 << 20, null)

// A throttled range-capable server so wget -c can really resume and watch can
// observe mid-download growth. The payload is deterministic and self-checking.
const serveScript = () => `const http = require('node:http')
const fs = require('node:fs')
const path = require('node:path')
const root = process.argv[2]
const port = Number(process.argv[3])
http.createServer((req, res) => {
  const name = decodeURIComponent(req.url.split('?')[0]).replace(/^\\//, '')
  const file = path.join(root, name)
  if (!fs.existsSync(file)) { res.writeHead(404); res.end('missing'); return }
  const size = fs.statSync(file).size
  const range = /bytes=(\\d+)-/.exec(req.headers.range || '')
  const start = range ? Number(range[1]) : 0
  res.writeHead(range ? 206 : 200, {
    'Accept-Ranges': 'bytes',
    'Content-Length': size - start,
    ...(range ? { 'Content-Range': 'bytes ' + start + '-' + (size - 1) + '/' + size } : {}),
  })
  const stream = fs.createReadStream(file, { start })
  stream.on('data', (chunk) => {
    res.write(chunk)
    stream.pause()
    setTimeout(() => stream.resume(), 25)
  })
  stream.on('end', () => res.end())
}).listen(port, '127.0.0.1')
`

const sizeOf = (file) => (existsSync(file) ? statSync(file).size : -1)

test('WHAT[repository-programming-052] wget -c resumes a partial download after an aborted run', async () => {
  const { dir, cleanup } = sandbox()
  try {
    const port = await freePort()
    const first = Buffer.alloc(400 * 1024, 7)
    const payloadTwo = Buffer.alloc(300 * 1024, 9)
    writeFileSync(join(dir, 'big1.bin'), first)
    writeFileSync(join(dir, 'big2.bin'), payloadTwo)
    writeFileSync(join(dir, 'serve.cjs'), serveScript(), 'utf8')

    const aborted = await runProgram(
      dir,
      `    const host = this;
    return await js.call(class extends js.Callable {
      maxDuration = 20000;
      interval = 20;
      next(ctx) {
        if (ctx.step === 0) return { spawn: "node serve.cjs . ${port}", name: "srv" };
        if (ctx.step === 1) return { waitFor: { port: ${port}, timeoutMs: 5000 } };
        if (ctx.step === 2) return "wget -c -q -O out1.bin http://127.0.0.1:${port}/big1.bin";
        return null;
      }
      async watch(ctx) {
        if (ctx.exited && ctx.exitCode !== 0) return { abort: "wget failed" };
        const s = await host.stat("out1.bin");
        return s.exists && s.size > 100000 ? { abort: "halfway" } : true;
      }
      sanity() {
        return {
          nextDone: () => [{ root: "/", step: 3 }],
          watchVeto: () => [{ root: "/", step: 0, stdout: "", stderr: "", exited: true, exitCode: 2, backgrounds: [], elapsedMs: 0, remainingMs: 60000 }],
        };
      }
    });`,
      'sess-052a',
    )
    assert.equal(caseName(aborted), 'Succeeded')
    assert.match(render(aborted), /status = "aborted"/)
    const partial = sizeOf(join(dir, 'out1.bin'))
    assert.ok(partial > 0 && partial < first.length, `partial download expected, got ${partial}`)

    const resumed = await runProgram(
      dir,
      `    const host = this;
    return await js.call(class extends js.Callable {
      maxDuration = 30000;
      interval = 20;
      next(ctx) {
        if (ctx.step === 0) return { spawn: "node serve.cjs . ${port}", name: "srv" };
        if (ctx.step === 1) return { waitFor: { port: ${port}, timeoutMs: 5000 } };
        if (ctx.step === 2) return "wget -c -q -O out1.bin http://127.0.0.1:${port}/big1.bin";
        if (ctx.step === 3) return "wget -c -q -O out2.bin http://127.0.0.1:${port}/big2.bin";
        if (ctx.step === 4) return "sleep 0.4";
        return null;
      }
      async watch(ctx) {
        if (ctx.exited && ctx.exitCode !== 0) return { abort: "wget failed" };
        const one = await host.stat("out1.bin");
        const two = await host.stat("out2.bin");
        if (one.exists && one.size === ${first.length} && !this.sawOne) {
          this.sawOne = true;
          return { park: { file: "out1.bin", size: one.size } };
        }
        if (two.exists && two.size === ${payloadTwo.length} && !this.sawTwo) {
          this.sawTwo = true;
          return { park: { file: "out2.bin", size: two.size } };
        }
        return true;
      }
      sanity() {
        return {
          nextDone: () => [{ root: "/", step: 5 }],
          watchVeto: () => [{ root: "/", step: 0, stdout: "", stderr: "", exited: true, exitCode: 2, backgrounds: [], elapsedMs: 0, remainingMs: 60000 }],
        };
      }
    });`,
      'sess-052b',
    )
    assert.equal(caseName(resumed), 'Parked')
    assert.deepEqual(parkedPayload(resumed), { file: 'out1.bin', size: first.length })

    const second = await execute(
      dir,
      'DevOps',
      'en',
      'sess-052b',
      { unpark: { handle: parkedHandle(resumed), value: 'continue' } },
      30_000,
      Date.now() + 180_000,
      1 << 20,
      null,
    )
    assert.equal(caseName(second), 'Parked')
    assert.deepEqual(parkedPayload(second), { file: 'out2.bin', size: payloadTwo.length })

    const finished = await execute(
      dir,
      'DevOps',
      'en',
      'sess-052b',
      { unpark: { handle: parkedHandle(second), value: 'continue' } },
      30_000,
      Date.now() + 180_000,
      1 << 20,
      null,
    )
    assert.equal(caseName(finished), 'Succeeded')
    assert.match(render(finished), /status = "completed"/)
    assert.equal(sizeOf(join(dir, 'out1.bin')), first.length)
    assert.equal(sizeOf(join(dir, 'out2.bin')), payloadTwo.length)
  } finally {
    cleanup()
  }
})

test('WHAT[repository-programming-052] wget exit 8 is dispatched to onExit instead of being guessed', async () => {
  const { dir, cleanup } = sandbox()
  try {
    const port = await freePort()
    writeFileSync(join(dir, 'big1.bin'), Buffer.alloc(1024, 1))
    writeFileSync(join(dir, 'serve.cjs'), serveScript(), 'utf8')

    const outcome = await runProgram(
      dir,
      `    const dispatched = [];
    const result = await js.call(class extends js.Callable {
      maxDuration = 15000;
      interval = 50;
      next(ctx) {
        if (ctx.step === 0) return { spawn: "node serve.cjs . ${port}", name: "srv" };
        if (ctx.step === 1) return { waitFor: { port: ${port}, timeoutMs: 5000 } };
        if (ctx.step === 2) return "wget -q -O out3.bin http://127.0.0.1:${port}/missing.bin";
        return null;
      }
      watch(ctx) {
        const srv = ctx.backgrounds.find((b) => b.name === "srv");
        if (srv && srv.exited) return { abort: "server died" };
        return true;
      }
      onExit(r) { dispatched.push(r.exitCode); }
      sanity() {
        return {
          nextDone: () => [{ root: "/", step: 3 }],
          watchVeto: () => [{ root: "/", step: 0, stdout: "", stderr: "", exited: false, exitCode: null, backgrounds: [{ name: "srv", exited: true, exitCode: 1 }], elapsedMs: 0, remainingMs: 60000 }],
        };
      }
    });
    return { result, dispatched };`,
      'sess-052c',
    )
    assert.equal(caseName(outcome), 'Succeeded')
    const doc = render(outcome)
    assert.match(doc, /status = "completed"/)
    assert.match(doc, /dispatched = \[8\]/)
    assert.ok(sizeOf(join(dir, 'out3.bin')) <= 0, 'a failed wget leaves no content behind')
  } finally {
    cleanup()
  }
})
