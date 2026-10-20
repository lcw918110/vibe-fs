import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const recovery = await import("../../../dist/Execution/Session/Recovery/Surface.js");

const root = 'ses_root'
const work = (session) => ({ kind: 'work', session })
const child = (parent, session, handle) => ({ kind: 'child', parent, child: session, handle })
const companion = (main, session) => ({ kind: 'companion', main, companion: session })
const managerJob = (job, manager) => ({ kind: 'managerJob', job, manager })

test('WHAT[crash-reconciliation-014] closure validator accepts unique sessions and returns exact membership', () => {
  const result = recovery.validateClosure(root, [child('p', 'c', 'h1'), companion('m', 'c2'), work('w1')])
  assert.equal(result.ok, true)
  assert.deepEqual([...result.members].sort(), ['A:p>c:h1', 'C:m>c2', 'W:w1'])
})
test('WHAT[crash-reconciliation-014] CRASH_CLOSURE_duplicate_session_is_a_cycle_block', () => {
  const result = recovery.validateClosure(root, [work('w1'), work('w1')])
  assert.equal(result.ok, false)
  assert.equal(result.error, 'RecoveryCycle')
})
test('WHAT[crash-reconciliation-014] CRASH_CLOSURE_member_tokens_are_stable_identities', () => {
  assert.equal(recovery.token(work('w')), 'W:w')
  assert.equal(recovery.token(child('p', 'c', 'h1')), 'A:p>c:h1')
  assert.equal(recovery.token(companion('m', 'c2')), 'C:m>c2')
  assert.equal(recovery.token(managerJob('j1', 'm4')), 'M:j1:m4')
})
test('WHAT[crash-reconciliation-014] CRASH_CLOSURE_members_set_matches_tokens', () => {
  const result = recovery.validateClosure(root, [work('w1'), child('p', 'c', 'h1')])
  assert.deepEqual([...result.members].sort(), ['A:p>c:h1', 'W:w1'])
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const recovery = await import("../../../dist/Execution/Session/Recovery/Surface.js");


test('WHAT[crash-reconciliation-014] MISC_recovery_validate_closure_pure', () => {
  const valid = recovery.validateClosure('a1', [
    { kind: 'work', session: 'a1' },
    { kind: 'child', parent: 'a1', child: 'a2', handle: 'h1' },
  ])
  assert.equal(valid.ok, true)
  const cycle = recovery.validateClosure('a1', [
    { kind: 'work', session: 'a1' },
    { kind: 'child', parent: 'a1', child: 'a2', handle: 'h1' },
    { kind: 'companion', main: 'a1', companion: 'a2' },
  ])
  assert.equal(cycle.ok, false)
  assert.equal(cycle.error, 'RecoveryCycle')
})
}
