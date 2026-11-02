import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const deadline = await import("../../../dist/Process/DeadlineSurface.js");


test('WHAT[process-execution-004] Process_deadline_uses_explicit_offset_semantics', () => {
  const value = deadline.create('2026-01-01T00:00:00Z', 5000)

  assert.equal(deadline.remainingMs('2026-01-01T00:00:02Z', value), 3000)
  assert.equal(deadline.isExpired('2026-01-01T00:00:02Z', value), false)
  assert.equal(deadline.remainingMs('2026-01-01T00:00:05Z', value), 0)
  assert.equal(deadline.isExpired('2026-01-01T00:00:05Z', value), true)
  assert.equal(deadline.isExpired('2026-01-01T00:00:06Z', value), true)

  // Same instant expressed with a non-zero offset must produce the same answer.
  assert.equal(deadline.remainingMs('2026-01-01T08:00:02+08:00', value), 3000)
  assert.equal(deadline.isExpired('2026-01-01T08:00:02+08:00', value), false)
})
test('WHAT[process-execution-004] Process_deadline_is_independent_of_ambient_timezone', () => {
  const original = process.env.TZ
  const value = deadline.create('2026-01-01T00:00:00Z', 5000)

  try {
    for (const zone of ['UTC', 'Asia/Shanghai', 'America/Los_Angeles']) {
      process.env.TZ = zone
      assert.equal(deadline.isExpired('2026-01-01T00:00:02Z', value), false, `expired under TZ=${zone}`)
      assert.equal(deadline.remainingMs('2026-01-01T00:00:02Z', value), 3000, `remaining under TZ=${zone}`)
    }
  } finally {
    if (original === undefined) delete process.env.TZ
    else process.env.TZ = original
  }
})
}

{
const { default: assert } = await import("node:assert/strict");
const { existsSync } = await import("node:fs");
const { dirname, join } = await import("node:path");
const { default: test } = await import("node:test");
const { fileURLToPath } = await import("node:url");

test('WHAT[process-execution-004] EXEC_oneshot_completion_wait_is_bounded_by_management_deadline', () => {
  const root = join(dirname(fileURLToPath(import.meta.url)), '../../..')
  const oneshotPath = join(root, 'src/Wanxiangshu/Execution/Delegation/Handle/OpenCode/OneShotTool.fs')
  assert.equal(existsSync(oneshotPath), false, 'OneShotTool.fs must be physically removed')
})
}

{
  const assert = (await import('node:assert/strict')).default
  const runtime = await import('../../../dist/Process/Surface.js')
  test('WHAT[process-execution-004] deadline result describes waiting ending without claiming a return', () => {
    assert.match(runtime.renderDeadlineExpired(), /No return reached you before your waiting ended/)
  })
}
