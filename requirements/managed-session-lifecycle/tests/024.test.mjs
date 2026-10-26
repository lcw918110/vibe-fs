import assert from 'node:assert/strict'
import test from 'node:test'
import * as handles from '../../../dist/Execution/Delegation/Handle/Surface.js'
import { Work } from 'wanxiangli/work'

test('WHAT[managed-session-lifecycle-024] active DevOps handle rejects a second physical binding in the production projection', () => {
  const link = { op: 'link', handle: handles.handleIdAgent('devops'), child: 'session-a', agent: 'devops', role: 'DevOps' }
  const first = handles.apply(handles.empty(), link)
  assert.equal(first.ok, true)
  const second = handles.apply(first.state, { ...link, child: 'session-b' })
  assert.equal(second.ok, false)
  assert.equal(handles.read(first.state, link.handle).child, 'session-a')
})

test('WHAT[managed-session-lifecycle-024] road projection retains the supplied logical operator and model binding', () => {
  const work = new Work('road_alpha')
  const opened = work.execute('open', {
    type: 'Review', office: 'Plan', providerRunId: 'run', toolCallId: 'open', updates: [{ key: 'need', value: 'bind' }],
  })
  assert.equal(opened.result.kind, 'Reviewed')
  const bound = work.execute('bind', {
    type: 'BindDevOps', trigger: 'Ask', devopsId: 'devops:road_alpha', target: 'provider/fixed-model:high',
  })
  assert.equal(bound.status, 'Applied', bound.reason)
  assert.equal(bound.result.kind, 'DevOpsBound')
  assert.equal(work.inspect().devopsId, 'devops:road_alpha')
  assert.equal(work.inspect().devopsTarget, 'provider/fixed-model:high')
  const conflict = work.execute('bind-other', {
    type: 'BindDevOps', trigger: 'Ask', devopsId: 'devops:road_alpha', target: 'provider/other-model:low',
  })
  assert.equal(conflict.status, 'Conflict')
  assert.equal(conflict.reason, 'DevOps binding conflict')
  const restored = new Work('road_alpha', work.records())
  assert.equal(restored.inspect().devopsId, 'devops:road_alpha')
  assert.equal(restored.inspect().devopsTarget, 'provider/fixed-model:high')
  assert.equal(restored.history().filter((frame) => frame.events.some((event) => event.type === 'DevOpsBound')).length, 1)
})

test('WHAT[managed-session-lifecycle-024] actual DevOps crash recovery keeps one executable authority, fixed Persona/model, avoids command replay and drains real PTYs (GAP-133, GAP-129)', { todo: 'GAP-133 / GAP-129: actual DevOps crash recovery requires real PTY process drain harness and D18 immutable binding scope decision' }, () => {
  // 规约依据：
  // 1. WHAT[managed-session-lifecycle-024]: 同一道路只有一个 DevOps 逻辑操作员权威，故障恢复可替换物理 Session，至多一个物理权威可执行；沿用初始化绑定的模型和 Persona，不借 resume 重置或切换。
  // 2. 真实 OS 进程及 PTY 排空（真实 TERM/KILL 退出证据）依赖物理执行环境，且 GAP-129 绑定范围（D18 决策）未定，按规约保留 todo 并在原因中明确记录。
})
