import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const Strength = await import("../../../dist/Strength/Surface.js");

test('WHAT[speculative-investigation-006] STRENGTH_006_prepared_commit_unknown_is_resolved_without_guessing', () => {
  assert.equal(Strength.commitResolvePrepared('Committed', 'Unknown'), 'Proceed')
  assert.equal(Strength.commitResolvePrepared('Rejected', 'Unknown'), 'FallBackNoDelegation')
  assert.equal(Strength.commitResolvePrepared('CommitUnknown', 'Matches'), 'Proceed')
  assert.equal(Strength.commitResolvePrepared('CommitUnknown', 'Absent'), 'FallBackNoDelegation')
  assert.equal(Strength.commitResolvePrepared('CommitUnknown', 'Unknown'), 'FailClosed')
  assert.equal(Strength.commitResolvePrepared('CommitUnknown', 'Conflicts'), 'FailClosed')
})
}

{
const { default: assert } = await import("node:assert/strict");
const { createHash } = await import("node:crypto");
const { default: test } = await import("node:test");
const Strength = await import("../../../dist/Strength/Surface.js");

const H = (text) => createHash('sha256').update(text).digest('hex')
const bundleFor = (toolName = 'read') => Strength.frameTryBuild(H, [{
  requestOrdinal: 1,
  exchanges: [{ toolName, canonicalArguments: toolName === 'read' ? '{"filePath":"a"}' : '{"pattern":"x"}', canonicalResult: toolName === 'read' ? 'alpha' : 'a:1:x' }],
}]).value
const request = (decisionId, rounds = 2) => Strength.eventRequested({
  decisionId,
  ownerSessionId: 'owner',
  ownerLogicalRun: { logicalRunId: 'logical-1', authorityRootUserMessageId: 'user-1' },
  sourcePhysicalUserMessageId: 'user-1',
  sourceProviderRun: 'run-1',
  sourceToolCallIds: ['call-1'],
  requestedRounds: rounds,
  contractRevision: 1,
})
const bound = (decisionId) => Strength.eventBound(decisionId, 'run-1', `replica-${decisionId}`, 'anchor-a')
const prepared = (bundle, decisionId = 'd1') => Strength.eventPrepared('owner', decisionId, 'run-1', `replica-${decisionId}`, 'anchor-a', bundle.digest, bundle.byteLength, [`p-${decisionId}`])
const promoted = (bundle, decisionId = 'd1') => Strength.eventPromoted('owner', decisionId, 'run-1', bundle.digest, [`p-${decisionId}`])
const apply = (state, event) => {
  const result = Strength.projectionApply(state, event)
  assert.equal(result.ok, true, result.error)
  return result.value
}
const refuse = (state, event) => {
  const result = Strength.projectionApply(state, event)
  assert.equal(result.ok, false)
  return result.error
}

test('WHAT[speculative-investigation-006] STRENGTH_006_requested_is_written_before_any_outbound_and_carries_the_full_authorization', () => {
  const bundle = bundleFor()
  const projection = apply(apply(Strength.projectionEmpty(), request('d1')), bound('d1'))
  const view = Strength.projectionCandidate('d1', projection)
  assert.equal(view.request.requestedRounds, 2)
  assert.equal(view.request.contractRevision, 1)
  assert.equal(view.request.sourceProviderRun, 'run-1')
  assert.deepEqual(view.request.sourceToolCallIds, ['call-1'])
  assert.equal(view.binding.targetProviderRun, 'run-1')
  assert.equal(view.binding.replicaSessionId, 'replica-d1')
  assert.equal(Strength.projectionRequestedRounds('d1', projection), 2)
  // The authorization is not re-derived from the visible history and not
  // copied into a mutable budget attached elsewhere. A stage that has not been
  // reached yet carries nothing at all.
  for (const attachment of ['binding', 'prepared']) {
    const attached = view[attachment]
    if (!attached) continue
    assert.equal('requestedRounds' in attached, false, `${attachment} must not carry a second budget`)
    assert.equal('budget' in attached, false)
  }
  apply(projection, prepared(bundle))
})
test('WHAT[speculative-investigation-006] STRENGTH_006_prepared_material_precedes_any_owner_visible_consumption', async () => {
  const bundle = bundleFor()
  const view = Strength.projectionCandidate('d1', apply(apply(apply(Strength.projectionEmpty(), request('d1')), bound('d1')), prepared(bundle)))
  assert.equal(view.state, 'Prepared')
  assert.equal(view.prepared.frameDigest, bundle.digest)
  assert.equal(view.prepared.byteLength, bundle.byteLength)
  assert.deepEqual(
    Object.keys(view.prepared).sort(),
    ['anchorDigest', 'byteLength', 'decisionId', 'frameDigest', 'materialPayloads', 'ownerSessionId', 'replicaSessionId', 'targetProviderRun'].sort(),
    'the durable Prepared write set rides the fact, not one field',
  )
})
test('WHAT[speculative-investigation-006] STRENGTH_006_prepared_before_bound_is_refused', () => {
  const bundle = bundleFor()
  assert.equal(refuse(Strength.projectionEmpty(), prepared(bundle)), 'PreparedWithoutBound')
  const requestedOnly = apply(Strength.projectionEmpty(), request('d1'))
  assert.equal(refuse(requestedOnly, prepared(bundle)), 'PreparedWithoutBound')
})
test('WHAT[speculative-investigation-006] STRENGTH_006_repeated_identical_request_and_bound_are_idempotent', () => {
  const bundle = bundleFor()
  let projection = apply(Strength.projectionEmpty(), request('d1'))
  projection = apply(projection, request('d1'))
  projection = apply(projection, bound('d1'))
  projection = apply(projection, bound('d1'))
  projection = apply(projection, prepared(bundle))
  projection = apply(projection, prepared(bundle))
  assert.equal(Strength.projectionCandidate('d1', projection).state, 'Prepared')
})
test('WHAT[speculative-investigation-006] STRENGTH_006_same_source_with_a_different_authorization_is_a_conflict', () => {
  const requested = apply(Strength.projectionEmpty(), request('d1', 2))
  assert.equal(refuse(requested, request('d1', 5)), 'RequestedConflict')
  const differentCalls = Strength.eventRequested({
    decisionId: 'd1', ownerSessionId: 'owner',
    ownerLogicalRun: { logicalRunId: 'logical-1', authorityRootUserMessageId: 'user-1' },
    sourcePhysicalUserMessageId: 'user-1', sourceProviderRun: 'run-1',
    sourceToolCallIds: ['call-1', 'call-2'], requestedRounds: 2, contractRevision: 1,
  })
  assert.equal(refuse(requested, differentCalls), 'RequestedConflict')
})
test('WHAT[speculative-investigation-006] STRENGTH_006_a_second_child_for_one_authorization_is_refused', () => {
  const boundOnce = apply(apply(Strength.projectionEmpty(), request('d1')), bound('d1'))
  assert.equal(refuse(boundOnce, Strength.eventBound('d1', 'run-1', 'replica-other', 'anchor-a')), 'BoundConflict')
  const other = apply(apply(Strength.projectionEmpty(), request('d2')), bound('d2'))
  assert.equal(refuse(other, Strength.eventBound('d3', 'run-1', 'replica-d3', 'anchor-a')), 'TargetAlreadyBound')
})
test('WHAT[speculative-investigation-006] STRENGTH_006_prepared_candidate_cannot_be_traced_or_raw_replayed', async () => {
  const bundle = bundleFor()
  const projection = apply(apply(apply(Strength.projectionEmpty(), request('d1')), bound('d1')), prepared(bundle))
  assert.equal(Strength.projectionIsPromoted('d1', projection), false)
  // A prepared candidate has no traced range and no replay plan yet: tracing
  // it early is refused, never silently accepted.
  const traced = Strength.projectionApply(projection, Strength.eventTraced('d1', 10n, 14n))
  assert.equal(traced.ok, false)
  const replay = await Strength.lifecycleReplayPlans('owner', [{ id: 'user-1' }, { id: 'run-1' }], bundle, projection)
  assert.equal(replay.ok, true)
  assert.equal(replay.value.length, 0)
})
}

