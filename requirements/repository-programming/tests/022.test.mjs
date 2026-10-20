import assert from 'node:assert/strict'
import test from 'node:test'
import {
  generate,
  isGeneratedToolName,
  memberBinding,
} from '../../../dist/Repository/Programming/Js/GeneratorSurface.js'
import { permissions as rolePermissions } from '../../../dist/Participant/Persona/OfficeCapabilitySurface.js'

const caps = (...permissions) => permissions

const surface = (role, permissionNames) => generate(role, caps(...permsOf(permissionNames)), 'en')

const memberNames = (s) => s.members.map((fragment) => fragment.memberName)

const isNone = (value) => value === null

const isSome = (value) => value !== null

const PERMISSION_NAMES = [
  'Fork', 'Join', 'Horizon', 'TodoWrite', 'Fission', 'Read', 'Write', 'Edit', 'Fetch', 'Glob', 'Grep', 'Move',
  'Remove', 'Inspect', 'Behavior', 'Exec', 'Pty', 'Network', 'ReviewAssessment',
]

const toolPermissionByName = Object.fromEntries(PERMISSION_NAMES.map((n) => [n, n]))

const ToolPermission = toolPermissionByName

const permsOf = (names) => names.map((n) => toolPermissionByName[n])

const fsPermissionsOf = (role) =>
  rolePermissions(role.toLowerCase()).filter((n) => ['Read', 'Write', 'Glob', 'Move', 'Remove'].includes(n))

const MEMBERS_BY_PERMISSION = {
  Read: ['cat', 'stat', 'whereAll', 'where'],
  Glob: ['ls'],
  Grep: ['rg'],
  Write: ['write', 'mkdir'],
  Move: ['mv'],
  Remove: ['rm'],
}

const BINDING_BY_MEMBER = {
  write: 'js.write',
  rm: 'js.rm',
  mv: 'js.mv',
  cat: 'js.cat',
  ls: 'js.ls',
  rg: 'js.rg',
  stat: 'js.stat',
  mkdir: 'js.mkdir',
  whereAll: 'js.whereAll',
  where: 'js.where',
}

const MEMBER_ORDER = ['write', 'rm', 'mv', 'cat', 'ls', 'rg', 'stat', 'mkdir', 'whereAll', 'where']

const layersOf = (s) =>
  Object.fromEntries(
    s.members.map((fragment) => [
      fragment.memberName,
      {
        description: fragment.description,
        example: fragment.canonicalExample,
        binding: fragment.runtimeBindingKey,
        inBaseClass: s.baseClassSource.includes(`this._api.${fragment.runtimeBindingKey}`),
        inDescription: s.description.includes(fragment.memberName),
        inExamples: s.examples.some((example) => example.includes(fragment.memberName)),
      },
    ]),
  )

test('WHAT[repository-programming-022] JS_description_is_action_first_then_teaches_paid_failure_memory', () => {
  const coder = surface('Coder', ['Read', 'Write', 'Edit', 'Glob', 'Grep'])
  const lessonText = coder.description.replace(/\s+/g, ' ')

  assert.equal(
    lessonText.startsWith('WARNING: You may be about to turn a bounded filesystem task into a self-inflicted repair job.'),
    true,
    'the first screen must provide the boundary warning and primitive contract',
  )

  for (const affordance of [
    'Read with `cat(path)`',
    'Locate with `where` / `whereAll`',
    'Recompose with slices',
    'Find and replace is a composition, not a primitive',
    'Batch replacement is one whereAll plus a reverse loop. Append is cat + write.',
  ]) {
    assert.equal(lessonText.includes(affordance), true, `action-first affordance missing: ${affordance}`)
  }

  for (const lesson of [
    'The Host already owns this boundary',
    'There are only two acceptable moves',
    'Suspicion is enough to trigger verification',
    'only facts earn permission to continue',
    'use the primitive that owns it',
    'prove it cannot express the job before dropping lower',
    'Precommitment matters',
    'does not get to renegotiate those rules after seeing an inconvenient result',
    'Numbers, required sentinels, and section counts outrank the story',
    'Do not let familiarity impersonate facts',
    'facts beat confidence',
    'Do not invent a third category called "probably fine"',
    'I have already paid for this mistake',
    'roughly 8k lines',
    'roughly 31k lines',
    'Raw search was finding candidates, not owning structure',
    'second and third cleanup programs',
    'cheap invariants',
    'throw before return',
    'zero committed mutations',
    'STOP SIGNAL',
    'Do not confuse "I can reimplement this" with "I should reimplement this"',
  ]) {
    assert.equal(lessonText.includes(lesson), true, `paid-failure lesson missing: ${lesson}`)
  }

  const zh = generate(
    'Coder',
    caps(...permsOf(['Read', 'Write', 'Edit', 'Glob', 'Grep'])),
    'zh-CN',
  ).description.replace(/\s+/g, ' ')

  assert.equal(
    zh.startsWith('警告：你正准备把一个本来有边界保护的文件任务，亲手变成一场返工事故。'),
    true,
    '中文第一屏必须先给边界合同与原语选择',
  )

  for (const affordance of [
    '用 `cat(path)` 读取',
    '用 `where` / `whereAll` 定位',
    '用切片重组',
    '查找替换是组合，不是原语',
    '批量替换 = 一次 whereAll 加反向循环。append = cat + write。',
  ]) {
    assert.equal(zh.includes(affordance), true, `中文 action-first affordance 缺失: ${affordance}`)
  }

  for (const lesson of [
    'Host 已经拥有这层边界',
    '只有两个合格动作',
    '只要起疑，就触发验证',
    '只有事实才能换来继续执行的资格',
    '使用已经拥有它的 primitive',
    '先证明它表达不了任务，再往下降一层',
    '先承诺，再动手',
    '就没有资格临时改口',
    '优先于你在坏结果出现后给自己编的解释',
    '不要让熟悉冒充事实',
    '事实 > 自信',
    '不要再发明第三类叫「大概没问题」',
    '我为这个错误付过代价',
    '约 8k 行',
    '约 31k 行',
    '给前一个 program 擦屁股',
    '停止信号',
    '别把「我也能自己重写一遍」误当成「我应该自己重写一遍」',
  ]) {
    assert.equal(zh.includes(lesson), true, `中文现身说法缺失: ${lesson}`)
  }
})
