import test from 'node:test'

{
const { default: assert } = await import('node:assert/strict')
const { existsSync, mkdtempSync, readFileSync, rmSync } = await import('node:fs')
const { tmpdir } = await import('node:os')
const { join } = await import('node:path')
const { fileURLToPath } = await import('node:url')
const { randomUUID } = await import('node:crypto')
const { runVerificationToolProbe } = await import('../../../scripts/lib/verification-tool-probe.mjs')
const child = fileURLToPath(new URL('./support/transaction-cut-child.mjs', import.meta.url))

for (const phase of ['Prepared', 'Committed']) {
  for (const scenario of ['valid', 'malformed', 'malformed-release']) {
    test(`WHAT[repository-programming-013] JS ${phase} actual EventStore port ${scenario} bad-input defense settles bytes before semantic-cut fatal`, async t => {
      const root = mkdtempSync(join(tmpdir(), 'js-cut-boundary-'))
      const commonDir = join(root, '.git')
      const writer = randomUUID()
      const env = { ...process.env }
      delete env.NODE_TEST_CONTEXT
      delete env.WANXIANGSHU_NO_FATAL_EXIT
      const probe = (mode, writerId, request) => runVerificationToolProbe(process.execPath,
        [child, mode, commonDir, writerId, phase, scenario, JSON.stringify(request)], { cwd: root, env, signal: t.signal })
      try {
        let outcome
        try { outcome = { stdout: await probe('measure', writer, {}), stderr: '', exitCode: 0, signal: null } }
        catch (error) {
          if (error?.code !== 'verification-tool-probe-failed') throw error
          outcome = error
        }
        const records = outcome.stdout.trim().split('\n').map(line => JSON.parse(line))
        const settled = records.find(record => record.stage === 'settled')
        assert.equal(settled?.physicalPreconditions, true, outcome.stderr)
        assert.deepEqual(settled.counts, { append: 1, fsync: 1, close: 1, release: 1, injected: scenario === 'malformed-release' ? 1 : 0 })
        if (scenario === 'malformed-release') assert.equal(settled.causeSame, true)
        const cold = JSON.parse(await probe('cold', randomUUID(), {
          sourceWriter: writer, bytes: settled.bytes, facts: settled.facts,
        }))
        assert.notEqual(cold.pid, settled.pid)
        assert.equal(cold.preserved, true, 'a separate process accepts the exact JS fact and actual cut tail')
        const eventsDir = join(commonDir, 'wanxiangshu', 'events')
        const actualFile = existsSync(join(eventsDir, `${writer}.ndjson`))
          ? join(eventsDir, `${writer}.ndjson`)
          : join(eventsDir, `${writer}-0000.ndjson`)
        assert.equal(readFileSync(actualFile, 'base64'), settled.bytes)
        t.diagnostic(JSON.stringify({ phase, scenario, physicalPreconditions: true, measurePid: settled.pid, coldPid: cold.pid }))
        if (scenario === 'valid') {
          assert.equal(outcome.exitCode, 0)
          assert.equal(outcome.signal, null)
          assert.deepEqual(records.at(-1), { stage: 'returned', ok: true })
          return
        }
        assert.equal(outcome.signal, 'SIGKILL', 'actual JS rule cut must terminate through the physical fatal owner after settlement')
        assert.equal(records.some(record => record.stage === 'returned'), false)
        const fatal = outcome.stderr.trim().split('\n').map(line => {
          try { return JSON.parse(line) } catch { return null }
        }).filter(record => record?.operation === 'js-transaction-semantic-cut')
        assert.equal(fatal.length, 1, 'one JS owner reports the actual rule cut')
        if (scenario === 'malformed-release') {
          assert.match(fatal[0].result, /JS bad-input actual owned Release completed/)
          assert.equal(fatal[0].result.includes(settled.facts[0].id), true, 'the terminal report retains the original JS request event identity')
        }
      } finally { rmSync(root, { recursive: true, force: true }) }
    })
  }
}
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join } = await import("node:path");
const { readUtf8, glob, findAnchor, requireUnique, grep, commitPlan, rollbackPlan } = await import("../../../dist/Repository/Programming/Js/FilesystemSurface.js");

