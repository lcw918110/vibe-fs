import * as bookkeeperTool from '../../../dist/Repository/Knowledge/Casebook/OpenCode/BookkeeperToolSurface.js' // CUT-40: tool-binding entries split from BookkeeperSurface
import test from 'node:test'

let programSequence = 0
const bookkeeperRequest = program => ({
  name: 'bookkeeper-010-' + (++programSequence),
  program,
  intent: 'Complete the bound Bookkeeper transaction and retain an explicit result if execution cannot finish.',
})


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
const openStore = (dir, writerId) => eventStore.create(join(dir, '.Herdchestra'), writerId)

test('WHAT[knowledge-reuse-010] CASE010_finalize_create_child_once_and_cleanup_never_runs_bookkeeper', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'wxs-bk-session-fin-'))
  const { port, createCalls, programCalls, prompts } = scriptedBookkeeperPort()
  try {
    execFileSync('git', ['init', '--quiet', dir])
    mkdirSync(join(dir, '.wanxiang', 'casebook'), { recursive: true })
    lifecycle.enable(dir)
    const key = 'insp-session-fin'
    await installBookkeeperRuntime(port, [key])
    lifecycle.notePrompt(key, 'Who owns PromptAuthority?')
    lifecycle.noteAnswer(key, 'Host owns PromptAuthority.')
    lifecycle.notePrompt(key, 'Where do Case facts live?')
    lifecycle.collect(key, 'read', { path: 'a.txt' }, 'hello')
    lifecycle.noteAnswer(key, 'Unified EventStore only.')
    assert.equal((await lifecycle.tryFinalize(dir, key)).ok, true)
    assert.equal(createCalls.length, 1)
    assert.equal(createCalls[0].physicalParentId, undefined)
    assert.equal(programCalls.length >= 1, true)
    assert.equal(prompts.some((text) => String(text).includes('CaseFinalize')), true)

    const handle = openStore(dir, 'bookkeeper-session-finalize-read')
    const fetched = await casebook.fetchCase(handle, 10, key)
    assert.equal(fetched.value.q, CANONICAL_Q)
    assert.equal(fetched.value.a, CANONICAL_A)
    const before = createCalls.length
    lifecycle.notePrompt(key, 'cleanup Q')
    lifecycle.noteAnswer(key, 'cleanup A')
    lifecycle.cleanup(key)
    assert.equal(createCalls.length, before)
    eventStore.dispose(handle)
  } finally {
    bookkeeper.resetRuntime()
    lifecycle.disable()
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

test('WHAT[knowledge-reuse-010] CASE010_finalize_uses_synthesizer_not_raw_noteAnswer', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'wxs-bk-fin-'))
  const { port, createCalls, programCalls } = scriptedBookkeeperPort()
  try {
    execFileSync('git', ['init', '--quiet', dir])
    mkdirSync(join(dir, '.wanxiang', 'casebook'), { recursive: true })
    lifecycle.enable(dir)
    const key = 'insp-synth-fin'
    await installBookkeeperRuntime(port, [key])
    const rawA = 'PromptAuthority is owned by the Host.'
    lifecycle.notePrompt(key, 'What owns PromptAuthority?')
    lifecycle.collect(key, 'read', { path: 'a.txt' }, 'hello')
    lifecycle.noteAnswer(key, rawA)
    assert.equal((await lifecycle.tryFinalize(dir, key)).ok, true)
    assert.equal(createCalls.length, 1)
    assert.equal(programCalls.length >= 1, true)

    const handle = eventStore.create(join(dir, '.Herdchestra'), 'bookkeeper-synthesis-finalize-read')
    const fetched = await casebook.fetchCase(handle, 10, key)
    assert.equal(fetched.value.q, CANONICAL_Q)
    assert.notEqual(fetched.value.a, rawA)
    assert.equal(fetched.value.a, CANONICAL_A)
    const publishedA = fetched.value.a
    lifecycle.notePrompt(key, 'Q2')
    lifecycle.noteAnswer(key, 'A2')
    const second = await lifecycle.tryFinalize(dir, key)
    assert.equal(second.ok, false)
    assert.match(String(second.error), /already finalized/)
    assert.equal((await casebook.fetchCase(handle, 10, key)).value.a, publishedA)
    eventStore.dispose(handle)
  } finally {
    bookkeeper.resetRuntime()
    lifecycle.disable()
    rmSync(dir, { recursive: true, force: true })
  }
})
test('WHAT[knowledge-reuse-010] CASE010_cleanup_never_synthesizes', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'wxs-bk-cleanup-'))
  const { port, createCalls, programCalls } = scriptedBookkeeperPort()
  try {
    execFileSync('git', ['init', '--quiet', dir])
    mkdirSync(join(dir, '.wanxiang', 'casebook'), { recursive: true })
    lifecycle.enable(dir)
    const key = 'insp-cleanup-synth'
    await installBookkeeperRuntime(port, [key])
    lifecycle.notePrompt(key, 'Q-cleanup-never-synth')
    lifecycle.collect(key, 'read', { path: 'b.txt' }, 'body')
    lifecycle.noteAnswer(key, 'A cleanup')
    lifecycle.cleanup(key)
    assert.equal(createCalls.length, 0)
    assert.equal(programCalls.length, 0)
    const handle = eventStore.create(join(dir, '.Herdchestra'), 'bookkeeper-synthesis-cleanup-read')
    const fetched = await casebook.fetchCase(handle, 10, key)
    assert.equal(fetched.value, null)
    eventStore.dispose(handle)
  } finally {
    bookkeeper.resetRuntime()
    lifecycle.disable()
    rmSync(dir, { recursive: true, force: true })
  }
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { mkdtempSync, rmSync, mkdirSync, writeFileSync, readFileSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join } = await import("node:path");
const { create: createEventStore, dispose: disposeEventStore } = await import("../../../dist/Persistence/EventStore/Surface.js");
const casebook = await import("../../../dist/Repository/Knowledge/Casebook/Surface.js");

