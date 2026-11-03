import assert from 'node:assert/strict'
import { createHash } from 'node:crypto'
import { mkdirSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import test from 'node:test'
import { sandbox, casebook, eventStore } from './support/casebook.mjs'

test('capture and compare preserve the complete canonical baseline bytes and path-key order', async () => {
  const local = sandbox()
  try {
    const bytes = Buffer.from('\uFEFFcanonical bytes\r\nlast line', 'utf8')
    const hash = createHash('sha256').update(bytes).digest('hex')
    for (const path of ['2', '10', '02', 'A.txt', 'a.txt', 'z.txt', 'é.txt']) {
      writeFileSync(join(local.dir, path), bytes)
    }
    const paths = ['é.txt', 'z.txt', '2', 'missing.txt', 'A.txt', '10', 'a.txt', '02', 'z.txt', 'missing.txt']
    const entry = `{"kind":"Present","payloadRef":"${hash}","payload_ref":"${hash}","sha256":"${hash}","contentHash":"${hash}"}`
    const expected = `{"2":${entry},"10":${entry},"02":${entry},"A.txt":${entry},"a.txt":${entry},"missing.txt":{"kind":"Missing"},"z.txt":${entry},"é.txt":${entry}}`

    const baseline = await casebook.freezeCompletionState(local.store, local.dir, paths)
    assert.equal(baseline, expected)
    assert.deepEqual(Buffer.from(await eventStore.readPayload(local.store, hash)), bytes)
    const compared = await casebook.computeMaintenanceDiff(local.store, local.dir, paths, baseline)
    assert.deepEqual(compared, { hasDiff: false, diffSummary: '', targetState: expected })
  } finally {
    local.close()
  }
})

test('baseline decoding ignores extra fields and conflicting aliases while requiring the canonical fields', async () => {
  const local = sandbox()
  try {
    const bytes = Buffer.from('stored baseline\n', 'utf8')
    const hash = createHash('sha256').update(bytes).digest('hex')
    writeFileSync(join(local.dir, 'subject.txt'), bytes)
    const captured = await casebook.freezeCompletionState(local.store, local.dir, ['subject.txt', 'missing.txt'])
    assert.equal(typeof captured, 'string')
    const legacy = `{"subject.txt":{"kind":"Present","payloadRef":"${hash}","payload_ref":"unused alias reference","sha256":"${hash}","contentHash":"unused alias digest","extra":{"ignored":true}},"missing.txt":{"kind":"Missing","sha256":false,"payloadRef":null,"extra":"ignored"}}`
    const expected = `{"missing.txt":{"kind":"Missing"},"subject.txt":{"kind":"Present","payloadRef":"${hash}","payload_ref":"${hash}","sha256":"${hash}","contentHash":"${hash}"}}`
    const compared = await casebook.computeMaintenanceDiff(local.store, local.dir, ['missing.txt', 'subject.txt'], legacy)
    assert.deepEqual(compared, { hasDiff: false, diffSummary: '', targetState: expected })

    const missingCanonical = [
      { entry: `{"kind":"Present","contentHash":"${hash}","payload_ref":"${hash}"}`, field: 'sha256' },
      { entry: `{"kind":"Present","sha256":"${hash}","payload_ref":"${hash}"}`, field: 'payloadRef' },
    ]
    for (const value of missingCanonical) {
      const result = await casebook.computeMaintenanceDiff(local.store, local.dir, ['subject.txt'], `{"subject.txt":${value.entry}}`)
      assert.equal(result.ok, false)
      assert.equal(result.error,
        `invalid maintenance baseline: Error at: \`$\`\nExpecting an object with a field named \`${value.field}\` but instead got:\n${JSON.stringify(JSON.parse(value.entry), null, 4)}`)
    }
  } finally {
    local.close()
  }
})

test('baseline capture resolves relative and absolute paths while retaining the caller path keys', async () => {
  const local = sandbox()
  try {
    const root = join(local.dir, 'workspace')
    mkdirSync(root)
    const outside = join(local.dir, 'outside.txt')
    const insideBytes = Buffer.from('inside\n', 'utf8')
    const outsideBytes = Buffer.from('outside\n', 'utf8')
    const nextBytes = Buffer.from('changed outside\r\n', 'utf8')
    writeFileSync(join(root, 'inside.txt'), insideBytes)
    writeFileSync(outside, outsideBytes)
    const insideHash = createHash('sha256').update(insideBytes).digest('hex')
    const outsideHash = createHash('sha256').update(outsideBytes).digest('hex')
    const nextHash = createHash('sha256').update(nextBytes).digest('hex')
    const insideEntry = `{"kind":"Present","payloadRef":"${insideHash}","payload_ref":"${insideHash}","sha256":"${insideHash}","contentHash":"${insideHash}"}`
    const outsideEntry = `{"kind":"Present","payloadRef":"${outsideHash}","payload_ref":"${outsideHash}","sha256":"${outsideHash}","contentHash":"${outsideHash}"}`
    const nextEntry = `{"kind":"Present","payloadRef":"${nextHash}","payload_ref":"${nextHash}","sha256":"${nextHash}","contentHash":"${nextHash}"}`
    const absoluteKey = JSON.stringify(outside)
    const paths = [outside, './inside.txt', '../outside.txt']
    const expected = `{"../outside.txt":${outsideEntry},"./inside.txt":${insideEntry},${absoluteKey}:${outsideEntry}}`

    const baseline = await casebook.freezeCompletionState(local.store, root, paths)
    assert.equal(baseline, expected)
    assert.deepEqual(await casebook.computeMaintenanceDiff(local.store, root, paths, baseline), {
      hasDiff: false, diffSummary: '', targetState: expected,
    })
    writeFileSync(outside, nextBytes)
    const compared = await casebook.computeMaintenanceDiff(local.store, root, paths, baseline)
    assert.equal(compared.hasDiff, true)
    assert.equal(compared.targetState, `{"../outside.txt":${nextEntry},"./inside.txt":${insideEntry},${absoluteKey}:${nextEntry}}`)
    assert.ok(compared.diffSummary.includes('../outside.txt'), compared.diffSummary)
    assert.ok(compared.diffSummary.includes(outside), compared.diffSummary)
    assert.deepEqual(Buffer.from(await eventStore.readPayload(local.store, nextHash)), nextBytes)
    assert.deepEqual(Buffer.from(await eventStore.readPayload(local.store, outsideHash)), outsideBytes)
  } finally {
    local.close()
  }
})
