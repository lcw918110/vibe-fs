import assert from 'node:assert/strict'
import test from 'node:test'
import {
  forkCall,
  observeTerminals,
  physicalUserMessageOf,
  resumeCall,
  withRealFork,
} from './support/real-fork.mjs'

// Field incident 2026-10-28 06:20–06:25 UTC (Manager ses_e7ed7cdb2ffe…, child
// Engineer `esb1t2`/控制路径 ses_e7ed78478ffe…): the child's turn ended
// finish=length with no prose and no tool call while an Advisor answer was
// queued as a busy steer. The late-consultation stop judged that empty turn
// "finished", aborted the answer's step, and its Cancelled terminal revoked the
// session's quiescence; the missing-final-report repair was abandoned twice
// (SupersededBeforePhysicalSend), nothing woke the child again, no
// HandleWorkCompleted was written and the Manager's join waited forever.
test('WHAT[delegation-040] an empty finished turn is no closing report: the queued answer is not stopped', async () => {
  const owner = 'owner-empty-turn-040'
  await withRealFork(owner, async ({ hooks, createdIds, runtime }) => {
    const terminals = await observeTerminals(runtime)
    try {
      const started = await forkCall(hooks, owner, 'Ada', 'CONTROL-PATH')
      assert.match(started, /Ada/)
      const child = createdIds[0]
      await runtime.awaitIngress()
      const root = physicalUserMessageOf(runtime, child)
      const history = async () => (await runtime.client.session.messages({ path: { id: child } })).data
        .map(message => ({ ...message, info: { ...(message.info ?? message), sessionID: child } }))

      const run = `run-${child}-1`
      const empty = {
        info: {
          id: run, role: 'assistant', sessionID: child, parentID: root.id, agent: 'engineer',
          providerID: 'provider', modelID: 'engineer-model', time: { created: 2 },
        },
        parts: [],
      }
      runtime.pushHostMessage(child, empty)
      await hooks['experimental.chat.messages.transform']({ sessionID: child }, { messages: await history() })

      const guided = await resumeCall(hooks, owner, 'Ada', 'ADVISOR-ANSWER')
      assert.match(guided, /Ada/)
      await runtime.awaitIngress()
      const steer = physicalUserMessageOf(runtime, child)
      assert.notEqual(steer.id, root.id, 'the answer is its own physical user message')
      for (const part of steer.parts ?? []) part.metadata = { ...(part.metadata ?? {}), wanxiangshu_consultation_steer: true }

      // The empty turn ends on the output limit; the Host loop goes straight to
      // the queued answer.
      empty.info.time.completed = 3
      empty.info.finish = 'length'
      const abortedBefore = runtime.abortedIds.length
      // Not stopped, the request goes on to the ordinary stages; this harness
      // has no Host run bound for the answer, so the provider start boundary
      // is where it ends here (the stop, by contrast, returns before it).
      await hooks['experimental.chat.messages.transform']({ sessionID: child }, { messages: await history() })
        .catch(error => assert.match(String(error?.message ?? error), /host-run-unavailable/))
      assert.deepEqual(runtime.abortedIds.slice(abortedBefore), [], 'the answer after an empty turn runs as ordinary work')
    } finally {
      terminals.dispose()
    }
  })
})
