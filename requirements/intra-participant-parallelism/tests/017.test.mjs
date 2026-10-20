import assert from 'node:assert/strict'
import test from 'node:test'
import { configure, installDefaultResources } from '../../../dist/OpenCode/Host/ManagedAgentConfigSurface.js'
import { acceptAuthorityRoot, withExecutablePlugin } from '../../verification-system/tests/support/plugin-fixture.mjs'

installDefaultResources()

const configuration = () => {
  const config = { agent: {} }
  for (const name of ['manager', 'orchestrator', 'engineer', 'devops', 'bookkeeper']) {
    config.agent[name] = { model: `${name}-model` }
  }
  assert.equal(configure(config).ok, true)
  return config
}

test('WHAT[intra-participant-parallelism-017] generated Host configuration grants Engineer fission and denies other active offices and Bookkeeper', () => {
  const config = configuration()
  assert.equal(config.agent.engineer.permission.fission, 'allow')
  for (const name of ['manager', 'orchestrator', 'devops', 'bookkeeper']) {
    assert.equal(config.agent[name].permission.fission, 'deny', name)
  }
})

test('WHAT[intra-participant-parallelism-017] actual tool rejects Manager and DevOps, including a forged Engineer claim, without effects', async () => {
  await withExecutablePlugin(async (hooks, _directory, createdIds, runtime) => {
    for (const role of ['manager', 'devops']) {
      const sessionID = `ses-${role}-fission`
      await acceptAuthorityRoot(runtime, sessionID, role)
      for (const claim of [role, 'engineer']) {
        const result = await hooks.tool.fission.execute(
          { prompts: ['lane 1', 'lane 2'], role: 'Engineer' },
          { sessionID, agent: claim, callID: `fission-${role}-${claim}`, messageID: `run-${role}` },
        )
        assert.match(result, /only available to Engineer|仅 Engineer 允许|denied/i)
      }
    }
    assert.deepEqual(createdIds, [])
    assert.deepEqual(runtime.prompts, [])
    assert.deepEqual(runtime.abortedIds, [])
  })
})

test.todo('WHAT[intra-participant-parallelism-017] GAP-158: actual full admission formula, every Manager lifecycle state, standard Engineer eligibility within Sphinx and historical Manager Fission replay')
