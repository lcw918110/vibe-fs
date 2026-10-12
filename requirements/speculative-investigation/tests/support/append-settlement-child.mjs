import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import {createHash} from 'node:crypto'
import {syncBuiltinESMExports} from 'node:module'

const [mode, commonDir, writerId, scenario, input] = process.argv.slice(2)
const H = text => createHash('sha256').update(text).digest('hex')
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
const EventStore = await import('../../../../dist/Persistence/EventStore/Surface.js')
const Strength = await import('../../../../dist/Strength/Surface.js')
const handle = EventStore.create(commonDir, writerId)
const originalRemove = fs.rmSync
const originalAppend = fs.appendFileSync
let armed = false
let removals = 0
let releaseCalls = 0
let appendCalls = 0
const appendedTypes = []
const injected = new Error('Strength actual owned Release completed before its response failed')
const canonical = value => value === null || typeof value !== 'object' ? JSON.stringify(value)
  : Array.isArray(value) ? '[' + value.map(canonical).join(',') + ']'
    : '{' + Object.keys(value).sort().map(key => JSON.stringify(key) + ':' + canonical(value[key])).join(',') + '}'
const sourceDto = event => ({id: event.event_id, stream: event.stream_id, type: event.event_type,
  parents: event.parents, payload: event.payload, payloadRefs: event.payload_refs})

