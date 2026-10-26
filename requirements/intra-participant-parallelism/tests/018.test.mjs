// WHAT[intra-participant-parallelism-018] Fission 上下文继承与成员签名。
//
// 两条性质：
// (a) `this.fission(payload)` 的成员签名逐字不变：只有 payload 一个形参，
//     不带 carry（同身份换执行道，不是委派）。
//
// 断言全部落在可观测行为上：编程面成员的形参数、宿主入口收到的实参。
// 没有源码文本断言。

import assert from 'node:assert/strict'
import test from 'node:test'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { caseName, execute, render } from '../../../dist/Repository/Programming/Js/WorkflowSurface.js'
import {
  clearDelegationHost,
  installDelegationHost,
} from '../../../dist/Repository/Programming/Js/RuntimeSurface.js'

const program = (body) => `class Js extends JsProgram {
  async run() {
${body}
  }
}`

const notExercised = async () => {
  throw new Error('this delegation member must not reach the delegation host')
}

test('WHAT[intra-participant-parallelism-018] fission forwards the payload alone while fork forwards its optional extra array', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'wxs-fission-member-'))
  const engineerSession = `ses-fission-member-${Math.random().toString(36).slice(2, 8)}`
  const managerSession = `${engineerSession}-manager`
  const calls = []
  const forkCalls = []
  try {
    installDelegationHost(
      engineerSession,
      notExercised,
      async (...args) => {
        calls.push(args)
        return { ok: true, groupId: 'group-fission-member' }
      },
      notExercised,
      notExercised,
    )
    installDelegationHost(managerSession, async (...args) => {
      forkCalls.push(args)
      return { ok: true, name: 'fork-child', agentId: 'fork-agent', outcome: 'created' }
    }, notExercised, notExercised, notExercised)

    const engineerOutcome = await execute(
      dir,
      'Engineer',
      'en',
      engineerSession,
      {
        name: 'fission-payload',
        intent: '等待裂变回执，保留原请求状态。',
        program: program(
          `    const arity = String(this.fission.length);
    const receipt = await this.fission({ charge: "explore three designs", lanes: 2 });
    return { arity, groupId: receipt.groupId };`,
        ),
      },
      3000,
      Date.now() + 60_000,
      1 << 20,
      null,
    )
    assert.equal(caseName(engineerOutcome), 'Succeeded')
    const engineerDoc = render(engineerOutcome)
    assert.match(engineerDoc, /arity = "1"/, 'the fission member declares one parameter: the payload')
    assert.match(engineerDoc, /groupId = "group-fission-member"/, 'the fission receipt reaches the program as data')

    // The fission host receives only the payload.
    assert.equal(calls.length, 1, 'the fission host entry is reached once')
    assert.equal(calls[0].length, 1, 'the fission host entry receives the payload alone')
    assert.equal(calls[0][0].charge, 'explore three designs')
    assert.equal(calls[0][0].lanes, 2)

    // JavaScript length excludes defaulted parameters. Observe the actual fork
    // calls as well.
    const managerOutcome = await execute(
      dir,
      'Manager',
      'en',
      managerSession,
      { name: 'fork-payload', intent: '等待两次派工回执，保留已发出的请求。', program: program(`    await this.fork("first call");
    await this.fork("second call");
    return { forkArity: String(this.fork.length) };`) },
      3000,
      Date.now() + 60_000,
      1 << 20,
      null,
    )
    assert.equal(caseName(managerOutcome), 'Succeeded')
    assert.match(render(managerOutcome), /forkArity = "1"/, 'only the required payload counts toward fork.length')
    assert.equal(forkCalls.length, 2)
    assert.equal(forkCalls[0].length, 2, 'the delegate host receives member and payload')
    assert.equal(forkCalls[0][0], 'fork')
    assert.equal(forkCalls[0][1].charge, 'first call')
    assert.equal(forkCalls[1][1].charge, 'second call')
  } finally {
    clearDelegationHost(engineerSession)
    clearDelegationHost(managerSession)
    rmSync(dir, { recursive: true, force: true })
  }
})
