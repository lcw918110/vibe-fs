import test from 'node:test'
import { integrationTest } from '../../verification-system/tests/support/tier-gate.mjs'

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");

const change = await import('../../../dist/Change/Surface.js')
const hostSurface = await import('../../../dist/Change/Host/Surface.js')
const fakeRunner = (answers) => {
  const calls = []
  const runner = (command) => {
    const args = command.args
    calls.push({ file: command.fileName, args, cwd: command.workingDirectory })
    const key = args.join(' ')
    for (const [prefix, response] of answers) {
      if (!key.startsWith(prefix)) continue
      if (Array.isArray(response) && Array.isArray(response[0])) {
        const triple = response.length > 1 ? response.shift() : response[0]
        return Promise.resolve(triple)
      }
      return Promise.resolve(response)
    }
    return Promise.resolve([0, '', ''])
  }
  return { runner, calls }
}
const REPO = '/repo'
const WORKTREE = '/repo/.worktrees/job-1'
const git = (runner) => change.createGit(REPO, runner)
const ok = (result) => {
  assert.equal(result.ok, true, result.ok ? '' : result.error)
  return result.value
}
const ffAnswers = ({ candidate = 'cafe01', targetHead = 'beef02', branch = 'main' } = {}) => [
  ['symbolic-ref --short HEAD', [0, `${branch}\n`, '']],
  ['rev-parse HEAD', [0, `${candidate}\n`, '']],
  ['rev-parse refs/heads/main', [0, `${targetHead}\n`, '']],
  ['merge-base --is-ancestor', [0, '', '']],
  ['status --porcelain', [0, '', '']],
  ['merge --ff-only', [0, '', '']],
]
const ff = (answers) => change.gitFfMerge(git(fakeRunner(answers).runner), WORKTREE, 'main', 'beef02', 'cafe01')

