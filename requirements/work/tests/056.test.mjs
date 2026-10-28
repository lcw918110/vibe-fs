// 056 — DurableWork:外部效果已发生、confirm 记录未落账的重开恢复窗口。
// 边界:wanxiangli/durable-work 公开构造器 + wanxiangli/eventstore 真实 SQLite journal,
// 不绕过内部状态;外部调用按真实端口名计数(createSession / sendDraft)。
// 本文件只声明测试,不在此运行;运行验证归 DevOps。
// 「未落账」用 journal.append 抛错注入,覆盖 confirm 写入前的持久化窗口;SIGKILL 场景由 assembly 003 另行覆盖。

import test from 'node:test'
import assert from 'node:assert/strict'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { DatabaseSync } from 'node:sqlite'
import { DurableWork } from 'wanxiangli/durable-work'
import { SqliteJournal } from 'wanxiangli/eventstore'

const MILESTONES = ['理解与路径', '流程与伪代码', '待执行']

const review = (requestId, updates, extra = {}) => ({
  type: 'Review',
  office: 'Plan',
  providerRunId: 'run',
  toolCallId: requestId,
  updates,
  ...extra,
})

const countingPorts = (overrides = {}) => {
  const counts = { createSession: 0, sendDraft: 0 }
  const calls = []
  const ports = {
    counts,
    calls,
    ask() { return 'Parked' },
    dispatch() { return 'Sent' },
    createSession(effect) {
      counts.createSession += 1
      calls.push({ port: 'createSession', id: effect.id })
      return { status: 'SessionCreated', execSessionId: 'session-' + counts.createSession }
    },
    sendDraft(effect) {
      counts.sendDraft += 1
      calls.push({ port: 'sendDraft', id: effect.id })
      return 'Admitted'
    },
    stop() { return 'Requested' },
    ...overrides,
  }
  return ports
}

const open = (databasePath, effects) => {
  const journal = new SqliteJournal(new DatabaseSync(databasePath))
  return { journal, work: new DurableWork('work-a', journal, effects) }
}

const recordCount = (rig) => rig.journal.read('work-a').length

