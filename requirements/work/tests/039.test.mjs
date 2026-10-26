import assert from 'node:assert/strict'
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { DatabaseSync } from 'node:sqlite'
import test from 'node:test'
import { SqliteJournal } from 'wanxiangli/eventstore'
import * as hostModule from '../../../dist/Mission/Lifecycle/DurableWorkHost.js'
import { withExecutablePlugin, acceptAuthorityRoot } from '../../verification-system/tests/support/plugin-fixture.mjs'
import { JournalSurface_initializeStateRoot } from '../../../dist/Persistence/Journal/Surface.js'

const host = hostModule.DurableWorkHost ?? hostModule

const withRoot = async (body) => {
  const root = mkdtempSync(join(tmpdir(), 'wxs-work-039-'))
  try {
    await body(root)
  } finally {
    rmSync(root, { recursive: true, force: true })
  }
}

const eventsOf = (dir, workId) => {
  const database = new DatabaseSync(host.journalPath(dir))
  const journal = new SqliteJournal(database)
  try {
    return journal.read(workId).flatMap((line) => JSON.parse(line).events ?? [])
  } finally {
    journal.close()
  }
}

const quietEffects = () => host.reviewEffects({
  park: async () => ({ ok: true }),
  send: async () => ({ ok: true }),
  open: async () => {
    throw new Error('a review before 待执行 must not open a manager')
  },
  draft: async () => {
    throw new Error('a review before 待执行 must not send a draft')
  },
  stop: async () => ({ ok: true }),
})

const reachReady = async (root, workId) => {
  const effects = quietEffects()
  for (const milestone of ['理解与路径', '流程与伪代码', '待执行']) {
    const kept = await host.submitReview(
      root, workId, `keep-${workId}-${milestone}`, 'Plan', 'run', `keep-${milestone}`,
      [{ key: 'item', value: 'keep' }], null, '', effects,
    )
    assert.equal(kept.kind, 'Reviewed', kept.reason)
    const relayed = await host.submitReview(
      root, workId, `clear-${workId}-${milestone}`, 'Plan', 'run', `clear-${milestone}`,
      [{ key: 'item', value: null }], null, '', effects,
    )
    assert.equal(relayed.kind, 'Relayed', relayed.reason)
    const advanced = await host.submitReview(
      root, workId, `next-${workId}-${milestone}`, 'Plan', 'run', `next-${milestone}`,
      [], null, '', effects,
    )
    assert.equal(advanced.kind, 'Advanced', advanced.reason)
    assert.equal(advanced.milestone, milestone)
  }
  assert.equal(host.inspected(root, workId).Stage, '待执行')
  assert.equal(host.inspected(root, workId).Delivered, false)
}

const launch = (root, workId, requestId, call, digest, effects, blockers = null) =>
  host.submitReview(root, workId, requestId, 'Plan', 'run', call, [], blockers, digest, effects)

test('WHAT[work-039] the real review member launches with the canonical draft bytes, not the milestone name', async () => {
  await withExecutablePlugin(async (hooks, directory, created, runtime) => {
    const session = 'plan-member-draft-039'
    await acceptAuthorityRoot(runtime, session, 'plan')
    const root = await JournalSurface_initializeStateRoot(directory)
    await reachReady(root, session)
    const planDir = join(root, 'wanxiangshu', 'plan', session)
    mkdirSync(planDir, { recursive: true })
    const content = '# Execute this exact draft\n\n保留中文、制表符\t和末尾换行。\n'
    writeFileSync(join(planDir, 'plan.md'), content)
    const result = await hooks.tool['todowrite-for-plan'].execute({
      name: 'launch-with-draft', intent: 'Launch the Manager with the saved, canonical planning draft.',
      remainingScopeTurnsEstimate: 0,
      program: 'class Js extends JsProgram { async run() { return await this.review([]); } }',
    }, { sessionID: session, agent: 'plan', callID: 'launch-draft', messageID: 'launch-run' })
    assert.match(String(result), /kind = "Launched"/)
    assert.equal(created.length, 1)
    const sent = runtime.prompts.find(prompt => prompt.path?.id === created[0])
    assert.equal(sent?.body?.parts?.[0]?.text, content)
  })
})

test('WHAT[work-039] a missing draft refuses the real review member before creating a Manager', async () => {
  await withExecutablePlugin(async (hooks, directory, created, runtime) => {
    const session = 'plan-member-missing-039'
    await acceptAuthorityRoot(runtime, session, 'plan')
    const root = await JournalSurface_initializeStateRoot(directory)
    await reachReady(root, session)
    await hooks.tool['todowrite-for-plan'].execute({
      name: 'missing-draft', intent: 'Refuse a launch with no draft instead of creating an empty Manager.',
      remainingScopeTurnsEstimate: 1,
      program: 'class Js extends JsProgram { async run() { return await this.review([]); } }',
    }, { sessionID: session, agent: 'plan', callID: 'missing-draft', messageID: 'missing-run' })
    assert.deepEqual(created, [])
    assert.deepEqual(host.inspect(root, session).launches, [])
  })
})

