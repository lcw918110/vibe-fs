import assert from 'node:assert/strict'
import { spawn } from 'node:child_process'
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join, resolve } from 'node:path'
import { PassThrough } from 'node:stream'
import { fileURLToPath } from 'node:url'
import test from 'node:test'
import { createCompactReporter } from './support/compact-reporter.mjs'
import { createRunState, isFileCompletionEvent } from './support/test-run-state.mjs'
import { drainTestStream } from './support/run-inner.mjs'
import { NODE_TEST_INNER, superviseNodeTest } from './e2e/support/supervise-node-test.mjs'

const fixture = fileURLToPath(new URL('./support/fixtures/two-leaf.fixture.mjs', import.meta.url))
const childEnv = { ...process.env }
delete childEnv.NODE_TEST_CONTEXT
delete childEnv.WXS_ACCEPT_TODO
const verdict = (name, type = 'test:pass', extra = {}) => ({
  type,
  data: { name, file: '/test/a.mjs', details: { duration_ms: 1 }, ...extra },
})
const counts = (s) => ({
  passed: s.passed, failed: s.failed, skipped: s.skipped, todo: s.todo, cancelled: s.cancelled,
})

test('WHAT[verification-system-021] compact and verbose reports preserve outcomes and visible exclusions', async () => {
  const events = [
    verdict('pass'), verdict('fail', 'test:fail'),
    verdict('skip', 'test:pass', { skip: 'requires release environment' }),
    verdict('todo', 'test:pass', { todo: 'known proof gap' }),
    { type: 'test:summary', data: { duration_ms: 4 } },
  ]
  const reports = []
  for (const verbose of [false, true]) {
    let output = ''
    const sink = { write(text) { output += text } }
    const reporter = createCompactReporter({
      verbose, stdout: sink, stderr: sink, onSummary(summary) { reports.push(summary) },
    })
    for await (const _ of reporter(events)) {}
    assert.match(output, /1 passed, 1 failed, 1 skipped, 1 todo/)
    assert.match(output, /failing tests/)
    assert.match(output, /requires release environment/)
    assert.match(output, /known proof gap/)
  }
  assert.deepEqual(reports[0], reports[1])
  assert.deepEqual(counts(reports[0]), { passed: 1, failed: 1, skipped: 1, todo: 1, cancelled: 0 })
})

test('WHAT[verification-system-021] cancelled and TODO failures are not ordinary assertion verdicts', () => {
  const state = createRunState()
  state.applyEvent(verdict('cancelled', 'test:fail', {
    details: { error: { failureType: 'testAborted', message: 'parent cancelled' } },
  }))
  state.applyEvent(verdict('known gap', 'test:fail', { todo: 'missing continuous input guard' }))
  assert.deepEqual(counts(state.summarize()), { passed: 0, failed: 0, skipped: 0, todo: 1, cancelled: 1 })
})

test('WHAT[verification-system-021] conflicting final results cannot erase a previous failure', () => {
  const state = createRunState()
  const failure = verdict('same execution', 'test:fail')
  state.applyEvent(failure)
  state.applyEvent(failure)
  assert.equal(state.summarize().failed, 1)
  assert.throws(() => state.applyEvent(verdict('same execution')), /conflicting.*verdict/i)
  assert.deepEqual(counts(state.summarize()), { passed: 0, failed: 1, skipped: 0, todo: 0, cancelled: 0 })
})

test('WHAT[verification-system-021] equal names in different files or distinct subtests do not collapse', () => {
  const state = createRunState()
  state.applyEvent(verdict('same', 'test:pass', { testId: 1 }))
  state.applyEvent(verdict('same', 'test:pass', { testId: 2 }))
  state.applyEvent(verdict('same', 'test:fail', { file: '/test/b.mjs', testId: 1 }))
  const summary = state.summarize()
  assert.deepEqual(counts(summary), { passed: 2, failed: 1, skipped: 0, todo: 0, cancelled: 0 })
  assert.equal(summary.files, 2)
  assert.equal(summary.leafDurations.length, 3)
  assert.deepEqual(summary.failures.map(({ name, file }) => ({ name, file })), [{ name: 'same', file: '/test/b.mjs' }])
})

test('WHAT[verification-system-021] a failing suite preserves its container failure without double counting leaves', () => {
  const state = createRunState()
  state.applyEvent(verdict('pass'))
  state.applyEvent(verdict('fail', 'test:fail'))
  state.applyEvent(verdict('suite', 'test:fail', { details: { type: 'suite' } }))
  const summary = state.summarize()
  assert.deepEqual(counts(summary), { passed: 1, failed: 1, skipped: 0, todo: 0, cancelled: 0 })
  assert.equal(summary.containerFailures, 1)
  assert.deepEqual(summary.failures.map(({ name }) => name), ['fail'])
})

