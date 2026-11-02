import test from 'node:test'
import assert from 'node:assert/strict'
import { requestLatencyDetector } from 'wanxiangli/degeneration'
import { withExecutablePlugin, bindManagedChild } from '../../verification-system/tests/support/plugin-fixture.mjs'

const model = { providerID: 'fixture', modelID: 'engineer-model' }
const assistant = (sessionID, id, parentID, finish) => ({
  id, sessionID, parentID, role: 'assistant', agent: 'engineer',
  providerID: model.providerID, modelID: model.modelID,
  time: { created: 1, ...(finish ? { completed: 2 } : {}) },
  ...(finish ? { finish } : {}),
})

test('registered OpenCode event and tool hooks settle exact model latency and forget a deleted session', async () => {
  await withExecutablePlugin(async (hooks, _directory, _created, runtime) => {
    const sessionID = 'latency-hook-child'
    const root = 'latency-hook-assignment'
    requestLatencyDetector.Forget(sessionID)
    await bindManagedChild(runtime, 'latency-hook-parent', sessionID, 'engineer')
    const message = { id: root, sessionID, role: 'user', agent: 'engineer', model }
    await hooks['chat.message']({ sessionID, messageID: root, agent: 'engineer', model },
      { message, parts: [{ type: 'text', text: 'Complete the assigned change.' }] })
    const emit = async info => {
      runtime.pushHostMessage(sessionID, { info, parts: [] })
      await hooks.event({ event: { type: 'message.updated', properties: { info } } })
    }
    try {
      await emit(assistant(sessionID, 'latency-hook-run-1', root))
      await emit(assistant(sessionID, 'latency-hook-run-1', root, 'tool-calls'))
      assert.equal(requestLatencyDetector.Snapshot(sessionID).Seen, 1)

      await emit(assistant(sessionID, 'latency-hook-run-2', root))
      await emit(assistant(sessionID, 'latency-hook-run-1', root, 'tool-calls'))
      assert.equal(requestLatencyDetector.Snapshot(sessionID).Seen, 1, 'an old step cannot settle the successor')
      await hooks['tool.execute.before'](
        { sessionID, tool: 'negotiate', callID: 'latency-hook-tool' },
        { args: { questions: 'Which file should change?', scope: { kind: 'keepLast' } } },
      )
      assert.equal(requestLatencyDetector.Snapshot(sessionID).Seen, 2, 'the registered tool hook ends model time')
      await emit(assistant(sessionID, 'latency-hook-run-2', root, 'tool-calls'))
      assert.equal(requestLatencyDetector.Snapshot(sessionID).Seen, 2, 'the later step-end does not add tool time')
      assert.deepEqual(runtime.abortedIds, [])
      await hooks.event({ event: { type: 'session.deleted', properties: { sessionID } } })
      assert.equal(requestLatencyDetector.Snapshot(sessionID).Seen, 0)
    } finally {
      requestLatencyDetector.Forget(sessionID)
    }
  })
})
