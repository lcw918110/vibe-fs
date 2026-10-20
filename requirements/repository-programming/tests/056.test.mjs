import assert from 'node:assert/strict'
import test from 'node:test'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import {
  execute,
  caseName,
  parkedHandle,
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

const runProgram = (dir, body, sessionId) =>
  execute(dir, 'Manager', 'en', sessionId, { program: program(body) }, 3000, Date.now() + 60_000, 1 << 20, null)

// A stateful fake host: forks are remembered until the next join consumes them.
const fakeHost = () => {
  const pending = []
  let counter = 0
  return {
    pending,
    fork: async (payload) => {
      if (!payload || !payload.charge) return { ok: false, code: 'charge-required' }
      counter += 1
      const agentId = `agent-${counter}`
      pending.push({ name: payload.name, agentId })
      return { ok: true, name: payload.name, agentId, outcome: 'created' }
    },
    join: async () => {
      const records = pending.splice(0)
      return {
        ok: true,
        records: records.map((entry) => ({
          name: entry.name,
          agentId: entry.agentId,
          role: 'engineer',
          status: 'completed',
          view: `record:${entry.name}`,
        })),
        byName: {},
      }
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
  installDelegationHost(sessionId, host.fork, host.join, host.fission, host.horizon, host.advisor)

test('WHAT[repository-programming-056] delegation members return data envelopes, never prose', async () => {
  const { dir, cleanup } = sandbox()
  const sessionId = `sess-env-${Math.random().toString(36).slice(2, 8)}`
  try {
    const host = fakeHost()
    install(sessionId, host)
    const outcome = await runProgram(
      dir,
      `    const placed = await this.fork("index the parser module");
    const joined = await this.join();
    return { placed, joined };`,
      sessionId,
    )
    assert.equal(caseName(outcome), 'Succeeded')
    const doc = render(outcome)
    assert.match(doc, /name = "index-the-parser-module"/, 'the name is derived from the charge')
    assert.match(doc, /outcome = "created"/)
    assert.match(doc, /record:index-the-parser-module/, 'join returns the delivered view')

    // fission belongs to roles that hold the Fission permission (Engineer).
    install(`${sessionId}-fission`, host)
    const fissionOutcome = await execute(
      dir,
      'Engineer',
      'en',
      `${sessionId}-fission`,
      {
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
    cleanup()
  }
})

test('WHAT[repository-programming-056] js.horizon serves the installed host roster and refuses with DELEGATION_UNAVAILABLE without one', async () => {
  const { dir, cleanup } = sandbox()
  const sessionId = `sess-horizon-${Math.random().toString(36).slice(2, 8)}`
  const roster = {
    ok: true,
    agents: [
      {
        label: 'Ada',
        status: 'still-away',
        workState: 'none',
        record: 'stub-horizon-roster',
        turnsUsed: 0,
        turnsLeft: 0,
        timeUsedMs: 0,
        timeLeftMs: 0,
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
      notExercised,
      async () => roster,
      notExercised,
    )
    const wired = await runProgram(dir, `    return await this.horizon();`, sessionId)
    assert.equal(caseName(wired), 'Succeeded')
    const wiredDoc = render(wired)
    assert.match(wiredDoc, /status = "still-away"/, 'the host roster reaches the program as data')
    assert.match(wiredDoc, /record = "stub-horizon-roster"/, 'the member returns the host envelope untouched')
    assert.match(wiredDoc, /ptys = \[\]/)

    clearDelegationHost(sessionId)
    const unwired = await runProgram(dir, `    return await this.horizon();`, sessionId)
    assert.equal(caseName(unwired), 'Succeeded')
    assert.match(render(unwired), /code = "DELEGATION_UNAVAILABLE"/)
  } finally {
    clearDelegationHost(sessionId)
    cleanup()
  }
})

test('WHAT[repository-programming-056] fork and dag require the Fork capability', async () => {
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
      { program: program(`    return { allowFork: typeof this.fork, allowDag: typeof this.dag };`) },
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
      `    const refused = await this.fork({});
    return { refused, continued: true };`,
      sessionId,
    )
    assert.equal(caseName(outcome), 'Succeeded')
    const doc = render(outcome)
    assert.match(doc, /code = "charge-required"/)
    assert.match(doc, /continued = true/, 'a refusal does not throw; the program keeps running')
  } finally {
    clearDelegationHost(sessionId)
    cleanup()
  }
})

test('WHAT[repository-programming-056] a missing host yields the DELEGATION_UNAVAILABLE envelope', async () => {
  const { dir, cleanup } = sandbox()
  try {
    const outcome = await runProgram(
      dir,
      `    return await this.fork("nowhere");`,
      'sess-056-nohost',
    )
    assert.equal(caseName(outcome), 'Succeeded')
    const doc = render(outcome)
    assert.match(doc, /code = "DELEGATION_UNAVAILABLE"/)
  } finally {
    cleanup()
  }
})

test('WHAT[repository-programming-056] dag runs dependencies in waves and carries their records', async () => {
  const { dir, cleanup } = sandbox()
  const sessionId = `sess-dag-${Math.random().toString(36).slice(2, 8)}`
  try {
    const host = fakeHost()
    install(sessionId, host)
    const outcome = await runProgram(
      dir,
      `    return await this.dag({ nodes: [
      { id: "spec", charge: "write the interface spec" },
      { id: "impl", charge: "implement it", deps: ["spec"] },
    ] });`,
      sessionId,
    )
    assert.equal(caseName(outcome), 'Succeeded')
    const doc = render(outcome)
    assert.match(doc, /record:spec/)
    assert.match(doc, /record:impl/)
    assert.match(doc, /skipped = \[\]/)
  } finally {
    clearDelegationHost(sessionId)
    cleanup()
  }
})

test('WHAT[repository-programming-056] a failed dag node parks and the injected skip decides', async () => {
  const { dir, cleanup } = sandbox()
  const sessionId = `sess-dagpark-${Math.random().toString(36).slice(2, 8)}`
  try {
    const host = fakeHost()
    // The "impl" node's child fails at terminal.
    const failingJoin = async () => {
      const records = host.pending.splice(0)
      return {
        ok: true,
        records: records.map((entry) => ({
          name: entry.name,
          agentId: entry.agentId,
          role: 'engineer',
          status: entry.name === 'impl' ? 'failed' : 'completed',
          view: entry.name === 'impl' ? '' : `record:${entry.name}`,
        })),
        byName: {},
      }
    }
    installDelegationHost(sessionId, host.fork, failingJoin, host.fission, host.horizon, host.advisor)

    const parked = await runProgram(
      dir,
      `    return await this.dag({ nodes: [
      { id: "spec", charge: "write the interface spec" },
      { id: "impl", charge: "implement it", deps: ["spec"] },
    ] });`,
      sessionId,
    )
    assert.equal(caseName(parked), 'Parked')
    const doc = render(parked)
    assert.match(doc, /code = "node-failed"/)
    assert.match(doc, /# parked/)

    const resumed = await execute(
      dir,
      'Manager',
      'en',
      sessionId,
      { unpark: { handle: parkedHandle(parked), value: 'skip' } },
      3000,
      Date.now() + 60_000,
      1 << 20,
      null,
    )
    assert.equal(caseName(resumed), 'Succeeded')
    const resumedDoc = render(resumed)
    assert.match(resumedDoc, /skipped = \["impl"\]/)
  } finally {
    clearDelegationHost(sessionId)
    cleanup()
  }
})

test('WHAT[repository-programming-056] an undelivered child reports status undelivered in join and parks dag on node-undelivered', async () => {
  const { dir, cleanup } = sandbox()
  const sessionId = `sess-dagundelivered-${Math.random().toString(36).slice(2, 8)}`
  try {
    const host = fakeHost()
    const undeliveredJoin = async () => {
      const records = host.pending.splice(0)
      const mapped = records.map((entry) => ({
        name: entry.name,
        agentId: entry.agentId,
        role: 'engineer',
        status: entry.name === 'impl' ? 'undelivered' : 'completed',
        view: entry.name === 'impl' ? '' : `record:${entry.name}`,
      }))
      return {
        ok: true,
        records: mapped,
        byName: Object.fromEntries(mapped.map((r) => [r.name, r])),
      }
    }
    installDelegationHost(sessionId, host.fork, undeliveredJoin, host.fission, host.horizon, host.advisor)

    const joinOutcome = await runProgram(
      dir,
      `    await this.fork({ charge: "implement it", name: "impl" });
    return await this.join();`,
      sessionId,
    )
    assert.equal(caseName(joinOutcome), 'Succeeded')
    const joinDoc = render(joinOutcome)
    assert.match(joinDoc, /status = "undelivered"/)
    assert.match(joinDoc, /view = ""/)

    const parked = await runProgram(
      dir,
      `    return await this.dag({ nodes: [
      { id: "spec", charge: "write the interface spec" },
      { id: "impl", charge: "implement it", deps: ["spec"] },
    ] });`,
      sessionId,
    )
    assert.equal(caseName(parked), 'Parked')
    const doc = render(parked)
    assert.match(doc, /code = "node-undelivered"/)
    assert.match(doc, /# parked/)

    const resumed = await execute(
      dir,
      'Manager',
      'en',
      sessionId,
      { unpark: { handle: parkedHandle(parked), value: { view: 'manual-impl' } } },
      3000,
      Date.now() + 60_000,
      1 << 20,
      null,
    )
    assert.equal(caseName(resumed), 'Succeeded')
    const resumedDoc = render(resumed)
    assert.match(resumedDoc, /manual-impl/)
  } finally {
    clearDelegationHost(sessionId)
    cleanup()
  }
})
