/**
 * runtime-key.js — the scenario lookup key, as semantic functions of body plus harness context.
 *
 * verification-system-001. Every semantic component is derived from the request alone; lane routing reads
 * only explicit harness context; nothing here reads
 * or writes state. That is the whole point: the matcher this replaces kept a
 * `pathCursor` per path and a `claimCount` per edge, so the answer depended on how
 * many requests had already arrived rather than on what this request says.
 *
 *   lane   which bound conversation thread — explicit harness session routing
 *   turn   the last user message's semantic content — longest-prefix matched
 *   step   how many assistant messages follow that user message
 *
 * `step` is what makes the cursor unnecessary. The Host appends exactly one
 * assistant message per provider step (`../opencode/packages/opencode/src/session/
 * prompt.ts:1186`, `parentID: lastUser.id`), so "which step of this turn" is a
 * countable property of the request. The cursor was bookkeeping for something the
 * request already carried.
 *
 * ── longest prefix, not predicate conjunction ───────────────────────────────
 *
 * The old key was a conjunction of up to eleven predicates (`user`, `userRegex`,
 * `containsText`, `requiredTools`, `forbiddenTools`, `messageCount`, `model`,
 * `sessionId`, `requestKind`, `role`, `afterToolResult`). A conjunction can match
 * several edges at once, which is why a `specificity` score existed — it summed
 * substring lengths and added magic numbers (`afterToolResult === true` → +50) to
 * decide which edge was "more specific".
 *
 * A true prefix cannot be ambiguous. Either the request's turn begins with the
 * declared text or it does not, and among several matches the longest is unique.
 * Two declarations of the SAME length that both match is an author error, not a
 * tie to break: `ambiguousTurn` reports it and the caller fails closed.
 */

import { messageText, semanticOf } from './provider-wire.js';
import { extractToolNames } from './strict-mock-matches.js';
// HOST-013: the production constant comes through its registered JS surface,
// so a rewording of the marker text fails here instead of silently making every
// marker-shaped assistant count as a real step.
import {
  source as pairProgrammingThoughtSource,
} from '../../../../../dist/OpenCode/Host/PairProgrammingThoughtSurface.js';
import { createHash } from 'node:crypto';
import { sanitizeMessage } from '../../../../../dist/OpenCode/Host/HostMessageProjection.js';

/**
 * The Host's own rendering of an assistant row that carries no content at all.
 *
 * Measured 2026-10-18 (Long Stroke release tier): the plugin stops a retired attempt by
 * emptying its message array (`RelayNarrativeTransform.project` → disposition
 * `RetiredAttemptStopped`), the Host still answers that physical attempt, and its row
 * survives into the successor's context — a part-less assistant message, which
 * `HostMessageProjection.sanitizeMessages` re-renders with exactly this placeholder text.
 *
 * Such a row is not a model step: no model reply ever existed for it, so no scenario can
 * declare one, and counting it would push every later step of that turn onto the wrong
 * declaration. The constant is DERIVED from the production surface at load time rather
 * than re-typed here (the HOST-013 rule above), so a rewording of the placeholder cannot
 * silently make every `...`-shaped reply a non-step.
 */
const hostNoContentAssistantText = (() => {
  const sanitized = sanitizeMessage({ role: 'assistant', parts: [] });
  const text = sanitized?.parts?.find((part) => part?.type === 'text')?.text;
  return typeof text === 'string' && text !== '' ? text : null;
})();

/**
 * The Host's synthetic dot ack, derived from the same production projection: a part-less
 * assistant row that carries a reasoning part is rendered with exactly this text
 * (`HostMessageProjection.fallbackText`), which is also the row its companion-frame
 * intersperse inserts. Exported so the seal's normalization reads the same derived value.
 */
export const hostDotAckText = (() => {
  const sanitized = sanitizeMessage({ role: 'assistant', parts: [{ type: 'reasoning', text: 'probe' }] });
  const text = sanitized?.parts?.find((part) => part?.type === 'text')?.text;
  return typeof text === 'string' && text !== '' ? text : null;
})();

