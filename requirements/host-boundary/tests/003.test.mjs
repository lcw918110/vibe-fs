import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const HostSignalSurface = await import("../../../dist/OpenCode/Host/HostSignalSurface.js");
const CompactionPolicySurface = await import("../../../dist/Host/Contract/CompactionPolicySurface.js");

const requiredSettings = CompactionPolicySurface.requiredSettings()
const judgeFirstTurn = (pseudoRuns) => CompactionPolicySurface.judgeFirstTurn('ses_probe', pseudoRuns)

test('WHAT[host-boundary-003] HOST_003_retry_signal_is_a_typed_wake_never_a_run_identity_carrier', () => {
  const retry = HostSignalSurface.tryDecode({ type: 'session.status', sessionID: 'ses_retry', properties: { status: { type: 'retry', attempt: 3, reason: 'again' } } })
  assert.equal(retry.kind, 'ProviderRetry')
  assert.equal(retry.sessionId, 'ses_retry')
  assert.equal('messageId' in retry, false)
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { withExecutablePlugin } = await import('../../verification-system/tests/support/plugin-fixture.mjs')
const routing = await import('../../../dist/OpenCode/Host/ModelRoutingSurface.js')
const recovery = await import('../../../dist/OpenCode/Host/SessionRecoveryHostSurface.js')

test('WHAT[host-boundary-003] coarse abort is only a wake while an exact operator cancellation settles its own accepted execution', async () => {
  await withExecutablePlugin(async (hooks, _directory, _created, runtime) => {
    const sessionID = 'ses-exact-operator-cancel'
    const message = { id: 'msg-current', sessionID, role: 'user', agent: 'engineer', model: { providerID: 'provider', modelID: 'engineer-model' } }
    await hooks['chat.message']({ sessionID, messageID: message.id, agent: 'engineer' }, {
      message, parts: [{ type: 'text', text: 'operator input' }],
    })
    const before = routing.sharedCapacitySnapshot()
    await hooks.event({ event: { type: 'session.error', properties: {
      sessionID, error: { name: 'MessageAbortedError', data: { message: 'operator interrupted' } },
    } } })
    assert.deepEqual(routing.sharedCapacitySnapshot(), before)
    assert.deepEqual(recovery.journalExecutionStatus(runtime.journal, sessionID, message.id), {
      phase: 'Accepted', disposition: null,
    })
    await hooks['chat.params']({ sessionID, message, agent: 'engineer', model: {
      providerID: 'provider', id: 'engineer-model', capabilities: {},
    } }, {})
    await hooks.event({ event: { type: 'message.updated', properties: { info: {
      id: 'assistant-current', parentID: message.id, sessionID, role: 'assistant', agent: 'engineer',
      providerID: 'provider', modelID: 'engineer-model', time: { created: 1, completed: 2 },
      error: { name: 'MessageAbortedError', data: { message: 'operator interrupted' } },
    } } } })
    assert.deepEqual(recovery.journalExecutionStatus(runtime.journal, sessionID, message.id), {
      phase: 'Terminal', disposition: 'Cancelled',
    })
    assert.equal(routing.sharedCapacitySnapshot().executions.some((owner) => owner.sessionId === sessionID), false)
    assert.equal(runtime.prompts.length, 0)
  })
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const HostSignalSurface = await import("../../../dist/OpenCode/Host/HostSignalSurface.js");
const HostSignalSubscribeSurface = await import("../../../dist/OpenCode/Host/HostSignalSubscribeSurface.js");

process.env.WANXIANGSHU_NO_FATAL_EXIT = '1'
const idleRaw = (sessionId) => ({ type: 'session.status', sessionID: sessionId, properties: { status: { type: 'idle' } } })
const dedicatedIdleRaw = (sessionId) => ({ type: 'session.idle', properties: { sessionID: sessionId } })
const retryRaw = (sessionId) => ({ type: 'session.status', sessionID: sessionId, properties: { status: { type: 'retry', attempt: '2', message: 'rate limited' } } })
const deletedRaw = (sessionId, parentID) => ({ type: 'session.deleted', sessionID: sessionId, properties: { parentID } })
const errorRaw = (sessionId, name = 'TimeoutError') => ({ type: 'session.error', sessionID: sessionId, properties: { error: { name } } })
const trySubscribe = async (input = {}) => HostSignalSubscribeSurface.trySubscribe(input, () => {})

test('WHAT[host-boundary-003] MISC_signals_session_id_of_all_cases', () => {
  for (const raw of [idleRaw('s1'), retryRaw('s1'), deletedRaw('s1', 'root'), errorRaw('s1')]) {
    assert.equal(HostSignalSurface.tryDecode(raw).sessionId, 's1')
  }
})
}