const exact = (text) => ({ kind: 'exact', text })
const regex = (text) => ({ kind: 'regex', text })
const sandbox = () => {
  const dir = mkdtempSync(join(tmpdir(), 'wxs-jstools-'))
  return { dir, cleanup: () => rmSync(dir, { recursive: true, force: true }) }
}
const ok = (result) => result.ok
const codeOf = (result) => result.code
const unwrap = (result) => {
  assert.equal(result.ok, true, `expected Ok, got ${JSON.stringify(result.error)}`)
  return result.value
}

test('WHAT[repository-programming-013] JS013_commitPlan_all_or_nothing', () => {
  const { dir, cleanup } = sandbox()
  try {
    writeFileSync(join(dir, 'a.txt'), 'oldA', 'utf8')
    // commit two files
    const plan = [
      { kind: 'rewrite', path: 'a.txt', expectedCurrent: 'oldA', newText: 'newA' },
      { kind: 'create', path: 'b.txt', newText: 'newB' },
    ]
    assert.equal(ok(commitPlan(dir, plan)), true)
    assert.equal(readFileSync(join(dir, 'a.txt'), 'utf8'), 'newA')
    assert.equal(readFileSync(join(dir, 'b.txt'), 'utf8'), 'newB')
  } finally {
    cleanup()
  }
})
test('WHAT[repository-programming-013] JS013_commitPlan_aborts_before_write_when_snapshot_fails', () => {
  const { dir, cleanup } = sandbox()
  try {
    writeFileSync(join(dir, 'a.txt'), 'oldA', 'utf8')
    // a directory target cannot be snapshotted → Phase 1 aborts BEFORE any write
    mkdirSync(join(dir, 'blocked'))
    const plan = [
      { kind: 'rewrite', path: 'a.txt', expectedCurrent: 'oldA', newText: 'newA' },
      { kind: 'rewrite', path: 'blocked', expectedCurrent: 'old', newText: 'nope' },
    ]
    assert.equal(codeOf(commitPlan(dir, plan)), 'FILE_CHANGED')
    assert.equal(readFileSync(join(dir, 'a.txt'), 'utf8'), 'oldA', 'no write happened at all')
  } finally {
    cleanup()
  }
})
test('WHAT[repository-programming-013] JS013_commitPlan_rolls_back_written_files_on_write_failure', () => {
  const { dir, cleanup } = sandbox()
  try {
    writeFileSync(join(dir, 'a.txt'), 'oldA', 'utf8')
    // second target's parent directory does not exist → write fails → the
    // already-written first file must be rolled back (all-or-nothing)
    const plan = [
      { kind: 'rewrite', path: 'a.txt', expectedCurrent: 'oldA', newText: 'newA' },
      { kind: 'create', path: 'x/y.txt', newText: 'nope' },
    ]
    assert.equal(codeOf(commitPlan(dir, plan)), 'TRANSACTION_COMMIT_FAILED')
    assert.equal(readFileSync(join(dir, 'a.txt'), 'utf8'), 'oldA', 'first write rolled back')
  } finally {
    cleanup()
  }
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { validateSingleIntent, validateTargets, validateFreshness, preflight, commitPlan, rollbackPlan } = await import("../../../dist/Repository/Programming/Js/TransactionSurface.js");

const ok = (result) => result.ok
const codeOf = (result) => result.code
const rewrite = (path, originalText, newText) => ({ kind: 'rewrite', path, originalText, newText })
const create = (path, text) => ({ kind: 'create', path, text })
const current = { 'a.txt': 'current' }

test('WHAT[repository-programming-013] JS013_preflight_orders_rules_and_short_circuits', () => {
  // duplicate intent wins over everything
  assert.equal(
    codeOf(preflight(['a.txt'], current, [], [rewrite('a.txt', 'current', 'x'), create('a.txt', 'y')])),
    'DUPLICATE_MUTATION_TARGET',
  )
  // missing target beats freshness
  assert.equal(codeOf(preflight(['a.txt'], current, [], [rewrite('missing.txt', 'anything', 'x')])), 'FILE_CHANGED')
  // all good
  assert.equal(ok(preflight(['a.txt'], current, [], [rewrite('a.txt', 'current', 'x'), create('b.txt', 'y')])), true)
})
test('WHAT[repository-programming-013] JS013_commit_plan_is_exact', () => {
  const mutations = [create('b.txt', 'newB'), rewrite('a.txt', 'oldA', 'newA')]
  assert.deepEqual(commitPlan(mutations), [
    { kind: 'rewrite', path: 'a.txt', expectedCurrent: 'oldA', newText: 'newA' },
    { kind: 'create', path: 'b.txt', newText: 'newB' },
  ])
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { mkdtempSync, mkdirSync, readFileSync, rmSync, writeFileSync, existsSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join } = await import("node:path");
const { randomUUID } = await import("node:crypto");
const { parse: parseToml } = await import("smol-toml");
const { generate } = await import("../../../dist/Repository/Programming/Js/GeneratorSurface.js");
const { run, runObserved, caseName, rewritten, created, failureCode, persistenceFailure, render } = await import("../../../dist/Repository/Programming/Js/WorkflowSurface.js");
const { create: createEventStore, dispose: disposeEventStore, createAppendFailureStore } = await import("../../../dist/Persistence/EventStore/Surface.js");
const { pending } = await import("../../../dist/Repository/Programming/Js/TransactionSurface.js");

const sandbox = () => {
  const dir = mkdtempSync(join(tmpdir(), 'wxs-workflow-'))
  return { dir, cleanup: () => rmSync(dir, { recursive: true, force: true }) }
}
const localStore = () => {
  const owned = mkdtempSync(join(tmpdir(), 'wxs-workflow-events-'))
  const commonDir = join(owned, '.git')
  mkdirSync(commonDir, { recursive: true })
  const handle = createEventStore(commonDir, randomUUID().replaceAll('-', ''))
  return { handle, close: () => { disposeEventStore(handle); rmSync(owned, { recursive: true, force: true }) } }
}
const coderSurface = () => generate('Coder', ['Read', 'Write', 'Edit', 'Glob', 'Grep'], 'en')
const runWorkflow = async (dir, program, { deadlineMs = 2000, store = null } = {}) => ({
  outcome: await run(dir, 'Coder', 'en', program, deadlineMs, Date.now() + 60_000, 1 << 20, store),
  surface: coderSurface(),
})

for (const fate of [
  { code: 'AppendNotAttempted', suffix: 'NOT_ATTEMPTED', kind: 'notAttempted', phase: 'BeforePhysicalAppend' },
  { code: 'CommitUnknown', suffix: 'UNKNOWN', kind: 'unknown', phase: 'DurabilityBarrier' },
  { code: 'NoNewWriteReleaseFailed', suffix: 'NO_NEW_WRITE_RELEASE_FAILED', kind: 'noNewWriteReleaseFailed', phase: 'StoreRelease' },
]) {
  for (const phase of ['Prepared', 'Committed']) {
    test(`WHAT[repository-programming-013] typed adapter mapping ${phase} ${fate.code} refuses success and preserves append identity and causes`, async () => {
      const { dir, cleanup } = sandbox()
      const local = localStore()
      try {
        writeFileSync(join(dir, 'a.txt'), 'old', 'utf8')
        const cause = new Error('original transaction append cause')
        const cleanupCause = new Error('original transaction cleanup cause')
        const observed = []
        const store = createAppendFailureStore(local.handle, {
          code: fate.code, phase: fate.phase, cause, failAt: phase === 'Prepared' ? 1 : 2,
          cleanupFailures: [{ phase: 'StoreRelease', cause: cleanupCause }],
        }, (append) => observed.push(append))
        const { outcome } = await runWorkflow(dir, `class Js extends JsProgram {
  async run() {
    this.rewrite('a.txt', 'new');
    await this.write('created.txt', 'created');
    return { receipt: 'must-not-report-success' };
  }
}`, { store })
        assert.equal(caseName(outcome), 'Failed')
        const expectedCode = `TRANSACTION_${phase === 'Prepared' ? 'PREPARE' : 'COMMIT'}_${fate.suffix}`
        assert.equal(failureCode(outcome), expectedCode)
        assert.deepEqual(rewritten(outcome), [])
        assert.deepEqual(created(outcome), [])
        assert.equal(observed.length, phase === 'Prepared' ? 1 : 2, 'no append retry or new transaction identity')
        const failedAppend = observed.at(-1)
        const failure = persistenceFailure(outcome)
        assert.equal(failure.phase, phase)
        assert.equal(failure.kind, fate.kind)
        assert.equal(failure.eventId, failedAppend.requested[0].id)
        assert.equal(failure.transactionId, failedAppend.requested[0].payload.transactionId)
        assert.deepEqual(failure.requestedEventIds, [failure.eventId])
        assert.equal(failure.isOriginalError(failedAppend.originalError), true)
        assert.strictEqual(failure.primary.cause, cause)
        if (fate.code !== 'NoNewWriteReleaseFailed') {
          assert.equal(failure.primary.phase, fate.phase)
          assert.strictEqual(failure.cleanupFailures[0].cause, cleanupCause)
        }
        if (phase === 'Prepared') {
          assert.equal(readFileSync(join(dir, 'a.txt'), 'utf8'), 'old', 'Prepared failure precedes all file effects')
          assert.equal(existsSync(join(dir, 'created.txt')), false)
          assert.deepEqual(pending(local.handle), [])
        } else {
          assert.equal(readFileSync(join(dir, 'a.txt'), 'utf8'), 'new', 'Committed failure does not claim rollback of real effects')
          assert.equal(readFileSync(join(dir, 'created.txt'), 'utf8'), 'created')
          const waiting = pending(local.handle)
          assert.equal(waiting.length, 1, 'the actual Prepared append remains pending')
          assert.equal(waiting[0].transactionId, failure.transactionId)
          assert.equal(observed[0].requested[0].payload.transactionId, failure.transactionId)
        }
        const rendered = render(outcome)
        assert.equal(rendered.startsWith('# failed\n'), true)
        const doc = parseToml(rendered)
        assert.equal(doc.code, expectedCode)
        assert.equal(doc.data, undefined)
        assert.equal(doc.fs, undefined)
      } finally { local.close(); cleanup() }
    })
  }
}

test('WHAT[repository-programming-013] JS085_workflow_reads_and_commits_rewrite', async () => {
  const { dir, cleanup } = sandbox()
  try {
    writeFileSync(join(dir, 'a.txt'), 'hello world', 'utf8')
    const program = `class Js extends JsProgram {
  async run() {
    const view = await this.file('a.txt', [['begin', 'end', 'hello']]);
    this.rewrite('a.txt', view.text('^', 'begin') + 'goodbye' + view.text('end', '$'));
    return { before: view.text() };
  }
}`
    const { outcome } = await runWorkflow(dir, program)
    assert.equal(caseName(outcome), 'Succeeded')
    assert.deepEqual(rewritten(outcome), ['a.txt'])
    assert.deepEqual(created(outcome), [])
    assert.equal(readFileSync(join(dir, 'a.txt'), 'utf8'), 'goodbye world', 'committed to disk')
  } finally {
    cleanup()
  }
})
test('WHAT[repository-programming-013] JS085_workflow_commits_create_and_reports', async () => {
  const { dir, cleanup } = sandbox()
  try {
    const program = `class Js extends JsProgram {
  async run() {
    await this.write('new.txt', 'fresh');
    return { ok: true };
  }
}`
    const { outcome } = await runWorkflow(dir, program)
    assert.equal(caseName(outcome), 'Succeeded')
    assert.deepEqual(rewritten(outcome), [])
    assert.deepEqual(created(outcome), ['new.txt'])
    assert.equal(readFileSync(join(dir, 'new.txt'), 'utf8'), 'fresh')
  } finally {
    cleanup()
  }
})
}
