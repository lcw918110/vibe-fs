import assert from 'node:assert/strict'
import test from 'node:test'
import { readFileSync } from 'node:fs'
import { withExecutablePlugin, acceptAuthorityRoot } from '../../verification-system/tests/support/plugin-fixture.mjs'
import * as prompts from '../../../dist/Resources/PromptSurface.js'

const lifecycleModule = await import('../../../dist/Mission/Lifecycle/Surface.js')
const journalSurface = await import('../../../dist/Persistence/Journal/Surface.js')
const language = await import('../../../dist/Participant/Provider/LanguageSurface.js')

const surface = lifecycleModule.LifecycleSurface ?? lifecycleModule

const stageFile = {
  '事实与取舍': 's1',
  '理解与路径': 's2',
  '流程与伪代码': 's3',
}

const guidanceFor = (milestone, draftPath) => {
  const text = readFileSync(
    new URL(`../../../resources/provider/planning/${stageFile[milestone]}/en.md`, import.meta.url),
    'utf8',
  )
  return text.replaceAll('{{draft_path}}', draftPath).trim()
}

const draftPathFor = (directory, session) =>
  `${directory.replace(/\\/g, '/').replace(/\/$/, '')}/plan/${session}/plan.md`

test('WHAT[work-031] the prompt catalog carries a localized PlanSystemPrompt for real system transform wiring', () => {
  for (const languageName of ['en', 'zh-CN']) {
    const catalog = prompts.loadForLanguage(languageName)
    assert.ok(catalog != null, `loadForLanguage(${languageName}) must return a catalog`)
    const planPrompt = catalog.PlanSystemPrompt
    assert.ok(
      typeof planPrompt === 'string' && planPrompt.trim().length > 0,
      `the ${languageName} prompt catalog must carry a non-empty PlanSystemPrompt`,
    )
  }
})

test('WHAT[work-031] the registered plan system transform appends each milestone method and replaces it in place', async () => {
  const previous = process.env.WANXIANGSHU_PROVIDER_LANGUAGE
  process.env.WANXIANGSHU_PROVIDER_LANGUAGE = 'en'
  language.refreshGlobalLanguage()
  const session = 'plan-031-stage'
  try {
    await withExecutablePlugin(async (hooks, directory, _created, runtime) => {
      assert.equal(typeof hooks['experimental.chat.system.transform'], 'function')
      await acceptAuthorityRoot(runtime, session, 'plan', 'user-root-031')
      const commonDir = await journalSurface.JournalSurface_initializeStateRoot(directory)
      const draftPath = draftPathFor(directory, session)
      const review = (id, updates) =>
        surface.submitReview(commonDir, session, id, 'Plan', `run-${id}`, `call-${id}`, updates, null, '')

      const opened = review('r1', [{ key: 'item', value: 'facts' }])
      assert.equal(opened.kind, 'Reviewed', JSON.stringify(opened))
      assert.equal(opened.milestone, '事实与取舍')

      const present = async (milestone) => {
        const seen = surface.inspected(commonDir, session)
        assert.equal(seen.Stage, milestone)
        assert.ok(seen.ActiveId, `${milestone} must have an active tenure`)
        const output = { system: ['host preface'] }
        await hooks['experimental.chat.system.transform']({ sessionID: session }, output)
        const lines = output.system
        const expected = guidanceFor(milestone, draftPath)
        const hits = lines.filter((line) => line === expected)
        assert.deepEqual(hits, [expected], lines.join('\n---\n'))
        for (const other of Object.keys(stageFile)) {
          if (other === milestone) continue
          assert.equal(lines.includes(guidanceFor(other, draftPath)), false, other)
        }
        return lines
      }

      const first = await present('事实与取舍')
      const again = await present('事实与取舍')
      assert.deepEqual(again, first)

      const relayed = review('r2', [{ key: 'item', value: null }])
      assert.equal(relayed.kind, 'Relayed', JSON.stringify(relayed))
      const toPaths = review('r3', [])
      assert.equal(toPaths.kind, 'Advanced', JSON.stringify(toPaths))
      assert.equal(toPaths.milestone, '理解与路径')
      const second = await present('理解与路径')
      assert.equal(second.length, first.length)
      assert.equal(second[0], 'host preface')

      const kept = review('r4', [{ key: 'item', value: 'path' }])
      assert.equal(kept.kind, 'Reviewed', JSON.stringify(kept))
      const relayedAgain = review('r5', [{ key: 'item', value: null }])
      assert.equal(relayedAgain.kind, 'Relayed', JSON.stringify(relayedAgain))
      const toProcess = review('r6', [])
      assert.equal(toProcess.kind, 'Advanced', JSON.stringify(toProcess))
      assert.equal(toProcess.milestone, '流程与伪代码')
      const third = await present('流程与伪代码')
      assert.equal(third.length, second.length)
      assert.equal(third[0], 'host preface')

      const stranger = { system: ['host preface'] }
      await hooks['experimental.chat.system.transform']({ sessionID: 'not-a-plan-031' }, stranger)
      assert.equal(stranger.system.includes(guidanceFor('事实与取舍', draftPath)), false)
      assert.equal(stranger.system.includes(guidanceFor('理解与路径', draftPath)), false)
      assert.equal(stranger.system.includes(guidanceFor('流程与伪代码', draftPath)), false)
    })
  } finally {
    if (previous === undefined) delete process.env.WANXIANGSHU_PROVIDER_LANGUAGE
    else process.env.WANXIANGSHU_PROVIDER_LANGUAGE = previous
    language.refreshGlobalLanguage()
  }
})
