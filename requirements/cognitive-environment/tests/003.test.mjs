import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import test from 'node:test'
import * as prompts from '../../../dist/Resources/PromptSurface.js'

const roles = ['manager', 'engineer', 'devops', 'orchestrator', 'plan']
const resourceRoot = new URL('../../../resources/provider/', import.meta.url)
const read = (path, locale) => readFileSync(new URL(`${path}/${locale}.md`, resourceRoot), 'utf8').trim()
const instructionText = (system) => system.trimEnd().split('\n').map((line) => {
  assert.ok(line === '#' || line.startsWith('# '), 'system material remains in the instruction plane')
  return line === '#' ? '' : line.slice(2)
}).join('\n')

for (const [language, locale] of [['English', 'en'], ['SimplifiedChinese', 'zh-CN']]) {
  test(`WHAT[cognitive-environment-003] ${locale} actual system assembly places complete Common Law before Role Law and any inherited library`, () => {
    const common = read('world/common-law', locale)
    const ingress = read('library/ingress', locale)
    const closing = read('library/closing', locale)
    const books = ['kolmogorov', 'scarcity', 'work/quality-ledger'].map((name) => read(`library/${name}`, locale))
    for (const role of roles) {
      const law = read(`role/${role}`, locale)
      const text = instructionText(prompts.systemForRole(language, role))
      assert.ok(text.startsWith(common), role)
      const roleStart = text.indexOf(law, common.length)
      assert.ok(roleStart >= common.length, `${role}: complete role law after common law`)
      assert.equal(text.indexOf(law, roleStart + law.length), -1, `${role}: no duplicated role law`)
      const remainder = text.slice(roleStart + law.length).trim()
      if (remainder.length > 0) {
        assert.ok(remainder.startsWith(ingress), `${role}: library starts after role`)
        assert.ok(remainder.endsWith(closing), `${role}: complete library closing`)
        assert.ok(books.some((book) => remainder.includes(book)), `${role}: actual inherited book`)
      }
    }
    const bookkeeper = instructionText(prompts.loadBookkeeperSystemFor(language))
    assert.ok(bookkeeper.startsWith(common))
    assert.ok(bookkeeper.indexOf(read('role/bookkeeper', locale), common.length) >= common.length)
  })
}

test.todo('WHAT[cognitive-environment-003] actual request keeps tools, lifecycle and mission in their designated channels (GAP-076)')
