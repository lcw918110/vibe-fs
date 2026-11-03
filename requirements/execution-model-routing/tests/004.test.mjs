import assert from 'node:assert/strict'
import test from 'node:test'
import * as routing from '../../../dist/OpenCode/Host/ModelRoutingSurface.js'

test('WHAT[execution-model-routing-004] unaccepted speculative intent must not preoccupy capacity', { todo: 'GAP-130: optional reservation currently occupies a token before chat.message; 34-D2 needs decision' }, () => {
  const runtime = routing.createRuntime(() => ({ model: 'provider/replica', reasoning: 'none' }))
  try {
    routing.tryReserveManaged(runtime, 'unaccepted', 'engineer', null)
    assert.deepEqual(routing.snapshotOccupied(runtime), [])
  } finally {
    routing.releaseExecution(runtime, 'unaccepted')
  }
})

const { createSdkClientPort, sendPrompt } = routing
const promptOptions = (overrides = {}) => ({
  model: undefined,
  agent: undefined,
  directory: undefined,
  metadata: undefined,
  tools: undefined,
  ...overrides,
})

test('WHAT[execution-model-routing-004] EMR_004_sdk_prompt_async_awaits_host_enqueue_and_surfaces_enqueue_rejection', async () => {
  let releaseHost
  let rejectHost
  let invoked = false
  const hostRun = new Promise((resolve) => {
    releaseHost = resolve
  })
  const failingRun = new Promise((_, reject) => { rejectHost = reject })
  const client = {
    session: {
      promptAsync: () => {
        invoked = true
        return hostRun
      },
    },
  }
  const port = createSdkClientPort(client)

  const sending = sendPrompt(
    port,
    'session-detached',
    'start child work',
    promptOptions({ agent: 'devops' }),
  )
  releaseHost({})
  const result = await sending
  assert.equal(invoked, true, 'the Host enqueue API is invoked')
  assert.match(JSON.stringify(result), /AdmittedWithReceipt/i)

  const failingPort = createSdkClientPort({ session: { promptAsync: () => failingRun } })
  rejectHost(new Error('Host enqueue refused'))
  const failed = await sendPrompt(failingPort, 'session-failing', 'refused work', promptOptions({ agent: 'devops' }))
  assert.match(JSON.stringify(failed), /Fatal|refused/i)
})
