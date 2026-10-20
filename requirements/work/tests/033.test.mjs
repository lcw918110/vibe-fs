import assert from 'node:assert/strict'
import { execFileSync } from 'node:child_process'
import { mkdirSync, mkdtempSync, realpathSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { DatabaseSync } from 'node:sqlite'
import test from 'node:test'
import { SqliteJournal } from 'wanxiangli/eventstore'
import { DurableWork } from 'wanxiangli/durable-work'
import { Work } from 'wanxiangli/work'
import * as lifecycle from '../../../dist/Mission/Lifecycle/Surface.js'
import * as planning from '../../../dist/Mission/Planning/Surface.js'
import * as workspace from '../../../dist/OpenCode/Host/WorkspaceEventStoreSurface.js'
import * as journalSurface from '../../../dist/Persistence/Journal/Surface.js'

const surface = lifecycle.LifecycleSurface ?? lifecycle

const framesOf = (text) => text.split('\n').filter((line) => line.length > 0).map((line) => JSON.parse(line))
const eventsOf = (text) => framesOf(text).flatMap((frame) => frame.events ?? [])
const incumbencyIdOf = (text) => [...eventsOf(text)].reverse().find((event) => event.type === 'IncumbencyOpened').incumbencyId
const sorted = (keys) => [...keys].sort((left, right) => (left < right ? -1 : left > right ? 1 : 0))

const withStore = async (body) => {
  const root = mkdtempSync(join(tmpdir(), 'wxs-lifecycle-033-'))
  execFileSync('git', ['init', '--quiet', root])
  try {
    // The journal directory is fixed next to the events dir; tests that open
    // the database directly create it here rather than relying on the host.
    const stateDir = await journalSurface.JournalSurface_initializeStateRoot(root)
    mkdirSync(join(stateDir, 'wanxiangshu'), { recursive: true })
    await body(workspace.acquire(stateDir), root)
  } finally {
    rmSync(root, { recursive: true, force: true })
  }
}

test('WHAT[work-033] one projection uses one record snapshot even when another connection commits between reads', async () => {
  await withStore(async (store, root) => {
    const workId = 'snapshot-race'
    const journal = new SqliteJournal(new DatabaseSync(surface.journalPath(store.gitCommonDir)))
    const originalPrepare = DatabaseSync.prototype.prepare
    try {
      const work = new DurableWork(workId, journal)
      const opened = work.execute('open', {
        type: 'Open', root, sequence: ['S1', 'S2', 'S3'], ownerId: workId, openingCursor: 0,
      })
      assert.equal(opened.status, 'Committed')
      const initialId = opened.result.incumbencyId
      const candidate = new Work(workId, journal.read(workId))
      const relay = candidate.execute('relay', {
        type: 'Deliver', incumbencyId: initialId, providerRunId: 'run', toolCallId: 'call',
        cursor: 1, blockers: [], nextOwnerId: workId,
      })
      assert.equal(relay.status, 'Applied')
      let interleaved = false
      DatabaseSync.prototype.prepare = function (sql) {
        const statement = originalPrepare.call(this, sql)
        if (sql === 'SELECT revision, body, checksum FROM journal_entries WHERE stream=? ORDER BY revision') {
          const all = statement.all.bind(statement)
          statement.all = (...args) => {
            const rows = all(...args)
            if (args[0] === workId && !interleaved) {
              interleaved = true
              assert.equal(journal.append(workId, 1, 'relay', relay.record).status, 'Committed')
            }
            return rows
          }
        }
        return statement
      }
      const captured = surface.inspected(store.gitCommonDir, workId)
      assert.equal(interleaved, true)
      assert.equal(captured.ActiveId, initialId)
      assert.equal(captured.RoundCount, 0)
      assert.equal(Array.from(captured.Retirements).length, 0, 'history cannot come from a newer read than the snapshot')
    } finally {
      DatabaseSync.prototype.prepare = originalPrepare
      journal.close()
    }
    assert.equal(surface.inspected(store.gitCommonDir, workId).RoundCount, 1)
  })
})

test('WHAT[work-033] the fixed DevOps identity and model target remain separate projection fields', async () => {
  await withStore(async (store, root) => {
    const opened = surface.execute(store.gitCommonDir, 'devops-target', 'open', {
      type: 'Open', root, sequence: ['S4'], ownerId: 'manager', openingCursor: 0,
    })
    assert.equal(opened.Status, 'Committed')
    const bound = surface.execute(store.gitCommonDir, 'devops-target', 'bind', {
      type: 'BindDevOps', incumbencyId: opened.Result.incumbencyId, trigger: 'Horizon',
      devopsId: 'devops-identity', target: 'provider/fixed-model',
    })
    assert.equal(bound.Status, 'Committed')
    const observed = surface.inspected(store.gitCommonDir, 'devops-target')
    assert.equal(observed.DevOpsId, 'devops-identity')
    assert.equal(observed.DevOpsTarget, 'provider/fixed-model')
  })
})

test('WHAT[work-033] lifecycle payload keys are ordered by Unicode code point', () => {
  const opened = surface.encodeOpening('plan', 'work-1', '/repo/root', 42n)
  const [workOpened, incumbencyOpened] = eventsOf(opened)
  assert.deepEqual(Object.keys(workOpened), sorted(Object.keys(workOpened)))
  assert.deepEqual(Object.keys(workOpened), ['authorityInitial', 'root', 'sequence', 'type', 'workId'])
  assert.deepEqual(Object.keys(incumbencyOpened), ['incumbencyId', 'openingCursor', 'ownerId', 'stage', 'type', 'workId'])
  assert.equal(incumbencyOpened.openingCursor, 42)

  const reviewed = surface.reviewProbe(opened, incumbencyIdOf(opened), 'Promoted')
  const review = eventsOf(reviewed.bytes)[0]
  assert.deepEqual(Object.keys(review), ['incumbencyId', 'outcome', 'stage', 'type'])

  const relayed = surface.deliverProbe(`${opened}${reviewed.bytes}`, 'run-1', 'call-1', 100n)
  const [retired, successor] = eventsOf(relayed.bytes)
  assert.deepEqual(Object.keys(retired), sorted(Object.keys(retired)))
  assert.equal(retired.outcome, 'Relayed')
  assert.equal(retired.retirementCursor, 100)
  assert.ok(Object.keys(retired).includes('incumbencyId'))
  assert.ok(Object.keys(retired).includes('retirementId'))
  assert.deepEqual(Object.keys(successor), ['incumbencyId', 'openingCursor', 'ownerId', 'stage', 'type', 'workId'])

  const authorized = surface.finalPromotionProbe(opened)
  assert.equal(authorized.authorized, true)

  const full = (() => {
    let text = opened
    for (let step = 0; step < 2; step += 1) {
      const reviewStep = surface.reviewProbe(text, incumbencyIdOf(text), 'Promoted')
      const relayStep = surface.deliverProbe(`${text}${reviewStep.bytes}`, `run-${step}`, `call-${step}`, 43n + BigInt(step))
      text = `${text}${reviewStep.bytes}${relayStep.bytes}`
    }
    const last = surface.reviewProbe(text, incumbencyIdOf(text), 'Promoted')
    const planRoot = mkdtempSync(join(tmpdir(), 'wxs-work-033-keys-'))
    let receipt
    try {
      const written = planning.PlanningSurface.rewritePlanAtomic(planRoot, 'work-1', '# plan\n')
      assert.equal(written.ok, true, written.error)
      const selected = planning.PlanningSurface.readAndValidatePlan(planRoot, 'work-1')
      assert.equal(selected.ok, true, selected.error)
      receipt = surface.deliverStateProbe(`${text}${last.bytes}`, 'run-final', 'call-final', 50n, { digest: selected.digest, path: selected.path })
    } finally {
      rmSync(planRoot, { recursive: true, force: true })
    }
    const reopened = surface.reopenProbe(`${text}${last.bytes}${receipt.bytes}`, incumbencyIdOf(text))
    return { receipt, reopened }
  })()
  const receipt = eventsOf(full.receipt.bytes).find((event) => event.type === 'IncumbencyRetired')
  for (const key of ['digest', 'execSessionId', 'incumbencyId', 'outcome', 'path', 'retirementCursor']) {
    assert.equal(Object.keys(receipt).includes(key), true, key)
  }
  const reopened = eventsOf(full.reopened.bytes).find((event) => event.type === 'Reopened')
  assert.deepEqual(Object.keys(reopened), sorted(Object.keys(reopened)))
  assert.deepEqual(Object.keys(reopened), ['retirementId', 'type'])
})

test('WHAT[work-033] canonical lines round-trip and keep int64 cursor precision', () => {
  const precise = 9007199254740991n
  const opened = surface.encodeOpening('plan', 'work-1', '/repo/root', precise)
  assert.equal(opened.endsWith('\n'), true, 'a canonical lifecycle line ends with LF')
  const decoded = surface.decodeDocument(opened)
  assert.equal(decoded.ok, true, decoded.fault)
  assert.equal(decoded.names, 'WorkOpened,IncumbencyOpened')
  const openedEvent = eventsOf(opened)[1]
  assert.equal(openedEvent.openingCursor, Number(precise))
  assert.equal(BigInt(openedEvent.openingCursor), precise)
  assert.equal(surface.documentView(opened).openingCursor, Number(precise))

  const outside = surface.encodeOpening('plan', 'work-1', '/repo/root', precise + 4n)
  assert.equal(outside.startsWith('REFUSE'), true, 'a cursor outside the safe range is refused, not rounded')
  assert.equal(outside.endsWith('\n'), false)

  const truncated = opened.replace(/\n$/, '')
  assert.equal(truncated.endsWith('\n'), false)
})

test('WHAT[work-033] real bytes fold through the codec and the live store', async () => {
  const opened = surface.encodeOpening('plan', 'work-demo', '/workspace', 0n)
  let text = opened
  for (let step = 0; step < 2; step += 1) {
    const reviewed = surface.reviewProbe(text, incumbencyIdOf(text), 'Promoted')
    const relayed = surface.deliverProbe(`${text}${reviewed.bytes}`, `run-${step}`, `call-${step}`, BigInt((step + 1) * 10))
    text = `${text}${reviewed.bytes}${relayed.bytes}`
  }
  const reviewed = surface.reviewProbe(text, incumbencyIdOf(text), 'Promoted')
  text = `${text}${reviewed.bytes}`
  const planRoot = mkdtempSync(join(tmpdir(), 'wxs-work-033-plan-'))
  let delivered
  let selected
  try {
    const written = planning.PlanningSurface.rewritePlanAtomic(planRoot, 'work-demo', '# plan\n')
    assert.equal(written.ok, true, written.error)
    selected = planning.PlanningSurface.readAndValidatePlan(planRoot, 'work-demo')
    assert.equal(selected.ok, true, selected.error)
    delivered = surface.deliverStateProbe(text, 'run-demo', 'call-demo', 30n, { digest: selected.digest, path: selected.path })
    assert.equal(delivered.decision, 'Write', delivered.detail)
  } finally {
    rmSync(planRoot, { recursive: true, force: true })
  }
  text = `${text}${delivered.bytes}`
  assert.equal(surface.documentView(text).digest, selected.digest)

  const folded = surface.readProbe(text)
  assert.equal(folded.ok, true)
  assert.equal(folded.next, 'Done')
  assert.equal(folded.activeId, '')
  const view = surface.documentView(text)
  assert.equal(view.found, true)
  assert.equal(view.workId, 'work-demo')
  assert.equal(view.retiredCount, 3)
  assert.equal(view.delivered, true)
  assert.equal(view.digest.length, 64)
  assert.equal(view.projection, 'S3')

  await withStore(async (store) => {
    const seeded = await surface.seedOpen(store, 'plan', 'work-demo', '/workspace', 0n)
    assert.equal(seeded.ok, true, seeded.error)
    const bound = await surface.bindDevOps(store, 'work-demo', 'devops-fixed', null)
    assert.equal(bound.ok, true, bound.error)
    for (let step = 0; step < 2; step += 1) {
      const review = await surface.seedReview(store, 'work-demo', 'Promoted')
      assert.equal(review.ok, true, review.error)
      const relay = await surface.seedDeliver(store, 'work-demo', `run-${step}`, `call-${step}`, BigInt((step + 1) * 10))
      assert.equal(relay.ok, true, relay.error)
      assert.equal(relay.decision, 'relayed')
    }
    const last = await surface.seedReview(store, 'work-demo', 'Promoted')
    assert.equal(last.ok, true, last.error)
    const blocked = await surface.seedDeliver(store, 'work-demo', 'run-demo', 'call-demo', 30n)
    assert.equal(blocked.ok, false, 'an authorized tenure does not deliver without a draft and a session')
    assert.match(blocked.error, /draft|session/i)
    const stored = surface.workView(store, 'work-demo')
    assert.equal(stored.found, true)
    assert.equal(stored.sequence, 'S1,S2,S3')
    assert.equal(stored.active, true)
    assert.equal(stored.reviewed, 'Promoted')
    assert.equal(stored.delivered, false, 'the refused deliver writes no retirement')
    assert.equal(surface.workDevOps(store, 'work-demo').devopsId, 'devops-fixed')
  })
})

test('WHAT[work-033] an illegal first stage is rejected by the fold before any live append', async () => {
  const opened = surface.encodeOpening('plan', 'work-mock', '/repo', 0n)
  const illegal = opened.replace('"stage":"S1"', '"stage":"S2"')
  const read = surface.readProbe(illegal)
  assert.equal(read.ok, false, 'the first incumbency must open at S1')
  assert.equal(read.opened, false)
  assert.equal(read.names, '')
  assert.match(read.reason, /Invalid first stage/)

  await withStore(async (store) => {
    const seeded = await surface.seedOpen(store, 'plan', 'work-mock', '/repo', 0n)
    assert.equal(seeded.ok, true, seeded.error)
    const database = new DatabaseSync(surface.journalPath(store.gitCommonDir))
    const journal = new SqliteJournal(database)
    try {
      const appended = journal.read('work-mock')
      assert.equal(appended.length, 1)
      const events = JSON.parse(appended[0]).events
      assert.equal(events[1].stage, 'S1', 'the kernel does not append an S2 opening')
    } finally {
      journal.close()
    }
  })
})

test('WHAT[work-033] a same-request replay is idempotent and a different payload is a structured conflict', async () => {
  await withStore(async (store) => {
    const database = new DatabaseSync(surface.journalPath(store.gitCommonDir))
    const journal = new SqliteJournal(database)
    try {
      const durable = new DurableWork('work-replay', journal)
      const command = (root) => ({ type: 'Open', root, sequence: ['S1', 'S2', 'S3'], ownerId: 'owner', openingCursor: 0 })

      const first = durable.execute('open:work-replay', command('/workspace'))
      assert.equal(first.status, 'Committed')
      assert.equal(journal.read('work-replay').length, 1)

      const replay = durable.execute('open:work-replay', command('/workspace'))
      assert.equal(replay.status, 'Replayed', 'the same request id and payload replays the original receipt')
      assert.equal(journal.read('work-replay').length, 1, 'a replay appends nothing')

      const conflict = durable.execute('open:work-replay', command('/other-workspace'))
      assert.equal(conflict.status, 'Conflict', 'a different payload under the same request id is a structured conflict, not a thrown failure')
      assert.equal(conflict.reason, 'RequestIdConflict')
      assert.equal(journal.read('work-replay').length, 1, 'a conflict appends nothing')
      assert.equal(durable.inspect().revision, 1, 'a conflict leaves the revision unchanged')
    } finally {
      journal.close()
    }
  })
})
