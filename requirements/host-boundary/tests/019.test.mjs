import assert from 'node:assert/strict'
import test from 'node:test'
import { ordinaryEffects } from '../../../dist/OpenCode/Host/PluginTransformSurface.js'

test('WHAT[host-boundary-019] the ordinary transform runs the full static stage sequence in contract order', async () => {
  const current = await ordinaryEffects()

  assert.deepEqual(current, [
    'begin',
    'session-time',
    'deferred',
    'relay',
    'tenure-isolation',
    'capture',
    'refresh-companion',
    'companion',
    'prefix',
    'freeze-plan',
    'pair',
    'deferred',
    'sanitize',
  ])
  assert.equal(
    current.filter((effect) => effect === 'deferred').length,
    2,
    'deferred inspections must be settled before relay and reapplied after current auxiliaries',
  )
  assert.equal(current.at(-1), 'sanitize', 'sanitize must close the ordinary pipeline')
})

// 边界：第 3 步的 RetiredAttemptStopped 早退（relay 切分后立即结束、后续 stage 不执行）
// 无法经 ordinaryEffects 观察，该 Surface 的 ApplyRelayProjection 固定返回 CurrentTenure。
// 此路径需要真实 Host canary 或专门的早退注入端口，不在此硬造旁路。

test('WHAT[host-boundary-019] ordinary transform runs companion and prefix compression without suppression', async () => {
  const current = await ordinaryEffects()
  assert.ok(current.includes('companion'), 'ordinary material must execute companion')
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
