import assert from 'node:assert/strict'
import test from 'node:test'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { isEligible, isReviewFinished } from '../../../../dist/Mission/Planning/Surface.js'
import * as hostModule from '../../../../dist/Mission/Lifecycle/DurableWorkHost.js'

const host = hostModule.DurableWorkHost ?? hostModule
const submitReview = host.submitReview
const reviewEffects = host.reviewEffects
const inspected = host.inspected

function countingEffects() {
  const counts = { park: 0, send: 0, open: 0, draft: 0, stop: 0 }
  const seen = []
  const effects = reviewEffects({
    park: async ({ text }) => {
      counts.park += 1
      seen.push(['park', text])
      return { ok: true }
    },
    send: async ({ text }) => {
      counts.send += 1
      seen.push(['send', text])
      return { ok: true }
    },
    open: async ({ digest }) => {
      counts.open += 1
      seen.push(['open', digest])
      return { ok: true, sessionId: 'manager-1' }
    },
    draft: async ({ sessionId, digest }) => {
      counts.draft += 1
      seen.push(['draft', sessionId, digest])
      return { ok: true }
    },
    stop: async ({ incumbencyId }) => {
      counts.stop += 1
      seen.push(['stop', incumbencyId])
      return { ok: true }
    },
  })
  return { counts, seen, effects }
}

function review(root, effects, workId, id, office, updates) {
  return submitReview(root, workId, id, office, 'run', 'call-' + id, updates, null, '', effects)
}

test('WHAT[work-003] one empty review parks once, and the wait is the answer', async () => {
  const root = mkdtempSync(join(tmpdir(), 'wxs-review-054-ask-'))
  const gate = countingEffects()
  try {
    const asked = await review(root, gate.effects, 'ask-work', 'ask-1', 'Plan', [])
    assert.equal(asked.status, 'Committed', asked.reason)
    assert.equal(asked.kind, 'Asked')
    assert.equal(asked.milestone, '事实与取舍')
    assert.equal(gate.counts.park, 1)
    assert.deepEqual(gate.seen[0], ['park', '我需要干什么？'])
    assert.equal(gate.counts.send, 0)
    assert.equal(gate.counts.open, 0)
    assert.equal(gate.counts.stop, 0)

    const again = await review(root, gate.effects, 'ask-work', 'ask-1', 'Plan', [])
    assert.equal(again.status, 'Replayed')
    assert.equal(gate.counts.park, 1)

    const view = inspected(root, 'ask-work')
    assert.equal(view.Wait, 'AwaitOperatorAnswer')
    assert.equal(view.Stage, '事实与取舍')
    assert.equal(view.Reviewed, false)
    assert.equal(view.ReviewOutcome == null, true)
    assert.equal(isEligible(root, 'ask-work', view.ActiveId), false)
    assert.equal(isReviewFinished(root, 'ask-work', view.ActiveId), false)
    console.log(`park external ${gate.counts.park}`)
  } finally {
    rmSync(root, { recursive: true, force: true })
  }
})

test('WHAT[work-003] a cleared ledger sends the successor prompt once', async () => {
  const root = mkdtempSync(join(tmpdir(), 'wxs-review-054-relay-'))
  const gate = countingEffects()
  try {
    const recorded = await review(root, gate.effects, 'relay-work', 'r1', 'Plan', [{ key: 'gap', value: 'fix' }])
    assert.equal(recorded.kind, 'Reviewed')
    const openView = inspected(root, 'relay-work')
    assert.equal(openView.Wait, 'AwaitReview')
    assert.equal(openView.Stage, '事实与取舍')
    assert.equal(isEligible(root, 'relay-work', openView.ActiveId), true)
    assert.equal(gate.counts.send, 0)

    const relayed = await review(root, gate.effects, 'relay-work', 'relay-1', 'Plan', [{ key: 'gap', value: null }])
    assert.equal(relayed.status, 'Committed', relayed.reason)
    assert.equal(relayed.kind, 'Relayed')
    assert.equal(gate.counts.send, 1)
    assert.equal(gate.seen.at(-1)[0], 'send')
    assert.match(gate.seen.at(-1)[1], /继任「事实与取舍」/)

    const again = await review(root, gate.effects, 'relay-work', 'relay-1', 'Plan', [{ key: 'gap', value: null }])
    assert.equal(again.status, 'Replayed')
    assert.equal(gate.counts.send, 1)
    console.log('relay external 1')
  } finally {
    rmSync(root, { recursive: true, force: true })
  }
})

