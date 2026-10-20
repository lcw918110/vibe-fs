import assert from 'node:assert/strict';
import { isDeepStrictEqual } from 'node:util';
import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import http from 'node:http';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import { ProcessHost } from '../../../verification-system/tests/e2e/support/process-host.js';
import { OPENCODE_BIN, initGitWorkspace } from '../../../verification-system/tests/e2e/support/process-host-utils.js';
import { buildTextChunks, buildToolCallChunks, sendJSON, sendSSE } from '../../../verification-system/tests/e2e/support/strict-mock-sse.js';
import { readRequestBody, startHttpServer, stopHttpServer } from '../../../verification-system/tests/e2e/support/strict-mock-server.js';
import { resolvePluginPath } from '../../../verification-system/tests/e2e/support/scenario-paths.js';
import { isCanaryTurnSettled, managerReviewStage } from './manager-review-canary-turn.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, '../../../..');
const canaryPluginPath = path.join(here, 'manager-review-tools-canary-plugin.mjs');
const fixtureCandidate1 = path.join(here, '../../fixtures/manager-review-tools-canary-1.18.29.json');
const fixtureCandidate2 = path.join(here, '../fixtures/manager-review-tools-canary-1.18.29.json');
const fixturePath = fs.existsSync(fixtureCandidate1) ? fixtureCandidate1 : fixtureCandidate2;
const fixture = JSON.parse(fs.readFileSync(fixturePath, 'utf8'));

let productionPluginPath = null;
try {
  productionPluginPath = resolvePluginPath('opencode', { productionRoot: repoRoot });
} catch {
  const fallback = path.join(repoRoot, 'dist', 'OpenCode', 'Plugin', 'Plugin.js');
  if (fs.existsSync(fallback)) productionPluginPath = fallback;
}

const pluginPackageJsonPath = path.join(repoRoot, 'node_modules/@opencode-ai/plugin/package.json');
const pluginVersion = JSON.parse(fs.readFileSync(pluginPackageJsonPath, 'utf8')).version;
const opencodeVersion = execFileSync(OPENCODE_BIN, ['--version'], { encoding: 'utf8' }).trim().replace(/^v/, '');
assert.equal(pluginVersion, fixture.targetVersion);
assert.equal(opencodeVersion, fixture.targetVersion);
assert.ok(productionPluginPath, 'production plugin must be available');

const REVIEW_TOOLS = ['todowrite-for-manager'];
const CONTROL_TOOLS = ['todowrite-for-engineer', 'todowrite-for-devops'];
const CONTRACT_TOKEN = 'do-not-use-except-for-review';
const CANCEL_ARGUMENTS = {
  program: 'class Js extends JsProgram { async run() { await new Promise(() => {}); return null; } }',
  contract: CONTRACT_TOKEN,
};
const EXECUTOR_THROW_SENTINEL = 'T180-EXECUTOR-THROW-SENTINEL';
const EXECUTOR_THROW_MESSAGE = 'T180-EXECUTOR-THROW: intentional executor rejection';

// ── Collector setup ──────────────────────────────────────────────────────────

const observations = [];
const waiters = [];

const publish = (observation) => {
  const recorded = { sequence: observations.length + 1, ...observation };
  observations.push(recorded);
  for (let index = waiters.length - 1; index >= 0; index -= 1) {
    if (!waiters[index].predicate(recorded)) continue;
    waiters.splice(index, 1)[0].resolve(recorded);
  }
};

const waitFor = (predicate, timeoutMs = 25000) => {
  const existing = observations.find(predicate);
  if (existing) return Promise.resolve(existing);
  return new Promise((resolve, reject) => {
    const pending = {
      predicate,
      resolve: (value) => {
        clearTimeout(timer);
        resolve(value);
      },
    };
    const timer = setTimeout(() => {
      const idx = waiters.indexOf(pending);
      if (idx >= 0) waiters.splice(idx, 1);
      reject(new Error(`Timed out waiting for ${predicate.toString()} after ${timeoutMs}ms`));
    }, timeoutMs);
    waiters.push(pending);
  });
};

