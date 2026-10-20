/**
 * cold-boundary.js — explicit declared exceptions to the prefix seal.
 *
 * ARCH-004 keeps the provider-visible prefix byte-stable so KV-cache hits. verification-system-001
 * §"冷边界显式声明" names an explicit finite set of legitimate exceptions and requires
 * the scenario to say WHERE each happens:
 *
 *   COMPANION-009  epoch switch — new SealRoot, one explicit prefix rebase
 *   PROVIDER-RETRY model rotation — fixed Role routes to a new model target, so the banner does
 *   SyncDelegate   request-kind switch — per-request tool map swap, same transcript
 *   MANAGER-LOOP   manager tenure — same system/plan, root authority kept while
 *                  retired-tenure traffic and the wake are removed
 *
 * Sniffing is forbidden, and package K1 measured why. The deleted `epochCold`
 * exemption read "tools and the leading system message unchanged" and then admitted
 * ANY body rewrite — which is most of what a wrong prefix replacement looks like. It
 * passed exactly the mutations it existed to catch.
 *
 * ── the provider-rotation boundary is narrower than the old exemption claimed ─
 *
 * `modelSideCold` allowed arbitrary system rewrites whenever the model id changed.
 * OpenCode 1.18.29 injects one exact model banner into the system message, so a real
 * provider retry changes that banner by construction. The seal normalizes only
 * that Host-owned sentence; every other system byte and every conversation part
 * remains protected.
 *
 * That makes the provider-rotation declaration a far tighter admission than a prefix rebase: messages
 * must still satisfy the ordinary seal, and only the model may move. A scenario that
 * declares it cannot use it to smuggle a message rewrite past the barrier.
 */

import { isDeepStrictEqual } from 'node:util';
import { isAppendOnlyPrefix, wireOf } from './provider-wire.js';
import { hostDotAckText } from './runtime-key.js';

export const BOUNDARY_KINDS = [
  'epoch-switch',
  'fallback-side',
  'request-kind-switch',
  'manager-loop',
];

// ── declaration lookup ──────────────────────────────────────────────────────

/**
 * Which boundary is declared AT this ENTRY, or `null`.
 *
 * A declaration names the point the break is expected at, so it is consumed by the
 * request that breaks the seal — not by the one before it.
 *
 * Keyed by entry id for the reason `faultFor` documents: this compared the DECLARED turn
 * text against the REQUEST text, and a declaration is a prefix, so the two matched only
 * when the author wrote the utterance out in full. Every cold boundary in every real
 * scenario was inert. `resolveEntry` has already chosen the declaration; asking again
 * with a weaker comparison could only disagree with it.
 */
export function boundaryFor(boundaries, entry) {
  if (entry === null || entry === undefined) return null;

  const matches = (boundaries ?? []).filter((boundary) => boundary.entryId === entry.id);

  if (matches.length === 0) return null;
  if (matches.length > 1) {
    throw new Error(
      `two cold boundaries declared for the same step '${entry.id}': ${matches.map((b) => b.kind).join(', ')}`,
    );
  }
  return matches[0];
}

// ── the seal decision ───────────────────────────────────────────────────────

/**
 * Whether the messages alone are an append-only continuation, ignoring the model.
 *
 * `isAppendOnlyPrefix` compares model fields too, which is right for the ordinary
 * seal — a model change is a real cache break. `FALLBACK-004` is the one case where
 * that break is expected while the transcript must still be intact, so the two
 * questions have to be separable.
 */
const messagesStillAppendOnly = (previousWire, nextWire) => {
  const normalizedNext = withoutHostModelBanner(nextWire);
  return isAppendOnlyPrefix(withModelOf(withoutHostModelBanner(previousWire), normalizedNext), normalizedNext);
};

/** SyncDelegate Returned→Completion keeps model/system/messages and replaces only tools. */
const requestKindKeepsPrefix = (previousWire, nextWire) =>
  isAppendOnlyPrefix({ ...previousWire, tools: nextWire.tools }, nextWire);

/** `previous` with `next`'s model fields, so only the messages/tools/system differ. */
const withModelOf = (previousWire, nextWire) => ({
  ...previousWire,
  providerId: nextWire.providerId,
  modelId: nextWire.modelId,
  variant: nextWire.variant,
});

const MODEL_BANNER = /You are powered by the model named [^\n]*?\. The exact model ID is [^\n]+/g;

const withoutHostModelBanner = (wire) => ({
  ...wire,
  messages: (wire.messages ?? []).map((message) => ({
    ...message,
    parts: (message.parts ?? []).map((part) =>
      part?.kind === 'text' && typeof part.text === 'string'
        ? { ...part, text: part.text.replace(MODEL_BANNER, '<host-model>') }
        : part),
  })),
});

