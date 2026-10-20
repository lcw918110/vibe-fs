import assert from 'node:assert/strict'
import { createHash } from 'node:crypto'
import { mkdtempSync, realpathSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import test from 'node:test'
import { DatabaseSync } from 'node:sqlite'
import { SqliteJournal } from 'wanxiangli/eventstore'
import { observeDeliveryDiscovery } from '../../../dist/OpenCode/Host/SessionsSurface.js'
import * as lifecycle from '../../../dist/Mission/Lifecycle/Surface.js'
import * as journalSurface from '../../../dist/Persistence/Journal/Surface.js'
import * as workHost from '../../../dist/Mission/Lifecycle/DurableWorkHost.js'
import { acceptAuthorityRoot, withRestartablePlugin } from '../../verification-system/tests/support/plugin-fixture.mjs'

const life = lifecycle.LifecycleSurface ?? lifecycle
const digest = text => createHash('sha256').update(text).digest('hex')

const accepted = receipt => {
  assert.ok(['Committed', 'Replayed', 'Observed'].includes(receipt.Status), JSON.stringify(receipt))
  return receipt
}

const seedPendingDelivery = async (control, directory, workId, execSessionId, content, bind = true) => {
  const commonDir = await journalSurface.JournalSurface_initializeStateRoot(directory)
  await control.withRuntime(async runtime => {
    await acceptAuthorityRoot(runtime, workId, 'manager', `root-${workId}`)
  })

  const execute = (requestId, command) => accepted(life.execute(commonDir, workId, requestId, command))
  execute('open', {
    type: 'Open',
    root: directory,
    sequence: ['S1', 'S2', 'S3'],
    ownerId: workId,
    openingCursor: 0,
  })

  for (let stage = 1; stage <= 3; stage += 1) {
    const active = life.inspected(commonDir, workId).ActiveId
    execute(`review-${stage}`, {
      type: 'Review',
      incumbencyId: active,
      reviewerId: 'reviewer',
      findings: [],
    })
    if (stage < 3) {
      execute(`relay-${stage}`, {
        type: 'Deliver',
        incumbencyId: active,
        providerRunId: `relay-run-${stage}`,
        toolCallId: `relay-call-${stage}`,
        cursor: stage * 10,
        blockers: [],
        nextOwnerId: workId,
      })
    }
  }

  const active = life.inspected(commonDir, workId).ActiveId
  const reserved = execute('reserve', {
    type: 'Deliver',
    incumbencyId: active,
    providerRunId: 'delivery-run',
    toolCallId: 'delivery-call',
    cursor: 40,
    blockers: [],
    draft: { digest: digest(content), path: `/plans/${workId}.md` },
  })
  assert.equal(reserved.Kind, 'DeliveryRequested')
  const operationId = reserved.Result.operationId
  if (bind) {
    const bound = execute(`${operationId}:session-created`, {
      type: 'ResolveDelivery',
      operationId,
      outcome: 'SessionCreated',
      execSessionId,
    })
    assert.equal(bound.Kind, 'ExecutionSessionBound')
    assert.equal(life.inspected(commonDir, workId).PendingExecSessionId, execSessionId)
  } else {
    assert.equal(life.inspected(commonDir, workId).PendingExecSessionId, undefined)
  }
  return { commonDir, operationId }
}

const userMessage = (sessionId, text) => ({
  info: {
    id: `msg-${sessionId}-plan`,
    sessionID: sessionId,
    role: 'user',
    agent: 'manager',
    time: { created: 1 },
  },
  parts: [{ id: `part-${sessionId}-plan`, type: 'text', text }],
})

const waitFor = async predicate => {
  for (let attempt = 0; attempt < 100; attempt += 1) {
    if (predicate()) return
    await new Promise(resolve => setTimeout(resolve, 10))
  }
  assert.fail('recovery did not settle within the test observation window')
}

const activateDurability = async (hooks, suffix) => {
  const sessionID = `activation-${suffix}`
  const message = {
    id: `msg-${sessionID}`,
    sessionID,
    role: 'user',
    agent: 'manager',
  }
  const parts = [{ id: `part-${sessionID}`, type: 'text', text: 'activate durable recovery' }]
  await hooks['chat.message']({ sessionID, agent: 'manager' }, { message, parts })
}

test('WHAT[work-048] startup batch inspection preserves corrupt-work isolation and observes later committed revisions', () => {
  const directory = mkdtempSync(join(tmpdir(), 'wxs-recovery-batch-'))
  const stateRoot = join(directory, '.Herdchestra')
  try {
    for (const workId of ['bad', 'good']) {
      accepted(life.execute(stateRoot, workId, 'open', {
        type: 'Open', root: directory, sequence: ['S4'], ownerId: workId, openingCursor: 0,
      }))
    }
    const database = new DatabaseSync(life.journalPath(stateRoot))
    try {
      database.exec('DROP TRIGGER journal_no_update')
      database.prepare('UPDATE journal_entries SET checksum=? WHERE stream=?').run('0'.repeat(64), 'bad')
    } finally {
      database.close()
    }

    const before = life.inspected(stateRoot, 'good')
    const observations = new Map(workHost.inspectedMany(stateRoot, ['bad', 'missing', 'good']))
    assert.equal(observations.get('bad').toJSON()[0], 'Error', 'a corrupt work must retain its explicit read failure')
    assert.match(observations.get('bad').toJSON()[1], /checksum mismatch/i)
    assert.equal(observations.get('missing').toJSON()[0], 'Ok')
    assert.equal(observations.get('missing').toJSON()[1], undefined)
    assert.equal(observations.get('good').toJSON()[0], 'Ok')
    assert.deepEqual(observations.get('good').toJSON()[1], before)
    assert.throws(() => life.inspected(stateRoot, 'bad'), /checksum mismatch/i)

    accepted(life.execute(stateRoot, 'good', 'review', {
      type: 'Review', incumbencyId: before.ActiveId, reviewerId: 'reviewer', findings: [],
    }))
    const later = new Map(workHost.inspectedMany(stateRoot, ['good'])).get('good')
    assert.equal(later.toJSON()[0], 'Ok')
    assert.equal(later.toJSON()[1].Reviewed, true, 'a later batch reads the journal, not an old process-local cache')
    assert.equal(before.Reviewed, false)
  } finally {
    rmSync(directory, { recursive: true, force: true })
  }
})

test('WHAT[work-048] corrupt work cannot prevent an independent bound delivery from recovering', async () => {
  await withRestartablePlugin(async (start, directory, control) => {
    const first = await start()
    const { commonDir } = await seedPendingDelivery(control, directory, 'a-corrupt-work', 'exec-bad', '# broken\n')
    await seedPendingDelivery(control, directory, 'z-valid-work', 'exec-good', '# valid\n')
    const journal = new SqliteJournal(new DatabaseSync(life.journalPath(commonDir)))
    try {
      const revision = journal.read('a-corrupt-work').length
      assert.equal(journal.append('a-corrupt-work', revision, 'damaged', '{"invalid":true}').status, 'Committed')
    } finally {
      journal.close()
    }
    control.pushHostMessage('exec-good', userMessage('exec-good', '# valid\n'))
    await control.stop(first)
    const second = await start()
    await activateDurability(second, 'corrupt-isolation')
    await waitFor(() => life.inspected(commonDir, 'z-valid-work')?.Delivered === true)
    assert.throws(() => life.inspected(commonDir, 'a-corrupt-work'), /field|history|record/i)
    assert.equal(control.prompts.length, 0)
    await control.stop(second)
  })
})

test('WHAT[work-048] a work damaged during Host observation cannot abort another work recovery', async () => {
  await withRestartablePlugin(async (start, directory, control) => {
    const first = await start()
    const { commonDir } = await seedPendingDelivery(control, directory, 'a-late-damage', 'exec-late-damage', '# first\n')
    await seedPendingDelivery(control, directory, 'z-independent', 'exec-independent', '# second\n')
    control.pushHostMessage('exec-late-damage', userMessage('exec-late-damage', '# first\n'))
    control.pushHostMessage('exec-independent', userMessage('exec-independent', '# second\n'))
    await control.stop(first)

    const messages = control.client.session.messages
    let damaged = false
    control.client.session.messages = async request => {
      if (request.path.id === 'exec-late-damage' && !damaged) {
        const journal = new SqliteJournal(new DatabaseSync(life.journalPath(commonDir)))
        try {
          const records = journal.read('a-late-damage')
          assert.equal(journal.append('a-late-damage', records.length, 'damaged', '{"invalid":true}').status, 'Committed')
          damaged = true
        } finally {
          journal.close()
        }
      }
      return messages(request)
    }

    const second = await start()
    await activateDurability(second, 'late-damage')
    await waitFor(() => life.inspected(commonDir, 'z-independent')?.Delivered === true)
    assert.equal(damaged, true, 'the corruption must occur after the initial work inspection')
    assert.throws(() => life.inspected(commonDir, 'a-late-damage'), /field|history|record/i)
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
  ['SDK error', { error: { message: 'unavailable' } }],
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

test('WHAT[work-048] restart settles a bound delivery from the exact persisted Host prompt without another external effect', async () => {
  await withRestartablePlugin(async (start, directory, control) => {
    const first = await start()
    const workId = 'plan-recovery-bound'
    const execSessionId = 'exec-recovery-bound'
    const content = '# exact recovered plan\n'
    const { commonDir } = await seedPendingDelivery(control, directory, workId, execSessionId, content)
    control.pushHostMessage(execSessionId, userMessage(execSessionId, content))
    assert.equal(control.prompts.length, 0)
    await control.stop(first)

    const second = await start()
    await activateDurability(second, 'settle')
    await waitFor(() => life.inspected(commonDir, workId)?.Delivered === true)
    const settled = life.inspected(commonDir, workId)
    assert.equal(settled.PendingDelivery, false)
    assert.equal(settled.Delivered, true)
    assert.equal(control.prompts.length, 0, 'recovery must observe the bound session, never resend its prompt')
    await control.stop(second)
  })
})

test('WHAT[work-048] restart leaves a bound delivery pending when the Host prompt bytes do not match the reservation', async () => {
  await withRestartablePlugin(async (start, directory, control) => {
    const first = await start()
    const workId = 'plan-recovery-mismatch'
    const execSessionId = 'exec-recovery-mismatch'
    const content = '# reserved plan\n'
    const { commonDir } = await seedPendingDelivery(control, directory, workId, execSessionId, content)
    control.pushHostMessage(execSessionId, userMessage(execSessionId, '# different bytes\n'))
    await control.stop(first)

    const second = await start()
    await activateDurability(second, 'mismatch')
    await new Promise(resolve => setTimeout(resolve, 80))
    const pending = life.inspected(commonDir, workId)
    assert.equal(pending.PendingDelivery, true)
    assert.equal(pending.PendingExecSessionId, execSessionId)
    assert.equal(pending.Delivered, false)
    assert.equal(control.prompts.length, 0, 'mismatch is not permission to resend or create')
    await control.stop(second)
  })
})

test('WHAT[work-048] restart discovers one exact operation-titled root session after create acknowledgement loss and never creates a second one', async () => {
  await withRestartablePlugin(async (start, directory, control) => {
    const first = await start()
    const workId = 'plan-recovery-unbound-create'
    const content = '# create acknowledgement was lost\n'
    const provisional = 'not-yet-known'
    const { commonDir, operationId } =
      await seedPendingDelivery(control, directory, workId, provisional, content, false)

    const created = await control.client.session.create({
      body: {
        title: `wanxiangshu-delivery:${operationId}`,
        agent: 'manager',
      },
    })
    const execSessionId = created.data.id
    control.pushHostMessage(execSessionId, userMessage(execSessionId, content))
    assert.equal(control.createdIds.length, 1)
    await control.stop(first)

    const second = await start()
    await activateDurability(second, 'discover-create')
    await waitFor(() => life.inspected(commonDir, workId)?.Delivered === true)
    const settled = life.inspected(commonDir, workId)
    assert.equal(settled.PendingDelivery, false)
    assert.equal(settled.Delivered, true)
    assert.equal(control.createdIds.length, 1, 'recovery must adopt the exact discovered root, not create another session')
    assert.equal(control.prompts.length, 0, 'recovery observes the persisted prompt and never resends it')
    await control.stop(second)
  })
})

test('WHAT[work-048] duplicate exact operation titles are ambiguous and cannot be adopted', async () => {
  await withRestartablePlugin(async (start, directory, control) => {
    const first = await start()
    const workId = 'plan-recovery-ambiguous-create'
    const content = '# ambiguous create acknowledgement\n'
    const { commonDir, operationId } =
      await seedPendingDelivery(control, directory, workId, 'not-yet-known', content, false)
    const title = `wanxiangshu-delivery:${operationId}`

    const firstCreated = await control.client.session.create({ body: { title, agent: 'manager' } })
    const secondCreated = await control.client.session.create({ body: { title, agent: 'manager' } })
    control.pushHostMessage(firstCreated.data.id, userMessage(firstCreated.data.id, content))
    control.pushHostMessage(secondCreated.data.id, userMessage(secondCreated.data.id, content))
    assert.equal(control.createdIds.length, 2)
    await control.stop(first)

    const second = await start()
    await activateDurability(second, 'ambiguous-create')
    await new Promise(resolve => setTimeout(resolve, 80))
    const pending = life.inspected(commonDir, workId)
    assert.equal(pending.PendingDelivery, true)
    assert.equal(pending.PendingExecSessionId, undefined)
    assert.equal(pending.Delivered, false)
    assert.equal(control.createdIds.length, 2)
    assert.equal(control.prompts.length, 0)
    await control.stop(second)
  })
})
