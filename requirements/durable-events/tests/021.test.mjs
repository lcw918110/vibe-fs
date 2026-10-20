import assert from 'node:assert/strict'
import { existsSync, readFileSync, mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { readFile } from 'node:fs/promises'
import path from 'node:path'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import test from 'node:test'
import * as eventStore from '../../../dist/Persistence/EventStore/Surface.js'
import * as journal from '../../../dist/Persistence/Journal/Surface.js'

const id = (n) => n.toString(16).padStart(40, '0')

const canonicalize = (value) => {
  if (Array.isArray(value)) return value.map(canonicalize)
  if (value && typeof value === 'object') {
    return Object.fromEntries(Object.keys(value).sort().map((key) => [key, canonicalize(value[key])]))
  }
  return value
}

const canonicalEventLine = (event) => `${JSON.stringify(canonicalize(event))}\n`

const withTemp = (fn) => {
  const base = mkdtempSync(join(tmpdir(), 'wxs-event-store-append-'))
  return fn(base)
}

const event = (n, parents = [], type = 'JobRequested', payload = { n }) => ({
  id: id(n),
  stream: 'append/proof',
  type,
  parents: parents.map((p) => id(p)),
  payload,
  payloadRefs: [],
})

test('WHAT[durable-events-021] low-level append persists bad fact and cut together; reopening a store replays the scoped reset', async () => {
  const dir = withTemp((base) => base)
  const store = eventStore.create(dir, 'semantic-cut-proof')
  try {
    const bad = event(10, [], 'InspectorCaseCaptured', {})
    const first = await eventStore.append(store, [bad])
    assert.equal(first.ok, true, 'semantic failure is durable, not a storage append failure')
    assert.equal(first.cuts.length, 1)
    assert.equal(first.cuts[0].failedEventId, id(10))
    assert.equal(first.cuts[0].rule, 'Casebook')

    const file = path.join(dir, 'wanxiangshu', 'events', 'semantic-cut-proof-0000.ndjson')
    const afterBad = (await readFile(file, 'utf8')).trim().split('\n').map(JSON.parse)
    assert.equal(afterBad.length, 2, 'bad fact and reset fact are one durable append')
    assert.equal(afterBad[0].event_type, 'InspectorCaseCaptured')
    assert.equal(afterBad[1].event_type, 'ProjectionCutTail')
    assert.equal(afterBad[1].payload.failed_event_id, id(10))
    assert.equal(afterBad[1].payload.rule, 'Casebook')

    const good = event(11, [10], 'InspectorCaseAccessed', { session_id: 'case-after-cut' })
    const second = await eventStore.append(store, [good])
    assert.equal(second.ok, true)
    assert.equal(second.cuts.length, 0, 'cut is self-limited; future feature use retries normally')

    const reopened = eventStore.create(dir, 'semantic-cut-reopen')
    try {
      const replayed = eventStore.read(reopened, id(11))
      assert.ok(replayed)
      assert.equal(replayed.id, id(11), 'replay preserves bad → reset → good timeline')
    } finally {
      eventStore.dispose(reopened)
    }
  } finally {
    eventStore.dispose(store)
    rmSync(dir, { recursive: true, force: true })
  }
})

test('WHAT[durable-events-021] the actual producing process exits after semantic cut and a distinct next process resumes from the reset Current', async () => {
  const dir = withTemp((base) => base)
  const producer = eventStore.create(dir, 'producer-process')
  try {
    // 1. Producing process encounters a semantic failure, appending bad fact and ProjectionCutTail
    const bad = event(20, [], 'InspectorCaseCaptured', {})
    const appendResult = await eventStore.append(producer, [bad])
    assert.equal(appendResult.ok, true)
    assert.equal(appendResult.cuts.length, 1)
    assert.equal(appendResult.cuts[0].failedEventId, id(20))
    assert.equal(appendResult.cuts[0].rule, 'Casebook')
  } finally {
    // Producing process exits / disposes
    eventStore.dispose(producer)
  }

  // 2. Distinct next process resumes from the reset Current
  const consumer = eventStore.create(dir, 'consumer-process')
  try {
    // Next process appends valid event referencing after-cut state
    const valid = event(21, [20], 'InspectorCaseAccessed', { session_id: 'resumed-session' })
    const secondAppend = await eventStore.append(consumer, [valid])
    assert.equal(secondAppend.ok, true)
    assert.equal(secondAppend.cuts.length, 0)

    const replayed = eventStore.read(consumer, id(21))
    assert.ok(replayed)
    assert.equal(replayed.id, id(21), 'next process successfully resumes and reads timeline')
  } finally {
    eventStore.dispose(consumer)
    rmSync(dir, { recursive: true, force: true })
  }
})

test('WHAT[durable-events-021] an uncut historical Journal fault suppresses only its own journal stream', async () => {
  const dir = withTemp((base) => base)
  const eventsDir = path.join(dir, 'wanxiangshu', 'events')
  mkdirSync(eventsDir, { recursive: true })

  const handleLinked = ({ session, child, seq, id }) =>
    canonicalEventLine({
      event_id: id,
      event_type: 'JournalEnvelope',
      parents: [],
      payload: {
        EventId: ['EventId', id],
        Fact: [
          'Agent',
          [
            'Execution',
            [
              'HandleLinked',
              {
                Byname: 'Ada',
                CanonicalRole: 'Engineer',
                ChildSessionId: ['SessionId', child],
                Handle: ['Agent', ['AgentHandleId', 'h-scope']],
                Ownership: 'DurableParentHandle',
                ParentSessionId: ['SessionId', session],
                TargetAgent: 'engineer',
              },
            ],
          ],
        ],
        LocalSeq: ['LocalSeq', String(seq)],
        // This proof is about Journal fault-scope isolation, not writer retention.
        // A fixed far-future observation keeps the writer retained without reading
        // the wall clock or coupling the test to the 24h physical retention law.
        ObservedAt: `9999-01-01T00:00:0${seq}.000+00:00`,
        RuntimeId: ['RuntimeId', 'rt_journal_fault_scope'],
        Stream: ['Session', ['SessionId', session]],
      },
      payload_refs: [],
      stream_id: `journal/session/${session}`,
    })

  const historical = [
    handleLinked({ session: 'ses_fault_a', child: 'ses_child_a1', seq: 1, id: '1'.repeat(40) }),
    // The same handle cannot change its durable child. That conflict is the fault.
    handleLinked({ session: 'ses_fault_a', child: 'ses_child_a2', seq: 2, id: '2'.repeat(40) }),
    handleLinked({ session: 'ses_healthy_b', child: 'ses_child_b1', seq: 3, id: '3'.repeat(40) }),
  ]

  writeFileSync(path.join(eventsDir, 'historical.ndjson'), historical.join(''))

  try {
    const booted = await journal.JournalSurface_bootWithWriterId(
      dir,
      'reopen-proof',
      'rt_reopen',
      4242,
      '2026-01-02T00:00:00Z',
    )
    assert.equal(booted.ok, true, `historical replay should boot: ${JSON.stringify(booted.error)}`)
    assert.equal(journal.JournalSurface_hasSession(booted.journal, 'ses_fault_a'), true)
    assert.equal(
      journal.JournalSurface_hasSession(booted.journal, 'ses_healthy_b'),
      true,
      'a semantic fault in one Journal stream must not black out later independent session streams',
    )
    journal.JournalSurface_dispose(booted.journal)
  } finally {
    rmSync(dir, { recursive: true, force: true })
  }
})
