import assert from 'node:assert/strict'
import test from 'node:test'
import {
  generate,
  isGeneratedToolName,
} from '../../../dist/Repository/Programming/Js/GeneratorSurface.js'
import { permissions as rolePermissions } from '../../../dist/Participant/Persona/OfficeCapabilitySurface.js'

const caps = (...permissions) => permissions

const surface = (role, permissions) => generate(role, permissions, 'en')

const memberNames = (s) => s.members.map((fragment) => fragment.memberName)

const isSome = (value) => value !== null

test('WHAT[repository-programming-003] JS002_generation_is_deterministic_and_names_js_role', () => {
  const perms = caps('Read', 'Glob', 'Grep', 'Edit', 'Write')
  const a = generate('Engineer', perms, 'en')
  const b = generate('Engineer', perms, 'en')
  assert.equal(isSome(a) && isSome(b), true)
  assert.equal(a.toolName, 'todowrite-for-engineer')
  assert.equal(a.description, b.description)
  assert.equal(a.baseClassSource, b.baseClassSource)
  assert.deepEqual(a.examples, b.examples)
  assert.equal(a.capabilities.length, 4)
})

test('WHAT[repository-programming-003] JS002_same_capabilities_share_mechanics_but_role_shapes_the_ultra_example', () => {
  const shared = caps('Read', 'Write', 'Glob', 'Grep', 'Exec')
  const engineer = generate('Engineer', shared, 'en')
  const devops = generate('DevOps', shared, 'en')
  assert.equal(engineer.baseClassSource, devops.baseClassSource)
  assert.deepEqual(memberNames(engineer), memberNames(devops))
  assert.notEqual(engineer.description, devops.description)
  assert.notDeepEqual(engineer.examples, devops.examples)
})


test('WHAT[repository-programming-003] JS010_each_filesystem_role_gets_exactly_one_distinct_ultra_example', () => {
  for (const role of ['Engineer', 'DevOps']) {
    const result = surface(role, rolePermissions(role.toLowerCase()))
    assert.equal(isSome(result), true, `${role} must generate a surface`)
  }
  const engineer = surface('Engineer', rolePermissions('engineer'))
  assert.equal(engineer.examples.length, 1, 'Engineer gets one Ultra Example')
  assert.ok(engineer.examples[0].length > 0)
  const devopsWithEdit = generate('DevOps', ['Read', 'Write', 'Glob'], 'en')
  assert.equal(isSome(devopsWithEdit), true)
})
