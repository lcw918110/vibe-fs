import assert from 'node:assert/strict'
import test from 'node:test'
import { planRequest, bindDelegation, askNegotiation, prepareNegotiationReply, confirmNegotiationSteer, clearSessionState } from '../../../dist/Context/Scope/Surface.js'
import { refreshGlobalLanguage } from '../../../dist/Participant/Provider/LanguageSurface.js'
import { withPreference } from '../../provider-language/tests/support/language-fixtures.mjs'
import { withExecutablePlugin, acceptAuthorityRoot } from '../../verification-system/tests/support/plugin-fixture.mjs'

const user = (key, time = 1) => ({ kind: 'user', operator: true, key, timestamp: time, parts: [] })

test('WHAT[cognitive-environment-018] registered Provider requests localize new Scope steers without rewriting user or tool history', async () => {
  const session = 'scope-hni-live-language'
  try {
    await withExecutablePlugin(async (hooks, _directory, _created, runtime) => {
      await acceptAuthorityRoot(runtime, session, 'engineer', 'language-user')
      const original = [
        { info: { id: 'language-user', role: 'user', sessionID: session, time: { created: 1 } },
          parts: [{ type: 'text', text: 'operator bytes\t\r\n' }] },
        { info: { id: 'language-tool', role: 'assistant', sessionID: session, parentID: 'language-user', time: { created: 2, completed: 3 } },
          parts: [{ type: 'tool', tool: 'read', callID: 'historical-read', state: { status: 'completed', input: {}, output: 'tool bytes\t\r\n' } }] },
        { info: { id: 'language-run', role: 'assistant', sessionID: session, parentID: 'language-user', time: { created: 4 } }, parts: [] },
      ]
      const bytes = JSON.stringify(original)
      for (const message of original) runtime.pushHostMessage(session, message)
      await hooks['chat.message']({ sessionID: session, messageID: 'language-user', agent: 'engineer' }, {
        message: { id: 'language-user', role: 'user', agent: 'engineer' }, parts: original[0].parts,
      })
      const request = async (language) => withPreference(language, async () => {
        refreshGlobalLanguage()
        await hooks['chat.params']({ sessionID: session, agent: 'engineer',
          model: { providerID: 'fixture', modelID: 'engineer-model' } }, {})
        const output = { messages: structuredClone(original) }
        await hooks['experimental.chat.messages.transform']({ sessionID: session }, output)
        const steer = output.messages.find(message => message.steering && message.attribution === 'agent')
        assert.equal(steer.strong, true)
        assert.equal(output.messages.find(message => message.info?.id === 'language-user').parts[0].text, original[0].parts[0].text)
        assert.equal(output.messages.find(message => message.info?.id === 'language-tool').parts[0].state.output, original[1].parts[0].state.output)
        return steer.parts[0].text
      })
      const chinese = await request('zh-CN')
      assert.match(chinese, /\p{Script=Han}/u)
      const english = await request('en')
      assert.doesNotMatch(english, /\p{Script=Han}/u)
      assert.notEqual(english, chinese)
      assert.equal(JSON.stringify(original), bytes)
    })
  } finally {
    clearSessionState(session)
    refreshGlobalLanguage()
  }
})

test('WHAT[cognitive-environment-018] Chinese delegated-first negotiation unlocks on acceptance; only the matching recipient settles the reply', async () => {
  const session = 'scope-hni-zh-negotiation'
  try {
    await withPreference('zh-CN', async () => {
      refreshGlobalLanguage()
      assert.equal(askNegotiation('unmanaged-no-delegator', '核实细节').ok, false)
      bindDelegation(session, 'genuine-delegator')
      const first = planRequest(session, [user('delegated-task')])
      assert.equal(first.negotiationRequired, true)
      assert.match(first.strong, /\p{Script=Han}/u)
      const asked = askNegotiation(session, '之前为什么决定放弃方案 B？')
      assert.equal(asked.ok, true)
      assert.equal(planRequest(session, [user('delegated-task')]).negotiationRequired, false, '受理即完成，不等答复')
      const reply = prepareNegotiationReply(asked.receipt, '此前观察到反例。')
      assert.equal(reply.ok, true)
      assert.match(reply.steer, /\p{Script=Han}/u)
      assert.equal(confirmNegotiationSteer(asked.receipt, 'other-session'), false)
      assert.equal(confirmNegotiationSteer(asked.receipt, session), true)
      assert.equal(planRequest(session, [user('delegated-task'), user('answer', 2)]).negotiationRequired, false)
    })
  } finally {
    clearSessionState(session)
    refreshGlobalLanguage()
  }
})
