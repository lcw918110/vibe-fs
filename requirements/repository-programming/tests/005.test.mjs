import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { generate, isGeneratedToolName, memberBinding } = await import("../../../dist/Repository/Programming/Js/GeneratorSurface.js");
const { permissions: rolePermissions } = await import("../../../dist/Participant/Persona/OfficeCapabilitySurface.js");

const caps = (...permissions) => permissions
const surface = (role, permissionNames) => generate(role, caps(...permissionNames), 'en')
const memberNames = (s) => s.members.map((fragment) => fragment.memberName)
const isNone = (value) => value === null
const isSome = (value) => value !== null
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
  write: 'write',
  rm: 'rm',
  mv: 'mv',
  cat: 'cat',
  ls: 'ls',
  rg: 'rg',
  stat: 'stat',
  mkdir: 'mkdir',
  whereAll: 'whereAll',
  where: 'where',
}
const MEMBER_ORDER = ['write', 'rm', 'mv', 'cat', 'ls', 'rg', 'stat', 'mkdir', 'whereAll', 'where']
const layersOf = (s) =>
  Object.fromEntries(
    s.members.map((fragment) => [
      fragment.memberName,
      {
        binding: fragment.memberName,
        inBaseClass: s.baseClassSource.includes(`this._api.${fragment.memberName}`),
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
  assert.equal(coder.description.includes('this._api.cat'), false)
})
test('WHAT[repository-programming-005] JS010_description_never_dilutes_the_ultra_example', () => {
  for (const role of ['Engineer', 'DevOps']) {
    const result = surface(role, rolePermissions(role.toLowerCase()))
    // The short, non-executable tool-call contract also names the required
    // class signature. Count *actual JS code blocks*, not prose mentions.
    const examples = result.description.match(/```js\s*\n\s*class Js extends JsProgram/g) ?? []
    assert.ok(examples.length <= 1, `${role} must have just one executable Ultra Example, not toy programs`)
    assert.match(result.description, /Semantic branches belong between programs/)
  }
})
test('WHAT[repository-programming-005] JS_description_retains_no_unsubstituted_placeholders', () => {
  const result = generate('Coder', caps('Read', 'Edit', 'Write'), 'en')
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
// The fake host schema builders mirror the real ones: every builder can be
// marked optional, so a registered argument can say "this may be absent" the
// way the zod-based host schema does.
const schemaOf = (state) => ({
  nullable: () => schemaOf({ ...state, nullable: true }),
  describe: (text) => schemaOf({ ...state, description: text }),
  optional: () => schemaOf({ ...state, optional: true }),
  int: () => schemaOf({ ...state, int: true }),
  nonnegative: () => schemaOf({ ...state, nonnegative: true }),
  integer: () => schemaOf({ ...state, int: true }),
  min: (bound) => schemaOf({ ...state, min: bound }),
  ...state,
})
const toolModule = () => {
  const tool = (definition) => definition
  tool.schema = {
    union: (items) => schemaOf({ type: 'union', items }),
    string: () => schemaOf({ type: 'string' }),
    any: () => schemaOf({ type: 'any' }),
    number: () => schemaOf({ type: 'number' }),
    array: (item) => schemaOf({ type: 'array', item }),
    record: (key, value) => schemaOf({ type: 'record', key, value }),
    object: (shape) => schemaOf({ type: 'object', shape }),
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
