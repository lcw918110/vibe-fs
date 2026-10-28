import assert from 'node:assert/strict'
import { existsSync, readdirSync } from 'node:fs'
import { join } from 'node:path'
import test from 'node:test'
import { isAllowed } from '../../../dist/Participant/Persona/OfficeCapabilitySurface.js'
import * as completedTurn from '../../../dist/Interaction/Repair/CompletedTurnSurface.js'
import * as dispatch from '../../../dist/Interaction/Dispatch/DispatchSurface.js'
import * as eventStore from '../../../dist/Persistence/EventStore/Surface.js'
import { acceptAuthorityRoot, withExecutablePlugin } from '../../verification-system/tests/support/plugin-fixture.mjs'

// 交付见证的观察口：已注册的 EventStore Surface 把 durable 事实读成普通 JSON
// （payload.Fact = ["Agent", [family, [case, payload]]]），不构造任何 F# union。
// 交付见证就是 Host 家族里那条 TaskDeliveryCommitted 事实。
const deliveryWitnesses = (commonDir) => {
  const eventsDir = join(commonDir, 'wanxiangshu', 'events')
  if (!existsSync(eventsDir)) return []
  const all = new Map()
  for (const file of readdirSync(eventsDir)) {
    const store = eventStore.create(commonDir, file.split('-')[0])
    for (const stream of eventStore.streams(store)) {
      let id = eventStore.head(store, stream)
      while (id) {
        const event = eventStore.read(store, id)
        if (!event) break
        all.set(id, event)
        id = event.parents?.[0]
      }
    }
    eventStore.dispose(store)
  }
  const witnesses = []
  for (const event of all.values()) {
    const fact = event?.payload?.Fact
    if (fact?.[1]?.[0] === 'Host' && fact[1][1][0] === 'TaskDeliveryCommitted') witnesses.push(fact)
  }
  return witnesses
}

// WHAT[work-032] keeps unrelated offices unchanged. Manager shares human Ask;
// it must not inherit Plan's DevOps consultation or draft capabilities. Two
// observable faces are asserted here: non-Plan roles never receive the
// Plan-only permission projection, and non-Plan empty stops keep requiring
// interaction repair (the full classification suite lives in 027.test.mjs
// under work-027). Manager's review tool is reached through the
// ReviewAssessment permission and the shared shell of work-038; the
// Plan-backend Review label itself never appears in a non-Plan projection.
// The Baton isolation trigger is gated by ownerRole = Plan inside the
// transform pipeline (PluginTransforms planSessionOf); its live proof
// belongs to the physical host canary tracked by GAP-230, not to this pure
// surface.

test('WHAT[work-032] non-Plan roles never receive the Plan-only tool projection', () => {
  const planOnlyPermissions = ['JsPlan', 'DevOpsConsultation', 'Deliver', 'Review']
  const nonPlanRoles = ['manager', 'engineer', 'devops', 'orchestrator']
  // Each role label must resolve to a real role, so the deny below is a
  // genuine projection fact and not an unknown-role fallback.
  const roleWitness = {
    manager: 'Fork',
    engineer: 'Write',
    devops: 'Exec',
    orchestrator: 'Fork',
  }
  for (const role of nonPlanRoles) {
    assert.equal(isAllowed(role, roleWitness[role]), true, `${role} must resolve as a real role`)
    for (const perm of planOnlyPermissions) {
      assert.equal(isAllowed(role, perm), false, `${role} must be denied ${perm}`)
    }
  }
})

test('WHAT[work-032] non-Plan empty stops keep requiring interaction repair', () => {
  // There is no Plan empty-stop exemption to propagate to another role.
  assert.equal(completedTurn.needsInteractionRepair('engineer', true, 'stop', []), true)
  assert.equal(completedTurn.needsInteractionRepair('manager', true, 'stop', []), true)
})

test('WHAT[work-032] only Plan may ask the human; Manager and other roles are denied ask', () => {
  assert.equal(isAllowed('plan', 'Ask'), true)
  for (const role of ['manager', 'engineer', 'devops', 'orchestrator', 'coder', 'inspector']) {
    assert.equal(isAllowed(role, 'Ask'), false, role)
    assert.equal(isAllowed(role, 'DevOpsConsultation'), false, role)
  }
})

test('WHAT[work-032] Engineer and DevOps natural stop completes cleanly without requiring a delivery witness', async () => {
  for (const name of ['engineer', 'devops']) {
    await withExecutablePlugin(async (hooks, directory, created, runtime) => {
      const session = `w032-${name}-natural`
      const physical = `phys-${session}`
      await acceptAuthorityRoot(runtime, session, name, physical)

      const user = {
        info: {
          id: physical,
          role: 'user',
          sessionID: session,
          agent: name,
          model: { providerID: 'provider', modelID: `${name}-model` },
          time: { created: 1 },
        },
        parts: [{ type: 'text', text: 'Do the work.' }],
      }
      runtime.pushHostMessage(session, user)
      await hooks['chat.message'](
        { sessionID: session, messageID: physical, agent: name },
        { message: user.info, parts: user.parts },
      )

      const run = `run-${session}`
      runtime.pushHostMessage(session, {
        info: {
          id: run,
          role: 'assistant',
          sessionID: session,
          parentID: physical,
          agent: name,
          providerID: 'provider',
          modelID: `${name}-model`,
          time: { created: 2 },
        },
        parts: [{ type: 'text', text: `${name} progress report: implementation stepped forward.` }],
      })

      const program = 'class Js extends JsProgram { async run() { return 1; } }'
      const executed = await hooks.tool[`todowrite-for-${name}`].execute(
        { name: `program-${run}`, program, intent: 'Observe this program result without treating it as a business delivery.', remainingScopeTurnsEstimate: 3 },
        { sessionID: session, agent: name, callID: `call-${session}`, messageID: run },
      )
      assert.match(String(executed), /# ok/)
    })
  }
})
