import assert from 'node:assert/strict'
import test from 'node:test'
import { scanEntries, scanRepo, scanText } from '../../../scripts/checks/provider-leak-gate.mjs'
import * as fission from '../../../dist/Execution/Fission/Surface.js'

const clean = 'let render label = sprintf "# %s is still away." label'
const leaky = 'let render = field "pty_id" (str payload.PtyId)\nSessionId.value sid'

test('WHAT[participant-horizon-002] current source leak scanner distinguishes its clean and machine-identity fixtures', () => {
  assert.deepEqual(scanText('HorizonTool.fs', clean), [])
  const hits = scanEntries([
    { file: 'HorizonTool.fs', text: clean },
    { file: 'JoinResultRenderer.fs', text: leaky },
  ])
  assert.ok(hits.some((hit) => hit.id.startsWith('token:SessionId') || hit.id === 'token:pty_id'))
})

test('WHAT[participant-horizon-002] existing repository source scan has no reported machine leaks in its selected scope', () => {
  const result = scanRepo(process.cwd())
  assert.equal(result.ok, true, JSON.stringify(result.violations))
  assert.deepEqual(result.counts, {})
})

test('WHAT[participant-horizon-002] fission lane startup carries no internal lane index', async () => {
  const startups = []
  const runtime = fission.createAdmission({
    parentOf: async () => 'fission-parent',
    ownerView: async () => 'CANONICAL-OWNER-VIEW',
    createLane: async (_owner, _parent, lane) => `lane-${lane.index}`,
    startLane: async (_session, startup) => {
      startups.push(startup)
    },
    abortLane: async () => {},
    silentInterruptOwner: async () => {},
  })
  const result = await fission.admit(runtime, 'fission-owner', fission.parsePrompt(['lane A', 'lane B']))
  assert.equal(result.ok, true)
  assert.equal(startups.length, 2)
  for (const startup of startups) {
    assert.doesNotMatch(startup, /lane_index/)
  }
})

test.todo('WHAT[participant-horizon-002] all actual Provider requests and tool output hide machine topology; source-token scanning is neither full coverage nor semantic classification (GAP-079)')
