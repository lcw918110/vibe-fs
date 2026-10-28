import assert from 'node:assert/strict'
import test from 'node:test'
import { fileURLToPath } from 'node:url'
import { dirname, join, resolve } from 'node:path'
import { activateWithTiming } from '../../../dist/Composition/Omp/OmpExtension.js'
import { terminalEntry, withSecondHostTime } from './support/second-host-time.fixture.mjs'
import {
  RAW_TIME_SCAN_ROOTS,
  collectRawTimeScanEntries,
  scanRawTimeEntries,
  scanRawTimeProduction,
} from '../../../scripts/lib/raw-time-scan.mjs'

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..', '..')

const PRODUCTION_ROOT = join(ROOT, 'src', 'Wanxiangshu')

test('WHAT[time-capability-004] domain_application_session_contain_no_raw_time_tokens', () => {
  const hits = scanRawTimeProduction(ROOT)
  assert.equal(
    hits.length,
    0,
    `no raw wall-clock / timer token may appear in Domain/Application/Session: ${hits
      .map((h) => `${h.file}:${h.line} ${h.token}`)
      .join('; ')}`,
  )
})

test('WHAT[time-capability-004] collector_reads_the_whole_production_tree_before_allowlisting_adapters', () => {
  assert.deepEqual([...RAW_TIME_SCAN_ROOTS], ['.'])
  const entries = collectRawTimeScanEntries(PRODUCTION_ROOT, RAW_TIME_SCAN_ROOTS)
  assert.ok(entries.some((entry) => entry.file === 'Process/NodeTiming.fs'))
  assert.ok(entries.some((entry) => entry.file === 'Change/Program.fs'))
})

test('WHAT[time-capability-004] exact_mutation_is_detected_without_a_directory_allowlist_escape', () => {
  const source = [
    'module Synthetic',
    'let a = DateTimeOffset.UtcNow',
    'let b = DateTime.Now',
    'let c = DateTime.UtcNow',
    'let d = Date.now()',
    'do setTimeout (fun () -> ()) 1',
    'do! NodeTiming.timerTask 100',
  ].join('\n')

  assert.match(source, /DateTimeOffset\.UtcNow/)
  const dirty = scanRawTimeEntries([
    {
      file: 'Execution/Synthetic.fs',
      text: source,
    },
  ])
  assert.deepEqual(
    dirty.map((hit) => hit.token),
    ['DateTimeOffset.UtcNow', 'DateTime.Now', 'DateTime.UtcNow', 'Date.now', 'setTimeout', 'timerTask'],
  )

  const unlistedSibling = scanRawTimeEntries([
    {
      file: 'Execution/Delegation/Fork/OpenCode/Unlisted.fs',
      text: 'let now = DateTimeOffset.UtcNow\n',
    },
  ])
  assert.deepEqual(unlistedSibling.map((hit) => hit.token), ['DateTimeOffset.UtcNow'])
})

test('WHAT[time-capability-004] missing_production_root_fails_closed', () => {
  const missing = join(PRODUCTION_ROOT, '__missing_time_gate_root__')
  assert.throws(
    () => collectRawTimeScanEntries(missing, RAW_TIME_SCAN_ROOTS),
    /raw-time scan root does not exist/,
  )
})

test('WHAT[time-capability-004] comment-only mentions do not access ambient time', () => {
  assert.deepEqual(scanRawTimeEntries([
    { file: 'Domain/Doc.fs', text: '/// Do not read DateTimeOffset.UtcNow here.\nmodule Doc\n' },
  ]), [])
})

test('WHAT[time-capability-004] an adapter exception applies only to the exact file', () => {
  const entry = { file: 'Session/PhysicalClockAdapter.fs', text: 'let now = DateTimeOffset.UtcNow\n' }
  assert.deepEqual(scanRawTimeEntries([entry], { allowlist: [entry.file] }), [])
  assert.equal(scanRawTimeEntries([entry], { allowlist: ['Session/'] }).length, 1)
  assert.equal(scanRawTimeEntries([entry], { allowlist: [] }).length, 1)
})

test('WHAT[time-capability-004] the registered second-host tool returns an observed terminal without a timer delay', async () => {
  await withSecondHostTime(activateWithTiming, async fixture => {
    const host = await fixture.activate()
    assert.deepEqual([...host.tools.keys()], ['wxs_deliver', 'negotiate', 'resetScope', 'askPreviousScope', 'todowrite'])
    host.setEntries([terminalEntry('actual terminal reply')])
    const result = await host.execute('request for the child')
    assert.equal(result.isError, false)
    assert.deepEqual(result.content, [{ type: 'text', text: 'actual terminal reply' }])
    assert.deepEqual(host.sent, ['request for the child'])
    assert.equal(host.childIds.length, 1)
    assert.equal(host.reads, 1)
    assert.deepEqual(host.timer.delays, [])
    await host.shutdown()
    assert.equal(host.timer.disposeCalls, 1)
    assert.deepEqual(host.errors, [])
  })
})

