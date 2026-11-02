import test from 'node:test'
import assert from 'node:assert/strict'
import { withExecutablePlugin } from '../../verification-system/tests/support/plugin-fixture.mjs'
import { outboundViewStore } from 'wanxiangli/pair-hint'
import { ProviderCalibration } from 'wanxiangli/provider-calibration'

// OpenCode 1.19.4: system.transform precedes chat.params; only params carries
// the actual agent. Auxiliary requests reuse the main session and user message.
test('WHAT[host-boundary-036] customized host agents preserve system and params in a managed session', async () => {
  await withExecutablePlugin(async (hooks) => {
    const config = { agent: {} }
    await hooks.config(config)
    const sessionID = 'ses-functional-shared'
    const model = { id: 'm', providerID: 'p', limit: { context: 100000 }, capabilities: { temperature: true } }
    const managedSystem = { system: [config.agent.manager.prompt + '\nHost environment'] }
    await hooks['experimental.chat.system.transform']({ sessionID, model }, managedSystem)
    assert.match(managedSystem.system.join('\n'), /resetScope/)
    ProviderCalibration.recordLimit(sessionID, 64000)
    for (const agent of ['title', 'compaction', 'summary', 'explore', 'generate', 'custom-helper']) {
      const system = { system: [`Custom ${agent} instructions. No product protocol.\n${config.agent.manager.prompt}`] }
      const original = structuredClone(system)
      await hooks['experimental.chat.system.transform']({ sessionID, model }, system)
      assert.deepEqual(system, original)
      const params = { temperature: 0.17, options: { custom: agent }, maxOutputTokens: 250 }
      await hooks['chat.params']({ sessionID, agent, model, message: { id: 'msg-shared', agent: 'manager', sessionID } }, params)
      assert.deepEqual(params, { temperature: 0.17, options: { custom: agent }, maxOutputTokens: 250 })
      assert.equal(ProviderCalibration.limit(sessionID), 64000)
    }
    outboundViewStore.clearSession(sessionID)
    ProviderCalibration.clearSession(sessionID)
  })
})

test('WHAT[host-boundary-036] compaction consumes its history bypass once and leaves the next managed system active', async () => {
  await withExecutablePlugin(async (hooks) => {
    const config = { agent: {} }
    await hooks.config(config)
    const sessionID = 'ses-compaction-once'
    await hooks['experimental.session.compacting']({ sessionID }, { context: [] })
    const history = [{ info: { id: 'msg-old', sessionID, role: 'user', agent: 'manager' }, parts: [{ type: 'text', text: 'Current conversation' }] }]
    const output = { messages: structuredClone(history) }
    const messages = output.messages
    await hooks['experimental.chat.messages.transform']({}, output)
    assert.equal(output.messages, messages)
    assert.deepEqual(output.messages, history)
    const system = { system: ['Custom compression instructions'] }
    await hooks['experimental.chat.system.transform']({ sessionID }, system)
    assert.deepEqual(system.system, ['Custom compression instructions'])
    const resumed = { system: [config.agent.manager.prompt] }
    await hooks['experimental.chat.system.transform']({ sessionID }, resumed)
    assert.match(resumed.system.join('\n'), /resetScope/)
    outboundViewStore.clearSession(sessionID)
  })
})

test('WHAT[host-boundary-036] functional ingress preserves host tools and content', async () => {
  await withExecutablePlugin(async (hooks) => {
    for (const agent of ['title', 'compaction', 'summary', 'explore', 'generate']) {
      const sessionID = `ses-ingress-${agent}`
      const output = { message: { id: `msg-${agent}`, sessionID, role: 'user', agent }, parts: [{ type: 'text', text: 'Host task' }] }
      const original = structuredClone(output)
      await hooks['chat.message']({ sessionID, agent }, output)
      assert.deepEqual(output, original)
    }
  })
})

test('OpenCode registered compaction hooks and snapshot callbacks share the session lifecycle', async () => {
  const { JournalSurface_snapshot } = await import('../../../dist/Persistence/Journal/Surface.js')
  await withExecutablePlugin(async (hooks, _directory, _created, runtime) => {
    const model = { providerID: 'fixture', modelID: 'engineer-model' }
    const sessionID = 'compaction-shared-snapshot'
    const other = 'compaction-other-session'
    const user = { id: 'compaction-shared-input', sessionID, role: 'user', agent: 'engineer', model }
    await hooks['chat.message']({ sessionID, messageID: user.id, agent: 'engineer', model },
      { message: user, parts: [{ type: 'text', text: 'Continue the assignment.' }] })
    await hooks['experimental.session.compacting']({ sessionID }, { context: [] })
    await hooks['experimental.session.compacting']({ sessionID: other }, { context: [] })
    const output = { messages: [{ info: user, parts: [{ type: 'text', text: 'Original transcript.' }] }] }
    const original = output.messages
    await hooks['experimental.chat.messages.transform']({}, output)
    assert.equal(output.messages, original)
    const continuation = {}
    await hooks['experimental.compaction.autocontinue']({ sessionID }, continuation)
    assert.equal(continuation.enabled, true)
    const compacted = { id: 'compaction-shared-run', sessionID, parentID: user.id, role: 'assistant', agent: 'compaction', summary: true, finish: 'stop', time: { created: 1, completed: 2 } }
    runtime.pushHostMessage(sessionID, { info: compacted, parts: [] })
    await hooks.event({ event: { type: 'message.updated', properties: { info: compacted } } })
    const deadline = Date.now() + 2000
    while (JournalSurface_snapshot(runtime.journal).sessionProjections[sessionID]?.prefixEpoch?.epochId !== 1n && Date.now() < deadline) {
      await new Promise(resolve => setTimeout(resolve, 5))
    }
    assert.equal(JournalSurface_snapshot(runtime.journal).sessionProjections[sessionID].prefixEpoch.epochId, 1n)
    const otherOutput = { messages: [{ info: { ...user, sessionID: other }, parts: [] }] }
    const otherMessages = otherOutput.messages
    await hooks['experimental.chat.messages.transform']({}, otherOutput)
    assert.equal(otherOutput.messages, otherMessages, 'one session snapshot does not consume the other session bypass')
    await hooks.event({ event: { type: 'message.updated', properties: { info: compacted } } })
    assert.equal(JournalSurface_snapshot(runtime.journal).sessionProjections[sessionID].prefixEpoch.epochId, 1n)
  })
})
