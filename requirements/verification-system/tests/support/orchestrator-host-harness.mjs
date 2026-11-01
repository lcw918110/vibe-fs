// Shared OrchestratorHost semantic harness.
//
// The owner surface constructs the real Host over plain JavaScript port
// contracts. Journal and Host state cross this boundary only as opaque
// capabilities; observations returned by this harness are ordinary JS values.

import { execFileSync } from 'node:child_process'
import { mkdirSync, mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

import * as hostSurface from '../../../../dist/Change/Host/Surface.js'
import * as journalSurface from '../../../../dist/Persistence/Journal/Surface.js'
import { GitWorktrees } from 'wanxiangli/git'

export const fakeGitPort = (behaviour = {}) => ({
  Worktrees: new GitWorktrees('/repo', async ({ args }) => {
    if (args[0] === 'worktree' && args[1] === 'list' && behaviour.listError) {
      return { code: 1, stdout: '', stderr: behaviour.listError }
    }
    return { code: 0, stdout: '', stderr: '' }
  }),
  IsDirty: async () => ({ ok: true, value: behaviour.dirty ? [behaviour.dirty] : [] }),
  FreezeTargetBranch: async () =>
    behaviour.freezeError ? { ok: false, error: behaviour.freezeError } : { ok: true, value: 'refs/heads/main' },
  Rebase: async () => ({ ok: true }),
  ConflictedFiles: async () => ({ ok: true, value: [] }),
  FfMerge: async () => ({ ok: true, value: 'cafe01' }),
  HasRebaseHead: async () => false,
  ReadHead: async () => ({ ok: true, value: 'beef02' }),
  GetTargetHead: async () => ({ ok: true, value: 'beef02' }),
})

export const fakeSessions = (behaviour = {}) => {
  const calls = []
  let childSeq = 0
  let sendSeq = 0
  const terminalListeners = new Map()
  const futureTerminalListeners = new Map()
  const stickyTerminals = new Map()

  const sessionKey = (value) => String(value)
  const invokeTerminalCallback = (callback, session, outcome) => {
    const first = callback(session, outcome)
    if (typeof first === 'function') first(outcome)
  }
  const notifyTerminal = (session, outcome) => {
    const key = sessionKey(session)
    stickyTerminals.set(key, outcome)
    for (const callback of terminalListeners.get(key) ?? []) invokeTerminalCallback(callback, session, outcome)
    for (const callback of futureTerminalListeners.get(key) ?? []) invokeTerminalCallback(callback, session, outcome)
  }

  return {
    calls,
    notifyTerminal,
    activeFutureTerminalSubscriptions: () =>
      [...futureTerminalListeners.values()].reduce((count, listeners) => count + listeners.size, 0),
    CreateChildSession: async (parentId, options) => {
      childSeq += 1
      calls.push(['CreateChildSession', options])
      if (behaviour.createError) return { ok: false, error: behaviour.createError }
      return { ok: true, value: `child-${childSeq}` }
    },
    AbortSession: async (id) => {
      calls.push(['AbortSession', id])
      return { ok: true }
    },
    InterruptAttempt: async (id) => {
      calls.push(['InterruptAttempt', id])
      return { ok: true }
    },
    AbortChildren: async (id) => {
      calls.push(['AbortChildren', id])
    },
    CreateSiblingSession: async (owner, parent, options) => {
      calls.push(['CreateSiblingSession', owner, parent, options])
      return { ok: true, value: `sibling-${++childSeq}` }
    },
    TryGetParentSession: async () => ({ ok: true, value: undefined }),
    SendPrompt: async (...args) => {
      calls.push(['SendPrompt', ...args])
      sendSeq += 1
      const physical =
        behaviour.physicalMessageForSend?.(sendSeq, ...args) ?? `msg_fake_prompt_${sendSeq}`

      behaviour.onSendPrompt?.(...args, { sendSeq, physical })
      if (behaviour.sendPromptError) return { kind: 'Fatal', reason: behaviour.sendPromptError }
      if (behaviour.terminalAfterSend) {
        queueMicrotask(() => notifyTerminal(args[0], { kind: 'Failed', error: behaviour.terminalAfterSend }))
      }
      return { kind: 'Physical', value: physical }
    },
    SubscribeTerminal: (childId, callback) => {
      calls.push(['SubscribeTerminal', childId])
      const key = sessionKey(childId)
      if (!terminalListeners.has(key)) terminalListeners.set(key, new Set())
      terminalListeners.get(key).add(callback)
      if (stickyTerminals.has(key)) {
        queueMicrotask(() => invokeTerminalCallback(callback, childId, stickyTerminals.get(key)))
      }
      return {
        Dispose: () => {
          terminalListeners.get(key)?.delete(callback)
        },
      }
    },
    SubscribeFutureTerminal: (childId, callback) => {
      calls.push(['SubscribeFutureTerminal', childId])
      const key = sessionKey(childId)
      if (!futureTerminalListeners.has(key)) futureTerminalListeners.set(key, new Set())
      futureTerminalListeners.get(key).add(callback)
      return {
        Dispose: () => {
          futureTerminalListeners.get(key)?.delete(callback)
        },
      }
    },
    ListChildren: async () => ({ ok: true, value: [] }),
    FamilyRootOf: (sessionId) => sessionId,
  }
}

/** A real git repo with one empty commit; gitCommonDir/init stay hermetic. */
export const gitDir = (label) => {
  const dir = mkdtempSync(join(tmpdir(), `wxs-host-${label}-`))
  execFileSync('git', ['init', '-b', 'main', dir], { stdio: 'ignore' })
  execFileSync(
    'git',
    ['-C', dir, '-c', 'user.email=t@t', '-c', 'user.name=t', 'commit', '--allow-empty', '-m', 'init'],
    { stdio: 'ignore' },
  )
  return dir
}

/**
 * Real OrchestratorHost over a real journal + real repo. The Host owns lazy
 * engine initialization through its own manager and git ports.
 */
export const liveOrchestrator = async (options = {}) => {
  const dir = mkdtempSync(join(tmpdir(), 'wxs-hostcov-'))
  const repoDir = join(dir, 'repo')
  mkdirSync(repoDir)
  execFileSync('git', ['init', '-b', 'main', repoDir], { stdio: 'ignore' })
  execFileSync(
    'git',
    ['-C', repoDir, '-c', 'user.email=t@t', '-c', 'user.name=t', 'commit', '--allow-empty', '-m', 'init'],
    { stdio: 'ignore' },
  )
  const opened =
    options.journal === false
      ? null
      : await journalSurface.JournalSurface_bootWithWriterId(
          dir,
          `writer-${options.orchestratorId ?? 'orchestrator'}`,
          'rt-orchestrator-host',
          4242,
          '2026-01-01T00:00:00Z',
        )
  if (opened) {
    const ok = opened.ok === true
    if (!ok) throw new Error('journal must open')
  }

  const sessions = fakeSessions(options.sessionBehaviour)
  const host = hostSurface.create({
    sessions,
    journal: opened?.journal,
    gitPort: options.gitPort ?? fakeGitPort(),
    repoPath: options.repoPath ?? repoDir,
    targetBranch: options.targetBranch ?? '',
    orchestratorId: options.orchestratorId ?? 'ses_orphost',
  })

  return {
    host,
    sessions,
    journal: opened?.journal,
    cleanup: async () => {
      try {
        await hostSurface.detachAndDrain(host)
      } catch {}
      try {
        if (opened?.journal) journalSurface.JournalSurface_dispose(opened.journal)
      } catch {}
      rmSync(dir, { recursive: true, force: true })
    },
  }
}

export const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms))
