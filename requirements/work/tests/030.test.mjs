import assert from 'node:assert/strict'
import test from 'node:test'
import * as ToolSurface from '../../../dist/OpenCode/Tools/ToolSurface.js'

test('WHAT[work-030] StaticTools explicitly registers all three plan tools and excludes handoff/delegation verbs', () => {
  const planTools = ['todowrite-for-plan', 'review']

  // ToolSurface exports toolSpecNames as an array of all recognized tool names
  const names = ToolSurface.toolSpecNames()
  assert.ok(Array.isArray(names), 'ToolSurface.toolSpecNames() must return an array')

  for (const tool of planTools) {
    assert.ok(names.includes(tool), `StaticTools.knownToolNames must explicitly include "${tool}"`)
  }

  // This existing surface excludes the delegation verbs from the spec list.
  // Their separate Manager/Orchestrator permissions remain intact; Plan still
  // cannot use them.
  assert.ok(!names.includes('resume'))
  assert.ok(!names.includes('fork'))
  assert.ok(!names.includes('commission'))
  assert.ok(!names.includes('handoff'))

  // The host devops tool is retired: it is no longer a registered tool.
  assert.ok(!names.includes('devops'))
  assert.ok(!names.includes('deliver'), 'lifecycle deliver is not a registered tool')

  // Admission is by exact registered name: the plan tools are the only
  // plan-admitted entries beyond the shared base permissions.
  const planRules = ToolSurface.rolePermissionRules('plan')
  for (const tool of planTools) {
    assert.equal(planRules[tool], 'allow', `${tool} must be admitted to Plan by exact name`)
  }
})

test('WHAT[work-030] tool registration does not allow prefix or wildcard matching for plan tools', () => {
  const planRules = ToolSurface.rolePermissionRules('plan')
  const invalidAttempts = ['js-plan-fake', 'plan*', 'ask_question', 'devops', 'devops_extra', 'review_next', 'deliver_final']
  for (const attempt of invalidAttempts) {
    assert.equal(Object.hasOwn(planRules, attempt), false, `"${attempt}" must not be a plan-admitted name`)
  }
  assert.equal(planRules['*'], 'deny', 'the wildcard fallback must deny every unregistered tool')
})
