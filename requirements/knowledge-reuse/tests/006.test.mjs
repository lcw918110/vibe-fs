import * as bookkeeperTool from '../../../dist/Repository/Knowledge/Casebook/OpenCode/BookkeeperToolSurface.js' // CUT-40: tool-binding entries split from BookkeeperSurface
import test from 'node:test'

let programSequence = 0
const bookkeeperRequest = program => ({
  name: 'bookkeeper-006-' + (++programSequence),
  program,
  intent: 'Complete the bound Bookkeeper transaction and retain an explicit result if execution cannot finish.',
})

import assert from 'node:assert/strict'
import * as casebookState from '../../../dist/Repository/Knowledge/Casebook/Surface.js'

const archiveWithBaseline = async (store, directory, record) => {
  const relatedPaths = record.observations.filter(observation => observation.kind === 'file-read').map(observation => observation.path)
  const baseline = await casebookState.freezeCompletionState(store, directory, relatedPaths)
  assert.equal(typeof baseline, 'string')
  return casebookState.archive(store, { ...record, relatedPaths, completionFileState: baseline, maintenanceFileState: baseline })
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { mkdtempSync, rmSync, writeFileSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join } = await import("node:path");
const eventStore = await import("../../../dist/Persistence/EventStore/Surface.js");
const casebook = await import("../../../dist/Repository/Knowledge/Casebook/Surface.js");
const bookkeeper = await import("../../../dist/Repository/Knowledge/Casebook/BookkeeperSurface.js");
const bookkeeperRefresh = await import("../../../dist/Repository/Knowledge/Casebook/BookkeeperRefreshSurface.js");
const { CANONICAL_A, CANONICAL_Q, installBookkeeperRuntime, scriptedBookkeeperPort } = await import("./support/bookkeeper-session-support.mjs");

const fileRead = (path, contentHash) => ({ kind: 'file-read', path, contentHash })
const sandbox = () => {
  const dir = mkdtempSync(join(tmpdir(), 'wxs-bk-'))
  const handle = eventStore.create(dir, 'bookkeeper-mechanical')
  return {
    dir,
    handle,
    cleanup: () => {
      eventStore.dispose(handle)
      rmSync(dir, { recursive: true, force: true })
    },
  }
}
const record = (sessionId, q, a, observations) => ({ sessionId, q, a, observations, lastAccessOrder: 0 })

test('WHAT[knowledge-reuse-006] CASE006_synthesis_refresh_publishes_refreshed_with_revised_a', async () => {
  const { dir, handle, cleanup } = sandbox()
  const { port, createCalls, programCalls } = scriptedBookkeeperPort()
  try {
    writeFileSync(join(dir, 'a.txt'), 'hello', 'utf8')
    assert.equal((await archiveWithBaseline(handle, dir, record('s-mech-1', 'Q keep', 'A keep', [fileRead('a.txt', casebook.contentHash('hello'))]))).ok, true)
    assert.equal((await bookkeeperRefresh.refreshStale(handle, dir, 's-mech-1')).value, false)
    assert.equal(createCalls.length, 0)

    writeFileSync(join(dir, 'a.txt'), 'changed', 'utf8')
    await installBookkeeperRuntime(port, ['s-mech-1'])
    const refreshed = await bookkeeperRefresh.refreshStale(handle, dir, 's-mech-1')
    assert.equal(refreshed.ok, true)
    assert.equal(refreshed.value, true)
    assert.equal(createCalls.length, 1)
    assert.equal(programCalls.length >= 1, true)

    const fetched = await casebook.fetchCase(handle, 10, 's-mech-1')
    assert.equal(fetched.value.q, CANONICAL_Q)
    assert.equal(fetched.value.a, CANONICAL_A)
    assert.equal(fetched.value.observations[0].contentHash, '2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824')
    assert.notEqual(fetched.value, null)
  } finally {
    bookkeeper.resetRuntime()
    cleanup()
  }
})
test('WHAT[knowledge-reuse-006] CASE006_mechanical_refresh_no_case_is_noop', async () => {
  const { dir, handle, cleanup } = sandbox()
  try {
    const result = await bookkeeperRefresh.refreshStale(handle, dir, 'missing')
    assert.equal(result.ok, true)
    assert.equal(result.value, false)
  } finally {
    cleanup()
  }
})
test('WHAT[knowledge-reuse-006] CASE006_mechanical_refresh_missing_file_still_publishes', async () => {
  const { dir, handle, cleanup } = sandbox()
  const { port, createCalls, programCalls } = scriptedBookkeeperPort()
  try {
    writeFileSync(join(dir, 'gone.txt'), 'x', 'utf8')
    assert.equal((await archiveWithBaseline(handle, dir, record('s-gone', 'Q', 'A', [fileRead('gone.txt', casebook.contentHash('x'))]))).ok, true)
    rmSync(join(dir, 'gone.txt'), { force: true })
    await installBookkeeperRuntime(port, ['s-gone'])
    const result = await bookkeeperRefresh.refreshStale(handle, dir, 's-gone')
    assert.equal(result.ok, true)
    assert.equal(result.value, true)
    assert.equal(createCalls.length, 1)
    assert.equal(programCalls.length >= 1, true)
    const fetched = await casebook.fetchCase(handle, 10, 's-gone')
    assert.equal(fetched.value.q, CANONICAL_Q)
    assert.equal(fetched.value.observations.length, 1)
  } finally {
    bookkeeper.resetRuntime()
    cleanup()
  }
})

for (const fate of [
  { code: 'AppendNotAttempted', suffix: 'NOT_ATTEMPTED', phase: 'BeforePhysicalAppend' },
  { code: 'CommitUnknown', suffix: 'COMMIT_UNKNOWN', phase: 'DurabilityBarrier' },
  { code: 'NoNewWriteReleaseFailed', suffix: 'NO_NEW_WRITE_RELEASE_FAILED', phase: 'StoreRelease' },
]) {
  test(`WHAT[knowledge-reuse-006] Bookkeeper typed mapping ${fate.code} does not report a successful refresh or retry`, async () => {
    const { dir, handle, cleanup } = sandbox()
    const { port, createCalls } = scriptedBookkeeperPort()
    try {
      writeFileSync(join(dir, 'a.txt'), 'old bytes', 'utf8')
      assert.equal((await archiveWithBaseline(handle, dir,
        record('typed-refresh', 'old Q', 'old A', [fileRead('a.txt', casebook.contentHash('old bytes'))]))).ok, true)
      const before = (await casebook.fetchCase(handle, 10, 'typed-refresh')).value
      writeFileSync(join(dir, 'a.txt'), 'changed bytes', 'utf8')
      await installBookkeeperRuntime(port, ['typed-refresh'])
      const cause = new Error('Bookkeeper append failure')
      const observed = []
      const failing = eventStore.createAppendFailureStore(handle, { code: fate.code, phase: fate.phase, cause },
        (append) => observed.push(append))
      const result = await bookkeeperRefresh.refreshStale(failing, dir, 'typed-refresh')
      assert.equal(result.ok, false)
      assert.equal(result.value, undefined)
      assert.equal(result.code, `CASEBOOK_APPEND_${fate.suffix}`)
      assert.equal(createCalls.length, 1)
      assert.equal(observed.length, 1)
      assert.equal(result.persistenceFailure.operation, 'Refresh')
      assert.equal(result.persistenceFailure.eventId, observed[0].requested[0].id)
      assert.equal(result.persistenceFailure.isOriginalError(observed[0].originalError), true)
      assert.strictEqual(result.persistenceFailure.primary.cause, cause)
      assert.deepEqual((await casebook.fetchCase(handle, 10, 'typed-refresh')).value, before,
        'controlled mapping fixture refused the append; native physical ambiguity has a separate oracle')
    } finally { bookkeeper.resetRuntime(); cleanup() }
  })
}
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { execFileSync } = await import("node:child_process");
const { mkdtempSync, mkdirSync, rmSync, writeFileSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join } = await import("node:path");
const eventStore = await import("../../../dist/Persistence/EventStore/Surface.js");
const casebook = await import("../../../dist/Repository/Knowledge/Casebook/Surface.js");
const bookkeeper = await import("../../../dist/Repository/Knowledge/Casebook/BookkeeperSurface.js");
const bookkeeperRefresh = await import("../../../dist/Repository/Knowledge/Casebook/BookkeeperRefreshSurface.js");
const lifecycle = await import("../../../dist/Repository/Knowledge/Casebook/LifecycleSurface.js");

const fileRead = (path, contentHash) => ({ kind: 'file-read', path, contentHash })
const CANONICAL_Q = 'Canonical maintained question'
const CANONICAL_A = 'Summary of Inspector answers across turns.'
const scriptedBookkeeperPort = () => {
  const createCalls = []
  const prompts = []
  const programCalls = []
  const terminals = new Set()
  let seq = 0
  const port = {
    CreateChildSession: async () => {
      throw new Error('Bookkeeper must not attach to a deleted physical parent')
    },
    CreateSiblingSession: async (_ownerSessionId, physicalParentId) => {
      seq += 1
      const child = `bk-child-${seq}`
      createCalls.push({ child, physicalParentId })
      return bookkeeper.acceptedSession(child)
    },
    AbortSession: async () => bookkeeper.aborted(),
    SubscribeTerminal: (_childId, callback) => {
      terminals.add(callback)
      return { Dispose: () => terminals.delete(callback) }
    },
    SendPrompt: async (childSession, text) => {
      prompts.push(text)
      const sid = bookkeeper.sessionValue(childSession)
      const tx = bookkeeper.txIdFor(sid)
      assert.notEqual(tx, '', 'SendPrompt must run against a bound Bookkeeper tx')
      const out = await bookkeeperTool.runProgram(
        sid,
        bookkeeperRequest(`class Js extends JsProgram { async run() { this.setQuestion(${JSON.stringify(CANONICAL_Q)}); this.setAnswer(${JSON.stringify(CANONICAL_A)}); return { changed: true }; } }`),
      )
      assert.equal(String(out).includes('changed = true'), true, out)
      programCalls.push(tx)
      for (const callback of terminals) callback(bookkeeper.sessionId(sid), bookkeeper.completed(sid))
      return bookkeeper.acceptedPrompt()
    },
  }
  return { port, createCalls, prompts, programCalls }
}
const installBookkeeperRuntime = (port, ownerSessionIds) => {
  const installed = bookkeeper.setRuntime(
    port,
    ownerSessionIds.map((sessionId) => ({
      sessionId,
      logicalRunId: `bookkeeper-run-${sessionId}`,
      authorityRootUserMessageId: `bookkeeper-root-${sessionId}`,
      agent: 'engineer',
    })),
  )
  assert.equal(installed.ok, true, installed.error)
}
const record = (sessionId, q, a, observations) => ({ sessionId, q, a, observations, lastAccessOrder: 0 })

test('WHAT[knowledge-reuse-006] CASE006_create_child_once_per_refresh_via_js_bookkeeper', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'wxs-bk-session-refresh-'))
  const handle = eventStore.create(dir, 'bookkeeper-session-refresh')
  const { port, createCalls, programCalls, prompts } = scriptedBookkeeperPort()
  try {
    writeFileSync(join(dir, 'a.txt'), 'hello', 'utf8')
    assert.equal((await archiveWithBaseline(handle, dir, record('s-session-refresh', 'Q keep', 'A keep', [fileRead('a.txt', casebook.contentHash('hello'))]))).ok, true)
    writeFileSync(join(dir, 'a.txt'), 'changed', 'utf8')
    await installBookkeeperRuntime(port, ['s-session-refresh'])
    const refreshed = await bookkeeperRefresh.refreshStale(handle, dir, 's-session-refresh')
    assert.equal(refreshed.ok, true)
    assert.equal(refreshed.value, true)
    assert.equal(createCalls.length, 1)
    assert.equal(createCalls[0].physicalParentId, undefined)
    assert.equal(programCalls.length >= 1, true)
    assert.equal(prompts.some((text) => String(text).includes('CaseRefresh')), true)
    const fetched = await casebook.fetchCase(handle, 10, 's-session-refresh')
    assert.equal(fetched.value.q, CANONICAL_Q)
    assert.equal(fetched.value.a, CANONICAL_A)
  } finally {
    bookkeeper.resetRuntime()
    eventStore.dispose(handle)
    rmSync(dir, { recursive: true, force: true })
  }
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { execFileSync } = await import("node:child_process");
const { mkdtempSync, mkdirSync, rmSync, writeFileSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join } = await import("node:path");
const eventStore = await import("../../../dist/Persistence/EventStore/Surface.js");
const casebook = await import("../../../dist/Repository/Knowledge/Casebook/Surface.js");
const bookkeeper = await import("../../../dist/Repository/Knowledge/Casebook/BookkeeperSurface.js");
const bookkeeperRefresh = await import("../../../dist/Repository/Knowledge/Casebook/BookkeeperRefreshSurface.js");
const lifecycle = await import("../../../dist/Repository/Knowledge/Casebook/LifecycleSurface.js");
const { CANONICAL_A, CANONICAL_Q, installBookkeeperRuntime, scriptedBookkeeperPort } = await import("./support/bookkeeper-session-support.mjs");

