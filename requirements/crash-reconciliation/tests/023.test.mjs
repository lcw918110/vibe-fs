import assert from 'node:assert/strict'
import test from 'node:test'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { setTimeout as sleep } from 'node:timers/promises'
import { withRestartablePlugin } from '../../verification-system/tests/support/plugin-fixture.mjs'
import { integrationTest } from '../../verification-system/tests/support/tier-gate.mjs'

// WHAT[crash-reconciliation-023]: the first admission (every chat.message hook)
// waits for the journal recovery barrier, which used to carry the whole
// load-phase recovery — the journal-only void settlement chained with every
// Host round-trip (delivery observation, the two process-wide reload sweeps
// that recover every session's unsettled executions). Any unrelated session's
// slow or hung Host answer therefore stalled the first admission of a fresh
// session, and a faulting recovery was swallowed into a slot only dispose ever
// surfaced. Now the barrier carries only the durable load settlement, its
// failure propagates honestly and a later caller retries it, while Host
// round-trip recovery is sliced per session on the recovery host: an admission
// joins its own session's sweep and unrelated sessions never block it.

const admitRoot = async (hooks, client, sessionID, physicalUserMessageId) => {
  const output = {
    message: {
      id: physicalUserMessageId,
      role: 'user',
      sessionID,
      agent: 'engineer',
      model: { providerID: 'host', modelID: 'placeholder' },
    },
    parts: [],
  }
  await hooks['chat.message']({ sessionID, agent: 'engineer' }, output)
  client.__pushHostMessage(sessionID, { info: output.message, parts: output.parts })
  return physicalUserMessageId
}

// Gate the SDK transcript reads of one session — the Host round-trip every
// load-phase reload sweep decides from. Parked reads are the honest "unrelated
// session's slow Host answer"; the pending count proves a sweep really visited
// (and survived) the gate.
const gateTranscripts = (client) => {
  if (client.session.messages.__gated) throw new Error('transcript gates already installed')
  const original = client.session.messages
  const gates = new Map()
  client.session.messages = async (args) => {
    const id = args?.path?.id ?? args?.sessionID ?? args?.sessionId
    const gate = gates.get(id)
    if (gate) {
      gate.pending += 1
      try {
        await gate.promise
      } finally {
        gate.pending -= 1
      }
    }
    return original(args)
  }
  client.session.messages.__gated = true
  return {
    gate: (sessionID) => {
      let release
      const promise = new Promise((resolveGate) => {
        release = resolveGate
      })
      gates.set(sessionID, { pending: 0, promise, release })
      return {
        pending: () => gates.get(sessionID).pending,
        release: () => release(),
      }
    },
  }
}

integrationTest('WHAT[crash-reconciliation-023] the first admission never queues behind unrelated sessions and its own leftovers settle first', async () => {
  const sessionB = 'ses-cr023-unrelated'
  const sessionA = 'ses-cr023-admitting'

  await withRestartablePlugin(async (start, _directory, { stop, client }) => {
    // Incarnation 1 leaves one unsettled execution per session through the real
    // PublishInput admission, exactly as a previous runtime would.
    const first = await start()
    await admitRoot(first, client, sessionB, `root-${sessionB}`)
    await admitRoot(first, client, sessionA, `root-${sessionA}`)
    await stop(first)

    // Incarnation 2 boots over the same journal: both sessions owe a load-phase
    // reload decision, and both Host transcript reads are gated.
    const second = await start()
    const gates = gateTranscripts(client)
    const gateA = gates.gate(sessionA)
    const gateB = gates.gate(sessionB)

    try {
      // This session's second physical input goes through the real hook. The
      // hook must not complete while this session's own reload sweep waits on
      // its Host read: the leftover decision precedes the identity judgment.
      const admission = second['chat.message'](
        { sessionID: sessionA, agent: 'engineer' },
        {
          message: {
            id: `root-${sessionA}-second`,
            role: 'user',
            sessionID: sessionA,
            agent: 'engineer',
            model: { providerID: 'host', modelID: 'placeholder' },
          },
          parts: [],
        },
      )

      await sleep(150)
      assert.equal(
        await Promise.race([admission.then(() => 'done'), sleep(1500).then(() => 'pending')]),
        'pending',
        'the admission must wait for its own session\'s load-phase reload sweep',
      )
      assert.ok(gateA.pending() > 0, 'the sweep is parked on this session\'s Host read')

      // Releasing only THIS session's read lets the admission finish while the
      // unrelated session's recovery is still parked — no cross-session queue.
      gateA.release()
      assert.equal(
        await Promise.race([admission.then(() => 'done'), sleep(4000).then(() => 'hung')]),
        'done',
        'the first admission must not queue behind the unrelated session\'s recovery',
      )
      assert.ok(gateB.pending() > 0, 'the unrelated session\'s slow recovery is still parked')

      // Recovery of the unrelated session is not dropped: releasing its read
      // lets its sweep settle too.
      gateB.release()
      await sleep(150)
      assert.equal(gateB.pending(), 0, 'the unrelated session\'s sweep eventually settles')
    } finally {
      gateA.release()
      gateB.release()
      await stop(second)
    }
  })
})

test('WHAT[crash-reconciliation-023] a faulting journal recovery propagates to its awaiters and a later caller retries it', async () => {
  const journalSurface = await import('../../../dist/Persistence/Journal/Surface.js')
  const agentJournal = await import('../../../dist/Persistence/Journal/AgentJournal.js')
  const directory = mkdtempSync(join(tmpdir(), 'wxs-journal-recovery-fault-'))
  let opened
  try {
    opened = await journalSurface.JournalSurface_bootWithWriterId(
      directory,
      'recovery-fault-writer-023',
      'recovery-fault-runtime-023',
      process.pid,
      new Date().toISOString(),
    )
    assert.equal(opened.ok, true, JSON.stringify(opened.error))
    const journal = journalSurface.JournalHandle__get_Journal(opened.journal)

    let calls = 0
    let fail = true
    agentJournal.AgentJournal__EnqueueRecovery_Z1C042147(journal, async () => {
      calls += 1
      if (fail) throw new Error('load settlement failed')
    })

    // Concurrent callers share the one run, and every awaiter sees the real
    // failure — never a silent success.
    const first = agentJournal.AgentJournal__RunOrAwaitRecovery(journal)
    const second = agentJournal.AgentJournal__RunOrAwaitRecovery(journal)
    await assert.rejects(() => first, /load settlement failed/)
    await assert.rejects(() => second, /load settlement failed/)
    assert.equal(calls, 1)

    // The faulted run is cleared: a later caller starts a fresh recovery, and
    // the idempotent settlement repairs exactly what the fault left unsettled.
    fail = false
    await agentJournal.AgentJournal__RunOrAwaitRecovery(journal)
    assert.equal(calls, 2)
    await agentJournal.AgentJournal__RunOrAwaitRecovery(journal)
    assert.equal(calls, 2, 'single-flight still holds after a retried recovery')

  } finally {
    if (opened !== undefined) journalSurface.JournalSurface_dispose(opened.journal)
    rmSync(directory, { recursive: true, force: true })
  }
})
