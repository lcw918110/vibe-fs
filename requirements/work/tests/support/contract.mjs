export const RESULT_NAMES = [
  'Asked',
  'Reviewed',
  'Relayed',
  'Advanced',
  'Launched',
  'Completed',
  'Reopened',
  'Blocked',
  'Ignored',
]
export const REVIEW_OUTCOMES = RESULT_NAMES
export const EVENTS = [
  'WorkOpened',
  'IncumbencyOpened',
  'QuestionRequested',
  'OperatorAnswered',
  'Reviewed',
  'IncumbencyRetired',
  'ContextBarrier',
  'PromptObliged',
  'MilestoneAdvanced',
  'DeliveryRequested',
  'DeliverySessionBound',
  'DeliveryAdmitted',
  'DeliveryRejected',
  'EffectConfirmed',
  'StopObliged',
  'DevOpsBound',
]
export const END_TOOL = 'review'

export function admitOldBytes(bytes) {
  return { admitted: false, reason: 'old-bytes-are-not-input', bytes }
}

export function bindDevOps(input) {
  if (input.bound) return { ok: true, written: [] }
  return { ok: true, written: [{ type: 'DevOpsBound', workId: input.workId, devopsId: input.devopsId }] }
}

export function relayHandover(relayed, owned) {
  const outcome = relayed.outcome ?? relayed.kind
  if (outcome !== 'Relayed') return { ok: false, orphaned: false, kept: [], written: [] }
  const written = Array.isArray(relayed.written) ? relayed.written : []
  return { ok: true, orphaned: false, kept: owned.received, written }
}
