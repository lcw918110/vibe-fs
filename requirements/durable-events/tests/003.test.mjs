import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { readFileSync } = await import("node:fs");
const { default: test } = await import("node:test");
const journalCodec = await import("../../../dist/Persistence/Journal/CodecSurface.js");
const factCodec = await import("../../../dist/Persistence/Journal/FactCodecSurface.js");

const SESSION = 'ses_a'
const ROOT_ACCEPTED = {
  family: 'Prompt',
  case: 'AuthorityRootAccepted',
  payload: {
    SchemaVersion: 2,
    SessionId: SESSION,
    LogicalRunId: 'run_a',
    AuthorityRootUserMessageId: 'msg_root_a',
    AuthorityKind: 'HumanRoot',
    IdentitySeed: {
      kind: 'RootSelection',
      ownerSession: null,
      ownerLogicalRun: null,
      ownerAuthorityRoot: null,
      participantIdentity: { participant: 'engineer', role: 'engineer', persona: 'Engineer', personaCatalogVersion: 1, origin: 'ResolvedAtRoot' },
    },
  },
}
const env = (overrides = {}) => ({
  runtime: 'rt_a',
  seq: 1,
  observedAt: '2026-01-02T03:04:05Z',
  id: 'a'.repeat(32),
  stream: { kind: 'Session', id: SESSION },
  providerRun: null,
  fact: ROOT_ACCEPTED,
  ...overrides,
})
const readEnvelope = (value) => ({
  runtime: value.runtime,
  seq: Number(value.seq),
  event: value.id,
  stream: value.stream,
  providerRun: value.providerRun,
  fact: value.fact.case,
})
const mustOk = (result, label = 'result') => {
  assert.equal(result.ok, true, `${label} should be Ok: ${JSON.stringify(result.error)}`)
  return result.value
}

