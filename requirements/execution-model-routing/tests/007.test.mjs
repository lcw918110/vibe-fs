import assert from 'node:assert/strict'
import test from 'node:test'
import * as signals from '../../../dist/OpenCode/Host/HostSignalSurface.js'

const assistant = (finish, extra = {}) => ({
  type: 'message.updated',
  properties: { info: {
    role: 'assistant', sessionID: 'session', id: 'provider-run', parentID: 'physical',
    time: { created: 1, completed: 2 }, finish, ...extra,
  } },
})

test('WHAT[execution-model-routing-007] actual decoder distinguishes successful physical end from tool and failed step end', () => {
  for (const finish of ['stop', 'length', 'content-filter']) {
    assert.deepEqual(signals.tryDecodePhysicalExecutionEnd(assistant(finish)), {
      sessionId: 'session', physicalUserMessageId: 'physical',
    })
  }
  for (const finish of ['tool-calls', 'unknown', 'error']) {
    assert.equal(signals.tryDecodePhysicalExecutionEnd(assistant(finish)), null)
    assert.ok(signals.tryDecodeProviderStepEnd(assistant(finish)))
  }
  for (const event of [
    assistant('stop', { error: { name: 'TimeoutError' } }),
    assistant('stop', { parentID: '' }),
    assistant('stop', { time: { created: 1 } }),
    { type: 'session.idle', properties: { sessionID: 'session' } },
  ]) {
    assert.equal(signals.tryDecodePhysicalExecutionEnd(event), null)
  }
})

test.todo('WHAT[execution-model-routing-007] actual Host event closes the physical execution only after durable terminal and idle/business events leave binding intact (GAP-128)')
