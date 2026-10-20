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
const guidanceSuffixCount = (messages) =>
  messages
    .map((m) => m.parts[0]?.state?.output ?? m.parts[0]?.state?.error ?? '')
    .filter((value) => typeof value === 'string')
    .map((value) => (value.match(/\0/g) ?? []).length)
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

test('WHAT[prefix-stability-014] PREFIX_STABILITY_pair_body_stays_out_of_the_trace_projections', () => {
  const session = 'h13-014'
  const markerBody = 'SECRET synthetic marker body'
  const result = pair.foldAnchoredPair({
    session,
    ordinal: 1,
    callId: stableCallId(session, 1n),
    markerText: markerBody,
    callGapAfter: 'msg_7',
    resultGapAfter: 'msg_7',
  })
  assert.equal(result.ok, true, result.ok ? '' : JSON.stringify(result))

  const flags = result.flags
  // The only projection the pair fact may grow is Guidelines (HOST-013 recovery).
  assert.equal(flags.guidelines, true, 'the anchored fact must land in Guidelines')

  // This fact fold does not create trace state. Message ingestion is
  // a separate path and is not proved by these flags.
  assert.equal(flags.xTrace, false, 'XTrace must not be created by the pair fact')
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
const guidanceSuffix = (markerText) => `\0\uFEFF${markerText}`
const terminalOutputOf = (messages, id) => messages.find((m) => m.info.id === id).parts[0].state.output

test('WHAT[prefix-stability-014] PPT_source_is_the_frozen_side_channel_identity', () => {
  assert.equal(source, 'pair-programming-auto-injected')
  assert.ok(text.length > 0, 'frozen thought text must be non-empty')
  assert.equal(isPairProgrammingThought(null), false)
  assert.equal(isPairProgrammingThought({}), false)
  assert.equal(isPairProgrammingThought({ info: { source: 'other' } }), false)
  assert.equal(isPairProgrammingThought({ info: { source } }), true)
  assert.equal(isPairProgrammingThought({ parts: [] }), false, 'no info.source means not a marker')
})
test('WHAT[prefix-stability-014] PPT_tryInject_user_quoting_the_thought_text_is_not_a_marker', async () => {
  const raw = [userMsg('u1'), assistantText('a1'), userMsg('msg_1', text)]
  const out = await inject('ses_quote', raw)
  assert.equal(isPairProgrammingThought(out[2]), false, 'matching text alone must not classify as marker')
  assert.equal(out.length, 3)
  assert.equal(out[2].info.role, 'user')
})
}

{
const assert = (await import('node:assert/strict')).default
const pair = await import('../../../dist/OpenCode/Host/PairProgrammingThoughtSurface.js')
const trace = await import('../../../dist/Context/Trace/SemanticTraceSurface.js')
const { withReopenableJournal } = await import('../../semantic-trace/tests/support/journal.mjs')

test('WHAT[prefix-stability-014] injected wire fed directly to durable capture excludes guidance and preserves ordinary quotation', { todo: 'GAP-108: direct capture retains suffix bytes; verify and close the actual Host admission path without teaching semantic capture to guess prose' }, async () => {
  const guidance = 'a unique guidance sentence'
  const raw = [{
    info: { id: 'tool-message', role: 'assistant' },
    parts: [{ id: 'tool-part', type: 'tool', tool: 'read', callID: 'call-1',
      state: { status: 'completed', input: {}, output: 'file content\r\n', time: { start: 0, end: 1 } } }],
  }]
  const injected = await pair.tryInject('ses-trace-guidance', guidance, raw)
  assert.equal(injected.ok, true)
  assert.equal(injected.value[0].parts[0].state.output, `file content\r\n\0\uFEFF${guidance}`)
  const quoted = {
    info: { id: 'user-quote', role: 'user' },
    parts: [{ id: 'quote-part', type: 'text', text: guidance }],
  }
  await withReopenableJournal(async (journal, reopen) => {
    const captured = await trace.captureObservedMessages(journal, 'ses-trace-guidance', [...injected.value, quoted].map(message => ({
      hostMessageId: message.info.id, message,
    })))
    assert.equal(captured.ok, true)
    const recovered = await trace.currentProjection(await reopen(), 'ses-trace-guidance')
    const parts = recovered.messages.flatMap(message => message.parts)
    assert.deepEqual(parts.filter(part => part.kind === 'tool-result'), [{ kind: 'tool-result', result: 'file content\r\n' }])
    assert.deepEqual(parts.filter(part => part.kind === 'text'), [{ kind: 'text', text: guidance }])
  })
})
}
