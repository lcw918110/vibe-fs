// tests/unit/support/run-inner.mjs — the inner tier: node:test itself, semantics unchanged.
//
// Spawned by the out-of-process supervisor (unit / integration / package). Its only added job is to
// report every event to the parent over IPC so the parent's watchdog can be fed by verdicts.
//
// ── what is deliberately NOT changed here ───────────────────────────────────
//
// One run per entry preserves ownership; bounded workers provide process parallelism:
//
//   explicit leaf timeout  a leaf that declares a timeout is failed and forgotten
//   file process           may contain many healthy leaves and has no leaf-sized total budget
//   process parallelism    keeps the full suite efficient
//
// Measured (Node v26.4.0): an explicit leaf `timeout` is a VERDICT line, not an abort line. A test that
// overruns is failed at the deadline and then keeps running to completion, and a test that never
// resolves while holding a live handle prevents the stream from ever emitting `end`. Neither is
// fixable from inside this process — that is the parent's job, and it is why there is a parent.
//
// The external supervisor owns verdict silence and the suite backstop. Putting either a leaf timeout
// or one shared AbortSignal here applies it to every process-isolated FILE wrapper under Node 20,
// which kills healthy multi-test files and fans hundreds of listeners out from one signal.
// It runs node:test files that load the compiled distribution under dist/.

import { lstatSync, mkdirSync, mkdtempSync, realpathSync, rmSync, writeFileSync } from 'node:fs'
import { dirname, isAbsolute, join, resolve } from 'node:path'
import { availableParallelism, tmpdir } from 'node:os'
import { PassThrough } from 'node:stream'
import { fileURLToPath, pathToFileURL } from 'node:url'

import { run } from 'node:test'
import { createCompactReporter } from './compact-reporter.mjs'
import { createRunState, applyEvent, summarize } from './test-run-state.mjs'
import { createWorkerCostObserver } from './worker-cost-observation.mjs'

import { parseConcurrency } from '../../../../scripts/lib/concurrency-cap.mjs'

/**
 * 契约：监听 'end'/'error'；
 * end → { drained: true, error: null }；
 * error → send({ type: 'runner:error', data: { name, message, stack } }) 后返回 { drained: false, error }；
 * end/error 竞态先到者胜，不可二次 resolve。
 *
 * @param {{
 *   stream: import('node:stream').Readable | import('node:events').EventEmitter,
 *   send?: (message: any) => void,
 * }} opts
 * @returns {Promise<{ drained: boolean, error: any }>}
 */
export function drainTestStream({ stream, send = (message) => process.send?.(message) }) {
  return new Promise((res) => {
    let settled = false
    const fail = (err) => {
      if (settled) return
      settled = true
      send?.({
        type: 'runner:error',
        data: { name: err?.name ?? 'StreamError', message: err?.message ?? String(err), stack: err?.stack },
      })
      res({ drained: false, error: err })
    }
    stream.on('end', () => {
      if (settled) return
      settled = true
      res({ drained: true, error: null })
    })
    stream.on('error', fail)
    stream.on('close', () => fail(new Error('test stream closed before completion')))
  })
}

export async function reportTestStream({ stream, reporter, send }) {
  const drained = drainTestStream({ stream, send })
  let reporterError = null
  try {
    for await (const chunk of reporter(stream)) process.stdout.write(chunk)
  } catch (error) {
    reporterError = error
    stream.destroy(error)
  }
  const outcome = await drained
  if (!reporterError || outcome.error === reporterError) return outcome
  send?.({ type: 'runner:error', data: { name: reporterError.name, message: reporterError.message } })
  return { drained: false, error: reporterError }
}

export function bindTestEntry(event, entryFile) {
  if (!event?.data || typeof event.data !== 'object' || Array.isArray(event.data)) {
    throw new TypeError('Test event data must be an object')
  }
  const planned = resolve(entryFile)
  const actual = event.data.entryFile
  if (actual !== undefined && (typeof actual !== 'string' || actual.length === 0)) {
    throw new TypeError('Test event entryFile must be a nonempty path when provided')
  }
  if (actual !== undefined && resolve(actual) !== planned && realpathSync(actual) !== realpathSync(planned)) {
    throw new Error(`Test event entry mismatch: expected ${planned}, received ${actual}`)
  }
  return { ...event, data: { ...event.data, entryFile: planned } }
}

