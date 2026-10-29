import test from 'node:test'

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

test('WHAT[process-execution-007] PORT_close_requests_terminate_but_keeps_the_session_live', async () => {
  const seen = []
  const port = createPtyPort({ handler: async (_pid, command) => { if (command.kind !== 'Spawn') seen.push(command.signal); return success } })
  const pid = forkDefault(port, 'pty-cs')
  portClose(port, pid)
  assert.deepEqual(seen, ['SIGTERM'])
  assert.equal(portExists(port, pid), true)
})
test('WHAT[process-execution-007] PORT_close_all_with_no_sessions_resolves', async () => {
  await portCloseAll(createPtyPort({}), 0)
})
test('WHAT[process-execution-007] PORT_close_all_awaits_exit_task_when_it_resolves_in_grace', async () => {
  const seen = []
  const exit = exitSignal()
  const port = createPtyPort({ handler: async (_pid, command) => { if (command.kind !== 'Spawn') seen.push(command.kind); return success } })
  const pid = forkDefault(port, 'pty-cg')
  portRegisterExitTask(port, pid, exit.promise)
  const closing = portCloseAll(port, 1000)
  exit.resolve()
  await closing
  assert.deepEqual(seen, ['Signal'])
})
test('WHAT[process-execution-007] PORT_close_all_escalates_to_kill_after_grace', async () => {
  const seen = []
  const exit = exitSignal()
  const port = createPtyPort({ handler: async (_pid, command) => {
    if (command.kind !== 'Spawn') seen.push(command.signal)
    if (command.kind === 'Signal' && command.signal === 'SIGKILL') exit.resolve()
    return success
  } })
  const pid = forkDefault(port, 'pty-ck')
  portRegisterExitTask(port, pid, exit.promise)
  await portCloseAll(port, 0)
  assert.deepEqual(seen, ['SIGTERM', 'SIGKILL'])
})

test('WHAT[process-execution-007] pending TERM dispatch cannot prevent KILL after the shared grace', async () => {
  const signals = []
  const exit = exitSignal()
  const port = createPtyPort({ handler: async (_pid, command) => {
    if (command.kind !== 'Signal') return success
    signals.push(command.signal)
    if (command.signal === 'SIGTERM') return new Promise(() => {})
    exit.resolve()
    return success
  } })
  const pid = forkDefault(port, 'pty-pending-term')
  portRegisterExitTask(port, pid, exit.promise)
  await portCloseAll(port, 10)
  assert.deepEqual(signals, ['SIGTERM', 'SIGKILL'])
  assert.equal(portExists(port, pid), true, 'a resolved waiter alone cannot fabricate a completion event')
  portComplete(port, pid, { ok: true, value: 'stopped' })
  assert.equal(portExists(port, pid), false)
})
test('WHAT[process-execution-007] PORT_close_all_kill_failure_propagates', async () => {
  const exit = exitSignal()
  const port = createPtyPort({ handler: async (_pid, command) => {
    if (command.kind === 'Signal' && command.signal === 'SIGKILL') return failure('no such process')
    return success
  } })
  const pid = forkDefault(port, 'pty-cf')
  portRegisterExitTask(port, pid, exit.promise)
  await assert.rejects(portCloseAll(port, 0), /PTY kill failed for pty-cf: no such process/)
})
test('WHAT[process-execution-007] PORT_close_all_skips_ids_without_exit_task', async () => {
  const seen = []
  const port = createPtyPort({ handler: async (_pid, command) => { if (command.kind !== 'Spawn') seen.push(command.kind); return success } })
  forkDefault(port, 'pty-cn')
  await portCloseAll(port, 0)
  assert.deepEqual(seen, ['Signal'])
})
test('WHAT[process-execution-007] PORT_list_reports_active_handles', () => {
  const port = createPtyPort({})
  const pid = forkDefault(port, 'pty-ls', 'tail -f')
  const listed = portList(port)
  assert.equal(listed.ptys.length, 1)
  assert.equal(listed.ptys[0].command, 'tail -f')
  assert.equal(listed.ptys[0].id, 'pty-ls')
  assert.equal(listed.ptys[0].agent, 'devops')
  assert.ok(typeof listed.ptys[0].startedAt === 'string')

  portComplete(port, pid, { ok: true, value: 'done' })
  assert.equal(portList(port).ptys.length, 0)
})
test('WHAT[process-execution-007] PORT_list_without_provider_returns_empty_agents', () => {
  const port = createPtyPort({})
  forkDefault(port, 'pty-le')
  assert.equal(portList(port).agents.length, 0)
})
}

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { spawn } = await import("node:child_process");
const { driverHandle, libraryTerminal, settleTerminal } = await import('./support/pty-terminal.mjs');

