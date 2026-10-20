import assert from 'node:assert/strict'
import test from 'node:test'
import * as routing from '../../../dist/OpenCode/Host/ModelRoutingSurface.js'

const {
  createRuntime,
  acquireExecutionAdmission,
  executionAdmissionTarget,
  commitExecutionAdmission,
  releasePhysicalExecution,
  releaseExecution,
  endProviderStep,
  retainFailedTargetForRetry,
  condemnFailedTarget,
} = routing

const target = (model = 'provider/shared', reasoning = 'none') => ({ model, reasoning })

const acquire = async (runtime, sessionId, physicalUserMessageId, role = 'engineer', participant = 'alice') => {
  const acquisition = await acquireExecutionAdmission(
    runtime,
    sessionId,
    physicalUserMessageId,
    role,
    participant,
    null,
  )
  assert.equal(acquisition.kind, 'Acquired', `admission for ${physicalUserMessageId} must acquire`)
  const acquiredTarget = executionAdmissionTarget(runtime, acquisition.lease)
  const settlement = commitExecutionAdmission(runtime, acquisition.lease, {
    sessionId,
    physicalUserMessageId,
    role,
    participant,
    target: acquiredTarget,
  })
  assert.ok(['Applied', 'AlreadyApplied'].includes(settlement.kind))
  return acquiredTarget
}

test('WHAT[execution-model-routing-017] retainFailedTargetForRetry binds target for next fresh admission and consumes once', async () => {
  const seenPrevious = []
  let scheduled = 0
  const runtime = createRuntime((_role, _running, previous) => {
    seenPrevious.push(previous)
    return previous ?? target(`provider-${++scheduled}/model`)
  })

  // 1. Session ses-1 admits msg-1 and gets provider-1
  const first = await acquire(runtime, 'ses-1', 'msg-1')
  assert.equal(first.model, 'provider-1/model')
  endProviderStep(runtime, 'ses-1', 'msg-1', 'run-1')

  // 2. Release msg-1 so no active physical previous survives
  releasePhysicalExecution(runtime, 'ses-1', 'msg-1')

  // 3. Retain failed target for retry on run-1 witness
  const retained = retainFailedTargetForRetry(runtime, 'ses-1', 'run-1')
  assert.deepEqual(retained, first)

  // 4. Next fresh admission on ses-1 must prefer the retained target even though previous active was released
  const retried = await acquire(runtime, 'ses-1', 'msg-2')
  assert.deepEqual(retried, first, 'fresh admission must receive the retained target')
  endProviderStep(runtime, 'ses-1', 'msg-2', 'run-2')
  releasePhysicalExecution(runtime, 'ses-1', 'msg-2')

  // 5. Single-consumption: the retry target was consumed, so third admission without active previous gets fresh schedule
  const third = await acquire(runtime, 'ses-1', 'msg-3')
  assert.equal(third.model, 'provider-2/model', 'retained target is single-consumption and must not survive second fresh admission')
  endProviderStep(runtime, 'ses-1', 'msg-3', 'run-3')
  releasePhysicalExecution(runtime, 'ses-1', 'msg-3')
})

test('WHAT[execution-model-routing-017] retainFailedTargetForRetry witness is single-consumption and rejects mismatched session', async () => {
  const runtime = createRuntime(() => target('provider-test/model'))

  // Session ses-a executes run-a1
  await acquire(runtime, 'ses-a', 'msg-a1')
  endProviderStep(runtime, 'ses-a', 'msg-a1', 'run-a1')

  // 1. Mismatched session returns null and consumes witness (fail closed)
  const mismatched = retainFailedTargetForRetry(runtime, 'ses-b', 'run-a1')
  assert.equal(mismatched, null, 'mismatched session must fail closed with null')

  // Calling again on already consumed witness returns null
  assert.equal(retainFailedTargetForRetry(runtime, 'ses-a', 'run-a1'), null, 'consumed witness returns null')

  // Session ses-a executes run-a2
  await acquire(runtime, 'ses-a', 'msg-a2')
  endProviderStep(runtime, 'ses-a', 'msg-a2', 'run-a2')

  // 2. Matching session retains target successfully
  const valid = retainFailedTargetForRetry(runtime, 'ses-a', 'run-a2')
  assert.deepEqual(valid, target('provider-test/model'))

  // 3. Re-retaining with the same providerRun witness returns null (single-consumption)
  const duplicate = retainFailedTargetForRetry(runtime, 'ses-a', 'run-a2')
  assert.equal(duplicate, null, 'witness is single-consumption and duplicate retain returns null')

  // 4. Non-existent providerRun returns null
  assert.equal(retainFailedTargetForRetry(runtime, 'ses-a', 'no-such-run'), null)
})

