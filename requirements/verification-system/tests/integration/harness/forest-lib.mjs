/**
 * gate-forest-lib.mjs — the shared device K10 (forest-wide determinism) and K11
 * (mutation self-checks) both drive. One harness, deliberately, because two would drift
 * and the drift would be invisible: each file would still be green about its own copy.
 *
 * `design-script-forest.md:581` states the property K10 has to prove — 「森林自检：同请求
 * 序列 → 同内容序列」 — and it is the only one of K10's four items that is FOREST-wide
 * rather than per-fixture. The other three (no dead edges, no index conflict, bounded
 * faults) are already enforced at load time by `scenario-schema.js`. So what this file
 * has to make possible is: compile all fifteen real scenarios, derive a request sequence
 * for one, drive it, and serialise the run to text two runs can be compared on.
 *
 * ── the derived sequence, and why it is derived rather than written ──────────
 *
 * A hand-written per-scenario request fixture would encode fifteen copies of the thing
 * the property is supposed to check: if the fixture and the scenario disagree the
 * fixture wins, and the run stays green while proving nothing about the file a human
 * edited. So the sequence comes from the compiled scenario alone, by four rules:
 *
 *   session   ONE synthetic session per declared TURN
 *   turn text `turnFragments(turn).join(FRAGMENT_GAP)`
 *   step      one synthetic assistant message appended per step
 *   attempts  a step governed by a fault is delivered `max(attempts) + 1` times
 *
 * The session rule is the one that needed measurement. `lanesOf` treats a binding as
 * alias → SET of sessions, so binding several sessions to one alias is legitimate rather
 * than a workaround (`orchestrator-publish` really does fork two Reviewers). Three
 * measured reasons to use one session per turn instead of one per lane:
 *
 *   a turn declares `tools`, and tools are part of the wire identity, so two turns of one
 *   lane with different tool sets cannot share a seal chain — `manager`'s
 *   `manager-guard` (fork/join/list) and a sibling manager turn with a different tool set
 *   would break ARCH-004 on the second request and the sequence would be testing the seal
 *   instead of the lookup
 *
 *   an `internal` turn declares no lane at all, and `resolveEntry` admits
 *   `entry.lane === undefined` at ANY session, so its session can simply stay unbound —
 *   no alias has to be invented for it, which would be the harness making up a fact
 *
 *   production forks a fresh session per child anyway, so one-per-turn is closer to the
 *   wire than one-per-lane would be
 *
 * Ordering is the compiled entry order, which is TOML declaration order. Nothing here
 * iterates a Map or a Set to decide sequence.
 *
 * ── what the derived sequence is NOT ────────────────────────────────────────
 *
 * Named here so K10 can scope its case honestly instead of appearing to cover more than
 * it does. All fifteen scenarios ARE derivable (measured), with three limits:
 *
 *   the request carries the DECLARED PREFIX, not production's full utterance. For an
 *   `internal` turn the declaration is only the head of a prompt production composes, so
 *   determinism is proven over the forest's own declarations — not over the bytes the
 *   Host will actually send. That is why this cannot replace a canary.
 *
 *   the ORDER is a canonical enumeration, not production's causal order: no fork/join
 *   wiring, no restart, no title-after-first-turn timing. Those depend on the Host, and
 *   a sequence that guessed at them would be a fixture again.
 *
 *   a `tool-call` response is followed by a plain synthetic assistant message rather than
 *   an assistant call plus its tool result. `step` counts assistant messages, so the
 *   count is faithful; the transcript is not.
 *
 * ── serialisation: what IS compared ────────────────────────────────────────
 *
 * Excluding everything that could vary would make byte-equality vacuous, so the line
 * format is stated positively. Per request, four fields:
 *
 *   resolved entry id   which content edge answered — the old `pathCursor` bug moved this
 *   selection kind      deliver / fault / unmatched / ambiguous / seal-broken
 *   attempt             the physical delivery counter, so a fault plan is visible
 *   response digest     sha256 of the canonical response body actually chosen
 *
 * Nothing is excluded as run-varying, because by construction nothing here varies:
 * session ids are derived from turn ids, and the response bodies come from the TOML. The
 * property therefore still has teeth — it fails if selection ever depends on arrival
 * order, on Map/Set iteration order, or on anything a fresh runtime does not carry.
 *
 * A header line naming the scenario and the request count is prepended so that two runs
 * that produced NO requests cannot compare equal by both being empty.
 *
 * ── patching: measured, and direct module patching is impossible ────────────
 *
 * `withPatched` refuses an ES module namespace rather than pretending. Measured on this
 * platform (node, .mjs, strict mode):
 *
 *   Object.prototype.toString.call(ns)              '[object Module]'
 *   Object.isFrozen(ns)                             false
 *   getOwnPropertyDescriptor(ns, 'sealDecision')    { writable: true, configurable: false }
 *   ns.sealDecision = fn                            TypeError: Cannot assign to property
 *                                                   'sealDecision' of [object Module]
 *   Object.defineProperty(ns, 'sealDecision', …)     TypeError: Cannot redefine property
 *
 * The descriptor is the trap: it reports `writable: true` while the module namespace
 * exotic object's [[Set]] always fails. A patcher that trusted the descriptor would
 * conclude it had patched. Under CommonJS (`node -e`, sloppy mode) the assignment does
 * not even throw — it silently no-ops — which is exactly the vacuously-green K11 the
 * brief warns about.
 *
 * `configurable: false` is what separates a module namespace from a patchable carrier, so
 * that is the property this checks, and the patch is then verified by reading the value
 * back. Two carriers do work and were measured: a class prototype
 * (`ScenarioRuntime.prototype.select` — writable, configurable, patch took effect and
 * restored) and any ordinary object.
 *
 * The consequence for K11 is a better shape than module patching would have been: three
 * of its four classes are naturally expressed as a mutated INPUT — a wrong scenario
 * source, or a request an incorrect implementation would have admitted — driven through
 * the UNMODIFIED forest, with `rejectsSelect` asserting the refusal. A mutated input
 * cannot fail to apply, and it proves the shipped code rejects the mutation rather than
 * proving a stand-in does.
 */

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { relative } from 'node:path';

