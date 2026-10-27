import assert from 'node:assert/strict'
import { existsSync } from 'node:fs'
import test from 'node:test'

test('WHAT[execution-model-routing-001] resources/wanxiangshu.mjs is deleted and no longer present in repository', () => {
  const templateUrl = new URL('../../../resources/wanxiangshu.mjs', import.meta.url)
  assert.equal(existsSync(templateUrl), false, 'wanxiangshu.mjs template must not exist')
})