test('WHAT[execution-model-routing-017] condemnFailedTarget poisons the provider and rotates next admission', async () => {
  const poisoned = []
  const template = {
    default: (role, running) => {
      const candidates = [target('provider-a/model'), target('provider-b/model')]
      return candidates.find((c) => !poisoned.includes('provider-a') || c.model.startsWith('provider-b')) ?? null
    },
    markProviderFailed: (providerName) => {
      poisoned.push(providerName)
    },
  }

  // Inject markProviderFailed onto the scheduler function before createRuntime
  template.default.markProviderFailed = template.markProviderFailed
  const runtime = createRuntime(template.default)

  const first = await acquire(runtime, 'ses-c', 'msg-c1')
  assert.equal(first.model, 'provider-a/model')
  endProviderStep(runtime, 'ses-c', 'msg-c1', 'run-c1')

  // Condemn failed target of run-c1
  const condemned = condemnFailedTarget(runtime, 'run-c1')
  assert.deepEqual(condemned, target('provider-a/model'))
  assert.ok(poisoned.includes('provider-a'), 'provider-a must be marked failed via markProviderFailed')

  // Next admission rotates to provider-b
  const second = await acquire(runtime, 'ses-c', 'msg-c2')
  assert.equal(second.model, 'provider-b/model', 'next admission rotates away from poisoned provider-a')

  // Calling condemnFailedTarget again on already consumed witness returns null
  assert.equal(condemnFailedTarget(runtime, 'run-c1'), null, 'witness is single-consumption')
})

test('WHAT[execution-model-routing-017] provider loss settles queued demands by role without cancelling other pools', async () => {
  let poisoned = false
  const scheduler = (role, running) =>
    role === 'engineer' && !poisoned && running.length === 0 ? target('only/model') : null
  scheduler.markProviderFailed = () => { poisoned = true }
  scheduler.hasTheoreticalCapacity = (role) =>
    role !== 'engineer' || !poisoned
  const runtime = createRuntime(scheduler)
  const begin = (session, role) =>
    routing.beginExecutionAdmission(runtime, session, `msg-${session}`, role, 'alice', null)
  const holder = await begin('holder', 'engineer')
  assert.equal(holder.kind, 'Acquired')
  commitExecutionAdmission(runtime, holder.lease, {
    sessionId: 'holder',
    physicalUserMessageId: 'msg-holder',
    role: 'engineer',
    participant: 'alice',
    target: target('only/model'),
  })
  endProviderStep(runtime, 'holder', 'msg-holder', 'failed-run')
  const waiting = await begin('waiting', 'engineer')
  const otherPool = await begin('other-pool', 'manager')
  assert.equal(waiting.kind, 'Queued')
  assert.equal(otherPool.kind, 'Queued')

  assert.deepEqual(condemnFailedTarget(runtime, 'failed-run'), target('only/model'))
  assert.equal(routing.pendingCount(runtime), 1, 'only the pool with no remaining candidate must stop waiting')
  const rejected = await routing.awaitQueuedExecutionAdmission(waiting.queue)
  assert.equal(rejected.kind, 'QueueFull')
  assert.equal(rejected.failure, 'CapacityQueueFull')
  assert.strictEqual(routing.tryReadExecution(runtime, 'holder', 'msg-holder'), holder.lease,
    'provider health does not authorize releasing an existing exact lease')
  assert.equal(condemnFailedTarget(runtime, 'failed-run'), null)
  assert.equal(routing.pendingCount(runtime), 1, 'a duplicate failure cannot settle unrelated work')

  routing.cancelPendingExecution(runtime, 'other-pool')
  assert.equal((await routing.awaitQueuedExecutionAdmission(otherPool.queue)).kind, 'Cancelled')
  releasePhysicalExecution(runtime, 'holder', 'msg-holder')
})