import { walk } from '../../../../../scripts/lib/walk.mjs';
import { createHash } from 'node:crypto';
import { canonicalJson } from '../../../../../dist/OpenCode/Codec/CanonicalJsonSurface.js';
import { compileScenario } from '../../e2e/support/scenario-schema.js';
import { faultBody } from '../../e2e/support/delivery-plan.js';
import { kindOf, stepOf, turnFragments } from '../../e2e/support/runtime-key.js';
import { ScenarioRuntime } from '../../e2e/support/scenario-runtime.js';

const REPO_ROOT = fileURLToPath(new URL('../../../', import.meta.url));
export const SCENARIO_DIR = fileURLToPath(new URL('../../e2e/scenarios', import.meta.url));

/** Build the explicit runtime context from the session id carried by a test request. */
export const contextOf = (sessionId) => ({ sessionId });

const repoPath = (file) => relative(REPO_ROOT, file);

// ── 1. load the forest ──────────────────────────────────────────────────────

/**
 * Every scenario source on disk, in path order.
 *
 * Read through `walk` rather than a listed set of names so a scenario added to the
 * directory joins the property automatically. A hard-coded list would let a new file skip
 * the forest-wide check silently, which is the same drift `CANARY_COUNT = 17` produced one
 * layer up.
 */
export function forestSources() {
  return walk(SCENARIO_DIR, ['.toml']).map((file) => ({
    file: repoPath(file),
    source: readFileSync(file, 'utf8'),
  }));
}

/**
 * Compile the whole forest, or throw naming the first file that failed and why.
 *
 * Fail closed rather than skipping: a forest-wide property computed over fourteen of
 * fifteen scenarios reports a pass for a coverage it does not have.
 *
 * `sources` is injectable so a self-test can feed a source that must not compile. Writing
 * a broken file into `tests/e2e/scripts/` would break `gate:toml` and every other
 * gate that reads the directory, so the fail-closed path has to be reachable without one.
 */
export function loadForest({ sources } = {}) {
  const loaded = [];

  for (const { file, source } of sources ?? forestSources()) {
    const result = compileScenario(source, { name: file });
    if (result.ok !== true) {
      throw new Error(`${file} does not compile, so the forest is incomplete: ${result.problems.join(' | ')}`);
    }
    loaded.push({ file, name: result.scenario.name, scenario: result.scenario });
  }

  return loaded;
}

