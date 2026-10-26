import assert from 'node:assert/strict'
import test from 'node:test'
import { allPublicRoleLabels, allRoleLabels } from '../../../dist/Foundation/RolesSurface.js'
import { admissionAuthority, capabilityToolNames, rolePredicate } from '../../../dist/OpenCode/Tools/ToolRegistrySurface.js'
import { toolSpecNames } from '../../../dist/OpenCode/Tools/ToolSurface.js'
import { generateRole } from '../../../dist/Repository/Programming/Js/GeneratorSurface.js'
import { isAllowed, permissions } from '../../../dist/Participant/Persona/OfficeCapabilitySurface.js'
import { acceptAuthorityRoot, withExecutablePlugin } from '../../verification-system/tests/support/plugin-fixture.mjs'

test('WHAT[work-021] Role catalog contains Plan and wire label is plan', () => {
  // Roles.all must contain Role.Plan with wire label 'plan'
  assert.ok(allRoleLabels.includes('plan'), 'Roles.all must contain Role.Plan with wire label "plan"')
  assert.ok(allPublicRoleLabels.includes('plan'), 'allPublicRoleLabels must contain "plan"')
})

test('WHAT[work-021] Plan separates human ask from devops investigation and denies direct execution', () => {
  const allowed = permissions('plan')
  assert.ok(Array.isArray(allowed), 'permissions("plan") must return an array')

  // WHAT[work-021]: ask asks the human; devops delegates an investigation.
  // resume and handoff are no longer part of the plan surface.
  const expectedPermissions = ['Ask', 'DevOpsConsultation', 'Deliver', 'JsPlan', 'ResetScope', 'Review']
  assert.deepEqual(
    [...allowed].sort(),
    expectedPermissions.sort(),
    'Plan must expose distinct human-question and DevOps capabilities',
  )

  // Each designated capability is actually allowed.
  for (const perm of expectedPermissions) {
    assert.equal(isAllowed('plan', perm), true, `Plan must be allowed ${perm}`)
  }

  // Retired plan tools must be strictly denied
  for (const perm of ['Handoff', 'Resume']) {
    assert.equal(isAllowed('plan', perm), false, `Plan must be denied retired tool ${perm}`)
  }

  // Must strictly deny repository, execution and orchestration tools
  const forbiddenPermissions = [
    'Read',
    'Write',
    'Edit',
    'Glob',
    'Grep',
    'Move',
    'Remove',
    'Exec',
    'Pty',
    'Fork',
    'Fission',
    'Fetch',
    'Finality',
    'BashHoneypot',
  ]
  for (const perm of forbiddenPermissions) {
    assert.equal(isAllowed('plan', perm), false, `Plan must be denied ${perm}`)
  }

  // 宿主 devops 工具已退役：它不在注册表里，也不再解析到 office 准入权威。
  assert.ok(!toolSpecNames().includes('devops'), 'the host devops tool must be retired')
  assert.notEqual(admissionAuthority('devops'), 'office', 'the retired devops name must resolve to no office admission authority')
  assert.equal(rolePredicate('devops', 'plan'), false, 'no role is admitted to a host devops tool')
  assert.ok(
    !capabilityToolNames('plan', 'work-main').includes('devops'),
    'the Plan work-main capability set must not carry the retired host devops tool',
  )
})

test('WHAT[work-021] the devops verb survives as a Plan programming-surface member only', () => {
  const plan = generateRole('plan', 'en')
  const planMembers = plan.members.map((member) => member.memberName)
  assert.ok(planMembers.includes('devops'), 'the Plan surface carries the devops member')
  assert.ok(
    plan.baseClassSource.includes('async devops(intent)'),
    'the Plan base class declares devops without extra array',
  )

  // 其它角色没有这个成员：DevOps 调查只对 Plan 开放。
  for (const role of allRoleLabels.filter((label) => label !== 'plan')) {
    const surface = generateRole(role, 'en')
    if (!surface) continue
    const members = surface.members.map((member) => member.memberName)
    assert.ok(!members.includes('devops'), `the "${role}" surface must carry no devops member`)
  }
})

test('WHAT[work-021] non-Plan role has no devops member and call fails closed', async () => {
  await withExecutablePlugin(async (hooks, _directory, _createdIds, runtime) => {
    // 非 Plan 角色没有这个成员：调用在编程面直接失败关闭，而不是静默成功。
    const engineerSession = 'ses-work-021-devops-engineer'
    await acceptAuthorityRoot(runtime, engineerSession, 'engineer', 'user-engineer-root')
    const engineer = String(
      await hooks.tool['todowrite-for-engineer'].execute(
        { program: 'class Js extends JsProgram { async run() { return await this.devops("find the flaky test"); } }', name: 'engineer-devops', intent: 'Verify the Engineer role refuses DevOps delegation without dispatching a child.', remainingScopeTurnsEstimate: 3 },
        { sessionID: engineerSession, agent: 'engineer' },
      ),
    )
    assert.match(engineer, /this\.devops is not a function/)
  })
})

