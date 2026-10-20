/**
 * Script forest contract tests (AGENTS.md KISS-N11).
 * File name kept for harness discovery; semantics are prefix/content forest.
 */
import { assertEq, assertTrue, postJson } from './lib.mjs';
import { StrictMockProvider } from '../../e2e/support/strict-mock-provider.js';
import { compileScenario } from '../../e2e/support/scenario-schema.js';
import { ScenarioRuntime } from '../../e2e/support/scenario-runtime.js';

function chat(content, tools = [], extraMessages = []) {
  return {
    model: 'test-model',
    messages: [...extraMessages, { role: 'user', content }],
    tools: tools.map((name) => ({ type: 'function', function: { name } })),
  };
}

function sessionHeaders(sessionId) {
  return { 'x-session-affinity': sessionId };
}

async function scenarioProvider(source, bindings) {
  const result = compileScenario(source, { name: 'gate-inline.toml' });
  assertTrue(result.ok, `fixture must compile: ${result.ok ? '' : result.problems.join(' | ')}`);

  const provider = new StrictMockProvider({ quiet: true });
  await provider.start();
  provider.attachScenario(new ScenarioRuntime(result.scenario));
  for (const [alias, sessionIds] of Object.entries(bindings)) {
    for (const sessionId of (Array.isArray(sessionIds) ? sessionIds : [sessionIds])) {
      provider.bindSession(alias, sessionId);
    }
  }
  return provider;
}

function assistant(text) {
  return { role: 'assistant', content: text };
}

async function runForestIndependentPaths() {
  const managerTools = ['fork', 'join', 'list'];
  const provider = await scenarioProvider(`scenario = "forest-independent"
flow = [
  { prompt = { text = "write x.txt" } },
  { prompt = { text = "manager first" } },
  { prompt = { text = "manager second" } },
]

[[turn]]
id = "manager-first"
lane = "manager"
user = "manager first"
tools = ["fork", "join", "list"]

  [[turn.step]]
  respond = { type = "tool-call", tool = "fork", args = { agent = "coder", prompt = "work" } }

[[turn]]
id = "manager-second"
lane = "manager"
user = "manager second"
tools = ["fork", "join", "list"]

  [[turn.step]]
  respond = { type = "text", text = "manager done" }

[[turn]]
id = "coder-write"
lane = "coder"
user = "write x.txt"
tools = ["todowrite-for-engineer"]

  [[turn.step]]
  respond = { type = "tool-call", tool = "todowrite-for-engineer", args = { program = "class Js extends JsProgram { async run() { this.write('x.txt', 'ok\\\\n'); return { written: true }; } }" } }
`, {
    manager: 'forest-manager',
    coder: 'forest-coder',
  });
  try {
    const coderFirst = await postJson(
      `${provider.url}/v1/chat/completions`,
      chat('write x.txt', ['todowrite-for-engineer']),
      sessionHeaders('forest-coder'),
    );
    assertTrue(coderFirst.ok, 'independent coder path may run before manager');
    const managerFirst = await postJson(
      `${provider.url}/v1/chat/completions`,
      chat('manager first', managerTools),
      sessionHeaders('forest-manager'),
    );
    assertTrue(managerFirst.ok, 'manager first user matches');
    const again = await postJson(
      `${provider.url}/v1/chat/completions`,
      chat('manager first', managerTools),
      sessionHeaders('forest-manager'),
    );
    assertTrue(again.ok, 'same prefix is idempotent (no mute)');
    const managerSecond = await postJson(
      `${provider.url}/v1/chat/completions`,
      chat('manager second', managerTools, [
        { role: 'user', content: 'manager first' },
        assistant('manager first response'),
      ]),
      sessionHeaders('forest-manager'),
    );
    assertTrue(managerSecond.ok, 'manager second user matches without turn queue');
    provider.expectSatisfied();
  } finally {
    await provider.stop();
  }
}