// ── 2. derive a deterministic request sequence ──────────────────────────────

/**
 * What joins a fragment declaration into one request text.
 *
 * `matchWeight` requires fragment 0 to be a true prefix and each later fragment to occur
 * after the previous one ends, so any separator works. A blank line is used because it is
 * what production's Reviewer wrapper puts between its sections
 * (`src/Wanxiangshu/Session/HostForkRuntimeFork.fs:98`), which keeps the derived text readable in a
 * diff without pretending to be the real wrapper.
 */
const FRAGMENT_GAP = '\n\n';

/**
 * The Host's title marker.
 *
 * `runtime-key.js:146` owns this string and does not export it; AGENTS.md forbids adding
 * an export for test visibility. So it is written here as a fixture and then CHECKED — a
 * derived title request whose `kindOf` is not `title` makes the scenario underivable with
 * that as the stated reason, rather than silently becoming a chat request that matches
 * nothing.
 */
const TITLE_MARKER = 'Generate a title for this conversation:\n';
/** The fixed system head every chat request carries on the real wire. */
const SYSTEM_MARKER = 'You are a managed agent.';

const user = (text) => ({ role: 'user', content: text });
const assistant = (text) => ({ role: 'assistant', content: text });
const systemMessage = (model) => ({
  role: 'system',
  content: `${SYSTEM_MARKER}\nYou are powered by the model named ${model}. The exact model ID is test/${model}`,
});

/** The declared text as one prefix-comparable string. */
export const declaredText = (turn) => turnFragments(turn).join(FRAGMENT_GAP);

/**
 * Entries grouped by declared turn, in declaration order.
 *
 * Keyed by `turnId`, which `compileTurns` copies onto every entry, so the grouping cannot
 * disagree with the compiler about which steps belong together.
 */
const turnGroups = (scenario) => {
  const groups = new Map();
  for (const entry of scenario.entries) {
    const group = groups.get(entry.turnId) ?? [];
    group.push(entry);
    groups.set(entry.turnId, group);
  }
  return [...groups.entries()].map(([turnId, entries]) => ({ turnId, entries }));
};

/** How many physical deliveries this step needs for its fault plan to run out. */
const deliveryCount = (scenario, entry) => {
  const fault = (scenario.faults ?? []).find((declared) => declared.entryId === entry.id);
  return fault === undefined ? 1 : Math.max(...fault.attempts) + 1;
};

/**
 * CTX-010: a cold-boundary turn continues the SAME session as the turn before it —
 * production retries a failed attempt in the same session, and the probe rebases
 * that session's prefix. The forest's one-session-per-turn rule would give the
 * continuation a fresh session with no seal, where the declaration can only ever
 * report `boundary-not-reached` — making every boundary-declaring scenario fail K10
 * by construction. Reusing the previous turn's session (TOML declaration order) is
 * the model's one piece of causal knowledge, and it matches how the wire actually
 * behaves.
 */
const hasBoundary = (scenario, entries) => entries.some((entry) => boundaryAt(scenario, entry) !== undefined);

/** The boundary declared AT an entry, if any. */
const boundaryAt = (scenario, entry) =>
  (scenario.boundaries ?? []).find((boundary) => boundary.entryId === entry.id);

/**
 * A request sequence for one compiled scenario.
 *
 * Returns `{ bindings, requests }`, or `{ underivable }` naming the reason. Never a
 * partial sequence: a sequence missing the steps it could not build would let K10 report
 * determinism over a fraction of the scenario.
 *
 * Each request records the entry it is BUILT for, so the run can report a request that
 * resolved to something else. That is the one way this derivation can be wrong without
 * being obviously wrong — two declarations whose match weights tie — and it must surface
 * as a mismatch rather than as a different-but-stable serialisation.
 */
