import assert from 'node:assert/strict'
import test from 'node:test'
import * as routing from '../../../../dist/OpenCode/Host/ModelRoutingSurface.js'

test('WHAT[execution-model-routing-004] a capacity policy error rejects pending and future demands', async () => {
  const runtime = routing.createRuntime(role => {
    if (role === 'devops') return null
    throw new Error('bad capacity policy')
  })
  const acquire = (session, role) => routing.acquireExecutionAdmission(runtime, session, `msg-${session}`, role, session, null)
  const waiting = acquire('waiter', 'devops')
  const rejected = assert.rejects(waiting, /bad capacity policy/)
  await Promise.resolve()
  assert.equal(routing.pendingCount(runtime), 1)
  await assert.rejects(acquire('boom', 'manager'), /bad capacity policy/)
  await rejected
  await assert.rejects(acquire('later', 'devops'), /bad capacity policy/)
  assert.equal(routing.pendingCount(runtime), 0)
})

test('WHAT[execution-model-routing-004] policy invocation preserves duplicate occupancy and exact previous target', () => {
  const running = [
    { model: 'provider/shared', reasoning: 'low' },
    { model: 'provider/shared', reasoning: 'low' },
  ]
  const previous = { model: 'provider/previous', reasoning: 'high' }
  const calls = []
  const policy = (...args) => {
    calls.push(args)
    return args[2]
  }
  assert.equal(routing.invokeScheduler(policy, 'engineer', running, null), null)
  assert.deepEqual(routing.invokeScheduler(policy, 'manager', running, previous), previous)
  assert.deepEqual(calls, [['engineer', running, null], ['manager', running, previous]])
})

test('WHAT[execution-model-routing-004] invalid policy results cannot grant admission', () => {
  assert.throws(() => routing.invokeScheduler(42, 'engineer', [], null), /function/i)
  assert.throws(() => routing.invokeScheduler(async () => ({ model: 'provider/x', reasoning: 'none' }), 'engineer', []), /Promise|synchronous/i)
  assert.throws(() => routing.invokeScheduler(() => ({ model: 'bare-model', reasoning: 'none' }), 'engineer', []), /provider\/model/i)
  assert.throws(() => routing.invokeScheduler(() => ({ model: 'provider/model', reasoning: '' }), 'engineer', []), /reasoning/i)
})

test('WHAT[execution-model-routing-004] an asynchronous capacity query rejects instead of queueing', async () => {
  const policy = () => null
  policy.hasTheoreticalCapacity = async () => false
  const runtime = routing.createRuntime(policy)
  const begin = session => routing.beginExecutionAdmission(runtime, session, `msg-${session}`, 'engineer', session, null)
  await assert.rejects(begin('first'), /hasTheoreticalCapacity must be synchronous/)
  assert.equal(routing.pendingCount(runtime), 0)
  await assert.rejects(begin('later'), /hasTheoreticalCapacity must be synchronous/)
})

test('WHAT[execution-model-routing-004] capacity queries and policy calls receive only their declared arguments', async () => {
  const calls = []
  const policy = (...args) => {
    calls.push(args)
    return { model: 'provider/model', reasoning: 'none' }
  }
  policy.hasTheoreticalCapacity = (...args) => {
    calls.push(args)
    return true
  }
  const runtime = routing.createRuntime(policy)
  const acquired = await routing.beginExecutionAdmission(runtime, 'session', 'msg-1', 'engineer', 'alice', null)
  assert.equal(acquired.kind, 'Acquired')
  assert.ok(calls.length >= 2)
  assert.deepEqual(calls[0], ['engineer'])
  assert.ok(calls.slice(1).every(args => args.length === 3))
  routing.releasePhysicalExecution(runtime, 'session', 'msg-1')
})
