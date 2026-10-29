import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");

const {
  estimate,
  outputCreate,
  outputAddStdout,
  outputAddStderr,
  outputBuildResult,
  outputView,
  spoolChunkCount,
  spoolReadPath,
  spoolDelete,
  command,
  context,
  createCancellationToken,
  run,
} = await import('../../../dist/Process/Surface.js')
const text = (value) => new TextEncoder().encode(value)
const decode = (bytes) => new TextDecoder().decode(Uint8Array.from(bytes))
const create = (seconds, bytes) => outputCreate(estimate(seconds, bytes, 'medium'))
const readSpool = async (spool) => {
  // Chunks split at byte offsets; decode once so a cut multi-byte character survives.
  const chunks = await spoolReadPath(spool.path)
  return decode(chunks.flatMap(chunk => Array.from(chunk)))
}

test('WHAT[process-execution-009] the real process host delivers every byte into the bounded spool', async t => {
  const size = 32 * 1024 * 1024
  const suffix = '\n原始输出🧭\n'
  const script = `process.stdout.write(Buffer.alloc(${size}, 0x61));process.stdout.write(${JSON.stringify(suffix)});`
  const result = await run(
    command(process.execPath, ['-e', script], process.cwd(), undefined),
    estimate(10, 1024, 'medium'),
    context(process.cwd(), 60_000),
    createCancellationToken(false),
  )
  assert.equal(result.ok, true)
  assert.equal(result.value.kind, 'Spooled')
  t.after(() => spoolDelete(result.value.spoolPath))
  assert.equal(result.value.exitCode, 0)
  assert.equal(result.value.totalBytes, size + Buffer.byteLength(suffix))
  assert.equal(await readSpool({ path: result.value.spoolPath }), 'a'.repeat(size) + suffix)
})

