import assert from 'node:assert/strict'
import {mkdtempSync, rmSync} from 'node:fs'
import {tmpdir} from 'node:os'
import {join} from 'node:path'
import test from 'node:test'
import * as journalSurface from '../../../dist/Persistence/Journal/Surface.js'
import * as lifecycle from '../../../dist/Mission/Lifecycle/Surface.js'
import * as workspaceSurface from '../../../dist/OpenCode/Host/WorkspaceEventStoreSurface.js'
import * as hostSurface from '../../../dist/Change/Host/Surface.js'
import * as managerWorkflow from '../../../dist/Mission/Manager/WorkflowSurface.js'
import {withReview, scores} from './support/plugin.mjs'
import { lifecycleWorkView } from '../../verification-system/tests/support/plugin-fixture.mjs'

const life = lifecycle.LifecycleSurface ?? lifecycle

const successorTexts = (runtime) =>
  runtime.prompts.map((item) => JSON.stringify(item)).filter((text) => text.includes('继任「执行」'))

test('WHAT[work-011] clearing the manager ledger relays once, sends the successor prompt once, and a replay does not send again', async () => {
  await withReview(async ({ execute, runtime, session, directory }) => {
    const root = runtime.messages.find((message) => (message.info?.id ?? message.id) === 'user-root')
    const rootBytes = JSON.stringify(root)
    const before = successorTexts(runtime).length
    const relayed = String(await execute(scores('CLEAR'), { call: 'relay-call', run: 'relay-run' }))
    assert.match(relayed, /ok = true/)
    assert.match(relayed, /kind = "Relayed"/)
    assert.equal(successorTexts(runtime).length, before + 1)
    assert.deepEqual(runtime.abortedIds.filter((id) => id === session), [])
    assert.equal(JSON.stringify(root), rootBytes, 'the relay does not rewrite stored user text')
    const replay = String(await execute(scores('CLEAR'), { call: 'relay-call', run: 'relay-run' }))
    assert.match(replay, /ok = true/)
    assert.match(replay, /kind = "Relayed"/)
    assert.equal(successorTexts(runtime).length, before + 1)
    const commonDir = await journalSurface.JournalSurface_initializeStateRoot(directory)
    const view = life.inspected(commonDir, session)
    assert.equal(view.Delivered, false)
    assert.equal(view.ActiveId.length > 0, true)
  })
})

test('WHAT[work-011] an empty cleanup review stops the current attempt once, and a replay does not stop again', async () => {
  await withReview(async ({ execute, runtime, session, directory }) => {
    assert.match(String(await execute(scores('CLEAR'), { call: 'relay-call', run: 'relay-run' })), /kind = "Relayed"/)
    const advanced = String(await execute(scores('PERFECT'), { call: 'advance-call', run: 'advance-run' }))
    assert.match(advanced, /ok = true/)
    assert.match(advanced, /kind = "Advanced"/)
    assert.match(advanced, /收尾/)
    const before = runtime.abortedIds.filter((id) => id === session).length
    const finished = String(await execute(scores('PERFECT'), { call: 'finish-call', run: 'finish-run' }))
    assert.match(finished, /ok = true/)
    assert.match(finished, /kind = "Completed"/)
    assert.equal(runtime.abortedIds.filter((id) => id === session).length, before + 1)
    const replay = String(await execute(scores('PERFECT'), { call: 'finish-call', run: 'finish-run' }))
    assert.match(replay, /ok = true/)
    assert.match(replay, /kind = "Completed"/)
    assert.equal(runtime.abortedIds.filter((id) => id === session).length, before + 1)
    const commonDir = await journalSurface.JournalSurface_initializeStateRoot(directory)
    const seen = life.inspected(commonDir, session)
    assert.equal(seen.Stage, '已完成')
    assert.equal(seen.Delivered, true)
    assert.equal(seen.ActiveId ?? null, null)
  })
})

test('WHAT[work-011] a thrown stop leaves the committed completion in place', async () => {
  await withReview(async ({ execute, runtime, session, directory }) => {
    assert.match(String(await execute(scores('CLEAR'), { call: 'relay-call', run: 'relay-run' })), /kind = "Relayed"/)
    assert.match(String(await execute(scores('PERFECT'), { call: 'advance-call', run: 'advance-run' })), /kind = "Advanced"/)
    runtime.client.session.abort = async () => {
      throw new Error('terminal-interrupt-thrown')
    }
    try {
      await execute(scores('PERFECT'), { call: 'finish-call', run: 'finish-run' })
    } catch {
      // The stop port may surface the throw. The committed completion stays.
    }
    const commonDir = await journalSurface.JournalSurface_initializeStateRoot(directory)
    const seen = life.inspected(commonDir, session)
    assert.equal(seen.Stage, '已完成')
    assert.equal(seen.Delivered, true)
    assert.equal(seen.ActiveId ?? null, null)
  })
})

test('WHAT[work-011] a review by the incumbency owner is recorded', async () => {
  await withReview(async ({ execute, runtime, session }) => {
    const recorded = String(await execute([{ key: 'extra', value: 'noted' }], { call: 'self-call', run: 'self-run' }))
    assert.match(recorded, /ok = true/)
    assert.match(recorded, /kind = "Reviewed"/)
    assert.match(recorded, /milestone = "执行"/)
    assert.doesNotMatch(recorded, /PROGRAM_FAILED|owner cannot review/)
    const view = await lifecycleWorkView(runtime, session)
    assert.equal(view.active, true)
    assert.equal(view.delivered, false)
  })
})

