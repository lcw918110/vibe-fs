import test from 'node:test'
import assert from 'node:assert/strict'
import { spawnSync } from 'node:child_process'
import { resolve } from 'node:path'
import { parseParallelOptions, runBuild } from '../../../../scripts/build.mjs'
import { buildSourceDependency } from '../../../../scripts/wanxiangli-source.mjs'

const root = resolve(import.meta.dirname, '../../../..')

test('WHAT[structured-workflow-012] parallel options preserve the default and explicit compiler limits', () => {
  assert.deepEqual(parseParallelOptions([]), { args: [], jobs: 1 })
  assert.deepEqual(parseParallelOptions(['--parallel']), { args: [], jobs: 2 })
  assert.deepEqual(parseParallelOptions(['--jobs', '4']), { args: [], jobs: 4 })
  assert.deepEqual(parseParallelOptions(['--jobs=1', '--help']), { args: ['--help'], jobs: 1 })
  for (const args of [['--jobs'], ['--jobs', '0'], ['--jobs=5'], ['--jobs=-1'], ['--jobs=1.5'],
    ['--parallel', '--jobs=2'], ['--parallel', '--parallel'], ['--parallel', '--plan'],
    ['--jobs=1', '--checker-status'], ['--parallel', '--checker-stop']]) {
    assert.throws(() => parseParallelOptions(args), /--jobs|Parallel compilation|only once/, JSON.stringify(args))
  }
})

test('WHAT[structured-workflow-012] invalid parallel requests fail before the build touches a workspace', async () => {
  for (const jobs of [0, 5, NaN, 1.5, '2']) {
    await assert.rejects(() => runBuild({ targetRoot: '/no-such-workspace', jobs }), /--jobs/)
    assert.throws(() => buildSourceDependency('/no-such-workspace', { jobs }), /--jobs/)
  }
  const result = spawnSync(process.execPath, ['scripts/build.mjs', '--parallel', '--checker-stop'], {
    cwd: root, encoding: 'utf8',
  })
  assert.notEqual(result.status, 0)
  assert.match(result.stderr, /Parallel compilation cannot be combined/)
})
