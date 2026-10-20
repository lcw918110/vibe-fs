export const RESULT_NAMES = ['Relayed', 'Delivered']
export const REVIEW_OUTCOMES = ['Promoted', 'Stayed']
export const EVENTS = [
  'WorkOpened',
  'DevOpsBound',
  'IncumbencyOpened',
  'ConfirmationCommitted',
  'IncumbencyRetired',
  'Reviewed',
  'Reopened',
  'AdvanceCommitted',
  'RetirementBlocked',
]
export const END_TOOL = 'deliver'

export function confirmationCount(sequence) {
  if (sequence.join(',') === 'S1,S2,S3') return 1
  if (sequence.join(',') === 'S4') return 2
  return null
}

export function openWork(input) {
  const sequence = input.sequence
  const count = confirmationCount(sequence)
  if (count == null) return { ok: false, written: [] }
  if (!input.observedHead) return { ok: false, written: [] }
  if ('snapshotId' in input || 'certificate' in input) return { ok: false, written: [] }
  const opened = {
    type: 'WorkOpened',
    workId: input.workId,
    root: input.root,
    sequence,
    authorityInitial: `authority-initial-v1:${input.workId}`,
  }
  const incumbency = {
    type: 'IncumbencyOpened',
    workId: input.workId,
    incumbencyId: input.incumbencyId,
    projection: sequence[0],
    openingCursor: input.observedHead,
  }
  return { ok: true, written: [opened, incumbency], confirmationCount: count }
}

export function review(view, outcome) {
  if (!view.active || view.reviewed) return { ok: false, written: [] }
  if (!REVIEW_OUTCOMES.includes(outcome)) return { ok: false, written: [] }
  const sequence = view.sequence
  const index = sequence.indexOf(view.projection)
  const hasNext = index >= 0 && index < sequence.length - 1
  let projection = view.projection
  let deliveryAuthorized = false
  if (outcome === 'Promoted' && hasNext) projection = sequence[index + 1]
  if (outcome === 'Promoted' && !hasNext && confirmationCount(sequence) === 1) deliveryAuthorized = true
  return {
    ok: true,
    written: [{ type: 'Reviewed', incumbencyId: view.incumbencyId, stage: view.projection, outcome }],
    projection,
    deliveryAuthorized,
  }
}

export function admitAction(view, action) {
  if (!view.active) return { admitted: false }
  if (view.reviewed) return { admitted: true }
  if (action === 'read' || action === 'observe' || action === 'review') return { admitted: true }
  if (action === 'write-source' || action === 'dispatch-devops') return { admitted: false }
  if (action === 'deliver' && confirmationCount(view.sequence) === 1 && !view.deliveryAuthorized) return { admitted: true }
  return { admitted: true }
}

export function deliverOnce(view) {
  if (confirmationCount(view.sequence) !== 1) return { ok: false, reason: 'wrong-sequence' }
  if (view.blockers.length > 0) {
    return { ok: true, finished: false, written: [{ type: 'RetirementBlocked', incumbencyId: view.incumbencyId }] }
  }
  if (view.deliveryAuthorized) {
    if (!view.execSessionId || !view.digest || !view.path) return { ok: false, written: [] }
    return {
      ok: true,
      finished: true,
      outcome: 'Delivered',
      written: [{ type: 'IncumbencyRetired', outcome: 'Delivered', retirementCursor: view.head }],
    }
  }
  return {
    ok: true,
    finished: true,
    outcome: 'Relayed',
    written: [
      { type: 'IncumbencyRetired', outcome: 'Relayed', retirementCursor: view.head },
      { type: 'IncumbencyOpened', openingCursor: view.head, projection: view.projection },
    ],
  }
}

export function deliverTwice(view, call) {
  if (confirmationCount(view.sequence) !== 2) return { ok: false, reason: 'wrong-sequence' }
  if (!view.reviewed && view.confirmed) return { ok: false, written: [], finished: false }
  if (!view.reviewed) return { ok: true, finished: false, assessmentRequired: true, written: [] }
  if (!view.confirmed) {
    return {
      ok: true,
      finished: false,
      written: [{ type: 'ConfirmationCommitted', toolCallId: call.toolCallId, providerRunId: call.providerRunId }],
    }
  }
  if (call.toolCallId === view.confirmedToolCallId && call.providerRunId === view.confirmedProviderRunId) {
    return { ok: true, finished: false, replay: true, written: [] }
  }
  if (call.toolCallId === view.confirmedToolCallId) return { ok: false, reason: 'RetirementConfirmationReplayConflict', written: [] }
  const outcome = view.reviewOutcome === 'Promoted' ? 'Delivered' : 'Relayed'
  const retired = { type: 'IncumbencyRetired', outcome, projectionCut: { providerRunId: call.providerRunId, toolCallId: call.toolCallId } }
  if ('retirementCursor' in retired || 'digest' in retired || 'snapshotId' in retired) return { ok: false, written: [] }
  if (outcome === 'Relayed') {
    return {
      ok: true,
      finished: true,
      outcome,
      written: [retired, { type: 'IncumbencyOpened', openingCursor: call.head }],
    }
  }
  return { ok: true, finished: true, outcome, written: [retired] }
}

export function projectSuccessor(messages, openingCursor, first) {
  // Relay no longer strips predecessor context. The projection keeps every
  // physical row; only view declarations may remove history.
  const projected = messages.filter((message) => !message.synthetic)
  if (projected.some((message) => message.role === 'synthetic_predecessor' || String(message.id ?? '').startsWith('synthetic-prev-'))) {
    return { ok: false }
  }
  return { ok: true, projected, first: Boolean(first), retainedPriorAssistant: projected.some((message) => message.role === 'assistant' && message.firstSeen <= openingCursor) }
}

export function admitOldBytes(bytes) {
  return { admitted: false, reason: 'old-bytes-are-not-input', bytes }
}

export function bindDevOps(input) {
  if (input.bound) return { ok: true, written: [] }
  return { ok: true, written: [{ type: 'DevOpsBound', workId: input.workId, devopsId: input.devopsId }] }
}

export function reviewFromFindings(findings) {
  if (!Array.isArray(findings)) return { ok: false, written: [] }
  if (findings.some((pair) => !Array.isArray(pair) || pair.length !== 2 || pair.some((item) => typeof item !== 'string' || item.length === 0))) {
    return { ok: false, written: [] }
  }
  return { ok: true, outcome: findings.length === 0 ? 'Promoted' : 'Stayed' }
}

export function relayHandover(relayed, owned) {
  if (relayed.outcome !== 'Relayed') return { ok: false, orphaned: false, kept: [], written: [] }
  return { ok: true, orphaned: false, kept: owned.received, written: relayed.written }
}
