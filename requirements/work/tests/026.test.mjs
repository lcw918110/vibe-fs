import assert from 'node:assert/strict'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { DatabaseSync } from 'node:sqlite'
import test from 'node:test'
import { SqliteJournal } from 'wanxiangli/eventstore'
import { acceptAuthorityRoot, withExecutablePlugin } from '../../verification-system/tests/support/plugin-fixture.mjs'
import * as lifecycle from '../../../dist/Mission/Lifecycle/Surface.js'
import * as journalSurface from '../../../dist/Persistence/Journal/Surface.js'

const life = lifecycle.LifecycleSurface ?? lifecycle

const recordsOf = (dir, workId) => {
  const database = new DatabaseSync(life.journalPath(dir))
  const journal = new SqliteJournal(database)
  try {
    return journal.read(workId)
  } finally {
    journal.close()
  }
}

const eventsOf = (dir, workId) => recordsOf(dir, workId).flatMap((line) => JSON.parse(line).events ?? [])

const planReview = (dir, workId, id, updates) =>
  life.submitReview(dir, workId, id, 'Plan', `run-${id}`, `call-${id}`, updates, null, '')

const managerReview = (dir, workId, id, updates, blockers = null) =>
  life.submitReview(dir, workId, id, 'Manager', `run-${id}`, `call-${id}`, updates, blockers, '')

const advancePlanToReady = (dir, workId) => {
  const opened = planReview(dir, workId, 'fill-open', [{ key: 'item', value: 'start' }])
  assert.equal(opened.status, 'Committed', opened.reason)
  assert.equal(opened.kind, 'Reviewed')
  assert.equal(opened.milestone, '事实与取舍')
  for (const next of ['理解与路径', '流程与伪代码', '待执行']) {
    const relayed = planReview(dir, workId, `clear-${next}`, [{ key: 'item', value: null }])
    assert.equal(relayed.kind, 'Relayed', relayed.reason)
    const advanced = planReview(dir, workId, `adv-${next}`, [])
    assert.equal(advanced.kind, 'Advanced', advanced.reason)
    assert.equal(advanced.milestone, next)
    if (next !== '待执行') {
      const filled = planReview(dir, workId, `fill-${next}`, [{ key: 'item', value: next }])
      assert.equal(filled.kind, 'Reviewed', filled.reason)
      assert.equal(filled.milestone, next)
    }
  }
}

const advanceManagerToCleanup = (dir, workId) => {
  const opened = managerReview(dir, workId, 'r1', [{ key: 'job', value: 'do' }])
  assert.equal(opened.status, 'Committed', opened.reason)
  assert.equal(opened.kind, 'Reviewed')
  assert.equal(opened.milestone, '执行')
  assert.equal(managerReview(dir, workId, 'r2', [{ key: 'job', value: null }]).kind, 'Relayed')
  const advanced = managerReview(dir, workId, 'r3', [])
  assert.equal(advanced.kind, 'Advanced', advanced.reason)
  assert.equal(advanced.milestone, '收尾')
}

const providerRequest = (sessionID, userMessageId, assistantMessageId) => ({
  messages: [
    {
      role: 'user',
      info: { id: userMessageId, sessionID, role: 'user' },
      parts: [{ type: 'text', text: 'Plan the feature.' }],
    },
    {
      role: 'assistant',
      info: {
        id: assistantMessageId,
        sessionID,
        role: 'assistant',
        parentID: userMessageId,
        providerID: 'provider',
        modelID: 'plan-model',
      },
      parts: [],
    },
  ],
})

test('WHAT[work-026] a ready empty plan review launches once and does not spawn an execution session', () => {
  const root = mkdtempSync(join(tmpdir(), 'wxs-work-026-launch-'))
  try {
    const workId = 'work-d3'
    advancePlanToReady(root, workId)
    const launched = planReview(root, workId, 'launch', [])
    assert.equal(launched.status, 'Committed', launched.reason)
    assert.equal(launched.kind, 'Launched')
    assert.equal(launched.milestone, '待执行')
    const replay = planReview(root, workId, 'launch', [])
    assert.equal(replay.status, 'Replayed')
    assert.equal(replay.kind, 'Launched')
    const snapshot = life.inspect(root, workId)
    assert.equal(snapshot.launches.length, 1)
    assert.equal(snapshot.milestone, '待执行')
    assert.equal(snapshot.launches[0].execSessionId, null, 'launch does not spawn a session')
    const events = eventsOf(root, workId)
    assert.equal(events.filter((event) => event.type === 'DeliveryRequested').length, 1)
    assert.equal(events.some((event) => event.type === 'DeliverySessionBound'), false)
    const seen = life.inspected(root, workId)
    assert.equal(seen.Stage, '待执行')
    assert.equal(seen.Delivered, false)
    assert.equal(Array.from(seen.Sequence ?? []).join(','), '')
    const before = recordsOf(root, workId).length
    assert.throws(
      () => life.execute(root, workId, 'deliver', { type: 'Deliver', providerRunId: 'run', toolCallId: 'call' }),
      /Unknown work command/,
    )
    assert.equal(recordsOf(root, workId).length, before)
    assert.equal(life.inspect(root, workId).launches.length, 1)
  } finally {
    rmSync(root, { recursive: true, force: true })
  }
})

