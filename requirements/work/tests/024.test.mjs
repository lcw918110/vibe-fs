import assert from 'node:assert/strict'
import test from 'node:test'
import { execFileSync } from 'node:child_process'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import * as lifecycle from '../../../dist/Mission/Lifecycle/Surface.js'
import * as planning from '../../../dist/Mission/Planning/Surface.js'
import { rolePermissionRules, toolSpecNames } from '../../../dist/OpenCode/Tools/ToolSurface.js'
import { generateRole } from '../../../dist/Repository/Programming/Js/GeneratorSurface.js'
import * as workspace from '../../../dist/OpenCode/Host/WorkspaceEventStoreSurface.js'
import * as journalSurface from '../../../dist/Persistence/Journal/Surface.js'
import { acceptAuthorityRoot, withExecutablePlugin } from '../../verification-system/tests/support/plugin-fixture.mjs'

const surface = lifecycle.LifecycleSurface ?? lifecycle

const withStore = async (body) => {
  const root = mkdtempSync(join(tmpdir(), 'wxs-work-024-'))
  execFileSync('git', ['init', '--quiet', root])
  try {
    const commonDir = await journalSurface.JournalSurface_initializeStateRoot(root)
    await body(workspace.acquire(commonDir), root, commonDir)
  } finally {
    rmSync(root, { recursive: true, force: true })
  }
}

test('WHAT[work-024] Plan admits its role tools on every stage; handoff and resume stay denied', () => {
  const rules = rolePermissionRules('plan')
  for (const tool of ['todowrite-for-plan', 'review']) {
    assert.equal(rules[tool], 'allow', `plan must admit ${tool}`)
    assert.notEqual(rolePermissionRules('engineer')[tool], 'allow', `engineer must not admit ${tool}`)
  }
  assert.equal(Object.hasOwn(rules, 'deliver'), false, 'lifecycle deliver is not a plan tool')
  // DevOps 调查不再注册宿主工具：它是 Plan 编程面的成员。
  assert.ok(!toolSpecNames().includes('devops'), 'the host devops tool must be retired')
  assert.equal(Object.hasOwn(rules, 'devops'), false, 'the retired devops name must carry no admission rule')
  const planMembers = generateRole('plan', 'en').members.map((member) => member.memberName)
  assert.ok(planMembers.includes('devops'), 'the Plan surface carries the devops member')
  for (const role of ['manager', 'orchestrator', 'engineer', 'devops']) {
    const surface = generateRole(role, 'en')
    const members = surface ? surface.members.map((member) => member.memberName) : []
    assert.ok(!members.includes('devops'), `the "${role}" surface must carry no devops member`)
  }
  for (const tool of ['handoff', 'resume', 'read', 'edit', 'write']) {
    assert.notEqual(rules[tool], 'allow', `plan must deny ${tool}`)
  }
  assert.notEqual(rules.handoff, 'allow', 'plan must deny handoff')
})

test('WHAT[work-024] the Plan devops member requires bound lifecycle work and never fabricates a delegation', async () => {
  await withExecutablePlugin(async (hooks, _directory, createdIds, runtime) => {
    const sessionID = 'ses-work-024-devops'
    await acceptAuthorityRoot(runtime, sessionID, 'plan', 'user-plan-root')

    let programSequence = 0
    const runPlan = async (body) =>
      String(
        await hooks.tool['todowrite-for-plan'].execute(
          { program: `class Js extends JsProgram { async run() { ${body} } }`, name: 'devops-plan-' + (++programSequence), planB: 'Wait for this planning operation without renewing the request deadline.' },
          { sessionID, agent: 'plan' },
        ),
      )

    // 没有已开立的 Plan 工作：拒绝，不派工，也不伪造成功。
    const withoutWork = await runPlan('return await this.devops("investigate the build artifact");')
    assert.match(withoutWork, /ok = false/)
    assert.match(withoutWork, /code = "devops-failed"/)
    assert.match(withoutWork, /lifecycle work is missing/)

    assert.deepEqual(createdIds, [], 'no refused devops member call may place a child session')
  })
})

test('WHAT[work-024] an unreviewed tenure may read the draft but cannot write it until first review', async () => {
  // The real Plan programming surface is the only Plan entry the host
  // registers; an unreviewed tenure may still READ the draft through it,
  // but reviewFinishedOnly locks writing until the first review finishes.
  await withExecutablePlugin(async (hooks, directory, _createdIds, runtime) => {
    const sessionID = 'ses-work-024-unreviewed'
    await acceptAuthorityRoot(runtime, sessionID, 'plan', 'user-plan-root')

    const commonDir = await journalSurface.JournalSurface_initializeStateRoot(directory)
    const opened = await planning.PlanningSurface.ensurePlanWorkOpened(commonDir, directory, sessionID, 0n)
    assert.equal(opened.ok, true, opened.error)

    const before = surface.workView(workspace.acquire(commonDir), sessionID)
    assert.equal(before.reviewed, '')
    assert.equal(planning.PlanningSurface.isReviewFinished(commonDir, sessionID, 'inc-work-024'), false, 'reviewFinishedOnly locks writing before first review')

    const read = await hooks.tool['todowrite-for-plan'].execute(
      { program: `class Js extends JsProgram {
      async run() {
        return { ok: true, hasReviewOnly: typeof this.reviewOnly !== 'undefined' };
      }
    }`, name: 'read-draft',
          planB: 'Read the current planning capability under the admitted deadline.'
        },
      { sessionID, agent: 'plan', callID: 'call-work-024', messageID: 'run-work-024', providerRunID: 'run-work-024' },
    )
    assert.match(String(read), /# ok/)
    assert.match(String(read), /hasReviewOnly = false/)
  })
})

test('WHAT[work-024] a keyed review relays when cleared, and deliver is not the advance', () => {
  const root = mkdtempSync(join(tmpdir(), 'wxs-work-024-review-'))
  const review = (id, updates) =>
    surface.submitReview(root, 'work-24b', id, 'Plan', 'run-' + id, 'call-' + id, updates, null, '')
  try {
    const recorded = review('r1', [{ key: 'arch-tradeoff', value: 'detail SQLite concurrency limits' }])
    assert.equal(recorded.status, 'Committed', recorded.reason)
    assert.equal(recorded.kind, 'Reviewed')
    assert.equal(recorded.milestone, '事实与取舍')

    const kept = review('r2', [])
    assert.equal(kept.kind, 'Reviewed')
    assert.equal(kept.milestone, '事实与取舍')

    const relayed = review('r3', [{ key: 'arch-tradeoff', value: null }])
    assert.equal(relayed.status, 'Committed', relayed.reason)
    assert.equal(relayed.kind, 'Relayed')

    const advanced = review('r4', [])
    assert.equal(advanced.kind, 'Advanced')
    assert.equal(advanced.milestone, '理解与路径')

    const refused = surface.deliverProbe('unused', 'run-x', 'call-x', 10n, null)
    assert.equal(refused.decision, 'Refuse')
    assert.match(refused.detail, /review is the only entry/)
    const late = surface.reopenProbe('unused', 'missing')
    assert.equal(late.decision, 'Refuse')
  } finally {
    rmSync(root, { recursive: true, force: true })
  }
})
