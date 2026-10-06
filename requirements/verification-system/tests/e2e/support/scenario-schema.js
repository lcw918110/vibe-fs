/**
 * scenario-schema.js — TOML scenario source → the runtime structures K2-K4 consume.
 *
 * verification-system-001. Two layers, deliberately not the same shape:
 *
 *   source     a conversation a human reads top to bottom
 *   compiled   (lane, turn, step) entries a machine looks up
 *
 * `design-script-forest.md` §9 argues the prefix index must never be the written
 * form: a hand-written prefix array repeats every earlier turn on every edge, so
 * adding one step means editing all downstream edges and the reader cannot see a
 * conversation at all. Same principle as production — SSOT clauses are prose, `Fold`
 * is the machine projection, and neither borrows the other's form.
 *
 * ── one deviation from the design document ──────────────────────────────────
 *
 * §10's example writes `step = "fork-agent"` inside `[[fault]]`, i.e. a step NAME.
 * Package K2 then established that the runtime `step` is an integer — the count of
 * assistant messages after the last user message, which is what makes the cursor
 * unnecessary.
 *
 * Both are right at their own layer, so the compiler resolves one into the other:
 * an author writes `turn = "manager"` and `step = "fork-agent"`, and the output
 * carries the integer. That is also what makes dangling-reference detection
 * possible — a name can be checked against the declared set, an integer cannot.
 */

import { parse as parseToml } from 'smol-toml';
import { retiredFieldProblems } from './legacy-fields.js';
import { turnFragments } from './runtime-key.js';
import { validateFault } from './delivery-plan.js';
import { eventCeilingSetupProblems } from './event-ceiling.js';

// ── the TOML root-key trap ──────────────────────────────────────────────────

const ROOT_KEYS = ['scenario', 'description', 'must', 'flow', 'setup', 'session', 'pass', 'prompt'];

/**
 * Collections consumed by the compiler must retain their TOML array shape.
 *
 * TOML permits a root table (`turn = {}`) where the scenario grammar expects an
 * array of tables (`[[turn]]`). Validate that distinction before downstream
 * validators iterate the collection, so invalid source fails closed rather than
 * escaping as a JavaScript array-method error.
 */
const collectionShapeProblems = (raw) => {
  const problems = [];
  const tableCollections = ['flow', 'turn', 'fault', 'epoch'];

  for (const field of tableCollections) {
    const value = raw[field];
    if (value === undefined) continue;
    if (!Array.isArray(value)) {
      problems.push(`${field} must be an array of tables`);
      continue;
    }
    value.forEach((entry, index) => {
      if (entry === null || typeof entry !== 'object' || Array.isArray(entry)) {
        problems.push(`${field}[${index}] must be a table`);
      }
    });
  }

  if (raw.must !== undefined) {
    if (!Array.isArray(raw.must)) {
      problems.push('must must be an array of signal identifiers');
    } else {
      raw.must.forEach((id, index) => {
        if (typeof id !== 'string' || id === '') {
          problems.push(`must[${index}] must be a non-empty signal identifier`);
        }
      });
    }
  }

  return problems;
};

/**
 * Flow verbs, measured from the 19 JSON scenarios rather than invented.
 *
 * A whitelist because `wait`'s dangling reference is checked but its NAME was not: a
 * typo'd `awaitTerminl` is silently ignored, and the author believes a barrier is in
 * force. Same protection as the retired-field rejector, one layer up.
 *
 * `loadScripts` is deliberately absent — the retired-field rejector owns it (§8).
 */
const FLOW_VERBS = new Set([
  'wait',
  'attempts',
  'waitAny',
  'waitFact',
  'armIdle',
  'awaitIdle',
  'prompt',
  'session',
  'lane',
  'timeoutMs',
  'awaitTerminal',
  'requireAssistantTerminal',
  'awaitEvent',
  'awaitRestart',
  'restart',
  'abort',
  'bindChild',
  'createChild',
  'createSession',
  'expectSatisfied',
  'requireIdleAfterActivity',
  'assertFacts',
  'assertFile',
  'assertDeliveries',
  'assertActiveRequests',
  'assertWorktreeClean',
  'assertPtyEcho',
  'assertModelTrajectory',
  'afterExpectation',
  'custom',
]);

const unknownFlowVerbs = (flow) =>
  (flow ?? []).flatMap((flowStep, index) =>
    Object.keys(flowStep ?? {})
      .filter((verb) => !FLOW_VERBS.has(verb))
      .map((verb) => `flow[${index}]: unknown verb '${verb}'; a misspelled verb is silently ignored`),
  );

const waitAttemptsProblems = (flow) =>
  (flow ?? []).flatMap((flowStep, index) => {
    if (flowStep?.attempts === undefined) return [];
    if (flowStep.wait === undefined) return [`flow[${index}] attempts is only valid with wait`];
    if (!Number.isInteger(flowStep.attempts) || flowStep.attempts < 1) {
      return [`flow[${index}] attempts must be a positive integer`];
    }
    return [];
  });

