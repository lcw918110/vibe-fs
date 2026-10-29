import assert from 'node:assert/strict'
import test from 'node:test'
import { readFileSync } from 'node:fs'
import { Delivery } from 'wanxiangli/delivery'
import { rolePermissionRules, toolSpecNames } from '../../../../dist/OpenCode/Tools/ToolSurface.js'

const read = (path) => readFileSync(new URL('../../../../' + path, import.meta.url), 'utf8')

test('WHAT[work-018] lifecycle deliver is absent from the catalog, the permission map, and the review help', () => {
  const names = toolSpecNames()
  assert.equal(names.includes('deliver'), false)
  assert.equal(names.includes('review'), true)
  for (const role of ['plan', 'manager', 'engineer']) {
    assert.equal(Object.hasOwn(rolePermissionRules(role), 'deliver'), false, `${role} must not map lifecycle deliver`)
  }
  assert.equal(rolePermissionRules('plan').review, 'allow')
  assert.equal(rolePermissionRules('manager').review, 'allow')
  const reviewEn = read('resources/provider/tool/review/description/en.md')
  const reviewZh = read('resources/provider/tool/review/description/zh-CN.md')
  assert.doesNotMatch(reviewEn, /deliver|eligibility|S1/)
  assert.doesNotMatch(reviewZh, /deliver|资格|S1/)
  assert.match(reviewEn, /obligation ledger/)
  assert.match(reviewZh, /义务台账/)
})

test('WHAT[work-018] wxs_deliver stays registered and delivery handOff returns the original piece', () => {
  const plugin = read('dist/Host/SecondHost/HostPlugin.js')
  assert.match(plugin, /wxs_deliver/)
  const delivery = Delivery()
  const fence = { providerRun: 'run-053', toolCallId: 'call-053' }
  delivery.deliver('session-053', fence, { text: 'draft' })
  const claimed = delivery.claim('session-053', { mode: 'run', fence })
  assert.equal(claimed.ok, true)
  const handed = delivery.handOff(claimed.ticket, [{ key: 'draft', text: 'draft' }])
  assert.deepEqual(handed.map((piece) => piece.key), ['draft'])
  assert.equal(handed[0].text, 'draft')
})