const collector = http.createServer(async (request, response) => {
  const chunks = [];
  for await (const chunk of request) chunks.push(chunk);
  try {
    const parsed = JSON.parse(Buffer.concat(chunks).toString('utf8'));
    publish(parsed);
  } catch {}
  response.writeHead(204).end();
});

await new Promise((resolve, reject) => {
  collector.once('error', reject);
  collector.listen(0, '127.0.0.1', resolve);
});
const collectorUrl = `http://127.0.0.1:${collector.address().port}`;

// ── Mock Provider setup with Request Routing Isolation ───────────────────────

const providerRequests = [];
const wireInspection = {
  contractRequired: false,
  contractType: null,
  contractEnum: null,
  historicalToolCallPreservesContract: false,
  round2ToolsStable: false,
  providerVisibleToolNames: [],
  providerVisibleProtocolAbsence: false,
};
let normalFollowupObserved = false;

let sessionID = null;

const isTitleRequest = (body) => {
  const messages = Array.isArray(body?.messages) ? body.messages : [];
  return (
    messages.slice(0, 4).some(
      (m) => typeof m?.content === 'string' && m.content.startsWith('Generate a title for this conversation:'),
    ) ||
    messages.some(
      (m) => m?.role === 'system' && typeof m?.content === 'string' && m.content.includes('title generator'),
    )
  );
};

const isManagerRequest = (request, body) => {
  if (isTitleRequest(body)) return false;

  const reqSessionId =
    request.headers['x-session-affinity'] ||
    request.headers['x-session-id'] ||
    request.headers['x-opencode-session'] ||
    null;

  // If header specifies a session ID and we already have our target sessionID, must match
  if (sessionID && reqSessionId && reqSessionId !== sessionID) {
    return false;
  }

  const toolNames = (body?.tools ?? [])
    .map((t) => t?.function?.name ?? t?.name)
    .filter((n) => typeof n === 'string');

  // Review tools are strictly exclusive to Role.Manager
  const hasManagerReviewTools = toolNames.includes('todowrite-for-manager');

  if (!hasManagerReviewTools || managerReviewStage(body) === null) {
    return false;
  }

  return true;
};