test('WHAT[process-execution-009] EXEC_011_collector_concatenates_stdout_stderr_utf8', () => {
  const collector = create(10, 1000)
  outputAddStdout(collector, text('hello '))
  outputAddStderr(collector, text('warn\n'))
  outputAddStdout(collector, text('世界'))
  assert.deepEqual(outputBuildResult(collector, 7), {
    kind: 'Completed',
    exitCode: 7,
    stdout: 'hello 世界',
    stderr: 'warn\n',
    spooled: false,
  })
})
test('WHAT[process-execution-009] EXEC_011_collector_ignores_empty_chunks', () => {
  const collector = create(10, 1000)
  outputAddStdout(collector, new Uint8Array(0))
  outputAddStdout(collector, undefined)
  assert.equal(outputView(collector).bytesObserved, 0)
  assert.equal(outputBuildResult(collector, 0).stdout, '')
})
test('WHAT[process-execution-009] EXEC_011_collector_spools_when_byte_count_crosses_threshold', async () => {
  const collector = create(10, 2)
  outputAddStdout(collector, text('abcdefg'))
  assert.equal(outputView(collector).spooled, true, 'crossing the threshold must start a spool')

  const outcome = outputBuildResult(collector, 3)
  assert.equal(outcome.kind, 'Spooled')
  assert.equal(outcome.exitCode, 3)
  assert.equal(outcome.totalBytes, 7)
  assert.equal(outcome.chunkCount, 1)
  assert.equal(await readSpool({ path: outcome.spoolPath }), 'abcdefg')
  spoolDelete(outcome.spoolPath)
})
test('WHAT[process-execution-009] EXEC_011_collector_spool_accumulates_later_chunks', async () => {
  const collector = create(10, 2)
  outputAddStdout(collector, text('abcdefg'))
  outputAddStderr(collector, text('x'))

  const outcome = outputBuildResult(collector, 4)
  assert.equal(outcome.kind, 'Spooled')
  assert.equal(outcome.totalBytes, 8)
  assert.equal(outcome.chunkCount, 1)
  assert.equal(await readSpool({ path: outcome.spoolPath }), 'abcdefgx')
  spoolDelete(outcome.spoolPath)
})
test('WHAT[process-execution-009] EXEC_011_collector_spooled_buffers_are_cleared', () => {
  const collector = create(10, 2)
  outputAddStdout(collector, text('abcdefg'))
  const view = outputView(collector)
  assert.equal(view.bytesHeld, 0, 'once spooled, no output stays in memory')
  spoolDelete(outputBuildResult(collector, 0).spoolPath)
})
test('WHAT[process-execution-009] EXEC_011_spool_chunk_count_rounds_up', () => {
  assert.equal(spoolChunkCount(0), 0)
  assert.equal(spoolChunkCount(1), 1)
  assert.equal(spoolChunkCount(204800), 1)
  assert.equal(spoolChunkCount(204801), 2)
})
test('WHAT[process-execution-009] EXEC_011_spool_keeps_every_byte_of_a_large_real_stream', async () => {
  const collector = create(10, 1024)
  const expected = []
  for (let i = 0; i < 64; i++) {
    const chunk = text(`${i}:${'中'.repeat(4000)}\n`)
    expected.push(chunk)
    if (i % 5 === 0) outputAddStderr(collector, chunk)
    else outputAddStdout(collector, chunk)
    assert.ok(outputView(collector).bytesHeld <= 1024, 'memory stays bounded while output grows')
  }
  const outcome = outputBuildResult(collector, 0)
  assert.equal(outcome.kind, 'Spooled')
  const all = Buffer.concat(expected)
  assert.equal(outcome.totalBytes, all.length)
  assert.equal(await readSpool({ path: outcome.spoolPath }), all.toString('utf8'))
  spoolDelete(outcome.spoolPath)
})
test('WHAT[process-execution-009] EXEC_011_late_output_after_build_result_is_not_part_of_the_outcome', () => {
  const collector = create(10, 1000)
  outputAddStdout(collector, text('done'))
  assert.equal(outputBuildResult(collector, 0).stdout, 'done')
  outputAddStdout(collector, text('late'))
  assert.equal(outputView(collector).bytesObserved, 4)
  assert.equal(outputBuildResult(collector, 0).stdout, 'done')
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");

const {
  sessionCreate,
  sessionView,
  sessionSetClosed,
  sessionSetBackend,
  sessionPushPending,
  sessionResolveExit,
  ptyCommandRead,
} = await import('../../../dist/Process/Surface.js')
const command = ptyCommandRead()

test('WHAT[process-execution-009] PTY_SESSION_create_sets_id_and_backend', () => {
  const backend = { pid: 1234 }
  const session = sessionCreate('pty-abc', backend)
  const view = sessionView(session)
  assert.equal(view.ptyId, 'pty-abc')
  assert.equal(view.backend, backend)
})
test('WHAT[process-execution-009] PTY_SESSION_create_defaults_open_empty_and_pending', () => {
  const view = sessionView(sessionCreate('pty-def', null))
  assert.equal(view.backend, null)
  assert.equal(view.closed, false)
  assert.equal(view.pendingCount, 0)
  assert.equal(view.exitPending, true)
})
test('WHAT[process-execution-009] PTY_SESSION_exit_completion_starts_unresolved', () => {
  const session = sessionCreate('pty-ghi', null)
  assert.equal(sessionView(session).exitPending, true)
  sessionResolveExit(session)
  assert.equal(sessionView(session).exitPending, false)
})
test('WHAT[process-execution-009] PTY_SESSION_mutable_state_roundtrips', () => {
  const session = sessionCreate('pty-jkl', null)

  sessionSetClosed(session, true)
  assert.equal(sessionView(session).closed, true)

  const backend = { pid: 42 }
  sessionSetBackend(session, backend)
  assert.equal(sessionView(session).backend, backend)

  sessionPushPending(session, command)
  assert.equal(sessionView(session).pendingCount, 1)
})
}

{
  const assert = (await import('node:assert/strict')).default
  const runtime = await import('../../../dist/Process/Surface.js')
  test('WHAT[process-execution-009] the output threshold reflects a nonnegative requested budget', () => {
    assert.equal(runtime.outputThreshold(0), 0)
    assert.equal(runtime.outputThreshold(-5), 0)
    assert.equal(runtime.outputThreshold(10), 10)
  })
}
