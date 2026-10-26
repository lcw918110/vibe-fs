import assert from 'node:assert/strict'
import test from 'node:test'
import { readFileSync } from 'node:fs'
import { join, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'
import {
  generate,
  isGeneratedToolName,
  memberBinding,
} from '../../../dist/Repository/Programming/Js/GeneratorSurface.js'
import {
  permissions as rolePermissions,
  permissionLabels,
  permissionOfLabel,
} from '../../../dist/Participant/Persona/OfficeCapabilitySurface.js'
import { allRoleLabels } from '../../../dist/Foundation/RolesSurface.js'

const caps = (...permissions) => permissions

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '../../..')

// The declared cases of the ToolPermission union, read from the one source
// that declares them. The label list in production must cover exactly these
// cases: a permission nobody holds yet still belongs to the catalog.
const declaredPermissionCases = () => {
  const source = readFileSync(
    join(ROOT, 'src/Wanxiangshu/Foundation/OfficeCapability.fs'),
    'utf8',
  )
  const declaration = source.match(
    /type ToolPermission =([\s\S]*?)\n\n\[<RequireQualifiedAccess>\]\ntype ManagerCapabilityFacts/,
  )
  assert.ok(declaration, 'the ToolPermission declaration must be readable')
  return [...declaration[1].matchAll(/^\s*\|\s*([A-Z][A-Za-z0-9]*)/gm)].map((match) => match[1])
}

const surface = (role, permissionNames) => generate(role, caps(...permissionNames), 'en')

const memberNames = (s) => s.members.map((fragment) => fragment.memberName)

const isNone = (value) => value === null

const isSome = (value) => value !== null

// park and deliver are lifecycle members: every generated surface carries them
// in its base class, but they are not the projection of a ToolPermission, so
// they are never counted among the capability members of a role.
const LIFECYCLE_MEMBERS = ['park', 'unpark', 'abandon']

const projectedMembers = (s) => memberNames(s).filter((name) => !LIFECYCLE_MEMBERS.includes(name))

// The member expectation map is the JS-side encoding of the code's
// ToolPermission -> JsCapability mapping: the expected set is rolePermissions
// crossed with it, never a hand list of projections per role. The manager's
// read-only members keep their own namespace.
const MEMBERS_BY_PERMISSION = {
  Read: ['cat', 'stat', 'whereAll', 'where'],
  Glob: ['ls'],
  Grep: ['rg'],
  Write: ['write', 'mkdir'],
  Move: ['mv'],
  Remove: ['rm'],
  Exec: ['call', 'signal', 'send'],
  Fork: ['fork'],
  Resume: ['resume'],
  DevOpsConsultation: ['devops'],
  Fission: ['fission'],
  Horizon: ['horizon'],
  Advisor: [],
  Ask: ['ask'],
  Review: ['review'],
  ReviewAssessment: ['review'],
}

const MEMBER_ORDER = [
  'write', 'rm', 'mv',
  'cat', 'ls', 'rg', 'stat', 'mkdir', 'whereAll', 'where',
  'call', 'signal', 'send',
  'fork', 'resume', 'commission', 'devops', 'dag',
  'fission', 'horizon',  'ask', 'review',
]

// The offices that hold at least one programming-surface permission, with the
// member set each of them projects. The delegation members are programming
// surface members only: the host registers no fork/resume/commission/devops
// tool of its own.
const EXPECTED_MEMBERS = [
  {
    role: 'Engineer',
    label: 'engineer',
    members: ['write', 'rm', 'mv', 'cat', 'ls', 'rg', 'stat', 'mkdir', 'whereAll', 'where', 'fission'],
  },
  {
    role: 'DevOps',
    label: 'devops',
    members: ['write', 'rm', 'mv', 'cat', 'ls', 'rg', 'stat', 'mkdir', 'whereAll', 'where', 'call', 'signal', 'send', 'horizon'],
  },
  {
    role: 'Manager',
    label: 'manager',
    members: ['reviewOnly.cat', 'reviewOnly.ls', 'reviewOnly.rg', 'reviewOnly.stat', 'whereAll', 'where', 'fork', 'resume', 'horizon',  'review'],
  },
  {
    role: 'Orchestrator',
    label: 'orchestrator',
    members: ['commission', 'horizon'],
  },
  {
    role: 'Plan',
    label: 'plan',
    members: ['write', 'cat', 'stat', 'mkdir', 'whereAll', 'where', 'devops', 'ask', 'review'],
  },
]