const waitAnyProblems = (flow) =>
  (flow ?? []).flatMap((flowStep, index) => {
    const alternatives = flowStep?.waitAny;
    if (alternatives === undefined) return [];
    if (!Array.isArray(alternatives)) return [`flow[${index}] waitAny must be an array`];

    const problems = [];
    if (alternatives.length < 2) {
      problems.push(`flow[${index}] waitAny requires at least two alternatives`);
    }
    if (alternatives.some((id) => typeof id !== 'string' || id.trim() === '')) {
      problems.push(`flow[${index}] waitAny alternatives must be non-blank signal identifiers`);
    }
    if (new Set(alternatives).size !== alternatives.length) {
      problems.push(`flow[${index}] waitAny alternatives must be unique`);
    }
    return problems;
  });

const BIND_CHILD_KEYS = new Set(['agent', 'bind']);

const bindChildProblems = (flow) =>
  (flow ?? []).flatMap((flowStep, index) => {
    const binding = flowStep?.bindChild;
    if (binding === undefined) return [];
    if (binding === null || typeof binding !== 'object' || Array.isArray(binding)) {
      return [`flow[${index}] bindChild must be a table`];
    }

    const problems = [];
    if (typeof binding.agent !== 'string' || binding.agent.trim() === '') {
      problems.push(`flow[${index}] bindChild requires an exact agent`);
    }
    for (const field of Object.keys(binding)) {
      if (!BIND_CHILD_KEYS.has(field)) {
        problems.push(`flow[${index}] bindChild field '${field}' is unsupported`);
      }
    }
    return problems;
  });

const AWAIT_IDLE_KEYS = new Set(['after', 'arm', 'attempts', 'armAttempts', 'session']);

const awaitIdleProblems = (flow) =>
  (flow ?? []).flatMap((flowStep, index) => {
    const idle = flowStep?.awaitIdle;
    if (idle === undefined) return [];
    if (idle === null || typeof idle !== 'object' || Array.isArray(idle)) {
      return [`flow[${index}] awaitIdle must be a table`];
    }

    const problems = [];
    if (typeof idle.after !== 'string' || idle.after.trim() === '') {
      problems.push(`flow[${index}] awaitIdle requires an exact provider expectation id`);
    }
    if (idle.arm !== undefined && (typeof idle.arm !== 'string' || idle.arm.trim() === '')) {
      problems.push(`flow[${index}] awaitIdle arm must be an exact provider expectation id`);
    }
    if (idle.attempts !== undefined && (!Number.isInteger(idle.attempts) || idle.attempts < 1)) {
      problems.push(`flow[${index}] awaitIdle attempts must be a positive integer`);
    }
    if (idle.armAttempts !== undefined && (!Number.isInteger(idle.armAttempts) || idle.armAttempts < 1)) {
      problems.push(`flow[${index}] awaitIdle armAttempts must be a positive integer`);
    }
    if (idle.session !== undefined && (typeof idle.session !== 'string' || idle.session.trim() === '')) {
      problems.push(`flow[${index}] awaitIdle session must be a non-empty session selector`);
    }
    for (const field of Object.keys(idle)) {
      if (!AWAIT_IDLE_KEYS.has(field)) {
        problems.push(`flow[${index}] awaitIdle field '${field}' is unsupported`);
      }
    }
    return problems;
  });

const ARM_IDLE_KEYS = new Set(['id', 'attempts']);

const armIdleProblems = (flow) =>
  (flow ?? []).flatMap((flowStep, index) => {
    const arm = flowStep?.armIdle;
    if (arm === undefined) return [];
    if (arm === null || typeof arm !== 'object' || Array.isArray(arm)) {
      return [`flow[${index}] armIdle must be a table`];
    }

    const problems = [];
    if (typeof arm.id !== 'string' || arm.id.trim() === '') {
      problems.push(`flow[${index}] armIdle requires an exact provider expectation id`);
    }
    if (arm.attempts !== undefined && (!Number.isInteger(arm.attempts) || arm.attempts < 1)) {
      problems.push(`flow[${index}] armIdle attempts must be a positive integer`);
    }
    for (const field of Object.keys(arm)) {
      if (!ARM_IDLE_KEYS.has(field)) {
        problems.push(`flow[${index}] armIdle field '${field}' is unsupported`);
      }
    }
    return problems;
  });

const AFTER_EXPECTATION_KEYS = new Set(['id', 'attempts', 'restart', 'gitConflictProof', 'file']);

const afterExpectationProblems = (flow) =>
  (flow ?? []).flatMap((flowStep, index) => {
    const hook = flowStep?.afterExpectation;
    if (hook === undefined) return [];
    if (hook === null || typeof hook !== 'object' || Array.isArray(hook)) {
      return [`flow[${index}] afterExpectation must be a table`];
    }

    const problems = [];
    if (typeof hook.id !== 'string' || hook.id.trim() === '') {
      problems.push(`flow[${index}] afterExpectation requires an exact id`);
    }
    if (hook.attempts !== undefined && (!Number.isInteger(hook.attempts) || hook.attempts < 1)) {
      problems.push(`flow[${index}] afterExpectation attempts must be a positive integer`);
    }
    for (const field of Object.keys(hook)) {
      if (!AFTER_EXPECTATION_KEYS.has(field)) {
        problems.push(`flow[${index}] afterExpectation field '${field}' is unsupported`);
      }
    }
    return problems;
  });

