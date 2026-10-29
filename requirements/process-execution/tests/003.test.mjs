import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");

const { create: createDeadline } = await import('../../../dist/Process/DeadlineSurface.js')
const {
  killAckGraceMs,
  childCreate,
  childExit,
  childOnExit,
  childView,
  waitForExit,
  createCancellationToken,
  cancel,
} = await import('../../../dist/Process/Surface.js')
const nowIso = () => new Date().toISOString()
const within = (promise, ms, label) =>
  Promise.race([
    promise,
    new Promise((_, reject) => {
      const timer = setTimeout(() => reject(new Error(`${label}: did not settle within ${ms}ms`)), ms)
      timer.unref?.()
    }),
  ])
const expired = () => createDeadline('2000-01-01T00:00:00Z', 1)
const killCount = (child) => childView(child).killCount

test('WHAT[process-execution-003] NODE_EXIT_registered_callbacks_run_once_in_registration_order_and_fail_fast', () => {
  const child = childCreate(undefined)
  const observed = []
  childOnExit(child, () => observed.push('first'))
  childOnExit(child, () => observed.push('second'))
  childExit(child, 0)
  assert.deepEqual(observed, ['first', 'second'])

  const failingChild = childCreate(undefined)
  const afterFailure = []
  childOnExit(failingChild, () => {
    afterFailure.push('failing')
    throw new Error('exit callback failed')
  })
  childOnExit(failingChild, () => afterFailure.push('must-not-run'))
  assert.throws(() => childExit(failingChild, 1), /exit callback failed/)
  assert.deepEqual(afterFailure, ['failing'])
})
test('WHAT[process-execution-003] EXEC_011_A_natural_exit_before_deadline_returns_code_without_kill', async () => {
  const child = childCreate(undefined)
  const deadline = createDeadline(nowIso(), 5_000)
  const wait = waitForExit(child, deadline, createCancellationToken(false))

  setTimeout(() => childExit(child, 42), 20)

  const outcome = await within(wait, 2_000, 'natural exit')
  assert.deepEqual(outcome, { exitCode: 42, timedOut: false })
  assert.equal(killCount(child), 0, 'natural exit must not Kill')
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

test('WHAT[process-execution-003] BACKEND_fork_without_bun_pty_fails_spawn_and_publishes_failed', async () => {
  const port = backendCreatePort()
  const completion = takeCompletions(port)
  const pid = portFork(port, 'echo hi', 'devops', ptyId('pty-sf'), undefined)
  const [item] = await completion

  assert.equal(ptyIdView(pid), 'pty-sf')
  assert.equal(item.kind, 'PtyFailed')
  assert.equal(item.ptyId, 'pty-sf')
  assert.equal(item.code, 'ERROR')
  assert.match(item.message, /^PTY spawn failed: /)
  assert.equal(item.closed, true)
})
test('WHAT[process-execution-003] BACKEND_concurrent_failed_forks_each_publish_one_completion', async () => {
  const port = backendCreatePort()
  const completion = takeCompletions(port, 2)
  portFork(port, 'cmd one', 'devops', ptyId('pty-f1'), undefined)
  portFork(port, 'cmd two', 'devops', ptyId('pty-f2'), undefined)
  const items = await completion
  assert.deepEqual(items.map((item) => item.ptyId).sort(), ['pty-f1', 'pty-f2'])
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

test('WHAT[process-execution-003] PORT_AddMailboxSender_reaches_every_registered_sender', () => {
  const port = createPtyPort({})
  const got = []
  portAddMailboxSender(port, (item) => got.push(item.kind))
  portAddMailboxSender(port, (item) => got.push(item.kind))
  const pid = forkDefault(port, 'pty-s1')
  portComplete(port, pid, { ok: true, value: 'done' })
  assert.deepEqual(got, ['PtyExited', 'PtyExited'])
})
test('WHAT[process-execution-003] PORT_send_plain_signal_does_not_abort_the_completion', async () => {
  const port = createPtyPort({})
  const got = []
  portAddMailboxSender(port, (item) => got.push(item))
  const pid = forkDefault(port, 'pty-hup')
  await portSend(port, pid, signalOf('HUP'))
  portComplete(port, pid, { ok: true, value: 'closed' })
  assert.equal(got[0].kind, 'PtyExited')
})
test('WHAT[process-execution-003] PORT_complete_default_publishes_pty_exited_closed', () => {
  const port = createPtyPort({})
  const pid = forkDefault(port, 'pty-cd')
  const item = completedItem(port, pid, undefined)
  assert.equal(item.kind, 'PtyExited')
  assert.deepEqual([item.ptyId, item.outcome, item.closed], ['pty-cd', 'closed', true])
})
test('WHAT[process-execution-003] PORT_complete_ok_publishes_exited_with_outcome_text', () => {
  const port = createPtyPort({})
  const pid = forkDefault(port, 'pty-ok')
  const item = completedItem(port, pid, { ok: true, value: 'script output' })
  assert.equal(item.kind, 'PtyExited')
  assert.equal(item.outcome, 'script output')
})
test('WHAT[process-execution-003] PORT_complete_error_publishes_failed_with_code_and_message', () => {
  const port = createPtyPort({})
  const pid = forkDefault(port, 'pty-err')
  const item = completedItem(port, pid, failure('PTY spawn failed: boom'))
  assert.equal(item.kind, 'PtyFailed')
  assert.equal(item.code, 'ERROR')
  assert.equal(item.message, 'PTY spawn failed: boom')
  assert.equal(item.closed, true)
})
test('WHAT[process-execution-003] PORT_complete_after_terminate_publishes_aborted', async () => {
  const port = createPtyPort({})
  const pid = forkDefault(port, 'pty-ab')
  await portSend(port, pid, signalOf('TERM'))
  const item = completedItem(port, pid, undefined)
  assert.equal(item.kind, 'PtyAborted')
  assert.deepEqual([item.code, item.message, item.closed], ['PTY_ABORTED', 'PTY aborted', true])
})
test('WHAT[process-execution-003] PORT_complete_abort_with_error_outcome_carries_the_error_text', async () => {
  const port = createPtyPort({})
  const pid = forkDefault(port, 'pty-ab2')
  await portSend(port, pid, signalOf('KILL'))
  const item = completedItem(port, pid, failure('owner SIGKILLed'))
  assert.equal(item.kind, 'PtyAborted')
  assert.equal(item.message, 'owner SIGKILLed')
})
test('WHAT[process-execution-003] PORT_complete_on_inactive_id_publishes_nothing', () => {
  const port = createPtyPort({})
  const got = []
  portAddMailboxSender(port, (item) => got.push(item))
  portComplete(port, id('pty-ghost'), { ok: true, value: 'x' })
  assert.equal(got.length, 0)
})
test('WHAT[process-execution-003] PORT_complete_isolates_failing_senders', () => {
  const port = createPtyPort({})
  const got = []
  portAddMailboxSender(port, () => { throw new Error('sender exploded') })
  portAddMailboxSender(port, (item) => got.push(item))
  const pid = forkDefault(port, 'pty-th')
  portComplete(port, pid, { ok: true, value: 'done' })
  assert.deepEqual(got.map((item) => item.kind), ['PtyExited'])
})
test('WHAT[process-execution-003] PORT_cleanup_reports_termination_failure_and_still_closes_other_owned_handles', async () => {
  const ids = new Map()
  const signalled = []
  const port = createPtyPort({ handler: (name, command) => {
    if (command.kind !== 'Signal') return success
    signalled.push(name)
    if (name === 'pty-failed') return failure('termination refused')
    portComplete(port, ids.get(name), { ok: true, value: 'closed by backend' })
    return success
  } })
  ids.set('pty-failed', forkDefault(port, 'pty-failed'))
  ids.set('pty-healthy', forkDefault(port, 'pty-healthy'))
  await assert.rejects(portCloseAll(port, 0), /termination refused/)
  assert.deepEqual(signalled, ['pty-failed', 'pty-healthy'])
  assert.equal(portExists(port, ids.get('pty-failed')), true, 'unknown exit remains owned')
  assert.equal(portExists(port, ids.get('pty-healthy')), false)
})

test('WHAT[process-execution-003] cleanup preserves each error when several terminals reject termination', async () => {
  const port = createPtyPort({ handler: (name, command) =>
    command.kind === 'Signal' ? failure(`cannot terminate ${name}`) : success,
  })
  const first = forkDefault(port, 'pty-first')
  const second = forkDefault(port, 'pty-second')
  await assert.rejects(portCloseAll(port, 0), error => {
    assert.ok(error instanceof AggregateError)
    assert.deepEqual(error.errors.map(item => item.message), [
      'cannot terminate pty-first',
      'cannot terminate pty-second',
    ])
    return true
  })
  assert.equal(portExists(port, first), true)
  assert.equal(portExists(port, second), true)
})

test('WHAT[process-execution-003] PORT_complete_removes_the_exit_task_entry', async () => {
  const port = createPtyPort({})
  const pid = forkDefault(port, 'pty-et')
  const exit = exitSignal()
  portRegisterExitTask(port, pid, exit.promise)
  portComplete(port, pid, { ok: true, value: 'done' })
  await portCloseAll(port, 0)
})
test('WHAT[process-execution-003] PORT_complete_aborted_forces_abort_without_terminate_mark', () => {
  const port = createPtyPort({})
  const pid = forkDefault(port, 'pty-cab')
  const got = []
  portAddMailboxSender(port, (item) => got.push(item))
  portCompleteAborted(port, pid, 'owner interrupt')
  assert.equal(got[0].kind, 'PtyAborted')
  assert.equal(got[0].message, 'owner interrupt')
  assert.equal(portExists(port, pid), false)
})
test('WHAT[process-execution-003] PORT_complete_aborted_defaults_message_and_clears_active', () => {
  const port = createPtyPort({})
  const pid = forkDefault(port, 'pty-cab2')
  const got = []
  portAddMailboxSender(port, (item) => got.push(item))
  portCompleteAborted(port, pid, undefined)
  assert.equal(got[0].message, 'PTY aborted')
  assert.equal(portExists(port, pid), false)
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

test('WHAT[process-execution-003] SUPERVISOR_failPending_resolves_every_tcs_with_the_reason', async () => {
  const supervisor = supervisorCreate()
  const session = sessionCreate('pty-fp0', null)
  const parked = sessionPushPendingTask(session, ptyCommandWrite('write'))
  supervisorAdd(supervisor, id('pty-fp0'), session)
  const pending = supervisorTakePending(supervisor, id('pty-fp0'))
  supervisorFailPending(pending, 'PTY exited before command was applied')
  assert.deepEqual(await parked, resultError('PTY exited before command was applied'))
})
test('WHAT[process-execution-003] SUPERVISOR_attach_onExit_completes_exit_publishes_closed_and_drops_session', async () => {
  const supervisor = supervisorCreate()
  const handle = driverHandle(9999)
  const term = libraryTerminal(handle)
  const completions = []
  const p = portWith('pty-ex')
  portAddMailboxSender(p, (item) => completions.push(item))
  supervisorAttach(supervisor, p, id('pty-ex'), term)
  handle.exitCb({ exitCode: 0 })
  await settleTerminal()
  assert.equal(supervisorTryGet(supervisor, id('pty-ex')), null, 'session dropped')
  assert.equal(completions.length, 1)
  assert.equal(completions[0].kind, 'PtyExited')
  assert.equal(completions[0].ptyId, 'pty-ex')
  assert.equal(completions[0].outcome, 'closed')
  assert.equal(completions[0].closed, true)
})
test('WHAT[process-execution-003] SUPERVISOR_attach_onExit_publishes_residual_output', async () => {
  const supervisor = supervisorCreate()
  const handle = driverHandle(9999)
  const term = libraryTerminal(handle)
  const completions = []
  const p = portWith('pty-ro')
  portAddMailboxSender(p, (item) => completions.push(item))
  supervisorAttach(supervisor, p, id('pty-ro'), term)
  handle.dataCb('final words')
  handle.exitCb({ exitCode: 1 })
  await settleTerminal()
  assert.equal(completions[0].kind, 'PtyExited')
  assert.equal(completions[0].outcome, 'final words')
})
test('WHAT[process-execution-003] SUPERVISOR_attach_onExit_fails_pending_writes_and_parked_read', async () => {
  const supervisor = supervisorCreate()
  const handle = driverHandle(9999)
  const term = libraryTerminal(handle)
  const p = portWith('pty-fp')
  supervisorAttach(supervisor, p, id('pty-fp'), term)
  const parkedRead = portRead(p, id('pty-fp'))
  const session = supervisorGet(supervisor, id('pty-fp'))
  const parkedWrite = sessionPushPendingTask(session, ptyCommandWrite(new Uint8Array(0)))
  handle.exitCb({ exitCode: 0 })
  assert.deepEqual(await parkedRead, resultError('PTY exited before read completed'))
  assert.deepEqual(await parkedWrite, resultError('PTY exited before command was applied'))
})
test('WHAT[process-execution-003] SUPERVISOR_attach_onExit_is_idempotent_for_already_dropped_session', async () => {
  const supervisor = supervisorCreate()
  const handle = driverHandle(9999)
  const term = libraryTerminal(handle)
  const completions = []
  const p = portWith('pty-idem')
  portAddMailboxSender(p, (item) => completions.push(item))
  supervisorAttach(supervisor, p, id('pty-idem'), term)
  handle.exitCb({ exitCode: 0 })
  await settleTerminal()
  handle.exitCb({ exitCode: 0 })
  assert.equal(completions.length, 1)
})
test('WHAT[process-execution-003] SUPERVISOR_attach_onExit_noop_when_session_already_closed', () => {
  const supervisor = supervisorCreate()
  const handle = driverHandle(9999)
  const term = libraryTerminal(handle)
  const completions = []
  const p = portWith('pty-ec')
  portAddMailboxSender(p, (item) => completions.push(item))
  supervisorAttach(supervisor, p, id('pty-ec'), term)
  sessionSetClosed(supervisorGet(supervisor, id('pty-ec')), true)
  handle.exitCb({ exitCode: 0 })
  assert.equal(completions.length, 0)
  assert.notEqual(supervisorTryGet(supervisor, id('pty-ec')), null)
})
}
