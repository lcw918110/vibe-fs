import test from 'node:test'
import assert from 'node:assert/strict'

test.todo('WHAT[context-compression-001] compile and audit the actual compression dependency boundary for absence of model-capacity authority; vocabulary scans cannot establish this property; GAP-104')

test('WHAT[context-compression-001] the real OpenCode hooks share one provider calibration owner and clear it on deletion', async () => {
  const assert = (await import('node:assert/strict')).default
  const calibration = (await import('wanxiangli/provider-calibration')).ProviderCalibration
  const params = await import('../../../dist/OpenCode/Host/ChatParamsSurface.js')
  const { withExecutablePlugin } = await import('../../verification-system/tests/support/plugin-fixture.mjs')
  const session = 'calibration-extracted-owner'
  calibration.clearAll()

  const seen = params.apply(
    { sessionID: session, model: { limit: { context: 32000 } } },
    { model: { modelID: 'host-model' } },
  )
  assert.equal(seen.ok, true, seen.error)
  assert.equal(calibration.limit(session), 32000)
  assert.equal(calibration.usage(session), null)

  await withExecutablePlugin(async (hooks) => {
    await hooks.event({
      event: {
        type: 'message.updated',
        properties: {
          info: {
            id: 'calibration-real-assistant',
            sessionID: session,
            role: 'assistant',
            tokens: { cache: { read: 456 }, input: 123 },
          },
        },
      },
    })
    assert.deepEqual(calibration.usage(session), { cached: 456, uncached: 123 })
    assert.equal(calibration.limit(session), 32000)

    await hooks.event({
      event: { type: 'session.deleted', properties: { sessionID: session } },
    })
    assert.equal(calibration.usage(session), null)
    assert.equal(calibration.limit(session), null)
  })
  calibration.clearAll()
})

test('WHAT[context-compression-001] functional agents (title and compaction) bypass managed observation barrier and preserve payload', async () => {
  const { withExecutablePlugin } = await import('../../verification-system/tests/support/plugin-fixture.mjs')
  const session = 'functional-agent-bypass-session'

  await withExecutablePlugin(async (hooks) => {
    // 0. chat.message hook: functional agents bypass managed admission barrier without lease errors
    const titleMsgIn = {
      sessionID: session,
      agent: 'title',
      messageID: 'msg-title-001',
    }
    const titleMsgOut = {
      message: { id: 'msg-title-001', sessionID: session, role: 'user', agent: 'title' },
      parts: [{ type: 'text', text: 'Generate title' }],
    }
    await hooks['chat.message'](titleMsgIn, titleMsgOut)
    assert.equal(titleMsgOut.message.id, 'msg-title-001')

    const compMsgIn = {
      sessionID: session,
      agent: 'compaction',
      messageID: 'msg-compaction-001',
    }
    const compMsgOut = {
      message: { id: 'msg-compaction-001', sessionID: session, role: 'user' },
      parts: [{ type: 'text', text: 'Compact conversation' }],
    }
    await hooks['chat.message'](compMsgIn, compMsgOut)
    assert.equal(compMsgOut.message.id, 'msg-compaction-001')

    // 1. chat.params hook: unmanaged functional agent calls must not throw PROMPT-006 lease errors
    // and must preserve the original temperature without managed forced overrides
    const titleInput = {
      sessionID: session,
      agent: 'title',
      model: { id: 'title-small-model', limit: { context: 16000 } },
    }
    const titleOutput = { temperature: 0.2 }
    await hooks['chat.params'](titleInput, titleOutput)
    assert.equal(titleOutput.temperature, 0.2, 'title agent preserves its own temperature setting')

    const compactionInput = {
      sessionID: session,
      info: { agent: 'compaction' },
      model: { id: 'compaction-model', limit: { context: 32000 } },
    }
    const compactionOutput = { temperature: 0.0 }
    await hooks['chat.params'](compactionInput, compactionOutput)
    assert.equal(compactionOutput.temperature, 0.0, 'compaction agent preserves its own temperature setting')

    // 2. system.transform hook: unmanaged functional agents pass through unmodified
    const sysInput = { sessionID: session, agent: 'title' }
    const sysOutput = { system: ['System prompt for title extraction only.'] }
    await hooks['experimental.chat.system.transform'](sysInput, sysOutput)
    assert.deepEqual(
      sysOutput.system,
      ['System prompt for title extraction only.'],
      'system.transform leaves title system prompt 100% untouched without injecting Scope or Common Law rules',
    )

    // 3. messages.transform hook: unmanaged functional agents bypass ScopeSession tail injection
    // Scenario A: explicit inObj.agent = 'title'
    const msgInTitle = { sessionID: session, agent: 'title' }
    const msgOutTitle = {
      messages: [
        {
          info: { role: 'user', id: 'user-title-msg' },
          parts: [{ type: 'text', text: 'Generate title for this conversation' }],
        },
      ],
    }
    await hooks['experimental.chat.messages.transform'](msgInTitle, msgOutTitle)
    assert.equal(msgOutTitle.messages.length, 1)
    assert.equal(msgOutTitle.messages[0].parts.length, 1)
    assert.equal(msgOutTitle.messages[0].parts[0].text, 'Generate title for this conversation')

    // Scenario B: OpenCode real physical calling convention where inObj = {} and compaction is marked on the last message
    const msgInEmpty = {}
    const msgOutCompaction = {
      messages: [
        {
          info: { role: 'user', id: 'm1' },
          parts: [{ type: 'text', text: 'Earlier context turn 1' }],
        },
        {
          info: { role: 'assistant', id: 'm2', mode: 'compaction' },
          parts: [{ type: 'text', text: 'Summarize earlier messages please' }],
        },
      ],
    }
    await hooks['experimental.chat.messages.transform'](msgInEmpty, msgOutCompaction)
    assert.equal(msgOutCompaction.messages.length, 2)
    assert.equal(
      msgOutCompaction.messages[1].parts[0].text,
      'Summarize earlier messages please',
      'messages.transform directly preserves compaction output payload without scope injection',
    )
  })
})
