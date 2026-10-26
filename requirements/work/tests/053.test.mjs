import assert from 'node:assert/strict'
import test from 'node:test'
import { mkdtempSync, readFileSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { Delivery } from 'wanxiangli/delivery'
import { rolePermissionRules, toolSpecNames } from '../../../dist/OpenCode/Tools/ToolSurface.js'
import * as lifecycle from '../../../dist/Mission/Lifecycle/Surface.js'

const surface = lifecycle.LifecycleSurface ?? lifecycle

const read = (path) => readFileSync(new URL('../../../' + path, import.meta.url), 'utf8')

test('WHAT[work-053] lifecycle deliver is absent from the catalog, the permission map, and the review help', () => {
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

test('WHAT[work-053] loaded Plan and Manager prose names review, not deliver, as the lifecycle entry', () => {
  const paths = [
    'role/plan', 'role/manager', 'planning/s1', 'planning/s2', 'planning/s3',
    'runtime/manager-work', 'runtime/manager-finish', 'work/plan-continue', 'work/manager-continue',
    'tool/js-plan', 'tool/js-program/base-class-review', 'tool/resume/dispatch-closed',
  ]
  for (const path of paths) {
    for (const locale of ['en', 'zh-CN']) {
      const text = read('resources/provider/' + path + '/' + locale + '.md')
      assert.doesNotMatch(text, /\bdeliver\b|post-S3|eligib|晋级资格|交付资格|交付授权/, path + '/' + locale)
    }
  }
  for (const locale of ['en', 'zh-CN']) {
    assert.match(read('resources/provider/role/plan/' + locale + '.md'), /`note`/)
  }
})

test('WHAT[work-053] one review relays and another closes cleanup through the work entry', () => {
  const root = mkdtempSync(join(tmpdir(), 'wxs-review-053-'))
  const workId = 'manager-session'
  const review = (id, updates) =>
    surface.submitReview(root, workId, id, 'Manager', 'run-' + id, 'call-' + id, updates, [], '')
  try {
    const opened = review('r1', [{ key: 'goal', value: 'ship the protocol' }])
    assert.equal(opened.status, 'Committed', opened.reason)
    assert.equal(opened.kind, 'Reviewed')
    assert.equal(opened.milestone, '执行')

    const relayed = review('r2', [{ key: 'goal', value: null }])
    assert.equal(relayed.status, 'Committed', relayed.reason)
    assert.equal(relayed.kind, 'Relayed')

    const again = review('r2', [{ key: 'goal', value: null }])
    assert.equal(again.status, 'Replayed')
    assert.equal(again.kind, 'Relayed')

    const advanced = review('r3', [])
    assert.equal(advanced.status, 'Committed', advanced.reason)
    assert.equal(advanced.kind, 'Advanced')
    assert.equal(advanced.milestone, '收尾')

    const closed = review('r4', [])
    assert.equal(closed.status, 'Committed', closed.reason)
    assert.equal(closed.kind, 'Completed')
    assert.equal(closed.milestone, '已完成')

    const refused = review('r5', [])
    assert.equal(refused.status, 'Conflict')
  } finally {
    rmSync(root, { recursive: true, force: true })
  }
})

test('WHAT[work-053] wxs_deliver stays registered and delivery handOff returns the original piece', () => {
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