const fileRead = (path, contentHash) => ({ kind: 'file-read', path, contentHash })
const globResult = (pattern, paths) => ({ kind: 'glob-result', pattern, paths })
const createCasebookEventStore = () => {
  const commonDir = mkdtempSync(join(tmpdir(), 'wxs-casebook-store-'))
  const store = createEventStore(commonDir, 'casebook-test-writer')
  return {
    store,
    close: () => {
      disposeEventStore(store)
      rmSync(commonDir, { recursive: true, force: true })
    },
  }
}
const unwrap = async (operation) => {
  const result = await operation
  assert.equal(result.ok, true, `expected successful Casebook operation, got ${JSON.stringify(result.error)}`)
  return result.value
}
const caseRec = (sessionId, q, a, observations) => ({
  sessionId,
  q,
  a,
  observations,
  lastAccessOrder: 0,
})
const findCase = async (store, sessionId) => {
  const result = await casebook.fetchCase(store, 10, sessionId)
  assert.equal(result.ok, true, JSON.stringify(result.error))
  return result.value
}

test('WHAT[knowledge-reuse-010] CASE010_finalize_is_exactly_once_per_scope', async () => {
  const local = createCasebookEventStore()
  try {
    assert.equal((await casebook.finalize(local.store, caseRec('scope-1', 'Q', 'A', []))).ok, true)
    const second = await casebook.finalize(local.store, caseRec('scope-1', 'Q', 'A2', []))
    assert.equal(second.ok, false)
    assert.match(second.error, /already finalized/)
    assert.equal((await casebook.finalize(local.store, caseRec('scope-2', 'Q', 'A', []))).ok, true)
  } finally {
    local.close()
  }
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { mkdtempSync, rmSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join } = await import("node:path");
const eventStore = await import("../../../dist/Persistence/EventStore/Surface.js");
const casebook = await import("../../../dist/Repository/Knowledge/Casebook/Surface.js");

const read = (path, hash) => ({ kind: 'file-read', path, contentHash: hash })
const glob = (pattern, paths) => ({ kind: 'glob-result', pattern, paths })
const caseRec = (sessionId, q, a, observations) => ({
  sessionId,
  q,
  a,
  observations,
  lastAccessOrder: 0,
})
const project = (events) =>
  events.reduce((world, event) => {
    const result = casebook.applyEvent(world, event)
    assert.equal(result.ok, true, JSON.stringify(result.error))
    return result.world
  }, casebook.emptyWorld())
const openStore = () => {
  const dir = mkdtempSync(join(tmpdir(), 'wxs-casebook-surface-'))
  return {
    dir,
    handle: eventStore.create(dir, 'casebook-surface'),
    close() {
      eventStore.dispose(this.handle)
      rmSync(dir, { recursive: true, force: true })
    },
  }
}

test('WHAT[knowledge-reuse-010] CASE010_finalize_is_exactly_once_per_scope', async () => {
  const local = openStore()
  try {
    const first = await casebook.finalize(local.handle, caseRec('scope-1', 'Q', 'A', []))
    assert.equal(first.ok, true, JSON.stringify(first.error))
    const second = await casebook.finalize(local.handle, caseRec('scope-1', 'Q', 'A2', []))
    assert.equal(second.ok, false, 'a second finalize for the same scope must be refused')
    assert.match(second.error, /already finalized/)
    const other = await casebook.finalize(local.handle, caseRec('scope-2', 'Q', 'A', []))
    assert.equal(other.ok, true, JSON.stringify(other.error))
  } finally {
    local.close()
  }
})
}

{
const { default: assert } = await import("node:assert/strict");
const { execFileSync } = await import("node:child_process");
const { mkdtempSync, mkdirSync, rmSync, writeFileSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join } = await import("node:path");
const { default: test } = await import("node:test");
const { parse: parseToml } = await import("smol-toml");
const eventStore = await import("../../../dist/Persistence/EventStore/Surface.js");
const syncDelegate = await import("../../../dist/Execution/Delegation/SyncDelegate/Surface.js");
const jsWorkflow = await import("../../../dist/Repository/Programming/Js/WorkflowSurface.js");
const runtimeSurface = await import("../../../dist/Repository/Programming/Js/RuntimeSurface.js");
const casebook = await import("../../../dist/Repository/Knowledge/Casebook/Surface.js");
const lifecycle = await import("../../../dist/Repository/Knowledge/Casebook/LifecycleSurface.js");
const bookkeeper = await import("../../../dist/Repository/Knowledge/Casebook/BookkeeperSurface.js");
const { CANONICAL_A, CANONICAL_Q, installBookkeeperRuntime, scriptedBookkeeperPort } = await import("./support/bookkeeper-session-support.mjs");

const QUESTIONS = [
  ['Who owns PromptAuthority?', 'Host owns PromptAuthority.'],
  ['Where do Case facts live?', 'Unified EventStore only.'],
  ['When does CaseFinalize run?', 'ReuseScope close, once.'],
]

test('WHAT[knowledge-reuse-010] actual Engineer charge results can be manually supplied to one Casebook draft and finalized', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'wxs-g6-engineer-charge-'))
  execFileSync('git', ['init', '--quiet', dir])
  mkdirSync(join(dir, '.wanxiang', 'casebook'), { recursive: true })
  writeFileSync(join(dir, 'a.txt'), 'hello', 'utf8')
  const owner = 'ses_meditator_engineer_charge'
  const runtime = await syncDelegate.create(dir, [{ sessionId: owner, agent: 'manager' }])
  const bookkeeperPort = scriptedBookkeeperPort()
  try {
    lifecycle.enable(dir)
    let delegateId

    for (let i = 0; i < QUESTIONS.length; i += 1) {
      const [question, answer] = QUESTIONS[i]
      const pending = syncDelegate.executeEngineerCharge(runtime, owner, question)
      await syncDelegate.awaitPromptCount(runtime, owner, 'Engineer', i + 1)
      assert.equal(syncDelegate.acceptPrompt(runtime, owner, 'Engineer', i), true)
      assert.equal(syncDelegate.childCount(runtime), 1, `EngineerCharge Q${i + 1} did not reuse a single child`)
      const child = syncDelegate.child(runtime, owner, 'Engineer')
      assert.notEqual(child, null, 'Engineer child must be attached')
      if (i === 0) {
        delegateId = child
      } else {
        assert.equal(child, delegateId, 'GetOrCreate must reuse the Engineer session')
      }

      lifecycle.notePrompt(delegateId, question)
      // carry 中的字符串是字面材料，由宿主在交付时统一渲染为 TOML。
      const deliveredMaterial = `Question: ${question}\nAnswer: ${answer}\nTurn: ${i + 1}`
      const delivered = await jsWorkflow.execute(
        dir,
        'Engineer',
        'en',
        delegateId,
        {
          name: `engineer-turn-${i + 1}`,
          intent: 'Observe this program result before separately recording the explicit archived material.',
          program: `class Js extends JsProgram { async run() { return { ok: true }; } }`,
        },
        3000,
        Date.now() + 60_000,
        1 << 20,
        null,
      )
      assert.equal(jsWorkflow.caseName(delivered), 'Succeeded', jsWorkflow.render(delivered))
      // Program completion does not create a work delivery. This archive fixture
      // explicitly records the material for the exact assistant run below.
      runtimeSurface.recordFencedDeliveredView(
        delegateId,
        `asst_q${i + 1}`,
        `call_q${i + 1}`,
        [deliveredMaterial],
      )
      assert.equal(await syncDelegate.settle(runtime, owner, 'Engineer', `RAW-PROSE-Q${i + 1}`, `asst_q${i + 1}`), true)
      const text = await pending
      // 消费者事实：返回的材料里有本轮的答案与本轮该交的散文。
      // repository-programming [054]：carry 选择的材料与自动保留的当前报告
      // 一起回传；正文不需要在 carry 中复制。
      assert.match(
        text,
        new RegExp(answer.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')),
        `Engineer Q${i + 1} must return the delivered material carrying this turn's answer`,
      )
      assert.match(
        text,
        new RegExp(`RAW-PROSE-Q${i + 1}`),
        `Engineer Q${i + 1} must return the assistant prose this deliver owes alongside the material`,
      )
      // 前次已返回的身份不重复：上一轮的材料与散文都不再出现。
      for (let prior = 0; prior < i; prior += 1) {
        assert.equal(
          text.includes(QUESTIONS[prior][1]),
          false,
          `Engineer Q${i + 1} must not repeat the Q${prior + 1} material it already returned`,
        )
        assert.equal(
          text.includes(`RAW-PROSE-Q${prior + 1}`),
          false,
          `Engineer Q${i + 1} must not repeat the Q${prior + 1} prose it already returned`,
        )
      }
      assert.equal(parseToml(text).error, undefined)
      lifecycle.noteAnswer(delegateId, answer)
    }

    assert.equal(syncDelegate.childCount(runtime), 1, 'Engineer CreateChildSession once')
    lifecycle.collect(delegateId, 'read', { path: 'a.txt' }, 'hello')

    installBookkeeperRuntime(bookkeeperPort.port, [delegateId])
    const first = await lifecycle.tryFinalize(dir, delegateId)
    assert.equal(first.ok, true, `tryFinalize ok: ${JSON.stringify(first.error)}`)
    assert.equal(bookkeeperPort.createCalls.length, 1, 'detached Bookkeeper sibling lane once')
    assert.equal(bookkeeperPort.programCalls.length >= 1, true, 'todowrite-for-bookkeeper must reshape Q and A in one program')
    assert.equal(bookkeeperPort.prompts.some((text) => String(text).includes('CaseFinalize')), true)
    assert.equal(bookkeeperPort.prompts.some((text) => String(text).includes('Q1')), true)
    assert.equal(bookkeeperPort.prompts.some((text) => String(text).includes('Q3')), true)
    // 归档事实：交付的材料进了 Casebook draft，finalize 把它作为 transcript 交给 Bookkeeper。
    const finalizePrompt = bookkeeperPort.prompts.map((text) => String(text)).find((text) => text.includes('CaseFinalize'))
    assert.ok(finalizePrompt, 'the CaseFinalize prompt must be sent to the Bookkeeper lane')
    for (const [question, answer] of QUESTIONS) {
      assert.ok(finalizePrompt.includes(question), `the finalized draft must carry Q: ${question}`)
      assert.ok(finalizePrompt.includes(answer), `the finalized draft must carry A: ${answer}`)
    }

    const store = eventStore.create(join(dir, '.Herdchestra'), 'g6-engineer-charge-fetch')
    const fetched = await casebook.fetchCase(store, 10, delegateId)
    assert.equal(fetched.ok, true)
    assert.notEqual(fetched.value, null, 'Case exists after finalize')
    assert.equal(fetched.value.sessionId, delegateId)
    assert.equal(fetched.value.q, CANONICAL_Q)
    assert.notEqual(fetched.value.q, QUESTIONS[2][0], 'fetch must not return the last Engineer Q')
    assert.equal(fetched.value.a, CANONICAL_A)
    assert.equal(String(fetched.value.a).includes('evidence:'), false)
    assert.equal(String(fetched.value.a).includes('digest'), false)
    eventStore.dispose(store)
  } finally {
    bookkeeper.resetRuntime()
    lifecycle.disable()
    syncDelegate.dispose(runtime)
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
const syncDelegate = await import("../../../dist/Execution/Delegation/SyncDelegate/Surface.js");
const runtimeSurface = await import("../../../dist/Repository/Programming/Js/RuntimeSurface.js");
const casebook = await import("../../../dist/Repository/Knowledge/Casebook/Surface.js");
const lifecycle = await import("../../../dist/Repository/Knowledge/Casebook/LifecycleSurface.js");
const bookkeeper = await import("../../../dist/Repository/Knowledge/Casebook/BookkeeperSurface.js");
const { CANONICAL_A, CANONICAL_Q, installBookkeeperRuntime, scriptedBookkeeperPort } = await import("./support/bookkeeper-session-support.mjs");

const QUESTIONS = [
  ['Who owns PromptAuthority?', 'Host owns PromptAuthority.'],
  ['Where do Case facts live?', 'Unified EventStore only.'],
  ['When does CaseFinalize run?', 'ReuseScope close, once.'],
]

test('WHAT[knowledge-reuse-010] actual reusable Engineer results can be manually collected and archived; cleanup retains that archive', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'wxs-g6-host-reuse-'))
  execFileSync('git', ['init', '--quiet', dir])
  mkdirSync(join(dir, '.wanxiang', 'casebook'), { recursive: true })
  writeFileSync(join(dir, 'a.txt'), 'hello', 'utf8')
  const owner = 'ses_meditator_g6'
  const runtime = await syncDelegate.create(dir, [{ sessionId: owner, agent: 'manager' }])
  const bookkeeperPort = scriptedBookkeeperPort()
  try {
    lifecycle.enable(dir)
    let delegateId

    for (let i = 0; i < QUESTIONS.length; i += 1) {
      const [question, answer] = QUESTIONS[i]
      const pending = syncDelegate.invoke(runtime, owner, 'Engineer', question)
      await syncDelegate.awaitPromptCount(runtime, owner, 'Engineer', i + 1)
      assert.equal(syncDelegate.acceptPrompt(runtime, owner, 'Engineer', i), true)
      assert.equal(syncDelegate.childCount(runtime), 1, `Inspector Q${i + 1} did not reuse a single child`)
      const child = syncDelegate.child(runtime, owner, 'Engineer')
      assert.notEqual(child, null)
      if (i === 0) {
        delegateId = child
      } else {
        assert.equal(child, delegateId, 'GetOrCreate must reuse Inspector session')
      }

      lifecycle.notePrompt(delegateId, question)
      // The turn carries a delivery, not only prose: the fenced cell is what
      // the sync reader consumes to settle the call. Without it the turn is
      // "completed without a delivered view" and the obligation stays open.
      runtimeSurface.recordFencedDeliveredView(
        delegateId,
        `asst_q${i + 1}`,
        `call_q${i + 1}`,
        [answer],
      )
      assert.equal(await syncDelegate.settle(runtime, owner, 'Engineer', answer, `asst_q${i + 1}`), true)
      const done = await pending
      assert.equal(done.ok, true, done.ok ? '' : done.error)
      assert.match(String(done.value), new RegExp(answer.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')))
      lifecycle.noteAnswer(delegateId, answer)
    }

    assert.equal(syncDelegate.childCount(runtime), 1, 'createChild once for reusable Inspector')

    lifecycle.collect(delegateId, 'read', { path: 'a.txt' }, 'hello')
    installBookkeeperRuntime(bookkeeperPort.port, [delegateId])
    const first = await lifecycle.tryFinalize(dir, delegateId)
    assert.equal(first.ok, true, `exactly one finalize ok: ${JSON.stringify(first.error)}`)
    assert.equal(bookkeeperPort.createCalls.length, 1, 'exactly one detached Bookkeeper sibling lane')
    assert.equal(bookkeeperPort.programCalls.length >= 1, true, 'todowrite-for-bookkeeper invoked')

    const store = eventStore.create(join(dir, '.Herdchestra'), 'g6-host-fetch')
    const published = await casebook.fetchCase(store, 10, delegateId)
    assert.equal(published.ok, true)
    assert.notEqual(published.value, null, 'Case exists after ReuseScope close')
    assert.equal(published.value.sessionId, delegateId)
    assert.equal(published.value.q, CANONICAL_Q)
    assert.equal(published.value.a, CANONICAL_A)
    assert.equal(published.value.a.includes('evidence:'), false)
    assert.equal(published.value.observations.length, 1)

    lifecycle.cleanup(delegateId)
    const cold = await casebook.fetchCase(store, 10, delegateId)
    assert.equal(cold.ok, true)
    assert.notEqual(cold.value, null, 'cleanup must not delete published Case (cold reuse)')
    assert.equal(cold.value.sessionId, delegateId)

    lifecycle.notePrompt(delegateId, 'second finalize must not publish')
    lifecycle.noteAnswer(delegateId, 'should be refused')
    const second = await lifecycle.tryFinalize(dir, delegateId)
    assert.equal(second.ok, false, 'finalize twice is refused')
    assert.match(String(second.error), /already finalized/)

    const still = await casebook.fetchCase(store, 10, delegateId)
    assert.equal(still.value.sessionId, delegateId, 'original Case retained after refused second finalize')
    assert.equal(syncDelegate.childCount(runtime), 1, 'createChild stays once after scope close')
    eventStore.dispose(store)
  } finally {
    bookkeeper.resetRuntime()
    lifecycle.disable()
    syncDelegate.dispose(runtime)
    rmSync(dir, { recursive: true, force: true })
  }
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { execFileSync } = await import("node:child_process");
const { mkdtempSync, mkdirSync, rmSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join } = await import("node:path");
const eventStore = await import("../../../dist/Persistence/EventStore/Surface.js");
const casebook = await import("../../../dist/Repository/Knowledge/Casebook/Surface.js");
const bookkeeper = await import("../../../dist/Repository/Knowledge/Casebook/BookkeeperSurface.js");
const lifecycle = await import("../../../dist/Repository/Knowledge/Casebook/LifecycleSurface.js");
const { CANONICAL_A, CANONICAL_Q, installBookkeeperRuntime, scriptedBookkeeperPort } = await import("./support/bookkeeper-session-support.mjs");

const sandbox = () => {
  const dir = mkdtempSync(join(tmpdir(), 'wxs-lifecycle-'))
  execFileSync('git', ['init', '--quiet', dir])
  mkdirSync(join(dir, '.wanxiang', 'casebook'), { recursive: true })
  const handle = eventStore.create(join(dir, '.Herdchestra'), 'lifecycle-wiring')
  return {
    dir,
    handle,
    reopen: (writerId) => eventStore.create(join(dir, '.Herdchestra'), writerId),
    cleanup: () => {
      eventStore.dispose(handle)
      rmSync(dir, { recursive: true, force: true })
    },
  }
}

test('WHAT[knowledge-reuse-010] lifecycle_cleanupInspector_never_publishes_case', async () => {
  const { dir, handle, cleanup } = sandbox()
  try {
    lifecycle.enable(dir)
    const key = 'insp-cleanup-1'
    lifecycle.notePrompt(key, 'Q cleanup')
    lifecycle.collect(key, 'read', { path: 'b.txt' }, 'body')
    lifecycle.noteAnswer(key, 'A cleanup')
    assert.ok(lifecycle.observationCount(key) > 0)
    lifecycle.cleanup(key)
    assert.equal(lifecycle.observationCount(key), 0)
    assert.equal((await casebook.fetchCase(handle, 10, key)).value, null)
    assert.equal((await lifecycle.tryFinalize(dir, key)).ok, true)
    assert.equal((await casebook.fetchCase(handle, 10, key)).value, null)
  } finally {
    lifecycle.disable()
    cleanup()
  }
})
test('WHAT[knowledge-reuse-010] lifecycle_missing_answer_is_noop_finalize', async () => {
  const { dir, handle, cleanup } = sandbox()
  try {
    lifecycle.enable(dir)
    const key = 'insp-no-a'
    lifecycle.notePrompt(key, 'Q only')
    assert.equal((await lifecycle.tryFinalize(dir, key)).ok, true)
    assert.equal((await casebook.fetchCase(handle, 10, key)).value, null)
  } finally {
    lifecycle.disable()
    cleanup()
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
const record = (sessionId, q, a, observations) => ({ sessionId, q, a, observations, lastAccessOrder: 0 })

test('WHAT[knowledge-reuse-010] G6_G_universal_loop_archive_finalize_fetch', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'wxs-universal-'))
  const handle = eventStore.create(dir, 'universal-archive')
  try {
    writeFileSync(join(dir, 'a.txt'), 'hello', 'utf8')
    const c1 = record('reuse-scope-1', 'Q1', 'A1', [fileRead('a.txt', casebook.contentHash('hello'))])
    assert.equal((await casebook.archive(handle, c1)).ok, true)
    assert.equal((await casebook.finalize(handle, c1)).ok, false)
    assert.equal((await casebook.fetchCase(handle, 10, 'reuse-scope-1')).value.a, 'A1')
  } finally {
    eventStore.dispose(handle)
    rmSync(dir, { recursive: true, force: true })
  }
})
test('WHAT[knowledge-reuse-010] G6_G_lifecycle_note_finalize_fetch_and_cleanup', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'wxs-universal-life-'))
  try {
    execFileSync('git', ['init', '--quiet', dir])
    mkdirSync(join(dir, '.wanxiang', 'casebook'), { recursive: true })
    writeFileSync(join(dir, 'a.txt'), 'hello', 'utf8')
    lifecycle.enable(dir)
    const { port, createCalls, programCalls } = scriptedBookkeeperPort()
    const key = 'reuse-insp-1'
    installBookkeeperRuntime(port, [key])
    lifecycle.notePrompt(key, 'Who owns PromptAuthority?')
    lifecycle.collect(key, 'read', { path: 'a.txt' }, 'hello')
    const rawA = 'Host owns PromptAuthority.'
    lifecycle.noteAnswer(key, rawA)
    assert.equal((await lifecycle.tryFinalize(dir, key)).ok, true)

    const handle = eventStore.create(join(dir, '.Herdchestra'), 'universal-lifecycle-read')
    const fetched = await casebook.fetchCase(handle, 10, key)
    assert.equal(fetched.value.q, CANONICAL_Q)
    assert.notEqual(fetched.value.a, rawA)
    assert.equal(fetched.value.a, CANONICAL_A)
    assert.equal(createCalls.length, 1)
    assert.equal(programCalls.length >= 1, true)
    assert.equal(fetched.value.observations.length, 1)

    const publishedA = fetched.value.a
    lifecycle.cleanup(key)
    assert.equal((await casebook.fetchCase(handle, 10, key)).value.a, publishedA)
    writeFileSync(join(dir, 'a.txt'), 'drift', 'utf8')
    const refreshed = await bookkeeperRefresh.refreshStale(handle, dir, key)
    assert.equal(refreshed.ok, true)
    assert.equal(refreshed.value, true)
    const after = await casebook.fetchCase(handle, 10, key)
    assert.equal(after.value.q, CANONICAL_Q)
    assert.equal(createCalls.length, 2)
    assert.equal(after.value.observations[0].contentHash, '2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824')
    eventStore.dispose(handle)
  } finally {
    bookkeeper.resetRuntime()
    lifecycle.disable()
    rmSync(dir, { recursive: true, force: true })
  }
})
test('WHAT[knowledge-reuse-010] G6_G_cancel_session_cleanup_no_publication', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'wxs-universal-cancel-'))
  try {
    execFileSync('git', ['init', '--quiet', dir])
    mkdirSync(join(dir, '.wanxiang', 'casebook'), { recursive: true })
    lifecycle.enable(dir)
    const key = 'cancel-insp'
    lifecycle.notePrompt(key, 'Q')
    lifecycle.collect(key, 'read', { path: 'x.txt' }, 'body')
    lifecycle.noteAnswer(key, 'A')
    lifecycle.cleanup(key)
    const handle = eventStore.create(join(dir, '.Herdchestra'), 'universal-cancel-read')
    assert.equal((await casebook.fetchCase(handle, 10, key)).value, null)
    eventStore.dispose(handle)
  } finally {
    lifecycle.disable()
    rmSync(dir, { recursive: true, force: true })
  }
})
}

