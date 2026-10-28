import assert from 'node:assert/strict'
import test from 'node:test'
import {
  agentIdOf,
  forkCall,
  joinCall,
  observeTerminals,
  physicalUserMessageOf,
  resumeCall,
  workLifecycleOf,
  withRealFork,
} from './support/real-fork.mjs'

// Field incident 2026-10-28 01:08–01:22 UTC (Manager ses_e7fff8f2fffe…): two
// children (Engineer `review-admission`, DevOps `devops`) were still writing
// their closing prose when an Advisor answer was queued as a busy steer. The
// Host loop ran straight from the finished report into that answer without a
// session.idle, the late-consultation stop aborted the queued step, and the
// only turn after the answer was the stop's own empty MessageAbortedError. The
// idle reconcile classified that abort (TurnAborted = observation only), the
// report's terminal was never published, no HandleWorkCompleted was written,
// and the Manager's join waited forever.
test('WHAT[delegation-039] a late consultation stop settles the run on the finished report it preserved', async () => {
  const owner = 'owner-late-consultation-039'
  await withRealFork(owner, async ({ hooks, createdIds, runtime }) => {
    const terminals = await observeTerminals(runtime)
    try {
      const started = await forkCall(hooks, owner, 'Ada', 'REVIEW-ADMISSION')
      assert.match(started, /Ada/)
      const child = createdIds[0]
      const agentId = agentIdOf(started)
      await runtime.awaitIngress()
      const root = physicalUserMessageOf(runtime, child)
      const history = async () => (await runtime.client.session.messages({ path: { id: child } })).data
        .map(message => ({ ...message, info: { ...(message.info ?? message), sessionID: child } }))

      // The closing report is being written.
      const run = `run-${child}-1`
      const report = {
        info: {
          id: run, role: 'assistant', sessionID: child, parentID: root.id, agent: 'engineer',
          providerID: 'provider', modelID: 'engineer-model', time: { created: 2 },
        },
        parts: [{ type: 'text', text: 'CLOSING REPORT' }],
      }
      runtime.pushHostMessage(child, report)
      await hooks['experimental.chat.messages.transform']({ sessionID: child }, { messages: await history() })
      const delivered = await hooks.tool['todowrite-for-engineer'].execute(
        {
          name: `child-program-${run}`,
          intent: 'Deliver the closing material inside the report turn.',
          remainingScopeTurnsEstimate: 0,
          program: `class Js extends JsProgram { async run() { return { answer: "REPORT-DONE" }; } }`,
        },
        { sessionID: child, agent: 'engineer', callID: `call-${run}`, messageID: run },
      )
      assert.match(String(delivered), /^# ok/)

      // The consultation answer arrives while the report is still running: a
      // busy steer queued behind it.
      const guided = await resumeCall(hooks, owner, 'Ada', 'ADVISOR-ANSWER')
      assert.match(guided, /Ada/)
      await runtime.awaitIngress()
      const steer = physicalUserMessageOf(runtime, child)
      assert.notEqual(steer.id, root.id, 'the answer is its own physical user message')
      for (const part of steer.parts ?? []) part.metadata = { ...(part.metadata ?? {}), wanxiangshu_consultation_steer: true }

      // The report finishes and the Host loop goes straight to the queued
      // answer: the next request's transform runs before the report's own
      // message.updated reaches the plugin (field order: loop step → cancel →
      // report terminal), and no session.idle comes in between.
      report.info.time.completed = 3
      report.info.finish = 'stop'
      const abortedBefore = runtime.abortedIds.length
      const output = { messages: await history() }
      await hooks['experimental.chat.messages.transform']({ sessionID: child }, output)
      assert.deepEqual(runtime.abortedIds.slice(abortedBefore), [child], 'the late answer is stopped')

      // The stop leaves only an empty aborted turn after the answer; then idle.
      const aborted = {
        info: {
          id: `run-${child}-2`, role: 'assistant', sessionID: child, parentID: steer.id, agent: 'engineer',
          providerID: 'provider', modelID: 'engineer-model', time: { created: 4, completed: 4 },
          error: { name: 'MessageAbortedError', data: { message: 'Aborted' } },
        },
        parts: [],
      }
      // The abort lands in the Host transcript before the report's own
      // message.updated reaches the plugin (field: cancel 01:17:09.544, report
      // terminal fact 01:17:09.570), so every snapshot already ends in it.
      runtime.pushHostMessage(child, aborted)
      await hooks.event({ event: { type: 'message.updated', properties: { info: report.info } } })
      await hooks.event({ event: { type: 'message.updated', properties: { info: aborted.info } } })
      await hooks.event({ event: { type: 'session.idle', properties: { sessionID: child } } })

      await terminals.awaitRun(child, run)
      assert.equal(workLifecycleOf(runtime, owner, agentId, child, root.id), 'CompletedAwaitingJoin')
      const joined = await joinCall(hooks, owner)
      assert.match(joined, /CLOSING REPORT/)
    } finally {
      terminals.dispose()
    }
  })
})