async function runIdempotentSamePrefix() {
  const provider = await scenarioProvider(`scenario = "idempotent"
prompt = { text = "idempotent ping" }

[[turn]]
id = "once"
lane = "manager"
user = "idempotent ping"
tools = ["fork", "join", "list"]

  [[turn.step]]
  respond = { type = "text", text = "hello" }
`, { manager: 'idempotent-session' });
  try {
    const body = chat('idempotent ping', ['fork', 'join', 'list']);
    const headers = sessionHeaders('idempotent-session');
    const a = await postJson(`${provider.url}/v1/chat/completions`, body, headers);
    const b = await postJson(`${provider.url}/v1/chat/completions`, body, headers);
    assertTrue(a.ok && b.ok, 'identical requests both succeed');
    // blocking edge observed → satisfied even if "pending" uses observed set
    provider.expectSatisfied();
  } finally {
    await provider.stop();
  }
}

async function runAmbiguousPrefixRejected() {
  const provider = await scenarioProvider(`scenario = "ambiguous"
flow = [
  { prompt = { text = "A" } },
  { prompt = { text = "B" } },
]

[[turn]]
id = "a"
lane = "manager"
user = ["shared marker", "A"]
tools = ["fork", "join", "list"]

  [[turn.step]]
  respond = { type = "text", text = "A" }

[[turn]]
id = "b"
lane = "manager"
user = ["shared marker", "B"]
tools = ["fork", "join", "list"]

  [[turn.step]]
  respond = { type = "text", text = "B" }
`, { manager: 'ambiguity-session' });
  try {
    const res = await postJson(
      `${provider.url}/v1/chat/completions`,
      chat('shared marker A B', ['fork', 'join', 'list']),
      sessionHeaders('ambiguity-session'),
    );
    assertEq(res.status, 500, 'ambiguous same-length prefixes must 500');
    assertTrue(
      provider.unexpectedRequests.some((u) => u.reason === 'ambiguous-turn'),
      'reason must be ambiguous-turn',
    );
  } finally {
    await provider.stop();
  }
}

async function runUserForkDisambiguates() {
  const provider = await scenarioProvider(`scenario = "user-fork"
flow = [
  { prompt = { text = "Ship job-A please" } },
  { prompt = { text = "Ship job-B please" } },
]

[[turn]]
id = "job-a"
lane = "job-a"
user = "Ship job-A"
tools = ["fork", "join", "list"]

  [[turn.step]]
  respond = { type = "text", text = "A" }

[[turn]]
id = "job-b"
lane = "job-b"
user = "Ship job-B"
tools = ["fork", "join", "list"]

  [[turn.step]]
  respond = { type = "text", text = "B" }
`, {
    'job-a': 'job-a-session',
    'job-b': 'job-b-session',
  });
  try {
    const tools = ['fork', 'join', 'list'];
    const a = await postJson(
      `${provider.url}/v1/chat/completions`,
      chat('Ship job-A please', tools),
      sessionHeaders('job-a-session'),
    );
    const b = await postJson(
      `${provider.url}/v1/chat/completions`,
      chat('Ship job-B please', tools),
      sessionHeaders('job-b-session'),
    );
    assertTrue(a.ok && b.ok, 'user-text fork disambiguates parallel paths');
    provider.expectSatisfied();
  } finally {
    await provider.stop();
  }
}

// K12.2: neverEnd has no valid ScenarioRuntime meaning. A static fixture must reject it
// and name the replacement instead of reintroducing a stream-lifetime matcher flag.
function runNeverEndRejected() {
  const result = compileScenario(`scenario = "never-end-retired"
flow = [
  { prompt = { text = "blog delta" } },
]

[[turn]]
id = "blog"
lane = "background"
user = "You are the background worker."

  [[turn.step]]
  neverEnd = true
  respond = { type = "text", text = "blog" }
`, { name: 'never-end-retired.toml' });
  assertTrue(result.ok === false, 'neverEnd must be rejected by the static scenario contract');
  assertTrue(
    result.problems.some((problem) => problem.includes('neverEnd is retired') && problem.includes('declare those steps')),
    `neverEnd rejection must name the step-based replacement: ${result.problems.join(' | ')}`,
  );
}

