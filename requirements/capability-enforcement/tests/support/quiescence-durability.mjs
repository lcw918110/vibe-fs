import assert from 'node:assert/strict'
import * as quiescence from '../../../../dist/OpenCode/Host/QuiescenceSurface.js'
import * as factCodec from '../../../../dist/Persistence/Journal/FactCodecSurface.js'
import * as journalCodec from '../../../../dist/Persistence/Journal/CodecSurface.js'
import * as eventCodec from '../../../../dist/Persistence/EventStore/CodecSurface.js'
import * as routing from '../../../../dist/OpenCode/Host/ModelRoutingSurface.js'
import { assertOpaque } from '../../../verification-system/tests/support/js-contract.mjs'

export const SESSION = 'ses-process-capability'
export const accepted = { accepted: true, failure: null }
export const rejected = (failure) => ({ accepted: false, failure })

export const receipt = {
  family: 'Execution',
  case: 'HandleCompleted',
  payload: {
    ParentSessionId: SESSION,
    Handle: 'completed-child',
    Kind: 'Terminal',
    CompletionRef: 'receipt-body',
    CompletionDigest: 'receipt-digest',
  },
}

const journalEnvelope = {
  runtime: 'quiescence-durability-runtime',
  seq: 1,
  observedAt: '2026-01-02T03:04:05Z',
  id: 'a'.repeat(40),
  stream: { kind: 'Session', id: SESSION },
  providerRun: null,
  fact: {
    family: 'Prompt',
    case: 'AuthorityRootAccepted',
    payload: {
      SchemaVersion: 2,
      SessionId: SESSION,
      LogicalRunId: 'run-durability',
      AuthorityRootUserMessageId: 'root-durability',
      AuthorityKind: 'HumanRoot',
      IdentitySeed: {
        kind: 'RootSelection',
        ownerSession: null,
        ownerLogicalRun: null,
        ownerAuthorityRoot: null,
        participantIdentity: {
          participant: 'engineer',
          role: 'engineer',
          persona: 'Engineer',
          personaCatalogVersion: 1,
          origin: 'ResolvedAtRoot',
        },
      },
    },
  },
}

export const eventShape = (event) => ({
  id: event.eventId,
  stream: event.streamId,
  type: event.eventType,
  parents: event.parents,
  payload: event.payload,
  payloadRefs: event.payloadRefs,
})

export const assertNativeSerializationRejected = (permit, refusal = /QuiescencePermit is process-local and cannot be serialized/) => {
  for (const value of [permit, { permit }, [permit], { nested: [{ permit }] }]) {
    assert.throws(() => JSON.stringify(value), refusal)
  }
  assert.deepEqual(JSON.parse(JSON.stringify(receipt)), receipt, 'ordinary durable data remains serializable')
}

export const createDurableMaterial = () => {
  const receiptLine = factCodec.encode(receipt)
  const journalLine = journalCodec.serialize(journalEnvelope)
  const journalEvent = journalCodec.encode([], [], journalEnvelope)
  const eventLine = eventCodec.encode(eventShape(journalEvent))
  return { session: SESSION, receiptLine, journalLine, eventLine }
}

export const decodeDurableMaterial = (material) => {
  assert.equal(material.session, SESSION)
  assert.equal(material.receiptLine, factCodec.encode(receipt))
  const completed = factCodec.decode(material.receiptLine)
  assert.deepEqual(completed, { ok: true, line: material.receiptLine, case: 'HandleCompleted', payload: {} })
  const journal = journalCodec.deserialize(material.journalLine)
  assert.equal(journal.ok, true, journal.error)
  assert.deepEqual(journal.value.fact, journalEnvelope.fact)
  assert.equal(journal.value.line, material.journalLine)
  assert.equal(journalCodec.serialize(journal.value), material.journalLine)
  const event = eventCodec.decode(material.eventLine)
  assert.equal(event.ok, true, JSON.stringify(event.error))
  assert.equal(eventCodec.encode(event.event), material.eventLine)
  const recovered = journalCodec.decode({
    eventId: event.event.id,
    streamId: event.event.stream,
    eventType: event.event.type,
    parents: event.event.parents,
    payload: event.event.payload,
    payloadRefs: event.event.payloadRefs,
  })
  assert.deepEqual(recovered, journal, 'real Event bytes recover the same Journal evidence, not a permit')
  return [completed, journal.value, event.event, recovered.value, JSON.parse(material.receiptLine), JSON.parse(JSON.stringify(receipt))]
}

