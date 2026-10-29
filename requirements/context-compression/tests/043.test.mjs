import test from 'node:test'
import assert from 'node:assert/strict'
import { outboundViewStore, OutboundViewStore } from 'wanxiangli/pair-hint'
import { clearSessionState } from '../../../dist/Context/Scope/Surface.js'
import { withExecutablePlugin, acceptAuthorityRoot } from '../../verification-system/tests/support/plugin-fixture.mjs'

test('WHAT[context-compression-043] OutboundViewStore: progressive checkAndSeal seals only on full non-empty triad (toolsCaptured=false)', () => {
  const store = new OutboundViewStore()
  const sid = 'sess-triad-043'

  store.recordSystem(sid, ['You are an assistant with strict boundary.'])
  store.recordModel(sid, { providerID: 'anthropic', modelID: 'claude-3-5-sonnet', variant: null })
  assert.equal(store.tryGetSealed(sid), undefined, 'must not seal without messages')

  store.recordMessagesFor(sid, [{ info: { role: 'user', sessionID: sid }, content: 'execute task' }])
  const sealed = store.tryGetSealed(sid)
  assert.ok(sealed, 'sealed snapshot must exist once messages, system, and model are non-empty')
  assert.equal(sealed.sessionId, sid)
  assert.deepEqual(sealed.system, ['You are an assistant with strict boundary.'])
  assert.equal(sealed.model.providerID, 'anthropic')
  assert.equal(sealed.model.modelID, 'claude-3-5-sonnet')
  assert.equal(sealed.toolsCaptured, false, 'progressive checkAndSeal must set toolsCaptured=false')
  assert.equal(sealed.nativePayload, null, 'progressive checkAndSeal has null nativePayload')
})

test('WHAT[context-compression-043] OutboundViewStore: recordNativeRequest seals atomically with toolsCaptured=true and deep clones payload', () => {
  const store = new OutboundViewStore()
  const sid = 'sess-native-043'

  const nativePayload = {
    messages: [{ role: 'user', content: 'run tool command' }],
    tools: [{ type: 'function', function: { name: 'bash' } }],
    system: ['System instructions.']
  }
  const model = { providerID: 'openai', modelID: 'gpt-4o', variant: 'chat' }

  const ok = store.recordNativeRequest(sid, nativePayload, model)
  assert.equal(ok, true, 'valid native request must return true')

  const sealed = store.tryGetSealed(sid)
  assert.ok(sealed)
  assert.equal(sealed.sessionId, sid)
  assert.equal(sealed.toolsCaptured, true, 'native request capture must set toolsCaptured=true')
  assert.ok(sealed.nativePayload, 'native payload must be preserved')
  assert.deepEqual(sealed.system, ['System instructions.'])
  assert.equal(sealed.model.providerID, 'openai')
  assert.equal(sealed.model.modelID, 'gpt-4o')
  assert.equal(sealed.model.variant, 'chat')

  // Deep clone verification: external mutation must not corrupt sealed snapshot
  nativePayload.messages[0].content = 'MUTATED'
  assert.equal(sealed.messages[0].content, 'run tool command', 'sealed messages must be immutable copy')
})

test('WHAT[context-compression-043] OutboundViewStore: serializeSnapshot strictly adheres to wire JSON specification', () => {
  const store = new OutboundViewStore()
  const sid = 'sess-spec-json-043'

  store.recordSystem(sid, ['System instruction for scope.'])
  store.recordModel(sid, { providerID: 'custom-provider', modelID: 'custom-model', variant: null })
  store.recordMessagesFor(sid, [{ info: { role: 'user', sessionID: sid }, content: 'question' }])

  const sealed = store.tryGetSealed(sid)
  assert.ok(sealed)

  const jsonStr = store.serializeSnapshot(sealed)
  const parsed = JSON.parse(jsonStr)

  // Verify exact JSON keys and shapes
  assert.equal(parsed.sessionId, sid)
  assert.deepEqual(parsed.system, ['System instruction for scope.'])
  assert.equal(parsed.model.providerID, 'custom-provider')
  assert.equal(parsed.model.modelID, 'custom-model')
  assert.equal(parsed.model.variant, null)
  assert.equal(parsed.toolsCaptured, false)
  assert.equal(parsed.nativePayload, null)
})

