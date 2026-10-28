import test from 'node:test'
import assert from 'node:assert/strict'
import { mkdtempSync, readFileSync, readdirSync, rmSync } from 'node:fs'
import { join, resolve } from 'node:path'
import { tmpdir } from 'node:os'
import { planImpactCompile, compileIncremental } from '../../../../scripts/lib/owner-compile.mjs'
import { integrationTest } from '../../../verification-system/tests/support/tier-gate.mjs'

const root = resolve(import.meta.dirname, '../../../..')
const projectDirectory = join(root, 'src/Wanxiangshu')
const consumer = join(projectDirectory, 'Execution/Fission/OpenCode/Tool.fs')
const provider = join(projectDirectory, 'Execution/Tool/ToolResultText.fs')
const sessionScope = join(projectDirectory, 'OpenCode/Host/PluginSessionScope.fs')
const toolRuntime = join(projectDirectory, 'OpenCode/Tools/ToolRuntimeScope.fs')

test('WHAT[structured-workflow-012] Fission impact preserves the Execution.Tool binding and reverse signature consumers', () => {
  const full = planImpactCompile({ projectDirectory, changedPaths: [join(root, 'Directory.Build.props')] })
  for (const changedPaths of [[consumer], [provider.replace(/\.fs$/, '.fsi')], [consumer, provider]]) {
    const plan = planImpactCompile({ projectDirectory, changedPaths })
    assert.ok(plan.compileItems.includes(consumer), 'the Fission consumer must be selected')
    assert.ok(plan.compileItems.includes(provider), 'the Execution.Tool provider must be selected')
    assert.ok(plan.compileItems.includes(provider.replace(/\.fs$/, '.fsi')))
    assert.ok(plan.compileItems.length < full.compileItems.length, 'a local change must keep a focused closure')
    const selected = new Set(plan.compileItems)
    assert.deepEqual(plan.compileItems, full.compileItems.filter(file => selected.has(file)))
  }
  const runtime = planImpactCompile({ projectDirectory, changedPaths: [toolRuntime] })
  assert.ok(runtime.compileItems.includes(sessionScope), 'include the HostJoinAttemptFactory provider')
  const session = planImpactCompile({ projectDirectory, changedPaths: [sessionScope] })
  assert.ok(!session.compileItems.includes(toolRuntime), 'the session provider must not depend on its runtime consumer')
  const signature = planImpactCompile({ projectDirectory, changedPaths: [sessionScope.replace(/\.fs$/, '.fsi')] })
  assert.ok(signature.compileItems.includes(toolRuntime), 'a session signature change must reach its runtime consumer')
})

integrationTest('WHAT[structured-workflow-012] Fission incremental JavaScript matches a clean full compilation byte for byte', { timeout: 300000 }, async t => {
  const scratchRoot = mkdtempSync(join(tmpdir(), 'wanxiangshu-fission-impact-'))
  const outputDir = join(scratchRoot, 'dist')
  const snapshot = () => Object.fromEntries(readdirSync(outputDir, { recursive: true })
    .filter(file => file.endsWith('.js')).sort().map(file => [file, readFileSync(join(outputDir, file), 'utf8')]))
  const compile = changedPaths => compileIncremental({
    root, projectDirectory, scratchRoot, outputDir, changedPaths,
    rootPropsPath: join(root, 'Directory.Build.props'), stdio: 'pipe',
  })
  try {
    const full = await compile([join(root, 'Directory.Build.props')])
    assert.equal(full.ok, true, `${full.stdout}\n${full.stderr}`)
    const expected = snapshot()
    for (const [changed, emitted] of [
      [consumer, 'Execution/Fission/OpenCode/Tool.js'],
      [sessionScope, 'OpenCode/Host/PluginSessionScope.js'],
      [provider.replace(/\.fs$/, '.fsi'), 'Execution/Fission/OpenCode/Tool.js'],
    ]) {
      rmSync(outputDir, { recursive: true })
      const focused = await compile([changed])
      assert.equal(focused.ok, true, `${focused.stdout}\n${focused.stderr}`)
      assert.equal(focused.cached, false)
      const actual = snapshot()
      assert.ok(Object.keys(actual).length < Object.keys(expected).length)
      assert.ok(actual[emitted], `compile the affected consumer: ${emitted}`)
      for (const [file, bytes] of Object.entries(actual)) {
        assert.equal(bytes, expected[file], `incremental/full mismatch: ${file}`)
      }
      t.diagnostic(`${changed}: ${Object.keys(actual).length} JavaScript files match the full build`)
    }
  } finally {
    rmSync(scratchRoot, { recursive: true, force: true })
  }
})
