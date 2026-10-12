import assert from 'node:assert/strict'
import test from 'node:test'
import * as attention from '../../../dist/Interaction/Attention/Surface.js'

test('WHAT[attention-regulation-006] deferred projection stores natural-language work and consumes only named occurrences', () => {
  let state = attention.empty()
  state = attention.record('session-a', 'first', 'deadline tomorrow; priority high is ordinary text', state)
  state = attention.record('session-a', 'second', 'look later', state)
  state = attention.record('session-b', 'first', 'independent work', state)
  const after = attention.consume('session-a', ['first'], state)
  assert.deepEqual(attention.pending('session-a', after), [{ occurrence: 'second', text: 'look later' }])
  assert.deepEqual(attention.pending('session-b', after), [{ occurrence: 'first', text: 'independent work' }])
  assert.equal(attention.pending('session-a', state).length, 2)
  // Consuming an unknown occurrence is idempotent and never touches other entries.
  const idempotent = attention.consume('session-a', ['missing'], after)
  assert.deepEqual(attention.pending('session-a', idempotent), attention.pending('session-a', after))
})

test('WHAT[attention-regulation-006] GAP-118 actual dependency and capability isolation exclude a hidden scheduler or cognitive workflow engine', () => {
  // WHAT[attention-regulation-006]:
  // 1. 本包只持久化 DeferredWork 的追加投影与消费凭据，不引入阶段、优先级、截止期、依赖图、自动恢复、后台执行器或通用认知状态机
  let state = attention.empty()
  const complexPrompt = 'phase: implementation; depends_on: [task_0, task_1]; priority: critical; deadline: 2026-10-31; retry: auto'
  state = attention.record('session-isolated', 'occ-complex', complexPrompt, state)

  const items = attention.pending('session-isolated', state)
  assert.equal(items.length, 1)
  const item = items[0]

  // 断言项的字段严格只有 occurrence 与 text，没有任何调度器、依赖图、阶段或优先级字段
  assert.deepEqual(Object.keys(item).sort(), ['occurrence', 'text'])
  assert.equal(item.occurrence, 'occ-complex')
  assert.equal(item.text, complexPrompt)

  // 验证不派生任何隐藏结构字段
  assert.equal('stage' in item, false)
  assert.equal('priority' in item, false)
  assert.equal('deadline' in item, false)
  assert.equal('dependencies' in item, false)
  assert.equal('workflow' in item, false)
  assert.equal('scheduler' in item, false)

  // 2. 验证 AttentionSurface 模块接口能力隔离：仅暴露纯投影与生命周期收束函数，绝不暴露执行器、调度器或流程引擎函数
  const exportedKeys = Object.keys(attention)
  assert.deepEqual(exportedKeys.sort(), ['closeLife', 'consume', 'empty', 'pending', 'projection', 'record'])

  // 绝无 hidden scheduler 或 workflow 执行器接口
  for (const forbidden of ['schedule', 'run', 'resume', 'step', 'workflowEngine', 'executeWorkflow', 'taskGraph']) {
    assert.equal(forbidden in attention, false, `forbidden capability ${forbidden} must not exist`)
  }
})
