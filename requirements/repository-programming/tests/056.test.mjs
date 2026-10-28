import assert from 'node:assert/strict'
import test from 'node:test'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { parse as parseToml } from 'smol-toml'
import {
  execute,
  caseName,
  closePrograms,
  render,
} from '../../../dist/Repository/Programming/Js/WorkflowSurface.js'
import {
  installDelegationHost,
  clearDelegationHost,
} from '../../../dist/Repository/Programming/Js/RuntimeSurface.js'

const sandbox = () => {
  const dir = mkdtempSync(join(tmpdir(), 'wxs-delegation-'))
  return { dir, cleanup: () => rmSync(dir, { recursive: true, force: true }) }
}

const program = (body) => `class Js extends JsProgram {
  async run() {
${body}
  }
}`

let nextProgram = 0
const WAIT = 'Wait for this delegated operation and preserve its original outcome if the shared deadline expires.'
const runProgram = (dir, body, sessionId) =>
  execute(dir, 'Manager', 'en', sessionId, { name: 'delegation-' + (++nextProgram), program: program(body), intent: WAIT }, 3000, Date.now() + 60_000, 1 << 20, null)

// The host receives structured delegation messages; join is a separate tool.
const fakeHost = () => {
  const pending = []
  const calls = []
  let counter = 0
  return {
    pending,
    calls,
    delegate: async (name, payload, carry) => {
      calls.push({ member: name, payload, carry })
      if (name === 'devops') {
        return { ok: true, returned: [{ text: 'stub-devops' }] }
      }
      if (name !== 'fork' && name !== 'commission' && name !== 'resume') {
        return { ok: false, code: 'unknown-delegate' }
      }
      if (!payload || !payload.charge) return { ok: false, code: 'charge-required' }
      counter += 1
      const agentId = `agent-${counter}`
      pending.push({ name: payload.name, agentId, member: name })
      return { ok: true, name: payload.name, agentId, outcome: 'created' }
    },
    fission: async (payload) => ({
      ok: true,
      groupId: 'group-1',
      lanes: Array.from({ length: payload.lanes }, (_, index) => ({ index, sessionId: `lane-${index}` })),
    }),
    horizon: async () => ({ ok: true, agents: [], ptys: [] }),
    advisor: async () => ({ ok: true, advice: 'stub-advice' }),
  }
}

const install = (sessionId, host) =>
  installDelegationHost(sessionId, host.delegate, host.fission, host.horizon, host.advisor)

test('WHAT[repository-programming-056] delegation members return data envelopes, never prose', async () => {
  const { dir, cleanup } = sandbox()
  const sessionId = `sess-env-${Math.random().toString(36).slice(2, 8)}`
  try {
    const host = fakeHost()
    install(sessionId, host)
    const outcome = await runProgram(
      dir,
      `    const placed = await this.fork("index the parser module");
    return { placed, join: typeof this.join, dag: typeof this.dag };`,
      sessionId,
    )
    assert.equal(caseName(outcome), 'Succeeded')
    const doc = render(outcome)
    assert.match(doc, /name = "index-the-parser-module"/, 'the name is derived from the charge')
    assert.match(doc, /outcome = "created"/)
    assert.equal(parseToml(doc).data.join, 'undefined')
    assert.equal(parseToml(doc).data.dag, 'undefined')

    // fission belongs to roles that hold the Fission permission (Engineer).
    install(`${sessionId}-fission`, host)
    const fissionOutcome = await execute(
      dir,
      'Engineer',
      'en',
      `${sessionId}-fission`,
      {
        name: 'fission-envelope',
        intent: WAIT,
        program: program(
          `    return await this.fission({ charge: "explore three designs", lanes: 2 });`,
        ),
      },
      3000,
      Date.now() + 60_000,
      1 << 20,
      null,
    )
    assert.equal(caseName(fissionOutcome), 'Succeeded')
    const fissionDoc = render(fissionOutcome)
    assert.match(fissionDoc, /groupId = "group-1"/)
    assert.match(fissionDoc, /sessionId = "lane-0"/)
  } finally {
    clearDelegationHost(sessionId)
    await closePrograms()
    cleanup()
  }
})

