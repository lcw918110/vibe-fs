import assert from 'node:assert/strict'
import test from 'node:test'
import * as routing from '../../../dist/OpenCode/Host/ModelRoutingSurface.js'
import { withExecutablePlugin } from '../../verification-system/tests/support/plugin-fixture.mjs'

// This exercises low-level SDK encoding; internal synthetic dispatch must still omit the model.
test('WHAT[execution-model-routing-009] SDK target encoding separates model identity from reasoning variant', async () => {
  let payload
  const port = routing.createSdkClientPort({ session: { promptAsync: async (value) => {
    payload = value
    return {}
  } } })
  await routing.sendPrompt(port, 'session', 'hello', {
    agent: 'engineer',
    model: { providerID: 'provider', modelID: 'model', variant: 'high' },
  })
  assert.deepEqual(payload.body.model, { providerID: 'provider', modelID: 'model' })
  assert.equal(payload.body.variant, 'high')
  assert.equal(payload.variant, 'high')
  assert.deepEqual(payload.model, { providerID: 'provider', modelID: 'model' })
})

test('WHAT[execution-model-routing-009] actual chat.message preserves the host-selected model', async () => {
  await withExecutablePlugin(async (hooks) => {
    const output = { message: {
      id: 'msg-output-session', role: 'user', sessionID: 'ses-output-session', agent: 'engineer',
      model: { providerID: 'host', modelID: 'placeholder' },
    }, parts: [] }
    await hooks['chat.message']({ messageID: 'msg-output-session' }, output)
    assert.deepEqual({ ...output.message.model }, { providerID: 'host', modelID: 'placeholder' })
  })
})


test('WHAT[execution-model-routing-009] chat.params cannot create missing admission and internal sends stay model-free', async () => {
  const { withExecutablePlugin, bindManagedChild } = await import('../../verification-system/tests/support/plugin-fixture.mjs')
  const chatParams = await import('../../../dist/OpenCode/Host/ChatParamsSurface.js')
  const dispatch = await import('../../../dist/Interaction/Dispatch/DispatchSurface.js')

  await withExecutablePlugin(async (hooks, _directory, _createdIds, runtime) => {
    const sessionID = 'ses_emr009_unadmitted'
    const messageID = 'msg-emr009-unadmitted'
    await bindManagedChild(runtime, 'ses_emr009_parent', sessionID, 'engineer')

    // Durable acceptance without a committed model lease: chat.params must
    // fail closed rather than invent the missing admission (WHAT 009).
    const accepted = await dispatch.acceptManagedExternal(runtime.journal, sessionID, messageID, 'engineer')
    assert.equal(accepted.ok, true, JSON.stringify(accepted))
    const output = { model: { providerID: 'anthropic', modelID: 'fast-haiku' } }
    const rejected = chatParams.applyWith(
      runtime.journal,
      { sessionID, messageID, agent: 'engineer', model: { providerID: 'anthropic', id: 'fast-haiku' } },
      output,
    )
    assert.equal(rejected.ok, false, 'chat.params must not create a missing admission')
    assert.match(rejected.error, /no committed execution lease/)
    assert.equal(output.model.modelID, 'fast-haiku', 'the caller model is untouched')

    // Internal synthetic dispatch stays model-free on the same runtime.
    const owner = await dispatch.acceptHumanRootSelection(
      runtime.journal,
      'ses_emr009_owner',
      'msg-emr009-owner',
      {
        kind: 'RootSelection',
        ownerSession: null,
        ownerLogicalRun: null,
        ownerAuthorityRoot: null,
        participantIdentity: {
          participant: 'manager', role: 'manager', persona: 'Lead',
          personaCatalogVersion: 1, origin: 'ResolvedAtRoot',
        },
      },
    )
    assert.equal(owner.ok, true, owner.error)
    const seed = (await import('../../../dist/Interaction/Authority/RuntimeSurface.js')).issueInheritedIdentitySeed('engineer', owner.profile)
    assert.equal(seed.ok, true, seed.error)
    const seen = []
    const port = {
      SubscribeTerminal: () => ({ Dispose: () => {} }),
      SendPrompt: async (session, text, options) => {
        seen.push({ session, text, model: options?.model ?? null })
        return dispatch.admittedWithReceipt('receipt-emr009')
      },
    }
    const sent = await dispatch.sendAgentOwnerRoot(port, runtime.journal, 'ses_emr009_child', 'internal work', seed.value)
    assert.equal(sent.ok, true, sent.error)
    assert.equal(seen.length, 1)
    assert.equal(seen[0].model, null, 'internal synthetic send stays model-free')
  })
})

