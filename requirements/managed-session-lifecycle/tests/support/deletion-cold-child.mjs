import assert from 'node:assert/strict'
import { existsSync, readFileSync, readdirSync } from 'node:fs'
import { join } from 'node:path'
import * as eventStore from '../../../../dist/Persistence/EventStore/Surface.js'
import * as casebook from '../../../../dist/Repository/Knowledge/Casebook/Surface.js'

const [commonDir, requestJson] = process.argv.slice(2)
const request = JSON.parse(requestJson)
const decodeCase = value => ({ ...value, accessOrder: BigInt(value.accessOrder), lastAccessOrder: BigInt(value.lastAccessOrder) })
const eventsDirectory = join(commonDir, 'wanxiangshu', 'events')
const physical = () => Object.fromEntries(readdirSync(eventsDirectory).filter(name => name.endsWith('.ndjson'))
  .sort().map(name => [name, readFileSync(join(eventsDirectory, name), 'base64')]))
assert.notEqual(process.pid, request.parentPid)
assert.deepEqual(physical(), request.files)
const handle = eventStore.create(commonDir, 'd0g-independent-cold')
try {
  assert.deepEqual(await casebook.fetchCaseByIdentity(handle, request.old.identity), decodeCase(request.old))
  assert.deepEqual(await casebook.fetchCaseByIdentity(handle, request.target.identity), decodeCase(request.target))
  assert.equal(Buffer.from(await eventStore.readPayload(handle, request.payloadRef)).toString('utf8'), request.payloadBody)
  assert.deepEqual(eventStore.read(handle, request.oldEvent.id), request.oldEvent)
  const capture = eventStore.read(handle, request.captureId)
  assert.equal(capture.type, 'EngineerCaseCaptured')
  assert.equal(capture.payload.identity, request.target.identity)
  assert.equal(capture.payload.q, request.target.q)
  assert.equal(capture.payload.a, request.target.a)
  assert.deepEqual(capture.parents, [request.oldEvent.id])
  assert.deepEqual(eventStore.heads(handle, 'casebook'), [request.captureId])
  assert.equal(eventStore.head(handle, 'casebook'), request.captureId)
  assert.deepEqual(physical(), request.files)
  // durable-events-012: payloads are inline in the ndjson event line; a
  // payloads directory must never exist.
  assert.equal(existsSync(join(commonDir, 'wanxiangshu', 'payloads')), false)
  assert.equal(existsSync(join(eventsDirectory, 'd0g-independent-cold.ndjson')), false)
  assert.equal(existsSync(join(eventsDirectory, 'd0g-independent-cold-0000.ndjson')), false)
  assert.equal(existsSync(join(commonDir, 'wanxiangshu.lock')), false)
  process.stdout.write(JSON.stringify({ pid: process.pid, verified: true }) + '\n')
} finally {
  eventStore.dispose(handle)
}