export function deriveRequests(scenario) {
  // A turn group whose lane a flow 'createSession' opens runs in a NEW session, not a
  // CTX-010 continuation of the previous chat turn: 'createSession' allocates a fresh
  // session, so its first request has no previous wire and the seal HOLDS. Reusing the
  // previous turn's session here would make that first delivery look like an undeclared
  // prefix rebase - the break vs-003 reports for strength-recovery-owner.0. Only a
  // request rejected and retried inside the SAME session is a continuation; a
  // createSession lane is the explicit signal that this group is not.
  const createSessionLanes = new Set(
    (scenario.flow ?? [])
      .filter((flowStep) => flowStep?.createSession !== undefined)
      .flatMap((flowStep) => [
        ...(Array.isArray(flowStep.createSession.bind) ? flowStep.createSession.bind : []),
        ...(typeof flowStep.createSession.as === "string" ? [flowStep.createSession.as] : []),
      ]),
  );
  const bindings = [];
  const requests = [];
  let previousSessionId = null;
  let previousTools = null;
  let previousMessages = null;

  turnGroups(scenario).forEach((group, groupIndex) => {
    // Internal prompts are composed by production after durable facts appear;
    // they are not caller requests and therefore cannot be derived from the
    // scenario's public request sequence. `unanswered()` excludes them too.
    const entries = group.entries.filter((entry) => entry.internal !== true);
    if (entries.length === 0) return;

    const first = entries[0];
    // A title request carries the Host's own marker as its first message and is not part
    // of the chat conversation: it neither continues the previous chat nor seeds the one
    // that follows it (the seal does not compare title requests at all).
    const isTitle = first.kind === 'title';
    // Derived from the declaration, not from a counter shared with other scenarios, so the
    // same scenario always yields the same ids no matter which order the forest is walked.
    // A cold-boundary turn continues the previous turn's session (see `hasBoundary`); a
    // `manager-loop` entry reuses its own session across deliveries, which is why its
    // triple is declared once.
    const managerLoop = group.entries.some((entry) => boundaryAt(scenario, entry)?.kind === 'manager-loop');
    const opensNewSession = first.lane !== undefined && createSessionLanes.has(first.lane);
    const continued =
      !isTitle && !managerLoop && !opensNewSession
        && hasBoundary(scenario, group.entries) && previousSessionId !== null;
    const sessionId = continued ? previousSessionId : `ses_${String(groupIndex).padStart(2, '0')}_${first.turnId}`;
    if (!isTitle) previousSessionId = sessionId;
    if (first.lane !== undefined) bindings.push([first.lane, sessionId]);

    const text = declaredText(first.turn);
    // Same causal model as the session reuse: a continuation stays in the same role, so
    // its tool set is the previous turn's. `internal` turns rarely declare `tools`
    // (production decides them), and a boundary turn MUST keep the fixed parts of the
    // previous request byte-identical for the probe admission to hold.
    const declaredTools = (first.tools ?? []).map((name) => ({ type: 'function', function: { name } }));
    const requestKindSwitch = group.entries.some(
      (entry) => boundaryAt(scenario, entry)?.kind === 'request-kind-switch',
    );
    const tools = continued && previousTools !== null && !requestKindSwitch && !managerLoop
      ? previousTools
      : declaredTools;
    if (!isTitle) previousTools = tools;

    // The conversation this session accumulates. Growing it in place is what keeps every
    // chat request an append-only continuation of the previous one (ARCH-004). A
    // boundary turn starts from its own declared text: its first delivery is NOT an
    // append-only continuation of the previous turn (that is the whole point of the
    // declaration), and later deliveries of the same entry append to it.
    //
    // A chat request carries its system prompt as `messages[0]` on the real wire, and
    // the boundary admission compares that head byte-for-byte — so the derived
    // sequence must carry one too, or every boundary-declared entry would look like it
    // rewrote the fixed parts. Title requests keep their marker shape (the seal does
    // not compare them).
    const model = 'forest-lib-model';
    // A `manager-loop` entry's first delivery establishes the seal and later deliveries of
    // the SAME entry append to it, so the group starts from its declared authority text.
    // The successor iteration is production-composed (its entry is `internal`), so no
    // caller request can derive it; retained-history retention is proved by the real e2e
    // oracles and the projection unit tests, not here. Every other cold boundary replaces
    // the prefix — that is its whole point.
    const messages =
      requestKindSwitch && previousMessages !== null
        ? [...previousMessages, user(text)]
        : isTitle
          ? [user(TITLE_MARKER), user(text)]
          : [systemMessage(model), user(text)];

    let derivedStep = stepOf({ messages });
    for (const entry of entries) {
      // `runtimeStep` may deliberately pin a sparse measured Host cursor. Fill
      // only the missing assistant positions so the derived request reaches the
      // exact declared step without inventing another scenario edge.
      while (derivedStep < entry.step) {
        messages.push(assistant(`derived sparse cursor ${derivedStep} of ${entry.turnId}`));
        derivedStep += 1;
      }
      for (let delivery = 0; delivery < deliveryCount(scenario, entry); delivery += 1) {
        requests.push({
          expectedEntryId: entry.id,
          sessionId,
          body: {
            sessionID: sessionId,
            model,
            tools,
            messages: [...messages],
          },
        });
      }
      messages.push(assistant(`declared step ${entry.step} of ${entry.turnId}`));
      derivedStep += 1;
    }
    if (!isTitle) previousMessages = messages;
  });

  const misclassified = requests.filter(
    (request) =>
      kindOf(request.body) !==
      (scenario.entries.find((entry) => entry.id === request.expectedEntryId).kind ?? 'chat'),
  );
  if (misclassified.length > 0) {
    return {
      underivable:
        `kindOf disagrees with the declared kind for ${misclassified.map((r) => r.expectedEntryId).join(', ')}; ` +
        'the title marker in runtime-key.js has drifted from the one this file builds',
    };
  }

  return { bindings, requests };
}

