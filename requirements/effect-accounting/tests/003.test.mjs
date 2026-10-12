import assert from 'node:assert/strict'
import test from 'node:test'
import { withDispatch, dispatch, journal } from './support/dispatch.mjs'

test('WHAT[effect-accounting-003] physical send sees its exact intent already recoverable from the production journal', async () => {
  await withDispatch(async ({ open, send }) => {
    let sends = 0
    const result = await send({
      SendPrompt: async (_session, _text, options) => {
        const reopened = await open('observer-writer')
        const claims = dispatch.projectionObservation(reopened, 'child').pendingClaims
        assert.equal(claims.length, 1)
        assert.equal(claims[0].promptKey, options.Metadata.wanxiangshu_prompt_key)
        sends += 1
        return dispatch.admittedWithReceipt('transport-receipt')
      },
    })
    assert.equal(result.ok, true, result.error)
    assert.equal(sends, 1)
  })
})

test('WHAT[effect-accounting-003] a disposed journal handle prevents the physical send', async () => {
  await withDispatch(async ({ handle, send }) => {
    journal.JournalSurface_dispose(handle)
    let sends = 0
    await assert.rejects(() => send({ SendPrompt: async () => {
      sends += 1
      return dispatch.admittedWithReceipt('must-not-happen')
    } }), /Journal handle is disposed/)
    assert.equal(sends, 0)
  })
})

