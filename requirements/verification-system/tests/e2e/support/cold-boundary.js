/**
 * cold-boundary.js — explicit declared exceptions to the prefix seal.
 *
 * ARCH-004 keeps the provider-visible prefix byte-stable so KV-cache hits. verification-system-001
 * §"冷边界显式声明" names an explicit finite set of legitimate exceptions and requires
 * the scenario to say WHERE each happens:
 *
 *   COMPANION-009  epoch switch — new SealRoot, one explicit prefix rebase
 *   PROVIDER-RETRY model rotation — fixed Role routes to a new model target, so the banner does
 *   CTX-010        prefix probe — attempt-local head, fixed tool set
 *   ENFORCER-030   frame commit — Blogger frame list grows, fixed system/tools
 *   SyncDelegate   request-kind switch — per-request tool map swap, same transcript
 *   MANAGER-LOOP   manager iteration — same system/plan, root authority kept while
 *                  retired-iteration traffic and the wake are removed
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

import { readFileSync } from 'node:fs';
import { isDeepStrictEqual } from 'node:util';
import { isAppendOnlyPrefix, wireOf } from './provider-wire.js';

export const BOUNDARY_KINDS = [
  'epoch-switch',
  'fallback-side',
  'prefix-probe',
  'frame-commit',
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

/**
 * CTX-010 probe admission: the tool set is a fixed part of the attempt
 * (PROMPT-008), everything else may move.
 *
 * The system prompt is deliberately NOT compared, and this is a measured Host
 * fact rather than a compromise: Host 1.18.9 injects the model name into the
 * system prompt (`../opencode/packages/opencode/src/session/system.ts:67`:
 * "You are powered by the model named ${model.api.id}…"), so a fallback side
 * switch — which is exactly what accompanies a recovery attempt — changes the
 * system bytes by construction. The probe's own claim is about the MESSAGE
 * prefix; the declaration admits the whole recovery request.
 */
const probeKeepsFixedParts = (previousWire, nextWire) => isDeepStrictEqual(previousWire.tools, nextWire.tools);

// ── pure manager loop ───────────────────────────────────────────────────────
//
// Every manager iteration keeps the same workspace, tools and authority plan: a
// Continue retirement is followed by another ordinary iteration, and the successor
// keeps the predecessor's provider history instead of restarting from a trimmed
// transcript (relay-context-projection-001: raw messages, tool calls, results, nudge,
// late parts and the internal loop wake are all retained). A preceding provider
// fallback may change only OpenCode's exact model banner. This layer checks structure
// only — the normalized system is equal, the previous message list is an ordered
// PREFIX of the next one, and the only admitted difference inside that prefix is a
// retired auxiliary injection (context-compression-019 / GAP-022: an old horizon's
// guidance occurrence or companion frame must not replay). So a rewritten or dropped
// historical message, a novel wake or a synthetic user cannot smuggle in, while the
// successor's own appended turns and its fresh-head prompt are exactly what the
// retained-history contract expects. Full typed authority-revision retention is proved
// by the unit projection tests and the long-stroke root-only oracle, not here.

const sameProviderPlan = (previousWire, nextWire) => isDeepStrictEqual(previousWire.tools, nextWire.tools);

// The Host appends each guidance occurrence — a `<system>` block or an
// Enforcer tip — as a `\0\uFEFF`-separated suffix to the request's terminal part (a user
// message or the last completed tool result; `cursorGuidanceSeparator` in
// PairProgrammingThoughtTransform.fs). A successor iteration retires such an occurrence
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

// The companion frame is the other retired auxiliary injection: the Host renders it as
// a synthetic assistant ack row plus a user row carrying the memory preamble and the
// prior work record. Match the complete rendered resource, never a keyword in user prose.
const COMPANION_PREAMBLES = ['en', 'zh-CN'].map((language) =>
  readFileSync(new URL(`../../../../../resources/provider/lifecycle/companion/memory-preamble/${language}.md`, import.meta.url), 'utf8')
    .trim()
    .split('\n')
    .map((line) => `# ${line}`)
    .join('\n'),
);

const isCompanionFrameRow = (message) =>
  message?.role === 'user'
  && message.parts?.length === 1
  && message.parts[0]?.kind === 'text'
  && typeof message.parts[0].text === 'string'
  && COMPANION_PREAMBLES.some((preamble) => message.parts[0].text.startsWith(`${preamble}\n`));

const withoutCompanionFrames = (messages) => {
  const kept = [];
  for (const message of messages) {
    if (!isCompanionFrameRow(message)) {
      kept.push(message);
      continue;
    }
    const ack = kept.at(-1);
    const isSyntheticAck = ack?.role === 'assistant'
      && ack.parts?.length === 1
      && ack.parts[0]?.kind === 'text'
      && ack.parts[0].text === '.';
    if (isSyntheticAck) kept.pop();
    else kept.push(message);
  }
  return kept;
};

