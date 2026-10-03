import assert from 'node:assert/strict'
import test from 'node:test'
import { call, decode } from './support/cycle.mjs'

test('WHAT[behavior-diagnosis-009] actual cycle decoder accepts one completed call and refuses zero or two', () => {
  const single = decode([call()])
  assert.equal(single.decodedCalls, 1)
  assert.deepEqual(single.decision, {
    ok: true,
    value: {
      text: 'resolve the current question work result settled continue on the settled path',
      evidence: '',
      ruleId: 'primitive-obsession',
      toolCallIds: ['call-1'],
    },
  })
  for (const parts of [[], [call(), call({ callID: 'call-2' })]]) {
    assert.equal(decode(parts).decision.ok, false)
  }
})

// GAP-112 real raw two-call terminal with only one decodable call commits nothing and advances no coverage

