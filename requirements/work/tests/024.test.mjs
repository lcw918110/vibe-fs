import assert from 'node:assert/strict'
import test from 'node:test'
import { execFileSync } from 'node:child_process'
import { mkdtempSync, realpathSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import * as lifecycle from '../../../dist/Mission/Lifecycle/Surface.js'
import * as planning from '../../../dist/Mission/Planning/Surface.js'
import { runJsPlan } from '../../../dist/OpenCode/Tools/ToolRegistrySurface.js'
import { rolePermissionRules } from '../../../dist/OpenCode/Tools/ToolSurface.js'
import * as workspace from '../../../dist/OpenCode/Host/WorkspaceEventStoreSurface.js'
import * as journalSurface from '../../../dist/Persistence/Journal/Surface.js'

const surface = lifecycle.LifecycleSurface ?? lifecycle

const framesOf = (text) => text.split('\n').filter((line) => line.length > 0).map((line) => JSON.parse(line))
const eventsOf = (text) => framesOf(text).flatMap((frame) => frame.events ?? [])
const incumbencyIdOf = (text) => [...eventsOf(text)].reverse().find((event) => event.type === 'IncumbencyOpened').incumbencyId

const withStore = async (body) => {
  const root = mkdtempSync(join(tmpdir(), 'wxs-work-024-'))
  execFileSync('git', ['init', '--quiet', root])
  try {
    await body(workspace.acquire(await journalSurface.JournalSurface_initializeStateRoot(root)), root)
  } finally {
    rmSync(root, { recursive: true, force: true })
  }
}

test('WHAT[work-024] Plan admits its five role tools on every stage; handoff and resume stay denied', () => {
  const rules = rolePermissionRules('plan')
  for (const tool of ['todowrite-for-plan', 'devops', 'ask', 'review', 'deliver']) {
    assert.equal(rules[tool], 'allow', `plan must admit ${tool}`)
    assert.notEqual(rolePermissionRules('engineer')[tool], 'allow', `engineer must not admit ${tool}`)
  }
  for (const tool of ['handoff', 'resume', 'read', 'edit', 'write']) {
    assert.notEqual(rules[tool], 'allow', `plan must deny ${tool}`)
  }
  // Stage is not an action gate: S1, S2 and S3 share the same admission.
  for (const stage of ['S1', 'S2', 'S3']) {
    assert.equal(rules.deliver, 'allow', stage)
    assert.notEqual(rules.handoff, 'allow', stage)
  }
})

test('WHAT[work-024] an unreviewed tenure may read the draft and must refuse a write', async () => {
  await withStore(async (store, root) => {
    const opened = await surface.seedOpen(store, 'plan', 'work-24', root, 0n)
    assert.equal(opened.ok, true, opened.error)
    const before = surface.workView(store, 'work-24')
    assert.equal(before.reviewed, '')

    const read = await runJsPlan(store.gitCommonDir, root, 'work-24', `class Js extends JsProgram {
      async run() {
        const file = await this.file([]);
        return { text: file.text() };
      }
    }`)
    assert.match(read, /# ok/)

    const write = await runJsPlan(store.gitCommonDir, root, 'work-24', `class Js extends JsProgram {
      async run() {
        this.rewrite('# mutated before review');
        return { written: true };
      }
    }`)
    assert.match(write, /# failed/)
    assert.match(write, /PLAN_REVIEW_REQUIRED/)
    assert.equal(surface.workView(store, 'work-24').reviewed, '', 'a refused write leaves the tenure unreviewed')
  })
})

test('WHAT[work-024] a reviewed tenure may write; a second review and a delivered tenure stay refused', async () => {
  await withStore(async (store, root) => {
    const seeded = await surface.seedOpen(store, 'plan', 'work-24b', root, 0n)
    assert.equal(seeded.ok, true, seeded.error)
    const reviewed = await surface.seedReview(store, 'work-24b', 'Stayed')
    assert.equal(reviewed.ok, true, reviewed.error)

    const write = await runJsPlan(store.gitCommonDir, root, 'work-24b', `class Js extends JsProgram {
      async run() {
        this.rewrite('# written after review');
        return { written: true };
      }
    }`)
    assert.match(write, /# ok/)

    const again = await surface.seedReview(store, 'work-24b', 'Promoted')
    assert.equal(again.ok, false, 'a tenure carries one review')
    assert.equal(again.error, 'RequestIdConflict')
    assert.equal(surface.workView(store, 'work-24b').reviewed, 'Stayed')
  })

  const opened = surface.encodeOpening('plan', 'work-24c', '/root', 0n)
  let text = opened
  for (let step = 0; step < 2; step += 1) {
    const reviewed = surface.reviewProbe(text, incumbencyIdOf(text), 'Promoted')
    const relayed = surface.deliverProbe(`${text}${reviewed.bytes}`, `run-${step}`, `call-${step}`, BigInt((step + 1) * 10))
    text = `${text}${reviewed.bytes}${relayed.bytes}`
  }
  const reviewed = surface.reviewProbe(text, incumbencyIdOf(text), 'Promoted')
  text = `${text}${reviewed.bytes}`
  const root = mkdtempSync(join(tmpdir(), 'wxs-work-024-draft-'))
  let delivered
  try {
    const written = planning.PlanningSurface.rewritePlanAtomic(root, 'work-24c', '# plan\n')
    assert.equal(written.ok, true, written.error)
    const selected = planning.PlanningSurface.readAndValidatePlan(root, 'work-24c')
    assert.equal(selected.ok, true, selected.error)
    delivered = surface.deliverStateProbe(text, 'run-final', 'call-final', 30n, { digest: selected.digest, path: selected.path })
  } finally {
    rmSync(root, { recursive: true, force: true })
  }
  text = `${text}${delivered.bytes}`
  const view = surface.documentView(text)
  assert.equal(view.delivered, true)
  assert.equal(view.active, false, 'a delivered work has no active tenure to admit a tool against')
  const lateReview = surface.reviewProbe(text, view.activeId || 'missing', 'Promoted')
  assert.equal(lateReview.decision, 'Refuse')
  const lateAsk = surface.reopenProbe(text, 'not-the-delivered-incumbency')
  assert.equal(lateAsk.decision, 'Refuse', 'a delivered work does not admit a new action until Reopened')
  assert.deepEqual(surface.documentView(text), view)
})

test('WHAT[work-024] deliver refuses a blocker and an unauthorized tenure relays rather than delivers', () => {
  const opened = surface.encodeOpening('plan', 'work-24d', '/root', 0n)
  const blocked = surface.deliverProbe(opened, 'run-blocked', 'call-blocked', 9n)
  assert.equal(blocked.decision, 'Write', blocked.detail)
  assert.equal(blocked.detail, 'Relayed', 'an unauthorized tenure relays; stage does not change that')
  assert.equal(blocked.names, 'IncumbencyRetired,IncumbencyOpened')
  assert.equal(eventsOf(blocked.bytes)[0].outcome, 'Relayed')

  const reviewed = surface.reviewProbe(opened, incumbencyIdOf(opened), 'Stayed')
  const stayed = surface.deliverProbe(`${opened}${reviewed.bytes}`, 'run-stay', 'call-stay', 11n)
  assert.equal(stayed.decision, 'Write', stayed.detail)
  assert.equal(stayed.detail, 'Relayed', 'Stayed keeps the unauthorized relay path on every stage')
  assert.equal(surface.documentView(`${opened}${reviewed.bytes}${stayed.bytes}`).projection, 'S1')
})
