import assert from 'node:assert/strict'
import test from 'node:test'
import * as attachment from '../../../dist/Execution/Session/Attachment/AttachmentSurface.js'

test('WHAT[managed-session-lifecycle-001] one attachment owner binds current Engineer requests once', async () => {
  const observed = await attachment.scenario('owner', 'Engineer', 'engineer', 'engineer', true)
  assert.deepEqual(observed, {
    owner: 'owner', role: 'engineer', created: 1,
    firstChild: 'child-1', secondChild: 'child-1',
    firstAgent: 'engineer', secondAgent: 'engineer',
  })
})

test('WHAT[managed-session-lifecycle-001] unknown role input cannot silently exercise another role', async () => {
  await assert.rejects(attachment.scenario('owner', 'unknown', 'engineer', 'engineer', true), /Unknown attachment role/)
})

test('WHAT[managed-session-lifecycle-001] all AttachmentKinds use the same creation, recovery and cleanup owner through public Host paths (GAP-133)', async () => {
  const observed = await attachment.everyKindScenario('owner')
  const [inspectorChild, coderChild, companionChild] = [
    observed.inspectorChild, observed.coderChild, observed.companionChild,
  ]

  // Every kind was established, but each got its own child
  assert.equal(new Set([inspectorChild, coderChild, companionChild]).size, 3)
  assert.deepEqual(observed.children.slice().sort(), [inspectorChild, coderChild, companionChild].sort())

  // One shared registry records every kind against ONE lifecycle owner: the reverse
  // lookup answers which kind holds each child, and every binding names this owner.
  assert.deepEqual(observed.bindings.map(entries => entries.length), [1, 1, 1])
  assert.deepEqual(
    observed.bindings.flat().map(([owner]) => owner),
    ['owner', 'owner', 'owner'],
  )
  const kinds = observed.bindings.flat().map(([, kind]) => kind).sort()
  assert.deepEqual(kinds, ['companion', 'sync-coder', 'sync-inspector'])
  assert.deepEqual(observed.bindings.map(entries => entries[0][1]).sort(), ['companion', 'sync-coder', 'sync-inspector'])
  assert.equal(observed.registrySize, 3)

  // Recovery through the same owner: the Sync kind keeps the agent bound at create
  // time, the Companion kind reuses instead of minting a second child, and the
  // read-only replica keeps its one resident child per owner.
  assert.equal(observed.coderReuseAgent, 'coder')
  assert.equal(observed.coderReuseChild, coderChild)
  assert.equal(observed.companionReuseChild, companionChild)
  assert.equal(observed.companionReuseOrigin, 'Reused')
  assert.equal(observed.companionOrigin, 'Created')
  assert.equal(observed.companionReuseLinked, 1)
  assert.equal(observed.companionClosedCount, 0)

  // Scope isolation survives the collapse: the owner's own scope resolves to its
  // binding while a different scope resolves to nothing.
  assert.equal(observed.syncInspectorScopeChild, inspectorChild)
  assert.equal(observed.otherScopeChild, '')
})
