import assert from 'node:assert/strict'
import test from 'node:test'
import { rolePredicate } from '../../../dist/OpenCode/Tools/ToolRegistrySurface.js'
import { allRoleLabels } from '../../../dist/Foundation/RolesSurface.js'

{
const { generateRole } = await import('../../../dist/Repository/Programming/Js/GeneratorSurface.js')
const { default: assert } = await import('node:assert/strict')

test('WHAT[repository-programming-002] registered_js_tools_match_js_tool_generator_output', () => {
  for (const role of allRoleLabels) {
    const generated = generateRole(role, 'en')
    const toolName = `todowrite-for-${role}`
    const admitted = rolePredicate(toolName, role)
    if (generated) {
      assert.equal(admitted, true, `Role ${role} has generated surface ${generated.toolName} but ToolRegistry denied it`)
      assert.equal(generated.toolName, toolName, `Generator tool name mismatch for ${role}`)
    } else {
      assert.equal(admitted, false, `Role ${role} has no generated surface but ToolRegistry admitted ${toolName}`)
    }
  }
})
}
import {
  generate,
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

const fsPermissionsOf = (role) =>
  rolePermissions(role.toLowerCase()).filter((n) => ['Read', 'Write', 'Glob', 'Move', 'Remove'].includes(n))

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
  'fork', 'dag', 'join', 'fission', 'horizon', 'advisor',
]

const expectedMembers = (permissionNames) =>
  permissionNames
    .flatMap((name) => MEMBERS_BY_PERMISSION[name] ?? [])
    .sort((left, right) => MEMBER_ORDER.indexOf(left) - MEMBER_ORDER.indexOf(right))

// Sugar members are compiled inside the base class itself instead of
// Sugar members are compiled inside the base class itself instead of
// delegating to `_api` (dag builds fork waves plus one join), so their base-class
// layer is the public method they declare.
const SUGAR_MEMBERS = ['dag']

// The Exec member is NOT on the base class: it is a member of the global `js`
// namespace only (`js.call(...)`), because the base class already owns `run()`
// as the model's program entry. Its binding still resolves, its description and
// example still name it, and the base class must not shadow it.
const GLOBAL_MEMBERS = ['call']

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

