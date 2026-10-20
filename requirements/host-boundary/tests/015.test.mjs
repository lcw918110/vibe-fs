import { test } from 'node:test'
import assert from 'node:assert/strict'
import * as toolResultBound from '../../../dist/Host/Contract/ToolResultBound.js'
import { registerBounded } from '../../../dist/OpenCode/Codec/ToolHostSurface.js'

const hostLines = (text) => text.split('\n').length

const hostBytes = (text) => Buffer.byteLength(text)

test('WHAT[host-boundary-015] TOOL_RESULT_BOUND_budget_derives_from_host_ceiling_exactly', () => {
  assert.equal(toolResultBound.MarkerBytes, Buffer.byteLength(toolResultBound.Marker))
  assert.equal(toolResultBound.DefaultHostMaxLines, 2000)
  assert.equal(toolResultBound.DefaultHostMaxBytes, 51200)
  // Default ceiling mirrors the Host transport when no tool_output is declared.
  assert.equal(toolResultBound.HostMaxLines(), toolResultBound.DefaultHostMaxLines)
  assert.equal(toolResultBound.HostMaxBytes(), toolResultBound.DefaultHostMaxBytes)
  assert.equal(toolResultBound.ContentMaxLines() + toolResultBound.MarkerLineCost, toolResultBound.HostMaxLines())
  assert.equal(toolResultBound.MarkerBytes + toolResultBound.ContentMaxBytes(), toolResultBound.HostMaxBytes())
})

test('WHAT[host-boundary-015] TOOL_RESULT_BOUND_reads_a_raised_host_ceiling_and_fills_it_exactly', () => {
  try {
    toolResultBound.setHostLimits(10, 1000)
    assert.equal(toolResultBound.HostMaxLines(), 10)
    assert.equal(toolResultBound.ContentMaxLines(), 10 - toolResultBound.MarkerLineCost)
    assert.equal(toolResultBound.ContentMaxBytes(), 1000 - toolResultBound.MarkerBytes)

    const original = Array.from({ length: 40 }, (_, n) => `line-${n}`).join('\n')
    const output = toolResultBound.bound(original)
    // Exactly at the ceiling: one more line would exceed it.
    assert.equal(hostLines(output), 10)
    assert.ok(hostBytes(output) <= 1000)
    assert.equal(toolResultBound.fitsInWindow(output), true)
  } finally {
    toolResultBound.clearHostLimitsForTests()
  }
})

test('WHAT[host-boundary-015] TOOL_RESULT_BOUND_reads_a_lowered_host_ceiling_never_exceeding_it', () => {
  try {
    toolResultBound.setHostLimits(50, 200)
    const original = Array.from({ length: 500 }, (_, n) => `x${n}`).join('\n')
    const output = toolResultBound.bound(original)
    assert.ok(hostLines(output) <= 50)
    assert.ok(hostBytes(output) <= 200)
    assert.ok(output.endsWith('x499'))
  } finally {
    toolResultBound.clearHostLimitsForTests()
  }
})

test('WHAT[host-boundary-015] TOOL_RESULT_BOUND_ignores_absent_or_malformed_host_limits', () => {
  try {
    toolResultBound.setHostLimits(undefined, null)
    assert.equal(toolResultBound.HostMaxLines(), toolResultBound.DefaultHostMaxLines)
    assert.equal(toolResultBound.HostMaxBytes(), toolResultBound.DefaultHostMaxBytes)

    toolResultBound.setHostLimits('40', -7)
    assert.equal(toolResultBound.HostMaxLines(), toolResultBound.DefaultHostMaxLines)
    assert.equal(toolResultBound.HostMaxBytes(), toolResultBound.DefaultHostMaxBytes)

    // A ceiling below the marker's own cost cannot be honoured; keep the current.
    toolResultBound.setHostLimits(1, 10)
    assert.equal(toolResultBound.HostMaxLines(), toolResultBound.DefaultHostMaxLines)
    assert.equal(toolResultBound.HostMaxBytes(), toolResultBound.DefaultHostMaxBytes)
  } finally {
    toolResultBound.clearHostLimitsForTests()
  }
})

