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
  'Fork', 'Join', 'Horizon', 'Advisor', 'TodoWrite', 'Fission', 'Read', 'Write', 'Edit', 'Fetch', 'Glob', 'Grep', 'Move',
  'Remove', 'Inspect', 'Behavior', 'Exec', 'Pty', 'Network', 'ReviewAssessment',
]

const toolPermissionByName = Object.fromEntries(PERMISSION_NAMES.map((n) => [n, n]))

const ToolPermission = toolPermissionByName

const permsOf = (names) => names.map((n) => toolPermissionByName[n])

// The member expectation map is the JS-side encoding of the code's
// ToolPermission -> JsCapability mapping: the expected set is rolePermissions
// crossed with it, never a hand list of projections per role.
const fsPermissionsOf = (role) =>
  rolePermissions(role.toLowerCase()).filter((name) => MEMBERS_BY_PERMISSION[name] !== undefined)

const MEMBERS_BY_PERMISSION = {
  Read: ['cat', 'stat', 'whereAll', 'where'],
  Glob: ['ls'],
  Grep: ['rg'],
  Write: ['write', 'mkdir'],
  Move: ['mv'],
  Remove: ['rm'],
  Exec: ['call'],
  Fork: ['fork', 'dag'],
  Join: ['join'],
  Fission: ['fission'],
  Horizon: ['horizon'],
  Advisor: ['advisor'],
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
  call: 'js.call',
  fork: 'js.fork',
  dag: 'js.dag',
  join: 'js.join',
  fission: 'js.fission',
  horizon: 'js.horizon',
  advisor: 'js.advisor',
}

const MEMBER_ORDER = [
  'write', 'rm', 'mv',
  'cat', 'ls', 'rg', 'stat', 'mkdir', 'whereAll', 'where',
  'call',
  'fork',
  'dag',
  'join',
  'fission',
  'horizon',
  'advisor',
]

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
  for (const role of ['Inquiry', 'Distiller']) {
    const perms = caps(...permsOf(rolePermissions(role.toLowerCase())))
    assert.equal(isNone(surface(role, rolePermissions(role.toLowerCase()))), true, `${role} must get no js-* surface`)
    assert.equal(isGeneratedToolName(role, perms, `todowrite-for-${role.toLowerCase()}`), false)
  }

  // Orchestrator has no file permission but holds Fork/Join, so it gets a
  // delegation-only programming surface (WHAT[repository-programming-056]).
  const orchestrator = surface('Orchestrator', rolePermissions('orchestrator'))
  assert.equal(isNone(orchestrator), false, 'Orchestrator gets the delegation-only surface')
  assert.deepEqual(
    orchestrator.members.map((fragment) => fragment.memberName),
    ['fork', 'dag', 'join', 'horizon', 'advisor'],
  )
})

test('WHAT[repository-programming-001] JS001_role_projection_is_exactly_roles_permissions_intersection', () => {
  for (const role of ['Manager', 'Orchestrator', 'Engineer', 'Coder', 'Inspector', 'Browser', 'Inquiry', 'DevOps', 'Distiller']) {
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
  for (const name of PERMISSION_NAMES.filter((n) => MEMBERS_BY_PERMISSION[n] === undefined)) {
    const result = generate('Engineer', caps(toolPermissionByName[name]), 'en')
    assert.equal(isNone(result), true, `${name} alone must not generate a surface`)
  }
})
