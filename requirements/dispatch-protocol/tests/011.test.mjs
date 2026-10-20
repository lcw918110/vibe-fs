import assert from 'node:assert/strict'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import test from 'node:test'
import * as authority from '../../../dist/Interaction/Authority/RuntimeSurface.js'
import * as dispatch from '../../../dist/Interaction/Dispatch/DispatchSurface.js'
import * as journal from '../../../dist/Persistence/Journal/Surface.js'

const hash = (value) => `H(${value})`

const capturingPort = () => ({
  SubscribeTerminal: () => ({ Dispose: () => {} }),
  SendPrompt: async () => dispatch.admittedWithReceipt('accepted-006'),
})

const personas = {
  engineer: 'Engineer',
  coder: 'Coder',
  manager: 'Lead',
}

const rootSelection = (participant) => {
  return {
    kind: 'RootSelection',
    ownerSession: null,
    ownerLogicalRun: null,
    ownerAuthorityRoot: null,
    participantIdentity: {
      participant,
      role: participant,
      selectedTier: 'deep',
      persona: personas[participant] ?? 'Unknown',
      personaCatalogVersion: 1,
      origin: 'ResolvedAtRoot',
    },
  }
}

const profileFor = (runtime = 'rt-send', session = 'ses_006', physical = 'msg_u1', participant = 'engineer') => {
  const built = authority.createAuthorityRoot(hash, runtime, session, 'HumanRoot', physical, rootSelection(participant))
  assert.equal(built.ok, true, built.ok ? '' : built.error)
  return built.value
}

const acceptOwner = async (handle, session = 'ses_owner') => {
  const accepted = await dispatch.acceptHumanRootSelection(
    handle,
    session,
    `msg-${session}`,
    rootSelection('manager'),
  )
  assert.equal(accepted.ok, true, accepted.ok ? '' : accepted.error)
  return accepted.profile
}

const observation = (result) => {
  assert.equal(result.ok, true, result.ok ? '' : result.error)
  assert.ok(result.observation)
  return result.observation
}

test('WHAT[dispatch-protocol-011] PROMPT_006_send_payload_carries_prompt_key_metadata', async () => {
  const base = mkdtempSync(join(tmpdir(), 'wxs-send-meta-'))
  try {
    const opened = await journal.JournalSurface_bootWithWriterId(base, 'writer-send-meta', 'rt-send-meta', 4242, '2026-01-01T00:00:00Z')
    assert.equal(opened.ok, true, opened.ok ? '' : JSON.stringify(opened.error))
    try {
      const owner = await acceptOwner(opened.journal, 'ses_006m_owner')
      const seed = authority.issueInheritedIdentitySeed('engineer', owner).value
      const ownerRoot = await dispatch.sendAgentOwnerRoot(
        capturingPort(),
        opened.journal,
        'ses_006m',
        'dispatch this',
        seed,
      )
      assert.equal(ownerRoot.ok, true, ownerRoot.error)
      const accepted = await dispatch.acceptAgentOwnerRoot(opened.journal, 'ses_006m', ownerRoot.key, 'msg-actual-metadata-root')
      assert.equal(accepted.ok, true, accepted.error)
      const continuation = await dispatch.sendContinuation(
        capturingPort(),
        opened.journal,
        'ses_006m',
        'retry the fixed participant',
        'ProviderRetryAttempt',
        accepted.profile,
        'Await',
      )

      assert.deepEqual(observation(ownerRoot).metadata, {
        wanxiangshu_prompt_key: ownerRoot.key,
        wanxiangshu_origin: 'AgentOwnerRoot',
        wanxiangshu_logical_run: null,
      })
      assert.deepEqual(observation(continuation).metadata, {
        wanxiangshu_prompt_key: continuation.key,
        wanxiangshu_origin: 'ProviderRetryAttempt',
        wanxiangshu_logical_run: accepted.profile.logicalRun,
      })
    } finally {
      journal.JournalSurface_dispose(opened.journal)
    }
  } finally {
    rmSync(base, { recursive: true, force: true })
  }
})