test.todo('WHAT[knowledge-reuse-010] GAP-160: actual Engineer resume boundaries and Fission takeover each archive exactly one distinct logical invocation, rejecting lane, late and duplicate terminal events')

test('WHAT[knowledge-reuse-010] 宿主真实链回归：PluginLifecycleSurface.takeEngineerDraft 消费单例草稿并返回原生快照数组', async () => {
  const assert = (await import('node:assert/strict')).default
  const { sessionDraftStore } = await import('wanxiangli/casebook')
  const PluginLifecycleSurface = await import('../../../dist/OpenCode/Plugin/PluginLifecycleSurface.js')
  const { sessionId } = await import('../../../dist/Repository/Knowledge/Casebook/BookkeeperSurface.js')
  
  const ownerSessionId = 'test-owner-' + Math.random().toString(36).slice(2)
  const delegateSessionId = 'test-delegate-' + Math.random().toString(36).slice(2)
  
  sessionDraftStore.setQ(delegateSessionId, '宿主测试问题')
  sessionDraftStore.setA(delegateSessionId, '宿主测试回答')
  
  const dummyRuntime = {
    Scope: {
      syncDelegateRuntime: {
        attached: {
          TryFind: () => sessionId(delegateSessionId)
        }
      }
    }
  }
  
  const taken = PluginLifecycleSurface.takeEngineerDraft(dummyRuntime, ownerSessionId)
  assert.ok(taken, 'takeEngineerDraft 成功取出草稿')
  assert.equal(taken.turns.length, 1)
  assert.equal(taken.turns[0].question, '宿主测试问题')
  assert.equal(taken.turns[0].answer, '宿主测试回答')
  
  const secondTaken = PluginLifecycleSurface.takeEngineerDraft(dummyRuntime, ownerSessionId)
  assert.equal(secondTaken, null, '已消费草稿再次调用必须返回 null (Fable None)')
})