{
const { default: assert } = await import("node:assert/strict");
const { createHash } = await import("node:crypto");
const { mkdtempSync, rmSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join } = await import("node:path");
const { default: test } = await import("node:test");
const Strength = await import("../../../dist/Strength/Surface.js");
const { createLocalEventStore } = await import("../../verification-system/tests/support/local-event-store.mjs");

const makeDir = (prefix) => mkdtempSync(join(tmpdir(), prefix))
const H = (text) => createHash('sha256').update(text).digest('hex')
const request = (decision) => Strength.eventRequested({
  decisionId: decision, ownerSessionId: 'owner',
  ownerLogicalRun: { logicalRunId: 'logical-1', authorityRootUserMessageId: 'user-1' },
  sourcePhysicalUserMessageId: 'user-1', sourceProviderRun: 'run-1',
  sourceToolCallIds: ['call-1'], requestedRounds: 2, contractRevision: 1,
})
const bundle = Strength.frameTryBuild(H, [{ requestOrdinal: 1, exchanges: [{ toolName: 'read', canonicalArguments: '{"filePath":"a"}', canonicalResult: 'alpha' }] }]).value
// A persisted frame payload is the Store wire contract (encodeFrameBundlePayload):
// version + digest + byte_length + batches[request_ordinal/exchanges[tool_name/arguments/result]].
// The JS frame shape is the Surface shape, not the payload shape; serializing it
// verbatim makes decodeFrameBundlePayload refuse the load.
const storeWirePayload = (frame, overrides = {}) => ({
  version: 1,
  digest: frame.digest,
  byte_length: frame.byteLength,
  batches: frame.batches.map((b) => ({
    request_ordinal: b.requestOrdinal,
    exchanges: b.exchanges.map((e) => ({ tool_name: e.toolName, arguments: e.canonicalArguments, result: e.canonicalResult })),
  })),
  ...overrides,
})
const prepared = (decision, ref) => Strength.eventPrepared('owner', decision, 'run-1', `replica-${decision}`, 'anchor-a', bundle.digest, bundle.byteLength, [ref])

test('WHAT[speculative-investigation-006] STRENGTH_006_durability_port_persists_the_full_chain_and_reloads_the_same_bundle', async () => {
  const local = createLocalEventStore()
  try {
    const durability = Strength.durabilityCreate(local.store)
    const ref = await Strength.storeWritePayload(local.store, new TextEncoder().encode(JSON.stringify(storeWirePayload(bundle))))
    assert.equal(ref.ok, true)
    for (const event of [
      request('d1'),
      Strength.eventBound('d1', 'run-1', 'replica-d1', 'anchor-a'),
      prepared('d1', ref.value),
    ]) {
      const appended = await Strength.durabilityAppend(durability, event)
      assert.equal(appended.ok, true, appended.error)
    }
    const projection = (await Strength.durabilityLoadProjection(durability)).value
    const view = Strength.projectionCandidate('d1', projection)
    assert.equal(view.prepared.frameDigest, bundle.digest)
    assert.equal(view.prepared.materialPayloads.length, 1)
    const loaded = await Strength.durabilityLoadBundleForDecision(durability, projection, 'd1')
    assert.equal(loaded.ok, true)
    assert.equal(loaded.value.digest, bundle.digest)
    assert.equal(loaded.value.byteLength, bundle.byteLength)
    assert.equal((await Strength.durabilityAppend(durability, Strength.eventPromoted('owner', 'd1', 'run-1', bundle.digest, view.prepared.materialPayloads))).ok, true)
    assert.equal((await Strength.durabilityAppend(durability, Strength.eventTraced('d1', 5n, 7n))).ok, true)
    const promoted = (await Strength.durabilityLoadProjection(durability)).value
    assert.equal(Strength.projectionIsPromoted('d1', promoted), true)
    assert.deepEqual(Strength.projectionTraceRange('d1', promoted), { startInclusive: 5n, endExclusive: 7n })
  } finally { local.close() }
})
test('WHAT[speculative-investigation-006] STRENGTH_006_durability_port_rejects_conflicting_Prepared_identity', async () => {
  const local = createLocalEventStore()
  try {
    const durability = Strength.durabilityCreate(local.store)
    const first = await Strength.storeWritePayload(local.store, new TextEncoder().encode(JSON.stringify(storeWirePayload(bundle))))
    assert.equal(first.ok, true)
    const other = Strength.frameTryBuild(H, [{ requestOrdinal: 1, exchanges: [{ toolName: 'grep', canonicalArguments: '{"pattern":"x"}', canonicalResult: 'a:1:x' }] }]).value
    const otherRef = await Strength.storeWritePayload(local.store, new TextEncoder().encode(JSON.stringify(storeWirePayload(other))))
    assert.equal((await Strength.durabilityAppend(durability, request('d1'))).ok, true)
    assert.equal((await Strength.durabilityAppend(durability, Strength.eventBound('d1', 'run-1', 'replica-d1', 'anchor-a'))).ok, true)
    assert.equal((await Strength.durabilityAppend(durability, prepared('d1', first.value))).ok, true)
    const conflict = await Strength.durabilityAppend(durability, prepared('d1', otherRef.value))
    assert.equal(conflict.ok, false)
    assert.equal(conflict.error, 'StorageInvalid')
  } finally { local.close() }
})
test('WHAT[speculative-investigation-006] prepared_cut_reopen_observes_only_durable_receipts', async () => {
  const base = makeDir('wxs-strength-cut-reopen-')
  const commonDir = join(base, '.git')
  const local = createLocalEventStore({ commonDir })
  try {
    const durability = Strength.durabilityCreate(local.store)
    const ref = await Strength.storeWritePayload(local.store, new TextEncoder().encode(JSON.stringify(storeWirePayload(bundle))))
    for (const event of [
      request('cut-d3'),
      Strength.eventBound('cut-d3', 'run-1', 'replica-cut-d3', 'anchor-a'),
      prepared('cut-d3', ref.value),
    ]) {
      assert.equal((await Strength.durabilityAppend(durability, event)).ok, true)
    }
  } finally { local.close() }

  const reopened = createLocalEventStore({ commonDir })
  try {
    const durability = Strength.durabilityCreate(reopened.store)
    const projection = (await Strength.durabilityLoadProjection(durability)).value
    const view = Strength.projectionCandidate('cut-d3', projection)
    assert.ok(view, 'reopen replays the durable Authorization receipt into Current')
    assert.equal(Strength.projectionRequestedRounds('cut-d3', projection), 2)
    const loaded = await Strength.durabilityLoadBundleForDecision(durability, projection, 'cut-d3')
    assert.equal(loaded.ok, true, 'payload closure survives reopen with the fact')
  } finally {
    reopened.close()
    rmSync(base, { recursive: true, force: true })
  }
})
test('WHAT[speculative-investigation-006] prepared_cut_has_no_optional_fatal_handler_path', async () => {
  const { readFileSync } = await import('node:fs')
  const source = readFileSync(
    new URL('../../../src/Wanxiangshu/Strength/Persistence/Durability.fs', import.meta.url),
    'utf8',
  )
  assert.doesNotMatch(source, /fatalTripHandler|setFatalTripHandler/)
})
}

