import test from 'node:test'
import assert from 'node:assert/strict'
import { ofArray as listOfArray } from '../../../dist/fable_modules/fable-library-js.5.13.0/List.js'
import * as recoveryWorkflow from '../../../dist/Execution/Delegation/ChildRecoveryWorkflow.js'
import * as journalSurface from '../../../dist/Persistence/Journal/Surface.js'
import * as journalAdapter from '../../../dist/Composition/Durable/AgentJournalPortAdapter.js'
import * as agentJournal from '../../../dist/Persistence/Journal/AgentJournal.js'
import * as chatProjection from '../../../dist/Execution/Session/ChatExecution/Projection.js'
import * as chatFacts from '../../../dist/Execution/Session/ChatExecution/Facts.js'
import * as chatJournal from '../../../dist/Composition/Durable/ChatExecutionJournal.js'
import * as identity from '../../../dist/Foundation/Identity.js'
import * as roles from '../../../dist/Foundation/Roles.js'
import * as childRecovery from '../../../dist/Execution/Delegation/Fork/ChildRecovery.js'
import * as linkage from '../../../dist/Execution/Delegation/LinkageProjection.js'
import {
  agentIdOf,
  childTurn,
  forkCall,
  joinCall,
  observeTerminals,
  physicalUserMessageOf,
  withRealFork,
  workLifecycleOf,
} from './support/real-fork.mjs'

const textPart = (text) => ({ tag: 0, fields: [text] })
const userMessage = (id, text) => ({
  Id: id,
  Role: 'user',
  Agent: 'engineer',
  Finish: undefined,
  ErrorName: undefined,
  Model: undefined,
  ParentId: undefined,
  CreatedAt: 1,
  Completed: true,
  IsCompaction: false,
  PromptKey: undefined,
  Parts: [textPart(text)],
  PartIds: [],
  ToolParts: [],
})
const assistantMessage = (id, parentId, view) => ({
  Id: id,
  Role: 'assistant',
  Agent: 'engineer',
  Finish: 'stop',
  ErrorName: undefined,
  Model: undefined,
  ParentId: parentId,
  CreatedAt: 2,
  Completed: true,
  IsCompaction: false,
  PromptKey: undefined,
  Parts: [textPart(view)],
  PartIds: [],
  ToolParts: [],
})

const journalOf = (runtime) => journalSurface.JournalHandle__get_Journal(runtime.journal)

const acceptedState = (journal, sessionId, physicalId) =>
  chatProjection.ChatExecutionProjection_byKey(
    new chatFacts.ChatExecutionKey(
      identity.SessionIdModule_create(sessionId),
      identity.PhysicalUserMessageIdModule_create(physicalId),
    ),
    agentJournal.AgentJournalModule_snapshot(journal).AgentProjections.ChatExecutions,
  )

const canonicalWorkRoot = (journal, owner, agentId) => {
  const handle = new identity.HandleId(0, [identity.AgentHandleIdModule_create(agentId)])
  const projection = agentJournal.AgentJournalModule_handleProjection(
    journal,
    identity.SessionIdModule_create(owner),
  )
  const record = linkage.HandleProjection_tryFind(handle, projection)
  assert.ok(record?.Work, `the fork must admit work for ${agentId}`)
  return identity.AuthorityRootUserMessageIdModule_value(record.Work.AuthorityRoot)
}

const portsOf = (journal, snapshot) => ({
  Journal: journalAdapter.fromAgentJournal(journal),
  DeliveryWitness: undefined,
  AcceptedRoot: undefined,
  ParentId: identity.SessionIdModule_create('owner'),
  Snapshot: snapshot,
  AgentId: 'agent',
  Handle: new identity.HandleId(0, [identity.AgentHandleIdModule_create('agent')]),
  ChildSession: identity.SessionIdModule_create('child'),
  Role: roles.Role.Engineer,
  Agent: 'engineer',
  Observations: listOfArray([childRecovery.HostObservation.SessionActive]),
  Pulse: undefined,
  Clock: { UtcNow: () => new Date() },
})