// The offices whose permission set projects no member at all.
const SURFACE_LESS_ROLES = ['Coder', 'Inspector', 'Browser', 'Inquiry', 'Distiller', 'Advisor']

test('WHAT[repository-programming-001] JS001_generate_none_when_no_filesystem_capability', () => {
  for (const role of SURFACE_LESS_ROLES) {
    const perms = caps(...rolePermissions(role.toLowerCase()))
    assert.equal(isNone(surface(role, rolePermissions(role.toLowerCase()))), true, `${role} must get no js-* surface`)
    assert.equal(isGeneratedToolName(role, perms, `todowrite-for-${role.toLowerCase()}`), false)
  }

  // Orchestrator has no file permission but holds Fork/Join, so it gets a
  // delegation-only programming surface (WHAT[repository-programming-056]).
  const orchestrator = surface('Orchestrator', rolePermissions('orchestrator'))
  assert.equal(isNone(orchestrator), false, 'Orchestrator gets the delegation-only surface')
  assert.deepEqual(
    projectedMembers(orchestrator),
    ['commission', 'horizon'],
  )
})

test('WHAT[repository-programming-001] JS001_role_projection_is_exactly_roles_permissions_intersection', () => {
  for (const office of EXPECTED_MEMBERS) {
    const permissions = rolePermissions(office.label)
    const result = surface(office.role, permissions)
    assert.equal(isSome(result), true, `${office.role} must get a js-* surface`)
    assert.deepEqual(projectedMembers(result), office.members, `${office.role} member set`)

    // The member gate resolves exactly the members the surface projects.
    for (const member of office.members) {
      assert.equal(
        memberBinding(office.role, permissions, member),
        member,
        `${office.role}: ${member} runtime gate`,
      )
    }
  }
})

test('WHAT[repository-programming-001] JS001_manager_carries_the_delegation_members_of_its_office', () => {
  const permissions = rolePermissions('manager')
  const manager = surface('Manager', permissions)
  const names = memberNames(manager)

  // Manager holds Fork/Resume/Join/Horizon/Advisor, so it carries the
  // dispatch members and the pull members alike.
  for (const member of ['fork', 'resume', 'horizon']) {
    assert.ok(names.includes(member), `Manager carries ${member}`)
    assert.equal(memberBinding('Manager', permissions, member), member, `Manager: ${member} runtime gate`)
  }

  // commission is the Orchestrator's projection of Fork and devops belongs to
  // the DevOpsConsultation permission: Manager holds neither.
  assert.equal(names.includes('commission'), false, 'commission is not a Manager member')
  assert.equal(names.includes('devops'), false, 'devops is not a Manager member')
  assert.equal(memberBinding('Manager', permissions, 'commission'), undefined)
  assert.equal(memberBinding('Manager', permissions, 'devops'), undefined)
})