const fileRead = (path, contentHash) => ({ kind: 'file-read', path, contentHash })
const FAIL_A = 'A-must-fail-synthesis'
const failingPort = () => {
  let seq = 0
  return {
    port: {
      CreateChildSession: async () => {
        throw new Error('Bookkeeper must not attach to a deleted physical parent')
      },
      CreateSiblingSession: async () => bookkeeper.acceptedSession(`bk-fail-${++seq}`),
      AbortSession: async () => bookkeeper.aborted(),
      SubscribeTerminal: () => ({ Dispose: () => {} }),
      SendPrompt: async () => bookkeeper.failedPrompt('injected synth failure'),
    },
  }
}
const record = (sessionId, q, a, observations) => ({ sessionId, q, a, observations, lastAccessOrder: 0 })

test('WHAT[knowledge-reuse-006] CASE006_injected_synthesizer_error_keeps_old_case', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'wxs-bk-err-'))
  const handle = eventStore.create(dir, 'bookkeeper-synthesis-error')
  const { port } = failingPort()
  try {
    writeFileSync(join(dir, 'a.txt'), 'hello', 'utf8')
    assert.equal((await archiveWithBaseline(handle, dir, record('s-err-1', 'Q keep', FAIL_A, [fileRead('a.txt', casebook.contentHash('hello'))]))).ok, true)
    writeFileSync(join(dir, 'a.txt'), 'changed', 'utf8')
    await installBookkeeperRuntime(port, ['s-err-1'])
    const refreshed = await bookkeeperRefresh.refreshStale(handle, dir, 's-err-1')
    assert.equal(refreshed.ok, false)
    assert.match(String(refreshed.error), /injected synth failure/)
    const fetched = await casebook.fetchCase(handle, 10, 's-err-1')
    assert.equal(fetched.value.a, FAIL_A)
    assert.equal(fetched.value.q, 'Q keep')
    assert.equal(fetched.value.observations[0].contentHash, casebook.contentHash('hello'))
  } finally {
    bookkeeper.resetRuntime()
    eventStore.dispose(handle)
    rmSync(dir, { recursive: true, force: true })
  }
})
test('WHAT[knowledge-reuse-006] CASE006_synthesizer_runs_once_per_stale_refresh', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'wxs-bk-once-'))
  const handle = eventStore.create(dir, 'bookkeeper-synthesis-once')
  const { port, createCalls, programCalls } = scriptedBookkeeperPort()
  try {
    writeFileSync(join(dir, 'a.txt'), 'hello', 'utf8')
    assert.equal((await archiveWithBaseline(handle, dir, record('s-once', 'Q-count-synth-once', 'A once', [fileRead('a.txt', casebook.contentHash('hello'))]))).ok, true)
    writeFileSync(join(dir, 'a.txt'), 'changed', 'utf8')
    await installBookkeeperRuntime(port, ['s-once'])
    const refreshed = await bookkeeperRefresh.refreshStale(handle, dir, 's-once')
    assert.equal(refreshed.ok, true)
    assert.equal(refreshed.value, true)
    assert.equal(createCalls.length, 1)
    assert.equal(programCalls.length >= 1, true)
  } finally {
    bookkeeper.resetRuntime()
    eventStore.dispose(handle)
    rmSync(dir, { recursive: true, force: true })
  }
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const bookkeeper = await import("../../../dist/Repository/Knowledge/Casebook/BookkeeperSurface.js");

