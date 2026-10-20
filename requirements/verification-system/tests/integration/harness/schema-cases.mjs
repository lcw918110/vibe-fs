/**
 * gate-schema-cases.mjs — a scenario that half-loads is worse than one that fails.
 *
 * Every check here runs at load time with no Host and no build artifacts,
 * which is what puts it in verification-system-001 layer 0 alongside `ssot-lint` and `shock-audit`.
 *
 * The dead-edge check is the one that requires the static whole: a step no flow can
 * reach is a step the author believes is covered. It was unavailable while scenarios
 * could be swapped mid-run (`loadScripts`), which is one of the reasons §8 of
 * `design-script-forest.md` rejects dynamic loading.
 */

import { assertEq, assertTrue } from './lib.mjs';
import { compileScenario, rootKeyOrderProblems } from '../../e2e/support/scenario-schema.js';

const compile = (source) => compileScenario(source, { name: 'p.toml' });

const rejects = (source, fragment) => {
  const result = compile(source);
  assertTrue(!result.ok, 'expected rejection');
  assertTrue(result.scenario === undefined, 'a rejection must not carry a usable scenario');
  assertTrue(
    result.problems.some((problem) => problem.includes(fragment)),
    `expected a problem mentioning '${fragment}', got: ${result.problems.join(' | ')}`,
  );
};

const accepts = (source) => {
  const result = compile(source);
  assertTrue(result.ok, `expected acceptance, got: ${result.ok ? '' : result.problems.join(' | ')}`);
  return result.scenario;
};

const HEALTHY = `scenario = "probe"
description = "ORCH-005 short CAS publish"
must = ["mgr.0"]

flow = [
  { prompt = { agent = "manager", text = "Ship the parser fix." } },
  { wait = "mgr.0" },
]

[[turn]]
id = "mgr"
lane = "manager"
user = "Ship the parser fix."
tools = ["fork", "join"]

  [[turn.step]]
  respond = { type = "tool-call", tool = "fork", args = { agent = "coder", prompt = "Fix it." } }

  [[turn.step]]
  respond = { type = "text", text = "Done." }

[[turn]]
id = "coder"
lane = "coder"
user = "Fix it."

  [[turn.step]]
  respond = { type = "text", text = "Fixed." }

[[fault]]
turn = "mgr"
step = 0
attempts = [1]
delivery = "provider-error"
status = 500
retryable = true

[[epoch]]
turn = "mgr"
step = 1
reason = "epoch-switch"
`;

/** One turn, one step, reachable — the smallest scenario that loads. */
const minimal = (extra = '') =>
  `scenario = "p"
flow = [ { prompt = { text = "go" } } ]

[[turn]]
id = "a"
user = "go"

  [[turn.step]]
  respond = { type = "text", text = "ok" }
${extra}`;

