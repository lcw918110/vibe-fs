import assert from 'node:assert/strict'
import test from 'node:test'
import * as forkTool from '../../../dist/Execution/Delegation/Fork/OpenCode/ToolSurface.js'
import { placeEngineer, toolModule, withForkRuntime } from './support/fork-runtime.mjs'

process.env.WANXIANGSHU_PROVIDER_LANGUAGE = 'en'

test('WHAT[participant-horizon-009] real Manager fork rejects hidden and unknown callings with the same generic response before child creation', async () => {
  await withForkRuntime(async ({ runtime, owner }) => {
    const count = forkTool.childCount(runtime)
    let generic
    for (const calling of ['bookkeeper', 'unknown-role']) {
      const result = await forkTool.executeManagerFork(runtime, toolModule, owner, calling, 'Hidden', 'PRIVATE-CHARGE')
      if (generic === undefined) generic = result
      assert.equal(result, generic, calling)
      assert.doesNotMatch(result, /bookkeeper|unknown-role|PRIVATE-CHARGE|internal/i)
      assert.equal(forkTool.childCount(runtime), count)
    }
    await placeEngineer(runtime, owner, 'Visible')
    assert.ok(forkTool.childCount(runtime) > count, 'authorized Engineer is not rejected by a blanket denial')
    await forkTool.cancelOwnerChildren(runtime, owner)
  })
})
