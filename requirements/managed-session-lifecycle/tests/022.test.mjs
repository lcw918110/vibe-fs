import assert from 'node:assert/strict'
import test from 'node:test'
import { execFileSync, spawnSync } from 'node:child_process'
import { createHash } from 'node:crypto'
import { existsSync, mkdtempSync, mkdirSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'
import * as eventStore from '../../../dist/Persistence/EventStore/Surface.js'
import * as casebook from '../../../dist/Repository/Knowledge/Casebook/Surface.js'
import * as casebookIndex from '../../../dist/Repository/Knowledge/Casebook/IndexSurface.js'
import * as bookkeeper from '../../../dist/Repository/Knowledge/Casebook/BookkeeperSurface.js'
import * as lifecycle from '../../../dist/Repository/Knowledge/Casebook/LifecycleSurface.js'
import * as pluginLifecycle from '../../../dist/OpenCode/Plugin/PluginLifecycleSurface.js'
import * as dispatch from '../../../dist/Interaction/Dispatch/DispatchSurface.js'
import * as executionStatus from '../../../dist/Execution/Session/ChatExecution/StatusSurface.js'
import * as semanticTrace from '../../../dist/Context/Trace/SemanticTraceSurface.js'
import * as journal from '../../../dist/Persistence/Journal/Surface.js'
import { withExecutablePlugin } from '../../verification-system/tests/support/plugin-fixture.mjs'
import {
  CANONICAL_A,
  installBookkeeperRuntime,
  scriptedBookkeeperPort,
} from '../../knowledge-reuse/tests/support/bookkeeper-session-support.mjs'

const sandbox = () => {
  const dir = mkdtempSync(join(tmpdir(), 'wxs-delegate-settle-'))
  execFileSync('git', ['init', '--quiet', dir])
  mkdirSync(join(dir, '.wanxiang', 'casebook'), { recursive: true })
  return {
    dir,
    cleanup: () => rmSync(dir, { recursive: true, force: true }),
  }
}

test('WHAT[managed-session-lifecycle-022] CASE_SETTLE_uncommitted_finalize_does_not_publish_a_case', async () => {
  const { dir, cleanup } = sandbox()
  try {
    lifecycle.enable(dir)
    // No Bookkeeper runtime: observe archive refusal, not identity retention.
    const key = 'insp-settle-uncommitted'
    lifecycle.notePrompt(key, 'What owns PromptAuthority?')
    lifecycle.noteAnswer(key, 'Host owns PromptAuthority.')

    const settled = await lifecycle.tryFinalize(dir, key)
    assert.equal(settled.ok, false)
    assert.match(String(settled.error), /bookkeeper runtime unavailable/)

    const handle = eventStore.create(join(dir, '.git'), 'insp-settle-uncommitted-read')
    try {
      const fetched = await casebook.fetchCase(handle, 10, key)
      assert.equal(fetched.ok, true)
      assert.equal(fetched.value, null)
    } finally {
      eventStore.dispose(handle)
    }
  } finally {
    bookkeeper.resetRuntime()
    lifecycle.disable()
    cleanup()
  }
})

test('WHAT[managed-session-lifecycle-022] archive commits once and duplicate finalize creates no second Bookkeeper child', async () => {
  const { dir, cleanup } = sandbox()
  try {
    lifecycle.enable(dir)
    const { port, createCalls } = scriptedBookkeeperPort()
    const key = 'insp-settle-finalized'
    await installBookkeeperRuntime(port, [key])
    lifecycle.notePrompt(key, 'What owns PromptAuthority?')
    lifecycle.noteAnswer(key, 'Host owns PromptAuthority.')
    const first = await lifecycle.tryFinalize(dir, key)
    assert.equal(first.ok, true)
    assert.equal(createCalls.length, 1)

    const handle = eventStore.create(join(dir, '.git'), 'insp-settle-read')
    try {
      const fetched = await casebook.fetchCase(handle, 10, key)
      assert.equal(fetched.ok, true)
      assert.notEqual(fetched.value, null)
    } finally {
      eventStore.dispose(handle)
    }

    lifecycle.notePrompt(key, 'second finalize must not publish')
    lifecycle.noteAnswer(key, 'should be refused')
    const second = await lifecycle.tryFinalize(dir, key)
    assert.equal(second.ok, false)
    assert.match(String(second.error), /already finalized/)
    assert.equal(createCalls.length, 1)

    const reread = eventStore.create(join(dir, '.git'), 'insp-settle-reread')
    try {
      const still = await casebook.fetchCase(reread, 10, key)
      assert.equal(still.ok, true)
      assert.equal(still.value.sessionId, key)
      assert.equal(still.value.a, CANONICAL_A)
    } finally {
      eventStore.dispose(reread)
    }
  } finally {
    bookkeeper.resetRuntime()
    lifecycle.disable()
    cleanup()
  }
})

test('WHAT[managed-session-lifecycle-022] no draft finalizes successfully without creating a Bookkeeper child', async () => {
  const { dir, cleanup } = sandbox()
  try {
    lifecycle.enable(dir)
    const { port, createCalls } = scriptedBookkeeperPort()
    const key = 'insp-settle-empty'
    await installBookkeeperRuntime(port, [key])
    const settled = await lifecycle.tryFinalize(dir, key)
    assert.equal(settled.ok, true)
    assert.equal(createCalls.length, 0)
  } finally {
    bookkeeper.resetRuntime()
    lifecycle.disable()
    cleanup()
  }
})

const withCompletedEngineer = async (action, { beforeCreate } = {}) => {
  let originalRuntime
  await withExecutablePlugin(async (hooks, directory, createdIds, runtime) => {
    assert.strictEqual(pluginLifecycle.hooks(originalRuntime), hooks)
    assert.equal(lifecycle.isEnabled(), true)
    const manager = 'd0p-manager'
    const managerPhysical = 'd0p-manager-physical'
    const charge = 'D0-P charge: establish the original attached execution boundary.'
    const answer = 'D0-P actual assistant completion: the attached execution owns this response.'
    const managerMessage = { id: managerPhysical, sessionID: manager, role: 'user', agent: 'manager', model: {} }
    const managerParts = [{ type: 'text', text: 'Delegate one controlled Engineer investigation.' }]
    runtime.pushHostMessage(manager, { info: managerMessage, parts: managerParts })
    await hooks['chat.message']({ sessionID: manager, messageID: managerPhysical, agent: 'manager' },
      { message: managerMessage, parts: managerParts })
    const owner = dispatch.projectionObservation(runtime.journal, manager).activeLogicalRun
    assert.notEqual(owner, null)
    assert.equal(owner.session, manager)
    assert.equal(owner.authorityKind, 'HumanRoot')
    assert.equal(owner.authorityRoot, managerPhysical)
    assert.equal(typeof owner.logicalRun, 'string')
    assert.ok(owner.logicalRun.length > 0)
    assert.equal(owner.participantIdentity.role, 'manager')
    assert.equal(owner.participantIdentity.participant, 'manager')
    assert.deepEqual(executionStatus.query(runtime.journal, manager, managerPhysical), {
      accepted: true, providerStarted: false, terminal: false, disposition: null,
    })
    assert.equal(pluginLifecycle.attachedEngineer(originalRuntime, manager), null)

    let signalPrompt
    const prompted = new Promise(resolve => { signalPrompt = resolve })
    const originalPrompt = runtime.client.session.promptAsync
    runtime.client.session.promptAsync = async function (args) {
      const result = await Reflect.apply(originalPrompt, this, [args])
      if (args.body?.agent === 'engineer') {
        const child = args.path.id
        const physicalMessages = (await runtime.client.session.messages({ path: { id: child } })).data
        const physical = physicalMessages.filter(message => (message.info ?? message).role === 'user').at(-1)
        signalPrompt({ child, message: physical, args })
      }
      return result
    }
    let invocation
    try {
      invocation = pluginLifecycle.invokeEngineer(originalRuntime, manager, charge)
      const sent = await Promise.race([
        prompted,
        invocation.then(result => { throw new Error(`Engineer finished before its physical prompt receipt: ${JSON.stringify(result)}`) }),
      ])
      assert.equal(pluginLifecycle.attachedEngineer(originalRuntime, manager), sent.child)
      assert.equal(createdIds.includes(sent.child), true)
      assert.equal(sent.args.body.agent, 'engineer')
      assert.ok(sent.message)
      const physicalInfo = sent.message.info ?? sent.message
      const physicalParts = sent.message.parts
      assert.equal(typeof physicalInfo.id, 'string')
      assert.ok(physicalInfo.id.length > 0)
      assert.equal(Array.isArray(physicalParts), true)
      assert.ok(physicalParts.some(part => part.type === 'text' && part.text.length > 0))
      assert.ok(physicalInfo.model)
      assert.equal(physicalInfo.model.providerID, 'provider')
      assert.equal(physicalInfo.model.modelID, 'engineer-model')
      assert.equal(await pluginLifecycle.awaitAssignmentReady(originalRuntime, sent.child), true)
      const childProfile = dispatch.projectionObservation(runtime.journal, sent.child).activeLogicalRun
      assert.notEqual(childProfile, null)
      assert.equal(childProfile.authorityKind, 'AgentOwnerRoot')
      assert.equal(childProfile.authorityRoot, physicalInfo.id)
      assert.equal(childProfile.participantIdentity.role, 'engineer')
      assert.equal(childProfile.identitySeed.kind, 'InheritedFromOwner')
      assert.equal(childProfile.identitySeed.ownerSession, manager)
      assert.equal(childProfile.identitySeed.ownerLogicalRun, owner.logicalRun)
      assert.equal(childProfile.identitySeed.ownerAuthorityRoot, managerPhysical)
      assert.deepEqual(executionStatus.query(runtime.journal, sent.child, physicalInfo.id), {
        accepted: true, providerStarted: false, terminal: false, disposition: null,
      })

      const assistant = { info: {
        id: 'd0p-assistant-run', sessionID: sent.child, parentID: physicalInfo.id,
        role: 'assistant', agent: 'engineer',
        providerID: physicalInfo.model.providerID, modelID: physicalInfo.model.modelID,
        time: { created: 2 },
      }, parts: [] }
      runtime.pushHostMessage(sent.child, assistant)
      const user = { info: { ...physicalInfo, sessionID: sent.child, agent: 'engineer' },
        parts: physicalParts }
      assert.strictEqual(user.parts, sent.message.parts)
      await hooks['experimental.chat.messages.transform']({ sessionID: sent.child }, { messages: [user] })
      assert.deepEqual(executionStatus.query(runtime.journal, sent.child, physicalInfo.id), {
        accepted: true, providerStarted: true, terminal: false, disposition: null,
      })

      assistant.parts.push({ id: 'd0p-answer-part', type: 'text', text: answer })
      assistant.info.time.completed = 3
      assistant.info.finish = 'stop'
      await hooks.event({ event: { type: 'message.updated', properties: { info: assistant.info } } })
      await hooks.event({ event: { type: 'session.idle', properties: { sessionID: sent.child } } })
      const result = await invocation
      assert.equal(result.ok, true, result.reason)
      assert.equal(typeof result.workRecord, 'string')
      assert.ok(result.workRecord.trim().length > 0)
      assert.ok(result.workRecord.includes(answer))
      assert.doesNotMatch(result.workRecord, /^Opening(?:\r?\n|$)/)
      assert.equal(pluginLifecycle.attachedEngineer(originalRuntime, manager), sent.child)
      assert.equal(runtime.prompts.filter(prompt => prompt.path?.id === sent.child).length, 1)

      const trace = semanticTrace.snapshot(runtime.journal, sent.child)
      assert.equal(semanticTrace.hasOpening(trace), true)
      const terminal = semanticTrace.terminalEvidenceForProviderRun(assistant.info.id, trace)
      assert.ok(terminal)
      assert.equal(terminal.providerRun, assistant.info.id)
      assert.deepEqual(terminal.frontier, semanticTrace.headCursor(trace))
      assert.ok(terminal.frontier.sequence > 0)
      assert.match(terminal.textRef, /^blobs\/[0-9a-f]{64}$/)
      const terminalBody = await journal.JournalSurface_readPayload(runtime.journal, terminal.textRef)
      assert.deepEqual(terminalBody, { ok: true, content: answer })
      assert.equal(terminal.textDigest, createHash('sha256').update(terminalBody.content, 'utf8').digest('hex'))

      await action({ hooks, directory, createdIds, runtime, originalRuntime, manager, owner, sent,
        childProfile, charge, answer, result, terminal })
    } finally {
      runtime.client.session.promptAsync = originalPrompt
      // Dispose cancels/drains any pending call before we wait for its settlement.
      try {
        await hooks.dispose()
      } finally {
        if (invocation) await Promise.allSettled([invocation])
      }
    }
  }, {}, async input => {
    mkdirSync(join(input.directory, '.wanxiang', 'casebook'), { recursive: true })
    await beforeCreate?.(input.directory)
    originalRuntime = await pluginLifecycle.create(input)
    return pluginLifecycle.hooks(originalRuntime)
  })
}

test('WHAT[managed-session-lifecycle-022] finalize prerequisite uses the original plugin scope for Manager admission, attached Engineer completion and its production draft', async () => {
  await withCompletedEngineer(async ({ originalRuntime, manager, sent, charge, result }) => {
    assert.deepEqual(pluginLifecycle.takeEngineerDraft(originalRuntime, manager), {
      sessionId: sent.child,
      turns: [{ question: charge, answer: result.workRecord }],
    })
    assert.equal(pluginLifecycle.takeEngineerDraft(originalRuntime, manager), null)
  })
})

const d0Deferred = () => {
  let resolve
  const promise = new Promise(done => { resolve = done })
  return { promise, resolve }
}
const d0EventFiles = directory => {
  const events = join(directory, '.git', 'wanxiangshu', 'events')
  return Object.fromEntries(readdirSync(events).filter(name => name.endsWith('.ndjson'))
    .sort().map(name => [name, readFileSync(join(events, name), 'base64')]))
}
const d0Facts = files => Object.entries(files).flatMap(([file, encoded]) =>
  Buffer.from(encoded, 'base64').toString('utf8').trimEnd().split('\n')
    .filter(Boolean).map(line => ({ file, event: JSON.parse(line) })))
const d0Json = value => JSON.parse(JSON.stringify(value,
  (_key, item) => typeof item === 'bigint' ? item.toString() : item))

test('WHAT[managed-session-lifecycle-022] original child and owner deletion archive the completed Engineer and same-scope dispose awaits actual Bookkeeper completion', async () => {
  const canonicalQuestion = 'D0-G canonical question from the original completed draft'
  const canonicalAnswer = 'D0-G canonical answer from the original completed draft'
  const decoyIdentity = 'd0g-prior-decoy'
  const decoyText = 'D0-G nonempty baseline payload retained across deletion and disposal\n'
  let decoy
  await withCompletedEngineer(async context => {
    const { hooks, directory, createdIds, runtime, originalRuntime, manager, owner, sent,
      childProfile, charge, answer, result, terminal } = context
    const beforeCreatedIds = createdIds.slice()
    const entered = d0Deferred()
    const release = d0Deferred()
    const received = d0Deferred()
    const originalPrompt = runtime.client.session.promptAsync
    let bookkeeperEntered = 0
    let disposeObserved
    let disposalState = 'not-started'
    let bookkeeperReceipt
    let programAttempted = false
    let terminalPublished = false
    let bookkeeperAssistant
    runtime.client.session.promptAsync = async function (args) {
      if (args.body?.agent !== 'bookkeeper') return Reflect.apply(originalPrompt, this, [args])
      bookkeeperEntered += 1
      const receipt = { child: args.path.id, args }
      entered.resolve(receipt)
      await release.promise
      try {
        const response = await Reflect.apply(originalPrompt, this, [args])
        const messages = (await runtime.client.session.messages({ path: { id: receipt.child } })).data
        receipt.physical = messages.filter(message => (message.info ?? message).role === 'user').at(-1)
        received.resolve(receipt)
        return response
      } catch (error) {
        received.resolve({ error })
        throw error
      }
    }
    const completeBookkeeper = async () => {
      release.resolve()
      if (bookkeeperEntered === 0) return
      bookkeeperReceipt ??= await received.promise
      if (bookkeeperReceipt.error !== undefined) throw bookkeeperReceipt.error
      const { child, physical } = bookkeeperReceipt
      const physicalInfo = physical.info ?? physical
      if (bookkeeperAssistant === undefined) {
        bookkeeperAssistant = { info: { id: 'd0g-bookkeeper-assistant', sessionID: child,
          parentID: physicalInfo.id, role: 'assistant', agent: 'bookkeeper',
          time: { created: 4 } }, parts: [] }
        runtime.pushHostMessage(child, bookkeeperAssistant)
      }
      const program = `class Js extends JsProgram { async run() {
        const question = this.question().text();
        const answer = this.answer().text();
        if (!question.includes(${JSON.stringify(charge)}) || !answer.includes(${JSON.stringify(answer)}))
          throw new Error('Bookkeeper did not receive the original completed draft');
        this.setQuestion(${JSON.stringify(canonicalQuestion)});
        this.setAnswer(${JSON.stringify(canonicalAnswer)});
        return { changed: true };
      } }`
      try {
        if (!programAttempted) {
          programAttempted = true
          const output = await hooks.tool['js-bookkeeper'].execute({ program }, {
            sessionID: child, agent: 'bookkeeper', messageID: bookkeeperAssistant.info.id,
            callID: 'd0g-bookkeeper-program',
          })
          assert.equal(typeof output, 'string')
          bookkeeperAssistant.parts.push({ type: 'tool', tool: 'js-bookkeeper', callID: 'd0g-bookkeeper-program',
            state: { status: 'completed', input: { program }, output } })
        }
      } finally {
        if (!terminalPublished) {
          terminalPublished = true
          bookkeeperAssistant.info.time.completed = 5
          bookkeeperAssistant.info.finish = 'stop'
          await hooks.event({ event: { type: 'message.updated', properties: { info: bookkeeperAssistant.info } } })
        }
      }
    }
    let completedBookkeeper = false
    try {
      const eventsDir = join(directory, '.git', 'wanxiangshu', 'events')
      const resolveDecoyFile = () => {
        const legacy = join(eventsDir, 'd0g-decoy.ndjson')
        if (existsSync(legacy)) return legacy
        return join(eventsDir, 'd0g-decoy-0000.ndjson')
      }
      assert.deepEqual(dispatch.projectionObservation(runtime.journal, sent.child).activeLogicalRun, childProfile)
      assert.deepEqual(readFileSync(resolveDecoyFile()), decoy.bytes)
      await hooks.event({ event: { type: 'session.deleted',
        properties: { sessionID: sent.child, parentID: manager } } })
      assert.equal(pluginLifecycle.attachedEngineer(originalRuntime, manager), null)
      await hooks.event({ event: { type: 'session.deleted', properties: { sessionID: manager } } })
      disposalState = 'pending'
      const disposal = hooks.dispose()
      disposeObserved = disposal.then(() => { disposalState = 'fulfilled'; return { ok: true } },
        error => { disposalState = 'rejected'; return { ok: false, error } })
      const prepared = await Promise.race([
        entered.promise,
        disposeObserved.then(outcome => {
          throw new Error(`original deletion disposal ended before its Bookkeeper SendPrompt: ${outcome.ok ? 'fulfilled' : 'rejected'}`,
            { cause: outcome.error })
        }),
      ])
      assert.equal(bookkeeperEntered, 1)
      assert.equal(prepared.args.body.agent, 'bookkeeper')
      assert.deepEqual(prepared.args.body.tools, { '*': false, 'js-bookkeeper': true })
      const promptText = prepared.args.body.parts.filter(part => part.type === 'text').map(part => part.text).join('\n')
      assert.ok(promptText.includes(sent.child))
      assert.ok(promptText.includes(charge))
      assert.ok(promptText.includes(answer))
      assert.ok(promptText.includes('CaseFinalize'))
      assert.equal(runtime.prompts.some(prompt => prompt.path?.id === prepared.child), false)
      assert.deepEqual(dispatch.projectionObservation(runtime.journal, sent.child).activeLogicalRun, childProfile)
      assert.deepEqual(dispatch.projectionObservation(runtime.journal, manager).activeLogicalRun, owner)
      const beforeFiles = d0EventFiles(directory)
      const beforeIndex = casebookIndex.tryGet()
      assert.equal(d0Facts(beforeFiles).filter(({ event }) => event.event_type === 'EngineerCaseCaptured'
        && event.payload.identity === sent.child).length, 0)

      // A bounded pending control; the isolated owned-drain mutation is separate.
      await new Promise(resolve => setImmediate(resolve))
      assert.equal(disposalState, 'pending')
      const heldFiles = d0EventFiles(directory)
      for (const [file, encoded] of Object.entries(beforeFiles)) {
        const prefix = Buffer.from(encoded, 'base64')
        assert.equal(Buffer.from(heldFiles[file], 'base64').subarray(0, prefix.length).equals(prefix), true,
          `${file} retains its original canonical prefix while child cleanup may append`)
      }
      const heldKey = Object.keys(heldFiles).find(k => k === 'd0g-decoy.ndjson' || k.startsWith('d0g-decoy-'))
      assert.equal(heldFiles[heldKey], decoy.bytes.toString('base64'))
      assert.equal(d0Facts(heldFiles).filter(({ event }) => event.event_type === 'EngineerCaseCaptured'
        && event.payload.identity === sent.child).length, 0)
      assert.deepEqual(casebookIndex.tryGet(), beforeIndex)
      assert.deepEqual(await journal.JournalSurface_readPayload(runtime.journal, terminal.textRef),
        { ok: true, content: answer })
      assert.match(decoy.payloadRef, /^[0-9a-f]{64}$/)
      assert.deepEqual(await journal.JournalSurface_readPayload(runtime.journal, `blobs/${decoy.payloadRef}`),
        { ok: true, content: decoyText })

      await completeBookkeeper()
      completedBookkeeper = true
      const disposed = await disposeObserved
      assert.equal(disposed.ok, true, disposed.error?.message)
      assert.equal(disposalState, 'fulfilled')
      assert.equal(bookkeeperEntered, 1)
      assert.deepEqual(createdIds.slice(beforeCreatedIds.length), [bookkeeperReceipt.child])
      assert.deepEqual(createdIds.slice(0, beforeCreatedIds.length), beforeCreatedIds)
      assert.equal(runtime.prompts.filter(prompt => prompt.body?.agent === 'bookkeeper').length, 1)
      const afterFiles = d0EventFiles(directory)
      for (const [file, encoded] of Object.entries(beforeFiles)) {
        assert.equal(Buffer.from(afterFiles[file], 'base64').subarray(0, Buffer.from(encoded, 'base64').length)
          .equals(Buffer.from(encoded, 'base64')), true, `${file} retains its original canonical prefix`)
      }
      const afterKey = Object.keys(afterFiles).find(k => k === 'd0g-decoy.ndjson' || k.startsWith('d0g-decoy-'))
      assert.equal(afterFiles[afterKey], decoy.bytes.toString('base64'))
      const cases = d0Facts(afterFiles).filter(({ event }) => event.event_type === 'EngineerCaseCaptured')
      assert.equal(cases.length, 2)
      const captured = cases.find(({ event }) => event.payload.identity === sent.child).event
      assert.equal(captured.payload.source_trace, sent.child)
      assert.equal(captured.payload.q, canonicalQuestion)
      assert.equal(captured.payload.a, canonicalAnswer)
      assert.deepEqual(captured.payload.related_paths, [])
      assert.equal(captured.payload.completion_file_state, '{}')
      assert.equal(captured.payload.maintenance_file_state, '{}')
      assert.deepEqual(captured.payload.observations, [])
      assert.deepEqual(captured.parents, [decoy.fact.id])
      assert.equal(d0Facts(afterFiles).some(({ event }) => event.event_type === 'ProjectionCutTail'), false)
      const afterIndex = casebookIndex.tryGet()
      assert.ok(afterIndex.epoch > beforeIndex.epoch)
      assert.ok(afterIndex.cases.some(entry => entry.question === canonicalQuestion))
      assert.ok(afterIndex.cases.some(entry => entry.question === decoy.current.q))
      assert.deepEqual(dispatch.projectionObservation(runtime.journal, sent.child).activeLogicalRun, childProfile)
      assert.equal(dispatch.projectionObservation(runtime.journal, bookkeeperReceipt.child).activeLogicalRun, null)
      const request = d0Json({ parentPid: process.pid, files: afterFiles, old: decoy.current,
        oldEvent: decoy.fact, payloadRef: decoy.payloadRef, payloadBody: decoyText,
        target: { identity: sent.child, sessionId: sent.child, sourceTrace: sent.child,
          q: canonicalQuestion, a: canonicalAnswer, relatedPaths: [],
          completionFileState: '{}', maintenanceFileState: '{}', accessOrder: 1n,
          lastAccessOrder: 1n, observations: [] }, captureId: captured.event_id })
      const coldEnv = { ...process.env }
      delete coldEnv.NODE_TEST_CONTEXT
      delete coldEnv.WANXIANGSHU_NO_FATAL_EXIT
      const cold = spawnSync(process.execPath, [fileURLToPath(new URL('./support/deletion-cold-child.mjs', import.meta.url)),
        join(directory, '.git'), JSON.stringify(request)], { encoding: 'utf8', env: coldEnv })
      assert.equal(cold.signal, null, cold.stderr)
      assert.equal(cold.status, 0, cold.stderr)
      const observation = JSON.parse(cold.stdout.trim())
      assert.notEqual(observation.pid, process.pid)
      assert.equal(observation.verified, true)
      assert.deepEqual(d0EventFiles(directory), afterFiles)
      assert.deepEqual(readFileSync(resolveDecoyFile()), decoy.bytes)
      assert.ok(result.workRecord.includes(answer))
    } finally {
      release.resolve()
      try {
        if (bookkeeperEntered > 0 && !completedBookkeeper) await completeBookkeeper()
      } finally {
        try {
          if (disposeObserved) await disposeObserved
        } finally {
          runtime.client.session.promptAsync = originalPrompt
        }
      }
    }
  }, { beforeCreate: async directory => {
    writeFileSync(join(directory, 'd0g-decoy.txt'), decoyText)
    const handle = eventStore.create(join(directory, '.git'), 'd0g-decoy')
    try {
      const baseline = await casebook.freezeCompletionState(handle, directory, ['d0g-decoy.txt'])
      assert.equal((await casebook.finalizeEngineerCase(handle, decoyIdentity, 'd0g-decoy-trace',
        'D0-G prior decoy question', 'D0-G prior decoy answer', ['d0g-decoy.txt'], baseline)).kind, 'finalized')
      const eventsDir = join(directory, '.git', 'wanxiangshu', 'events')
      const decoyFile = existsSync(join(eventsDir, 'd0g-decoy.ndjson'))
        ? join(eventsDir, 'd0g-decoy.ndjson')
        : join(eventsDir, 'd0g-decoy-0000.ndjson')
      decoy = { current: await casebook.fetchCaseByIdentity(handle, decoyIdentity),
        fact: eventStore.read(handle, eventStore.head(handle, 'casebook')),
        payloadRef: JSON.parse(baseline)['d0g-decoy.txt'].payloadRef,
        bytes: readFileSync(decoyFile) }
      assert.equal(Buffer.from(await eventStore.readPayload(handle, decoy.payloadRef)).toString('utf8'), decoyText)
    } finally {
      eventStore.dispose(handle)
    }
  } })
})

test.todo('WHAT[managed-session-lifecycle-022] real deletion preserves exact Inspector identity for NotCommitted, Unknown and PhaseConflict; only committed or empty finalization releases it (GAP-133)')
