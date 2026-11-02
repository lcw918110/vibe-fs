import assert from 'node:assert/strict'
import { randomUUID } from 'node:crypto'
import { existsSync } from 'node:fs'
import test from 'node:test'
import * as change from '../../../dist/Change/Surface.js'
import * as host from '../../../dist/Change/Host/Surface.js'
import { liveOrchestrator } from '../../verification-system/tests/support/orchestrator-host-harness.mjs'
import { acceptOwner } from '../../interaction-authority/tests/support/authority.mjs'

const published = (jobId, head) => ({ kind: 'Published', jobId, head })
const interrupt = () => change.createVerdictInterrupt()
const join = (mailbox, limit, signal = interrupt()) => change.verdictMailboxJoinAvailable(mailbox, limit, signal)

const assertEmpty = result => {
  assert.equal(result.kind, 'ResultsAvailable')
  assert.equal(result.count, 1)
  assert.deepEqual(result.verdicts, [{ kind: 'Empty', detail: '' }])
}

const assertPublished = (result, head) => {
  assert.equal(result.kind, 'ResultsAvailable')
  assert.equal(result.count, 1)
  assert.deepEqual(result.verdicts, [{ kind: 'Published', detail: head }])
}

test('a rejected mailbox interrupt keeps the same Error and the next waiter receives one publication', async () => {
  const mailbox = change.createVerdictMailbox()
  change.verdictMailboxStartJob(mailbox)
  const firstSignal = interrupt()
  const first = join(mailbox, 8, firstSignal)
  const failure = new Error('controlled join interruption failure')
  change.rejectVerdictInterrupt(firstSignal, failure)
  await assert.rejects(first, error => error === failure)

  const nextSignal = interrupt()
  const next = join(mailbox, 8, nextSignal)
  change.verdictMailboxPublish(mailbox, published('after-rejection', 'head-after-rejection'))
  try {
    const observed = await Promise.race([
      next.then(value => ({ settled: true, value })),
      new Promise(resolve => setImmediate(() => resolve({ settled: false }))),
    ])
    assert.equal(observed.settled, true, 'publication must wake the new waiter without another interruption')
    assertPublished(observed.value, 'head-after-rejection')
    assert.equal(change.verdictMailboxPendingCount(mailbox), 0)
    assertEmpty(await join(mailbox, 8))
  } finally {
    change.fireVerdictInterrupt(nextSignal, 'OperatorAbort')
    await next
  }
})

test('nonpositive mailbox limits preserve queued results on idle and active paths', async () => {
  for (const limit of [0, -7]) {
    const idle = change.createVerdictMailbox()
    assertEmpty(await join(idle, limit))

    const waiting = change.createVerdictMailbox()
    change.verdictMailboxStartJob(waiting)
    const pending = join(waiting, limit)
    change.verdictMailboxPublish(waiting, published('zero-limit', `head-${limit}`))
    assertEmpty(await pending)
    assert.equal(change.verdictMailboxPendingCount(waiting), 1)
    assertPublished(await join(waiting, 8), `head-${limit}`)
    assertEmpty(await join(waiting, 8))

    const queued = change.createVerdictMailbox()
    change.verdictMailboxStartJob(queued)
    change.verdictMailboxStartJob(queued)
    change.verdictMailboxPublish(queued, published('queued-before-join', 'queued-head'))
    assertEmpty(await join(queued, limit))
    assert.equal(change.verdictMailboxPendingCount(queued), 1)
    assertPublished(await join(queued, 8), 'queued-head')
    change.verdictMailboxPublish(queued, published('remaining-job', 'remaining-head'))
    assertPublished(await join(queued, 8), 'remaining-head')
    assertEmpty(await join(queued, 8))
  }
})

test('publication across one to three microtasks still wins over the current mailbox interruption', async () => {
  for (const steps of [1, 2, 3]) {
    const mailbox = change.createVerdictMailbox()
    change.verdictMailboxStartJob(mailbox)
    const signal = interrupt()
    const pending = join(mailbox, 8, signal)
    change.fireVerdictInterrupt(signal, 'OperatorAbort')
    const publication = (async () => {
      for (let step = 0; step < steps; step += 1) await Promise.resolve()
      change.verdictMailboxPublish(mailbox, published(`microtask-${steps}`, `head-${steps}`))
    })()
    const result = await pending
    await publication
    assertPublished(result, `head-${steps}`)
    assert.equal(change.verdictMailboxPendingCount(mailbox), 0)
    assertEmpty(await join(mailbox, 8))
  }
})

test('the real Host publishes and joins one forked job terminal verdict exactly once', async () => {
  const fx = await liveOrchestrator()
  const jobId = `result-mailbox-${randomUUID()}`
  const neverInterrupted = new Promise(() => {})
  try {
    await acceptOwner(fx.journal, 'ses_orphost')
    const admission = await host.forkManagerJob(fx.host, jobId, 'manager', 'Inspect this change.')
    assert.equal(admission.ok, true, admission.error)
    assert.equal(existsSync(admission.value), false, 'the fake Git runner leaves the physical worktree unavailable')
    assert.equal(fx.sessions.calls.filter(([name]) => name === 'CreateChildSession').length, 1)
    assert.equal(fx.sessions.calls.filter(([name]) => name === 'SendPrompt').length, 1)

    const result = await host.joinPublishedAvailable(fx.host, 8, neverInterrupted)
    assert.equal(result.ok, true, result.error)
    assert.equal(result.kind, 'ResultsAvailable')
    assert.equal(result.verdicts.length, 1)
    assert.equal(result.verdicts[0].kind, 'IntegrationFailed')
    assert.equal(result.verdicts[0].jobId, jobId)
    assert.equal(typeof result.verdicts[0].detail, 'string')
    assert.ok(result.verdicts[0].detail.trim().length > 0)

    const after = await host.joinPublishedAvailable(fx.host, 8, neverInterrupted)
    assert.equal(after.ok, true, after.error)
    assert.equal(after.kind, 'ResultsAvailable')
    assert.deepEqual(after.verdicts, [{ kind: 'Empty', jobId: '', detail: '' }])
  } finally {
    await fx.cleanup()
  }
})
