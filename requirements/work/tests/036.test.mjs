import assert from 'node:assert/strict'
import { execFileSync } from 'node:child_process'
import { existsSync, mkdirSync, mkdtempSync, realpathSync, rmSync } from 'node:fs'
import { DatabaseSync } from 'node:sqlite'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import test from 'node:test'
import { SqliteJournal } from 'wanxiangli/eventstore'
import * as lifecycle from '../../../dist/Mission/Lifecycle/Surface.js'
import * as surface from '../../../dist/Mission/Planning/Surface.js'
import * as workspace from '../../../dist/OpenCode/Host/WorkspaceEventStoreSurface.js'
import { ensurePlanWorkOpenedForSession } from '../../../dist/OpenCode/Plugin/PlanWorkOpenWiring.js'
import { acceptAuthorityRoot, withExecutablePlugin } from '../../verification-system/tests/support/plugin-fixture.mjs'

const life = lifecycle.LifecycleSurface ?? lifecycle

const withStore = async (body) => {
  const root = mkdtempSync(join(tmpdir(), 'wxs-planning-020-'))
  const commonDir = join(root, '.Herdchestra')
  mkdirSync(commonDir, { recursive: true })
  try {
    await body(workspace.acquire(commonDir), root, commonDir)
  } finally {
    rmSync(root, { recursive: true, force: true })
  }
}

const eventsOf = (dir, workId) => {
  const path = life.journalPath(dir)
  if (!existsSync(path)) return []
  const database = new DatabaseSync(path)
  const journal = new SqliteJournal(database)
  try {
    return journal.read(workId).flatMap((line) => JSON.parse(line).events ?? [])
  } finally {
    journal.close()
  }
}

const planReview = (dir, workId, id, updates) =>
  life.submitReview(dir, workId, id, 'Plan', `run-${id}`, `call-${id}`, updates, null, '')

test('WHAT[work-036] the first plan review opens the work at 事实与取舍 and ensurePlanWorkOpened does not', async () => {
  assert.equal(typeof surface.PlanningSurface.ensurePlanWorkOpened, 'function')
  const sessionId = 'ses-plan-entry-1'

  await withStore(async (_store, root, commonDir) => {
    const legacyEvents = join(commonDir, 'wanxiangshu', 'events')
    const missing = await surface.PlanningSurface.ensurePlanWorkOpened(commonDir, root, sessionId, undefined)
    assert.equal(missing.ok, true, missing.error)
    assert.equal(existsSync(legacyEvents), false, 'opening is not a legacy event stream')
    assert.deepEqual(eventsOf(commonDir, sessionId), [], 'ensurePlanWorkOpened does not open a work')

    const reviewed = planReview(commonDir, sessionId, 'review-1', [{ key: 'tradeoff', value: 'keep' }])
    assert.equal(reviewed.status, 'Committed', reviewed.reason)
    assert.equal(reviewed.kind, 'Reviewed')
    assert.equal(reviewed.milestone, '事实与取舍')
    const events = eventsOf(commonDir, sessionId)
    assert.deepEqual(events.map((event) => event.type), ['WorkOpened', 'IncumbencyOpened', 'Reviewed'])
    assert.equal(events.some((event) => event.type === 'DevOpsBound'), false)
    assert.equal(events[0].workId, sessionId)
    assert.equal(events[0].office, 'Plan')
    assert.equal(events[1].milestone, '事实与取舍')
    const view = life.inspected(commonDir, sessionId)
    assert.equal(view.Stage, '事实与取舍')
    assert.equal(Array.from(view.Sequence ?? []).join(','), '')
    assert.equal(view.DevOpsId ?? null, null, 'the first review does not bind DevOps')

    const replay = planReview(commonDir, sessionId, 'review-1', [{ key: 'tradeoff', value: 'keep' }])
    assert.equal(replay.status, 'Replayed')
    assert.equal(eventsOf(commonDir, sessionId).filter((event) => event.type === 'WorkOpened').length, 1)

    const again = await surface.PlanningSurface.ensurePlanWorkOpened(commonDir, root, sessionId, 8n)
    assert.equal(again.ok, true, again.error)
    assert.equal(eventsOf(commonDir, sessionId).filter((event) => event.type === 'WorkOpened').length, 1)

    const conflictingRoot = await surface.PlanningSurface.ensurePlanWorkOpened(commonDir, join(root, 'other'), sessionId, 9n)
    assert.equal(conflictingRoot.ok, true, conflictingRoot.error)
    assert.equal(eventsOf(commonDir, sessionId).length, events.length, 'ensurePlanWorkOpened appends nothing')
    assert.equal(existsSync(legacyEvents), false)
  })
})