/**
 * `waitFact.renewOn` — the causal intermediate facts that renew the barrier
 * (verification-system-006 / waitfact-causal-renewal). An empty or absent list is a simple
 * barrier that only follows the target. Rejected: non-array, non-string entries,
 * empty strings, duplicates, or an entry repeating the awaited `name` (a
 * fact that renews itself is the old any-append bug in a costume).
 */
const waitFactRenewOnProblems = (flow) =>
  (flow ?? []).flatMap((flowStep, index) => {
    const wf = flowStep?.waitFact;
    if (wf === undefined) return [];
    if (wf === null || typeof wf !== 'object' || Array.isArray(wf)) {
      return [`flow[${index}] waitFact must be a table`];
    }
    if (wf.renewOn === undefined) return [];

    const problems = [];
    if (!Array.isArray(wf.renewOn) || wf.renewOn.some((entry) => typeof entry !== 'string')) {
      problems.push(`flow[${index}] renewOn must be an array of fact names`);
      return problems;
    }
    if (wf.renewOn.some((entry) => entry.trim() === '')) {
      problems.push(`flow[${index}] renewOn entries must be non-empty strings`);
    }
    if (new Set(wf.renewOn).size !== wf.renewOn.length) {
      problems.push(`flow[${index}] renewOn entries must be unique`);
    }
    if (typeof wf.name === 'string' && wf.renewOn.includes(wf.name)) {
      problems.push(`flow[${index}] renewOn must not contain the target fact`);
    }
    return problems;
  });

const ASSERT_DELIVERIES_KEYS = new Set(['id', 'eq', 'gte', 'lte']);

const assertDeliveriesProblems = (flow) =>
  (flow ?? []).flatMap((flowStep, index) => {
    const claim = flowStep?.assertDeliveries;
    if (claim === undefined) return [];
    if (claim === null || typeof claim !== 'object' || Array.isArray(claim)) {
      return [`flow[${index}] assertDeliveries must be a table`];
    }

    const problems = [];
    if (typeof claim.id !== 'string' || claim.id.trim() === '') {
      problems.push(`flow[${index}] assertDeliveries requires an exact id`);
    }
    if (claim.eq === undefined && claim.gte === undefined && claim.lte === undefined) {
      problems.push(`flow[${index}] assertDeliveries requires eq, gte, or lte`);
    }
    for (const bound of ['eq', 'gte', 'lte']) {
      const value = claim[bound];
      if (value !== undefined && (!Number.isInteger(value) || value < 0)) {
        problems.push(`flow[${index}] assertDeliveries ${bound} must be a non-negative integer`);
      }
    }
    if (Number.isInteger(claim.gte) && Number.isInteger(claim.lte) && claim.gte > claim.lte) {
      problems.push(`flow[${index}] assertDeliveries lower bound exceeds upper bound`);
    }
    for (const field of Object.keys(claim)) {
      if (!ASSERT_DELIVERIES_KEYS.has(field)) {
        problems.push(`flow[${index}] assertDeliveries field '${field}' is unsupported`);
      }
    }
    return problems;
  });

/**
 * TOML assigns a root-level key to the LAST table header above it, silently.
 *
 * Measured: `flow = [...]` placed after `[[epoch]]` parses as `epoch[0].flow` with no
 * error. The parsed object cannot reveal this — `epoch[0].flow` is indistinguishable
 * from a `flow` key the author meant to put inside that table — so the check has to
 * read the source text, before and independently of parsing.
 */
export function rootKeyOrderProblems(source) {
  const problems = [];
  let seenHeader = null;

  source.split('\n').forEach((line, index) => {
    const text = line.trim();
    if (text === '' || text.startsWith('#')) return;

    if (text.startsWith('[')) {
      seenHeader = text;
      return;
    }

    const key = text.match(/^([A-Za-z_][\w-]*)\s*=/)?.[1];
    if (key !== undefined && ROOT_KEYS.includes(key) && seenHeader !== null) {
      problems.push(
        `line ${index + 1}: root key '${key}' appears after ${seenHeader}; ` +
          `TOML would silently nest it inside that table`,
      );
    }
  });

  return problems;
}

// ── compilation ─────────────────────────────────────────────────────────────

/**
 * `user` is either a prefix or an ordered fragment list.
 *
 * The list form exists for one measured reason (`runtime-key.js` documents it): production
 * WRAPS a forked Reviewer's assignment, so the text a scenario knows sits at the end of the
 * request. Two fragments is the minimum that can say "starts with the wrapper, and later
 * contains this".
 *
 * A one-element list is rejected because it is a prefix wearing a costume — the reader would
 * have to compare bracket shapes to see which rule applies.
 */
