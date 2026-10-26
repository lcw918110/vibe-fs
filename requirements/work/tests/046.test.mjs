import assert from 'node:assert/strict'
import test from 'node:test'
import { DatabaseSync } from 'node:sqlite'
import { DurableWork } from 'wanxiangli/durable-work'
import { SqliteJournal } from 'wanxiangli/eventstore'
import * as lifecycle from '../../../dist/Mission/Lifecycle/Surface.js'

const surface = lifecycle.LifecycleSurface ?? lifecycle

const review = (work, requestId, updates) =>
  work.execute(requestId, {
    type: 'Review',
    office: 'Plan',
    providerRunId: 'run',
    toolCallId: requestId,
    updates,
  })

test('WHAT[work-046] durable result keeps the caller requestId and only a committed DeliveryRequested may start', () => {
  const database = new DatabaseSync(':memory:')
  const journal = new SqliteJournal(database)
  const work = new DurableWork('work-1', journal)
  try {
    const requestId = 'review-work-1'
    const committed = review(work, requestId, [])
    const seen = surface.observeDurable(requestId, committed)
    assert.equal(seen.requestId, requestId)
    assert.equal(seen.status, 'Committed')
    assert.equal(seen.kind, 'Asked')
    assert.equal(surface.mayStartDelivery(committed), false)

    const replayed = review(work, requestId, [])
    const replayedSeen = surface.observeDurable(requestId, replayed)
    assert.equal(replayedSeen.requestId, requestId)
    assert.notEqual(replayedSeen.requestId, 'review-work-1-replay')
    assert.equal(replayedSeen.status, 'Replayed')
    assert.equal(surface.mayStartDelivery(replayed), false)

    const refused = review(work, 'review-work-1-other', [{ key: 'item', value: 'later' }])
    assert.notEqual(refused.status, 'Committed')
    assert.equal(surface.observeDurable('review-work-1-other', refused).requestId, 'review-work-1-other')
    assert.equal(surface.mayStartDelivery(refused), false)

    for (const status of ['Unknown', 'NotWritten', 'Conflict']) {
      const foreign = { status, revision: 0, result: { kind: 'DeliveryRequested', operationId: 'op' }, reason: status }
      assert.equal(surface.mayStartDelivery(foreign), false)
      assert.equal(surface.observeDurable('same-request', foreign).requestId, 'same-request')
    }

    const incompatible = { status: 'Committed', revision: 1, result: { kind: 'Applied' }, reason: null }
    assert.equal(surface.mayStartDelivery(incompatible), false)
    const requested = {
      status: 'Committed',
      revision: 2,
      result: { kind: 'DeliveryRequested', operationId: 'op-1' },
      reason: null,
    }
    assert.equal(surface.mayStartDelivery(requested), true)
    assert.equal(surface.observeDurable('deliver-1', requested).requestId, 'deliver-1')
    assert.equal(surface.journalPath('/repo/.Herdchestra').endsWith('/wanxiangshu/work.sqlite'), true)
    assert.equal(surface.journalPath('/repo/.Herdchestra').includes('/events/'), false)
  } finally {
    journal.close()
  }
})
