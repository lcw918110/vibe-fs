/**
 * gate-cold-boundary-cases.mjs — every declared seal exception is explicit.
 *
 * ARCH-004 / verification-system-003. The property is not "cold boundaries work" but "a break the
 * scenario did not declare is fatal, and a declaration that never fires is fatal too".
 *
 * Package K1 measured what the alternative costs. The deleted `epochCold` exemption
 * read "tools and the leading system message unchanged" and then admitted any body
 * rewrite — so it passed precisely the mutations it existed to catch, and the canaries
 * stayed green. `design-script-forest.md` §14 lists it among the four sniffed
 * exemptions that could green-light a wrong implementation.
 */

import { assertEq, assertTrue } from './lib.mjs';
import { boundaryFor, sealDecision, validateBoundary } from '../../e2e/support/cold-boundary.js';
import { wireOf } from '../../e2e/support/provider-wire.js';

const SYSTEM = { role: 'system', content: 'You are a coder.' };
const hostSystem = (model) => ({
  role: 'system',
  content: `You are a coder.\nYou are powered by the model named ${model}. The exact model ID is test/${model}`,
});
const user = (text) => ({ role: 'user', content: text });
const assistant = (text) => ({ role: 'assistant', content: text });

const body = (model, messages, tools = ['write']) => ({
  model,
  tools: tools.map((name) => ({ type: 'function', function: { name } })),
  messages,
});

const FIRST = body('test-model', [SYSTEM, user('Round 1')]);
const APPENDED = body('test-model', [SYSTEM, user('Round 1'), assistant('r1'), user('Round 2')]);

/** FALLBACK-004: the model moves, the transcript does not. */
const SIDE_SWITCHED = body('test-model-b', [SYSTEM, user('Round 1'), assistant('r1'), user('Round 2')]);

/** A side switch that also rewrote history — what the old exemption would have passed. */
const SIDE_SWITCHED_AND_REWRITTEN = body('test-model-b', [SYSTEM, user('DIFFERENT'), user('Round 2')]);

/** COMPANION-009: the prefix is replaced by a companion-memory head. */
const EPOCH_REBASED = body('test-model', [SYSTEM, user('[companion memory]'), user('Round 2')]);

/** AGENT-020/PROMPT-012: transcript grows while the typed request changes tools. */
const REQUEST_KIND_SWITCHED = body(
  'test-model',
  [SYSTEM, user('Round 1'), assistant('r1'), user('Compile')],
  ['read', 'write', 'return'],
);

// MANAGER-LOOP: the retired iteration ends with narrative and tool traffic, its
// authority turn carries the guidance occurrence the Host had appended, and (in the
// nudge variant) a non-authority nudge the projection had injected. The next iteration
// keeps every one of those messages, in order, and appends its own fresh-head prompt
// (relay-context-projection-001 retains raw messages, tool calls, results, nudge, late
// parts and the internal loop wake); only the retired guidance occurrence and the
// companion frame may disappear (context-compression-019 / GAP-022). Structural only:
// typed authority-revision retention is proved by the unit projection tests and the
// long-stroke root-only oracle.
const GUIDANCE_SUFFIX = '\0\uFEFF<system>\n# # Wait-cost calibration: priced interval.\n</system>';
// Production memory-preamble (en.md) — line-broken with `# ` prefixes as Host renders it.
const COMPANION_PREAMBLE =
  '# The following Chronicle records carry durable state transitions from an older\n'
  + '# prefix of this session. Continue from what they actually settled and what they\n'
  + '# left open. Raw code, tool mechanics, image contents, and incidental observation\n'
  + '# details may have been removed.';
const MANAGER_TRAFFIC = [
  user(`Round 1${GUIDANCE_SUFFIX}`),
  assistant('assessment evidence'),
  { role: 'tool', tool_call_id: 'review-call', content: 'scores' },
];
const MANAGER_RETIRED = body('test-model', [SYSTEM, ...MANAGER_TRAFFIC]);
// The same messages with the retired occurrence gone and the successor's own prompt appended.
const MANAGER_NEXT = body('test-model', [
  SYSTEM,
  user('Round 1'),
  assistant('assessment evidence'),
  { role: 'tool', tool_call_id: 'review-call', content: 'scores' },
  user('Round 2'),
]);
const MANAGER_RETIRED_WITH_NUDGE = body('test-model', [SYSTEM, ...MANAGER_TRAFFIC, user('work nudge')]);
// Companion frame (synthetic ack + preamble user) may disappear across iterations.
const MANAGER_RETIRED_WITH_COMPANION = body('test-model', [
  SYSTEM,
  user('Round 1'),
  assistant('.'),
  user(`${COMPANION_PREAMBLE}\n# prior work record`),
  assistant('assessment evidence'),
  { role: 'tool', tool_call_id: 'review-call', content: 'scores' },
]);

