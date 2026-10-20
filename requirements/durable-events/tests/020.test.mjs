import assert from 'node:assert/strict'
import test from 'node:test'
import { existsSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import * as journal from '../../../dist/Persistence/Journal/Surface.js'
import * as workspaceStore from '../../../dist/OpenCode/Host/WorkspaceEventStoreSurface.js'

async function withStoreDirectory(scenario) {
  const root = mkdtempSync(join(tmpdir(), 'wxs-durable-activation-'))
  try {
    await scenario(root)
  } finally {
    rmSync(root, { recursive: true, force: true })
  }
}

test('WHAT[durable-events-020] Journal boot creates no writer until the first business append', async () => {
  await withStoreDirectory(async (commonDir) => {
    const booted = await journal.JournalSurface_bootWithWriterId(commonDir, 'boot-proof', 'runtime-proof', 6001, '9999-05-01T00:00:00Z')
    assert.equal(booted.ok, true)
    try {
      const writer = join(commonDir, 'wanxiangshu/events/boot-proof-0000.ndjson')
      assert.equal(Number(booted.localSeq), 1)
      assert.equal(existsSync(writer), false)
      const appended = await journal.JournalSurface_appendAgent(booted.journal, { kind: 'Session', session: 'session-proof' }, null, {
        family: 'Companion',
        case: 'TerminalOutputCaptured',
        payload: { SessionId: 'session-proof', TextRef: 'blob-term', TextDigest: 'sha-term', ProviderRun: 'run-term' },
      })
      assert.equal(appended.ok, true)
      const lines = readFileSync(writer, 'utf8').trimEnd().split('\n').map(JSON.parse)
      assert.ok(lines.length >= 2, 'activation persists the runtime watermark and the business fact')
      assert.equal(journal.JournalSurface_hasSession(booted.journal, 'session-proof'), true)
    } finally {
      journal.JournalSurface_dispose(booted.journal)
    }
  })
})

test('WHAT[durable-events-020] workspace capability acquisition defers malformed-history rejection until actual consumption', async () => {
  await withStoreDirectory(async (commonDir) => {
    const events = join(commonDir, 'wanxiangshu/events')
    mkdirSync(events, { recursive: true })
    const writer = join(events, 'invalid.ndjson')
    const bytes = '{invalid retained event}\n'
    writeFileSync(writer, bytes)
    const store = workspaceStore.acquire(commonDir)
    try {
      assert.deepEqual(readdirSync(events), ['invalid.ndjson'])
      assert.equal(readFileSync(writer, 'utf8'), bytes)
      assert.throws(() => workspaceStore.activate(store), /MalformedEnvelope/)
      assert.equal(readFileSync(writer, 'utf8'), bytes, 'failed activation must not repair the historical file')
      assert.deepEqual(readdirSync(events), ['invalid.ndjson'])
    } finally {
      workspaceStore.release(commonDir)
    }
  })
})

test('WHAT[durable-events-020] activated replay ignores the recognized legacy Journal fact without writing a cut', async () => {
  await withStoreDirectory(async (commonDir) => {
    const eventId = 'e'.repeat(40)
    const legacy = {
      event_id: eventId,
      event_type: 'JournalEnvelope',
      parents: [],
      payload: {
        EventId: ['EventId', eventId],
        Fact: ['Agent', ['Fallback', ['PluginPromptAccepted', {}]]],
        LocalSeq: ['LocalSeq', '1'],
        ObservedAt: '9999-01-01T00:00:00.000+00:00',
        RuntimeId: ['RuntimeId', 'runtime-legacy'],
        Stream: 'Workspace',
      },
      payload_refs: [],
      stream_id: 'journal/workspace',
    }
    const events = join(commonDir, 'wanxiangshu/events')
    mkdirSync(events, { recursive: true })
    const bytes = `${JSON.stringify(legacy)}\n`
    writeFileSync(join(events, 'legacy.ndjson'), bytes)
    const booted = await journal.JournalSurface_bootWithWriterId(commonDir, 'modern-writer', 'runtime-modern', 7001, '9999-05-02T00:00:00Z')
    assert.equal(booted.ok, true)
    try {
      assert.equal(journal.JournalSurface_hasSession(booted.journal, 'missing-session'), false)
      assert.deepEqual(readdirSync(events), ['legacy.ndjson'])
      assert.equal(readFileSync(join(events, 'legacy.ndjson'), 'utf8'), bytes)
    } finally {
      journal.JournalSurface_dispose(booted.journal)
    }
  })
})

test.todo('WHAT[durable-events-020] actual plugin load checks readability without replay repair or writes; first durable consumer activates once')