const provider = await startHttpServer(async (request, response) => {
  const url = new URL(request.url, `http://${request.headers.host}`);
  if ((url.pathname === '/v1/models' || url.pathname === '/models') && request.method === 'GET') {
    sendJSON(response, 200, { object: 'list', data: [{ id: 'test-model', object: 'model' }] });
    return;
  }

  if (url.pathname === '/v1/chat/completions' && request.method === 'POST') {
    const body = await readRequestBody(request);
    providerRequests.push(body);
    publish({ kind: 'provider.request.observed', value: {
      request: providerRequests.length,
      route: isTitleRequest(body) ? 'title' : isManagerRequest(request, body) ? 'manager' : 'companion',
      stage: managerReviewStage(body),
      prompts: (body.messages ?? []).filter((message) => message.role === 'user')
        .map((message) => typeof message.content === 'string' ? message.content.slice(0, 150) : '<structured>'),
    } });

    // Title requests do not belong to a canary execution path.
    if (isTitleRequest(body)) {
      sendSSE(response, buildTextChunks(`title_${Date.now()}`, 'Manager Review Canary Title', 1));
      return;
    }

    // Companion sidecars do not belong to a canary execution path.
    if (!isManagerRequest(request, body)) {
      sendSSE(response, buildTextChunks(`companion_${Date.now()}`, 'COMPANION_OK', 1));
      return;
    }

    const stage = managerReviewStage(body);

    // Normal call.
    if (stage === 1) {
      if (Array.isArray(body.tools) && wireInspection.providerVisibleToolNames.length === 0) {
        wireInspection.providerVisibleToolNames = body.tools.map((tool) => tool?.function?.name ?? tool?.name).sort();
        wireInspection.providerVisibleProtocolAbsence = body.tools.every((tool) => {
          const properties = tool?.function?.parameters?.properties ?? tool?.parameters?.properties ?? {};
          return !Object.hasOwn(properties, 'estimated_readonly_rounds') && !Object.hasOwn(properties, 'self_note');
        });
        const readManagerTool = body.tools.find(
          (t) => (t?.function?.name ?? t?.name) === 'todowrite-for-manager',
        );
        const contractProp = readManagerTool?.function?.parameters?.properties?.contract;
        const required = readManagerTool?.function?.parameters?.required ?? [];
        wireInspection.contractType = contractProp?.type ?? null;
        wireInspection.contractEnum = contractProp?.enum ?? null;
        wireInspection.contractRequired = Array.isArray(required) && required.includes('contract');
      }

      const call = {
        name: 'todowrite-for-manager',
        argsStr: JSON.stringify({
          program: "class Js extends JsProgram { async run() { return await this.cat('fixture-sample.txt'); } }",
          contract: CONTRACT_TOKEN,
        }),
      };
      sendSSE(response, buildToolCallChunks('call_read_norm_1', call.name, call.argsStr, 10));
      return;
    }

    // The same physical turn's follow-up proves history and schema stability.
    if (stage === 2) {
      if (!normalFollowupObserved) {
        normalFollowupObserved = true;
        const historical = (body.messages ?? []).flatMap((message) => message.tool_calls ?? [])
          .find((call) => call.id === 'call_read_norm_1');
        wireInspection.historicalToolCallPreservesContract =
          historical !== undefined && JSON.parse(historical.function?.arguments ?? '{}').contract === CONTRACT_TOKEN;
        const toolNames = (body.tools ?? []).map((tool) => tool?.function?.name ?? tool?.name);
        wireInspection.round2ToolsStable = JSON.stringify(toolNames.sort()) === JSON.stringify(wireInspection.providerVisibleToolNames);
      }
      sendSSE(response, buildTextChunks('resp_read_norm_done', 'CANARY_READ_DONE', 15));
      return;
    }

    // Business error call.
    if (stage === 4) {
      const call = {
        name: 'todowrite-for-manager',
        argsStr: JSON.stringify({
          program: "class Js extends JsProgram { async run() { return await this.cat('nonexistent-missing-file.txt'); } }",
          contract: CONTRACT_TOKEN,
        }),
      };
      sendSSE(response, buildToolCallChunks('call_read_err_1', call.name, call.argsStr, 25));
      return;
    }

    if (stage === 5) {
      sendSSE(response, buildTextChunks('resp_err_done', 'CANARY_ERROR_DONE', 30));
      return;
    }

    // Cancellation call.
    if (stage === 6) {
      const call = {
        name: 'todowrite-for-manager',
        argsStr: JSON.stringify(CANCEL_ARGUMENTS),
      };
      sendSSE(response, buildToolCallChunks('call_js_cancel_1', call.name, call.argsStr, 35));
      return;
    }

    if (stage === 7) {
      const cancelled = (body.messages ?? []).flatMap((message) => message.tool_calls ?? [])
        .find((call) => call.id === 'call_js_cancel_1');
      publish({ kind: 'manager.cancellation.history', value: cancelled?.function?.arguments ?? null });
    }

    // Executor rejection call (T180): the canary plugin wraps the todowrite-for-manager
    // executor to reject this specific program before it runs. The Host must
    // materialize the rejection as a durable error tool part without invoking
    // tool.execute.after, and the follow-up request must still carry the
    // original wire arguments.
    if (stage === 8) {
      const call = {
        name: 'todowrite-for-manager',
        argsStr: JSON.stringify({
          program: `class Js extends JsProgram { async run() { return '${EXECUTOR_THROW_SENTINEL}'; } }`,
          contract: CONTRACT_TOKEN,
        }),
      };
      sendSSE(response, buildToolCallChunks('call_exec_throw_1', call.name, call.argsStr, 45));
      return;
    }

    if (stage === 9) {
      const thrown = (body.messages ?? []).flatMap((message) => (message.tool_calls ?? []))
        .find((call) => call.id === 'call_exec_throw_1');
      const thrownArguments = thrown ? JSON.parse(thrown.function?.arguments ?? '{}') : null;
      publish({ kind: 'manager.executorThrow.history', value: {
        observed: thrown !== undefined,
        contract: thrownArguments?.contract ?? null,
      } });
      sendSSE(response, buildTextChunks('resp_exec_throw_done', 'CANARY_EXECUTOR_THROW_DONE', 50));
      return;
    }
    sendSSE(response, buildTextChunks(`resp_stage_${stage}`, 'CANARY_OK', 40));
    return;
  }

  sendJSON(response, 404, { error: `unexpected ${request.method} ${url.pathname}` });
});

