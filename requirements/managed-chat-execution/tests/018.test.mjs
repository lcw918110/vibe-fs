import test from 'node:test'
import assert from 'node:assert/strict'
import * as recovery from '../../../dist/OpenCode/Host/SessionRecoveryHostSurface.js'
import { withExecutablePlugin } from '../../verification-system/tests/support/plugin-fixture.mjs'

// Field incident 2026-10-28 18:32 UTC (Manager ses_e7ed7cdb2ffe…): a consultation answer
// arrived as user row msg_183c8b54d while the Host loop was still running; the Host
// built assistant run msg_183c8b793 under it, and the transform died with
// `MANAGED-CHAT-005 … frozen-attempt-plan-missing` because the process-local frozen
// plan for that exact run was not there. The run was real and its execution durable
// Accepted, so nothing was missing but a cache entry.

const model = { providerID: 'provider', modelID: 'manager-model' }
const userInfo = (sessionID, id) => ({ id, sessionID, role: 'user', agent: 'engineer', model })
const hostAssistant = (sessionID, id, parentID, created) => ({
  id, sessionID, parentID, role: 'assistant', agent: 'engineer', providerID: model.providerID,
  modelID: model.modelID, time: { created },
})

test('WHAT[managed-chat-execution-018] the Host run starts its execution with no frozen plan in the process', async () => {
  await withExecutablePlugin(async (hooks, _directory, _created, runtime) => {
    const sessionID = 'ses-no-frozen-plan-018'
    const root = userInfo(sessionID, 'msg-root-018')
    await hooks['chat.message']({ sessionID, messageID: root.id, agent: 'engineer', model },
      { message: root, parts: [{ type: 'text', text: 'assignment' }] })
    assert.deepEqual(recovery.journalExecutionStatus(runtime.journal, sessionID, root.id),
      { phase: 'Accepted', disposition: null }, 'construction: the execution is durable Accepted')

    // The Host announces its run before any transform froze a plan for it.
    const assistant = hostAssistant(sessionID, 'assistant-018', root.id, 1)
    runtime.pushHostMessage(sessionID, { info: assistant, parts: [] })
    await hooks.event({ event: { type: 'message.updated', properties: { info: assistant } } })
    assert.deepEqual(recovery.journalExecutionStatus(runtime.journal, sessionID, root.id),
      { phase: 'ProviderStarted', disposition: null },
      'the plan is rebuilt from the Accepted fact; the start is not refused for a missing cache entry')

    // The later transform for the same run neither refuses nor starts it twice.
    await hooks['experimental.chat.messages.transform']({}, {
      messages: [{ info: root, parts: [{ type: 'text', text: 'assignment' }] }],
    })
    assert.deepEqual(recovery.journalExecutionStatus(runtime.journal, sessionID, root.id),
      { phase: 'ProviderStarted', disposition: null })
    assert.deepEqual(runtime.abortedIds, [])
  })
})
