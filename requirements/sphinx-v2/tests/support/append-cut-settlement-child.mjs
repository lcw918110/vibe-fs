import assert from 'node:assert/strict'
import { createHash } from 'node:crypto'
import fs from 'node:fs'
import { syncBuiltinESMExports } from 'node:module'
import path from 'node:path'

const [mode, commonDir, writerId, scenario, requestJson] = process.argv.slice(2)
assert.ok(['measure', 'cold'].includes(mode))
assert.ok(['valid', 'malformed', 'valid-release', 'malformed-release'].includes(scenario))
const request = JSON.parse(requestJson)
const store = await import('../../../../dist/Persistence/EventStore/Surface.js')
const persistence = await import('../../../../dist/Sphinx/V2/Persistence/Surface.js')
const digest = value => createHash('sha256').update(value).digest('hex')
const resolveWriterFile = (dir, id) => {
  const eventsDir = path.join(dir, 'wanxiangshu', 'events')
  const legacy = path.join(eventsDir, `${id}.ndjson`)
  if (fs.existsSync(legacy)) return legacy
  const segment0 = path.join(eventsDir, `${id}-0000.ndjson`)
  if (fs.existsSync(segment0)) return segment0
  return segment0
}
const file = resolveWriterFile(commonDir, writerId)
const lock = path.join(commonDir, 'wanxiangshu.lock')
const handle = store.create(commonDir, writerId)
const handles = [handle]
const originals = {
  appendFileSync: fs.appendFileSync, openSync: fs.openSync, fsyncSync: fs.fsyncSync,
  closeSync: fs.closeSync, rmSync: fs.rmSync,
}
const canonical = value => Array.isArray(value) ? value.map(canonical)
  : value !== null && typeof value === 'object'
    ? Object.fromEntries(Object.keys(value).sort().map(key => [key, canonical(value[key])])) : value
const dto = event => ({ id: event.event_id, stream: event.stream_id, type: event.event_type,
  parents: event.parents, payload: event.payload, payloadRefs: event.payload_refs })