test('WHAT[durable-events-003] PERSIST_001_serialization_is_deterministic_for_one_envelope', () => {
  const value = env({ seq: 3, observedAt: '2026-02-03T04:05:06Z' })
  assert.equal(journalCodec.serialize(value), journalCodec.serialize(value))
  assert.equal(journalCodec.serialize(value), journalCodec.serialize(env({ seq: 3, observedAt: '2026-02-03T04:05:06Z' })))
})
test('WHAT[durable-events-003] PERSIST_001_an_absent_provider_run_is_omitted_rather_than_written_null', () => {
  const withoutRun = journalCodec.serialize(env({ seq: 1 }))
  assert.equal(withoutRun.includes('ProviderRun'), false)

  const withRun = journalCodec.serialize(env({ seq: 1, providerRun: 'run_9' }))
  assert.equal(withRun.includes('ProviderRun'), true)
  assert.equal(withRun.includes('run_9'), true)
})
test('WHAT[durable-events-003] PERSIST_001_an_envelope_survives_a_round_trip_unchanged', () => {
  const original = env({ seq: 4, observedAt: '2026-03-04T05:06:07Z', providerRun: 'run_1' })
  const line = journalCodec.serialize(original)
  const decoded = journalCodec.deserialize(line)

  assert.equal(decoded.ok, true, decoded.ok ? '' : decoded.error)
  assert.deepEqual(readEnvelope(decoded.value), readEnvelope(original))
  assert.equal(journalCodec.serialize(decoded.value), line)
})
test('WHAT[durable-events-003] PERSIST_001_serialized_bytes_do_not_depend_on_the_writers_utc_offset', () => {
  const instant = '2026-03-04T05:06:07Z'
  const atUtc = env({ seq: 1, observedAt: instant })
  const shanghai = env({ seq: 1, observedAt: '2026-03-04T13:06:07+08:00' })
  const newYork = env({ seq: 1, observedAt: '2026-03-04T00:06:07-05:00' })

  assert.equal(new Date(shanghai.observedAt).getTime(), new Date(instant).getTime())
  assert.equal(new Date(newYork.observedAt).getTime(), new Date(instant).getTime())

  const line = journalCodec.serialize(atUtc)
  assert.equal(journalCodec.serialize(shanghai), line)
  assert.equal(journalCodec.serialize(newYork), line)
  assert.equal(JSON.parse(line).ObservedAt, '2026-03-04T05:06:07.000+00:00')
})
test('WHAT[durable-events-003] PERSIST_001_parents_and_payload_refs_are_canonicalized_at_the_codec_boundary', () => {
  const encoded = journalCodec.encode(
    ['b'.repeat(32), 'a'.repeat(32), 'b'.repeat(32)],
    ['ref-z', 'ref-a', 'ref-z'],
    env(),
  )
  assert.deepEqual(encoded.parents, ['a'.repeat(32), 'b'.repeat(32)])
  assert.deepEqual(encoded.payloadRefs, ['ref-a', 'ref-z'])
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const eventCodec = await import("../../../dist/Persistence/EventStore/CodecSurface.js");
const eventStore = await import("../../../dist/Persistence/EventStore/Surface.js");

const A = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
const envelope = ({
  id = A,
  stream = 'job/main',
  eventType = 'JobRequested',
  parents = [],
  payload = { status: 'open' },
  payloadRefs = [],
} = {}) => ({
  id,
  stream,
  type: eventType,
  parents,
  payload,
  payloadRefs,
})

test('WHAT[durable-events-003] same_EventId_different_canonical_bytes_fail_closed', () => {
  const left = envelope({ payload: { status: 'open' } })
  const right = envelope({ payload: { status: 'closed' } })

  const checked = eventCodec.checkIdentity(left, right)
  assert.equal(checked.ok, false)
  assert.equal(checked.error.code, 'IdentityCollision')
  assert.equal(checked.error.eventId, A)

  const merged = eventCodec.mergeByIdentity([left, right])
  assert.equal(merged.ok, false)
  assert.equal(merged.error.code, 'IdentityCollision')
})
test('WHAT[durable-events-003] same_EventId_same_canonical_bytes_dedupe_ok', () => {
  const a = envelope({
    parents: [
      'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',
      'cccccccccccccccccccccccccccccccccccccccc',
    ],
    payloadRefs: ['oid-2', 'oid-1'],
  })
  const b = envelope({
    parents: [
      'cccccccccccccccccccccccccccccccccccccccc',
      'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',
      'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',
    ],
    payloadRefs: ['oid-1', 'oid-2', 'oid-1'],
  })

  assert.equal(eventCodec.encode(a), eventCodec.encode(b))
  const checked = eventCodec.checkIdentity(a, b)
  assert.equal(checked.ok, true)

  const merged = eventCodec.mergeByIdentity([a, b])
  assert.equal(merged.ok, true)
  assert.equal(merged.events.length, 1)
})
test('WHAT[durable-events-003] canonical_bytes_are_utf8_json_plus_single_LF_with_sorted_keys', () => {
  const value = envelope({
    parents: [
      'ffffffffffffffffffffffffffffffffffffffff',
      '0000000000000000000000000000000000000000',
    ],
    payload: { z: 1, a: { m: true, b: 2 } },
    payloadRefs: ['p2', 'p1'],
  })

  const text = eventCodec.encode(value)
  assert.equal(text.endsWith('\n'), true)
  assert.equal(text.endsWith('\n\n'), false)
  assert.equal(text.includes('\r'), false)
  assert.equal(text.charCodeAt(0) !== 0xfeff, true)

  const body = text.slice(0, -1)
  assert.deepEqual(Object.keys(JSON.parse(body)), [
    'event_id',
    'event_type',
    'parents',
    'payload',
    'payload_refs',
    'stream_id',
  ])

  const parsed = JSON.parse(body)
  assert.deepEqual(parsed.parents, [
    '0000000000000000000000000000000000000000',
    'ffffffffffffffffffffffffffffffffffffffff',
  ])
  assert.deepEqual(parsed.payload_refs, ['p1', 'p2'])
  assert.deepEqual(Object.keys(parsed.payload), ['a', 'z'])
  assert.deepEqual(Object.keys(parsed.payload.a), ['b', 'm'])

  assert.equal(eventCodec.encode(value), eventCodec.encode(value))
})
test('WHAT[durable-events-003] event payload keys follow Unicode code-point order without integer-key reordering', () => {
  const text = eventCodec.encode(envelope({
    payload: { 2: 'two', 10: 'ten', '\u{10000}': 'supplementary', '\uE000': 'bmp' },
  }))

  assert.match(text, /"payload":\{"10":"ten","2":"two","":"bmp","𐀀":"supplementary"\}/u)
})
test('WHAT[durable-events-003] distinct_EventIds_are_both_retained', () => {
  const a = envelope({ id: '1111111111111111111111111111111111111111' })
  const b = envelope({
    id: '2222222222222222222222222222222222222222',
    payload: { other: true },
  })

  assert.equal(eventCodec.checkIdentity(a, b).ok, true)
  const merged = eventCodec.mergeByIdentity([b, a])
  assert.equal(merged.ok, true)
  assert.equal(merged.events.length, 2)
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const journalCodec = await import("../../../dist/Persistence/Journal/CodecSurface.js");
const eventCodec = await import("../../../dist/Persistence/EventStore/CodecSurface.js");

const SESSION = 'ses_a'
const ROOT_ACCEPTED = {
  family: 'Prompt',
  case: 'AuthorityRootAccepted',
  payload: {
    SchemaVersion: 2,
    SessionId: SESSION,
    LogicalRunId: 'run_a',
    AuthorityRootUserMessageId: 'msg_root_a',
    AuthorityKind: 'HumanRoot',
    IdentitySeed: {
      kind: 'RootSelection',
      ownerSession: null,
      ownerLogicalRun: null,
      ownerAuthorityRoot: null,
      participantIdentity: { participant: 'engineer', role: 'engineer', persona: 'Engineer', personaCatalogVersion: 1, origin: 'ResolvedAtRoot' },
    },
  },
}
const env = (overrides = {}) => ({
  runtime: 'rt_a',
  seq: 1,
  observedAt: '2026-01-02T03:04:05Z',
  id: 'a'.repeat(40),
  stream: { kind: 'Session', id: SESSION },
  providerRun: null,
  fact: ROOT_ACCEPTED,
  ...overrides,
})
const eventShape = (event) => ({
  id: event.eventId,
  stream: event.streamId,
  type: event.eventType,
  parents: event.parents,
  payload: event.payload,
  payloadRefs: event.payloadRefs,
})
const readEnvelope = (value) => ({
  runtime: value.runtime,
  seq: Number(value.seq),
  event: value.id,
  stream: value.stream,
  providerRun: value.providerRun,
  fact: value.fact.case,
  line: value.line,
})
const mustOk = (result, label = 'result') => {
  assert.equal(result.ok, true, `${label} should be Ok: ${JSON.stringify(result.error)}`)
  return result.value
}

test('WHAT[durable-events-003] parents_are_accepted_and_canonicalized', () => {
  const parentA = 'b'.repeat(40)
  const parentB = 'a'.repeat(40)
  const encoded = journalCodec.encode([parentA, parentB, parentA], [], env())
  assert.deepEqual(encoded.parents, [parentB, parentA])
})
test('WHAT[durable-events-003] payloadRefs_are_accepted_and_canonicalized_without_RuntimePath_IO', () => {
  const encoded = journalCodec.encode([], ['ref-z', 'ref-a', 'ref-z'], env())
  assert.deepEqual(encoded.payloadRefs, ['ref-a', 'ref-z'])
})
test('WHAT[durable-events-003] canonical_identity_bytes_stable_under_section_5_0', () => {
  const original = env({ seq: 3, observedAt: '2026-01-02T03:04:05Z', providerRun: 'run_stable' })
  const parents = ['c'.repeat(40), 'b'.repeat(40)]
  const refs = ['oid-2', 'oid-1']

  const a = journalCodec.encode(parents, refs, original)
  const b = journalCodec.encode([...parents].reverse(), [...refs].reverse(), original)

  assert.equal(eventCodec.encode(eventShape(a)), eventCodec.encode(eventShape(b)))
  assert.equal(eventCodec.checkIdentity(eventShape(a), eventShape(b)).ok, true)

  const redecoded = eventCodec.decode(eventCodec.encode(eventShape(a)))
  assert.equal(redecoded.ok, true, 'event decode should be Ok')
  assert.equal(eventCodec.encode(redecoded.event), eventCodec.encode(eventShape(a)))
})
}

{
const { default: assert } = await import("node:assert/strict");
const { performance } = await import("node:perf_hooks");
const { default: test } = await import("node:test");
const eventMerge = await import("../../../dist/Persistence/EventStore/MergeSurface.js");

const envelope = (id, parents = [], stream = 'proof/merge', payload = {}) => ({
  id,
  stream,
  type: 'JobRequested',
  parents,
  payload,
  payloadRefs: [],
})

test('WHAT[durable-events-003] DURABLE_EVENTS_003_same_EventId_same_bytes_dedupes', () => {
  const id = '0'.repeat(39) + '1'
  const sameA = envelope(id, [], 'proof/a', { x: 1 })
  const sameB = envelope(id, [], 'proof/a', { x: 1 })

  const merged = eventMerge.merge([
    ['a', [sameA]],
    ['b', [sameB]],
  ])
  assert.equal(merged.ok, true)
  assert.equal(merged.events.length, 1)
  assert.deepEqual(merged.events[0].payload, { x: 1 }, 'merge returns the event payload, not the canonical envelope')
})
test('WHAT[durable-events-003] DURABLE_EVENTS_003_same_EventId_different_bytes_fail_closed', () => {
  const id = '0'.repeat(39) + '2'
  const a = envelope(id, [], 'proof/a', { x: 1 })
  const b = envelope(id, [], 'proof/a', { x: 2 })

  const result = eventMerge.merge([
    ['a', [a]],
    ['b', [b]],
  ])
  assert.equal(result.ok, false)
})
}

{
const { default: assert } = await import("node:assert/strict");
const { mkdirSync, mkdtempSync, rmSync, writeFileSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join } = await import("node:path");
const { default: test } = await import("node:test");
const eventCodec = await import("../../../dist/Persistence/EventStore/CodecSurface.js");
const eventStore = await import("../../../dist/Persistence/EventStore/Surface.js");

const event = {
  id: '757466382d62797465732d6172652d6964656e7469',
  stream: 'proof/utf8',
  type: 'JobRequested',
  parents: [],
  payload: { text: 'é' },
  payloadRefs: [],
}
const invalidUtf8Event = () => {
  const bytes = Buffer.from(eventCodec.encode(event))
  const continuation = bytes.indexOf(0xa9)
  assert.notEqual(continuation, -1)
  bytes[continuation] = 0x20
  return bytes
}

test('WHAT[durable-events-003] invalid UTF-8 bytes fail closed before canonical JSON decoding', () => {
  const decoded = eventCodec.decodeUtf8(invalidUtf8Event())

  assert.equal(decoded.ok, false)
  assert.equal(decoded.error.code, 'NonCanonical')
  assert.match(decoded.error.reason, /not valid UTF-8/)
})
test('WHAT[durable-events-003] UTF-8 BOM bytes are rejected rather than stripped by the decoder', () => {
  const decoded = eventCodec.decodeUtf8(Buffer.concat([
    Buffer.from([0xef, 0xbb, 0xbf]),
    Buffer.from(eventCodec.encode(event)),
  ]))

  assert.equal(decoded.ok, false)
  assert.equal(decoded.error.code, 'NonCanonical')
  assert.match(decoded.error.reason, /must not contain a BOM/)
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const factCodec = await import("../../../dist/Persistence/Journal/FactCodecSurface.js");

const runtimeStarted = (startedAt = '2026-01-01T00:00:00Z') => ({
  family: 'Runtime',
  case: 'RuntimeStarted',
  payload: { RuntimeId: 'rt_fact', ProcessId: 42, StartedAt: startedAt },
})
const handleAbandoned = (abandonedAt = '2026-01-01T00:00:00Z') => ({
  family: 'Execution',
  case: 'HandleAbandoned',
  payload: {
    ParentSessionId: 'ses_pin',
    Handle: 'h-pin',
    Reason: 'ParentCancelled',
    AbandonedAt: abandonedAt,
  },
})
const handleCompleted = (overrides = {}) => ({
  family: 'Execution',
  case: 'HandleCompleted',
  payload: {
    ParentSessionId: 'ses_hc',
    Handle: 'h-hc',
    Kind: 'Terminal',
    CompletionRef: null,
    CompletionDigest: null,
    ...overrides,
  },
})
const handleLinked = (overrides = {}) => ({
  family: 'Execution',
  case: 'HandleLinked',
  payload: {
    ParentSessionId: 'ses_hl',
    ChildSessionId: 'ses_hl_child',
    Handle: 'h-hl',
    TargetAgent: 'coder',
    Byname: 'Rhea',
    CanonicalRole: 'Engineer',
    Ownership: 'DurableParentHandle',
    ...overrides,
  },
})

test('WHAT[durable-events-003] PERSIST_001_runtime_started_pins_offset_on_serialize_and_deserialize', () => {
  const line = factCodec.encode(runtimeStarted())
  assert.match(line, /StartedAt[^Z]*Z|StartedAt.*\+00:00/, `offset must be pinned to UTC: ${line}`)

  const shifted = line.replace('T00:00:00.000+00:00', 'T08:00:00.000+08:00')
  const decoded = factCodec.decode(shifted)
  assert.equal(decoded.ok, true, decoded.ok ? '' : decoded.error)
  assert.equal(decoded.line, line)
})
test('WHAT[durable-events-003] PERSIST_001_handle_abandoned_pins_abandoned_at_offset', () => {
  const line = factCodec.encode(handleAbandoned('2026-01-01T08:00:00+08:00'))
  const decoded = factCodec.decode(line)
  assert.equal(decoded.ok, true, decoded.ok ? '' : decoded.error)
  assert.equal(decoded.line, line, 'offset must normalise to +00:00')
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const journalCodec = await import("../../../dist/Persistence/Journal/CodecSurface.js");

const SESSION = 'ses_meta'
const ROOT_ACCEPTED = {
  family: 'Prompt',
  case: 'AuthorityRootAccepted',
  payload: {
    SchemaVersion: 2,
    SessionId: SESSION,
    LogicalRunId: 'run_meta',
    AuthorityRootUserMessageId: 'msg_root_meta',
    AuthorityKind: 'HumanRoot',
    IdentitySeed: {
      kind: 'RootSelection',
      ownerSession: null,
      ownerLogicalRun: null,
      ownerAuthorityRoot: null,
      participantIdentity: { participant: 'engineer', role: 'engineer', persona: 'Engineer', personaCatalogVersion: 1, origin: 'ResolvedAtRoot' },
    },
  },
}
const envelope = (overrides = {}) => ({
  runtime: 'rt_meta',
  seq: 1,
  observedAt: '2026-03-04T05:06:07Z',
  id: 'a'.repeat(40),
  stream: { kind: 'Session', id: SESSION },
  providerRun: null,
  fact: ROOT_ACCEPTED,
  ...overrides,
})
const readEnvelope = (value) => ({
  runtime: value.runtime,
  seq: Number(value.seq),
  event: value.id,
  stream: value.stream,
  providerRun: value.providerRun,
  fact: value.fact,
})

test('WHAT[durable-events-003] Journal_codec_serializes_one_envelope_to_one_UTC_line', () => {
  const line = journalCodec.serialize(envelope())
  assert.equal(line.includes('\n'), false)
  assert.equal(line.includes('\r'), false)
  assert.equal(JSON.parse(line).ObservedAt, '2026-03-04T05:06:07.000+00:00')

  const shifted = envelope({ observedAt: '2026-03-04T13:06:07+08:00' })
  assert.equal(journalCodec.serialize(shifted), line)
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const canonical = await import("../../../dist/OpenCode/Codec/CanonicalJsonSurface.js");


test('WHAT[durable-events-003] MISC_canonical_json_sorts_keys_recursively', () => {
  assert.equal(canonical.canonicalJson({ b: 1, a: { d: 4, c: 3 } }), '{"a":{"c":3,"d":4},"b":1}')
  assert.equal(canonical.canonicalJson({ a: 1, b: 2 }), canonical.canonicalJson({ b: 2, a: 1 }))
  assert.equal(canonical.canonicalJson([3, { x: 1, y: 2 }]), '[3,{"x":1,"y":2}]')
  assert.equal(canonical.canonicalJson('s'), '"s"')
  assert.equal(canonical.canonicalJson(null), 'null')
})
test('WHAT[durable-events-003] canonical JSON orders numeric-looking and non-BMP keys by Unicode code point', () => {
  assert.equal(canonical.canonicalJson({ 2: 'two', 10: 'ten' }), '{"10":"ten","2":"two"}')
  assert.equal(
    canonical.canonicalJson({ '\u{10000}': 'supplementary', '\uE000': 'bmp' }),
    '{"":"bmp","𐀀":"supplementary"}',
  )
})
test('WHAT[durable-events-003] canonical JSON preserves JSON sparse-array null semantics', () => {
  assert.equal(canonical.canonicalJson(new Array(2)), '[null,null]')
})
test('WHAT[durable-events-003] MISC_canonical_json_equal_ignores_key_order', () => {
  assert.equal(canonical.equal({ a: 1, b: 2 }, { b: 2, a: 1 }), true)
  assert.equal(canonical.equal({ a: 1 }, { a: 2 }), false)
  assert.equal(canonical.equal({ a: 1 }, { a: 1, b: 2 }), false)
  assert.equal(canonical.equal(null, undefined), false)
})
test('WHAT[durable-events-003] MISC_without_keys_drops_named_fields_only', () => {
  assert.deepEqual(canonical.withoutKeys(['id', 'secret'], { id: 'x', secret: 'y', keep: 1 }), { keep: 1 })
  assert.equal(canonical.withoutKeys(['a'], 'plain'), 'plain')
  assert.equal(canonical.withoutKeys(['a'], null), null)
  assert.deepEqual(canonical.withoutKeys(['a'], [1, 2]), [1, 2], 'arrays pass through untouched')
})
}

{
const { default: assert } = await import("node:assert/strict");
const codec = await import("../../../dist/Persistence/EventStore/CodecSurface.js");
const { integrationTest } = await import("../../verification-system/tests/support/tier-gate.mjs");
const event = (overrides = {}) => ({
  id: 'a'.repeat(40),
  stream: 'identity/proof',
  type: 'JobRequested',
  parents: [],
  payload: { answer: 42, nested: { b: 2, a: 1 } },
  payloadRefs: [],
  ...overrides,
})

integrationTest('WHAT[durable-events-003] canonical_event_bytes_are_stable_under_object_and_set_order', () => {
  const left = event({
    parents: ['c'.repeat(40), 'b'.repeat(40), 'c'.repeat(40)],
    payloadRefs: ['ref-z', 'ref-a', 'ref-z'],
  })
  const right = event({
    parents: ['b'.repeat(40), 'c'.repeat(40)],
    payloadRefs: ['ref-a', 'ref-z'],
    payload: { nested: { a: 1, b: 2 }, answer: 42 },
  })

  const bytes = codec.encode(left)
  assert.equal(bytes.endsWith('\n'), true)
  assert.equal(bytes.endsWith('\n\n'), false)
  assert.equal(codec.encode(right), bytes)
  assert.equal(codec.checkIdentity(left, right).ok, true)
})

integrationTest('WHAT[durable-events-003] same_event_id_different_canonical_bytes_is_identity_collision', () => {
  const result = codec.checkIdentity(event(), event({ payload: { answer: 43 } }))
  assert.equal(result.ok, false)
  assert.equal(result.error.code, 'IdentityCollision')
  assert.equal(result.error.eventId, 'a'.repeat(40))
})

integrationTest('WHAT[durable-events-003] canonical_event_bytes_decode_to_the_same_plain_event', () => {
  const original = event({
    id: 'b'.repeat(40),
    parents: ['d'.repeat(40), 'c'.repeat(40)],
    payloadRefs: ['payload-z', 'payload-a'],
  })
  const decoded = codec.decode(codec.encode(original))
  assert.equal(decoded.ok, true, JSON.stringify(decoded.error))
  assert.deepEqual(decoded.event, {
    ...original,
    parents: ['c'.repeat(40), 'd'.repeat(40)],
    payloadRefs: ['payload-a', 'payload-z'],
  })
})

integrationTest('WHAT[durable-events-003] decode_rejects_noncanonical_key_and_set_order_without_reencoding_the_event', async () => {
  const base = {
    event_id: 'e'.repeat(40),
    event_type: 'JobRequested',
    parents: [],
    payload: { a: 1, b: 2 },
    payload_refs: [],
    stream_id: 'identity/proof',
  }

  const wrongTopLevelOrder = `${JSON.stringify({ stream_id: base.stream_id, ...base })}\n`
  const wrongPayloadOrder = `${JSON.stringify({ ...base, payload: { b: 2, a: 1 } })}\n`
  const wrongParentOrder = `${JSON.stringify({ ...base, parents: ['b'.repeat(40), 'a'.repeat(40)] })}\n`
  const duplicateRefs = `${JSON.stringify({ ...base, payload_refs: ['ref-a', 'ref-a'] })}\n`

  for (const text of [wrongTopLevelOrder, wrongPayloadOrder, wrongParentOrder, duplicateRefs]) {
    const decoded = codec.decode(text)
    assert.equal(decoded.ok, false, text)
    assert.equal(decoded.error.code, 'NonCanonical')
  }

  const { readFile } = await import('node:fs/promises')
  const source = await readFile(
    new URL('../../../src/Wanxiangshu/Persistence/EventStore/CanonicalEventCodec.fs', import.meta.url),
    'utf8',
  )
  assert.doesNotMatch(
    source,
    /let private ensureCanonical[\s\S]{0,350}encode\s+normalized/,
    'decode must validate parsed canonical shape directly instead of allocating a second normalized event encoding',
  )
})

integrationTest('WHAT[durable-events-003] merge_by_identity_dedupes_equal_bytes_and_rejects_collisions', () => {
  const same = codec.mergeByIdentity([event(), event()])
  assert.equal(same.ok, true)
  assert.equal(same.events.length, 1)

  const collision = codec.mergeByIdentity([event(), event({ payload: { answer: 99 } })])
  assert.equal(collision.ok, false)
  assert.equal(collision.error.code, 'IdentityCollision')
})
}