test('WHAT[repository-programming-002] JS004_capability_exactness_plus_one_ultra_example_coder', () => {
  const result = surface('Engineer', ['Read', 'Write', 'Edit', 'Glob', 'Grep'])
  assert.equal(isSome(result), true)
  const layers = layersOf(result)
  assert.deepEqual(
    Object.keys(layers).sort(),
    ['cat', 'ls', 'mkdir', 'rg', 'stat', 'where', 'whereAll', 'write'],
  )
  for (const [member, layer] of Object.entries(layers)) {
    assert.equal(layer.inBaseClass, true, `${member} in base class`)
    assert.equal(layer.inDescription, true, `${member} in description`)
    assert.equal(layer.binding, BINDING_BY_MEMBER[member], `${member} binding`)
  }
  assert.match(result.description, /cat\(path\)/)
  assert.match(result.description, /where\(content, needle, from = 0\)/)
  assert.match(result.description, /whereAll\(content, needle, from = 0\)/)
  assert.match(result.description, /write\(path, content\)/)
  assert.match(result.description, /mkdir\(path\)/)
  assert.equal(result.description.includes('file(path'), false)
  assert.equal(result.description.includes('edit(path'), false)
  assert.equal(result.description.includes('rewrite(path'), false)
  assert.equal(result.description.includes('_api'), false)
  assert.equal(result.description.includes('__jsFailure'), false)
  assert.equal(result.examples.length, 1, 'one responsibility-shaped Ultra Example')
  assert.match(result.examples[0], /this\.rg\(/)
  assert.match(result.examples[0], /oldApi/)
})

test('WHAT[repository-programming-002] JS004_absent_capability_is_absent_in_all_four_layers', () => {
  const result = surface('Engineer', ['Read', 'Glob', 'Grep']) // no Edit / Write
  assert.equal(isSome(result), true)
  assert.deepEqual(memberNames(result), ['cat', 'ls', 'rg', 'stat', 'whereAll', 'where'])
  assert.equal(result.description.includes('edit(path'), false)
  assert.equal(result.description.includes('rewrite(path'), false)
  assert.equal(result.description.includes('write(path, content)'), false)
  assert.equal(result.baseClassSource.includes('js.edit'), false)
  assert.equal(result.baseClassSource.includes('js.write'), false)
  assert.equal(result.examples.some((example) => example.includes('this.write')), false)
})

test('WHAT[repository-programming-002] JS004_edit_guidance_never_names_missing_read_or_write_members', () => {
  const editOnly = surface('Engineer', ['Edit'])
  assert.equal(isNone(editOnly), true, 'Edit permission alone projects no JsCapability after legacy retirement')

  const writeOnly = surface('Engineer', ['Write'])
  assert.deepEqual(memberNames(writeOnly), ['write', 'mkdir'])
  for (const unavailable of [
    'async cat(path)',
    'async where(content',
    'whereAll(content, needle',
    'file(matches)',
    'file() + JavaScript',
    'file(path',
    'FileView.text',
    'edit(path',
    'rewrite(path',
  ]) {
    assert.equal(writeOnly.description.includes(unavailable), false, `Write-only surface leaked ${unavailable}`)
  }
  assert.match(writeOnly.description, /(^|[^A-Za-z0-9_])write\(path, content\)/m)

  const readEdit = surface('Engineer', ['Read', 'Edit'])
  assert.deepEqual(memberNames(readEdit), ['cat', 'stat', 'whereAll', 'where'])
  assert.equal(readEdit.description.includes('cat(path)'), true)
  assert.equal(readEdit.description.includes('file(matches)'), false)
  assert.equal(readEdit.description.includes('edit(path'), false)
  assert.equal(readEdit.description.includes('rewrite(path'), false)
  assert.doesNotMatch(readEdit.description, /(^|[^A-Za-z0-9_])write\(path, content\)/m)

  const editWrite = surface('Engineer', ['Edit', 'Write'])
  assert.deepEqual(memberNames(editWrite), ['write', 'mkdir'])
  assert.equal(editWrite.description.includes('file(matches)'), false)
  assert.equal(editWrite.description.includes('file(path'), false)
  assert.equal(editWrite.description.includes('FileView.text'), false)
  assert.match(editWrite.description, /(^|[^A-Za-z0-9_])write\(path, content\)/m)
})

test('WHAT[repository-programming-002] JS004_member_gate_binds_present_members_only', () => {
  const perms = caps(ToolPermission.Read, ToolPermission.Glob, ToolPermission.Grep)
  assert.equal(memberBinding('Engineer', perms, 'file'), undefined)
  assert.equal(memberBinding('Engineer', perms, 'glob'), undefined)
  assert.equal(memberBinding('Engineer', perms, 'grep'), undefined)
  assert.equal(memberBinding('Engineer', perms, 'cat'), 'js.cat')
  assert.equal(memberBinding('Engineer', perms, 'ls'), 'js.ls')
  assert.equal(memberBinding('Engineer', perms, 'stat'), 'js.stat')
  assert.equal(memberBinding('Engineer', perms, 'whereAll'), 'js.whereAll')
  assert.equal(memberBinding('Engineer', perms, 'where'), 'js.where')
  assert.equal(memberBinding('Engineer', perms, 'edit'), undefined)
  assert.equal(memberBinding('Engineer', perms, 'rewrite'), undefined)
  assert.equal(memberBinding('Engineer', perms, 'write'), undefined)
})

test('WHAT[repository-programming-002] JS004_lying_generator_counterexample_is_rejected', () => {
  // A "lying" surface advertises a member with no runtime binding — the exact
  // failure mode the four-layer invariant exists to make impossible. The gate
  // must refuse the member: memberBinding returns undefined for it, so a
  // forged call cannot resolve an executor (JS-004).
  const perms = caps(ToolPermission.Read, ToolPermission.Glob, ToolPermission.Grep)
  // lie: retired 'file'/'edit'/'rewrite' and absent 'write' are NOT in this surface's bindings
  assert.equal(memberBinding('Inspector', perms, 'file'), undefined)
  assert.equal(memberBinding('Inspector', perms, 'edit'), undefined)
  assert.equal(memberBinding('Inspector', perms, 'rewrite'), undefined)
  assert.equal(memberBinding('Inspector', perms, 'write'), undefined)
  // the honest members resolve
  assert.equal(memberBinding('Inspector', perms, 'cat'), 'js.cat')
  // a lying description would name methods the surface lacks; the generator
  // never does — prove the surface itself contains no unbounded members
  const result = generate('Inspector', perms, 'en')
  const names = result.members.map((f) => f.memberName)
  for (const name of names) {
    assert.notEqual(memberBinding('Inspector', perms, name), undefined, `${name} must resolve`)
  }
  assert.equal(names.includes('rewrite'), false)
  assert.equal(names.includes('write'), false)
})

test('WHAT[repository-programming-002] JS004_horizon_projects_four_layers_for_every_holding_role', () => {
  const holders = allRoleLabels.filter((role) => rolePermissions(role.toLowerCase()).includes('Horizon'))
  assert.deepEqual(holders, ['devops', 'manager', 'orchestrator'], 'the roles holding ToolPermission.Horizon')

  for (const role of holders) {
    const permissions = rolePermissions(role.toLowerCase())
    const result = surface(role, permissions)
    assert.equal(isSome(result), true, `${role} gets a js-* surface`)
    assert.deepEqual(memberNames(result), expectedMembers(permissions), `${role} member set`)
    assert.equal(
      memberBinding(role, permissions, 'horizon'),
      'js.horizon',
      `${role} horizon runtime gate`,
    )

    const layers = layersOf(result)
    assert.equal(layers.horizon.binding, 'js.horizon', `${role}: horizon binding key`)
    assert.equal(layers.horizon.example.includes('horizon'), true, `${role}: horizon canonical example`)
    for (const [member, layer] of Object.entries(layers)) {
      if (GLOBAL_MEMBERS.includes(member)) {
        assert.equal(
          result.baseClassSource.includes(`this._api.${layer.binding}`),
          false,
          `${role}: ${member} must not land on the base class`,
        )
      } else {
        const baseClassMarker = SUGAR_MEMBERS.includes(member)
          ? `async ${member}(`
          : `this._api.${layer.binding}`
        assert.equal(
          result.baseClassSource.includes(baseClassMarker),
          true,
          `${role}: ${member} in base class`,
        )
      }
      assert.equal(layer.inDescription, true, `${role}: ${member} in description`)
      assert.equal(layer.binding, BINDING_BY_MEMBER[member], `${role}: ${member} binding`)
    }
  }

  // The permission is the sole source of the member: Fork/Join without Horizon
  // project fork/dag/join but never horizon.
  assert.equal(memberBinding('Engineer', caps(ToolPermission.Fork, ToolPermission.Join), 'horizon'), undefined)
  assert.deepEqual(expectedMembers(['Fork', 'Join', 'Horizon']), ['fork', 'dag', 'join', 'horizon'])
})

test('WHAT[repository-programming-002] JS004_advisor_projects_four_layers_for_every_holding_role', () => {
  const holders = allRoleLabels.filter((role) => rolePermissions(role.toLowerCase()).includes('Advisor'))
  assert.deepEqual(holders, ['devops', 'engineer', 'manager', 'orchestrator'], 'the roles holding ToolPermission.Advisor')

  for (const role of holders) {
    const permissions = rolePermissions(role.toLowerCase())
    const result = surface(role, permissions)
    assert.equal(isSome(result), true, `${role} gets a js-* surface`)
    assert.deepEqual(memberNames(result), expectedMembers(permissions), `${role} member set`)
    assert.equal(
      memberBinding(role, permissions, 'advisor'),
      'js.advisor',
      `${role} advisor runtime gate`,
    )

    const layers = layersOf(result)
    assert.equal(layers.advisor.binding, 'js.advisor', `${role}: advisor binding key`)
    assert.equal(layers.advisor.example.includes('advisor'), true, `${role}: advisor canonical example`)
    for (const [member, layer] of Object.entries(layers)) {
      if (GLOBAL_MEMBERS.includes(member)) {
        assert.equal(
          result.baseClassSource.includes(`this._api.${layer.binding}`),
          false,
          `${role}: ${member} must not land on the base class`,
        )
      } else {
        const baseClassMarker = SUGAR_MEMBERS.includes(member)
          ? `async ${member}(`
          : `this._api.${layer.binding}`
        assert.equal(
          result.baseClassSource.includes(baseClassMarker),
          true,
          `${role}: ${member} in base class`,
        )
      }
      assert.equal(layer.inDescription, true, `${role}: ${member} in description`)
      assert.equal(layer.binding, BINDING_BY_MEMBER[member], `${role}: ${member} binding`)
    }
  }

  // The permission is the sole source of the member: Fork/Join without Advisor
  // project fork/dag/join but never advisor.
  const delegationOnly = surface('Engineer', caps(ToolPermission.Fork, ToolPermission.Join))
  assert.deepEqual(memberNames(delegationOnly), expectedMembers(['Fork', 'Join']))
  assert.equal(memberBinding('Engineer', caps(ToolPermission.Fork, ToolPermission.Join), 'advisor'), undefined)
  assert.deepEqual(expectedMembers([ToolPermission.Advisor]), ['advisor'])

  // Absence: a role without the permission never projects the member — the
  // advisor seat itself holds no permission at all, and plan keeps its
  // surface advisor-free.
  const without = allRoleLabels.filter((role) => !rolePermissions(role.toLowerCase()).includes('Advisor'))
  assert.deepEqual(without, ['advisor', 'plan'], 'the roles without ToolPermission.Advisor')
  for (const role of without) {
    assert.equal(
      memberBinding(role, rolePermissions(role.toLowerCase()), 'advisor'),
      undefined,
      `${role} never resolves js.advisor`,
    )
  }
})
