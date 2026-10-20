import assert from 'node:assert/strict'
import { execFileSync } from 'node:child_process'
import { mkdirSync, mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import test from 'node:test'
import * as lifecycle from '../../../dist/Mission/Lifecycle/Surface.js'
import * as surface from '../../../dist/Mission/Planning/Surface.js'
import * as workspace from '../../../dist/OpenCode/Host/WorkspaceEventStoreSurface.js'
import * as journalSurface from '../../../dist/Persistence/Journal/Surface.js'

const life = lifecycle.LifecycleSurface ?? lifecycle

const framesOf = (text) => text.split('\n').filter((line) => line.length > 0).map((line) => JSON.parse(line))
const eventsOf = (text) => framesOf(text).flatMap((frame) => frame.events ?? [])

const withStore = async (body) => {
  const root = mkdtempSync(join(tmpdir(), 'wxs-planning-007-'))
  const commonDir = join(root, '.Herdchestra')
  mkdirSync(commonDir, { recursive: true })
  try {
    await body(workspace.acquire(commonDir), root, commonDir)
  } finally {
    rmSync(root, { recursive: true, force: true })
  }
}

const openedActiveId = (text) => {
  const folded = life.readProbe(text)
  assert.equal(folded.ok, true, folded.reason)
  assert.notEqual(folded.activeId, '')
  return folded.activeId
}

const openWork = async (store, commonDir, workId) => {
  const opened = await surface.PlanningSurface.ensurePlanWorkOpened(commonDir, commonDir, workId, 0n)
  assert.equal(opened.ok, true, opened.error)
  return openedActiveId(life.encodeOpening('plan', workId, commonDir, 0n))
}

const forbiddenExecution = () => ({
  create: async () => { throw new Error('execution create must not be called') },
  prompt: async () => { throw new Error('execution prompt must not be called') },
  discard: async () => {},
})

test('WHAT[work-025] deliver without authorization relays: retires Relayed and opens the next tenure at the same stage', async () => {
  const text = life.encodeOpening('plan', 'work-h1', '/repo/root', 0n)
  const promoted = life.reviewProbe(text, openedActiveId(text), 'Promoted')
  assert.equal(promoted.decision, 'Write')
  assert.equal(promoted.names, 'Reviewed')
  const relayed = life.deliverProbe(`${text}${promoted.bytes}`, 'run-1', 'call-1', 10n)
  assert.equal(relayed.decision, 'Write')
  assert.equal(relayed.detail, 'Relayed')
  assert.equal(relayed.names, 'IncumbencyRetired,IncumbencyOpened')
  assert.equal(relayed.bytes.includes('ConfirmationCommitted'), false)
  assert.equal(relayed.bytes.includes('Delivered'), false)

  const folded = life.readProbe(`${text}${promoted.bytes}${relayed.bytes}`)
  assert.equal(folded.ok, true)
  assert.equal(folded.next, 'Review')
  assert.equal(folded.activeId.length > 0, true)
  const written = eventsOf(relayed.bytes)
  assert.equal(written[0].outcome, 'Relayed')
  assert.equal(written[1].stage, 'S2', 'relay keeps the promoted stage')
  assert.equal(written[1].openingCursor, written[0].retirementCursor)
})

test('WHAT[work-025] an unauthorised durable deliver relays instead of refusing for a missing next owner', async () => {
  const root = mkdtempSync(join(tmpdir(), 'wxs-planning-025-relay-'))
  execFileSync('git', ['init', '--quiet', root])
  const commonDir = await journalSurface.JournalSurface_initializeStateRoot(root)
  try {
    const opened = await surface.PlanningSurface.ensurePlanWorkOpened(commonDir, root, 'work-relay-owner', 0n)
    assert.equal(opened.ok, true, opened.error)
    const store = workspace.acquire(commonDir)
    const view = life.workView(store, 'work-relay-owner')
    assert.equal(view.found, true)
    const reviewed = await life.seedReview(store, 'work-relay-owner', 'Stayed')
    assert.equal(reviewed.ok, true, reviewed.error)
    const host = await import('../../../dist/Mission/Lifecycle/DurableWorkHost.js')
    const inspected = (host.inspected ?? host.DurableWorkHost?.inspected)(commonDir, 'work-relay-owner')
    const incumbencyId = inspected.ActiveId
    const result = await surface.PlanningSurface.executeDeliver(
      commonDir, root, 'work-relay-owner', incumbencyId, 'run-relay', 'call-relay', false,
      forbiddenExecution(),
      4n,
    )
    assert.equal(result.ok, true, result.error)
    assert.equal(result.relayed, true)
    const after = (host.inspected ?? host.DurableWorkHost?.inspected)(commonDir, 'work-relay-owner')
    assert.notEqual(after.ActiveId, incumbencyId)
    assert.equal(after.Stage, 'S1')
  } finally {
    rmSync(root, { recursive: true, force: true })
  }
})

test('WHAT[work-025] continuous reviewed relay advances tenures at the same stage without spawning', () => {
  let text = life.encodeOpening('plan', 'work-ladder', '/repo/root', 0n)
  let cursor = 0n
  for (let step = 0; step < 3; step += 1) {
    cursor += 10n
    const reviewed = life.reviewProbe(text, openedActiveId(text), 'Stayed')
    assert.equal(reviewed.decision, 'Write', reviewed.detail)
    text = `${text}${reviewed.bytes}`
    const relayed = life.deliverProbe(text, `run-ladder-${step}`, `call-ladder-${step}`, cursor)
    assert.equal(relayed.decision, 'Write', relayed.detail)
    assert.equal(relayed.detail, 'Relayed')
    assert.equal(relayed.names, 'IncumbencyRetired,IncumbencyOpened')
    const [retired, opened] = eventsOf(relayed.bytes)
    assert.equal(retired.outcome, 'Relayed')
    assert.equal(opened.stage, 'S1', 'a stayed review keeps the stage')
    assert.equal(opened.openingCursor, retired.retirementCursor)
    assert.equal(opened.openingCursor, Number(cursor))
    text = `${text}${relayed.bytes}`
    const folded = life.readProbe(text)
    assert.equal(folded.ok, true, folded.reason)
    assert.equal(folded.activeId, opened.incumbencyId)
  }
})

test('WHAT[work-025] a second consecutive unreviewed relay is refused with no write, and a review reopens the handoff', () => {
  let text = life.encodeOpening('plan', 'work-noop', '/repo/root', 0n)
  const promoted = life.reviewProbe(text, openedActiveId(text), 'Promoted')
  assert.equal(promoted.decision, 'Write', promoted.detail)
  text = `${text}${promoted.bytes}`
  // First relay: the reviewed tenure hands off; the chain is not yet unreviewed.
  const first = life.deliverProbe(text, 'run-1', 'call-1', 10n)
  assert.equal(first.detail, 'Relayed', first.detail)
  text = `${text}${first.bytes}`
  // Second relay: an unreviewed successor, but the prior handoff was reviewed. Allowed once.
  const second = life.deliverProbe(text, 'run-2', 'call-2', 20n)
  assert.equal(second.detail, 'Relayed', second.detail)
  text = `${text}${second.bytes}`
  // Third relay: two consecutive unreviewed handoffs is the no-op chain. Refused, zero write.
  const activeBefore = openedActiveId(text)
  const refused = life.deliverProbe(text, 'run-3', 'call-3', 30n)
  assert.equal(refused.decision, 'Done', refused.detail)
  assert.equal(refused.detail, 'ReviewRequired')
  assert.equal(refused.bytes, '', 'the refusal appends nothing')
  const afterRefusal = life.readProbe(text)
  assert.equal(afterRefusal.activeId, activeBefore, 'the tenure is not retired')
  // The runner submits its review; the handoff resumes at the same stage.
  const reviewed = life.reviewProbe(text, activeBefore, 'Stayed')
  text = `${text}${reviewed.bytes}`
  const resumed = life.deliverProbe(text, 'run-4', 'call-4', 40n)
  assert.equal(resumed.detail, 'Relayed', resumed.detail)
})

test('WHAT[work-025] relay is blocked by live resources or a wrong incumbency id', async () => {
  await withStore(async (store, root, commonDir) => {
    const activeId = await openWork(store, commonDir, 'work-h2')

    const blocked = await surface.PlanningSurface.executeDeliver(
      commonDir, root, 'work-h2', activeId, 'run-b', 'call-b', true,
      forbiddenExecution(),
      10n,
    )
    assert.equal(blocked.ok, true, blocked.error)
    assert.equal(blocked.relayed, false)
    assert.equal(blocked.delivered, false)
    assert.equal(blocked.retirementOutcome, 'Blocked')
    assert.match(blocked.message, /retirement-blocked/, 'a blocked retirement returns the live resources the runner must clear')
    const recorded = life.inspected(commonDir, 'work-h2')
    assert.equal(recorded.Delivered, false, 'a blocked retirement must not retire the tenure')
    assert.ok(recorded.BlockerDigest, 'the blockage is a written RetirementBlocked fact, not only a reply string')
    assert.equal(recorded.ActiveId, activeId, 'the blocked tenure stays active for the next attempt')

    const wrong = await surface.PlanningSurface.executeDeliver(
      commonDir, root, 'work-h2', 'wrong-inc', 'run-w', 'call-w', false,
      forbiddenExecution(),
      10n,
    )
    assert.equal(wrong.ok, false)
    assert.match(wrong.error, /does not match invocation target/)
  })
})

test('WHAT[work-025] missing retirement evidence causes no spawn and no Plan writes', async () => {
  await withStore(async (store, root, commonDir) => {
    const activeId = await openWork(store, commonDir, 'work-h3')
    let spawnCalls = 0
    const res = await surface.PlanningSurface.executeDeliver(
      commonDir, root, 'work-h3', activeId, 'run-3', 'call-3', false,
      {
        create: async () => { spawnCalls += 1; return 'exec-never' },
        prompt: async () => { spawnCalls += 1; return { kind: 'Started' } },
        discard: async () => {},
      },
      undefined,
    )
    assert.equal(res.ok, false)
    assert.match(res.error, /retirement cursor evidence is required/i)
    assert.equal(spawnCalls, 0)
  })
})

test('WHAT[work-025] the first successor cursor opens at the exact consumed-through retirement sequence, not +1', async () => {
  const text = life.encodeOpening('plan', 'work-h4', '/repo/root', 0n)
  const relayed = life.deliverProbe(text, 'run-4', 'call-4', 10n)
  assert.equal(relayed.decision, 'Write')
  const [retired, opened] = eventsOf(relayed.bytes)
  assert.equal(retired.type, 'IncumbencyRetired')
  assert.equal(retired.outcome, 'Relayed')
  assert.equal(retired.retirementCursor, 10)
  assert.equal(opened.type, 'IncumbencyOpened')
  assert.equal(opened.openingCursor, 10)
  assert.notEqual(opened.openingCursor, 11)
  assert.notEqual(opened.openingCursor, 20)
})
