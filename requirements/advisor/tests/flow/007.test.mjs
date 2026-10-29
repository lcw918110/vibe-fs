import assert from 'node:assert/strict'
import test from 'node:test'
import { DatabaseSync } from 'node:sqlite'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { SqliteJournal } from 'wanxiangli/eventstore'
import { AdvisorFlow, AdvisorProcessQueue, advisorClaim, advisorFinish, advisorPending, advisorStageBegin, advisorStageConfirm } from 'wanxiangli/scope-progress'
import { ToolRuntimeScopeSurface } from '../../../../dist/OpenCode/Tools/ToolRuntimeScopeSurface.js'
import * as dispatcher from '../../../../dist/Interaction/Dispatch/DispatchSurface.js'
import {
  withJournal, hostPort, prepareAcceptanceObserver, acceptObservedPhysical,
} from '../../../dispatch-protocol/tests/support/authority.mjs'

const ok = Prose => ({ Ok: true, Prose, Reason: '' })
const waitFor = async predicate => {
  for (let n = 0; n < 100; n++) {
    if (predicate()) return
    await new Promise(resolve => setTimeout(resolve, 10))
  }
  assert.fail('expected durable Advisor transition was not observed')
}
const makeRuntime = ToolRuntimeScopeSurface.createAdvisorSender

const withDatabase = async action => {
  const dir = mkdtempSync(join(tmpdir(), 'advisor-flow-physical-'))
  const name = join(dir, 'advisor.sqlite')
  const journalCall = callback => {
    const journal = new SqliteJournal(new DatabaseSync(name))
    try { return callback(journal) } finally { journal.close() }
  }
  try { await action(journalCall) } finally { rmSync(dir, { recursive: true, force: true }) }
}

for (const advanceDuringDelivery of [false, true]) {
  test(`WHAT[advisor-007] actual four-step Flow ${advanceDuringDelivery ? 'cannot complete stale Scope' : 'only completes after physical acceptance'}`, async t => {
    await withDatabase(async persist => withJournal('advisor-end-to-end-' + advanceDuringDelivery, async handle => {
      const context = await prepareAcceptanceObserver(handle, 'continuation', 'advisor-complete-' + advanceDuringDelivery)
      let epoch = 'scope-5'
      let pendingKey
      let signalSent
      const dispatched = new Promise(resolve => { signalSent = resolve })
      const runtime = makeRuntime(handle, hostPort(async (_sid, _text, options) => {
        pendingKey = options.Metadata.wanxiangshu_prompt_key
        signalSent()
        return dispatcher.admittedWithReceipt('sdk-submitted-not-physical')
      }))
      t.after(() => runtime.dispose())
      const snapshot = () => persist(journal => journal.read('advisor-process:' + context.session).map(JSON.parse))
      const queue = AdvisorProcessQueue()
      let modelCalls = 0
      const flow = AdvisorFlow(queue, {
        CurrentEpoch: () => epoch,
        LanguageFor: () => 'en',
        Claim: async (sid, scope, id, issue) => persist(journal => advisorClaim(journal, sid, scope, id, issue)),
        Finish: async (sid, scope, ids, outcome) => persist(journal => advisorFinish(journal, sid, scope, ids, outcome)),
        BeginStage: async (sid, scope, process, phase, question, members) => persist(journal => advisorStageBegin(journal, sid, scope, process, phase, question, members)),
        ConfirmStage: async (sid, scope, process, phase, prose) => persist(journal => advisorStageConfirm(journal, sid, scope, process, phase, prose)),
        Pending: async (sid, scope) => persist(journal => advisorPending(journal, sid, scope)),
        AskWorker: async () => { modelCalls++; return ok('worker evidence') },
        AskSeat: async () => { modelCalls++; return ok('advisor recommendation') },
        SendSteer: async (sid, scope, id, prose) => {
          const result = await runtime.send(sid, prose, id, () => epoch === scope)
          return result.ok ? ok('') : { Ok: false, Prose: '', Reason: result.error }
        },
      })
      const admitted = await flow.trigger(context.session, epoch, 'actual-run', 'why was estimate unchanged?')
      assert.equal(admitted.start, true)
      await dispatched
      assert.equal(modelCalls, 4)
      assert.equal(queue.hasActive(context.session, 'scope-5'), true)
      assert.deepEqual(snapshot().filter(item => item.kind === 'Finished'), [],
        'SDK queued receipt is not authority to persist Finished(Completed)')
      if (advanceDuringDelivery) epoch = 'scope-6'
      const landed = await acceptObservedPhysical(handle, context, pendingKey, 'actual-advisor-physical-' + advanceDuringDelivery)
      assert.equal(landed.ok, true, landed.error)
      if (advanceDuringDelivery) {
        await new Promise(resolve => setTimeout(resolve, 50))
        assert.equal(snapshot().filter(item => item.kind === 'Finished').length, 0)
        assert.equal(queue.hasActive(context.session, 'scope-5'), true)
        assert.deepEqual(persist(journal => advisorPending(journal, context.session, 'scope-5')).pending.map(x => x.reportId),
          ['actual-run'])
      } else {
        await waitFor(() => snapshot().some(item => item.kind === 'Finished'))
        assert.deepEqual(snapshot().filter(item => item.kind === 'Finished').map(item => item.outcome), ['Completed'])
        assert.equal(snapshot().filter(item => item.kind === 'StageAnswered').length, 5,
          'all model replies and the physical steer must be durably confirmed')
        assert.equal(queue.hasActive(context.session, 'scope-5'), false)
        assert.deepEqual(persist(journal => advisorPending(journal, context.session, 'scope-5')).pending, [])
      }
    }))
  })
}
