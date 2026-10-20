import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { mkdirSync, mkdtempSync, rmSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join } = await import("node:path");
const { default: test } = await import("node:test");
const eventStore = await import("../../../dist/Persistence/EventStore/Surface.js");
const casebook = await import("../../../dist/Repository/Knowledge/Casebook/Surface.js");
const transaction = await import("../../../dist/Repository/Programming/Js/TransactionSurface.js");

const structuralEvent = {
  id: '7'.repeat(40),
  stream: 'integrator-parity/proof',
  type: 'JobRequested',
  parents: [],
  payload: { proof: 'replayed' },
  payloadRefs: [],
}
const mustOk = (result, label) => {
  assert.equal(result.ok, true, `${label}: ${JSON.stringify(result.error)}`)
  return result
}
const observe = async (store) => {
  const caseResult = mustOk(await casebook.fetchCase(store, 10, 'parity-case'), 'read Casebook Current')

  return {
    structuralHead: eventStore.head(store, structuralEvent.stream),
    structuralPayload: eventStore.read(store, structuralEvent.id)?.payload?.proof ?? null,
    caseAnswer: caseResult.value?.a ?? null,
    pendingTransactions: transaction.pending(store).map(({ transactionId }) => transactionId).sort(),
  }
}

test('WHAT[durable-convergence-007] retained rich history rebuilds the exact live production Current', async () => {
  const root = mkdtempSync(join(tmpdir(), 'wxs-integrator-parity-'))
  const commonDir = join(root, '.Herdchestra')
  mkdirSync(commonDir, { recursive: true })

  try {
    const liveStore = eventStore.create(commonDir, 'parity-live')
    let live

    try {
      mustOk(await eventStore.append(liveStore, [structuralEvent]), 'append Structural fact')
      mustOk(
        await casebook.archive(liveStore, {
          sessionId: 'parity-case',
          q: 'Q',
          a: 'replayed answer',
          observations: [],
          lastAccessOrder: 0,
        }),
        'append Casebook fact',
      )
      mustOk(
        await transaction.appendPrepared(liveStore, {
          transactionId: 'parity-transaction',
          workspaceRoot: '/parity-workspace',
          mutation: { kind: 'rewrite', path: 'a.txt', originalText: 'before', newText: 'after' },
        }),
        'append JsTransaction fact',
      )
      live = await observe(liveStore)
    } finally {
      eventStore.dispose(liveStore)
    }

    assert.deepEqual(live, {
      structuralHead: structuralEvent.id,
      structuralPayload: 'replayed',
      caseAnswer: 'replayed answer',
      pendingTransactions: ['parity-transaction'],
    })

    const replayStore = eventStore.create(commonDir, 'parity-replay')
    try {
      assert.deepEqual(await observe(replayStore), live)
    } finally {
      eventStore.dispose(replayStore)
    }
  } finally {
    rmSync(root, { recursive: true, force: true })
  }
})
}

test.todo('WHAT[durable-convergence-007] independently synchronized replicas agree on nonempty production Current for every registered business oracle (GAP-151)')
