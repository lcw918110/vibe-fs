import assert from 'node:assert/strict'
import test from 'node:test'
import * as routing from '../../../dist/OpenCode/Host/ModelRoutingSurface.js'

const templateUrl = new URL('../../../resources/wanxiangshu.mjs', import.meta.url)

const firstTarget = { model: 'provider/first', reasoning: 'high' }
const changedTarget = { model: 'other/changed', reasoning: 'none' }
const acquire = (runtime, physical) => routing.beginExecutionAdmission(runtime, 'devops-session', physical, 'devops', 'devops', null)
const commit = (runtime, acquired, physical) => routing.commitExecutionAdmission(runtime, acquired.lease, {
  sessionId: 'devops-session', physicalUserMessageId: physical,
  role: 'devops', participant: 'devops', target: routing.executionAdmissionTarget(runtime, acquired.lease),
})

test('WHAT[execution-model-routing-019] local DevOps binding rejects conflicting explicit rebind while original target remains available', async () => {
  const runtime = routing.createRuntime(() => firstTarget)
  routing.seedDevOpsModelTarget(runtime, 'devops-session', 'provider/first:high')
  for (const physical of ['first', 'next', 'third']) {
    const acquired = await acquire(runtime, physical)
    assert.equal(acquired.kind, 'Acquired')
    assert.deepEqual(routing.executionAdmissionTarget(runtime, acquired.lease), firstTarget)
    assert.deepEqual(commit(runtime, acquired, physical), { kind: 'Applied' })
    assert.throws(() => routing.seedDevOpsModelTarget(runtime, 'devops-session', 'other/changed:none'), /immutable/)
    assert.throws(() => routing.commitExecutionAdmission(runtime, acquired.lease, {
      sessionId: 'devops-session', physicalUserMessageId: physical,
      role: 'devops', participant: 'devops', target: changedTarget,
    }), /immutable/)
    assert.deepEqual(routing.boundDevopsTarget(runtime, 'devops-session'), firstTarget)
    routing.releasePhysicalExecution(runtime, 'devops-session', physical)
  }
})

test('WHAT[execution-model-routing-019] an unrecorded DevOps session is scheduled normally and routing never invents a binding for it', async () => {
  let allocations = 0
  let selected = firstTarget
  const runtime = routing.createRuntime(() => {
    allocations += 1
    return selected
  })
  // A human-rooted DevOps office session, and a road's DevOps child before any
  // target is recorded, both arrive here with no binding at all. Neither is a
  // "resume that changed the model": there is no recorded target to change.
  assert.equal(routing.boundDevopsTarget(runtime, 'devops-session'), null, 'a cold DevOps session reports no binding')
  const first = await acquire(runtime, 'unrecorded-first')
  assert.equal(first.kind, 'Acquired', 'an unrecorded DevOps session must reach the scheduler, not be refused')
  assert.equal(allocations, 1)
  assert.deepEqual(routing.executionAdmissionTarget(runtime, first.lease), firstTarget)
  assert.deepEqual(commit(runtime, first, 'unrecorded-first'), { kind: 'Applied' })
  assert.equal(
    routing.boundDevopsTarget(runtime, 'devops-session'),
    null,
    'committing a target must not fabricate a fixed binding nobody recorded',
  )
  routing.releasePhysicalExecution(runtime, 'devops-session', 'unrecorded-first')

  // Being unbound, the session follows the scheduler like any other session
  // instead of deadlocking on a model that a later preference stops offering.
  selected = changedTarget
  const second = await acquire(runtime, 'unrecorded-second')
  assert.equal(second.kind, 'Acquired')
  assert.equal(allocations, 2)
  assert.deepEqual(routing.executionAdmissionTarget(runtime, second.lease), changedTarget)
  routing.releasePhysicalExecution(runtime, 'devops-session', 'unrecorded-second')
})

test('WHAT[execution-model-routing-019] DevOps admission resolves missing cache entry from durable resolver into acceleration layer', async () => {
  // The real scheduler honours `previous` as a preference; here it answers with
  // that same target, which is the only answer a bound DevOps session accepts.
  const runtime = routing.createRuntime((role, running, previous) => previous ?? changedTarget)
  routing.setDurableDevopsResolver(runtime, (sessionId) => {
    if (sessionId === 'devops-session') return 'provider/first:high'
    return null
  })
  // First admission misses cache, queries durable resolver, populates acceleration layer
  const acquired = await acquire(runtime, 'durable-first')
  assert.equal(acquired.kind, 'Acquired')
  assert.deepEqual(routing.executionAdmissionTarget(runtime, acquired.lease), firstTarget)
  assert.deepEqual(routing.boundDevopsTarget(runtime, 'devops-session'), firstTarget)
  routing.releasePhysicalExecution(runtime, 'devops-session', 'durable-first')
})

test('WHAT[execution-model-routing-019] a scheduler that answers with another model queues the bound DevOps session instead of drifting it', async () => {
  const runtime = routing.createRuntime(() => changedTarget)
  routing.seedDevOpsModelTarget(runtime, 'devops-session', 'provider/first:high')
  const outcome = await acquire(runtime, 'drifting')
  assert.equal(outcome.kind, 'Queued', 'a different model answer is a refusal to run, not a new binding')
  routing.cancelPendingExecution(runtime, 'devops-session')
  assert.deepEqual(routing.boundDevopsTarget(runtime, 'devops-session'), firstTarget)
})

test('WHAT[execution-model-routing-019] dropSession purges bound DevOps target entry from acceleration layer', () => {
  const runtime = routing.createRuntime(() => firstTarget)
  routing.seedDevOpsModelTarget(runtime, 'ses-to-drop', 'provider/first:high')
  assert.deepEqual(routing.boundDevopsTarget(runtime, 'ses-to-drop'), firstTarget)
  routing.dropSession(runtime, 'ses-to-drop')
  assert.equal(routing.boundDevopsTarget(runtime, 'ses-to-drop'), null)
})