// ── The successor-prompt occasion, read from the fold alone ────────────────
// WHAT[work-011]: the occasion is derived from the durable fold — sequence S4,
// an open tenure, and a committed retirement that the open tenure has not
// reviewed yet. `successorPromptContextOf` is the read-only probe over the same
// predicate the dispatch reads; it dispatches nothing.

const withSeededManagerWork = async (outcome, body) => {
  const root = mkdtempSync(join(tmpdir(), 'wxs-successor-context-'))
  const stateRoot = await journalSurface.JournalSurface_initializeStateRoot(root)
  const store = workspaceSurface.acquire(stateRoot)
  try {
    const sessionId = `manager-${outcome ?? 'first'}`
    const submit = (id, updates) =>
      life.submitReview(stateRoot, sessionId, id, 'Manager', 'run-1', id, updates, [], '')
    if (outcome == null) {
      const opened = submit('open', [])
      assert.equal(opened.kind, 'Asked', opened.reason)
    } else if (outcome === 'Completed') {
      assert.equal(submit('review', [{ key: 'job', value: 'do' }]).kind, 'Reviewed')
      assert.equal(submit('relay', [{ key: 'job', value: null }]).kind, 'Relayed')
      assert.equal(submit('advance', []).kind, 'Advanced')
      assert.equal(submit('close', []).kind, 'Completed')
    } else {
      assert.equal(submit('review', [{ key: 'stayed', value: 'keep' }]).kind, 'Reviewed')
      const relayed = submit('relay', [{ key: 'stayed', value: null }])
      assert.equal(relayed.kind, 'Relayed', relayed.reason)
    }
    await body({root, stateRoot, store, sessionId, view: life.inspected(stateRoot, sessionId)})
  } finally {
    workspaceSurface.release(stateRoot)
    rmSync(root, {recursive: true, force: true})
  }
}

const successorPromptOf = async (root, sessionId) => managerWorkflow.successorPromptContextOf(root, sessionId)

test('WHAT[work-011] a first tenure with no retirement owes no successor prompt', async () => {
  await withSeededManagerWork(null, async ({root, sessionId}) => {
    const context = await successorPromptOf(root, sessionId)
    assert.equal(context.present, false, 'the first tenure has no predecessor to hand over')
    assert.equal(context.retirementId, '')
    assert.equal(context.providerRunId, '')
  })
})

test('WHAT[work-011] a Relayed retirement owes the successor prompt until that successor is reviewed', async () => {
  await withSeededManagerWork('Stayed', async ({root, stateRoot, sessionId, view}) => {
    const retirement = Array.from(view.Retirements ?? []).at(-1)
    assert.equal(retirement.Outcome, 'Relayed')
    assert.equal(view.ActiveId.length > 0, true, 'the relay opened its successor in the same record')
    assert.equal(view.Reviewed, false, 'the successor starts unreviewed')

    const context = await successorPromptOf(root, sessionId)
    assert.equal(context.present, true, 'the unreviewed successor owes the successor prompt')
    assert.equal(context.retirementId, retirement.RetirementId, 'the occasion is named by the retirement')
    assert.equal(context.providerRunId, 'run-1', 'the cut carries the provider run that made the retirement')

    const reviewed = life.submitReview(
      stateRoot,
      sessionId,
      'reviewed-successor',
      'Manager',
      'run-1',
      'reviewed-successor',
      [{ key: 'stayed', value: 'accepted' }],
      [],
      '',
    )
    assert.equal(reviewed.kind, 'Reviewed', reviewed.reason)
    assert.equal((await successorPromptOf(root, sessionId)).present, false, 'a reviewed successor owes no successor prompt')
  })
})

test('WHAT[work-011] a completed work refuses hand-off and reopens only from an operator message', async () => {
  await withSeededManagerWork('Completed', async ({root, stateRoot, sessionId, view}) => {
    const retired = Array.from(view.Retirements ?? []).at(-1)
    assert.equal(retired.Outcome, 'Completed')
    assert.equal(view.ActiveId ?? null, null, 'a completed work has no open tenure')
    assert.equal((await successorPromptOf(root, sessionId)).present, false, 'a completed work owes no successor prompt')

    const receipt = await hostSurface.handOffDeliveredTenure(root, sessionId, 'hand-off:successor-context', 3)
    assert.equal(receipt.ok, false, receipt.error)
    assert.match(receipt.error, /operator message/)
    assert.equal(life.inspected(stateRoot, sessionId).ActiveId ?? null, null)

    const reopened = life.execute(stateRoot, sessionId, 'operator-1', {
      type: 'OperatorMessage', kind: 'Operator', messageId: 'operator-1',
    })
    assert.equal(reopened.Status, 'Committed', reopened.Reason)
    assert.equal(reopened.Kind, 'Reopened')
    const after = life.inspected(stateRoot, sessionId)
    assert.equal(after.Stage, '执行')
    assert.equal((await successorPromptOf(root, sessionId)).present, false)
    const same = life.execute(stateRoot, sessionId, 'operator-1b', {
      type: 'OperatorMessage', kind: 'Operator', messageId: 'operator-1',
    })
    assert.equal(same.Status, 'Observed')
    const other = life.execute(stateRoot, sessionId, 'operator-2', {
      type: 'OperatorMessage', kind: 'Operator', messageId: 'operator-2',
    })
    assert.equal(other.Status, 'Conflict')
    assert.equal(life.inspected(stateRoot, sessionId).ActiveId, after.ActiveId)
  })
})

