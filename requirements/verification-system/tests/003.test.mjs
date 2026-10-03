import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const { attachEventCeilings, eventCeilingSetupProblems, isCountedSseEvent, normalizeEventCeilings } = await import("./e2e/support/event-ceiling.js");
const { resolveEntry } = await import("./e2e/support/runtime-key.js");
const { compileScenario } = await import("./e2e/support/scenario-schema.js");
const { readFileSync } = await import("node:fs");
const { fileURLToPath } = await import("node:url");
const { default: path } = await import("node:path");


test('WHAT[verification-system-003] isCountedSseEvent excludes server.heartbeat only', () => {
  assert.equal(isCountedSseEvent({ type: 'server.heartbeat' }), false);
  assert.equal(isCountedSseEvent({ type: 'message.updated' }), true);
  assert.equal(isCountedSseEvent({ type: '' }), false);
  assert.equal(isCountedSseEvent({}), false);
});
test('WHAT[verification-system-003] normalizeEventCeilings rejects non-positive integers', () => {
  assert.deepEqual(normalizeEventCeilings({}), {});
  assert.deepEqual(normalizeEventCeilings({ maxJournalEvents: 12, maxSseEvents: 34 }), {
    maxJournalEvents: 12,
    maxSseEvents: 34,
  });
  assert.throws(() => normalizeEventCeilings({ maxJournalEvents: 0 }), /positive integer/);
  assert.throws(() => normalizeEventCeilings({ maxSseEvents: 1.2 }), /positive integer/);
});
test('WHAT[verification-system-003] eventCeilingSetupProblems matches schema contract', () => {
  assert.deepEqual(eventCeilingSetupProblems(undefined), []);
  assert.ok(eventCeilingSetupProblems({ maxJournalEvents: 0 })[0].includes('maxJournalEvents'));
  assert.ok(eventCeilingSetupProblems({ maxSseEvents: -3 })[0].includes('maxSseEvents'));
});
test('WHAT[verification-system-003] attachEventCeilings breaches maxSseEvents without counting heartbeats', () => {
  const listeners = [];
  const scenario = {
    host: { workDir: '/tmp/does-not-need-to-exist-for-sse-only' },
    events: {
      allEvents: [
        { type: 'message.updated' },
        { type: 'server.heartbeat' },
        { type: 'sync' },
      ],
      onEvent(cb) {
        listeners.push(cb);
        return () => {
          const i = listeners.indexOf(cb);
          if (i >= 0) listeners.splice(i, 1);
        };
      },
      dump: () => '',
    },
    watchdog: { stop() {} },
  };

  let breached = null;
  // Journal ceiling omitted — avoid touching a real workDir tip.
  attachEventCeilings(
    scenario,
    { maxSseEvents: 2 },
    {
      onBreach: (detail) => {
        breached = detail;
        throw new Error('ceiling');
      },
    },
  );
  // Already had 2 counted frames at attach; next counted frame breaches.
  assert.equal(breached, null);
  assert.throws(() => listeners[0]({ type: 'session.idle' }), /ceiling/);
  assert.equal(breached.kind, 'maxSseEvents');
  assert.equal(breached.observed, 3);
  assert.equal(breached.limit, 2);
  // Heartbeat must not increment.
  assert.equal(breached.sseEvents, 3);
});
test('WHAT[verification-system-003] long-stroke.toml pins measured exact event ceilings', () => {
  const dir = path.dirname(fileURLToPath(import.meta.url));
  const source = readFileSync(path.join(dir, 'e2e/scenarios/long-stroke.toml'), 'utf8');
  const result = compileScenario(source, { name: 'long-stroke.toml' });
  assert.equal(result.ok, true, result.ok ? '' : result.problems.join('\n'));
  // Complete owner-controlled conflict canaries measured at most 466 durable envelopes and 2234 SSE frames.
  // Pins retain headroom above observed maxima while failing fast on event regressions.
  assert.equal(result.scenario.setup.maxJournalEvents, 550);
  assert.equal(result.scenario.setup.maxSseEvents, 2500);
});
test('WHAT[verification-system-003] Long Stroke keeps one Manager loop and two exact consecutive failures', () => {
  const dir = path.dirname(fileURLToPath(import.meta.url));
  const source = readFileSync(path.join(dir, 'e2e/scenarios/long-stroke.toml'), 'utf8');
  const result = compileScenario(source, { name: 'long-stroke.toml' });
  assert.equal(result.ok, true, result.ok ? '' : result.problems.join('\n'));

  const byId = new Map(result.scenario.entries.map((entry) => [entry.id, entry]));
  const ordinary = byId.get('manager-loop.2');

  assert.deepEqual(
    { optional: ordinary?.optional, lane: ordinary?.lane, step: ordinary?.step },
    { optional: false, lane: 'manager', step: 2 },
  );
  assert.deepEqual(
    result.scenario.faults.filter((fault) => fault.kind === 'provider-error' && fault.status === 400)
      .map((fault) => fault.entryId),
    ['manager-loop.2', 'continue.0'],
  );

  const loopTools = ['fork', 'resume', 'join', 'horizon', 'review', 'suicide'];
  // The Manager provider surface carries independent assume + native todowrite.
  const managerTools = [
    'abandon', 'assume', 'celebrate', 'defer', 'enough',
    'fork', 'horizon', 'join', 'js-manager', 'publish', 'regret', 'resume',
    'review', 'skill', 'subscribe', 'suicide', 'todowrite',
  ];
  const request = (turn, step) => ({
    messages: [
      { role: 'user', content: turn },
      ...Array.from({ length: step }, (_, index) => ({ role: 'assistant', content: `reply-${index}` })),
    ],
    tools: managerTools.map((name) => ({ name })),
  });
  const bindings = new Map([['manager', 'ses_manager']]);
  const context = { sessionId: 'ses_manager' };

  assert.equal(
    resolveEntry(
      request('# Delegated work you sent out has not come back yet. Continue the work.', 1),
      result.scenario.entries,
      bindings,
      context,
    ).matched?.id,
    'manager-join-guard.0',
  );

  const assessUser =
    '# You are the 2 Manager taking over this mission. A predecessor may already have done\n' +
    '# part of the work, or may already have finished it; investigate the actual workspace before you\n' +
    "# act on either assumption. The predecessor's work is the object you must assess; the shared workspace is the actual state it left behind: check it directly\n" +
    '# rather than trusting any inherited claim.\n' +
    '#\n' +
    "# During assessment, you may directly use the review-only read tool js-manager, or entrust read-only work to Engineer to establish facts about the predecessor's work";
  const loopRequest = (turn, step) => ({
    messages: [
      { role: 'user', content: turn },
      ...Array.from({ length: step }, (_, index) => ({ role: 'assistant', content: `reply-${index}` })),
    ],
    tools: loopTools.map((name) => ({ name })),
  });

  const reopened0 = byId.get('manager-reopened-loop.0');
  assert.equal(reopened0?.step, 0);
  assert.equal(reopened0?.optional, true);
  assert.equal(reopened0?.internal, true);
  assert.equal(reopened0?.respond?.tool, 'review');

  const reopened1 = byId.get('manager-reopened-loop.1');
  assert.equal(reopened1?.step, 1);
  assert.equal(reopened1?.optional, true);
  assert.equal(reopened1?.internal, true);
  assert.equal(reopened1?.respond?.tool, 'suicide');

  assert.equal(
    resolveEntry(loopRequest(assessUser, 0), result.scenario.entries, bindings, context).matched?.id,
    'manager-reopened-loop.0',
  );
  assert.equal(
    resolveEntry(loopRequest(assessUser, 1), result.scenario.entries, bindings, context).matched?.id,
    'manager-reopened-loop.1',
  );

  // Every live action in the reusable authority-first loop is exact; obsolete
  // explicit repair-resume and the optional join-guard race stay out of must.
  assert.equal(result.scenario.flow.filter((step) => step.waitAny).length, 0);
  for (let index = 0; index <= 2; index += 1) {
    const id = `manager-loop.${index}`;
    assert.ok(result.scenario.must.includes(id), `${id} must be an exact must step`);
    assert.deepEqual(byId.get(id)?.tools, loopTools);
    assert.equal(byId.get(id)?.internal, false);
  }

  assert.ok(!result.scenario.entries.some((entry) => entry.id.startsWith('manager-resume.')));
  const currentActions = result.scenario.entries.filter((entry) => entry.turnId === 'manager-current-action');
  assert.deepEqual(currentActions.map((entry) => entry.step), Array.from({ length: 11 }, (_, index) => index));
  assert.ok(currentActions.every((entry) => entry.optional === true));
  assert.ok(!result.scenario.must.some((id) => id.startsWith('manager-current-action.')));
  assert.ok(!result.scenario.entries.some((entry) => entry.id.startsWith('manager-repair-resume.')));
  assert.ok(!result.scenario.must.some((id) => id.startsWith('manager-join-guard.')));
});
}