// D03 (2026-10-04): the old "provider todo mutation" half of the removed TODO
// is retired by the Host-owned todowrite ruling — WHAT[008] and the existing
// effect-accounting-008 tests own the live boundary (checkpoint only after the
// Host reports the exact completed ToolPart). The worktree half is proven here
// against the real ForkManagerJob chain.
{
const { default: assert } = await import('node:assert/strict')
const { existsSync, mkdtempSync, readFileSync, rmSync } = await import('node:fs')
const { tmpdir } = await import('node:os')
const { join } = await import('node:path')
const { default: test } = await import('node:test')
const hostSurface = await import('../../../dist/Change/Host/Surface.js')

const openJournal = (base, writer) =>
  journal.JournalSurface_bootWithWriterId(base, writer, `rt-${writer}`, 4242, '2026-01-01T00:00:00Z')

const eventsFile = (base, writer) => {
  const eventsDir = join(base, 'wanxiangshu', 'events')
  const legacy = join(eventsDir, `${writer}.ndjson`)
  if (existsSync(legacy)) return legacy
  return join(eventsDir, `${writer}-0000.ndjson`)
}

const worktreeCases = (base, writer) => {
  const file = eventsFile(base, writer)
  if (!existsSync(file)) return []
  return readFileSync(file, 'utf8')
    .trim().split('\n').filter(Boolean)
    .map((line) => JSON.parse(line).payload.Fact)
    .filter((fact) => Array.isArray(fact) && fact[0] === 'Agent' && Array.isArray(fact[1]) && fact[1][0] === 'Orchestrator')
    .map((fact) => fact[1][1][0])
}

const failingSessions = () => ({
  SubscribeTerminal: () => ({ Dispose() {} }),
  SubscribeFutureTerminal: () => ({ Dispose() {} }),
  CreateChildSession: async () => ({ ok: false, error: 'manager session host unavailable' }),
})

test('WHAT[effect-accounting-003] worktree creation is blocked when the WorktreeCreateRequested intent cannot be committed', async () => {
  const base = mkdtempSync(join(tmpdir(), 'wxs-effect003-block-'))
  try {
    const writer = 'writer-effect003-block'
    const opened = await openJournal(base, writer)
    assert.equal(opened.ok, true, JSON.stringify(opened.error))

    let createCalls = 0
    const git = {
      IsDirty: async () => ({ ok: true, value: false }),
      FreezeTargetBranch: async () => ({ ok: true, value: 'refs/heads/main' }),
      ListManagerBranches: async () => ({ ok: true, value: [] }),
      DeleteBranch: async () => ({ ok: true }),
      HasRebaseHead: async () => false,
      ReadHead: async () => ({ ok: true, value: 'head' }),
      GetTargetHead: async () => ({ ok: true, value: 'target-head' }),
      CreateWorktree: async () => {
        createCalls += 1
        return { ok: true, value: `manager/${writer}` }
      },
      ListWorktrees: async () => ({ ok: true, value: [] }),
      RemoveWorktree: async () => ({ ok: true }),
    }
    const host = hostSurface.create({
      sessions: failingSessions(),
      journal: opened.journal,
      gitPort: git,
      repoPath: base,
      targetBranch: 'refs/heads/main',
      orchestratorId: 'ses_orchestrator_effect003_block',
    })

    // The journal writer is closed before the fork: the WorktreeCreateRequested
    // append cannot commit, so the physical Git worktree creation must not run.
    journal.JournalSurface_dispose(opened.journal)

    let rejected = null
    let result = null
    try {
      result = await hostSurface.forkManagerJob(host, 'job-effect003-block', 'manager', 'root request')
    } catch (error) {
      rejected = String(error?.message ?? error)
    }

    assert.equal(createCalls, 0, 'a worktree whose create intent cannot be committed must never reach Git')
    if (rejected === null) {
      assert.equal(result.ok, false, 'the fork must not report success when the intent append failed')
    }
  } finally {
    rmSync(base, { recursive: true, force: true })
  }
})

test('WHAT[effect-accounting-003] the worktree intent is durable before the physical creation and the confirmation follows it', async () => {
  const base = mkdtempSync(join(tmpdir(), 'wxs-effect003-order-'))
  try {
    const writer = 'writer-effect003-order'
    const opened = await openJournal(base, writer)
    assert.equal(opened.ok, true, JSON.stringify(opened.error))
    try {
      const coldReads = []
      let createCalls = 0
      let removeCalls = 0
      const git = {
        IsDirty: async () => ({ ok: true, value: false }),
        FreezeTargetBranch: async () => ({ ok: true, value: 'refs/heads/main' }),
        ListManagerBranches: async () => ({ ok: true, value: [] }),
        DeleteBranch: async () => ({ ok: true }),
        HasRebaseHead: async () => false,
        ReadHead: async () => ({ ok: true, value: 'head' }),
        GetTargetHead: async () => ({ ok: true, value: 'target-head' }),
        CreateWorktree: async () => {
          createCalls += 1
          const cases = worktreeCases(base, writer)
          coldReads.push({
            requested: cases.includes('WorktreeCreateRequested'),
            created: cases.includes('WorktreeCreated'),
          })
          return { ok: true, value: `manager/${writer}` }
        },
        ListWorktrees: async () => ({ ok: true, value: [] }),
        RemoveWorktree: async () => {
          removeCalls += 1
          return { ok: true }
        },
      }
      const host = hostSurface.create({
        sessions: failingSessions(),
        journal: opened.journal,
        gitPort: git,
        repoPath: base,
        targetBranch: 'refs/heads/main',
        orchestratorId: 'ses_orchestrator_effect003_order',
      })

      // The manager-session stage fails after the worktree stages, so the fork
      // returns a manager-stage error while the worktree evidence chain stays
      // complete and observable. The stage attribution is carried by
      // createCalls below (the physical creation already happened), not by the
      // verdict rendering.
      const result = await hostSurface.forkManagerJob(host, 'job-effect003-order', 'manager', 'root request')
      assert.equal(result.ok, false, 'the manager-session stub must end the fork in the manager stage')
      assert.ok(result.error, 'a failed fork must carry its typed error')

      assert.equal(createCalls, 1, 'the physical creation ran exactly once, proving the create observer is live')
      assert.deepEqual(
        coldReads,
        [{ requested: true, created: false }],
        'while the physical creation runs, the intent is already durable and the confirmation is not yet written',
      )

      const cases = worktreeCases(base, writer)
      assert.ok(cases.includes('WorktreeCreated'), 'the confirmation is persisted after the physical creation')
      assert.ok(
        cases.indexOf('WorktreeCreateRequested') < cases.indexOf('WorktreeCreated'),
        'the intent line precedes the confirmation line',
      )
      assert.equal(removeCalls, 0, 'a journaled worktree is kept for recovery, never released on a later stage failure')
    } finally {
      journal.JournalSurface_dispose(opened.journal)
    }
  } finally {
    rmSync(base, { recursive: true, force: true })
  }
})
}
