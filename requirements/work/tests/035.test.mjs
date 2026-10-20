import assert from 'node:assert/strict'
import test from 'node:test'
import { execFileSync } from 'node:child_process'
import { mkdtempSync, realpathSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import * as lifecycle from '../../../dist/Mission/Lifecycle/Surface.js'
import * as planning from '../../../dist/Mission/Planning/Surface.js'
import * as workspace from '../../../dist/OpenCode/Host/WorkspaceEventStoreSurface.js'
import * as journalSurface from '../../../dist/Persistence/Journal/Surface.js'

const surface = lifecycle.LifecycleSurface ?? lifecycle

const framesOf = (text) => text.split('\n').filter((line) => line.length > 0).map((line) => JSON.parse(line))
const eventsOf = (text) => framesOf(text).flatMap((frame) => frame.events ?? [])
const incumbencyIdOf = (text) => [...eventsOf(text)].reverse().find((event) => event.type === 'IncumbencyOpened').incumbencyId

const withStore = async (body) => {
  const root = mkdtempSync(join(tmpdir(), 'wxs-work-035-'))
  execFileSync('git', ['init', '--quiet', root])
  try {
    await body(workspace.acquire(await journalSurface.JournalSurface_initializeStateRoot(root)), root)
  } finally {
    rmSync(root, { recursive: true, force: true })
  }
}

const climb = (workId, relays) => {
  let text = surface.encodeOpening('plan', workId, '/root', 0n)
  for (let step = 0; step < relays; step += 1) {
    const reviewed = surface.reviewProbe(text, incumbencyIdOf(text), 'Promoted')
    const relayed = surface.deliverProbe(`${text}${reviewed.bytes}`, `run-${step}`, `call-${step}`, BigInt((step + 1) * 10))
    text = `${text}${reviewed.bytes}${relayed.bytes}`
  }
  return text
}

test('WHAT[work-035] a delivered document recovers Done and not Review', () => {
  let text = climb('work-rec-1', 2)
  const reviewed = surface.reviewProbe(text, incumbencyIdOf(text), 'Promoted')
  text = `${text}${reviewed.bytes}`
  const planRoot = mkdtempSync(join(tmpdir(), 'wxs-work-035-plan-'))
  let delivered
  let selected
  try {
    const written = planning.PlanningSurface.rewritePlanAtomic(planRoot, 'work-rec-1', '# plan\n')
    assert.equal(written.ok, true, written.error)
    selected = planning.PlanningSurface.readAndValidatePlan(planRoot, 'work-rec-1')
    assert.equal(selected.ok, true, selected.error)
    delivered = surface.deliverStateProbe(text, 'run-final', 'call-final', 30n, { digest: selected.digest, path: selected.path })
  } finally {
    rmSync(planRoot, { recursive: true, force: true })
  }
  assert.equal(delivered.decision, 'Write', delivered.detail)
  text = `${text}${delivered.bytes}`

  const read = surface.readProbe(text)
  assert.equal(read.ok, true)
  assert.equal(read.next, 'Done')
  assert.equal(read.activeId, '')
  assert.notEqual(read.next, 'Review')
  const view = surface.documentView(text)
  assert.equal(view.delivered, true)
  assert.equal(view.active, false)
  assert.equal(view.digest, selected.digest)
  assert.equal(view.path, selected.path)
  assert.equal(view.projection, 'S3')
})

test('WHAT[work-035] an active work rebinds the incumbency and stage; draft text does not choose the stage', async () => {
  await withStore(async (store, root) => {
    const opened = await surface.seedOpen(store, 'plan', 'work-rec-2', root, 7n)
    assert.equal(opened.ok, true, opened.error)
    const reviewed = await surface.seedReview(store, 'work-rec-2', 'Promoted')
    assert.equal(reviewed.ok, true, reviewed.error)
    const relayed = await surface.seedDeliver(store, 'work-rec-2', 'run-2', 'call-2', 11n)
    assert.equal(relayed.ok, true, relayed.error)

    const written = planning.PlanningSurface.rewritePlanAtomic(root, 'work-rec-2', '# Stage S3 - Final Deliver - Finished Work')
    assert.equal(written.ok, true, written.error)
    const stored = surface.workView(store, 'work-rec-2')
    assert.equal(stored.found, true)
    assert.equal(stored.active, true)
    assert.equal(stored.sequence, 'S1,S2,S3')
    assert.equal(stored.delivered, false)

    const bytes = surface.encodeOpening('plan', 'work-rec-2', root, 7n)
    const promoted = surface.reviewProbe(bytes, incumbencyIdOf(bytes), 'Promoted')
    const successor = surface.deliverProbe(`${bytes}${promoted.bytes}`, 'run-2', 'call-2', 11n)
    const folded = surface.readProbe(`${bytes}${promoted.bytes}${successor.bytes}`)
    assert.equal(folded.ok, true)
    assert.equal(folded.next, 'Review')
    assert.equal(folded.activeId.length > 0, true)
    const view = surface.documentView(`${bytes}${promoted.bytes}${successor.bytes}`)
    assert.equal(view.activeId.length > 0, true)
    assert.equal(view.openingStage, 'S2', 'recovery reads the folded stage, not the draft text')
    assert.equal(view.authorized, false)
    assert.equal(planning.PlanningSurface.readPlan(root, 'work-rec-2').ok, true)
  })
})

test('WHAT[work-035] a missing draft stays Review and is not read as delivered', async () => {
  await withStore(async (store, root) => {
    const opened = await surface.seedOpen(store, 'plan', 'work-rec-3', root, 1n)
    assert.equal(opened.ok, true, opened.error)
    const missing = planning.PlanningSurface.readPlan(root, 'work-rec-3')
    assert.equal(missing.ok, false)
    const stored = surface.workView(store, 'work-rec-3')
    assert.equal(stored.active, true)
    assert.equal(stored.delivered, false)
    const folded = surface.readProbe(surface.encodeOpening('plan', 'work-rec-3', root, 1n))
    assert.equal(folded.ok, true)
    assert.equal(folded.next, 'Review')
    assert.equal(folded.activeId.length > 0, true)
    assert.notEqual(folded.next, 'Done')
    assert.equal(surface.documentView(surface.encodeOpening('plan', 'work-rec-3', root, 1n)).openingStage, 'S1')
  })
})

test('WHAT[work-035] a missing work is not opened and does not guess', async () => {
  await withStore(async (store) => {
    const missing = surface.workView(store, 'work-rec-4')
    assert.equal(missing.found, false)
    assert.equal(missing.active, false)
    assert.equal(missing.delivered, false)
    assert.equal(missing.sequence, '')
  })
  const rejected = surface.readProbe('\n')
  assert.equal(rejected.ok, true)
  assert.equal(rejected.opened, false, 'a newline with no WorkOpened is not a guessed opening')
  assert.equal(rejected.next, 'Open')
  assert.equal(rejected.names, '')
  assert.equal(rejected.activeId, '')
  assert.notEqual(rejected.next, 'Done')
  assert.notEqual(rejected.next, 'Review')
})

test('WHAT[work-035] a second opening with the same request id and different bytes is refused', () => {
  let text = climb('work-rec-5', 2)
  const reviewed = surface.reviewProbe(text, incumbencyIdOf(text), 'Promoted')
  const planRoot = mkdtempSync(join(tmpdir(), 'wxs-work-035-conflict-'))
  let delivered
  try {
    const written = planning.PlanningSurface.rewritePlanAtomic(planRoot, 'work-rec-5', '# plan\n')
    assert.equal(written.ok, true, written.error)
    const selected = planning.PlanningSurface.readAndValidatePlan(planRoot, 'work-rec-5')
    assert.equal(selected.ok, true, selected.error)
    delivered = surface.deliverStateProbe(`${text}${reviewed.bytes}`, 'run-5', 'call-5', 30n, { digest: selected.digest, path: selected.path })
  } finally {
    rmSync(planRoot, { recursive: true, force: true })
  }
  text = `${text}${reviewed.bytes}${delivered.bytes}`
  assert.equal(surface.readProbe(text).next, 'Done')

  const forged = `${text}${surface.encodeOpening('plan', 'work-rec-5', '/root', 40n)}`
  const conflict = surface.readProbe(forged)
  assert.equal(conflict.ok, false, 'the conflicting replay must be refused')
  assert.equal(conflict.opened, false)
  assert.equal(conflict.next, '')
  assert.equal(conflict.activeId, '')
  assert.match(conflict.reason, /RequestIdConflict/)
})

test('WHAT[work-035] recovery effects keep Done and Review for an open incumbency while a missing id stays unopened', async () => {
  const effects = []
  let delivered = climb('work-batch-deliv', 2)
  const reviewed = surface.reviewProbe(delivered, incumbencyIdOf(delivered), 'Promoted')
  const planRoot = mkdtempSync(join(tmpdir(), 'wxs-work-035-effects-'))
  let receipt
  try {
    const written = planning.PlanningSurface.rewritePlanAtomic(planRoot, 'work-batch-deliv', '# plan\n')
    assert.equal(written.ok, true, written.error)
    const selected = planning.PlanningSurface.readAndValidatePlan(planRoot, 'work-batch-deliv')
    assert.equal(selected.ok, true, selected.error)
    receipt = surface.deliverStateProbe(`${delivered}${reviewed.bytes}`, 'run-b', 'call-b', 30n, { digest: selected.digest, path: selected.path })
  } finally {
    rmSync(planRoot, { recursive: true, force: true })
  }
  delivered = `${delivered}${reviewed.bytes}${receipt.bytes}`
  effects.push(surface.readProbe(delivered))
  effects.push(surface.readProbe(climb('work-batch-active', 2)))
  assert.equal(effects.length, 2)
  assert.equal(effects[0].next, 'Done')
  assert.equal(effects[0].activeId, '')
  assert.equal(effects[1].next, 'Review')
  assert.equal(effects[1].activeId.length > 0, true)
  assert.equal(surface.documentView(delivered).workId, 'work-batch-deliv')
  assert.equal(surface.documentView(climb('work-batch-active', 2)).workId, 'work-batch-active')

  await withStore(async (store) => {
    const missing = surface.workView(store, '')
    assert.equal(missing.found, false)
  })
})

test('WHAT[work-035] S4 recovery reads confirmation, relay and delivery from the fold, and a reopened delivered tenure is active again', async () => {
  await withStore(async (store, root) => {
    const commonDir = await journalSurface.JournalSurface_initializeStateRoot(root)
    const workId = 'work-rec-s4-successor'
    const accepted = ['Committed', 'Replayed', 'Observed']
    const commit = (requestId, command) => {
      const receipt = surface.execute(commonDir, workId, requestId, command)
      assert.ok(accepted.includes(receipt.Status), JSON.stringify(receipt))
      return receipt
    }
    const activeId = () => surface.inspected(commonDir, workId).ActiveId
    const review = (requestId, promoted) =>
      commit(requestId, {
        type: 'Review',
        incumbencyId: activeId(),
        reviewerId: 'reviewer',
        findings: promoted ? [] : ['gap'],
      })
    const deliver = (requestId, providerRunId, toolCallId, cursor) =>
      commit(requestId, {
        type: 'Deliver',
        incumbencyId: activeId(),
        providerRunId,
        toolCallId,
        cursor,
        blockers: [],
        nextOwnerId: workId,
      })

    commit('open', {
      type: 'Open',
      root,
      sequence: ['S4'],
      ownerId: workId,
      openingCursor: 0,
    })
    review('review-1', false)
    const confirmed = deliver('confirm-1', 'run-1', 'call-1', 1)
    assert.equal(confirmed.Kind, 'Confirmed')
    const confirmedWork = surface.inspected(commonDir, workId)
    assert.equal(confirmedWork.Delivered, false, 'a recorded confirmation is not a retirement')
    assert.equal(Array.from(confirmedWork.Retirements).length, 0)
    assert.notEqual(confirmedWork.ActiveId, undefined)
    const relay = deliver('relay-1', 'run-1', 'call-2', 2)
    assert.equal(relay.Kind, 'Relayed')
    const successor = surface.inspected(commonDir, workId)
    assert.equal(successor.Reviewed, false)
    assert.equal(Array.from(successor.Retirements).at(-1).Outcome, 'Relayed')
    assert.notEqual(successor.ActiveId, undefined, 'a relay leaves its successor active')

    review('review-2', true)
    deliver('confirm-2', 'run-2', 'call-3', 3)
    const closed = deliver('close-2', 'run-2', 'call-4', 4)
    assert.equal(closed.Kind, 'Delivered')
    const delivered = surface.inspected(commonDir, workId)
    assert.equal(delivered.Delivered, true)
    assert.equal(Array.from(delivered.Retirements).at(-1).Outcome, 'Delivered')

    const retired = Array.from(delivered.Retirements).at(-1)
    const reopened = commit('reopen', {
      type: 'Reopen',
      retirementId: retired.RetirementId,
      ownerId: workId,
      openingCursor: 5,
      blockers: [],
    })
    assert.equal(reopened.Kind, 'Reopened')
    const rebound = surface.inspected(commonDir, workId)
    assert.notEqual(rebound.ActiveId, undefined)
    assert.equal(rebound.Reviewed, false)
    assert.equal(rebound.Delivered, false, 'reopen clears the delivered read')
    assert.equal(Array.from(rebound.Retirements).at(-1).Outcome, 'Delivered')
    assert.notEqual(rebound.ActiveId, retired.IncumbencyId, 'reopen opens a tenure, it revives none')
    assert.equal(surface.workView(store, workId).active, true)
  })
})
