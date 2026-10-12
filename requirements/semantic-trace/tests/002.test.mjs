import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { readFileSync } = await import("node:fs");
const { default: test } = await import("node:test");
const trace = await import("../../../dist/Context/Trace/SemanticTraceSurface.js");

const unwrap = (result) => {
  assert.equal(result.ok, true, result.ok ? '' : result.error)
  return result.projection
}
const partDescriptor = {
  sequence: 1,
  role: 'assistant',
  provenance: 'g:0/msg:message-1/host-part:part-1',
  turn: 0,
  partIndex: 0,
  kind: 'tool_call',
  toolName: 'read',
  textRef: 'blob-1',
  textDigest: 'digest-1',
  providerRun: 'provider-run-1',
  toolCallId: 'call-1',
  hostToolPartId: 'part-1',
}

test('WHAT[semantic-trace-002] copied semantic evidence excludes transport metadata', () => {
  const projection = unwrap(trace.appendPart(trace.emptyProjection(), partDescriptor))
  const evidence = trace.orderedSemanticParts(projection)[0]
  assert.equal(evidence.providerRun, 'provider-run-1')
  assert.equal(evidence.toolName, 'read')
  for (const forbidden of ['usage', 'cost', 'timestamp', 'elapsed', 'directory', 'finishReason', 'runtimeId', 'tokens', 'uiDelta']) {
    assert.equal(Object.hasOwn(evidence, forbidden), false, `semantic evidence must not carry ${forbidden}`)
  }
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { mkdtempSync, readFileSync, rmSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join, resolve } = await import("node:path");
const journal = await import("../../../dist/Persistence/Journal/Surface.js");
const trace = await import("../../../dist/Context/Trace/SemanticTraceSurface.js");

const SESSION = 'ses_semantic_capture'
const withJournal = async (fn) => {
  const dir = mkdtempSync(join(tmpdir(), 'semantic-trace-'))
  const created = await journal.JournalSurface_boot(dir, 'rt_semantic_trace', 4242, '2026-01-01T00:00:00Z')
  assert.equal(created.ok, true, created.ok ? '' : JSON.stringify(created.error))
  try {
    return await fn(created.journal)
  } finally {
    journal.JournalSurface_dispose(created.journal)
    rmSync(dir, { recursive: true, force: true })
  }
}
const projection = (messages) => ({ messages })

test('WHAT[semantic-trace-002] typed retry observation retains stable identity but appends no semantics', async () => {
  await withJournal(async (handle) => {
    const captured = await trace.captureObservedMessages(handle, SESSION, [
      {
        hostMessageId: 'retry-message',
        origin: 'ProviderRetryAttempt',
        message: { info: { id: 'retry-run', role: 'user' }, parts: [{ id: 'retry-part', type: 'text', text: 'transport retry' }] },
      },
      {
        hostMessageId: 'answer-message',
        message: { info: { id: 'answer-run', role: 'assistant' }, parts: [{ id: 'answer-part', type: 'text', text: 'semantic answer' }] },
      },
    ])
    assert.equal(captured.ok, true, captured.ok ? '' : captured.error)
    assert.equal(captured.receipt.identity, 'stable-host')
    assert.equal(captured.receipt.capturedPartCount, 1)
    assert.deepEqual(trace.orderedSemanticParts(captured.projection).map((part) => part.provenance), [
      'g:0/msg:answer-message/host-part:answer-part',
    ])
    assert.deepEqual(
      trace.orderedSemanticParts(captured.projection),
      trace.orderedSemanticParts(trace.snapshot(handle, SESSION)),
      'the returned opaque current projection is the resulting owner state',
    )
  })
})
test('WHAT[semantic-trace-002] retry-like ordinary text is preserved and metadata does not become semantic material', async () => {
  await withJournal(async (handle) => {
    const body = '上一物理尝试失败，请继续。\n/real/path is part of the task.'
    const ordinary = {
      hostMessageId: 'ordinary',
      message: {
        info: { id: 'ordinary-run', role: 'user', cost: 42, timestamp: 123, directory: '/metadata-only' },
        parts: [{ id: 'ordinary-part', type: 'text', text: body }],
      },
    }
    const retry = {
      hostMessageId: 'retry', origin: 'ProviderRetryAttempt',
      message: { info: { id: 'retry-run', role: 'user' }, parts: [{ id: 'retry-part', type: 'text', text: 'arbitrary control text' }] },
    }
    const captured = await trace.captureObservedMessages(handle, SESSION, [retry, ordinary])
    assert.equal(captured.ok, true)
    assert.equal(captured.receipt.capturedPartCount, 1)
    assert.deepEqual(await trace.currentProjection(handle, SESSION), {
      messages: [{ role: 'user', parts: [{ kind: 'text', text: body }] }],
    })
    const replay = await trace.captureObservedMessages(handle, SESSION, [ordinary])
    assert.equal(replay.receipt.capturedPartCount, 0, 'removing the transport row must not renumber the existing physical part')
  })
})
test('WHAT[semantic-trace-002] missing, blank or duplicate Host ids cannot receive stable capture identity', async () => {
  await withJournal(async (handle) => {
    const observation = (id, index) => ({
      hostMessageId: id,
      message: { info: { id: `run-${index}`, role: 'assistant' }, parts: [{ id: `part-${index}`, type: 'text', text: 'body' }] },
    })
    for (const [index, ids] of [[undefined], [' '], ['same', 'same'], ['a', 'b']].entries()) {
      const result = await trace.captureObservedMessages(handle, `${SESSION}-${index}`, ids.map(observation))
      assert.equal(result.ok, true)
      assert.equal(result.receipt.identity, index === 3 ? 'stable-host' : 'positional')
    }
    await trace.captureProjection(handle, 'legacy-session', { messages: [{ role: 'user', parts: [trace.semanticText('legacy')] }] })
    const legacy = await trace.captureObservedMessages(handle, 'legacy-session', [observation('stable-id', 0)])
    assert.equal(legacy.receipt.identity, 'positional')
  })
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const trace = await import("../../../dist/Context/Trace/SemanticTraceSurface.js");


test('WHAT[semantic-trace-002] capture mapper copies text and reasoning semantics', () => {
  assert.deepEqual(trace.mapPart(trace.textPart('hello')), { kind: 'text', text: 'hello' })
  assert.deepEqual(trace.mapPart(trace.reasoningPart('considering')), { kind: 'reasoning', text: 'considering' })
})
test('WHAT[semantic-trace-002] capture mapper drops transport call identities', () => {
  assert.deepEqual(trace.mapPart(trace.toolCallPart('call-1', 'read', '{}')), {
    kind: 'tool-call',
    name: 'read',
    args: '{}',
  })
  assert.deepEqual(trace.mapPart(trace.toolResultPart('call-1', 'output')), {
    kind: 'tool-result',
    result: 'output',
  })
})
test('WHAT[semantic-trace-002] activity bookkeeping has no semantic part', () => {
  assert.equal(trace.mapPart(trace.activityPart('step-start')), undefined)
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const trace = await import("../../../dist/Context/Trace/SemanticTraceSurface.js");

const unwrap = (result) => {
  assert.equal(result.ok, true, result.ok ? '' : result.error)
  return result.projection
}
const append = (projection, sequence, options = {}) => unwrap(trace.appendPart(projection, {
  sequence,
  role: 'assistant',
  provenance: `g:0/msg:${options.messageId ?? 'message-a'}/host-part:${options.hostToolPartId ?? `part-${sequence}`}`,
  turn: options.turn ?? 0,
  partIndex: options.partIndex ?? sequence - 1,
  kind: options.kind ?? 'tool_call',
  toolName: options.toolName ?? 'todowrite',
  textRef: `blob-${sequence}`,
  textDigest: `digest-${sequence}`,
  providerRun: options.providerRun ?? 'provider-run-a',
  toolCallId: options.toolCallId ?? 'call-a',
  hostToolPartId: options.hostToolPartId ?? `part-${sequence}`,
}))
const fixture = () => {
  let projection = trace.emptyProjection()
  projection = append(projection, 1, { messageId: 'message-a', hostToolPartId: 'part-a', partIndex: 0 })
  projection = append(projection, 2, { messageId: 'message-a', hostToolPartId: 'part-b', partIndex: 1, kind: 'tool_result' })
  projection = append(projection, 3, { messageId: 'message-b', providerRun: 'provider-run-b', toolCallId: 'call-b', hostToolPartId: 'part-c', turn: 1, partIndex: 0 })
  return projection
}

test('WHAT[semantic-trace-002] provider-run query returns copied semantic evidence', () => {
  const parts = trace.providerRunParts('provider-run-a', fixture())
  assert.deepEqual(parts.map((part) => part.cursor.sequence), [1, 2])
  assert.deepEqual(parts.map((part) => part.providerRun), ['provider-run-a', 'provider-run-a'])
})
test('WHAT[semantic-trace-002] exact provider tool and Host identities are queryable', () => {
  const projection = fixture()
  assert.deepEqual(trace.toolResultParts('provider-run-a', 'call-a', projection).map((part) => part.cursor.sequence), [2])
  assert.deepEqual(
    trace.toolPartsForHostIdentity('provider-run-a', 'call-a', 'part-a', projection).map((part) => part.cursor.sequence),
    [1],
  )
})
}

{
const { default: assert } = await import('node:assert/strict')
const trace = await import('../../../dist/Context/Trace/SemanticTraceSurface.js')
const { PlanningSurface } = await import('../../../dist/Mission/Planning/Surface.js')

test('WHAT[semantic-trace-002] later parts and reanchor copies cannot move a retired physical exchange into the current tenure', () => {
  let projection = trace.emptyProjection()
  for (const [sequence, messageId, generation, kind] of [
    [1, 'user-opening', 0, 'text'],
    [2, 'retired-exchange', 0, 'tool_call'],
    [5, 'retired-exchange', 0, 'tool_result'],
    [6, 'current-answer', 0, 'text'],
    [7, 'retired-exchange', 1, 'tool_result'],
    [8, 'current-answer', 1, 'text'],
    [9, 'user-followup', 1, 'text'],
  ]) {
    const appended = trace.appendPart(projection, {
      sequence,
      role: messageId.startsWith('user-') ? 'user' : 'assistant',
      provenance: `g:${generation}/msg:${messageId}/host-part:part-${sequence}`,
      turn: sequence,
      partIndex: 0,
      kind,
      toolName: kind.startsWith('tool_') ? 'js-plan' : undefined,
      textRef: `blob-${sequence}`,
      textDigest: `digest-${sequence}`,
      providerRun: messageId.startsWith('user-') ? undefined : messageId,
      toolCallId: kind.startsWith('tool_') ? 'retired-call' : undefined,
      hostToolPartId: kind.startsWith('tool_') ? 'retired-tool-part' : undefined,
    })
    assert.equal(appended.ok, true, appended.error)
    projection = appended.projection
  }

  const opening = {
    info: { id: 'user-opening', role: 'user' },
    parts: [{ type: 'text', text: 'Original requirement' }],
  }
  const retired = {
    info: { id: 'retired-exchange', role: 'assistant' },
    parts: [{
      id: 'retired-tool-part', type: 'tool', callID: 'retired-call', tool: 'js-plan',
      state: { status: 'completed', input: { program: 'previous work' }, output: 'retired output' },
    }],
  }
  const answer = {
    info: { id: 'current-answer', role: 'assistant' },
    parts: [{ type: 'text', text: 'CURRENT_ANSWER' }],
  }
  const followup = {
    info: { id: 'user-followup', role: 'user' },
    parts: [{ type: 'text', text: 'CURRENT_FOLLOWUP' }],
  }
  const result = PlanningSurface.assembleTenureMessages(
    [opening, retired, answer, followup],
    {
      workId: 'trace-tenure', incumbencyId: 'inc-current', stage: 'S2',
      openingCursor: 4n, previousRange: [0n, 4n], isFreshHandover: false,
    },
    () => 'PREVIOUS_WORK_RECORD',
    trace.hostMessageFirstCursors(projection),
  )
  assert.deepEqual(
    result.messages.map((message) => message.info.id),
    ['user-opening', 'lwr-prev-inc-current', 'current-answer', 'user-followup'],
  )
  assert.equal(result.messages[2], answer)
  assert.equal(result.messages[3], followup)
  assert.deepEqual(result.messages.flatMap((message) => message.parts.map((part) => part.text)), [
    'Original requirement', 'PREVIOUS_WORK_RECORD', 'CURRENT_ANSWER', 'CURRENT_FOLLOWUP',
  ])
})
}
