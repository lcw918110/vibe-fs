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

test('WHAT[repository-programming-001] JS001_generate_none_when_no_filesystem_capability', () => {
  for (const role of ['Orchestrator', 'Inquiry', 'Distiller', 'Blogger']) {
    const perms = caps(...permsOf(rolePermissions(role.toLowerCase())))
    assert.equal(isNone(surface(role, rolePermissions(role.toLowerCase()))), true, `${role} must get no js-* surface`)
    assert.equal(isGeneratedToolName(role, perms, `js-${role.toLowerCase()}`), false)
  }
})

test('WHAT[repository-programming-001] JS001_role_projection_is_exactly_roles_permissions_intersection', () => {
  for (const role of ['Manager', 'Orchestrator', 'Coder', 'Inspector', 'Browser', 'Inquiry', 'DevOps', 'Distiller', 'Blogger']) {
    const fsPerms = fsPermissionsOf(role)
    const result = surface(role, rolePermissions(role.toLowerCase()))
    if (fsPerms.length === 0) {
      assert.equal(isNone(result), true, `${role} has no fs capability`)
    } else {
      assert.equal(isSome(result), true, `${role} must get a js-* surface`)
      const expected = fsPerms
        .flatMap((name) => MEMBERS_BY_PERMISSION[name])
        .sort((left, right) => MEMBER_ORDER.indexOf(left) - MEMBER_ORDER.indexOf(right))
      assert.deepEqual(memberNames(result), expected, `${role} member set`)
    }
  }
})

test('WHAT[repository-programming-001] JS001_non_fs_permissions_never_produce_members', () => {
  for (const name of PERMISSION_NAMES.filter((n) => !['Read', 'Write', 'Edit', 'Glob', 'Grep'].includes(n))) {
    const result = generate('Engineer', caps(toolPermissionByName[name]), 'en')
    assert.equal(isNone(result), true, `${name} alone must not generate a surface`)
  }
})
