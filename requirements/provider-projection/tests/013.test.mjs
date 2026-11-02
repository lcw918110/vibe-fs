import assert from 'node:assert/strict'
import test from 'node:test'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { runOutputAssertions } from '../../../scripts/checks/llm-facing-format-gate.mjs'

test('WHAT[provider-projection-013] synthetic representation proves escaping, empty values, outcome distinction, required fields and isolation without filename whitelist', async () => {
  const result = await runOutputAssertions()
  assert.equal(result.skipped, false)
  assert.deepEqual(
    result.issues,
    [],
    `synthetic representation output assertions must hold: ${result.issues.map((i) => i.message).join('; ')}`,
  )
})

test('WHAT[provider-projection-013] missing host artifact fails instead of skipping output checks', async () => {
  const emptyDist = mkdtempSync(join(tmpdir(), 'llm-facing-missing-'))
  try {
    await assert.rejects(runOutputAssertions(emptyDist), /missing LlmFacing artifact/)
  } finally {
    rmSync(emptyDist, { recursive: true, force: true })
  }
})

test.todo('WHAT[provider-projection-013] all synthetic payload paths compose through the representation owner; selected output tests do not cover every caller (GAP-082)')
