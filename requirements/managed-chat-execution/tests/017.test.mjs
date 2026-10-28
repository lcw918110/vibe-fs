import test from 'node:test'
import assert from 'node:assert/strict'
import * as recovery from '../../../dist/OpenCode/Host/SessionRecoveryHostSurface.js'
import * as routing from '../../../dist/OpenCode/Host/ModelRoutingSurface.js'
import * as eventsSurface from '../../../dist/OpenCode/Host/EventsSurface.js'
import { withExecutablePlugin } from '../../verification-system/tests/support/plugin-fixture.mjs'

// Field incident 2026-10-28 11:42 UTC (DevOps ses_e7eba7c32ffe…, child of Manager
// ses_e7ed7cdb2ffe…): the Host compacted the context mid-run (msg_1825197d8 compaction,
// summary msg_1825197fc) and wrote its own "Continue if you have next steps" row
// msg_18251cf3b straight into storage — part `synthetic: true`,
// `metadata.compaction_continue: true`, no chat.message. The next transform died with
// `HOST-BOUNDARY-008: provider attempt plan freeze failed (accepted-execution-missing)`,
// the child loop stopped, and the Manager join waited on a run nobody would finish.

const model = { providerID: 'provider', modelID: 'engineer-model' }
const userInfo = (sessionID, id) => ({ id, sessionID, role: 'user', agent: 'engineer', model })
const continueRow = (sessionID, id) => ({
  info: userInfo(sessionID, id),
  parts: [{ type: 'text', synthetic: true, metadata: { compaction_continue: true },
    text: 'Continue if you have next steps, or stop and ask for clarification if you are unsure how to proceed.' }],
})
const compactionRows = (sessionID) => [
  { info: { id: 'msg-compaction', sessionID, role: 'user', agent: 'engineer', model }, parts: [{ type: 'compaction', auto: true }] },
  { info: { id: 'assistant-summary', sessionID, parentID: 'msg-compaction', role: 'assistant', agent: 'compaction',
      mode: 'compaction', summary: true, finish: 'stop', time: { created: 3, completed: 4 } },
    parts: [{ type: 'text', text: 'Summary of the work so far.' }] },
]
const hostAssistant = (sessionID, id, parentID, created) => ({
  info: { id, sessionID, parentID, role: 'assistant', agent: 'engineer', providerID: model.providerID,
    modelID: model.modelID, time: { created } },
  parts: [],
})

const startRoot = async (hooks, runtime, sessionID) => {
  const root = userInfo(sessionID, 'msg-root')
  await hooks['chat.message']({ sessionID, messageID: root.id, agent: 'engineer', model },
    { message: root, parts: [{ type: 'text', text: 'assignment' }] })
  runtime.pushHostMessage(sessionID, hostAssistant(sessionID, 'assistant-root', root.id, 1))
  await hooks['experimental.chat.messages.transform']({}, {
    messages: [{ info: root, parts: [{ type: 'text', text: 'assignment' }] }],
  })
  assert.deepEqual(recovery.journalExecutionStatus(runtime.journal, sessionID, root.id),
    { phase: 'ProviderStarted', disposition: null })
  return root
}

test('WHAT[managed-chat-execution-017] the Host post-compaction continuation continues the compacted execution', async () => {
  await withExecutablePlugin(async (hooks, _directory, _created, runtime) => {
    const sessionID = 'ses-compaction-continue-017'
    const root = await startRoot(hooks, runtime, sessionID)
    const cont = continueRow(sessionID, 'msg-continue')
    runtime.pushHostMessage(sessionID, hostAssistant(sessionID, 'assistant-continue', cont.info.id, 5))
    const seen = []
    const subscription = eventsSurface.subscribe(await runtime.terminalPort, (sid, outcome) => seen.push([sid, outcome]))
    try {
      await hooks['experimental.chat.messages.transform']({}, {
        messages: [{ info: root, parts: [{ type: 'text', text: 'assignment' }] }, ...compactionRows(sessionID), cont],
      })
    } finally {
      eventsSurface.dispose(subscription)
    }
    assert.deepEqual(seen.filter(([sid]) => sid === sessionID), [],
      'handing the run to the continuation publishes no terminal the parent join would take for the run ending')
    assert.deepEqual(recovery.journalExecutionStatus(runtime.journal, sessionID, root.id),
      { phase: 'Terminal', disposition: 'Cancelled' }, 'the compacted step is superseded, as for any same-run continuation')

    assert.deepEqual(recovery.journalExecutionStatus(runtime.journal, sessionID, cont.info.id),
      { phase: 'ProviderStarted', disposition: null }, 'the continuation runs as a managed execution')
    const owned = routing.sharedCapacitySnapshot().tokens.filter(token => token.owner.sessionId === sessionID)
    assert.deepEqual(owned.map(token => token.owner.physicalUserMessageId), [cont.info.id],
      'the compacted execution hands its one lease to the continuation; no second capacity is taken')
    assert.deepEqual(runtime.abortedIds, [])
  })
})

test('WHAT[managed-chat-execution-017] a continuation with nothing live to inherit tells the waiting parent the run failed', async () => {
  await withExecutablePlugin(async (hooks, _directory, _created, runtime) => {
    const sessionID = 'ses-compaction-orphan-017'
    const root = await startRoot(hooks, runtime, sessionID)
    routing.releasePhysical(sessionID, root.id)
    const seen = []
    const subscription = eventsSurface.subscribe(await runtime.terminalPort, (sid, outcome) => seen.push([sid, outcome]))
    try {
      const cont = continueRow(sessionID, 'msg-continue-orphan')
      runtime.pushHostMessage(sessionID, hostAssistant(sessionID, 'assistant-orphan', cont.info.id, 5))
      await assert.rejects(hooks['experimental.chat.messages.transform']({}, {
        messages: [{ info: root, parts: [{ type: 'text', text: 'assignment' }] }, ...compactionRows(sessionID), cont],
      }), /accepted-execution-missing/, 'no execution is forged for the row')
      const failed = seen.filter(([sid, outcome]) => sid === sessionID && outcome.kind === 'Failed')
      assert.equal(failed.length, 1, `the parent hears one Failed terminal, saw ${JSON.stringify(seen)}`)
      assert.match(JSON.stringify(failed[0][1]), /compaction continuation/)
    } finally {
      eventsSurface.dispose(subscription)
    }
  })
})

test('WHAT[managed-chat-execution-017] an ordinary unadmitted row is not taken for a compaction continuation', async () => {
  await withExecutablePlugin(async (hooks, _directory, _created, runtime) => {
    const sessionID = 'ses-not-continuation-017'
    const root = await startRoot(hooks, runtime, sessionID)
    const stray = { info: userInfo(sessionID, 'msg-stray'), parts: [{ type: 'text', text: 'not admitted' }] }
    runtime.pushHostMessage(sessionID, hostAssistant(sessionID, 'assistant-stray', stray.info.id, 5))
    await assert.rejects(hooks['experimental.chat.messages.transform']({}, {
      messages: [{ info: root, parts: [{ type: 'text', text: 'assignment' }] }, stray],
    }), /accepted-execution-missing/)
    assert.equal(recovery.journalExecutionStatus(runtime.journal, sessionID, stray.info.id)?.phase ?? null, null)
  })
})
