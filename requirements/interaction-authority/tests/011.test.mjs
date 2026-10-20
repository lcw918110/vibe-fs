import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const authority = await import("../../../dist/Interaction/Authority/RuntimeSurface.js");

const hash = (value) => `H(${value})`
const personas = {
  engineer: 'Engineer',
  coder: 'Coder',
  manager: 'Lead',
  reviewer: 'Auditor',
  inspector: 'Investigator',
  devops: 'Operator',
}
const rootSelection = (agent) => {
  return {
    kind: 'RootSelection',
    ownerSession: null,
    ownerLogicalRun: null,
    ownerAuthorityRoot: null,
    participantIdentity: {
      participant: agent,
      role: agent,
      selectedTier: 'deep',
      persona: personas[agent] ?? 'Unknown',
      personaCatalogVersion: 1,
      origin: 'ResolvedAtRoot',
    },
  }
}
const rootFor = (agent = 'engineer', physical = 'msg_u1') => {
  const result = authority.createAuthorityRoot(hash, 'rt_1', 'ses_a', 'HumanRoot', physical, rootSelection(agent))
  assert.equal(result.ok, true, result.error)
  return result.value
}
const profile = (value) => ({
  session: value.session,
  logicalRun: value.logicalRun,
  authorityRoot: value.authorityRoot,
  authorityKind: value.authorityKind,
  participant: value.participantIdentity.participant,
  role: value.participantIdentity.role,
})
const register = (root) => authority.registerAuthority(root, authority.empty)
const continuation = (key, root, kind = 'ManagerGuard', payload = 'payload') =>
  authority.claimContinuation(key, 'ses_a', kind, root, payload)

