import assert from 'node:assert/strict'
import test from 'node:test'
import * as ToolSurface from '../../../dist/OpenCode/Tools/ToolSurface.js'
import * as Strength from '../../../dist/Strength/Surface.js'

test('WHAT[planning-013] StaticTools and estimate contract explicitly recognize the five plan tools', () => {
  const planTools = ['js-plan', 'ask', 'resume', 'handoff', 'deliver']

  // ToolSurface exports toolSpecNames as an array of all recognized tool names
  const names = ToolSurface.toolSpecNames()
  assert.ok(Array.isArray(names), 'ToolSurface.toolSpecNames() must return an array')

  for (const tool of planTools) {
    assert.ok(names.includes(tool), `StaticTools.knownToolNames must explicitly include "${tool}"`)
  }

  // Strength.classifyTool must explicitly classify plan tools
  assert.ok(typeof Strength.classifyTool === 'function', 'Strength.classifyTool must be a function')

  // All 5 plan tools are classified as NoEstimate
  for (const tool of planTools) {
    const policy = Strength.classifyTool(tool)
    assert.equal(policy, 'NoEstimate', `${tool} must be explicitly classified as NoEstimate`)
  }
})

test('WHAT[planning-013] tool registration does not allow prefix or wildcard matching for plan tools', () => {
  assert.ok(typeof Strength.classifyTool === 'function', 'Strength.classifyTool must be a function')

  // Prefix or wildcard attempts must be rejected or classified as Unreviewed
  // They must NEVER match EstimateAfterCall or NoEstimate
  const invalidAttempts = ['js-plan-fake', 'plan*', 'ask_question', 'handoff_next', 'deliver_final']
  for (const attempt of invalidAttempts) {
    const res = Strength.classifyTool(attempt)
    assert.equal(res, 'Unreviewed', `"${attempt}" must not match plan tool via prefix; must be Unreviewed`)
  }
})