/**
 * The Host's inserted readonly-replay block, measured on the provider wire.
 *
 * Production emits one `call` + one `result` row per BUNDLE BATCH of the promoted strength
 * frames (`Strength/OpenCode/Replay.fs` `expectedHostIds`: `StrengthFrame.hostMessageId …`
 * per `batch.RequestOrdinal`), so the block's size is per-decision (the canary decision
 * carries two batches, recovery decisions one) — it is a sequence of pairs, not a fixed
 * triple. The wire strips `info`, so the surviving discriminator is production's own
 * generated call identity: each inserted call id is the frame digest (`Frame.fs`
 * `hostMessageId` = `sha256` of the joined owner/decision/ordinal/half/digest coordinates),
 * whose shape is derived here from the production digest surface. The Host's own tool calls
 * carry `call_*` ids, so a real model step never matches.
 *
 * The trailing dot ack is NOT part of the insertion: `HostMessageProjection.sanitizeMessages`
 * synthesizes it only between two consecutive `role=user` rows, so a block that ends before
 * the next user row has none. It is therefore skipped when present, never required.
 */
const strengthReplayIdPattern = (() => {
  // Production's inserted rows carry `StrengthFrame.hostMessageId` ids (`Frame.fs`: the
  // sha256 hex of the joined owner/decision/ordinal/half/digest coordinates), so the shape
  // is the digest alphabet and length — taken here from the same hashing the harness
  // already uses for wire identities rather than by importing the production module.
  const digest = createHash('sha256').update('strength-replay-id-probe').digest('hex');
  return new RegExp(`^[0-9a-f]{${digest.length}}$`);
})();

const isInsertedReplayCall = (message) => {
  const calls = Array.isArray(message?.tool_calls) ? message.tool_calls : [];
  return strengthReplayIdPattern !== null
    && message?.role === 'assistant'
    && calls.length === 1
    && typeof calls[0]?.id === 'string'
    && strengthReplayIdPattern.test(calls[0].id);
};

/** How many rows this inserted replay pair occupies, or 0 when it is not one. */
const insertedReplayPairLength = (messages, index) => {
  const call = messages[index];
  if (!isInsertedReplayCall(call)) return 0;
  const result = messages[index + 1];
  if (result?.role !== 'tool' || result.tool_call_id !== call.tool_calls[0].id) return 0;
  const ack = messages[index + 2];
  const hasAck =
    ack?.role === 'assistant'
    && ack.content === hostDotAckText
    && !Array.isArray(ack.tool_calls);
  return hasAck ? 3 : 2;
};

const scopeReminderTexts = new Set([
  '[System: Estimate the turnBudget for the first fine-grained task block (state closed-loop). Then call resetScope immediately, before doing anything else.]',
  '[System: Before any resetScope initialization, call negotiate(questions: string) NOW. Ask the delegator all questions needed to clarify this assignment. Group related questions into the single plural questions argument. Once the call is accepted, negotiation is complete and the timeline continues: do not wait for any answer, and call resetScope promptly. If the delegator’s answer is obtained, it may arrive later as a user steer; it never blocks the timeline. You may negotiate again at any time; later calls never delay resetScope.]',
  '[System: You have received the handoff from the previous scope. You must now call askPreviousScope(questions) first to clarify specific questions with the previous scope based on the handoff and current objective. The call acknowledges sending immediately; continue your work without waiting. Answers will arrive later as a user steer.]',
]);

export const isScopeReminderText = (text) => scopeReminderTexts.has(text);

// ── lane ────────────────────────────────────────────────────────────────────

/**
 * Which lanes this request could belong to — a SET, not one name.
 *
 * A lane is addressed by the session the Host issued, and aliases exist because a
 * scenario names sessions before they exist (`manager`, `engineer-after`). The
 * binding is established when the Host mints the real id; HOST-008 makes the
 * association durable and the mock is told it rather than inferring it.
 *
 * ── why a set ───────────────────────────────────────────────────────────────
 *
 * Measured: every converted scenario binds TWO aliases to its primary session, e.g.
 * `bind = ["inspector-title", "inspector"]`, because the Host titles a session
 * on the same id it chats on. A reverse lookup returning the first match is then
 * decided by Map insertion order, not by the request — the exact impurity this
 * module exists to remove, hidden inside the function that names the key.
 *
 * So membership is the question the request can actually answer: "is this entry's
 * lane one of the aliases bound to this session?" `kind` then separates the title
 * entry from the chat entry, which is what it was added for. Two entries that still
 * tie after that are an author error `resolveEntry` reports rather than resolves.
 */
