import test from 'node:test'
import assert from 'node:assert/strict'
import { withRealFork, forkCall, physicalUserMessageOf, observeTerminals, joinCall, coldWorkSnapshot } from '../../delegation/tests/support/real-fork.mjs'

test('WHAT[managed-session-lifecycle-029] a rooted natural Engineer stop with a completed provider turn must settle parent join without an extra idle notification', async () => {
  await withRealFork('owner-natural-terminal-029', async ({ hooks, runtime, createdIds, directory }) => {
    const observer = await observeTerminals(runtime)
    try {
      const accepted = await forkCall(hooks, 'owner-natural-terminal-029', 'Ada', 'Finish and return with prose')
      assert.match(accepted, /Ada/)
      await runtime.awaitIngress()
      const child = createdIds[0]
      const physical = physicalUserMessageOf(runtime, child)
      const assistant = {
        info: {
          id: 'child-run-natural-029', role: 'assistant', sessionID: child,
          parentID: physical.id, agent: 'engineer', providerID: 'provider',
          modelID: 'engineer-model', time: { created: 2 },
        },
        parts: [{ type: 'text', text: 'Natural completion with a substantive engineer report.' }],
      }
      runtime.pushHostMessage(child, assistant)
      const messages = await runtime.client.session.messages({ path: { id: child } })
      await hooks['experimental.chat.messages.transform']({ sessionID: child }, {
        messages: messages.data.map(m => ({ ...m, info: { ...(m.info ?? m), sessionID: child } })),
      })
      // A foreign provider terminal must not receive current idle authority.
      const foreign = {
        ...assistant.info,
        id: 'foreign-natural-terminal-029',
        parentID: 'foreign-physical-user-029',
        time: { created: 2, completed: 3 },
        finish: 'stop',
      }
      await hooks.event({ event: { type: 'message.updated', properties: { info: foreign } } })
      assert.equal(observer.seen.some(e => e.sessionId === child && e.outcome?.kind === 'Completed'), false,
        'a foreign assistant terminal cannot complete a current child run')
      assistant.info.finish = 'stop'
      assistant.info.time.completed = 3
      await hooks.event({ event: { type: 'message.updated', properties: { info: assistant.info } } })
      const joined = await Promise.race([
        joinCall(hooks, 'owner-natural-terminal-029'),
        new Promise(resolve => setTimeout(() => resolve('JOIN-HUNG-029'), 3500)),
      ])
      assert.notEqual(joined, 'JOIN-HUNG-029', 'a proven natural stop cannot leave the parent in agent-join forever')
      assert.match(String(joined), /Natural completion with a substantive engineer report/)
      const works = await coldWorkSnapshot(directory, 'owner-natural-terminal-029')
      assert.ok(works.some(w => w.lifecycle === 'Retired'))
    } finally { observer.dispose() }
  })
})


test('WHAT[managed-session-lifecycle-029] completed exact assistant event reaches the Host signal observer without requiring an extra start event', async () => {
  const {HostSignalRouter, HostSignalRouter__ObserveLocal_4E60E31B}=
    await import('../../../dist/OpenCode/Signals/HostSignalAdapter.js')
  const {SessionIdModule_create}=await import('../../../dist/Foundation/Identity.js')
  const seen=[]
  const router=new HostSignalRouter(new Set(),()=>{},(started,stepEnd,terminal)=>{
    seen.push({started,stepEnd,terminal})
    return Promise.resolve()
  })
  const event={type:'message.updated',properties:{info:{
    sessionID:'child-natural-029',id:'run-natural-029',parentID:'msg-child-natural-029',
    role:'assistant',time:{created:2,completed:3},finish:'stop',
  }}}
  await HostSignalRouter__ObserveLocal_4E60E31B(router,event)
  assert.equal(seen.length,1,'exact terminal event itself must reach the lifecycle owner')
  assert.equal(seen[0].stepEnd,true)
  assert.equal(seen[0].started.ProviderRun?.fields?.[0],'run-natural-029')
  assert.equal(seen[0].terminal?.Disposition?.tag,0)
})