test('WHAT[work-026] a manager cleanup review completes without a delivery outcome, and a replay does not complete again', async () => {
  await withExecutablePlugin(async (_hooks, directory, createdIds) => {
    const commonDir = await journalSurface.JournalSurface_initializeStateRoot(directory)
    const sessionID = 'ses-manager-complete'
    advanceManagerToCleanup(commonDir, sessionID)
    const before = life.inspect(commonDir, sessionID).revision
    const blocked = managerReview(commonDir, sessionID, 'blocked', [], ['pty'])
    assert.equal(blocked.status, 'Observed')
    assert.equal(blocked.kind, 'Blocked')
    assert.equal(life.inspect(commonDir, sessionID).revision, before, 'a blocked cleanup writes nothing')
    const completed = managerReview(commonDir, sessionID, 'done', [])
    assert.equal(completed.status, 'Committed', completed.reason)
    assert.equal(completed.kind, 'Completed')
    assert.equal(completed.milestone, '已完成')
    const events = eventsOf(commonDir, sessionID)
    assert.ok(events.some((event) => event.type === 'IncumbencyRetired' && event.outcome === 'Completed'))
    assert.ok(events.some((event) => event.type === 'StopObliged'))
    assert.equal(events.some((event) => event.outcome === 'Delivered'), false)
    const retirements = Array.from(life.inspected(commonDir, sessionID).Retirements)
    assert.equal(retirements.filter((item) => item.Outcome === 'Completed').length, 1)
    assert.equal(retirements.some((item) => item.Outcome === 'Delivered'), false)
    assert.equal(life.inspected(commonDir, sessionID).Stage, '已完成')
    const replay = managerReview(commonDir, sessionID, 'done', [])
    assert.equal(replay.status, 'Replayed')
    assert.equal(replay.kind, 'Completed')
    assert.equal(Array.from(life.inspected(commonDir, sessionID).Retirements).length, retirements.length)
    assert.equal(createdIds.length, 0, 'completion does not spawn an execution session')
  })
})

test('WHAT[work-026] a following operator message is not stopped by the previous completion and reopens once', async () => {
  await withExecutablePlugin(async (hooks, directory, createdIds, runtime) => {
    const commonDir = await journalSurface.JournalSurface_initializeStateRoot(directory)
    const sessionID = 'ses-manager-reopen'
    await acceptAuthorityRoot(runtime, sessionID, 'manager', 'user-manager-root')
    runtime.pushHostMessage(sessionID, {
      info: { id: 'user-manager-root', role: 'user', sessionID, time: { created: 1 } },
      parts: [{ type: 'text', text: 'Do the work.' }],
    })
    advanceManagerToCleanup(commonDir, sessionID)
    const completed = managerReview(commonDir, sessionID, 'done', [])
    assert.equal(completed.kind, 'Completed', completed.reason)

    const newInput = {
      id: 'user-new-input',
      role: 'user',
      agent: 'manager',
      sessionID,
      model: { providerID: 'provider', modelID: 'manager-model' },
    }
    runtime.pushHostMessage(sessionID, {
      info: { ...newInput, time: { created: 3 } },
      parts: [{ type: 'text', text: 'Continue from here.' }],
    })
    await hooks['chat.message']({ sessionID, agent: 'manager', messageID: 'user-new-input' }, {
      message: newInput,
      parts: [{ type: 'text', text: 'Continue from here.' }],
    })
    assert.equal(life.inspected(commonDir, sessionID).Stage, '执行',
      'physical human admission must reopen Work without a test issuing the command')
    runtime.pushHostMessage(sessionID, {
      info: {
        id: 'run-new-input',
        role: 'assistant',
        sessionID,
        parentID: 'user-new-input',
        providerID: 'provider',
        modelID: 'manager-model',
        time: { created: 4 },
        finish: 'stop',
      },
      parts: [],
    })
    const request = providerRequest(sessionID, 'user-new-input', 'run-new-input')
    await hooks['experimental.chat.messages.transform']({ sessionID }, request)
    assert.deepEqual(runtime.abortedIds, [], 'completion does not stop the following user input')
    assert.equal(
      request.messages.filter((message) => !message.steering).length,
      2,
      'the following input is not cleared by the previous completion',
    )

    const reopened = life.execute(commonDir, sessionID, 'operator-1', {
      type: 'OperatorMessage',
      kind: 'Operator',
      messageId: 'user-new-input',
    })
    assert.equal(reopened.Status, 'Observed', reopened.Reason)
    assert.equal(reopened.Kind, 'Reopened')
    assert.equal(life.inspected(commonDir, sessionID).Stage, '执行')
    const same = life.execute(commonDir, sessionID, 'operator-2', {
      type: 'OperatorMessage',
      kind: 'Operator',
      messageId: 'user-new-input',
    })
    assert.equal(same.Status, 'Observed')
    const other = life.execute(commonDir, sessionID, 'operator-3', {
      type: 'OperatorMessage',
      kind: 'Operator',
      messageId: 'user-other',
    })
    assert.equal(other.Status, 'Conflict')
    const reopenings = eventsOf(commonDir, sessionID).filter((event) => event.type === 'IncumbencyOpened' && event.messageId === 'user-new-input')
    assert.equal(reopenings.length, 1)
    assert.equal(createdIds.length, 0)
  })
})