test('WHAT[work-003] cleanup stops the provider attempt once', async () => {
  const root = mkdtempSync(join(tmpdir(), 'wxs-review-054-stop-'))
  const gate = countingEffects()
  try {
    assert.equal((await review(root, gate.effects, 'stop-work', 'r1', 'Manager', [{ key: 'job', value: 'run' }])).kind, 'Reviewed')
    assert.equal((await review(root, gate.effects, 'stop-work', 'r2', 'Manager', [{ key: 'job', value: null }])).kind, 'Relayed')
    const advanced = await review(root, gate.effects, 'stop-work', 'r3', 'Manager', [])
    assert.equal(advanced.kind, 'Advanced')
    assert.equal(advanced.milestone, '收尾')
    const cleanup = inspected(root, 'stop-work')
    assert.equal(cleanup.Stage, '收尾')
    assert.equal(gate.counts.stop, 0)

    const finished = await review(root, gate.effects, 'stop-work', 'finish', 'Manager', [])
    assert.equal(finished.status, 'Committed', finished.reason)
    assert.equal(finished.kind, 'Completed')
    assert.equal(finished.milestone, '已完成')
    assert.equal(gate.counts.stop, 1)
    assert.equal(gate.seen.at(-1)[0], 'stop')
    assert.equal(typeof gate.seen.at(-1)[1], 'string')
    assert.equal(gate.seen.at(-1)[1].length > 0, true)

    const again = await review(root, gate.effects, 'stop-work', 'finish', 'Manager', [])
    assert.equal(again.status, 'Replayed')
    assert.equal(gate.counts.stop, 1)
    console.log(`stop external ${gate.counts.stop}`)
  } finally {
    rmSync(root, { recursive: true, force: true })
  }
})

test('WHAT[work-003] the ready milestone opens one manager session and sends one draft', async () => {
  const root = mkdtempSync(join(tmpdir(), 'wxs-review-054-launch-'))
  const gate = countingEffects()
  try {
    for (const milestone of ['理解与路径', '流程与伪代码', '待执行']) {
      assert.equal((await review(root, gate.effects, 'launch-work', `keep-${milestone}`, 'Plan', [{ key: 'item', value: 'keep' }])).kind, 'Reviewed')
      assert.equal((await review(root, gate.effects, 'launch-work', `clear-${milestone}`, 'Plan', [{ key: 'item', value: null }])).kind, 'Relayed')
      const advanced = await review(root, gate.effects, 'launch-work', `next-${milestone}`, 'Plan', [])
      assert.equal(advanced.kind, 'Advanced', milestone)
      assert.equal(advanced.milestone, milestone)
    }
    assert.equal(inspected(root, 'launch-work').Stage, '待执行')
    const beforeOpen = gate.counts.open
    const beforeDraft = gate.counts.draft
    const started = await review(root, gate.effects, 'launch-work', 'launch-1', 'Plan', [])
    assert.equal(started.status, 'Committed', started.reason)
    assert.equal(started.kind, 'Launched')
    assert.equal(gate.counts.open, beforeOpen + 1)
    assert.equal(gate.counts.draft, beforeDraft + 1)
    assert.deepEqual(gate.seen.at(-2), ['open', '待执行'])
    assert.deepEqual(gate.seen.at(-1), ['draft', 'manager-1', '待执行'])
    assert.equal(inspected(root, 'launch-work').Stage, '待执行')

    const again = await review(root, gate.effects, 'launch-work', 'launch-1', 'Plan', [])
    assert.equal(again.status, 'Replayed')
    assert.equal(gate.counts.open, beforeOpen + 1)
    assert.equal(gate.counts.draft, beforeDraft + 1)
    console.log('launch external 1')
  } finally {
    rmSync(root, { recursive: true, force: true })
  }
})