const userTextProblems = (turn, index) => {
  const user = turn.user;

  if (typeof user === 'string') {
    return user === '' ? [`turn[${index}] needs user text`] : [];
  }
  if (!Array.isArray(user)) return [`turn[${index}] needs user text`];

  if (user.length < 2) {
    return [`turn[${index}] a one-fragment user list is just a prefix; write the string`];
  }
  if (user.some((fragment) => typeof fragment !== 'string' || fragment === '')) {
    return [`turn[${index}] every user fragment must be non-empty text`];
  }
  return [];
};

/** A step's position within its turn is its runtime `step`, unless `runtimeStep` declares the measured cursor explicitly. */
const compileTurns = (turns) =>
  turns.flatMap((turn, turnIndex) =>
    (turn.step ?? []).map((step, stepIndex) => ({
      id: step.id ?? `${turn.id}.${stepIndex}`,
      turnId: turn.id,
      turnIndex,
      lane: turn.lane,
      internal: turn.internal === true,
      // A race path may or may not be reached on a given run (e.g. a restart
      // window where the pre-crash tool already completed). Declared so a
      // request CAN be answered, but its absence is not an error. Declarable on
      // the turn when the request may arrive against ANY step cursor of the
      // lane: naming one step would claim alignment the scenario cannot know.
      optional: turn.optional === true || step.optional === true,
      kind: turn.kind ?? 'chat',
      turn: turn.user,
      step: step.runtimeStep ?? stepIndex,
      tools: turn.tools ?? [],
      forbiddenTools: turn.forbiddenTools ?? [],
      parentLane: turn.parentLane ?? undefined,
      respond: step.respond,
    })),
  );

/** Resolve `turn`+`step` names against the compiled entries. */
const resolveReference = (entries, reference) => {
  const byTurn = entries.filter((entry) => entry.turnId === reference.turn);
  if (byTurn.length === 0) return { missingTurn: reference.turn };

  if (reference.step === undefined) return { entry: byTurn[0] };

  const byStep =
    typeof reference.step === 'number'
      ? byTurn.filter((entry) => entry.step === reference.step)
      : byTurn.filter((entry) => entry.id === reference.step || entry.id.endsWith(`.${reference.step}`));

  return byStep.length === 0 ? { missingStep: reference.step } : { entry: byStep[0] };
};

/**
 * Which declared turns a flow can actually reach.
 *
 * Two ways in, and they are the only two the wire supports:
 *
 *   a `flow.prompt` sends the turn's user text
 *   an earlier step's tool call carries it as `args.charge` (or legacy `args.prompt`)
 *
 * The second is how child turns exist at all — `fork(agent, prompt)` is what creates
 * the session that will receive them. Both are prefix comparisons because a scenario
 * declares a distinctive fragment, not the whole utterance.
 *
 * This is the one check that requires the static whole. It is also the one most
 * likely to be too strict; package K8 converts the real scenarios and will say.
 */
export function reachableTurnIds(turns, entries, { flow, prompt, setup } = {}) {
  // Reached by a scenario prompt, or by a reachable step's tool-call prompt. Fixpoint,
  // not one pass, because forks chain: Manager → Coder → the Coder's own children.
  //
  // `internal = true` opts a turn out. Production composes those prompts itself, so no
  // scenario text can reach them and the check would have no evidence either way. Three
  // measured sites, and the third is not a child session at all:
  //
  //   `CompanionHostBlogger.fs:72,77,118`  Blogger child, prompt built from the delta
  //   `ExecutorSummarize.fs:95`            map child, prompt built per output chunk
  //   `TurnCompletionProgram.fs:92`        continuation, SAME lane, new user message
  //
  // That last one is why this is a per-TURN flag rather than a per-lane one.
  const scenarioPrompts = [
    prompt?.text,
    ...(flow ?? []).flatMap((flowStep) => [flowStep.prompt?.text, flowStep.prompt?.resumeAfterIdleText]),
  ].filter((text) => typeof text === 'string');
  const preFlowTurnIds = new Set(
    Array.isArray(setup?.preFlowTurns) ? setup.preFlowTurns.filter((id) => typeof id === 'string') : [],
  );

  // For a fragment declaration the scenario-visible part is the LAST fragment: the earlier
  // ones are production's wrapper, which no scenario text can carry. Comparing against the
  // wrapper would make every such turn trivially unreachable.
  const declaredText = (turn) => turnFragments(turn.user).at(-1);

  const reachedBy = (texts, turn) => {
    // fork child sessions declare the wire form ("# <assignment>"), but the matching tool prompt
    // is the naked form ("<assignment>"); normalize the leading comment marker away before comparing.
    let declared = declaredText(turn);
    if (declared.startsWith('# ')) {
      declared = declared.slice(2);
    } else if (declared.startsWith('#')) {
      declared = declared.slice(1);
    }
    return texts.some((text) => text.startsWith(declared) || declared.startsWith(text));
  };

  const reachableTurns = new Set(
    turns.filter(
      (turn) => turn.internal === true || preFlowTurnIds.has(turn.id) || reachedBy(scenarioPrompts, turn),
    ),
  );

  for (;;) {
    const before = reachableTurns.size;

    const toolPrompts = entries
      .filter((entry) => [...reachableTurns].some((turn) => turn.id === entry.turnId))
      .flatMap((entry) => [entry.respond?.args?.charge, entry.respond?.args?.prompt])
      .filter((text) => typeof text === 'string');

    for (const turn of turns) {
      if (!reachableTurns.has(turn) && reachedBy(toolPrompts, turn)) reachableTurns.add(turn);
    }

    if (reachableTurns.size === before) break;
  }

  return new Set([...reachableTurns].map((turn) => turn.id));
}

