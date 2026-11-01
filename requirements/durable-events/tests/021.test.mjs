import assert from 'node:assert/strict'
import { existsSync, readFileSync, mkdirSync, mkdtempSync, readdirSync, rmSync, writeFileSync } from 'node:fs'
import { readFile } from 'node:fs/promises'
import path from 'node:path'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import test from 'node:test'
import * as eventStore from '../../../dist/Persistence/EventStore/Surface.js'
import * as journal from '../../../dist/Persistence/Journal/Surface.js'
import * as dispatch from '../../../dist/Interaction/Dispatch/DispatchSurface.js'

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

test('WHAT[durable-events-021] a foreign writer rejected in a shared Journal stream quarantines only its own chain, never another writer\'s committed facts', async () => {
  const dir = withTemp((base) => base)
  const eventsDir = path.join(dir, 'wanxiangshu', 'events')
  mkdirSync(eventsDir, { recursive: true })
  const session = 'ses_shared_owner'

  const handleLinked = ({ runtime, handle, child, seq, id }) =>
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
                Byname: handle,
                CanonicalRole: 'Engineer',
                ChildSessionId: ['SessionId', child],
                Handle: ['Agent', ['AgentHandleId', handle]],
                Ownership: 'DurableParentHandle',
                ParentSessionId: ['SessionId', session],
                TargetAgent: 'engineer',
              },
            ],
          ],
        ],
        LocalSeq: ['LocalSeq', String(seq)],
        ObservedAt: `9999-01-01T00:00:0${seq}.000+00:00`,
        RuntimeId: ['RuntimeId', runtime],
        Stream: ['Session', ['SessionId', session]],
      },
      payload_refs: [],
      stream_id: `journal/session/${session}`,
    })

  // Two processes drove the same session concurrently, each against its own
  // Current. The k-way merge interleaves them: owner A1, foreign B1 (rejected:
  // the handle cannot change its child), then owner A2. A2 never depended on B1.
  writeFileSync(
    path.join(eventsDir, 'owner-writer.ndjson'),
    [
      handleLinked({ runtime: 'rt_owner', handle: 'h-shared', child: 'ses_owner_child', seq: 1, id: '1'.repeat(40) }),
      handleLinked({ runtime: 'rt_owner', handle: 'h-owner-later', child: 'ses_owner_later', seq: 2, id: '3'.repeat(40) }),
    ].join(''),
  )
  writeFileSync(
    path.join(eventsDir, 'foreign-writer.ndjson'),
    [
      handleLinked({ runtime: 'rt_foreign', handle: 'h-shared', child: 'ses_foreign_child', seq: 1, id: '2'.repeat(40) }),
      handleLinked({ runtime: 'rt_foreign', handle: 'h-foreign-later', child: 'ses_foreign_later', seq: 2, id: '4'.repeat(40) }),
    ].join(''),
  )

  try {
    const booted = await journal.JournalSurface_bootWithWriterId(dir, 'reload-proof', 'rt_reload', 4243, '2026-01-02T00:00:00Z')
    assert.equal(booted.ok, true, `replay should boot: ${JSON.stringify(booted.error)}`)
    const handles = journal.JournalSurface_snapshot(booted.journal).sessionProjections[session]?.handles?.handles ?? []
    const byHandle = Object.fromEntries(handles.map((h) => [h.handle, h.childSessionId]))
    assert.equal(byHandle['agent:h-shared'], 'ses_owner_child', 'the first committed binding wins')
    assert.equal(
      byHandle['agent:h-owner-later'],
      'ses_owner_later',
      "a foreign writer's rejected fact must not erase the owner's later committed facts on reload",
    )
    assert.equal(byHandle['agent:h-foreign-later'], undefined, "the rejected writer's own tail stays quarantined")
    journal.JournalSurface_dispose(booted.journal)
  } finally {
    rmSync(dir, { recursive: true, force: true })
  }
})

test('WHAT[durable-events-021] a stale second writer cannot admit a competing HumanRoot', async () => {
  const dir = withTemp((base) => base)
  const selection = {
    kind: 'RootSelection',
    ownerSession: null,
    ownerLogicalRun: null,
    ownerAuthorityRoot: null,
    participantIdentity: {
      participant: 'manager',
      role: 'manager',
      selectedTier: 'deep',
      persona: 'Lead',
      personaCatalogVersion: 1,
      origin: 'ResolvedAtRoot',
    },
  }
  const accept = (handle, session, physicalMessageId) =>
    dispatch.acceptHumanRootSelection(handle, session, physicalMessageId, selection)

  const owner = await journal.JournalSurface_bootWithWriterId(dir, 'owner-writer', 'rt_owner', 4244, '2026-01-02T00:00:00Z')
  const stale = await journal.JournalSurface_bootWithWriterId(dir, 'stale-writer', 'rt_stale', 4245, '2026-01-02T00:00:00Z')
  assert.equal(owner.ok, true, JSON.stringify(owner.error))
  assert.equal(stale.ok, true, JSON.stringify(stale.error))

  try {
    const accepted = await accept(owner.journal, 'ses-manager', 'msg-owner-root')
    assert.equal(accepted.ok, true, JSON.stringify(accepted.error))

    const refused = await accept(stale.journal, 'ses-manager', 'msg-stale-root')
    assert.equal(refused.ok, false, 'a stale snapshot must not admit a second HumanRoot')
    assert.ok(
      /ActiveRunIdentityConflict|AuthorityPersistenceRejected|writer refused/.test(JSON.stringify(refused.error)),
      `refusal must be a typed business outcome: ${JSON.stringify(refused.error)}`,
    )

    const eventsDir = join(dir, 'wanxiangshu', 'events')
    const lines = readdirSync(eventsDir).flatMap((name) =>
      readFileSync(join(eventsDir, name), 'utf8')
        .split('\n')
        .filter((line) => line.length > 0)
        .map(JSON.parse),
    )
    assert.ok(
      !lines.some((line) => line.event_type === 'ProjectionCutTail'),
      'the refused admission must not append a ProjectionCutTail',
    )

    const staleLines = readFileSync(join(eventsDir, 'stale-writer-0000.ndjson'), 'utf8')
      .split('\n')
      .filter((line) => line.length > 0)
    assert.equal(
      staleLines.filter((line) => JSON.parse(line).event_type === 'JournalEnvelope').length,
      1,
      'the stale writer must have committed only its lazy RuntimeStarted',
    )

    const later = await accept(stale.journal, 'ses-follow-up', 'msg-stale-next')
    assert.equal(later.ok, true, `the refusal must not poison the writer: ${JSON.stringify(later.error)}`)

    journal.JournalSurface_dispose(owner.journal)
    journal.JournalSurface_dispose(stale.journal)

    const cold = await journal.JournalSurface_bootWithWriterId(dir, 'cold-writer', 'rt_cold', 4246, '2026-01-02T00:00:00Z')
    assert.equal(cold.ok, true, JSON.stringify(cold.error))
    const ownerRun = dispatch.projectionObservation(cold.journal, 'ses-manager').activeLogicalRun
    assert.equal(ownerRun.authorityRoot, accepted.profile.authorityRoot, 'cold replay keeps exactly the first admitted HumanRoot')
    journal.JournalSurface_dispose(cold.journal)
  } finally {
    journal.JournalSurface_dispose(owner.journal)
    journal.JournalSurface_dispose(stale.journal)
    rmSync(dir, { recursive: true, force: true })
  }
})
