/**
 * gate-runtime-key-cases.mjs — the scenario lookup key is a pure function.
 *
 * Three properties, each of which the old matcher broke:
 *
 *   pure       the answer depends on the request, not on how many came before
 *   prefix     longest declared prefix wins, and ties are author errors
 *   countable  `step` is read off the request, never accumulated
 *
 * `design-script-forest.md` §3-§4 measured the alternative: an eleven-predicate
 * conjunction can match several edges at once, so a `specificity` score summed
 * substring lengths and added magic numbers (`afterToolResult === true` → +50) to
 * pick one. That is not disambiguation, it is a tiebreak over a key that should not
 * have ties.
 */

import { assertEq, assertTrue } from './lib.mjs';
import { kindOf, lanesOf, resolveEntry, runtimeKeyOf, sessionIdOf, stepOf, turnOf } from '../../e2e/support/runtime-key.js';
import { forkAnchor, forkRelay } from '../../e2e/support/production.js';
import * as providerProjection from '../../../../../dist/Participant/Provider/Projection/Surface.js';
// HOST-013: production constants read from the build artifact, so the step
// cases exercise the real marker text and source, not a copy.
import {
  source as pairProgrammingThoughtSource,
  text as pairProgrammingThoughtText,
} from '../../../../../dist/OpenCode/Host/PairProgrammingThoughtSurface.js';
import { readText } from '../../../../../dist/Participant/Provider/LanguageSurface.js';

const SESSION = 'ses_real_1';
const BINDINGS = new Map([
  ['manager', SESSION],
  ['coder-after', 'ses_real_2'],
]);

const user = (text) => ({ role: 'user', content: text });
const assistant = (text) => ({ role: 'assistant', content: text });
const toolResult = (content) => ({ role: 'tool', tool_call_id: 'c1', content });
const toolCall = (name, args = '{}') => ({
  role: 'assistant',
  content: null,
  tool_calls: [{ id: 'c1', type: 'function', function: { name, arguments: args } }],
});

const request = (messages) => ({ messages });

const entry = ({ id, turn, step = 0, lane = 'manager' }) => ({ id, lane, turn, step });

/**
 * The anchor a forked-child declaration uses, taken from production rather than copied.
 *
 * It is the one unconditional instruction every forked child receives — the report-format line
 * (`ForkChildPayload.BaseInstructions`). It is NOT the first line of the wire: the header leads
 * with the `Assignment` comment block and this line follows it (`ForkChildPayload.render`), so a
 * declaration names the assignment comment first and this anchor second.
 *
 * It used to be a literal copy of `HostForkRuntimeFork.fs:98`'s first envelope line. That copy was a
 * mirror: N3 deleted the envelope and the constant would have kept describing text no longer sent,
 * while these cases stayed green — proving the matcher against a shape production had stopped
 * producing. Reading the real value makes a future rewording fail here instead.
 */
const ANCHOR = forkAnchor();

/** Production's own renderer, so a declaration is tested against bytes a child will actually see. */
const forkPrompt = (assignment, requirements = []) => forkRelay(assignment, undefined, requirements);

/** `prompt.ts:235` prepends this as `messages[0]`, then appends the real conversation. */
const titleRequest = (text) =>
  request([{ role: 'user', content: 'Generate a title for this conversation:\n' }, user(text)]);

/** Wire tools list in the shape `extractToolNames` reads from the provider body. */
const withTools = (body, names) => ({
  ...body,
  tools: names.map((name) => ({ name })),
});