test('WHAT[host-boundary-015] TOOL_RESULT_BOUND_under_limit_is_identity', () => {
  const text = 'status = "completed"\nagent = "coder"\n'
  assert.equal(toolResultBound.bound(text), text)
})

test('WHAT[host-boundary-015] TOOL_RESULT_BOUND_over_lines_keeps_tail_and_stays_under_host', () => {
  const text = Array.from({ length: 2500 }, (_, index) => `L${index}`).join('\n')
  const output = toolResultBound.bound(text)
  assert.notEqual(output, text)
  assert.equal(output.slice(0, toolResultBound.Marker.length), toolResultBound.Marker)
  assert.equal(output.includes('L0\n'), false)
  assert.equal(output.includes('L2499'), true)
  assert.equal(hostLines(output) <= toolResultBound.HostMaxLines(), true)
  assert.equal(hostBytes(output) <= toolResultBound.HostMaxBytes(), true)
})

test('WHAT[host-boundary-015] TOOL_RESULT_BOUND_over_bytes_keeps_tail_and_stays_under_host', () => {
  const text = 'HEAD' + 'x'.repeat(60000) + 'TAIL'
  const output = toolResultBound.bound(text)
  assert.equal(output.slice(0, toolResultBound.Marker.length), toolResultBound.Marker)
  assert.equal(output.endsWith('TAIL'), true)
  assert.equal(output.includes('HEAD'), false)
  assert.equal(hostBytes(output) <= toolResultBound.HostMaxBytes(), true)
  assert.equal(hostLines(output) <= toolResultBound.HostMaxLines(), true)
})

test('WHAT[host-boundary-015] TOOL_RESULT_BOUND_exact_host_edge_is_identity', () => {
  const text = Array.from({ length: 2000 }, (_, index) => `r${index}`).join('\n')
  assert.equal(hostLines(text), 2000)
  assert.equal(hostBytes(text) < toolResultBound.HostMaxBytes(), true)
  assert.equal(toolResultBound.bound(text), text)
})

test('WHAT[host-boundary-015] custom_tool_output_undergoes_deterministic_tail_truncation', async () => {
  const registrations = []
  const fakeTool = (definition) => {
    registrations.push(definition)
    return { registered: definition.description, execute: definition.execute }
  }

  const registered = registerBounded(
    { tool: fakeTool },
    'demo',
    'a demo tool',
    () => Promise.resolve('x'.repeat(60000) + '\nfinal complete line'),
  )

  assert.equal(registrations.length, 1)
  assert.equal(registrations[0].description, 'a demo tool')

  // Execute is uncurried (args, context) and the result passes ToolResultBound.
  const output = await registered.execute({}, { sessionID: 'ses_demo' })
  assert.equal(output, toolResultBound.Marker + 'final complete line')
  assert.ok(hostBytes(output) <= toolResultBound.HostMaxBytes())
  assert.ok(hostLines(output) <= toolResultBound.HostMaxLines())
})

test('WHAT[host-boundary-015] truncation respects UTF-8 bytes and keeps complete recent lines', () => {
  const recent = '结论完成 ✅\n'
  const text = '中'.repeat(20000) + '\n' + recent
  const output = toolResultBound.bound(text)
  assert.equal(output, toolResultBound.Marker + recent)
  assert.ok(hostBytes(output) <= toolResultBound.HostMaxBytes())
  assert.equal(toolResultBound.bound(output), output, 'already bounded output is stable')
})

test('WHAT[host-boundary-015] an oversized single line preserves a valid Unicode suffix within the byte budget', () => {
  const output = toolResultBound.bound('😀'.repeat(20000) + '终')
  const count = Math.floor((toolResultBound.ContentMaxBytes() - Buffer.byteLength('终')) / 4)
  assert.equal(output, toolResultBound.Marker + '😀'.repeat(count) + '终')
  assert.equal(output.isWellFormed(), true)
  assert.ok(hostBytes(output) <= toolResultBound.HostMaxBytes())
})
