import assert from 'node:assert/strict'
import { existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, renameSync, rmSync, symlinkSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import test from 'node:test'
import * as store from '../../../dist/Persistence/EventStore/Surface.js'
import * as retention from '../../../dist/Persistence/EventStore/RetentionSurface.js'
import * as codec from '../../../dist/Persistence/EventStore/CodecSurface.js'

const event = (ordinal, text, parents = []) => ({
  id: ordinal.toString(16).padStart(40, '0'),
  stream: 'writer/cache-reselection',
  type: 'JobRequested',
  parents,
  payload: { text },
  payloadRefs: [],
})

test('an active store extends its legacy file and reselects the new series after test-only reorganization removes that cache target', async () => {
  const root = mkdtempSync(join(tmpdir(), 'wxs-writer-reselection-'))
  const stateRoot = join(root, '.Herdchestra')
  const events = join(stateRoot, 'wanxiangshu', 'events')
  const writerId = 'active-legacy-writer'
  const legacy = join(events, `${writerId}.ndjson`)
  const cap = 512
  let active
  try {
    mkdirSync(events, { recursive: true })
    const first = event(1, 'first legacy line '.repeat(40))
    const second = event(2, 'second legacy line '.repeat(40), [first.id])
    const third = event(3, 'append after the cached legacy file was removed', [second.id])
    const firstBytes = Buffer.from(codec.encode(first))
    const secondBytes = Buffer.from(codec.encode(second))
    const thirdBytes = Buffer.from(codec.encode(third))
    assert.ok(firstBytes.length > cap)
    assert.ok(secondBytes.length > cap)
    writeFileSync(legacy, firstBytes)

    active = store.createWithSegmentCap(stateRoot, writerId, cap)
    assert.deepEqual(await store.append(active, [second]), { ok: true, cuts: [] })
    assert.deepEqual(readdirSync(events), [`${writerId}.ndjson`], 'legacy appends stay in place even beyond the segment cap')
    assert.deepEqual(readFileSync(legacy), Buffer.concat([firstBytes, secondBytes]))

    const reorganized = await retention.reorganizeWithSegmentCap(stateRoot, cap)
    assert.deepEqual(reorganized, { ok: true })
    assert.equal(existsSync(legacy), false)
    const zero = join(events, `${writerId}-0000.ndjson`)
    const one = join(events, `${writerId}-0001.ndjson`)
    assert.deepEqual(readFileSync(zero), firstBytes)
    assert.deepEqual(readFileSync(one), secondBytes)

    assert.deepEqual(await store.append(active, [third]), { ok: true, cuts: [] })
    assert.deepEqual(readdirSync(events).sort(), [
      `${writerId}-0000.ndjson`, `${writerId}-0001.ndjson`, `${writerId}-0002.ndjson`,
    ])
    assert.equal(existsSync(legacy), false, 'the same live writer must not recreate its vanished cached legacy name')
    assert.deepEqual(readFileSync(zero), firstBytes)
    assert.deepEqual(readFileSync(one), secondBytes)
    assert.deepEqual(readFileSync(join(events, `${writerId}-0002.ndjson`)), thirdBytes,
      'the highest replacement segment is full, so continuation rolls from 0001 to 0002')
    assert.deepEqual(store.read(active, third.id), third)
    assert.deepEqual(store.heads(active, third.stream), [third.id])
  } finally {
    if (active) store.dispose(active)
    rmSync(root, { recursive: true, force: true })
  }
})

test('the original writer rechecks a replaced events directory for Git paths while empty append remains a no-op', () => {
  const root = mkdtempSync(join(tmpdir(), 'wxs-writer-directory-swap-'))
  const stateRoot = join(root, '.Herdchestra')
  const events = join(stateRoot, 'wanxiangshu', 'events')
  const savedEvents = join(root, 'saved-events')
  const gitTarget = join(root, '.git', 'writer-target')
  const writerId = 'directory-swap-writer'
  try {
    const original = store.createPhysicalWriter(stateRoot, writerId)
    const first = event(1, 'accepted before the physical directory changes')
    const next = event(2, 'must not enter Git metadata', [first.id])
    const written = store.appendPhysicalWriter(original, [first])
    assert.deepEqual(written, { ok: true, completion: 'AppendDurable' })
    assert.equal(store.physicalWriterPath(original), join(events, `${writerId}-0000.ndjson`))
    const before = readFileSync(store.physicalWriterPath(original))

    mkdirSync(gitTarget, { recursive: true })
    const marker = join(gitTarget, 'preserve.txt')
    const markerBytes = Buffer.from('existing Git-side bytes\n')
    writeFileSync(marker, markerBytes)
    renameSync(events, savedEvents)
    symlinkSync(gitTarget, events, 'dir')

    assert.throws(() => store.physicalWriterPath(original), /\.git/)
    const refused = store.appendPhysicalWriter(original, [next])
    assert.equal(refused.ok, false)
    assert.equal(refused.kind, 'BeforeAppend')
    assert.equal(refused.fault.phase, 'BeforePhysicalAppend')
    assert.match(refused.fault.cause.message, /\.git/)

    const empty = store.appendPhysicalWriter(original, [])
    assert.deepEqual(empty, { ok: true, completion: 'NoAppend' })
    assert.deepEqual(readdirSync(gitTarget), ['preserve.txt'])
    assert.deepEqual(readFileSync(marker), markerBytes)
    assert.deepEqual(readFileSync(join(savedEvents, `${writerId}-0000.ndjson`)), before)
  } finally {
    rmSync(root, { recursive: true, force: true })
  }
})