const managerLoopKeepsAuthority = (previousWire, nextWire) => {
  if (!sameProviderPlan(previousWire, nextWire)) return false;
  const systemOf = (wire) => (wire.messages ?? []).filter((message) => message?.role === 'system');
  const normalizedPrevious = withoutHostModelBanner(previousWire);
  const normalizedNext = withoutHostModelBanner(nextWire);
  if (!isDeepStrictEqual(systemOf(normalizedPrevious), systemOf(normalizedNext))) return false;

  const messagesOf = (wire) => withoutCompanionFrames((wire.messages ?? []).map(withoutGuidanceMessage));
  const previousMessages = messagesOf(normalizedPrevious);
  const nextMessages = messagesOf(normalizedNext);
  if (previousMessages.length === 0 || nextMessages.length < previousMessages.length) return false;

  return previousMessages.every((message, index) => isDeepStrictEqual(message, nextMessages[index]));
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
export function sealDecision({ previousWire, body, boundary }) {
  if (previousWire === null || previousWire === undefined) {
    // `prefix-probe`, `frame-commit`, and `manager-loop` share the multi-delivery
    // shape: the first request establishes the seal (nothing to break), later ones
    // break it. A `manager-loop` boundary sits on a reusable step-0 entry, so its
    // initial delivery has no previous wire and must be held.
    if (boundary !== null && boundary !== undefined
        && (boundary.kind === 'prefix-probe' || boundary.kind === 'frame-commit' || boundary.kind === 'manager-loop')) {
      return { held: true };
    }
    return boundary === null || boundary === undefined
      ? { held: true }
      : { broken: 'boundary-not-reached', kind: boundary.kind };
  }

  const nextWire = wireOf(body);
  const held = isAppendOnlyPrefix(previousWire, nextWire);

  if (boundary === null || boundary === undefined) {
    return held ? { held: true } : { broken: 'undeclared' };
  }

  if (held) {
    // A `prefix-probe` declaration governs an ENTRY, and an entry is delivered
    // several times across a recovery sequence (probe slots and ordinary slots
    // alternate as the cursor advances). Only some of those deliveries break the
    // prefix, so an append-only delivery is legal; "the declaration never fired at
    // all" is checked at scenario end by `ScenarioRuntime.unfiredBoundaries`.
    // `frame-commit` shares this shape: the Blogger session's first request
    // establishes the seal (nothing to break), later requests break it as frames
    // accumulate. `manager-loop` shares it too: a reusable step-0 entry may see
    // append-only retries that stay held, with only the next iteration's restart
    // breaking the seal.
    // `epoch-switch` shares the same shape: the declaration admits a wholesale
    // prefix rebase, and a delivery that stays append-only is perfectly legal
    // (the boundary simply wasn't needed for this particular run).
    return boundary.kind === 'prefix-probe' || boundary.kind === 'frame-commit' || boundary.kind === 'manager-loop' || boundary.kind === 'epoch-switch'
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
      return messagesStillAppendOnly(previousWire, nextWire)
        ? { resealed: 'fallback-side' }
        : { broken: 'fallback-side-rewrote-messages' };

    // CTX-010: an attempt-local X prefix probe replaces the committed prefix with a
    // synthetic companion-memory head plus the tail after the candidate cutoff. The
    // declaration admits exactly that: the system prompt and the tool set are fixed
    // for the attempt (they belong to the profile, PROMPT-008), while the message
    // prefix is deliberately rebased and the model may move with the fallback side
    // switch that usually accompanies a recovery attempt. Anything that rewrites the
    // fixed parts is not a probe.
    case 'prefix-probe':
      return probeKeepsFixedParts(previousWire, nextWire)
        ? { resealed: 'prefix-probe' }
        : { broken: 'prefix-probe-rewrote-fixed' };

    // ENFORCER-030: a frame commit rebuilds the Blogger session's provider view —
    // the frame list grows by one and the delta advances. The system prompt and
    // the tool set (blog only) are fixed parts of the Blogger profile
    // (ENFORCER-010), so only the message prefix may move.
    case 'frame-commit':
      return probeKeepsFixedParts(previousWire, nextWire)
        ? { resealed: 'frame-commit' }
        : { broken: 'frame-commit-rewrote-fixed' };

    // SyncDelegate: a dedicated Inspector/Coder CE may swap the per-request
    // tool map (Returned → Completion) without replacing its transcript.
    case 'request-kind-switch':
      return requestKindKeepsPrefix(previousWire, nextWire)
        ? { resealed: 'request-kind-switch' }
        : { broken: 'request-kind-switch-rewrote-prefix' };

    // MANAGER-LOOP: a Continue retirement is followed by another ordinary
    // iteration under the same system/provider plan. The root user is kept and
    // every next user is a subsequence of the previous users; retired-iteration
    // assistant/tool traffic and a novel wake are absent. Anything else is a
    // rewrite of the fixed parts.
    case 'manager-loop':
      return managerLoopKeepsAuthority(previousWire, nextWire)
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
