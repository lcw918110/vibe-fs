import assert from 'node:assert/strict'
import test from 'node:test'
import * as change from '../../../dist/Change/Surface.js'

test('WHAT[effect-accounting-012] actual relay records confirmed rebase and a new review before its publish claim and physical update', async () => {
  const result = await change.observeRelayProgram('fresh')
  assert.equal(result.verdict.kind, 'Published')
  const rebase = result.timeline.indexOf('fact:RebasedCandidateReady')
  const review = result.timeline.indexOf('await:Candidate', rebase + 1)
  const claim = result.timeline.indexOf('fact:PublishClaimed')
  const update = result.timeline.indexOf('git:ff:rebased-1')
  assert.ok(rebase >= 0 && review > rebase && claim > review && update > claim)
  assert.equal(result.ffCalls, 1)
})

test('WHAT[effect-accounting-012] actual relay rejects a claim whose witness no longer matches, without a publish claim or physical update', async () => {
  // The former `reentry-missing-rebased` scenario was retired together with the
  // cross-module workflow control state; a claim can now only arrive through the
  // projection, so the remaining guard is the claim-vs-witness comparison.
  const result = await change.observeRelayProgram('reentry-claim-target-mismatch')
  assert.equal(result.verdict.kind, 'IntegrationFailed')
  assert.match(result.verdict.detail, /Publish claim target mismatch/)
  assert.equal(result.facts.includes('PublishClaimed'), false)
  assert.equal(result.ffCalls, 0)
})

test('WHAT[effect-accounting-012] a scenario name the program no longer owns fails closed instead of inventing a world', async () => {
  await assert.rejects(
    () => change.observeRelayProgram('reentry-missing-rebased'),
    (error) => {
      const text = String((error && error.message) || error)
      assert.match(text, /unknown Change program scenario/)
      return true
    },
  )
})