test('WHAT[repository-programming-001] JS001_delegation_permissions_project_programming_surface_members', () => {
  // The delegation verbs live on the programming surface of the office that
  // holds the permission — nowhere else.
  assert.equal(memberBinding('Manager', rolePermissions('manager'), 'fork'), 'fork')
  assert.equal(memberBinding('Manager', rolePermissions('manager'), 'resume'), 'resume')
  assert.equal(memberBinding('Orchestrator', rolePermissions('orchestrator'), 'commission'), 'commission')
  assert.equal(memberBinding('Plan', rolePermissions('plan'), 'devops'), 'devops')

  // A permission nobody holds projects no member at all.
  assert.equal(memberBinding('Engineer', rolePermissions('engineer'), 'fork'), undefined)
  assert.equal(memberBinding('Engineer', rolePermissions('engineer'), 'resume'), undefined)
  assert.equal(memberBinding('Engineer', rolePermissions('engineer'), 'commission'), undefined)
  assert.equal(memberBinding('Engineer', rolePermissions('engineer'), 'devops'), undefined)
})

test('WHAT[repository-programming-001] JS001_lifecycle_members_are_carried_by_every_generated_surface', () => {
  for (const office of EXPECTED_MEMBERS) {
    const result = surface(office.role, rolePermissions(office.label))
    assert.equal(isSome(result), true, `${office.role} must get a js-* surface`)
    assert.match(result.baseClassSource, /async park\(payload\)/, `${office.role} carries park`)
  }
})

test('WHAT[repository-programming-001] JS001_non_projected_permissions_never_produce_members', () => {
  for (const name of ['Join', 'Edit', 'Pty', 'Fetch', 'Finality', 'ResetScope', 'BashHoneypot']) {
    const result = generate('Engineer', caps(name), 'en')
    assert.equal(isNone(result), true, `${name} alone must not generate a surface`)
  }
})

test('WHAT[repository-programming-001] JS001_member_projection_follows_the_capability_families', () => {
  // The capability families are the only source of a member: a permission that
  // projects members brings exactly its own family, in the canonical order.
  for (const [permission, members] of Object.entries(MEMBERS_BY_PERMISSION)) {
    const result = generate('Engineer', caps(permission), 'en')
    assert.equal(isSome(result), true, `${permission} alone must generate a surface`)
    const projected = projectedMembers(result)
    const expected = members
      .slice()
      .sort((left, right) => MEMBER_ORDER.indexOf(left) - MEMBER_ORDER.indexOf(right))
    assert.deepEqual(projected, expected, `${permission} member family`)
  }
})

test('WHAT[repository-programming-001] JS001_the_permission_label_list_is_the_single_authority', () => {
  const labels = permissionLabels()

  // No duplicates: a duplicate would make the list a set in disguise.
  assert.deepEqual(
    [...labels].sort(),
    [...new Set(labels)].sort(),
    'the permission label list must not repeat a label',
  )

  // Every listed label resolves back to itself, so the list holds only real
  // permissions and never a retired name.
  for (const label of labels) {
    assert.equal(permissionOfLabel(label), label, `${label} must resolve back to itself`)
  }

  // A name that is not a permission never resolves: "unknown label" stays
  // distinguishable from "known label that projects no member".
  assert.equal(permissionOfLabel('TodoWrite'), undefined, 'a retired name is not a permission')

  // The reverse direction: every label an office actually holds is listed. A
  // permission the offices hold but the list does not name fails here.
  const held = new Set(
    allRoleLabels
      .concat(SURFACE_LESS_ROLES.map((role) => role.toLowerCase()))
      .flatMap((label) => rolePermissions(label)),
  )
  for (const label of held) {
    assert.ok(labels.includes(label), `${label} is held by an office and must be listed`)
  }

  // A listed label no office holds is still a real permission: the catalog
  // covers permissions the offices may not exercise.
  for (const label of labels) {
    if (!held.has(label)) {
      assert.equal(permissionOfLabel(label), label, `${label} is listed and must be a real permission`)
    }
  }

  // The exact coverage: the list names every declared case of the union, and
  // nothing else. A permission the offices happen not to hold (Pty) still
  // belongs to the catalog, and a label the union does not declare must never
  // appear here.
  assert.deepEqual(
    [...labels].sort(),
    [...declaredPermissionCases()].sort(),
    'the permission label list must cover exactly the declared ToolPermission cases',
  )
})

