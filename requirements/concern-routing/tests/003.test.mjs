import assert from 'node:assert/strict'
import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs'
import { resolve } from 'node:path'
import test from 'node:test'

const root = process.cwd()
const read = (path) => readFileSync(resolve(root, path), 'utf8')
const listFiles = (dir) => {
  if (!existsSync(dir)) return []
  return readdirSync(dir).flatMap((entry) => {
    const full = resolve(dir, entry)
    return statSync(full).isDirectory() ? listFiles(full) : [full]
  })
}

// Superseded by the programming surface (this.ask, this.deliver, this.fork, this.join): publish is retired and its provider resources are deleted.
test('WHAT[concern-routing-003] publish is retired from ToolRegistry and provider resources, superseded by this.ask/this.deliver/this.fork/this.join', () => {
  const staticTools = read('src/Wanxiangshu/OpenCode/Tools/StaticTools.fs')
  const toolRegistry = read('src/Wanxiangshu/OpenCode/Tools/ToolRegistry.fs')
  const toolMap = JSON.parse(read('resources/ablation/tool-map.json'))
  assert.doesNotMatch(staticTools, /"publish"/)
  assert.doesNotMatch(toolRegistry, /ConcernTools/)
  assert.doesNotMatch(toolRegistry, /"publish"/)
  assert.equal('publish' in (toolMap.tools ?? {}), false)

  const providerDir = resolve(root, 'resources/provider/concern-routing')
  assert.equal(
    listFiles(providerDir).length,
    0,
    'resources/provider/concern-routing must be deleted',
  )

  assert.match(read('resources/provider/tool/js-plan/en.md'), /\b(?:js|this)\.ask\(question\)/)
  assert.equal(existsSync(resolve(root, 'resources/provider/tool/js-program/deliver')), false, 'retired js-program/deliver resource must be absent')
  assert.match(read('resources/provider/tool/deliver/en.md'), /wxs_deliver/, 'material handback keeps its tool name')
  assert.doesNotMatch(read('resources/provider/tool/deliver/en.md'), /S1|S4/)
  const delegationEn = read('resources/provider/tool/js-program/delegation-rules/en.md')
  assert.match(delegationEn, /\b(?:js\.|this\.)?fork\(/)
  assert.match(delegationEn, /\b(?:js\.|this\.)?join\(/)
})

