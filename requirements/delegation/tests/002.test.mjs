import assert from 'node:assert/strict'
import test from 'node:test'
import * as RolesSurface from '../../../dist/Foundation/RolesSurface.js'
import * as PersonaSurface from '../../../dist/Participant/Persona/Surface.js'
import { permissions as officePermissions } from '../../../dist/Participant/Persona/OfficeCapabilitySurface.js'

test('WHAT[delegation-002] DELEG_002_calling_name_preserves_office_authority', () => {
  // 1. 活跃 Role 集合中的每个角色
  const activeRoles = [
    { label: 'manager', aliases: ['manager', 'Manager', 'MANAGER'] },
    { label: 'orchestrator', aliases: ['orchestrator', 'Orchestrator'] },
    { label: 'engineer', aliases: ['engineer', 'Engineer'] },
    { label: 'devops', aliases: ['devops', 'DevOps'] },
  ]

  for (const { label, aliases } of activeRoles) {
    // 验证 Canonical label 属于合法角色
    assert.ok(RolesSurface.allRoleLabels.includes(label), `Canonical label ${label} must be a valid role`)

    // 验证所有 calling 别名均解析为同一个 CanonicalRole label
    for (const alias of aliases) {
      const canonical = PersonaSurface.roleName(alias)
      assert.equal(canonical, label, `Alias ${alias} must resolve to canonical ${label}`)

      // 权限同构：角色在权限矩阵中派生的权限必须完全由 CanonicalRole 决定，与别名无关
      const perm1 = officePermissions(label)
      const perm2 = officePermissions(alias)
      assert.deepEqual(perm1, perm2, `Permissions for ${alias} must match ${label}`)
    }
  }
})