// ── the load-time validations ───────────────────────────────────────────────

const responseDigest = (respond) => JSON.stringify(respond ?? null);

/**
 * One (lane, turn, step) declared twice. Replaces `specificity`.
 *
 * The design document lists "conflicting responses" and "ambiguous prefixes" as two
 * checks. Under (lane, turn, step) keying they are one: two declarations for one key
 * either disagree about the response or duplicate it, so a separate conflict check
 * would report the same pair twice with a weaker message.
 *
 * DEVIATION: the design document collapses identical templates. That was a mitigation
 * for predicate-conjunction matching, where template reuse produced duplicates
 * naturally. Under this keying a recurring nudge has several steps, so a true
 * duplicate is debris — both cases reject, and the message says which fix applies.
 *
 */
const signalIdentifierCollisions = (turns, entries) => {
  const owners = new Map();

  turns.forEach((turn, index) => {
    const claims = owners.get(turn.id) ?? [];
    claims.push(`turn[${index}]`);
    owners.set(turn.id, claims);
  });
  entries.forEach((entry) => {
    const claims = owners.get(entry.id) ?? [];
    claims.push(`turn[${entry.turnIndex}].step[${entry.step}]`);
    owners.set(entry.id, claims);
  });

  return [...owners]
    .filter(([, claims]) => claims.length > 1)
    .map(
      ([id, claims]) =>
        `signal id '${id}' is claimed by ${claims.join(' and ')}; ` +
        'turn and step waits share one signal namespace',
    );
};

const duplicateDeclarations = (entries) => {
  const problems = [];

  // `turn` may be an array, so compare a digest: two distinct arrays with the same contents
  // are the same declaration and `!==` would not say so.
  const turnDigest = (entry) => JSON.stringify(turnFragments(entry.turn));
  // toolsGate (runtime-key) distinguishes declarations by required/forbidden tools;
  // compile-time must use the same identity or optional "#" repairs for manager vs
  // blogger (different tool surfaces, same bare user text) falsely collide.
  const toolsDigest = (entry) =>
    JSON.stringify({ tools: entry.tools ?? [], forbiddenTools: entry.forbiddenTools ?? [] });

  for (const left of entries) {
    for (const right of entries) {
      if (left.lane !== right.lane || left.step !== right.step) continue;
      if (turnDigest(left) !== turnDigest(right)) continue;
      if (toolsDigest(left) !== toolsDigest(right)) continue;
      if (left.kind !== right.kind) continue;
      if (left.parentLane !== right.parentLane) continue;
      if (left.id >= right.id) continue;

      problems.push(
        responseDigest(left.respond) === responseDigest(right.respond)
          ? `${left.id} and ${right.id} declare the same (lane, turn, step) with the same response; delete one`
          : `${left.id} and ${right.id} declare the same (lane, turn, step) with different responses; ` +
            'the scenario does not say what the model does next',
      );
    }
  }

  return problems;
};

/**
 * A fault declaration that is malformed rather than merely misplaced.
 *
 * `validateFault` had eight callers in the gate and none here, so until now a real
 * scenario could declare `attempts = []` — a fault that never fires — and load clean.
 * Exactly the zero-call-site shape `architecture-gate`'s `single-constructor` check
 * exists to catch one layer down.
 */
const malformedFaults = (scenario) =>
  (scenario.fault ?? []).flatMap((fault, index) =>
    validateFault({ ...fault, kind: fault.delivery ?? fault.kind }).map(
      (problem) => `fault[${index}]: ${problem}`,
    ),
  );

/**
 * A `provider-error` fault must say whether the Host should retry it.
 *
 * This is the load-bearing bit of a provider-recovery scenario and it is invisible in the
 * response body: a retryable 500 means the HOST drives the retries, a non-retryable 400
 * means the Host gives up and the plugin must send a continuation (PAR-019). Get it
 * wrong and the scenario still runs, just proving a different clause than its author
 * believes. So it is required rather than defaulted.
 */
