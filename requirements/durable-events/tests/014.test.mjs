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

test('WHAT[durable-events-014] PERSIST_001_ordering_is_by_local_seq_inside_a_runtime_and_by_time_across', () => {
  const a1 = env({ runtime: 'rt_a', seq: 1, observedAt: '2026-01-01T00:00:09Z' })
  const a2 = env({ runtime: 'rt_a', seq: 2, observedAt: '2026-01-01T00:00:00Z' })
  assert.equal(journalCodec.compareSortKey(a1, a2) < 0, true)
  assert.equal(journalCodec.compareSortKey(a2, a1) > 0, true)
  assert.equal(journalCodec.compareSortKey(a1, a1), 0)

  const b1 = env({ runtime: 'rt_b', seq: 1, observedAt: '2026-01-01T00:00:05Z' })
  assert.equal(journalCodec.compareSortKey(a1, b1) > 0, true)
  assert.equal(journalCodec.compareSortKey(b1, a1) < 0, true)
})
test('WHAT[durable-events-014] PERSIST_001_same_instant_across_runtimes_breaks_the_tie_by_runtime_id', () => {
  const at = '2026-01-01T00:00:00Z'
  const a = env({ runtime: 'rt_a', seq: 1, observedAt: at })
  const b = env({ runtime: 'rt_b', seq: 1, observedAt: at })
  assert.equal(journalCodec.compareSortKey(a, b) < 0, true)
  assert.equal(journalCodec.compareSortKey(b, a) > 0, true)
})
test('WHAT[durable-events-014] PERSIST_001_k_way_merge_is_a_total_order_regardless_of_input_order', () => {
  const at = (s) => `2026-01-01T00:00:0${s}Z`
  const streamA = [
    env({ runtime: 'rt_a', seq: 1, observedAt: at(1) }),
    env({ runtime: 'rt_a', seq: 2, observedAt: at(4) }),
  ]
  const streamB = [
    env({ runtime: 'rt_b', seq: 1, observedAt: at(2) }),
    env({ runtime: 'rt_b', seq: 2, observedAt: at(3) }),
  ]
  const label = (merged) => merged.map((value) => `${value.runtime}#${value.seq}`)
  const expected = ['rt_a#1', 'rt_b#1', 'rt_b#2', 'rt_a#2']

  assert.deepEqual(label(journalCodec.kWayMerge([streamA, streamB])), expected)
  assert.deepEqual(label(journalCodec.kWayMerge([streamB, streamA])), expected)
  assert.deepEqual(label(journalCodec.kWayMerge([[], streamA, [], streamB])), expected)
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

test('WHAT[durable-events-014] DURABLE_EVENTS_014_k_way_merge_is_deterministic_with_EventId_tiebreak', () => {
  const root = make({ id: A })
  const high = make({ id: C, parents: [A], eventType: 'JobAccepted' })
  const low = make({ id: B, parents: [A], eventType: 'JobRejected' })

  const first = eventMerge.merge([
    ['writer-a', [root, high]],
    ['writer-b', [low]],
  ])
  const second = eventMerge.merge([
    ['writer-b', [low]],
    ['writer-a', [root, high]],
  ])

  assert.equal(first.ok, true)
  assert.equal(second.ok, true)
  assert.deepEqual(first.events.map((e) => e.id), [A, B, C])
  assert.deepEqual(second.events.map((e) => e.id), first.events.map((e) => e.id))
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

test('WHAT[durable-events-014] DURABLE_EVENTS_014_k_way_merge_is_writer_enumeration_independent', () => {
  const a = envelope('0'.repeat(39) + 'a')
  const b = envelope('0'.repeat(39) + 'b')
  const c = envelope('0'.repeat(39) + 'c', ['0'.repeat(39) + 'a'])

  const left = eventMerge.merge([
    ['writer-a', [a, c]],
    ['writer-b', [b]],
  ])
  const right = eventMerge.merge([
    ['writer-b', [b]],
    ['writer-a', [a, c]],
  ])

  assert.equal(left.ok, true)
  assert.equal(right.ok, true)
  assert.deepEqual(left.events.map((e) => e.id), right.events.map((e) => e.id))
})
test('WHAT[durable-events-014] k-way merge bounds actual ready-cursor comparisons', (t) => {
  const writers = 512
  const eventsPerWriter = 16
  let nextId = 0
  const streams = Array.from({ length: writers }, (_, writer) => [
    `writer-${String(writer).padStart(4, '0')}`,
    Array.from({ length: eventsPerWriter }, (_, offset) =>
      envelope((nextId++).toString(16).padStart(40, '0'), [], 'proof/merge', { writer, offset })),
  ])
  const expected = streams.flatMap(([, events]) => events)
  const eventCount = writers * eventsPerWriter
  const heapHeight = Math.ceil(Math.log2(writers))
  const comparisonBound = BigInt(3 * eventCount * heapHeight + 2 * writers)

  assert.equal(typeof eventMerge.mergeWithDiagnostics, 'function',
    'the native merge owner must report comparisons from its actual execution')

  const started = performance.now()
  const merged = eventMerge.mergeWithDiagnostics(streams)
  const elapsedMs = performance.now() - started
  t.diagnostic(`${eventCount}-event / ${writers}-writer merge wall time: ${elapsedMs.toFixed(1)}ms`)

  assert.equal(merged.ok, true)
  assert.equal(merged.events.length, eventCount)
  assert.deepEqual(merged.events, expected)
  assert.equal(new Set(merged.events.map((event) => event.id)).size, eventCount)
  assert.equal(typeof merged.diagnostics?.readyComparisons, 'string')
  assert.match(merged.diagnostics.readyComparisons, /^(0|[1-9][0-9]*)$/)
  const comparisons = BigInt(merged.diagnostics.readyComparisons)
  assert.ok(comparisons > 0n, 'a nontrivial merge must observe actual ready-cursor comparisons')
  t.diagnostic(`actual ready-cursor comparisons: ${comparisons}; heap bound: ${comparisonBound}`)
  assert.ok(comparisons <= comparisonBound,
    `${eventCount}-event / ${writers}-writer merge used ${comparisons} ready-cursor comparisons; bound ${comparisonBound}`)
})
}