{
const { default: assert } = await import("node:assert/strict");
const { existsSync, mkdtempSync, mkdirSync, writeFileSync, rmSync } = await import("node:fs");
const { tmpdir } = await import("node:os");
const { join } = await import("node:path");
const { default: test } = await import("node:test");
const { E2E_ROOT_REL, SOLE_ENTRY, e2eTestCaseFiles, scanE2EWatchdogFeed } = await import("./e2e/support/watchdog-feed-scan.mjs");

const makeTempRoot = (layout) => {
  const root = mkdtempSync(join(tmpdir(), 'e2e-wdf-fc-'))
  const e2e = join(root, E2E_ROOT_REL)
  if (layout.e2eDir !== false) mkdirSync(e2e, { recursive: true })
  for (const name of layout.files ?? []) {
    writeFileSync(join(e2e, name), '// throwaway\n')
  }
  if (layout.e2eIsFile) {
    rmSync(e2e, { recursive: true, force: true })
    writeFileSync(e2e, 'not a directory\n')
  }
  return root
}
const cleanup = (root) => rmSync(root, { recursive: true, force: true })

test('WHAT[verification-system-003] e2e case ceiling is zero — no cases/ channel', () => {
  // E2E_CASE_CEILING = 0：case 天花板只降不升。机器面 = 顶层清单不递归
  // cases/ 或 support/；缺失或空 cases/ 必须被容忍（不存在 = 零 case），
  // 不许 walk 或 require 该目录。
  const files = e2eTestCaseFiles()

  for (const file of files) {
    assert.ok(existsSync(file), `e2e top-level test file missing: ${file}`)
  }
})
test('WHAT[verification-system-003] missing or empty cases/ is allowed (no throw)', () => {
  // cases/ is not required and not walked. A valid e2e root with the sole
  // entry and NO cases/ directory must return exactly the entry — proving the
  // fail-closed tightening did not regress the documented cases/ tolerance.
  const root = makeTempRoot({ files: [SOLE_ENTRY] })
  try {
    const files = e2eTestCaseFiles(root)
    assert.equal(files.length, 1, 'only the sole top-level entry is in scope')
    assert.ok(
      files[0].endsWith('/tests/' + SOLE_ENTRY) || files[0].endsWith(SOLE_ENTRY),
      `expected sole entry path, got ${files[0]}`,
    )
  } finally {
    cleanup(root)
  }
})
}