async function runPrefixCacheInvalidation() {
  const provider = await scenarioProvider(`scenario = "prefix-invalidation"
flow = [
  { prompt = { text = "stable system prefix first user" } },
  { prompt = { text = "appended user turn" } },
]

[[turn]]
id = "turn-1"
lane = "manager"
user = "stable system prefix"

  [[turn.step]]
  respond = { type = "text", text = "ok1" }

[[turn]]
id = "turn-2"
lane = "manager"
user = "appended user turn"

  [[turn.step]]
  respond = { type = "text", text = "ok2" }
`, { manager: 'prefix-session-1' });
  try {
    const tools = [
      { type: 'function', function: { name: 'fork' } },
      { type: 'function', function: { name: 'join' } },
      { type: 'function', function: { name: 'list' } },
    ];
    const body1 = {
      model: 'test-model',
      tools,
      messages: [
        { role: 'system', content: 'stable system prefix' },
        { role: 'user', content: 'stable system prefix first user' },
      ],
    };
    const first = await postJson(`${provider.url}/v1/chat/completions`, body1, {
      'x-session-affinity': 'prefix-session-1',
    });
    assertTrue(first.ok, 'prefix: first request matches');

    const body2 = {
      model: 'test-model',
      tools,
      messages: [
        { role: 'system', content: 'MUTATED system prefix' },
        { role: 'user', content: 'stable system prefix first user' },
        { role: 'user', content: 'appended user turn' },
      ],
    };
    const second = await postJson(`${provider.url}/v1/chat/completions`, body2, {
      'x-session-affinity': 'prefix-session-1',
    });
    assertEq(second.status, 500, 'prefix: mutated sealed prefix must 500');
    assertTrue(provider.unexpectedRequests.some((u) => u.reason === 'seal-undeclared'), 'prefix: reason must be seal-undeclared');
  } finally {
    await provider.stop();
  }
}

async function runPrefixCacheAppendOk() {
  const provider = await scenarioProvider(`scenario = "prefix-append"
flow = [
  { prompt = { text = "append-ok first" } },
  { prompt = { text = "append-ok second" } },
]

[[turn]]
id = "turn-1"
lane = "manager"
user = "append-ok first"

  [[turn.step]]
  respond = { type = "text", text = "ok1" }

[[turn]]
id = "turn-2"
lane = "manager"
user = "append-ok second"

  [[turn.step]]
  respond = { type = "text", text = "ok2" }
`, { manager: 'prefix-session-2' });
  try {
    const tools = [
      { type: 'function', function: { name: 'fork' } },
      { type: 'function', function: { name: 'join' } },
      { type: 'function', function: { name: 'list' } },
    ];
    const body1 = {
      model: 'test-model',
      tools,
      messages: [
        { role: 'system', content: 'append-ok system' },
        { role: 'user', content: 'append-ok first' },
      ],
    };
    const first = await postJson(`${provider.url}/v1/chat/completions`, body1, {
      'x-session-affinity': 'prefix-session-2',
    });
    assertTrue(first.ok, 'prefix append: first ok');
    const body2 = {
      model: 'test-model',
      tools,
      messages: [
        { role: 'system', content: 'append-ok system' },
        { role: 'user', content: 'append-ok first' },
        { role: 'assistant', content: 'ok1' },
        { role: 'user', content: 'append-ok second' },
      ],
    };
    const second = await postJson(`${provider.url}/v1/chat/completions`, body2, {
      'x-session-affinity': 'prefix-session-2',
    });
    assertTrue(second.ok, 'prefix append: second ok when sealed prefix preserved');
    provider.expectSatisfied();
  } finally {
    await provider.stop();
  }
}