export function lanesOf(body, bindings, context) {
  const sessionId = sessionIdOf(context);
  if (sessionId === null) return new Set();

  const lanes = new Set();
  for (const [alias, bound] of bindings ?? []) {
    // A binding is alias → SET of sessions. `fast-reviewer` is not one thread, it is "any
    // reviewer thread": `orchestrator-publish` forks a Reviewer before the rebase and
    // another after it, and both belong to that lane. Their `turn` and `step` tell them
    // apart because each has its own conversation.
    //
    // A plain string is accepted too, so a unit fixture can write `new Map([['a', 'ses']])`.
    const matches = bound instanceof Set ? bound.has(sessionId) : bound === sessionId;
    if (matches) lanes.add(alias);
  }
  return lanes;
}

/**
 * The session id explicitly supplied by the harness adapter.
 *
 * Headers are observed at the HTTP boundary and never attached to the provider body. This
 * identity is routing and seal context; semantic content remains body-derived.
 */
export function sessionIdOf(context) {
  const id = context?.sessionId ?? null;
  return typeof id === 'string' && id !== '' ? id : null;
}

// ── kind ────────────────────────────────────────────────────────────────────

/**
 * The Host's own title marker, and where it actually appears.
 *
 * ── measured: not at messages[0] ────────────────────────────────────────────
 *
 * The comment this replaces cited `prompt.ts:235` for
 * `messages: [{ role: "user", content: "Generate a title…" }, ...msgs]` and so tested
 * `messages[0].role === 'user'`. The real request is:
 *
 *   roles   ["system", "user", "user"]
 *   [0]     "You are a title generator. You output ONLY a thread title…"
 *   [1]     "Generate a title for this conversation:\n"
 *
 * The title agent's system prompt comes first, so the marker is at [1]. With the old
 * check every title request classified as `chat`, and no title entry could ever match.
 *
 * Both defects here — this one and `sessionIdOf` — were invisible to 175 green gate
 * cases because the fixtures built bodies in the shape the code expected. Hence
 * `gate-lane-cases.mjs`, which posts to a live mock: the shape has to come from the
 * Host, not from the test author's memory of it.
 *
 * Scanning for the marker instead of indexing a fixed position: the number of leading
 * system messages is a Host prompt-assembly detail, and pinning it here would make this
 * function wrong again the next time that assembly changes.
 */
const TITLE_MARKER = 'Generate a title for this conversation:';

/** How far in to look. The marker is a preamble; a real turn cannot push it back. */
const TITLE_PREAMBLE_LIMIT = 4;

/**
 * `title` or `chat`. Read from the marker's presence in the preamble, not from prose
 * matching over the whole conversation.
 *
 * There is deliberately no `synthetic`. The old classifier had one, decided by
 * `NUDGE_MARKERS` — a table of production prompt sentences copied into the mock, which
 * the extinction list condemns as a cross-product dead heuristic. It is unnecessary
 * anyway: a nudge's LAST user message IS the nudge sentence, so `turnOf` already tells
 * it apart from any real turn. Only the title case needs help, because its marker sits
 * in the preamble while `turnOf` looks at the end.
 */
export function kindOf(body) {
  const messages = Array.isArray(body?.messages) ? body.messages : [];

  for (const message of messages.slice(0, TITLE_PREAMBLE_LIMIT)) {
    const content = message?.content;
    if (typeof content === 'string' && content.startsWith(TITLE_MARKER)) return 'title';
  }
  return 'chat';
}

// ── turn ────────────────────────────────────────────────────────────────────

const isUser = (message) => message?.role === 'user';

const isAssistant = (message) => {
  if (message?.role !== 'assistant') return false;
  // HOST-013: the synthetic pair-programming thought marker is not a real
  // assistant step; it never enters the scenario step cursor.
  //
  // Measured shapes: Host raw message may keep `info.source`. Provider wire often
  // strips it and instead carries the current empty-name `skill` call or legacy
  // auto-injected tool parts. All synthetic shapes must be skipped without hiding
  // real non-empty skill calls.
  const source = message?.info?.source;
  if (source === pairProgrammingThoughtSource || source === 'pair-programming-thought') return false;
  const isGuidelineToolPart = (part) => {
    if (part?.type !== 'tool') return false;
    if (part?.tool === '-' || part?.tool === 'auto-injected') return true;
    return part?.tool === 'skill' && part?.state?.input?.name === '';
  };
  if (Array.isArray(message?.parts) && message.parts.some(isGuidelineToolPart)) return false;
  const content = message?.content;
  if (Array.isArray(content) && content.some(isGuidelineToolPart)) return false;
  // OpenAI HTTP wire: current FakeReq is skill({name:""}); old transcripts may
  // still carry - / auto-injected. A non-empty real skill call remains a step.
  const isGuidelineToolCall = (call) => {
    const name = call?.function?.name ?? call?.name;
    if (name === '-' || name === 'auto-injected') return true;
    if (name !== 'skill') return false;
    const args = call?.function?.arguments ?? call?.arguments;
    if (args && typeof args === 'object') return args.name === '';
    if (typeof args !== 'string') return false;
    try {
      return JSON.parse(args)?.name === '';
    } catch {
      return false;
    }
  };
  const toolCalls = Array.isArray(message.tool_calls) ? message.tool_calls : [];
  if (toolCalls.length > 0 && toolCalls.every(isGuidelineToolCall)) return false;
  // The Host's no-content placeholder (see `hostNoContentAssistantText`): a stopped
  // retired attempt leaves one such row behind, and it is not a model step.
  if (
    hostNoContentAssistantText !== null
    && toolCalls.length === 0
    && message.content === hostNoContentAssistantText
  ) return false;
  return true;
};

