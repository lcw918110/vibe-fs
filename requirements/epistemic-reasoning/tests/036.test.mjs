import assert from 'node:assert/strict'
import test from 'node:test'
import { mkdtemp, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import * as events from '../../../dist/Persistence/EventStore/Surface.js'
import * as api from '../../../dist/Sphinx/InquirySurface.js'
import { integrationTest } from '../../verification-system/tests/support/tier-gate.mjs'

async function fixture(t) {
  const directory = await mkdtemp(join(tmpdir(), 'sphinx-turn-budget-'))
  const stores = []
  const open = () => {
    const store = events.create(directory, `writer-${stores.length}`)
    stores.push(store)
    return api.createRuntime(store)
  }
  t.after(async () => {
    for (const store of stores) events.dispose(store)
    await rm(directory, { recursive: true, force: true })
  })
  return { open }
}

function research({ gain = 1, available = Infinity, cost = 0, grounded = true } = {}) {
  let investigations = 0
  const requests = []
  const observe = async ({ request, budget }) => {
    requests.push({ type: request.type, budget })
    switch (request.type) {
      case 'SemanticAssessmentRequest':
        return { type: 'SemanticAssessment', forms: { Why: 1 } }
      case 'GenerateCandidatesRequest':
        return { type: 'Candidates', items: investigations >= available ? [] : [{
          method: 'CausalMechanism', question: `Independent investigation ${investigations}`,
          semanticKey: `candidate-${investigations}`, expectedRootGain: gain, gatewayGain: 0, cost,
        }] }
      case 'InvestigateRequest': {
        const id = `source-${investigations++}`
        return { type: 'Investigation', actionKey: request.action.id,
          findings: grounded ? [{ semanticKey: id, text: `Observed ${id}`, evidenceKeys: [id] }] : [],
          evidence: grounded ? [{ semanticKey: id, proposition: `Observed ${id}`,
            source: { id, kind: 'tool' }, dependencyKey: id }] : [],
        }
      }
      case 'SynthesizeRequest':
        return { type: 'Synthesis', text: 'Answer from accepted evidence', findingKeys: request.findingKeys, uncertainties: [] }
      default: assert.fail(`Unexpected request: ${request.type}`)
    }
  }
  return { observe, requests, count: () => investigations }
}

const run = (runtime, id, expected, study, question = 'Why does this happen?') =>
  api.runExpected(runtime, id, question, expected, undefined, study.observe, () => false)

for (const [expected, turns] of [[5, 5], [9, 9], [21, 21], [100, 101]]) {
  test(`WHAT[epistemic-reasoning-036] expected ${expected} buys ${turns} total work items under the reference loss model`, async t => {
    const { open } = await fixture(t)
    const study = research()
    const result = await run(open(), `reference-${expected}`, expected, study)
    assert.equal(study.requests.length, turns)
    assert.equal(study.count(), (turns - 3) / 2)
    assert.equal(result.answer.stopReason, 'turn-price')
    assert.equal(result.answer.turnBudget.usedTurns, turns)
    assert.equal(result.answer.turnBudget.expectedTurns, expected)
    assert.ok(Math.abs(result.answer.turnBudget.turnPrice - 1.44 / (expected - 1) ** 2) < 1e-12)
    assert.ok(study.requests.every(({ budget }) => budget.turnPrice === result.answer.turnBudget.turnPrice))
    study.requests.forEach(({ budget }, index) => assert.equal(budget.usedTurns, index + 1))
  })
}

test('WHAT[epistemic-reasoning-036] expected budget is neither a hard ceiling nor a quota', async t => {
  const { open } = await fixture(t)
  const runtime = open()
  const continuing = research()
  await run(runtime, 'over-target', 12, continuing)
  assert.equal(continuing.requests.length, 13)
  const done = research({ available: 1 })
  const answer = await run(runtime, 'finished-early', 100, done)
  assert.equal(done.requests.length, 5)
  assert.equal(answer.answer.stopReason, 'stop-dominates')
  const again = await run(runtime, 'finished-again', 100, research({ available: 1 }))
  assert.equal(again.answer.turnBudget.turnPrice, answer.answer.turnBudget.turnPrice)
  assert.equal(again.answer.turnBudget.calibrationSamples, 0, 'early exhaustion must not buy useless work through feedback')
})

test('WHAT[epistemic-reasoning-036] uncalibrated candidate costs cannot masquerade as purchased work-item cost', async t => {
  const { open } = await fixture(t)
  const study = research({ cost: 1e9 })
  const result = await run(open(), 'untrusted-cost', 21, study)
  assert.equal(study.requests.length, 21)
  assert.equal(result.answer.turnBudget.usedTurns, 21)
})

integrationTest('WHAT[epistemic-reasoning-036] one independent safety ceiling survives indefinitely useful proposals', async t => {
  const { open } = await fixture(t)
  const study = research({ grounded: false })
  const result = await run(open(), 'safety-ceiling', 100, study)
  assert.equal(study.requests.length, result.answer.turnBudget.safetyLimit)
  assert.equal(result.answer.turnBudget.usedTurns, study.requests.length)
  assert.equal(result.answer.stopReason, 'budget')
  assert.equal(result.status, 'unresolved')
})

test('WHAT[epistemic-reasoning-036] price and target survive restart and conflicting retries buy nothing', async t => {
  const { open } = await fixture(t)
  const study = research()
  const first = await run(open(), 'restart', 21, study)
  const replayed = await run(open(), 'restart', 21, { observe: () => assert.fail('accepted work was purchased again') })
  assert.deepEqual(replayed, first)
  await assert.rejects(run(open(), 'restart', 100, study), /expectTurns|budget|arguments/i)
  assert.equal(study.requests.length, 21)
})

integrationTest('WHAT[epistemic-reasoning-036] observed price-limited work calibrates later invocations of the same question and target', async t => {
  const { open } = await fixture(t)
  const runtime = open()
  const counts = []
  let first, last
  for (let i = 0; i < 12; i++) {
    const study = research({ gain: 0.5 })
    const result = await run(runtime, `calibration-${i}`, 21, study)
    first ??= result
    last = result
    counts.push(study.requests.length)
  }
  assert.ok(Math.abs(counts.at(-1) - 21) < Math.abs(counts[0] - 21), counts.join(', '))
  assert.ok(last.answer.turnBudget.turnPrice < first.answer.turnBudget.turnPrice)
  assert.equal(last.answer.turnBudget.calibrationSamples, 11)
  const restarted = await run(open(), 'calibration-restarted', 21, research({ gain: 0.5 }))
  assert.equal(restarted.answer.turnBudget.calibrationSamples, 12)
  const unrelated = await run(runtime, 'other-question', 21, research(), 'Another question')
  assert.equal(unrelated.answer.turnBudget.calibrationSamples, 0)
  t.diagnostic(`Target 21 with half-size gains: ${counts.join(', ')}`)
})

integrationTest('WHAT[epistemic-reasoning-036] calibration centers repeated discrete reference inquiries on expected 100', async t => {
  const { open } = await fixture(t)
  const runtime = open()
  const counts = []
  for (let i = 0; i < 8; i++) {
    const study = research()
    await run(runtime, `mean-${i}`, 100, study)
    counts.push(study.requests.length)
  }
  const mean = counts.reduce((sum, count) => sum + count, 0) / counts.length
  assert.ok(Math.abs(mean - 100) <= 0.25, `actual counts: ${counts}`)
  assert.ok(counts.some(count => count < 100) && counts.some(count => count > 100))
  t.diagnostic(`Reference target 100: ${counts.join(', ')}; mean ${mean}`)
})

test('WHAT[epistemic-reasoning-036] cancellation and failed work do not become calibration samples', async t => {
  const { open } = await fixture(t)
  const runtime = open()
  await api.runExpected(runtime, 'cancelled', 'Why does this happen?', 21, undefined,
    () => assert.fail('cancelled work must not dispatch'), () => true)
  await api.runExpected(runtime, 'failed', 'Why does this happen?', 21, undefined,
    () => { throw new Error('controlled worker failure') }, () => false)
  const after = await run(runtime, 'after-failures', 21, research())
  assert.equal(after.answer.turnBudget.calibrationSamples, 0)
  assert.equal(after.answer.turnBudget.usedTurns, 21)
})

test('WHAT[epistemic-reasoning-036] unsupported expectations are rejected before any work or budget is purchased', async t => {
  const { open } = await fixture(t)
  for (const expected of [1, 2, 3, 4, 0, -1, 1.5, 512, Infinity, NaN]) {
    await assert.rejects(run(open(), `invalid-${expected}`, expected, {
      observe: () => assert.fail('invalid expectation purchased work'),
    }), /expectTurns/i)
  }
})

for (const throughLane of [false, true]) {
  test(`WHAT[epistemic-reasoning-036] nested inquiry through ${throughLane ? 'Fission present' : 'Engineer'} inherits and charges the root budget`, async t => {
    const executionApi = await import('../../../dist/OpenCode/Host/SphinxExecutionSurface.js')
    const directory = await mkdtemp(join(tmpdir(), 'sphinx-shared-budget-'))
    const store = events.create(directory, 'shared')
    let execution, nestedAnswer, nested = false, purchases = 0
    const studies = new Map()
    execution = executionApi.createWithOwner(store, async invocation => {
      purchases++
      const worker = `${invocation.ownerSessionId}-worker`
      invocation.admitted(worker)
      if (invocation.ownerSessionId === 'owner' && !nested) {
        nested = true
        const childCaller = throughLane ? 'fission-lane' : worker
        await assert.rejects(executionApi.run(execution, childCaller, 'invalid-override', 'Nested question', 100, () => () => {}), /root budget/)
        nestedAnswer = await executionApi.run(execution, childCaller, 'nested', 'Nested question', undefined, () => () => {})
      }
      let study = studies.get(invocation.ownerSessionId)
      if (!study) { study = research({ available: 1 }); studies.set(invocation.ownerSessionId, study) }
      const marker = 'Current work and known basis:\n'
      const work = JSON.parse(invocation.charge.slice(invocation.charge.lastIndexOf(marker) + marker.length))
      assert.equal(work.budget.budgetRoot, 'root')
      assert.equal(work.budget.expectedTurns, 5)
      return JSON.stringify(await study.observe(work))
    }, async () => {}, present => present === 'fission-lane' ? 'owner-worker' : present)
    t.after(async () => {
      await executionApi.dispose(execution)
      events.dispose(store)
      await rm(directory, { recursive: true, force: true })
    })
    const answer = await executionApi.run(execution, 'owner', 'root', 'Root question', 5, () => () => {})
    assert.equal(purchases, 10)
    assert.equal(answer.turnBudget.usedTurns, purchases)
    assert.equal(nestedAnswer.turnBudget.expectedTurns, 5)
    assert.equal(nestedAnswer.turnBudget.turnPrice, answer.turnBudget.turnPrice)
    const replay = await executionApi.run(execution, throughLane ? 'fission-lane' : 'owner-worker', 'nested', 'Nested question', undefined, () => () => {})
    assert.deepEqual(replay, nestedAnswer, 'completed child reports retain their exact completion-time budget snapshot')
    assert.equal(purchases, 10)
  })
}

integrationTest('WHAT[epistemic-reasoning-036] a nested investigation cannot spend a second safety allowance', async t => {
  const { open } = await fixture(t)
  const runtime = open()
  const child = research({ grounded: false })
  let parentCalls = 0
  const result = await api.runExpected(runtime, 'budget-root', 'Parent question', 100, undefined, async () => {
    parentCalls++
    const nested = await api.runExpected(runtime, 'budget-child', 'Child question', undefined, 'budget-root', child.observe, () => false)
    assert.equal(nested.answer.stopReason, 'budget')
    return { type: 'SemanticAssessment', forms: { Why: 1 } }
  }, () => false)
  assert.equal(result.answer.stopReason, 'budget')
  assert.equal(parentCalls, 1)
  assert.equal(parentCalls + child.requests.length, result.answer.turnBudget.safetyLimit)
  assert.equal(result.answer.turnBudget.usedTurns, parentCalls + child.requests.length)
})

test('WHAT[epistemic-reasoning-036] closed root fences late child observations even without a child-local cancellation signal', async t => {
  const { open } = await fixture(t)
  const runtime = open()
  let entered, release, child
  const started = new Promise(resolve => { entered = resolve })
  let calls = 0
  const result = await api.runExpected(runtime, 'closing-root', 'Parent', 5, undefined, async ({ request }) => {
    calls++
    if (request.type === 'SemanticAssessmentRequest') {
      child = api.runExpected(runtime, 'late-child', 'Child', undefined, 'closing-root', ({ request: childRequest }) => {
        if (childRequest.type !== 'SemanticAssessmentRequest') return { type: 'Candidates', items: [] }
        entered()
        return new Promise(resolve => { release = () => resolve({ type: 'SemanticAssessment', forms: { Why: 1 } }) })
      }, () => false)
      await started
      return { type: 'SemanticAssessment', forms: { Why: 1 } }
    }
    return { type: 'Candidates', items: [] }
  }, () => false)
  assert.equal(calls, 2)
  assert.equal(result.answer.turnBudget.usedTurns, 3)
  release()
  const late = await child
  assert.equal(late.answer.revision, 0, 'a late observation cannot update epistemic state after its root closes')
  assert.equal(late.answer.stopReason, 'cancelled')
})
