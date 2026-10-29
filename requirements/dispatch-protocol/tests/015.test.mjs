import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { default: fc } = await import("fast-check");
// CUT-40: the chat.message ingress decode is exercised on the OpenCode codec surface (one copy, next to PromptIngressCodec).
const { decodeIngress } = await import("../../../dist/OpenCode/Codec/ProviderProjectionSurface.js");

// An own key holding `undefined` is an absent JSON carrier, not a malformed one (see the undefined test below).
const malformedString = fc.anything({ withBoxedValues: true }).filter(value => typeof value !== 'string' && value !== undefined)
const nonblankString = fc.string().filter(value => value.trim().length > 0)

test('WHAT[dispatch-protocol-015] ingress identity property rejects every malformed or ambiguous carrier world', () => {
  fc.assert(fc.property(malformedString, nonblankString, (malformed, valid) => {
    assert.doesNotThrow(() => decodeIngress({ sessionID: malformed }, {}))
    assert.equal(decodeIngress({ sessionID: malformed }, {}).sessionId, null)
    assert.equal(decodeIngress({ sessionID: valid, sessionId: malformed }, {}).sessionId, null)
    assert.equal(decodeIngress({ agent: valid }, { message: { agent: malformed } }).explicitAgent, null)
    assert.equal(
      decodeIngress({ metadata: { wanxiangshu_prompt_key: valid } }, { parts: [{ metadata: { wanxiangshu_prompt_key: malformed } }] }).promptKey,
      null,
    )
  }), { seed: 15015, numRuns: 160 })

  fc.assert(fc.property(nonblankString, nonblankString, (left, right) => {
    fc.pre(left !== right)
    assert.equal(decodeIngress({ sessionID: left }, { info: { sessionID: right } }).sessionId, null)
  }), { seed: 25015, numRuns: 120 })
})
test('WHAT[dispatch-protocol-015] generated non-arrays and non-booleans remain inert without exceptions', () => {
  fc.assert(fc.property(fc.anything({ withBoxedValues: true }), value => {
    if (!Array.isArray(value)) {
      assert.doesNotThrow(() => decodeIngress({}, { parts: value }))
      assert.equal(decodeIngress({}, { parts: value }).isHostSynthetic, false)
    }
    if (typeof value !== 'boolean') {
      assert.equal(decodeIngress({}, { message: { summary: value } }).isHostCompaction, false)
      assert.equal(decodeIngress({}, { parts: [{ synthetic: value }] }).isHostSynthetic, false)
    }
  }), { seed: 35015, numRuns: 200 })
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
// CUT-40: the chat.message ingress decode is exercised on the OpenCode codec surface (one copy, next to PromptIngressCodec).
const { decodeIngress } = await import("../../../dist/OpenCode/Codec/ProviderProjectionSurface.js");


test('WHAT[dispatch-protocol-015] ingress identity carrier algebra is exact conflict closed and byte preserving', () => {
  const value = '  session opaque  '
  const decoded = decodeIngress(
    { sessionID: value, session: { id: value }, agent: 'agent-a', metadata: { wanxiangshu_prompt_key: 'prompt-a' } },
    {
      sessionId: value,
      session: { sessionID: value },
      agent: 'agent-a',
      message: { session: { sessionId: value }, agent: 'agent-a' },
      info: { sessionID: value },
      parts: [{ type: 'text', text: 'hello', metadata: { wanxiangshu_prompt_key: 'prompt-a' } }],
    },
  )

  assert.deepEqual(decoded, {
    sessionId: value,
    physicalUserMessageId: null,
    explicitAgent: 'agent-a',
    promptKey: 'prompt-a',
    isHostCompaction: false,
    isHostSynthetic: false,
    text: 'hello',
  })
})
test('WHAT[dispatch-protocol-015] ingress rejects conflicting or explicitly invalid SessionId carriers', () => {
  const conflicts = [
    [{ sessionID: 'a', sessionId: 'b' }, {}],
    [{ sessionID: 'a', session: { id: 'b' } }, {}],
    [{ sessionID: 'a' }, { sessionID: 'b' }],
    [{ sessionID: 'a' }, { message: { sessionID: 'b' } }],
    [{ sessionID: 'a' }, { info: { sessionID: 'b' } }],
    [{ sessionID: 'a', sessionId: 7 }, {}],
    [{ sessionID: 'a', session: { id: null } }, {}],
    [{ sessionID: 'a' }, { message: { sessionID: {} } }],
  ]

  for (const [input, output] of conflicts) assert.equal(decodeIngress(input, output).sessionId, null)
})
test('WHAT[dispatch-protocol-015] ingress accepts plain own data fields only and never invokes accessors', () => {
  const inherited = Object.create({ id: 'inherited' })
  const nullPrototype = Object.create(null)
  nullPrototype.id = 'plain-null-prototype'
  let getterCalls = 0
  const accessor = {}
  Object.defineProperty(accessor, 'id', { enumerable: true, get() { getterCalls += 1; return 'getter' } })

  assert.equal(decodeIngress({ session: inherited }, {}).sessionId, null)
  assert.equal(decodeIngress({ session: accessor }, {}).sessionId, null)
  assert.equal(getterCalls, 0)
  assert.equal(decodeIngress({ session: nullPrototype }, {}).sessionId, 'plain-null-prototype')
  assert.equal(decodeIngress({ session: new String('boxed') }, {}).sessionId, null)
})
test('WHAT[dispatch-protocol-015] agent and PromptKey require one exact primitive string', () => {
  assert.equal(decodeIngress({ agent: 'a' }, { message: { agent: 'b' } }).explicitAgent, null)
  assert.equal(decodeIngress({ agent: 'a' }, { info: { agent: 7 } }).explicitAgent, null)
  assert.equal(
    decodeIngress({ metadata: { wanxiangshu_prompt_key: 'a' } }, { parts: [{ metadata: { wanxiangshu_prompt_key: 'b' } }] }).promptKey,
    null,
  )
  assert.equal(
    decodeIngress({ metadata: { wanxiangshu_prompt_key: 'a' } }, { parts: [{ metadata: { wanxiangshu_prompt_key: true } }] }).promptKey,
    null,
  )
})
test('WHAT[dispatch-protocol-015] explicit malformed carrier containers cannot collapse into Missing', () => {
  let metadataGetterCalls = 0
  const metadataAccessor = {}
  Object.defineProperty(metadataAccessor, 'metadata', {
    enumerable: true,
    get() { metadataGetterCalls += 1; return { wanxiangshu_prompt_key: 'hidden' } },
  })
  assert.equal(
    decodeIngress({ metadata: { wanxiangshu_prompt_key: 'valid' } }, { parts: [{ metadata: 7 }] }).promptKey,
    null,
  )
  assert.equal(
    decodeIngress({ metadata: { wanxiangshu_prompt_key: 'valid' } }, { parts: [metadataAccessor] }).promptKey,
    null,
  )
  assert.equal(metadataGetterCalls, 0)
  assert.equal(
    decodeIngress({ metadata: { wanxiangshu_prompt_key: 'valid' } }, { parts: [{ metadata: new String('boxed') }] }).promptKey,
    null,
  )

  let messageGetterCalls = 0
  const outputWithAccessor = { info: { sessionID: 'valid', agent: 'valid-agent' } }
  Object.defineProperty(outputWithAccessor, 'message', {
    enumerable: true,
    get() { messageGetterCalls += 1; return { sessionID: 'hidden', agent: 'hidden' } },
  })
  assert.equal(decodeIngress({}, { message: 7, info: { sessionID: 'valid' } }).sessionId, null)
  assert.equal(decodeIngress({}, outputWithAccessor).sessionId, null)
  assert.equal(decodeIngress({}, outputWithAccessor).explicitAgent, null)
  assert.equal(messageGetterCalls, 0)
  assert.equal(decodeIngress({ agent: 'valid-agent' }, { info: new String('boxed') }).explicitAgent, null)

  assert.deepEqual(
    decodeIngress({ session: 's1', agent: 'agent-a' }, {}).sessionId,
    's1',
    'the valid scalar session grammar is not an agent container error',
  )
})
test('WHAT[dispatch-protocol-015] malformed parts and boolean lookalikes are absent and never throw', () => {
  for (const parts of [7, 'text', {}, true, new String('boxed')]) {
    assert.doesNotThrow(() => decodeIngress({}, { parts }))
    assert.deepEqual(decodeIngress({}, { parts }), {
      sessionId: null,
      physicalUserMessageId: null,
      explicitAgent: null,
      promptKey: null,
      isHostCompaction: false,
      isHostSynthetic: false,
      text: null,
    })
  }

  for (const summary of [1, 'true', {}, [], new Boolean(true)]) {
    assert.equal(decodeIngress({}, { message: { summary } }).isHostCompaction, false)
  }
  for (const synthetic of [1, 'true', {}, [], new Boolean(true)]) {
    assert.equal(decodeIngress({}, { parts: [{ synthetic }] }).isHostSynthetic, false)
  }
})
}

test('WHAT[dispatch-protocol-015] actual ingress treats a malformed PromptKey as absent and admits by the valid explicit agent', async () => {
  const { default: assert } = await import('node:assert/strict')
  const { withExecutablePlugin } = await import('../../verification-system/tests/support/plugin-fixture.mjs')
  const dispatch = await import('../../../dist/Interaction/Dispatch/DispatchSurface.js')
  await withExecutablePlugin(async (hooks, _directory, _children, runtime) => {
    // The real Host fills the message model it selected (the configured declared
    // value); the plugin reads it to select the committed lease and never writes back.
    const output = (session, physical) => ({ message: { sessionID: session, id: physical, agent: 'engineer', role: 'user',
      model: { providerID: 'fixture', modelID: 'engineer-model' } }, parts: [] })
    await hooks['chat.message']({ sessionID: 'valid-carrier-control', messageID: 'valid-physical', agent: 'engineer' }, output('valid-carrier-control', 'valid-physical'))
    assert.notEqual(dispatch.projectionObservation(runtime.journal, 'valid-carrier-control').activeLogicalRun, null)
    // A malformed PromptKey is an absent PromptKey: the message is exactly the
    // plain explicit-agent prompt it otherwise looks like, and with no active run
    // in a fresh session it is admitted as a new external root by that agent.
    await hooks['chat.message']({ sessionID: 'invalid-key', messageID: 'invalid-key-physical', agent: 'engineer', metadata: { wanxiangshu_prompt_key: 7 } }, output('invalid-key', 'invalid-key-physical'))
    assert.notEqual(dispatch.projectionObservation(runtime.journal, 'invalid-key').activeLogicalRun, null,
      'a malformed prompt key is absent; the valid explicit agent grants the external root on its own')
  })
})

test('WHAT[dispatch-protocol-015] a malformed explicit agent is absent and the pending claim still admits the prompt', async () => {
  const { default: assert } = await import('node:assert/strict')
  const { withExecutablePlugin } = await import('../../verification-system/tests/support/plugin-fixture.mjs')
  const dispatch = await import('../../../dist/Interaction/Dispatch/DispatchSurface.js')
  const authority = await import('../../../dist/Interaction/Authority/RuntimeSurface.js')
  const { acceptOwner, hostPort } = await import('./support/authority.mjs')
  await withExecutablePlugin(async (hooks, _directory, _children, runtime) => {
    const seed = authority.issueInheritedIdentitySeed('engineer', await acceptOwner(runtime.journal, 'carrier-owner'))
    assert.equal(seed.ok, true)
    const sent = await dispatch.sendAgentOwnerRootAwait(hostPort(async () => dispatch.admittedWithReceipt('accepted-carrier')), runtime.journal, 'carrier-child', 'assignment', seed.value)
    assert.equal(sent.ok, true)
    // The malformed agent carrier is absent, so the exact pending claim carries the
    // message on its own — the same path as a prompt sent without an agent field.
    await hooks['chat.message']({ sessionID: 'carrier-child', messageID: 'carrier-physical', agent: 7, metadata: { wanxiangshu_prompt_key: sent.key } }, {
      message: { sessionID: 'carrier-child', id: 'carrier-physical', agent: 'engineer', role: 'user',
        model: { providerID: 'fixture', modelID: 'engineer-model' } }, parts: [],
    })
    const projection = dispatch.projectionObservation(runtime.journal, 'carrier-child')
    assert.notEqual(projection.activeLogicalRun, null, 'the pending claim admits its prompt without an explicit agent')
    assert.equal(projection.pendingClaims.length, 0)
  })
})

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { decodeIngress } = await import("../../../dist/OpenCode/Codec/ProviderProjectionSurface.js");
test('WHAT[dispatch-protocol-015] an own carrier key holding undefined is absent, as OpenCode 1.19 chat.message sends it', () => {
  // OpenCode 1.19.4 triggers chat.message with { agent: input.agent, ... }; an agent-less prompt_async
  // therefore arrives with agent: undefined. That must decode as no explicit agent.
  const decoded = decodeIngress(
    { sessionID: 's-undef', agent: undefined, model: undefined, messageID: 'm-undef', variant: undefined },
    { message: { sessionID: 's-undef', id: 'm-undef', agent: 'manager', role: 'user' }, parts: [] },
  )
  assert.equal(decoded.sessionId, 's-undef')
  assert.equal(decoded.explicitAgent, 'manager')
  assert.equal(decodeIngress({ sessionID: 's', agent: undefined }, {}).explicitAgent, null)
  assert.equal(decodeIngress({ sessionID: 's', agent: null }, {}).explicitAgent, null)
})
}
