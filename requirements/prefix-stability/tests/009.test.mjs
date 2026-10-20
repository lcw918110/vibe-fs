import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const xwire = await import("../../../dist/Context/Prefix/XWireSurface.js");

const textMessage = (id, role, text) => ({
  info: { id, role },
  parts: [{ type: 'text', text }],
})

test('WHAT[prefix-stability-009] transport suppression removes only exact stale Host ids', () => {
  const retryMessage = (id, text) => ({
    info: { id, role: 'user' },
    parts: [{ type: 'text', text, metadata: { wanxiangshu_origin: 'ProviderRetryAttempt' } }],
  })
  const raw = [
    textMessage('root', 'user', 'root'),
    retryMessage('retry-old', 'retry old'),
    textMessage('business-assistant', 'assistant', 'must survive'),
    retryMessage('retry-current', 'retry current'),
  ]

  const projected = xwire.suppressHostMessagesByIds(raw, ['retry-old'])

  assert.deepEqual(projected.map(item => item.info.id), ['root', 'business-assistant', 'retry-current'])
  assert.equal(projected[0], raw[0])
  assert.equal(projected[1], raw[2], 'unaddressed assistant semantics must survive as the same Host object')
  assert.equal(projected[2], raw[3], 'current retry continuation must survive as the same Host object')
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const providerCodec = await import("../../../dist/OpenCode/Codec/ProviderProjectionSurface.js");
const providerProjection = await import("../../../dist/Participant/Provider/Projection/Surface.js");
const XWireSurface = await import("../../../dist/Context/Prefix/XWireSurface.js");

const semanticView = (raw) => providerProjection.semanticProjection(providerCodec.decodeMessageView(raw).messages)
const stage2Snapshot = (raw, committed = null) => ({
  currentProjection: semanticView(raw),
  committedPrefix: committed,
})

test('WHAT[prefix-stability-009] CTX_011_step5_cutoff_digest_truncates_exactly_at_the_cutoff', () => {
  const snapshot = stage2Snapshot([
    { info: { id: 'm1', role: 'user' }, parts: [{ type: 'text', text: 'first' }] },
    { info: { id: 'm2', role: 'assistant' }, parts: [{ type: 'text', text: 'second' }] },
    { info: { id: 'm3', role: 'user' }, parts: [{ type: 'text', text: 'third' }] },
  ])

  const truncated = {
    ...snapshot.currentProjection,
    messages: snapshot.currentProjection.messages.slice(0, 2),
  }
  assert.equal(
    XWireSurface.coveredPrefixDigest(snapshot.currentProjection, 2),
    XWireSurface.coveredPrefixDigest(truncated, 2),
  )
  assert.notEqual(
    XWireSurface.coveredPrefixDigest(snapshot.currentProjection, 2),
    XWireSurface.coveredPrefixDigest(snapshot.currentProjection, 99),
    'a real cutoff changes the digest',
  )

  // cutoff 0 proves the EMPTY prefix — the load-bearing CTX-011 step-5 shape.
  const empty = { ...snapshot.currentProjection, messages: [] }
  assert.equal(
    XWireSurface.coveredPrefixDigest(snapshot.currentProjection, 0),
    XWireSurface.coveredPrefixDigest(empty, 0),
  )

  // An out-of-range cutoff keeps every message; the selector clamps before this
  // proof is requested, but the proof itself remains total.
  assert.equal(
    XWireSurface.coveredPrefixDigest(snapshot.currentProjection, 99),
    XWireSurface.coveredPrefixDigest(snapshot.currentProjection, snapshot.currentProjection.messages.length),
  )
})
test('WHAT[prefix-stability-009] CTX_011_step5_the_proof_reads_the_SNAPSHOT_not_a_stale_closure', () => {
  // The digest must be recomputed from X's CURRENT projection each attempt — a
  // closure captured once would re-prove yesterday's numbering.
  const before = stage2Snapshot([
    { info: { id: 'm1', role: 'user' }, parts: [{ type: 'text', text: 'old' }] },
  ])
  const after = stage2Snapshot([
    { info: { id: 'm1', role: 'user' }, parts: [{ type: 'text', text: 'old' }] },
    { info: { id: 'm2', role: 'user' }, parts: [{ type: 'text', text: 'new' }] },
  ])

  // Same cutoff over a 1-message and a 2-message projection: the grown one keeps
  // its second message, so the proof cannot be the same.
  assert.notEqual(
    XWireSurface.coveredPrefixDigest(before.currentProjection, 2),
    XWireSurface.coveredPrefixDigest(after.currentProjection, 2),
    'the same cutoff over a grown projection must not produce the same proof',
  )
})
test.todo('WHAT[prefix-stability-009] actual Host transform derives proof and writeback identities from canonical XTrace despite request-local presentation changes; GAP-106')
}
