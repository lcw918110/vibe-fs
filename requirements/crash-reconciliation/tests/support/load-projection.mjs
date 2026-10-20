import assert from 'node:assert/strict'
import * as codec from '../../../../dist/Persistence/Journal/FactCodecSurface.js'
import * as recovery from '../../../../dist/OpenCode/Host/LoadRecoverySurface.js'
import * as identity from '../../../../dist/Participant/Persona/Surface.js'
import * as authority from '../../../../dist/Interaction/Authority/RuntimeSurface.js'

export { recovery }

export const fold = (state, fact) => {
  const line = typeof fact === 'string' ? fact : codec.encode(fact)
  const result = recovery.foldCanonical(state, line)
  assert.equal(result.ok, true, result.error)
}

export const link = (state, { parent = 'parent', child = 'child', handle = 'work', agent = 'engineer', byname = 'review-change', ownership = 'DurableParentHandle' } = {}) => fold(state, {
  family: 'Execution', case: 'HandleLinked', payload: {
    ParentSessionId: parent, ChildSessionId: child, Handle: handle,
    TargetAgent: agent, Byname: byname, CanonicalRole: agent, Ownership: ownership,
  },
})

const rootFact = profile => ({
  family: 'Prompt', case: 'AuthorityRootAccepted', payload: {
    SchemaVersion: 2, SessionId: profile.session, LogicalRunId: profile.logicalRun,
    AuthorityRootUserMessageId: profile.authorityRoot, AuthorityKind: profile.authorityKind,
    IdentitySeed: profile.identitySeed,
  },
})

const createRoot = (session, kind, seed) => {
  const result = authority.createAuthorityRoot(value => `load-fixture:${value}`, 'load-fixture-runtime', session, kind, `root-${session}`, seed)
  assert.equal(result.ok, true, result.error)
  return result.value
}

const humanProfile = (session, agent) => {
  const resolved = identity.resolveParticipantIdentityAtRoot(agent)
  assert.equal(resolved.ok, true, resolved.error)
  const value = resolved.identity
  return createRoot(session, 'HumanRoot', {
    kind: 'RootSelection', ownerSession: null, ownerLogicalRun: null, ownerAuthorityRoot: null,
    participantIdentity: {
      participant: value.name, role: value.role, persona: value.persona,
      personaCatalogVersion: value.catalogVersion, origin: value.origin,
    },
  })
}

export const acceptRun = (state, { child = 'child', agent = 'engineer', parent = 'parent', human = false } = {}) => {
  let profile
  if (human) {
    profile = humanProfile(child, agent)
  } else {
    const owner = humanProfile(parent, 'manager')
    fold(state, rootFact(owner))
    const issued = authority.issueInheritedIdentitySeed(agent, owner)
    assert.equal(issued.ok, true, issued.error)
    profile = createRoot(child, 'AgentOwnerRoot', issued.value)
    const accepted = JSON.parse(codec.encode(rootFact(profile)))[1][1][1]
    const promptKey = id('PromptKey', `load-${child}`)
    fold(state, canonical('Prompt', 'PluginPromptClaimed', {
      PromptKey: promptKey, SessionId: accepted.SessionId, ContinuationKind: 'AgentOwnerRoot',
      LogicalRunId: null, AuthorityRootUserMessageId: null, IdentitySeed: accepted.IdentitySeed,
      PayloadDigest: `load-payload-${child}`,
    }))
    fold(state, canonical('Prompt', 'PluginPromptPhysicalAccepted', {
      PromptKey: promptKey, SessionId: accepted.SessionId,
      PhysicalUserMessageId: id('PhysicalUserMessageId', profile.authorityRoot),
    }))
  }
  fold(state, rootFact(profile))
  return profile
}

export const terminal = (disposition, profile) => {
  const root = JSON.parse(codec.encode(rootFact(profile)))[1][1][1]
  const accepted = {
    SessionId: root.SessionId, LogicalRunId: root.LogicalRunId,
    AuthorityRootUserMessageId: root.AuthorityRootUserMessageId,
    AuthorityKind: root.AuthorityKind, IdentitySeed: root.IdentitySeed,
    PhysicalUserMessageId: ['PhysicalUserMessageId', 'message-child'],
    Origin: ['AuthorityRoot', 'AgentOwnerRoot'],
  }
  return canonical('ChatExecution', 'Terminal', {
    SchemaVersion: 1,
    Key: { SessionId: root.SessionId, PhysicalUserMessageId: accepted.PhysicalUserMessageId },
    Evidence: ['AfterProviderStart', {
      Accepted: accepted, ProviderRun: ['ProviderRunIdentity', 'provider-child'],
      RequestKind: 'WorkMain', ProjectionChoice: 'UseCommittedEpoch',
    }],
    Disposition: disposition,
  })
}

// Canonical journal JSON is a supported durable wire format, not Fable runtime object layout.
export const canonical = (family, name, payload) => JSON.stringify(['Agent', [family, [name, payload]]])
export const id = (kind, value) => [kind, value]

export const admittedFission = () => canonical('Fission', 'FissionAdmitted', {
  GroupId: 'group', OwnerSessionId: id('SessionId', 'owner'), ParentSessionId: null,
  OriginToolCallId: id('ToolCallId', 'fission-call'), LaneCount: 2,
  LaneSessions: [id('SessionId', 'lane-0'), id('SessionId', 'lane-1')],
  LanePrompts: ['left', 'right'], OwnerViewRef: id('BlobRef', 'blobs/work'),
  OwnerViewDigest: id('BlobDigest', 'digest-work'), PreFissionCompletionIds: [],
})
