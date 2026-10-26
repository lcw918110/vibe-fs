import assert from 'node:assert/strict'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import test from 'node:test'
import { Work } from 'wanxiangli/work'
import * as lifecycle from '../../../dist/Mission/Lifecycle/Surface.js'

const surface = lifecycle.LifecycleSurface ?? lifecycle

const review = (work, id, updates) =>
  work.execute(id, {
    type: 'Review',
    office: 'Plan',
    providerRunId: 'run',
    toolCallId: id,
    updates,
  })

test('WHAT[work-012] a repeated opening is refused and the first review records the work', () => {
  const work = new Work('work-12')
  const asked = review(work, 'r1', [])
  assert.equal(asked.result.kind, 'Asked')
  assert.equal(asked.result.effect.text, '我需要干什么？')
  assert.deepEqual(work.history()[0].events.map((event) => event.type), ['WorkOpened', 'IncumbencyOpened', 'QuestionRequested'])
  const again = new Work('work-12', work.records()).execute('r1', {
    type: 'Review',
    office: 'Plan',
    providerRunId: 'run',
    toolCallId: 'r1',
    updates: [],
  })
  assert.equal(again.status, 'Replayed')
  assert.equal(work.history().filter((frame) => frame.events.some((event) => event.type === 'WorkOpened')).length, 1)

  const before = work.records()
  assert.throws(() => work.execute('open', { type: 'Open', sequence: ['S1', 'S2', 'S3'] }), /Unknown work command/)
  assert.deepEqual(work.records(), before)
  assert.throws(() => new Work('legacy', ['{"type":"Open","sequence":["S1","S2","S3"],"confirmationCount":1}\n']))
  const doubled = work.records().concat(work.records())
  assert.throws(() => new Work('work-12', doubled))

  const root = mkdtempSync(join(tmpdir(), 'wxs-work-012-'))
  try {
    const recorded = surface.submitReview(root, 'work-12b', 'r1', 'Plan', 'run', 'call-1', [{ key: 'tradeoff', value: 'keep sqlite' }], null, '')
    assert.equal(recorded.kind, 'Reviewed')
    assert.equal(recorded.milestone, '事实与取舍')
  } finally {
    rmSync(root, { recursive: true, force: true })
  }
})
