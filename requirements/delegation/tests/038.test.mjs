import test from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { dirname, join } from 'node:path'

const root = join(dirname(fileURLToPath(import.meta.url)), '../../..')
const read = rel => readFileSync(join(root, rel), 'utf8')

// The closing report is the child's final-turn prose (JoinResultRenderer). A
// resetScope after it opens an invented next Scope and pushes the report out of
// the final turn, so every model-facing text that asks for resetScope must say so.
const rules = {
  en: /closing report[\s\S]*do not call resetScope/,
  'zh-CN': /收尾报告[\s\S]*不要再调用 resetScope/,
}

for (const semantic of ['delegation/fork-child-base', 'host/scope-steer/reset-due']) {
  for (const [lang, rule] of Object.entries(rules)) {
    test(`WHAT[delegation-038] ${semantic} (${lang}) forbids resetScope after the prose closing report`, () => {
      assert.match(read(`resources/provider/${semantic}/${lang}.md`), rule)
    })
  }
}

test('WHAT[delegation-038] the resetScope tool description forbids calling it after the closing report', () => {
  assert.match(read('dist/OpenCode/Tools/ResetScopeTool.js'), /Never call it after you have written your closing report in prose/)
})
