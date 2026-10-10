import test from 'node:test'

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

test('WHAT[execution-model-routing-010] seeded lender soak shares one physical credit without retained settlement nodes', async (context) => {
  const runtime = routing.createRuntime((_role, running) => (running.length === 0 ? target : null))
  const auditor = createAuditor(runtime, lineageRetainedBound)

  for (let cycle = 0; cycle < lineageCycles; cycle += 1) {
    const parent = {
      sessionId: `parent-${cycle}`,
      physicalUserMessageId: `parent-physical-${cycle}`,
      role: 'engineer',
      participant: `parent-owner-${cycle}`,
    }
    const child = {
      sessionId: `child-${cycle}`,
      physicalUserMessageId: `child-physical-${cycle}`,
      role: 'manager',
      participant: `child-owner-${cycle}`,
    }

    const parentOutcome = await begin(runtime, parent.sessionId, parent.physicalUserMessageId, parent.role, parent.participant)
    assert.equal(parentOutcome.kind, 'Acquired')
    auditor.operation()
    assert.deepEqual(
      routing.commitExecutionAdmission(runtime, parentOutcome.lease, observedIdentity(runtime, parentOutcome.lease, parent)),
      { kind: 'Applied' },
    )
    auditor.operation()

    const childOutcome = await begin(
      runtime,
      child.sessionId,
      child.physicalUserMessageId,
      child.role,
      child.participant,
      parent.sessionId,
    )
    assert.equal(childOutcome.kind, 'Acquired')
    auditor.operation()
    assert.deepEqual(
      routing.commitExecutionAdmission(runtime, childOutcome.lease, observedIdentity(runtime, childOutcome.lease, child)),
      { kind: 'Applied' },
    )
    auditor.operation()
    assert.equal(routing.capacitySnapshot(runtime).ledgerEntries.length, 1, 'explicit lender borrowing does not duplicate capacity')
    assert.deepEqual(routing.capacitySnapshot(runtime).lineage, [], 'borrowing leaves no ambient lineage edge')

    await routing.enterProviderStep(runtime, child.sessionId, child.physicalUserMessageId, [])
    auditor.operation()
    assert.equal(routing.capacitySnapshot(runtime).activeCount, 1, 'the borrowed step holds the one real credit')
    routing.endProviderStep(runtime, child.sessionId, child.physicalUserMessageId, `child-run-${cycle}`)
    auditor.operation()
    assert.equal(routing.capacitySnapshot(runtime).activeCount, 0, 'the causal step end releases the borrowed credit')
    routing.endProviderStep(runtime, child.sessionId, child.physicalUserMessageId, `child-run-${cycle}`)
    auditor.operation()
    assert.equal(routing.capacitySnapshot(runtime).activeCount, 0, 'a duplicate old end cannot release anything twice')

    assert.deepEqual(routing.releasePhysicalExecution(runtime, child.sessionId, child.physicalUserMessageId), {
      kind: 'Applied',
    })
    auditor.operation()
    assert.deepEqual(routing.releasePhysicalExecution(runtime, parent.sessionId, parent.physicalUserMessageId), {
      kind: 'Applied',
    })
    auditor.operation()
    const drained = auditor.operation()
    assert.equal(drained.ledgerEntries.length, 0)
    assert.equal(drained.tokens.length, 0)
    assert.equal(drained.custodies.length, 0)
    assert.equal(drained.executions.length, 0)
    assert.equal(drained.waiters.length, 0)
    assert.equal(drained.owners.length, 0)
    assert.equal(drained.lineage.length, 0)
  }

  const report = auditor.report()
  context.diagnostic(
    `task36 lender seed=${seed} cycles=${lineageCycles} operations=${report.operations} maxRetained=${report.maxRetained} retainedBound=${lineageRetainedBound} retainedComposition=${JSON.stringify(lineageRetainedComposition)}`,
  )
})
}

