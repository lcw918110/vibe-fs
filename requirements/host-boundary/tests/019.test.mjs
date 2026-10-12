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
import { integrationTest } from '../../verification-system/tests/support/tier-gate.mjs'
import { spawnSync, execFileSync } from 'node:child_process'
import { existsSync } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const here = path.dirname(fileURLToPath(import.meta.url))
const runnerPath = path.join(here, 'support/run-manager-review-tools-canary.mjs')
const repoRoot = path.resolve(here, '../../..')
const OPENCODE_BIN = process.env.OPENCODE_BIN ?? 'opencode'

const checkOpencodeExecutable = () => {
  if (process.env.OPENCODE_BIN && existsSync(process.env.OPENCODE_BIN)) return true
  const localBin = path.join(repoRoot, 'node_modules/.bin/opencode')
  if (existsSync(localBin)) return true
  try {
    execFileSync(OPENCODE_BIN, ['--version'], { stdio: 'ignore' })
    return true
  } catch {
    return false
  }
}

integrationTest(
  'WHAT[host-boundary-019] every required Host capability needs a real supported-Host canary; injected transform ports prove only composition',
  async (t) => {
    if (!checkOpencodeExecutable()) {
      return t.skip(
        `OpenCode binary not executable at ${OPENCODE_BIN}; requires real OpenCode host to execute canary runner`,
      )
    }

    const launched = spawnSync(process.execPath, [runnerPath], {
      cwd: repoRoot,
      encoding: 'utf8',
      timeout: 120000,
    })

    let stdoutSummary = null
    try {
      stdoutSummary = JSON.parse(launched.stdout.trim())
    } catch {
      stdoutSummary = launched.stdout
    }

    const failureMessage = [
      `Real host canary runner exited with code ${launched.status}.`,
      `[Runner Stderr]:\n${launched.stderr || '(empty)'}`,
      `[Runner Stdout Summary]:\n${typeof stdoutSummary === 'object' ? JSON.stringify(stdoutSummary, null, 2) : stdoutSummary}`,
    ].join('\n')

    assert.equal(launched.status, 0, failureMessage)
    assert.ok(stdoutSummary, 'Canary runner must produce JSON summary output')
    assert.equal(stdoutSummary.versions?.plugin, '1.18.29', 'plugin version must match fixture')
    assert.ok(stdoutSummary.wireInspection, 'wireInspection evidence required')
    assert.equal(stdoutSummary.wireInspection?.contractRequired, true, 'wire contract must be required')
    assert.ok(stdoutSummary.calls?.normal, 'normal call evidence required')
    assert.equal(stdoutSummary.calls.normal.status, 'completed')
    assert.ok(stdoutSummary.calls?.executorThrow, 'executorThrow boundary canary evidence required')
    assert.equal(stdoutSummary.calls.executorThrow.status, 'error')
  },
)
