// 057 — createSession 端口失败的诚实映射：无法区分「确定没建」与「不知道建没建」的失败报 Unknown，
// 不再冒充 NotStarted。宿主明确应答拒绝才报 NotStarted；已落账 Unknown 重开不重发。
// 边界:dist/Mission/Lifecycle/DurableWorkHost.js 的 reviewEffects 组装层 + wanxiangli/durable-work 真实 SQLite journal。
// 本文件只声明测试，不在此运行；运行验证归 DevOps。

import assert from 'node:assert/strict'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { DatabaseSync } from 'node:sqlite'
import test from 'node:test'
import { SqliteJournal } from 'wanxiangli/eventstore'
import * as hostModule from '../../../dist/Mission/Lifecycle/DurableWorkHost.js'
import { DurableWork } from 'wanxiangli/durable-work'

const host = hostModule.DurableWorkHost ?? hostModule

const withRoot = async (body) => {
  const root = mkdtempSync(join(tmpdir(), 'wxs-work-057-'))
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

// 推进到「待执行」：与 039 相同的三轮 增补 -> 交付转出 -> 空评审推进。
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
}

const launch = (root, workId, requestId, call, digest, effects, blockers = null) =>
  host.submitReview(root, workId, requestId, 'Plan', 'run', call, [], blockers, digest, effects)

// 传输超时：open 端口失败但宿主从未应答（无 refused 标记）——不知道建没建。
// 组装层必须报 Unknown，落账 EffectConfirmed Unknown；重开零重调。
test('WHAT[work-057] createSession 传输超时（宿主未应答）报 Unknown：不写 DeliveryRejected，已落账 Unknown 重开零重调', async () => {
  await withRoot(async (root) => {
    const workId = 'work-57-timeout'
    const content = '# Plan content'
    await reachReady(root, workId)

    let opens = 0
    const effects = host.reviewEffects({
      park: async () => ({ ok: true }),
      send: async () => ({ ok: true }),
      open: async () => {
        opens += 1
        // 传输超时：宿主从未应答，建没建无从区分。
        return { ok: false, reason: 'fetch failed: timeout' }
      },
      draft: async () => ({ ok: true }),
      stop: async () => ({ ok: true }),
    })

    const started = await launch(root, workId, 'launch-timeout', 'launch-timeout-call', content, effects)
    assert.equal(started.status, 'Committed', started.reason)
    assert.equal(started.kind, 'Launched')
    assert.equal(opens, 1)

    const events = eventsOf(root, workId)
    assert.equal(events.some((event) => event.type === 'DeliveryRejected'), false, '超时不得写成明确拒绝')
    assert.equal(events.some((event) => event.type === 'EffectConfirmed' && event.outcome === 'Unknown'), true, '超时必须落账 Unknown')
    const row = host.inspect(root, workId).launches[0]
    assert.equal(row.status, 'Unknown')
    assert.equal(row.execSessionId, null)
    assert.equal(host.inspected(root, workId).Delivered, false)

    // 已落账 Unknown：重开不再调 open 端口。
    const reopened = await launch(root, workId, 'launch-timeout', 'launch-timeout-call', content, effects)
    assert.equal(reopened.status, 'Replayed')
    assert.equal(opens, 1, '已落账 Unknown 不重发 createSession')
    assert.equal(eventsOf(root, workId).filter((event) => event.type === 'EffectConfirmed' && event.outcome === 'Unknown').length, 1)
  })
})

