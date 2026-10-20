import assert from 'node:assert/strict'
import test from 'node:test'
import { check } from '../../../scripts/checks/llm-facing-format-gate.mjs'

const scan = (file, text) => check({ productionFiles: () => [{ file, text }] }).issues

test('WHAT[provider-projection-013] existing representation gate accepts the formatting owner and rejects direct low-level access by a feature', () => {
  const code = 'let result = SyntheticToml.renderString value'
  assert.deepEqual(scan('src/Wanxiangshu/Foundation/LlmFacing.fs', code), [])
  assert.deepEqual(scan('src/Wanxiangshu/Foundation/SyntheticTomlSurface.fs', code), [])
  assert.deepEqual(scan('src/Wanxiangshu/Execution/Delegation/Advisor/View.fs', code), [])
  assert.deepEqual(scan('src/Wanxiangshu/Feature/Fixture.fs', 'let result = LlmFacing.render document'), [])
  assert.ok(scan('src/Wanxiangshu/Feature/Fixture.fs', code).some((issue) => issue.code === 'llm-facing-format-violation'))
  assert.deepEqual(check().issues, [])
})

test.todo('WHAT[provider-projection-013] all synthetic payloads use the common typed representation owner; the gate currently checks direct SyntheticToml access only (GAP-082)')
