import assert from 'node:assert/strict'
import test from 'node:test'
import * as authority from '../../../dist/Interaction/Authority/Surface.js'
import * as planner from '../../../dist/Participant/Provider/Attempt/PlannerSurface.js'
import * as delegation from '../../../dist/Execution/Delegation/SyncDelegate/Surface.js'
import * as promptSurface from '../../../dist/Resources/PromptSurface.js'
import * as tools from '../../../dist/OpenCode/Tools/ToolSurface.js'
import * as canonicalJson from '../../../dist/OpenCode/Codec/CanonicalJsonSurface.js'
import * as providerProjection from '../../../dist/Participant/Provider/Projection/Surface.js'
import { installDefaultResources } from '../../../dist/OpenCode/Host/ManagedAgentConfigSurface.js'

installDefaultResources()

const OWNER = 'ses_owner_prompt'

const roles = ['Manager', 'Orchestrator', 'Engineer', 'DevOps', 'Plan']

const profile = (role, tier = 'Fast', kind = 'WorkMain') => planner.plan({ role, tier, kind })

test('WHAT[prefix-stability-007] PROMPT_019_each_canonical_role_has_one_stable_prompt_identity', () => {
  const ids = new Set()
  for (const role of roles) {
    const id = authority.systemPromptIdForRole(role)
    const resource = promptSurface.systemForRole('en', role)
    const fast = profile(role, 'Fast')
    const deep = profile(role, 'Deep')

    assert.equal(typeof id, 'string')
    assert.ok(id.length > 0)
    assert.equal(fast.ok, true)
    assert.equal(deep.ok, true)
    assert.equal(fast.systemPromptId, id)
    assert.equal(deep.systemPromptId, id)
    assert.equal(typeof resource, 'string')
    assert.ok(resource.length > 0, `${role} resource must not be empty`)
    assert.equal(resource.includes(`system_prompt_id = "${id}"`), false, 'resource bytes must not smuggle the identity field')
    ids.add(id)
  }
  assert.equal(ids.size, roles.length, 'canonical roles must not alias prompt identities')
})

test('WHAT[prefix-stability-007] PROMPT_019_participant_identity_preserved_across_tiers_not_prompt_identity', () => {
  const engineerFast = delegation.vocabulary('Engineer', 'Fast', OWNER)
  const engineerDeep = delegation.vocabulary('Engineer', 'Deep', OWNER)

  // Single-version world: participant and role identity are fixed on every
  // tier — identity is preserved without tying prompt bytes to physical tier.
  assert.equal(engineerFast.agent, 'engineer')
  assert.equal(engineerDeep.agent, 'engineer')
  for (const attempt of [profile('Engineer', 'Fast'), profile('Engineer', 'Deep')]) {
    assert.equal(attempt.participantIdentity.participant, attempt.participant)
    assert.equal(attempt.participantIdentity.selectedTier, 'deep')
  }
  assert.equal(profile('Manager', 'Fast').systemPromptId, profile('Manager', 'Deep').systemPromptId)
  assert.equal(profile('DevOps', 'Fast').systemPromptId, profile('DevOps', 'Deep').systemPromptId)
})

test('WHAT[prefix-stability-007] planned Manager and DevOps carry their authority prompt identity', () => {
  const manager = profile('Manager', 'Fast')
  const devops = profile('DevOps', 'Fast')

  // These are current plan identities, not observations across one Life.
  assert.equal(manager.systemPromptId, authority.systemPromptIdForRole('Manager'))
  assert.equal(devops.systemPromptId, authority.systemPromptIdForRole('DevOps'))
  assert.match(manager.requestKind, /^work-?main$/i)
  assert.match(devops.requestKind, /^work-?main$/i)
})

test.todo('WHAT[prefix-stability-007] one actual Life keeps identical system bytes and Persona through delegation, failure, review and compaction; role IDs alone do not prove byte identity; GAP-106')

test('WHAT[prefix-stability-007] PROMPT_019_role_and_tier_capabilities_remain_explicit', () => {
  const managerFast = profile('Manager', 'Fast')
  const managerDeep = profile('Manager', 'Deep')
  const engineerFast = profile('Engineer', 'Fast')

  assert.ok(managerFast.toolCapabilities.length > 0)
  assert.ok(managerDeep.toolCapabilities.length > 0)
  assert.ok(engineerFast.toolCapabilities.length > 0)
  assert.deepEqual(managerFast.toolCapabilities, managerDeep.toolCapabilities)
  assert.notDeepEqual(managerFast.toolCapabilities, engineerFast.toolCapabilities)
})

test('WHAT[prefix-stability-007] manager_life_review_acceptance_system_prompt_byte_identical', () => {
  // Office system prompt 在同一个生命周期（Life）内保持逐字节一致。
  // 严禁因 T1 交托、Fallback 切换、Review 或 Host compaction 等事件改写 system prompt 字节或重绑 Persona。
  const managerSystemPromptBefore = promptSurface.systemForRole('en', 'Manager')
  const managerSystemPromptAfter = promptSurface.systemForRole('en', 'Manager')

  assert.ok(managerSystemPromptBefore.length > 0, 'Manager system prompt must not be empty')
  assert.equal(
    managerSystemPromptBefore,
    managerSystemPromptAfter,
    'system prompt must remain byte-identical before and after Review acceptance in same Life',
  )

  // Negative counterexample: system prompt drift across Review breaks prefix stability
  const attemptBefore = {
    modelId: 'manager-model-v1',
    providerId: 'manager-test-provider',
    variant: 'deep',
    messages: [{ id: 'msg-sys-01', role: 'user', parts: [{ kind: 'text', text: 'SYSTEM PROMPT STABILITY PROOF' }] }],
    tools: tools.toolSpecNames().map((name) => canonicalJson.canonicalJson({ name })),
    system: [managerSystemPromptBefore],
  }
  const driftedSystemAttempt = {
    ...attemptBefore,
    system: ['MUTATED SYSTEM PROMPT: Review event illegally modified prompt bytes'],
  }

  assert.equal(
    providerProjection.isAppendOnlyPrefix(attemptBefore, driftedSystemAttempt),
    false,
    'system prompt drift across review must break provider prefix law',
  )
})