export async function runTestFiles({
  files, concurrency = parseConcurrency(process.env.NODE_TEST_CONCURRENCY),
  send = (message, callback) => {
    // 当 supervisor 已终止（watchdog 静默 / backstop）时，process.connected 会先翻为 false，
    // 而 inner runner 尚未收到 SIGKILL。此时调用 process.send 会触发 ERR_IPC_CHANNEL_CLOSED，
    // 该错误经 process.nextTick 以 'error' 事件异步抛出，无监听器时直接崩掉 runner。
    // 先检查 process.connected，同时避免同步 throw 与异步 'error' 事件。
    if (typeof process.send !== 'function' || !process.connected) return
    try { process.send(message, callback) } catch { /* IPC 已关闭 —— supervisor 已终止 */ }
  }, stdout = process.stdout, stderr = process.stderr,
}) {
  const fileLimit = parseConcurrency(concurrency)
  const workerCount = Math.min(files.length,
    fileLimit === true ? Math.max(availableParallelism() - 1, 1) : fileLimit)
  const startedAt = performance.now()
  const runState = createRunState()
  const output = new PassThrough({ objectMode: true })
  const active = new Set()
  const errors = new Set()
  const reportError = (error) => {
    if (errors.has(error)) return
    errors.add(error)
    send({ type: 'runner:error', data: {
      name: error?.name ?? 'StreamError', message: error?.message ?? String(error), stack: error?.stack,
    } })
    console.error(`run-inner: stream error: ${error?.message ?? error}`)
    for (const controller of active) controller.abort(error)
    output.destroy(error)
  }
  const reporterFinished = reportTestStream({
    stream: output,
    reporter: createCompactReporter({ state: runState, stdout, stderr }),
    send: (message) => { if (message.type === 'runner:error') reportError(new Error(message.data.message)) },
  }).then((outcome) => { if (outcome.error) reportError(outcome.error) })
  const eventTypes = [
    'test:start', 'test:pass', 'test:fail', 'test:complete',
    'test:diagnostic', 'test:stderr', 'test:stdout',
  ]
  const transportUrl = new URL('./verdict-transport.mjs', import.meta.url).href
  const hasTransport = process.execArgv.some((argument, index) =>
    argument === `--import=${transportUrl}` ||
    (argument === '--import' && process.execArgv[index + 1] === transportUrl))
  const execArgv = hasTransport ? process.execArgv : [...process.execArgv, `--import=${transportUrl}`]
  const runFile = async (file) => {
    const controller = new AbortController()
    active.add(controller)
    const costs = createWorkerCostObserver({
      entryFile: resolve(file), parentPid: process.pid,
      enabled: process.env.WXS_VERIFICATION_WORKER_DIAGNOSTICS !== '0',
    })
    let reportedCosts = null
    const reportCosts = (snapshot = costs.snapshot()) => {
      const current = JSON.stringify(snapshot)
      if (current === reportedCosts) return
      reportedCosts = current
      try { send({ type: 'runner:worker-cost', data: snapshot }, () => {}) } catch {}
    }
    try {
      const stream = run({ files: [resolve(file)], concurrency: 1, signal: controller.signal, execArgv })
      send({ type: 'runner:file-start', data: { entryFile: resolve(file), pid: process.pid, parentPid: process.ppid } })
      reportCosts()
      const drained = drainTestStream({ stream, send() {} })
      const attributed = new WeakMap()
      const attribute = (event) => {
        if (!attributed.has(event.data)) attributed.set(event.data, bindTestEntry(event, file).data)
        return { ...event, data: attributed.get(event.data) }
      }
      for (const type of eventTypes) {
        stream.on(type, (data) => {
          if (errors.size > 0) return
          try {
            const event = attribute({ type, data })
            if (type === 'test:stdout') {
              costs.observe(event)
              reportCosts()
            }
            applyEvent(runState, event)
            send({ type, data: {
              name: data?.name, file: data?.file, entryFile: event.data.entryFile,
              testId: data?.testId, line: data?.line, column: data?.column,
              nesting: data?.nesting, testNumber: data?.testNumber, durationMs: data?.details?.duration_ms,
              pid: process.pid, parentPid: process.ppid,
            } })
          } catch (error) { reportError(error) }
        })
      }
      try {
        for await (const event of stream) {
          if (errors.size > 0 || !eventTypes.includes(event.type)) continue
          await new Promise((accept, reject) => {
            output.write(attribute(event), (error) => error ? reject(error) : accept())
          })
        }
      } catch (error) { reportError(error) }
      const completion = await drained
      reportCosts(costs.finish())
      if (!completion.drained) reportError(completion.error)
      else send({ type: 'runner:file-drained', data: { entryFile: resolve(file) } })
    } finally { active.delete(controller) }
  }
  const pending = files[Symbol.iterator]()
  await Promise.all(Array.from({ length: workerCount }, async () => {
    for (let next = pending.next(); !next.done && errors.size === 0; next = pending.next()) {
      try { await runFile(next.value) } catch (error) { reportError(error) }
    }
  }))
  if (errors.size === 0) {
    output.end({ type: 'test:summary', data: { duration_ms: performance.now() - startedAt } })
  }
  await reporterFinished
  if (errors.size > 0) return false
  send({ type: 'runner:summary', data: summarize(runState) })
  send({ type: 'inner:drained' })
  return true
}

