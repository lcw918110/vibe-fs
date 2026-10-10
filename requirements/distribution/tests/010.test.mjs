import assert from 'node:assert/strict'
import { readdirSync, existsSync } from 'node:fs'
import { join } from 'node:path'
import test from 'node:test'

const ROOT = new URL('../../..', import.meta.url).pathname

test('WHAT[distribution-010] repository resources and emitted role/tool declarations exclude retired role registrations', async () => {
  // 1. 资源目录完全闭包深比较 (deepEqual)
  const roleDir = join(ROOT, 'resources/provider/role')
  assert.ok(existsSync(roleDir), 'resources/provider/role directory must exist')
  const actualRoles = readdirSync(roleDir, { withFileTypes: true })
    .filter((d) => d.isDirectory())
    .map((d) => d.name)
    .sort()

  const expectedActiveRoles = [
    'blogger',
    'bookkeeper',
    'devops',
    'engineer',
    'manager',
    'orchestrator',
    'plan',
  ].sort()

  assert.deepEqual(
    actualRoles,
    expectedActiveRoles,
    'resources/provider/role must strictly equal the 6 active roles with zero legacy residue',
  )

  // 2. RolesSurface 必须包含 5 个公开活跃角色，且严格排除全部 5 个已废止角色
  const rolesSurface = await import('../../../dist/Foundation/RolesSurface.js')
  const activeRoles5 = ['manager', 'orchestrator', 'engineer', 'devops', 'blogger']
  const deprecatedRoles5 = ['coder', 'inspector', 'browser', 'inquiry', 'distiller']

  for (const role of activeRoles5) {
    assert.ok(
      rolesSurface.allRoleLabels.includes(role),
      `Roles surface must contain active role ${role}`,
    )
  }

  for (const dep of deprecatedRoles5) {
    assert.equal(
      rolesSurface.allRoleLabels.includes(dep),
      false,
      `Roles surface must strictly exclude deprecated role ${dep}`,
    )
  }

  // 3. ToolSurface.toolSpecNames 必须包含新工具 surface 并严格排除废弃工具
  const toolSurface = await import('../../../dist/OpenCode/Tools/ToolSurface.js')
  const knownTools = toolSurface.toolSpecNames() || []

  const requiredTools = [
    'js-engineer',
    'js-devops',
    'js-bookkeeper',
    'js-manager',
    'js-orchestrator',
    'js-blogger',
  ]
  const forbiddenTools = ['js-coder', 'js-inspector', 'js-browser']

  for (const tool of requiredTools) {
    assert.ok(
      knownTools.includes(tool),
      `knownToolNames must include active tool surface ${tool}`,
    )
  }

  for (const tool of forbiddenTools) {
    assert.equal(
      knownTools.includes(tool),
      false,
      `knownToolNames must strictly exclude deprecated tool surface ${tool}`,
    )
  }

})

test.todo('WHAT[distribution-010] GAP-210: actual installed plugin registration exposes only active tools and resources')
