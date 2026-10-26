import assert from 'node:assert/strict'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import test from 'node:test'
import { DatabaseSync } from 'node:sqlite'
import { SqliteJournal } from 'wanxiangli/eventstore'
import { observeDeliveryDiscovery } from '../../../dist/OpenCode/Host/SessionsSurface.js'
import * as lifecycle from '../../../dist/Mission/Lifecycle/Surface.js'
import * as workHostModule from '../../../dist/Mission/Lifecycle/DurableWorkHost.js'
import { withRestartablePlugin } from '../../verification-system/tests/support/plugin-fixture.mjs'

const life = lifecycle.LifecycleSurface ?? lifecycle
const host = workHostModule.DurableWorkHost ?? workHostModule

const acceptedReview = (receipt) => {
  assert.ok(['Committed', 'Replayed', 'Observed'].includes(receipt.status ?? receipt.Status), JSON.stringify(receipt))
  return receipt
}

const openPlan = (stateRoot, workId) => {
  const opened = acceptedReview(life.submitReview(
    stateRoot, workId, `open-${workId}`, 'Plan', 'run', `open-${workId}`, [], null, '',
  ))
  assert.equal(opened.kind, 'Asked', opened.reason)
  return life.inspected(stateRoot, workId)
}

const reachReady = (stateRoot, workId) => {
  for (const milestone of ['理解与路径', '流程与伪代码', '待执行']) {
    assert.equal(life.submitReview(
      stateRoot, workId, `keep-${workId}-${milestone}`, 'Plan', 'run', `keep-${workId}-${milestone}`,
      [{ key: 'item', value: 'keep' }], null, '',
    ).kind, 'Reviewed')
    assert.equal(life.submitReview(
      stateRoot, workId, `clear-${workId}-${milestone}`, 'Plan', 'run', `clear-${workId}-${milestone}`,
      [{ key: 'item', value: null }], null, '',
    ).kind, 'Relayed')
    const advanced = life.submitReview(
      stateRoot, workId, `next-${workId}-${milestone}`, 'Plan', 'run', `next-${workId}-${milestone}`,
      [], null, '',
    )
    assert.equal(advanced.kind, 'Advanced', advanced.reason)
    assert.equal(advanced.milestone, milestone)
  }
}

const admitLaunch = async (stateRoot, workId, content, sessionId) => {
  reachReady(stateRoot, workId)
  let drafted = 0
  const launched = await host.submitReview(
    stateRoot, workId, `launch-${workId}`, 'Plan', 'run', `launch-${workId}`, [], null, content,
    host.reviewEffects({
      park: async () => ({ ok: true }),
      send: async () => ({ ok: true }),
      open: async ({ digest }) => {
        assert.equal(digest, content)
        return { ok: true, sessionId }
      },
      draft: async ({ sessionId: bound, digest }) => {
        drafted += 1
        assert.equal(bound, sessionId)
        assert.equal(digest, content)
        return { ok: true }
      },
      stop: async () => ({ ok: true }),
    }),
  )
  assert.equal(launched.status, 'Committed', launched.reason)
  assert.equal(launched.kind, 'Launched')
  assert.equal(drafted, 1)
  const seen = life.inspected(stateRoot, workId)
  assert.equal(seen.Stage, '待执行')
  assert.equal(seen.Delivered, false)
}

const corrupt = (stateRoot, workId) => {
  const journal = new SqliteJournal(new DatabaseSync(life.journalPath(stateRoot)))
  try {
    const revision = journal.read(workId).length
    assert.equal(journal.append(workId, revision, 'damaged', '{"invalid":true}').status, 'Committed')
  } finally {
    journal.close()
  }
}