test('WHAT[verification-system-021] shared registration sources retain each entry ownership and repeated container verdicts count once', () => {
  const state = createRunState()
  state.applyEvent(verdict('shared helper', 'test:pass', { entryFile: '/test/first.mjs', testId: 1 }))
  state.applyEvent(verdict('shared helper', 'test:fail', { entryFile: '/test/second.mjs', testId: 1 }))
  const container = verdict('suite', 'test:fail', {
    entryFile: '/test/second.mjs', testId: 2, details: { type: 'suite', error: { message: 'child failed' } },
  })
  state.applyEvent(container)
  state.applyEvent(container)
  const summary = state.summarize()
  assert.deepEqual(counts(summary), { passed: 1, failed: 1, skipped: 0, todo: 0, cancelled: 0 })
  assert.equal(summary.files, 2)
  assert.equal(summary.containerFailures, 1)
  assert.equal(summary.failures[0].file, '/test/second.mjs')
  assert.equal(summary.failures[0].sourceFile, '/test/a.mjs')
  assert.equal(summary.containerFailureDetails[0].error.message, 'child failed')
})

test('WHAT[verification-system-021] the real concurrent runner keeps shared helper tests under their own entry files', async () => {
  const directory = mkdtempSync(join(tmpdir(), 'entry-ownership-'))
  try {
    writeFileSync(join(directory, 'helper.mjs'), "import test from 'node:test'\ntest('shared helper', () => {})\n")
    const entries = [join(directory, 'first.fixture.mjs'), join(directory, 'second.fixture.mjs')]
    for (const entry of entries) writeFileSync(entry, "import './helper.mjs'\n")
    const child = spawn(process.execPath, [NODE_TEST_INNER, ...entries], {
      stdio: ['ignore', 'pipe', 'pipe', 'ipc'], env: { ...childEnv, NODE_TEST_CONCURRENCY: '2' },
    })
    const messages = []
    let diagnostics = ''
    child.stdout.resume()
    child.stderr.on('data', (chunk) => { diagnostics += chunk })
    child.on('message', (message) => messages.push(message))
    const code = await new Promise((resolveExit, reject) => {
      child.on('error', reject)
      child.on('close', resolveExit)
    })
    assert.equal(code, 0, diagnostics)
    const summary = messages.find(({ type }) => type === 'runner:summary').data
    assert.equal(summary.passed, 2)
    assert.equal(summary.files, 2)
    assert.equal(summary.filesCompleted, 2)
    assert.equal(summary.containerFailures, 0)
    assert.deepEqual(summary.byFile.map(({ file, passed }) => ({ file, passed })).sort((a, b) => a.file.localeCompare(b.file)),
      entries.map((file) => ({ file, passed: 1 })))
  } finally {
    rmSync(directory, { recursive: true, force: true })
  }
})

test('WHAT[verification-system-021] a leaf completion cannot stand for completion of its file', () => {
  const complete = (name, file = fixture) => ({ type: 'test:complete', data: { name, file } })
  assert.equal(isFileCompletionEvent(complete('leaf')), false)
  assert.equal(isFileCompletionEvent(complete('nested leaf')), false)
  assert.equal(isFileCompletionEvent(complete(fixture)), true)
  assert.equal(isFileCompletionEvent(complete('./a.mjs', resolve('a.mjs'))), true)
  assert.equal(isFileCompletionEvent(complete('a.mjs', '/elsewhere/a.mjs')), false)
})

test('WHAT[verification-system-021] stream error and clean end have distinct completion evidence', async () => {
  const broken = new PassThrough({ objectMode: true })
  const messages = []
  const error = new Error('truncated result stream')
  const waiting = drainTestStream({ stream: broken, send(message) { messages.push(message) } })
  broken.destroy(error)
  assert.deepEqual(await waiting, { drained: false, error })
  assert.deepEqual(messages.map(({ type }) => type), ['runner:error'])
  assert.equal(messages[0].data.message, error.message)

  const clean = new PassThrough({ objectMode: true })
  clean.resume()
  const ended = drainTestStream({ stream: clean, send() { assert.fail('clean end must not emit an error') } })
  clean.end()
  assert.deepEqual(await ended, { drained: true, error: null })
})

