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

test('WHAT[durable-events-007] PERSIST_005_malformed_json_is_an_error_value_not_an_exception', () => {
  for (const bad of ['', '{', '{"unclosed": ', 'null', '[]', 'not json at all']) {
    const decoded = journalCodec.deserialize(bad)
    assert.equal(decoded.ok, false, `${JSON.stringify(bad)} must not decode`)
    assert.equal(typeof decoded.error, 'string')
  }
})
}

{
const { default: assert } = await import("node:assert/strict");
const { existsSync, readFileSync, mkdirSync, mkdtempSync, rmSync, writeFileSync } = await import("node:fs");
const { readFile } = await import("node:fs/promises");
const { default: path } = await import("node:path");
const { tmpdir } = await import("node:os");
const { join } = await import("node:path");
const { default: test } = await import("node:test");
const eventStore = await import("../../../dist/Persistence/EventStore/Surface.js");
const journal = await import("../../../dist/Persistence/Journal/Surface.js");

const id = (n) => n.toString(16).padStart(40, '0')
const canonicalize = (value) => {
  if (Array.isArray(value)) return value.map(canonicalize)
  if (value && typeof value === 'object') {
    return Object.fromEntries(Object.keys(value).sort().map((key) => [key, canonicalize(value[key])]))
  }
  return value
}
const canonicalEventLine = (event) => `${JSON.stringify(canonicalize(event))}\n`
const withTemp = (fn) => {
  const base = mkdtempSync(join(tmpdir(), 'wxs-event-store-append-'))
  return fn(base)
}
const event = (n, parents = [], type = 'JobRequested', payload = { n }) => ({
  id: id(n),
  stream: 'append/proof',
  type,
  parents: parents.map((p) => id(p)),
  payload,
  payloadRefs: [],
})

test('WHAT[durable-events-007] append_rejects_missing_parent_without_writing_bytes', async () => {
  const dir = withTemp((base) => base)
  const store = eventStore.create(dir, 'missing-parent-proof')
  try {
    const file = path.join(dir, 'wanxiangshu', 'events', 'missing-parent-proof.ndjson')
    assert.equal(existsSync(file), false)
    const r = await eventStore.append(store, [event(2, [99])])
    assert.equal(r.ok, false)
    assert.equal(existsSync(file), false, 'rejected structural append creates no writer file')
  } finally {
    eventStore.dispose(store)
    rmSync(dir, { recursive: true, force: true })
  }
})
test('WHAT[durable-events-007] append_rejects_cycle_in_one_batch_before_durability', async () => {
  const dir = withTemp((base) => base)
  const store = eventStore.create(dir, 'cycle-proof')
  try {
    const a = event(1, [2])
    const b = event(2, [1])
    const r = await eventStore.append(store, [a, b])
    assert.equal(r.ok, false)
    const file = path.join(dir, 'wanxiangshu', 'events', 'cycle-proof.ndjson')
    assert.equal(existsSync(file), false, 'rejected structural append creates no writer file')
  } finally {
    eventStore.dispose(store)
    rmSync(dir, { recursive: true, force: true })
  }
})
test('WHAT[durable-events-007] append_rejects_unknown_event_type_fail_closed', async () => {
  const dir = withTemp((base) => base)
  const store = eventStore.create(dir, 'unknown-type-proof')
  try {
    const r = await eventStore.append(store, [event(1, [], 'UnknownFutureEvent')])
    assert.equal(r.ok, false)
  } finally {
    eventStore.dispose(store)
    rmSync(dir, { recursive: true, force: true })
  }
})
}