test('WHAT[time-capability-004] the registered second-host tool polls again at 250 virtual milliseconds and never at 249', async () => {
  await withSecondHostTime(activateWithTiming, async fixture => {
    const host = await fixture.activate()
    const pending = host.execute('wait for a real terminal')
    let settled = false
    pending.then(() => { settled = true }, () => { settled = true })
    const delay = await host.nextDelay()
    await delay.awaited
    assert.equal(delay.milliseconds, 250)
    assert.equal(host.reads, 1)
    host.setEntries([terminalEntry('reply after polling')])
    fixture.advance(249)
    assert.equal(host.reads, 1)
    assert.equal(settled, false)
    fixture.advance(1)
    const result = await pending
    assert.equal(result.isError, false)
    assert.deepEqual(result.content, [{ type: 'text', text: 'reply after polling' }])
    assert.equal(host.reads, 2)
    assert.deepEqual(host.timer.delays.map(item => item.milliseconds), [250])
    assert.equal(delay.cancelCalls, 1)
    assert.deepEqual(host.errors, [])
  })
})

test('WHAT[time-capability-004] the registered second-host tool times out at its original 120000 virtual milliseconds without inventing a reply', async () => {
  await withSecondHostTime(activateWithTiming, async fixture => {
    const host = await fixture.activate()
    const pending = host.execute('a child that never answers')
    let settled = false
    pending.then(() => { settled = true }, () => { settled = true })
    const first = await host.nextDelay()
    await first.awaited
    fixture.advance(119750)
    const second = await host.nextDelay()
    await second.awaited
    assert.equal(host.reads, 2)
    fixture.advance(249)
    assert.equal(host.reads, 2)
    assert.equal(settled, false)
    fixture.advance(1)
    const result = await pending
    assert.equal(result.isError, true)
    assert.match(result.content[0].text, /timed out after 120s/)
    assert.match(result.content[0].text, /observed 0 message\(s\).*no terminal assistant reply/)
    assert.equal(host.reads, 3)
    assert.deepEqual(host.timer.delays.map(item => item.milliseconds), [250, 250])
    assert.deepEqual(host.timer.delays.map(item => item.cancelCalls), [1, 1])
    assert.deepEqual(host.errors, [])
  })
})

test('WHAT[time-capability-004] a real terminal observed at the deadline remains a reply rather than a timeout', async () => {
  await withSecondHostTime(activateWithTiming, async fixture => {
    const host = await fixture.activate()
    const pending = host.execute('a reply at the deadline')
    const delay = await host.nextDelay()
    await delay.awaited
    host.setEntries([terminalEntry('observed at the deadline')])
    fixture.advance(120000)
    const result = await pending
    assert.equal(result.isError, false)
    assert.deepEqual(result.content, [{ type: 'text', text: 'observed at the deadline' }])
    assert.equal(host.reads, 2)
    assert.deepEqual(host.timer.delays.map(item => item.milliseconds), [250])
    assert.equal(delay.cancelCalls, 1)
    assert.deepEqual(host.errors, [])
  })
})

test('WHAT[time-capability-004] separate real Host activations own separate timers and disposing the first leaves the second usable', async () => {
  await withSecondHostTime(activateWithTiming, async fixture => {
    const first = await fixture.activate()
    const second = await fixture.activate()
    assert.equal(fixture.timers.length, 2)
    assert.notEqual(first.timer.port, second.timer.port)
    await first.shutdown()
    assert.equal(first.timer.disposeCalls, 1)
    assert.equal(second.timer.disposeCalls, 0)
    const pending = second.execute('the second activation remains live')
    const delay = await second.nextDelay()
    await delay.awaited
    second.setEntries([terminalEntry('second activation reply')])
    fixture.advance(250)
    const result = await pending
    assert.equal(result.isError, false)
    assert.deepEqual(result.content, [{ type: 'text', text: 'second activation reply' }])
    assert.equal(second.reads, 2)
    await second.shutdown()
    assert.equal(second.timer.disposeCalls, 1)
    assert.deepEqual(first.errors, [])
    assert.deepEqual(second.errors, [])
  })
})

test('WHAT[time-capability-004] a failed real Host activation disposes its timer and retains the original registration failure', async () => {
  await withSecondHostTime(activateWithTiming, async fixture => {
    const original = new Error('second-host registration original cause')
    const host = await fixture.activate({ registrationFailure: original })
    assert.equal(host.tools.size, 0)
    assert.equal(host.timer.disposeCalls, 1)
    assert.deepEqual(host.timer.delays, [])
    assert.equal(host.errors.length, 1)
    assert.equal(host.errors[0].message, 'wanxiangshu activation failed')
    assert.match(host.errors[0].detail.error, /second-host registration original cause/)
    await host.shutdown()
    assert.equal(host.timer.disposeCalls, 1)
  })
})
