import assert from 'node:assert/strict'
import test from 'node:test'
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import * as routing from '../../../../dist/OpenCode/Host/ModelRoutingSurface.js'
import { withExecutablePlugin } from '../../../verification-system/tests/support/plugin-fixture.mjs'

for (const legacy of [
  'throw new Error("retired scheduler must never load")',
  'export default async () => ({ model: "backup/model", reasoning: "high" })',
  'export const routingProtocol = 1; export default 42',
]) {
  test(`WHAT[execution-model-routing-002] Host admission ignores retired configuration: ${legacy}`, async () => {
    let path
    await withExecutablePlugin(async hooks => {
      const model = { providerID: 'host', modelID: 'selected', variant: 'high' }
      const message = { id: 'legacy-input', sessionID: 'legacy-session', role: 'user', agent: 'engineer', model }
      await hooks['chat.message']({ sessionID: message.sessionID, messageID: message.id, agent: message.agent }, { message, parts: [] })
      assert.equal(message.model, model)
      assert.deepEqual(message.model, { providerID: 'host', modelID: 'selected', variant: 'high' })
      assert.equal(readFileSync(path, 'utf8'), legacy)
      const executions = routing.sharedCapacitySnapshot().executions.filter(value => value.sessionId === message.sessionID)
      assert.equal(executions.length, 1)
    }, {}, async input => {
      path = join(process.env.HOME, '.config', 'opencode', 'wanxiangshu.mjs')
      mkdirSync(dirname(path), { recursive: true })
      writeFileSync(path, legacy)
      const { default: plugin } = await import('wanxiangshu')
      return plugin.server(input)
    })
    assert.equal(existsSync(path), false, 'the owned temporary configuration is removed after disposal')
  })
}

test('WHAT[execution-model-routing-002] retrying the same physical input keeps its Host target and exact capacity', async () => {
  await withExecutablePlugin(async hooks => {
    const input = { sessionID: 'retry-target', messageID: 'retry-physical', agent: 'engineer' }
    const output = () => ({ message: { id: input.messageID, sessionID: input.sessionID, role: 'user', agent: input.agent,
      model: { providerID: 'host', modelID: 'selected', variant: 'high' } }, parts: [] })
    await hooks['chat.message'](input, output())
    const before = routing.sharedCapacitySnapshot()
    await hooks.event({ event: { type: 'session.status', properties: { sessionID: input.sessionID,
      status: { type: 'retry', attempt: 1, message: 'temporary provider error' } } } })
    const retry = output()
    await hooks['chat.message'](input, retry)
    assert.deepEqual(retry.message.model, { providerID: 'host', modelID: 'selected', variant: 'high' })
    assert.deepEqual(routing.sharedCapacitySnapshot(), { ...before,
      counters: { ...before.counters, duplicate: before.counters.duplicate + 1 } })
    const afterRetry = routing.sharedCapacitySnapshot()

    const changed = output()
    changed.message.model.modelID = 'backup'
    await assert.rejects(hooks['chat.message'](input, changed), /HostProjectionFailed.*OpenCode model does not match the execution lease/)
    assert.deepEqual(routing.sharedCapacitySnapshot(), { ...afterRetry,
      counters: { ...afterRetry.counters, conflict: afterRetry.counters.conflict + 1 } },
    'a rejected target change cannot replace or release the original lease')
  })
})

test('WHAT[execution-model-routing-002] missing Host selection cannot create a fallback lease', async () => {
  await withExecutablePlugin(async hooks => {
    const before = routing.sharedCapacitySnapshot()
    for (const model of [undefined, {}, { providerID: 'host' }, { modelID: 'selected' }]) {
      const input = { sessionID: 'missing-target', messageID: 'missing-physical', agent: 'engineer' }
      const message = { id: input.messageID, sessionID: input.sessionID, role: 'user', agent: input.agent, model }
      await assert.rejects(hooks['chat.message'](input, { message, parts: [] }), /did not provide the selected model/)
      assert.deepEqual(routing.sharedCapacitySnapshot(), before)
    }
  })
})
