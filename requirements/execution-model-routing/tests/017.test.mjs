import assert from 'node:assert/strict'
import test from 'node:test'
import * as routing from '../../../dist/OpenCode/Host/ModelRoutingSurface.js'

const target = { model: 'provider/selected', reasoning: 'none' }
const begin = (runtime, session, physical, role = 'engineer', participant = 'alice') =>
  routing.beginExecutionAdmission(runtime, session, physical, role, participant, null)
const commit = (runtime, acquired, session, physical) => {
  assert.equal(acquired.kind, 'Acquired')
  assert.deepEqual(routing.commitExecutionAdmission(runtime, acquired.lease, {
    sessionId: session, physicalUserMessageId: physical, role: 'engineer', participant: 'alice', target,
  }), { kind: 'Applied' })
}

test('WHAT[execution-model-routing-017] a capacity change does not reject or replace an already committed exact admission', async () => {
  let available = true
  const policy = () => available ? target : null
  policy.hasTheoreticalCapacity = () => available
  const runtime = routing.createRuntime(policy)
  const original = await begin(runtime, 'session', 'physical')
  commit(runtime, original, 'session', 'physical')
  available = false

  const replay = await begin(runtime, 'session', 'physical')
  assert.equal(replay.kind, 'Acquired')
  assert.equal(replay.lease, original.lease)
  await assert.rejects(begin(runtime, 'session', 'physical', 'engineer', 'intruder'), /participant/)
  assert.equal((await begin(runtime, 'session', 'new-physical')).kind, 'QueueFull')
  assert.equal(routing.tryReadExecution(runtime, 'session', 'physical'), original.lease)
  routing.releasePhysicalExecution(runtime, 'session', 'physical')
})

test('WHAT[execution-model-routing-017] capacity loss rejects only its waiting role and preserves unrelated pending work', async () => {
  let available = true
  const policy = (role, running) => role === 'engineer' && available && running.length === 0 ? target : null
  policy.hasTheoreticalCapacity = role => role !== 'engineer' || available
  const runtime = routing.createRuntime(policy)
  const holder = await begin(runtime, 'holder', 'msg-holder')
  commit(runtime, holder, 'holder', 'msg-holder')
  const waiting = await begin(runtime, 'waiting', 'msg-waiting')
  const unrelated = await begin(runtime, 'other', 'msg-other', 'manager')
  assert.equal(waiting.kind, 'Queued')
  assert.equal(unrelated.kind, 'Queued')
  available = false
  routing.releasePhysicalExecution(runtime, 'holder', 'msg-holder')
  assert.deepEqual(await routing.awaitQueuedExecutionAdmission(waiting.queue), {
    kind: 'QueueFull', failure: 'CapacityQueueFull', lease: null, queue: null,
  })
  assert.equal(routing.pendingCount(runtime), 1)
  routing.cancelPendingExecution(runtime, 'other')
  assert.equal((await routing.awaitQueuedExecutionAdmission(unrelated.queue)).kind, 'Cancelled')
})

test('WHAT[execution-model-routing-017] releasing capacity resumes its waiter on the same selected model without a backup', async () => {
  const runtime = routing.createRuntime((_role, running) => running.length === 0 ? target : null)
  const holder = await begin(runtime, 'holder', 'msg-holder')
  commit(runtime, holder, 'holder', 'msg-holder')
  const waiting = await begin(runtime, 'waiting', 'msg-waiting')
  assert.equal(waiting.kind, 'Queued')
  routing.releasePhysicalExecution(runtime, 'holder', 'msg-holder')
  const resumed = await routing.awaitQueuedExecutionAdmission(waiting.queue)
  assert.equal(resumed.kind, 'Acquired')
  assert.deepEqual(routing.executionAdmissionTarget(runtime, resumed.lease), target)
  assert.equal(routing.pendingCount(runtime), 0)
  routing.releasePhysicalExecution(runtime, 'waiting', 'msg-waiting')
})
