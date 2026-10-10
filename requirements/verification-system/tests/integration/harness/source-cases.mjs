/**
 * gate-source-cases.mjs — scenario SOURCE hygiene: retired vocabulary.
 *
 * toml-format gate removed in 0.5.3; formatter cases retired with scripts/toml-format.mjs.
 * Retained: retired-field load refusals and forest structural checks that do not depend on
 * the deleted formatter.
 */

import { assertEq, assertTrue } from './lib.mjs';
import { RETIRED_FIELDS, retiredFieldProblems } from '../../e2e/support/legacy-fields.js';
import { compileScenario } from '../../e2e/support/scenario-schema.js';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { walk } from '../../../../../scripts/lib/walk.mjs';

/** The forest itself, so a scenario added later is covered without being registered. */
const SCENARIO_ROOT = fileURLToPath(new URL('../../e2e/scenarios', import.meta.url));

const compile = (source) => compileScenario(source, { name: 'p.toml' });

/** One turn, one step, reachable — a scenario that loads, plus whatever is under test. */
const withField = (field) => `scenario = "p"
flow = [ { prompt = { text = "go" } } ]

[[turn]]
id = "a"
user = "go"
${field}

  [[turn.step]]
  respond = { type = "text", text = "ok" }
`;

const rejectsField = (field, fragment) => {
  const result = compile(withField(field));
  assertTrue(!result.ok, `'${field}' must not load`);
  assertTrue(
    result.problems.some((problem) => problem.includes(fragment)),
    `expected a problem mentioning '${fragment}', got: ${result.problems.join(' | ')}`,
  );
};

export const sourceCases = [
  {
    name: 'verification-system-003 predicate-bag matching is retired',
    fn: () => {
      rejectsField('match = { user = "go" }', 'keyed by (lane, turn, step)');
      rejectsField('userRegex = "Fix.*"', 'cannot be prefix-ordered');
      rejectsField('containsText = ["go"]', 'as a prefix');
      rejectsField('messageCount = 3', 'use step');
      rejectsField('afterToolResult = true', 'already in the semantic prefix');
    },
  },

  {
    name: 'verification-system-003 scoring is retired because a longest prefix is unique',
    fn: () => {
      rejectsField('specificity = 9', 'nothing left to score');
    },
  },

  {
    name: 'PROMPT-008 the mock may not carry a role',
    fn: () => {
      rejectsField('role = "manager"', 'AttemptExecutionProfile');
      rejectsField('requestRoleOf = "manager"', 'only source');
    },
  },

  {
    name: 'verification-system-003 harness bookkeeping may not participate in matching',
    fn: () => {
      rejectsField('__testkitHeaders = { "x-session-id" = "s" }', 'one-way');
    },
  },

  {
    name: 'verification-system-003 the four matching flags are retired',
    fn: () => {
      rejectsField('reusable = true', 'inherently reusable');
      rejectsField('pathless = true', 'no cursor to be exempt from');
      rejectsField('neverEnd = true', 'declare those steps');
      rejectsField('blocking = false', 'assert arrival with must');
      rejectsField('claimCount = 2', 'independently waitable');
      rejectsField('aliases = ["second-perfect"]', 'two steps (REVIEW-003)');
    },
  },

  {
    name: 'verification-system-003 tool-set predicates are retired but declared tools are live assertions',
    fn: () => {
      rejectsField('requiredTools = ["fork"]', 'the runtime asserts every declared tool is present on the wire request');
      assertTrue(compile(withField('forbiddenTools = ["executor"]')).ok, 'forbiddenTools is a live wire assertion and must load');
      assertTrue(compile(withField('tools = ["fork", "join"]')).ok, 'declared tools must still load');
    },
  },

  {
    name: 'verification-system-003 dynamic loading is retired at any nesting depth',
    fn: () => {
      const result = compile(`scenario = "p"
flow = [ { loadScripts = "after.toml" } ]

[[turn]]
id = "a"
user = "go"

  [[turn.step]]
  respond = { type = "text", text = "ok" }
`);

      assertTrue(!result.ok);
      assertTrue(result.problems[0].includes('one static file'), result.problems[0]);
      assertTrue(result.problems[0].includes('flow[0].loadScripts'), 'the path locates it');
    },
  },

  {
    name: 'ARCH-001 turn as an ordinal is retired, turn as text is required',
    fn: () => {
      const result = compile(`scenario = "p"
flow = []

[[fault]]
turn = 7
step = 0
attempts = [1]
delivery = "provider-error"
`);

      assertTrue(!result.ok);
      assertTrue(result.problems[0].includes('not an ordinal'), result.problems[0]);
      assertTrue(result.problems[0].includes('step already carries position'), result.problems[0]);
    },
  },

  {
    name: 'verification-system-003 every scenario in the forest compiles',
    fn: () => {
      const files = walk(SCENARIO_ROOT, ['.toml']);
      assertTrue(files.length > 0, `no scenarios found under ${SCENARIO_ROOT}`);

      const broken = files
        .map((file) => ({ file, result: compileScenario(readFileSync(file, 'utf8'), { name: file }) }))
        .filter(({ result }) => !result.ok);

      assertEq(
        broken.length,
        0,
        broken.map(({ result }) => result.problems.join('\n    ')).join('\n  '),
      );
    },
  },

  {
    name: 'verification-system-003 no internal turn declares a lane',
    fn: () => {
      const offenders = walk(SCENARIO_ROOT, ['.toml']).flatMap((file) => {
        const source = readFileSync(file, 'utf8');
        const blocks = source.split(/^\[\[turn\]\]$/m).slice(1);
        return blocks
          .filter((block) => {
            const own = block.split(/^\[\[/m)[0];
            return /^internal = true$/m.test(own) && /^lane = /m.test(own);
          })
          .map(() => file);
      });

      assertEq(offenders.length, 0, `internal turns must not name a lane: ${offenders.join(', ')}`);
    },
  },

  {
    name: 'COMPANION-002 a Companion turn declares no lane',
    fn: () => {
      const offenders = walk(SCENARIO_ROOT, ['.toml']).flatMap((file) =>
        readFileSync(file, 'utf8')
          .split('\n')
          .map((line, index) => ({ file, line: index + 1, text: line.trim() }))
          .filter(({ text }) => /^lane = ".*blogger.*"$/.test(text)),
      );

      assertEq(
        offenders.length,
        0,
        offenders.map(({ file, line, text }) => `${file}:${line} ${text}`).join(', '),
      );
    },
  },

  {
    name: 'COMPANION-002 every scenario declares its Companion turn',
    fn: () => {
      const missing = walk(SCENARIO_ROOT, ['.toml']).filter(
        (file) => !readFileSync(file, 'utf8').includes('# Call the chronicle tool exactly once now'),
      );

      assertEq(missing.length, 0, `no Companion turn declared in: ${missing.join(', ')}`);
    },
  },
];