// ── pure manager loop ───────────────────────────────────────────────────────
//
// Every manager tenure keeps the same workspace, tools and authority plan: a
// Relayed retirement is followed by another ordinary tenure. The successor does
// NOT keep the predecessor's physical history — work-008 makes stripping the
// predecessor's assistant/tool traffic the obligation (`RelayNarrativeTransform.
// projectActive` keeps only interactions whose first XTrace observation is at or after
// the active tenure's opening, and always keeps user input; the retired attempt's own
// continuation is stopped outright). A preceding provider fallback may change only
// OpenCode's exact model banner.
//
// So the claims that survive at this layer are the ones the projection may not move:
//
//   tools       the provider plan is fixed
//   system      the normalized system prompt is fixed
//   users       every previous user message is still present, in order and byte-equal
//               (the authority root is never rewritten and user input is never dropped)
//
// Dropping assistant/tool traffic is the projection's own admitted behavior and is
// therefore NOT a break; a rewritten or dropped user, a changed tool set or system
// prompt is, and stays fail-closed. What a rewritten-but-retained interaction would look
// like is indistinguishable from a dropped one plus a new one, so that claim is not
// asserted here — the long-stroke oracle reads the successor's own structure instead.

const sameProviderPlan = (previousWire, nextWire) => isDeepStrictEqual(previousWire.tools, nextWire.tools);

// The Host appends each guidance occurrence — a `<system>` block or an
// Enforcer tip — as a `\0\uFEFF`-separated suffix to the request's terminal part (a user
// message or the last completed tool result; `cursorGuidanceSeparator` in
// PairProgrammingThoughtTransform.fs). A successor tenure retires such an occurrence
// without rewriting the message it was attached to.
const GUIDANCE_MARKER = '\0\uFEFF';

const withoutGuidanceValue = (value) => {
  if (typeof value !== 'string') return value;
  const markerAt = value.indexOf(GUIDANCE_MARKER);
  return markerAt < 0 ? value : value.slice(0, markerAt);
};

const withoutGuidancePart = (part) => {
  const text = withoutGuidanceValue(part?.text);
  const result = withoutGuidanceValue(part?.result);
  if (text === part?.text && result === part?.result) return part;
  return { ...part, ...(text === part?.text ? {} : { text }), ...(result === part?.result ? {} : { result }) };
};

// System messages are never stripped: their content is compared separately.
const withoutGuidanceMessage = (message) =>
  message?.role === 'system' ? message : { ...message, parts: (message.parts ?? []).map(withoutGuidancePart) };

/**
 * A retired owner-facing readonly-replay block: one projected companion exchange —
 * `assistant(tool-call)` closed by its own `tool(result)`, followed by the Host's dot ack.
 * Production re-renders or retires that replay when the horizon advances, so the block may
 * disappear between two deliveries of the same entry (the Manager's reusable nudge ladder
 * does exactly that). Structural only: the call must be closed by the result that names
 * its own id, and the ack must be the production dot text.
 */
const isReadonlyReplayBlock = (call, result, ack) =>
  call?.role === 'assistant'
  && call.parts?.length === 1
  && call.parts[0]?.kind === 'tool-call'
  && result?.role === 'tool'
  && result.parts?.length === 1
  && result.parts[0]?.kind === 'tool-result'
  && result.parts[0].callId === call.parts[0].callId
  && ack?.role === 'assistant'
  && ack.parts?.length === 1
  && ack.parts[0]?.kind === 'text'
  && hostDotAckText !== null
  && ack.parts[0].text === hostDotAckText;

const withoutReadonlyReplayBlocks = (messages) => {
  const kept = [];
  for (let index = 0; index < messages.length; index += 1) {
    if (isReadonlyReplayBlock(messages[index], messages[index + 1], messages[index + 2])) {
      index += 2;
      continue;
    }
    kept.push(messages[index]);
  }
  return kept;
};

const withoutGuidanceOccurrences = (wire) => ({
  ...wire,
  messages: withoutReadonlyReplayBlocks(
    (wire.messages ?? []).map(withoutGuidanceMessage),
  ),
});

const managerLoopKeepsAuthority = (previousWire, nextWire) => {
  if (!sameProviderPlan(previousWire, nextWire)) return false;
  const systemOf = (wire) => (wire.messages ?? []).filter((message) => message?.role === 'system');
  const normalizedPrevious = withoutHostModelBanner(previousWire);
  const normalizedNext = withoutHostModelBanner(nextWire);
  if (!isDeepStrictEqual(systemOf(normalizedPrevious), systemOf(normalizedNext))) return false;

  // Guidance occurrences are already retired by `sealDecision`.
  const previousMessages = normalizedPrevious.messages ?? [];
  const nextMessages = normalizedNext.messages ?? [];
  if (previousMessages.length === 0 || nextMessages.length === 0) return false;

  const usersOf = (messages) => messages.filter((message) => message?.role === 'user');
  const previousUsers = usersOf(previousMessages);
  const nextUsers = usersOf(nextMessages);
  if (previousUsers.length === 0) return false;

  // The authority root is the handover's fixed point: the first user the successor
  // sends is the predecessor's first user, unchanged.
  if (!isDeepStrictEqual(nextUsers[0], previousUsers[0])) return false;

  // User input stays: every previous user is still there, in order and unchanged.
  let cursor = 0;
  for (const previousUser of previousUsers) {
    const found = nextUsers.findIndex(
      (candidate, index) => index >= cursor && isDeepStrictEqual(candidate, previousUser),
    );
    if (found < 0) return false;
    cursor = found + 1;
  }

  return true;
};

