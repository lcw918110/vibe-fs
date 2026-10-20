import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { mkdtempSync, rmSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join } = await import("node:path");
const { default: test } = await import("node:test");
const pair = await import("../../../dist/OpenCode/Host/PairProgrammingThoughtSurface.js");
const providerCodec = await import("../../../dist/OpenCode/Codec/ProviderProjectionSurface.js");
const providerProjection = await import("../../../dist/Participant/Provider/Projection/Surface.js");

const {
  tryInjectWithJournal,
  isPairProgrammingThought,
  source,
  text,
  stableCallId,
} = pair
const userMsg = (id, body = 'hello') => ({
  info: { id, role: 'user' },
  parts: [{ type: 'text', text: body }],
})
const assistantText = (id) => ({
  info: { id, role: 'assistant' },
  parts: [{ type: 'text', text: 'ok' }],
})
const toolCall = (id, tool, callID) => ({
  info: { id, role: 'assistant' },
  parts: [{
    type: 'tool',
    tool,
    callID,
    state: { status: 'pending', input: {}, time: { start: 0 } },
  }],
})
const toolResult = (id, tool, callID, output = 'ok') => ({
  info: { id, role: 'assistant' },
  parts: [{
    type: 'tool',
    tool,
    callID,
    state: { status: 'completed', input: {}, output, time: { start: 0, end: 0 } },
  }],
})
const pairMessages = (messages) => messages.filter((m) => isPairProgrammingThought(m))
const inject = async (journal, session, raw, markerText = text) => {
  const result = await tryInjectWithJournal(journal, session, markerText, raw)
  assert.equal(result.ok, true, `HOST-013 transform must commit the pair: ${result.error ?? ''}`)
  return result.value
}
const wire = (raw) => providerCodec.decodeMessageView(raw)
const assertPrefixLaw = (previous, next, label) => {
  assert.equal(
    providerProjection.isAppendOnlyPrefix(wire(previous), wire(next)),
    true,
    `${label}: previous wire must be an exact prefix of next wire`,
  )
}
const assertWireEqual = (a, b, label) => {
  assert.equal(
    providerProjection.isAppendOnlyPrefix(wire(a), wire(b)),
    true,
    `${label}: wire(a) must be a prefix of wire(b)`,
  )
  assert.equal(
    providerProjection.isAppendOnlyPrefix(wire(b), wire(a)),
    true,
    `${label}: wire(b) must be a prefix of wire(a)`,
  )
}
const toolNames = (messages) => messages.map((m) => m.parts[0]?.tool)
const userCarrier = (body, markerText = text) => `${body}\0\uFEFF<system>\n${markerText.trim()}\n</system>`
const openJournal = async (dir) => {
  const opened = await pair.createJournal(dir)
  assert.equal(opened.ok, true, JSON.stringify(opened))
  return opened
}
const durablePairCount = (journal, session) => pair.pairCount(journal, session)
const mulberry32 = (seed) => {
  let a = seed >>> 0
  return () => {
    a |= 0
    a = (a + 0x6d2b79f5) | 0
    let t = Math.imul(a ^ (a >>> 15), 1 | a)
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296
  }
}

