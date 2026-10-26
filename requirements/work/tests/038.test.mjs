import assert from 'node:assert/strict'
import test from 'node:test'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { rolePredicate } from '../../../dist/OpenCode/Tools/ToolRegistrySurface.js'
import { memberBinding } from '../../../dist/Repository/Programming/Js/GeneratorSurface.js'
import { permissions as rolePermissions } from '../../../dist/Participant/Persona/OfficeCapabilitySurface.js'
import * as surface from '../../../dist/Mission/Planning/Surface.js'

test('WHAT[work-038] review does not derive the next target from a stage name', () => {
  assert.equal(surface.PlanningSurface.reviewTargetFor, undefined)
  assert.equal(typeof surface.PlanningSurface.executeReview, 'function')
})

test('WHAT[work-038] one plan review records the ledger, a real clear relays, and the successor empty review advances', async () => {
  const root = mkdtempSync(join(tmpdir(), 'wxs-review-038-'))
  assert.equal(root.includes(`${join('wanxiangshu', '.Herdchestra')}`), false)
  const review = (id, findings) =>
    surface.PlanningSurface.executeReview(root, 'plan-work', 'run-plan', id, findings, null)
  try {
    const opened = await review('r1', [{ key: 'tradeoff', value: 'compare stores' }])
    assert.equal(opened.ok, true, opened.error)
    assert.equal(opened.kind, 'Reviewed')
    assert.equal(opened.milestone, '事实与取舍')

    const kept = await review('r2', [])
    assert.equal(kept.ok, true, kept.error)
    assert.equal(kept.kind, 'Reviewed', 'an empty array does not clear an existing key')
    assert.equal(kept.milestone, '事实与取舍')

    const relayed = await review('r3', [{ key: 'tradeoff', value: null }])
    assert.equal(relayed.ok, true, relayed.error)
    assert.equal(relayed.kind, 'Relayed')
    assert.equal(relayed.milestone, '事实与取舍')

    const replay = await review('r3', [{ key: 'tradeoff', value: null }])
    assert.equal(replay.ok, true, replay.error)
    assert.equal(replay.kind, 'Relayed')

    const advanced = await review('r4', [])
    assert.equal(advanced.ok, true, advanced.error)
    assert.equal(advanced.kind, 'Advanced')
    assert.equal(advanced.milestone, '理解与路径')

    const invalid = await review('r5', 'not-an-array')
    assert.equal(invalid.ok, false)
    assert.match(String(invalid.error), /array/)
  } finally {
    rmSync(root, { recursive: true, force: true })
  }
})

test('WHAT[work-038] opening and deliver write no second lifecycle', async () => {
  const root = mkdtempSync(join(tmpdir(), 'wxs-review-038-open-'))
  assert.equal(root.includes(`${join('wanxiangshu', '.Herdchestra')}`), false)
  const host = await import('../../../dist/Mission/Lifecycle/DurableWorkHost.js')
  const ensure = host.ensureOpened ?? host.DurableWorkHost?.ensureOpened
  const inspected = host.inspected ?? host.DurableWorkHost?.inspected
  assert.equal(typeof ensure, 'function')
  assert.equal(typeof inspected, 'function')
  try {
    const opened = ensure(root, 'work-open', 'req-open', root, ['S1', 'S2', 'S3'], 'owner', 0)
    const text = JSON.stringify(opened)
    assert.equal(text.includes('"Opened":true') || text.includes('Committed'), false, text)
    assert.equal(inspected(root, 'work-open') ?? null, null)

    const delivered = await surface.PlanningSurface.executeDeliver(
      root,
      root,
      'work-open',
      'inc',
      'run',
      'call',
      false,
      {},
      1n,
    )
    assert.equal(delivered.ok, false)
    assert.match(String(delivered.error), /review/)
    assert.equal(inspected(root, 'work-open') ?? null, null)

    const plan = await surface.PlanningSurface.ensurePlanWorkOpened(root, root, 'work-open', 1n)
    assert.equal(plan.ok, true, plan.error)
    assert.equal(inspected(root, 'work-open') ?? null, null)
  } finally {
    rmSync(root, { recursive: true, force: true })
  }
})

test('WHAT[work-038] review admission admits Plan and Manager', () => {
  assert.equal(rolePredicate('review', 'plan'), true)
  assert.equal(rolePredicate('review', 'manager'), true)
  for (const role of ['engineer', 'devops', 'orchestrator']) {
    assert.equal(rolePredicate('review', role), false, `${role} must be denied admission to review`)
  }
  assert.equal(memberBinding('Manager', rolePermissions('manager'), 'review'), 'review')
  assert.equal(memberBinding('Plan', rolePermissions('plan'), 'review'), 'review')
  assert.equal(memberBinding('Orchestrator', rolePermissions('orchestrator'), 'review'), undefined)
})
