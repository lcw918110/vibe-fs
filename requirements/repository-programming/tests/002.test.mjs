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
    const toolName = `js-${role}`
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

test('WHAT[repository-programming-002] JS004_capability_exactness_plus_one_ultra_example_coder', () => {
  const result = surface('Engineer', ['Read', 'Write', 'Edit', 'Glob', 'Grep'])
  assert.equal(isSome(result), true)
  const layers = layersOf(result)
  assert.deepEqual(Object.keys(layers).sort(), ['edit', 'file', 'glob', 'grep', 'rewrite', 'write'])
  for (const [member, layer] of Object.entries(layers)) {
    assert.equal(layer.inBaseClass, true, `${member} in base class`)
    assert.equal(layer.inDescription, true, `${member} in description`)
    assert.equal(layer.binding, BINDING_BY_MEMBER[member], `${member} binding`)
  }
  assert.equal(result.description.includes('HOST_READ_IMMUTABLE_UTF8_SNAPSHOT'), true)
  assert.match(result.description, /name\+N \/ name-N/)
  assert.match(result.description, /not a line number/)
  assert.match(result.description, /text\(from = "\^", to = "\$"\)/)
  assert.match(result.description, /edit\(path, changes\)/)
  assert.equal(result.description.includes('_api'), false)
  assert.equal(result.description.includes('__jsFailure'), false)
  assert.equal(result.examples.length, 1, 'one responsibility-shaped Ultra Example')
  assert.match(result.examples[0], /oldApi → newApi/)
})

test('WHAT[repository-programming-002] JS004_absent_capability_is_absent_in_all_four_layers', () => {
  const result = surface('Engineer', ['Read', 'Glob', 'Grep']) // no Edit / Write
  assert.equal(isSome(result), true)
  assert.deepEqual(memberNames(result), ['file', 'glob', 'grep'])
  assert.equal(result.description.includes('edit(path'), false)
  assert.equal(result.description.includes('rewrite(path'), false)
  assert.equal(result.description.includes('write(path'), false)
  assert.equal(result.baseClassSource.includes('js.edit'), false)
  assert.equal(result.baseClassSource.includes('js.write'), false)
  assert.equal(result.examples.some((example) => example.includes('this.rewrite')), false)
})

test('WHAT[repository-programming-002] JS004_edit_guidance_never_names_missing_read_or_write_members', () => {
  const editOnly = surface('Engineer', ['Edit'])
  assert.deepEqual(memberNames(editOnly), ['edit', 'rewrite'])
  for (const unavailable of [
    'file(matches)',
    'file() + JavaScript',
    'file(path',
    'FileView.text',
  ]) {
    assert.equal(editOnly.description.includes(unavailable), false, `Edit-only surface leaked ${unavailable}`)
  }
  assert.doesNotMatch(editOnly.description, /(^|[^A-Za-z0-9_])write\(path/m)

  const readEdit = surface('Engineer', ['Read', 'Edit'])
  assert.deepEqual(memberNames(readEdit), ['file', 'edit', 'rewrite'])
  assert.equal(readEdit.description.includes('file(matches) + text() + rewrite()'), true)
  assert.doesNotMatch(readEdit.description, /(^|[^A-Za-z0-9_])write\(path/m)

  const editWrite = surface('Engineer', ['Edit', 'Write'])
  assert.deepEqual(memberNames(editWrite), ['edit', 'rewrite', 'write'])
  assert.equal(editWrite.description.includes('file(matches)'), false)
  assert.equal(editWrite.description.includes('file(path'), false)
  assert.equal(editWrite.description.includes('FileView.text'), false)
  assert.match(editWrite.description, /(^|[^A-Za-z0-9_])write\(path/m)
})

test('WHAT[repository-programming-002] JS004_member_gate_binds_present_members_only', () => {
  const perms = caps(ToolPermission.Read, ToolPermission.Glob, ToolPermission.Grep)
  assert.equal(memberBinding('Engineer', perms, 'file'), 'js.read')
  assert.equal(memberBinding('Engineer', perms, 'glob'), 'js.glob')
  assert.equal(memberBinding('Engineer', perms, 'grep'), 'js.grep')
  assert.equal(memberBinding('Engineer', perms, 'edit'), undefined)
  assert.equal(memberBinding('Engineer', perms, 'rewrite'), undefined)
  assert.equal(memberBinding('Engineer', perms, 'write'), undefined)
  assert.equal(memberBinding('Blogger', caps(ToolPermission.Chronicle), 'file'), undefined)
})

test('WHAT[repository-programming-002] JS004_lying_generator_counterexample_is_rejected', () => {
  // A "lying" surface advertises a member with no runtime binding — the exact
  // failure mode the four-layer invariant exists to make impossible. The gate
  // must refuse the member: memberBinding returns undefined for it, so a
  // forged call cannot resolve an executor (JS-004).
  const perms = caps(ToolPermission.Read, ToolPermission.Glob, ToolPermission.Grep)
  // lie: 'rewrite' is NOT in this surface's bindings (Inspector has no Edit)
  assert.equal(memberBinding('Inspector', perms, 'rewrite'), undefined)
  assert.equal(memberBinding('Inspector', perms, 'write'), undefined)
  // the honest members resolve
  assert.equal(memberBinding('Inspector', perms, 'file'), 'js.read')
  // a lying description would name methods the surface lacks; the generator
  // never does — prove the surface itself contains no unbounded members
  const result = generate('Inspector', perms, 'en')
  const names = result.members.map((f) => f.memberName)
  for (const name of names) {
    assert.notEqual(memberBinding('Inspector', perms, name), undefined, `${name} must resolve`)
  }
  assert.equal(names.includes('rewrite'), false)
})
