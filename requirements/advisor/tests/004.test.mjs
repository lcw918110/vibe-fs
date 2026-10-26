import assert from 'node:assert/strict'
import test from 'node:test'
import { AdvisorFlow, AdvisorProcessQueue } from 'wanxiangli/scope-progress'

const ok = (Prose) => ({ Ok: true, Prose, Reason: '' })
const settle = async (predicate) => {
  for (let attempts = 0; attempts < 80; attempts++) {
    if (predicate()) return
    await new Promise((resolve) => setTimeout(resolve, 10))
  }
  assert.fail('the four-stage consultation did not finish')
}

test('WHAT[advisor-004] two worker questions consult the same continuing Advisor seat without leaking orchestration', async () => {
  const queue = AdvisorProcessQueue()
  const observed = []
  const seatHistory = []
  const steers = []
  const flow = AdvisorFlow(queue, {
    Claim: async () => ({ ok: true, accepted: true }),
    Finish: async () => ({ ok: true, status: 'Committed' }),
    BeginStage: async () => ({ ok: true, start: true }),
    ConfirmStage: async () => ({ ok: true, status: 'Committed' }),
    Pending: async () => ({ ok: true, pending: [] }),
    LanguageFor: () => 'en',
    CurrentEpoch: () => 'scope-3',
    AskWorker: async (_session, question) => {
      observed.push(['worker', question])
      return ok(observed.length === 1
        ? 'The current test failed after a provider retry.'
        : 'The retry used a stale accepted request; the event log confirms it.')
    },
    AskSeat: async (session, question) => {
      assert.equal(session, 'working-session')
      seatHistory.push(question)
      observed.push(['seat', question])
      return ok(seatHistory.length === 1
        ? 'Show the actual accepted request and its failure result.'
        : 'Compare both receipts before replaying the request.')
    },
    SendSteer: async (session, epoch, processId, answer) => {
      steers.push({ session, epoch, processId, answer })
      return ok('')
    },
  })
  assert.equal((await flow.trigger('working-session', 'scope-3', 'report-1', 'Why did the remaining work increase?')).start, true)
  await settle(() => steers.length === 1)
  assert.deepEqual(observed.map(([kind]) => kind), ['worker', 'seat', 'worker', 'seat'])
  assert.equal(seatHistory.length, 2)
  assert.match(seatHistory[0], /What background do you need/)
  assert.match(observed[2][1], /You raised the following problem:/)
  assert.match(seatHistory[1], /Additional background:/)
  assert.match(observed[2][1], /The current test failed/)
  assert.match(observed[2][1], /Show the actual accepted request/)
  assert.equal(steers[0].answer, 'Compare both receipts before replaying the request.')
  assert.equal(steers[0].epoch, 'scope-3')
  for (const [, prompt] of observed) {
    assert.doesNotMatch(prompt, /temporary session|archived context|prose[1-4]|Scope snapshot|Advisor Process/)
  }
})

test('WHAT[advisor-004] replay and a retired scope cannot trigger another Advisor model exchange', async () => {
  const queue = AdvisorProcessQueue()
  let epoch = 'scope-one'
  let calls = 0
  const steers = []
  const flow = AdvisorFlow(queue, {
    Claim: async () => ({ ok: true, accepted: true }),
    Finish: async () => ({ ok: true, status: 'Committed' }),
    BeginStage: async () => ({ ok: true, start: true }),
    ConfirmStage: async () => ({ ok: true, status: 'Committed' }),
    Pending: async () => ({ ok: true, pending: [] }),
    LanguageFor: () => 'en',
    CurrentEpoch: () => epoch,
    AskWorker: async () => { calls++; return ok('worker answer') },
    AskSeat: async () => { calls++; return ok('seat answer') },
    SendSteer: async (...args) => { steers.push(args); return ok('') },
  })
  await flow.trigger('worker', epoch, 'report', 'problem')
  await settle(() => steers.length === 1)
  assert.equal(calls, 4)
  assert.equal((await flow.trigger('worker', epoch, 'report', 'problem')).start, false)
  assert.equal(calls, 4)
  epoch = 'scope-two'
  assert.equal((await flow.trigger('worker', 'scope-one', 'retired', 'stale problem')).ok, false)
  assert.equal(calls, 4)
  queue.reset('worker', epoch)
  assert.equal((await flow.trigger('worker', epoch, 'fresh', 'new problem')).start, true)
  await settle(() => steers.length === 2)
  assert.equal(calls, 8)
})
