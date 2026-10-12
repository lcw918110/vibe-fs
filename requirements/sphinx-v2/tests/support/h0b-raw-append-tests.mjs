import assert from 'node:assert/strict'
import { randomUUID } from 'node:crypto'
import { readFileSync, readdirSync } from 'node:fs'
import { join } from 'node:path'
import test from 'node:test'
import { fileURLToPath } from 'node:url'
import * as codec from '../../../../dist/Persistence/EventStore/CodecSurface.js'
import { runVerificationToolProbe } from '../../../../scripts/lib/verification-tool-probe.mjs'
import {
  store, persistence, batch, append, current, mustOk, withStore, digest,
} from '../persistence-support.mjs'

const childPath = fileURLToPath(new URL('./h0b-raw-append-child.mjs', import.meta.url))
const repoPath = fileURLToPath(new URL('../../../../', import.meta.url))
const environment = () => {
  const env = { ...process.env }
  delete env.NODE_TEST_CONTEXT
  return env
}
const filesAt = commonDir => {
  const directory = join(commonDir, 'wanxiangshu', 'events')
  return Object.fromEntries(readdirSync(directory).sort().map(name => [name, readFileSync(join(directory, name)).toString('base64')]))
}
const expectFacts = (commonDir, facts) => {
  const files = filesAt(commonDir)
  assert.deepEqual(Object.values(files).sort(), facts.map(fact => Buffer.from(codec.encode(fact), 'utf8').toString('base64')).sort(),
    'Each actual writer owns one complete canonical fact; there are no extra intents or cut tails')
  return files
}
const coldObservation = async (t, commonDir, inquiry, base, facts) => {
  const before = expectFacts(commonDir, facts)
  const result = JSON.parse(await runVerificationToolProbe(process.execPath, [childPath, 'cold', JSON.stringify({
    commonDir, writerId: `h0b-cold-${randomUUID()}`, inquiry, stream: base.stream, ids: facts.map(fact => fact.id),
  })], { cwd: repoPath, env: environment(), signal: t.signal }))
  assert.notEqual(result.pid, process.pid)
  assert.deepEqual(result.events, facts)
  assert.deepEqual(result.heads, facts.slice(1).map(fact => fact.id).sort())
  assert.equal(result.head, null)
  assert.deepEqual(result.current, {
    ok: false,
    error: { code: 'DomainConflict', stream: base.stream, heads: result.heads },
  }, 'The original canonical replay preserves the legal fork and refuses to select a dispatch winner')
  assert.equal(result.writerFileExists, false, 'A cold query does not open a new writer')
  assert.deepEqual(filesAt(commonDir), before, 'Independent cold replay is read-only')
  return result
}
const expectPending = (observed, request) => {
  assert.deepEqual(mustOk(observed).physicalBindings, [{
    key: request.payload.dispatchIntentId, value: { request: request.payload, receipt: null },
  }])
}