const {
  supervisorCreate,
  supervisorAdd,
  supervisorTryGet,
  supervisorGet,
  supervisorRemove,
  supervisorList,
  supervisorSignalName,
  supervisorEnsureSpawn,
  supervisorSpawnSync,
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
const portWith = (value) => {
  const p = port()
  portFork(p, 'echo hi', 'devops', id(value), undefined)
  return p
}

test('WHAT[process-execution-007] SUPERVISOR_add_tryGet_get_roundtrip', () => {
  const supervisor = supervisorCreate()
  const session = sessionCreate('pty-a', null)
  supervisorAdd(supervisor, id('pty-a'), session)
  assert.notEqual(supervisorTryGet(supervisor, id('pty-a')), null)
  assert.equal(sessionView(supervisorGet(supervisor, id('pty-a'))).ptyId, 'pty-a')
})
test('WHAT[process-execution-007] SUPERVISOR_tryGet_missing_returns_none_and_get_throws', () => {
  const supervisor = supervisorCreate()
  assert.equal(supervisorTryGet(supervisor, id('pty-missing')), null)
  assert.throws(() => supervisorGet(supervisor, id('pty-missing')), /Unknown PTY id: pty-missing/)
})
test('WHAT[process-execution-007] SUPERVISOR_remove_drops_the_session', () => {
  const supervisor = supervisorCreate()
  supervisorAdd(supervisor, id('pty-r'), sessionCreate('pty-r', null))
  supervisorRemove(supervisor, id('pty-r'))
  assert.equal(supervisorTryGet(supervisor, id('pty-r')), null)
})
test('WHAT[process-execution-007] SUPERVISOR_list_returns_added_ids_only', () => {
  const supervisor = supervisorCreate()
  supervisorAdd(supervisor, id('pty-1'), sessionCreate('pty-1', null))
  supervisorAdd(supervisor, id('pty-2'), sessionCreate('pty-2', null))
  assert.deepEqual(supervisorList(supervisor).sort(), ['pty-1', 'pty-2'])
  supervisorRemove(supervisor, id('pty-1'))
  assert.deepEqual(supervisorList(supervisor), ['pty-2'])
})
test('WHAT[process-execution-007] SUPERVISOR_takePending_returns_and_clears_the_queue', () => {
  const supervisor = supervisorCreate()
  const session = sessionCreate('pty-q', null)
  sessionPushPending(session, ptyCommandWrite('first'))
  sessionPushPending(session, ptyCommandWrite('second'))
  supervisorAdd(supervisor, id('pty-q'), session)

  const pending = supervisorTakePending(supervisor, id('pty-q'))
  assert.deepEqual(supervisorPendingEntries(pending).map(pendingEntryView).map((entry) => entry.command.kind), ['Write', 'Write'])
  assert.equal(sessionView(session).pendingCount, 0)
  assert.deepEqual(supervisorPendingEntries(supervisorTakePending(supervisor, id('pty-q'))), [])
})
test('WHAT[process-execution-007] SUPERVISOR_takePending_unknown_id_is_empty', () => {
  const supervisor = supervisorCreate()
  assert.deepEqual(supervisorPendingEntries(supervisorTakePending(supervisor, id('pty-nope'))), [])
})
test('WHAT[process-execution-007] SUPERVISOR_drop_removes_session_and_returns_pending', () => {
  const supervisor = supervisorCreate()
  const session = sessionCreate('pty-d', null)
  sessionPushPending(session, ptyCommandWrite('queued'))
  supervisorAdd(supervisor, id('pty-d'), session)

  const dropped = supervisorDropPending(supervisor, id('pty-d'))
  assert.equal(supervisorPendingEntries(dropped).length, 1)
  assert.equal(supervisorTryGet(supervisor, id('pty-d')), null)
  assert.deepEqual(supervisorPendingEntries(supervisorDropPending(supervisor, id('pty-d'))), [])
})
test('WHAT[process-execution-007] SUPERVISOR_attach_registers_live_session_and_its_output_reaches_the_terminal_buffer', () => {
  const supervisor = supervisorCreate()
  const handle = driverHandle(9999)
  const term = libraryTerminal(handle)
  supervisorAttach(supervisor, portWith('pty-at'), id('pty-at'), term)
  const session = supervisorGet(supervisor, id('pty-at'))
  assert.equal(sessionView(session).backend, term)
  assert.equal(sessionView(session).closed, false)
  handle.dataCb('hello ')
  handle.dataCb('world')
  assert.equal(term.inspect().buffered, 'hello world'.length)
})
test('WHAT[process-execution-007] SUPERVISOR_commands_on_a_closed_session_never_reach_the_terminal', async () => {
  const supervisor = supervisorCreate()
  const handle = driverHandle(9999)
  supervisorAttach(supervisor, portWith('pty-ic'), id('pty-ic'), libraryTerminal(handle))
  sessionSetClosed(supervisorGet(supervisor, id('pty-ic')), true)
  assert.deepEqual(await supervisorApplyLive(supervisor, port(), id('pty-ic'), ptyCommandWrite(new TextEncoder().encode('late'))), resultOk)
  assert.deepEqual(handle.writes, [])
})
test('WHAT[process-execution-007] SUPERVISOR_attach_replays_pending_writes_onto_the_live_backend', async () => {
  const supervisor = supervisorCreate()
  const session = sessionCreate('pty-rp', null)
  supervisorAdd(supervisor, id('pty-rp'), session)
  const parked = supervisorApplyLive(supervisor, port(), id('pty-rp'), ptyCommandWrite(new TextEncoder().encode('early')))
  const handle = driverHandle(9999)
  supervisorAttach(supervisor, portWith('pty-rp'), id('pty-rp'), libraryTerminal(handle))
  assert.deepEqual(await parked, resultOk)
  assert.deepEqual(handle.writes, ['early'])
})
}

{
const { default: assert } = await import("node:assert/strict");
const { spawnSync } = await import("node:child_process");
const { default: test } = await import("node:test");


test('WHAT[process-execution-007] short PTY exit race owns enough physical lifetime to settle', () => {
  const surfaceUrl = new URL('../../../dist/Process/Surface.js', import.meta.url).href
  const program = `
    const { ptyRaceExit } = await import(${JSON.stringify(surfaceUrl)});
    const result = await ptyRaceExit(new Promise(() => {}), 0);
    process.stdout.write(String(result));
  `
  const child = spawnSync(process.execPath, ['--input-type=module', '--eval', program], { encoding: 'utf8' })

  assert.equal(child.status, 0, child.stderr)
  assert.equal(child.stdout, 'false')
})
}