// 明确拒答：open 端口失败且宿主明确应答拒绝（refused: true）——确定没建。
// 组装层仍报 NotStarted，落账 DeliveryRejected。
test('WHAT[work-057] createSession 宿主明确应答拒绝（refused）报 NotStarted：落账 DeliveryRejected，不发草稿', async () => {
  await withRoot(async (root) => {
    const workId = 'work-57-refused'
    const content = '# Plan content'
    await reachReady(root, workId)

    let opens = 0
    let drafts = 0
    const effects = host.reviewEffects({
      park: async () => ({ ok: true }),
      send: async () => ({ ok: true }),
      open: async () => {
        opens += 1
        // 宿主应答了 HTTP 409：确定没建。
        return { ok: false, reason: 'HTTP 409: session refused', refused: true }
      },
      draft: async () => {
        drafts += 1
        return { ok: true }
      },
      stop: async () => ({ ok: true }),
    })

    const refused = await launch(root, workId, 'launch-refused', 'launch-refused-call', content, effects)
    assert.equal(refused.status, 'Committed', refused.reason)
    assert.equal(refused.kind, 'Launched')
    assert.equal(opens, 1)
    assert.equal(drafts, 0, '明确拒绝后不发草稿')

    const events = eventsOf(root, workId)
    assert.equal(events.some((event) => event.type === 'DeliveryRejected' && event.reason === 'HTTP 409: session refused'), true)
    const row = host.inspect(root, workId).launches[0]
    assert.equal(row.status, 'Rejected')
    assert.equal(row.execSessionId, null)

    // 拒绝后新一次 launch 可以重新开（新 requestId），与 039 的拒绝重试一致。
    let retryOpens = 0
    const retry = await launch(root, workId, 'launch-refused-retry', 'launch-refused-retry-call', content, host.reviewEffects({
      park: async () => ({ ok: true }),
      send: async () => ({ ok: true }),
      open: async () => {
        retryOpens += 1
        return { ok: true, sessionId: 'exec-after-refusal' }
      },
      draft: async () => ({ ok: true }),
      stop: async () => ({ ok: true }),
    }))
    assert.equal(retry.kind, 'Launched', retry.reason)
    assert.equal(retryOpens, 1)
    assert.equal(host.inspect(root, workId).launches.filter((row) => row.status === 'Admitted').length, 1)
  })
})



test('WHAT[work-057] createSession 传输超时映射为 Unknown（durable-work 直连）：落账后重开零外部调用', async () => {
  const directory = mkdtempSync(join(tmpdir(), 'wanxiangli-work-057-'))
  try {
    const databasePath = join(directory, 'work.sqlite')
    const workId = 'work-57-direct'
    const MILESTONES = ['理解与路径', '流程与伪代码', '待执行']
    const review = (requestId, updates, extra = {}) => ({
      type: 'Review',
      office: 'Plan',
      providerRunId: 'run',
      toolCallId: requestId,
      updates,
      ...extra,
    })
    const reachReadyCore = (work) => {
      let serial = 0
      for (const milestone of MILESTONES) {
        const keep = work.execute('keep-' + serial, review('keep-' + serial, [{ key: 'item', value: 'keep' }]))
        assert.equal(keep.status, 'Committed', JSON.stringify(keep))
        const clear = work.execute('clear-' + serial, review('clear-' + serial, [{ key: 'item', value: null }]))
        assert.equal(clear.status, 'Committed', JSON.stringify(clear))
        const advanced = work.execute('advance-' + serial, review('advance-' + serial, []))
        assert.equal(advanced.result.milestone, milestone, JSON.stringify(advanced))
        serial += 1
      }
    }
    const openRig = (effects) => {
      const journal = new SqliteJournal(new DatabaseSync(databasePath))
      return { journal, work: new DurableWork(workId, journal, effects) }
    }

    let creates = 0
    const effects = {
      ask() { return 'Parked' },
      dispatch() { return 'Sent' },
      createSession() {
        creates += 1
        return { status: 'Unknown' }
      },
      sendDraft() { return 'Admitted' },
      stop() { return 'Requested' },
    }

    const rig = openRig(effects)
    reachReadyCore(rig.work)
    const base = rig.journal.read(workId).length

    const started = rig.work.execute('launch-1', review('launch-1', [], { digest: '待执行' }))
    assert.equal(started.status, 'Committed', JSON.stringify(started))
    assert.equal(creates, 1)

    const reopened = openRig(effects)
    reopened.work.recover()
    assert.equal(creates, 1, '已落账 Unknown 不重发 createSession')
    assert.equal(reopened.work.recover().effect, null)
    assert.equal(reopened.journal.read(workId).length, base + 2)
    assert.equal(reopened.work.inspect().launches[0].status, 'Unknown')
  } finally {
    rmSync(directory, { recursive: true, force: true })
  }
})
