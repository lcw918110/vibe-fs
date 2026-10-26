// WHAT[repository-programming-065] An actual waiting command must not monopolize the provider run.
import assert from 'node:assert/strict'
import test from 'node:test'
import { existsSync, writeFileSync, readFileSync } from 'node:fs'
import { join } from 'node:path'
import { setTimeout as delay } from 'node:timers/promises'
import { parse as parseToml } from 'smol-toml'
import { acceptAuthorityRoot, withExecutablePlugin } from '../../verification-system/tests/support/plugin-fixture.mjs'

const SESSION = 'ses-065-independent-programs'
const RUN = 'run-065-independent-programs'
const WAIT = 'Wait for the held physical command while a second independent program proves that the host does not serialize whole executions.'
const program = body => `class Js extends JsProgram { async run() { ${body} } }`
const withEstimate = input => ({ ...input, remainingScopeTurnsEstimate: 4 })
async function until(predicate, label) {
  const end = Date.now() + 4000
  while (!predicate()) {
    assert.ok(Date.now() < end, label)
    await delay(10)
  }
}
test('WHAT[repository-programming-065] another program completes while a same-message command remains in flight', async () => {
  await withExecutablePlugin(async (hooks, directory, _ids, runtime) => {
    await acceptAuthorityRoot(runtime, SESSION, 'devops')
    const tool = hooks.tool['todowrite-for-devops']
    writeFileSync(join(directory, 'held.cjs'), `const fs = require('node:fs'); fs.writeFileSync('entered','yes'); const timer = setInterval(() => { if(fs.existsSync('release')) { clearInterval(timer); process.stdout.write('released'); } }, 10);`)
    let firstDone = false
    const context = callID => ({ sessionID: SESSION, agent: 'devops', callID, messageID: RUN })
    const first = tool.execute(withEstimate({ name: 'held', program: program("return await this.call('node held.cjs');"), intent: WAIT }), context('call-065-held'))
      .then(result => { firstDone = true; return result })
    try {
      await until(() => existsSync(join(directory, 'entered')), 'first command must actually enter')
      const second = await Promise.race([
        tool.execute(withEstimate({ name: 'independent', program: program("await this.write('second', 'completed'); return { ready: true };"), intent: WAIT }), context('call-065-independent')),
        delay(1800).then(() => { throw new Error('the waiting program blocked an independent tool call') }),
      ])
      assert.equal(firstDone, false, 'the command must still be pending when the second program returns')
      assert.equal(parseToml(String(second)).data.ready, true, String(second))
      assert.equal(readFileSync(join(directory, 'second'), 'utf8'), 'completed')
    } finally {
      writeFileSync(join(directory, 'release'), 'yes')
    }
    const result = await first
    assert.equal(parseToml(String(result)).data.stdout, 'released', String(result))
  })
})


const registeredDevOps = async (hooks, runtime, session) => {
  const id = 'root-' + session
  const message = { id, sessionID: session, role: 'user', agent: 'devops',
    model: { providerID: 'provider', modelID: 'devops-model' } }
  const parts = [{ type: 'text', text: 'Verify the program resource lifecycle.' }]
  runtime.pushHostMessage(session, { info: { ...message, time: { created: 1 } }, parts })
  await hooks['chat.message']({ sessionID: session, messageID: id, agent: 'devops' }, { message, parts })
}
const lifecycleContext = (session, call) => ({
  sessionID: session, agent: 'devops', messageID: 'run-' + session, callID: call,
})
test('WHAT[repository-programming-065] plugin disposal closes its paused command and runs finally before releasing file capabilities', async () => {
  await withExecutablePlugin(async (hooks, directory, _ids, runtime) => {
    const session = 'ses-065-dispose-command'
    await registeredDevOps(hooks, runtime, session)
    writeFileSync(join(directory, 'held.cjs'),
      "require('node:fs').writeFileSync('held.pid', String(process.pid)); setInterval(() => {}, 1000);")
    const parked = await hooks.tool['todowrite-for-devops'].execute({
      name: 'held',
      intent: 'Wait for the real command to enter and retain it for explicit cleanup.',
      program: program("try { await this.call('node held.cjs'); } finally { await this.write('closed.txt', 'closed'); }"),
      remainingScopeTurnsEstimate: 4,
    }, lifecycleContext(session, 'held-call'))
    assert.match(String(parked), /# parked/, String(parked))
    const pid = Number(readFileSync(join(directory, 'held.pid'), 'utf8'))
    await hooks.dispose()
    assert.equal(readFileSync(join(directory, 'closed.txt'), 'utf8'), 'closed')
    await until(() => {
      try {
        const stat = readFileSync('/proc/' + pid + '/stat', 'utf8')
        return ['Z', 'X', 'x'].includes(stat.slice(stat.lastIndexOf(')') + 1).trim().split(/\s+/)[0])
      } catch (error) {
        if (error.code === 'ENOENT' || error.code === 'ESRCH') return true
        throw error
      }
    }, 'plugin disposal must confirm the real command has exited')
  })
})
test('WHAT[repository-programming-065] disposing one plugin preserves another plugin program and releases only its own names', async () => {
  const sessionA = 'ses-065-owner-a'
  const sessionB = 'ses-065-owner-b'
  const parkInput = name => ({ name, intent: WAIT, program: program("await this.park('held'); return { alive: true };") ,remainingScopeTurnsEstimate:4})
  await withExecutablePlugin(async (hooksA, _directoryA, _idsA, runtimeA) => {
    await registeredDevOps(hooksA, runtimeA, sessionA)
    assert.match(String(await hooksA.tool['todowrite-for-devops'].execute(
      parkInput('owned'), lifecycleContext(sessionA, 'owned-call'))), /# parked/)
    await withExecutablePlugin(async (hooksB, _directoryB, _idsB, runtimeB) => {
      await registeredDevOps(hooksB, runtimeB, sessionB)
      assert.match(String(await hooksB.tool['todowrite-for-devops'].execute(
        parkInput('owned'), lifecycleContext(sessionB, 'owned-call'))), /# parked/)
      await hooksA.dispose()
      const resumed = await hooksB.tool['todowrite-for-devops'].execute({
        name: 'resume-owned', intent: WAIT,
        program: program("return await this.unpark('owned');"),
        remainingScopeTurnsEstimate:4,
      }, lifecycleContext(sessionB, 'resume-call'))
      assert.match(String(resumed), /# ok/, String(resumed))
      assert.match(String(resumed), /alive = true/, String(resumed))
    })
  })
  await withExecutablePlugin(async (hooks, _directory, _ids, runtime) => {
    await registeredDevOps(hooks, runtime, sessionA)
    const fresh = await hooks.tool['todowrite-for-devops'].execute({
      name: 'owned', intent: WAIT, program: program("return { fresh: true };"), remainingScopeTurnsEstimate:4,
    }, lifecycleContext(sessionA, 'owned-call'))
    assert.match(String(fresh), /# ok/, String(fresh))
    assert.match(String(fresh), /fresh = true/, String(fresh))
  })
})