try {
  if (mode === 'cold') {
    assert.notEqual(writerId, request.sourceWriter)
    const source = resolveWriterFile(commonDir, request.sourceWriter)
    assert.equal(fs.readFileSync(source, 'base64'), request.bytes)
    for (const fact of request.facts) {
      assert.deepEqual(store.read(handle, fact.id), fact)
      assert.deepEqual(store.heads(handle, fact.stream), [fact.id])
      assert.equal(store.head(handle, fact.stream), fact.id)
    }
    const current = persistence.canonicalCurrent(handle, digest, request.inquiryId)
    assert.deepEqual(current, request.current)
    assert.equal(fs.existsSync(file), false, 'Cold replay must not open a new writer file')
    assert.equal(fs.existsSync(lock), false)
    assert.equal(fs.readFileSync(source, 'base64'), request.bytes)
    fs.writeSync(1, JSON.stringify({ pid: process.pid, writerId, current, preserved: true }) + '\n')
  } else {
    const malformed = scenario.startsWith('malformed')
    const release = scenario.endsWith('-release')
    const cause = new Error('Sphinx bad-payload actual owned Release completed before its response failed')
    const descriptors = new Set()
    const synced = new Set()
    const counts = { append: 0, fsync: 0, close: 0, release: 0, injected: 0 }
    const ownsFile = value => {
      if (typeof value !== 'string') return false
      const resolved = path.resolve(value)
      return resolved === path.resolve(file) || resolved.startsWith(path.resolve(path.join(commonDir, 'wanxiangshu', 'events', `${writerId}`)))
    }

    fs.appendFileSync = function (...args) {
      const result = Reflect.apply(originals.appendFileSync, this, args)
      if (ownsFile(args[0])) counts.append += 1
      return result
    }
    fs.openSync = function (...args) {
      const descriptor = Reflect.apply(originals.openSync, this, args)
      if (ownsFile(args[0])) descriptors.add(descriptor)
      return descriptor
    }
    fs.fsyncSync = function (...args) {
      const result = Reflect.apply(originals.fsyncSync, this, args)
      if (descriptors.has(args[0])) {
        counts.fsync += 1
        synced.add(args[0])
      }
      return result
    }
    fs.closeSync = function (...args) {
      const result = Reflect.apply(originals.closeSync, this, args)
      if (synced.has(args[0])) counts.close += 1
      descriptors.delete(args[0])
      synced.delete(args[0])
      return result
    }
    fs.rmSync = function (...args) {
      const owned = typeof args[0] === 'string' && path.resolve(args[0]) === lock
      if (owned) assert.equal(JSON.parse(fs.readFileSync(path.join(lock, 'owner.json'), 'utf8')).pid, process.pid)
      const result = Reflect.apply(originals.rmSync, this, args)
      if (owned) {
        counts.release += 1
        assert.equal(fs.existsSync(lock), false)
        if (release) {
          counts.injected += 1
          throw cause
        }
      }
      return result
    }
    syncBuiltinESMExports()

    const prepared = persistence.prepareTransition(handle, digest, request.raw)
    assert.equal(prepared.ok, true, JSON.stringify(prepared.error))
    const original = prepared.value
    assert.equal(original.type, 'sphinx/v2-transition@2')
    assert.equal(original.payload.inquiry, request.raw.inquiry)
    assert.equal(original.payload.commandId, request.raw.commandId)
    assert.deepEqual(counts, { append: 0, fsync: 0, close: 0, release: 0, injected: 0 })
    assert.equal(fs.existsSync(file), false, 'Sealing a transition is not an append')

    const observations = []
    const payloadStore = store.createAppendPayloadStore(handle, malformed, observation => {
      observations.push({ layer: 'payload', observation })
    })
    handles.push(payloadStore)
    const forwardingStore = store.createAppendPayloadStore(payloadStore, false, observation => {
      observations.push({ layer: 'forward', observation })
    })
    handles.push(forwardingStore)
    const result = await store.append(forwardingStore, [original])
    assert.deepEqual(observations.map(value => value.layer), ['payload', 'forward'])
    const inner = observations[0].observation
    const outer = observations[1].observation
    assert.deepEqual(inner.originalRequested, [original])
    assert.deepEqual(outer.originalRequested, [original])
    assert.strictEqual(inner.append.originalError, outer.append.originalError)

    const bytes = fs.readFileSync(file)
    const lines = bytes.toString('utf8').trimEnd().split('\n').map(JSON.parse)
    assert.equal(bytes.toString('utf8'), lines.map(event => JSON.stringify(canonical(event)) + '\n').join(''))
    const facts = lines.map(dto)
    assert.equal(facts.length, malformed ? 2 : 1)
    const requested = { ...original, payload: malformed ? {} : original.payload }
    assert.deepEqual(facts[0], requested)
    assert.deepEqual(inner.append.requested, [requested])
    assert.deepEqual(outer.append.requested, [original])
    assert.deepEqual(inner.append.cuts, outer.append.cuts)
    assert.equal(inner.append.cuts.length, malformed ? 1 : 0)
    if (malformed) {
      const cut = facts[1]
      assert.equal(cut.type, 'ProjectionCutTail')
      assert.equal(cut.payload.rule, 'SphinxV2')
      assert.equal(cut.payload.failed_event_id, original.id)
      assert.deepEqual(cut.parents, [original.id])
      assert.equal(inner.append.cuts[0].failedEventId, original.id)
      assert.equal(inner.append.cuts[0].cutEventId, cut.id)
      assert.equal(inner.append.cuts[0].rule, 'SphinxV2')
      assert.match(inner.append.cuts[0].reason, /INVALID_TRANSITION_DTO/)
    }

    for (const fact of facts) {
      assert.deepEqual(store.read(handle, fact.id), fact)
      assert.deepEqual(store.heads(handle, fact.stream), [fact.id])
      assert.equal(store.head(handle, fact.stream), fact.id)
    }
    const current = persistence.canonicalCurrent(handle, digest, request.raw.inquiry)
    if (malformed) {
      assert.equal(current.ok, false)
      assert.equal(current.error.code, 'SemanticCut')
      assert.match(current.error.message, /INVALID_TRANSITION_DTO/)
      assert.equal(Object.hasOwn(current, 'value'), false)
    } else {
      assert.equal(current.ok, true, JSON.stringify(current.error))
      assert.equal(current.value.eventHead, original.id)
      assert.equal(current.value.revision, '0')
      assert.equal(current.value.goal.originalText, request.raw.events[0].payload.goal.originalText)
    }

    assert.equal(result.ok, !release)
    if (release) {
      assert.notEqual(inner.append.originalError, null)
      for (const error of [inner.append.error, outer.append.error, result.error]) {
        assert.equal(error.code, 'CommitUnknown')
        assert.equal(error.phase, 'StoreRelease')
        assert.strictEqual(error.cause, cause)
        assert.deepEqual(error.cleanupFailures, [])
        assert.deepEqual(error.requested, [requested])
        assert.deepEqual(error.prepared.durableEvents, facts)
        assert.deepEqual(error.prepared.cuts, inner.append.cuts)
      }
    } else {
      assert.equal(inner.append.originalError, null)
      assert.equal(inner.append.error, null)
      assert.equal(outer.append.error, null)
      assert.deepEqual(result.cuts, inner.append.cuts)
    }
    assert.deepEqual(counts, { append: 1, fsync: 1, close: 1, release: 1, injected: release ? 1 : 0 })
    assert.equal(fs.existsSync(lock), false)
    assert.equal(descriptors.size, 0)
    assert.equal(synced.size, 0)
    fs.writeSync(1, JSON.stringify({ pid: process.pid, original, facts, current, counts,
      inquiryId: request.raw.inquiry, commandId: request.raw.commandId,
      bytes: bytes.toString('base64'), cutIds: inner.append.cuts.map(cut => cut.cutEventId),
      kind: result.ok ? 'Committed' : result.error.code,
      phase: result.ok ? null : result.error.phase,
      causeSame: release && result.error.cause === cause,
      originalErrorSame: release ? inner.append.originalError === outer.append.originalError : null,
      physicalPreconditions: true }) + '\n')
  }
} finally {
  Object.assign(fs, originals)
  syncBuiltinESMExports()
  for (const owned of handles.reverse()) store.dispose(owned)
}
