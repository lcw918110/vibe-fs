import assert from 'node:assert/strict'
import test from 'node:test'
import { parse as parseToml } from 'smol-toml'
import * as join from '../../../dist/Execution/Delegation/Fork/OpenCode/JoinSurface.js'
import * as handles from '../../../dist/Execution/Delegation/Handle/Surface.js'

const completed = (name, record) => ({ kind: 'completed', agentId: `physical-${name}`, agentName: name, role: 'Engineer', runId: `run-${name}`, view: record })

test('WHAT[delegation-013] renderer preserves entry order and distinguishes completed, failed and abandoned testimony', () => {
  assert.equal(join.renderBatch('english', []), '')
  const wire = join.renderBatch('english', [
    completed('Ada', 'First evidence.'),
    { kind: 'failed', agentId: 'b', agentName: 'Bob', role: 'DevOps', runId: 'run-b', code: 'E', message: 'Second evidence.' },
    { kind: 'abandoned', agentId: 'c', agentName: 'Cal', reason: 'gone' },
  ])
  assert.match(wire, /Ada has returned/)
  assert.match(wire, /Bob could not complete/)
  assert.match(wire, /Cal did not return/)
  assert.ok(wire.indexOf('First evidence.') < wire.indexOf('Second evidence.'))
  assert.ok(wire.indexOf('Second evidence.') < wire.indexOf('Cal'))
  assert.deepEqual(parseToml(wire), {})
})

test('WHAT[delegation-013] renderer rejects malformed completion carriers without a success wire', () => {
  const base = completed('Ada', 'ok')
  const { role, ...missingRole } = base
  for (const invalid of [missingRole, { ...base, role: 'UnknownRole' }, { ...base, runId: '' }, { ...base, kind: 'unknown' }]) {
    assert.equal(join.renderBatch('english', [invalid]), '')
  }
  assert.equal(join.renderBatch('english', [{ kind: 'abandoned', agentId: 'a', agentName: 'Ada', role: 'UnknownRole', reason: 'gone' }]), '')
})

test('WHAT[delegation-013] display window retains whole entries and signals remaining testimony in both languages', () => {
  for (const language of ['english', 'zh-CN']) {
    const items = ['Ada', 'Bob', 'Cal'].map(name => completed(name, Array.from({ length: 800 }, (_, index) => `${name} line ${index}`).join('\n')))
    const wire = join.renderBatch(language, items)
    assert.match(wire, /Ada line 799/)
    assert.match(wire, /Bob line 799/)
    assert.doesNotMatch(wire, /Cal/)
    assert.match(wire, language === 'english' ? /Remaining completions are available/ : /仍有未展示的完成项/)
    const short = join.renderBatch(language, [completed('Ada', 'first'), completed('Bob', 'second')])
    assert.match(short, /first/)
    assert.match(short, /second/)
    assert.doesNotMatch(short, /Remaining completions|仍有未展示/)
  }
})

test('WHAT[delegation-013] PTY interruption wire retains its observed output', () => {
  const wire = join.renderBatch('english', [{ kind: 'pty-aborted', ptyId: 'p1', terminalLabel: 'watch', outcome: 'abort', message: 'stop' }])
  assert.match(wire, /watch was interrupted/)
  assert.equal(parseToml(wire).output, 'stop')
})

test('WHAT[delegation-013] display does not truncate the sole large entry supplied to the renderer', () => {
  const record = Array.from({ length: 2500 }, (_, index) => `line ${index}`).join('\n')
  const wire = join.renderBatch('english', [completed('Ada', record)])
  assert.match(wire, /line 2499/)
  assert.doesNotMatch(wire, /Remaining completions/)
})

test('WHAT[delegation-013] actual handle completion and retirement each take effect once', () => {
  const linked = handles.apply(handles.empty(), { op: 'link', handle: 'agent:h1', child: 'child', agent: 'engineer', role: 'Engineer' })
  assert.equal(linked.ok, true)
  const completion = handles.apply(linked.state, { op: 'complete', handle: 'agent:h1', kind: 'Terminal' })
  assert.equal(completion.ok, true)
  assert.equal(handles.read(completion.state, 'agent:h1').lifecycle, 'CompletedAwaitingJoin')
  assert.equal(handles.views(completion.state).joinable.length, 1)
  const duplicate = handles.apply(completion.state, { op: 'complete', handle: 'agent:h1', kind: 'Terminal' })
  assert.deepEqual(duplicate.error, { kind: 'TransitionRejected', reason: 'AlreadyCompleted' })
  const retired = handles.apply(completion.state, { op: 'retire', handle: 'agent:h1' })
  assert.equal(retired.ok, true)
  assert.equal(handles.read(retired.state, 'agent:h1').lifecycle, 'Retired')
  assert.equal(handles.views(retired.state).joinable.length, 0)
  const repeated = handles.apply(retired.state, { op: 'retire', handle: 'agent:h1' })
  assert.deepEqual(repeated.error, { kind: 'TransitionRejected', reason: 'HandleIsRetired' })
})

test('WHAT[delegation-013] actual join queue enforces its global cap and drains the exact remainder once', async () => {
  const cap = join.joinMaxBatch()
  assert.ok(Number.isInteger(cap) && cap > 0)
  const probe = join.createJoinProbe()
  const ids = []
  for (let index = 0; index < cap + 8; index += 1) {
    const placed = await join.joinProbeForkPty(probe, `exit ${index}`)
    assert.equal(placed.ok, true)
    ids.push(placed.ptyId)
  }
  for (const id of ids) join.joinProbeCompletePty(probe, id)
  assert.equal(join.joinProbeCounts(probe).pendingCompletions, cap + 8)
  const first = await join.joinAvailable(probe, 1000, join.createJoinInterrupt())
  assert.equal(first.kind, 'ResultsAvailable')
  assert.equal(first.count, cap)
  assert.equal(new Set(first.ptyIds).size, cap)
  assert.equal(join.joinProbeCounts(probe).pendingCompletions, 8)
  const second = await join.joinAvailable(probe, 1000, join.createJoinInterrupt())
  assert.equal(second.kind, 'ResultsAvailable')
  assert.equal(second.count, 8)
  assert.deepEqual([...first.ptyIds, ...second.ptyIds].sort(), [...ids].sort())
  assert.equal(join.joinProbeCounts(probe).pendingCompletions, 0)
  assert.equal(join.joinProbeCounts(probe).ptyRuns, 0)
  assert.equal((await join.joinAvailable(probe, 1000, join.createJoinInterrupt())).error, 'NothingToJoin')
})

test.todo('WHAT[delegation-013] real agent completion join enforces current ownership and durable CAS consumption across concurrent calls and restart (GAP-153)')
