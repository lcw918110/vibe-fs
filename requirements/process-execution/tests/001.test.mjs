import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");

const {
  bytes,
  newId,
  ptyIdView,
  registerParentAbort,
  unregisterParentAbort,
  abortParent,
} = await import('../../../dist/Process/Surface.js')

test('WHAT[process-execution-001] PTY_API_bytes_encodes_utf8', () => {
  assert.deepEqual(bytes('abc'), [97, 98, 99])
  assert.deepEqual(bytes('é'), [0xc3, 0xa9])
  assert.deepEqual(bytes('雪'), [0xe9, 0x9b, 0xaa])
  assert.deepEqual(bytes(''), [])
})
test('WHAT[process-execution-001] PTY_API_new_id_has_pty_prefix_and_eight_hex_chars', () => {
  assert.match(ptyIdView(newId()), /^pty-[0-9a-f]{8}$/)
})
test('WHAT[process-execution-001] PTY_API_new_id_is_unique_per_call', () => {
  const seen = new Set(Array.from({ length: 64 }, () => ptyIdView(newId())))
  assert.equal(seen.size, 64)
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");

const {
  backendCreatePort,
  ptyId,
  ptyIdView,
  ptyCommandWrite,
  ptyCommandSignal,
  portAddMailboxSender,
  portFork,
  portExists,
  portKnown,
  portSend,
  portRead,
  portCloseAll,
} = await import('../../../dist/Process/Surface.js')
const write = ptyCommandWrite(new TextEncoder().encode('x'))
const signal = ptyCommandSignal('KILL')
const failure = (error) => ({ ok: false, error })
const takeCompletions = (port, count = 1) => new Promise((resolve) => {
  const items = []
  portAddMailboxSender(port, (item) => {
    items.push(item)
    if (items.length === count) resolve(items)
  })
})

test('WHAT[process-execution-001] BACKEND_createPort_returns_a_working_port', () => {
  const port = backendCreatePort()
  assert.ok(port, 'port exists')
  assert.equal(portExists(port, ptyId('pty-b')), false)
})
test('WHAT[process-execution-001] BACKEND_failed_fork_leaves_unknown_active_but_known_closed', async () => {
  const port = backendCreatePort()
  const completion = takeCompletions(port)
  const pid = portFork(port, 'echo hi', 'devops', ptyId('pty-fa'), undefined)
  await completion
  assert.equal(portExists(port, pid), false)
  assert.equal(portKnown(port, pid), true)
})
test('WHAT[process-execution-001] BACKEND_generated_id_also_fails_cleanly', async () => {
  const port = backendCreatePort()
  const completion = takeCompletions(port)
  const pid = portFork(port, 'echo hi', 'devops', undefined, undefined)
  const [item] = await completion
  assert.match(ptyIdView(pid), /^pty-[0-9a-f]{8}$/)
  assert.equal(item.ptyId, ptyIdView(pid))
})
test('WHAT[process-execution-001] BACKEND_send_after_failed_fork_reports_closed', async () => {
  const port = backendCreatePort()
  const completion = takeCompletions(port)
  const pid = portFork(port, 'echo hi', 'devops', ptyId('pty-wc'), undefined)
  await completion
  assert.deepEqual(await portSend(port, pid, write), failure('PTY closed'))
})
test('WHAT[process-execution-001] BACKEND_send_on_never_forked_id_is_unknown', async () => {
  const port = backendCreatePort()
  assert.deepEqual(await portSend(port, ptyId('pty-uk'), write), failure('Unknown PTY id: pty-uk'))
})
test('WHAT[process-execution-001] BACKEND_read_after_failed_fork_returns_empty_closed', async () => {
  const port = backendCreatePort()
  const completion = takeCompletions(port)
  const pid = portFork(port, 'echo hi', 'devops', ptyId('pty-rf'), undefined)
  await completion
  assert.deepEqual(await portRead(port, pid), { ok: true, value: { output: '', closed: true } })
})
test('WHAT[process-execution-001] BACKEND_read_never_forked_is_an_error', async () => {
  const port = backendCreatePort()
  assert.deepEqual(await portRead(port, ptyId('pty-rn')), failure('Unknown PTY id: pty-rn'))
})
test('WHAT[process-execution-001] BACKEND_close_all_with_nothing_active_resolves', async () => {
  await portCloseAll(backendCreatePort(), 0)
})
test('WHAT[process-execution-001] BACKEND_ports_are_isolated_from_each_other', async () => {
  const a = backendCreatePort()
  const b = backendCreatePort()
  const completion = takeCompletions(a)
  const pid = portFork(a, 'echo hi', 'devops', ptyId('pty-iso'), undefined)
  await completion
  assert.equal(portKnown(a, pid), true)
  assert.equal(portKnown(b, pid), false)
  assert.deepEqual(await portSend(b, pid, write), failure('Unknown PTY id: pty-iso'))
})
test('WHAT[process-execution-001] BACKEND_signal_on_failed_id_is_rejected_as_closed', async () => {
  const port = backendCreatePort()
  const completion = takeCompletions(port)
  const pid = portFork(port, 'echo hi', 'devops', ptyId('pty-sg'), undefined)
  await completion
  assert.deepEqual(await portSend(port, pid, signal), failure('PTY closed'))
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");

const {
  createPtyPort,
  ptyId,
  ptyIdView,
  ptyCommandWrite,
  ptyCommandSignal,
  ptyCommandResize,
  ptyCommandRead,
  portAddMailboxSender,
  portFork,
  portExists,
  portKnown,
  portSend,
  portRead,
  portReadResult,
  portFailRead,
  portRegisterExitTask,
  portComplete,
  portCompleteAborted,
  portClose,
  portCloseAll,
  portList,
} = await import('../../../dist/Process/Surface.js')
const agent = { Name: 'devops' }
const bytes = new TextEncoder().encode('hi')
const write = ptyCommandWrite(bytes)
const signalOf = (name) => ptyCommandSignal(name)
const id = (value) => ptyId(value)
const forkDefault = (port, value, command = 'echo hi') =>
  portFork(port, command, 'devops', value === undefined ? undefined : id(value), undefined)
const success = { ok: true, value: undefined }
const failure = (error) => ({ ok: false, error })
const exitSignal = () => {
  let resolve
  const promise = new Promise((r) => { resolve = r })
  return { promise, resolve }
}
const completedItem = (port, pid, outcome) => {
  const got = []
  portAddMailboxSender(port, (item) => got.push(item))
  portComplete(port, pid, outcome)
  return got[0]
}

test('WHAT[process-execution-001] PORT_ctor_defaults_are_safe_and_functional', async () => {
  const port = createPtyPort({})
  const pid = forkDefault(port, 'pty-x')
  assert.equal(portExists(port, pid), true)
  assert.deepEqual(await portSend(port, pid, write), success)
})
test('WHAT[process-execution-001] PORT_ctor_keeps_supplied_sender_and_handler', async () => {
  const seen = []
  const receivedEvents = []
  const sender = (item) => receivedEvents.push(item)
  const handler = async (_pid, command) => {
    if (command.kind !== 'Spawn') seen.push(command.kind)
    return success
  }
  const port = createPtyPort({ sender, handler })
  const pid = forkDefault(port, 'pty-x')
  assert.deepEqual(await portSend(port, pid, write), success)
  assert.deepEqual(seen, ['Write'])
  assert.equal(portList(port).ptys.length, 1)
  assert.equal(portList(port).ptys[0].agent, 'devops')

  portComplete(port, pid, { ok: true, value: 'done' })
  assert.equal(receivedEvents.length, 1)
  assert.equal(receivedEvents[0].kind, 'PtyExited')
  assert.equal(receivedEvents[0].ptyId, 'pty-x')
  assert.equal(receivedEvents[0].outcome, 'done')
  assert.equal(receivedEvents[0].closed, true)
})
test('WHAT[process-execution-001] PORT_fork_generates_pty_id_and_dispatches_spawn', async () => {
  const seen = []
  const port = createPtyPort({
    handler: async (pid, command) => {
      seen.push([pid, command.kind, command.command, command.cwd])
      return success
    },
  })
  const pid = forkDefault(port, undefined, 'sleep 1')
  const value = ptyIdView(pid)
  assert.match(value, /^pty-[0-9a-f]{8}$/)
  assert.deepEqual(seen, [[value, 'Spawn', 'sleep 1', '']])
  assert.equal(portExists(port, pid), true)
})
test('WHAT[process-execution-001] PORT_fork_honors_explicit_id_and_cwd', async () => {
  const seen = []
  const port = createPtyPort({
    handler: async (pid, command) => {
      seen.push([pid, command.command, command.cwd])
      return success
    },
  })
  const pid = portFork(port, 'ls -la', 'devops', id('pty-custom'), '/srv')
  assert.equal(ptyIdView(pid), 'pty-custom')
  assert.deepEqual(seen, [['pty-custom', 'ls -la', '/srv']])
})
test('WHAT[process-execution-001] PORT_fork_twice_on_same_id_replaces_the_handle', () => {
  const port = createPtyPort({})
  forkDefault(port, 'pty-rf', 'first')
  forkDefault(port, 'pty-rf', 'second')
  assert.equal(portList(port).ptys.length, 1)
  assert.equal(portList(port).ptys[0].command, 'second')
})
test('WHAT[process-execution-001] PORT_exists_and_known_track_active_and_closed', () => {
  const port = createPtyPort({})
  const unknown = id('pty-ne')
  assert.equal(portExists(port, unknown), false)
  assert.equal(portKnown(port, unknown), false)

  const pid = forkDefault(port, 'pty-ek')
  assert.equal(portExists(port, pid), true)
  assert.equal(portKnown(port, pid), true)

  portComplete(port, pid, { ok: true, value: 'bye' })
  assert.equal(portExists(port, pid), false)
  assert.equal(portKnown(port, pid), true)
})
test('WHAT[process-execution-001] PORT_send_unknown_and_closed_ids_fail_with_distinct_reasons', async () => {
  const port = createPtyPort({})
  assert.deepEqual(await portSend(port, id('pty-un'), write), failure('Unknown PTY id: pty-un'))

  const pid = forkDefault(port, 'pty-cl')
  portComplete(port, pid, { ok: true, value: 'done' })
  assert.deepEqual(await portSend(port, pid, write), failure('PTY closed'))
})
test('WHAT[process-execution-001] PORT_send_forwards_command_and_propagates_handler_outcomes', async () => {
  const seen = []
  const port = createPtyPort({
    handler: async (_pid, command) => {
      if (command.kind !== 'Spawn') seen.push(command.kind)
      if (command.kind === 'Write') return failure('disk full')
      if (command.kind === 'Signal') throw new Error('signal exploded')
      return success
    },
  })
  const pid = forkDefault(port, 'pty-sd')

  assert.deepEqual(await portSend(port, pid, ptyCommandResize(10, 10)), success)
  assert.deepEqual(await portSend(port, pid, write), failure('disk full'))
  assert.deepEqual(await portSend(port, pid, signalOf('HUP')), failure('signal exploded'))
  assert.deepEqual(seen, ['Resize', 'Write', 'Signal'])
})
test('WHAT[process-execution-001] PORT_read_unknown_id_is_an_error', async () => {
  assert.deepEqual(await portRead(createPtyPort({}), id('pty-ru')), failure('Unknown PTY id: pty-ru'))
})
test('WHAT[process-execution-001] PORT_read_after_close_returns_empty_closed_without_handling', async () => {
  const seen = []
  const port = createPtyPort({ handler: async (_pid, command) => { if (command.kind !== 'Spawn') seen.push(command.kind); return success } })
  const pid = forkDefault(port, 'pty-rc')
  portComplete(port, pid, { ok: true, value: 'done' })
  assert.deepEqual(await portRead(port, pid), { ok: true, value: { output: '', closed: true } })
  assert.deepEqual(seen, [])
})
test('WHAT[process-execution-001] PORT_read_parks_waiter_and_read_result_resolves_it', async () => {
  const seen = []
  const port = createPtyPort({ handler: async (_pid, command) => { if (command.kind !== 'Spawn') seen.push(command.kind); return success } })
  const pid = forkDefault(port, 'pty-pr')
  const read = portRead(port, pid)
  assert.deepEqual(seen, ['Read'])
  portReadResult(port, pid, 'buffered', false)
  assert.deepEqual(await read, { ok: true, value: { output: 'buffered', closed: false } })
})
test('WHAT[process-execution-001] PORT_read_result_can_report_closed_and_reparks_after_resolution', async () => {
  const port = createPtyPort({})
  const pid = forkDefault(port, 'pty-pr2')
  const first = portRead(port, pid)
  portReadResult(port, pid, 'tail', true)
  assert.deepEqual(await first, { ok: true, value: { output: 'tail', closed: true } })

  const second = portRead(port, pid)
  portReadResult(port, pid, 'again', false)
  assert.deepEqual(await second, { ok: true, value: { output: 'again', closed: false } })
})
test('WHAT[process-execution-001] PORT_concurrent_read_fails_fast_without_unparking', async () => {
  const port = createPtyPort({})
  const pid = forkDefault(port, 'pty-cc')
  const first = portRead(port, pid)
  assert.deepEqual(await portRead(port, pid), failure('PTY read already in progress'))
  portReadResult(port, pid, 'kept', false)
  assert.deepEqual(await first, { ok: true, value: { output: 'kept', closed: false } })
})
test('WHAT[process-execution-001] PORT_fail_read_resolves_parked_reader_with_error', async () => {
  const port = createPtyPort({})
  const pid = forkDefault(port, 'pty-fr')
  const read = portRead(port, pid)
  portFailRead(port, pid, 'backend died')
  assert.deepEqual(await read, failure('backend died'))
})
test('WHAT[process-execution-001] PORT_read_result_and_fail_read_without_waiter_are_noops', () => {
  const port = createPtyPort({})
  const pid = forkDefault(port, 'pty-nw')
  portReadResult(port, pid, 'orphan', false)
  portFailRead(port, pid, 'orphan')
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { spawn } = await import("node:child_process");

const {
  supervisorCreate,
  supervisorAdd,
  supervisorTryGet,
  supervisorGet,
  supervisorRemove,
  supervisorList,
  supervisorSignalName,
  supervisorEnsureSpawn,
  supervisorSpawn,
  supervisorSetSpawn,
  supervisorFailPending,
  supervisorTakePending,
  supervisorDropPending,
  supervisorApplyLive,
  supervisorAttach,
  supervisorCancelAll,
  supervisorPendingEntries,
  pendingEntryView,
  pendingResolve,
  sessionCreate,
  sessionView,
  sessionSetClosed,
  sessionAppendOutput,
  sessionPushPending,
  sessionPushPendingTask,
  createPtyPort,
  portFork,
  portRead,
  portReadResult,
  portAddMailboxSender,
  ptyId,
  ptyIdView,
  ptyCommandRead,
  ptyCommandWrite,
  ptyCommandSignal,
  ptyCommandResize,
  ptyCommandSpawn,
} = await import('../../../dist/Process/Surface.js')
const id = (value) => ptyId(value)
const port = () => createPtyPort({})
const resultError = (reason) => ({ ok: false, error: reason })
const resultOk = { ok: true, value: undefined }
const settle = () => new Promise((resolve) => setImmediate(resolve))
const child = () => spawn(process.execPath, ['-e', 'setInterval(() => {}, 1000)'])
const killChild = (value) => {
  if (value && value.exitCode === null && value.signalCode === null) value.kill('SIGKILL')
}
const died = (value) => value.exitCode !== null || value.signalCode !== null
const waitExit = (value) =>
  Promise.race([
    new Promise((resolve) => value.once('exit', resolve)),
    new Promise((resolve) => setTimeout(resolve, 2_000)),
  ])
const fakeTerm = (pid) => {
  const term = {
    pid,
    dataCb: null,
    exitCb: null,
    writes: [],
    resizes: [],
    onData: (cb) => { term.dataCb = cb },
    onExit: (cb) => { term.exitCb = cb },
    write: (text) => term.writes.push(text),
    resize: (width, height) => term.resizes.push([width, height]),
  }
  return term
}
const portWith = (value) => {
  const p = port()
  portFork(p, 'echo hi', 'devops', id(value), undefined)
  return p
}

test('WHAT[process-execution-001] SUPERVISOR_signalName_maps_every_signal_to_a_kill_name', () => {
  for (const [wire, expected] of [
    ['TERM', 'SIGTERM'], ['KILL', 'SIGKILL'], ['INT', 'SIGINT'], ['HUP', 'SIGHUP'],
    ['QUIT', 'SIGQUIT'], ['USR1', 'SIGUSR1'], ['USR2', 'SIGUSR2'],
  ]) assert.equal(supervisorSignalName(wire), expected)
})
test('WHAT[process-execution-001] SUPERVISOR_ensureSpawn_reuses_one_loader_and_faults_without_bun_pty', async () => {
  const supervisor = supervisorCreate()
  const first = supervisorEnsureSpawn(supervisor)
  const second = supervisorEnsureSpawn(supervisor)
  assert.equal(first, second, 'loader task is cached')
  await assert.rejects(first)
  assert.throws(() => supervisorSpawn(supervisor, 'echo hi', ''), /bun-pty is not loaded/)
})
test('WHAT[process-execution-001] SUPERVISOR_spawnSync_fails_fast_when_loader_never_ran', () => {
  assert.throws(() => supervisorSpawn(supervisorCreate(), 'echo hi', ''), /bun-pty is not loaded/)
})
test('WHAT[process-execution-001] SUPERVISOR_spawnSync_invokes_sh_lc_with_fixed_options', async () => {
  const supervisor = supervisorCreate()
  let seen
  supervisorSetSpawn(supervisor, (shell, args, options) => {
    seen = [shell, args, options]
    return { pid: 4242 }
  })
  const term = await supervisorSpawn(supervisor, 'echo hi', '/tmp/work')
  assert.equal(term.pid, 4242)
  assert.equal(seen[0], 'sh')
  assert.deepEqual(seen[1], ['-lc', 'echo hi'])
  assert.equal(seen[2].name, 'xterm-256color')
  assert.equal(seen[2].cols, 80)
  assert.equal(seen[2].rows, 24)
  assert.equal(seen[2].cwd, '/tmp/work')
})
test('WHAT[process-execution-001] SUPERVISOR_spawnSync_defaults_cwd_to_process_cwd', async () => {
  const supervisor = supervisorCreate()
  let seenCwd
  supervisorSetSpawn(supervisor, (_shell, _args, options) => {
    seenCwd = options.cwd
    return {}
  })
  await supervisorSpawn(supervisor, 'ls', '')
  assert.equal(seenCwd, process.cwd())
})
test('WHAT[process-execution-001] SUPERVISOR_applyLive_closed_session_short_circuits_ok', async () => {
  const supervisor = supervisorCreate()
  const session = sessionCreate('pty-c', null)
  sessionSetClosed(session, true)
  supervisorAdd(supervisor, id('pty-c'), session)
  assert.deepEqual(await supervisorApplyLive(supervisor, port(), id('pty-c'), ptyCommandRead()), resultOk)
})
test('WHAT[process-execution-001] SUPERVISOR_applyLive_write_forwards_utf8_to_backend', async () => {
  const supervisor = supervisorCreate()
  const writes = []
  const backend = { write: (text) => writes.push(text) }
  supervisorAdd(supervisor, id('pty-w'), sessionCreate('pty-w', backend))
  const result = await supervisorApplyLive(supervisor, port(), id('pty-w'), ptyCommandWrite(new TextEncoder().encode('héllo')))
  assert.deepEqual(result, resultOk)
  assert.deepEqual(writes, ['héllo'])
})
test('WHAT[process-execution-001] SUPERVISOR_applyLive_write_backend_error_becomes_error_result', async () => {
  const supervisor = supervisorCreate()
  const backend = { write: () => { throw new Error('EPIPE') } }
  supervisorAdd(supervisor, id('pty-we'), sessionCreate('pty-we', backend))
  assert.deepEqual(await supervisorApplyLive(supervisor, port(), id('pty-we'), ptyCommandWrite(new Uint8Array(0))), resultError('EPIPE'))
})
test('WHAT[process-execution-001] SUPERVISOR_applyLive_read_drains_buffer_into_port', async () => {
  const supervisor = supervisorCreate()
  const p = portWith('pty-re')
  const session = sessionCreate('pty-re', {})
  sessionAppendOutput(session, 'partial output')
  supervisorAdd(supervisor, id('pty-re'), session)

  assert.deepEqual(await supervisorApplyLive(supervisor, p, id('pty-re'), ptyCommandRead()), resultOk)
  assert.equal(sessionView(session).output, '', 'buffer drained')
  const read = portRead(p, id('pty-re'))
  portReadResult(p, id('pty-re'), 'after', false)
  assert.deepEqual(await read, { ok: true, value: { output: 'after', closed: false } })
})
test('WHAT[process-execution-001] SUPERVISOR_applyLive_resize_swallows_backend_errors', async () => {
  const supervisor = supervisorCreate()
  const resizes = []
  const backend = { resize: (width, height) => { resizes.push([width, height]); throw new Error('nope') } }
  supervisorAdd(supervisor, id('pty-z'), sessionCreate('pty-z', backend))
  assert.deepEqual(await supervisorApplyLive(supervisor, port(), id('pty-z'), ptyCommandResize(120, 40)), resultOk)
  assert.deepEqual(resizes, [[120, 40]])
})
test('WHAT[process-execution-001] SUPERVISOR_applyLive_spawn_on_live_backend_is_a_noop', async () => {
  const supervisor = supervisorCreate()
  supervisorAdd(supervisor, id('pty-sp'), sessionCreate('pty-sp', {}))
  assert.deepEqual(await supervisorApplyLive(supervisor, port(), id('pty-sp'), ptyCommandSpawn('x', '')), resultOk)
})
test('WHAT[process-execution-001] SUPERVISOR_applyLive_write_without_backend_parks_until_resolved', async () => {
  const supervisor = supervisorCreate()
  const session = sessionCreate('pty-p', null)
  supervisorAdd(supervisor, id('pty-p'), session)
  const pending = supervisorApplyLive(supervisor, port(), id('pty-p'), ptyCommandWrite(new Uint8Array(0)))
  const entries = supervisorTakePending(supervisor, id('pty-p'))
  assert.equal(pendingEntryView(supervisorPendingEntries(entries)[0]).command.kind, 'Write')
  pendingResolve(entries, 0, resultOk)
  assert.deepEqual(await pending, resultOk)
})
test('WHAT[process-execution-001] SUPERVISOR_applyLive_parked_write_resolves_with_error', async () => {
  const supervisor = supervisorCreate()
  const session = sessionCreate('pty-pe', null)
  supervisorAdd(supervisor, id('pty-pe'), session)
  const pending = supervisorApplyLive(supervisor, port(), id('pty-pe'), ptyCommandWrite(new Uint8Array(0)))
  const entries = supervisorTakePending(supervisor, id('pty-pe'))
  pendingResolve(entries, 0, resultError('backend vanished'))
  assert.deepEqual(await pending, resultError('backend vanished'))
})
test('WHAT[process-execution-001] SUPERVISOR_applyLive_non_write_commands_without_backend_return_ok_immediately', async () => {
  const supervisor = supervisorCreate()
  const session = sessionCreate('pty-nb', null)
  supervisorAdd(supervisor, id('pty-nb'), session)
  for (const command of [ptyCommandRead(), ptyCommandSignal('HUP'), ptyCommandResize(10, 10)]) {
    assert.deepEqual(await supervisorApplyLive(supervisor, port(), id('pty-nb'), command), resultOk)
  }
  assert.equal(sessionView(session).pendingCount, 3)
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");

const {
  signalParse,
  ptySignalView,
  ptyCommandSpawn,
  ptyCommandWrite,
  ptyCommandRead,
  ptyCommandSignal,
  ptyCommandResize,
  ptyCommandView,
  ptyId,
  ptyIdView,
  createPtyPort,
  portFork,
  portComplete,
  portRead,
  portReadResult,
  portList,
} = await import('../../../dist/Process/Surface.js')

test('WHAT[process-execution-001] PTY_TYPES_tryParse_accepts_every_supported_signal_name', () => {
  const expected = [
    ['TERM', 'SIGTERM'],
    ['KILL', 'SIGKILL'],
    ['INT', 'SIGINT'],
    ['HUP', 'SIGHUP'],
    ['QUIT', 'SIGQUIT'],
    ['USR1', 'SIGUSR1'],
    ['USR2', 'SIGUSR2'],
  ]
  for (const [wire, name] of expected) {
    assert.deepEqual(signalParse(wire), { ok: true, value: name }, wire)
    assert.equal(ptySignalView(wire), name)
  }
})
test('WHAT[process-execution-001] PTY_TYPES_tryParse_rejects_unknown_and_prefixed_names', () => {
  for (const bad of ['SIGTERM', 'term', '', 'SIGKILL', 'STOP']) {
    const parsed = signalParse(bad)
    assert.equal(parsed.ok, false, bad)
    assert.match(String(parsed.error), /Unsupported PTY signal/)
    if (bad !== '') assert.ok(String(parsed.error).includes(bad), `${bad} echoed in error`)
  }
})
test('WHAT[process-execution-001] PTY_TYPES_command_views_carry_their_fields', () => {
  assert.deepEqual(ptyCommandView(ptyCommandSpawn('sh -c ls', '/tmp')), {
    kind: 'Spawn',
    command: 'sh -c ls',
    cwd: '/tmp',
  })
  assert.deepEqual(ptyCommandView(ptyCommandWrite(new TextEncoder().encode('abc'))), {
    kind: 'Write',
    bytes: [97, 98, 99],
  })
  assert.deepEqual(ptyCommandView(ptyCommandRead()), { kind: 'Read' })
  assert.deepEqual(ptyCommandView(ptyCommandSignal('HUP')), { kind: 'Signal', signal: 'SIGHUP' })
  assert.deepEqual(ptyCommandView(ptyCommandResize(120, 40)), {
    kind: 'Resize',
    width: 120,
    height: 40,
  })
})
test('WHAT[process-execution-001] PTY_TYPES_pty_id_roundtrips_its_value', () => {
  assert.equal(ptyIdView(ptyId('pty-deadbeef')), 'pty-deadbeef')
})
test('WHAT[process-execution-001] PTY_TYPES_pty_handle_view_exposes_identity_and_command', () => {
  const port = createPtyPort({})
  const id = portFork(port, 'sleep 1', 'devops', ptyId('pty-1'), undefined)
  const listed = portList(port).ptys
  assert.equal(listed.length, 1)
  assert.equal(listed[0].id, 'pty-1')
  assert.equal(listed[0].command, 'sleep 1')
  assert.equal(listed[0].agent, 'devops')
  assert.ok(typeof listed[0].startedAt === 'string')
  assert.equal(ptyIdView(id), 'pty-1')
})
test('WHAT[process-execution-001] PTY_TYPES_pty_read_view_reports_output_and_closed', async () => {
  const port = createPtyPort({})
  const id = portFork(port, 'echo hi', 'devops', ptyId('pty-read'), undefined)
  const pending = portRead(port, id)
  portReadResult(port, id, 'partial output', true)
  assert.deepEqual(await pending, { ok: true, value: { output: 'partial output', closed: true } })
  portComplete(port, id, undefined)
})
test('WHAT[process-execution-001] PTY_TYPES_read_plans_cover_unknown_in_progress_closed_and_park', async () => {
  const port = createPtyPort({})
  const unknown = await portRead(port, ptyId('pty-unknown'))
  assert.deepEqual(unknown, { ok: false, error: 'Unknown PTY id: pty-unknown' })

  const id = portFork(port, 'echo hi', 'devops', ptyId('pty-plan'), undefined)
  const parked = portRead(port, id)
  assert.deepEqual(await portRead(port, id), { ok: false, error: 'PTY read already in progress' })
  portReadResult(port, id, '', false)
  assert.deepEqual(await parked, { ok: true, value: { output: '', closed: false } })

  portComplete(port, id, undefined)
  assert.deepEqual(await portRead(port, id), { ok: true, value: { output: '', closed: true } })
})
}

{
  // The four PTY host tools (`open-terminal`, `send-terminal`, `read-terminal`,
  // `signal-terminal`) are retired; interactive execution is superseded by `js.call`
  // (`{ spawn, name }`, `check(ctx)`, `{ send: { name, input } }`, `{ signal, name }`,
  // repository-programming [050]-[052]).
  const { default: assert } = await import('node:assert/strict')
  const { default: test } = await import('node:test')
  const { sandbox, runProgram, caseName, render } = await import('./support/executor.mjs')

  test('WHAT[process-execution-001] js.call interactive actions spawn, send, observe incremental output in check, and signal background child', async () => {
    const { dir, cleanup } = sandbox('wxs-proc-001-')
    try {
      const outcome = await runProgram(
        dir,
        `    const chunks = [];
    const result = await js.call(class extends js.Callable {
      maxDuration = 8000;
      interval = 30;
      next(ctx) {
        if (ctx.step === 0) {
          return {
            spawn: "node -e \\"process.stdin.setEncoding('utf8'); process.stdin.on('data', d => process.stdout.write('ACK:' + d.trim()));\\"",
            name: "repl",
          };
        }
        if (ctx.step === 1) return { send: { name: "repl", input: "hello-interactive" } };
        if (ctx.step === 2) return "sleep 0.2";
        if (ctx.step === 3) return { signal: "SIGTERM", name: "repl" };
        return null;
      }
      check(ctx) {
        if (ctx.stdout) chunks.push(ctx.stdout);
        return true;
      }
      watch(ctx) {
        if (ctx.exited && ctx.exitCode !== 0) return { abort: "command failed" };
        return this.check(ctx);
      }
      sanity() {
        return {
          nextDone: () => [{ root: "/", step: 4 }],
          watchVeto: () => [{ root: "/", step: 0, stdout: "", stderr: "", exited: true, exitCode: 2, backgrounds: [{ name: "repl", exited: true, exitCode: 1 }], elapsedMs: 0, remainingMs: 60000 }],
        };
      }
    });
    return { status: result.status, observed: chunks.join("") };`,
        'ses-001-interactive',
      )
      assert.equal(caseName(outcome), 'Succeeded')
      const doc = render(outcome)
      assert.match(doc, /status = "completed"/)
      assert.match(doc, /ACK:hello-interactive/)
    } finally {
      cleanup()
    }
  })
}
