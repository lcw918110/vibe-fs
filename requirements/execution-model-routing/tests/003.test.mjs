import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { execFileSync } = await import("node:child_process");
const { mkdirSync, mkdtempSync, rmSync, writeFileSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { dirname, join } = await import("node:path");
const { default: test } = await import("node:test");

const seed = 0x36a11ce
const restartCount = 16
const operationsPerRestart = 6
const surfaceUrl = new URL('../../../dist/OpenCode/Host/ModelRoutingSurface.js', import.meta.url).href
const childProgram = String.raw`
import assert from 'node:assert/strict'
import * as routing from ${JSON.stringify(surfaceUrl)}

const cycle = Number.parseInt(process.argv[1], 10)
let maxRetained = 0
const retained = (snapshot) =>
  snapshot.ledgerEntries.length + snapshot.tokens.length + snapshot.custodies.length +
  snapshot.executions.length + snapshot.waiters.length + snapshot.owners.length + snapshot.lineage.length
const audit = () => {
  const snapshot = routing.sharedCapacitySnapshot()
  assert.ok(snapshot.activeCount >= 0 && snapshot.activeCount <= snapshot.ledgerEntries.length)
  assert.equal(
    snapshot.tokenStateCounts.idle + snapshot.tokenStateCounts.inFlight + snapshot.tokenStateCounts.retiring,
    snapshot.tokens.length,
  )
  assert.ok(snapshot.waiters.length <= 32)
  assert.deepEqual(routing.reconcileCapacityEvidence(snapshot), { kind: 'NoOp' })
  maxRetained = Math.max(maxRetained, retained(snapshot))
  return snapshot
}

await routing.initialize()
const initial = audit()
await routing.initialize()
const afterReload = audit()
assert.deepEqual(afterReload, initial, 'plugin reload preserves the process singleton without replaying work')

const exact = {
  sessionId: 'restart-session-' + cycle,
  physicalUserMessageId: 'restart-physical-' + cycle,
  role: 'engineer',
  participant: 'restart-owner',
}
const first = await routing.acquireSharedExecutionAdmission(
  exact.sessionId,
  exact.physicalUserMessageId,
  exact.role,
  exact.participant,
  null,
)
assert.equal(first.kind, 'Acquired')
const afterAcquire = audit()
assert.equal(afterAcquire.ledgerEntries.length, 1)
const projected = routing.sharedExecutionAdmissionTarget(first.lease)
assert.deepEqual(
  routing.commitSharedExecutionAdmission(first.lease, { ...exact, target: projected }),
  { kind: 'Applied' },
)
const observed = audit()

await routing.initialize()
const beforeDuplicateReload = audit()
assert.deepEqual(beforeDuplicateReload, observed)
const duplicate = await routing.acquireSharedExecutionAdmission(
  exact.sessionId,
  exact.physicalUserMessageId,
  exact.role,
  exact.participant,
  null,
)
assert.equal(duplicate.kind, 'Acquired')
assert.equal(duplicate.lease, first.lease, 'plugin reload reuses the exact physical admission fence')
const afterDuplicateReload = audit()
assert.deepEqual(afterDuplicateReload, observed, 'plugin reload cannot duplicate ledger, token, custody, or execution work')

process.stdout.write(JSON.stringify({ initial, observed, afterDuplicateReload, maxRetained }))
`
const runProcess = (home, cycle) =>
  JSON.parse(
    execFileSync(process.execPath, ['--input-type=module', '--eval', childProgram, String(cycle)], {
      encoding: 'utf8',
      env: { ...process.env, HOME: home },
    }),
  )

test('WHAT[execution-model-routing-003] process restart drops process-local capacity and rebuilds only from explicit physical observations', (context) => {
  const home = mkdtempSync(join(tmpdir(), 'wanxiangshu-capacity-restart-'))
  const config = join(home, '.config', 'opencode', 'wanxiangshu.mjs')
  mkdirSync(dirname(config), { recursive: true })
  writeFileSync(
    config,
    "export const routingProtocol = 2\nexport const hasTheoreticalCapacity = () => true\nexport default function route(_role, running, previous) { return running.length < 1 ? { model: 'provider/restart', reasoning: 'none' } : null }\n",
  )

  let maxRetained = 0
  try {
    for (let cycle = 0; cycle < restartCount; cycle += 1) {
      const result = runProcess(home, (seed + cycle) >>> 0)
      assert.equal(result.initial.ledgerEntries.length, 0, 'a new process cannot inherit a dead process token')
      assert.equal(result.initial.tokens.length, 0)
      assert.equal(result.initial.custodies.length, 0)
      assert.equal(result.initial.executions.length, 0)
      assert.equal(result.initial.waiters.length, 0)
      assert.equal(result.initial.owners.length, 0)
      assert.equal(result.initial.lineage.length, 0)

      assert.equal(result.observed.ledgerEntries.length, 1, 'only the new explicit admission observation reconstructs capacity')
      assert.equal(result.observed.tokens.length, 1)
      assert.equal(result.observed.executions.length, 1)
      assert.deepEqual(result.afterDuplicateReload, result.observed)
      maxRetained = Math.max(maxRetained, result.maxRetained)
    }
  } finally {
    rmSync(home, { recursive: true, force: true })
  }

  assert.ok(maxRetained <= 5, 'one live exact execution retains only ledger, token, custody, execution, and owner nodes')
  context.diagnostic(
    `task36 restart seed=${seed} restarts=${restartCount} operations=${restartCount * operationsPerRestart} maxRetained=${maxRetained}`,
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
  enterProviderStep,
  endProviderStep,
  takeProviderRunTarget,
  suppressProviderStep,
  snapshotOccupied,
  capacitySnapshot,
  pendingCount,
} = routing
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

test('WHAT[execution-model-routing-003] EMR_003_each_active_physical_execution_contributes_one_running_occurrence', async () => {
  const runtime = createRuntime(() => target())

  const first = await acquireTarget(runtime, 'session-a', 'msg-a', 'engineer', 'alice')
  const same = await acquireTarget(runtime, 'session-a', 'msg-a', 'engineer', 'alice')
  const otherExecution = await acquireTarget(runtime, 'session-b', 'msg-b', 'devops', 'bob')

  assert.equal(key(first), 'provider/shared|none')
  assert.equal(key(same), 'provider/shared|none')
  assert.equal(key(otherExecution), 'provider/shared|none')
  assert.equal(snapshotOccupied(runtime).length, 2, 'two physical executions contribute two occurrences even on one target')
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { default: plugin } = await import("../../../dist/OpenCode/Plugin/Plugin.js");
const { createEnvironment, managedConfig, routeMessage } = await import("./support/process-shared-routing.mjs");


test('WHAT[execution-model-routing-003] EMR_003_two_plugin_instances_share_one_process_running_multiset', async () => {
  const environment = createEnvironment(plugin.server)
  const previousHome = process.env.HOME
  process.env.HOME = environment.home
  let first
  let second

  try {
    first = await environment.createPlugin('root-workspace')
    second = await environment.createPlugin('worktree-workspace')
    await first.config(managedConfig())
    await second.config(managedConfig())

    const a = await routeMessage(first, 'ses_shared_a')
    const b = await routeMessage(second, 'ses_shared_b')

    assert.deepEqual([a.providerID, a.modelID, a.variant], ['provider', 'model-a', 'none'])
    assert.deepEqual([b.providerID, b.modelID, b.variant], ['provider', 'model-b', 'none'])
  } finally {
    if (second) await second.dispose()
    if (first) await first.dispose()
    process.env.HOME = previousHome
    environment.dispose()
  }
})
}