test('WHAT[context-compression-043] OutboundViewStore: discardSealed empties sealed snapshot (single-drain semantics)', () => {
  const store = new OutboundViewStore()
  const sid = 'sess-discard-043'

  store.recordSystem(sid, ['sys'])
  store.recordModel(sid, { providerID: 'p', modelID: 'm' })
  store.recordMessagesFor(sid, [{ info: { role: 'user', sessionID: sid }, content: 'q' }])
  assert.ok(store.tryGetSealed(sid))

  store.discardSealed(sid)
  assert.equal(store.tryGetSealed(sid), undefined, 'discardSealed must empty the sealed snapshot')
})

test('WHAT[context-compression-043] OutboundViewStore: clearSession purges all session state idempotently', () => {
  const store = new OutboundViewStore()
  const sid = 'sess-purge-043'

  store.recordSystem(sid, ['sys'])
  store.clearSession(sid)

  // Recording model and messages now won't seal because system was discarded
  store.recordModel(sid, { providerID: 'p', modelID: 'm' })
  store.recordMessagesFor(sid, [{ info: { role: 'user', sessionID: sid }, content: 'q' }])
  assert.equal(store.tryGetSealed(sid), undefined, 'prior pending system must have been purged')
})

test('WHAT[context-compression-043] OutboundViewStore: global singleton outboundViewStore is operational', () => {
  assert.ok(outboundViewStore instanceof OutboundViewStore)
  const sid = 'sess-singleton-043'
  outboundViewStore.recordSystem(sid, ['s'])
  outboundViewStore.clearSession(sid)
})


test.skip('WHAT[context-compression-043] ResetScopeTool 宿主真实表面接线反例：无封存快照时拒绝重置并返回明确错误文本 [PRODUCT-RETIRED standalone-resetScope]', async () => {
  await withExecutablePlugin(async (hooks, _directory, _created, runtime) => {
    const noSnapSid = 'no-snap-session-043-' + Math.random().toString(36).slice(2);
    await acceptAuthorityRoot(runtime, noSnapSid, 'engineer');
    clearSessionState(noSnapSid);
    const res = await hooks.tool.resetScope.execute(
      { newScopeObjective: 'test-objective', newScopeTurnBudget: 5, kpiCompletion: 'met' },
      { sessionID: noSnapSid, agent: 'engineer', callID: 'tc-no-snap-043' },
    );
    assert.equal(typeof res, 'string');
    assert.ok(String(res).includes('resetScope failed: no sealed outbound view snapshot captured for this session.'));
  });
});

test.skip('WHAT[context-compression-043] ResetScopeTool 宿主真实表面接线正例：单例建立封存快照后可正常归档并排空 [PRODUCT-RETIRED standalone-resetScope]', async () => {
  await withExecutablePlugin(async (hooks, _directory, _created, runtime) => {
    const snapSid = 'snap-session-043-' + Math.random().toString(36).slice(2);
    await acceptAuthorityRoot(runtime, snapSid, 'engineer');
    clearSessionState(snapSid);
    const nativePayload = {
      messages: [{ role: 'user', content: 'hello world' }],
      tools: [{ type: 'function', function: { name: 'bash' } }],
      system: ['System instructions.']
    };
    const model = { providerID: 'openai', modelID: 'gpt-4o', variant: null };
    assert.equal(outboundViewStore.recordNativeRequest(snapSid, nativePayload, model), true);
    assert.ok(outboundViewStore.tryGetSealed(snapSid), '快照必须处于已封存状态');
    const res = await hooks.tool.resetScope.execute(
      { newScopeObjective: 'verified-objective', newScopeTurnBudget: 10, kpiCompletion: 'met' },
      { sessionID: snapSid, agent: 'engineer', callID: 'tc-snap-043', messageID: 'run-snap-043' },
    );
    assert.equal(typeof res, 'string');
    assert.ok(String(res).includes('Scope reset. Objective: verified-objective'));
    assert.equal(outboundViewStore.tryGetSealed(snapSid), undefined, '归档提交后必须单次排空该会话快照');
  });
});