const lastUserIndex = (messages) => {
  for (let index = messages.length - 1; index >= 0; index -= 1) {
    if (!isUser(messages[index])) continue;
    if (isScopeReminderText(messages[index].content)) continue;
    return index;
  }
  return -1;
};

/**
 * The last user message, as prefix-comparable semantic text.
 *
 * Goes through the production semantic projection, so a scenario matches exactly
 * what verification-system-001 says the exchange meant — ids and runtime metadata excluded,
 * multimodal parts reduced to their digest.
 *
 * No truncation. The old `extractLastUserMsg` sliced at 2000 characters, which made
 * two long prompts identical whenever they differed only past the cut — and a prompt
 * long enough to be truncated is exactly the kind a scenario needs to tell apart.
 */
export function turnOf(body) {
  const messages = Array.isArray(body?.messages) ? body.messages : [];
  const index = lastUserIndex(messages);
  if (index < 0) return null;

  const projected = semanticOf({ messages: [messages[index]] }).messages;
  if (projected.length === 0) return null;
  return messageText(projected[0]).replace(/^\[ANCHOR: [1-9]\d*\]\u0000\uFEFF/, '');
}

// ── step ────────────────────────────────────────────────────────────────────

/**
 * How many assistant messages follow the last user message.
 *
 * Zero on the first provider step of a turn, one after the model's first reply, and
 * so on. Counted from the request, never accumulated.
 */
export function stepOf(body) {
  const messages = Array.isArray(body?.messages) ? body.messages : [];
  const index = lastUserIndex(messages);
  if (index < 0) return 0;

  let step = 0;
  for (let cursor = index + 1; cursor < messages.length; cursor += 1) {
    const replayRows = insertedReplayPairLength(messages, cursor);
    if (replayRows > 0) {
      cursor += replayRows - 1;
      continue;
    }
    const message = messages[cursor];
    const next = messages[cursor + 1];
    const initialReminderAck = isUser(messages[cursor - 1])
      && message?.role === 'assistant' && message.content === hostDotAckText
      && !Array.isArray(message.tool_calls)
      && isUser(next) && isScopeReminderText(next.content);
    if (isAssistant(message) && !initialReminderAck) step += 1;
  }
  return step;
}

/**
 * The whole key.
 *
 * `lanes` is plural for the reason `lanesOf` explains. The diagnostic field `lane` is
 * the sorted join, so an unmatched-request report names every alias the session holds
 * instead of one arbitrarily chosen member.
 */
export function runtimeKeyOf(body, bindings, context) {
  const lanes = lanesOf(body, bindings, context);
  return {
    lanes,
    lane: lanes.size === 0 ? null : [...lanes].sort().join('|'),
    kind: kindOf(body),
    turn: turnOf(body),
    step: stepOf(body),
  };
}

// ── longest-prefix lookup ───────────────────────────────────────────────────

