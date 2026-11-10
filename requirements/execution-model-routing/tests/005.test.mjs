import assert from 'node:assert/strict'
import test from 'node:test'
import * as routing from '../../../dist/OpenCode/Host/ModelRoutingSurface.js'

const acquire = (runtime, id) => routing.beginExecutionAdmission(runtime, id, `msg-${id}`, 'engineer', id, null)

test('WHAT[execution-model-routing-005] changing only the scheduler changes both target and capacity policy', async () => {
  for (const [model, limit] of [['first/choice', 1], ['second/another-choice', 3]]) {
    const target = { model, reasoning: 'none' }
    const observations = []
    const runtime = routing.createRuntime((role, running, previous) => {
      observations.push({ role, running: structuredClone(running), previous })
      return running.length < limit ? target : null
    })
    for (let index = 0; index < limit; index += 1) {
      const result = await acquire(runtime, `owner-${index}`)
      assert.equal(result.kind, 'Acquired')
      assert.deepEqual(routing.executionAdmissionTarget(runtime, result.lease), target)
    }
    const waiting = await acquire(runtime, 'waiting')
    assert.equal(waiting.kind, 'Queued')
    assert.equal(routing.pendingCount(runtime), 1)
    assert.deepEqual(observations.at(-1).running, Array.from({ length: limit }, () => target))
    assert.equal(observations.at(-1).role, 'engineer')
    assert.equal(observations.at(-1).previous, null)
    // These leases never reached the provider, so their permits are still Pending:
    // the before-provider release is the only legal way to return the credit.
    routing.releasePendingPhysicalExecution(runtime, 'owner-0', 'msg-owner-0')
    const resumed = await routing.awaitQueuedExecutionAdmission(waiting.queue)
    assert.equal(resumed.kind, 'Acquired')
    assert.deepEqual(routing.executionAdmissionTarget(runtime, resumed.lease), target)
    routing.releasePendingPhysicalExecution(runtime, 'waiting', 'msg-waiting')
    for (let index = 1; index < limit; index += 1) {
      routing.releasePendingPhysicalExecution(runtime, `owner-${index}`, `msg-owner-${index}`)
    }
    assert.deepEqual(routing.snapshotOccupied(runtime), [])
  }
})
