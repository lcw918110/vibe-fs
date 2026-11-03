import assert from 'node:assert/strict'
import { execFileSync } from 'node:child_process'
import fs from 'node:fs'
import path from 'node:path'
import test from 'node:test'
import os from 'node:os'
import { fileURLToPath } from 'node:url'
import { checkCandidateDependency, assertNoTrackedArchives } from '../../../scripts/wanxiangli-source.mjs'

const root = fileURLToPath(new URL('../../..', import.meta.url))

test('WHAT[distribution-011] source checkout consumes one tracked wanxiangli npm artifact with matching lock integrity', () => {
  const manifest = JSON.parse(fs.readFileSync(path.join(root, 'package.json'), 'utf8'))
  const lock = JSON.parse(fs.readFileSync(path.join(root, 'package-lock.json'), 'utf8'))
  assert.deepEqual(manifest.bundleDependencies, ['wanxiangli'])
  assert.equal(manifest.dependencies.wanxiangli, 'file:wanxiangli')

  const { source: sourceDir } = checkCandidateDependency(root)
  assert.equal(fs.realpathSync(sourceDir), fs.realpathSync(path.join(root, 'wanxiangli')))
  assertNoTrackedArchives(root)
  assertNoTrackedArchives(sourceDir)

  const sourcePkg = JSON.parse(fs.readFileSync(path.join(sourceDir, 'package.json'), 'utf8'))
  const locked = lock.packages['node_modules/wanxiangli']
  assert.deepEqual(locked, {
    resolved: 'wanxiangli',
    link: true,
  })
  const lockedWorkspace = lock.packages['wanxiangli']
  assert.equal(lockedWorkspace.version, sourcePkg.version)

  const installed = path.join(root, 'node_modules/wanxiangli')
  assert.equal(fs.realpathSync(installed), fs.realpathSync(sourceDir))

  const trackedSource = execFileSync('git', ['ls-files', '--stage', 'wanxiangli'], {
    cwd: root,
    encoding: 'utf8',
  }).trim()
  assert.match(trackedSource, /^160000 [0-9a-f]{40} 0\twanxiangli$/)
  const ignored = fs.readFileSync(path.join(root, '.gitignore'), 'utf8')
  assert.match(ignored, /^\*\.tgz$/m)
  assert.doesNotMatch(ignored, /^\/wanxiangli\/$/m)
})

function candidateFixture(t) {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'wx-candidate-'))
  t.after(() => fs.rmSync(directory, { recursive: true, force: true }))
  const source = path.join(directory, 'wanxiangli')
  fs.mkdirSync(source)
  const git = (cwd, ...args) => execFileSync('git', args, { cwd, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim()
  git(directory, 'init', '--quiet')
  git(source, 'init', '--quiet')
  fs.writeFileSync(path.join(source, 'package.json'), JSON.stringify({ name: 'wanxiangli', version: '1.0.0' }))
  fs.writeFileSync(path.join(source, 'entry.js'), 'export const value = 1\n')
  const commit = () => {
    git(source, 'add', '.')
    git(source, '-c', 'user.name=Candidate fixture', '-c', 'user.email=candidate@example.invalid', 'commit', '--quiet', '-m', 'fixture')
    return git(source, 'rev-parse', 'HEAD')
  }
  const sha = commit()
  const pin = value => git(directory, 'update-index', '--add', '--cacheinfo', `160000,${value},wanxiangli`)
  pin(sha)
  fs.writeFileSync(path.join(directory, '.gitmodules'), '[submodule "wanxiangli"]\npath = wanxiangli\nbranch = main\n')
  fs.writeFileSync(path.join(directory, 'package.json'), JSON.stringify({ dependencies: { wanxiangli: 'file:wanxiangli' }, bundleDependencies: ['wanxiangli'] }))
  fs.writeFileSync(path.join(directory, 'package-lock.json'), JSON.stringify({ packages: {
    '': { dependencies: { wanxiangli: 'file:wanxiangli' } },
    'node_modules/wanxiangli': { resolved: 'wanxiangli', link: true },
    wanxiangli: { version: '1.0.0' },
  } }))
  fs.mkdirSync(path.join(directory, 'node_modules'))
  fs.symlinkSync(source, path.join(directory, 'node_modules/wanxiangli'), 'junction')
  return { directory, source, sha, commit, pin }
}

test('WHAT[distribution-011] candidate accepts a clean selected SHA with matching lock and install', t => {
  const fixture = candidateFixture(t)
  assert.deepEqual(checkCandidateDependency(fixture.directory, { expectedSha: fixture.sha }), {
    source: fixture.source, sha: fixture.sha, version: '1.0.0',
  })
})

test('WHAT[distribution-011] candidate rejects the wrong promised SHA even when package versions agree', t => {
  const fixture = candidateFixture(t)
  fs.writeFileSync(path.join(fixture.source, 'entry.js'), 'export const value = 2\n')
  const next = fixture.commit()
  fixture.pin(next)
  assert.throws(() => checkCandidateDependency(fixture.directory, { expectedSha: fixture.sha }), /does not match expected/)
  assert.equal(checkCandidateDependency(fixture.directory, { expectedSha: next }).version, '1.0.0')
})

test('WHAT[distribution-011] candidate rejects an unstaged dependency checkout', t => {
  const fixture = candidateFixture(t)
  fs.writeFileSync(path.join(fixture.source, 'entry.js'), 'export const value = 2\n')
  const next = fixture.commit()
  assert.throws(() => checkCandidateDependency(fixture.directory, { expectedSha: next }), /does not match gitlink/)
})

test('WHAT[distribution-011] candidate rejects changed source at the same HEAD', t => {
  const fixture = candidateFixture(t)
  fs.writeFileSync(path.join(fixture.source, 'entry.js'), 'export const value = 2\n')
  assert.throws(() => checkCandidateDependency(fixture.directory, { expectedSha: fixture.sha }), /uncommitted files/)
})

test('WHAT[distribution-011] candidate rejects untracked source at the same HEAD', t => {
  const fixture = candidateFixture(t)
  fs.writeFileSync(path.join(fixture.source, 'extra.js'), 'export const value = 2\n')
  assert.throws(() => checkCandidateDependency(fixture.directory, { expectedSha: fixture.sha }), /uncommitted files/)
})

test('WHAT[distribution-011] candidate rejects a stale lock or another installed checkout', t => {
  const fixture = candidateFixture(t)
  const lockPath = path.join(fixture.directory, 'package-lock.json')
  const lock = JSON.parse(fs.readFileSync(lockPath, 'utf8'))
  lock.packages.wanxiangli.version = '0.9.0'
  fs.writeFileSync(lockPath, JSON.stringify(lock))
  assert.throws(() => checkCandidateDependency(fixture.directory, { expectedSha: fixture.sha }), /package-lock link must agree/)
  lock.packages.wanxiangli.version = '1.0.0'
  fs.writeFileSync(lockPath, JSON.stringify(lock))
  fs.unlinkSync(path.join(fixture.directory, 'node_modules/wanxiangli'))
  fs.mkdirSync(path.join(fixture.directory, 'node_modules/wanxiangli'))
  assert.throws(() => checkCandidateDependency(fixture.directory, { expectedSha: fixture.sha }), /must resolve to the selected candidate/)
})

test('WHAT[distribution-011] candidate rejects a malformed explicit SHA', t => {
  const fixture = candidateFixture(t)
  assert.throws(() => checkCandidateDependency(fixture.directory, { expectedSha: 'main' }), /full lowercase Git SHA/)
})
