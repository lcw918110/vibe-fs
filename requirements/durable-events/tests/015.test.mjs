import assert from 'node:assert/strict'
import test from 'node:test'
import * as contextFold from '../../../dist/Context/Companion/FoldSurface.js'

const SESSION = 'ses_ctx'

let sequence = 0

const next = (fact, run) => ({
  runtime: 'rt_ctx',
  seq: ++sequence,
  id: `event-${sequence}`,
  observedAt: '2026-04-01T08:00:00Z',
  session: SESSION,
  run,
  fact,
})

const companionFact = (caseName, payload, run = null) =>
  next(
    {
      family: 'Companion',
      case: caseName,
      payload,
    },
    run,
  )


const openingFact = () =>
  companionFact(
    'OpeningPromptCaptured',
    {
      SessionId: SESSION,
      AssignmentText: 'ship the durable fold',
      AuthoritativeRequirements: ['preserve replay'],
      ProviderRun: 'msg_opening',
    },
    'msg_opening',
  )

const partFact = () =>
  companionFact(
    'XTracePartAppended',
    {
      SessionId: SESSION,
      CursorSequence: 1n,
      Role: 'assistant',
      Provenance: 'g:0/provider',
      Turn: 0,
      PartIndex: 0,
      Kind: 'text',
      ToolName: null,
      ProviderRun: 'msg_part',
      ToolCallId: null,
      HostToolPartId: null,
      TextRef: 'blob-part',
      TextDigest: 'sha-part',
    },
    'msg_part',
  )

const terminalFact = () =>
  companionFact(
    'TerminalOutputCaptured',
    {
      SessionId: SESSION,
      TextRef: 'blob-terminal',
      TextDigest: 'sha-terminal',
      ProviderRun: 'msg_terminal',
    },
    'msg_terminal',
  )

const checkpointFact = (callId) =>
  next(
    {
      family: 'Context',
      case: 'ViewCheckpointCommitted',
      payload: {
        SessionId: SESSION,
        ToolCallId: callId,
      },
    },
    `msg_${callId}`,
  )

const reanchorFact = ({ previousEpoch, nextEpoch, run = 'msg_compaction' }) =>
  next(
    {
      family: 'Context',
      case: 'ContextReanchored',
      payload: {
        SessionId: SESSION,
        PreviousEpochId: previousEpoch,
        NextEpochId: nextEpoch,
        ObservedCompactionRun: run,
      },
    },
    run,
  )

const foldOk = (envelopes) => {
  const result = contextFold.fold(envelopes)
  assert.equal(result.ok, true, result.ok ? '' : JSON.stringify(result.error))
  return result.value.sessions[SESSION]
}

test('WHAT[durable-events-015] PERSIST_010_companion_online_and_codec_replay_are_identical', () => {
  const envelopes = [openingFact(), partFact(), terminalFact(), checkpointFact('call-1')]

  assert.deepEqual(contextFold.replay(envelopes), contextFold.fold(envelopes))
})

test('WHAT[durable-events-015] PERSIST_010 view checkpoints fold in order and replay identically', () => {
  const envelopes = [checkpointFact('call-1'), checkpointFact('call-2')]

  assert.deepEqual(contextFold.replay(envelopes), contextFold.fold(envelopes))

  const value = contextFold.fold(envelopes).value
  assert.deepEqual(
    value.viewCheckpoints[SESSION].checkpoints.map((checkpoint) => checkpoint.callId),
    ['call-1', 'call-2'],
  )
})

test('WHAT[durable-events-015] PERSIST_010 a replayed checkpoint keeps one entry and a reanchor clears the window', () => {
  const replayed = contextFold.fold([checkpointFact('call-1'), checkpointFact('call-1')]).value
  assert.deepEqual(
    replayed.viewCheckpoints[SESSION].checkpoints.map((checkpoint) => checkpoint.callId),
    ['call-1'],
  )

  const reanchored = contextFold.fold([
    openingFact(),
    partFact(),
    checkpointFact('call-1'),
    reanchorFact({ previousEpoch: 0, nextEpoch: 1 }),
  ]).value
  assert.equal(reanchored.viewCheckpoints[SESSION], undefined)
  assert.equal(reanchored.sessions[SESSION].XTrace.Parts.length, 1)
})

test('WHAT[durable-events-015] PERSIST_010_companion_facts_fold_into_the_owned_projection', () => {
  const session = foldOk([openingFact(), partFact(), terminalFact()])

  assert.deepEqual(session.XTrace.Opening, {
    AssignmentText: 'ship the durable fold',
    AuthoritativeRequirements: ['preserve replay'],
  })
  assert.equal(session.XTrace.Parts.length, 1)
  assert.equal(Number(session.XTrace.Parts[0].CursorSequence), 1)
  assert.deepEqual(
    { ...session.XTrace.Parts[0], CursorSequence: undefined },
    {
      CursorSequence: undefined,
      Provenance: 'g:0/provider',
      Role: 'assistant',
      Kind: 'text',
      TextRef: 'blob-part',
      TextDigest: 'sha-part',
    },
  )
  assert.equal(Number(session.XTrace.LatestTerminal.FrontierSequence), 2)
  assert.deepEqual({ ...session.XTrace.LatestTerminal, FrontierSequence: undefined }, {
    TextRef: 'blob-terminal',
    TextDigest: 'sha-terminal',
    ProviderRun: 'msg_terminal',
    FrontierSequence: undefined,
  })
})