/**
 * A declaration's fragments, in order. A plain string is a one-fragment declaration.
 *
 * ── why more than one fragment exists ───────────────────────────────────────
 *
 * Measured in K9 against REVIEW-002. When a Manager forks a Reviewer, production does not
 * send the Manager's assignment as the prompt — it WRAPS it
 * (`src/Wanxiangshu/Session/HostForkRuntimeFork.fs:98`):
 *
 *   [Original user requirements — authoritative review scope]
 *   …verified HumanRoot prompts since the prior review…
 *
 *   User prompt 1:
 *   <the human's actual prompt>
 *
 *   [Manager review request — supplementary]
 *   <the assignment the scenario declared>
 *
 * So the text a scenario knows sits at the END. A single prefix cannot reach it, and
 * declaring only the wrapper is worse than useless: `manager-full-loop` forks a Reviewer
 * twice (a REVISE round, then a dual-PERFECT round) and both requests share that wrapper
 * byte for byte. One prefix declaration would make them the same key, and the load-time
 * duplicate check would rightly refuse to guess which response belongs to which.
 *
 * ── why this is not `containsText` coming back ──────────────────────────────
 *
 * The retired `containsText` was an UNORDERED BAG of substrings, each free to match
 * anywhere. Three properties separate this from it:
 *
 *   anchored    fragment 0 must be a true prefix of the turn
 *   ordered     each later fragment must occur after the previous one ends
 *   additive    the uniqueness weight is total fragment length, so "more declared text"
 *               still means "more specific" and ties are still author errors
 *
 * A bag has none of the three, which is why it needed `specificity` scoring to choose
 * between overlapping matches. This is the same longest-prefix rule with a hole punched
 * through the middle at a point the author names explicitly.
 */
export const turnFragments = (turn) => (Array.isArray(turn) ? turn : [turn]);

/**
 * How much declared text matches, or `null` when the declaration does not apply.
 *
 * The weight is what replaces the old prefix `.length`, and it must be the SUM rather than
 * the span: measuring "distance from start of first to end of last" would make a
 * declaration look more specific for skipping more text it never named.
 */
const matchWeight = (turn, declaration) => {
  const fragments = turnFragments(declaration.turn);
  if (!turn.startsWith(fragments[0])) return null;

  let cursor = fragments[0].length;
  for (const fragment of fragments.slice(1)) {
    const found = turn.indexOf(fragment, cursor);
    if (found < 0) return null;
    cursor = found + fragment.length;
  }

  return fragments.reduce((total, fragment) => total + fragment.length, 0);
};

const prefixMatches = (turn, declarations) =>
  turn === null
    ? []
    : declarations
        .map((declaration) => ({ declaration, weight: matchWeight(turn, declaration) }))
        .filter(({ weight }) => weight !== null)
        .sort((left, right) => right.weight - left.weight);

/**
 * Resolve a request against declared (lane, turn, step) entries.
 *
 * Returns one of three shapes, never a scored best guess:
 *
 *   { matched: entry }              exactly one longest prefix at this lane+step
 *   { ambiguousTurn: [...] }        two declarations of equal length both match
 *   { unmatched: { key, ... } }     nothing declared for this request
 *
 * `ambiguousTurn` is a load-time author error surfaced at runtime. It cannot be
 * resolved by picking one: two same-length prefixes that both match describe the
 * same point in the conversation with two different responses, so the scenario does
 * not say what the model does next.
 */
/**
 * The tools gate: a declared `tools` list is an assertion that the wire request
 * carries every named tool (the old `requiredTools` semantics), and
 * `forbiddenTools` asserts absence. A request failing the gate is not a match
 * for that entry — it falls through to `unmatched` and the strict mock fails
 * closed, exactly like an undeclared turn. Without the gate the fields were
 * dead data: compiled into entries, read by no one, while authors believed the
 * AGENT-006 tool matrix was under test.
 */
const toolsGate = (entry, requestTools) => {
  if ((entry.tools ?? []).some((name) => !requestTools.includes(name))) return false;
  if ((entry.forbiddenTools ?? []).some((name) => requestTools.includes(name))) return false;
  return true;
};

export function resolveEntry(body, entries, bindings, context) {
  const key = runtimeKeyOf(body, bindings, context);
  const requestTools = extractToolNames(body);

  const atKey = entries.filter(
    (entry) =>
      (entry.lane === undefined || key.lanes.has(entry.lane)) &&
      (entry.kind ?? 'chat') === key.kind &&
      entry.step === key.step &&
      toolsGate(entry, requestTools),
  );

  const matches = prefixMatches(key.turn, atKey);
  if (matches.length === 0) {
    return { unmatched: { key, candidates: atKey } };
  }

  const heaviest = matches[0].weight;
  const tied = matches.filter((match) => match.weight === heaviest);
  if (tied.length > 1) {
    return { ambiguousTurn: tied.map((match) => match.declaration), key };
  }

  return { matched: matches[0].declaration, key };
}