export const assertCodecSerializationRejected = (permit, material, refusal = /QuiescencePermit is process-local and cannot be serialized/) => {
  assert.throws(() => factCodec.encode(permit), /FactCodecSurface: unknown fact/)
  assert.throws(() => journalCodec.serialize({ ...journalEnvelope, fact: permit }), /JournalCodecSurface: unknown fact/)
  const normalEvent = eventCodec.decode(material.eventLine)
  assert.equal(normalEvent.ok, true, JSON.stringify(normalEvent.error))
  for (const payload of [permit, { permit }, [permit], { evidence: normalEvent.event.payload, nested: [permit] }]) {
    assert.throws(
      () => eventCodec.encode({ ...normalEvent.event, payload }),
      refusal,
    )
  }
  assert.throws(
    () => journalCodec.decode({
      eventId: journalEnvelope.id,
      streamId: journalCodec.encodeStreamId(journalEnvelope.stream),
      eventType: journalCodec.JournalEnvelopeEventType,
      parents: [],
      payload: { permit },
      payloadRefs: [],
    }),
    refusal,
  )
}

export const assertMaterialsCannotAdmit = (gate, material) => {
  const restored = decodeDurableMaterial(material)
  const forwarded = JSON.parse(JSON.stringify(material))
  for (const value of [...restored, forwarded, forwarded.session, {}, null]) {
    assert.deepEqual(quiescence.tryConsume(gate, value), rejected('WrongOwner'))
    assert.deepEqual(quiescence.tryRelease(gate, value), rejected('WrongOwner'))
  }
}

export const assertLeaseNonDurability = async (material) => {
  const target = { model: 'fixture/model', reasoning: 'none' }
  const observed = {
    sessionId: SESSION, physicalUserMessageId: 'msg-process-capability',
    role: 'engineer', participant: 'engineer', target,
  }
  const runtime = routing.createRuntime(() => target)
  assertOpaque(runtime, 'routing runtime')
  assert.deepEqual(routing.snapshotOccupied(runtime), [], 'fresh owner does not inherit capacity from durable material')
  const acquired = await routing.acquireExecutionAdmission(
    runtime, observed.sessionId, observed.physicalUserMessageId,
    observed.role, observed.participant, null, 'normal',
  )
  assert.equal(acquired.kind, 'Acquired')
  assert.equal(acquired.failure, null)
  assert.equal(acquired.queue, null)
  assertOpaque(acquired.lease, 'execution admission lease')
  try {
    const refusal = /ExecutionAdmissionToken is process-local and cannot be serialized/
    assertNativeSerializationRejected(acquired.lease, refusal)
    assertCodecSerializationRejected(acquired.lease, material, refusal)
    const restored = decodeDurableMaterial(material)
    for (const value of [...restored, JSON.parse(JSON.stringify(observed)), {}, null]) {
      assert.equal(routing.executionAdmissionTarget(runtime, value), null)
      assert.deepEqual(routing.commitExecutionAdmission(runtime, value, observed), { kind: 'StaleFence' })
      assert.deepEqual(routing.releaseExecutionAdmissionBeforeProvider(runtime, value, observed), { kind: 'StaleFence' })
    }
    assert.deepEqual(routing.executionAdmissionTarget(runtime, acquired.lease), target)
  } finally {
    assert.deepEqual(routing.releaseExecutionAdmissionBeforeProvider(runtime, acquired.lease, observed), { kind: 'Applied' })
  }
}
