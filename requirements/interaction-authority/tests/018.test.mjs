import assert from 'node:assert/strict'
import { execFileSync } from 'node:child_process'
import { mkdtempSync, readdirSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
// 注意：closureKindForRoot 的 kind 参数须传 DU 单例（PromptRootAuthorityKind.AgentOwnerRoot 等）；裸字符串因缺 .tag 会错落分支。
import test from 'node:test'
import * as dispatch from '../../../dist/Interaction/Dispatch/DispatchSurface.js'
import * as authority from '../../../dist/Interaction/Authority/RuntimeSurface.js'
import * as lifeClassification from '../../../dist/Interaction/Authority/LifeClassification.js'
import * as origin from '../../../dist/Interaction/Authority/Origin.js'
import * as journal from '../../../dist/Persistence/Journal/Surface.js'
import * as lifecycle from '../../../dist/Mission/Lifecycle/Surface.js'
import { withJournal, withSharedJournal, acceptOwner, rootSelection as durableRootSelection } from './support/authority.mjs'

test('WHAT[interaction-authority-018] 闭合种类区分根身份与交付见证', () => {
  assert.equal(lifeClassification.rootKindOfKind('HumanRootManagerLife'), 'HumanRoot')
  assert.equal(lifeClassification.rootKindOfKind('AgentOwnerChildWork'), 'AgentOwnerRoot')
  assert.equal(lifeClassification.witnessKindOf('HumanRootManagerLife'), 'LifeCompleted')
  assert.equal(lifeClassification.witnessKindOf('AgentOwnerChildWork'), 'WorkCompleted')
  assert.equal(lifeClassification.expectedClosureKind('AgentOwnerRoot', false, undefined), undefined)
  assert.equal(lifeClassification.expectedClosureKind('AgentOwnerRoot', false, 'Attached'), 'AgentOwnerAttachedWork')
  assert.throws(() => lifeClassification.rootKindOfKind('Unknown'))
})

test('WHAT[interaction-authority-018] closureKindForRoot is total over the root-kind DU', () => {
  const K = origin.PromptRootAuthorityKind
  assert.deepEqual(lifeClassification.closureKindForRoot(K.HumanRoot, true, undefined), ['HumanRootManagerLife', undefined])
  assert.deepEqual(lifeClassification.closureKindForRoot(K.HumanRoot, false, undefined), ['HumanRootManagedRun', undefined])
  assert.deepEqual(lifeClassification.closureKindForRoot(K.AgentOwnerRoot, false, 'Attached'), ['AgentOwnerAttachedWork', 'Attached'])
  assert.deepEqual(lifeClassification.closureKindForRoot(K.AgentOwnerRoot, false, 'InternalLeaf'), ['AgentOwnerInternalLeaf', 'InternalLeaf'])
  assert.deepEqual(lifeClassification.closureKindForRoot(K.AgentOwnerRoot, false, 'Child'), ['AgentOwnerChildWork', 'Child'])
  // no proven life, and any unknown life label, fall to the documented coarse life
  assert.deepEqual(lifeClassification.closureKindForRoot(K.AgentOwnerRoot, false, undefined), ['AgentOwnerChildWork', 'Child'])
  assert.deepEqual(lifeClassification.closureKindForRoot(K.AgentOwnerRoot, false, 'bogus'), ['AgentOwnerChildWork', 'Child'])
});

const hash = (value) => `H(${value})`

const personas = {
  engineer: 'Engineer',
  coder: 'Coder',
  manager: 'Lead',
  reviewer: 'Auditor',
  inspector: 'Investigator',
  devops: 'Operator',
}

const rootSelection = (agent) => {
  const role = agent === 'predictor' ? 'inspector' : agent
  return {
    kind: 'RootSelection',
    ownerSession: null,
    ownerLogicalRun: null,
    ownerAuthorityRoot: null,
    participantIdentity: {
      participant: agent,
      role,
      selectedTier: 'deep',
      persona: personas[agent] ?? 'Unknown',
      personaCatalogVersion: 1,
      origin: 'ResolvedAtRoot',
    },
  }
}

const rootFor = (agent = 'engineer', physical = 'msg_u1') => {
  const result = authority.createAuthorityRoot(hash, 'rt_1', 'ses_a', 'HumanRoot', physical, rootSelection(agent))
  assert.equal(result.ok, true, result.error)
  return result.value
}

const profile = (value) => ({
  session: value.session,
  logicalRun: value.logicalRun,
  authorityRoot: value.authorityRoot,
  authorityKind: value.authorityKind,
  participant: value.participantIdentity.participant,
  role: value.participantIdentity.role,
})

const register = (root) => authority.registerAuthority(root, authority.empty)

const continuation = (key, root, kind = 'ManagerGuard', payload = 'payload') =>
  authority.claimContinuation(key, 'ses_a', kind, root, payload)

test('WHAT[interaction-authority-018] pure close transition clears exact run resources before root reuse', () => {
  const first = rootFor()
  let state = register(first)
  const claim = continuation('pk_1', first)
  state = authority.registerClaim(claim, state)
  state = authority.acceptClaim('pk_1', 'msg_c1', state)
  assert.equal(state.claimSequences.length, 1)
  assert.equal(state.acceptedContinuations.length, 1)

  const second = rootFor('manager', 'msg_u2')
  const premature = authority.registerAuthority(second, state)
  assert.equal(premature.ok, false)
  assert.equal(premature.error.kind, 'ActiveRunIdentityConflict')
  assert.equal(premature.error.active.logicalRun, first.logicalRun)
  assert.equal(premature.error.requested.logicalRun, second.logicalRun)

  const closed = authority.closeAuthority(first.logicalRun, first.authorityRoot, state)
  assert.equal(closed.ok, true, closed.ok ? '' : closed.error)
  const after = authority.registerAuthority(second, closed.value)
  assert.deepEqual(profile(after.activeLogicalRun), profile(second))
  assert.deepEqual(profile(after.lastAuthorityProfile), profile(second))
  assert.equal(after.pendingClaims.length, 0)
  assert.equal(after.claimSequences.length, 0)
  assert.equal(after.acceptedContinuations.length, 0)
})

test('WHAT[interaction-authority-018] exact-run close keeps an unlanded root claim and consumed scope while clearing run-scoped resources', () => {
  const session = 'ses_a'
  const root = rootFor('manager', 'msg_close_manager')
  let state = register(root)
  const seed = authority.issueInheritedIdentitySeed('engineer', root)
  assert.equal(seed.ok, true, seed.error)
  const rootOrigin = { kind: 'AuthorityRoot', label: 'AgentOwnerRoot' }
  const unknownKey = authority.derivePromptKey(hash, session, null, null, rootOrigin, 'unknown-root', 1)
  const unknown = authority.claimAgentOwnerRoot(unknownKey, session, 'unknown-root', seed.value)
  assert.equal(unknown.ok, true, unknown.error)
  state = authority.registerClaim(unknown.value, state)
  const unknownScope = authority.claimScopeDigest(session, null, rootOrigin, 'unknown-root')
  assert.equal(authority.nextClaimSequence(unknownScope, state), 2)

  const continuationOrigin = authority.originForContinuation('ManagerGuard')
  const pendingScope = authority.claimScopeDigest(session, root.logicalRun, continuationOrigin, 'pending')
  const acceptedScope = authority.claimScopeDigest(session, root.logicalRun, continuationOrigin, 'accepted')
  const pendingKey = authority.derivePromptKey(hash, session, root.logicalRun, root.authorityRoot, continuationOrigin, 'pending', 1)
  const acceptedKey = authority.derivePromptKey(hash, session, root.logicalRun, root.authorityRoot, continuationOrigin, 'accepted', 1)
  state = authority.registerClaim(authority.claimContinuation(pendingKey, session, 'ManagerGuard', root, 'pending'), state)
  state = authority.registerClaim(authority.claimContinuation(acceptedKey, session, 'ManagerGuard', root, 'accepted'), state)
  state = authority.acceptClaim(acceptedKey, 'msg_accepted_continuation', state)
  assert.equal(state.acceptedContinuations.length, 1)

  const closed = authority.closeAuthority(root.logicalRun, root.authorityRoot, state)
  assert.equal(closed.ok, true, closed.ok ? '' : closed.error)
  assert.equal(closed.value.activeLogicalRun, null)
  assert.deepEqual(closed.value.lastAuthorityProfile, root)
  assert.deepEqual(closed.value.pendingClaims, [unknown.value], 'an unlanded root claim crosses the exact-run close unchanged')
  assert.equal(authority.nextClaimSequence(unknownScope, closed.value), 2)
  assert.equal(authority.nextClaimSequence(pendingScope, closed.value), 1, 'run-scoped continuation sequences are discarded by the close')
  assert.equal(authority.nextClaimSequence(acceptedScope, closed.value), 1)
  assert.deepEqual(closed.value.acceptedContinuations, [])
  assert.deepEqual(closed.value.physicalLandings, state.physicalLandings)
  assert.deepEqual(closed.value.acceptedDispatches, state.acceptedDispatches)
  assert.equal(authority.resolveKnownOrigin('', unknownKey, false, closed.value), 'AuthorityRoot')

  const again = authority.closeAuthority(root.logicalRun, root.authorityRoot, closed.value)
  assert.equal(again.ok, true, again.ok ? '' : again.error)
  assert.deepEqual(again.value, closed.value, 'a second exact-run close is idempotent once LastAuthorityProfile matches')
})

const inventory = (root) => {
  const paths = []
  const visit = (dir) => {
    for (const entry of readdirSync(dir, { withFileTypes: true })) {
      const path = join(dir, entry.name)
      if (entry.isDirectory()) visit(path)
      else paths.push(path)
    }
  }
  visit(root)
  return paths.sort()
}

const eventJournalBytes = (workspace) => {
  const events = join(workspace, '.Herdchestra', 'wanxiangshu', 'events')
  return readdirSync(events)
    .filter((name) => name.endsWith('.ndjson'))
    .reduce((total, name) => total + statSync(join(events, name)).size, 0)
}

const surface = lifecycle.LifecycleSurface ?? lifecycle

const completeManagerReviews = (directory, session) => {
  const step = (requestId, updates) => {
    const receipt = surface.submitReview(directory, session, requestId, 'Manager', 'run-1', requestId, updates, [], '')
    assert.equal(receipt.status, 'Committed', receipt.reason)
    return receipt
  }
  const reviewed = step('review', [{ key: 'job', value: 'do' }])
  assert.equal(reviewed.kind, 'Reviewed', reviewed.reason)
  assert.equal(reviewed.milestone, '执行')
  const relayed = step('relay', [{ key: 'job', value: null }])
  assert.equal(relayed.kind, 'Relayed', relayed.reason)
  const advanced = step('advance', [])
  assert.equal(advanced.kind, 'Advanced', advanced.reason)
  assert.equal(advanced.milestone, '收尾')
  const completed = step('close', [])
  assert.equal(completed.kind, 'Completed', completed.reason)
  assert.equal(completed.milestone, '已完成')
  const view = surface.inspected(directory, session)
  assert.equal(view.Delivered, true)
  assert.equal(view.ActiveId ?? null, null)
  const retirement = Array.from(view.Retirements ?? []).at(-1)
  assert.equal(retirement.Outcome, 'Completed')
  assert.match(retirement.RetirementId, /\S/)
  return retirement.RetirementId
}

const expectClosureRefused = async (directory, session, retirementId) => {
  let refused = null
  try {
    await journal.JournalSurface_appendAuthorityLogicalRunClosed(directory, session, retirementId)
  } catch (error) {
    refused = error
  }
  assert.ok(refused, 'a missing shared journal entry must throw')
  assert.match(String(refused.message ?? refused.Message ?? refused), /authority closure refused/)
}

test('WHAT[interaction-authority-018] a missing shared journal entry refuses the closure append without creating a second writer', async () => {
  await withJournal('narrow-absent', async (_handle, _reopen, directory) => {
    const session = 'narrow-absent'
    const retirementId = completeManagerReviews(directory, session)
    // bootWithWriterId never registers a shared entry. work.sqlite is already
    // the work writer; the refused append must not create another one.
    const before = inventory(directory)
    await expectClosureRefused(directory, session, retirementId)
    assert.deepEqual(inventory(directory), before, 'the refusal must not create a second writer segment')
    await expectClosureRefused(directory, session, retirementId)
    assert.deepEqual(inventory(directory), before)
  })
})

test('WHAT[interaction-authority-018] a repeated closure of an already closed run is a successful no-op with zero durable append', async () => {
  const workspace = mkdtempSync(join(tmpdir(), 'wxs-narrow-idempotent-'))
  execFileSync('git', ['init', '--quiet', workspace])
  const acquired = await journal.JournalSurface_acquireSharedForWorkspace(workspace, process.pid, '2026-10-14T00:00:00Z')
  assert.equal(acquired.ok, true, JSON.stringify(acquired.error))
  const handle = acquired.journal
  try {
    const session = 'narrow-idempotent'
    const original = await acceptOwner(handle, session)
    const retirementId = completeManagerReviews(workspace, session)
    await journal.JournalSurface_appendAuthorityLogicalRunClosed(workspace, session, retirementId)

    const closed = journal.JournalSurface_snapshot(handle).sessionProjections[session].promptAuthority
    assert.equal(closed.activeLogicalRun, null, 'the completed retirement closure cleared the active run')
    assert.equal(closed.lastAuthorityProfile.logicalRun, original.logicalRun)

    const before = eventJournalBytes(workspace)
    await journal.JournalSurface_appendAuthorityLogicalRunClosed(workspace, session, retirementId)
    assert.equal(eventJournalBytes(workspace), before, 'an already closed run must not append a second closure')
    const after = journal.JournalSurface_snapshot(handle).sessionProjections[session].promptAuthority
    assert.equal(after.activeLogicalRun, null)
    assert.deepEqual(after.lastAuthorityProfile, closed.lastAuthorityProfile)
  } finally {
    journal.JournalSurface_dispose(handle)
    rmSync(workspace, { recursive: true, force: true })
  }
})

test('WHAT[interaction-authority-018] a refused closure append keeps the active run and blocks identity reuse across restart', async () => {
  await withJournal('narrow-refused', async (handle, reopen, directory) => {
    const session = 'narrow-refused'
    const original = await acceptOwner(handle, session)
    const retirementId = completeManagerReviews(directory, session)
    const before = inventory(directory)
    await expectClosureRefused(directory, session, retirementId)
    assert.deepEqual(inventory(directory), before, 'the refusal must not create a second writer segment')

    const after = journal.JournalSurface_snapshot(handle).sessionProjections[session].promptAuthority
    assert.notEqual(after.activeLogicalRun, null, 'a refused closure must not release the active run')
    assert.equal(after.activeLogicalRun.session, session)
    assert.equal(after.activeLogicalRun.logicalRun, original.logicalRun)
    assert.equal(after.activeLogicalRun.authorityRoot, original.authorityRoot)
    assert.equal(after.activeLogicalRun.authorityKind, 'HumanRoot')
    // Registering the root installs it as LastAuthorityProfile; a refused
    // closure must leave that archived profile exactly as accepted.
    assert.equal(after.lastAuthorityProfile.logicalRun, original.logicalRun, 'no closure success may be recorded')
    assert.equal(after.lastAuthorityProfile.authorityRoot, original.authorityRoot)

    const fresh = await dispatch.acceptHumanRootSelection(handle, session, 'fresh-' + session, durableRootSelection('manager'))
    assert.equal(fresh.ok, false, 'a fresh identity must be refused while the prior run is not closed')

    const cold = await reopen()
    const coldView = journal.JournalSurface_snapshot(cold).sessionProjections[session].promptAuthority
    assert.equal(coldView.activeLogicalRun.logicalRun, original.logicalRun)
    assert.equal(coldView.activeLogicalRun.authorityRoot, original.authorityRoot)
    const coldFresh = await dispatch.acceptHumanRootSelection(cold, session, 'cold-fresh-' + session, durableRootSelection('manager'))
    assert.equal(coldFresh.ok, false)
  })
})


// CUT-09-b: the closure of a HumanRoot Manager life carries the canonical
// LifeCompleted(life, retirement) witness, and durable replay admits a closure
// only with the exact source witness of the active root's expected kind.
const closedManagerLife = async (handle, directory, session) => {
  const owner = await acceptOwner(handle, session)
  const retirementId = completeManagerReviews(directory, session)
  await journal.JournalSurface_appendAuthorityLogicalRunClosed(directory, session, retirementId)
  return owner
}

const rewriteClosureWitness = (directory, rewrite) => {
  const events = join(directory, '.Herdchestra', 'wanxiangshu', 'events')
  let rewritten = 0
  for (const name of readdirSync(events).filter((file) => file.endsWith('.ndjson'))) {
    const path = join(events, name)
    const lines = readFileSync(path, 'utf8').split('\n').map((line) => {
      if (!line.includes('"AuthorityLogicalRunClosed"')) return line
      const event = JSON.parse(line)
      const payload = event.payload.Fact[1][1][1]
      payload.ClosureWitness = rewrite(payload)
      rewritten += 1
      return JSON.stringify(event)
    })
    writeFileSync(path, lines.join('\n'))
  }
  assert.equal(rewritten, 1, 'exactly one durable closure was written')
}

test('WHAT[interaction-authority-018] a completed Manager retirement closes the life with the canonical LifeCompleted witness', async () => {
  await withSharedJournal('witness-canonical', async (handle, reopen, directory) => {
    const session = 'witness-canonical'
    const owner = await closedManagerLife(handle, directory, session)
    let witness
    rewriteClosureWitness(directory, (payload) => {
      assert.equal(payload.RootKind, 'HumanRoot')
      assert.equal(payload.LogicalRunId[1], owner.logicalRun)
      witness = payload.ClosureWitness
      return witness
    })
    const [kind, life, fact] = JSON.parse(witness)
    assert.deepEqual([kind, life], ['LifeCompleted', session], 'the Manager life is the session whose completed retirement closed')
    assert.match(fact, /\S/, 'the witness names the exact delivered retirement')
    const cold = await reopen()
    assert.equal(journal.JournalSurface_snapshot(cold).sessionProjections[session].promptAuthority.activeLogicalRun, null)
  })
})

for (const [label, rewrite] of [
  ['another closure kind', (payload) => JSON.stringify(['ManagedLogicalRunTerminal', payload.LogicalRunId[1], JSON.parse(payload.ClosureWitness)[2]])],
  ['another Manager life', (payload) => JSON.stringify(['LifeCompleted', 'another-session', JSON.parse(payload.ClosureWitness)[2]])],
  ['a malformed witness', () => '["LifeCompleted"'],
]) {
  test(`WHAT[interaction-authority-018] a durable closure carrying ${label} does not release the active run on replay`, async () => {
    await withSharedJournal('witness-foreign', async (handle, reopen, directory) => {
      const session = 'witness-foreign'
      const owner = await closedManagerLife(handle, directory, session)
      rewriteClosureWitness(directory, rewrite)
      const cold = await reopen()
      const active = journal.JournalSurface_snapshot(cold).sessionProjections[session].promptAuthority.activeLogicalRun
      assert.notEqual(active, null, 'only the exact source witness may close the run')
      assert.equal(active.logicalRun, owner.logicalRun)
    })
  })
}

test('WHAT[interaction-authority-018] a pre-CUT-09-b closure with a bare retirement id still closes the Manager life it was written for', async () => {
  await withSharedJournal('witness-legacy', async (handle, reopen, directory) => {
    const session = 'witness-legacy'
    await closedManagerLife(handle, directory, session)
    rewriteClosureWitness(directory, (payload) => JSON.parse(payload.ClosureWitness)[2])
    const cold = await reopen()
    assert.equal(journal.JournalSurface_snapshot(cold).sessionProjections[session].promptAuthority.activeLogicalRun, null)
  })
})

// GAP-123: the five accepted lifecycle kinds each carry their ExpectedClosureKind
// and their exact source witness into one durable AuthorityLogicalRunClosed.
// The lifecycle owner writes the durable source terminal; the closure writer
// only cites a source that is already durable, and only the unique one.

// The lifecycle owner builds the durable source terminal; the fact id is
// derived from the identity of the lifecycle and the outcome, so the same
// outcome recorded twice names the same source.
const terminalSource = (kind, session, logicalRun, authorityRoot, outcome, extra = {}) => {
  const factId = hash(`authority-terminal:${session}:${logicalRun}:${authorityRoot}:${outcome}`)
  switch (kind) {
    case 'HumanRootManagedRun':
      return {
        family: 'Prompt',
        case: 'AuthorityManagedRunTerminal',
        payload: {
          SessionId: session,
          LogicalRunId: logicalRun,
          AuthorityRootUserMessageId: authorityRoot,
          FactId: factId,
        },
      }
    case 'AgentOwnerChildWork':
      return {
        family: 'Prompt',
        case: 'AuthorityChildWorkTerminal',
        payload: {
          SessionId: session,
          OwnerLogicalRunId: extra.ownerLogicalRunId ?? '',
          ChildLogicalRunId: logicalRun,
          AuthorityRootUserMessageId: authorityRoot,
          FactId: factId,
        },
      }
    case 'AgentOwnerAttachedWork':
      return {
        family: 'Prompt',
        case: 'AuthorityAttachedWorkTerminal',
        payload: {
          SessionId: session,
          OwnerLogicalRunId: extra.ownerLogicalRunId ?? '',
          ChildLogicalRunId: logicalRun,
          AuthorityRootUserMessageId: authorityRoot,
          AttachmentKind: extra.attachmentKind ?? '',
          AssociationGeneration: extra.associationGeneration ?? '',
          FactId: factId,
        },
      }
    default:
      throw new Error(`unknown terminal kind: ${kind}`)
  }
}

const authorityOf = (handle, session) =>
  journal.JournalSurface_snapshot(handle).sessionProjections[session].promptAuthority

test('WHAT[interaction-authority-018] GAP-123 an accepted root records its expected closure kind and a durable source terminal becomes the exact closure witness', async () => {
  await withSharedJournal('managed-closure', async (handle, reopen, directory) => {
    const session = 'managed-closure'

    // A non-Manager HumanRoot is a managed run: its expected closure kind is
    // HumanRootManagedRun, recorded atomically with the acceptance.
    const accepted = await dispatch.acceptHumanRootSelection(
      handle,
      session,
      'root-managed',
      durableRootSelection('engineer'),
    )
    assert.equal(accepted.ok, true, JSON.stringify(accepted.error))

    const contract = authorityOf(handle, session).acceptedRootClosures
    assert.equal(contract.length, 1, 'the accepted root records exactly one closure contract')
    assert.equal(contract[0].expectedClosureKind, 'HumanRootManagedRun')
    assert.equal(contract[0].rootKind, 'HumanRoot')

    // The lifecycle owner writes the durable source terminal.
    const source = terminalSource(
      'HumanRootManagedRun',
      session,
      accepted.profile.logicalRun,
      accepted.profile.authorityRoot,
      'completed',
    )
    const appended = await journal.JournalSurface_appendAgent(handle, { kind: 'Session', session }, null, source)
    assert.equal(appended.ok, true, JSON.stringify(appended.error))

    const withSource = authorityOf(handle, session)
    assert.equal(withSource.terminalSources.length, 1)
    assert.equal(withSource.terminalSources[0].sources.length, 1)
    assert.equal(withSource.terminalSources[0].sources[0].sourceKind, 'HumanRootManagedRun')
    const sourceFactId = withSource.terminalSources[0].sources[0].factId
    assert.match(sourceFactId, /\S/, 'the source terminal names its durable fact')

    // The closure writer cites the durable source and writes exactly one
    // closure for this run.
    await journal.JournalSurface_appendAuthorityLogicalRunClosed(directory, session, 'retirement-managed')

    const closed = authorityOf(handle, session)
    assert.equal(closed.activeLogicalRun, null, 'the durable closure releases the active run')
    assert.equal(closed.closedRuns.length, 1)
    assert.equal(closed.closedRuns[0].rootKind, 'HumanRoot')
    const [kind, run, fact] = JSON.parse(closed.closedRuns[0].closureWitness)
    assert.equal(kind, 'ManagedLogicalRunTerminal')
    assert.equal(run, accepted.profile.logicalRun)
    assert.equal(fact, sourceFactId, 'the witness names the exact durable source terminal')
  })
})

test('WHAT[interaction-authority-018] GAP-123 a closure without a durable source terminal is refused and the active run keeps its identity', async () => {
  await withSharedJournal('managed-no-source', async (handle, reopen, directory) => {
    const session = 'managed-no-source'
    const accepted = await dispatch.acceptHumanRootSelection(
      handle,
      session,
      'root-no-source',
      durableRootSelection('engineer'),
    )
    assert.equal(accepted.ok, true, JSON.stringify(accepted.error))

    // No durable source terminal exists, so no closure may be written.
    await journal.JournalSurface_appendAuthorityLogicalRunClosed(directory, session, 'retirement-no-source')

    const active = authorityOf(handle, session)
    assert.notEqual(active.activeLogicalRun, null, 'a closure without a durable source releases nothing')
    assert.equal(active.activeLogicalRun.logicalRun, accepted.profile.logicalRun)
    assert.equal(active.closedRuns.length, 0)
  })
})

test('WHAT[interaction-authority-018] GAP-123 a repeated closure append is idempotent and a conflicting closure is refused across restart', async () => {
  await withSharedJournal('managed-conflict', async (handle, reopen, directory) => {
    const session = 'managed-conflict'
    const accepted = await dispatch.acceptHumanRootSelection(
      handle,
      session,
      'root-conflict',
      durableRootSelection('engineer'),
    )
    assert.equal(accepted.ok, true, JSON.stringify(accepted.error))

    const appended = await journal.JournalSurface_appendAgent(
      handle,
      { kind: 'Session', session },
      null,
      terminalSource(
        'HumanRootManagedRun',
        session,
        accepted.profile.logicalRun,
        accepted.profile.authorityRoot,
        'completed',
      ),
    )
    assert.equal(appended.ok, true, JSON.stringify(appended.error))

    await journal.JournalSurface_appendAuthorityLogicalRunClosed(directory, session, 'retirement-conflict-1')
    const closed = authorityOf(handle, session)
    assert.equal(closed.activeLogicalRun, null)
    assert.equal(closed.closedRuns.length, 1)

    // The same closure again is a no-op, even across a restart.
    const cold = await reopen()
    await journal.JournalSurface_appendAuthorityLogicalRunClosed(directory, session, 'retirement-conflict')
    const afterRepeat = authorityOf(cold, session)
    assert.equal(afterRepeat.closedRuns.length, 1, 'a repeated closure appends no second closure')
    assert.equal(afterRepeat.activeLogicalRun, null)

    // A fresh identity is refused while the prior run's closure is recorded
    // but a conflicting closure must never look like a duplicate.
    const conflicting = {
      family: 'Prompt',
      case: 'AuthorityLogicalRunClosed',
      payload: {
        SessionId: session,
        LogicalRunId: accepted.profile.logicalRun,
        AuthorityRootUserMessageId: accepted.profile.authorityRoot,
        RootKind: 'HumanRoot',
        ClosureWitness: JSON.stringify(['ManagedLogicalRunTerminal', accepted.profile.logicalRun, 'terminal-conflict-other']),
      },
    }
    const previousFatalExit = process.env.WANXIANGSHU_NO_FATAL_EXIT
    let rejected
    try {
      process.env.WANXIANGSHU_NO_FATAL_EXIT = '1'
      rejected = await journal.JournalSurface_appendAgent(
        cold,
        { kind: 'Session', session },
        null,
        conflicting,
      )
    } finally {
      if (previousFatalExit === undefined) delete process.env.WANXIANGSHU_NO_FATAL_EXIT
      else process.env.WANXIANGSHU_NO_FATAL_EXIT = previousFatalExit
    }
    assert.equal(rejected.ok, false, 'a conflicting closure is refused')
    const afterConflict = authorityOf(cold, session)
    assert.equal(afterConflict.activeLogicalRun, null, 'the refused closure changed nothing')
    assert.equal(afterConflict.closedRuns.length, 1, 'the recorded closure is still the only one')
    assert.equal(afterConflict.closedRuns[0].closureWitness, closed.closedRuns[0].closureWitness)
  })
})

// The recorded closure kind and the recorded authority kind are two fields of
// one acceptance. The journal refuses an acceptance whose closure kind belongs
// to another root kind (a semantic cut, fatal to the process). The boundary
// must therefore have exactly one derivation owner and never forward a
// mismatched pair. This property covers the whole input space of the law.
test('WHAT[interaction-authority-018] every acceptance closure kind belongs to its authority kind', async () => {
  const fc = (await import('fast-check')).default

  const rootKinds = ['HumanRoot', 'AgentOwnerRoot']
  const lives = ['Child', 'Attached', 'InternalLeaf']
  const closureKinds = [
    'HumanRootManagerLife',
    'HumanRootManagedRun',
    'AgentOwnerChildWork',
    'AgentOwnerAttachedWork',
    'AgentOwnerInternalLeaf',
  ]

  fc.assert(
    fc.property(
      fc.constantFrom(...rootKinds),
      fc.boolean(),
      fc.option(fc.constantFrom(...lives), { nil: null }),
      fc.option(fc.constantFrom(...closureKinds, 'bogus'), { nil: null }),
      (rootKind, isManager, life, recorded) => {
        const chosen = authority.recordedClosureKind(rootKind, isManager, life, recorded)

        assert.ok(closureKinds.includes(chosen), `unknown closure kind ${chosen}`)
        assert.equal(authority.rootKindOfKind(chosen), rootKind, `${rootKind}/${life}/${recorded} produced ${chosen}`)
        assert.ok(authority.witnessKindOf(chosen), `closure kind ${chosen} has no witness kind`)

        if (rootKind === 'AgentOwnerRoot') {
          assert.notEqual(
            authority.rootKindOfKind(chosen),
            'HumanRoot',
            'an agent-owner root must never close under a HumanRoot kind',
          )
        }
      },
    ),
    { seed: 18018, numRuns: 400 },
  )

  // The exact fatal input: an agent-owner root with no proven life used to
  // record HumanRootManagedRun. It now records the coarse agent-owner life.
  const normalized = authority.recordedClosureKind('AgentOwnerRoot', false, null, 'HumanRootManagedRun')
  assert.equal(normalized, 'AgentOwnerChildWork')
  assert.equal(authority.rootKindOfKind(normalized), 'AgentOwnerRoot')
})
