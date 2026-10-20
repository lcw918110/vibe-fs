import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { mkdtemp, mkdir, rm, writeFile } = await import("node:fs/promises");
const { tmpdir } = await import("node:os");
const { join } = await import("node:path");
const { default: test, after } = await import("node:test");
const routing = await import("../../../dist/OpenCode/Host/ModelRoutingSurface.js");
const { runListenerRefcountScenario } = await import("./support/listener-refcount.mjs");

const originalHome = process.env.HOME
const originalUserProfile = process.env.USERPROFILE
const home = await mkdtemp(join(tmpdir(), 'wanxiangshu-host-binding-home-'))
process.env.HOME = home
process.env.USERPROFILE = home
await mkdir(join(home, '.config', 'opencode'), { recursive: true })
await writeFile(
  join(home, '.config', 'opencode', 'wanxiangshu.mjs'),
  `
export const routingProtocol = 2
export default function route(role, running, previous, purpose) {
  return { model: 'test/system', reasoning: 'none' }
}
`,
  'utf8',
)
await routing.initialize()
after(async () => {
  if (originalHome === undefined) delete process.env.HOME
  else process.env.HOME = originalHome
  if (originalUserProfile === undefined) delete process.env.USERPROFILE
  else process.env.USERPROFILE = originalUserProfile
  await rm(home, { recursive: true, force: true })
})
const model = { providerID: 'openai', modelID: 'gpt-5' }
const modelFromLease = async (sessionId, physicalUserMessageId, role, participant, lenderSessionId) => {
  const outcome = await routing.acquireSharedExecutionAdmission(
    sessionId,
    physicalUserMessageId,
    role,
    participant,
    lenderSessionId,
  )
  assert.equal(outcome.kind, 'Acquired')
  const target = routing.sharedExecutionAdmissionTarget(outcome.lease)
  const settlement = routing.commitSharedExecutionAdmission(outcome.lease, {
    sessionId,
    physicalUserMessageId,
    role,
    participant,
    target,
  })
  assert.ok(['Applied', 'AlreadyApplied'].includes(settlement.kind))
  const [providerID, ...modelParts] = target.model.split('/')
  return { providerID, modelID: modelParts.join('/'), variant: target.reasoning }
}

test('WHAT[host-boundary-006] HOST-006_params_observation_is_read_only_and_does_not_reject_agent_drift', async () => {
  const params = await import('../../../dist/OpenCode/Host/ChatParamsSurface.js')
  const session = 'ses_hb006_drift'
  const physical = 'msg_hb006_drift'
  await modelFromLease(session, physical, 'engineer', 'engineer', undefined)

  // The Host observes a different agent than the lease participant. Runtime
  // observation is read-only: drift is a test-time fast-check property, so
  // this observation passes and only projects temperature.
  const output = {}
  const observed = params.apply(
    {
      sessionID: session,
      messageID: physical,
      agent: 'devops',
      model: { providerID: 'test', id: 'system', capabilities: {} },
      message: { id: physical, model: {} },
    },
    output,
  )

  assert.equal(observed.ok, true, observed.error)
  assert.equal(observed.temperature, 1)
  assert.equal(output.temperature, 1)
})
test('WHAT[host-boundary-006] HOST-006_params_observation_leaves_a_message_without_an_exact_lease_to_the_host', async () => {
  const params = await import('../../../dist/OpenCode/Host/ChatParamsSurface.js')

  // No durable Accepted execution and no committed lease exists for this exact
  // key: the observation barrier owns nothing here and must not invent an error.
  const observed = params.apply(
    {
      sessionID: 'ses_hb006_unmanaged',
      messageID: 'msg_hb006_unmanaged',
      agent: 'engineer',
      model: { providerID: 'test', id: 'system', capabilities: {} },
      message: { id: 'msg_hb006_unmanaged', model: {} },
    },
    {},
  )

  assert.equal(observed.ok, true, observed.error)
})
test('WHAT[host-boundary-006] HOST-006_terminal_listener_refcounts_do_not_share_disposal', () => {
  const observed = runListenerRefcountScenario()
  assert.equal(observed.afterOneDisposeFatal, true)
  assert.equal(observed.afterAllDisposeFatal, false)
  assert.deepEqual(observed.sends, [])
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const SessionSnapshotSurface = await import("../../../dist/OpenCode/Host/SessionSnapshotSurface.js");

const projectMessages = SessionSnapshotSurface.projectMessages
const locateToolCall = SessionSnapshotSurface.locateToolCall
const toolPartStateAt = SessionSnapshotSurface.toolPartStateAt
const assistantToolMessage = ({ messageID = 'asst_run', partID = 'part_todo', callID = 'call_todo', status = 'pending' } = {}) => ({
  info: { id: messageID, role: 'assistant' },
  parts: [{ type: 'tool', id: partID, callID, tool: 'auto-injected', state: { status } }],
})

test('WHAT[host-boundary-006] HOST-004 keeps failed session tool state consistent across Parts and ToolParts', () => {
  const messages = projectMessages([assistantToolMessage({ status: 'error' })])
  const part = toolPartStateAt(messages, 0, 0)
  assert.equal(part.ok, true)
  assert.equal(part.state, 'failed')
})
}