async function runMissingEdgeFailsSatisfied() {
  const provider = await scenarioProvider(`scenario = "missing-edge"
prompt = { text = "never sent" }

[[turn]]
id = "never-hit"
lane = "manager"
user = "never sent"
tools = ["fork", "join", "list"]

  [[turn.step]]
  respond = { type = "text", text = "x" }
`, { manager: 'missing-edge-session' });
  try {
    let threw = false;
    try {
      provider.expectSatisfied();
    } catch (err) {
      threw = true;
      assertTrue(err.message.includes('declared but never reached'), 'must report unanswered scenario turns');
      assertTrue(err.message.includes('[never-hit.'), 'must report the unanswered turn id');
    }
    assertTrue(threw, 'unobserved blocking edge fails satisfied');
  } finally {
    await provider.stop();
  }
}

async function runHistorySplitsSameLastUser() {
  // Same lastUser phrase, different full history → different seals; both use same template edge (idempotent template).
  const provider = await scenarioProvider(`scenario = "history-splits"
flow = [
  { prompt = { text = "Review the current worktree for correctness." } },
  { prompt = { text = "Nope, let's re-evaluate: does it really fully satisfy the original task without cutting corners?" } },
]

[[turn]]
id = "assess-first"
lane = "manager"
user = "Review the current worktree"
tools = ["review"]

  [[turn.step]]
  respond = { type = "tool-call", tool = "review", args = { findings = [] } }

[[turn]]
id = "assess-confirm"
lane = "manager"
user = "Nope, let's re-evaluate: does it really fully satisfy the original task without cutting corners?"
tools = ["review"]

  [[turn.step]]
  respond = { type = "tool-call", tool = "review", args = { findings = [{ acceptance_criteria = "the target state is not yet reached", work_plan = "close the remaining gap" }] } }
`, {
    manager: ['mgr-1', 'mgr-2'],
  });
  try {
    const tools = [{ type: 'function', function: { name: 'review' } }];
    const first = await postJson(`${provider.url}/v1/chat/completions`, {
      model: 'test-model',
      tools,
      messages: [{ role: 'user', content: 'Review the current worktree for correctness.' }],
    }, sessionHeaders('mgr-1'));
    assertTrue(first.ok, 'first review matches');
    const confirm = await postJson(`${provider.url}/v1/chat/completions`, {
      model: 'test-model',
      tools,
      messages: [
        { role: 'user', content: 'Review the current worktree for correctness.' },
        { role: 'assistant', content: null, tool_calls: [{ id: 'c1', type: 'function', function: { name: 'review', arguments: '{}' } }] },
        { role: 'tool', tool_call_id: 'c1', content: "Nope, let's re-evaluate: does it really fully satisfy the original task without cutting corners?" },
        { role: 'user', content: "Nope, let's re-evaluate: does it really fully satisfy the original task without cutting corners?" },
      ],
    }, sessionHeaders('mgr-1'));
    assertTrue(confirm.ok, 'confirm user matches different edge');
    // Same first-user request shape on another session (post-rebase style) — same template, ok
    const again = await postJson(`${provider.url}/v1/chat/completions`, {
      model: 'test-model',
      tools,
      messages: [{ role: 'user', content: 'Review the current worktree for correctness.' }],
    }, sessionHeaders('mgr-2'));
    assertTrue(again.ok, 'identical first-user template is reusable across sessions');
    provider.expectSatisfied();
  } finally {
    await provider.stop();
  }
}

export const laneCases = [
  { name: 'forest independent content paths', fn: runForestIndependentPaths },
  { name: 'identical prefix is idempotent', fn: runIdempotentSamePrefix },
  { name: 'ambiguous equal templates rejected', fn: runAmbiguousPrefixRejected },
  { name: 'user-text fork disambiguates parallel jobs', fn: runUserForkDisambiguates },
  { name: 'neverEnd is rejected by the scenario contract', fn: runNeverEndRejected },
  { name: 'prefix cache invalidation fails closed', fn: runPrefixCacheInvalidation },
  { name: 'prefix cache append-only succeeds', fn: runPrefixCacheAppendOk },
  { name: 'unobserved blocking edge fails satisfied', fn: runMissingEdgeFailsSatisfied },
  { name: 'history splits first vs confirm review', fn: runHistorySplitsSameLastUser },
];
