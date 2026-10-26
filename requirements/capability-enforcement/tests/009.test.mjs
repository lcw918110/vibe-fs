import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { mkdtempSync, rmSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join } = await import("node:path");
const { acceptAuthorityRoot, grantWorkOwned, withExecutablePlugin } = await import("../../verification-system/tests/support/plugin-fixture.mjs");
const { generateRole } = await import("../../../dist/Repository/Programming/Js/GeneratorSurface.js");
const { execute, caseName, render } = await import("../../../dist/Repository/Programming/Js/WorkflowSurface.js");
const { clearDelegationHost } = await import("../../../dist/Repository/Programming/Js/RuntimeSurface.js");

// The delegation verbs are programming-surface members now: the host no
// longer registers `fork` / `resume` / `commission` as tools. The member
// block of the generated surface is where the payload shape is stated.
const memberDoc = (description, memberName) => {
  const start = description.indexOf(`async ${memberName}(`)
  assert.notEqual(start, -1, `the surface documents the ${memberName} member`)
  const end = description.indexOf('\n  }', start)
  return description.slice(start, end === -1 ? description.length : end)
}

test('WHAT[capability-enforcement-009] FORK_programming_surface_exposes_expected_members_and_neither_manager_fork_nor_commission_carries_keywords', async () => {
  const manager = generateRole('manager', 'en')
  const orchestrator = generateRole('orchestrator', 'en')

  const managerMembers = manager.members.map((member) => member.memberName)
  assert.equal(managerMembers.includes('fork'), true, 'the manager surface carries the fork member')
  assert.equal(managerMembers.includes('resume'), true, 'the manager surface carries the resume member')
  assert.equal(managerMembers.includes('commission'), false, 'the manager surface carries no commission member')

  const orchestratorMembers = orchestrator.members.map((member) => member.memberName)
  assert.equal(orchestratorMembers.includes('commission'), true, 'the orchestrator surface carries the commission member')

  assert.equal(manager.baseClassSource.includes('async fork(payload)'), true, 'the manager base class declares the fork member')
  assert.equal(manager.baseClassSource.includes('async resume(payload)'), true, 'the manager base class declares the resume member')
  assert.equal(manager.baseClassSource.includes('async commission('), false, 'the manager base class declares no commission member')
  assert.equal(orchestrator.baseClassSource.includes('async commission(payload)'), true, 'the orchestrator base class declares the commission member')

  const managerFork = memberDoc(manager.description, 'fork')
  assert.equal(managerFork.includes('keywords'), false, 'fork must not carry warm-start keywords')
  const orchestratorCommission = memberDoc(orchestrator.description, 'commission')
  assert.match(orchestratorCommission, /payload is \{ charge, name\?, calling\? \}/, 'the commission payload states its own shape')
  assert.equal(orchestratorCommission.includes('keywords'), false, 'commission must not carry warm-start keywords')
})
test('WHAT[capability-enforcement-009] FORK_disposed_or_unbound_execution_surface_surfaces_natural_execution_consequence', async () => {
  await withExecutablePlugin(async (hooks) => {
    // The delegation verb is a programming-surface member now: the program
    // carries the payload and the report mapping.
    const result = await hooks.tool['todowrite-for-manager'].execute(
      { program: 'class Js extends JsProgram { async run() { return await this.fork({ calling: "engineer", name: "Ada", charge: "do work" }, { 1: null }); } }',
          name: 'unbound',
          intent: 'Wait for the delegation refusal within this request window.'
        },
      { sessionID: '', agent: 'engineer' },
    )
    assert.match(result, /cannot be placed from this execution context|caller's authority is established|调用方权威确立之前/i)
    assert.doesNotMatch(result, /sessionID|\berror\s*=/i)
  })

  // A disposed (unbound) execution surface carries no delegation host: the
  // member refuses with a data envelope instead of failing the program.
  const directory = mkdtempSync(join(tmpdir(), 'wxs-cap009-'))
  const sessionID = `ses-cap009-${Math.random().toString(36).slice(2, 8)}`
  try {
    clearDelegationHost(sessionID)
    const outcome = await execute(
      directory,
      'Manager',
      'en',
      sessionID,
      { name: 'disposed', intent: 'Observe the unavailable delegation owner without repeating work.', program: 'class Js extends JsProgram { async run() { return await this.fork({ calling: "engineer", name: "Ada", charge: "do work" }, { 1: null }); } }' },
      3000,
      Date.now() + 60_000,
      1 << 20,
      null,
    )
    assert.equal(caseName(outcome), 'Succeeded', 'the refusal is data, not a program failure')
    const doc = render(outcome)
    assert.match(doc, /ok = false/)
    assert.match(doc, /code = "DELEGATION_UNAVAILABLE"/)
    assert.equal(doc.includes('name = "Ada"'), false, 'no child was placed')
  } finally {
    clearDelegationHost(sessionID)
    rmSync(directory, { recursive: true, force: true })
  }
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { rolePredicate } = await import("../../../dist/OpenCode/Tools/ToolRegistrySurface.js");
const { generateRole } = await import("../../../dist/Repository/Programming/Js/GeneratorSurface.js");
const { allRoleLabels } = await import("../../../dist/Foundation/RolesSurface.js");
const { LEGACY_FORBIDDEN_NAMES, extractKnownToolNames, scanEntries, scanRepo } = await import("../../../scripts/checks/tool-referential-integrity.mjs");

const REPO_ROOT = new URL('../../..', import.meta.url).pathname

const LEGACY_VERDICT = `
module VerdictTool =
    let spec factory scope =
        { Name = "verdict"
          Description = "legacy"
          Arguments = []
          Execute = fun _ _ -> task { return "" } }
`
const STATIC_TOOLS_SNIPPET = `
module StaticTools =
    let knownToolNames =
        [ "fork"
          "resume"
          "commission"
          "join"
          "horizon" ]
`
const REGISTRY_SNIPPET = `
module ToolRegistry =
    let rolePredicate specName parkedHost sessionId =
        match specName with
        | "fork" -> fun _ -> true
        | "join" -> fun _ -> true
        | _ -> fun _ -> false
`

test('WHAT[capability-enforcement-009] gate_a_documents_legacy_forbidden_names', () => {
  assert.ok(LEGACY_FORBIDDEN_NAMES.includes('verdict'))
  assert.ok(LEGACY_FORBIDDEN_NAMES.includes('list'))
  assert.ok(LEGACY_FORBIDDEN_NAMES.includes('inspect'))
})
test('WHAT[capability-enforcement-009] gate_a_legacy_tool_name_is_red', () => {
  const violations = scanEntries([{ file: 'VerdictTool.fs', text: LEGACY_VERDICT }])
  assert.ok(violations.some((v) => v.code === 'legacy-tool-name' && v.detail?.includes('verdict')))
})
test('WHAT[capability-enforcement-009] gate_a_unknown_tool_not_in_static_is_red', () => {
  const customSpec = `
module CustomTool =
    let spec factory scope syncDelegate =
        { Name = "custom-unknown"
          Description = "custom"
          Arguments = []
          Execute = fun _ _ -> task { return "" } }
`
  const violations = scanEntries([{ file: 'CustomTool.fs', text: customSpec }], {
    staticTools: STATIC_TOOLS_SNIPPET,
    toolRegistry: REGISTRY_SNIPPET,
  })
  assert.ok(violations.some((v) => v.code === 'unknown-tool-not-in-static' && v.detail?.includes('custom-unknown')))
})
test('WHAT[capability-enforcement-009] gate_a_extract_known_tool_names', () => {
  assert.deepEqual(extractKnownToolNames(STATIC_TOOLS_SNIPPET), ['fork', 'resume', 'commission', 'join', 'horizon'])
})
test('WHAT[capability-enforcement-009] gate_a_repo_scan_is_green', () => {
  const result = scanRepo(REPO_ROOT)
  assert.equal(result.ok, true, JSON.stringify(result.violations, null, 2))
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { rolePredicate } = await import("../../../dist/OpenCode/Tools/ToolRegistrySurface.js");


test('WHAT[capability-enforcement-009] TOOLSPEC_review_and_finality_tools_have_owner_defined_admission', () => {
  // review: Manager only (Relay)
  assert.equal(rolePredicate('review', 'manager'), true)
  assert.equal(rolePredicate('review', 'engineer'), false)

  // deliver: retired as a lifecycle tool; review is the only lifecycle entry (work-053)
  assert.equal(rolePredicate('deliver', 'manager'), false)
  assert.equal(rolePredicate('deliver', 'engineer'), false)
  assert.equal(rolePredicate('review', 'plan'), true)
})
test('WHAT[capability-enforcement-009] TOOLSPEC_unknown_tools_and_host_natives_fail_closed', () => {
  assert.equal(rolePredicate('nonexistent-tool', 'engineer'), false)
  for (const retired of ['read', 'write', 'edit', 'glob', 'grep', 'mv', 'rm']) {
    assert.equal(rolePredicate(retired, 'engineer'), false, `retired ${retired} must fail closed`)
  }
  assert.equal(rolePredicate('skill', 'engineer'), false)
})
}