test('WHAT[prefix-stability-010] H13_02_historical_pair_never_relocates_to_current_batch', async () => {
  const session = 'h13-02'

  const round1 = [toolCall('c1', 'bash', 't1'), toolResult('r1', 'bash', 't1')]
  const wire1 = await inject(undefined, session, round1)
  // Req1 Resp1+suffix — no synthetic row follows the batch.
  assert.deepEqual(toolNames(wire1), ['bash', 'bash'])
  assert.equal(pairMessages(wire1).length, 0)
  assert.equal(wire1[1].parts[0].state.output, `ok\0\uFEFF${text}`)

  const round2 = [...wire1, toolCall('c2', 'read', 't2'), toolResult('r2', 'read', 't2')]
  const wire2 = await inject(undefined, session, round2)
  // Req1 Resp1+suffix Req2 Resp2+suffix
  // A historyBlock implementation would rewrite pair1's bytes next to the
  // current batch; instead the historical suffix is frozen in place.
  assert.deepEqual(toolNames(wire2), [
    'bash', 'bash',
    'read', 'read',
  ])
  assert.equal(pairMessages(wire2).length, 0)
  assert.equal(wire2[1].parts[0].state.output, `ok\0\uFEFF${text}`)
  assert.equal(wire2[3].parts[0].state.output, `ok\0\uFEFF${text}`)
  assertPrefixLaw(wire1, wire2, 'H13-02 no historical relocation')
})
test('WHAT[prefix-stability-010] H13_02b_durable_history_replays_the_current_skill_wire_only', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'wxs-h1302b-'))
  const opened = await openJournal(dir)
  try {
    const session = 'h13-02b'
    const appended = await pair.appendAnchoredPair(opened.journal, {
      session,
      ordinal: 1n,
      callId: 'pair-call',
      markerText: pair.text,
      callGapAfter: { kind: 'start' },
      resultGapAfter: { kind: 'start' },
    })
    assert.equal(appended.ok, true, JSON.stringify(appended))

    // Universal cursor mode: the durable fact persists journal-side, but with
    // no terminal real tool result there is no guidance carrier to render —
    // zero synthetic rows, even with history present.
    const wire = await inject(opened.journal, session, [])
    assert.deepEqual(wire, [])
    assert.equal(pairMessages(wire).length, 0)
    assert.equal(durablePairCount(opened.journal, session), 1)

    // The same durable history guides the next real terminal result instead.
    const guided = await inject(opened.journal, session, [
      toolCall('c1', 'bash', 't1'),
      toolResult('r1', 'bash', 't1', 'out1'),
    ])
    assert.deepEqual(toolNames(guided), ['bash', 'bash'])
    assert.equal(guided[1].parts[0].state.output, `out1\0\uFEFF${pair.text}`)
  } finally {
    pair.disposeJournal(opened.journal)
    rmSync(dir, { recursive: true, force: true })
  }
})
test('WHAT[prefix-stability-010] H13_03_same_placement_reentry_appends_no_pair', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'wxs-h1303-'))
  const opened = await openJournal(dir)
  try {
    const session = 'h13-03'
    const raw = [userMsg('u1'), assistantText('a1'), userMsg('msg_1')]

    const once = await inject(opened.journal, session, raw)
    assert.equal(once.length, 3)
    // The newest real message hosts the guidance; nothing synthetic is minted.
    assert.equal(once[0].parts[0].text, 'hello')
    assert.equal(once[1].parts[0].text, 'ok')
    assert.equal(once[2].parts[0].text, userCarrier('hello'))
    assert.equal(pairMessages(once).length, 0)
    assert.equal(durablePairCount(opened.journal, session), 1)

    const twice = await inject(opened.journal, session, [...once])
    assert.equal(twice.length, 3, 'same placement must replay, not append')
    assert.deepEqual(twice, once)
    assert.equal(durablePairCount(opened.journal, session), 1, 'journal must hold exactly one anchored fact')
  } finally {
    pair.disposeJournal(opened.journal)
    rmSync(dir, { recursive: true, force: true })
  }
})
test('WHAT[prefix-stability-010] same-process journal reopen preserves guidance bytes', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'wxs-h1304-'))
  const session = 'h13-04'
  const raw = [
    toolCall('c1', 'bash', 't1'),
    toolResult('r1', 'bash', 't1'),
    userMsg('u1', 'steer'),
  ]

  const before = await openJournal(dir)
  let wireBefore
  try {
    wireBefore = await inject(before.journal, session, raw)
  } finally {
    pair.disposeJournal(before.journal)
  }

  // Reopen the same directory with a fresh writer in this process.
  const after = await openJournal(dir)
  let wireAfter
  try {
    wireAfter = await inject(after.journal, session, [...wireBefore])
    assertWireEqual(wireBefore, wireAfter, 'H13-04 restart replay')
    assert.equal(durablePairCount(after.journal, session), 1, 'restart re-entry must not append a second fact')
  } finally {
    pair.disposeJournal(after.journal)
  }

  rmSync(dir, { recursive: true, force: true })
})
test('WHAT[prefix-stability-010] H13_05_missing_anchor_pair_is_omitted_not_relocated', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'wxs-h1305-'))
  const opened = await openJournal(dir)
  try {
    const session = 'h13-05'

    const appended = await pair.appendAnchoredPair(opened.journal, {
      session,
      ordinal: 1,
      callId: stableCallId(session, 1n),
      markerText: text,
      callGapAfter: 'msg_7',
      resultGapAfter: 'msg_7',
    })
    assert.equal(appended.ok, true, JSON.stringify(appended))

    // Rewritten view: covered u1/msg_7 gone; only a synthetic prefix + continue.
    const synthPrefix = {
      info: { id: 'synth-prefix-frozen', role: 'user' },
      parts: [{ type: 'text', text: '# Opening\ncovered work' }],
    }
    const cont = userMsg('u-continue', '# The previous attempt did not complete.')
    const result = await tryInjectWithJournal(opened.journal, session, text, [synthPrefix, cont])
    assert.equal(result.ok, true, `missing-anchor pair must omit, not fail: ${result.error ?? ''}`)
    const wire = result.value
    // Pair1 (after msg_7) must not reappear anywhere — no relocate.
    const call1 = stableCallId(session, 1n)
    assert.equal(
      wire.some((m) => m.parts?.[0]?.callID === call1),
      false,
      'unplaceable historical pair must not be relocated onto the rewritten view',
    )
    // Historical fact remains; a new pair may land on the rewritten trailing placement.
    const durable = durablePairCount(opened.journal, session)
    assert.ok(durable === 1 || durable === 2, `durable pairs 1..2, got ${durable}`)
    const pairs = pairMessages(wire)
    assert.ok(pairs.length === 0 || pairs.length === 1, 'at most one new pair on the rewritten view')
    assert.equal(
      pairs.some((m) => m.parts?.[0]?.callID === call1),
      false,
      'new wire pairs must not reuse the unplaceable historical callId',
    )
  } finally {
    pair.disposeJournal(opened.journal)
    rmSync(dir, { recursive: true, force: true })
  }
})
test('WHAT[prefix-stability-010] H13_05b_xwire_drop_leading_continue_still_commits', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'wxs-h1305b-'))
  const opened = await openJournal(dir)
  try {
    const session = 'h13-05b'
    const user0 = userMsg('u0', 'opening')
    const asst0 = assistantText('a0')
    const user1 = userMsg('u1', 'X-B round 1')
    const failAsst = { info: { id: 'a1', role: 'assistant' }, parts: [] }
    const cont = userMsg('u2', '# The previous attempt did not complete.')
    const synthPrefix = {
      info: { id: 'synth-prefix-frozen', role: 'user' },
      parts: [{ type: 'text', text: '# Opening\nX-B round 1\n\n# History\nframe' }],
    }

    const wire1 = await inject(opened.journal, session, [user0, asst0, user1])
    assert.equal(durablePairCount(opened.journal, session), 1)
    assert.equal(pairMessages(wire1).length, 0)
    assert.equal(wire1[2].parts[0].text, userCarrier('X-B round 1'))

    // DropLeading removes u1 (pair1's After(u1) anchor). Its occurrence is
    // unplaceable and must not reappear; the rewritten trailing user is a new
    // carrier, so a second occurrence commits there.
    const result = await tryInjectWithJournal(opened.journal, session, text, [synthPrefix, failAsst, cont])
    assert.equal(result.ok, true, `XWire continue must not fail closed: ${result.error ?? ''}`)
    const wire2 = result.value
    const call1 = stableCallId(session, 1n)
    assert.equal(
      wire2.some((m) => m.parts?.[0]?.callID === call1),
      false,
      'pair1 anchors dropped with covered prefix — must not reappear',
    )
    assert.equal(pairMessages(wire2).length, 0)
    const countAfterContinue = durablePairCount(opened.journal, session)
    assert.equal(countAfterContinue, 2, 'the rewritten trailing user gets its own occurrence')
    // Full transcript (no drop) replays both durable occurrences without
    // appending a third; pure u1 re-entry is byte-identical to wire1.
    const restored = await inject(opened.journal, session, [user0, asst0, user1, failAsst, cont])
    assert.equal(pairMessages(restored).length, 0)
    assert.equal(restored[2].parts[0].text, userCarrier('X-B round 1'))
    assert.equal(restored[4].parts[0].text, userCarrier('# The previous attempt did not complete.'))
    assert.equal(durablePairCount(opened.journal, session), countAfterContinue, 're-entry must not append a second fact')
    assertWireEqual(wire1, await inject(opened.journal, session, [user0, asst0, user1]), 'H13-05b same placement on u1 is pure replay')
  } finally {
    pair.disposeJournal(opened.journal)
    rmSync(dir, { recursive: true, force: true })
  }
})
test('WHAT[prefix-stability-010] H13_06_prior_tip_only_affects_the_new_pair', async () => {
  const session = 'h13-06'

  const wire1 = await inject(
    undefined,
    session,
    [toolCall('c1', 'bash', 't1'), toolResult('r1', 'bash', 't1', 'out1')],
    'guideline',
  )
  assert.equal(pairMessages(wire1).length, 0)
  assert.equal(wire1[1].parts[0].state.output, `out1\0\uFEFFguideline`)

  const wire2 = await inject(
    undefined,
    session,
    [...wire1, toolCall('c2', 'bash', 't2'), toolResult('r2', 'bash', 't2', 'out2')],
    'tip2\n\nguideline',
  )
  assert.equal(pairMessages(wire2).length, 0)
  assert.equal(wire2[1].parts[0].state.output, `out1\0\uFEFFguideline`, 'historical marker bytes must never change')
  assert.equal(wire2[3].parts[0].state.output, `out2\0\uFEFFtip2\n\nguideline`)

  assertPrefixLaw(wire1, wire2, 'H13-06 prior tip isolation')
})
test('WHAT[prefix-stability-010] PPT_newest_user_message_keeps_receiving_the_current_occurrence', async () => {
  // Regression: the placement must always name a message the cursor renderer
  // can actually change. A pure user continuation has no terminal tool result,
  // so the newest user message is the carrier -- exactly like the first round.
  // The defect was a `Before(user)` / `After(assistant text)` placement that
  // the renderer could not honour, leaving the occurrence committed but silent.
  const dir = mkdtempSync(join(tmpdir(), 'wxs-h13-user-carrier-'))
  const opened = await openJournal(dir)
  try {
    const session = 'ses_user_carrier'
    const first = await inject(opened.journal, session, [userMsg('u1', 'go')])
    assert.equal(first[0].parts[0].text, userCarrier('go'))

    const second = await inject(opened.journal, session, [
      ...first,
      assistantText('a1'),
      userMsg('u2', 'steer'),
    ])
    assert.equal(second[0].parts[0].text, userCarrier('go'), 'history bytes are never rewritten')
    assert.equal(second[2].parts[0].text, userCarrier('steer'), 'the newest user message carries the new occurrence')

    const third = await inject(opened.journal, session, [
      ...second,
      assistantText('a2'),
      userMsg('u3', 'again'),
    ])
    assert.equal(third[4].parts[0].text, userCarrier('again'))
    assert.equal(durablePairCount(opened.journal, session), 3, 'one occurrence per carrier round')
  } finally {
    pair.disposeJournal(opened.journal)
    rmSync(dir, { recursive: true, force: true })
  }
})
test('WHAT[prefix-stability-010] PPT_carrier_only_commits_when_the_newest_message_hosts_it', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'wxs-h13-no-carrier-'))
  const opened = await openJournal(dir)
  try {
    const session = 'ses_no_carrier'
    const first = await inject(opened.journal, session, [userMsg('u1', 'go')])
    assert.equal(durablePairCount(opened.journal, session), 1)

    // Newest message is a plain assistant text: no carrier, so no new
    // occurrence -- and, crucially, no silent durable fact either.
    const second = await inject(opened.journal, session, [...first, assistantText('a1')])
    assert.deepEqual(second, [...first, assistantText('a1')])
    assert.equal(durablePairCount(opened.journal, session), 1, 'no carrier means no occurrence')

    const third = await inject(opened.journal, session, [...second, userMsg('u2', 'steer')])
    assert.equal(third[2].parts[0].text, userCarrier('steer'))
    assert.equal(durablePairCount(opened.journal, session), 2)
  } finally {
    pair.disposeJournal(opened.journal)
    rmSync(dir, { recursive: true, force: true })
  }
})
test('WHAT[prefix-stability-010] PPT_reanchor_retires_old_visibility_and_new_occurrence_lands_on_the_new_carrier', async () => {
  // Regression: after ContextReanchored the previous occurrence is retired. A
  // new request must still be able to place a fresh occurrence on the newest
  // carrier; the old window where the wire carried zero guidance bytes is gone.
  const dir = mkdtempSync(join(tmpdir(), 'wxs-h13-reanchor-'))
  const opened = await openJournal(dir)
  try {
    const session = 'ses_reanchor_carrier'
    const first = await inject(opened.journal, session, [userMsg('u1', 'go'), assistantText('a1'), userMsg('u2', 'work')])
    assert.equal(first[2].parts[0].text, userCarrier('work'))

    await pair.appendContextReanchored(opened.journal, session, 0n, 1n, 'run-a')

    const second = await inject(opened.journal, session, [
      ...first,
      assistantText('a2'),
      userMsg('u3', 'next'),
    ])
    assert.equal(second[4].parts[0].text, userCarrier('next'), 'the new occurrence lands on the new carrier')
    assert.equal(durablePairCount(opened.journal, session), 2)
  } finally {
    pair.disposeJournal(opened.journal)
    rmSync(dir, { recursive: true, force: true })
  }
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const pair = await import("../../../dist/OpenCode/Host/PairProgrammingThoughtSurface.js");

const {
  tryInject,
  isPairProgrammingThought,
  skipAutoInjectedRequested,
  source,
  text,
  stableCallId,
} = pair
const inject = async (session, raw, markerText = text) => {
  const result = await tryInject(session, markerText, raw)
  assert.equal(result.ok, true, `HOST-013 transform must commit the pair: ${result.error ?? ''}`)
  return result.value
}
const userMsg = (id, body = 'hello') => ({
  info: { id, role: 'user' },
  parts: [{ type: 'text', text: body }],
})
const assistantText = (id) => ({
  info: { id, role: 'assistant' },
  parts: [{ type: 'text', text: 'ok' }],
})
const toolCall = (id, tool, callID) => ({
  info: { id, role: 'assistant' },
  parts: [{
    type: 'tool',
    tool,
    callID,
    state: { status: 'pending', input: {}, time: { start: 0 } },
  }],
})
const toolResult = (id, tool, callID, output = 'ok') => ({
  info: { id, role: 'assistant' },
  parts: [{
    type: 'tool',
    tool,
    callID,
    state: { status: 'completed', input: {}, output, time: { start: 0, end: 0 } },
  }],
})
const pairMessages = (messages) => messages.filter((m) => isPairProgrammingThought(m))
const terminalOutputOf = (messages, id) => messages.find((m) => m.info.id === id).parts[0].state.output
// User messages wrap the marker in the `<system>` envelope (HOST-013); tool
// results carry the raw suffix.
const userCarrier = (body, markerText = text) => `${body}\0\uFEFF<system>\n${markerText.trim()}\n</system>`

test('WHAT[prefix-stability-010] PPT_tryInject_empty_history_does_not_inject_pair', async () => {
  const out = await inject('ses_empty', [])
  assert.equal(out.length, 0, 'empty history must not inject pair')
})
test('WHAT[prefix-stability-010] PPT_tryInject_single_user_message_does_not_inject_pair_to_prevent_tool_start', async () => {
  const raw = [userMsg('msg_1')]
  const out = await inject('ses_1', raw)
  assert.ok(out)
  assert.equal(out.length, 1)
  assert.equal(pairMessages(out).length, 0)
  assert.equal(out[0].parts[0].text, `hello\0\uFEFF<system>\n${text.trim()}\n</system>`)
  assert.doesNotMatch(out[0].parts[0].text, /name=/)
  const replay = await inject('ses_1', out)
  assert.deepEqual(replay, out)

  const subsequent = await inject('ses_1', [out[0], assistantText('a1'), userMsg('u2', 'steer')])
  assert.equal(subsequent.length, 3)
  assert.equal(
    subsequent[2].parts[0].text,
    userCarrier('steer'),
    'a new occurrence lands on the newest user message like the first round',
  )
})
test('WHAT[prefix-stability-010] PPT_tryInject_places_pair_before_trailing_user_with_prior_assistant', async () => {
  const raw = [userMsg('u1'), assistantText('a1'), userMsg('u2', 'steer')]
  const out = await inject('ses_assistant', raw)
  assert.ok(out)
  // The trailing user message is the carrier: the guidance rides inside the
  // same `<system>` wrapper as the first round, and no synthetic message is
  // inserted before it.
  assert.equal(out.length, 3)
  assert.equal(out[0].parts[0].text, 'hello')
  assert.equal(out[1].parts[0].text, 'ok')
  assert.equal(out[2].parts[0].text, userCarrier('steer'))
  assert.equal(pairMessages(out).length, 0)
})
test('WHAT[prefix-stability-010] PPT_tryInject_merges_into_tool_batches_before_user', async () => {
  const raw = [
    toolCall('c1', 'bash', 't1'),
    toolCall('c2', 'read', 't2'),
    toolResult('r1', 'bash', 't1', 'out1'),
    toolResult('r2', 'read', 't2', 'out2'),
    userMsg('u1', 'steer'),
  ]
  const out = await inject('ses_tools', raw)
  assert.ok(out)
  // The newest real message (the steer user) is the carrier; the batch rows are
  // untouched.
  assert.equal(out.length, 5)
  assert.equal(pairMessages(out).length, 0)

  assert.equal(out[0].parts[0].tool, 'bash')
  assert.equal(out[1].parts[0].tool, 'read')
  assert.equal(out[2].parts[0].tool, 'bash')
  assert.equal(out[2].parts[0].state.status, 'completed')
  assert.equal(out[2].parts[0].state.output, 'out1')
  assert.equal(out[3].parts[0].tool, 'read')
  assert.equal(out[3].parts[0].state.status, 'completed')
  assert.equal(out[3].parts[0].state.output, 'out2')
  assert.equal(
    out[4].parts[0].text,
    userCarrier('steer'),
    'steer user carries the guidance as the newest row',
  )

  // Re-feeding the wire strips the suffix for placement, then re-applies it:
  // replay is byte-identical, never a doubled suffix.
  const replay = await inject('ses_tools', out)
  assert.deepEqual(replay, out)
  assert.equal(replay[4].parts[0].text, out[4].parts[0].text)
})
test('WHAT[prefix-stability-010] PPT_tryInject_second_pass_of_same_placement_replays_existing_pair', async () => {
  const initial = [userMsg('u1'), assistantText('a1'), userMsg('u2')]
  const once = await inject('ses_append', initial)
  assert.ok(once)
  assert.equal(once.length, 3)
  assert.equal(pairMessages(once).length, 0)

  // Same real transcript again: the same placement replays byte-identically.
  const twice = await inject('ses_append', once)
  assert.ok(twice)
  assert.equal(twice.length, 3)
  assert.deepEqual(twice, once, 'replay must be byte-identical')
})
test('WHAT[prefix-stability-010] PPT_skip_auto_injected_env_blocks_new_pair_but_replays_history', async () => {
  const previous = process.env.WANXIANGSHU_SKIP_AUTO_INJECTED
  try {
    delete process.env.WANXIANGSHU_SKIP_AUTO_INJECTED
    assert.equal(skipAutoInjectedRequested(undefined), false)

    const session = 'ses_skip_env'
    const seeded = await inject(session, [
      toolCall('msg_c0', 'bash', 'call_0'),
      toolResult('msg_r0', 'bash', 'call_0', 'out0'),
      userMsg('msg_u1'),
    ])
    assert.equal(pairMessages(seeded).length, 0)
    // The newest message (msg_u1) carries the guidance, not the older tool result.
    assert.equal(terminalOutputOf(seeded, 'msg_r0'), 'out0')
    assert.equal(seeded[2].parts[0].text, userCarrier('hello'))

    process.env.WANXIANGSHU_SKIP_AUTO_INJECTED = '1'
    assert.equal(skipAutoInjectedRequested(undefined), true)

    // Historical guidance bytes replay untouched; the new turn gets no fresh
    // occurrence while the env gate is set.
    const replay = await inject(session, [...seeded])
    assert.deepEqual(replay, seeded, 'history replays byte-identical under the skip gate')

    const raw = [
      ...seeded,
      toolCall('msg_c1', 'bash', 'call_1'),
      toolResult('msg_r1', 'bash', 'call_1', 'out1'),
      userMsg('msg_u2'),
    ]
    const out = await inject(session, raw)
    assert.equal(pairMessages(out).length, 0)
    assert.equal(out.length, 6)
    assert.equal(terminalOutputOf(out, 'msg_r0'), 'out0')
    assert.equal(terminalOutputOf(out, 'msg_r1'), 'out1')
    assert.equal(out[5].parts[0].text, 'hello', 'the gate suppresses the fresh occurrence on the newest user message')
  } finally {
    if (previous === undefined) delete process.env.WANXIANGSHU_SKIP_AUTO_INJECTED
    else process.env.WANXIANGSHU_SKIP_AUTO_INJECTED = previous
  }
})
test('WHAT[prefix-stability-010] PPT_skip_auto_injected_env_keeps_empty_transcript_without_pair', async () => {
  const previous = process.env.WANXIANGSHU_SKIP_AUTO_INJECTED
  try {
    process.env.WANXIANGSHU_SKIP_AUTO_INJECTED = '1'
    const out = await inject('ses_skip_empty', [])
    assert.equal(out.length, 0)
    assert.equal(pairMessages(out).length, 0)
  } finally {
    if (previous === undefined) delete process.env.WANXIANGSHU_SKIP_AUTO_INJECTED
    else process.env.WANXIANGSHU_SKIP_AUTO_INJECTED = previous
  }
})
test('WHAT[prefix-stability-010] C_PH_ordinary_cursor_ordinary_suppresses_then_restores_same_occurrence', async () => {
  const session = 'ses_cursor_transition'
  const initial = [userMsg('u1'), assistantText('a1'), userMsg('u2')]
  const ordinary = await inject(session, initial)
  // Universal cursor mode: no synthetic messages; the occurrence rides the
  // newest user message, identically for ordinary and cursor providers.
  assert.equal(pairMessages(ordinary).length, 0)
  assert.equal(ordinary[0].parts[0].text, 'hello')
  assert.equal(ordinary[1].parts[0].text, 'ok')
  assert.equal(ordinary[2].parts[0].text, userCarrier('hello'))

  const cursorReal = [
    { info: { id: 'u1', role: 'user', model: { providerID: 'cursor', modelID: 'composer' } }, parts: [{ type: 'text', text: 'hello' }] },
    assistantText('a1'),
    userMsg('u2'),
  ]
  const cursor = await inject(session, cursorReal)
  assert.equal(pairMessages(cursor).length, 0)
  assert.equal(cursor[2].parts[0].text, userCarrier('hello'))

  const back = await inject(session, initial)
  assert.equal(pairMessages(back).length, 0)
  assert.deepEqual(back, ordinary, 'same occurrence restores the identical wire')
})
test('WHAT[prefix-stability-010] PPT_distiller_never_injects_pair_hint', async () => {
  const distillerMsg = [
    userMsg('u3'),
    assistantText('a3'),
    { info: { id: 'u4', role: 'user', agent: 'distiller' }, parts: [{ type: 'text', text: 'distill task' }] },
  ]
  const distillerOut = await inject('ses_distiller', distillerMsg)
  assert.equal(pairMessages(distillerOut).length, 0)
  assert.deepEqual(distillerOut, distillerMsg)
})
test('WHAT[prefix-stability-010] PPT_tryInject_call_id_is_stable_per_session_and_ordinal', () => {
  assert.equal(stableCallId('ses_1', 1n), stableCallId('ses_1', 1n))
  assert.notEqual(stableCallId('ses_1', 1n), stableCallId('ses_1', 2n))
  assert.notEqual(stableCallId('ses_1', 1n), stableCallId('ses_2', 1n))
})
test('WHAT[prefix-stability-010] PPT_tryInject_without_session_id_still_appends_stable_pair', async () => {
  // Without a session id the guidance still lands deterministically on the
  // terminal real tool result; a session-less trailing-user turn passes through.
  const raw = [toolCall('c1', 'bash', 't1'), toolResult('r1', 'bash', 't1', 'out1')]
  const out = await inject(undefined, raw)
  assert.ok(out)
  assert.equal(out.length, 2)
  assert.equal(pairMessages(out).length, 0)
  assert.equal(out[1].parts[0].state.output, `out1\0\uFEFF${text}`)
  assert.deepEqual(await inject(undefined, out), out, 'session-less replay stays byte-identical')
})

}
