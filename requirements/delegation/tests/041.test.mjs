import test from 'node:test'
import assert from 'node:assert/strict'
import {withRealFork} from './support/real-fork.mjs'

const steerPart = { type: 'text', text: 'answer', metadata: { wanxiangshu_consultation_steer: true } }
const user = (sid, id, agent, parts) => ({ info: { id, role: 'user', sessionID: sid, agent }, parts })
const assistant = (sid, id, agent, parts) =>
  ({ info: { id, role: 'assistant', sessionID: sid, agent, mode: agent, finish: 'stop', time: { created: 2, completed: 5 } }, parts })

// Field incident 2026-10-28 10:56 UTC (Manager ses_e7ed7cdb2ffe…, "指南.txt 重构指南实施"):
// the Manager ended a turn in prose with no tool call (msg_18227b36a, finish=stop);
// the Advisor answer msg_18227b433 landed as its consultation steer; the late-consultation
// stop judged the prose a closing report and aborted the answer's step 9 s later
// (opencode.log `message=cancel`, msg_18227d63d MessageAbortedError). The Manager
// stood still until the owner typed again at 11:00:36.
test('WHAT[delegation-041] a long-running role is never stopped by a late consultation answer', async () => {
  await withRealFork('long-running-consultation-041', async ({ hooks, runtime }) => {
    for (const agent of ['manager', 'orchestrator', 'plan']) {
      const sid = `consult-041-${agent}`
      const output = { messages: [
        user(sid, 'u', agent, [{ type: 'text', text: 'task' }]),
        assistant(sid, 'a', agent, [{ type: 'step-start' }, { type: 'text', text: 'Interim report; work goes on.' }, { type: 'step-finish' }]),
        user(sid, 's', agent, [steerPart]),
      ] }
      const before = runtime.abortedIds.length
      await assert.rejects(hooks['experimental.chat.messages.transform']({}, output), /accepted-execution-missing/,
        `${agent}: the answer goes on to the ordinary stages as the nudge`)
      assert.deepEqual(runtime.abortedIds.slice(before), [], `${agent}: no AbortSession`)
      assert.notDeepEqual(output.messages, [], `${agent}: the request is not cleared`)
    }

    // Unchanged boundary: a prose-closing role is still stopped.
    const sid = 'consult-041-engineer'
    const output = { messages: [
      user(sid, 'u', 'engineer', [{ type: 'text', text: 'task' }]),
      assistant(sid, 'a', 'engineer', [{ type: 'text', text: 'Finished' }]),
      user(sid, 's', 'engineer', [steerPart]),
    ] }
    const before = runtime.abortedIds.length
    await hooks['experimental.chat.messages.transform']({}, output)
    assert.deepEqual(output.messages, [])
    assert.deepEqual(runtime.abortedIds.slice(before), [sid])
  })
})