test('WHAT[work-039] a committed ready review opens one manager, sends the draft bytes, and a replay does not open again', async () => {
  await withRoot(async (root) => {
    const workId = 'work-39-verbatim'
    const content = '# Plan with tricky bytes\n\n- line with\ttab\n- trailing newline\n'
    await reachReady(root, workId)
    const opened = []
    const drafted = []
    const effects = host.reviewEffects({
      park: async () => ({ ok: true }),
      send: async () => ({ ok: true }),
      open: async ({ digest }) => {
        opened.push(digest)
        return { ok: true, sessionId: 'exec-verbatim-42' }
      },
      draft: async ({ sessionId, digest }) => {
        drafted.push([sessionId, digest])
        return { ok: true }
      },
      stop: async () => ({ ok: true }),
    })
    const started = await launch(root, workId, 'launch-1', 'launch-call', content, effects)
    assert.equal(started.status, 'Committed', started.reason)
    assert.equal(started.kind, 'Launched')
    assert.equal(started.milestone, '待执行')
    assert.deepEqual(opened, [content])
    assert.deepEqual(drafted, [['exec-verbatim-42', content]])
    const seen = host.inspected(root, workId)
    assert.equal(seen.Stage, '待执行')
    assert.equal(seen.Delivered, false)
    const snapshot = host.inspect(root, workId)
    assert.equal(snapshot.launches.length, 1)
    assert.equal(snapshot.launches[0].status, 'Admitted')
    assert.equal(snapshot.launches[0].execSessionId, 'exec-verbatim-42')
    assert.equal(snapshot.launches[0].digest, content)
    const events = eventsOf(root, workId)
    assert.equal(events.filter((event) => event.type === 'DeliveryRequested').length, 1)
    assert.equal(events.filter((event) => event.type === 'DeliverySessionBound').length, 1)
    assert.equal(events.filter((event) => event.type === 'DeliveryAdmitted').length, 1)
    assert.equal(events.some((event) => event.type === 'IncumbencyRetired' && event.outcome === 'Completed'), false)

    const replay = await launch(root, workId, 'launch-1', 'launch-call', content, effects)
    assert.equal(replay.status, 'Replayed')
    assert.equal(replay.kind, 'Launched')
    assert.deepEqual(opened, [content])
    assert.deepEqual(drafted, [['exec-verbatim-42', content]])
    assert.equal(host.inspect(root, workId).launches.length, 1)
    assert.equal(host.inspected(root, workId).Delivered, false)
  })
})

test('WHAT[work-039] an unknown session creation does not send the draft, and the same review does not create again', async () => {
  await withRoot(async (root) => {
    const workId = 'work-39-unknown-create'
    const content = '# Plan content\n'
    await reachReady(root, workId)
    let creates = 0
    let drafts = 0
    const unknownEffects = {
      ask: async () => 'Unknown',
      dispatch: async () => 'Sent',
      stop: async () => 'Requested',
      createSession: async () => {
        creates += 1
        return { status: 'Unknown' }
      },
      sendDraft: async () => {
        drafts += 1
        return 'Admitted'
      },
    }
    const first = await launch(root, workId, 'create-unknown', 'create-unknown-call', content, unknownEffects)
    assert.equal(first.status, 'Committed', first.reason)
    assert.equal(first.kind, 'Launched')
    assert.equal(creates, 1)
    assert.equal(drafts, 0)
    const pending = host.inspect(root, workId).launches[0]
    assert.equal(pending.status, 'Unknown')
    assert.equal(pending.execSessionId, null)
    assert.equal(host.inspected(root, workId).Delivered, false)
    const replay = await launch(root, workId, 'create-unknown', 'create-unknown-call', content, unknownEffects)
    assert.equal(replay.status, 'Replayed')
    assert.equal(creates, 1)
    assert.equal(drafts, 0)

    let secondCreates = 0
    const retry = await launch(root, workId, 'create-retry', 'create-retry-call', content, host.reviewEffects({
      park: async () => ({ ok: true }),
      send: async () => ({ ok: true }),
      open: async () => {
        secondCreates += 1
        return { ok: true, sessionId: 'exec-after-unknown' }
      },
      draft: async () => ({ ok: true }),
      stop: async () => ({ ok: true }),
    }))
    assert.equal(retry.status, 'Committed', retry.reason)
    assert.equal(retry.kind, 'Launched')
    assert.equal(secondCreates, 1)
    assert.equal(host.inspect(root, workId).launches.length, 2)
    assert.equal(host.inspected(root, workId).Stage, '待执行')
    assert.equal(host.inspected(root, workId).Delivered, false)
  })
})