{
const { default: assert } = await import("node:assert/strict");
const { createHash } = await import("node:crypto");
const { default: test } = await import("node:test");
const Strength = await import("../../../dist/Strength/Surface.js");
const { createLocalEventStore } = await import("../../verification-system/tests/support/local-event-store.mjs");

const H = (text) => createHash('sha256').update(text).digest('hex')
const request = () => Strength.eventRequested({
  decisionId: 'd1', ownerSessionId: 'owner',
  ownerLogicalRun: { logicalRunId: 'logical-1', authorityRootUserMessageId: 'user-1' },
  sourcePhysicalUserMessageId: 'user-1', sourceProviderRun: 'run-1',
  sourceToolCallIds: ['call-1'], requestedRounds: 2, contractRevision: 1,
})
const prepared = ({ refs = ['payload-a'], digest = 'frame-a', decision = 'd1' } = {}) => Strength.eventPrepared('owner', decision, 'run-1', 'replica', 'anchor-a', digest, 123, refs)
const promoted = ({ refs = ['payload-a'], digest = 'frame-a', decision = 'd1' } = {}) => Strength.eventPromoted('owner', decision, 'run-1', digest, refs)
const append = async (store, event) => Strength.storeAppend(store, H, event)
const writePayload = async (store, text) => {
  const result = await Strength.storeWritePayload(store, new TextEncoder().encode(text))
  assert.equal(result.ok, true)
  return result.value
}

test('WHAT[speculative-investigation-006] STRENGTH_006_017_strength_event_types_are_authoritative_store_vocabulary', () => {
  assert.deepEqual([
    Strength.eventType(request()),
    Strength.eventType(Strength.eventBound('d1', 'run-1', 'replica', 'anchor-a')),
    Strength.eventType(Strength.eventClosed('d1', 'Requested', 'Superseded')),
    Strength.eventType(prepared()),
    Strength.eventType(promoted()),
    Strength.eventType(Strength.eventTraced('d1', 1n, 2n)),
    Strength.eventType(Strength.eventAbandoned('d1', 'run-1')),
  ], ['DelegationRequested', 'DelegationBound', 'DelegationClosed', 'StrengthCandidatePrepared', 'StrengthCandidatePromoted', 'StrengthFramesTraced', 'StrengthCandidateAbandoned'])
})
test('WHAT[speculative-investigation-006] STRENGTH_006_store_envelope_puts_large_material_only_in_payload_refs', () => {
  const first = Strength.envelopeView(Strength.storeToEnvelope(H, prepared()))
  const conflicting = Strength.envelopeView(Strength.storeToEnvelope(H, prepared({ refs: ['payload-b'], digest: 'frame-b' })))
  assert.equal(first.eventType, 'StrengthCandidatePrepared')
  assert.deepEqual(first.payloadRefs, ['payload-a'])
  assert.equal(first.id, conflicting.id)
  const decoded = Strength.storeTryDecodeEnvelope(Strength.storeToEnvelope(H, request()))
  assert.equal(decoded.ok, true)
  assert.equal(decoded.value.kind, 'DelegationRequested')
  assert.equal(decoded.value.requestedRounds, 2)
})
test('WHAT[speculative-investigation-006] STRENGTH_006_same_decision_different_prepared_material_is_identity_collision', async () => {
  const local = createLocalEventStore()
  try {
    const firstRef = await writePayload(local.store, 'first')
    const secondRef = await writePayload(local.store, 'second')
    assert.equal((await append(local.store, request())).ok, true)
    assert.equal((await append(local.store, Strength.eventBound('d1', 'run-1', 'replica', 'anchor-a'))).ok, true)
    const first = Strength.eventPrepared('owner', 'd1', 'run-1', 'replica', 'anchor-a', 'frame-a', 5, [firstRef])
    const conflict = Strength.eventPrepared('owner', 'd1', 'run-1', 'replica', 'anchor-a', 'frame-b', 6, [secondRef])
    assert.equal((await append(local.store, first)).ok, true)
    const rejected = await append(local.store, conflict)
    assert.equal(rejected.ok, false)
    assert.equal(rejected.error, 'IdentityCollision')
  } finally { local.close() }
})
test('WHAT[speculative-investigation-006] STRENGTH_006_payload_bytes_are_local_content_addressed_payloads', async () => {
  const local = createLocalEventStore()
  try {
    const bytes = new Uint8Array([1, 2, 3, 4])
    const first = await Strength.storeWritePayload(local.store, bytes)
    const second = await Strength.storeWritePayload(local.store, bytes)
    assert.equal(first.value, second.value)
    const loaded = await Strength.storeReadPayload(local.store, first.value)
    assert.deepEqual([...loaded.value], [...bytes])
  } finally { local.close() }
})
test('WHAT[speculative-investigation-006] STRENGTH_006_integrator_Current_reflects_the_authorization_without_history_scan', async () => {
  const local = createLocalEventStore()
  try {
    const ref = await writePayload(local.store, 'frame-material')
    assert.equal((await append(local.store, request())).ok, true)
    assert.equal((await append(local.store, Strength.eventBound('d1', 'run-1', 'replica', 'anchor-a'))).ok, true)
    assert.equal((await append(local.store, Strength.eventPrepared('owner', 'd1', 'run-1', 'replica', 'anchor-a', 'frame-a', 14, [ref]))).ok, true)
    const projection = Strength.storeCurrent(local.store)
    assert.equal(Strength.projectionDecisionForTarget('run-1', projection), 'd1')
    assert.equal(Strength.projectionIsPromoted('d1', projection), false)
    assert.equal(Strength.projectionRequestedRounds('d1', projection), 2)
  } finally { local.close() }
})
test('WHAT[speculative-investigation-006] STRENGTH_006_authorization_payload_has_no_estimation_or_prediction_fields', () => {
  const envelope = Strength.storeToEnvelope(H, request())
  const decoded = Strength.storeTryDecodeEnvelope(envelope)
  assert.equal(decoded.ok, true)
  const payload = decoded.value
  assert.equal(payload.requestedRounds, 2)
  for (const forbidden of ['V0', 'V1', 'V2', 'P1', 'P2', 'estimate', 'prediction', 'score', 'cost', 'budget', 'tier']) {
    assert.equal(forbidden in payload, false, `the durable authorization must not carry ${forbidden}`)
  }
})
}

