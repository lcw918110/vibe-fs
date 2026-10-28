import assert from 'node:assert/strict'
import { execFileSync } from 'node:child_process'
import { mkdirSync, mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { DatabaseSync } from 'node:sqlite'
import test from 'node:test'
import { SqliteJournal } from 'wanxiangli/eventstore'
import { DurableWork } from 'wanxiangli/durable-work'
import { Work } from 'wanxiangli/work'
import * as lifecycle from '../../../dist/Mission/Lifecycle/Surface.js'
import * as workspace from '../../../dist/OpenCode/Host/WorkspaceEventStoreSurface.js'
import * as journalSurface from '../../../dist/Persistence/Journal/Surface.js'
import * as eventStore from '../../../dist/Persistence/EventStore/Surface.js'
import * as dispatch from '../../../dist/Interaction/Dispatch/DispatchSurface.js'
import { existsSync, readdirSync } from 'node:fs'
import { acceptAuthorityRoot, withExecutablePlugin } from '../../verification-system/tests/support/plugin-fixture.mjs'

const surface = lifecycle.LifecycleSurface ?? lifecycle

const sorted = (keys) => [...keys].sort((left, right) => (left < right ? -1 : left > right ? 1 : 0))
const canonical = (value) => {
  if (Array.isArray(value)) return `[${value.map(canonical).join(',')}]`
  if (value !== null && typeof value === 'object') {
    return `{${Object.keys(value).sort().map((key) => `${JSON.stringify(key)}:${canonical(value[key])}`).join(',')}}`
  }
  return JSON.stringify(value)
}
const reviewCommand = (updates, toolCallId = 'call-1') => ({
  type: 'Review',
  office: 'Plan',
  providerRunId: 'run',
  toolCallId,
  updates,
})

// 交付见证的观察口：已注册的 EventStore Surface 把 durable 事实读成普通 JSON
// （payload.Fact = ["Agent", [family, [case, payload]]]），不构造任何 F# union。
// 交付见证就是 Host 家族里那条 TaskDeliveryCommitted 事实。
const deliveryWitnesses = (commonDir) => {
  const eventsDir = join(commonDir, 'wanxiangshu', 'events')
  if (!existsSync(eventsDir)) return []
  const all = new Map()
  for (const file of readdirSync(eventsDir)) {
    const store = eventStore.create(commonDir, file.split('-')[0])
    for (const stream of eventStore.streams(store)) {
      let id = eventStore.head(store, stream)
      while (id) {
        const event = eventStore.read(store, id)
        if (!event) break
        all.set(id, event)
        id = event.parents?.[0]
      }
    }
    eventStore.dispose(store)
  }
  const witnesses = []
  for (const event of all.values()) {
    const fact = event?.payload?.Fact
    if (fact?.[1]?.[0] === 'Host' && fact[1][1][0] === 'TaskDeliveryCommitted') witnesses.push(fact)
  }
  return witnesses
}

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
  await withStore(async (store) => {
    const workId = 'snapshot-race'
    const journal = new SqliteJournal(new DatabaseSync(surface.journalPath(store.gitCommonDir)))
    const originalPrepare = DatabaseSync.prototype.prepare
    try {
      const work = new DurableWork(workId, journal)
      const opened = work.execute('review-open', {
        type: 'Review',
        office: 'Plan',
        providerRunId: 'run-open',
        toolCallId: 'call-open',
        updates: [{ key: 'need', value: 'stay' }],
      })
      assert.equal(opened.status, 'Committed')
      assert.equal(opened.result.kind, 'Reviewed')
      const initialId = opened.result.incumbencyId
      const candidate = new Work(workId, journal.read(workId))
      const relay = candidate.execute('relay', {
        type: 'Review',
        office: 'Plan',
        providerRunId: 'run',
        toolCallId: 'relay',
        updates: [{ key: 'need', value: null }],
      })
      assert.equal(relay.status, 'Applied')
      assert.equal(relay.result.kind, 'Relayed')
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
  await withStore(async (store) => {
    const reviewed = surface.submitReview(
      store.gitCommonDir,
      'devops-target',
      'review-1',
      'Manager',
      'run-1',
      'call-1',
      [{ key: 'job', value: 'do' }],
      null,
      '',
    )
    assert.equal(reviewed.status, 'Committed', reviewed.reason)
    assert.equal(reviewed.kind, 'Reviewed')
    const command = {
      type: 'BindDevOps',
      trigger: 'Horizon',
      devopsId: 'devops-identity',
      target: 'provider/fixed-model',
    }
    const bound = surface.execute(store.gitCommonDir, 'devops-target', 'bind', command)
    assert.equal(bound.Status, 'Committed', bound.Reason)
    assert.equal(bound.Kind, 'DevOpsBound')
    const again = surface.execute(store.gitCommonDir, 'devops-target', 'bind-again', command)
    assert.equal(again.Status, 'Observed')
    assert.equal(again.Kind, 'DevOpsBound')
    const conflict = surface.execute(store.gitCommonDir, 'devops-target', 'bind-other', {
      ...command,
      target: 'provider/other',
    })
    assert.equal(conflict.Status, 'Conflict')
    assert.equal(conflict.Reason, 'DevOps binding conflict')
    const observed = surface.inspected(store.gitCommonDir, 'devops-target')
    assert.equal(observed.DevOpsId, 'devops-identity')
    assert.equal(observed.DevOpsTarget, 'provider/fixed-model')
    assert.equal(Array.from(observed.Sequence ?? []).join(','), '')
  })
})

test('WHAT[work-033] lifecycle payload keys are ordered by Unicode code point', () => {
  const work = new Work('work-1')
  const reviewed = work.execute('r1', reviewCommand([{ key: 'need', value: '范围' }], 'r1'))
  assert.equal(reviewed.status, 'Applied', reviewed.reason)
  const record = work.records()[0]
  const frame = JSON.parse(record)
  assert.deepEqual(Object.keys(frame), sorted(Object.keys(frame)))
  assert.equal(record, canonical(frame))
  for (const event of frame.events) {
    assert.deepEqual(Object.keys(event), sorted(Object.keys(event)))
    assert.equal(Object.hasOwn(event, 'openingCursor'), false)
  }
  const [workOpened, incumbencyOpened, reviewedEvent] = frame.events
  assert.deepEqual(Object.keys(workOpened), ['office', 'type', 'workId'])
  assert.deepEqual(Object.keys(incumbencyOpened), ['incumbencyId', 'messageId', 'milestone', 'origin', 'type'])
  assert.equal(incumbencyOpened.milestone, '事实与取舍')
  assert.deepEqual(Object.keys(reviewedEvent), ['incumbencyId', 'providerRunId', 'toolCallId', 'type', 'updates'])

  const relayed = work.execute('r2', reviewCommand([{ key: 'need', value: null }], 'r2'))
  assert.equal(relayed.result.kind, 'Relayed')
  const relayFrame = JSON.parse(work.records().at(-1))
  assert.equal(work.records().at(-1), canonical(relayFrame))
  for (const event of relayFrame.events) {
    assert.deepEqual(Object.keys(event), sorted(Object.keys(event)))
    assert.equal(Object.hasOwn(event, 'openingCursor'), false)
  }
  const retired = relayFrame.events.find((event) => event.type === 'IncumbencyRetired')
  assert.equal(retired.outcome, 'Relayed')
  assert.deepEqual(Object.keys(retired), ['incumbencyId', 'outcome', 'providerRunId', 'retirementId', 'toolCallId', 'type', 'updates'])

  const before = work.records()
  assert.throws(
    () => work.execute('bad', { ...reviewCommand([], 'bad'), snapshotId: 's' }),
    /Unknown field: snapshotId/,
  )
  assert.deepEqual(work.records(), before)
})

test('WHAT[work-033] canonical records round-trip and a non-canonical line is refused', () => {
  const work = new Work('work-1')
  work.execute('r1', reviewCommand([{ key: 'need', value: 'keep' }], 'r1'))
  const body = work.records()[0]
  assert.equal(body.endsWith('\n'), false, 'a canonical lifecycle record has no trailing newline')
  const restored = new Work('work-1', [body])
  assert.equal(restored.inspect().milestone, '事实与取舍')
  assert.deepEqual(restored.history()[0].events.map((event) => event.type), ['WorkOpened', 'IncumbencyOpened', 'Reviewed'])
  assert.throws(() => new Work('work-1c', [`${body}\n`]), /canonical/)

  const unknown = JSON.parse(body)
  unknown.events[2].type = 'StageOpened'
  assert.throws(() => new Work('work-1d', [canonical(unknown)]), /Unknown lifecycle event/)

  const relayed = work.execute('r2', reviewCommand([{ key: 'need', value: null }], 'r2'))
  assert.equal(relayed.result.kind, 'Relayed')
  const retirement = JSON.parse(work.records().at(-1))
  const retired = retirement.events.find((event) => event.type === 'IncumbencyRetired')
  assert.throws(
    () => new Work('work-1f', [canonical({ ...retirement, events: [retired] })]),
    /Illegal lifecycle transaction/,
  )
  retired.outcome = 'Delivered'
  assert.throws(() => new Work('work-1e', [canonical(retirement)]), /Unknown value: Delivered/)
})

test('WHAT[work-033] real bytes fold through the codec and the live store', async () => {
  const work = new Work('work-demo')
  const reviewed = work.execute('r1', reviewCommand([{ key: 'need', value: '范围' }], 'call-1'))
  assert.equal(reviewed.result.kind, 'Reviewed')
  const folded = new Work('work-demo', work.records())
  assert.equal(folded.inspect().office, 'Plan')
  assert.equal(folded.inspect().milestone, '事实与取舍')
  assert.equal(folded.history().flatMap((frame) => frame.events).some((event) => event.type === 'IncumbencyRetired'), false)

  await withStore(async (store) => {
    const recorded = surface.submitReview(
      store.gitCommonDir,
      'work-demo',
      'r1',
      'Plan',
      'run',
      'call-1',
      [{ key: 'need', value: '范围' }],
      null,
      '',
    )
    assert.equal(recorded.status, 'Committed', recorded.reason)
    assert.equal(recorded.kind, 'Reviewed')
    assert.equal(recorded.milestone, '事实与取舍')
    const bound = await surface.bindDevOps(store, 'work-demo', 'devops-fixed', 'provider/fixed-model')
    assert.equal(bound.ok, true, bound.error)
    const stored = surface.workView(store, 'work-demo')
    assert.equal(stored.found, true)
    assert.equal(stored.sequence, '')
    assert.equal(stored.active, true)
    assert.equal(stored.delivered, false)
    assert.equal(surface.workDevOps(store, 'work-demo').devopsId, 'devops-fixed')
    const inspected = surface.inspected(store.gitCommonDir, 'work-demo')
    assert.equal(Array.from(inspected.Sequence ?? []).join(','), '')
    assert.equal(inspected.Stage, '事实与取舍')
    assert.equal(inspected.DevOpsId, 'devops-fixed')
    assert.equal(inspected.DevOpsTarget, 'provider/fixed-model')
  })
})

test('WHAT[work-033] an unknown field is rejected by the fold before any live append', async () => {
  const work = new Work('work-mock')
  const reviewed = work.execute('r1', reviewCommand([{ key: 'need', value: 'keep' }], 'r1'))
  assert.equal(reviewed.status, 'Applied')
  const frame = JSON.parse(work.records()[0])
  frame.events[1].snapshotId = 'extra'
  assert.throws(() => new Work('work-mock', [canonical(frame)]), /Unknown field: snapshotId/)
  assert.equal(work.records().length, 1)

  await withStore(async (store) => {
    assert.throws(
      () => surface.execute(store.gitCommonDir, 'work-mock', 'bad', { ...reviewCommand([], 'bad'), snapshotId: 's' }),
      /Unknown field: snapshotId/,
    )
    const database = new DatabaseSync(surface.journalPath(store.gitCommonDir))
    const journal = new SqliteJournal(database)
    try {
      assert.equal(journal.read('work-mock').length, 0, 'an unknown field writes nothing')
    } finally {
      journal.close()
    }
  })
})

test('WHAT[work-033] Engineer and DevOps natural stop completes cleanly without requiring a delivery witness', async () => {
  for (const name of ['engineer', 'devops']) {
      await withExecutablePlugin(async (hooks, directory, created, runtime) => {
      const session = `session-natural-${name}`
        const physical = `phys-${session}`
        await acceptAuthorityRoot(runtime, session, name, physical)

        const user = {
          info: {
            id: physical,
            role: 'user',
            sessionID: session,
            agent: name,
            model: { providerID: 'provider', modelID: `${name}-model` },
            time: { created: 1 },
        },
          parts: [{ type: 'text', text: 'Do the work.' }],
        }
        runtime.pushHostMessage(session, user)
        await hooks['chat.message'](
          { sessionID: session, messageID: physical, agent: name },
          { message: user.info, parts: user.parts },
        )

        const run = `run-${session}`
        runtime.pushHostMessage(session, {
          info: {
            id: run,
            role: 'assistant',
            sessionID: session,
            parentID: physical,
            agent: name,
            providerID: 'provider',
            modelID: `${name}-model`,
          time: { created: 2 },
        },
        parts: [{ type: 'text', text: 'I finished the task naturally.' }],
      })

      const program = 'class Js extends JsProgram { async run() { return 1; } }'
      const executed = await hooks.tool[`todowrite-for-${name}`].execute(
        { name: `program-${run}`, program, intent: 'Observe this program result without treating it as a business delivery.', remainingScopeTurnsEstimate: 3 },
          { sessionID: session, agent: name, callID: `call-${session}`, messageID: run },
        )
      assert.match(String(executed), /# ok/)
})
  }
})

test('WHAT[work-033] a same-request replay is idempotent and a different payload is a structured conflict', async () => {
  await withStore(async (store) => {
    const database = new DatabaseSync(surface.journalPath(store.gitCommonDir))
    const journal = new SqliteJournal(database)
    try {
      const durable = new DurableWork('work-replay', journal)
      const command = (value) => reviewCommand([{ key: 'need', value }], 'call-replay')

      const first = durable.execute('review:work-replay', command('one'))
      assert.equal(first.status, 'Committed')
      assert.equal(first.result.kind, 'Reviewed')
      assert.equal(journal.read('work-replay').length, 1)

      const replay = durable.execute('review:work-replay', command('one'))
      assert.equal(replay.status, 'Replayed', 'the same request id and payload replays the original receipt')
      assert.equal(journal.read('work-replay').length, 1, 'a replay appends nothing')

      const conflict = durable.execute('review:work-replay', command('two'))
      assert.equal(conflict.status, 'Conflict', 'a different payload under the same request id is a structured conflict, not a thrown failure')
      assert.equal(conflict.reason, 'RequestIdConflict')
      assert.equal(journal.read('work-replay').length, 1, 'a conflict appends nothing')
      assert.equal(durable.inspect().revision, 1, 'a conflict leaves the revision unchanged')
      const before = journal.read('work-replay')
      assert.throws(() => durable.execute('open:work-replay', { type: 'Open' }), /Unknown work command/)
      assert.throws(() => durable.execute('deliver:work-replay', { type: 'Deliver' }), /Unknown work command/)
      assert.throws(() => durable.execute('advance:work-replay', { type: 'Advance' }), /Unknown work command/)
      assert.deepEqual(journal.read('work-replay'), before)
    } finally {
      journal.close()
    }
  })
})