test('WHAT[work-039] a definite refusal writes DeliveryRejected and does not send the draft', async () => {
  await withRoot(async (root) => {
    const workId = 'work-39-refused'
    const content = '# Plan content\n'
    await reachReady(root, workId)
    let drafts = 0
    const effects = host.reviewEffects({
      park: async () => ({ ok: true }),
      send: async () => ({ ok: true }),
      open: async () => ({ ok: false, reason: 'host refused the manager session' }),
      draft: async () => {
        drafts += 1
        return { ok: true }
      },
      stop: async () => ({ ok: true }),
    })
    const refused = await launch(root, workId, 'refused', 'refused-call', content, effects)
    assert.equal(refused.status, 'Committed', refused.reason)
    assert.equal(refused.kind, 'Launched')
    assert.equal(drafts, 0)
    const launchRow = host.inspect(root, workId).launches[0]
    assert.equal(launchRow.status, 'Rejected')
    assert.equal(launchRow.execSessionId, null)
    assert.equal(eventsOf(root, workId).some((event) => event.type === 'DeliveryRejected' && event.reason === 'host refused the manager session'), true)
    assert.equal(host.inspected(root, workId).Delivered, false)

    const retry = await launch(root, workId, 'refused-retry', 'refused-retry-call', content, host.reviewEffects({
      park: async () => ({ ok: true }),
      send: async () => ({ ok: true }),
      open: async () => ({ ok: true, sessionId: 'exec-after-refusal' }),
      draft: async ({ digest }) => {
        assert.equal(digest, content)
        return { ok: true }
      },
      stop: async () => ({ ok: true }),
    }))
    assert.equal(retry.kind, 'Launched')
    assert.equal(host.inspect(root, workId).launches.filter((row) => row.status === 'Admitted').length, 1)
    assert.equal(host.inspected(root, workId).Stage, '待执行')
  })
})

test('WHAT[work-039] an unknown draft admission keeps the bound session and a replay does not send again', async () => {
  await withRoot(async (root) => {
    const workId = 'work-39-unknown-draft'
    const content = '# Plan content\n'
    await reachReady(root, workId)
    let drafts = 0
    const effects = host.reviewEffects({
      park: async () => ({ ok: true }),
      send: async () => ({ ok: true }),
      open: async () => ({ ok: true, sessionId: 'exec-unknown-draft' }),
      draft: async () => {
        drafts += 1
        return { ok: false, reason: 'transport closed after the request may have landed' }
      },
      stop: async () => ({ ok: true }),
    })
    const unknown = await launch(root, workId, 'draft-unknown', 'draft-unknown-call', content, effects)
    assert.equal(unknown.status, 'Committed', unknown.reason)
    assert.equal(unknown.kind, 'Launched')
    assert.equal(drafts, 1)
    const row = host.inspect(root, workId).launches[0]
    assert.equal(row.status, 'Unknown')
    assert.equal(row.execSessionId, 'exec-unknown-draft')
    assert.equal(eventsOf(root, workId).some((event) => event.type === 'DeliveryAdmitted'), false)
    assert.equal(eventsOf(root, workId).some((event) => event.type === 'DeliverySessionBound'), true)
    const replay = await launch(root, workId, 'draft-unknown', 'draft-unknown-call', content, effects)
    assert.equal(replay.status, 'Replayed')
    assert.equal(drafts, 1)
    assert.equal(host.inspected(root, workId).Delivered, false)
    assert.equal(host.inspected(root, workId).Stage, '待执行')
  })
})

test('WHAT[work-039] blockers on a ready review do not retire the plan', async () => {
  await withRoot(async (root) => {
    const workId = 'work-39-blockers'
    const content = '# accepted while a resource is active\n'
    await reachReady(root, workId)
    let opens = 0
    const started = await launch(
      root, workId, 'blocked-launch', 'blocked-call', content,
      host.reviewEffects({
        park: async () => ({ ok: true }),
        send: async () => ({ ok: true }),
        open: async () => {
          opens += 1
          return { ok: true, sessionId: 'exec-resource' }
        },
        draft: async ({ digest }) => {
          assert.equal(digest, content)
          return { ok: true }
        },
        stop: async () => ({ ok: true }),
      }),
      ['owned-live-process'],
    )
    assert.equal(started.kind, 'Launched', started.reason)
    assert.equal(opens, 1)
    assert.equal(host.inspected(root, workId).Stage, '待执行')
    assert.equal(host.inspected(root, workId).Delivered, false)
    assert.equal(host.inspect(root, workId).completed, false)
  })
})
