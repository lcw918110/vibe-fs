import assert from 'node:assert/strict'
import { createHash } from 'node:crypto'
import { execFileSync } from 'node:child_process'
import { mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, statSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import test from 'node:test'
import * as workspaceHost from '../../../dist/OpenCode/Host/WorkspaceSharedJournal.js'
import * as journalSurface from '../../../dist/Persistence/Journal/Surface.js'

const POISON = 'LEAVE_UNREAD_POISON_SENTINEL_NEVER_PARSE\n{not-a-journal-envelope\n'

const fingerprint = (path) => {
  const st = statSync(path)
  return { size: st.size, mtimeMs: st.mtimeMs, ino: st.ino, sha256: createHash('sha256').update(readFileSync(path)).digest('hex') }
}

const mustOk = (result) => {
  assert.equal(result.ok, true, result.ok ? '' : JSON.stringify(result.error))
  return result
}

const openRepo = (name) => {
  const workspace = mkdtempSync(join(tmpdir(), `wxs-host-es-${name}-`))
  execFileSync('git', ['init', '--quiet', workspace])
  return {
    workspace,
    commonDir: join(workspace, '.Herdchestra'),
    close: () => rmSync(workspace, { recursive: true, force: true }),
  }
}

const withRepo = async (name, fn) => {
  const opened = openRepo(name)
  try {
    await fn(opened.workspace, opened.commonDir)
  } finally {
    opened.close()
  }
}

test('WHAT[durable-events-010] SharedAgentJournal_boots_local_EventStore_and_leaves_retired_RuntimePath_ndjson_unread', async () => {
  await withRepo('boot', async (workspace, commonDir) => {
    const retiredDir = join(commonDir, 'wanxiangshu-next', 'runtimes')
    mkdirSync(retiredDir, { recursive: true })
    const stale = join(retiredDir, 'abandoned-runtime-host.ndjson')
    writeFileSync(stale, POISON)
    const before = fingerprint(stale)

    const acquired = mustOk(await journalSurface.JournalSurface_acquireSharedForWorkspace(workspace, process.pid, '2026-04-01T00:00:00Z'))
    assert.deepEqual(fingerprint(stale), before)
    assert.equal(readFileSync(stale, 'utf8'), POISON)

    mustOk(await journalSurface.JournalSurface_appendAgent(
      acquired.journal,
      { kind: 'Session', session: 'ses_host_es' },
      null,
      { family: 'Companion', case: 'TerminalOutputCaptured', payload: { SessionId: 'ses_host_es', TextRef: 'blob-term', TextDigest: 'sha-term', ProviderRun: 'run-term' } },
    ))
    assert.deepEqual(fingerprint(stale), before)

    const files = readdirSync(join(commonDir, 'wanxiangshu', 'events'))
    assert.equal(files.length >= 1, true)
    assert.equal(files.every((name) => name.endsWith('.ndjson')), true)
    assert.equal(journalSurface.JournalSurface_hasSession(acquired.journal, 'ses_host_es'), true)

    journalSurface.JournalSurface_dispose(acquired.journal)
  })
})