test('WHAT[work-048] startup batch inspection preserves corrupt-work isolation and observes later committed revisions', () => {
  const directory = mkdtempSync(join(tmpdir(), 'wxs-recovery-batch-'))
  const stateRoot = join(directory, '.Herdchestra')
  try {
    for (const workId of ['bad', 'good']) openPlan(stateRoot, workId)
    const database = new DatabaseSync(life.journalPath(stateRoot))
    try {
      database.exec('DROP TRIGGER journal_no_update')
      database.prepare('UPDATE journal_entries SET checksum=? WHERE stream=?').run('0'.repeat(64), 'bad')
    } finally {
      database.close()
    }

    const before = life.inspected(stateRoot, 'good')
    const observations = new Map(host.inspectedMany(stateRoot, ['bad', 'missing', 'good']))
    assert.equal(observations.get('bad').toJSON()[0], 'Error', 'a corrupt work must retain its explicit read failure')
    assert.match(observations.get('bad').toJSON()[1], /checksum mismatch/i)
    assert.equal(observations.get('missing').toJSON()[0], 'Ok')
    assert.equal(observations.get('missing').toJSON()[1], undefined)
    assert.equal(observations.get('good').toJSON()[0], 'Ok')
    assert.deepEqual(observations.get('good').toJSON()[1], before)
    assert.throws(() => life.inspected(stateRoot, 'bad'), /checksum mismatch/i)

    const reviewed = life.submitReview(
      stateRoot, 'good', 'review', 'Plan', 'run', 'review', [{ key: 'need', value: 'scope' }], null, '',
    )
    assert.equal(reviewed.kind, 'Reviewed', reviewed.reason)
    const later = new Map(host.inspectedMany(stateRoot, ['good'])).get('good')
    assert.equal(later.toJSON()[0], 'Ok')
    assert.equal(later.toJSON()[1].Reviewed, true, 'a later batch reads the journal, not an old process-local cache')
    assert.equal(before.Reviewed, false)
  } finally {
    rmSync(directory, { recursive: true, force: true })
  }
})

test('WHAT[work-048] a corrupt work cannot prevent an independent admitted launch from remaining readable', async () => {
  await withRestartablePlugin(async (start, directory, control) => {
    const first = await start()
    const stateRoot = join(directory, '.Herdchestra')
    await admitLaunch(stateRoot, 'z-valid-work', '# valid\n', 'exec-good')
    openPlan(stateRoot, 'a-corrupt-work')
    corrupt(stateRoot, 'a-corrupt-work')
    const promptsBefore = control.prompts.length
    const createdBefore = control.createdIds.length
    await control.stop(first)
    const second = await start()
    const valid = life.inspected(stateRoot, 'z-valid-work')
    assert.equal(valid.Stage, '待执行')
    assert.equal(valid.Delivered, false)
    assert.equal(host.inspect(stateRoot, 'z-valid-work').launches[0].status, 'Admitted')
    assert.equal(host.inspect(stateRoot, 'z-valid-work').launches[0].execSessionId, 'exec-good')
    assert.throws(() => life.inspected(stateRoot, 'a-corrupt-work'), /field|history|record/i)
    assert.equal(control.prompts.length, promptsBefore)
    assert.equal(control.createdIds.length, createdBefore)
    await control.stop(second)
  })
})

test('WHAT[work-048] damage to one work after the first read does not erase another admitted launch', async () => {
  await withRestartablePlugin(async (start, directory, control) => {
    const first = await start()
    const stateRoot = join(directory, '.Herdchestra')
    await admitLaunch(stateRoot, 'z-independent', '# second\n', 'exec-independent')
    const readable = life.inspected(stateRoot, 'z-independent')
    openPlan(stateRoot, 'a-late-damage')
    corrupt(stateRoot, 'a-late-damage')
    await control.stop(first)
    const second = await start()
    assert.deepEqual(life.inspected(stateRoot, 'z-independent'), readable)
    assert.throws(() => life.inspected(stateRoot, 'a-late-damage'), /field|history|record/i)
    assert.equal(control.prompts.length, 0)
    await control.stop(second)
  })
})