test('WHAT[change-integration-002] GIT_is_dirty_true_only_on_nonempty_porcelain', async () => {
  assert.deepEqual(await change.gitIsDirty(git(fakeRunner([['status --porcelain', [0, ' M file.fs\n', '']]]).runner), WORKTREE), { ok: true, value: true })
  assert.deepEqual(await change.gitIsDirty(git(fakeRunner([['status --porcelain', [0, '', '']]]).runner), WORKTREE), { ok: true, value: false })
})
test('WHAT[change-integration-002] failed cleanliness inspection preserves the command failure', async () => {
  for (const [stdout, stderr, error] of [
    ['', 'cannot inspect', 'cannot inspect'],
    ['status failed without stderr', '', 'status failed without stderr'],
  ]) {
    const fake = fakeRunner([['status --porcelain', [128, stdout, stderr]]])
    const result = await change.gitIsDirty(git(fake.runner), WORKTREE)
    assert.deepEqual(result, { ok: false, error })
    assert.deepEqual(fake.calls, [{ file: 'git', args: ['status', '--porcelain'], cwd: WORKTREE }])
  }
})
test('WHAT[change-integration-002] rejected cleanliness command preserves its original exception', async () => {
  const failure = new Error('status transport failed')
  await assert.rejects(
    change.gitIsDirty(git(() => Promise.reject(failure)), WORKTREE),
    error => error === failure,
  )
})
test('WHAT[change-integration-002] unreadable target cleanliness cannot authorize ff publication', async () => {
  const answers = ffAnswers().map(([prefix, response]) => prefix === 'status --porcelain'
    ? [prefix, [128, '', 'cannot inspect worktree']]
    : [prefix, response])
  const fake = fakeRunner(answers)
  const result = await change.gitFfMerge(git(fake.runner), WORKTREE, 'main', 'beef02', 'cafe01')
  assert.equal(result.ok, false)
  assert.equal(fake.calls.some(call => call.args[0] === 'merge'), false)
})
test('WHAT[change-integration-002] GIT_ff_merge_refuses_dirty_target_worktree', async () => {
  const answers = ffAnswers().map(([prefix, response]) => prefix === 'status --porcelain' ? [prefix, [0, ' M dirty.fs\n', '']] : [prefix, response])
  const result = await ff(answers)
  assert.equal(result.ok, false)
  assert.match(result.error, /target worktree is dirty; refusing ff-only merge/)
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");

const change = await import('../../../dist/Change/Surface.js')
const job = (id, path = `/tmp/${id}`) => ({
  jobId: id,
  managerSessionId: `ses-${id}`,
  managerAgent: 'manager',
  byname: id,
  worktreeIdentity: `manager/${id}`,
  worktreePath: path,
  targetRef: 'refs/heads/main',
  targetBranchFrozen: 'refs/heads/main',
})

test('WHAT[change-integration-002] Git adapter reports worktree enumeration failure', async () => {
  const runner = (command) => command.args[0] === 'worktree' ? Promise.resolve([128, '', 'no .git']) : Promise.resolve([0, '', ''])
  const result = await change.gitListWorktrees(change.createGit('/repo', runner))
  assert.equal(result.ok, false)
  assert.match(result.error, /no \.git/)
})
test('WHAT[change-integration-002] Git adapter detects a modified target worktree', async () => {
  const runner = () => Promise.resolve([0, ' M dirty.fs\n', ''])
  assert.deepEqual(await change.gitIsDirty(change.createGit('/repo', runner), '/tmp/hostfw5'), { ok: true, value: true })
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { assertOpaque } = await import("../../verification-system/tests/support/js-contract.mjs");

const change = await import('../../../dist/Change/Surface.js')
const PATH = '/repo/.worktrees/job-9'
const fakeRunner = (answers = []) => {
  const calls = []
  const runner = (command) => {
    const args = command.args
    calls.push({ args, cwd: command.workingDirectory })
    const key = args.join(' ')
    for (const [prefix, response] of answers) {
      if (key.startsWith(prefix)) return Promise.resolve(response)
    }
    return Promise.resolve([0, '', ''])
  }
  return { runner, calls }
}
const fakeGit = (answers = []) => {
  const fake = fakeRunner(answers)
  return { ...fake, git: change.createGit('/repo', fake.runner) }
}
const valueOf = async (promise) => {
  const result = await promise
  assert.equal(result.ok, true, result.ok ? '' : result.error)
  return result.value
}

test('WHAT[change-integration-002] WORKTREE_CMD_is_dirty_reads_porcelain', async () => {
  assert.deepEqual(await change.gitIsDirty(fakeGit([['status --porcelain', [0, ' M x.fs\n', '']]]).git, PATH), { ok: true, value: true })
  assert.deepEqual(await change.gitIsDirty(fakeGit([['status --porcelain', [0, '\n', '']]]).git, PATH), { ok: true, value: false })
})
}

{
const { default: assert } = await import("node:assert/strict");
const { execFileSync, spawnSync } = await import("node:child_process");
const { mkdtempSync, readFileSync, rmSync, writeFileSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join } = await import("node:path");
const change = await import('../../../dist/Change/Surface.js');
const hostSurface = await import('../../../dist/Change/Host/Surface.js');
const journal = await import('../../../dist/Persistence/Journal/Surface.js');
const { fakeGitPort, fakeSessions } = await import('../../verification-system/tests/support/orchestrator-host-harness.mjs');

const git = (cwd, ...args) => execFileSync('git', args, { cwd, encoding: 'utf8' }).trim()

const commit = (repo, file, contents, message) => {
  writeFileSync(join(repo, file), contents)
  git(repo, 'add', file)
  git(repo, 'commit', '--quiet', '-m', message)
  return git(repo, 'rev-parse', 'HEAD')
}

const realRunner = (command) => {
  const result = spawnSync(command.fileName, command.args, {
    cwd: command.workingDirectory,
    encoding: 'utf8',
  })
  return Promise.resolve([result.status ?? 1, result.stdout ?? '', result.stderr ?? ''])
}

const fixture = () => {
  const root = mkdtempSync(join(tmpdir(), 'wxs-ff-merge-real-'))
  const repo = join(root, 'repo')
  const candidate = join(root, 'candidate')

  execFileSync('git', ['init', '--quiet', repo])
  git(repo, 'config', 'user.email', 'test@example.com')
  git(repo, 'config', 'user.name', 'test')
  git(repo, 'checkout', '--quiet', '-b', 'main')
  const expectedHead = commit(repo, 'shared.txt', 'base\n', 'base')
  git(repo, 'worktree', 'add', '--quiet', '-b', 'candidate', candidate, 'main')
  const candidateHead = commit(candidate, 'candidate.txt', 'candidate\n', 'candidate')

  return {
    root,
    candidate,
    candidateHead,
    expectedHead,
    gitAdapter: change.createGit(repo, realRunner),
    repo,
    remove: () => rmSync(root, { recursive: true, force: true }),
  }
}

const withAdmissionFixture = async (status, body) => {
  const fx = fixture()
  let handle
  let host
  try {
    const opened = await journal.JournalSurface_bootWithWriterId(
      join(fx.repo, '.git'), 'writer-clean-gate', 'runtime-clean-gate', 4242, '2026-01-01T00:00:00Z',
    )
    assert.equal(opened.ok, true, opened.error)
    handle = opened.journal
    const commands = []
    const adapter = change.createGit(fx.repo, async (command) => {
      commands.push(command)
      if (command.args[0] === 'status') {
        if (status instanceof Error) throw status
        return status
      }
      assert.equal(command.args[0], 'worktree')
      assert.equal(command.args[1], 'add')
      return [1, '', 'controlled stop before worktree creation']
    })
    const sessions = fakeSessions()
    host = hostSurface.create({
      sessions,
      journal: handle,
      gitPort: {
        ...fakeGitPort(),
        IsDirty: path => change.gitIsDirty(adapter, path),
        CreateWorktree: (jobId, path) => change.gitCreateWorktree(adapter, jobId, path),
      },
      repoPath: fx.repo,
      targetBranch: 'main',
      orchestratorId: 'orchestrator-clean-gate',
    })
    const writerFile = join(fx.repo, '.git', 'wanxiangshu', 'events', 'writer-clean-gate-0000.ndjson')
    const readHistory = () => {
      try {
        return readFileSync(writerFile, 'utf8')
      } catch (error) {
        if (error.code === 'ENOENT' && error.path === writerFile) return null
        throw error
      }
    }
    const history = readHistory()
    assert.equal(history, null, 'opening an empty journal must leave its lazy writer absent')
    await body({
      ...fx, host, sessions, commands, history,
      readHistory,
      fork: () => hostSurface.forkManagerJob(host, 'clean-gate-job', 'manager', 'Investigate the request.'),
    })
  } finally {
    try {
      if (host) await hostSurface.detachAndDrain(host)
    } finally {
      if (handle) journal.JournalSurface_dispose(handle)
      fx.remove()
    }
  }
}

integrationTest('WHAT[change-integration-002] failed Git cleanliness blocks a new Host job before its worktree request or Manager creation', async () => {
  await withAdmissionFixture([128, '', 'cannot inspect target'], async (fx) => {
    const result = await fx.fork()
    assert.equal(result.ok, false)
    assert.match(result.error, /IntegrationFailed/)
    assert.match(result.error, /Failed to inspect target cleanliness: cannot inspect target/)
    assert.deepEqual(fx.commands.map(command => command.args), [['status', '--porcelain']])
    assert.equal(fx.commands[0].workingDirectory, fx.repo)
    assert.deepEqual(fx.sessions.calls, [])
    assert.equal(fx.readHistory(), fx.history)
    assert.equal(git(fx.repo, 'rev-parse', 'HEAD'), fx.expectedHead)
  })
})

integrationTest('WHAT[change-integration-002] rejected Git command leaves a new Host job without its worktree request or Manager creation', async () => {
  const failure = new Error('status command transport failed')
  await withAdmissionFixture(failure, async (fx) => {
    await assert.rejects(fx.fork(), error => error === failure)
    assert.deepEqual(fx.commands.map(command => command.args), [['status', '--porcelain']])
    assert.deepEqual(fx.sessions.calls, [])
    assert.equal(fx.readHistory(), fx.history)
    assert.equal(git(fx.repo, 'rev-parse', 'HEAD'), fx.expectedHead)
  })
})

integrationTest('WHAT[change-integration-002] dirty Git result still rejects admission while clean permits the worktree request', async () => {
  await withAdmissionFixture([0, ' M shared.txt\n', ''], async (fx) => {
    const result = await fx.fork()
    assert.equal(result.ok, false)
    assert.match(result.error, /RejectedDirty/)
    assert.deepEqual(fx.commands.map(command => command.args), [['status', '--porcelain']])
    assert.deepEqual(fx.sessions.calls, [])
    assert.equal(fx.readHistory(), fx.history)
  })
  await withAdmissionFixture([0, '', ''], async (fx) => {
    const result = await fx.fork()
    assert.equal(result.ok, false)
    assert.match(result.error, /Failed to create worktree: controlled stop before worktree creation/)
    assert.deepEqual(fx.commands.map(command => command.args[0]), ['status', 'worktree'])
    assert.equal(typeof fx.readHistory(), 'string', 'clean admission must create the observed writer file')
    assert.match(fx.readHistory(), /WorktreeCreateRequested/)
    assert.deepEqual(fx.sessions.calls, [])
  })
})

integrationTest('WHAT[change-integration-002] actual Git status failure returns an inspection error', async () => {
  const fx = fixture()
  try {
    const result = await change.gitIsDirty(fx.gitAdapter, fx.root)
    assert.equal(result.ok, false)
    assert.match(result.error, /not a git repository/)
    assert.equal(git(fx.repo, 'rev-parse', 'HEAD'), fx.expectedHead)
  } finally {
    fx.remove()
  }
})

integrationTest('WHAT[change-integration-002] Adapter_ffMerge_dirty_target_fails_closed_without_advancing_head', async () => {
  const fx = fixture()

  try {
    writeFileSync(join(fx.repo, 'shared.txt'), 'dirty\n')
    const result = await change.gitFfMerge(fx.gitAdapter, fx.candidate, 'main', fx.expectedHead, fx.candidateHead)

    assert.deepEqual(result, { ok: false, error: 'target worktree is dirty; refusing ff-only merge' })
    assert.equal(git(fx.repo, 'rev-parse', 'refs/heads/main'), fx.expectedHead)
    assert.equal(git(fx.candidate, 'rev-parse', 'HEAD'), fx.candidateHead)
  } finally {
    fx.remove()
  }
})
}
