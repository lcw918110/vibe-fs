import assert from 'node:assert/strict'
import test from 'node:test'
import * as ToolSurface from '../../../dist/OpenCode/Tools/ToolSurface.js'

test('WHAT[work-030] StaticTools explicitly registers all five plan tools and excludes handoff/resume', () => {
  const planTools = ['todowrite-for-plan', 'devops', 'ask', 'review', 'deliver']

  // ToolSurface exports toolSpecNames as an array of all recognized tool names
  const names = ToolSurface.toolSpecNames()
  assert.ok(Array.isArray(names), 'ToolSurface.toolSpecNames() must return an array')

  for (const tool of planTools) {
    assert.ok(names.includes(tool), `StaticTools.knownToolNames must explicitly include "${tool}"`)
  }

  // This existing surface excludes resume from the spec list. Its separate
  // Manager permission remains intact; Plan still cannot use it.
  assert.ok(!names.includes('resume'))
  assert.ok(!names.includes('handoff'))

  // Admission is by exact registered name: the five plan tools are the only
  // plan-admitted entries beyond the shared base permissions.
  const planRules = ToolSurface.rolePermissionRules('plan')
  for (const tool of planTools) {
    assert.equal(planRules[tool], 'allow', `${tool} must be admitted to Plan by exact name`)
  }
})

test('WHAT[work-030] tool registration does not allow prefix or wildcard matching for plan tools', () => {
  // Prefix or wildcard attempts must be denied; the only wildcard is the
  // fail-closed `*` fallback that denies every unlisted name.
  const planRules = ToolSurface.rolePermissionRules('plan')
  const invalidAttempts = ['js-plan-fake', 'plan*', 'ask_question', 'devops_extra', 'review_next', 'deliver_final']
  for (const attempt of invalidAttempts) {
    assert.equal(Object.hasOwn(planRules, attempt), false, `"${attempt}" must not be a plan-admitted name`)
  }
  assert.equal(planRules['*'], 'deny', 'the wildcard fallback must deny every unregistered tool')
})