{
const { default: assert } = await import("node:assert/strict");
const { readdir, readFile } = await import("node:fs/promises");
const { join } = await import("node:path");
const { fileURLToPath } = await import("node:url");
const { default: test } = await import("node:test");

const repoRoot = fileURLToPath(new URL('../../../', import.meta.url))
const srcRoot = join(repoRoot, 'src/Wanxiangshu')
const toRelative = (absPath) => {
  const rel = absPath.startsWith(repoRoot) ? absPath.slice(repoRoot.length) : absPath
  return rel.replace(/\\/g, '/')
}
async function getAllProductionFsFiles(dir) {
  const entries = await readdir(dir, { withFileTypes: true })
  const nested = await Promise.all(
    entries.map(async (entry) => {
      const fullPath = join(dir, entry.name)
      if (entry.isDirectory()) {
        return getAllProductionFsFiles(fullPath)
      } else if (entry.isFile() && entry.name.endsWith('.fs')) {
        return [fullPath]
      }
      return []
    })
  )
  return nested.flat()
}
const EXCLUSIVE_PRIVATE_KNOWLEDGE = Object.freeze([
  // Private types & DU states
  'CapacityStep',
  'CapacityCreditState',
  'CapacityCredit',
  'CapacityStepDemand',
  'CapacityCreditSource',

  // Private mutable state / dictionary resources
  'ownedTokenByExecution',
  'creditSourceByExecution',
  'nextCapacityDemandSequence',

  // Private borrowing, recall & reservation algorithm helpers
  'currentCreditSource',
  'clearCreditSource',
  'clearCreditSourcesForToken',
  'clearCreditSourcesForSession',
  'rememberCreditSource',
  'moveCreditSource',
  'isRetiring',
  'releaseToken',
  'retireToken',
  'retireTokenId',
  'retireExecution',
  'creditTokens',
  'withoutTokens',
  'schedulingView',
  'capacityOrdinaryDecision',
  'attributedDecision',
  'matchingCreditDecision',
  'routeDecision',
  'acquireOwnedToken',
  'moveOwnedToken',
  'finishStep',
  'reconcileFence',
  'tryGrantOwned',
  'tryGrantBorrowed',
  'demandOwnsToken',
  'tryGrantOrdinary',
  'tryGrantDemand',
  'acquireForRoute',
  'recordRoutedCredit',
  'applyRoutedToken',
  'commitRoutedTarget',
  'ensureReservationToken',
  'recordReservationCredit',
  'adoptOwnedToken',
])
const CONTROLLED_PUBLIC_TYPES = Object.freeze([
  'CapacityLedger',
  'BorrowingCapacity',
])
const OWNER_FILES = Object.freeze([
  'src/Wanxiangshu/OpenCode/Host/ModelCapacity/Model.fs',
  'src/Wanxiangshu/OpenCode/Host/ModelCapacity/Ledger.fs',
  'src/Wanxiangshu/OpenCode/Host/ModelCapacity/Queue.fs',
  'src/Wanxiangshu/OpenCode/Host/ModelCapacity/Borrowing.fs',
  'src/Wanxiangshu/OpenCode/Host/ModelCapacity/Surface.fs',
])
const PERMITTED_CONSUMER_FILES = Object.freeze([
  ...OWNER_FILES,
  'src/Wanxiangshu/OpenCode/Host/ModelRouting.fs',
])

test('WHAT[execution-model-routing-010] EMR_010_model_capacity_owner_defines_all_private_borrowing_knowledge', async () => {
  const ownerContent = (
    await Promise.all(OWNER_FILES.map((file) => readFile(join(repoRoot, file), 'utf8')))
  ).join('\n')

  for (const identifier of EXCLUSIVE_PRIVATE_KNOWLEDGE) {
    const pattern = new RegExp(`\\b${identifier}\\b`)
    assert.match(
      ownerContent,
      pattern,
      `ModelCapacity owner file must define knowledge identifier: ${identifier}`
    )
  }

  for (const publicType of CONTROLLED_PUBLIC_TYPES) {
    const pattern = new RegExp(`\\b${publicType}\\b`)
    assert.match(
      ownerContent,
      pattern,
      `ModelCapacity owner file must define controlled public type: ${publicType}`
    )
  }
})
test('WHAT[execution-model-routing-010] EMR_010_model_capacity_private_knowledge_is_exclusive_to_owner', async () => {
  const allFsFiles = await getAllProductionFsFiles(srcRoot)
  assert.ok(allFsFiles.length >= 600, `Expected at least 600 production files, got ${allFsFiles.length}`)

  const violations = []

  for (const fileAbs of allFsFiles) {
    const relPath = toRelative(fileAbs)
    if (OWNER_FILES.includes(relPath)) {
      continue
    }

    const content = await readFile(fileAbs, 'utf8')
    for (const identifier of EXCLUSIVE_PRIVATE_KNOWLEDGE) {
      const pattern = new RegExp(`\\b${identifier}\\b`)
      if (pattern.test(content)) {
        violations.push({
          file: relPath,
          leakedIdentifier: identifier,
        })
      }
    }
  }

  assert.deepEqual(
    violations,
    [],
    `Private ModelCapacity borrowing/lineage knowledge leaked outside owner modules (${OWNER_FILES.join(', ')}). Violations: ${JSON.stringify(violations, null, 2)}`
  )
})
test('WHAT[execution-model-routing-010] EMR_010_capacity_ledger_and_borrowing_capacity_types_are_restricted_to_permitted_zones', async () => {
  const allFsFiles = await getAllProductionFsFiles(srcRoot)
  const violations = []

  for (const fileAbs of allFsFiles) {
    const relPath = toRelative(fileAbs)
    if (PERMITTED_CONSUMER_FILES.includes(relPath)) {
      continue
    }

    const content = await readFile(fileAbs, 'utf8')
    for (const publicType of CONTROLLED_PUBLIC_TYPES) {
      const pattern = new RegExp(`\\b${publicType}\\b`)
      if (pattern.test(content)) {
        violations.push({
          file: relPath,
          leakedType: publicType,
        })
      }
    }
  }

  assert.deepEqual(
    violations,
    [],
    `Controlled ModelCapacity types appeared outside permitted zones (${PERMITTED_CONSUMER_FILES.join(', ')}). Violations: ${JSON.stringify(violations, null, 2)}`
  )
})
test('WHAT[execution-model-routing-010] EMR_010_exclusivity_test_is_refutable_and_fails_closed_on_violation', () => {
  // Test refutability: simulate a leaked knowledge identifier in non-owner content
  const simulatedLeakedContent = `
    namespace Wanxiangshu.SomeModule
    let decide = routeDecision None route
  `

  const checkContent = (content, identifiers) => {
    const matches = []
    for (const id of identifiers) {
      if (new RegExp(`\\b${id}\\b`).test(content)) {
        matches.push(id)
      }
    }
    return matches
  }

  const detected = checkContent(simulatedLeakedContent, EXCLUSIVE_PRIVATE_KNOWLEDGE)
  assert.deepEqual(
    detected,
    ['routeDecision'],
    'Exclusivity detector must reliably catch leaked identifiers'
  )

  const cleanContent = `
    namespace Wanxiangshu.SomeModule
    let doSomething () = ()
  `
  assert.deepEqual(
    checkContent(cleanContent, EXCLUSIVE_PRIVATE_KNOWLEDGE),
    [],
    'Clean content must produce zero violations'
  )
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const routing = await import("../../../dist/OpenCode/Host/ModelRoutingSurface.js");

const {
  createRuntime,
  acquireExecutionAdmission,
  beginExecutionAdmission,
  awaitQueuedExecutionAdmission,
  executionAdmissionTarget,
  commitExecutionAdmission,
  tryReserveManaged,
  tryLease,
  releasePhysicalExecution,
  cancelPendingExecution,
  enterProviderStep: rawEnterProviderStep,
  endProviderStep,
  takeProviderRunTarget,
  suppressProviderStep,
  snapshotOccupied,
  capacitySnapshot,
  pendingCount,
} = routing
const enterProviderStep = (runtime, sessionId, physicalUserMessageId, visibleProviderRuns, requestKey = null) =>
  rawEnterProviderStep(runtime, sessionId, physicalUserMessageId, visibleProviderRuns, requestKey)
const target = (model = 'provider/shared', reasoning = 'none') => ({ model, reasoning })
const key = (value) => `${value.model}|${value.reasoning}`
const acquireManaged = async (runtime, sessionId, physicalUserMessageId, role, participant, lenderSessionId = null) => {
  const acquisition = await acquireExecutionAdmission(
    runtime,
    sessionId,
    physicalUserMessageId,
    role,
    participant,
    lenderSessionId,
  )
  if (acquisition.kind !== 'Acquired') return { kind: acquisition.kind, target: null }

  const projected = executionAdmissionTarget(runtime, acquisition.lease)
  const observed = {
    sessionId,
    physicalUserMessageId,
    role,
    participant,
    target: projected,
  }
  const settlement = commitExecutionAdmission(runtime, acquisition.lease, observed)
  assert.ok(['Applied', 'AlreadyApplied'].includes(settlement.kind))
  return { kind: 'Acquired', target: projected }
}
const acquireTarget = async (...args) => {
  const outcome = await acquireManaged(...args)
  assert.equal(outcome.kind, 'Acquired')
  return outcome.target
}
const provider = (model) => model.slice(0, model.indexOf('/'))
const providerLimited = (limits, routes) => (role, running, previous) => {
  const candidates = routes[role] ?? []
  const count = (name) => running.filter((item) => provider(item.model) === name).length
  const available = (candidate) => count(provider(candidate.model)) < (limits[provider(candidate.model)] ?? 0)
  if (previous && candidates.some((candidate) => key(candidate) === key(previous)) && available(previous)) return previous
  return candidates.find(available) ?? null
}

test('WHAT[execution-model-routing-010] EMR_010_explicit_lender_credit_is_free_only_to_borrowers_not_global_waiters', async () => {
  const only = target('provider/only')
  const runtime = createRuntime(providerLimited({ provider: 1 }, { engineer: [only], manager: [only], devops: [only] }))

  await acquireTarget(runtime, 'parent', 'msg-parent', 'engineer', 'alice')
  assert.equal(key(await acquireTarget(runtime, 'child', 'msg-child', 'manager', 'bob', 'parent')), key(only))
  assert.equal(snapshotOccupied(runtime).length, 1, 'borrowing never creates a second provider token')

  let settled = false
  const stranger = acquireManaged(runtime, 'stranger', 'msg-stranger', 'devops', 'carol').then((value) => {
    settled = true
    return value
  })
  await Promise.resolve()
  assert.equal(settled, false, 'a session without an explicit lender still sees the token as occupied')
  cancelPendingExecution(runtime, 'stranger')
  assert.equal((await stranger).kind, 'Cancelled')
})
test('WHAT[execution-model-routing-010] EMR_010_absent_lender_queues_without_borrowing', async () => {
  const only = target('provider/only')
  const runtime = createRuntime(providerLimited({ provider: 1 }, { engineer: [only], manager: [only] }))

  await acquireTarget(runtime, 'parent', 'msg-parent', 'engineer', 'alice')
  const ghost = await beginExecutionAdmission(runtime, 'child', 'msg-child', 'manager', 'bob', 'ghost')
  assert.equal(ghost.kind, 'Queued', 'a lender with no credit authorizes nothing')

  cancelPendingExecution(runtime, 'child')
  assert.equal((await awaitQueuedExecutionAdmission(ghost.queue)).kind, 'Cancelled')
  assert.equal(snapshotOccupied(runtime).length, 1)
})
test('WHAT[execution-model-routing-010] EMR_010_borrowed_step_handoff_reuses_the_same_credit', async () => {
  const only = target('provider/only')
  const runtime = createRuntime(providerLimited({ provider: 1 }, { engineer: [only], manager: [only] }))

  await acquireTarget(runtime, 'parent', 'msg-parent', 'engineer', 'alice')
  await acquireTarget(runtime, 'child', 'msg-child', 'manager', 'bob', 'parent')
  await enterProviderStep(runtime, 'child', 'msg-child', [])

  assert.equal(snapshotOccupied(runtime).length, 1, 'handoff reuses the same real provider credit')

  suppressProviderStep(runtime, 'child', 'msg-child')
  assert.deepEqual(capacitySnapshot(runtime).tokenStateCounts, { idle: 1, inFlight: 0, retiring: 0 })
  assert.equal(snapshotOccupied(runtime).length, 1)
})
test('WHAT[execution-model-routing-010] EMR_010_owner_transform_entry_reclaims_foreign_inflight_borrow', async () => {
  const only = target('provider/only')
  const runtime = createRuntime(providerLimited({ provider: 1 }, { engineer: [only], manager: [only] }))

  await acquireTarget(runtime, 'parent', 'msg-parent', 'engineer', 'alice')
  await acquireTarget(runtime, 'child', 'msg-child', 'manager', 'bob', 'parent')

  await enterProviderStep(runtime, 'parent', 'msg-parent', [])
  endProviderStep(runtime, 'parent', 'msg-parent', 'run-parent-0')
  assert.deepEqual(capacitySnapshot(runtime).tokenStateCounts, { idle: 1, inFlight: 0, retiring: 0 })

  await enterProviderStep(runtime, 'child', 'msg-child', [])
  assert.deepEqual(
    capacitySnapshot(runtime).tokenStateCounts,
    { idle: 0, inFlight: 1, retiring: 0 },
    'descendant borrow holds the owner credit in flight',
  )

  // Leave the borrower's step open (the Long Stroke failure mode: EndStep blocked / never arrives).
  // Owner re-entering messages.transform must reclaim — not hang behind the foreign InFlight step.
  const parentNext = enterProviderStep(runtime, 'parent', 'msg-parent', ['run-parent-0'])
  assert.deepEqual(
    capacitySnapshot(runtime).waiters.map((waiter) => waiter.sessionId),
    [],
    'owner transform-entry reclaim grants immediately; must not leave the owner waiting behind a foreign InFlight borrow',
  )
  assert.deepEqual(capacitySnapshot(runtime).tokenStateCounts, { idle: 0, inFlight: 1, retiring: 0 })
  await parentNext
  suppressProviderStep(runtime, 'parent', 'msg-parent')
  assert.deepEqual(capacitySnapshot(runtime).tokenStateCounts, { idle: 1, inFlight: 0, retiring: 0 })
})
test('WHAT[execution-model-routing-010] EMR_010_older_borrowed_step_precedes_later_owned_step', async () => {
  const only = target('provider/only')
  const runtime = createRuntime(providerLimited({ provider: 1 }, { engineer: [only], manager: [only] }))

  await acquireTarget(runtime, 'parent', 'msg-parent', 'engineer', 'alice')
  await acquireTarget(runtime, 'child', 'msg-child', 'manager', 'bob', 'parent')
  await enterProviderStep(runtime, 'parent', 'msg-parent', [])

  const childStep = enterProviderStep(runtime, 'child', 'msg-child', [])
  const parentNextStep = enterProviderStep(runtime, 'parent', 'msg-parent', [])
  assert.deepEqual(
    capacitySnapshot(runtime).waiters.map((waiter) => waiter.sessionId),
    ['child', 'parent'],
  )

  endProviderStep(runtime, 'parent', 'msg-parent', 'run-parent')
  assert.deepEqual(
    capacitySnapshot(runtime).waiters.map((waiter) => waiter.sessionId),
    ['parent'],
    'borrowed waiter precedes later owned waiter for the same credit',
  )
  await childStep
  suppressProviderStep(runtime, 'child', 'msg-child')

  assert.deepEqual(
    capacitySnapshot(runtime).waiters.map((waiter) => waiter.sessionId),
    [],
  )
  await parentNextStep
  suppressProviderStep(runtime, 'parent', 'msg-parent')
  assert.deepEqual(capacitySnapshot(runtime).tokenStateCounts, { idle: 1, inFlight: 0, retiring: 0 })
  assert.equal(snapshotOccupied(runtime).length, 1)
})
test('WHAT[execution-model-routing-010] EMR_010_credit_never_crosses_provider_boundary', async () => {
  const a = target('provider-a/model')
  const b = target('provider-b/model')
  const runtime = createRuntime(providerLimited(
    { 'provider-a': 1, 'provider-b': 1 },
    { engineer: [a], manager: [b] },
  ))

  await acquireTarget(runtime, 'parent', 'msg-parent', 'engineer', 'alice')
  const child = await beginExecutionAdmission(runtime, 'child', 'msg-child', 'manager', 'bob', 'parent')
  assert.equal(child.kind, 'Acquired', 'a borrower needing another provider takes ordinary capacity')
  assert.equal(key(executionAdmissionTarget(runtime, child.lease)), 'provider-b/model|none')
  assert.equal(snapshotOccupied(runtime).length, 2, 'no provider token is shared across providers')

  cancelPendingExecution(runtime, 'child')
})
test('WHAT[execution-model-routing-010] EMR_010_reservation_borrowing_shares_one_token', async () => {
  const runtime = createRuntime(() => target('provider/shared'))

  const first = tryReserveManaged(runtime, 'parent', 'engineer', null)
  const second = tryReserveManaged(runtime, 'child', 'engineer', 'parent')
  assert.equal(key(first), 'provider/shared|none')
  assert.equal(key(second), 'provider/shared|none')
  assert.equal(capacitySnapshot(runtime).ledgerEntries.length, 1, 'an explicit lender reservation duplicates no capacity')
  assert.equal(snapshotOccupied(runtime).length, 1)
})
}

{
const { default: assert } = await import("node:assert/strict");
const { readFile } = await import("node:fs/promises");
const { default: test } = await import("node:test");

const source = async (relative) => readFile(new URL(`../../../${relative}`, import.meta.url), 'utf8')

test('WHAT[execution-model-routing-010] EMR_010_borrowing_complexity_is_owned_only_by_the_capacity_decorator', async () => {
  const capacity = (
    await Promise.all([
      source('src/Wanxiangshu/OpenCode/Host/ModelCapacity/Model.fs'),
      source('src/Wanxiangshu/OpenCode/Host/ModelCapacity/Ledger.fs'),
      source('src/Wanxiangshu/OpenCode/Host/ModelCapacity/Queue.fs'),
      source('src/Wanxiangshu/OpenCode/Host/ModelCapacity/Borrowing.fs'),
      source('src/Wanxiangshu/OpenCode/Host/ModelCapacity/Surface.fs'),
    ])
  ).join('\n')
  const routing = await source('src/Wanxiangshu/OpenCode/Host/ModelRouting.fs')
  const sessions = await source('src/Wanxiangshu/OpenCode/Host/Sessions.fs')
  const binding = await source('src/Wanxiangshu/OpenCode/Host/SessionExecutionBinding.fs')
  const transform = await source('src/Wanxiangshu/OpenCode/Plugin/PluginTransforms.fs')
  const host = await source('src/Wanxiangshu/OpenCode/Host/HostSignalBootstrap.fs')
  const scheduler = await source('resources/wanxiangshu.mjs')

  assert.match(capacity, /type internal CapacityLedger<'target>/)
  assert.match(capacity, /type internal BorrowingCapacity<'target>/)
  assert.match(capacity, /routeDecision/)
  assert.match(capacity, /CapacityCreditState/)
  assert.match(routing, /BorrowingCapacity<ModelRoutingTarget>/)
  assert.doesNotMatch(routing, /routeDecision|recordRoutedCredit|ownedTokenByExecution|creditSourceByExecution/)

  for (const main of [sessions, binding, transform, host]) {
    assert.doesNotMatch(main, /routeDecision|recordRoutedCredit|ownedTokenByExecution|creditSourceByExecution|CapacityCreditState|CapacityStepDemand/)
  }
  assert.doesNotMatch(scheduler, /borrow|recall|lineage|parentSession|childSession/i)
})
}

{
const { default: assert } = await import("node:assert/strict");
const { readFile } = await import("node:fs/promises");
const { default: test } = await import("node:test");

const root = new URL('../../../', import.meta.url)
const source = (path) => readFile(new URL(path, root), 'utf8')

test('WHAT[execution-model-routing-010] EMR_010_managed_tool_execution_ends_the_current_provider_step_before_tool_body', async () => {
  const [binding, registry] = await Promise.all([
    source('src/Wanxiangshu/OpenCode/Host/SessionExecutionBinding.fs'),
    source('src/Wanxiangshu/OpenCode/Tools/ToolRegistry.fs'),
  ])

  // The tool context carries this call's own exact ProviderRunIdentity. The
  // physical message it answers comes from the ModelRouting relation the
  // authoritative Host start observation wrote, never from a session-current
  // binding copy.
  assert.match(
    binding,
    /let beginPhysicalProviderAttemptForTransform[\s\S]*enterBoundProviderStep/,
    'the transform boundary enters the exact provider step of its own messages',
  )

  const boundaryCall = 'ModelRouting.tryProviderStepIdentity'
  const boundaryIndex = registry.indexOf(boundaryCall)

  assert.ok(boundaryIndex >= 0, 'ToolRegistry must cross the provider→tool capacity boundary')
  assert.match(
    registry,
    /let (?:private )?endObservedStep[\s\S]*ModelRouting\.tryProviderStepIdentity[\s\S]*ModelRouting\.endProviderStep/,
    'the tool boundary resolves the run physical message from the exact run-to-physical relation',
  )
  assert.match(
    registry,
    /let (?:private )?providerToolBoundary[\s\S]*endObservedStep/,
    'provider-to-tool handoff is a named stage resolving the run physical message',
  )
  assert.match(
    registry,
    /match providerToolBoundary ctx with[\s\S]*\| Ok\(\) ->[\s\S]*accountingStage gate ctx[\s\S]*runBoundaryStages gate spec managerPermission args ctx/,
    'all later gates execute only after the provider boundary succeeds',
  )
  assert.match(
    registry,
    /let (?:private )?runAfterAblation[\s\S]*match! replicaStage gate spec ctx with[\s\S]*admissionStage gate spec managerPermission args ctx[\s\S]*let (?:private )?runBoundaryStages[\s\S]*match! ablationStage spec ctx with[\s\S]*runAfterAblation gate spec managerPermission args ctx/,
    'strength and role/admission gates remain downstream of the provider boundary',
  )
  assert.match(
    registry,
    /match spec\.Admission with[\s\S]*OfficeRole[\s\S]*officeStage[\s\S]*PrivateAttachment[\s\S]*attachmentStage/,
    'the declared tool authority, not a guessed one, selects the admission path downstream of the boundary',
  )
  assert.match(
    registry,
    /let (?:private )?executeAdmittedRole[\s\S]*spec\.Execute args ctx/,
    'after the outer handoff and gates, ToolRegistry still delegates to the original tool body',
  )
  assert.match(
    registry,
    /let (?:private )?attachmentStage[\s\S]*attachmentAdmission ctx[\s\S]*spec\.Execute args ctx/,
    'an internal leaf tool also reaches the original body only after the provider boundary',
  )

  const boundarySlice = registry.slice(Math.max(0, boundaryIndex - 500), boundaryIndex + 1000)
  assert.doesNotMatch(
    boundarySlice,
    /DateTime|setTimeout|timer|sleep|TimeoutMs|milliseconds?/i,
    'provider→tool handoff is causal and must not depend on elapsed time',
  )
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const routing = await import("../../../dist/OpenCode/Host/ModelRoutingSurface.js");

const {
  createRuntime,
  acquireExecutionAdmission,
  commitExecutionAdmission,
  executionAdmissionTarget,
  enterProviderStep,
  endProviderStep,
  suppressProviderStep,
  snapshotOccupied,
  capacitySnapshot,
} = routing
const target = (model = 'provider/only', reasoning = 'none') => ({ model, reasoning })
const key = (value) => `${value.model}|${value.reasoning}`
const providerOf = (model) => model.slice(0, model.indexOf('/'))
const providerLimited = (limits, routes) => (role, running, previous) => {
  const candidates = routes[role] ?? []
  const available = (candidate) =>
    running.filter((item) => providerOf(item.model) === providerOf(candidate.model)).length
    < (limits[providerOf(candidate.model)] ?? 0)
  if (previous && candidates.some((candidate) => key(candidate) === key(previous)) && available(previous)) return previous
  return candidates.find(available) ?? null
}
const acquireTarget = async (runtime, sessionId, physicalUserMessageId, role, participant, lenderSessionId = null) => {
  const acquisition = await acquireExecutionAdmission(
    runtime,
    sessionId,
    physicalUserMessageId,
    role,
    participant,
    lenderSessionId,
  )
  assert.equal(acquisition.kind, 'Acquired')
  const projected = executionAdmissionTarget(runtime, acquisition.lease)
  const settlement = commitExecutionAdmission(runtime, acquisition.lease, {
    sessionId,
    physicalUserMessageId,
    role,
    participant,
    target: projected,
  })
  assert.ok(['Applied', 'AlreadyApplied'].includes(settlement.kind))
  return projected
}

// DELEGATE 9.3 / execution-model-routing-010: with one provider token the owner
// must never hold an undelivered step while it synchronously waits for the
// Replica, the Replica must run on the owner credit through an explicit lender,
// and the credit must return to the owner afterwards. No timeout, no capacity
// growth, no second in-flight step.
test('WHAT[execution-model-routing-010] EMR_010_owner_waits_for_replica_on_one_provider_token_without_deadlock', async () => {
  const only = target('provider/only')
  const runtime = createRuntime(providerLimited({ provider: 1 }, { engineer: [only] }))

  // The owner holds the only provider token with its request in flight.
  const ownerTarget = await acquireTarget(runtime, 'owner', 'msg-owner', 'engineer', 'alice')
  assert.equal(key(ownerTarget), key(only))
  await enterProviderStep(runtime, 'owner', 'msg-owner', [])
  assert.deepEqual(capacitySnapshot(runtime).tokenStateCounts, { idle: 0, inFlight: 1, retiring: 0 })

  // The Replica is admitted against the owner's credit through the explicit
  // lender: borrowing creates no second token.
  const replicaTarget = await acquireTarget(runtime, 'replica', 'msg-replica', 'engineer', 'replica', 'owner')
  assert.equal(key(replicaTarget), key(only))
  assert.equal(capacitySnapshot(runtime).ledgerEntries.length, 1)
  assert.equal(snapshotOccupied(runtime).length, 1)

  // While the owner still holds its step, the borrowed step cannot be granted:
  // one token never serves two simultaneously in-flight steps.
  const replicaStep = enterProviderStep(runtime, 'replica', 'msg-replica', [])
  assert.deepEqual(
    capacitySnapshot(runtime).waiters.map((waiter) => waiter.sessionId),
    ['replica'],
    'a borrow cannot fake concurrency against an undelivered owner step',
  )
  assert.deepEqual(capacitySnapshot(runtime).tokenStateCounts, { idle: 0, inFlight: 1, retiring: 0 })

  // The provider→tool boundary ends the owner step before the tool body runs,
  // so the owner waiting inside the tool body no longer holds the token; the
  // waiting Replica step is granted the same credit.
  suppressProviderStep(runtime, 'owner', 'msg-owner')
  assert.deepEqual(
    capacitySnapshot(runtime).waiters.map((waiter) => waiter.sessionId),
    [],
    'ending the owner step hands the same credit to the waiting Replica step',
  )
  await replicaStep
  assert.equal(snapshotOccupied(runtime).length, 1, 'the Replica runs on the shared credit')

  // The Replica finishes; the credit returns to idle.
  endProviderStep(runtime, 'replica', 'msg-replica', 'run-replica')
  assert.deepEqual(capacitySnapshot(runtime).tokenStateCounts, { idle: 1, inFlight: 0, retiring: 0 })

  // The owner re-enters messages.transform and takes the credit back.
  const ownerNext = enterProviderStep(runtime, 'owner', 'msg-owner', ['run-owner'])
  assert.deepEqual(capacitySnapshot(runtime).waiters.map((waiter) => waiter.sessionId), [])
  await ownerNext
  suppressProviderStep(runtime, 'owner', 'msg-owner')
  assert.deepEqual(capacitySnapshot(runtime).tokenStateCounts, { idle: 1, inFlight: 0, retiring: 0 })
  assert.equal(capacitySnapshot(runtime).ledgerEntries.length, 1)
  assert.equal(snapshotOccupied(runtime).length, 1)
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const routing = await import("../../../dist/OpenCode/Host/ModelRoutingSurface.js");

const {
  createRuntime,
  acquireExecutionAdmission,
  commitExecutionAdmission,
  executionAdmissionTarget,
  enterProviderStep,
  endProviderStep,
  releasePhysicalExecution,
  snapshotOccupied,
  capacitySnapshot,
} = routing
const target = (model = 'provider/only', reasoning = 'none') => ({ model, reasoning })
const key = (value) => `${value.model}|${value.reasoning}`
const providerOf = (model) => model.slice(0, model.indexOf('/'))
const providerLimited = (limits, routes) => (role, running, previous) => {
  const candidates = routes[role] ?? []
  const available = (candidate) =>
    running.filter((item) => providerOf(item.model) === providerOf(candidate.model)).length
    < (limits[providerOf(candidate.model)] ?? 0)
  if (previous && candidates.some((candidate) => key(candidate) === key(previous)) && available(previous)) return previous
  return candidates.find(available) ?? null
}
const acquireTarget = async (runtime, sessionId, physicalUserMessageId, role, participant, lenderSessionId = null) => {
  const acquisition = await acquireExecutionAdmission(
    runtime,
    sessionId,
    physicalUserMessageId,
    role,
    participant,
    lenderSessionId,
  )
  assert.equal(acquisition.kind, 'Acquired')
  const projected = executionAdmissionTarget(runtime, acquisition.lease)
  const settlement = commitExecutionAdmission(runtime, acquisition.lease, {
    sessionId,
    physicalUserMessageId,
    role,
    participant,
    target: projected,
  })
  assert.ok(['Applied', 'AlreadyApplied'].includes(settlement.kind))
  return projected
}

// DELEGATE 14.5 / execution-model-routing-010: two owners run concurrently.
// Model resources belong to exact executions: one owner's step or teardown never
// touches the other owner's token or lease.
test('WHAT[execution-model-routing-010] EMR_010_two_owners_never_crosstalk_capacity_or_leases', async () => {
  const only = target('provider/only')
  const runtime = createRuntime(providerLimited({ provider: 2 }, { engineer: [only, only] }))

  // Both owners hold their own lease on the same provider at the same time.
  const first = await acquireTarget(runtime, 'owner-a', 'msg-a', 'engineer', 'alice')
  const second = await acquireTarget(runtime, 'owner-b', 'msg-b', 'engineer', 'bob')
  assert.equal(key(first), key(only))
  assert.equal(key(second), key(only))
  assert.equal(capacitySnapshot(runtime).ledgerEntries.length, 2)
  assert.equal(snapshotOccupied(runtime).length, 2)

  // Interleaved provider steps: each step belongs to its own execution.
  await enterProviderStep(runtime, 'owner-a', 'msg-a', [])
  await enterProviderStep(runtime, 'owner-b', 'msg-b', [])
  assert.deepEqual(capacitySnapshot(runtime).tokenStateCounts, { idle: 0, inFlight: 2, retiring: 0 })

  // Ending owner-a's step must not end owner-b's.
  endProviderStep(runtime, 'owner-a', 'msg-a', 'run-a')
  assert.deepEqual(capacitySnapshot(runtime).tokenStateCounts, { idle: 1, inFlight: 1, retiring: 0 })

  // Releasing owner-a's execution frees exactly one token — never owner-b's.
  releasePhysicalExecution(runtime, 'owner-a', 'msg-a')
  assert.equal(snapshotOccupied(runtime).length, 1, "one owner's teardown never frees the other owner's token")
  assert.equal(capacitySnapshot(runtime).ledgerEntries.length, 1)
  assert.deepEqual(
    capacitySnapshot(runtime).owners.map((owner) => owner.sessionId),
    ['owner-b'],
    'the surviving capacity belongs to owner-b alone',
  )

  // Owner-b still finishes its own step and releases cleanly.
  endProviderStep(runtime, 'owner-b', 'msg-b', 'run-b')
  assert.deepEqual(capacitySnapshot(runtime).tokenStateCounts, { idle: 1, inFlight: 0, retiring: 0 })
  releasePhysicalExecution(runtime, 'owner-b', 'msg-b')
  assert.equal(snapshotOccupied(runtime).length, 0)
  assert.equal(capacitySnapshot(runtime).ledgerEntries.length, 0)
})

test('WHAT[execution-model-routing-010] EMR_010_provider_step_reentry_with_same_request_key_is_idempotent_and_shares_task', async () => {
  const single = target('provider/shared')
  const runtime = createRuntime(providerLimited({ provider: 1 }, { engineer: [single] }))

  // 1. Session A enters step with key-1
  await acquireTarget(runtime, 'owner', 'msg-owner', 'engineer', 'alice')
  const stepPromise1 = enterProviderStep(runtime, 'owner', 'msg-owner', [], 'req-key-1')
  await stepPromise1

  // 同 key 重复进入已持有的 step：返回已完成 Task，waiters 队列不增长、不死锁
  const stepPromise2 = enterProviderStep(runtime, 'owner', 'msg-owner', [], 'req-key-1')
  assert.equal(capacitySnapshot(runtime).waiters.length, 0, 'waiters does not grow on idempotent re-entry of in-flight step')
  await stepPromise2

  // 2. 另一个 session 排队等待
  await acquireTarget(runtime, 'waiter-1', 'msg-w1', 'engineer', 'bob', 'owner')
  const waiterPromise1 = enterProviderStep(runtime, 'waiter-1', 'msg-w1', [], 'req-key-w1')
  assert.equal(capacitySnapshot(runtime).waiters.length, 1)

  // 3. waiter-1 同 key 重复进入：返回与第一次完全相同的同一个 Task/Promise 对象，waiters 快照不增长
  const waiterPromise1Dup = enterProviderStep(runtime, 'waiter-1', 'msg-w1', [], 'req-key-w1')
  assert.strictEqual(waiterPromise1Dup, waiterPromise1, 'duplicate entry with same requestKey returns identical Task promise')
  assert.equal(capacitySnapshot(runtime).waiters.length, 1, 'waiters snapshot does not grow on duplicate waiter requestKey')

  // 4. 不同 key 进入排队：正常排队，waiters 顺序保持
  await acquireTarget(runtime, 'waiter-2', 'msg-w2', 'engineer', 'charlie', 'owner')
  const waiterPromise2 = enterProviderStep(runtime, 'waiter-2', 'msg-w2', [], 'req-key-w2')
  assert.equal(capacitySnapshot(runtime).waiters.length, 2, 'different requestKey enqueues normally')
  assert.deepEqual(
    capacitySnapshot(runtime).waiters.map((w) => w.sessionId),
    ['waiter-1', 'waiter-2'],
    'waiters preserve FIFO sequence for different requestKeys'
  )

  // 清理
  endProviderStep(runtime, 'owner', 'msg-owner', 'run-owner')
  await waiterPromise1
  endProviderStep(runtime, 'waiter-1', 'msg-w1', 'run-w1')
  await waiterPromise2
  endProviderStep(runtime, 'waiter-2', 'msg-w2', 'run-w2')
  releasePhysicalExecution(runtime, 'owner', 'msg-owner')
  releasePhysicalExecution(runtime, 'waiter-1', 'msg-w1')
  releasePhysicalExecution(runtime, 'waiter-2', 'msg-w2')
})
}