// Field debt: the last user message in a child transcript can be a retry's
// physical message, whose id is not the Authority Root. Recovery used to
// promote that physical id to the root, then look up the delivery witness and
// encode the completion under it; the canonical admitted work stayed under the
// original root, so a restart could never settle the child.
test('WHAT[delegation-042] restart recovery takes the Authority Root from the durable Accepted of the exact physical message', async () => {
  const owner = 'owner-recovery-retry-042'
  await withRealFork(owner, async ({ hooks, createdIds, runtime }) => {
    const terminals = await observeTerminals(runtime)
    try {
      const started = await forkCall(hooks, owner, 'Ada', 'RETRY-RECOVERY')
      const child = createdIds[0]
      const agentId = agentIdOf(started)
      await runtime.awaitIngress()

      const initialPhysical = physicalUserMessageOf(runtime, child).id
      const journal = journalOf(runtime)
      const initialAccepted = acceptedState(journal, child, initialPhysical)
      assert.ok(initialAccepted, 'the child prompt must have a durable Accepted cell')
      const initialRoot = identity.AuthorityRootUserMessageIdModule_value(
        chatProjection.ChatExecutionState__get_acceptedEvidence(initialAccepted).AuthorityRootUserMessageId,
      )
      assert.equal(
        canonicalWorkRoot(journal, owner, agentId),
        initialRoot,
        'the accepted root and the canonical admitted work agree',
      )

      // A retry lands as a new physical user message under the original root.
      const retryPhysical = `msg-${child}-retry-042`
      const initialEvidence = chatProjection.ChatExecutionState__get_acceptedEvidence(initialAccepted)
      const retryEvidence = new chatFacts.AcceptedChatExecutionEvidence(
        initialEvidence.SessionId,
        initialEvidence.LogicalRunId,
        initialEvidence.AuthorityRootUserMessageId,
        initialEvidence.AuthorityKind,
        initialEvidence.IdentitySeed,
        identity.PhysicalUserMessageIdModule_create(retryPhysical),
        initialEvidence.Origin,
      )
      const accepted = await chatJournal.ManagedChatAcceptance_accept(
        journal,
        new chatFacts.ChatExecutionKey(
          identity.SessionIdModule_create(child),
          identity.PhysicalUserMessageIdModule_create(retryPhysical),
        ),
        retryEvidence,
      )
      assert.equal(accepted.tag, 0, JSON.stringify(accepted))

      const view = JSON.stringify({ body: ['RETRY PROSE'], carry: [] })
      const messages = [
        userMessage(initialPhysical, 'initial charge'),
        userMessage(retryPhysical, 'retry charge'),
        assistantMessage(`run-${child}-retry-042`, retryPhysical, view),
      ]
      const snapshot = {
        GetMessages: () => Promise.resolve({ tag: 0, fields: [listOfArray(messages)] }),
      }

      const witnessRoots = []
      const ports = portsOf(journal, snapshot)
      ports.ParentId = identity.SessionIdModule_create(owner)
      ports.AgentId = agentId
      ports.Handle = new identity.HandleId(0, [identity.AgentHandleIdModule_create(agentId)])
      ports.ChildSession = identity.SessionIdModule_create(child)
      ports.DeliveryWitness = (_session, _run, root) => {
        const value = identity.AuthorityRootUserMessageIdModule_value(root)
        witnessRoots.push(value)
        return value === initialRoot
      }
      ports.AcceptedRoot = (sessionId, physicalId) => {
        const state = chatProjection.ChatExecutionProjection_byKey(
          new chatFacts.ChatExecutionKey(sessionId, physicalId),
          agentJournal.AgentJournalModule_snapshot(journal).AgentProjections.ChatExecutions,
        )
        if (state == null) return undefined
        const evidence = chatProjection.ChatExecutionState__get_acceptedEvidence(state)
        return {
          PhysicalUserMessageId: evidence.PhysicalUserMessageId,
          AuthorityRoot: evidence.AuthorityRootUserMessageId,
        }
      }
      let pulsed = false
      ports.Pulse = () => {
        pulsed = true
      }

      const resolved = await recoveryWorkflow.resolveAndCommit(ports)
      assert.equal(resolved.tag, 0, JSON.stringify(resolved))
      assert.equal(resolved.fields[0].tag, 1, 'the retry terminal is recovered')
      assert.equal(pulsed, true)
      assert.deepEqual(witnessRoots, [initialRoot], 'the witness is queried under the durable root, not the retry physical')
      assert.equal(
        workLifecycleOf(runtime, owner, agentId, child, initialRoot),
        'CompletedAwaitingJoin',
        'the completion belongs to the original canonical work',
      )

      const joined = await joinCall(hooks, owner)
      assert.match(joined, /RETRY PROSE/)
    } finally {
      terminals.dispose()
    }
  })
})

