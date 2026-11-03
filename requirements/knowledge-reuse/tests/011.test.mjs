import assert from 'node:assert/strict'
import test from 'node:test'
import { existsSync, mkdtempSync, mkdirSync, readFileSync, realpathSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { runVerificationToolProbe } from '../../../scripts/lib/verification-tool-probe.mjs'
import { sandbox, createCase, deferred, casebook, eventStore, index, parse } from './support/casebook.mjs'
import * as fetchSurface from '../../../dist/Repository/Knowledge/Casebook/FetchSurface.js'
import * as settlements from '../../../dist/Repository/Knowledge/Casebook/SettlementSurface.js'
import * as bookkeeper from '../../../dist/Repository/Knowledge/Casebook/BookkeeperSurface.js'
import * as language from '../../../dist/Participant/Provider/LanguageSurface.js'
import { withPreference } from '../../provider-language/tests/support/language-fixtures.mjs'
import { CANONICAL_Q, CANONICAL_A, installBookkeeperRuntime, scriptedBookkeeperPort } from './support/bookkeeper-session-support.mjs'

test('WHAT[knowledge-reuse-011] overlapping fetches share one actual maintenance and the first caller language', async () => {
  const local = sandbox()
  const reached = deferred()
  const release = deferred()
  let first
  let second
  try {
    const { identity, shelfmark } = await createCase(local)
    writeFileSync(join(local.dir, 'subject.txt'), 'version-C')
    const { port, createCalls } = scriptedBookkeeperPort()
    const send = port.SendPrompt
    port.SendPrompt = async (...args) => { reached.resolve(); await release.promise; return send(...args) }
    installBookkeeperRuntime(port, [identity])
    await withPreference('en', async () => {
      language.refreshGlobalLanguage()
      first = local.fetch(shelfmark)
      await Promise.race([
        reached.promise,
        first.then(() => assert.fail('the first Bookkeeper must reach its SendPrompt barrier')),
      ])
      try {
        await withPreference('zh-CN', async () => {
          language.refreshGlobalLanguage()
          second = local.fetch(shelfmark)
          assert.equal(createCalls.length, 1)
          release.resolve()
          const [firstText, secondText] = await Promise.all([first, second])
          assert.equal(firstText, secondText)
          assert.match(firstText, /The facts this case depended on had changed/)
          assert.doesNotMatch(firstText, /这份 case 所依赖的事实已经变化/)
          assert.equal(createCalls.length, 1)
        })
      } finally {
        language.refreshGlobalLanguage()
      }
    })
  } finally {
    release.resolve()
    await Promise.allSettled([first, second])
    language.refreshGlobalLanguage()
    bookkeeper.resetRuntime()
    local.close()
  }
})

const bindOwnedFetch = (directory, store, owner) => fetchSurface.contract(
  { tool: { schema: { string: () => ({}) } } }, directory, store, owner,
)
const executeFetch = (tool, shelfmark) => tool.execute({ shelfmark }, { sessionID: 'reader', agent: 'engineer' })
const observedTypes = observations => observations.flatMap(value => value.originalRequested.map(event => event.type))

test('WHAT[knowledge-reuse-011] same workspace and actual store share one Bookkeeper across two tool bindings with different required owners', async () => {
  const local = sandbox()
  const reached = deferred()
  const release = deferred()
  const observations = []
  const leftIncidents = []
  const rightIncidents = []
  let shared
  let left
  let right
  try {
    const { identity, baseline, shelfmark } = await createCase(local, 'same-workspace-two-owners')
    writeFileSync(join(local.dir, 'subject.txt'), 'version-C')
    shared = eventStore.createAppendPayloadStore(local.store, false, value => observations.push(value))
    const leftOwner = settlements.createOwner(value => leftIncidents.push(value))
    const rightOwner = settlements.createOwner(value => rightIncidents.push(value))
    assert.notStrictEqual(leftOwner, rightOwner)
    const leftTool = bindOwnedFetch(local.dir, shared, leftOwner)
    const rightTool = bindOwnedFetch(local.dir, shared, rightOwner)
    const { port, createCalls, programCalls } = scriptedBookkeeperPort()
    const send = port.SendPrompt
    port.SendPrompt = async (...args) => {
      reached.resolve()
      await release.promise
      return send(...args)
    }
    installBookkeeperRuntime(port, [identity])

    left = executeFetch(leftTool, shelfmark)
    await Promise.race([
      reached.promise,
      left.then(() => assert.fail('the actual left Bookkeeper must reach its SendPrompt barrier')),
    ])
    assert.equal(createCalls.length, 1)
    assert.equal(programCalls.length, 0)
    assert.equal(observations.length, 0)
    right = executeFetch(rightTool, shelfmark)
    const outcomes = Promise.allSettled([left, right])
    release.resolve()
    const [first, second] = await outcomes

    assert.equal(first.status, 'fulfilled', first.reason)
    assert.equal(second.status, 'fulfilled', second.reason)
    assert.equal(first.value, second.value)
    assert.equal(parse(first.value).answer, CANONICAL_A)
    assert.equal(createCalls.length, 1, 'different required owners must not split the workspace flight')
    assert.equal(programCalls.length, 1)
    assert.deepEqual(leftIncidents, [])
    assert.deepEqual(rightIncidents, [])
    // One successful Fetch refreshes once and then records one actual access.
    assert.deepEqual(observedTypes(observations), ['EngineerCaseRefreshed', 'EngineerCaseAccessed'])
    for (const observation of observations) {
      assert.equal(observation.append.error, null)
      assert.deepEqual(observation.append.cuts, [])
      const [event] = observation.originalRequested
      assert.equal(event.payload.identity, identity)
      assert.deepEqual(eventStore.read(local.store, event.id), event)
    }
    const current = await casebook.fetchCaseByIdentity(local.store, identity)
    assert.equal(current.a, CANONICAL_A)
    assert.equal(current.completionFileState, baseline)
    assert.notEqual(current.maintenanceFileState, baseline)
  } finally {
    release.resolve()
    await Promise.allSettled([left, right])
    bookkeeper.resetRuntime()
    if (shared) eventStore.dispose(shared)
    local.close()
  }
})

test('WHAT[knowledge-reuse-011] different workspaces with the same shelfmark keep the paused Refresh and actual malformed Access cut with their own owners', async () => {
  const leftLocal = sandbox()
  const rightLocal = sandbox()
  const reached = deferred()
  const release = deferred()
  const leftObservations = []
  const rightObservations = []
  const leftIncidents = []
  const rightIncidents = []
  let leftStore
  let rightStore
  let left
  let right
  try {
    const identity = 'same-shelfmark-different-workspaces'
    const leftCase = await createCase(leftLocal, identity)
    const rightCase = await createCase(rightLocal, identity)
    assert.equal(leftCase.shelfmark, rightCase.shelfmark)
    assert.notEqual(leftLocal.dir, rightLocal.dir)
    const rightBefore = await casebook.fetchCaseByIdentity(rightLocal.store, identity)
    writeFileSync(join(leftLocal.dir, 'subject.txt'), 'version-C')
    leftStore = eventStore.createAppendPayloadStore(leftLocal.store, false, value => leftObservations.push(value))
    rightStore = eventStore.createAppendPayloadStore(rightLocal.store, true, value => rightObservations.push(value))
    const leftOwner = settlements.createOwner(value => leftIncidents.push(value))
    const rightOwner = settlements.createOwner(value => rightIncidents.push(value))
    const leftTool = bindOwnedFetch(leftLocal.dir, leftStore, leftOwner)
    const rightTool = bindOwnedFetch(rightLocal.dir, rightStore, rightOwner)
    const { port, createCalls, programCalls } = scriptedBookkeeperPort()
    const send = port.SendPrompt
    port.SendPrompt = async (...args) => {
      reached.resolve()
      await release.promise
      return send(...args)
    }
    installBookkeeperRuntime(port, [identity])

    left = executeFetch(leftTool, leftCase.shelfmark)
    await Promise.race([
      reached.promise,
      left.then(() => assert.fail('the actual left Bookkeeper must reach its SendPrompt barrier')),
    ])
    assert.equal(createCalls.length, 1)
    assert.equal(programCalls.length, 0)
    assert.equal(leftObservations.length, 0)
    right = executeFetch(rightTool, rightCase.shelfmark)
    const outcomes = Promise.allSettled([left, right])
    // Never wait for a right-hand barrier the old shelfmark-only implementation cannot reach.
    release.resolve()
    const [first, second] = await outcomes

    assert.equal(first.status, 'fulfilled', first.reason)
    assert.equal(parse(first.value).answer, CANONICAL_A)
    assert.equal(createCalls.length, 1)
    assert.equal(programCalls.length, 1)
    assert.deepEqual(observedTypes(leftObservations), ['EngineerCaseRefreshed', 'EngineerCaseAccessed'])
    assert.deepEqual(leftIncidents, [])
    assert.equal(rightObservations.length, 1, 'the right workspace must execute its own Access instead of borrowing the left answer')
    const { originalRequested, append } = rightObservations[0]
    assert.equal(originalRequested.length, 1)
    const [original] = originalRequested
    assert.equal(original.type, 'EngineerCaseAccessed')
    assert.equal(original.payload.identity, identity)
    assert.deepEqual(append.requested, [{ ...original, payload: {} }])
    assert.equal(append.error, null)
    assert.equal(append.cuts.length, 1)
    const [cut] = append.cuts
    assert.equal(cut.rule, 'Casebook')
    assert.equal(cut.failedEventId, original.id)
    assert.deepEqual(eventStore.read(rightLocal.store, original.id), append.requested[0])
    const cutFact = eventStore.read(rightLocal.store, cut.cutEventId)
    assert.equal(cutFact.type, 'ProjectionCutTail')
    assert.equal(cutFact.payload.rule, 'Casebook')
    assert.equal(cutFact.payload.failed_event_id, original.id)
    assert.deepEqual(cutFact.parents, [original.id])
    assert.equal(eventStore.read(leftLocal.store, original.id), null)
    assert.equal(eventStore.read(leftLocal.store, cut.cutEventId), null)
    assert.deepEqual(await casebook.fetchCaseByIdentity(rightLocal.store, identity), rightBefore)

    assert.equal(rightIncidents.length, 1)
    const incident = settlements.describeIncident(rightIncidents[0])
    assert.equal(incident.operation, 'Access')
    assert.equal(incident.caseIdentity, identity)
    assert.equal(incident.eventId, original.id)
    assert.deepEqual(incident.cuts, append.cuts)
    assert.equal(second.status, 'fulfilled', second.reason)
    assert.match(second.value, /No change was found|没有变化/,
      'the unavailable case keeps its old body and reports the cache result')
    assert.equal(settlements.isIncident(rightIncidents[0]), true)
  } finally {
    release.resolve()
    await Promise.allSettled([left, right])
    bookkeeper.resetRuntime()
    if (leftStore) eventStore.dispose(leftStore)
    if (rightStore) eventStore.dispose(rightStore)
    leftLocal.close()
    rightLocal.close()
  }
})

const storeMismatchMessage = 'Casebook fetch flight store binding mismatch'

test('WHAT[knowledge-reuse-011] an active workspace flight rejects a different actual store capability without a second Bookkeeper or append', async () => {
  const local = sandbox()
  const reached = deferred()
  const release = deferred()
  const firstObservations = []
  const secondObservations = []
  const firstIncidents = []
  const secondIncidents = []
  let firstStore
  let secondBase
  let secondStore
  let first
  let second
  try {
    const { identity, shelfmark } = await createCase(local, 'same-workspace-different-stores')
    secondBase = eventStore.create(local.dir, 'second-independent-writer')
    assert.deepEqual(await casebook.fetchCaseByIdentity(secondBase, identity),
      await casebook.fetchCaseByIdentity(local.store, identity), 'the second actual store has the same initial durable Case')
    writeFileSync(join(local.dir, 'subject.txt'), 'version-C')
    firstStore = eventStore.createAppendPayloadStore(local.store, false, value => firstObservations.push(value))
    secondStore = eventStore.createAppendPayloadStore(secondBase, false, value => secondObservations.push(value))
    const firstTool = bindOwnedFetch(local.dir, firstStore, settlements.createOwner(value => firstIncidents.push(value)))
    const secondTool = bindOwnedFetch(local.dir, secondStore, settlements.createOwner(value => secondIncidents.push(value)))
    const { port, createCalls, programCalls } = scriptedBookkeeperPort()
    const send = port.SendPrompt
    port.SendPrompt = async (...args) => {
      reached.resolve()
      await release.promise
      return send(...args)
    }
    installBookkeeperRuntime(port, [identity])

    first = executeFetch(firstTool, shelfmark)
    await Promise.race([
      reached.promise,
      first.then(() => assert.fail('the actual first Bookkeeper must reach its SendPrompt barrier')),
    ])
    assert.equal(createCalls.length, 1)
    assert.equal(programCalls.length, 0)
    assert.equal(firstObservations.length, 0)
    second = executeFetch(secondTool, shelfmark)
    const outcomes = Promise.allSettled([first, second])
    release.resolve()
    const [started, refused] = await outcomes

    assert.equal(started.status, 'fulfilled', started.reason)
    assert.equal(parse(started.value).answer, CANONICAL_A)
    assert.equal(createCalls.length, 1)
    assert.equal(programCalls.length, 1)
    assert.deepEqual(observedTypes(firstObservations), ['EngineerCaseRefreshed', 'EngineerCaseAccessed'])
    assert.deepEqual(secondObservations, [])
    assert.deepEqual(firstIncidents, [])
    assert.deepEqual(secondIncidents, [])
    assert.equal(existsSync(join(local.dir, 'wanxiangshu', 'events', 'second-independent-writer-0000.ndjson')), false)
    assert.equal(refused.status, 'rejected', 'an incompatible store binding must not receive the first flight result')
    assert.equal(refused.reason.message, storeMismatchMessage)
    assert.equal(settlements.isIncident(refused.reason), false, 'binding refusal has no settled semantic-cut incident')
  } finally {
    release.resolve()
    await Promise.allSettled([first, second])
    bookkeeper.resetRuntime()
    if (firstStore) eventStore.dispose(firstStore)
    if (secondStore) eventStore.dispose(secondStore)
    if (secondBase) eventStore.dispose(secondBase)
    local.close()
  }
})

const assertSharedCutSettlement = async (t, unknown) => {
  const directory = realpathSync(mkdtempSync(join(tmpdir(), 'wxs-shared-cut-')))
  mkdirSync(join(directory, '.wanxiang', 'casebook'), { recursive: true })
  const cause = new Error('original shared Refresh Current commit cause')
  const reached = deferred()
  const release = deferred()
  const observations = []
  const leftIncidents = []
  const rightIncidents = []
  let handle
  let shared
  let left
  let right
  try {
    handle = eventStore.create(directory, 'setup')
    const identity = 'shared-cut-two-owners'
    const { baseline, shelfmark } = await createCase({ dir: directory, store: handle }, identity)
    const before = await casebook.fetchCaseByIdentity(handle, identity)
    const baselineEntry = JSON.parse(baseline)['subject.txt']
    assert.equal(baselineEntry.kind, 'Present')
    const baselineBytes = Buffer.from(await eventStore.readPayload(handle, baselineEntry.payloadRef))
    assert.equal(baselineBytes.toString('utf8'), 'version-B')
    await index.refresh(handle, 256)
    const beforeIndex = index.tryGet()
    const eventsDirectory = join(directory, 'wanxiangshu', 'events')
    const setupFile = join(eventsDirectory, 'setup-0000.ndjson')
    const setupBytes = readFileSync(setupFile)
    eventStore.dispose(handle)
    handle = undefined
    handle = unknown
      ? eventStore.createWithCurrentCommitFault(directory, 'operation', cause, false)
      : eventStore.create(directory, 'operation')
    shared = eventStore.createAppendPayloadStore(handle, true, value => observations.push(value))
    const leftOwner = settlements.createOwner(value => {
      assert.equal(observations.length, 1, 'the original actual Store settlement precedes owner delivery')
      leftIncidents.push(value)
    })
    const rightOwner = settlements.createOwner(value => rightIncidents.push(value))
    assert.notStrictEqual(leftOwner, rightOwner)
    const leftTool = bindOwnedFetch(directory, shared, leftOwner)
    const rightTool = bindOwnedFetch(directory, shared, rightOwner)
    writeFileSync(join(directory, 'subject.txt'), 'version-C')
    const { port, createCalls, programCalls } = scriptedBookkeeperPort()
    const send = port.SendPrompt
    port.SendPrompt = async (...args) => {
      reached.resolve()
      await release.promise
      return send(...args)
    }
    installBookkeeperRuntime(port, [identity])
    left = executeFetch(leftTool, shelfmark)
    await Promise.race([
      reached.promise,
      left.then(() => assert.fail('the first actual Bookkeeper must reach its SendPrompt barrier')),
    ])
    assert.equal(createCalls.length, 1)
    assert.equal(programCalls.length, 0)
    assert.equal(observations.length, 0)
    right = executeFetch(rightTool, shelfmark)
    const outcomes = Promise.allSettled([left, right])
    release.resolve()
    const [first, second] = await outcomes

    assert.equal(createCalls.length, 1)
    assert.equal(programCalls.length, 1)
    assert.equal(observations.length, 1, 'the joined fetches neither retry Refresh nor append Access')
    assert.deepEqual(observedTypes(observations), ['EngineerCaseRefreshed'])
    const { originalRequested, append } = observations[0]
    assert.equal(originalRequested.length, 1)
    const [original] = originalRequested
    assert.equal(original.payload.identity, identity)
    assert.deepEqual(append.requested, [{ ...original, payload: {} }])
    assert.equal(append.cuts.length, 1)
    const [cut] = append.cuts
    assert.equal(cut.rule, 'Casebook')
    assert.equal(cut.failedEventId, original.id)
    if (unknown) {
      assert.equal(append.error.code, 'CommitUnknown')
      assert.equal(append.error.phase, 'CurrentCommit')
      assert.strictEqual(append.error.cause, cause)
      assert.deepEqual(append.error.cleanupFailures, [])
      assert.deepEqual(append.error.requested, append.requested)
      assert.deepEqual(append.error.prepared.cuts, [cut])
      assert.deepEqual(append.error.prepared.durableEvents.map(event => event.id), [original.id, cut.cutEventId])
      assert.equal(eventStore.read(handle, original.id), null, 'the injected fault precedes the live Current commit')
      assert.equal(eventStore.read(handle, cut.cutEventId), null)
    } else {
      assert.equal(append.error, null)
      assert.deepEqual(eventStore.read(handle, original.id), append.requested[0])
      const cutFact = eventStore.read(handle, cut.cutEventId)
      assert.equal(cutFact.type, 'ProjectionCutTail')
      assert.equal(cutFact.payload.rule, 'Casebook')
      assert.equal(cutFact.payload.failed_event_id, original.id)
      assert.deepEqual(cutFact.parents, [original.id])
    }
    assert.equal(leftIncidents.length, 1)
    assert.deepEqual(rightIncidents, [], 'only the first executing flight owns this settlement callback')
    assert.equal(first.status, 'fulfilled', first.reason)
    assert.equal(second.status, 'fulfilled', second.reason)
    assert.equal(first.value, second.value, 'the waiter receives the same unavailable-case result')
    assert.equal(parse(first.value).answer, before.a)
    assert.match(first.value, /Maintenance could not|未能根据所给文件 diff/)
    assert.equal(settlements.isIncident(leftIncidents[0]), true)
    const incident = settlements.describeIncident(leftIncidents[0])
    assert.equal(incident.operation, 'Refresh')
    assert.equal(incident.caseIdentity, identity)
    assert.equal(incident.eventId, original.id)
    assert.deepEqual(incident.cuts, append.cuts)
    if (unknown) {
      assert.equal(incident.failure.code, 'CASEBOOK_APPEND_COMMIT_UNKNOWN')
      assert.equal(incident.failure.kind, 'unknown')
      assert.equal(incident.failure.isOriginalError(append.originalError), true)
      assert.equal(incident.sharesPreparedWithError(append.originalError), true)
      assert.strictEqual(incident.failure.primary.cause, cause)
      assert.equal(incident.failure.primary.phase, 'CurrentCommit')
      assert.deepEqual(incident.failure.cleanupFailures, [])
      assert.deepEqual(incident.failure.requestedEventIds, [original.id])
      assert.deepEqual(incident.failure.preparedEventIds, [original.id, cut.cutEventId])
    }
    assert.deepEqual(index.tryGet(), beforeIndex, 'neither waiter advances the provider index after the cut')
    assert.deepEqual(await casebook.fetchCaseByIdentity(handle, identity), before)
    assert.equal(before.completionFileState, baseline)
    assert.equal(before.maintenanceFileState, baseline)
    const operationFile = join(eventsDirectory, 'operation-0000.ndjson')
    const operationBytes = readFileSync(operationFile)
    const facts = operationBytes.toString('utf8').trimEnd().split('\n').map(JSON.parse)
    assert.deepEqual(facts.map(event => event.event_id), [original.id, cut.cutEventId])
    assert.equal(facts[0].event_type, 'EngineerCaseRefreshed')
    assert.deepEqual(facts[0].payload, {})
    assert.equal(facts[1].event_type, 'ProjectionCutTail')
    assert.equal(facts[1].payload.rule, 'Casebook')
    assert.equal(facts[1].payload.failed_event_id, original.id)
    assert.deepEqual(facts[1].parents, [original.id])
    assert.deepEqual(readFileSync(setupFile), setupBytes)
    assert.deepEqual(Buffer.from(await eventStore.readPayload(handle, baselineEntry.payloadRef)), baselineBytes)

    eventStore.dispose(shared)
    shared = undefined
    eventStore.dispose(handle)
    handle = undefined
    const env = { ...process.env }
    delete env.NODE_TEST_CONTEXT
    const cold = JSON.parse(await runVerificationToolProbe(process.execPath, [
      fileURLToPath(new URL('./support/cut-cold-child.mjs', import.meta.url)), directory,
      JSON.stringify({ writer: 'operation', setupBytes: setupBytes.toString('base64'), operationBytes: operationBytes.toString('base64'),
        failedEventId: original.id, cutEventId: cut.cutEventId, identity,
        before: { ...before, accessOrder: before.accessOrder.toString(), lastAccessOrder: before.lastAccessOrder.toString() }, baseline,
        payloadRef: baselineEntry.payloadRef, payloadBytes: baselineBytes.toString('base64') }),
    ], { cwd: directory, env, signal: t.signal }).catch(error => {
      if (error.stderr) error.message += '\n' + error.stderr
      throw error
    }))
    assert.notEqual(cold.pid, process.pid)
    assert.deepEqual({ ...cold.current, accessOrder: BigInt(cold.current.accessOrder), lastAccessOrder: BigInt(cold.current.lastAccessOrder) }, before)
    assert.deepEqual(readFileSync(setupFile), setupBytes)
    assert.deepEqual(readFileSync(operationFile), operationBytes)
    assert.deepEqual(index.tryGet(), beforeIndex)
  } finally {
    release.resolve()
    await Promise.allSettled([left, right])
    bookkeeper.resetRuntime()
    if (shared) eventStore.dispose(shared)
    if (handle) eventStore.dispose(handle)
    rmSync(directory, { recursive: true, force: true })
  }
}

test('WHAT[knowledge-reuse-011] two required owners on one workspace store share the original settled cut rejection and one callback',
  t => assertSharedCutSettlement(t, false))

test('WHAT[knowledge-reuse-011] two required owners on one workspace store share one actual CurrentCommitUnknown cut, original incident and Prepared',
  t => assertSharedCutSettlement(t, true))

test('WHAT[knowledge-reuse-011] two required owners on one workspace store share one actual CurrentCommitUnknown without cuts and retain the stale response', async t => {
  const directory = realpathSync(mkdtempSync(join(tmpdir(), 'wxs-shared-unknown-valid-')))
  mkdirSync(join(directory, '.wanxiang', 'casebook'), { recursive: true })
  const cause = new Error('original shared legal Refresh Current commit cause')
  const reached = deferred()
  const release = deferred()
  const sourceObservations = []
  const observations = []
  const leftIncidents = []
  const rightIncidents = []
  let handle
  let observed
  let shared
  let left
  let right
  let completed = false
  try {
    handle = eventStore.create(directory, 'setup')
    const identity = 'shared-unknown-without-cuts-two-owners'
    const { baseline, shelfmark } = await createCase({ dir: directory, store: handle }, identity)
    const before = await casebook.fetchCaseByIdentity(handle, identity)
    assert.equal(before.accessOrder, 0n)
    assert.equal(before.lastAccessOrder, 0n)
    const baselineEntry = JSON.parse(baseline)['subject.txt']
    assert.equal(baselineEntry.kind, 'Present')
    const baselineBytes = Buffer.from(await eventStore.readPayload(handle, baselineEntry.payloadRef))
    assert.equal(baselineBytes.toString('utf8'), 'version-B')
    await index.refresh(handle, 256)
    const beforeIndex = index.tryGet()
    const eventsDirectory = join(directory, 'wanxiangshu', 'events')
    const setupFile = join(eventsDirectory, 'setup-0000.ndjson')
    const setupBytes = readFileSync(setupFile)
    eventStore.dispose(handle)
    handle = eventStore.createWithCurrentCommitFault(directory, 'operation', cause, false)
    observed = eventStore.createAppendPayloadStore(handle, false, value => sourceObservations.push(value))
    shared = eventStore.createAppendPayloadStore(observed, false, value => observations.push(value))
    const leftOwner = settlements.createOwner(value => leftIncidents.push(value))
    const rightOwner = settlements.createOwner(value => rightIncidents.push(value))
    assert.notStrictEqual(leftOwner, rightOwner)
    const leftTool = bindOwnedFetch(directory, shared, leftOwner)
    const rightTool = bindOwnedFetch(directory, shared, rightOwner)
    writeFileSync(join(directory, 'subject.txt'), 'version-C')
    const { port, createCalls, programCalls } = scriptedBookkeeperPort()
    const send = port.SendPrompt
    port.SendPrompt = async (...args) => {
      reached.resolve()
      await release.promise
      return send(...args)
    }
    installBookkeeperRuntime(port, [identity])
    left = executeFetch(leftTool, shelfmark)
    await Promise.race([
      reached.promise,
      left.then(() => assert.fail('the first actual Bookkeeper must reach its SendPrompt barrier')),
    ])
    assert.equal(createCalls.length, 1)
    assert.equal(programCalls.length, 0)
    assert.equal(observations.length, 0)
    assert.equal(sourceObservations.length, 0)
    right = executeFetch(rightTool, shelfmark)
    const outcomes = Promise.allSettled([left, right])
    release.resolve()
    const [first, second] = await outcomes

    assert.equal(createCalls.length, 1)
    assert.equal(programCalls.length, 1)
    assert.equal(observations.length, 1, 'joined waiters do not retry Refresh or append Access')
    assert.equal(sourceObservations.length, 1)
    assert.deepEqual(observedTypes(observations), ['EngineerCaseRefreshed'])
    const { originalRequested, append } = observations[0]
    assert.equal(originalRequested.length, 1)
    const [original] = originalRequested
    assert.equal(original.payload.identity, identity)
    assert.equal(original.payload.q, CANONICAL_Q)
    assert.equal(original.payload.a, CANONICAL_A)
    assert.deepEqual(append.requested, originalRequested)
    assert.equal(append.error.code, 'CommitUnknown')
    assert.equal(append.error.phase, 'CurrentCommit')
    assert.strictEqual(append.error.cause, cause)
    assert.deepEqual(append.error.cleanupFailures, [])
    assert.deepEqual(append.cuts, [])
    assert.deepEqual(append.error.prepared.cuts, [])
    assert.deepEqual(append.error.requested, [original])
    assert.deepEqual(append.error.prepared.durableEvents, [original])
    assert.notEqual(append.originalError, null)
    assert.strictEqual(append.originalError, sourceObservations[0].append.originalError,
      'both borrowed ports forward the same original Error, including its required Prepared carrier')
    assert.strictEqual(sourceObservations[0].append.error.cause, cause)
    assert.deepEqual(sourceObservations[0].append.error.prepared, append.error.prepared)
    assert.equal(eventStore.read(handle, original.id), null, 'the fault precedes the hot Current commit')
    assert.equal(first.status, 'fulfilled', first.reason)
    assert.equal(second.status, 'fulfilled', second.reason)
    assert.equal(first.value, second.value)
    assert.equal(parse(first.value).answer, before.a)
    assert.match(first.value, /Maintenance could not|未能根据所给文件 diff/)
    assert.deepEqual(leftIncidents, [])
    assert.deepEqual(rightIncidents, [])
    assert.deepEqual(index.tryGet(), beforeIndex)
    assert.deepEqual(await casebook.fetchCaseByIdentity(handle, identity), before)
    const maintenanceFileState = original.payload.maintenance_file_state
    assert.notEqual(maintenanceFileState, baseline)
    const targetEntry = JSON.parse(maintenanceFileState)['subject.txt']
    assert.equal(targetEntry.kind, 'Present')
    assert.notEqual(targetEntry.payloadRef, baselineEntry.payloadRef)
    const targetBytes = Buffer.from(await eventStore.readPayload(handle, targetEntry.payloadRef))
    assert.equal(targetBytes.toString('utf8'), 'version-C')
    assert.deepEqual(Buffer.from(await eventStore.readPayload(handle, baselineEntry.payloadRef)), baselineBytes)
    const operationFile = join(eventsDirectory, 'operation-0000.ndjson')
    const operationBytes = readFileSync(operationFile)
    const facts = operationBytes.toString('utf8').trimEnd().split('\n').map(JSON.parse)
    assert.deepEqual(facts.map(event => event.event_id), [original.id])
    assert.equal(facts[0].event_type, 'EngineerCaseRefreshed')
    assert.deepEqual(facts[0].payload, original.payload)
    assert.deepEqual(facts[0].parents, original.parents)
    assert.deepEqual(facts[0].payload_refs, original.payloadRefs)
    assert.deepEqual(readFileSync(setupFile), setupBytes)
    eventStore.dispose(shared)
    shared = undefined
    eventStore.dispose(observed)
    observed = undefined
    eventStore.dispose(handle)
    handle = undefined
    const env = { ...process.env }
    delete env.NODE_TEST_CONTEXT
    const cold = JSON.parse(await runVerificationToolProbe(process.execPath, [
      fileURLToPath(new URL('./support/cut-cold-child.mjs', import.meta.url)), directory,
      JSON.stringify({ writer: 'operation', setupBytes: setupBytes.toString('base64'), operationBytes: operationBytes.toString('base64'),
        identity, before: { ...before, accessOrder: before.accessOrder.toString(), lastAccessOrder: before.lastAccessOrder.toString() },
        baseline, payloadRef: baselineEntry.payloadRef, payloadBytes: baselineBytes.toString('base64'),
        refreshed: { fact: original, maintenanceFileState, targetPayloadRef: targetEntry.payloadRef,
          targetPayloadBytes: targetBytes.toString('base64') } }),
    ], { cwd: directory, env, signal: t.signal }).catch(error => {
      if (error.stderr) error.message += '\n' + error.stderr
      throw error
    }))
    assert.notEqual(cold.pid, process.pid)
    const expected = { ...before, q: original.payload.q, a: original.payload.a, maintenanceFileState,
      accessOrder: 1n, lastAccessOrder: 1n }
    assert.deepEqual({ ...cold.current, accessOrder: BigInt(cold.current.accessOrder), lastAccessOrder: BigInt(cold.current.lastAccessOrder) }, expected)
    assert.deepEqual(readFileSync(setupFile), setupBytes)
    assert.deepEqual(readFileSync(operationFile), operationBytes)
    assert.deepEqual(index.tryGet(), beforeIndex)
    completed = true
  } finally {
    release.resolve()
    await Promise.allSettled([left, right])
    bookkeeper.resetRuntime()
    if (shared) eventStore.dispose(shared)
    if (observed) eventStore.dispose(observed)
    if (handle) eventStore.dispose(handle)
    if (completed) rmSync(directory, { recursive: true, force: true })
    else t.diagnostic('SHARED_UNKNOWN_WITHOUT_CUTS_EVIDENCE: retained ' + directory)
  }
})

test.todo('WHAT[knowledge-reuse-011] GAP-160: real replica branches become DomainConflict and converge through explicit resolution without LWW')