try {
  if (mode === 'cold') {
    const request = JSON.parse(input)
    assert.notEqual(writerId, request.sourceWriter)
    const source = resolveWriterFile(commonDir, request.sourceWriter)
    assert.equal(fs.readFileSync(source, 'base64'), request.bytes)
    assert.deepEqual(EventStore.read(handle, request.event.id), request.event)
    assert.equal(EventStore.head(handle, request.event.stream), request.event.id)
    assert.equal(fs.existsSync(file), false)
    const candidate = Strength.projectionCandidate('settlement', Strength.storeCurrent(handle))
    process.stdout.write(JSON.stringify({pid: process.pid, event: EventStore.read(handle, request.event.id), candidate}))
  } else {
    assert.equal(mode, 'measure')
    assert.ok(['store', 'append', 'prepared', 'duplicate'].includes(scenario))
    const durability = Strength.durabilityCreate(handle)
    const requested = Strength.eventRequested({decisionId: 'settlement', ownerSessionId: 'owner',
      ownerLogicalRun: {logicalRunId: 'logical', authorityRootUserMessageId: 'user'},
      sourcePhysicalUserMessageId: 'user', sourceProviderRun: 'run', sourceToolCallIds: ['call'],
      requestedRounds: 2, contractRevision: 1})
    let publishRequest
    if (scenario === 'prepared' || scenario === 'duplicate') {
      assert.deepEqual(await Strength.durabilityAppend(durability, requested), {ok: true})
    }
    if (scenario === 'prepared') {
      assert.deepEqual(await Strength.durabilityAppend(durability,
        Strength.eventBound('settlement', 'run', 'replica', 'anchor')), {ok: true})
      const built = Strength.frameTryBuild(H, [{requestOrdinal: 1, exchanges: [{toolName: 'read',
        canonicalArguments: '{"filePath":"owned"}', canonicalResult: '真实材料\r\n尾部  '}]}])
      assert.equal(built.ok, true)
      publishRequest = {ownerSessionId: 'owner', decisionId: 'settlement', targetProviderRun: 'run',
        replicaSessionId: 'replica', anchorDigest: 'anchor', bundle: built.value}
    }
    const before = fs.existsSync(file) ? fs.readFileSync(file) : Buffer.alloc(0)
    const ownsFile = value => {
      if (typeof value !== 'string') return false
      const resolved = path.resolve(value)
      return resolved === path.resolve(file) || resolved.startsWith(path.resolve(path.join(commonDir, 'wanxiangshu', 'events', `${writerId}`)))
    }
    fs.appendFileSync = function (...args) {
      const result = Reflect.apply(originalAppend, this, args)
      if (armed && ownsFile(args[0])) {
        appendCalls += 1
        const appended = Buffer.from(args[1]).toString('utf8').trimEnd().split('\n').map(JSON.parse)
        appendedTypes.push(...appended.map(event => event.event_type))
        const physical = fs.readFileSync(file)
        assert.deepEqual(physical.subarray(before.length), Buffer.from(args[1]), 'The original append wrote this actual batch')
      }
      return result
    }
    fs.rmSync = function (...args) {
      const owned = armed && typeof args[0] === 'string' && path.resolve(args[0]) === lock
      if (owned) assert.equal(JSON.parse(fs.readFileSync(path.join(lock, 'owner.json'), 'utf8')).pid, process.pid)
      const result = Reflect.apply(originalRemove, this, args)
      if (owned) {
        releaseCalls += 1
        if (scenario === 'prepared' && !appendedTypes.includes('StrengthCandidatePrepared')) return result
        armed = false
        removals += 1
        assert.equal(fs.existsSync(lock), false)
        throw injected
      }
      return result
    }
    syncBuiltinESMExports()
    armed = true
    let result
    let thrown
    try {
      result = scenario === 'prepared' ? await Strength.durabilityPublishPrepared(durability, publishRequest)
        : scenario === 'store' ? await Strength.storeAppend(handle, H, requested)
          : await Strength.durabilityAppend(durability, requested)
    } catch (error) { thrown = error }
    assert.equal(removals, 1)
    // durable-events-012: staging inline payload bytes takes no store lock, so a
    // published Prepared releases the append gate exactly once, on its own
    // append. Payload publication no longer has a separate physical release.
    assert.equal(releaseCalls, 1)
    assert.equal(appendCalls, scenario === 'duplicate' ? 0 : 1)
    assert.deepEqual(appendedTypes, scenario === 'duplicate' ? []
      : [scenario === 'prepared' ? 'StrengthCandidatePrepared' : 'DelegationRequested'])
    assert.equal(fs.existsSync(lock), false)
    const bytes = fs.readFileSync(file)
    const lines = bytes.toString('utf8').trimEnd().split('\n').map(JSON.parse)
    assert.equal(bytes.toString('utf8'), lines.map(event => canonical(event) + '\n').join(''))
    assert.equal(lines.length, scenario === 'prepared' ? 3 : 1)
    if (scenario === 'duplicate') assert.deepEqual(bytes, before)
    else assert.ok(bytes.length > before.length)
    const event = sourceDto(lines.at(-1))
    const expectedType = scenario === 'prepared' ? 'StrengthCandidatePrepared' : 'DelegationRequested'
    assert.equal(event.id, H(['strength-event-v1', 'settlement', expectedType].join('\u001f')))
    assert.equal(event.type, expectedType)
    assert.equal(event.stream, 'strength/settlement')
    assert.deepEqual(EventStore.read(handle, event.id), event)
    assert.equal(EventStore.head(handle, event.stream), event.id)
    const candidate = Strength.projectionCandidate('settlement', Strength.storeCurrent(handle))
    assert.equal(candidate.state, scenario === 'prepared' ? 'Prepared' : 'Requested')
    assert.equal(candidate.request.requestedRounds, 2)
    if (scenario === 'prepared') {
      assert.equal(candidate.prepared.frameDigest, publishRequest.bundle.digest)
      const loaded = await Strength.durabilityLoadBundleForDecision(durability, Strength.storeCurrent(handle), 'settlement')
      assert.equal(loaded.ok, true)
      assert.deepEqual(loaded.value, publishRequest.bundle)
    }
    let causeSame = false
    if (result?.settlement) {
      try {
        assert.strictEqual(result.settlement.cause, injected)
        causeSame = true
      } catch (error) {
        if (error.code !== 'ERR_ASSERTION') throw error
      }
      result = {...result, settlement: {...result.settlement, cause: undefined}}
    }
    if (thrown !== undefined) assert.strictEqual(thrown, injected)
    process.stdout.write(JSON.stringify({pid: process.pid, bytes: bytes.toString('base64'), event,
      candidate, removals, releaseCalls, appendCalls, appendedTypes, causeSame,
      result: result ?? null, threw: thrown !== undefined}))
  }
} finally {
  armed = false
  fs.rmSync = originalRemove
  fs.appendFileSync = originalAppend
  syncBuiltinESMExports()
  EventStore.dispose(handle)
}