test('WHAT[work-036] ensurePlanWorkOpened writes no legacy events and does not open a work', async () => {
  const root = mkdtempSync(join(tmpdir(), 'wxs-planning-no-event-store-'))
  const commonDir = join(root, '.Herdchestra')
  mkdirSync(commonDir, { recursive: true })
  try {
    const legacyEvents = join(commonDir, 'wanxiangshu', 'events')
    assert.equal(existsSync(legacyEvents), false, 'the legacy EventStore has not been activated')
    const res = await surface.PlanningSurface.ensurePlanWorkOpened(commonDir, root, 'ses-plan-entry-2', 7n)
    assert.equal(res.ok, true, res.error)
    assert.equal(existsSync(legacyEvents), false, 'ensurePlanWorkOpened must not create the legacy event stream')
    assert.equal(life.workView({ gitCommonDir: commonDir }, 'ses-plan-entry-2').found, false)
    assert.deepEqual(eventsOf(commonDir, 'ses-plan-entry-2'), [])
  } finally {
    rmSync(root, { recursive: true, force: true })
  }
})

test('WHAT[work-036] ensurePlanWorkOpened does not forge an opening when the directory is missing', async () => {
  const missing = join(tmpdir(), 'wxs-planning-missing-dir', 'never-created')
  const res = await surface.PlanningSurface.ensurePlanWorkOpened(missing, '/workspace/repo', 'ses-plan-entry-3', 7n)
  assert.equal(res.ok, true, res.error)
  assert.equal(existsSync(missing), false)
  assert.throws(() => life.journalPath(''), /local state root is required/i)
  assert.throws(() => life.journalPath('   '), /local state root is required/i)
})

test('WHAT[work-036] manager transforms do not open work; the first review does, and a replay does not second-open', async () => {
  await withExecutablePlugin(async (hooks, directory, _createdIds, runtime) => {
    const managerSession = 'ses-manager-open-idempotency'
    await hooks['chat.message'](
      { sessionID: managerSession, agent: 'manager' },
      {
        message: { id: 'msg-mgr-1', sessionID: managerSession, role: 'user', agent: 'manager', model: { providerID: 'fixture', modelID: 'manager-model' } },
        parts: [{ id: 'prt-mgr-1', type: 'text', text: 'first instruction' }],
      },
    )

    const user1 = {
      info: { id: 'msg-mgr-1', sessionID: managerSession, role: 'user', agent: 'manager', time: { created: 1 } },
      parts: [{ id: 'prt-mgr-1', type: 'text', text: 'first instruction' }],
    }
    const asst1 = {
      info: {
        id: 'asst-mgr-1',
        sessionID: managerSession,
        parentID: 'msg-mgr-1',
        role: 'assistant',
        agent: 'manager',
        providerID: 'provider',
        modelID: 'manager-model',
        time: { created: 2 },
      },
      parts: [],
    }
    runtime.pushHostMessage(managerSession, user1)
    runtime.pushHostMessage(managerSession, asst1)
    await hooks['experimental.chat.messages.transform']({ sessionID: managerSession }, { messages: [user1] })

    const commonDir = join(directory, '.Herdchestra')
    const openedCount = () =>
      eventsOf(commonDir, managerSession).filter((event) => event.type === 'WorkOpened' || event.type === 'IncumbencyOpened').length
    assert.equal(openedCount(), 0, 'a manager transform does not open the work')

    asst1.info.finish = 'stop'
    asst1.info.time.completed = 3
    asst1.parts = [{ id: 'prt-asst-1', type: 'text', text: 'step 1 done' }]
    const asst2 = {
      info: {
        id: 'asst-mgr-2',
        sessionID: managerSession,
        parentID: 'msg-mgr-1',
        role: 'assistant',
        agent: 'manager',
        providerID: 'provider',
        modelID: 'manager-model',
        time: { created: 4 },
      },
      parts: [],
    }
    runtime.pushHostMessage(managerSession, asst2)
    await hooks['experimental.chat.messages.transform']({ sessionID: managerSession }, { messages: [user1, asst1] })
    assert.equal(openedCount(), 0, 'a later transform pass still does not open the work')

    const reviewed = life.submitReview(commonDir, managerSession, 'review-1', 'Manager', 'run-1', 'call-1', [{ key: 'job', value: 'do' }], null, '')
    assert.equal(reviewed.status, 'Committed', reviewed.reason)
    assert.equal(reviewed.kind, 'Reviewed')
    assert.equal(reviewed.milestone, '执行')
    assert.equal(openedCount(), 2, 'the first review writes WorkOpened and IncumbencyOpened once')
    const replay = life.submitReview(commonDir, managerSession, 'review-1', 'Manager', 'run-1', 'call-1', [{ key: 'job', value: 'do' }], null, '')
    assert.equal(replay.status, 'Replayed')
    assert.equal(openedCount(), 2, 'the same review does not second-open')
    assert.equal(Array.from(life.inspected(commonDir, managerSession).Sequence ?? []).join(','), '')
  })
})