export const runtimeKeyCases = [
  // ── kind: the fourth component, and why turn alone cannot carry it ────────

  {
    name: 'verification-system-003 a title request and its chat turn share turn and step',
    fn: () => {
      // The measurement that forced `kind` into the key. The Host prepends its title
      // marker at `messages[0]` and appends the whole conversation after it
      // (`../opencode/packages/opencode/src/session/prompt.ts:235`), while `turnOf`
      // reads the LAST user message — so both requests report the same turn.
      //
      // Without a fourth component a title edge and a chat edge for one turn collide,
      // and the load-time duplicate check would reject a legitimate scenario.
      const title = titleRequest('Ship the parser fix.');
      const chat = request([user('Ship the parser fix.')]);

      assertEq(turnOf(title), turnOf(chat), 'turn cannot tell them apart');
      assertEq(stepOf(title), stepOf(chat), 'nor can step');
      assertEq(kindOf(title), 'title');
      assertEq(kindOf(chat), 'chat');
    },
  },

  {
    name: 'verification-system-003 kind is read from the preamble, not from position 0',
    fn: () => {
      // This case previously asserted the marker must be at `messages[0]` and cited
      // `prompt.ts:235` for it. Measured against a live Host in K9, a real title request is:
      //
      //   roles   ["system", "user", "user"]
      //   [0]     "You are a title generator. You output ONLY a thread title…"
      //   [1]     "Generate a title for this conversation:\n"
      //
      // The title agent's system prompt comes first. So the old assertion was not merely
      // incomplete — it pinned the WRONG position, every title request classified as `chat`,
      // and no title turn could ever match. The case passed because its fixture built the
      // shape the code expected.
      assertEq(kindOf(titleRequest('Ship it.')), 'title', 'marker after a system preamble');
      assertEq(
        kindOf(request([
          { role: 'system', content: 'You are a title generator.' },
          user('Generate a title for this conversation:\n'),
          user('Ship it.'),
        ])),
        'title',
        'the measured production shape',
      );

      // Still bounded: the marker is a preamble, so a real turn cannot push it back. A user
      // quoting the phrase deep in a conversation is having an ordinary chat turn, and
      // answering it with a title would be the prose-matching this avoids.
      assertEq(
        kindOf(request([user('a'), user('b'), user('c'), user('d'), user('Generate a title for this conversation:')])),
        'chat',
        'beyond the preamble it is just text',
      );
      assertEq(kindOf(request([])), 'chat');
    },
  },

  {
    name: 'verification-system-003 there is no synthetic kind',
    fn: () => {
      // The old classifier had one, decided by `NUDGE_MARKERS` — production prompt
      // sentences copied into the mock, which the extinction list condemns as a
      // cross-product dead heuristic. It is unnecessary: a nudge's LAST user message IS
      // the nudge sentence, so `turnOf` already distinguishes it.
      const nudge = request([
        user('Ship it.'),
        assistant('r1'),
        user('There are still incomplete todos. Continue working through the remaining items.'),
      ]);

      assertEq(kindOf(nudge), 'chat', 'a nudge is an ordinary chat request');
      assertEq(turnOf(nudge), 'There are still incomplete todos. Continue working through the remaining items.');
    },
  },

  {
    name: 'verification-system-003 kind partitions declarations, and defaults to chat',
    fn: () => {
      const entries = [
        { id: 'chat', lane: 'manager', turn: 'Ship it.', step: 0 },
        { id: 'title', lane: 'manager', kind: 'title', turn: 'Ship it.', step: 0 },
      ];

      assertEq(resolveEntry(request([user('Ship it.')]), entries, BINDINGS, { sessionId: SESSION }).matched?.id, 'chat');
      assertEq(resolveEntry(titleRequest('Ship it.'), entries, BINDINGS, { sessionId: SESSION }).matched?.id, 'title');

      // An undeclared kind means chat, so single-lane scenarios need not say so.
      assertEq(runtimeKeyOf(request([user('Ship it.')]), BINDINGS, { sessionId: SESSION }).kind, 'chat');
    },
  },

  // ── step is a property of the request ─────────────────────────────────────

  {
    name: 'verification-system-003 step counts assistant messages after the last user message',
    fn: () => {
      // The Host appends exactly one assistant message per provider step
      // (`../opencode/packages/opencode/src/session/prompt.ts:1186`), so this is
      // countable rather than something the mock has to remember.
      assertEq(stepOf(request([user('go')])), 0, 'first provider step of a turn');
      assertEq(stepOf(request([user('go'), assistant('r1')])), 1, 'after one reply');
      assertEq(stepOf(request([user('go'), toolCall('fork'), toolResult('ok'), assistant('r2')])), 2, 'two replies');
    },
  },

  {
    name: 'verification-system-003 a new user message resets step',
    fn: () => {
      // Turn boundaries are where step restarts. A cursor would have kept counting.
      assertEq(stepOf(request([user('go'), assistant('r1'), user('again')])), 0);
      assertEq(stepOf(request([user('go'), assistant('r1'), user('again'), assistant('r2')])), 1);
    },
  },

  {
    name: 'verification-system-003 step is zero when there is no user message at all',
    fn: () => {
      assertEq(stepOf(request([assistant('r1')])), 0);
      assertEq(stepOf(request([])), 0);
      assertEq(stepOf({}), 0);
    },
  },

  {
    name: 'verification-system-003 tool results do not count as steps',
    fn: () => {
      // Only assistant messages are provider steps. Counting tool results would
      // double-count a single step that happened to call a tool.
      assertEq(stepOf(request([user('go'), toolResult('a'), toolResult('b')])), 0);
      assertEq(stepOf(request([user('go'), toolCall('fork'), toolResult('a')])), 1);
    },
  },

  {
    name: 'HOST-013 the pair-programming thought marker never counts as a step',
    fn: () => {
      // The marker is a synthetic assistant message, not a provider step. Current
      // empty-name skill wire and legacy marker shapes must all be skipped, while a
      // real non-empty skill call remains an ordinary provider step.
      const rawShape = {
        role: 'assistant',
        info: { source: pairProgrammingThoughtSource },
        parts: [{ type: 'tool', tool: 'skill', state: { status: 'completed', input: { name: '' }, output: pairProgrammingThoughtText } }],
      };
      const legacyRawShape = {
        role: 'assistant',
        info: { source: pairProgrammingThoughtSource },
        parts: [{ type: 'tool', tool: 'auto-injected', state: { status: 'completed', output: pairProgrammingThoughtText } }],
      };
      const legacySourceShape = {
        role: 'assistant',
        info: { source: 'pair-programming-thought' },
      };
      const contentShape = {
        role: 'assistant',
        content: [{ type: 'tool', tool: 'skill', state: { status: 'completed', input: { name: '' }, output: pairProgrammingThoughtText } }],
      };
      const pendingFakeReq = {
        role: 'assistant',
        parts: [{ type: 'tool', tool: 'skill', state: { status: 'pending', input: { name: '' } } }],
      };
      const completedFakeResp = {
        role: 'assistant',
        parts: [{ type: 'tool', tool: 'skill', state: { status: 'completed', input: { name: '' }, output: pairProgrammingThoughtText } }],
      };
      const openAiFakeReq = toolCall('skill', '{"name":""}');
      const openAiLegacyHyphenReq = toolCall('-');
      const openAiLegacyFakeReq = toolCall('auto-injected');

      for (const marker of [rawShape, legacyRawShape, legacySourceShape, contentShape, pendingFakeReq, completedFakeResp, openAiFakeReq, openAiLegacyHyphenReq, openAiLegacyFakeReq]) {
        assertEq(stepOf(request([user('go'), marker])), 0, 'marker alone is not a step');
        assertEq(
          stepOf(request([user('go'), marker, assistant('r1')])),
          1,
          'marker before a real reply does not shift the count',
        );
      }

      // Probe: synthetic empty-name skill does not count; a real skill does.
      assertEq(stepOf(request([user('go'), openAiFakeReq])), 0, 'OpenAI empty-name skill FakeReq is step 0');
      assertEq(stepOf(request([user('go'), toolCall('skill', '{"name":"pdfs"}')])), 1, 'real non-empty skill call is a step');

      // Measured failure: FakeReq+FakeResp both assistant halves around a real
      // tool batch must not shift stepOf — only the real assistant counts.
      assertEq(
        stepOf(request([
          user('go'),
          pendingFakeReq,
          completedFakeResp,
          toolCall('fork'),
          toolResult('ok'),
          assistant('r2'),
        ])),
        2,
        'FakeReq+FakeResp around a real assistant still yield the real step count',
      );

      // Measured OpenAI sequence: user + real toolCall + fakeReq tool_calls + tool
      // + real toolCall + fakeReq + tool counts only the real assistants.
      assertEq(
        stepOf(request([
          user('go'),
          toolCall('fork'),
          openAiFakeReq,
          toolResult('ok'),
          toolCall('fork-manager'),
          openAiFakeReq,
          toolResult('ok'),
        ])),
        2,
        'OpenAI FakeReq halves must not inflate step around real tool_calls',
      );

      // Mixed real + auto-injected tool_calls still counts as a real step.
      assertEq(
        stepOf(request([
          user('go'),
          {
            role: 'assistant',
            content: null,
            tool_calls: [
              { id: 'c1', type: 'function', function: { name: 'fork', arguments: '{}' } },
              { id: 'c2', type: 'function', function: { name: 'auto-injected', arguments: '{}' } },
            ],
          },
        ])),
        1,
        'mixed real+synthetic tool_calls still counts',
      );

      // A real assistant message quoting the same sentence is NOT a marker.
      assertEq(
        stepOf(request([user('go'), assistant(`prefix ${pairProgrammingThoughtText} suffix`)])),
        1,
        'an assistant message containing the sentence still counts as a step',
      );
    },
  },

  {
    name: 'verification-system-003 reading the key twice gives the same answer',
    fn: () => {
      // Purity, stated directly. The old `pathCursor` advanced on observation, so
      // asking twice moved the answer.
      const body = request([user('go'), assistant('r1')]);
      const first = runtimeKeyOf(body, BINDINGS, { sessionId: SESSION });
      const second = runtimeKeyOf(body, BINDINGS, { sessionId: SESSION });

      assertEq(first.lane, second.lane);
      assertEq(first.turn, second.turn);
      assertEq(first.step, second.step);
    },
  },

  // ── turn is prefix-comparable semantic text ──────────────────────────────

  {
    name: 'verification-system-003 a shorter utterance is a string prefix of a longer one',
    fn: () => {
      // The property longest-prefix matching rests on. `renderSemantic` would fail
      // it: its closing `}]}]}` sits after the text, so no shorter utterance is ever
      // a prefix and the rule silently degrades to whole-string equality.
      const short = turnOf(request([user('Fix the bug')]));
      const long = turnOf(request([user('Fix the bug in parser')]));

      assertEq(short, 'Fix the bug');
      assertTrue(long.startsWith(short), 'longer utterance must extend the shorter one');
    },
  },

  {
    name: 'verification-system-003 turn reads the LAST user message',
    fn: () => {
      // A conversation carries many user messages; the one being answered is the
      // last. Matching on the first would pin every step of a session to turn one.
      assertEq(turnOf(request([user('first'), assistant('r1'), user('second')])), 'second');
    },
  },

  {
    name: 'verification-system-003 turn is null when no user message exists',
    fn: () => {
      assertEq(turnOf(request([assistant('r1')])), null);
      assertEq(turnOf(request([])), null);
    },
  },

  {
    name: 'verification-system-003 turn is not truncated',
    fn: () => {
      // `extractLastUserMsg` sliced at 2000 characters, so two long prompts became
      // identical whenever they differed only past the cut — and a prompt long
      // enough to be truncated is exactly the kind a scenario needs to distinguish.
      const head = 'x'.repeat(2000);
      const a = turnOf(request([user(`${head}ALPHA`)]));
      const b = turnOf(request([user(`${head}BETA`)]));

      assertTrue(a !== b, 'two prompts differing past 2000 chars must not collapse');
      assertEq(a.length, 2005);
    },
  },

  {
    name: 'verification-system-003 prose can never be confused with a tool call',
    fn: () => {
      // Non-prose parts are tagged with `\u001f`, which prose cannot contain. Without
      // the tag, a scenario declaring the text `fork` would match a tool call to
      // `fork` — content and structure would share one namespace.
      //
      // `turnOf` only projects the last user message, so the tool-call half must be
      // taken from the same `messageText` path applied to the tool-call message
      // itself — not from a later user utterance that never contains the call.
      const prose = turnOf(request([user('fork')]));
      const projected = providerProjection.semanticProjection([
        {
          role: 'assistant',
          parts: [{ kind: 'tool-call', callId: 'c1', name: 'fork', args: '{}' }],
        },
      ]);
      const call = projected.messages.length === 0 ? null : projected.messages[0].parts[0];

      assertEq(prose, 'fork');
      assertEq(call?.kind, 'tool-call');
      assertEq(call?.name, 'fork');
      assertEq(call?.args, '{}');
      assertTrue(call !== prose, 'tool call text must be distinguishable from prose');
    },
  },

  // ── an ordered fragment declaration ──────────────────────────────────────

  {
    name: 'REVIEW-002 a fragment declaration reaches text production put after the anchor',
    fn: () => {
      // Measured in K9, and the reason ordered fragments exist. When a Manager forks a child,
      // production composes the prompt (`src/Wanxiangshu/Domain/ForkChildPayload.fs`): the
      // `Assignment` comment block leads the header, then the unconditional report-format line,
      // then the optional interpretive lines when present.
      //
      // So the text a scenario knows IS the prefix of what arrives — the `# ` comment marker
      // included — and the report-format line sits after it. Every forked-child turn in the forest
      // failed to match before this, because the old envelope's first line was conditional.
      //
      // Driven through production's own renderer rather than a hand-built string: a local template
      // would keep passing after the real one changed shape.
      const entries = [
        entry({ id: 'revise', lane: 'manager', turn: ['# Review current worktree', ANCHOR] }),
        entry({ id: 'perfect', lane: 'manager', turn: ['# Re-review the fixed tree', ANCHOR] }),
      ];

      const revise = forkPrompt('Review current worktree', ['Ship it.']);
      const perfect = forkPrompt('Re-review the fixed tree', ['Ship it.']);

      assertEq(resolveEntry(request([user(revise)]), entries, BINDINGS, { sessionId: SESSION }).matched.id, 'revise');
      assertEq(resolveEntry(request([user(perfect)]), entries, BINDINGS, { sessionId: SESSION }).matched.id, 'perfect');
    },
  },

  {
    name: 'REVIEW-002 one declaration matches whether the optional fields are present or absent',
    fn: () => {
      // The property N3 bought, and the one the four-shape envelope made impossible. A scenario
      // author knows a child was forked and what the assignment said; they do not know whether the
      // parent had produced a work record yet, and must not have to.
      const entries = [entry({ id: 'child', lane: 'manager', turn: ['# Write proof.txt', ANCHOR] })];

      const shapes = [
        forkRelay('Write proof.txt', undefined, []),
        forkRelay('Write proof.txt', 'B says background.', []),
        forkRelay('Write proof.txt', undefined, ['Ship it.']),
        forkRelay('Write proof.txt', 'B says background.', ['Ship it.']),
      ];

      for (const [index, text] of shapes.entries()) {
        const resolved = resolveEntry(request([user(text)]), entries, BINDINGS, { sessionId: SESSION });
        assertEq(resolved.matched?.id, 'child', `shape ${index} did not match the single declaration`);
      }
    },
  },

  {
    name: 'REVIEW-002 fragment 0 is anchored, so a wrapper that is absent does not match',
    fn: () => {
      // Anchoring is the first of the three properties that separate this from the retired
      // `containsText`. Without it a fragment list would be a bag of substrings free to match
      // anywhere, which is what needed `specificity` scoring to disambiguate.
      const entries = [entry({ id: 'a', turn: ['# Review current worktree', ANCHOR] })];

      const noWrapper = resolveEntry(request([user('Review current worktree now')]), entries, BINDINGS, { sessionId: SESSION });
      assertTrue(noWrapper.unmatched !== undefined, 'the anchor must be a true prefix');
    },
  },

  {
    name: 'REVIEW-002 fragments must occur in the declared order',
    fn: () => {
      // The second property. A bag would match these in any arrangement; an ordered list
      // says "after the wrapper", which is what makes the declaration describe a shape rather
      // than a set of coincidences.
      const entries = [entry({ id: 'a', turn: ['HEAD', 'middle', 'tail'] })];

      assertEq(resolveEntry(request([user('HEAD then middle then tail')]), entries, BINDINGS, { sessionId: SESSION }).matched.id, 'a');

      const reordered = resolveEntry(request([user('HEAD then tail then middle')]), entries, BINDINGS, { sessionId: SESSION });
      assertTrue(reordered.unmatched !== undefined, 'out of order is not a match');
    },
  },

  {
    name: 'REVIEW-002 fragments do not overlap: a later one starts after the previous ends',
    fn: () => {
      // `indexOf(fragment, cursor)` where the cursor sits past the previous fragment. Reusing
      // the same characters twice would let `["abc", "bc"]` match "abc" — a declaration
      // claiming more text than the request contains.
      const entries = [entry({ id: 'a', turn: ['abc', 'bc'] })];

      assertTrue(resolveEntry(request([user('abc')]), entries, BINDINGS, { sessionId: SESSION }).unmatched !== undefined);
      assertEq(resolveEntry(request([user('abcbc')]), entries, BINDINGS, { sessionId: SESSION }).matched.id, 'a');
    },
  },

  {
    name: 'REVIEW-002 total declared length decides, not the span it covers',
    fn: () => {
      // The third property, and the one that keeps ties meaningful. Weighing by span — from
      // the start of the first fragment to the end of the last — would make a declaration
      // look MORE specific for skipping more text it never named. Summing what is actually
      // declared keeps "more declared text" equal to "more specific".
      const entries = [
        entry({ id: 'span', turn: ['HEAD', 'z'] }),
        entry({ id: 'declared', turn: ['HEAD', 'middle'] }),
      ];

      // 'span' covers to the very end of the turn; 'declared' names more characters.
      assertEq(
        resolveEntry(request([user('HEAD then middle then z')]), entries, BINDINGS, { sessionId: SESSION }).matched.id,
        'declared',
      );
    },
  },

  {
    name: 'REVIEW-003 two fragment declarations of equal weight are an author error',
    fn: () => {
      // Same rule as two equal-length prefixes: a tie is not something to break, it means the
      // scenario does not say what the model does next.
      const entries = [
        entry({ id: 'left', turn: ['HEAD', 'xy'] }),
        entry({ id: 'right', turn: ['HEAD', 'yz'] }),
      ];

      const resolved = resolveEntry(request([user('HEAD xy yz')]), entries, BINDINGS, { sessionId: SESSION });
      assertEq(resolved.ambiguousTurn?.length, 2, JSON.stringify(resolved.matched ?? resolved.unmatched));
    },
  },

  // ── lane comes from the durable binding, never from a guess ──────────────

  {
    name: 'HOST-008 lane resolves through the session binding',
    fn: () => {
      assertEq([...lanesOf(request([user('go')]), BINDINGS, { sessionId: SESSION })].join(), 'manager');
      assertEq([...lanesOf(request([user('go')]), BINDINGS, { sessionId: 'ses_real_2' })].join(), 'coder-after');
    },
  },

  {
    name: 'HOST-008 production bindings shape is alias to a set of session ids',
    fn: () => {
      // Production binds alias → Set of session ids: one lane can own several concurrent
      // threads (e.g. a Reviewer forked before and after a rebase). Unit fixtures use a
      // plain string, so the Set form must be exercised here or `lanesOf`/`resolveEntry`
      // only ever see the fixture shape.
      const reviewerSessions = new Set(['ses_rev_1', 'ses_rev_2']);
      const bindings = new Map([
        ['reviewer', reviewerSessions],
        ['manager', new Set([SESSION])],
      ]);

      assertEq([...lanesOf(request([user('go')]), bindings, { sessionId: 'ses_rev_1' })].join(), 'reviewer');
      assertEq([...lanesOf(request([user('go')]), bindings, { sessionId: 'ses_rev_2' })].join(), 'reviewer');
      assertEq(lanesOf(request([user('go')]), bindings, { sessionId: 'ses_other' }).size, 0);
      assertEq([...lanesOf(request([user('go')]), bindings, { sessionId: SESSION })].join(), 'manager');

      const entries = [
        entry({ id: 'rev-a', lane: 'reviewer', turn: 'Review A' }),
        entry({ id: 'rev-b', lane: 'reviewer', turn: 'Review B' }),
        entry({ id: 'mgr', lane: 'manager', turn: 'Review A' }),
      ];
      assertEq(
        resolveEntry(request([user('Review A')]), entries, bindings, { sessionId: 'ses_rev_2' }).matched?.id,
        'rev-a',
      );
      assertEq(
        resolveEntry(request([user('Review B')]), entries, bindings, { sessionId: 'ses_rev_1' }).matched?.id,
        'rev-b',
      );
      assertEq(
        resolveEntry(request([user('Review A')]), entries, bindings, { sessionId: SESSION }).matched?.id,
        'mgr',
      );
      assertTrue(
        resolveEntry(request([user('Review A')]), entries, bindings, { sessionId: 'ses_other' }).matched === undefined,
        'a session outside the set must not resolve the lane',
      );
    },
  },

  {
    name: 'HOST-008 an unbound session yields no lane, not a guess',
    fn: () => {
      // The mock cannot know which alias an unbound session belongs to. Inventing
      // one would answer a question only the durable association can answer.
      assertEq(lanesOf(request([user('go')]), BINDINGS, { sessionId: 'ses_unknown' }).size, 0);
      assertEq(lanesOf({ messages: [] }, BINDINGS, { sessionId: 'ses_unknown' }).size, 0);
      assertEq(lanesOf(request([user('go')]), undefined, { sessionId: SESSION }).size, 0);
    },
  },

  {
    name: 'HOST-008 one session with two aliases yields both, not the first',
    fn: () => {
      // Measured in K9, and it was an impurity hiding inside the function that names the
      // key. Every converted scenario binds two aliases to its primary session — the Host
      // titles a session on the same id it chats on, so
      // `bind = ["inspector-title", "inspector"]` is the norm rather than an edge case.
      //
      // A reverse lookup returning the FIRST match made the answer depend on Map insertion
      // order: same request, same bindings, different lane depending on which alias the
      // driver happened to register first. `process-stress` failed to resolve its title
      // request for exactly this reason.
      const shared = 'ses_shared';
      const titleFirst = new Map([['inspector-title', shared], ['inspector', shared]]);
      const chatFirst = new Map([['inspector', shared], ['inspector-title', shared]]);
      const body = request([user('go')]);

      assertEq([...lanesOf(body, titleFirst, { sessionId: shared })].sort().join('|'), 'inspector|inspector-title');
      assertEq(
        [...lanesOf(body, chatFirst, { sessionId: shared })].sort().join('|'),
        [...lanesOf(body, titleFirst, { sessionId: shared })].sort().join('|'),
        'insertion order may not change the answer',
      );
    },
  },

  {
    name: 'HOST-008 kind separates the title entry from the chat entry on one session',
    fn: () => {
      // The consequence of the above: both entries are at the same session and the same
      // step, so `kind` is what tells them apart. That is what it was added to the key for.
      const shared = 'ses_shared';
      const bindings = new Map([['t', shared], ['c', shared]]);
      const entries = [
        { id: 'chat.0', lane: 'c', kind: 'chat', turn: 'Ship it.', step: 0 },
        { id: 'title.0', lane: 't', kind: 'title', turn: 'Ship it.', step: 0 },
      ];

      assertEq(resolveEntry(request([user('Ship it.')]), entries, bindings, { sessionId: shared }).matched.id, 'chat.0');
      assertEq(
        resolveEntry(titleRequest('Ship it.'), entries, bindings, { sessionId: shared }).matched.id,
        'title.0',
      );
    },
  },

  {
    name: 'verification-system-003 the session id comes from explicit runtime context',
    fn: () => {
      // Session identity is routing context supplied by the provider adapter. It is not part
      // of semantic request content and must not be inferred from a request body.
      assertEq(sessionIdOf({ sessionId: 'ses_a' }), 'ses_a');
      assertEq(sessionIdOf({ sessionId: 'ses_b' }), 'ses_b');
      assertEq(sessionIdOf({ sessionId: '' }), null);
      assertEq(sessionIdOf({ messages: [] }), null);
      assertEq(sessionIdOf({}), null);
    },
  },

  // ── longest prefix wins, and a tie is an author error ────────────────────

  {
    name: 'verification-system-003 the longest declared prefix wins',
    fn: () => {
      const entries = [
        entry({ id: 'short', turn: 'Fix' }),
        entry({ id: 'long', turn: 'Fix the bug' }),
        entry({ id: 'other', turn: 'Ship it' }),
      ];

      const resolved = resolveEntry(request([user('Fix the bug in parser')]), entries, BINDINGS, { sessionId: SESSION });
      assertEq(resolved.matched?.id, 'long', 'longest matching prefix, not first or most specific');
    },
  },

  {
    name: 'verification-system-003 a shorter prefix still wins when the longer one does not match',
    fn: () => {
      const entries = [entry({ id: 'short', turn: 'Fix' }), entry({ id: 'long', turn: 'Fix the bug' })];

      const resolved = resolveEntry(request([user('Fixate on this')]), entries, BINDINGS, { sessionId: SESSION });
      assertEq(resolved.matched?.id, 'short');
    },
  },

  {
    name: 'verification-system-003 two same-length prefixes are ambiguous, never scored',
    fn: () => {
      // The replacement for `specificity`. Two declarations of equal length that both
      // match describe one point in the conversation with two different responses, so
      // the scenario does not say what the model does next. Picking one would answer
      // a question the author never answered.
      const entries = [entry({ id: 'x', turn: 'Do it' }), entry({ id: 'y', turn: 'Do it' })];

      const resolved = resolveEntry(request([user('Do it now')]), entries, BINDINGS, { sessionId: SESSION });

      assertTrue(resolved.matched === undefined, 'a tie must not resolve to a match');
      assertEq(resolved.ambiguousTurn?.length, 2);
      assertEq(
        resolved.ambiguousTurn
          .map((e) => e.id)
          .sort()
          .join(','),
        'x,y',
      );
    },
  },

  {
    name: 'verification-system-003 step and lane partition the declarations before prefixing',
    fn: () => {
      // Same turn text at two steps is the normal shape of a multi-step turn, and it
      // must not be an ambiguity. The old matcher needed `messageCount` for this.
      const entries = [
        entry({ id: 'step0', turn: 'Do it', step: 0 }),
        entry({ id: 'step1', turn: 'Do it', step: 1 }),
      ];

      assertEq(resolveEntry(request([user('Do it')]), entries, BINDINGS, { sessionId: SESSION }).matched?.id, 'step0');
      assertEq(resolveEntry(request([user('Do it'), assistant('r1')]), entries, BINDINGS, { sessionId: SESSION }).matched?.id, 'step1');

      // And the same turn in another lane is another conversation.
      const laned = [
        entry({ id: 'mgr', turn: 'Do it', lane: 'manager' }),
        entry({ id: 'coder', turn: 'Do it', lane: 'coder-after' }),
      ];
      assertEq(resolveEntry(request([user('Do it')]), laned, BINDINGS, { sessionId: SESSION }).matched?.id, 'mgr');
      assertEq(resolveEntry(request([user('Do it')]), laned, BINDINGS, { sessionId: 'ses_real_2' }).matched?.id, 'coder');
    },
  },

  {
    name: 'verification-system-003 nothing declared fails closed with the key that missed',
    fn: () => {
      const entries = [entry({ id: 'only', turn: 'Ship it' })];
      const resolved = resolveEntry(request([user('Something else')]), entries, BINDINGS, { sessionId: SESSION });

      assertTrue(resolved.matched === undefined, 'no match must not produce a match');
      assertEq(resolved.unmatched?.key.turn, 'Something else');
      assertEq(resolved.unmatched?.key.step, 0);
      assertEq(resolved.unmatched?.key.lane, 'manager');
    },
  },

  {
    name: 'AGENT-006 missing required tool yields unmatched',
    fn: () => {
      // toolsGate: declared `tools` are required on the wire. A request that lacks one
      // is not a match for that entry — it fails closed like an undeclared turn.
      const entries = [{
        id: 'needs-fork',
        lane: 'manager',
        turn: 'Do it',
        step: 0,
        tools: ['fork'],
      }];

      const missing = resolveEntry(
        withTools(request([user('Do it')]), ['join']),
        entries,
        BINDINGS,
        { sessionId: SESSION },
      );
      assertTrue(missing.matched === undefined, 'missing required tool must not match');
      assertTrue(missing.unmatched !== undefined, 'missing required tool must yield unmatched');

      const present = resolveEntry(
        withTools(request([user('Do it')]), ['fork', 'join']),
        entries,
        BINDINGS,
        { sessionId: SESSION },
      );
      assertEq(present.matched?.id, 'needs-fork', 'required tool present must still match');
    },
  },

  {
    name: 'AGENT-006 present forbiddenTools yields unmatched',
    fn: () => {
      // toolsGate: `forbiddenTools` asserts absence. A request carrying a forbidden
      // name is filtered out of candidates and fails closed.
      const entries = [{
        id: 'no-fork',
        lane: 'manager',
        turn: 'Do it',
        step: 0,
        forbiddenTools: ['fork'],
      }];

      const forbidden = resolveEntry(
        withTools(request([user('Do it')]), ['fork', 'join']),
        entries,
        BINDINGS,
        { sessionId: SESSION },
      );
      assertTrue(forbidden.matched === undefined, 'forbidden tool must not match');
      assertTrue(forbidden.unmatched !== undefined, 'forbidden tool must yield unmatched');

      const allowed = resolveEntry(
        withTools(request([user('Do it')]), ['join']),
        entries,
        BINDINGS,
        { sessionId: SESSION },
      );
      assertEq(allowed.matched?.id, 'no-fork', 'request without forbidden tools must match');
    },
  },

  {
    name: 'verification-system-003 a request with no user message matches nothing',
    fn: () => {
      // A bare continuation cannot begin with any declared user text. Admitting it
      // would make every scenario match every synthetic nudge.
      const entries = [entry({ id: 'any', turn: '' })];
      const resolved = resolveEntry(request([assistant('r1')]), entries, BINDINGS, { sessionId: SESSION });

      assertTrue(resolved.matched === undefined, 'a null turn must not match the empty declaration');
    },
  },

  {
    name: 'verification-system-003 a lane-less declaration matches any lane',
    fn: () => {
      // Single-lane scenarios should not have to name their only lane. `undefined`
      // means "any", which is different from `null` — the value an unbound session
      // produces — so an unbound session cannot silently match a lane-bound edge.
      const entries = [{ id: 'anywhere', turn: 'Do it', step: 0 }];

      assertEq(resolveEntry(request([user('Do it')]), entries, BINDINGS, { sessionId: SESSION }).matched?.id, 'anywhere');
      assertEq(resolveEntry(request([user('Do it')]), entries, BINDINGS, { sessionId: 'ses_unbound' }).matched?.id, 'anywhere');

      const bound = [entry({ id: 'mgr-only', turn: 'Do it' })];
      assertTrue(
        resolveEntry(request([user('Do it')]), bound, BINDINGS, { sessionId: 'ses_unbound' }).matched === undefined,
        'an unbound session must not match a lane-bound declaration',
      );
    },
  },

  // ── a delegated Replica re-sends its bootstrap prompt as the last user message ──

  {
    name: 'DELEGATE-014 step distinguishes a Replica bootstrap from its own reply',
    fn: () => {
      // A delegated Replica re-sends its readonly-investigation prompt as the LAST
      // user message on every request (`Strength/Replica/Transform.fs`). Its own
      // js-predictor reply therefore sits *before* that trailing prompt, so counting
      // "assistant messages after the last user message" collapses the bootstrap
      // (step 0) and the first reply (step 1) to the same value.
      const readonly = readText('en', 'delegation/readonly-investigation');

      const bootstrap = {
        ...request([
          { role: 'system', content: 'sys' },
          { role: 'system', content: 'sys' },
          user('STRENGTH_HOST_CANARY: inspect README.md'),
          toolCall('js-manager'),
          toolResult('# ok'),
          user(readonly),
        ]),
        tools: [{ name: 'js-predictor' }],
      };
      const firstReply = {
        ...request([
          { role: 'system', content: 'sys' },
          { role: 'system', content: 'sys' },
          user('STRENGTH_HOST_CANARY: inspect README.md'),
          toolCall('js-manager'),
          toolResult('# ok'),
          toolCall('js-predictor'),
          toolResult('# ok'),
          user(readonly),
        ]),
        tools: [{ name: 'js-predictor' }],
      };

      const bootstrapStep = stepOf(bootstrap);
      const replyStep = stepOf(firstReply);

      assertEq(bootstrapStep, 0, 'bootstrap is the Replica turn first provider step');
      assertEq(replyStep, 1, 'the Replica own reply is the second step');
    },
  },
];
