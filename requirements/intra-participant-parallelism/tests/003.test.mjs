import assert from 'node:assert/strict'
import test from 'node:test'
import { fission, harness, parsed } from './support/admission.mjs'
import { acceptAuthorityRoot, openIncumbency, withExecutablePlugin } from '../../verification-system/tests/support/plugin-fixture.mjs'
import * as executionStatus from '../../../dist/Execution/Session/ChatExecution/StatusSurface.js'

test('WHAT[intra-participant-parallelism-003] actual admission creates distinct siblings under the existing parent with the owner view and each exact input', async () => {
  const { events, runtime } = harness()
  const owner = 'sibling-owner'
  try {
    const result = await fission.admit(runtime, owner, parsed())
    assert.equal(result.ok, true)
    assert.deepEqual(result.lanes, [{ index: 0, prompt: ' lane A  ' }, { index: 1, prompt: 'lane B' }])
    assert.deepEqual(events.filter(([kind]) => kind === 'create').map(([, index, parent]) => [index, parent]), [[0, 'old-parent'], [1, 'old-parent']])
    const starts = events.filter(([kind]) => kind === 'start')
    assert.equal(starts.length, 2)
    assert.equal(new Set(starts.map(([, , session]) => session)).size, 2)
    assert.match(starts[0][3], /CANONICAL-OWNER-VIEW/)
    assert.match(starts[1][3], /CANONICAL-OWNER-VIEW/)
    assert.match(starts[0][3], /lane A  /)
    assert.match(starts[1][3], /lane B/)
    assert.equal(fission.isActive(runtime, owner), true)
  } finally {
    fission.release(runtime, owner)
  }
})

test('WHAT[intra-participant-parallelism-003] actual admission admits the lanes of an owner that has not delivered a view, and the lane startup carries only the lane input', async () => {
  const { events, runtime } = harness({ ownerView: null })
  const owner = 'undelivered-owner'
  try {
    const result = await fission.admit(runtime, owner, parsed())
    assert.equal(result.ok, true)
    assert.equal(result.ownerView, null)
    const starts = events.filter(([kind]) => kind === 'start')
    assert.equal(starts.length, 2)
    for (const [, , , startup] of starts) {
      assert.doesNotMatch(startup, /CANONICAL-OWNER-VIEW/)
    }
    assert.match(starts[0][3], /lane A  /)
    assert.match(starts[1][3], /lane B/)
    assert.equal(fission.isActive(runtime, owner), true)
  } finally {
    fission.release(runtime, owner)
  }
})

test('WHAT[intra-participant-parallelism-003] actual Host-created Fission lanes record ChatExecution.Accepted under each lane session and pass provider attempt plan freeze', async () => {
  await withExecutablePlugin(async (hooks, _directory, createdIds, runtime) => {
    const managerSession = 'ses-fission-manager-parent'
    await acceptAuthorityRoot(runtime, managerSession, 'manager')
    await openIncumbency(runtime, managerSession)

    const forked = await hooks.tool.fork.execute(
      { name: 'Ada', charge: 'investigate seven items' },
      { sessionID: managerSession, agent: 'manager' },
    )
    assert.match(forked, /现已接下这项托付|carries this charge now/i)
    assert.equal(createdIds.length, 1)
    const engineerSession = createdIds[0]

    const fissioned = await hooks.tool.fission.execute(
      { prompts: ['lane alpha task', 'lane beta task'] },
      { sessionID: engineerSession, agent: 'engineer', callID: 'call-fission-lanes', messageID: 'run-engineer-1' },
    )
    assert.match(fissioned, /status\s*=\s*"fissioned"/i)
    assert.match(fissioned, /lane_count\s*=\s*2/i)
    assert.equal(createdIds.length, 3)

    const laneSessions = createdIds.slice(1)
    for (const [idx, laneSession] of laneSessions.entries()) {
      const laneUserMessages = runtime.messages.filter(m => m.id?.startsWith(`msg-${laneSession}-`) && m.role === 'user')
      assert.equal(laneUserMessages.length, 1, `lane ${idx} must have one admitted physical user message`)
      const physicalId = laneUserMessages[0].id

      const assistantMsg = {
        info: {
          id: `assistant-${laneSession}`,
          sessionID: laneSession,
          parentID: physicalId,
          role: 'assistant',
          agent: 'engineer',
          providerID: 'provider',
          modelID: 'engineer-model',
          time: { created: 2 },
        },
        parts: [],
      }
      runtime.pushHostMessage(laneSession, assistantMsg)

      const transformOutput = {
        messages: [
          {
            info: {
              id: physicalId,
              sessionID: laneSession,
              role: 'user',
              agent: 'engineer',
              time: { created: 1 },
            },
            parts: laneUserMessages[0].parts,
          },
        ],
      }

      await assert.doesNotReject(
        () => hooks['experimental.chat.messages.transform']({ sessionID: laneSession }, transformOutput),
        `lane ${idx} (${laneSession}) transform must not fail with AcceptedExecutionMissing`,
      )

      const execution = executionStatus.query(runtime.journal, laneSession, physicalId)
      assert.deepEqual(
        execution,
        { accepted: true, providerStarted: true, terminal: false, disposition: null },
        `lane ${idx} must hold its own accepted execution under its own session id`,
      )
    }
  })
})

test.todo('WHAT[intra-participant-parallelism-003] actual Host-created siblings bind the original participant identity and receive its current canonical view (GAP-158)')