export function registerH0bRawAppendTests({ inquiry, seed, request }) {
  test('WHAT[sphinx-v2-010] two stale handles can both append the same intent under different commands and cold replay retains the legal fork', async t => {
    await withStore(async ({ open, close, commonDir }) => {
      const seedWriter = open()
      const base = await append(seedWriter, batch(inquiry, 'h0b-handle-seed', seed()))
      close(seedWriter)
      const leftWriter = open()
      const rightWriter = open()
      const before = current(leftWriter, inquiry)
      assert.deepEqual(current(rightWriter, inquiry), before)
      assert.deepEqual(mustOk(before).physicalBindings, [])
      assert.deepEqual(store.heads(leftWriter, base.stream), [base.id])
      assert.deepEqual(store.heads(rightWriter, base.stream), [base.id])
      const left = mustOk(persistence.prepareTransition(leftWriter, digest, batch(inquiry, 'h0b-handle-left', [request], base)))
      const right = mustOk(persistence.prepareTransition(rightWriter, digest, batch(inquiry, 'h0b-handle-right', [request], base)))
      assert.notEqual(left.id, right.id)
      for (const event of [left, right]) {
        assert.deepEqual(event.parents, [base.id])
        assert.deepEqual(event.payload.events, [request])
      }
      assert.deepEqual(current(leftWriter, inquiry), before, 'Pure seal does not publish the intent')
      assert.deepEqual(current(rightWriter, inquiry), before)
      assert.deepEqual(await store.append(leftWriter, [left]), { ok: true, cuts: [] })
      expectPending(current(leftWriter, inquiry), request)
      assert.deepEqual(current(rightWriter, inquiry), before, 'The second actual handle still owns its original Current')
      assert.equal(store.read(rightWriter, left.id), null)
      const afterLeft = filesAt(commonDir)
      assert.deepEqual(await store.append(leftWriter, [left]), { ok: true, cuts: [] })
      assert.deepEqual(filesAt(commonDir), afterLeft, 'The same raw success shape also represents exact no-new-write replay')
      assert.deepEqual(await store.append(rightWriter, [right]), { ok: true, cuts: [] })
      expectPending(current(rightWriter, inquiry), request)
      close(leftWriter)
      close(rightWriter)
      const cold = await coldObservation(t, commonDir, inquiry, base, [base, left, right])
      t.diagnostic(JSON.stringify({ kind: 'raw-append-capability-boundary', mode: 'two-handles', parentPid: process.pid,
        coldPid: cold.pid, eventIds: [left.id, right.id], heads: cold.heads }))
    })
  })

  test('WHAT[sphinx-v2-010] two actual processes seal the same base before either raw append and independent cold replay retains both intent branches', async t => {
    await withStore(async ({ open, close, commonDir }) => {
      const seedWriter = open()
      const base = await append(seedWriter, batch(inquiry, 'h0b-process-seed', seed()))
      const before = current(seedWriter, inquiry)
      assert.deepEqual(mustOk(before).physicalBindings, [])
      close(seedWriter)
      const identity = randomUUID()
      const inputs = ['left', 'right'].map(side => ({
        commonDir, writerId: `h0b-${side}-${identity}`, inquiry,
        raw: batch(inquiry, `h0b-process-${side}`, [request], base),
      }))
      const measured = JSON.parse(await runVerificationToolProbe(process.execPath, [childPath, 'pair', JSON.stringify({ inputs })], {
        cwd: repoPath, env: environment(), signal: t.signal,
      }))
      assert.notEqual(measured.pid, process.pid)
      assert.equal(new Set(measured.sealed.map(record => record.pid)).size, 2)
      assert.deepEqual(measured.trace.map(record => record.phase), ['sealed', 'sealed', 'go', 'go', 'appended', 'appended'])
      const branches = measured.sealed.map(record => record.event)
      assert.notEqual(branches[0].id, branches[1].id)
      for (let index = 0; index < inputs.length; index++) {
        const sealed = measured.sealed[index]
        const completed = measured.appended[index]
        assert.equal(sealed.writerId, inputs[index].writerId)
        assert.notEqual(sealed.pid, process.pid)
        assert.notEqual(sealed.pid, measured.pid)
        assert.deepEqual(sealed.before, before)
        assert.deepEqual(sealed.heads, [base.id])
        assert.equal(sealed.writerFileExists, false, 'The real contender has not opened its writer before the common go barrier')
        assert.equal(sealed.event.payload.commandId, inputs[index].raw.commandId)
        assert.deepEqual(sealed.event.parents, [base.id])
        assert.deepEqual(sealed.event.payload.events, [request])
        assert.equal(completed.pid, sealed.pid)
        assert.equal(completed.writerId, sealed.writerId)
        assert.deepEqual(completed.event, sealed.event)
        assert.deepEqual(completed.receipt, { ok: true, cuts: [] })
        assert.equal(completed.writerBytes, Buffer.from(codec.encode(sealed.event), 'utf8').toString('base64'))
        const allFiles = filesAt(commonDir)
        const writtenBytes = allFiles[`${sealed.writerId}.ndjson`] ?? allFiles[`${sealed.writerId}-0000.ndjson`]
        assert.equal(writtenBytes, completed.writerBytes,
          'Each actual OS contender appended its original complete canonical bytes to its own writer')
        expectPending(completed.current, request)
        assert.deepEqual(completed.heads, [sealed.event.id])
      }
      assert.deepEqual(measured.closed, [{ code: 0, signal: null }, { code: 0, signal: null }])
      const cold = await coldObservation(t, commonDir, inquiry, base, [base, ...branches])
      assert.notEqual(cold.pid, measured.pid)
      for (const record of measured.sealed) assert.notEqual(cold.pid, record.pid)
      t.diagnostic(JSON.stringify({ kind: 'raw-append-capability-boundary', mode: 'two-processes', parentPid: process.pid,
        coordinatorPid: measured.pid, writerPids: measured.sealed.map(record => record.pid), coldPid: cold.pid,
        eventIds: branches.map(event => event.id), trace: measured.trace, heads: cold.heads }))
    })
  })
}
