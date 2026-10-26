import assert from 'node:assert/strict'
import { mkdirSync, mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import test from 'node:test'
import * as surface from '../../../dist/Mission/Planning/Surface.js'
import * as lifecycle from '../../../dist/Mission/Lifecycle/Surface.js'
import * as workspace from '../../../dist/OpenCode/Host/WorkspaceEventStoreSurface.js'

const withStore = async (body) => {
  const root = mkdtempSync(join(tmpdir(), 'wxs-planning-028-'))
  const commonDir = join(root, '.Herdchestra')
  mkdirSync(commonDir, { recursive: true })
  try {
    await body(workspace.acquire(commonDir), root, commonDir)
  } finally {
    rmSync(root, { recursive: true, force: true })
  }
}

const openWork = async (store, commonDir, workId) => {
  const opened = await surface.PlanningSurface.executeReview(
    commonDir, workId, 'provider-open', `open-${workId}`,
    [{ key: 'investigate', value: 'establish the requested repository facts' }], null,
  )
  assert.equal(opened.ok, true, opened.error)
}

test('WHAT[work-028] devops delegates the intent to the bound DevOps and returns its delivered view', async () => {
  await withStore(async (store, _root, commonDir) => {
    await openWork(store, commonDir, 'work-ask-1')
    const life = lifecycle.LifecycleSurface ?? lifecycle
    // 开立阶段不提前绑定 DevOps
    const initial = life.inspected(commonDir, 'work-ask-1')
    assert.equal(initial.DevOpsId, undefined, 'initial work opening must not bind DevOps')

    let delegatedIntent = null
    const delegate = async (intent) => {
      delegatedIntent = intent
      return '# DevOps delivered view\nInvestigated build artifact paths; they match expectations.'
    }

    const res = await surface.PlanningSurface.executeAsk(commonDir, 'work-ask-1', 'Check whether build artifact paths match expectation', delegate)
    assert.equal(res.ok, true, res.error)
    assert.equal(res.kind, 'delegated')
    assert.equal(delegatedIntent, 'Check whether build artifact paths match expectation')

    // 首个 devops 调查收敛后必须绑定固定 DevOps 事实
    const after = life.inspected(commonDir, 'work-ask-1')
    assert.ok(after.DevOpsId, 'devops delegation must bind a fixed DevOps')
    assert.match(after.DevOpsId, /^devops-/, 'bound DevOps must have fixed devops identity')
    const again = await surface.PlanningSurface.executeAsk(commonDir, 'work-ask-1', 'Check the follow-up fact', delegate)
    assert.equal(again.ok, true, again.error)
    assert.equal(life.inspected(commonDir, 'work-ask-1').DevOpsId, after.DevOpsId)
    const resEmpty = await surface.PlanningSurface.executeAsk(commonDir, 'work-ask-1', '   ', delegate)
    assert.equal(resEmpty.ok, false)
    assert.match(resEmpty.error, /cannot be empty/i)
  })
})

test('WHAT[work-028] a second devops investigation is rejected while one delegation is pending', async () => {
  await withStore(async (store, _root, commonDir) => {
    await openWork(store, commonDir, 'work-ask-2')
    let finish
    const pendingDelegate = () => new Promise(resolve => { finish = resolve })
    const first = surface.PlanningSurface.executeAsk(commonDir, 'work-ask-2', 'First investigation', pendingDelegate)
    const second = await surface.PlanningSurface.executeAsk(commonDir, 'work-ask-2', 'Second investigation', pendingDelegate)
    assert.equal(second.ok, false)
    assert.match(second.error, /pending|already/i)
    finish('Investigation completed.')
    assert.equal((await first).ok, true)
  })
})
