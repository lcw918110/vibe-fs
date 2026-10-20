import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { generate, isGeneratedToolName, memberBinding } = await import("../../../dist/Repository/Programming/Js/GeneratorSurface.js");
const { permissions: rolePermissions } = await import("../../../dist/Participant/Persona/OfficeCapabilitySurface.js");

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

test('WHAT[repository-programming-005] JS002_description_embeds_spec_base_class_rules_and_one_ultra_example', () => {
  const coder = surface('Engineer', ['Read', 'Write', 'Edit', 'Glob', 'Grep'])
  for (const token of [
    'class JsProgram',
    'class Js extends JsProgram',
    'async cat(path)',
    'whereAll(content, needle, from = 0)',
    'async where(content, needle, from = 0)',
    'write(path, content)',
    'mkdir(path)',
    'begin',
    'end',
    'Anchors locate',
    'Define exactly one class named Js',
    'Ultra Example',
    'RetryPolicy',
    'Mechanical branches belong inside the program',
  ]) {
    assert.equal(coder.description.includes(token), true, `coder description missing: ${token}`)
  }
  assert.equal(coder.description.includes('_api'), false)
  assert.equal(coder.description.includes('js.cat'), false)
})
test('WHAT[repository-programming-005] JS010_description_never_dilutes_the_ultra_example', () => {
  for (const role of ['Engineer', 'DevOps']) {
    const result = surface(role, rolePermissions(role.toLowerCase()))
    const classes = result.description.match(/class Js extends JsProgram/g) ?? []
    assert.ok(classes.length <= 1, `${role} description must not dilute the Ultra Example with toy examples`)
    assert.match(result.description, /Semantic branches belong between programs/)
  }
})
test('WHAT[repository-programming-005] JS_description_retains_no_unsubstituted_placeholders', () => {
  const result = generate('Coder', caps(ToolPermission.Read, ToolPermission.Edit, ToolPermission.Write), 'en')
  assert.equal(result.description.includes('{{'), false)
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { mkdtempSync, rmSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join } = await import("node:path");
const { createRegistered, name, description } = await import("../../../dist/Repository/Programming/Js/OpenCode/ToolHostSurface.js");

// Superseded note: BuiltinToolDescriptionHook (`builtinTools`, `annotate`, `validateRecommendation`)
// was retired with the direct Host filesystem tools (`read`/`write`/`edit`/`glob`/`grep`/`patch`);
// the js programming surface (`cat`/`where`/`write`/`ls`/`mv`/`rm`) is the sole contract now.
const sandbox = () => {
  const dir = mkdtempSync(join(tmpdir(), 'wxs-host-'))
  return { dir, cleanup: () => rmSync(dir, { recursive: true, force: true }) }
}
const toolModule = () => {
  const tool = (definition) => definition
  tool.schema = {
    string: () => ({
      type: 'string',
      describe: (description) => ({ type: 'string', description }),
    }),
    any: () => ({
      type: 'any',
      optional: () => ({ type: 'any', optional: true }),
    }),
    number: () => ({ type: 'number', optional: () => ({ type: 'number', optional: true }) }),
    array: (item) => ({
      type: 'array',
      item,
      describe: (description) => ({ type: 'array', item, description }),
    }),
    record: (key, value) => ({
      type: 'record',
      key,
      value,
      describe: (description) => ({ type: 'record', key, value, description }),
    }),
    object: (shape) => ({ type: 'object', shape }),
  }
  return { tool }
}

test('WHAT[repository-programming-005] JS073_spec_carries_generated_name_and_honest_description', () => {
  const { dir, cleanup } = sandbox()
  try {
    const registered = createRegistered(toolModule(), 'Coder', 'en', dir, null)
    assert.equal(name(registered), 'todowrite-for-coder')
    assert.equal(description(registered).includes('class JsProgram'), true)
    assert.equal(description(registered).includes('async cat(path)'), true)
    assert.equal(description(registered).includes('_api'), false)
  } finally {
    cleanup()
  }
})
}