{
const { default: assert } = await import("node:assert/strict");
const { readFileSync } = await import("node:fs");
const { dirname, join, resolve } = await import("node:path");
const { fileURLToPath } = await import("node:url");
const { default: test } = await import("node:test");

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '../../..')
const ENTRY = join(ROOT, 'requirements/verification-system/tests/014.test.mjs')
const LONG_STROKE = join(ROOT, 'requirements/verification-system/tests/e2e/scenarios/long-stroke.toml')
const MARKER = 'PHYSICAL CONTRACTS (verification-system [002]/[014])'
const REQUIRED = [
  /OpenCode process lifetime|spawn count === 1|spawn === 1/,
  /Host-assigned assistant messageID|HOST-010/,
  /repeat-until-pass/i,
]

test('WHAT[verification-system-003] sole e2e entry declares unsimulatable physical contracts', () => {
  const text = readFileSync(ENTRY, 'utf8')
  assert.equal(text.includes(MARKER), true, 'e2e entry must name PHYSICAL CONTRACTS (verification-system [002]/[014])')
  for (const contract of REQUIRED) {
    assert.match(text, contract, `e2e entry must declare ${contract}`)
  }
  assert.equal(text.replace(MARKER, '').includes(MARKER), false)
})
test('WHAT[verification-system-003] active-join user-message injection waits for physical ToolPart running', () => {
  const scenario = readFileSync(LONG_STROKE, 'utf8')
  const entry = readFileSync(ENTRY, 'utf8')
  const joinExpectation = scenario.indexOf('{ wait = "manager-loop.1"')
  const runningBarrier = scenario.indexOf('{ custom = "awaitManagerJoinRunning" }')
  const userWake = scenario.indexOf('Interrupt the active join.')

  assert.ok(joinExpectation >= 0, 'Long Stroke must wait for manager-loop.1 provider expectation')
  assert.ok(runningBarrier > joinExpectation, 'physical join-running barrier must follow manager-loop.1')
  assert.ok(userWake > runningBarrier, 'user-message wake must be injected only after join ToolPart is running')
  assert.match(entry, /awaitManagerJoinRunning/)
  assert.match(entry, /message\.part\.updated/)
  assert.match(entry, /toolName\s*===\s*['"]join['"]/)
  assert.match(entry, /toolStatus\s*===\s*['"]running['"]/)
})
test('WHAT[verification-system-003] format-build-test does not repeat-until-pass', () => {
  const { scripts } = JSON.parse(readFileSync(join(ROOT, 'package.json'), 'utf8'))
  const command = scripts['format-build-test']
  assert.equal(typeof command, 'string')
  assert.doesNotMatch(command, /repeat-until-pass|--repeat|until-pass/i)
})
}