test('WHAT[verification-system-021] a real inner runner completes all leaves before file completion and summary', async () => {
  const child = spawn(process.execPath, [NODE_TEST_INNER, fixture], {
    stdio: ['ignore', 'pipe', 'pipe', 'ipc'],
    env: childEnv,
  })
  const messages = []
  let stderr = ''
  child.stdout.resume()
  child.stderr.on('data', (text) => { stderr += text })
  child.on('message', (message) => messages.push(message))
  const exit = await new Promise((resolveExit, reject) => {
    child.on('error', reject)
    child.on('close', (code, signal) => resolveExit({ code, signal }))
  })
  assert.deepEqual(exit, { code: 0, signal: null }, stderr)
  const completions = messages.filter(({ type }) => type === 'test:complete')
  assert.equal(completions.length, 3)
  assert.deepEqual(completions.map(isFileCompletionEvent), [false, false, true])
  const summaryIndex = messages.findIndex(({ type }) => type === 'runner:summary')
  const drainedIndex = messages.findIndex(({ type }) => type === 'inner:drained')
  assert.ok(summaryIndex > messages.indexOf(completions.at(-1)))
  assert.ok(drainedIndex > summaryIndex)
  assert.deepEqual(counts(messages[summaryIndex].data), { passed: 2, failed: 0, skipped: 0, todo: 0, cancelled: 0 })
  assert.equal(messages[summaryIndex].data.filesCompleted, 1)
})

test('WHAT[verification-system-021] summary and clean exit cannot disguise missing file completion', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'incomplete-test-run-'))
  try {
    const inner = join(dir, 'inner.mjs')
    writeFileSync(inner, `process.send({type:'runner:summary',data:{passed:1,failed:0,leafDurations:[]}})
process.send({type:'inner:drained'})
`)
    await assert.rejects(superviseNodeTest({
      files: [fixture], inner, env: childEnv, label: 'incomplete-fixture', silenceMs: 10000, throwOnFailure: true,
    }), /supervised suite failed/)
  } finally {
    rmSync(dir, { recursive: true, force: true })
  }
})

test('WHAT[verification-system-021] a clean child exit without authoritative summary is inconclusive', async () => {
  const messages = []
  const originalError = console.error
  console.error = (...args) => {
    messages.push(args.join(' '))
    originalError(...args)
  }
  try {
    await assert.rejects(superviseNodeTest({
      files: [fixture], inner: fixture, env: childEnv, label: 'no-summary-fixture', silenceMs: 10000, throwOnFailure: true,
    }), /supervised suite failed/)
  } finally {
    console.error = originalError
  }
  assert.match(messages.join('\n'), /verdict counts unavailable; no authoritative summary/)
  assert.doesNotMatch(messages.join('\n'), /\b0 passed, 0 failed\b/)
})

test('WHAT[verification-system-021] a tier exclusion identifies the missing execution tier', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'tier-report-'))
  try {
    const file = join(dir, 'tier.fixture.mjs')
    const gate = new URL('./support/tier-gate.mjs', import.meta.url).href
    writeFileSync(file, `import { releaseTest } from ${JSON.stringify(gate)};
releaseTest('physical acceptance', () => { throw new Error('must remain unexecuted') });
`)
    const child = spawn(process.execPath, [NODE_TEST_INNER, file], {
      stdio: ['ignore', 'pipe', 'pipe', 'ipc'], env: { ...childEnv, WXS_TIER_RELEASE: '0' },
    })
    const messages = []
    child.stdout.resume()
    child.stderr.resume()
    child.on('message', (message) => messages.push(message))
    const code = await new Promise((resolveExit, reject) => {
      child.on('error', reject)
      child.on('close', resolveExit)
    })
    assert.equal(code, 0)
    const summary = messages.find(({ type }) => type === 'runner:summary').data
    assert.deepEqual(counts(summary), { passed: 0, failed: 0, skipped: 1, todo: 0, cancelled: 0 })
    assert.match(summary.exclusions[0].reason, /release.*not enabled/)
  } finally {
    rmSync(dir, { recursive: true, force: true })
  }
})

test('WHAT[verification-system-021] a known TODO cannot authorize complete acceptance', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'pending-proof-'))
  try {
    const inner = join(dir, 'inner.mjs')
    writeFileSync(inner, `const file = process.argv[2];
process.send({type:'test:complete',data:{name:file,file}});
process.send({type:'runner:summary',data:{passed:1,failed:0,todo:1,leafDurations:[]}});
process.send({type:'inner:drained'});
`)
    await assert.rejects(superviseNodeTest({
      files: [fixture], inner, env: childEnv, label: 'pending-proof', silenceMs: 10000, throwOnFailure: true,
    }), /supervised suite failed/)
  } finally {
    rmSync(dir, { recursive: true, force: true })
  }
})

test('WHAT[verification-system-021] missing or unloadable planned files fail the real supervisor despite completed file wrappers', async () => {
  const directory = mkdtempSync(join(tmpdir(), 'failed-test-file-'))
  try {
    const missing = join(directory, 'missing.fixture.mjs')
    const broken = join(directory, 'broken.fixture.mjs')
    writeFileSync(broken, 'throw new Error("fixture failed before registering any test")\n')
    for (const file of [missing, broken]) {
      await assert.rejects(superviseNodeTest({
        files: [fixture, file], env: childEnv, label: 'failed-file', silenceMs: 10000, throwOnFailure: true,
      }), /supervised suite failed/)
    }
  } finally {
    rmSync(directory, { recursive: true, force: true })
  }
})
