import assert from 'node:assert/strict'
import test from 'node:test'
import * as handles from '../../../dist/Execution/Delegation/Handle/Surface.js'
import * as relay from '../../../dist/Mission/Relay/Surface.js'

test('WHAT[managed-session-lifecycle-024] active DevOps handle rejects a second physical binding in the production projection', () => {
  const link = { op: 'link', handle: handles.handleIdAgent('devops'), child: 'session-a', agent: 'devops', role: 'DevOps' }
  const first = handles.apply(handles.empty(), link)
  assert.equal(first.ok, true)
  const second = handles.apply(first.state, { ...link, child: 'session-b' })
  assert.equal(second.ok, false)
  assert.equal(handles.read(first.state, link.handle).child, 'session-a')
})

test('WHAT[managed-session-lifecycle-024] road projection retains the supplied logical operator and model binding', () => {
  const road = relay.openRoad('road_alpha', 'rev_1', 'msg_1', 'inc_1', 'snap_1')
  const bound = relay.bindRoadDevOps(road, 'road_alpha', 'devops:road_alpha', 'provider/fixed-model:high')
  assert.equal(bound.ok, true)
  const observed = relay.roadDevOps(bound.state, 'road_alpha')
  assert.equal(observed.devopsId, 'devops:road_alpha')
  assert.equal(observed.modelTarget, 'provider/fixed-model:high')
})

test('WHAT[managed-session-lifecycle-024] actual DevOps crash recovery keeps one executable authority, fixed Persona/model, avoids command replay and drains real PTYs (GAP-133, GAP-129)', { todo: 'GAP-133 / GAP-129: actual DevOps crash recovery requires real PTY process drain harness and D18 immutable binding scope decision' }, () => {
  // 规约依据：
  // 1. WHAT[managed-session-lifecycle-024]: 同一道路只有一个 DevOps 逻辑操作员权威，故障恢复可替换物理 Session，至多一个物理权威可执行；沿用初始化绑定的模型和 Persona，不借 resume 重置或切换。
  // 2. 真实 OS 进程及 PTY 排空（真实 TERM/KILL 退出证据）依赖物理执行环境，且 GAP-129 绑定范围（D18 决策）未定，按规约保留 todo 并在原因中明确记录。
})
