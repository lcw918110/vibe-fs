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
  'Remove', 'Inspect', 'Behavior', 'Exec', 'Pty', 'Network', 'ReviewAssessment', 'Chronicle',
  'Finality', 'BashHoneypot',
]

const toolPermissionByName = Object.fromEntries(PERMISSION_NAMES.map((n) => [n, n]))

const ToolPermission = toolPermissionByName

const permsOf = (names) => names.map((n) => toolPermissionByName[n])

const fsPermissionsOf = (role) =>
  rolePermissions(role.toLowerCase()).filter((n) => ['Read', 'Write', 'Edit', 'Glob', 'Grep'].includes(n))

const MEMBERS_BY_PERMISSION = {
  Read: ['file'],
  Glob: ['glob'],
  Grep: ['grep'],
  Edit: ['edit', 'rewrite'],
  Write: ['write'],
}

const BINDING_BY_MEMBER = {
  file: 'js.read',
  glob: 'js.glob',
  grep: 'js.grep',
  edit: 'js.edit',
  rewrite: 'js.edit',
  write: 'js.write',
}

const MEMBER_ORDER = ['file', 'glob', 'grep', 'edit', 'rewrite', 'write']

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
    lessonText.startsWith('For ordinary edits, start with edit(path, changes).'),
    true,
    'the first screen must provide the default action before the cautionary manual',
  )

  for (const affordance of [
    'Decision ladder',
    'one exact replacement, insertion, deletion, or repeated replacement',
    'one edit call per path',
    '{ find, put, all? }',
    'use file(matches) + text() + rewrite() for structural recomposition',
    'Near matches are diagnostics, never write authority',
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
    'The lower-level technique carries the burden of proof',
    'facts beat confidence',
    'Do not invent a third category called "probably fine"',
    'I have already paid for this mistake',
    'roughly 8k lines',
    'roughly 31k lines',
    'grep was finding candidates, not owning structure',
    'ordered anchors',
    'second and third cleanup programs',
    'cheap invariants',
    'throw before return',
    'zero committed mutations',
    'If you are about to calculate structural boundaries by hand',
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
    zh.startsWith('普通编辑默认从 edit(path, changes) 开始。'),
    true,
    '中文第一屏必须先给动作，再给风险记忆',
  )

  for (const affordance of [
    '选择阶梯',
    '一次精确替换、插入、删除或重复替换',
    '每个路径只调用一次 edit',
    '{ find, put, all? }',
    '结构重组才使用 file(matches) + text() + rewrite()',
    '近似匹配只产生诊断，绝不获得写权限',
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
    '别让熟悉感冒充事实',
    '举证责任在你',
    '事实 > 自信',
    '不要再发明第三类叫「大概没问题」',
    '这笔学费我已经交过一次',
    '约 8k 行',
    '约 31k 行',
    '给前一个 program 擦屁股',
    '如果你正准备手算结构边界',
    '停止信号',
    '别把「我也能自己重写一遍」误当成「我应该自己重写一遍」',
  ]) {
    assert.equal(zh.includes(lesson), true, `中文现身说法缺失: ${lesson}`)
  }
})