// 推进到「待执行」:每轮 增补 -> 交付转出 -> 空评审推进,与 kill-worker 的启动路径一致。
const reachReady = (work) => {
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

// 窗口一:会话已真实创建,SessionCreated 的 confirm 在写入 journal 前失败(append 抛错注入)。
// 重开后同一效果身份重开会话,投递恰好一次,Admitted 落账;再重开不重发。
test('WHAT[work-056] createSession 已发生但 confirm 未落账:重开后按同一效果身份重开会话,投递恰好一次', (t) => {
  const directory = mkdtempSync(join(tmpdir(), 'wanxiangli-work-056-'))
  t.after(() => rmSync(directory, { recursive: true, force: true }))
  const databasePath = join(directory, 'work.sqlite')

  const ports = countingPorts()
  const first = open(databasePath, ports)
  reachReady(first.work)
  const base = recordCount(first)

  const realAppend = first.journal.append.bind(first.journal)
  let cut = false
  first.journal.append = (stream, revision, requestId, body) => {
    if (requestId.startsWith('confirm:') && requestId.includes(':SessionCreated:')) {
      cut = true
      throw new Error('Lost confirm acknowledgement')
    }
    return realAppend(stream, revision, requestId, body)
  }

  const started = first.work.execute('launch-1', review('launch-1', [], { digest: '待执行' }))
  assert.equal(cut, true)
  assert.equal(started.status, 'Unknown', JSON.stringify(started))
  assert.equal(ports.counts.createSession, 1)
  assert.equal(ports.counts.sendDraft, 0)
  assert.equal(recordCount(first), base + 1)
  const originalId = ports.calls[0].id

  // 原组合已封存:不能原地重试,只能重开观察。
  assert.equal(first.work.recover().next, 'ObserveCommit')
  assert.throws(() => first.work.execute('launch-1', review('launch-1', [], { digest: '待执行' })), /unknown/)

  const reopened = open(databasePath, ports)
  reopened.work.recover()
  assert.equal(ports.counts.createSession, 2)
  assert.equal(ports.counts.sendDraft, 1)
  assert.equal(recordCount(reopened), base + 3)
  const createdIds = ports.calls.filter((call) => call.port === 'createSession').map((call) => call.id)
  assert.equal(createdIds.length, 2)
  assert.equal(createdIds[0], originalId)
  assert.equal(createdIds[1], originalId)

  const settled = open(databasePath, ports)
  settled.work.recover()
  assert.equal(ports.counts.createSession, 2)
  assert.equal(ports.counts.sendDraft, 1)
  assert.equal(recordCount(settled), base + 3)
  assert.equal(settled.work.inspect().launches[0].status, 'Admitted')
})

// 对照:会话端口返回 Unknown 且 Unknown 的 confirm 已落账。重开零外部调用、零新记录。
test('WHAT[work-056] createSession 返回 Unknown 且已落账:重开不再触发任何外部调用', (t) => {
  const directory = mkdtempSync(join(tmpdir(), 'wanxiangli-work-056-'))
  t.after(() => rmSync(directory, { recursive: true, force: true }))
  const databasePath = join(directory, 'work.sqlite')

  const ports = countingPorts({
    createSession(effect) {
      ports.counts.createSession += 1
      ports.calls.push({ port: 'createSession', id: effect.id })
      return { status: 'Unknown' }
    },
  })
  const rig = open(databasePath, ports)
  reachReady(rig.work)
  const base = recordCount(rig)

  const started = rig.work.execute('launch-1', review('launch-1', [], { digest: '待执行' }))
  assert.equal(started.status, 'Committed', JSON.stringify(started))
  assert.equal(ports.counts.createSession, 1)
  assert.equal(ports.counts.sendDraft, 0)
  assert.equal(recordCount(rig), base + 2)

  const reopened = open(databasePath, ports)
  reopened.work.recover()
  assert.equal(ports.counts.createSession, 1)
  assert.equal(ports.counts.sendDraft, 0)
  assert.equal(reopened.work.recover().effect, null)
  assert.equal(recordCount(reopened), base + 2)
  assert.equal(reopened.work.inspect().launches[0].status, 'Unknown')
})

// 窗口二:投递已真实发生,Admitted 的 confirm 在写入 journal 前失败(append 抛错注入)。重开后投递重发一次并落账。
test('WHAT[work-056] sendDraft 已发生但 Admitted confirm 未落账:重开后投递重发一次并落账', (t) => {
  const directory = mkdtempSync(join(tmpdir(), 'wanxiangli-work-056-'))
  t.after(() => rmSync(directory, { recursive: true, force: true }))
  const databasePath = join(directory, 'work.sqlite')

  const ports = countingPorts()
  const first = open(databasePath, ports)
  reachReady(first.work)
  const base = recordCount(first)

  const realAppend = first.journal.append.bind(first.journal)
  let cut = false
  first.journal.append = (stream, revision, requestId, body) => {
    if (requestId.startsWith('confirm:') && requestId.includes(':Admitted:')) {
      cut = true
      throw new Error('Lost admit acknowledgement')
    }
    return realAppend(stream, revision, requestId, body)
  }

  const started = first.work.execute('launch-1', review('launch-1', [], { digest: '待执行' }))
  assert.equal(cut, true)
  assert.equal(started.status, 'Unknown', JSON.stringify(started))
  assert.equal(ports.counts.createSession, 1)
  assert.equal(ports.counts.sendDraft, 1)
  assert.equal(recordCount(first), base + 2)

  const reopened = open(databasePath, ports)
  reopened.work.recover()
  assert.equal(ports.counts.createSession, 1)
  assert.equal(ports.counts.sendDraft, 2)
  assert.equal(recordCount(reopened), base + 3)
  const sentIds = ports.calls.filter((call) => call.port === 'sendDraft').map((call) => call.id)
  assert.equal(sentIds.length, 2)
  assert.equal(sentIds[0], sentIds[1])
})

// 对照:投递端口返回 Unknown 且 Unknown 的 confirm 已落账。重开不重发。
test('WHAT[work-056] sendDraft 返回 Unknown 且已落账:重开不再重发投递', (t) => {
  const directory = mkdtempSync(join(tmpdir(), 'wanxiangli-work-056-'))
  t.after(() => rmSync(directory, { recursive: true, force: true }))
  const databasePath = join(directory, 'work.sqlite')

  const ports = countingPorts({
    sendDraft(effect) {
      ports.counts.sendDraft += 1
      ports.calls.push({ port: 'sendDraft', id: effect.id })
      return 'Unknown'
    },
  })
  const rig = open(databasePath, ports)
  reachReady(rig.work)
  const base = recordCount(rig)

  const started = rig.work.execute('launch-1', review('launch-1', [], { digest: '待执行' }))
  assert.equal(started.status, 'Committed', JSON.stringify(started))
  assert.equal(ports.counts.createSession, 1)
  assert.equal(ports.counts.sendDraft, 1)
  assert.equal(recordCount(rig), base + 3)

  const reopened = open(databasePath, ports)
  reopened.work.recover()
  assert.equal(ports.counts.createSession, 1)
  assert.equal(ports.counts.sendDraft, 1)
  assert.equal(recordCount(reopened), base + 3)
})

// 对照:会话与投递都已确认落账。重开零外部调用、零新记录。
test('WHAT[work-056] 会话与投递均已确认落账:重开零外部调用', (t) => {
  const directory = mkdtempSync(join(tmpdir(), 'wanxiangli-work-056-'))
  t.after(() => rmSync(directory, { recursive: true, force: true }))
  const databasePath = join(directory, 'work.sqlite')

  const ports = countingPorts()
  const rig = open(databasePath, ports)
  reachReady(rig.work)
  const base = recordCount(rig)

  const started = rig.work.execute('launch-1', review('launch-1', [], { digest: '待执行' }))
  assert.equal(started.status, 'Committed', JSON.stringify(started))
  assert.equal(ports.counts.createSession, 1)
  assert.equal(ports.counts.sendDraft, 1)
  assert.equal(recordCount(rig), base + 3)

  const reopened = open(databasePath, ports)
  reopened.work.recover()
  assert.equal(ports.counts.createSession, 1)
  assert.equal(ports.counts.sendDraft, 1)
  assert.equal(reopened.work.recover().effect, null)
  assert.equal(recordCount(reopened), base + 3)
})
