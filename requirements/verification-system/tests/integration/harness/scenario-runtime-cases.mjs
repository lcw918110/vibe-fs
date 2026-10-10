/**
 * gate-scenario-runtime-cases.mjs — the composition, and its first real caller.
 *
 * K2-K5 each produced a pure piece and each had ZERO callers on the provider
 * path (measured at the start of K9). Every gate case before this one exercised a piece in
 * isolation, which proves the piece and says nothing about the forest.
 *
 * These cases drive `ScenarioRuntime` with real compiled scenarios, so they are the first
 * place the four pieces have to agree with each other. The properties below are exactly the
 * ones the old matcher broke — see `design-script-forest.md` §14's list of seven patches
 * that landed on the wrong layer.
 */

import { assertEq, assertTrue } from './lib.mjs';
import { contextOf } from './forest-lib.mjs';
import { compileScenario } from '../../e2e/support/scenario-schema.js';
import { ScenarioRuntime } from '../../e2e/support/scenario-runtime.js';

const SESSION = 'ses_real_1';

const compile = (source) => {
  const result = compileScenario(source, { name: 'p.toml' });
  assertTrue(result.ok, `fixture must compile: ${result.ok ? '' : result.problems.join(' | ')}`);
  return result.scenario;
};

/** A runtime with `manager` already bound, which is what the driver does on session create. */
const runtimeOf = (source, alias = 'manager') => {
  const runtime = new ScenarioRuntime(compile(source));
  runtime.bindAlias(alias, SESSION);
  return runtime;
};

const user = (text) => ({ role: 'user', content: text });
const assistant = (text) => ({ role: 'assistant', content: text });
const SYSTEM = { role: 'system', content: 'You are a manager.' };

/** One provider request fixture; its explicit session id is passed separately as runtime context. */
const request = (messages, extra = {}) => ({
  sessionID: SESSION,
  model: 'test-model',
  messages,
  ...extra,
});

/** Deliver a request and record it, the way the provider path does. */
const deliver = (runtime, body) => {
  const context = contextOf(body.sessionID);
  const selection = runtime.select(body, context);
  runtime.consume(body, selection, context);
  return selection;
};

const TWO_STEPS = `scenario = "p"
prompt = { text = "Ship the parser fix." }

[[turn]]
id = "mgr"
lane = "manager"
user = "Ship the parser fix."

  [[turn.step]]
  respond = { type = "text", text = "step zero" }

  [[turn.step]]
  respond = { type = "text", text = "step one" }
`;

const WITH_FAULT = `${TWO_STEPS}
[[fault]]
turn = "mgr"
step = 0
delivery = "provider-error"
status = 500
retryable = true
attempts = [1, 2]
`;