export const schemaCases = [
  // ── compilation: source is a conversation, output is a lookup table ────────

  {
    name: 'verification-system-003 a step position compiles to the runtime step integer',
    fn: () => {
      // §9's two layers. The author never writes a step number; the position within
      // the turn IS the number, which is what K2's `stepOf` counts off the request.
      const scenario = accepts(HEALTHY);

      assertEq(
        scenario.entries.map((entry) => `${entry.id}@${entry.step}`).join(' '),
        'mgr.0@0 mgr.1@1 coder.0@0',
      );
    },
  },

  {
    name: 'verification-system-003 an explicit runtimeStep compiles a sparse measured cursor',
    fn: () => {
      const scenario = accepts(`scenario = "p"
flow = [ { prompt = { text = "go" } } ]

[[turn]]
id = "a"
user = "go"

  [[turn.step]]
  respond = { type = "tool-call", tool = "return", args = {} }

  [[turn.step]]
  runtimeStep = 3
  respond = { type = "text", text = "done" }
`);

      assertEq(
        scenario.entries.map((entry) => `${entry.id}@${entry.step}`).join(' '),
        'a.0@0 a.1@3',
      );
    },
  },

  {
    name: 'verification-system-003 runtimeStep rejects malformed and duplicate cursors',
    fn: () => {
      rejects(minimal('\n  [[turn.step]]\n  runtimeStep = -1\n  respond = { type = "text", text = "bad" }\n'), 'runtimeStep must be a non-negative integer');
      rejects(minimal('\n  [[turn.step]]\n  runtimeStep = 0\n  respond = { type = "text", text = "duplicate" }\n'), 'runtimeStep 0 duplicates');
    },
  },

  {
    name: 'verification-system-003 a fault names a step and compiles to the integer key',
    fn: () => {
      // The deviation from §10 worth pinning: the document writes `step = "fork-agent"`
      // (a name), K2 made the runtime step an integer. The compiler resolves one into
      // the other, and that resolution is what makes dangling references detectable —
      // a name can be checked against the declared set, an integer cannot.
      const scenario = accepts(HEALTHY);

      assertEq(scenario.faults.length, 1);
      // The author wrote `turn = "mgr"`, `step = 0`; the compiler resolved that pair to the
      // one entry it names. `entryId` rather than a copied (lane, turn, step) triple, because
      // the entry is what `resolveEntry` already chose for a request — an id comparison
      // cannot disagree with it, and the copied triple did (measured: every real fault inert).
      assertEq(scenario.faults[0].entryId, 'mgr.0');
      assertEq(scenario.faults[0].kind, 'provider-error');
      assertEq(scenario.faults[0].lane, 'manager');
    },
  },

  {
    name: 'verification-system-003 a cold boundary compiles the same way',
    fn: () => {
      const scenario = accepts(HEALTHY);

      assertEq(scenario.boundaries.length, 1);
      assertEq(scenario.boundaries[0].entryId, 'mgr.1');
      assertEq(scenario.boundaries[0].kind, 'epoch-switch');
    },
  },

  {
    name: 'verification-system-003 a child turn is reached through a tool call argument',
    fn: () => {
      // `fork(agent, prompt)` is how the session that receives a child turn comes to
      // exist, so the prompt argument is a real edge in the reachability graph. Without
      // this, every child turn in every scenario would read as a dead edge.
      const scenario = accepts(HEALTHY);
      assertTrue(
        scenario.entries.some((entry) => entry.turnId === 'coder'),
        'the coder turn must survive the dead-edge check',
      );
    },
  },

  // ── the TOML root-key trap ───────────────────────────────────────────────

  {
    name: 'verification-system-003 a root key after a table header is rejected',
    fn: () => {
      // Measured in §10: `flow = [...]` after `[[epoch]]` parses as `epoch[0].flow`
      // with no error. The parsed object cannot reveal this — it is indistinguishable
      // from a `flow` key the author meant to nest — so the check reads source text.
      rejects(
        `scenario = "p"

[[turn]]
id = "a"
user = "go"
flow = []

  [[turn.step]]
  respond = { type = "text", text = "ok" }
`,
        "root key 'flow' appears after",
      );
    },
  },

  {
    name: 'verification-system-003 every root key is guarded, and the line is reported',
    fn: () => {
      for (const key of ['scenario', 'description', 'must', 'flow']) {
        const problems = rootKeyOrderProblems(`[[turn]]\nid = "a"\n${key} = "x"\n`);
        assertEq(problems.length, 1, `${key} must be guarded`);
        assertTrue(problems[0].startsWith('line 3:'), problems[0]);
      }
    },
  },

  {
    name: 'verification-system-003 a key inside a table is not mistaken for a root key',
    fn: () => {
      // `id`, `user`, `tools`, `respond` legitimately live under a header. Flagging
      // them would make the check unusable, so only the four root keys are guarded.
      assertEq(rootKeyOrderProblems(HEALTHY).length, 0);
    },
  },

  {
    name: 'verification-system-003 commented-out root keys are ignored',
    fn: () => {
      // §10 makes comments load-bearing for clause references, so a commented example
      // must not fail the file.
      assertEq(rootKeyOrderProblems('[[turn]]\nid = "a"\n# flow = []\n').length, 0);
    },
  },

  // ── one point, one declaration ───────────────────────────────────────────

  {
    name: 'verification-system-003 two declarations for one key with different responses are rejected',
    fn: () => {
      rejects(
        `scenario = "p"
flow = [ { prompt = { text = "go" } } ]

[[turn]]
id = "a"
user = "go"

  [[turn.step]]
  respond = { type = "text", text = "A" }

[[turn]]
id = "b"
user = "go"

  [[turn.step]]
  respond = { type = "text", text = "B" }
`,
        'does not say what the model does next',
      );
    },
  },

  {
    name: 'verification-system-003 two declarations for one key with the SAME response are also rejected',
    fn: () => {
      // The documented deviation. §10 collapses identical templates, which was a
      // mitigation for predicate-conjunction matching where template reuse produced
      // duplicates naturally. Under (lane, turn, step) keying a recurring nudge has
      // several steps, so a true duplicate is debris the author should delete.
      rejects(
        `scenario = "p"
flow = [ { prompt = { text = "go" } } ]

[[turn]]
id = "a"
user = "go"

  [[turn.step]]
  respond = { type = "text", text = "same" }

[[turn]]
id = "b"
user = "go"

  [[turn.step]]
  respond = { type = "text", text = "same" }
`,
        'delete one',
      );
    },
  },

  {
    name: 'verification-system-003 the same turn text at different steps is legitimate',
    fn: () => {
      // A multi-step turn is the normal shape. If this were an ambiguity the old
      // matcher's `messageCount` predicate would still be needed.
      const scenario = accepts(minimal());
      assertEq(scenario.entries.length, 1);

      accepts(`scenario = "p"
flow = [ { prompt = { text = "go" } } ]

[[turn]]
id = "a"
user = "go"

  [[turn.step]]
  respond = { type = "text", text = "first" }

  [[turn.step]]
  respond = { type = "text", text = "second" }
`);
    },
  },

  {
    name: 'verification-system-003 the same turn text in different lanes is legitimate',
    fn: () => {
      accepts(`scenario = "p"
flow = [ { prompt = { text = "go" } } ]

[[turn]]
id = "a"
lane = "one"
user = "go"

  [[turn.step]]
  respond = { type = "text", text = "A" }

[[turn]]
id = "b"
lane = "two"
user = "go"

  [[turn.step]]
  respond = { type = "text", text = "B" }
`);
    },
  },

  // ── dangling references ──────────────────────────────────────────────────

  {
    name: 'verification-system-003 a fault referencing an undeclared turn is rejected',
    fn: () => {
      rejects(
        minimal('\n[[fault]]\nturn = "nope"\nstep = 0\nattempts = [1]\ndelivery = "provider-error"\n'),
        "references turn 'nope'",
      );
    },
  },

  {
    name: 'verification-system-003 a fault referencing an undeclared step is rejected',
    fn: () => {
      // The narrower dangling case: the turn exists, the step does not. Silently
      // accepting it would make the fault inert — a step the author believes is
      // covered and is not, which is the same damage as an empty `attempts` list.
      rejects(
        minimal('\n[[fault]]\nturn = "a"\nstep = 7\nattempts = [1]\ndelivery = "provider-error"\n'),
        "references step '7'",
      );
    },
  },

  {
    name: 'verification-system-003 a cold boundary referencing an undeclared step is rejected',
    fn: () => {
      rejects(minimal('\n[[epoch]]\nturn = "nope"\nstep = 0\nreason = "epoch-switch"\n'), "references turn 'nope'");
      rejects(minimal('\n[[epoch]]\nturn = "a"\nstep = 3\nreason = "epoch-switch"\n'), "references step '3'");
    },
  },

  {
    name: 'verification-system-003 must referencing an undeclared step is rejected',
    fn: () => {
      rejects(
        `scenario = "p"
must = ["ghost"]
flow = [ { prompt = { text = "go" } } ]

[[turn]]
id = "a"
user = "go"

  [[turn.step]]
  respond = { type = "text", text = "ok" }
`,
        "must references 'ghost'",
      );
    },
  },

  {
    name: 'verification-system-003 a flow wait referencing an undeclared step is rejected',
    fn: () => {
      // Not in §12's list, added here: a `wait` on a step that cannot arrive hangs the
      // scenario until the watchdog fires, and the diagnostic then points at a timeout
      // rather than at the typo.
      rejects(
        `scenario = "p"
flow = [ { prompt = { text = "go" } }, { wait = "ghost" } ]

[[turn]]
id = "a"
user = "go"

  [[turn.step]]
  respond = { type = "text", text = "ok" }
`,
        "flow wait references 'ghost'",
      );
    },
  },

  {
    name: 'verification-system-003 waitAny accepts two exact declared alternatives',
    fn: () => {
      accepts(`scenario = "p"
flow = [ { prompt = { text = "go" } }, { waitAny = ["a.0", "a.1"] } ]

[[turn]]
id = "a"
user = "go"

  [[turn.step]]
  respond = { type = "text", text = "first" }

  [[turn.step]]
  respond = { type = "text", text = "second" }
`);
    },
  },

  {
    name: 'verification-system-003 waitAny rejects malformed and dangling alternatives',
    fn: () => {
      rejects(minimal().replace('flow = [ { prompt = { text = "go" } } ]', 'flow = [ { prompt = { text = "go" } }, { waitAny = "a.0" } ]'), 'waitAny must be an array');
      rejects(minimal().replace('flow = [ { prompt = { text = "go" } } ]', 'flow = [ { prompt = { text = "go" } }, { waitAny = ["a.0"] } ]'), 'waitAny requires at least two');
      rejects(minimal().replace('flow = [ { prompt = { text = "go" } } ]', 'flow = [ { prompt = { text = "go" } }, { waitAny = ["a.0", "a.0"] } ]'), 'waitAny alternatives must be unique');
      rejects(minimal().replace('flow = [ { prompt = { text = "go" } } ]', 'flow = [ { prompt = { text = "go" } }, { waitAny = ["a.0", "ghost"] } ]'), "waitAny references 'ghost'");
    },
  },

  {
    name: 'verification-system-003 must and wait may name a turn or a step',
    fn: () => {
      // Both granularities are useful: a turn id for "this exchange happened", a step
      // id for "this particular provider step happened".
      accepts(HEALTHY);
      accepts(`scenario = "p"
must = ["a"]
flow = [ { prompt = { text = "go" } }, { wait = "a" } ]

[[turn]]
id = "a"
user = "go"

  [[turn.step]]
  respond = { type = "text", text = "ok" }
`);
    },
  },

  {
    name: 'verification-system-003 turn and step ids cannot share one wait signal',
    fn: () => {
      rejects(
        `scenario = "p"
flow = [
  { prompt = { text = "plan" } },
  { prompt = { text = "activate" } },
]

[[turn]]
id = "manager"
user = "plan"

  [[turn.step]]
  respond = { type = "text", text = "first" }

  [[turn.step]]
  respond = { type = "text", text = "second" }

[[turn]]
id = "manager.1"
user = "activate"

  [[turn.step]]
  respond = { type = "text", text = "activated" }
`,
        "signal id 'manager.1'",
      );
    },
  },

  // ── dead edges: the check only a static whole allows ─────────────────────

  {
    name: 'verification-system-003 a turn no flow can reach is rejected',
    fn: () => {
      rejects(
        `scenario = "p"
flow = [ { prompt = { text = "go" } } ]

[[turn]]
id = "a"
user = "go"

  [[turn.step]]
  respond = { type = "text", text = "ok" }

[[turn]]
id = "orphan"
user = "nobody ever sends this"

  [[turn.step]]
  respond = { type = "text", text = "x" }
`,
        'dead edge',
      );
    },
  },

  {
    name: 'verification-system-003 a production-composed lane opts out with internal',
    fn: () => {
      // The internal background worker and the Executor map child cannot be reached by any scenario text:
      // production composes their prompts itself
      // `src/Wanxiangshu/OpenCode/Tools/ExecutorSummarize.fs:95`). Without an opt-out the dead-edge
      // check has no evidence either way and rejects a correct scenario.
      accepts(`scenario = "p"
prompt = { text = "go" }

[[turn]]
id = "a"
user = "go"

  [[turn.step]]
  respond = { type = "text", text = "ok" }

[[turn]]
id = "internal-worker"
internal = true
user = "You are an internal worker."

  [[turn.step]]
  respond = { type = "text", text = "blog" }
`);
    },
  },

  {
    name: 'verification-system-003 internal must be true when present',
    fn: () => {
      // `internal = false` would read as "checked and reachable", which is the opposite
      // of what the field means. Omitting it is the way to say that.
      rejects(
        `scenario = "p"
prompt = { text = "go" }

[[turn]]
id = "a"
internal = false
user = "go"

  [[turn.step]]
  respond = { type = "text", text = "ok" }
`,
        'internal must be true when present',
      );
    },
  },

  {
    name: 'verification-system-003 a race step opts out of unanswered with optional',
    fn: () => {
      // A race path (restart window, crash timing) may or may not be reached on a
      // given run. Declaring the step lets a request be answered when it fires;
      // `optional = true` says its absence at scenario end is expected, not a defect.
      accepts(`scenario = "p"
prompt = { text = "go" }

[[turn]]
id = "a"
user = "go"

  [[turn.step]]
  respond = { type = "text", text = "ok" }

  [[turn.step]]
  optional = true
  respond = { type = "text", text = "race tail" }
`);
    },
  },

  {
    name: 'verification-system-003 optional must be true when present',
    fn: () => {
      // `optional = false` would read as "checked and required", which is the opposite
      // of what the field means. Omitting it is the way to say that.
      rejects(
        `scenario = "p"
prompt = { text = "go" }

[[turn]]
id = "a"
user = "go"

  [[turn.step]]
  optional = false
  respond = { type = "text", text = "ok" }
`,
        'optional must be true when present',
      );
    },
  },

  {
    name: 'verification-system-003 a required step may not follow an optional race tail',
    fn: () => {
      // The race tail is terminal for the turn's requirement surface: a required
      // step after it would only be reachable when the race fired, silently
      // coupling the requirement to the race.
      rejects(
        `scenario = "p"
prompt = { text = "go" }

[[turn]]
id = "a"
user = "go"

  [[turn.step]]
  optional = true
  respond = { type = "text", text = "race tail" }

  [[turn.step]]
  respond = { type = "text", text = "required after race" }
`,
        'follows an optional step but is not optional',
      );
    },
  },

  {
    name: 'verification-system-003 a must requirement may not name an optional step',
    fn: () => {
      // `must` means "this MUST be reached"; `optional` means absence is fine.
      // Naming one in the other is contradictory.
      rejects(
        `scenario = "p"
prompt = { text = "go" }
must = ["a.1"]

[[turn]]
id = "a"
user = "go"

  [[turn.step]]
  respond = { type = "text", text = "ok" }

  [[turn.step]]
  optional = true
  respond = { type = "text", text = "race tail" }
`,
        'contradicts optional',
      );
    },
  },

  {
    name: 'verification-system-003 parentSession is retired, not a reachability input',
    fn: () => {
      // Measured dead twice over. Its only source was a retired legacy parent-session
      // header (`x-parent-session-id` under harness bookkeeping the provider never
      // sees) and `matchesExpectation` resolved it through `sessionBindings`, where
      // all 16 scenarios declaring a parent had never bound it. So the comparison
      // short-circuited and never ran, in every scenario, for its whole life.
      rejects(
        `scenario = "p"
prompt = { text = "go" }

[[turn]]
id = "a"
parentSession = "manager"
user = "go"

  [[turn.step]]
  respond = { type = "text", text = "ok" }
`,
        'mark the lane internal = true instead',
      );
    },
  },

  {
    name: 'verification-system-003 a title turn is reachable through the turn it titles',
    fn: () => {
      // No special case needed: a title request carries the conversation being titled,
      // so its declared text prefix-matches the real turn's. Special-casing `kind` here
      // would have exempted title turns from the dead-edge check for nothing.
      const scenario = accepts(`scenario = "p"
prompt = { text = "Ship the parser fix now." }

[[turn]]
id = "a"
user = "Ship the parser fix now."

  [[turn.step]]
  respond = { type = "text", text = "ok" }

[[turn]]
id = "title"
kind = "title"
user = "Ship the parser fix"

  [[turn.step]]
  respond = { type = "title", text = "Parser fix" }
`);

      assertEq(scenario.entries.length, 2);
    },
  },

  {
    name: 'verification-system-003 reachability is prefix-based in both directions',
    fn: () => {
      // A flow prompt may be longer than the declared fragment (the scenario declares
      // a distinctive prefix) or shorter (the flow sends a short instruction and the
      // turn text quotes more of it). Requiring equality would force authors to repeat
      // long prompts verbatim in two places.
      accepts(`scenario = "p"
flow = [ { prompt = { text = "Ship the parser fix now, carefully." } } ]

[[turn]]
id = "a"
user = "Ship the parser fix"

  [[turn.step]]
  respond = { type = "text", text = "ok" }
`);
    },
  },

  // ── structural requirements ──────────────────────────────────────────────

  {
    name: 'verification-system-003 a malformed fault is rejected by the real compiler, not only the unit',
    fn: () => {
      // `validateFault` had eight callers in this gate and none in the compiler, so a
      // real scenario could declare `attempts = []` — a fault that never fires — and
      // load clean. Same zero-call-site shape `single-constructor` catches one layer
      // down: a checker nothing calls is a checker that is not in force.
      rejects(
        `${HEALTHY}
[[fault]]
turn = "mgr"
step = 1
delivery = "provider-error"
status = 500
retryable = true
attempts = []
`,
        'an empty list is a fault that never fires',
      );
    },
  },

  {
    name: 'verification-system-003 a provider-error must declare status and retryable',
    fn: () => {
      // provider-attempt-recovery-019. `retryable` decides WHO drives the retry: a retryable 500 means
      // the Host does it and no continuation is ever sent; a non-retryable 400 means the
      // Host gives up and the plugin must carry the Logical Run forward. Both run
      // silently, proving different clauses — so neither may be defaulted.
      rejects(
        `${HEALTHY}
[[fault]]
turn = "mgr"
step = 1
delivery = "provider-error"
status = 500
attempts = [1]
`,
        'must declare retryable',
      );

      rejects(
        `${HEALTHY}
[[fault]]
turn = "mgr"
step = 1
delivery = "provider-error"
retryable = false
attempts = [1]
`,
        'must declare the HTTP status',
      );

      // A disconnect has no status to declare, so the requirement is scoped to the one
      // fault kind that carries one.
      accepts(`${HEALTHY}
[[fault]]
turn = "mgr"
step = 1
delivery = "disconnect"
attempts = [1]
`);
    },
  },

  {
    name: 'verification-system-003 two faults on one key are rejected at load, not at delivery',
    fn: () => {
      // `faultFor` throws on this, which means the author finds out mid-run — after the
      // Host is up, in whichever scenario happened to reach that step first. The whole
      // point of a load-time check is that the static whole is available before then.
      rejects(
        `${HEALTHY}
[[fault]]
turn = "mgr"
step = 0
delivery = "disconnect"
attempts = [2]
`,
        'two faults declared for the same (lane, turn, step)',
      );
    },
  },

  {
    name: 'verification-system-003 assertModelTrajectory must name a declared lane and an exact sequence',
    fn: () => {
      // The effective model is a CONCLUSION of the run (PROMPT-008 makes
      // `AttemptExecutionProfile` its only source), so a scenario asserts it and never
      // matches on it. The old scenario matched `model` per edge, which let
      // it silently agree with whatever the scheduler happened to do.
      const claiming = (claim) => `scenario = "probe"

flow = [
  { prompt = { agent = "manager", text = "Ship the parser fix." } },
  { wait = "mgr.0" },
  { assertModelTrajectory = ${claim} },
]

[[turn]]
id = "mgr"
lane = "manager"
user = "Ship the parser fix."

  [[turn.step]]
  respond = { type = "text", text = "Done." }
`;

      rejects(claiming('false'), 'assertModelTrajectory must be a table');
      rejects(claiming('{ lane = "nonexistent", models = ["test-model"] }'), "references lane 'nonexistent'");
      rejects(claiming('{ lane = "manager", models = [] }'), 'needs the exact expected model sequence');
      accepts(claiming('{ lane = "manager", models = ["test-model", "test-model-b"] }'));
    },
  },

  {
    name: 'verification-system-003 bindChild identifies the next child by exact Host agent only',
    fn: () => {
      const binding = (value) => `scenario = "p"
flow = [
  { prompt = { text = "go" } },
  { bindChild = ${value} },
]

[[turn]]
id = "a"
user = "go"

  [[turn.step]]
  respond = { type = "text", text = "ok" }
`;

      rejects(binding('{ bind = ["coder"] }'), 'bindChild requires an exact agent');
      rejects(
        binding('{ agent = "coder", bind = ["coder"], parent = "manager" }'),
        "bindChild field 'parent' is unsupported",
      );
      accepts(binding('{ agent = "coder", bind = ["coder"] }'));
    },
  },

  {
    name: 'verification-system-003 afterExpectation attempts is a one-based physical delivery count',
    fn: () => {
      const withAttempts = (attempts) => minimal().replace(
        'flow = [ { prompt = { text = "go" } } ]',
        `flow = [
  { afterExpectation = { id = "a.0", attempts = ${attempts}, restart = true } },
  { prompt = { text = "go" } },
]`,
      );

      rejects(withAttempts(0), 'afterExpectation attempts must be a positive integer');
      accepts(withAttempts(2));
    },
  },

  {
    name: 'verification-system-003 assertDeliveries is a load-time bounded delivery claim',
    fn: () => {
      const withAssertion = (claim) => minimal().replace(
        'flow = [ { prompt = { text = "go" } } ]',
        `flow = [
  { prompt = { text = "go" } },
  { assertDeliveries = ${claim} },
]`,
      );

      rejects(withAssertion('{ id = "a.0" }'), 'assertDeliveries requires eq, gte, or lte');
      rejects(withAssertion('{ id = "", eq = 1 }'), 'assertDeliveries requires an exact id');
      rejects(withAssertion('{ id = "a.0", eq = -1 }'), 'assertDeliveries eq must be a non-negative integer');
      rejects(withAssertion('{ id = "a.0", gte = 2, lte = 1 }'), 'assertDeliveries lower bound exceeds upper bound');
      rejects(withAssertion('{ id = "missing.0", eq = 0 }'), "assertDeliveries references 'missing.0'");
      rejects(withAssertion('{ id = "a.0", eq = 1, parent = "manager" }'), "assertDeliveries field 'parent' is unsupported");
      accepts(withAssertion('{ id = "a.0", gte = 1, lte = 2 }'));
    },
  },

  {
    name: 'verification-system-003 waitFact renewOn is a unique non-empty fact list distinct from the target',
    fn: () => {
      const withWaitFact = (claim) => minimal().replace(
        'flow = [ { prompt = { text = "go" } } ]',
        `flow = [
  { prompt = { text = "go" } },
  { waitFact = ${claim} },
]`,
      );

      rejects(withWaitFact('false'), 'waitFact must be a table');
      rejects(withWaitFact('{ name = "Published", renewOn = ["", "CandidateReady"] }'), 'renewOn entries must be non-empty strings');
      rejects(withWaitFact('{ name = "Published", renewOn = ["CandidateReady", "CandidateReady"] }'), 'renewOn entries must be unique');
      rejects(withWaitFact('{ name = "Published", renewOn = ["Published"] }'), 'renewOn must not contain the target fact');
      rejects(withWaitFact('{ name = "Published", renewOn = "CandidateReady" }'), 'renewOn must be an array of fact names');
      accepts(withWaitFact('{ name = "Published", eq = 1, renewOn = ["CandidateReady"] }'));
    },
  },

  {
    name: 'verification-system-003 a scenario needs a name, and a turn needs user text and a step',
    fn: () => {
      rejects('[[turn]]\nid = "a"\nuser = "go"\n\n  [[turn.step]]\n  respond = { type = "text" }\n', 'scenario name');
      rejects('scenario = "p"\nflow = []\n\n[[turn]]\nid = "a"\n\n  [[turn.step]]\n  respond = {}\n', 'needs user text');
      rejects('scenario = "p"\nflow = []\n\n[[turn]]\nid = "a"\nuser = "go"\n', 'at least one step');
      rejects('scenario = "p"\nflow = []\n\n[[turn]]\nuser = "go"\n\n  [[turn.step]]\n  respond = {}\n', 'needs an id');
    },
  },

  {
    name: 'verification-system-003 a scenario with no turns is rejected',
    fn: () => {
      // The shape `loadScripts` needed. `host-restart-after.json` was
      // `{ "scenario": "host-restart" }` plus three edges: a fragment that named a
      // scenario it was not, could not run alone, and had no way to say so. It loaded
      // clean, which is how the dynamic-loading damage stayed invisible.
      rejects('scenario = "host-restart"\n', 'a scenario declares at least one turn');
      rejects('scenario = "p"\nflow = [ { prompt = { text = "go" } } ]\n', 'at least one turn');
    },
  },

  {
    name: 'verification-system-003 malformed TOML is reported as a parse failure, not a schema problem',
    fn: () => {
      // The two are different author actions: fix the syntax, versus fix the meaning.
      rejects('scenario = "p"\n[[turn\nid = "a"\n', 'TOML parse failed');
    },
  },

  {
    name: 'verification-system-003 malformed top-level collections fail closed with prefixed schema diagnostics',
    fn: () => {
      // TOML accepts a root table or scalar where the scenario grammar requires an
      // array. Reject before the compiler reaches flatMap, forEach, or map so every
      // invalid source has one fail-closed result rather than a thrown TypeError.
      for (const [source, fragment] of [
        ['scenario = "p"\nflow = "x"\n', 'flow must be an array of tables'],
        ['scenario = "p"\nturn = {}\n', 'turn must be an array of tables'],
        ['scenario = "p"\nfault = {}\n', 'fault must be an array of tables'],
        ['scenario = "p"\nepoch = {}\n', 'epoch must be an array of tables'],
      ]) {
        const result = compile(source);
        assertTrue(!result.ok, `${fragment} must reject`);
        assertTrue(result.scenario === undefined, `${fragment} must not yield a usable scenario`);
        assertTrue(result.problems.every((problem) => problem.startsWith('p.toml: ')), result.problems.join(' | '));
        assertTrue(result.problems.some((problem) => problem.includes(fragment)), result.problems.join(' | '));
      }
    },
  },

  {
    name: 'verification-system-003 setup event ceilings are positive theoretical exact trigger counts',
    fn: () => {
      const withSetup = (setup) => `scenario = "p"
setup = ${setup}
flow = [ { prompt = { text = "go" } } ]

[[turn]]
id = "a"
user = "go"

  [[turn.step]]
  respond = { type = "text", text = "ok" }
`;

      rejects(withSetup('{ maxJournalEvents = 0 }'), 'setup.maxJournalEvents must be a positive integer');
      rejects(withSetup('{ maxSseEvents = -1 }'), 'setup.maxSseEvents must be a positive integer');
      rejects(withSetup('{ maxJournalEvents = 1.5 }'), 'setup.maxJournalEvents must be a positive integer');
      const ok = accepts(withSetup('{ strict = true, maxJournalEvents = 367, maxSseEvents = 1724 }'));
      assertEq(ok.setup.maxJournalEvents, 367);
      assertEq(ok.setup.maxSseEvents, 1724);
    },
  },

  {
    name: 'verification-system-003 a rejected scenario yields no partial result',
    fn: () => {
      // A scenario that half-loads is a scenario whose author believes something is
      // covered that is not — the same failure mode as a dangling fault, one level up.
      const result = compile(minimal('\n[[fault]]\nturn = "nope"\nstep = 0\nattempts = [1]\ndelivery = "provider-error"\n'));

      assertTrue(!result.ok);
      assertTrue(result.scenario === undefined, 'a rejection must not carry a usable scenario');
    },
  },

  {
    name: 'verification-system-003 every problem is prefixed with the file it came from',
    fn: () => {
      // Nineteen scenarios load at once; a bare message would not say which file.
      const result = compile('scenario = "p"\nflow = []\n\n[[turn]]\nid = "a"\n\n  [[turn.step]]\n  respond = {}\n');

      assertTrue(!result.ok);
      assertTrue(
        result.problems.every((problem) => problem.startsWith('p.toml: ')),
        result.problems.join(' | '),
      );
    },
  },
];