// A work that already carries HandleWorkCompleted is settled from durable
// evidence alone; recovery must not require an Accepted snapshot or append a
// second completion. Join still returns the child's material exactly once.
test('WHAT[delegation-042] an existing HandleWorkCompleted still joins normally after restart recovery', async () => {
  const owner = 'owner-recovery-completed-042'
  await withRealFork(owner, async ({ hooks, createdIds, runtime }) => {
    const terminals = await observeTerminals(runtime)
    try {
      const started = await forkCall(hooks, owner, 'Ada', 'COMPLETED-RECOVERY')
      const child = createdIds[0]
      const agentId = agentIdOf(started)
      await runtime.awaitIngress()
      const journal = journalOf(runtime)
      const root = canonicalWorkRoot(journal, owner, agentId)

      await childTurn(hooks, runtime, child, `run-${child}-normal-042`, 'NORMAL PROSE', 'NORMAL-ANSWER', terminals.awaitRun)
      assert.equal(workLifecycleOf(runtime, owner, agentId, child, root), 'CompletedAwaitingJoin')

      const ports = portsOf(journal, undefined)
      ports.ParentId = identity.SessionIdModule_create(owner)
      ports.AgentId = agentId
      ports.Handle = new identity.HandleId(0, [identity.AgentHandleIdModule_create(agentId)])
      ports.ChildSession = identity.SessionIdModule_create(child)

      const resolved = await recoveryWorkflow.resolveAndCommit(ports)
      assert.equal(resolved.tag, 0, JSON.stringify(resolved))
      assert.equal(resolved.fields[0].tag, 1, 'the durable completion is recovered')
      assert.equal(workLifecycleOf(runtime, owner, agentId, child, root), 'CompletedAwaitingJoin', 'no second completion is written')

      const joined = await joinCall(hooks, owner)
      assert.match(joined, /NORMAL-ANSWER/)
    } finally {
      terminals.dispose()
    }
  })
})

// Missing durable acceptance is an observation, not permission to invent a
// root from the physical id. Recovery waits (RecoveryIncomplete) and writes
// no completion; the active work stays active.
test('WHAT[delegation-042] a terminal without durable Accepted is observed, never guessed', async () => {
  const owner = 'owner-recovery-missing-042'
  await withRealFork(owner, async ({ hooks, createdIds, runtime }) => {
    const terminals = await observeTerminals(runtime)
    try {
      const started = await forkCall(hooks, owner, 'Ada', 'MISSING-ACCEPTED')
      const child = createdIds[0]
      const agentId = agentIdOf(started)
      await runtime.awaitIngress()
      const journal = journalOf(runtime)
      const root = canonicalWorkRoot(journal, owner, agentId)

      const view = JSON.stringify({ body: ['UNACCEPTED PROSE'], carry: [] })
      const unknownPhysical = `msg-${child}-unaccepted-042`
      const messages = [
        userMessage(root, 'initial charge'),
        assistantMessage(`run-${child}-unaccepted-042`, unknownPhysical, view),
      ]
      const snapshot = {
        GetMessages: () => Promise.resolve({ tag: 0, fields: [listOfArray(messages)] }),
      }

      const ports = portsOf(journal, snapshot)
      ports.ParentId = identity.SessionIdModule_create(owner)
      ports.AgentId = agentId
      ports.Handle = new identity.HandleId(0, [identity.AgentHandleIdModule_create(agentId)])
      ports.ChildSession = identity.SessionIdModule_create(child)
      ports.AcceptedRoot = () => undefined
      ports.DeliveryWitness = () => {
        assert.fail('recovery must not query the delivery witness without a durable Accepted root')
      }

      const resolved = await recoveryWorkflow.resolveAndCommit(ports)
      assert.equal(resolved.tag, 0, JSON.stringify(resolved))
      assert.equal(resolved.fields[0].tag, 3, 'missing Accepted is RecoveryIncomplete, not a guessed terminal')
      assert.equal(workLifecycleOf(runtime, owner, agentId, child, root), 'Active', 'no completion is written')

      // A newer user message after the terminal assistant is a pending turn;
      // the stale assistant is not this child's terminal.
      const pendingSnapshot = {
        GetMessages: () => Promise.resolve({
          tag: 0,
          fields: [listOfArray([
            userMessage(root, 'initial charge'),
            assistantMessage(`run-${child}-unaccepted-042`, unknownPhysical, view),
            userMessage(`msg-${child}-newer-042`, 'newer charge'),
          ])],
        }),
      }
      const pendingPorts = portsOf(journal, pendingSnapshot)
      pendingPorts.ParentId = identity.SessionIdModule_create(owner)
      pendingPorts.AgentId = agentId
      pendingPorts.Handle = new identity.HandleId(0, [identity.AgentHandleIdModule_create(agentId)])
      pendingPorts.ChildSession = identity.SessionIdModule_create(child)
      pendingPorts.AcceptedRoot = () => {
        assert.fail('a pending turn must not be resolved as a terminal')
      }
      pendingPorts.DeliveryWitness = () => {
        assert.fail('a pending turn must not query the delivery witness')
      }

      const pending = await recoveryWorkflow.resolveAndCommit(pendingPorts)
      assert.equal(pending.tag, 0, JSON.stringify(pending))
      assert.equal(pending.fields[0].tag, 0, 'a pending turn is RecoveredActive, not a terminal')
      assert.equal(workLifecycleOf(runtime, owner, agentId, child, root), 'Active', 'the pending turn writes no completion')
    } finally {
      terminals.dispose()
    }
  })
})