// ── 3. serialise a run to comparable text ───────────────────────────────────

const sha256Hex = (value) => createHash('sha256').update(value).digest('hex');
const digestOf = (value) => sha256Hex(canonicalJson(value ?? null)).slice(0, 16);

/** The four fields, and the one selection shape each comes from. */
const lineOf = (selection) => {
  if (selection.unmatched !== undefined) return ['-', 'unmatched', '-', '-'];
  if (selection.ambiguous !== undefined) {
    return [
      selection.ambiguous.entries.map((entry) => entry.id).join('+'),
      'ambiguous',
      '-',
      '-',
    ];
  }
  if (selection.sealBroken !== undefined) return ['-', `seal-broken:${selection.sealBroken.reason}`, '-', '-'];
  if (selection.fault !== undefined) {
    return [selection.entry.id, 'fault', String(selection.attempt), digestOf(faultBody(selection.fault))];
  }
  return [selection.entry.id, 'deliver', String(selection.attempt), digestOf(selection.entry.respond)];
};

/**
 * Drive a FRESH runtime over `requests` and return the run as text plus its defects.
 *
 * Fresh rather than reused: `attempts` counts physical deliveries for the whole scenario,
 * so a second run on the same runtime would legitimately produce different attempt
 * numbers and the comparison would be measuring the counter instead of the selection.
 */
export function runForest(scenario, { bindings, requests }) {
  const runtime = new ScenarioRuntime(scenario);
  for (const [alias, sessionId] of bindings) runtime.bindAlias(alias, sessionId);

  const lines = [`scenario ${scenario.name} requests ${requests.length}`];
  const mismatches = [];

  for (const request of requests) {
    const context = contextOf(request.body.sessionID);
    const selection = runtime.select(request.body, context);
    runtime.consume(request.body, selection, context);

    const fields = lineOf(selection);
    lines.push(fields.join(' '));

    if (fields[0] !== request.expectedEntryId) {
      mismatches.push(`${request.expectedEntryId} resolved as ${fields[0]} (${fields[1]})`);
    }
  }

  return {
    text: `${lines.join('\n')}\n`,
    mismatches,
    unanswered: runtime.unanswered().map((entry) => entry.id),
  };
}

/** One scenario's derivation and run, as one line of report text. */
export const forestReportLine = ({ name, scenario }) => {
  const derived = deriveRequests(scenario);
  if (derived.underivable !== undefined) return `${name} UNDERIVABLE ${derived.underivable}`;

  const run = runForest(scenario, derived);
  return (
    `${name} requests=${derived.requests.length} ` +
    `mismatched=${run.mismatches.length} unanswered=${run.unanswered.length}`
  );
};

// ── 4. the two assertion helpers ────────────────────────────────────────────

const SELECT_DISCRIMINANTS = ['unmatched', 'ambiguous', 'sealBroken'];

