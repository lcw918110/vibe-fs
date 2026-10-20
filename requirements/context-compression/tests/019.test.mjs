import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const contextFold = await import("../../../dist/Context/Companion/FoldSurface.js");

const contextReanchor = (overrides = {}) => ({
  runtime: 'rt_context',
  seq: 1,
  observedAt: '2026-03-04T05:06:07Z',
  id: 'context-event-1',
  session: 'ses_context',
  run: 'run_context',
  fact: {
    family: 'Context',
    case: 'ContextReanchored',
    payload: {
      SessionId: 'ses_context',
      PreviousEpochId: 0,
      NextEpochId: 1,
      ObservedCompactionRun: 'run_context',
    },
  },
  ...overrides,
})

test('WHAT[context-compression-019] Context_fold_accepts_plain_envelopes_and_replays_the_line_codec', () => {
  const folded = contextFold.fold([contextReanchor()])
  assert.equal(folded.ok, true, folded.ok ? '' : JSON.stringify(folded.error))
  assert.equal(folded.value.viewCheckpoints.ses_context, undefined)

  const replayed = contextFold.replay([contextReanchor()])
  assert.equal(replayed.ok, true, replayed.ok ? '' : JSON.stringify(replayed.error))
  assert.deepEqual(replayed.value.sessions, folded.value.sessions)
})
test('WHAT[context-compression-019] Context_fold_rejects_unknown_fact_cases_loudly', () => {
  assert.throws(
    () => contextFold.fold([
      contextReanchor({
        fact: { family: 'Context', case: 'NoSuchContextFact', payload: {} },
      }),
    ]),
    /unknown context fact/i,
  )
})
}

{
const { default: assert } = await import("node:assert/strict");
const { mkdtempSync, mkdirSync, rmSync, writeFileSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join } = await import("node:path");
const { default: test } = await import("node:test");
const pair = await import("../../../dist/OpenCode/Host/PairProgrammingThoughtSurface.js");

const suffixCount = (text) => text.split("\u0000\uFEFF").length - 1
const pairCallIds = (messages) => messages
  .filter((message) => pair.isPairProgrammingThought(message))
  .map((message) => message.parts[0].callID)

test('WHAT[context-compression-019] CTX_019_reanchor_retires_old_pair_wire_but_keeps_history_and_allows_a_fresh_pair', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'wanxiang-pair-reanchor-'))
  const opened = await pair.createJournal(dir)
  assert.equal(opened.ok, true)
  try {
    const session = 'ctx-019-pair'
    const raw = [{
      info: { id: 'r1', role: 'assistant', providerID: 'anthropic' },
      parts: [{ type: 'tool', tool: 'read', callID: 'source', state: { status: 'completed', input: {}, output: 'source\n', time: { start: 0, end: 0 } } }],
    }]
    const first = await pair.tryInjectWithJournal(opened.journal, session, pair.text, raw)
    assert.equal(first.ok, true)
    assert.equal(pairCallIds(first.value).length, 0, 'universal cursor mode emits zero synthetic pair rows')
    assert.equal(suffixCount(first.value[0].parts[0].state.output), 1)
    assert.equal(pair.pairCount(opened.journal, session), 1)

    const reanchored = await pair.appendContextReanchored(opened.journal, session, 0n, 1n, 'compaction-1')
    assert.equal(reanchored.ok, true, reanchored.error)

    const next = await pair.tryInjectWithJournal(opened.journal, session, pair.text, first.value)
    assert.equal(next.ok, true, next.error)
    assert.equal(pairCallIds(next.value).length, 0, 'new Y horizon carries no synthetic pair rows either')
    assert.equal(
      suffixCount(next.value[0].parts[0].state.output),
      1,
      'old pair suffix must be stripped before the fresh horizon suffix is appended',
    )
    assert.equal(pair.pairCount(opened.journal, session), 2, 'durable audit history keeps both occurrences')
  } finally {
    pair.disposeJournal(opened.journal)
    rmSync(dir, { recursive: true, force: true })
  }
})
test('WHAT[context-compression-019] CTX_019_cursor_reanchor_strips_old_pair_suffix_before_adding_the_new_horizon_pair', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'wanxiang-cursor-pair-reanchor-'))
  const opened = await pair.createJournal(dir)
  assert.equal(opened.ok, true)
  try {
    const session = 'ctx-019-cursor-pair'
    const raw = [{
      info: { id: 'r1', role: 'assistant', providerID: 'cursor' },
      parts: [{ type: 'tool', tool: 'read', callID: 'source', state: { status: 'completed', input: {}, output: 'source\n', time: { start: 0, end: 0 } } }],
    }]
    const first = await pair.tryInjectWithJournal(opened.journal, session, pair.text, raw)
    assert.equal(first.ok, true)
    assert.equal(suffixCount(first.value[0].parts[0].state.output), 1)

    const reanchored = await pair.appendContextReanchored(opened.journal, session, 0n, 1n, 'compaction-1')
    assert.equal(reanchored.ok, true, reanchored.error)
    const second = await pair.tryInjectWithJournal(opened.journal, session, pair.text, first.value)
    assert.equal(second.ok, true, second.error)
    assert.equal(
      suffixCount(second.value[0].parts[0].state.output),
      1,
      'old Cursor pair suffix must be removed before the fresh horizon suffix is appended',
    )
    assert.equal(pair.pairCount(opened.journal, session), 2)
  } finally {
    pair.disposeJournal(opened.journal)
    rmSync(dir, { recursive: true, force: true })
  }
})
}
