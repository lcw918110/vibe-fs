import test from 'node:test'
import assert from 'node:assert/strict'
import * as recovery from '../../../dist/OpenCode/Host/SessionRecoveryHostSurface.js'
import * as routing from '../../../dist/OpenCode/Host/ModelRoutingSurface.js'
import { withExecutablePlugin } from '../../verification-system/tests/support/plugin-fixture.mjs'

// WHAT[managed-chat-execution-016]
//
// OpenCode 1.19 triggers chat.message with `{ agent: input.agent, ... }`, so a
// prompt sent without an agent arrives as an OWN `agent` key whose value is
// `undefined`. JSON has no undefined: that carrier is absent, not malformed.
// A genuinely malformed carrier is absent too — it decodes to no explicit agent
// and follows the same agent-less admission path. The decode-layer semantics
// live in dispatch-protocol 015; this file pins the same distinction on the real
// registered chat.message hook, end to end through admission. Model
// projection details are pinned by 003 and deliberately not asserted here.

test('WHAT[managed-chat-execution-016] an own undefined agent key is an absent carrier on the real chat.message hook, admitted like any agent-less prompt', async () => {
  await withExecutablePlugin(async (hooks, _directory, _created, runtime) => {
    const sessionId = 'ses-agent-undefined'
    const messageId = 'msg-agent-undefined'
    const input = {
      sessionID: sessionId,
      messageID: messageId,
      agent: undefined,
      model: undefined,
      variant: undefined,
    }
    const output = {
      message: {
        id: messageId,
        sessionID: sessionId,
        role: 'user',
        agent: 'engineer',
        model: { providerID: 'host', modelID: 'placeholder' },
      },
      parts: [{ type: 'text', text: 'human prompt without an agent' }],
    }

    await hooks['chat.message'](input, output)

    assert.deepEqual(recovery.journalExecutionStatus(runtime.journal, sessionId, messageId), {
      phase: 'Accepted',
      disposition: null,
    })
    const capacity = routing.sharedCapacitySnapshot()
    assert.equal(
      capacity.executions.filter((execution) => execution.sessionId === sessionId).length,
      1,
    )
    assert.equal(
      capacity.tokens.filter((token) => token.owner.sessionId === sessionId).length,
      1,
    )
  })
})

test('WHAT[managed-chat-execution-016] a hook-reachable malformed agent carrier is absent, admitted like any agent-less prompt with zero side effects', async () => {
  await withExecutablePlugin(async (hooks, _directory, _created, runtime) => {
    const sessionId = 'ses-agent-malformed'
    const messageId = 'msg-agent-malformed'
    // Same shape as the admitted agent-less prompt above, except the agent
    // carrier: a non-string primitive is genuinely malformed at the hook, and a
    // malformed carrier is an absent carrier — no explicit agent, no rejection.
    const input = {
      sessionID: sessionId,
      messageID: messageId,
      agent: 42,
      model: undefined,
      variant: undefined,
    }
    const output = {
      message: {
        id: messageId,
        sessionID: sessionId,
        role: 'user',
        agent: 42,
        model: { providerID: 'host', modelID: 'placeholder' },
      },
      parts: [{ type: 'text', text: 'human prompt' }],
    }
    const before = routing.sharedCapacitySnapshot()

    await hooks['chat.message'](input, output)

    assert.deepEqual(output.message.model, { providerID: 'host', modelID: 'placeholder' })
    // No explicit agent and no other managed carrier: unmanaged, so no admission,
    // no capacity and no provider prompt — the same path an agent-less prompt takes.
    assert.equal(recovery.journalExecutionStatus(runtime.journal, sessionId, messageId), null)
    assert.deepEqual(routing.sharedCapacitySnapshot(), before)
    assert.equal(runtime.prompts.length, 0)
  })
})