{
const { default: assert } = await import("node:assert/strict");
const { mkdtempSync, rmSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join } = await import("node:path");
const { default: test } = await import("node:test");
const eventStore = await import("../../../dist/Persistence/EventStore/Surface.js");
const eventMerge = await import("../../../dist/Persistence/EventStore/MergeSurface.js");

const A = 'a'.repeat(40)
const B = 'b'.repeat(40)
const C = 'c'.repeat(40)
const D = 'd'.repeat(40)
const make = ({
  id,
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
const withTemp = (fn) => {
  const base = mkdtempSync(join(tmpdir(), 'wxs-event-store-fold-'))
  return fn(base)
}

test('WHAT[durable-events-007] DURABLE_EVENTS_014_k_way_merge_rejects_missing_parent_fail_closed', () => {
  const child = make({ id: B, parents: [A] })
  const result = eventMerge.merge([['writer', [child]]])
  assert.equal(result.ok, false)
  assert.equal(result.error.code, 'MissingParent')
  assert.equal(result.error.eventId, A)
})
test('WHAT[durable-events-007] DURABLE_EVENTS_014_k_way_merge_rejects_backward_or_cyclic_writer_frontier', () => {
  const a = make({ id: A, parents: [B] })
  const b = make({ id: B, parents: [A] })
  const result = eventMerge.merge([['writer', [a, b]]])
  assert.equal(result.ok, false)
  assert.equal(result.error.code, 'NonCanonical')
})
test('WHAT[durable-events-007] DURABLE_EVENTS_007_unknown_authoritative_event_type_is_rejected_before_durability', async () => {
  const dir = withTemp((base) => base)
  const store = eventStore.create(dir, 'unknown-type-fold')
  try {
    const result = await eventStore.append(store, [make({ id: A, eventType: 'TotallyUnknownEventType' })])
    assert.equal(result.ok, false)
    assert.equal(result.error.code, 'StorageInvalid')
    assert.equal(result.error.error.code, 'UnknownEventType')
  } finally {
    eventStore.dispose(store)
    rmSync(dir, { recursive: true, force: true })
  }
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

test('WHAT[durable-events-007] DURABLE_EVENTS_014_missing_parent_fails_closed', () => {
  const child = envelope('0'.repeat(39) + '3', ['0'.repeat(39) + '9'])
  const result = eventMerge.merge([['a', [child]]])
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

test('WHAT[durable-events-007] local writer boot rejects invalid UTF-8 without replacement decoding', () => {
  const root = mkdtempSync(join(tmpdir(), 'wanxiang-invalid-utf8-'))
  const commonDir = join(root, '.Herdchestra')
  const eventsDir = join(commonDir, 'wanxiangshu', 'events')
  mkdirSync(eventsDir, { recursive: true })
  writeFileSync(join(eventsDir, 'broken.ndjson'), invalidUtf8Event())

  try {
    assert.throws(() => eventStore.create(commonDir, 'new-writer'), /not valid UTF-8/)
  } finally {
    rmSync(root, { recursive: true, force: true })
  }
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

test('WHAT[durable-events-007] PERSIST_005_unparseable_json_is_a_decode_error_not_a_throw', () => {
  const decoded = factCodec.decode('{not json')
  assert.equal(decoded.ok, false)
  assert.equal(typeof decoded.error, 'string')
})
test('WHAT[durable-events-007] PERSIST_005_unknown_case_is_a_decode_error', () => {
  const decoded = factCodec.decode('{"NoSuchFactCase":{"X":1}}')
  assert.equal(decoded.ok, false)
})
test('WHAT[durable-events-007] Fact_codec_distinguishes_current_and_malformed_lines', () => {
  const current = factCodec.decode(factCodec.encode(runtimeStarted()))
  assert.equal(current.ok, true, current.ok ? '' : current.error)
  assert.equal(current.case, 'RuntimeStarted')

  const malformed = factCodec.decode('{not json')
  assert.equal(malformed.ok, false)
  assert.equal(typeof malformed.error, 'string')
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

test('WHAT[durable-events-007] Journal_codec_refuses_unknown_facts_and_streams', () => {
  assert.throws(
    () => journalCodec.serialize(envelope({ stream: { kind: 'Unknown' } })),
    /unknown stream/i,
  )
  assert.throws(
    () => journalCodec.serialize(envelope({ fact: { family: 'Unknown', case: 'NoSuchFact', payload: {} } })),
    /unknown fact/i,
  )
})
}
