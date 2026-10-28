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
  takeProviderRunTarget,
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

test('WHAT[execution-model-routing-017] a confirmed failure retried verbatim via session previous inheritance', async () => {
  let scheduled = 0
  const runtime = createRuntime((_role, _running, previous) => previous ?? target(`provider-${++scheduled}/model`))

  const first = await acquire(runtime, 'ses-1', 'msg-1')
  assert.equal(first.model, 'provider-1/model')
  endProviderStep(runtime, 'ses-1', 'msg-1', 'run-1')

  // 无 fallback：失败不产生重试绑定；witness 是结算凭据，目标随会话 previous 原样重试。
  const witness = takeProviderRunTarget(runtime, 'run-1')
  assert.deepEqual(witness, first, 'the exact witness records the target of the failed run for settlement')

  const retried = await acquire(runtime, 'ses-1', 'msg-2')
  assert.equal(retried.model, 'provider-1/model', 'the retry follows the session previous target verbatim')
  endProviderStep(runtime, 'ses-1', 'msg-2', 'run-2')
  releasePhysicalExecution(runtime, 'ses-1', 'msg-2')
})

test('WHAT[execution-model-routing-017] a failure never poisons a provider; fresh scheduling keeps the model', async () => {
  let calls = 0
  const runtime = createRuntime(() => {
    calls += 1
    return target('provider-stable/model')
  })

  const first = await acquire(runtime, 'ses-c', 'msg-c1')
  assert.equal(first.model, 'provider-stable/model')
  endProviderStep(runtime, 'ses-c', 'msg-c1', 'run-c1')

  const second = await acquire(runtime, 'ses-c', 'msg-c2')
  assert.equal(second.model, 'provider-stable/model', 'a failed provider is never poisoned; the next admission keeps the model')
  assert.ok(calls >= 2)
})

test('WHAT[execution-model-routing-017] a failure does not settle queued demands by eviction', async () => {
  let saturated = false
  const scheduler = () => (saturated ? null : target('only/model'))
  scheduler.hasTheoreticalCapacity = () => true
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

  // 无 fallback 下 witness 只是结算凭据：witness 记录该 run 的目标，不代表绑定。
  assert.deepEqual(
    takeProviderRunTarget(runtime, 'failed-run'),
    target('only/model'),
    'the witness records the run target for settlement only',
  )

  // 容量暂满进入 pending：失败不驱逐队列，也无 poison 副作用。
  saturated = true
  const waiting = await begin('waiting', 'engineer')
  assert.equal(waiting.kind, 'Queued')
  assert.equal(routing.pendingCount(runtime), 1)
  routing.cancelPendingExecution(runtime, 'waiting')
  assert.equal((await routing.awaitQueuedExecutionAdmission(waiting.queue)).kind, 'Cancelled')

  releasePhysicalExecution(runtime, 'holder', 'msg-holder')
})

test('WHAT[execution-model-routing-017] a committed exact admission survives a provider failure', async () => {
  const runtime = createRuntime(() => target('only/model'))
  const begin = (physical, participant = 'alice') =>
    routing.beginExecutionAdmission(runtime, 'session', physical, 'engineer', participant, null)
  const original = await begin('physical')
  assert.equal(original.kind, 'Acquired')
  commitExecutionAdmission(runtime, original.lease, {
    sessionId: 'session', physicalUserMessageId: 'physical',
    role: 'engineer', participant: 'alice', target: target('only/model'),
  })
  endProviderStep(runtime, 'session', 'physical', 'failed-run')

  const replay = await begin('physical')
  assert.equal(replay.kind, 'Acquired')
  assert.strictEqual(replay.lease, original.lease)
  await assert.rejects(begin('physical', 'intruder'), /participant/)
  assert.strictEqual(routing.tryReadExecution(runtime, 'session', 'physical'), original.lease)
  releasePhysicalExecution(runtime, 'session', 'physical')
})

test('WHAT[execution-model-routing-017] force cleanup (releaseExecution) leaves no retry residue', async () => {
  let count = 0
  const runtime = createRuntime(() => target(`provider-${++count}/model`))

  const first = await acquire(runtime, 'ses-d', 'msg-d1')
  assert.equal(first.model, 'provider-1/model')
  endProviderStep(runtime, 'ses-d', 'msg-d1', 'run-d1')

  releaseExecution(runtime, 'ses-d')

  const fresh = await acquire(runtime, 'ses-d', 'msg-d2')
  assert.equal(fresh.model, 'provider-2/model', 'force cleanup leaves no retry residue; scheduling starts fresh')
})