// ── Host client helper ──────────────────────────────────────────────────────

const request = async (baseUrl, method, pathname, body, expectedStatus) => {
  const response = await fetch(baseUrl + pathname, {
    method,
    headers: { 'content-type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const text = await response.text();
  if (expectedStatus !== undefined) {
    const allowed = Array.isArray(expectedStatus) ? expectedStatus : [expectedStatus];
    assert.ok(allowed.includes(response.status), `${method} ${pathname}: ${text}`);
  }
  return { status: response.status, data: text ? JSON.parse(text) : null };
};

const sessionIdOf = ({ data }) => data?.data?.data?.id ?? data?.data?.id ?? data?.id;

const prompt = (messageID, text) => ({
  messageID,
  agent: 'manager',
  model: { providerID: 'test', modelID: 'test-model' },
  parts: [{ type: 'text', text }],
});

const waitForTurnSettled = async (messageID) => {
  const deadline = Date.now() + 25000;
  let state;
  while (Date.now() < deadline) {
    const messages = await request(host.baseUrl, 'GET', `/session/${sessionID}/message`, undefined, 200);
    const statuses = await request(host.baseUrl, 'GET', '/session/status', undefined, 200);
    state = { sessionID, messageID, messages: messages.data, status: statuses.data?.[sessionID] };
    if (isCanaryTurnSettled(state)) return;
    await new Promise((resolve) => setTimeout(resolve, 50));
  }
  throw new Error(`Physical canary turn did not settle after 25000ms: ${JSON.stringify({
    sessionID, messageID, status: state?.status, messages: state?.messages.map(({ info }) => ({
      messageID: info.id, parentID: info.parentID, role: info.role,
      finish: info.finish, completed: info.time?.completed, error: info.error,
    })),
  })}`);
};

// ── Run Canary Scenario ─────────────────────────────────────────────────────

const scenarioDir = fs.mkdtempSync(path.join(os.tmpdir(), 'wanxiangshu-manager-review-canary-'));
const workspace = path.join(scenarioDir, 'workspace');
fs.mkdirSync(workspace, { recursive: true });

// Create test fixture file for todowrite-for-manager
fs.writeFileSync(
  path.join(workspace, 'fixture-sample.txt'),
  'Hello Wanxiangshu Manager Review Tools Canary\nLine 2: sample text\n',
  'utf8',
);
await initGitWorkspace(workspace);

const host = new ProcessHost();
const sessionIDs = [];
const createManagerSession = async () => {
  const sessionRes = await request(host.baseUrl, 'POST', '/api/session', {
    agent: 'manager', model: { providerID: 'test', id: 'test-model' },
  }, 200);
  sessionID = sessionIdOf(sessionRes);
  assert.ok(sessionID, 'session creation failed to return sessionID');
  sessionIDs.push(sessionID);
};

try {
  await host.start({
    scenarioDir,
    providerUrl: `${provider.url}/v1`,
    pluginPaths: [canaryPluginPath],
    extraEnv: {
      WANXIANGSHU_REVIEW_TOOLS_CANARY_COLLECTOR: collectorUrl,
      ...(productionPluginPath ? { WANXIANGSHU_REVIEW_TOOLS_PRODUCTION_PLUGIN: productionPluginPath } : {}),
    },
  });

  await createManagerSession();

  // 1. Normal todowrite-for-manager prompt
  const msg1 = 'msg_canary_prompt_1';
  await request(host.baseUrl, 'POST', `/session/${sessionID}/message`, prompt(msg1, 'READ_SAMPLE'), 200);

  // Wait for normal before & after observations
  const normBeforeObs = await waitFor(
    ({ kind, value }) => kind === 'tool.execute.before.observed' && value?.callID === 'call_read_norm_1',
  );
  assert.equal(normBeforeObs.value.sessionID, sessionID);
  assert.equal(normBeforeObs.value.physicalUserMessageID, msg1);
  const normAfterObs = await waitFor(
    ({ kind, value }) => kind === 'tool.execute.after.observed' && value?.callID === 'call_read_norm_1',
  );
  const normalTerminal = await waitFor(
    ({ kind, value }) => kind === 'tool.terminal.observed' && value?.callID === 'call_read_norm_1',
  );
  await waitForTurnSettled(msg1);

  // Each execution path owns its physical prompt; automatic Manager continuations
  // must not compete with the next canary case in the same session.
  await createManagerSession();
  // 2. Error path (read nonexistent file)
  const msg3 = 'msg_canary_prompt_3';
  await request(host.baseUrl, 'POST', `/session/${sessionID}/message`, prompt(msg3, 'TRIGGER_ERROR'), 200);
  const errBeforeObs = await waitFor(
    ({ kind, value }) => kind === 'tool.execute.before.observed' && value?.callID === 'call_read_err_1',
  );
  assert.equal(errBeforeObs.value.sessionID, sessionID);
  assert.equal(errBeforeObs.value.physicalUserMessageID, msg3);
  const errAfterObs = await waitFor(
    ({ kind, value }) => kind === 'tool.execute.after.observed' && value?.callID === 'call_read_err_1',
  );
  const errorTerminal = await waitFor(
    ({ kind, value }) => kind === 'tool.terminal.observed' && value?.callID === 'call_read_err_1',
  );
  await waitForTurnSettled(msg3);

  await createManagerSession();
  // 3. Cancellation path (long task + abort)
  const msg4 = 'msg_canary_prompt_4';
  await request(host.baseUrl, 'POST', `/session/${sessionID}/prompt_async`, prompt(msg4, 'TRIGGER_CANCEL'), 204);
  const cancelBeforeObs = await waitFor(
    ({ kind, value }) => kind === 'tool.execute.before.observed' && value?.callID === 'call_js_cancel_1',
  );
  assert.equal(cancelBeforeObs.value.sessionID, sessionID);
  assert.equal(cancelBeforeObs.value.physicalUserMessageID, msg4);
  const cancelRunningObs = await waitFor(
    ({ kind, value }) => kind === 'tool.running.observed' && value?.callID === 'call_js_cancel_1',
  );
  assert.equal(observations.some(({ kind, value }) =>
    kind === 'tool.terminal.observed' && value?.callID === 'call_js_cancel_1'), false,
  'cancellation requires a live call, not one already failed');
  const observationsBeforeAbort = observations.length;

  // Trigger external abort
  await request(host.baseUrl, 'POST', `/session/${sessionID}/abort`, {}, [200, 204]);

  const cancelTerminal = await waitFor(
    ({ kind, value }) => kind === 'tool.terminal.observed' && value?.callID === 'call_js_cancel_1',
  );
  assert.ok(cancelTerminal.sequence > observationsBeforeAbort);
  assert.ok(cancelTerminal.sequence > cancelRunningObs.sequence);
  await request(host.baseUrl, 'POST', `/session/${sessionID}/message`, prompt('msg_canary_prompt_5', 'VERIFY_CANCEL_HISTORY'), 200);
  const cancellationHistory = await waitFor(({ kind }) => kind === 'manager.cancellation.history');
  assert.equal(typeof cancellationHistory.value, 'string');
  const cancellationArguments = JSON.parse(cancellationHistory.value);
  assert.deepEqual(cancellationArguments, CANCEL_ARGUMENTS);
  assert.deepEqual(Object.keys(cancellationArguments), Object.keys(CANCEL_ARGUMENTS));

  await createManagerSession();
  // 4. Executor rejection path (T180): the plugin wrapper rejects the executor
  // before it runs; the Host materializes the error and must NOT invoke after.
  const msg6 = 'msg_canary_prompt_6';
  await request(host.baseUrl, 'POST', `/session/${sessionID}/message`, prompt(msg6, 'TRIGGER_EXECUTOR_THROW'), 200);
  const throwBeforeObs = await waitFor(
    ({ kind, value }) => kind === 'tool.execute.before.observed' && value?.callID === 'call_exec_throw_1',
  );
  assert.equal(throwBeforeObs.value.sessionID, sessionID);
  assert.equal(throwBeforeObs.value.physicalUserMessageID, msg6);
  const executorThrowingObs = await waitFor(
    ({ kind, value }) => kind === 'tool.executor.throwing' && value?.callID === 'call_exec_throw_1',
  );
  const throwTerminal = await waitFor(
    ({ kind, value }) => kind === 'tool.terminal.observed' && value?.callID === 'call_exec_throw_1',
  );
  await waitForTurnSettled(msg6);
  const throwHistoryObs = await waitFor(
    ({ kind }) => kind === 'manager.executorThrow.history',
  );

  // The Host boundary contract for the executor rejection path: after is
  // never invoked. A settled turn (idle + assistant stop) plus a grace window
  // separates "never called" from "called before settle"; a future Host that
  // starts invoking after on this path turns this red (WHAT[019] canary).
  await new Promise((resolve) => setTimeout(resolve, 1500));
  const throwAfterObserved = observations.some(({ kind, value }) =>
    kind === 'tool.execute.after.observed' && value?.callID === 'call_exec_throw_1');

  // ── Verification & Assertions against Fixture ───────────────────────────────

  // Check definition observations
  const defObs = observations.filter(({ kind }) => kind === 'tool.definition.observed');
  for (const tool of REVIEW_TOOLS) {
    const o = defObs.find(({ value }) => value?.toolName === tool);
    assert.ok(o, `missing definition observation for review tool ${tool}`);
    assert.equal(o.value.hasContractProperty, true, `${tool} definition missing contract property`);
  }

  // Normal terminal assertions
  assert.equal(normBeforeObs.value.argsIdentityPreserved, true, 'before must preserve args reference identity');
  assert.equal(normBeforeObs.value.preContractInArgs, true, 'pre-before args must carry contract');
  assert.equal(normBeforeObs.value.postContractInArgs, false, 'post-before must hide contract from args');
  assert.equal(normBeforeObs.value.businessKeysPreserved, true, 'post-before must preserve business args');

  assert.equal(normAfterObs.value.identityWithBefore, true, 'after must receive same args reference as before');
  assert.equal(normAfterObs.value.postAfterContractInArgs, true, 'post-after must restore contract on args');
  assert.equal(normAfterObs.value.postAfterContractValue, CONTRACT_TOKEN, 'restored contract value mismatch');

  // Error terminal assertions
  assert.equal(errBeforeObs.value.postContractInArgs, false, 'error before must hide contract');
  assert.equal(errAfterObs.value.postAfterContractInArgs, true, 'error after must restore contract');

  // Cancellation assertions
  assert.equal(cancelBeforeObs.value.postContractInArgs, false, 'cancel before must hide contract');

  // Wire inspection assertions
  assert.equal(
    wireInspection.historicalToolCallPreservesContract,
    true,
    'round 2 provider request must preserve contract in historical tool_calls',
  );
  const calls = {};
  for (const [name, observation, terminal] of [
    ['normal', normAfterObs, normalTerminal],
    ['executorError', errAfterObs, errorTerminal],
  ]) {
    const value = observation.value;
    calls[name] = {
      sameArguments: value.identityWithBefore,
      originalOrder: value.originalOrder,
      originalValues: value.originalValues,
      durableInputRetainsContract: terminal.value.contractRetained,
      originalDurableInput: terminal.value.originalInput,
      status: terminal.value.status,
    };
    assert.equal(value.identityWithBefore, true, name);
    assert.equal(value.originalOrder, true, name);
    assert.equal(value.originalValues, true, name);
  }
  calls.cancellation = {
    sameArguments: cancelBeforeObs.value.argsIdentityPreserved,
    originalOrder: isDeepStrictEqual(Object.keys(cancellationArguments), Object.keys(CANCEL_ARGUMENTS)),
    originalValues: isDeepStrictEqual(cancellationArguments, CANCEL_ARGUMENTS),
    status: cancelTerminal.value.status,
    providerHistoryObserved: true,
  };
  assert.equal(calls.normal.status, 'completed');
  assert.equal(calls.executorError.status, 'completed');
  assert.match(errorTerminal.value.output, /FILE_NOT_FOUND|nonexistent-missing-file|does not exist/);
  calls.executorError.failureOutputObserved = true;
  assert.equal(calls.cancellation.status, 'error');

  // Executor rejection assertions (T180 / WHAT[019] Host boundary canary)
  assert.equal(throwBeforeObs.value.postContractInArgs, false, 'executorThrow before must hide contract');
  assert.equal(throwAfterObserved, false, 'executor rejection path must not invoke tool.execute.after');
  assert.equal(throwTerminal.value.status, 'error', 'executor rejection must materialize durable status error');
  assert.ok(
    /T180-EXECUTOR-THROW/.test(JSON.stringify(throwTerminal.value.error ?? '')),
    'durable error must carry the executor rejection text',
  );
  assert.equal(throwTerminal.value.contractRetained, true, 'executorThrow durable input must retain contract');
  assert.equal(throwHistoryObs.value.observed, true, 'follow-up wire request must carry the thrown call history');
  assert.equal(throwHistoryObs.value.contract, CONTRACT_TOKEN, 'executorThrow wire arguments must preserve contract');
  calls.executorThrow = {
    afterInvoked: throwAfterObserved,
    executorRejected: executorThrowingObs.value.message === EXECUTOR_THROW_MESSAGE,
    inMemoryArgsStripped: executorThrowingObs.value.contractInArgs === false,
    status: throwTerminal.value.status,
    durableErrorCarriesRejection: /T180-EXECUTOR-THROW/.test(JSON.stringify(throwTerminal.value.error ?? '')),
    durableInputRetainsContract: throwTerminal.value.contractRetained,
    originalDurableInput: throwTerminal.value.originalInput,
    wireHistoryPreservesContract: throwHistoryObs.value.contract === CONTRACT_TOKEN,
  };

  // ── Output Final Summary Artifact ──────────────────────────────────────────

  const summary = {
    schemaVersion: 1,
    versions: { opencode: opencodeVersion, plugin: pluginVersion },
    reviewTools: REVIEW_TOOLS,
    controlTools: CONTROL_TOOLS,
    wireInspection,
    calls,
    historicalToolCallPreservesContract: wireInspection.historicalToolCallPreservesContract,
    observationsCount: observations.length,
    providerRequestsCount: providerRequests.length,
  };

  process.stdout.write(`${JSON.stringify(summary, null, 2)}\n`);
} catch (err) {
  console.error('[run-manager-review-tools-canary] Canary failed:', err);
  console.error(JSON.stringify({
    observations: observations.filter(({ kind }) => kind !== 'tool.definition.observed' && kind !== 'tool.terminal.observed')
      .slice(-80),
    providerRequests: providerRequests.map((body) => (body.messages ?? [])
      .filter((message) => message.role === 'user')
      .map((message) => typeof message.content === 'string' ? message.content.slice(0, 150) : '<structured>')),
    providerRequestsCount: providerRequests.length,
  }));
  console.error(`Host stdout:\n${host.stdoutLog}\nHost stderr:\n${host.stderrLog}`);
  const hostLogDir = path.join(scenarioDir, 'xdg', 'data', 'opencode', 'log');
  if (fs.existsSync(hostLogDir)) {
    for (const name of fs.readdirSync(hostLogDir).filter((name) => name.endsWith('.log'))) {
      console.error(`Host server log ${name}:\n${fs.readFileSync(path.join(hostLogDir, name), 'utf8').split('\n').slice(-80).join('\n')}`);
    }
  }
  process.exitCode = 1;
} finally {
  for (const ownedSessionID of sessionIDs) {
    if (!host.baseUrl) break;
    try { await request(host.baseUrl, 'POST', `/session/${ownedSessionID}/abort`, {}, [200, 204]); } catch {}
  }
  try {
    await host.stop();
  } catch {}
  await stopHttpServer(provider.server);
  await new Promise((resolve) => collector.close(resolve));
  fs.rmSync(scenarioDir, { recursive: true, force: true });
}