const schemaNode = (kind, extra = {}) => ({
  kind,
  ...extra,
  describe: () => schemaNode(`${kind}-described`, extra),
  optional: () => schemaNode(`${kind}-optional`, extra),
})
const fakeSchema = {
  string: () => schemaNode('string'),
  enum: (values) => schemaNode('enum', { values }),
}
const factory = { tool: { schema: fakeSchema } }

test('WHAT[knowledge-reuse-006] CASE006_bookkeeper_provider_contract_is_one_program', () => {
  const tool = bookkeeperTool.contract(factory)
  assert.equal(tool.name, 'todowrite-for-bookkeeper')
  assert.deepEqual([...tool.argumentNames].sort(), ['intent', 'name', 'program'])
  assert.match(tool.description, /one atomic JavaScript transformation|用一次原子的 JavaScript transformation/i)
  assert.match(tool.description, /setQuestion\(newText\)/)
  assert.match(tool.description, /setAnswer\(newText\)/)
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const bookkeeper = await import("../../../dist/Repository/Knowledge/Casebook/BookkeeperSurface.js");

const schemaNode = (kind, extra = {}) => ({
  kind,
  ...extra,
  describe: () => schemaNode(`${kind}-described`, extra),
  optional: () => schemaNode(`${kind}-optional`, extra),
})
const fakeSchema = {
  string: () => schemaNode('string'),
  enum: (values) => schemaNode('enum', { values }),
}
const factory = { tool: { schema: fakeSchema } }
const run = (sessionId, program) => bookkeeperTool.runProgram(sessionId, bookkeeperRequest(program))
const current = (txId) => {
  const staged = bookkeeper.snapshot(txId)
  assert.equal(staged.ok, true)
  return staged.value
}
const currentCase = (question, answer) => [question, answer]

test('WHAT[knowledge-reuse-006] js_bookkeeper_surface_requires_request_envelope_and_has_case_sdk', () => {
  const tool = bookkeeperTool.contract(factory)
  assert.equal(tool.name, 'todowrite-for-bookkeeper')
  assert.deepEqual([...tool.argumentNames].sort(), ['intent', 'name', 'program'])
  assert.match(tool.description, /question\(matches = \[\]\)/)
  assert.match(tool.description, /answer\(matches = \[\]\)/)
  assert.match(tool.description, /setQuestion\(newText\)/)
  assert.match(tool.description, /setAnswer\(newText\)/)
  assert.match(tool.description, /not a line number|不是行号/)
  assert.doesNotMatch(tool.description, /Q\.md|A\.md|old_text|new_text|filesystem/i)
})
test('WHAT[knowledge-reuse-006] js_bookkeeper_program_reshapes_question_and_answer_atomically', async () => {
  const tx = 'tx-js-bookkeeper-both'
  const session = 'bk-js-bookkeeper-both'
  bookkeeper.beginTransaction(tx, '## Goal\nkeep old goal\n## Constraints\nold constraint', '## Answer\nold answer\n## Evidence\nweak')
  bookkeeper.bindSession(session, tx, 'owner-1')

  try {
    const result = await run(
      session,
      `class Js extends JsProgram {
        async run() {
          const question = this.question([
            ["goal", "afterGoal", "## Goal"],
            ["constraints", "afterConstraints", "## Constraints"],
          ]);
          const answer = this.answer([
            ["claim", "afterClaim", "## Answer"],
            ["evidence", "afterEvidence", "## Evidence"],
          ]);

          this.setQuestion(
            question.text("^", "constraints")
              + "## Constraints\\nnew constraint"
          );
          this.setAnswer(
            "## Answer\\nnew answer\\n"
              + answer.text("evidence", "$")
          );
          return { changed: true, source: "coherent case" };
        }
      }`,
    )

    assert.match(String(result), /changed = true/)
    assert.deepEqual(current(tx), ['## Goal\nkeep old goal\n## Constraints\nnew constraint', '## Answer\nnew answer\n## Evidence\nweak'])

    const taken = bookkeeper.take(tx)
    assert.equal(taken.ok, true)
    assert.deepEqual(taken.value, currentCase(taken.value[0], taken.value[1]))
  } finally {
    bookkeeper.abort(tx)
    bookkeeper.resetRuntime()
  }
})
test('WHAT[knowledge-reuse-006] js_bookkeeper_zero_mutation_is_legal', async () => {
  const tx = 'tx-js-bookkeeper-idle'
  const session = 'bk-js-bookkeeper-idle'
  bookkeeper.beginTransaction(tx, 'Q', 'A')
  bookkeeper.bindSession(session, tx, 'owner-1')

  try {
    const result = await run(
      session,
      `class Js extends JsProgram {
        async run() {
          return { changed: false, question: this.question().text(), answer: this.answer().text() };
        }
      }`,
    )

    assert.match(String(result), /changed = false/)
    assert.deepEqual(current(tx), ['Q', 'A'])
  } finally {
    bookkeeper.abort(tx)
    bookkeeper.resetRuntime()
  }
})
test('WHAT[knowledge-reuse-006] js_bookkeeper_duplicate_set_rolls_back_the_whole_program', async () => {
  const tx = 'tx-js-bookkeeper-duplicate'
  const session = 'bk-js-bookkeeper-duplicate'
  bookkeeper.beginTransaction(tx, 'Q', 'A')
  bookkeeper.bindSession(session, tx, 'owner-1')

  try {
    const result = await run(
      session,
      `class Js extends JsProgram {
        async run() {
          this.setQuestion("Q1");
          this.setQuestion("Q2");
          this.setAnswer("A1");
          return { changed: true };
        }
      }`,
    )

    assert.match(String(result), /setQuestion may be called at most once|setQuestion 在同一个 todowrite-for-bookkeeper program 中最多只能调用一次/i)
    assert.deepEqual(current(tx), ['Q', 'A'])
  } finally {
    bookkeeper.abort(tx)
    bookkeeper.resetRuntime()
  }
})
test('WHAT[knowledge-reuse-006] js_bookkeeper_program_failure_rolls_back_staged_mutation', async () => {
  const tx = 'tx-js-bookkeeper-throw'
  const session = 'bk-js-bookkeeper-throw'
  bookkeeper.beginTransaction(tx, 'Q', 'A')
  bookkeeper.bindSession(session, tx, 'owner-1')

  try {
    const result = await run(
      session,
      `class Js extends JsProgram {
        async run() {
          this.setAnswer("changed");
          throw new Error("semantic stop");
        }
      }`,
    )

    assert.match(String(result), /semantic stop/)
    assert.deepEqual(current(tx), ['Q', 'A'])
  } finally {
    bookkeeper.abort(tx)
    bookkeeper.resetRuntime()
  }
})
test('WHAT[knowledge-reuse-006] js_bookkeeper_unbound_session_cannot_change_a_case', async () => {
  const result = await run(
    'no-such-session',
    `class Js extends JsProgram {
      async run() {
        this.setQuestion("changed");
        return null;
      }
    }`,
  )

  assert.match(String(result), /no Bookkeeper transaction|没有 Bookkeeper transaction/i)
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const bookkeeper = await import("../../../dist/Repository/Knowledge/Casebook/BookkeeperSurface.js");

test('WHAT[knowledge-reuse-006] refresh prompt renders the supplied old case and diff without full-file sections', async () => {
  // Bookkeeper CaseRefresh receives: old case + diff. No repo read/glob/grep.
  assert.equal(typeof bookkeeper.createRefreshPrompt, 'function', 'bookkeeper must format refresh prompt with diff only')
  const prompt = bookkeeper.createRefreshPrompt({
    q: 'Old Q',
    a: 'Old A',
    relatedPaths: ['a.txt'],
    diff: '-B\n+C',
  })
  assert.match(prompt, /CaseRefresh/)
  assert.match(prompt, /Old Q/)
  assert.match(prompt, /Old A/)
  assert.match(prompt, /diff/)
  assert.match(prompt, /Do not read the repository/)
  assert.doesNotMatch(prompt, /\[(?:transcript|file_contents|observations)\]/i,
    'maintenance must not receive a replay trace or full file payloads')
})
}

test.todo('WHAT[knowledge-reuse-006] GAP-160: actual Bookkeeper repository access is rejected')

test('WHAT[knowledge-reuse-006] successful zero-setter maintenance advances the captured baseline across store reopen', async () => {
  const { mkdtempSync, mkdirSync, rmSync, writeFileSync } = await import('node:fs')
  const { tmpdir } = await import('node:os')
  const { join } = await import('node:path')
  const { createHash } = await import('node:crypto')
  const { createCase, casebook, eventStore, parse } = await import('./support/casebook.mjs')
  const { installBookkeeperRuntime } = await import('./support/bookkeeper-session-support.mjs')
  const fetchSurface = await import('../../../dist/Repository/Knowledge/Casebook/FetchSurface.js')
  const settlements = await import('../../../dist/Repository/Knowledge/Casebook/SettlementSurface.js')
  const owner = settlements.createOwner(() => assert.fail('zero-setter maintenance must not produce a semantic cut'))
  const bookkeeper = await import('../../../dist/Repository/Knowledge/Casebook/BookkeeperSurface.js')
  const directory = mkdtempSync(join(tmpdir(), 'wxs-bookkeeper-zero-setter-'))
  const prompts = []
  const programs = []
  const children = []
  const terminals = new Set()
  let store
  const port = {
    CreateChildSession: async () => { throw new Error('Bookkeeper must use its private sibling session') },
    CreateSiblingSession: async () => {
      const child = `zero-setter-${children.length + 1}`
      children.push(child)
      return bookkeeper.acceptedSession(child)
    },
    AbortSession: async () => bookkeeper.aborted(),
    SubscribeTerminal: (_child, callback) => {
      terminals.add(callback)
      return { Dispose: () => terminals.delete(callback) }
    },
    SendPrompt: async (child, prompt) => {
      prompts.push(prompt)
      const session = bookkeeper.sessionValue(child)
      assert.notEqual(bookkeeper.txIdFor(session), '', 'the real runtime binds the transaction before dispatch')
      const output = await bookkeeperTool.runProgram(session, bookkeeperRequest(`class Js extends JsProgram {
        async run() {
          return { changed: false, question: this.question().text(), answer: this.answer().text() };
        }
      }`))
      assert.deepEqual(parse(output), { data: { changed: false, question: 'Question?', answer: 'Answer B' } })
      programs.push(output)
      for (const callback of terminals) callback(bookkeeper.sessionId(session), bookkeeper.completed(session))
      return bookkeeper.acceptedPrompt()
    },
  }
  const fetch = (shelfmark) => fetchSurface.contract(
    { tool: { schema: { string: () => ({}) } } }, directory, store, owner,
  ).execute({ shelfmark }, { sessionID: 'zero-setter-reader', agent: 'engineer' })
  const expectedDiff = (before, after) => [
    'diff --git a/subject.txt b/subject.txt',
    '--- a/subject.txt',
    '+++ b/subject.txt',
    '@@ -1,1 +1,1 @@',
    `-${before}`, `+${after}`, '',
  ].join('\n')
  const assertCapturedState = async (state, text) => {
    const entry = JSON.parse(state)['subject.txt']
    assert.equal(entry.kind, 'Present')
    assert.equal(entry.sha256, createHash('sha256').update(text).digest('hex'))
    assert.deepEqual(Buffer.from(await eventStore.readPayload(store, entry.payloadRef)), Buffer.from(text))
  }
  try {
    mkdirSync(join(directory, '.wanxiang', 'casebook'), { recursive: true })
    store = eventStore.create(directory, 'zero-setter-before-reopen')
    const { identity, baseline, shelfmark } = await createCase({ dir: directory, store }, 'zero-setter', 'version-B\n')
    installBookkeeperRuntime(port, [identity])
    writeFileSync(join(directory, 'subject.txt'), 'version-C\n')

    assert.equal(parse(await fetch(shelfmark)).answer, 'Answer B')
    assert.equal(programs.length, 1)
    assert.equal(children.length, 1)
    assert.equal(parse(prompts[0]).diff.content, expectedDiff('version-B', 'version-C'))
    const maintained = await casebook.fetchCaseByIdentity(store, identity)
    assert.equal(maintained.q, 'Question?')
    assert.equal(maintained.a, 'Answer B')
    assert.equal(maintained.completionFileState, baseline)
    await assertCapturedState(maintained.maintenanceFileState, 'version-C\n')

    bookkeeper.resetRuntime()
    eventStore.dispose(store)
    store = undefined
    store = eventStore.create(directory, 'zero-setter-after-reopen')
    installBookkeeperRuntime(port, [identity])
    const reopened = await casebook.fetchCaseByIdentity(store, identity)
    assert.equal(reopened.q, maintained.q)
    assert.equal(reopened.a, maintained.a)
    assert.equal(reopened.completionFileState, baseline)
    assert.equal(reopened.maintenanceFileState, maintained.maintenanceFileState)
    await assertCapturedState(reopened.completionFileState, 'version-B\n')
    await assertCapturedState(reopened.maintenanceFileState, 'version-C\n')
    const unchanged = await fetch(shelfmark)
    assert.equal(parse(unchanged).answer, 'Answer B')
    assert.match(unchanged, /No change was found|没有变化/)
    assert.equal(programs.length, 1, 'reopened maintenance must not repeat a diff already accepted without setters')
    assert.equal(children.length, 1)

    writeFileSync(join(directory, 'subject.txt'), 'version-D\n')
    assert.equal(parse(await fetch(shelfmark)).answer, 'Answer B')
    assert.equal(programs.length, 2)
    assert.equal(children.length, 2)
    assert.equal(parse(prompts[1]).diff.content, expectedDiff('version-C', 'version-D'))
    const latest = await casebook.fetchCaseByIdentity(store, identity)
    assert.equal(latest.q, 'Question?')
    assert.equal(latest.a, 'Answer B')
    assert.equal(latest.completionFileState, baseline)
    await assertCapturedState(latest.maintenanceFileState, 'version-D\n')
  } finally {
    bookkeeper.resetRuntime()
    if (store) eventStore.dispose(store)
    rmSync(directory, { recursive: true, force: true })
  }
})

test('WHAT[knowledge-reuse-006] duplicate answer setter rolls back both staged fields', async () => {
  const bookkeeper = await import('../../../dist/Repository/Knowledge/Casebook/BookkeeperSurface.js')
  const assert = (await import('node:assert/strict')).default
  const tx = 'duplicate-answer'
  const session = 'bookkeeper-duplicate-answer'
  bookkeeper.beginTransaction(tx, 'Q', 'A')
  bookkeeper.bindSession(session, tx, 'owner')
  try {
    const result = await bookkeeperTool.runProgram(session, bookkeeperRequest('class Js extends JsProgram { async run() { this.setQuestion("changed Q"); this.setAnswer("one"); this.setAnswer("two"); } }'))
    assert.match(String(result), /setAnswer/)
    assert.deepEqual(bookkeeper.snapshot(tx).value, ['Q', 'A'])
  } finally { bookkeeper.abort(tx); bookkeeper.resetRuntime() }
})

test('WHAT[knowledge-reuse-006] awaitCompletion unwritten provider aborts staged transaction and take retains untouched slot', async () => {
  const { bookkeeperStaging } = await import('wanxiangli/casebook')
  const { mkdtempSync, rmSync, writeFileSync } = await import('node:fs')
  const { tmpdir } = await import('node:os')
  const { join } = await import('node:path')
  const eventStore = await import('../../../dist/Persistence/EventStore/Surface.js')
  const casebook = await import('../../../dist/Repository/Knowledge/Casebook/Surface.js')
  const bookkeeper = await import('../../../dist/Repository/Knowledge/Casebook/BookkeeperSurface.js')
  const bookkeeperRefresh = await import('../../../dist/Repository/Knowledge/Casebook/BookkeeperRefreshSurface.js')
  const { installBookkeeperRuntime } = await import('./support/bookkeeper-session-support.mjs')

  const fileRead = (path, contentHash) => ({ kind: 'file-read', path, contentHash })
  const sandbox = () => {
    const dir = mkdtempSync(join(tmpdir(), 'wxs-bk-'))
    const handle = eventStore.create(dir, 'bookkeeper-mechanical')
    return {
      dir,
      handle,
      cleanup: () => {
        eventStore.dispose(handle)
        rmSync(dir, { recursive: true, force: true })
      },
    }
  }
  const record = (sessionId, q, a, observations) => ({ sessionId, q, a, observations, lastAccessOrder: 0 })

  // 1. 验证库语义：未 touched 的 take 被拒绝，但 slot 依然保持
  const staging = bookkeeperStaging
  const txUntouched = 'tx-test-untouched-retained'
  staging.beginTransaction(txUntouched, 'Q-keep', 'A-keep')
  assert.equal(staging.touched(txUntouched), false)
  const takeRes = bookkeeper.take(txUntouched)
  assert.equal(takeRes.ok, false)
  assert.match(takeRes.error, /not written/)
  // 验证 slot 仍然保留在 staging 中
  const snapRes = bookkeeper.snapshot(txUntouched)
  assert.equal(snapRes.ok, true)
  assert.deepEqual(snapRes.value, ['Q-keep', 'A-keep'])
  staging.abort(txUntouched)

  // 2. 验证宿主 awaitCompletion：未写入（Ok() 但 touched=false）时，错误返回且 slot 被 abort 清除
  const { dir, handle, cleanup } = sandbox()
  const txCaptured = []
  const terminals = new Set()
  let seq = 0
  const unwrittenPort = {
    CreateChildSession: async () => { throw new Error('not used') },
    CreateSiblingSession: async (_ownerSessionId, physicalParentId) => {
      seq += 1
      const child = 'bk-unwritten-' + seq
      return bookkeeper.acceptedSession(child)
    },
    AbortSession: async () => bookkeeper.aborted(),
    SubscribeTerminal: (_childId, callback) => {
      terminals.add(callback)
      return { Dispose: () => terminals.delete(callback) }
    },
    SendPrompt: async (childSession, _text) => {
      const sid = bookkeeper.sessionValue(childSession)
      const tx = bookkeeper.txIdFor(sid)
      txCaptured.push(tx)
      // 不调用 bookkeeperTool.runProgram，模拟 provider 完成但未写入任何暂存内容（touched 保持 false）
      for (const callback of terminals) callback(bookkeeper.sessionId(sid), bookkeeper.completed(sid))
      return bookkeeper.acceptedPrompt()
    }
  }

  try {
    writeFileSync(join(dir, 'a.txt'), 'initial', 'utf8')
    assert.equal((await archiveWithBaseline(handle, dir, record('s-unwritten', 'Q0', 'A0', [fileRead('a.txt', casebook.contentHash('initial'))]))).ok, true)
    writeFileSync(join(dir, 'a.txt'), 'changed-unwritten', 'utf8')
    await installBookkeeperRuntime(unwrittenPort, ['s-unwritten'])
    const refreshed = await bookkeeperRefresh.refreshStale(handle, dir, 's-unwritten')
    assert.equal(refreshed.ok, false)
    assert.match(String(refreshed.error), /did not write the staged case/)
    assert.equal(txCaptured.length, 1)
    const tx = txCaptured[0]
    // 关键断言：slot 已被 abort 清除！
    const snapAfterAbort = bookkeeper.snapshot(tx)
    assert.equal(snapAfterAbort.ok, false, 'did not write 分支必须已调用 abort(txId) 清除槽位')
  } finally {
    bookkeeper.resetRuntime()
    cleanup()
  }
})
