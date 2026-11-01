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

test('WHAT[process-execution-006] PTY_API_abort_parent_invokes_every_registered_callback', () => {
  const parent = 'parent-all'
  const calls = []
  registerParentAbort(parent, () => calls.push('a'))
  registerParentAbort(parent, () => calls.push('b'))

  abortParent(parent)
  assert.deepEqual(calls.sort(), ['a', 'b'])

  abortParent(parent)
  assert.equal(calls.length, 4)
})
test('WHAT[process-execution-006] PTY_API_abort_parent_with_unknown_id_is_a_noop', () => {
  abortParent('parent-never-registered')
})
test('WHAT[process-execution-006] PTY_API_unregister_removes_only_the_matching_token', () => {
  const parent = 'parent-partial'
  const calls = []
  const tokenA = registerParentAbort(parent, () => calls.push('a'))
  registerParentAbort(parent, () => calls.push('b'))

  unregisterParentAbort(parent, tokenA)
  abortParent(parent)
  assert.deepEqual(calls, ['b'])
})
test('WHAT[process-execution-006] PTY_API_unregister_last_callback_drops_the_parent_entry', () => {
  const parent = 'parent-dropped'
  const calls = []
  const token = registerParentAbort(parent, () => calls.push('a'))

  unregisterParentAbort(parent, token)
  abortParent(parent)
  assert.deepEqual(calls, [])
})
test('WHAT[process-execution-006] PTY_API_unregister_with_unknown_parent_or_token_is_a_noop', () => {
  unregisterParentAbort('parent-nope', 1)
  const parent = 'parent-mismatch'
  registerParentAbort(parent, () => {})
  unregisterParentAbort(parent, 99999)
  abortParent(parent)
})
test('WHAT[process-execution-006] PTY_API_tokens_are_monotonic_across_parents', () => {
  const t1 = registerParentAbort('parent-tok-1', () => {})
  const t2 = registerParentAbort('parent-tok-2', () => {})
  assert.ok(t2 > t1, `${t2} > ${t1}`)
})
test('WHAT[process-execution-006] PTY_API_throwing_abort_callback_does_not_block_the_rest', () => {
  const parent = 'parent-throw'
  const calls = []
  registerParentAbort(parent, () => {
    throw new Error('abort exploded')
  })
  registerParentAbort(parent, () => calls.push('after'))

  abortParent(parent)
  assert.deepEqual(calls, ['after'])
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

test('WHAT[process-execution-006] SUPERVISOR_attach_without_port_entry_kills_the_term', async () => {
  const process = child()
  try {
    const supervisor = supervisorCreate()
    supervisorAttach(supervisor, createPtyPort({}), id('pty-nk'), libraryTerminal(driverHandle(process.pid)))
    await waitExit(process)
    assert.ok(died(process), 'unregistered attach closes the terminal process group')
  } finally { killChild(process) }
})
test('WHAT[process-execution-006] SUPERVISOR_cancelAll_kills_live_sessions_and_skips_closed_or_null_backends', async () => {
  const process = child()
  try {
    const supervisor = supervisorCreate()
    const live = sessionCreate('pty-l1', libraryTerminal(driverHandle(process.pid)))
    const closed = sessionCreate('pty-l2', libraryTerminal(driverHandle(2_147_483_647)))
    sessionSetClosed(closed, true)
    supervisorAdd(supervisor, id('pty-l1'), live)
    supervisorAdd(supervisor, id('pty-l2'), closed)
    supervisorAdd(supervisor, id('pty-l3'), sessionCreate('pty-l3', null))
    supervisorCancelAll(supervisor)
    await waitExit(process)
    assert.ok(died(process), 'live session killed')
  } finally { killChild(process) }
})
}
