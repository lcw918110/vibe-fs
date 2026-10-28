// Drives the functions the host actually calls:
// OpenCode work-projection-cut calls RelayNarrativeTransform.apply.
// OMP registers the context handler inside activateOnOmp.
import assert from 'node:assert/strict'
import test from 'node:test'
import { mkdtempSync, mkdirSync, rmSync, realpathSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { activateOnOmp } from '../../../dist/Omp/Adapter.js'
import { AgentRole } from '../../../dist/Host/Ports/Vocabulary.js'
import { AgentRoleSpec } from '../../../dist/Host/Ports/Lifecycle.js'
import { ofArray } from '../../../dist/fable_modules/fable-library-js.5.13.0/List.js'
import { stateRootSync } from '../../../dist/Persistence/Journal/RuntimePath.js'
import * as lifecycle from '../../../dist/Mission/Lifecycle/Surface.js'
import { apply as applyRelay } from '../../../dist/Mission/Work/OpenCode/NarrativeTransform.js'

const surface = lifecycle.LifecycleSurface ?? lifecycle
const PRE = 'operator before the relay'
const REASON = 'private reasoning before the relay'
const RESULT = 'relay result body'
const KEPT = 'successor sees this'
const STRAY = 'unattributed after the relay'
const OTHER = 'other tenure after the relay'
const SYNTH = 'synthetic successor line'

function textsOf(value) {
  return JSON.stringify(value)
}

function snapshotOf(messages) {
  return JSON.parse(JSON.stringify(messages))
}

function openCodeHistory(toolCallId, successorId) {
  const text = (id, role, tenureId, body, extra = {}) => ({
    id,
    role,
    tenureId,
    info: { id, role },
    parts: [{ type: 'text', text: body }],
    ...extra,
  })
  return [
    text('before', 'user', 'old-tenure', PRE),
    text('reason', 'assistant', 'old-tenure', REASON, {
      parts: [{ type: 'reasoning', text: REASON }],
    }),
    {
      id: 'call',
      role: 'assistant',
      tenureId: 'old-tenure',
      info: { id: 'call', role: 'assistant' },
      parts: [{ type: 'tool', tool: 'review', callID: toolCallId, state: { status: 'pending', input: {} } }],
    },
    {
      id: 'result',
      role: 'tool',
      tenureId: 'old-tenure',
      info: { id: 'result', role: 'tool' },
      parts: [{ type: 'tool-result', callID: toolCallId, output: RESULT }],
    },
    text('kept', 'user', successorId, KEPT),
    text('stray', 'user', null, STRAY),
    text('other', 'assistant', 'someone-else', OTHER),
    text('synth', 'user', successorId, SYNTH, { synthetic: true }),
  ]
}

function ompHistory(toolCallId, successorId) {
  const text = (id, role, tenureId, body, extra = {}) => ({
    id,
    role,
    tenureId,
    content: [{ type: 'text', text: body }],
    ...extra,
  })
  return [
    text('before', 'user', 'old-tenure', PRE),
    text('reason', 'assistant', 'old-tenure', REASON),
    {
      id: 'call',
      role: 'assistant',
      tenureId: 'old-tenure',
      content: [{ type: 'toolCall', id: toolCallId, name: 'review', arguments: {} }],
    },
    {
      id: 'result',
      role: 'toolResult',
      tenureId: 'old-tenure',
      toolCallId,
      content: [{ type: 'text', text: RESULT }],
    },
    text('kept', 'user', successorId, KEPT),
    text('stray', 'user', null, STRAY),
    text('other', 'assistant', 'someone-else', OTHER),
    text('synth', 'user', successorId, SYNTH, { synthetic: true }),
  ]
}

function assertCut(body, stored, original) {
  const rendered = textsOf(body)
  for (const shown of [PRE, RESULT, KEPT, STRAY]) {
    assert.match(rendered, new RegExp(shown))
  }
  for (const hidden of [REASON, OTHER, SYNTH]) {
    assert.equal(rendered.includes(hidden), false, hidden)
  }
  assert.deepEqual(stored, original)
}

function assertUncut(body, stored, original) {
  const rendered = textsOf(body ?? stored)
  assert.match(rendered, new RegExp(PRE))
  assert.match(rendered, new RegExp(KEPT))
  assert.deepEqual(stored, original)
}

function review(stateRoot, workId, id, updates) {
  const receipt = surface.submitReview(
    stateRoot,
    workId,
    id,
    'Manager',
    'run-' + id,
    'call-' + id,
    updates,
    [],
    '',
  )
  assert.equal(receipt.status, 'Committed', `${id} ${receipt.status} ${receipt.reason}`)
  return receipt
}

function prepare(prefix) {
  const raw = mkdtempSync(join(tmpdir(), prefix))
  // stateRootSync canonicalizes through realpath; macOS resolves /var to /private/var.
  const workspace = realpathSync(raw)
  mkdirSync(join(workspace, '.Herdchestra'))
  const stateRoot = stateRootSync(workspace)
  if (!stateRoot.startsWith(workspace) || !stateRoot.endsWith(`${join('', '.Herdchestra')}`) && !stateRoot.endsWith('.Herdchestra')) {
    rmSync(workspace, { recursive: true, force: true })
    throw new Error(`refusing to write outside the temp workspace: ${stateRoot}`)
  }
  if (stateRoot.includes(`${join('wanxiangshu', '.Herdchestra')}`)) {
    rmSync(workspace, { recursive: true, force: true })
    throw new Error(`refusing the live state directory: ${stateRoot}`)
  }
  return { workspace, stateRoot }
}

test('provider barrier: OpenCode apply keeps user messages and the triggering review, hides the rest before it', async () => {
  const { workspace, stateRoot } = prepare('wxs-oc-barrier-')
  const workId = 'ses-opencode-barrier'
  const toolCallId = 'call-relay'
  try {
    review(stateRoot, workId, 'open', [{ key: 'goal', value: 'ship the protocol' }])
    const before = openCodeHistory(toolCallId, 'not-yet')
    const beforeStored = snapshotOf(before)
    const beforeRequest = before.map((message) => message)
    const beforeOut = { messages: beforeRequest }
    await applyRelay(null, false, async () => {}, null, surface.inspected(stateRoot, workId), beforeOut)
    assertUncut(beforeOut.messages, before, beforeStored)
    console.log('opencode no barrier leaves history')

    const relayed = review(stateRoot, workId, 'relay', [{ key: 'goal', value: null }])
    assert.equal(relayed.kind, 'Relayed')
    const snap = surface.inspect(stateRoot, workId)
    assert.equal(snap.barrier.toolCallId, toolCallId)
    assert.equal(snap.active.id, snap.barrier.successorTenureId)
    const successorId = snap.barrier.successorTenureId
    const stored = openCodeHistory(toolCallId, successorId)
    const original = snapshotOf(stored)
    const request = stored.map((message) => message)
    const out = { messages: request }
    await applyRelay(null, false, async () => {}, null, surface.inspected(stateRoot, workId), out)
    assert.equal(out.messages, request)
    assertCut(out.messages, stored, original)
    assert.deepEqual(out.messages.map((message) => message.id), ['before', 'call', 'result', 'kept', 'stray'])
    console.log('opencode barrier excludes pre-barrier')
    console.log('opencode stored originals unchanged')
  } finally {
    rmSync(workspace, { recursive: true, force: true })
  }
})

test('provider barrier: registered OMP context hook keeps user messages and the triggering review', async () => {
  const { workspace, stateRoot } = prepare('wxs-omp-barrier-')
  const workId = 'ses-omp-barrier'
  const toolCallId = 'call-relay'
  const handlers = new Map()
  const pi = {
    logger: { debug() {}, info() {}, warn() {}, error() {} },
    on(name, handler) {
      handlers.set(name, handler)
    },
    registerTool() {},
  }
  const plugin = {
    Activate: async () => ({
      tag: 0,
      fields: [{ OnSignal: async () => {}, Turn: null, ToolGate: null }],
    }),
    AttachTransport() {},
  }
  const agents = ofArray([
    new AgentRoleSpec(new AgentRole(1), null, null, null),
    new AgentRoleSpec(new AgentRole(3), null, null, null),
  ])
  try {
    await activateOnOmp(pi, plugin, agents)
    const session = {
      cwd: workspace,
      sessionManager: {
        getSessionId: () => workId,
        getSessionDir: () => workspace,
        getHeader: () => ({ parentSession: null }),
      },
    }
    await handlers.get('session_start')({}, session)
    const context = handlers.get('context')
    assert.equal(typeof context, 'function')

    review(stateRoot, workId, 'open', [{ key: 'goal', value: 'ship the protocol' }])
    const before = ompHistory(toolCallId, 'not-yet')
    const beforeStored = snapshotOf(before)
    const beforeReply = await context({ messages: before }, session)
    const beforeBody = beforeReply?.messages ?? before
    assertUncut(beforeBody, before, beforeStored)
    console.log('omp no barrier leaves history')

    const relayed = review(stateRoot, workId, 'relay', [{ key: 'goal', value: null }])
    assert.equal(relayed.kind, 'Relayed')
    const snap = surface.inspect(stateRoot, workId)
    assert.equal(snap.barrier.toolCallId, toolCallId)
    const successorId = snap.barrier.successorTenureId
    const stored = ompHistory(toolCallId, successorId)
    const original = snapshotOf(stored)
    const reply = await context({ messages: stored }, session)
    assert.ok(reply?.messages, 'the context hook must return the cut request')
    assert.notEqual(reply.messages, stored)
    assertCut(reply.messages, stored, original)
    // OMP appends its own scope reminder after the cut view; it has no Host id.
    assert.deepEqual(reply.messages.map((message) => message.id).filter(Boolean), ['before', 'call', 'result', 'kept', 'stray'])
    console.log('omp context excludes pre-barrier')
    console.log('omp stored originals unchanged')
  } finally {
    rmSync(workspace, { recursive: true, force: true })
  }
})

test('WHAT[work-008] durable ownership selects successor replies while user messages remain visible', async () => {
  const { cutVisible } = await import('../../../dist/Mission/Work/OpenCode/NarrativeTransform.js')
  const toolCallId = 'call-relay'
  const successor = 'tenure-next'
  const work = {
    BarrierToolCallId: toolCallId,
    SuccessorTenureId: successor,
    ActiveId: successor,
  }
  const plain = (id, role, body, info = {}) => ({ info: { id, role, ...info }, parts: [{ type: 'text', text: body }] })
  const history = [
    plain('before', 'user', PRE),
    { info: { id: 'call', role: 'assistant' }, parts: [{ type: 'tool', tool: 'review', callID: toolCallId, state: { status: 'pending', input: {} } }] },
    { info: { id: 'result', role: 'tool' }, parts: [{ type: 'tool-result', callID: toolCallId, output: RESULT }] },
    plain('owned-user', 'user', KEPT),
    plain('owned-reply', 'assistant', 'successor reply', { parentID: 'owned-user' }),
    plain('stray', 'user', STRAY),
    plain('stray-reply', 'assistant', 'stray reply', { parentID: 'stray' }),
    { ...plain('owned-other', 'user', OTHER), tenureId: 'someone-else' },
  ]
  const owned = new Set(['owned-user', 'owned-other', 'before'])
  const ids = (kept) => kept.map((message) => message.info.id)

  const kept = cutVisible((id) => owned.has(id), history, work)
  assert.deepEqual(ids(kept), ['before', 'call', 'result', 'owned-user', 'owned-reply', 'stray', 'owned-other'])

  const nothingOwned = cutVisible(() => false, history, work)
  assert.deepEqual(ids(nothingOwned), ['before', 'call', 'result', 'owned-user', 'stray', 'owned-other'])

  const notSuccessor = cutVisible(() => true, history, { ...work, ActiveId: 'tenure-old' })
  assert.equal(notSuccessor, undefined)
})

test('WHAT[work-008] a settled OpenCode tool part is its own result for the barrier', async () => {
  const { cutVisible } = await import('../../../dist/Mission/Work/OpenCode/NarrativeTransform.js')
  const work = { BarrierToolCallId: 'call_3', SuccessorTenureId: 'S', ActiveId: 'S' }
  const user = (id, body) => ({ info: { id, role: 'user' }, parts: [{ type: 'text', text: body }] })
  const history = [
    user('old', PRE),
    { info: { id: 'call', role: 'assistant', parentID: 'old' },
      parts: [{ type: 'tool', tool: 'review', callID: 'call_3', state: { status: 'completed', input: {}, output: RESULT } }] },
    user('next', KEPT),
    { info: { id: 'reply', role: 'assistant', parentID: 'next' }, parts: [{ type: 'text', text: 'reply' }] },
  ]
  assert.equal(cutVisible((id) => id === 'next', history, work), undefined)
  const pending = structuredClone(history)
  pending[1].parts[0].state.status = 'running'
  assert.deepEqual(cutVisible((id) => id === 'next', pending, work), [])
})