test('WHAT[knowledge-reuse-010] 宿主真实链回归：Lifecycle.tryFinalizeDraft 与 cleanupDraft 真实消费同一单例', async () => {
  const assert = (await import('node:assert/strict')).default
  const { sessionDraftStore } = await import('wanxiangli/casebook')
  const Lifecycle = await import('../../../dist/Repository/Knowledge/Casebook/LifecycleSurface.js')
  
  const testSessionId = 'test-finalize-' + Math.random().toString(36).slice(2)
  sessionDraftStore.setQ(testSessionId, '最终定稿问题')
  sessionDraftStore.setA(testSessionId, '最终定稿回答')
  
  const draft = sessionDraftStore.tryTake(testSessionId)
  assert.ok(draft, '成功取出快照')
  assert.equal(sessionDraftStore.lastQuestion(draft), '最终定稿问题')
  assert.equal(sessionDraftStore.lastAnswer(draft), '最终定稿回答')
  
  sessionDraftStore.setQ(testSessionId, '临时问题')
  Lifecycle.cleanup(testSessionId)
  const emptyDraft = sessionDraftStore.tryTake(testSessionId)
  assert.equal(emptyDraft, undefined, 'cleanupDraft 成功清除会话草稿')
  
  const { mkdtempSync, rmSync } = await import('node:fs')
  const { tmpdir } = await import('node:os')
  const { join } = await import('node:path')
  const workspace = mkdtempSync(join(tmpdir(), 'casebook-finalize-'))
  try {
    const res = await Lifecycle.tryFinalize(workspace, testSessionId)
    assert.equal(res.ok, true, '无草稿时安全返回真实结算结果')
  } finally {
    rmSync(workspace, { recursive: true, force: true })
  }
})
