import assert from 'node:assert/strict'
import test from 'node:test'
import * as fork from '../../../dist/Execution/Delegation/Fork/Surface.js'
import { clearAllForTests, refreshGlobalLanguage, languageOfSession, transformRoleSystem } from '../../../dist/Participant/Provider/LanguageSurface.js'
import { configure, installDefaultResources } from '../../../dist/OpenCode/Host/ManagedAgentConfigSurface.js'
import * as prompts from '../../../dist/Resources/PromptSurface.js'
import { withPreference } from './support/language-fixtures.mjs'

const fields = {
  manager: 'ManagerSystemPrompt', orchestrator: 'OrchestratorSystemPrompt',
  engineer: 'EngineerSystemPrompt', devops: 'DevopsSystemPrompt', plan: 'PlanSystemPrompt',
}
const buildConfig = () => ({
  agent: Object.fromEntries([...Object.keys(fields), 'bookkeeper'].map((name) => [name, { model: `${name}-model` }])),
})

test.beforeEach(clearAllForTests)
test.afterEach(clearAllForTests)

test('WHAT[provider-language-013] config projection selects the complete catalog for each global language', async () => {
  for (const [preference, language] of [['zh-CN', 'SimplifiedChinese'], ['en', 'English']]) {
    await withPreference(preference, () => {
      installDefaultResources()
      const config = buildConfig()
      assert.equal(configure(config).ok, true)
      const expected = prompts.loadForLanguage(language)
      for (const [name, field] of Object.entries(fields)) {
        assert.equal(config.agent[name].prompt, expected[field], `${name}/${language}`)
      }
      assert.equal(config.agent.bookkeeper.prompt, prompts.loadBookkeeperSystemFor(language))
    })
  }
})

test('WHAT[provider-language-013] system repair follows the live global preference, switching mid-session', async () => {
  for (const [initial, language, changed, otherLanguage] of [
    ['en', 'English', 'zh-CN', 'SimplifiedChinese'],
    ['zh-CN', 'SimplifiedChinese', 'en', 'English'],
  ]) {
    const session = `manager-${language}`
    await withPreference(initial, () => refreshGlobalLanguage())
    await withPreference(changed, async () => {
      refreshGlobalLanguage()
      installDefaultResources()
      // The live preference wins even though the session was first seen under
      // the opposite language: the prefix may change with the language.
      const expected = prompts.loadForLanguage(otherLanguage).ManagerSystemPrompt
      const foreign = 'HOST-owned bytes: exit_code 中文'
      for (const input of [
        prompts.loadForLanguage(language).ManagerSystemPrompt,
        prompts.loadForLanguage(otherLanguage).ManagerSystemPrompt,
      ]) {
        const output = await transformRoleSystem(session, 'Manager', [input, foreign])
        assert.equal(output.system[0], expected)
        assert.equal(output.system[1], foreign)
        if (otherLanguage === 'SimplifiedChinese') {
          assert.match(output.system[2], /状态闭环/)
        } else {
          assert.match(output.system[2], /state closed-loop/i)
        }
        const repeated = await transformRoleSystem(session, 'Manager', output.system)
        assert.deepEqual(repeated.system, output.system)
      }
      assert.equal(languageOfSession(session), otherLanguage)
    })
  }
})

test('WHAT[provider-language-013] localized role prompt and fork child payload instruction exports contain Chinese prose under a Chinese preference', async () => {
  await withPreference('zh-CN', async () => {
    refreshGlobalLanguage()
    const managerSystem = prompts.systemForRole('SimplifiedChinese', 'Manager')
    assert.match(managerSystem, /[\u4e00-\u9fff]/)

    const instructions = fork.instructions('zh-CN')
    for (const text of [instructions.Attachment, instructions.CommissionerRecord, instructions.Requirements, ...instructions.Base]) {
      if (!text) continue
      assert.match(text, /[\u4e00-\u9fff]/)
    }
  })
})

test('WHAT[provider-language-013] horizon prose contains Chinese while the technical participant label survives', async () => {
  await withPreference('zh-CN', async () => {
    refreshGlobalLanguage()
    const forkTool = await import('../../../dist/Execution/Delegation/Fork/OpenCode/ToolSurface.js')
    const { mkdtempSync, rmSync } = await import('node:fs')
    const { tmpdir } = await import('node:os')
    const { join } = await import('node:path')
    const directory = mkdtempSync(join(tmpdir(), 'wxs-horizon-zh-'))
    const owner = 'manager-horizon-zh'
    const runtime = await forkTool.createRuntime(directory, [{ sessionId: owner, agent: 'manager' }])
    try {
      const roster = await forkTool.executeHorizon(runtime, owner)
      assert.match(roster, /[\u4e00-\u9fff]/)
      assert.match(roster, /devops/)
    } finally {
      forkTool.disposeRuntime(runtime)
      rmSync(directory, { recursive: true, force: true })
    }
  })
})