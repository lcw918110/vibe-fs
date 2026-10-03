import assert from 'node:assert/strict'
import { existsSync, readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import test from 'node:test'

const root = process.cwd()
const read = (path) => readFileSync(resolve(root, path), 'utf8')

test('WHAT[cognitive-workspace-007] no TodoSink compatibility bridge survives', () => {
  assert.equal(existsSync(resolve(root, 'src/Wanxiangshu/Participant/Cognition/TodoSink.fs')), false)
  const hooks = read('src/Wanxiangshu/OpenCode/Plugin/PluginHooks.fs')
  assert.doesNotMatch(hooks, /TodoWriteCompressionContract/)
  assert.doesNotMatch(hooks, /obligations/)
  assert.doesNotMatch(hooks, /MagicTodo/)
  assert.doesNotMatch(hooks, /TodoSink/)
})