/**
 * Assert `select()` REFUSED this request, with the specific refusal named.
 *
 * `gate-lib.mjs` has no negative helper on purpose and gains none here; the house pattern
 * is a local helper per cases file (`gate-schema-cases.mjs:18 rejects`,
 * `gate-source-cases.mjs:41 rejectsField`). This one lives in the shared library only
 * because K10 and K11 both need it, and two copies of a negative assertion is how one of
 * them ends up asserting something weaker.
 *
 * Why the discriminant is required rather than "any refusal": the three refusals mean
 * different things — `unmatched` is nothing declared, `ambiguous` is two declarations that
 * disagree, `sealBroken` is ARCH-004. A helper that accepted any of them would pass a
 * mutation that turned a real ambiguity into an unmatched request, which is precisely the
 * substitution K11 class 2 exists to catch.
 *
 * A THROW is a failure, not a pass. The forest's contract is that a refusal is a returned
 * value; an exception means a checker crashed on the way to deciding.
 */
export function rejectsSelect(runtime, body, expectedDiscriminant) {
  if (!SELECT_DISCRIMINANTS.includes(expectedDiscriminant)) {
    throw new Error(
      `rejectsSelect: '${expectedDiscriminant}' is not a refusal shape; ` +
        `select returns one of ${SELECT_DISCRIMINANTS.join(', ')}`,
    );
  }

  const selection = runtime.select(body, contextOf(body.sessionID));
  const present = SELECT_DISCRIMINANTS.filter((name) => selection[name] !== undefined);

  if (present.length === 1 && present[0] === expectedDiscriminant) return selection;

  const observed =
    present.length > 0
      ? present.join('+')
      : selection.fault !== undefined
        ? `fault at ${selection.entry.id} attempt ${selection.attempt}`
        : `delivered ${selection.entry?.id ?? '<no entry>'}`;

  throw new Error(`select must return { ${expectedDiscriminant} }, got ${observed}`);
}

const isThenable = (value) => value !== null && typeof value?.then === 'function';

/**
 * Replace one property of a mutable carrier for the duration of `body`, and restore it
 * even when `body` throws.
 *
 * `carrier` is a class prototype or an ordinary object — NOT an ES module namespace, which
 * this refuses with the measurement in the file header. The patch is verified by reading
 * the value back, because the only descriptor a namespace exposes says `writable: true`
 * and would otherwise be believed.
 *
 * Restoration is in a `finally` so a thrown assertion cannot leak a mutation into the next
 * case. A leaked mutation is worse than a failed case: the cases that follow run against a
 * wrong implementation and their greens mean nothing.
 *
 * `body` must be SYNCHRONOUS, and an async one is rejected rather than awaited. Every
 * checker in the forest is a pure function, so nothing here needs to await — and an
 * awaited-but-unrestored window is not the risk. The risk is the opposite: restoring when
 * the promise is created rather than when it settles would run the whole assertion against
 * the ORIGINAL implementation and report green for a mutation that was never in force.
 * Refusing states the contract instead of assuming it.
 */
export function withPatched(carrier, propertyName, replacement, body) {
  if (Object.prototype.toString.call(carrier) === '[object Module]') {
    throw new Error(
      `withPatched cannot patch the ES module namespace member '${propertyName}': its ` +
        '[[Set]] always fails and its descriptor claims writable: true. Patch a class ' +
        'prototype or an object, or mutate the INPUT the module is given',
    );
  }

  const descriptor = Object.getOwnPropertyDescriptor(carrier, propertyName);
  if (descriptor === undefined) {
    throw new Error(`withPatched: '${propertyName}' is not an own property of the carrier`);
  }
  if (descriptor.configurable !== true) {
    throw new Error(`withPatched: '${propertyName}' is not configurable, so it cannot be restored`);
  }

  const original = descriptor.value;
  Object.defineProperty(carrier, propertyName, { ...descriptor, value: replacement });

  if (carrier[propertyName] !== replacement) {
    Object.defineProperty(carrier, propertyName, descriptor);
    throw new Error(`withPatched: '${propertyName}' did not take the replacement, so no mutation was applied`);
  }

  let outcome;
  try {
    outcome = body();
  } finally {
    Object.defineProperty(carrier, propertyName, { ...descriptor, value: original });
  }

  if (isThenable(outcome)) {
    throw new Error(
      `withPatched body for '${propertyName}' returned a thenable; the patch was already ` +
        'restored, so an awaited assertion would run against the original implementation',
    );
  }

  return outcome;
}