const providerErrorProblems = (scenario) =>
  (scenario.fault ?? []).flatMap((fault, index) => {
    if ((fault.delivery ?? fault.kind) !== 'provider-error') return [];

    const problems = [];
    if (!Number.isInteger(fault.status)) {
      problems.push(`fault[${index}]: a provider-error must declare the HTTP status it returns`);
    }
    if (typeof fault.retryable !== 'boolean') {
      problems.push(
        `fault[${index}]: a provider-error must declare retryable; ` +
          'it decides whether the Host retries or the plugin continues the Logical Run (PAR-019)',
      );
    }
    return problems;
  });

/**
 * Two faults governing one (lane, turn, step).
 *
 * `faultFor` already throws on this, but it throws at DELIVERY — so the author finds out
 * mid-run, in whichever scenario reached that step first, with the Host already up. The
 * static whole is available at load time; there is no reason to wait.
 */
const conflictingFaults = (entries, scenario) => {
  const seen = new Map();
  const problems = [];

  (scenario.fault ?? []).forEach((fault, index) => {
    const { entry } = resolveReference(entries, fault);
    if (entry === undefined) return;

    const key = `${entry.lane ?? ''}\u001f${entry.turn}\u001f${entry.step}`;
    const earlier = seen.get(key);

    if (earlier === undefined) seen.set(key, index);
    else {
      problems.push(
        `fault[${earlier}] and fault[${index}]: two faults declared for the same ` +
          `(lane, turn, step); one delivery cannot fail two ways`,
      );
    }
  });

  return problems;
};

/** An `assertModelTrajectory` naming a lane no turn declares. */
const trajectoryProblems = (entries, scenario) =>
  (scenario.flow ?? []).flatMap((flowStep, index) => {
    const claim = flowStep.assertModelTrajectory;
    if (claim === undefined) return [];
    if (claim === null || typeof claim !== 'object' || Array.isArray(claim)) {
      return [`flow[${index}] assertModelTrajectory must be a table`];
    }

    const problems = [];
    if (!entries.some((entry) => entry.lane === claim.lane)) {
      problems.push(`flow[${index}] assertModelTrajectory references lane '${claim.lane}', which no turn declares`);
    }
    if (!Array.isArray(claim.models) || claim.models.length === 0) {
      problems.push(`flow[${index}] assertModelTrajectory needs the exact expected model sequence`);
    }
    return problems;
  });

/** 3-5. A fault, cold boundary or `must` naming something that does not exist. */
const danglingReferences = (entries, scenario) => {
  const problems = [];

  const check = (label, reference) => {
    const resolved = resolveReference(entries, reference);
    if (resolved.missingTurn !== undefined) {
      problems.push(`${label} references turn '${resolved.missingTurn}', which is not declared`);
    } else if (resolved.missingStep !== undefined) {
      problems.push(`${label} references step '${resolved.missingStep}' of turn '${reference.turn}', which is not declared`);
    }
    return resolved;
  };

  for (const fault of scenario.fault ?? []) check(`fault (${fault.delivery ?? fault.kind})`, fault);
  for (const boundary of scenario.epoch ?? []) check(`cold boundary (${boundary.reason ?? boundary.kind})`, boundary);

  for (const id of scenario.must ?? []) {
    const targets = entries.filter((entry) => entry.id === id || entry.turnId === id);
    if (targets.length === 0) {
      problems.push(`must references '${id}', which is not a declared step or turn`);
    } else if (targets.some((entry) => entry.optional === true)) {
      // `must` means "this MUST be reached"; `optional` means "absence is fine".
      // Naming an optional step (or a turn containing one) in must is
      // contradictory: either the step is required (drop optional) or it may
      // be skipped (drop must).
      problems.push(`must references '${id}', which is or contains an optional step — a must requirement contradicts optional`);
    }
  }

  for (const flowStep of scenario.flow ?? []) {
    const waited = flowStep.wait;
    if (typeof waited === 'string' && !entries.some((entry) => entry.id === waited || entry.turnId === waited)) {
      problems.push(`flow wait references '${waited}', which is not a declared step or turn`);
    }

    for (const alternative of Array.isArray(flowStep.waitAny) ? flowStep.waitAny : []) {
      if (!entries.some((entry) => entry.id === alternative || entry.turnId === alternative)) {
        problems.push(`flow waitAny references '${alternative}', which is not a declared step or turn`);
      }
    }

    const idleAfter = flowStep.awaitIdle?.after;
    if (typeof idleAfter === 'string' && !entries.some((entry) => entry.id === idleAfter || entry.turnId === idleAfter)) {
      problems.push(`flow awaitIdle references '${idleAfter}', which is not a declared step or turn`);
    }

    const idleArm = flowStep.awaitIdle?.arm;
    if (typeof idleArm === 'string' && !entries.some((entry) => entry.id === idleArm || entry.turnId === idleArm)) {
      problems.push(`flow awaitIdle arm references '${idleArm}', which is not a declared step or turn`);
    }

    const armedIdle = flowStep.armIdle?.id;
    if (typeof armedIdle === 'string' && !entries.some((entry) => entry.id === armedIdle || entry.turnId === armedIdle)) {
      problems.push(`flow armIdle references '${armedIdle}', which is not a declared step or turn`);
    }

    const asserted = flowStep.assertDeliveries?.id;
    if (typeof asserted === 'string' && !entries.some((entry) => entry.id === asserted)) {
      problems.push(`assertDeliveries references '${asserted}', which is not a declared step`);
    }
  }

  return problems;
};