test('WHAT[interaction-authority-011] PROMPT_011_logical_run_id_is_stable_and_input_sensitive', () => {
  const id = (runtime, session, physical) => authority.stableLogicalRunId(hash, runtime, session, physical)
  const base = id('rt_1', 'ses_a', 'msg_u1')
  assert.equal(base, 'H(rt_1\nses_a\nmsg_u1)')
  assert.equal(id('rt_1', 'ses_a', 'msg_u1'), base)
  assert.notEqual(id('rt_2', 'ses_a', 'msg_u1'), base)
  assert.notEqual(id('rt_1', 'ses_b', 'msg_u1'), base)
  assert.notEqual(id('rt_1', 'ses_a', 'msg_u2'), base)
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const chatParams = await import("../../../dist/OpenCode/Host/ChatParamsSurface.js");
const { default: plugin } = await import("../../../dist/OpenCode/Plugin/Plugin.js");
const { execFileSync } = await import("node:child_process");
const { mkdtempSync, mkdirSync, rmSync, writeFileSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join } = await import("node:path");

// P2a: chat.params validates the exact committed ModelRouting lease, so a
// passing observation needs a real chat.message admission first. The routing
// environment is process-local (temp HOME + wanxiangshu.mjs); the committed
// lease lives on the process-shared runtime the hook reads.
const createChatParamsEnvironment = () => {
  const root = mkdtempSync(join(tmpdir(), 'wxs-chat-params-'))
  const home = join(root, 'home')
  const routingDir = join(home, '.config', 'opencode')
  mkdirSync(routingDir, { recursive: true })
  writeFileSync(
    join(routingDir, 'wanxiangshu.mjs'),
    'export const routingProtocol = 2\n' +
      'export default function route(role, running) {\n' +
      "  if (role === 'engineer') {\n" +
      "    const occupied = running.filter((item) => item.model === 'provider/model-a' && item.reasoning === 'none').length\n" +
      '    return occupied === 0\n' +
      "      ? { model: 'provider/model-a', reasoning: 'none' }\n" +
      "      : { model: 'provider/model-b', reasoning: 'none' }\n" +
      '  }\n' +
      "  throw new Error('unexpected role: ' + role)\n" +
      '}\n',
    'utf8',
  )
  return {
    home,
    createPlugin: async (name) => {
      const directory = join(root, name)
      mkdirSync(directory, { recursive: true })
      execFileSync('git', ['init', '--quiet', directory])
      return plugin.server({
        directory,
        client: {},
        events: { listen: () => () => {} },
      })
    },
    dispose: () => rmSync(root, { recursive: true, force: true }),
  }
}

const managedAgentConfig = () => {
  const agent = {}
  for (const role of ['orchestrator', 'manager', 'engineer', 'devops', 'bookkeeper']) {
    agent[role] = {}
  }
  return { agent }
}

// Admit one physical execution through the real chat.message hook; returns
// the projected message model (the committed lease target).
const admitExecution = async (hooks, sessionID, messageID) => {
  const admitted = {
    message: {
      id: messageID,
      role: 'user',
      sessionID,
      agent: 'engineer',
      model: { providerID: 'host', modelID: 'placeholder' },
    },
    parts: [],
  }
  await hooks['chat.message']({ sessionID, agent: 'engineer', messageID }, admitted)
  return admitted.message.model
}


test('WHAT[interaction-authority-011] CHAT_PARAMS_managed_provider_run_without_committed_lease_fails_closed', async () => {
  const { withExecutablePlugin, bindManagedChild } = await import('../../verification-system/tests/support/plugin-fixture.mjs')
  const dispatch = await import('../../../dist/Interaction/Dispatch/DispatchSurface.js')

  await withExecutablePlugin(async (hooks, _directory, _createdIds, runtime) => {
    const sessionID = 'ses_chat_params_child'
    const messageID = 'msg-chat-params-unleased'
    await bindManagedChild(runtime, 'ses_chat_params_parent', sessionID, 'engineer')

    // Durable acceptance alone: the Dispatcher writes Accepted evidence with no
    // model capacity, so no committed lease exists for this exact key.
    const accepted = await dispatch.acceptManagedExternal(runtime.journal, sessionID, messageID, 'engineer')
    assert.equal(accepted.ok, true, JSON.stringify(accepted))

    const output = { model: { providerID: 'anthropic', modelID: 'fast-haiku' } }
    const rejected = chatParams.applyWith(
      runtime.journal,
      {
        sessionID,
        messageID,
        agent: 'engineer',
        model: { providerID: 'anthropic', id: 'fast-haiku' },
      },
      output,
    )
    assert.equal(rejected.ok, false)
    assert.match(rejected.error, /no committed execution lease for physical user message 'msg-chat-params-unleased'/)
    assert.equal(output.model.modelID, 'fast-haiku')
  })
})
test('WHAT[interaction-authority-011] CHAT_PARAMS_an_unaccepted_message_does_not_claim_managed_execution', () => {
  const output = { model: { providerID: 'anthropic', modelID: 'fast-haiku' } }
  const observed = chatParams.apply(
    { sessionID: 'ses_chat_params_title', agent: 'coder', model: { providerID: 'anthropic', modelID: 'fast-haiku' } },
    output,
  )

  assert.equal(observed.ok, true, observed.error)
  assert.equal(observed.temperature, undefined)
  assert.equal(output.model.modelID, 'fast-haiku')
})
test('WHAT[interaction-authority-011] CHAT_PARAMS_admitted_lease_validates_without_rewriting_host_model', async () => {
  const environment = createChatParamsEnvironment()
  const previousHome = process.env.HOME
  process.env.HOME = environment.home
  let hooks
  try {
    hooks = await environment.createPlugin('chat-params-admitted')
    await hooks.config(managedAgentConfig())
    const sessionID = 'ses_chat_params_admitted'
    const messageID = 'msg-chat-params-admitted'
    const leaseModel = await admitExecution(hooks, sessionID, messageID)

    const output = { model: { providerID: leaseModel.providerID, modelID: leaseModel.modelID } }
    const observed = chatParams.apply(
      {
        sessionID,
        messageID,
        agent: 'engineer',
        model: { providerID: leaseModel.providerID, id: leaseModel.modelID },
        message: { id: messageID, model: { providerID: leaseModel.providerID, modelID: leaseModel.modelID, variant: leaseModel.variant } },
      },
      output,
    )
    assert.equal(observed.ok, true, observed.error)
    assert.equal(observed.modelID, leaseModel.modelID)
    assert.equal(observed.temperature, 1)
    assert.equal(output.model.modelID, leaseModel.modelID)
  } finally {
    if (hooks) await hooks.dispose()
    process.env.HOME = previousHome
    environment.dispose()
  }
})
test('WHAT[interaction-authority-011] CHAT_PARAMS_uses_the_resolved_provider_model_id_not_the_mutated_user_message_model', async () => {
  const environment = createChatParamsEnvironment()
  const previousHome = process.env.HOME
  process.env.HOME = environment.home
  let hooks
  try {
    hooks = await environment.createPlugin('chat-params-resolved-model')
    await hooks.config(managedAgentConfig())
    const sessionID = 'ses_chat_params_resolved'
    const messageID = 'msg-chat-params-resolved'
    const leaseModel = await admitExecution(hooks, sessionID, messageID)

    // The resolved catalog model differs from the committed lease target.
    // Runtime observation is read-only: drift is a test-time fast-check
    // property, so this observation passes and only projects temperature.
    const output = {}
    const observed = chatParams.apply(
      {
        sessionID,
        messageID,
        agent: 'engineer',
        model: { id: 'model-drifted', providerID: leaseModel.providerID },
        message: { model: { providerID: leaseModel.providerID, modelID: leaseModel.modelID, variant: leaseModel.variant } },
      },
      output,
    )
    assert.equal(observed.ok, true, observed.error)
    assert.equal(observed.temperature, 1)
    assert.equal(output.temperature, 1)
  } finally {
    if (hooks) await hooks.dispose()
    process.env.HOME = previousHome
    environment.dispose()
  }
})
test('WHAT[interaction-authority-011] CHAT_PARAMS_accepts_the_real_provider_model_shape_with_message_variant', async () => {
  const environment = createChatParamsEnvironment()
  const previousHome = process.env.HOME
  process.env.HOME = environment.home
  let hooks
  try {
    hooks = await environment.createPlugin('chat-params-real-shape')
    await hooks.config(managedAgentConfig())
    const sessionID = 'ses_chat_params_real_shape'
    const messageID = 'msg-chat-params-real-shape'
    const leaseModel = await admitExecution(hooks, sessionID, messageID)

    const inputModel = {
      id: leaseModel.modelID,
      providerID: leaseModel.providerID,
      capabilities: { temperature: true },
      variants: { none: {}, high: { reasoning: { effort: 'high' } } },
      options: {},
    }
    const output = { options: { existing: 'sentinel' } }

    const observed = chatParams.apply(
      {
        sessionID,
        messageID,
        agent: 'engineer',
        model: inputModel,
        message: { id: messageID, model: { providerID: leaseModel.providerID, modelID: leaseModel.modelID, variant: leaseModel.variant } },
      },
      output,
    )

    assert.equal(observed.ok, true, observed.error)
    assert.equal(observed.temperature, 1)
    assert.equal(output.temperature, 1)
    assert.equal(output.options.temperature, 1)
    assert.equal(output.options.existing, 'sentinel')
    assert.equal(inputModel.variants.none.temperature, 1)
    assert.equal(inputModel.variants.high.temperature, 1)
    assert.equal(inputModel.options.temperature, 1)
  } finally {
    if (hooks) await hooks.dispose()
    process.env.HOME = previousHome
    environment.dispose()
  }
})
test('WHAT[interaction-authority-011] CHAT_PARAMS_leaves_temperature_untouched_when_model_capability_disables_it', async () => {
  const environment = createChatParamsEnvironment()
  const previousHome = process.env.HOME
  process.env.HOME = environment.home
  let hooks
  try {
    hooks = await environment.createPlugin('chat-params-temperature-off')
    await hooks.config(managedAgentConfig())
    const sessionID = 'ses_chat_params_temperature_off'
    const messageID = 'msg-chat-params-temperature-off'
    const leaseModel = await admitExecution(hooks, sessionID, messageID)

    const inputModel = {
      id: leaseModel.modelID,
      providerID: leaseModel.providerID,
      capabilities: { temperature: false },
      variants: { none: {} },
    }
    const output = { options: {} }
    const observed = chatParams.apply(
      {
        sessionID,
        messageID,
        agent: 'engineer',
        model: inputModel,
        message: { id: messageID, model: { providerID: leaseModel.providerID, modelID: leaseModel.modelID, variant: leaseModel.variant } },
      },
      output,
    )

    assert.equal(observed.ok, true, observed.error)
    assert.equal(observed.temperature, undefined)
    assert.equal(output.temperature, undefined)
    assert.equal(output.options.temperature, undefined)
    assert.equal(inputModel.variants.none.temperature, undefined)
  } finally {
    if (hooks) await hooks.dispose()
    process.env.HOME = previousHome
    environment.dispose()
  }
})
test('WHAT[interaction-authority-011] CHAT_PARAMS_agentless_root_does_not_invent_binding', () => {
  const output = { model: { providerID: 'anthropic', modelID: 'fast-haiku' } }
  const observed = chatParams.apply({ sessionID: 'ses_unbound_root' }, output)
  assert.equal(observed.ok, true)
  assert.equal(observed.temperature, undefined)
  assert.equal(output.model.modelID, 'fast-haiku')
})

test('WHAT[interaction-authority-011] accepted root identity and its projection agree field by field', async () => {
  const authority = await import('../../../dist/Interaction/Authority/RuntimeSurface.js')
  const dispatch = await import('../../../dist/Interaction/Dispatch/DispatchSurface.js')
  const { withJournal, acceptOwner, hostPort } = await import('./support/authority.mjs')
  await withJournal('ia011-atomic-profile', async (handle) => {
    const owner = await acceptOwner(handle)
    const seed = authority.issueInheritedIdentitySeed('engineer', owner)
    assert.equal(seed.ok, true, seed.error)
    const sent = await dispatch.sendAgentOwnerRootAwait(
      hostPort(async () => dispatch.admittedWithReceipt('msg-ia011-physical')),
      handle, 'child-ia011', 'a bounded assignment', seed.value,
    )
    assert.equal(sent.ok, true, sent.error)
    const accepted = await dispatch.acceptAgentOwnerRoot(handle, 'child-ia011', sent.key, 'actual-ia011-physical')
    assert.equal(accepted.ok, true, accepted.error)

    // The attempt profile is one atomic record: every identity field the
    // Authority fold exposes comes from the accepted root, never assembled
    // from session caches or scattered messages (WHAT 011).
    const profile = accepted.profile
    for (const field of ['session', 'logicalRun', 'authorityRoot', 'authorityKind']) {
      assert.ok(profile[field], 'profile.' + field + ' must be present and non-empty')
    }
    assert.equal(profile.session, 'child-ia011')
    assert.equal(profile.authorityRoot, 'actual-ia011-physical')
    assert.equal(profile.authorityKind, 'AgentOwnerRoot')
    assert.deepEqual(profile.identitySeed, seed.value)
    assert.ok(profile.participantIdentity)
    assert.equal(profile.participantIdentity.participant, 'engineer')

    // The projection serves the same record: no field-level drift between
    // the acceptance and the durable observation (same-process only).
    const observed = dispatch.projectionObservation(handle, 'child-ia011').activeLogicalRun
    assert.deepEqual(observed, profile)
  })
})
}



test.todo('WHAT[interaction-authority-011] per-physical target/lease is carried atomically in the attempt profile and survives restart (GAP-122: lease atomicity and restart replay pending — the identity-agreement test above covers the same-process projection only)')
