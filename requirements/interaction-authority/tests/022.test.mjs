import assert from 'node:assert/strict'
import test from 'node:test'
import * as handles from '../../../dist/Execution/Delegation/Handle/Surface.js'
import * as lifecycle from '../../../dist/Mission/Lifecycle/Surface.js'
import { realpathSync } from 'node:fs'
import { mkdirSync, mkdtempSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { execFileSync } from 'node:child_process'
import * as workspace from '../../../dist/OpenCode/Host/WorkspaceEventStoreSurface.js'

test('WHAT[interaction-authority-022] actual work decision preserves an established DevOps model on a conflicting rebind', async () => {
  const directory = mkdtempSync(join(tmpdir(), 'devops-rebind-'))
  execFileSync('git', ['init'], { cwd: directory })
  mkdirSync(join(directory, '.Herdchestra'), { recursive: true })
  const store = workspace.acquire(realpathSync(join(directory, '.Herdchestra')))
  const surface = lifecycle.LifecycleSurface ?? lifecycle
  const opened = surface.submitReview(store.gitCommonDir, 'work-fixed', 'open-work-fixed', 'Plan', 'run', 'open', [{ key: 'need', value: 'bind' }], [], '')
  assert.equal(opened.kind, 'Reviewed', opened.reason)
  const bound = await lifecycle.bindDevOps(store, 'work-fixed', 'devops:work-fixed', 'provider/fixed-model:high')
  assert.equal(bound.ok, true)
  const original = lifecycle.workDevOps(store, 'work-fixed')
  assert.equal(original.devopsId, 'devops:work-fixed')
  assert.equal(original.modelTarget, 'provider/fixed-model:high')
  const repeated = await lifecycle.bindDevOps(store, 'work-fixed', 'devops:work-fixed', 'provider/other-model:low')
  assert.equal(repeated.ok, true)
  assert.deepEqual(lifecycle.workDevOps(store, 'work-fixed'), original)
})

test('WHAT[interaction-authority-022] pure handle transition rejects a second child on the occupied DevOps handle', () => {
  const handle = handles.handleIdAgent('devops')
  const first = handles.apply(handles.emptyState(), { op: 'link', handle, child: 'devops-child-1', agent: 'devops', role: 'DevOps' })
  assert.equal(first.ok, true)
  const second = handles.apply(first.state, { op: 'link', handle, child: 'devops-child-2', agent: 'devops', role: 'DevOps' })
  assert.equal(second.ok, false)
})

test.todo('WHAT[interaction-authority-022] GAP-122 actual DevOps resume and crash recovery preserve model Persona and one logical authority through concurrent physical attempts')