/** 6. A declared step no flow can reach. */
const deadEdges = (turns, entries, scenario) => {
  const reachable = reachableTurnIds(turns, entries, scenario);

  return turns
    .filter((turn) => !reachable.has(turn.id))
    .map((turn) => `turn '${turn.id}' is declared but no flow prompt or tool call reaches it (dead edge)`);
};

// ── the entry point ─────────────────────────────────────────────────────────

/**
 * Compile one scenario source.
 *
 * Returns `{ ok: true, scenario }` or `{ ok: false, problems }`. Never a partial
 * scenario with warnings: a scenario that half-loads is a scenario whose author
 * believes something is covered that is not.
 */
export function compileScenario(source, { name = '<inline>' } = {}) {
  const orderProblems = rootKeyOrderProblems(source);
  if (orderProblems.length > 0) {
    return { ok: false, problems: orderProblems.map((problem) => `${name}: ${problem}`) };
  }

  let raw;
  try {
    raw = parseToml(source);
  } catch (error) {
    return { ok: false, problems: [`${name}: TOML parse failed: ${error.message}`] };
  }

  const shapeProblems = collectionShapeProblems(raw);
  if (shapeProblems.length > 0) {
    return { ok: false, problems: shapeProblems.map((problem) => `${name}: ${problem}`) };
  }

  const retired = retiredFieldProblems(raw);
  if (retired.length > 0) {
    return { ok: false, problems: retired.map((problem) => `${name}: ${problem}`) };
  }

  const problems = [];
  if (typeof raw.scenario !== 'string' || raw.scenario === '') problems.push('scenario name is required');

  const turns = raw.turn ?? [];

  // A scenario with no turns describes no provider behaviour at all. It loaded clean
  // until now, and that is precisely the shape `loadScripts` needed:
  // `host-restart-after.json` was `{ "scenario": "host-restart" }` plus three edges — a
  // fragment that named a scenario it was not, could not run alone, and had no way to
  // say so. With one static file per scenario there is no such thing as a fragment.
  if (turns.length === 0) {
    problems.push('a scenario declares at least one turn; a file with none describes no provider behaviour');
  }

  turns.forEach((turn, index) => {
    if (typeof turn.id !== 'string' || turn.id === '') problems.push(`turn[${index}] needs an id`);
    problems.push(...userTextProblems(turn, index));
    const steps = Array.isArray(turn.step) ? turn.step : [];
    if (steps.length === 0) problems.push(`turn[${index}] needs at least one step`);
    if (turn.kind !== undefined && turn.kind !== 'chat' && turn.kind !== 'title') {
      problems.push(`turn[${index}] kind '${turn.kind}' is not chat or title`);
    }
    if (turn.internal !== undefined && turn.internal !== true) {
      problems.push(`turn[${index}] internal must be true when present; omit it otherwise`);
    }
    if (turn.optional !== undefined && turn.optional !== true) {
      problems.push(`turn[${index}] optional must be true when present; omit it otherwise`);
    }
    // `tools` and `forbiddenTools` are live wire assertions (runtime-key.js toolsGate):
    // every declared tool must be present on the request, every forbidden one absent.
    for (const field of ['tools', 'forbiddenTools']) {
      const declared = turn[field];
      if (declared === undefined) continue;
      if (!Array.isArray(declared) || declared.some((name) => typeof name !== 'string' || name === '')) {
        problems.push(`turn[${index}] ${field} must be an array of non-empty tool names`);
      } else if (new Set(declared).size !== declared.length) {
        problems.push(`turn[${index}] ${field} entries must be unique`);
      }
    }
    if (Array.isArray(turn.tools) && Array.isArray(turn.forbiddenTools)) {
      const overlap = turn.tools.filter((name) => turn.forbiddenTools.includes(name));
      if (overlap.length > 0) {
        problems.push(`turn[${index}] tool '${overlap[0]}' is both required and forbidden`);
      }
    }
    const runtimeSteps = new Map();
    steps.forEach((step, stepIndex) => {
      if (step === null || typeof step !== 'object' || Array.isArray(step)) {
        problems.push(`turn[${index}].step[${stepIndex}] must be a table`);
        return;
      }
      const runtimeStep = step.runtimeStep ?? stepIndex;
      if (!Number.isInteger(runtimeStep) || runtimeStep < 0) {
        problems.push(`turn[${index}].step[${stepIndex}] runtimeStep must be a non-negative integer`);
      } else {
        const earlier = runtimeSteps.get(runtimeStep);
        if (earlier !== undefined) {
          problems.push(
            `turn[${index}].step[${stepIndex}] runtimeStep ${runtimeStep} duplicates ` +
              `turn[${index}].step[${earlier}] within the same turn`,
          );
        } else {
          runtimeSteps.set(runtimeStep, stepIndex);
        }
      }
      if (step.optional !== undefined && step.optional !== true) {
        problems.push(`turn[${index}].step[${stepIndex}] optional must be true when present; omit it otherwise`);
      }
      // A race tail is terminal for the turn's requirement surface: every step
      // AFTER an optional one is only reachable when the race fired, so a
      // required step there would silently depend on the race. Declare the
      // tail as optional too, or move the required step before the race.
      const previous = steps[stepIndex - 1];
      if (previous?.optional === true && step.optional !== true) {
        problems.push(
          `turn[${index}].step[${stepIndex}] follows an optional step but is not optional; ` +
            'a required step after a race tail would depend on the race firing',
        );
      }
    });
    // An internal turn's prompt is composed by production, which decides WHICH session
    // receives it. Pinning it to one alias claims knowledge the scenario does not have.
    //
    // Measured in K9, three times, each looking like an unrelated conversion bug:
    //
    //   a Companion prompt is identical across sessions, so a lane-bound blogger turn
    //   answered one work session out of six (`CompanionHostBlogger.fs:77`)
    //
    //   `Submit a structured verdict…` (`HostReviewGuard.fs:164`) arrived at the Reviewer
    //   the Manager had FORKED, not at the session the scenario created for that purpose
    //
    //   `Review is required before completion.` reaches whichever Manager tried to finish
    //
    // The turn text is the identity in every case — production composes one exact sentence
    // per situation. A lane adds nothing and subtracts sessions.
    if (turn.internal === true && turn.lane !== undefined) {
      problems.push(
        `turn[${index}] '${turn.id}' is internal, so it may not declare a lane: ` +
          'production decides which session receives a prompt it composed',
      );
    }
  });

  if (problems.length > 0) {
    return { ok: false, problems: problems.map((problem) => `${name}: ${problem}`) };
  }

  const entries = compileTurns(turns);

  const validationProblems = [
    ...unknownFlowVerbs(raw.flow),
    ...waitAttemptsProblems(raw.flow),
    ...waitAnyProblems(raw.flow),
    ...bindChildProblems(raw.flow),
    ...awaitIdleProblems(raw.flow),
    ...armIdleProblems(raw.flow),
    ...afterExpectationProblems(raw.flow),
    ...waitFactRenewOnProblems(raw.flow),
    ...assertDeliveriesProblems(raw.flow),
    ...eventCeilingSetupProblems(raw.setup),
    ...malformedFaults(raw),
    ...providerErrorProblems(raw),
    ...conflictingFaults(entries, raw),
    ...trajectoryProblems(entries, raw),
    ...signalIdentifierCollisions(turns, entries),
    ...duplicateDeclarations(entries),
    ...danglingReferences(entries, raw),
    ...deadEdges(turns, entries, raw),
  ];

  if (validationProblems.length > 0) {
    return { ok: false, problems: validationProblems.map((problem) => `${name}: ${problem}`) };
  }

  return {
    ok: true,
    scenario: {
      name: raw.scenario,
      description: raw.description,
      // Harness-facing keys, passed through rather than compiled: they configure the
      // workspace and the first prompt, and none of them participates in matching. Kept on
      // the same object so a driver compiles ONCE — reading the file twice was how the JSON
      // era ended up with `readScript` and `loadScripts` disagreeing about the same file.
      setup: raw.setup ?? {},
      routingSource: raw.routingSource,
      session: raw.session,
      prompt: raw.prompt,
      pass: raw.pass,
      must: raw.must ?? [],
      flow: raw.flow ?? [],
      entries,
      faults: (raw.fault ?? []).map((fault) => compileFault(entries, fault)),
      boundaries: (raw.epoch ?? []).map((boundary) => compileBoundary(entries, boundary)),
    },
  };
}

/**
 * A fault's `turn`/`step` names resolve to the ENTRY it governs.
 *
 * `entryId` rather than a (lane, turn, step) copy: the entry is what `resolveEntry`
 * already chose for a request, so an id comparison cannot disagree with it. The copied
 * triple could and did — `faultFor` compared the declared turn text against the request
 * text, so every fault in every real scenario was inert (measured in K9).
 */
const compileFault = (entries, fault) => {
  const { entry } = resolveReference(entries, fault);
  return {
    entryId: entry.id,
    lane: entry.lane,
    attempts: fault.attempts,
    kind: fault.delivery ?? fault.kind,
    status: fault.status,
    retryable: fault.retryable,
  };
};

const compileBoundary = (entries, boundary) => {
  const { entry } = resolveReference(entries, boundary);
  return {
    entryId: entry.id,
    lane: entry.lane,
    kind: boundary.reason ?? boundary.kind,
    optional: boundary.optional === true,
  };
};
