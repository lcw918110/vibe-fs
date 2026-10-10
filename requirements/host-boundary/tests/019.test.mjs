import assert from 'node:assert/strict'
import test from 'node:test'
import { ordinaryEffects } from '../../../dist/OpenCode/Host/PluginTransformSurface.js'

test('WHAT[host-boundary-019] a tentative prefix suppresses historical auxiliaries within the actual transform', async () => {
  const current = await ordinaryEffects(false)
  const tentative = await ordinaryEffects(true)
  const historical = ['pair', 'grounding', 'delegation']
  assert.deepEqual(current.filter((effect) => historical.includes(effect)), historical)
  assert.deepEqual(tentative, current.filter((effect) => !historical.includes(effect)))
  assert.ok(tentative.includes('capture'), 'prefix gating must not bypass current trace capture')
  assert.ok(tentative.includes('freeze-plan'), 'the current attempt still needs its frozen plan')
  assert.equal(tentative.at(-1), 'sanitize')
})

test('WHAT[host-boundary-019] the ordinary transform runs the full static stage sequence in contract order', async () => {
  const current = await ordinaryEffects(false)

  // 17 步合同的全量 effect 序列：第 3 步与第 16 步各结算一次 deferred，
  // 14.1/14.2/14.3 三段只在 Current horizon 出现，第 17 步 sanitize 收尾。
  assert.deepEqual(current, [
    'begin',
    'session-time',
    'deferred',
    'relay',
    'tenure-isolation',
    'replay',
    'restore-arguments',
    'capture',
    'commit-trace',
    'refresh-companion',
    'companion',
    'prefix',
    'freeze-plan',
    'continuation',
    'pair',
    'grounding',
    'delegation',
    'chronicle',
    'deferred',
    'sanitize',
  ])
  assert.equal(
    current.filter((effect) => effect === 'deferred').length,
    2,
    'steps 3 and 16 must each settle deferred inspections',
  )
  assert.equal(current.at(-1), 'sanitize', 'step 17 must close the ordinary pipeline')

  const tentative = await ordinaryEffects(true)
  assert.deepEqual(tentative, [
    'begin',
    'session-time',
    'deferred',
    'relay',
    'tenure-isolation',
    'replay',
    'restore-arguments',
    'capture',
    'commit-trace',
    'refresh-companion',
    'companion',
    'prefix',
    'freeze-plan',
    'continuation',
    'chronicle',
    'deferred',
    'sanitize',
  ])
  for (const auxiliary of ['pair', 'grounding', 'delegation']) {
    assert.ok(!tentative.includes(auxiliary), auxiliary + ' must be skipped on a tentative cold horizon')
  }
})

// 边界：第 3 步的 RetiredAttemptStopped 早退（relay 切分后立即结束、后续 stage 不执行）
// 无法经 ordinaryEffects 观察，该 Surface 的 ApplyRelayProjection 固定返回 CurrentIteration。
// 此路径需要真实 Host canary 或专门的早退注入端口，不在此硬造旁路。

test('WHAT[host-boundary-019] ordinary transform runs companion and prefix compression without suppression', async () => {
  const current = await ordinaryEffects(false)
  assert.ok(current.includes('companion'), 'ordinary material must execute companion to trigger blogger')
  assert.ok(current.includes('prefix'), 'ordinary material must execute XWire to apply prefix compression')
  const companionIndex = current.indexOf('companion')
  const prefixIndex = current.indexOf('prefix')
  assert.ok(companionIndex < prefixIndex, 'companion runs before prefix compression')
})
test.todo('WHAT[host-boundary-019] every required Host capability needs a real supported-Host canary; injected transform ports prove only composition')
