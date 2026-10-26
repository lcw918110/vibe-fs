import assert from 'node:assert/strict'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import test from 'node:test'
import { DatabaseSync } from 'node:sqlite'
import { SqliteJournal } from 'wanxiangli/eventstore'
import { DurableWork } from 'wanxiangli/durable-work'
import { Work } from 'wanxiangli/work'

const canonical = (value) => {
  if (Array.isArray(value)) return `[${value.map(canonical).join(',')}]`
  if (value !== null && typeof value === 'object') {
    return `{${Object.keys(value).sort().map((key) => `${JSON.stringify(key)}:${canonical(value[key])}`).join(',')}}`
  }
  return JSON.stringify(value)
}

const review = (work, id, updates) =>
  work.execute(id, {
    type: 'Review',
    office: 'Plan',
    providerRunId: 'run',
    toolCallId: id,
    updates,
  })

test('WHAT[work-015] a relay missing its successor is not restored as an open work', () => {
  const work = new Work('work-15')
  review(work, 'r1', [{ key: 'need', value: 'scope' }])
  const relayed = review(work, 'r2', [{ key: 'need', value: null }])
  assert.equal(relayed.result.kind, 'Relayed')
  const frame = JSON.parse(work.records().at(-1))
  assert.deepEqual(frame.events.map((event) => event.type), ['IncumbencyRetired', 'ContextBarrier', 'IncumbencyOpened', 'PromptObliged'])
  const unpaired = canonical({ ...frame, events: [frame.events[0]] })
  assert.throws(() => new Work('work-15', [work.records()[0], unpaired]), /Illegal lifecycle transaction/)
  const restored = new Work('work-15', work.records())
  assert.equal(restored.inspect().active.origin, 'Successor')
  assert.equal(restored.inspect().wait, restored.recover().wait)
})

test('WHAT[work-015] a truncated record is not an opening and a canonical record needs no trailing newline', () => {
  const work = new Work('work-truncated')
  review(work, 'r1', [])
  const body = work.records()[0]
  assert.equal(body.endsWith('\n'), false)
  assert.equal(new Work('work-truncated', [body]).history()[0].events.some((event) => event.type === 'WorkOpened'), true)
  assert.throws(() => new Work('work-truncated', [`${body}\n`]), /canonical/)
  assert.throws(() => new Work('work-truncated', [`${body.slice(0, 12)}`]))
  assert.throws(() => new Work('work-truncated', ['{"event_type":"IncumbencyOpened"']))
})

test('WHAT[work-015] durable acknowledgement uses Committed, Replayed, Conflict, and NotWritten', () => {
  const directory = mkdtempSync(join(tmpdir(), 'wxs-work-015-'))
  const database = new DatabaseSync(join(directory, 'work.sqlite'))
  const journal = new SqliteJournal(database)
  try {
    const durable = new DurableWork('work-15', journal)
    const command = { type: 'Review', office: 'Plan', providerRunId: 'run', toolCallId: 'c1', updates: [] }
    const committed = durable.execute('r1', command)
    assert.equal(committed.status, 'Committed')
    assert.equal(durable.execute('r1', command).status, 'Replayed')
    const conflict = durable.execute('r1', { ...command, updates: [{ key: 'gap', value: 'fix' }] })
    assert.equal(conflict.status, 'Conflict')
    const body = journal.read('work-15')[0]
    const stale = journal.append('work-15', 0, 'r-stale', body)
    assert.ok(stale.status === 'Conflict' || stale.status === 'NotWritten', stale.status)
    assert.notEqual(stale.status, 'AppendNotAttempted')
    assert.notEqual(stale.status, 'CommitUnknown')
    assert.throws(() => new Work('work-15').execute('old', { type: 'AppendNotAttempted' }), /Unknown work command/)
  } finally {
    journal.close()
    rmSync(directory, { recursive: true, force: true })
  }
})