const decide = (previous, next, boundary = null) =>
  sealDecision({ previousWire: previous === null ? null : wireOf(previous), body: next, boundary });

/** A SOURCE boundary, as an author writes it and `validateBoundary` checks it. */
const at = (kind) => ({ kind, lane: 'coder', turn: 'Round 2', step: 0 });

/**
 * A COMPILED boundary, as `boundaryFor` consumes it: it names the entry it governs.
 *
 * Deliberately a different shape from `at`. Sharing one fixture across both layers is what
 * let the lookup cases assert against author-shaped input the runtime never sees — and hid
 * that the lookup compared DECLARED text to REQUEST text, so every real boundary was inert.
 */
const compiledAt = (kind, entryId = 'round2') => ({ kind, lane: 'coder', entryId });

const entry = (id) => ({ id, lane: 'coder' });

export const coldBoundaryCases = [
  // ── the ordinary case ─────────────────────────────────────────────────────

  {
    name: 'ARCH-004 an append-only continuation keeps the seal',
    fn: () => {
      assertEq(decide(FIRST, APPENDED).held, true);
      assertEq(decide(FIRST, FIRST).held, true, 'an unchanged request keeps the seal');
    },
  },

  {
    name: 'ARCH-004 the first request of a session has nothing to break',
    fn: () => {
      assertEq(decide(null, EPOCH_REBASED).held, true);
    },
  },

  {
    name: 'ARCH-004 an undeclared break is fatal',
    fn: () => {
      // No sniffing. Every shape below is a real cache break, and none of them earns
      // an exemption from looking plausible.
      assertEq(decide(FIRST, EPOCH_REBASED).broken, 'undeclared', 'rewritten prefix');
      assertEq(decide(FIRST, SIDE_SWITCHED).broken, 'undeclared', 'model change alone');
      assertEq(
        decide(FIRST, body('test-model', [SYSTEM, user('Round 1')], ['write', 'read'])).broken,
        'undeclared',
        'tool set change',
      );
      assertEq(
        decide(FIRST, body('test-model', [{ role: 'system', content: 'Other.' }, user('Round 1')])).broken,
        'undeclared',
        'system prompt change',
      );
    },
  },

  {
    name: 'ARCH-004 shrinking the transcript is a break, not a continuation',
    fn: () => {
      // A shorter request means the plugin dropped messages the provider already saw.
      // COMPANION-009 exists to make that explicit rather than silent.
      assertEq(decide(APPENDED, FIRST).broken, 'undeclared');
    },
  },

  // ── COMPANION-009: epoch switch ──────────────────────────────────────────

  {
    name: 'COMPANION-009 a declared epoch switch reseals a rebased prefix',
    fn: () => {
      assertEq(decide(FIRST, EPOCH_REBASED, at('epoch-switch')).resealed, 'epoch-switch');
    },
  },

  // ── FALLBACK-004: side switch is narrower than the old exemption ─────────

  {
    name: 'FALLBACK-004 a declared side switch admits a model change only',
    fn: () => {
      assertEq(decide(FIRST, SIDE_SWITCHED, at('fallback-side')).resealed, 'fallback-side');
    },
  },

  {
    name: 'FALLBACK-004 a side switch may not rewrite the transcript',
    fn: () => {
      // The tightening this file's measurement produced. `modelSideCold` allowed the
      // system prompt to change whenever the model id did — but AGENT-001 gives
      // each canonical role carries ONE byte-identical system prompt (verified for
      // coder/manager/reviewer/devops/inspector), so a real side switch moves the
      // model field and nothing else.
      //
      // Declaring `fallback-side` therefore cannot smuggle a message rewrite past the
      // barrier, which is exactly what the old exemption permitted.
      assertEq(
        decide(FIRST, SIDE_SWITCHED_AND_REWRITTEN, at('fallback-side')).broken,
        'fallback-side-rewrote-messages',
      );
    },
  },

  {
    name: 'FALLBACK-004 a side switch may not change tools either',
    fn: () => {
      const retooled = body('test-model-b', [SYSTEM, user('Round 1')], ['write', 'read']);
      assertEq(decide(FIRST, retooled, at('fallback-side')).broken, 'fallback-side-rewrote-messages');
    },
  },

  // ── CTX-010: prefix probe ────────────────────────────────────────────────

  {
    name: 'CTX-010 a declared prefix probe admits a rebased prefix with fixed system/tools',
    fn: () => {
      // The probe replaces the covered head with the synthetic companion memory and
      // keeps the live tail; the system prompt and the tool set are the attempt's
      // fixed parts (PROMPT-008) and must survive byte-identical.
      const probed = body('test-model-b', [SYSTEM, user('[companion memory]'), user('Round 2')]);
      assertEq(decide(FIRST, probed, at('prefix-probe')).resealed, 'prefix-probe');
    },
  },

  {
    name: 'CTX-010 a prefix probe may not rewrite the tool set',
    fn: () => {
      // The tools belong to the attempt profile (PROMPT-008); a probe that swapped
      // them would be changing what the model may call, not rebasing the covered
      // prefix. The system prompt is deliberately exempt: Host 1.18.9 injects the
      // model name into it (system.ts:67), so a fallback side switch — the usual
      // companion of a recovery attempt — changes the system bytes by construction.
      const retooled = body('test-model-b', [SYSTEM, user('[companion memory]'), user('Round 2')], ['write', 'read']);
      assertEq(
        decide(FIRST, retooled, at('prefix-probe')).broken,
        'prefix-probe-rewrote-fixed',
        'tool rewrite is not a probe',
      );

      // A side switch with a rebased prefix and unchanged tools is exactly the
      // recovery shape, and it is admitted.
      const sideSwitchedProbe = body('test-model-b', [SYSTEM, user('[companion memory]'), user('Round 2')]);
      assertEq(decide(FIRST, sideSwitchedProbe, at('prefix-probe')).resealed, 'prefix-probe');
    },
  },

  {
    name: 'CTX-010 an append-only delivery of a probe entry is legal',
    fn: () => {
      // A recovery sequence alternates probe slots and ordinary slots (FALLBACK-012
      // arms only odd offsets), so the same entry delivers breaking and
      // non-breaking requests. The "never fired" check lives at scenario end.
      const decision = decide(FIRST, APPENDED, at('prefix-probe'));
      assertEq(decision.held, true);
    },
  },

  // ── AGENT-020 / PROMPT-012: typed Student request-kind switch ────────────

  {
    name: 'PROMPT-012 a Student request-kind switch changes only tools',
    fn: () => {
      assertEq(
        decide(FIRST, REQUEST_KIND_SWITCHED, at('request-kind-switch')).resealed,
        'request-kind-switch',
      );
    },
  },

  {
    name: 'PROMPT-012 a request-kind switch may not rewrite the message prefix',
    fn: () => {
      const rewritten = body('test-model', [SYSTEM, user('DIFFERENT'), user('Compile')], [
        'read',
        'write',
        'return',
      ]);
      assertEq(
        decide(FIRST, rewritten, at('request-kind-switch')).broken,
        'request-kind-switch-rewrote-prefix',
      );
    },
  },

  // ── pure manager loop ───────────────────────────────────────────────────

  {
    name: 'MANAGER-LOOP a new iteration keeps the retained history and retires only its injection',
    fn: () => {
      assertEq(decide(MANAGER_RETIRED, MANAGER_NEXT, at('manager-loop')).resealed, 'manager-loop');
      assertEq(
        decide(MANAGER_RETIRED_WITH_COMPANION, MANAGER_NEXT, at('manager-loop')).resealed,
        'manager-loop',
        'production Chronicle-records companion frames may disappear across iterations',
      );
      assertEq(
        decide(MANAGER_RETIRED_WITH_NUDGE, MANAGER_NEXT, at('manager-loop')).broken,
        'manager-loop-rewrote-fixed',
        'a retained nudge may not be dropped: relay-context-projection-001 keeps it',
      );

      const beforeFallback = body('test-model', [hostSystem('test-model'), ...MANAGER_TRAFFIC]);
      const afterFallback = body('test-model-b', [
        hostSystem('test-model-b'),
        user('Round 1'),
        assistant('assessment evidence'),
        { role: 'tool', tool_call_id: 'review-call', content: 'scores' },
        user('Round 2'),
      ]);
      assertEq(
        decide(beforeFallback, afterFallback, at('manager-loop')).resealed,
        'manager-loop',
        'an exact Host model banner change cannot masquerade as a history rewrite',
      );
    },
  },

  {
    name: 'MANAGER-LOOP a new iteration fails when retained history is rewritten, dropped or padded',
    fn: () => {
      // Dropping the retained tool traffic is no longer an iteration; it is a rewrite.
      const leakedAssistant = body('test-model', [SYSTEM, user('Round 1'), assistant('assessment evidence')]);
      assertEq(
        decide(MANAGER_RETIRED, leakedAssistant, at('manager-loop')).broken,
        'manager-loop-rewrote-fixed',
      );

      const leakedTool = body('test-model', [
        SYSTEM,
        user('Round 1'),
        { role: 'tool', tool_call_id: 'review-call', content: 'scores' },
      ]);
      assertEq(
        decide(MANAGER_RETIRED, leakedTool, at('manager-loop')).broken,
        'manager-loop-rewrote-fixed',
      );

      // A novel user wake inside the retained prefix is exactly what the contract forbids.
      const wakePrompt = body('test-model', [SYSTEM, user('Round 1'), user('continue the Road')]);
      assertEq(
        decide(MANAGER_RETIRED, wakePrompt, at('manager-loop')).broken,
        'manager-loop-rewrote-fixed',
      );

      const paddedPrefix = body('test-model', [
        SYSTEM,
        user('Round 1'),
        user('a synthetic wake'),
        assistant('assessment evidence'),
        { role: 'tool', tool_call_id: 'review-call', content: 'scores' },
      ]);
      assertEq(
        decide(MANAGER_RETIRED, paddedPrefix, at('manager-loop')).broken,
        'manager-loop-rewrote-fixed',
      );
    },
  },

  {
    name: 'MANAGER-LOOP a new iteration fails when authority or plan changes',
    fn: () => {
      const rewritten = body('test-model', [SYSTEM, user('DIFFERENT')]);
      assertEq(
        decide(MANAGER_RETIRED, rewritten, at('manager-loop')).broken,
        'manager-loop-rewrote-fixed',
      );

      const dropped = body('test-model', [SYSTEM]);
      assertEq(
        decide(MANAGER_RETIRED, dropped, at('manager-loop')).broken,
        'manager-loop-rewrote-fixed',
      );

      const rewrittenRoot = body('test-model', [
        SYSTEM,
        user('DIFFERENT'),
        user('Round 1'),
      ]);
      assertEq(
        decide(MANAGER_RETIRED, rewrittenRoot, at('manager-loop')).broken,
        'manager-loop-rewrote-fixed',
      );

      const retooled = body('test-model', MANAGER_NEXT.messages, ['write', 'read']);
      assertEq(
        decide(MANAGER_RETIRED, retooled, at('manager-loop')).broken,
        'manager-loop-rewrote-fixed',
      );

      const resystemed = body('test-model', [{ role: 'system', content: 'Other.' }, user('Round 1')]);
      assertEq(
        decide(MANAGER_RETIRED, resystemed, at('manager-loop')).broken,
        'manager-loop-rewrote-fixed',
      );
    },
  },

  {
    name: 'MANAGER-LOOP a reusable step-0 entry establishes the seal then reseals',
    fn: () => {
      assertEq(decide(null, MANAGER_NEXT, at('manager-loop')).held, true, 'initial delivery establishes the seal');
      assertEq(
        decide(MANAGER_NEXT, MANAGER_NEXT, at('manager-loop')).held,
        true,
        'an append-only retry on the same entry stays held',
      );
      const continued = body('test-model', [...MANAGER_NEXT.messages, assistant('declared step 0')]);
      assertEq(
        decide(MANAGER_NEXT, continued, at('manager-loop')).held,
        true,
        'an append-only continuation stays held until the restart breaks it',
      );
    },
  },

  // ── a declaration that never fires is also fatal ─────────────────────────

  {
    name: 'verification-system-003 a declared boundary that did not break is fatal',
    fn: () => {
      // Same reasoning as an empty `attempts` list: the author believes a cold
      // boundary is covered, and the scenario silently stopped exercising it. Treating
      // it as harmless is how a scenario decays into an assertion about nothing.
      // `manager-loop` is absent here on purpose: a reusable step-0 entry
      // legitimately mixes held retries with one breaking restart, so held is
      // legal for it (see the case above) and never `boundary-not-reached`.
      for (const kind of ['epoch-switch', 'fallback-side']) {
        const decision = decide(FIRST, APPENDED, at(kind));
        assertEq(decision.broken, 'boundary-not-reached', `${kind} declared but seal held`);
        assertEq(decision.kind, kind, 'the diagnostic names which declaration went unused');
      }
    },
  },

  {
    name: 'verification-system-003 a boundary declared on the first request is unreachable',
    fn: () => {
      // Nothing is sealed yet, so no break can happen there. Accepting it silently
      // would make the declaration decorative.
      assertEq(decide(null, FIRST, at('epoch-switch')).broken, 'boundary-not-reached');
    },
  },

  // ── declaration lookup is keyed, never inferred ──────────────────────────

  {
    name: 'verification-system-003 a boundary governs exactly the step it names',
    fn: () => {
      const boundaries = [compiledAt('epoch-switch')];

      assertTrue(boundaryFor(boundaries, entry('round2')) !== null, 'the named step');
      assertTrue(boundaryFor(boundaries, entry('round2step1')) === null, 'another step');
      assertTrue(boundaryFor(boundaries, entry('round3')) === null, 'another turn');
      assertTrue(boundaryFor(boundaries, undefined) === null, 'an unresolved request has no boundary');
    },
  },

  {
    name: 'verification-system-003 a boundary cannot spread to a declaration sharing its prefix',
    fn: () => {
      // A prefix-matched boundary would excuse every later turn that happens to start with
      // the same words — one declaration silently covering a whole conversation.
      //
      // This used to be asserted by comparing text with `===`, and that was the defect: the
      // lookup compared its DECLARED turn against the REQUEST turn, and a declaration is a
      // prefix, so they matched only when the author wrote the utterance out in full. Every
      // cold boundary in every real scenario was inert, and this case passed because its
      // fixtures did exactly that.
      //
      // Naming the entry makes it structural: `resolveEntry` picks one declaration, and the
      // boundary either names it or does not.
      const boundaries = [compiledAt('epoch-switch', 'round')];

      assertTrue(boundaryFor(boundaries, entry('round')) !== null);
      assertTrue(boundaryFor(boundaries, entry('round2')) === null, 'a boundary must not spread by prefix');
    },
  },

  {
    name: 'verification-system-003 two boundaries for one key is an error, not a precedence question',
    fn: () => {
      const boundaries = [compiledAt('epoch-switch'), compiledAt('fallback-side')];

      let threw = null;
      try {
        boundaryFor(boundaries, entry('round2'));
      } catch (error) {
        threw = error.message;
      }

      assertTrue(threw !== null, 'a duplicate declaration must throw');
      assertTrue(threw.includes('epoch-switch') && threw.includes('fallback-side'), 'the message names both');
    },
  },

  // ── load-time validation ─────────────────────────────────────────────────

  {
    name: 'ARCH-004 only the named boundary kinds exist',
    fn: () => {
      assertEq(validateBoundary(at('epoch-switch')).length, 0);
      assertEq(validateBoundary(at('fallback-side')).length, 0);
      assertEq(validateBoundary(at('prefix-probe')).length, 0);
      assertEq(validateBoundary(at('frame-commit')).length, 0);
      assertEq(validateBoundary(at('request-kind-switch')).length, 0);
      assertEq(validateBoundary(at('manager-loop')).length, 0);

      // An unlisted kind is rejected without naming ban-test-style examples:
      // positive behavior above is the proof.
      const problems = validateBoundary({ ...at('epoch-switch'), kind: 'not-a-boundary' });
      assertEq(problems.length, 1, 'an unknown kind must be rejected');
      assertTrue(problems[0].includes('unknown cold boundary kind'), problems[0]);
    },
  },

  {
    name: 'verification-system-003 a boundary must name a turn and a step',
    fn: () => {
      assertTrue(validateBoundary({ kind: 'epoch-switch', step: 0 }).some((p) => p.includes('name the turn')));
      assertTrue(validateBoundary({ kind: 'epoch-switch', turn: '', step: 0 }).some((p) => p.includes('name the turn')));
      assertTrue(
        validateBoundary({ kind: 'epoch-switch', turn: 'x', step: -1 }).some((p) => p.includes('non-negative')),
      );
    },
  },
];
