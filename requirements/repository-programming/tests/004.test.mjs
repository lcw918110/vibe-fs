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

test('WHAT[repository-programming-004] JS001_generated_name_gate_rejects_forged_names', () => {
  const perms = caps(ToolPermission.Read, ToolPermission.Glob, ToolPermission.Grep)
  assert.equal(isGeneratedToolName('Engineer', perms, 'todowrite-for-engineer'), true)
  assert.equal(isGeneratedToolName('Engineer', perms, 'todowrite-for-devops'), false)
  assert.equal(isGeneratedToolName('Engineer', perms, 'read'), false)
  // a role without the capability set never admits its own name
  assert.equal(isGeneratedToolName('Engineer', caps(ToolPermission.Ask), 'todowrite-for-engineer'), false)
})