async function main() {
  // Fatal semantics stay physical in production. The verification child opts out
  // explicitly so tests can inspect the fatal classification and durable aftermath
  // without killing the whole test tier. Production code never infers this from
  // NODE_TEST_CONTEXT or any other Host-owned environment variable.
  const originalHome = process.env.HOME || process.env.USERPROFILE
  process.env.WANXIANGSHU_NO_FATAL_EXIT = '1'
  const arguments_ = process.argv.slice(2)
  const homeIndex = arguments_.indexOf('--owned-test-home')
  let suppliedHome = null
  if (homeIndex !== -1) {
    suppliedHome = arguments_[1]
    if (homeIndex !== 0 || arguments_.lastIndexOf('--owned-test-home') !== 0 || typeof suppliedHome !== 'string' || !isAbsolute(suppliedHome) || !lstatSync(suppliedHome).isDirectory() || realpathSync(suppliedHome) !== suppliedHome) {
      throw new Error('run-inner: owned test HOME must be an explicit canonical directory')
    }
  }
  const files = (suppliedHome === null ? arguments_ : arguments_.slice(2))
    .filter(argument => argument.endsWith('.mjs'))

  // Isolate HOME / USERPROFILE for the node:test inner runner so any test or
  // pre-import that touches ~/.config/opencode defaults to a throwaway temporary
  // directory rather than the developer's real user configuration directory.
  // Keep the .NET CLI tool store independent: compiler canaries must still resolve
  // the repository-pinned local Fable tool after application HOME is isolated.
  process.env.DOTNET_CLI_HOME ??= process.env.HOME
  const runnerTestHome = suppliedHome ?? realpathSync(mkdtempSync(join(tmpdir(), 'wxs-runner-home-')))
  try {
    const runnerRoutingDir = join(runnerTestHome, '.config', 'opencode')
    mkdirSync(runnerRoutingDir, { recursive: true })
    writeFileSync(
      join(runnerRoutingDir, 'wanxiangshu.mjs'),
      `export const routingProtocol = 2
export default function route(role, running, previous, purpose) {
  if (!new Set(['manager', 'orchestrator', 'engineer', 'coder', 'inspector', 'browser', 'inquiry', 'reviewer', 'devops', 'distiller', 'bookkeeper', 'plan', 'advisor']).has(role)) throw new Error('unexpected managed role: ' + role)
  return { model: 'provider/' + role + '-model', reasoning: 'none' }
}
\n`,
      'utf8',
    )
    process.env.HOME = runnerTestHome
    process.env.USERPROFILE = runnerTestHome
    if (originalHome) process.env.DOTNET_CLI_HOME = originalHome
    if (files.length === 0) {
      console.error('run-inner: no test files given')
      process.exitCode = 2
      return
    }
    if (!await runTestFiles({ files })) process.exitCode = 1
  } finally {
    if (suppliedHome === null) rmSync(runnerTestHome, { recursive: true, force: true })
  }
}

if (typeof process.argv[1] === 'string' && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  await main()
}
