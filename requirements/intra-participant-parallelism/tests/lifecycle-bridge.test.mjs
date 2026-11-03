import assert from 'node:assert/strict'
import test from 'node:test'
import { readdirSync, readFileSync } from 'node:fs'
import { join } from 'node:path'
import { acceptAuthorityRoot, openIncumbency, withExecutablePlugin } from '../../verification-system/tests/support/plugin-fixture.mjs'
import * as events from '../../../dist/OpenCode/Host/EventsSurface.js'
import * as runtimeSurface from '../../../dist/Repository/Programming/Js/RuntimeSurface.js'

const factsAt = directory => readdirSync(join(directory, '.Herdchestra', 'wanxiangshu', 'events'))
  .filter(name => name.endsWith('.ndjson'))
  .flatMap(name => readFileSync(join(directory, '.Herdchestra', 'wanxiangshu', 'events', name), 'utf8')
    .split('\n').filter(Boolean).map(line => JSON.parse(line).payload?.Fact))
  .filter(fact => fact?.[0] === 'Agent' && fact?.[1]?.[0] === 'Fission')
  .map(fact => fact[1][1])

const waitFor = async (read, description) => {
  const end = Date.now() + 5000
  while (Date.now() < end) {
    const value = read()
    if (value) return value
    await new Promise(resolve => setTimeout(resolve, 5))
  }
  assert.fail(description)
}

const finishLane = async (hooks, runtime, session, run, answer) => {
  await runtime.awaitIngress()
  const physical = runtime.messages.filter(message => message.role === 'user' && message.id?.startsWith(`msg-${session}-`)).at(-1)
  assert.ok(physical, `physical prompt for ${session}`)
  const created = 100 + runtime.messages.length * 10
  const assistant = { info: {
    id: run, sessionID: session, parentID: physical.id, role: 'assistant', agent: 'engineer',
    providerID: 'fixture', modelID: 'engineer-model', time: { created },
  }, parts: [{ id: `text-${run}`, type: 'text', text: answer }] }
  runtime.pushHostMessage(session, assistant)
  await hooks['experimental.chat.messages.transform']({ sessionID: session }, { messages: [{
    info: { ...physical, sessionID: session, role: 'user', agent: 'engineer', time: { created: 1 } },
    parts: physical.parts,
  }] })
  // Seed the existing delivery input; an ordinary program return is not a delivery.
  runtimeSurface.recordFencedDeliveredView(session, run, `call-${run}`, [answer])
  assistant.info.time.completed = created + 1
  assistant.info.finish = 'stop'
  await hooks.event({ event: { type: 'message.updated', properties: { info: assistant.info } } })
  await hooks.event({ event: { type: 'session.idle', properties: { sessionID: session } } })
}

test('actual Fission lanes materialize and dispatch takeover, then reject owner completion without durable delivery', async () => {
  await withExecutablePlugin(async (hooks, directory, createdIds, runtime) => {
    const manager = 'fission-lifecycle-manager'
    await acceptAuthorityRoot(runtime, manager, 'manager')
    await openIncumbency(runtime, manager)
    const forked = await hooks.tool['todowrite-for-manager'].execute({
      name: 'fission-lifecycle', intent: 'Collect both lane results and return their combined answer.',
      program: 'class Js extends JsProgram { async run() { return await this.fork({ name: "research", charge: "combine both findings" }); } }',
    }, { sessionID: manager, agent: 'manager' })
    assert.match(forked, /outcome = "created"/)
    const owner = createdIds[0]
    const terminals = []
    const subscription = events.subscribeFuture(runtime.terminalPort, (session, outcome) => {
      if (session === owner) terminals.push(outcome)
    })
    try {
      const admitted = await hooks.tool.fission.execute({ prompts: ['first finding', 'second finding'] }, {
        sessionID: owner, agent: 'engineer', callID: 'fission-lifecycle-call', messageID: 'fission-owner-run',
      })
      assert.match(admitted, /status\s*=\s*"fissioned"/)
      const [first, last] = createdIds.slice(1)
      assert.ok(first && last)
      await finishLane(hooks, runtime, first, 'fission-first-run', 'FIRST-FINDING')
      await waitFor(() => factsAt(directory).some(fact => fact[0] === 'FissionLaneMaterialized'), 'first lane materialization')
      assert.equal(terminals.length, 0)
      await finishLane(hooks, runtime, last, 'fission-last-run', 'SECOND-FINDING')
      await waitFor(() => factsAt(directory).some(fact => fact[0] === 'FissionTakeoverClaimed'), 'takeover claim after both lane materials')
      const laneFacts = factsAt(directory).filter(fact => fact[0] === 'FissionLaneMaterialized')
      assert.deepEqual([...new Set(laneFacts.map(fact => fact[1].LaneIndex))].sort(), [0, 1])
      const takeover = runtime.prompts.filter(prompt => prompt.path?.id === last).at(-1)
      const text = takeover.body.parts.filter(part => part.type === 'text').map(part => part.text).join('\n')
      assert.match(text, /FIRST-FINDING/)
      assert.match(text, /SECOND-FINDING/)
      assert.equal(terminals.length, 0)
      await finishLane(hooks, runtime, last, 'fission-integrated-run', 'COMBINED-FINDINGS')
      await waitFor(() => terminals.length > 0, 'logical owner terminal')
      assert.equal(terminals.length, 1)
      assert.equal(terminals[0].kind, 'Failed')
      assert.match(terminals[0].text, /without a delivery witness/)
      await waitFor(() => factsAt(directory).some(fact => fact[0] === 'FissionFailed'), 'durable failed group')
      assert.equal(factsAt(directory).filter(fact => fact[0] === 'FissionConverged').length, 0)
    } finally {
      events.dispose(subscription)
    }
  })
})