test('WHAT[execution-model-routing-017] provider loss immediately routes an existing waiter to a surviving candidate', async () => {
  let poisoned = false
  const scheduler = (_role, running) => {
    const candidate = target(poisoned ? 'backup/model' : 'primary/model')
    return running.some((active) => active.model === candidate.model) ? null : candidate
  }
  scheduler.markProviderFailed = () => { poisoned = true }
  scheduler.hasTheoreticalCapacity = () => true
  const runtime = createRuntime(scheduler)
  await acquire(runtime, 'holder', 'msg-holder')
  endProviderStep(runtime, 'holder', 'msg-holder', 'failed-run')
  const waiting = await routing.beginExecutionAdmission(runtime, 'waiting', 'msg-waiting', 'engineer', 'bob', null)
  assert.equal(waiting.kind, 'Queued')

  condemnFailedTarget(runtime, 'failed-run')
  assert.equal(routing.pendingCount(runtime), 0, 'provider health changes are scheduling events')
  const resumed = await routing.awaitQueuedExecutionAdmission(waiting.queue)
  assert.equal(resumed.kind, 'Acquired')
  assert.deepEqual(executionAdmissionTarget(runtime, resumed.lease), target('backup/model'))
  releasePhysicalExecution(runtime, 'waiting', 'msg-waiting')
  releasePhysicalExecution(runtime, 'holder', 'msg-holder')
})

test('WHAT[execution-model-routing-017] provider loss does not reject or replace an already committed exact admission', async () => {
  let unavailable = false
  const scheduler = () => unavailable ? null : target('only/model')
  scheduler.markProviderFailed = () => { unavailable = true }
  scheduler.hasTheoreticalCapacity = () => !unavailable
  const runtime = createRuntime(scheduler)
  const begin = (physical, participant = 'alice') =>
    routing.beginExecutionAdmission(runtime, 'session', physical, 'engineer', participant, null)
  const original = await begin('physical')
  assert.equal(original.kind, 'Acquired')
  commitExecutionAdmission(runtime, original.lease, {
    sessionId: 'session', physicalUserMessageId: 'physical',
    role: 'engineer', participant: 'alice', target: target('only/model'),
  })
  endProviderStep(runtime, 'session', 'physical', 'failed-run')
  condemnFailedTarget(runtime, 'failed-run')

  const replay = await begin('physical')
  assert.equal(replay.kind, 'Acquired')
  assert.strictEqual(replay.lease, original.lease)
  await assert.rejects(begin('physical', 'intruder'), /participant/)
  const fresh = await begin('new-physical')
  assert.equal(fresh.kind, 'QueueFull')
  assert.strictEqual(routing.tryReadExecution(runtime, 'session', 'physical'), original.lease)
  releasePhysicalExecution(runtime, 'session', 'physical')
})

test('WHAT[execution-model-routing-017] force cleanup (releaseExecution) clears recovery retry target before consumption', async () => {
  let count = 0
  const runtime = createRuntime((_role, _running, previous) => previous ?? target(`provider-${++count}/model`))

  const first = await acquire(runtime, 'ses-d', 'msg-d1')
  assert.equal(first.model, 'provider-1/model')
  endProviderStep(runtime, 'ses-d', 'msg-d1', 'run-d1')

  // Retain for retry
  const retained = retainFailedTargetForRetry(runtime, 'ses-d', 'run-d1')
  assert.deepEqual(retained, target('provider-1/model'))

  // Force cleanup (ReleaseExecution) clears the pending retry target
  releaseExecution(runtime, 'ses-d')

  // Next fresh admission should NOT receive the cleared target, but get a fresh schedule
  const fresh = await acquire(runtime, 'ses-d', 'msg-d2')
  assert.equal(fresh.model, 'provider-2/model', 'cleared recovery retry target must not be consumed by next admission')
})
