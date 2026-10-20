import assert from 'node:assert/strict'
import test from 'node:test'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { execute, caseName } from '../../../dist/Repository/Programming/Js/WorkflowSurface.js'
import {
  deliveredValue,
  clearDeliveredValue,
  recordDeliveredView,
  takeDeliveredHandoff,
  takeDeliveredHandoffWith,
  clearDeliveredReturned,
  deliveredReturned,
} from '../../../dist/Repository/Programming/Js/RuntimeSurface.js'

const sandbox = () => {
  const dir = mkdtempSync(join(tmpdir(), 'wxs-delivered-'))
  return { dir, cleanup: () => rmSync(dir, { recursive: true, force: true }) }
}

const program = (body) => `class Js extends JsProgram {
  async run() {
${body}
  }
}`

const runProgram = (dir, body, sessionId) =>
  execute(dir, 'Manager', 'en', sessionId, { program: program(body) }, 3000, Date.now() + 60_000, 1 << 20, null)

test('WHAT[repository-programming-059] js.deliver records the handed-over value for the completion path', async () => {
  const { dir, cleanup } = sandbox()
  const sessionId = `sess-delivered-${Math.random().toString(36).slice(2, 8)}`
  try {
    clearDeliveredValue(sessionId)
    const outcome = await runProgram(
      dir,
      `    await this.deliver({ summary: "indexed the parser", files: 3 });
    return { unreachable: true };`,
      sessionId,
    )
    assert.equal(caseName(outcome), 'Succeeded')
    const delivered = deliveredValue(sessionId)
    assert.equal(delivered.summary, 'indexed the parser')
    assert.equal(delivered.files, 3)
  } finally {
    clearDeliveredValue(sessionId)
    cleanup()
  }
})

test('WHAT[repository-programming-059] view with thenRemove anchors >= prefixUntil returns summaries, deduplicates, and manages assistant prose', () => {
  const sessionId = `sess-059-${Math.random().toString(36).slice(2, 8)}`
  clearDeliveredValue(sessionId)
  clearDeliveredReturned(sessionId)
  try {
    // (a) view with thenRemove anchors >= prefixUntil returns those summaries
    recordDeliveredView(
      sessionId,
      { status: 'done' },
      {
        prefixUntil: 3,
        thenRemove: {
          '1': 'dropped summary before prefixUntil',
          '3': 'summary at prefixUntil',
          '5': 'summary after prefixUntil',
        },
      },
    )
    const firstHandoff = String(takeDeliveredHandoff(sessionId))
    assert.match(firstHandoff, /summary at prefixUntil/)
    assert.match(firstHandoff, /summary after prefixUntil/)
    assert.equal(firstHandoff.includes('dropped summary before prefixUntil'), false, 'anchors < prefixUntil are omitted')
    assert.deepEqual([...deliveredReturned(sessionId)].sort(), ['anchor:3', 'anchor:5'])

    // (b) a second deliver in the same session naming the same anchor does not return it again
    recordDeliveredView(
      sessionId,
      { status: 'continuation' },
      {
        prefixUntil: 3,
        thenRemove: {
          '3': 'summary at prefixUntil',
          '7': 'fresh summary',
        },
      },
    )
    const secondHandoff = String(takeDeliveredHandoff(sessionId))
    assert.equal(secondHandoff.includes('summary at prefixUntil'), false, 'previously returned anchor is not returned again')
    assert.match(secondHandoff, /fresh summary/)
    assert.deepEqual([...deliveredReturned(sessionId)].sort(), ['anchor:3', 'anchor:5', 'anchor:7'])
  } finally {
    clearDeliveredValue(sessionId)
    clearDeliveredReturned(sessionId)
  }

  // (c) no view => no assistant prose returned
  const noviewSession = `sess-059-noview-${Math.random().toString(36).slice(2, 8)}`
  clearDeliveredValue(noviewSession)
  clearDeliveredReturned(noviewSession)
  try {
    recordDeliveredView(noviewSession, { value: 'only value' }, null)
    const handoffNoView = String(takeDeliveredHandoffWith(noviewSession, 'assistant prose without view'))
    assert.equal(handoffNoView.includes('assistant prose without view'), false, 'no view implies no assistant prose')
  } finally {
    clearDeliveredValue(noviewSession)
    clearDeliveredReturned(noviewSession)
  }

  // (d) view present => last assistant prose returned once
  const viewSession = `sess-059-view-${Math.random().toString(36).slice(2, 8)}`
  clearDeliveredValue(viewSession)
  clearDeliveredReturned(viewSession)
  try {
    recordDeliveredView(viewSession, { value: 'with view' }, { prefixUntil: 1, thenRemove: {} })
    const handoffWithView = String(takeDeliveredHandoffWith(viewSession, 'assistant prose with view'))
    assert.match(handoffWithView, /assistant prose with view/, 'view present returns assistant prose')
  } finally {
    clearDeliveredValue(viewSession)
    clearDeliveredReturned(viewSession)
  }
})

test('WHAT[repository-programming-059] an ordinary return leaves the delivered cell untouched', async () => {
  const { dir, cleanup } = sandbox()
  const sessionId = `sess-return-${Math.random().toString(36).slice(2, 8)}`
  try {
    clearDeliveredValue(sessionId)
    const outcome = await runProgram(dir, `    return { plain: true };`, sessionId)
    assert.equal(caseName(outcome), 'Succeeded')
    assert.equal(deliveredValue(sessionId), null, 'a plain return is not a delivery')
  } finally {
    clearDeliveredValue(sessionId)
    cleanup()
  }
})

test('WHAT[repository-programming-059] a later deliver does not return prose already handed back', () => {
  const sessionId = `sess-view-${Math.random().toString(36).slice(2, 8)}`
  clearDeliveredValue(sessionId)
  clearDeliveredReturned(sessionId)
  try {
    recordDeliveredView(sessionId, { files: 1 }, {
      prefixUntil: 1,
      thenRemove: { '2': 'the parser index', '4': 'the retry' },
    })
    const first = String(takeDeliveredHandoff(sessionId))
    assert.match(first, /\[\[returned\]\]/)
    assert.match(first, /the parser index/)
    assert.match(first, /the retry/)
    assert.equal(first.includes('files'), true)
    assert.deepEqual([...deliveredReturned(sessionId)].sort(), ['anchor:2', 'anchor:4'])

    recordDeliveredView(sessionId, { files: 2 }, {
      prefixUntil: 1,
      thenRemove: { '2': 'the parser index', '7': 'the new seam' },
    })
    const second = String(takeDeliveredHandoff(sessionId))
    assert.equal(second.includes('the parser index'), false, 'already returned prose is not returned again')
    assert.match(second, /the new seam/)
    assert.equal(deliveredValue(sessionId), null, 'take consumes the cell')
  } finally {
    clearDeliveredValue(sessionId)
    clearDeliveredReturned(sessionId)
  }
})

test('WHAT[repository-programming-059] a deliver without a view does not hand the assistant prose back', () => {
  const sessionId = `sess-noview-${Math.random().toString(36).slice(2, 8)}`
  clearDeliveredValue(sessionId)
  clearDeliveredReturned(sessionId)
  try {
    recordDeliveredView(sessionId, 'the terminal value', null)
    const handoff = String(takeDeliveredHandoff(sessionId))
    assert.equal(handoff, 'the terminal value')
    assert.equal(handoff.includes('[[returned]]'), false)
    assert.deepEqual(deliveredReturned(sessionId), [])
  } finally {
    clearDeliveredValue(sessionId)
    clearDeliveredReturned(sessionId)
  }
})
