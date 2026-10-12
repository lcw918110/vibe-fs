import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import { syncBuiltinESMExports } from 'node:module'

const [mode, commonDir, writerId, phase, scenario, requestJson] = process.argv.slice(2)
const request = JSON.parse(requestJson)
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
const store = await import('../../../../dist/Persistence/EventStore/Surface.js')
const transaction = await import('../../../../dist/Repository/Programming/Js/TransactionSurface.js')
const handle = store.create(commonDir, writerId)
const originals = { appendFileSync: fs.appendFileSync, openSync: fs.openSync, fsyncSync: fs.fsyncSync, closeSync: fs.closeSync, rmSync: fs.rmSync }
const cause = new Error('JS bad-input actual owned Release completed before its response failed')
const descriptors = new Set()
const synced = new Set()
const counts = { append: 0, fsync: 0, close: 0, release: 0, injected: 0 }
const canonical = value => Array.isArray(value) ? value.map(canonical)
  : value !== null && typeof value === 'object'
    ? Object.fromEntries(Object.keys(value).sort().map(key => [key, canonical(value[key])])) : value
const dto = event => ({ id: event.event_id, stream: event.stream_id, type: event.event_type,
  parents: event.parents, payload: event.payload, payloadRefs: event.payload_refs })
const ownsFile = value => {
  if (typeof value !== 'string') return false
  const resolved = path.resolve(value)
  return resolved === path.resolve(file) || resolved.startsWith(path.resolve(path.join(commonDir, 'wanxiangshu', 'events', `${writerId}`)))
}

try {
  if (mode === 'cold') {
    const source = resolveWriterFile(commonDir, request.sourceWriter)
    assert.notEqual(writerId, request.sourceWriter)
    assert.equal(fs.readFileSync(source, 'base64'), request.bytes)
    for (const fact of request.facts) {
      assert.deepEqual(store.read(handle, fact.id), fact)
      assert.equal(store.head(handle, fact.stream), fact.id)
    }
    assert.equal(fs.existsSync(file), false)
    fs.writeSync(1, JSON.stringify({ pid: process.pid, preserved: true }) + '\n')
  } else {
    assert.equal(mode, 'measure')
    assert.ok(['valid', 'malformed', 'malformed-release'].includes(scenario))
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
      if (descriptors.has(args[0])) { counts.fsync += 1; synced.add(args[0]) }
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
        if (scenario === 'malformed-release') { counts.injected += 1; throw cause }
      }
      return result
    }
    syncBuiltinESMExports()
    const observed = store.createAppendPayloadStore(handle, scenario !== 'valid', ({ originalRequested, append }) => {
      const settlement = {
        originalIds: originalRequested.map(event => event.id),
        requestedIds: append.requested.map(event => event.id),
        cutIds: append.cuts.map(cut => cut.cutEventId),
        failedIds: append.cuts.map(cut => cut.failedEventId),
        kind: append.error?.code ?? 'Committed',
        phase: append.error?.phase,
        cause: append.error?.cause,
        evidenceRequestedIds: append.error?.requested?.map(event => event.id),
        preparedIds: append.error?.prepared?.durableEvents.map(event => event.id),
      }
      const bytes = fs.readFileSync(file)
      const lines = bytes.toString('utf8').trimEnd().split('\n').map(JSON.parse)
      assert.equal(bytes.toString('utf8'), lines.map(event => JSON.stringify(canonical(event)) + '\n').join(''))
      const facts = lines.map(dto)
      assert.equal(facts.length, scenario === 'valid' ? 1 : 2)
      assert.equal(facts[0].type, `JsTransaction${phase}`)
      assert.deepEqual(settlement.originalIds, [facts[0].id])
      assert.deepEqual(settlement.requestedIds, [facts[0].id])
      if (scenario !== 'valid') {
        assert.deepEqual(facts[0].payload, {}, 'explicit bad payload injection retains the original JS envelope identity')
        assert.equal(facts[1].type, 'ProjectionCutTail')
        assert.equal(facts[1].payload.rule, 'JsTransaction')
        assert.equal(facts[1].payload.failed_event_id, facts[0].id)
        assert.deepEqual(facts[1].parents, [facts[0].id])
        assert.deepEqual(settlement.cutIds, [facts[1].id])
        assert.deepEqual(settlement.failedIds, [facts[0].id])
      } else assert.deepEqual(settlement.cutIds, [])
      for (const fact of facts) {
        assert.deepEqual(store.read(handle, fact.id), fact)
        assert.equal(store.head(handle, fact.stream), fact.id)
      }
      assert.equal(fs.existsSync(lock), false)
      assert.equal(descriptors.size, 0)
      assert.equal(synced.size, 0)
      assert.deepEqual(counts, { append: 1, fsync: 1, close: 1, release: 1, injected: scenario === 'malformed-release' ? 1 : 0 })
      if (scenario === 'malformed-release') {
        assert.equal(settlement.kind, 'CommitUnknown')
        assert.equal(settlement.phase, 'StoreRelease')
        assert.strictEqual(settlement.cause, cause)
        assert.deepEqual(settlement.evidenceRequestedIds, [facts[0].id])
        assert.deepEqual(settlement.preparedIds, facts.map(fact => fact.id))
      } else assert.equal(settlement.kind, 'Committed')
      fs.writeSync(1, JSON.stringify({ stage: 'settled', pid: process.pid, counts, facts,
        bytes: bytes.toString('base64'), physicalPreconditions: true, causeSame: settlement.cause === cause }) + '\n')
    })
    const transactionId = 'boundary-transaction'
    const result = phase === 'Prepared'
      ? await transaction.appendPrepared(observed, { transactionId, workspaceRoot: 'owned-workspace', mutations: [] })
      : await transaction.appendCommitted(observed, transactionId)
    fs.writeSync(1, JSON.stringify({ stage: 'returned', ok: result.ok, code: result.code }) + '\n')
  }
} finally {
  Object.assign(fs, originals)
  syncBuiltinESMExports()
  store.dispose(handle)
}