test('WHAT[execution-model-routing-019] changed scheduler cannot overwrite a previously bound DevOps target', async () => {
  let selected = firstTarget
  const runtime = routing.createRuntime(() => selected)
  routing.seedDevOpsModelTarget(runtime, 'devops-session', 'provider/first:high')
  const first = await acquire(runtime, 'first')
  assert.equal(first.kind, 'Acquired')
  assert.deepEqual(commit(runtime, first, 'first'), { kind: 'Applied' })
  routing.releasePhysicalExecution(runtime, 'devops-session', 'first')

  selected = changedTarget
  // The fixed binding is authority, not a hint to the scheduler: a changed
  // scheduler preference cannot drift a road's DevOps model. The session waits.
  const next = await acquire(runtime, 'next')
  assert.equal(next.kind, 'Queued')
  routing.cancelPendingExecution(runtime, 'devops-session')
  assert.deepEqual(routing.boundDevopsTarget(runtime, 'devops-session'), firstTarget)
})

test.todo('WHAT[execution-model-routing-019] fixed road binding persists across real restart and physical-session replacement without model drift (GAP-129)')

test('WHAT[execution-model-routing-019] durable DevOps target seeding is fail-closed and idempotent', async () => {
  const { default: route } = await import(`${templateUrl.href}?test=${Date.now()}`)
  const runtime = routing.createRuntime(route)
  const fixedTarget = { model: 'provider/fixed-devops', reasoning: 'high' }

  // 1. A road's durable target seeds the binding, and the binding reads back exactly that target.
  // Red if seeding stops writing (returns null), writes a different target, or silently skips a well-formed road target.
  routing.seedDevOpsModelTarget(runtime, 'ses_seed_bound', 'provider/fixed-devops:high')
  assert.deepEqual(
    routing.boundDevopsTarget(runtime, 'ses_seed_bound'),
    fixedTarget,
    'a seeded road target must be readable as the fixed DevOps target',
  )

  // 2. Reseeding the identical target is an idempotent no-op.
  // Red if a repeat seed throws (duplicate rejection) or drifts the stored target.
  routing.seedDevOpsModelTarget(runtime, 'ses_seed_bound', 'provider/fixed-devops:high')
  assert.deepEqual(routing.boundDevopsTarget(runtime, 'ses_seed_bound'), fixedTarget, 'repeat seed of the same target must stay idempotent')

  // 3. A conflicting target is refused, and the refusal must not overwrite the fixed binding.
  // Red if the immutability guard is weakened so a later seed silently replaces the target.
  assert.throws(
    () => routing.seedDevOpsModelTarget(runtime, 'ses_seed_bound', 'provider/other-model:low'),
    /DevOps model binding is immutable/,
    'a conflicting seed must be refused',
  )
  assert.deepEqual(
    routing.boundDevopsTarget(runtime, 'ses_seed_bound'),
    fixedTarget,
    'a refused reseed must leave the fixed target untouched',
  )

  // 4. Malformed durable targets are refused, and refuse without seeding anything.
  // Red if the parser is loosened (default reasoning, non-qualified model, blank reasoning) or if a failed parse still writes a partial binding.
  assert.throws(() => routing.seedDevOpsModelTarget(runtime, 'ses_seed_malformed', ''), /durable DevOps model target/)
  assert.throws(() => routing.seedDevOpsModelTarget(runtime, 'ses_seed_malformed', 'provider/fixed-devops'), /durable DevOps model target/)
  assert.throws(() => routing.seedDevOpsModelTarget(runtime, 'ses_seed_malformed', 'noslashmodel:high'), /provider\/model/)

  // 5. CUT-10: model names may contain ':' (ollama-cloud/gemma4:31b); the
  // reasoning is after the last ':'. Splitting at the first ':' bound the
  // DevOps road to model 'ollama-cloud/gemma4' with reasoning '31b:none'.
  routing.seedDevOpsModelTarget(runtime, 'ses_seed_colon', 'ollama-cloud/gemma4:31b:none')
  assert.deepEqual(routing.boundDevopsTarget(runtime, 'ses_seed_colon'), { model: 'ollama-cloud/gemma4:31b', reasoning: 'none' })
  assert.throws(() => routing.seedDevOpsModelTarget(runtime, 'ses_seed_malformed', 'provider/fixed-devops:'), /reasoning/)
  assert.equal(
    routing.boundDevopsTarget(runtime, 'ses_seed_malformed'),
    null,
    'a malformed target must not seed any binding',
  )

  // 5. A session with no seeded target has no binding.
  // Red if the query returns a default/guessed target, or if bindings are prewarmed for unknown sessions.
  assert.equal(
    routing.boundDevopsTarget(runtime, 'ses_never_seeded'),
    null,
    'a session without a seeded target must have no binding',
  )
})

test('WHAT[execution-model-routing-019] durable target seeding belongs only to the supplied runtime', () => {
  const first = routing.createRuntime(() => firstTarget)
  const second = routing.createRuntime(() => changedTarget)
  routing.seedDevOpsModelTarget(first, 'same-devops-session', 'provider/first:high')
  assert.deepEqual(routing.boundDevopsTarget(first, 'same-devops-session'), firstTarget)
  assert.equal(routing.boundDevopsTarget(second, 'same-devops-session'), null)
  routing.seedDevOpsModelTarget(second, 'same-devops-session', 'other/changed:none')
  assert.deepEqual(routing.boundDevopsTarget(second, 'same-devops-session'), changedTarget)
  assert.deepEqual(routing.boundDevopsTarget(first, 'same-devops-session'), firstTarget)
})
