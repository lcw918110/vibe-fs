import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { spawnSync } = await import("node:child_process");
const { fileURLToPath } = await import("node:url");
const { default: test } = await import("node:test");

const scenario = fileURLToPath(
  new URL('../../participant-identity/tests/support/session-reuse-plugin-scenario.mjs', import.meta.url),
)

test('WHAT[managed-session-lifecycle-020] public plugin rejects an active identity conflict and retains the new identity after Manager life completion and journal reopen', () => {
  const child = spawnSync(process.execPath, [scenario], {
    cwd: process.cwd(),
    encoding: 'utf8',
    env: {
      ...process.env,
      WANXIANGSHU_NO_FATAL_EXIT: '1',
      WANXIANGSHU_PROVIDER_LANGUAGE: 'en',
    },
  })

  assert.equal(child.error, undefined, child.error?.message)
  assert.equal(child.status, 0, `subprocess exited ${child.status}\nstdout:\n${child.stdout}\nstderr:\n${child.stderr}`)
  assert.doesNotMatch(child.stderr, /AGENT-028|\bfatal\b|plugin-hook-[^\n]*-failed/i)

  const lines = child.stdout.trim().split('\n').filter(Boolean)
  assert.equal(lines.length, 1, `expected one structured event line:\n${child.stdout}`)
  const trace = JSON.parse(lines[0])
  assert.equal(trace.ok, true)

  const replacement = trace.events.find((event) => event.case === 'same-session-role-replacement')
  assert.ok(replacement)
  assert.equal(replacement.conflictKind, 'ActiveRunIdentityConflict')
  assert.equal(replacement.providerOrRetryExecutionsBeforeClose, 0)
  assert.equal(replacement.duplicateLeadLogicalRun, replacement.leadLogicalRun)
  assert.notEqual(replacement.operatorLogicalRun, replacement.leadLogicalRun)

  const restart = trace.events.find((event) => event.case === 'rolling-restart-durable-identity')
  assert.ok(restart)
  assert.equal(restart.activeLogicalRun, replacement.operatorLogicalRun)
  assert.equal(restart.duplicateLogicalRun, replacement.operatorLogicalRun)
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { default: fc } = await import("fast-check");
const authority = await import("../../../dist/Interaction/Authority/RuntimeSurface.js");

const hash = (value) => `H(${value})`
const childSession = 'ses-reusable-subagent-child'
const ownerSession = 'ses-manager-owner'
const ownerPhysical = 'msg-manager-root'
const propertyOptions = { seed: 0x53554241, numRuns: 200 }
const subagentAgents = [
  'engineer',
  'devops',
]
const arbitrarySubagentAgent = fc.constantFrom(...subagentAgents)
const arbitraryToken = fc
  .array(fc.constantFrom(...'abcdefghijklmnopqrstuvwxyz0123456789'), { minLength: 2, maxLength: 8 })
  .map((chars) => chars.join(''))
const managerSeed = {
  kind: 'RootSelection',
  ownerSession: null,
  ownerLogicalRun: null,
  ownerAuthorityRoot: null,
  participantIdentity: {
    selectedAgent: 'manager',
    peerAgent: 'manager',
    canonicalRole: 'manager',
    selectedTier: 'deep',
    persona: 'Lead',
    personaCatalogVersion: 1,
    origin: 'ResolvedAtRoot',
  },
}
const ownerProfile = (() => {
  const result = authority.createAuthorityRoot(
    hash,
    'runtime-subagent-reuse',
    ownerSession,
    'HumanRoot',
    ownerPhysical,
    managerSeed,
  )
  assert.equal(result.ok, true, result.ok ? '' : result.error)
  return result.value
})()
const createSubagentRoot = (physicalId, agentName) => {
  const seedResult = authority.issueInheritedIdentitySeed(agentName, ownerProfile)
  assert.equal(seedResult.ok, true, seedResult.ok ? '' : seedResult.error)
  const result = authority.createAuthorityRoot(
    hash,
    'runtime-subagent-reuse',
    childSession,
    'AgentOwnerRoot',
    physicalId,
    seedResult.value,
  )
  assert.equal(result.ok, true, result.ok ? '' : result.error)
  return result.value
}
const runTurnArbitrary = fc.record({
  agentName: arbitrarySubagentAgent,
  token: arbitraryToken,
})

test('WHAT[managed-session-lifecycle-020] pure authority projection rejects premature roots and permits them after matching child-work closure', () => {
  fc.assert(
    fc.property(
      fc.array(runTurnArbitrary, { minLength: 2, maxLength: 8 }),
      (turns) => {
        let state = authority.empty

        for (let i = 0; i < turns.length; i += 1) {
          const turn = turns[i]
          const physicalId = `msg-subagent-${i}-${turn.token}`
          const profile = createSubagentRoot(physicalId, turn.agentName)

          // 1. Fresh run registers successfully (returns updated state with activeLogicalRun)
          state = authority.registerAuthority(profile, state)
          assert.notEqual(state.ok, false, `turn ${i} registration must succeed`)
          assert.equal(state.activeLogicalRun.logicalRun, profile.logicalRun)
          assert.equal(state.activeLogicalRun.authorityRoot, profile.authorityRoot)
          assert.equal(state.activeLogicalRun.authorityKind, 'AgentOwnerRoot')

          // 2. While active, any premature next run MUST be rejected with ActiveRunIdentityConflict
          if (i + 1 < turns.length) {
            const nextTurn = turns[i + 1]
            const nextPhysical = `msg-premature-${i + 1}-${nextTurn.token}`
            const prematureProfile = createSubagentRoot(nextPhysical, nextTurn.agentName)
            const premature = authority.registerAuthority(prematureProfile, state)
            assert.equal(premature.ok, false, 'unclosed prior run must reject concurrent root')
            assert.equal(premature.error.kind, 'ActiveRunIdentityConflict')
            assert.equal(premature.error.active.logicalRun, profile.logicalRun)
            assert.equal(premature.error.requested.logicalRun, prematureProfile.logicalRun)
          }

          // 3. Mismatched authority root close fails closed
          const bogusClose = authority.closeAuthority(profile.logicalRun, 'msg-bogus-root', state)
          assert.equal(bogusClose.ok, false)
          assert.match(bogusClose.error, /logical-run close mismatch/)

          // Pure closure transition; this does not append a durable closure fact.
          state = authority.closeCompletedAgentOwnerChildWork(profile.logicalRun, profile.authorityRoot, state)
          assert.equal(state.activeLogicalRun, null, `turn ${i} child-work completion must clear activeLogicalRun`)
          assert.equal(state.lastAuthorityProfile.logicalRun, profile.logicalRun)
          assert.equal(state.lastAuthorityProfile.authorityRoot, profile.authorityRoot)
        }
      },
    ),
    propertyOptions,
  )
})
test('WHAT[managed-session-lifecycle-020] pure child closure permits a new profile and retains the last profile', () => {
  fc.assert(
    fc.property(
      runTurnArbitrary,
      runTurnArbitrary,
      (turnA, turnB) => {
        let state = authority.empty
        const profileA = createSubagentRoot(`msg-run-a-${turnA.token}`, turnA.agentName)
        const profileB = createSubagentRoot(`msg-run-b-${turnB.token}`, turnB.agentName)

        // Register run A
        state = authority.registerAuthority(profileA, state)
        assert.notEqual(state.ok, false)
        assert.equal(state.activeLogicalRun.logicalRun, profileA.logicalRun)

        // Apply the pure child-work closure transition.
        const closedState = authority.closeCompletedAgentOwnerChildWork(profileA.logicalRun, profileA.authorityRoot, state)
        assert.equal(closedState.activeLogicalRun, null)
        assert.equal(closedState.lastAuthorityProfile.logicalRun, profileA.logicalRun)
        assert.equal(closedState.pendingClaims.length, 0)

        // Register run B on the same child session
        const nextState = authority.registerAuthority(profileB, closedState)
        assert.notEqual(nextState.ok, false)
        assert.equal(nextState.activeLogicalRun.logicalRun, profileB.logicalRun)
        assert.notEqual(nextState.activeLogicalRun.logicalRun, profileA.logicalRun)
        assert.notEqual(nextState.activeLogicalRun.authorityRoot, profileA.authorityRoot)

        // Closing run B also succeeds
        const finalState = authority.closeCompletedAgentOwnerChildWork(profileB.logicalRun, profileB.authorityRoot, nextState)
        assert.equal(finalState.activeLogicalRun, null)
        assert.equal(finalState.lastAuthorityProfile.logicalRun, profileB.logicalRun)
      },
    ),
    propertyOptions,
  )
})
test('WHAT[managed-session-lifecycle-020] Manager AgentOwnerRoot remains active for owner-directed post-life recovery', () => {
  const profile = createSubagentRoot('msg-manager-child-work', 'manager')
  const active = authority.registerAuthority(profile, authority.empty)
  assert.notEqual(active.ok, false)

  const refused = authority.closeCompletedAgentOwnerChildWork(
    profile.logicalRun,
    profile.authorityRoot,
    active,
  )
  assert.equal(refused.ok, false)
  assert.match(refused.error, /non-Manager AgentOwnerRoot/)
  assert.equal(active.activeLogicalRun.logicalRun, profile.logicalRun)
})
test('WHAT[managed-session-lifecycle-020] registering a second root without closure returns ActiveRunIdentityConflict', () => {
  const property = fc.property(
    runTurnArbitrary,
    runTurnArbitrary,
    (turnA, turnB) => {
      let state = authority.empty
      const profileA = createSubagentRoot(`msg-a-${turnA.token}`, turnA.agentName)
      const profileB = createSubagentRoot(`msg-b-${turnB.token}`, turnB.agentName)

      // Register run A
      state = authority.registerAuthority(profileA, state)
      assert.notEqual(state.ok, false)

      // A second root is rejected while the first is active.
      const regB = authority.registerAuthority(profileB, state)
      // Assert that omitting closure correctly produces ActiveRunIdentityConflict
      assert.equal(regB.ok, false)
      assert.equal(regB.error.kind, 'ActiveRunIdentityConflict')
    },
  )

  fc.assert(property, { seed: 0x53554242, numRuns: 100 })
})
}

test.todo('WHAT[managed-session-lifecycle-020] each authority kind requires matching durable exact prior-run closure before fresh identity installation (GAP-123)')