test('WHAT[work-048] malformed discovery rows cannot disappear to turn an ambiguous result into a unique session', async () => {
  const title = 'wanxiangshu-delivery:operation'
  const result = await observeDeliveryDiscovery({ client: { session: { list: async () => ({ data: [
    { id: 'real-session', title, agent: 'manager' },
    { title, agent: 'manager' },
  ] }) } } }, title)
  assert.equal(result.ok, false, 'an exact candidate without identity must refuse discovery, not be silently dropped')
})

for (const [label, response] of [
  ['missing list', {}],
  ['SDK error', { error: { data: { message: 'unavailable' }, message: 'unavailable' } }],
  ['missing identity', { data: [{ title: 'wanxiangshu-delivery:operation', agent: 'manager' }] }],
  ['wrong agent', { data: [{ id: 'session', title: 'wanxiangshu-delivery:operation', agent: 'coder' }] }],
  ['full page before exact filtering', { data: [
    { id: 'exact', title: 'wanxiangshu-delivery:operation', agent: 'manager' },
    { id: 'near', title: 'wanxiangshu-delivery:operation-extra', agent: 'manager' },
  ] }],
]) {
  test(`WHAT[work-048] discovery refuses ${label} instead of claiming a unique execution`, async () => {
    const result = await observeDeliveryDiscovery({ client: { session: { list: async request => {
      assert.equal(request.query.limit, 2)
      assert.equal(request.query.roots, true)
      return response
    } } } }, 'wanxiangshu-delivery:operation')
    assert.equal(result.ok, false)
  })
}

test('WHAT[work-048] restart leaves an admitted launch bound and does not send another draft', async () => {
  await withRestartablePlugin(async (start, directory, control) => {
    const first = await start()
    const stateRoot = join(directory, '.Herdchestra')
    const content = '# exact recovered plan\n'
    await admitLaunch(stateRoot, 'plan-recovery-bound', content, 'exec-recovery-bound')
    assert.equal(control.prompts.length, 0)
    await control.stop(first)
    const second = await start()
    const settled = life.inspected(stateRoot, 'plan-recovery-bound')
    assert.equal(settled.Stage, '待执行')
    assert.equal(settled.Delivered, false)
    const row = host.inspect(stateRoot, 'plan-recovery-bound').launches[0]
    assert.equal(row.status, 'Admitted')
    assert.equal(row.execSessionId, 'exec-recovery-bound')
    assert.equal(row.digest, content)
    assert.equal(control.prompts.length, 0, 'restart must not send the draft again')
    assert.equal(control.createdIds.length, 0, 'restart must not create another manager')
    await control.stop(second)
  })
})

test('WHAT[work-048] restart leaves an unknown create unadmitted and does not open another manager', async () => {
  await withRestartablePlugin(async (start, directory, control) => {
    const first = await start()
    const stateRoot = join(directory, '.Herdchestra')
    const workId = 'plan-recovery-unbound-create'
    const content = '# create acknowledgement was lost\n'
    reachReady(stateRoot, workId)
    let creates = 0
    const launched = await host.submitReview(
      stateRoot, workId, 'create-unknown', 'Plan', 'run', 'create-unknown', [], null, content,
      {
        ask: async () => 'Unknown',
        dispatch: async () => 'Sent',
        stop: async () => 'Requested',
        createSession: async () => {
          creates += 1
          return { status: 'Unknown' }
        },
        sendDraft: async () => {
          throw new Error('an unknown create must not send the draft')
        },
      },
    )
    assert.equal(launched.kind, 'Launched', launched.reason)
    assert.equal(creates, 1)
    await control.stop(first)
    const second = await start()
    const pending = host.inspect(stateRoot, workId).launches[0]
    assert.equal(pending.status, 'Unknown')
    assert.equal(pending.execSessionId, null)
    assert.equal(life.inspected(stateRoot, workId).Delivered, false)
    assert.equal(life.inspected(stateRoot, workId).Stage, '待执行')
    assert.equal(creates, 1)
    assert.equal(control.createdIds.length, 0)
    assert.equal(control.prompts.length, 0)
    await control.stop(second)
  })
})