test('WHAT[repository-programming-056] this.horizon serves the installed host roster and refuses with DELEGATION_UNAVAILABLE without one', async () => {
  const { dir, cleanup } = sandbox()
  const sessionId = `sess-horizon-${Math.random().toString(36).slice(2, 8)}`
  const roster = {
    ok: true,
    agents: [
      {
        label: 'stub-horizon-roster',
        status: 'still-away',
        pace: { turnsUsed: 2, turnsLeft: 3, timeUsedMs: 1000, timeLeftMs: 1500 },
      },
    ],
    ptys: [],
  }
  const notExercised = async () => {
    throw new Error('the horizon member must not reach fork/join/fission')
  }
  try {
    installDelegationHost(
      sessionId,
      notExercised,
      notExercised,
      async () => roster,
      notExercised,
    )
    const wired = await runProgram(dir, `    return await this.horizon();`, sessionId)
    assert.equal(caseName(wired), 'Succeeded')
    const wiredDoc = render(wired)
    assert.match(wiredDoc, /status = "still-away"/, 'the host roster reaches the program as data')
    assert.match(wiredDoc, /label = "stub-horizon-roster"/, 'the member returns the host envelope untouched')
    assert.match(wiredDoc, /ptys = \[\]/)

    clearDelegationHost(sessionId)
    const unwired = await runProgram(dir, `    return await this.horizon();`, sessionId)
    assert.equal(caseName(unwired), 'Succeeded')
    assert.match(render(unwired), /code = "DELEGATION_UNAVAILABLE"/)
  } finally {
    clearDelegationHost(sessionId)
    await closePrograms()
    cleanup()
  }
})

test('WHAT[repository-programming-056] fork requires the Fork capability and dag is absent', async () => {
  const { dir, cleanup } = sandbox()
  const sessionId = `sess-nofork-${Math.random().toString(36).slice(2, 8)}`
  try {
    const host = fakeHost()
    install(sessionId, host)
    const outcome = await execute(
      dir,
      'Engineer',
      'en',
      sessionId,
      { name: 'fork-capability', intent: WAIT, program: program(`    return { allowFork: typeof this.fork, allowDag: typeof this.dag };`) },
      3000,
      Date.now() + 60_000,
      1 << 20,
      null,
    )
    assert.equal(caseName(outcome), 'Succeeded')
    const doc = render(outcome)
    assert.match(doc, /allowFork = "undefined"/)
    assert.match(doc, /allowDag = "undefined"/)
  } finally {
    clearDelegationHost(sessionId)
    await closePrograms()
    cleanup()
  }
})

test('WHAT[repository-programming-056] a refusal envelope reaches the program as data', async () => {
  const { dir, cleanup } = sandbox()
  const sessionId = `sess-refuse-${Math.random().toString(36).slice(2, 8)}`
  try {
    const host = fakeHost()
    install(sessionId, host)
    const outcome = await runProgram(
      dir,
      `    const refused = await this.fork({}, [1]);
    return { refused, continued: true };`,
      sessionId,
    )
    assert.equal(caseName(outcome), 'Succeeded')
    const doc = render(outcome)
    assert.match(doc, /code = "charge-required"/)
    assert.match(doc, /continued = true/, 'a refusal does not throw; the program keeps running')
  } finally {
    clearDelegationHost(sessionId)
    await closePrograms()
    cleanup()
  }
})

test('WHAT[repository-programming-056] a missing host yields the DELEGATION_UNAVAILABLE envelope', async () => {
  const { dir, cleanup } = sandbox()
  try {
    const outcome = await runProgram(
      dir,
      `    return await this.fork("nowhere", [1]);`,
      'sess-056-nohost',
    )
    assert.equal(caseName(outcome), 'Succeeded')
    const doc = render(outcome)
    assert.match(doc, /code = "DELEGATION_UNAVAILABLE"/)
  } finally {
    await closePrograms()
    cleanup()
  }
})

test('WHAT[repository-programming-056] fork starts a delegate with payload alone', async () => {
  const { dir, cleanup } = sandbox()
  const sessionId = `sess-no-carry-${Math.random().toString(36).slice(2, 8)}`
  try {
    const host = fakeHost()
    install(sessionId, host)
    const outcome = await runProgram(
      dir,
      `    const placed = await this.fork("index the parser module");
    const continued = "kept-running";
    return { placed, continued };`,
      sessionId,
    )
    assert.equal(caseName(outcome), 'Succeeded')
    const doc = render(outcome)
    assert.match(doc, /outcome = "created"/)
    assert.match(doc, /continued = "kept-running"/)
    assert.equal(host.calls.length, 1)
  } finally {
    clearDelegationHost(sessionId)
    await closePrograms()
    cleanup()
  }
})
