import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const routing = await import("../../../dist/OpenCode/Host/ModelRoutingSurface.js");

const target = { model: 'provider/shared', reasoning: 'none' }
const identity = (sessionId, physicalUserMessageId, role = 'engineer', participant = `${sessionId}-owner`) => ({
  sessionId,
  physicalUserMessageId,
  role,
  participant,
  target,
})
const acquire = async (runtime, exact) => {
  const outcome = await routing.acquireExecutionAdmission(
    runtime,
    exact.sessionId,
    exact.physicalUserMessageId,
    exact.role,
    exact.participant,
    null,
  )
  assert.equal(outcome.kind, 'Acquired')
  return outcome.lease
}
const exactKey = ({ sessionId, physicalUserMessageId }) => `${sessionId}\u001f${physicalUserMessageId}`

test('WHAT[execution-model-routing-014] valid immutable snapshot is a reconciliation no-op with traceable tokens and waiters', async () => {
  const runtime = routing.createRuntime((_role, running) => (running.length === 0 ? target : null))
  const holder = identity('holder', 'physical-holder')
  const holderLease = await acquire(runtime, holder)
  assert.deepEqual(routing.commitExecutionAdmission(runtime, holderLease, holder), { kind: 'Applied' })
  const queued = await routing.beginExecutionAdmission(runtime, 'waiting', 'physical-waiting', 'manager', 'waiting-owner', null)
  assert.equal(queued.kind, 'Queued')

  const snapshot = routing.capacitySnapshot(runtime)
  assert.equal(Object.isFrozen(snapshot), true)
  assert.equal(Object.isFrozen(snapshot.tokens), true)
  assert.equal(Object.isFrozen(snapshot.tokens[0]), true)
  assert.equal(Object.isFrozen(snapshot.waiters), true)
  assert.equal(snapshot.ledgerEntries.length, 1)
  assert.ok(snapshot.activeCount >= 0 && snapshot.activeCount <= snapshot.ledgerEntries.length)
  assert.deepEqual(snapshot.tokenStateCounts, { idle: 1, inFlight: 0, retiring: 0 })

  const executionKeys = new Set(snapshot.executions.map(exactKey))
  for (const token of snapshot.tokens) assert.ok(executionKeys.has(exactKey(token.owner)), 'token owner is exact and traceable')
  for (const waiter of snapshot.waiters)
    assert.ok(snapshot.owners.some((owner) => exactKey(owner) === exactKey(waiter)), 'waiter owner is traceable')
  assert.deepEqual(snapshot.lineage, [], 'explicit lender borrowing leaves no ambient lineage edge')
  assert.deepEqual(routing.reconcileCapacityEvidence(snapshot), { kind: 'NoOp' })
  assert.deepEqual(routing.capacitySnapshot(runtime), snapshot, 'reconciliation is read-only')

  assert.throws(() => snapshot.tokens.push({}), TypeError)
  assert.deepEqual(routing.cancelPendingExecution(runtime, 'waiting'), { kind: 'Applied' })
})
test('WHAT[execution-model-routing-014] duplicate release never decrements twice', async () => {
  const runtime = routing.createRuntime(() => target)
  const firstIdentity = identity('session', 'physical-1')
  await acquire(runtime, firstIdentity)

  assert.deepEqual(routing.releasePhysicalExecution(runtime, 'session', 'physical-1'), { kind: 'Applied' })
  const released = routing.capacitySnapshot(runtime)
  assert.equal(released.ledgerEntries.length, 0)
  assert.deepEqual(routing.releasePhysicalExecution(runtime, 'session', 'physical-1'), { kind: 'AlreadyApplied' })
  const duplicate = routing.capacitySnapshot(runtime)
  assert.equal(duplicate.ledgerEntries.length, 0)
  assert.equal(duplicate.counters.duplicate, released.counters.duplicate + 1)
})
test('WHAT[execution-model-routing-014] stale fence cannot touch a newer execution', async () => {
  const runtime = routing.createRuntime(() => target)
  const firstIdentity = identity('session', 'physical-1')
  const first = await acquire(runtime, firstIdentity)
  const secondIdentity = identity('session', 'physical-2')
  const second = await acquire(runtime, secondIdentity)
  const before = routing.capacitySnapshot(runtime)
  assert.deepEqual(routing.commitExecutionAdmission(runtime, first, firstIdentity), { kind: 'StaleFence' })
  const stale = routing.capacitySnapshot(runtime)
  assert.equal(stale.counters.stale, before.counters.stale + 1)
  assert.ok(stale.executions.some((owner) => exactKey(owner) === exactKey(secondIdentity)))
  assert.deepEqual(routing.commitExecutionAdmission(runtime, second, secondIdentity), { kind: 'Applied' })
})
test('WHAT[execution-model-routing-014] opposite terminal transition is a monotonic conflict', async () => {
  const runtime = routing.createRuntime(() => target)
  const exact = identity('session', 'physical')
  const lease = await acquire(runtime, exact)
  assert.deepEqual(routing.commitExecutionAdmission(runtime, lease, exact), { kind: 'Applied' })
  const before = routing.capacitySnapshot(runtime)
  assert.deepEqual(routing.releaseExecutionAdmissionBeforeProvider(runtime, lease, exact), { kind: 'Conflict' })
  const conflict = routing.capacitySnapshot(runtime)
  assert.equal(conflict.counters.conflict, before.counters.conflict + 1)
})
test('WHAT[execution-model-routing-014] reconciliation fails closed on map ledger divergence without repair', async () => {
  const runtime = routing.createRuntime(() => target)
  await acquire(runtime, identity('holder', 'physical-holder'))
  const valid = routing.capacitySnapshot(runtime)
  const divergent = { ...valid, ledgerEntries: [] }

  const decision = routing.reconcileCapacityEvidence(divergent)
  assert.equal(decision.kind, 'FailClosed')
  assert.ok(decision.reasons.includes('MapLedgerDivergence'))
  assert.deepEqual(routing.capacitySnapshot(runtime), valid, 'diagnostic reconciliation never repairs production state')
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const routing = await import("../../../dist/OpenCode/Host/ModelRoutingSurface.js");

const seed = 0x36c0ffee
const rounds = 32
const queueWidth = 32
const lineageCycles = 64
const capacity = 4
const admissionRetainedBound = 84
const ELIGIBLE = 'engineer'
const BLOCKED = 'devops'
const lineageRetainedComposition = Object.freeze({
  ledgerEntries: 1,
  token: 1,
  ownerAndBorrowerCustodies: 2,
  executions: 2,
  pendingProviderWaiter: 1,
  owners: 2,
  lineage: 0,
})
const lineageRetainedBound = Object.values(lineageRetainedComposition).reduce((sum, count) => sum + count, 0)
const target = { model: 'provider/shared', reasoning: 'none' }
const exactKey = ({ sessionId, physicalUserMessageId }) => `${sessionId}\u001f${physicalUserMessageId}`
const seeded = (initial) => {
  let state = initial >>> 0
  return () => {
    state ^= state << 13
    state ^= state >>> 17
    state ^= state << 5
    return state >>> 0
  }
}
const shuffled = (count, next) => {
  const values = Array.from({ length: count }, (_, index) => index)
  for (let index = values.length - 1; index > 0; index -= 1) {
    const other = next() % (index + 1)
    ;[values[index], values[other]] = [values[other], values[index]]
  }
  return values
}
const createAuditor = (runtime, retainedBound) => {
  let operations = 0
  let maxRetained = 0
  let previousCounters = { duplicate: 0, stale: 0, conflict: 0 }

  const audit = () => {
    const snapshot = routing.capacitySnapshot(runtime)
    const admissionWaiters = snapshot.waiters.filter((waiter) => waiter.kind === 'Admission')
    const ownerKeys = new Set(snapshot.owners.map(exactKey))
    const ledgerByCredit = new Map(snapshot.ledgerEntries.map((entry) => [entry.credit, entry]))
    const ledgerCredits = new Set(ledgerByCredit.keys())
    const tokenCredits = new Set(snapshot.tokens.map((token) => token.credit))
    const custodyEdges = new Set()

    assert.equal(Object.isFrozen(snapshot), true)
    assert.equal(Object.isFrozen(snapshot.tokens), true)
    assert.ok(snapshot.activeCount >= 0 && snapshot.activeCount <= snapshot.ledgerEntries.length)
    assert.equal(
      snapshot.tokenStateCounts.idle + snapshot.tokenStateCounts.inFlight + snapshot.tokenStateCounts.retiring,
      snapshot.tokens.length,
    )
    assert.equal(snapshot.activeCount, snapshot.tokenStateCounts.inFlight + snapshot.tokenStateCounts.retiring)
    assert.equal(snapshot.tokens.length, snapshot.ledgerEntries.length)
    assert.equal(ledgerCredits.size, snapshot.ledgerEntries.length)
    assert.equal(tokenCredits.size, snapshot.tokens.length)
    assert.equal(admissionWaiters.length, routing.pendingCount(runtime))
    assert.ok(admissionWaiters.length <= routing.pendingBound(runtime))
    assert.equal(routing.pendingBound(runtime), 32)

    for (const token of snapshot.tokens) {
      assert.ok(ledgerCredits.has(token.credit), 'every token traces to one ledger credit')
      assert.deepEqual(token.target, ledgerByCredit.get(token.credit).target, 'token and ledger target are exact')
      assert.ok(ownerKeys.has(exactKey(token.owner)), 'every token traces to one exact owner')
    }
    for (const custody of snapshot.custodies) {
      assert.ok(tokenCredits.has(custody.credit), 'every custody traces to one token')
      assert.ok(custody.owner.sessionId.length > 0 && custody.owner.physicalUserMessageId.length > 0)
      assert.ok(ownerKeys.has(exactKey(custody.owner)), 'every custody traces to one exact owner')
      const edge = `${custody.credit}\u001f${exactKey(custody.owner)}`
      assert.equal(custodyEdges.has(edge), false, 'custody edges are exact and unique')
      custodyEdges.add(edge)
    }
    for (const execution of snapshot.executions)
      assert.ok(ownerKeys.has(exactKey(execution)), 'every execution traces to one exact owner')
    for (const waiter of snapshot.waiters)
      assert.ok(ownerKeys.has(exactKey(waiter)), 'every waiter traces to one exact owner')

    for (const name of ['duplicate', 'stale', 'conflict'])
      assert.ok(snapshot.counters[name] >= previousCounters[name], `${name} counter is monotonic`)
    previousCounters = snapshot.counters

    assert.deepEqual(routing.reconcileCapacityEvidence(snapshot), { kind: 'NoOp' })
    assert.deepEqual(routing.capacitySnapshot(runtime), snapshot, 'reconciliation never mutates owner state')

    const retained =
      snapshot.ledgerEntries.length +
      snapshot.tokens.length +
      snapshot.custodies.length +
      snapshot.executions.length +
      snapshot.waiters.length +
      snapshot.owners.length +
      snapshot.lineage.length
    assert.ok(retained <= 6 * snapshot.owners.length + snapshot.lineage.length)
    if (retainedBound !== undefined) assert.ok(retained <= retainedBound)
    maxRetained = Math.max(maxRetained, retained)
    return snapshot
  }

  return {
    operation() {
      operations += 1
      return audit()
    },
    report() {
      return { operations, maxRetained }
    },
  }
}
const begin = (runtime, sessionId, physicalUserMessageId, role, participant, lenderSessionId = null) =>
  routing.beginExecutionAdmission(runtime, sessionId, physicalUserMessageId, role, participant, lenderSessionId)
const observedIdentity = (runtime, lease, record) => ({
  sessionId: record.sessionId,
  physicalUserMessageId: record.physicalUserMessageId,
  role: record.role,
  participant: record.participant,
  target: routing.executionAdmissionTarget(runtime, lease),
})

test('WHAT[execution-model-routing-014] seeded bounded admission soak preserves fairness and exact reconciliation after every operation', async (context) => {
  const next = seeded(seed)
  let blockedEligible = false
  const runtime = routing.createRuntime((role, running) => {
    if (running.length >= capacity) return null
    if (role === BLOCKED && !blockedEligible) return null
    return target
  })
  const auditor = createAuditor(runtime, admissionRetainedBound)
  const records = new Map()
  const active = []

  const commit = (record, lease) => {
    const observed = observedIdentity(runtime, lease, record)
    assert.deepEqual(routing.commitExecutionAdmission(runtime, lease, observed), { kind: 'Applied' })
    auditor.operation()
    active.push(record)
    return observed
  }

  const release = (record) => {
    assert.deepEqual(
      routing.releasePhysicalExecution(runtime, record.sessionId, record.physicalUserMessageId),
      { kind: 'Applied' },
    )
    auditor.operation()
  }

  for (let round = 0; round < rounds; round += 1) {
    blockedEligible = false
    active.length = 0
    records.clear()

    for (let index = 0; index < capacity; index += 1) {
      const record = {
        sessionId: `holder-${index}`,
        physicalUserMessageId: `holder-${round}-${index}`,
        role: 'manager',
        participant: `holder-owner-${index}`,
      }
      const outcome = await begin(runtime, record.sessionId, record.physicalUserMessageId, record.role, record.participant)
      assert.equal(outcome.kind, 'Acquired')
      auditor.operation()
      commit(record, outcome.lease)
    }

    const staleRecord = active.shift()
    const staleLeaseOutcome = await begin(
      runtime,
      staleRecord.sessionId,
      staleRecord.physicalUserMessageId,
      staleRecord.role,
      staleRecord.participant,
    )
    assert.equal(staleLeaseOutcome.kind, 'Acquired')
    auditor.operation()
    const staleIdentity = observedIdentity(runtime, staleLeaseOutcome.lease, staleRecord)
    release(staleRecord)
    const replacement = { ...staleRecord, physicalUserMessageId: `${staleRecord.physicalUserMessageId}-new` }
    const replacementOutcome = await begin(
      runtime,
      replacement.sessionId,
      replacement.physicalUserMessageId,
      replacement.role,
      replacement.participant,
    )
    assert.equal(replacementOutcome.kind, 'Acquired')
    auditor.operation()
    assert.deepEqual(
      routing.commitExecutionAdmission(
        runtime,
        staleLeaseOutcome.lease,
        staleIdentity,
      ),
      { kind: 'StaleFence' },
    )
    auditor.operation()
    commit(replacement, replacementOutcome.lease)

    const committed = active[0]
    const committedLease = await begin(
      runtime,
      committed.sessionId,
      committed.physicalUserMessageId,
      committed.role,
      committed.participant,
    )
    assert.equal(committedLease.kind, 'Acquired')
    auditor.operation()
    assert.deepEqual(
      routing.releaseExecutionAdmissionBeforeProvider(
        runtime,
        committedLease.lease,
        observedIdentity(runtime, committedLease.lease, committed),
      ),
      { kind: 'Conflict' },
    )
    auditor.operation()

    const order = shuffled(queueWidth, next)
    for (let position = 0; position < order.length; position += 1) {
      const index = order[position]
      const record = {
        sessionId: `waiter-${index}`,
        physicalUserMessageId: `waiter-${round}-${index}`,
        role:
          position === 0 || (position !== 1 && (next() & 3) === 0)
            ? BLOCKED
            : ELIGIBLE,
        participant: `waiter-owner-${index}`,
      }
      const outcome = await begin(runtime, record.sessionId, record.physicalUserMessageId, record.role, record.participant)
      assert.equal(outcome.kind, 'Queued')
      auditor.operation()
      record.queue = outcome.queue
      records.set(exactKey(record), record)
    }

    for (const index of shuffled(queueWidth, next).slice(0, 3)) {
      const current = [...records.values()].find((record) => record.sessionId === `waiter-${index}`)
      if (!current) continue
      const replacementRecord = {
        ...current,
        physicalUserMessageId: `${current.physicalUserMessageId}-new`,
      }
      const replacementOutcome = await begin(
        runtime,
        replacementRecord.sessionId,
        replacementRecord.physicalUserMessageId,
        replacementRecord.role,
        replacementRecord.participant,
      )
      assert.equal(replacementOutcome.kind, 'Queued')
      auditor.operation()
      assert.equal((await routing.awaitQueuedExecutionAdmission(current.queue)).kind, 'Superseded')
      records.delete(exactKey(current))
      replacementRecord.queue = replacementOutcome.queue
      records.set(exactKey(replacementRecord), replacementRecord)
    }

    const overflow = await begin(runtime, `overflow-${round}`, `overflow-physical-${round}`, ELIGIBLE, 'overflow-owner')
    assert.deepEqual(
      { kind: overflow.kind, failure: overflow.failure },
      { kind: 'QueueFull', failure: 'CapacityQueueFull' },
    )
    assert.equal(routing.pendingCount(runtime), 32)
    auditor.operation()

    const cancellationRecords = shuffled(queueWidth, next)
      .map((index) => [...records.values()].find((candidate) => candidate.sessionId === `waiter-${index}`))
      .filter((record) => record?.role === ELIGIBLE)
      .slice(0, 4)
    assert.equal(cancellationRecords.length, 4)
    for (const record of cancellationRecords) {
      assert.deepEqual(routing.cancelPendingExecution(runtime, record.sessionId), { kind: 'Applied' })
      auditor.operation()
      assert.equal((await routing.awaitQueuedExecutionAdmission(record.queue)).kind, 'Cancelled')
      records.delete(exactKey(record))
      assert.deepEqual(routing.cancelPendingExecution(runtime, record.sessionId), { kind: 'AlreadyApplied' })
      auditor.operation()
    }

    const admitOldestEligible = async (eligible) => {
      const snapshot = routing.capacitySnapshot(runtime)
      const expected = snapshot.waiters
        .filter((waiter) => waiter.kind === 'Admission' && eligible(waiter.role))
        .sort((left, right) => left.sequence - right.sequence)[0]
      if (!expected) return false
      const released = active.shift()
      assert.ok(released, 'finite eligible schedule always has a releasable capacity owner')
      release(released)
      const record = records.get(exactKey(expected))
      const outcome = await routing.awaitQueuedExecutionAdmission(record.queue)
      assert.equal(outcome.kind, 'Acquired')
      records.delete(exactKey(record))
      commit(record, outcome.lease)
      return true
    }

    while (await admitOldestEligible((role) => role === ELIGIBLE)) {}
    assert.ok(records.size > 0, 'the seeded schedule retains an ineligible head')

    blockedEligible = true
    const duplicateCandidate = active[0]
    while (records.size > 0) assert.equal(await admitOldestEligible(() => true), true)
    while (active.length > 0) release(active.shift())

    assert.deepEqual(
      routing.releasePhysicalExecution(
        runtime,
        duplicateCandidate.sessionId,
        duplicateCandidate.physicalUserMessageId,
      ),
      { kind: 'AlreadyApplied' },
    )
    const drained = auditor.operation()
    assert.equal(routing.pendingCount(runtime), 0)
    assert.equal(drained.ledgerEntries.length, 0)
    assert.equal(drained.tokens.length, 0)
    assert.equal(drained.custodies.length, 0)
    assert.equal(drained.executions.length, 0)
    assert.equal(drained.waiters.length, 0)
    assert.equal(drained.owners.length, 0)
  }

  const impossibleRuntime = routing.createRuntime(() => target)
  const impossibleAuditor = createAuditor(impossibleRuntime)
  const exact = {
    sessionId: 'impossible-holder',
    physicalUserMessageId: 'impossible-physical',
    role: ELIGIBLE,
    participant: 'impossible-owner',
  }
  const acquired = await begin(impossibleRuntime, exact.sessionId, exact.physicalUserMessageId, exact.role, exact.participant)
  assert.equal(acquired.kind, 'Acquired')
  const valid = impossibleAuditor.operation()
  const impossible = { ...valid, ledgerEntries: [] }
  const decision = routing.reconcileCapacityEvidence(impossible)
  assert.equal(decision.kind, 'FailClosed')
  assert.ok(decision.reasons.includes('MapLedgerDivergence'))
  assert.deepEqual(routing.capacitySnapshot(impossibleRuntime), valid, 'impossible evidence cannot repair owner state')

  const report = auditor.report()
  context.diagnostic(
    `task36 admission seed=${seed} rounds=${rounds} queueWidth=${queueWidth} operations=${report.operations} maxRetained=${report.maxRetained} retainedBound=${admissionRetainedBound}`,
  )
})
}