test('WHAT[work-036] a manager review of an existing plan work conflicts because the office is fixed', async () => {
  await withExecutablePlugin(async (_hooks, directory) => {
    const managerSession = 'ses-manager-sequence-collision'
    const commonDir = join(directory, '.Herdchestra')
    const opened = planReview(commonDir, managerSession, 'plan-review', [{ key: 'need', value: 'scope' }])
    assert.equal(opened.status, 'Committed', opened.reason)
    assert.equal(opened.milestone, '事实与取舍')
    assert.equal(eventsOf(commonDir, managerSession).filter((event) => event.type === 'WorkOpened').length, 1)

    const conflict = life.submitReview(commonDir, managerSession, 'manager-review', 'Manager', 'run-manager', 'call-manager', [{ key: 'job', value: 'do' }], null, '')
    assert.equal(conflict.status, 'Conflict')
    assert.match(conflict.reason, /Office is fixed/)
    assert.equal(eventsOf(commonDir, managerSession).filter((event) => event.type === 'WorkOpened').length, 1)
    assert.equal(life.inspected(commonDir, managerSession).Stage, '事实与取舍')
    assert.equal(Array.from(life.inspected(commonDir, managerSession).Sequence ?? []).join(','), '')
  })
})

test('WHAT[work-036] the first plan review does not record a worktree root or a legacy sequence', async () => {
  const directory = realpathSync(mkdtempSync(join(tmpdir(), 'wxs-plan-open-root-')))
  try {
    execFileSync('git', ['init', '--quiet', directory])
    const planSession = 'ses-plan-first-prompt-root'
    await ensurePlanWorkOpenedForSession(directory, planSession, 3n)
    const stateRoot = join(directory, '.Herdchestra')
    assert.equal(eventsOf(stateRoot, planSession).some((event) => event.type === 'WorkOpened'), false)

    const reviewed = planReview(stateRoot, planSession, 'review-1', [{ key: 'need', value: 'scope' }])
    assert.equal(reviewed.status, 'Committed', reviewed.reason)
    assert.equal(reviewed.milestone, '事实与取舍')
    const opened = eventsOf(stateRoot, planSession).find((event) => event.type === 'WorkOpened')
    assert.equal(opened.office, 'Plan')
    assert.equal(opened.root, undefined)
    assert.equal(opened.sequence, undefined)
    assert.equal(Array.from(life.inspected(stateRoot, planSession).Sequence ?? []).join(','), '')
  } finally {
    rmSync(directory, { recursive: true, force: true })
  }
})

test('WHAT[work-036] engineer and devops prompts open nothing; a manager review opens execution work', async () => {
  await withExecutablePlugin(async (hooks, directory, _createdIds, runtime) => {
    const drive = async (agent) => {
      const sessionId = `ses-entry-closed-${agent}`
      const physical = `root-${sessionId}`
      await acceptAuthorityRoot(runtime, sessionId, agent, physical)
      const user = {
        info: { id: physical, sessionID: sessionId, role: 'user', agent, model: { providerID: 'fixture', modelID: `${agent}-model` }, time: { created: 1 } },
        parts: [{ id: `prt-${agent}`, type: 'text', text: 'first instruction' }],
      }
      await hooks['chat.message']({ sessionID: sessionId, agent }, { message: user.info, parts: user.parts })
      runtime.pushHostMessage(sessionId, user)
      runtime.pushHostMessage(sessionId, {
        info: {
          id: `asst-${agent}`,
          sessionID: sessionId,
          parentID: physical,
          role: 'assistant',
          agent,
          providerID: 'provider',
          modelID: `${agent}-model`,
          time: { created: 2 },
        },
        parts: [],
      })
      await hooks['experimental.chat.messages.transform']({ sessionID: sessionId }, { messages: [user] })
      return sessionId
    }

    const stateRoot = join(directory, '.Herdchestra')
    const engineer = await drive('engineer')
    const devops = await drive('devops')
    assert.deepEqual(eventsOf(stateRoot, engineer), [], 'an Engineer prompt opens no lifecycle work')
    assert.deepEqual(eventsOf(stateRoot, devops), [], 'a DevOps prompt opens no lifecycle work')

    const manager = await drive('manager')
    assert.deepEqual(eventsOf(stateRoot, manager), [], 'a Manager prompt does not itself open the work')
    const reviewed = life.submitReview(stateRoot, manager, 'review-1', 'Manager', 'run-1', 'call-1', [{ key: 'job', value: 'do' }], null, '')
    assert.equal(reviewed.status, 'Committed', reviewed.reason)
    assert.equal(reviewed.kind, 'Reviewed')
    assert.equal(reviewed.milestone, '执行')
    const events = eventsOf(stateRoot, manager)
    assert.deepEqual(events.map((event) => event.type), ['WorkOpened', 'IncumbencyOpened', 'Reviewed'])
    assert.equal(events[0].office, 'Manager')
    assert.equal(events[0].sequence, undefined)
    assert.equal(Array.from(life.inspected(stateRoot, manager).Sequence ?? []).join(','), '')
  })
})