export const scenarioRuntimeCases = [
  // ── content is a pure function of the request ──────────────────────────────

  {
    name: 'verification-system-003 the same request twice selects the same content',
    fn: () => {
      // The property the whole design rests on, and the one `pathCursor` broke: the old
      // matcher advanced a cursor on every match, so asking twice could answer twice
      // differently. Here the second call must be indistinguishable from the first.
      const runtime = runtimeOf(TWO_STEPS);
      const body = request([user('Ship the parser fix.')]);

      const first = deliver(runtime, body);
      const second = deliver(runtime, body);

      assertEq(first.entry.id, 'mgr.0');
      assertEq(second.entry.id, 'mgr.0', 'a repeated request may not advance anything');
      assertEq(second.entry.respond.text, 'step zero');
    },
  },

  {
    name: 'verification-system-003 step comes from the request, not from arrival order',
    fn: () => {
      // Step 1 resolves on a runtime that has never seen step 0. A cursor-based matcher
      // cannot do this: its answer depends on how many requests already arrived, so the
      // first request it sees must be the first edge in the path.
      //
      // Note what this case may NOT do: deliver step 1 and then step 0 in the same session.
      // That would break the ARCH-004 seal for real — a shorter message list is not an
      // append-only continuation — and the first draft of this case did exactly that,
      // mistaking "step is pure" for "requests may arrive in any order".
      const runtime = runtimeOf(TWO_STEPS);

      const laterBody = request([user('Ship the parser fix.'), assistant('step zero')]);
      const later = runtime.select(laterBody, contextOf(laterBody.sessionID));
      assertEq(later.entry.id, 'mgr.1', 'step is counted off the request');

      const fresh = runtimeOf(TWO_STEPS);
      const freshBody = request([user('Ship the parser fix.')]);
      assertEq(fresh.select(freshBody, contextOf(freshBody.sessionID)).entry.id, 'mgr.0');
    },
  },

  // ── fail closed ───────────────────────────────────────────────────────────

  {
    name: 'verification-system-003 an undeclared request is unmatched, never a default reply',
    fn: () => {
      const runtime = runtimeOf(TWO_STEPS);
      const body = request([user('Something nobody declared.')]);
      const selection = runtime.select(body, contextOf(body.sessionID));

      assertTrue(selection.unmatched !== undefined, 'must fail closed');
      assertEq(selection.unmatched.key.lane, 'manager', 'the diagnostic names the lane');
      assertEq(selection.unmatched.key.step, 0);
    },
  },

  {
    name: 'verification-system-003 an unbound session is unmatched rather than guessed',
    fn: () => {
      // HOST-008 makes the alias→session association durable and the harness is TOLD it.
      // A mock that guessed would answer a question it cannot know.
      const runtime = new ScenarioRuntime(compile(TWO_STEPS));
      const body = request([user('Ship the parser fix.')]);
      const selection = runtime.select(body, contextOf(body.sessionID));

      assertTrue(selection.unmatched !== undefined, 'no binding means no lane');
      assertEq(selection.unmatched.key.lane, null);
    },
  },

  // ── faults are orthogonal to content ──────────────────────────────────────

  {
    name: 'verification-system-003 a retry re-selects the SAME content edge',
    fn: () => {
      // The reason `delivery-plan.js` exists. The old form put the fault inside the content
      // edge, so a failing attempt and its retry were DIFFERENT edges with two ids whose
      // `match` predicates had to be kept in step by hand. Here one edge answers all three
      // deliveries and only the transport outcome differs.
      const runtime = runtimeOf(WITH_FAULT);
      const body = request([user('Ship the parser fix.')]);

      const first = deliver(runtime, body);
      const second = deliver(runtime, body);
      const third = deliver(runtime, body);

      assertEq(first.attempt, 1);
      assertEq(second.attempt, 2);
      assertEq(third.attempt, 3);

      assertTrue(first.fault !== undefined, 'attempt 1 is refused');
      assertTrue(second.fault !== undefined, 'attempt 2 is refused');
      assertTrue(third.fault === undefined, 'attempt 3 is delivered');

      assertEq(first.entry.id, 'mgr.0');
      assertEq(second.entry.id, 'mgr.0');
      assertEq(third.entry.id, 'mgr.0', 'all three are one content edge');
    },
  },

  {
    name: 'verification-system-003 a faulted delivery still seals, so the retry is not a break',
    fn: () => {
      // The old `consumeExpectation` DELETED its cache entry for an error, because caching
      // seal→error would trap every retry on the failure forever. That made the content
      // cache non-idempotent in order to express a transport fact — §14's clearest example
      // of a patch on the wrong layer.
      //
      // Separating the layers removes the need: the seal records what the provider saw, the
      // fault decides what came back, and a retry carrying the same prefix holds.
      const runtime = runtimeOf(WITH_FAULT);
      const body = request([user('Ship the parser fix.')]);

      deliver(runtime, body);
      const retry = deliver(runtime, body);

      assertTrue(retry.sealBroken === undefined, 'a retry of a refused delivery is not a break');
      assertEq(retry.entry.id, 'mgr.0');
    },
  },

  // ── ARCH-004: the prefix seal ─────────────────────────────────────────────

  {
    name: 'ARCH-004 an undeclared prefix rewrite fails closed',
    fn: () => {
      // What `epochCold` used to admit. That exemption read "tools and the leading system
      // message unchanged" and then allowed ANY body rewrite — which is most of what a wrong
      // prefix replacement looks like, so it passed exactly the mutations it existed to
      // catch (measured in K1).
      const runtime = runtimeOf(TWO_STEPS);

      deliver(runtime, request([user('Ship the parser fix.')]));

      // Two ways to get this fixture wrong, both of which prove nothing:
      //
      //   an extra top-level body field — `wireOf` projects the conversation, so the
      //   barrier never sees it
      //   a changed LAST user message — the turn stops matching, so the request comes back
      //   unmatched and the seal is never consulted
      //
      // A real prefix rewrite inserts ahead of the live turn: the last user message is
      // untouched (so the key still resolves) while the transcript is no longer append-only.
      const rewrittenBody = request([
        user('Injected head.'),
        user('Ship the parser fix.'),
        assistant('step zero'),
      ]);
      const rewritten = runtime.select(rewrittenBody, contextOf(rewrittenBody.sessionID));

      assertTrue(rewritten.sealBroken !== undefined, 'the barrier must hold');
      assertEq(rewritten.sealBroken.reason, 'undeclared');
    },
  },

  {
    name: 'ARCH-004 a declared epoch switch admits the rebase at that step',
    fn: () => {
      const runtime = runtimeOf(`${TWO_STEPS}
[[epoch]]
turn = "mgr"
step = 1
reason = "epoch-switch"
`);

      deliver(runtime, request([user('Ship the parser fix.')]));

      // A rebase replaces the head with the companion summary and keeps the live tail, so
      // the turn text still matches at step 1 while the prefix is no longer append-only.
      const rebasedBody = request([
        user('Condensed companion context.'),
        user('Ship the parser fix.'),
        assistant('step zero'),
      ]);
      const rebased = runtime.select(rebasedBody, contextOf(rebasedBody.sessionID));

      assertEq(rebased.resealed, 'epoch-switch');
      assertEq(rebased.entry.id, 'mgr.1');
    },
  },

  {
    name: 'ARCH-004 a declared boundary that does not fire is itself a failure',
    fn: () => {
      // A declaration that never fires is worse than a missing one: the author believes a
      // cold boundary is covered and the scenario silently stopped exercising it. Same
      // reasoning as an empty `attempts` list in a fault.
      // `epoch-switch` is excluded here because it shares the multi-delivery shape
      // with `prefix-probe`/`frame-commit`/`manager-loop`: append-only deliveries
      // are legal and held, not `boundary-not-reached`. Use `fallback-side` instead.
      const runtime = runtimeOf(`${TWO_STEPS}
[[epoch]]
turn = "mgr"
step = 1
reason = "fallback-side"
`);

      deliver(runtime, request([user('Ship the parser fix.')]));

      const appendOnlyBody = request([user('Ship the parser fix.'), assistant('step zero')]);
      const appendOnly = runtime.select(appendOnlyBody, contextOf(appendOnlyBody.sessionID));

      assertTrue(appendOnly.sealBroken !== undefined);
      assertEq(appendOnly.sealBroken.reason, 'boundary-not-reached');
    },
  },

  {
    name: 'CTX-010 a prefix-probe entry admits breaking and append-only deliveries alike',
    fn: () => {
      // A recovery sequence alternates probe slots (odd offset, armed) and ordinary
      // slots, so one entry legitimately mixes rebased and append-only requests.
      // The declaration is checked on every delivery; "never fired" surfaces at
      // scenario end via `unfiredBoundaries`.
      const runtime = runtimeOf(`${TWO_STEPS}
[[epoch]]
turn = "mgr"
step = 1
reason = "prefix-probe"
`);

      deliver(runtime, request([SYSTEM, user('Ship the parser fix.')]));

      // Delivery 1 of step 1: the probe rebases the prefix — the covered head is
      // replaced by the synthetic companion memory, the system and tool set stay
      // fixed, and the declared user text remains the physical tail. The assistant
      // after it makes this step 1 of the turn.
      const probed = request([
        SYSTEM,
        user('[companion memory]'),
        assistant('step zero'),
        user('Ship the parser fix.'),
        assistant('reply'),
      ]);
      const first = runtime.select(probed, contextOf(probed.sessionID));
      assertEq(first.resealed, 'prefix-probe');

      // Delivery 2 of step 1: an ordinary append-only continuation of the probed
      // wire, same declared text as its physical tail.
      const appended = request([
        SYSTEM,
        user('[companion memory]'),
        assistant('step zero'),
        user('Ship the parser fix.'),
        assistant('reply'),
        user('Ship the parser fix.'),
        assistant('reply2'),
      ]);
      const second = runtime.select(appended, contextOf(appended.sessionID));
      assertTrue(second.sealBroken === undefined, 'append-only delivery after a fired probe is legal');

      // The declaration fired, so the scenario-end check is clean.
      assertEq(runtime.unfiredBoundaries().length, 0);
    },
  },

  {
    name: 'CTX-010 a prefix-probe declaration that never fires is reported at scenario end',
    fn: () => {
      const runtime = runtimeOf(`${TWO_STEPS}
[[epoch]]
turn = "mgr"
step = 1
reason = "prefix-probe"
`);

      deliver(runtime, request([user('Ship the parser fix.')]));

      // Only append-only deliveries: the declaration never admitted a break.
      const appendOnlyBody = request([user('Ship the parser fix.'), assistant('step zero')]);
      const appendOnly = runtime.select(appendOnlyBody, contextOf(appendOnlyBody.sessionID));
      assertTrue(appendOnly.sealBroken === undefined, 'an append-only delivery of a probe entry is not a seal error');

      const unfired = runtime.unfiredBoundaries();
      assertEq(unfired.length, 1, 'the declaration is reported at scenario end');
      assertEq(unfired[0].entryId, 'mgr.1');
      assertEq(unfired[0].kind, 'prefix-probe');
    },
  },

  {
    name: 'ARCH-004 a title request does not disturb the chat seal',
    fn: () => {
      // A title request carries the Host's marker at `messages[0]` and the conversation
      // after it (`prompt.ts:235`), so its wire projection is a different shape. Sealing it
      // would break the very next chat turn.
      const runtime = runtimeOf(`${TWO_STEPS}
[[turn]]
id = "title"
lane = "manager"
kind = "title"
user = "Ship the parser fix."

  [[turn.step]]
  respond = { type = "title", text = "Parser fix" }
`);

      deliver(runtime, request([user('Ship the parser fix.')]));

      const title = deliver(
        runtime,
        request([user('Generate a title for this conversation:\n'), user('Ship the parser fix.')]),
      );
      assertEq(title.entry.id, 'title.0');

      const nextBody = request([user('Ship the parser fix.'), assistant('step zero')]);
      const next = runtime.select(nextBody, contextOf(nextBody.sessionID));
      assertTrue(next.sealBroken === undefined, 'the chat seal survived the title request');
      assertEq(next.entry.id, 'mgr.1');
    },
  },

  // ── coverage reporting ────────────────────────────────────────────────────

  {
    name: 'verification-system-003 unanswered steps are reported, and internal turns are exempt',
    fn: () => {
      // `internal` turns exist only if production decides to compose them — a re-anchor
      // frame needs a restart, a guard nudge needs an unreviewed completion. Their absence
      // is not evidence of a broken scenario, so a scenario that NEEDS one says `must`.
      const runtime = runtimeOf(`scenario = "p"
prompt = { text = "Ship the parser fix." }

[[turn]]
id = "mgr"
lane = "manager"
user = "Ship the parser fix."

  [[turn.step]]
  respond = { type = "text", text = "done" }

[[turn]]
id = "guard"
internal = true
user = "# Review is required before completion."

  [[turn.step]]
  respond = { type = "text", text = "acknowledged" }
`);

      assertEq(runtime.unanswered().length, 1, 'only the non-internal step counts');
      assertEq(runtime.unanswered()[0].id, 'mgr.0');

      deliver(runtime, request([user('Ship the parser fix.')]));
      assertEq(runtime.unanswered().length, 0, 'the internal turn never has to arrive');
    },
  },

  {
    name: 'verification-system-003 must names a turn or a step and is checked by arrival',
    fn: () => {
      const runtime = runtimeOf(`scenario = "p"
must = ["guard"]
prompt = { text = "Ship the parser fix." }

[[turn]]
id = "mgr"
lane = "manager"
user = "Ship the parser fix."

  [[turn.step]]
  respond = { type = "text", text = "done" }

[[turn]]
id = "guard"
internal = true
user = "# Review is required before completion."

  [[turn.step]]
  respond = { type = "text", text = "acknowledged" }
`);

      deliver(runtime, request([user('Ship the parser fix.')]));
      assertEq(runtime.unmetMust().length, 1, 'the required internal turn has not arrived');

      // A guard nudge is a new user message APPENDED to the conversation, so the seal holds
      // and `turnOf` reads the nudge as the last user message.
      deliver(
        runtime,
        request([
          user('Ship the parser fix.'),
          assistant('done'),
          user('# Review is required before completion.'),
        ]),
      );
      assertEq(runtime.unmetMust().length, 0);
    },
  },

  // ── a real compiled conversation (inline; no retired canary basename) ─────

  {
    name: 'verification-system-003 a real scenario drives its whole declared conversation',
    fn: () => {
      // Long Stroke is too large for this deliver()-style unit proof, so an INLINE fixture
      // carries the same shape the old smallest converted scenario used: one chat turn with
      // two provider steps plus a title on a shared session. Do not recreate a second TOML
      // on disk — One World keeps long-stroke.toml as the sole product scenario.
      // `session = { bind = ["inspector-title", "inspector"] }` — both aliases point at
      // the one session the Host mints, which is why `lanesOf` returns a set.
      const INLINE_RUNTIME = `scenario = "inline-runtime"
prompt = { text = "Run the command and report if it timed out." }

[[turn]]
id = "inspector"
lane = "inspector"
user = "Run the command and report if it timed out."
tools = ["executor"]

  [[turn.step]]
  respond = { type = "tool-call", tool = "executor", args = { command = "true" } }

  [[turn.step]]
  respond = { type = "text", text = "timeout observed." }

[[turn]]
id = "title"
lane = "inspector-title"
kind = "title"
user = "Run the command and report if it timed out."

  [[turn.step]]
  respond = { type = "title", text = "E2E Test Session" }
`;
      const runtime = runtimeOf(INLINE_RUNTIME, 'inspector');
      runtime.bindAlias('inspector-title', SESSION);
      const prompt = 'Run the command and report if it timed out.';

      const step0 = deliver(runtime, request([user(prompt)], { tools: [{ name: 'executor' }] }));
      assertEq(step0.entry.id, 'inspector.0');
      assertEq(step0.entry.respond.tool, 'executor');

      const step1 = deliver(runtime, request([user(prompt), assistant('executor called')], { tools: [{ name: 'executor' }] }));
      assertEq(step1.entry.id, 'inspector.1');

      const title = deliver(runtime, request([user('Generate a title for this conversation:\n'), user(prompt)]));
      assertEq(title.entry.id, 'title.0');

      assertEq(runtime.unanswered().length, 0, 'every declared step was reached');
      assertEq(runtime.unmetMust().length, 0);
    },
  },
];