test('WHAT[execution-model-routing-009] host-selected models remain unchanged through chat.params', async () => {
  const { default: fc } = await import('fast-check')
  const chatParams = await import('../../../dist/OpenCode/Host/ChatParamsSurface.js')

  const arbitraryTarget = fc.record({
    model: fc.constantFrom('provider/alpha', 'provider/beta', 'provider/gamma'),
    reasoning: fc.constantFrom('none', 'high'),
  })

  await withExecutablePlugin(async (hooks, _directory, _created, runtime) => {
    let ordinal = 0
    await fc.assert(
      fc.asyncProperty(
        arbitraryTarget,
        async target => {
          const suffix = (++ordinal).toString(36)
          const sessionID = 'ses-emr009-' + suffix
          const messageID = 'msg-emr009-' + suffix
          {
            const [providerID, ...modelParts] = target.model.split('/')
            const modelID = modelParts.join('/')
            const admitted = {
              message: {
                id: messageID, role: 'user', sessionID, agent: 'engineer',
                model: { providerID, modelID, variant: target.reasoning },
              },
              parts: [],
            }
            await hooks['chat.message']({ sessionID, agent: 'engineer', messageID }, admitted)
            const projected = admitted.message.model
            assert.equal(projected.providerID, providerID)
            assert.equal(projected.modelID, modelID)
            assert.equal(projected.variant, target.reasoning)

            const output = {}
            const observed = chatParams.apply(
              {
                sessionID, messageID, agent: 'engineer',
                model: { providerID: projected.providerID, id: projected.modelID, capabilities: {} },
                message: { id: messageID, model: { providerID: projected.providerID, modelID: projected.modelID, variant: projected.variant } },
              },
              output,
            )
            assert.equal(observed.ok, true, observed.error)
            assert.equal(observed.temperature, 1)
            assert.equal(output.temperature, 1)
            assert.equal(output.model, undefined, 'the host model object is not rewritten')
          }
        },
      ),
      { seed: 0x44524946, numRuns: 100 },
    )
  })
})

test('WHAT[execution-model-routing-009] BusyAgentNudge cannot steer-preempt a running child after resetScope', async () => {
  const submitted=[]
  const client={session:{promptAsync:async request=>{submitted.push(request);return {}}}}
  const port=routing.createSdkClientPort(client)
  const busy={
    wanxiangshu_origin:'BusyAgentNudge',
    wanxiangshu_prompt_key:'pk-busy-queued',
  }
  const consultation={
    ...busy,wanxiangshu_consultation_steer:true,
    wanxiangshu_prompt_key:'pk-consultation-steer',
  }
  const retry={
    wanxiangshu_origin:'ProviderRetryAttempt',
    wanxiangshu_prompt_key:'pk-provider-recovery',
  }
  for(const metadata of [busy,consultation,retry]){
    const result=await routing.sendPrompt(port,'ses-engineer-reset','Continue work.',{
      agent:'engineer',metadata,
    })
    assert.equal(result.kind,'AdmittedWithReceipt')
  }
  assert.equal(submitted.length,3)
  assert.equal(submitted[0].body.delivery,'queue',
    'BusyAgentNudge is a next-input message: it must not interrupt the current provider step')
  assert.equal(submitted[0].delivery,'queue',
    'SDK top-level delivery must agree with body in both payload protocol versions')
  assert.equal(submitted[1].body.delivery??'steer','steer',
    'the genuine consultation steer retains the established tool-free latest assistant rule')
  assert.equal(submitted[2].body.delivery??'steer','steer',
    'confirmed provider retry must retain immediate continuation semantics')
  assert.equal(submitted[0].body.parts[0].metadata.wanxiangshu_origin,'BusyAgentNudge')
  assert.equal(submitted[0].body.parts[0].metadata.wanxiangshu_prompt_key,'pk-busy-queued')
})