/**
 * Decide one chat request against the session's seal.
 *
 * Four outcomes, and the caller must treat them as four:
 *
 *   { held: true }                      the ordinary case, seal intact
 *   { resealed: kind }                  a declared boundary consumed the break
 *   { broken: 'undeclared' }            fail closed — ARCH-004 with no declaration
 *   { broken: 'boundary-not-reached' }  declared, but the seal did NOT break
 *
 * The fourth exists because a declaration that never fires is worse than a missing
 * one: the author believes a cold boundary is covered, and the scenario silently
 * stopped exercising it. Same reasoning as an empty `attempts` list in a fault.
 */
export function sealDecision({ previousWire, body, boundary, stripAdmitted = false }) {
  if (previousWire === null || previousWire === undefined) {
    // `manager-loop` and `epoch-switch` share the multi-delivery
    // shape: the first request establishes the seal (nothing to break), later ones
    // break it. A `manager-loop` boundary sits on a reusable step-0 entry, so its
    // initial delivery has no previous wire and must be held.
    if (boundary !== null && boundary !== undefined
        && (boundary.kind === 'manager-loop' || boundary.kind === 'epoch-switch')) {
      return { held: true };
    }
    return boundary === null || boundary === undefined
      ? { held: true }
      : { broken: 'boundary-not-reached', kind: boundary.kind };
  }

  // Both sides of every comparison below are the occurrence-normalized wires: retiring a
  // guidance occurrence is intended plugin drift (see `withoutGuidanceOccurrences`), while
  // every other byte of the prefix still has to match.
  const previous = withoutGuidanceOccurrences(previousWire);
  const nextWire = withoutGuidanceOccurrences(wireOf(body));
  const held = isAppendOnlyPrefix(previous, nextWire);

  if (boundary === null || boundary === undefined) {
    if (held) return { held: true };
    // work-008: when the session crossed a tenure boundary between the two deliveries,
    // production's successor projection legitimately strips the predecessor's
    // assistant/tool traffic (it keeps user input and whatever the new tenure observed).
    // The caller establishes that boundary from the durable journal, so this admission is
    // never taken from the wire alone — and even then the strip has to satisfy the same
    // structural claims the `manager-loop` boundary makes.
    return stripAdmitted && managerLoopKeepsAuthority(withoutGuidanceOccurrences(previousWire), nextWire)
      ? { resealed: 'successor-strip' }
      : { broken: 'undeclared' };
  }

  if (held) {
    return boundary.kind === 'manager-loop' || boundary.kind === 'epoch-switch'
      ? { held: true }
      : { broken: 'boundary-not-reached', kind: boundary.kind };
  }

  switch (boundary.kind) {
    // COMPANION-009: the prefix is deliberately rebased, so no message-level claim
    // survives. The declaration is the whole authority, which is why it has to name a
    // single point rather than a range.
    case 'epoch-switch':
      return { resealed: 'epoch-switch' };

    // FALLBACK-004: only the model may move. Messages must still be append-only, so a
    // declared side switch cannot excuse a rewritten transcript.
    case 'fallback-side':
      return messagesStillAppendOnly(previous, nextWire)
        ? { resealed: 'fallback-side' }
        : { broken: 'fallback-side-rewrote-messages' };

    // SyncDelegate: a dedicated Inspector/Coder CE may swap the per-request
    // tool map (Returned → Completion) without replacing its transcript.
    case 'request-kind-switch':
      return requestKindKeepsPrefix(previous, nextWire)
        ? { resealed: 'request-kind-switch' }
        : { broken: 'request-kind-switch-rewrote-prefix' };

    // MANAGER-LOOP: a Relayed retirement is followed by another ordinary
    // tenure under the same system/provider plan. The root user is kept and
    // every next user is a subsequence of the previous users; retired-tenure
    // assistant/tool traffic and a novel wake are absent. Anything else is a
    // rewrite of the fixed parts.
    case 'manager-loop':
      return managerLoopKeepsAuthority(previous, nextWire)
        ? { resealed: 'manager-loop' }
        : { broken: 'manager-loop-rewrote-fixed' };

    default:
      throw new Error(`unknown cold boundary kind '${boundary.kind}'`);
  }
}

// ── load-time validation ────────────────────────────────────────────────────

export function validateBoundary(boundary) {
  const problems = [];

  if (!BOUNDARY_KINDS.includes(boundary.kind)) {
    problems.push(
      `unknown cold boundary kind '${boundary.kind}'; ARCH-004 admits only ${BOUNDARY_KINDS.join(', ')}`,
    );
  }
  if (typeof boundary.turn !== 'string' || boundary.turn === '') {
    problems.push('cold boundary must name the turn it happens at');
  }
  if (!Number.isInteger(boundary.step) || boundary.step < 0) {
    problems.push('cold boundary step must be a non-negative integer');
  }

  return problems;
}