{
const {default: assert} = await import('node:assert/strict')
const {existsSync, mkdtempSync, realpathSync, readFileSync, rmSync} = await import('node:fs')
const {randomUUID} = await import('node:crypto')
const {tmpdir} = await import('node:os')
const {join} = await import('node:path')
const {fileURLToPath} = await import('node:url')
const {default: test} = await import('node:test')
const {runVerificationToolProbe} = await import('../../../scripts/lib/verification-tool-probe.mjs')
const child = fileURLToPath(new URL('./support/append-settlement-child.mjs', import.meta.url))
for (const scenario of ['store', 'append', 'prepared', 'duplicate']) {
  test(`WHAT[speculative-investigation-006] actual_${scenario}_release_failure_keeps_strength_settlement_evidence`, async t => {
    const root = realpathSync(mkdtempSync(join(tmpdir(), 'strength-settlement-')))
    const commonDir = join(root, '.git')
    const sourceWriter = randomUUID()
    const env = {...process.env}
    delete env.NODE_TEST_CONTEXT
    const probe = async (mode, writer, input) => {
      try {
        return JSON.parse(await runVerificationToolProbe(process.execPath,
          [child, mode, commonDir, writer, scenario, JSON.stringify(input)], {cwd: root, env, signal: t.signal}))
      } catch (error) {
        if (typeof error?.stderr === 'string') error.message += '\n' + error.stderr
        throw error
      }
    }
    try {
      const measured = await probe('measure', sourceWriter, {})
      assert.equal(measured.removals, 1)
      // durable-events-012: inline payload staging takes no store lock, so the
      // published Prepared releases the append gate exactly once on its own append.
      assert.equal(measured.releaseCalls, 1)
      assert.equal(measured.appendCalls, scenario === 'duplicate' ? 0 : 1)
      assert.deepEqual(measured.appendedTypes, scenario === 'duplicate' ? []
        : [scenario === 'prepared' ? 'StrengthCandidatePrepared' : 'DelegationRequested'])
      const cold = await probe('cold', randomUUID(), {sourceWriter, bytes: measured.bytes, event: measured.event})
      assert.notEqual(cold.pid, measured.pid)
      assert.deepEqual(cold.event, measured.event)
      assert.deepEqual(cold.candidate, measured.candidate)
      const eventsDir = join(commonDir, 'wanxiangshu', 'events')
      const actualFile = existsSync(join(eventsDir, `${sourceWriter}.ndjson`))
        ? join(eventsDir, `${sourceWriter}.ndjson`)
        : join(eventsDir, `${sourceWriter}-0000.ndjson`)
      assert.equal(readFileSync(actualFile, 'base64'), measured.bytes)
      t.diagnostic(JSON.stringify({scenario, physicalAndCold: true, measurePid: measured.pid, coldPid: cold.pid}))
      assert.equal(measured.threw, false, 'The consumer returns the settled outcome after the original Release failure')
      assert.equal(measured.causeSame, true)
      assert.equal(measured.result.eventId, measured.event.id)
      assert.equal(measured.result.kind ?? measured.result.ok, scenario === 'prepared' ? 'SettlementFailed' : false)
      assert.deepEqual(measured.result.settlement, {
        code: scenario === 'duplicate' ? 'NoNewWriteReleaseFailed' : 'CommitUnknown',
        phase: 'StoreRelease', cleanupFailures: [], requested: [measured.event],
        prepared: scenario === 'duplicate' ? null : {durableEvents: [measured.event], cuts: []}, priorRejection: null,
      })
    } finally { rmSync(root, {recursive: true, force: true}) }
  })
}
}
