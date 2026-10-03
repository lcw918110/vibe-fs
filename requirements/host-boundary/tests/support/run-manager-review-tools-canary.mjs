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
assert.match(opencodeVersion, /^1\.18\./);
assert.ok(productionPluginPath, 'production plugin must be available');

const REVIEW_TOOLS = ['js-manager'];
const CONTROL_TOOLS = ['read', 'grep', 'glob', 'js-engineer', 'js-devops'];
const CONTRACT_TOKEN = 'do-not-use-except-for-review';
const CANCEL_ARGUMENTS = {
  program: 'class Js extends JsProgram { async run() { await new Promise(() => {}); return null; } }',
  contract: CONTRACT_TOKEN,
  estimated_readonly_rounds: 0,
};

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
    const timer = setTimeout(() => {
      const idx = waiters.findIndex((w) => w.resolve === resolve);
      if (idx >= 0) waiters.splice(idx, 1);
      reject(new Error(`Timed out waiting for ${predicate.toString()} after ${timeoutMs}ms`));
    }, timeoutMs);
    waiters.push({
      predicate,
      resolve: (value) => {
        clearTimeout(timer);
        resolve(value);
      },
    });
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

let sessionID = null;
/** Diagnostic only — lane replies are keyed by prompt/tool history, not this counter. */
let managerCompletions = 0;

const messageText = (message) => {
  if (typeof message?.content === 'string') return message.content;
  if (!Array.isArray(message?.content)) return '';
  return message.content
    .map((part) => (typeof part?.text === 'string' ? part.text : typeof part === 'string' ? part : ''))
    .join('');
};

const lastUserText = (body) => {
  const messages = Array.isArray(body?.messages) ? body.messages : [];
  for (let index = messages.length - 1; index >= 0; index -= 1) {
    if (messages[index]?.role === 'user') return messageText(messages[index]);
  }
  return '';
};

/** Latest tool result id, if the Host is asking for a post-tool assistant turn. */
const pendingToolResultCallId = (body) => {
  const messages = Array.isArray(body?.messages) ? body.messages : [];
  for (let index = messages.length - 1; index >= 0; index -= 1) {
    const message = messages[index];
    if (message?.role === 'tool' && typeof message?.tool_call_id === 'string') {
      return message.tool_call_id;
    }
    if (message?.role === 'user' || message?.role === 'assistant') break;
  }
  return null;
};

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
  const hasManagerReviewTools = toolNames.includes('js-manager');

  if (!hasManagerReviewTools) {
    return false;
  }

  // Reject blogger requests that might slip through
  const messages = Array.isArray(body?.messages) ? body.messages : [];
  const hasBloggerPrompt = messages.some(
    (m) =>
      (m?.role === 'system' || m?.role === 'user') &&
      typeof m?.content === 'string' &&
      (m.content.includes('role/blogger') ||
        m.content.includes('BloggerSystemPrompt') ||
        m.content.includes('Call the chronicle tool exactly once now')),
  );
  if (hasBloggerPrompt) {
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

    // 1. Isolate Title requests -> return harmless plain text, do not advance manager steps
    if (isTitleRequest(body)) {
      sendSSE(response, buildTextChunks(`title_${Date.now()}`, 'Manager Review Canary Title', 1));
      return;
    }

    // 2. Isolate Blogger or other companion sidecars -> return harmless text, do not advance manager steps
    if (!isManagerRequest(request, body)) {
      const chronicle = (body.tools ?? []).find((tool) => (tool?.function?.name ?? tool?.name) === 'chronicle');
      if (chronicle) {
        const tip = chronicle.function?.parameters?.properties?.tip?.enum?.[0];
        assert.equal(typeof tip, 'string', 'Blogger wire must publish a legal chronicle tip');
        sendSSE(response, buildToolCallChunks(`chronicle_${providerRequests.length}`, 'chronicle', JSON.stringify({
          charge: 'Preserve the canary observation that matters after this sidecar turn.',
          occurrence: 'The Manager canary inspected its tool contract.',
          settlement: 'The inspected contract is represented in the current canary record.',
          consequence: 'The sidecar can finish without inventing additional work.',
          tip,
        }), 1));
        return;
      }
      sendSSE(response, buildTextChunks(`companion_${Date.now()}`, 'COMPANION_OK', 1));
      return;
    }

    // 3. Manager lane: reply by prompt / tool-result history.
    // A global step counter desyncs when Host retries or inserts sidecar
    // completions, then the cancel tool call is never emitted and the
    // harness burns a full wait. Content routing keeps each round O(1).
    managerCompletions += 1;
    const toolResultId = pendingToolResultCallId(body);
    const userText = lastUserText(body);

    if (toolResultId === 'call_read_norm_1') {
      const historical = (body.messages ?? []).flatMap((message) => message.tool_calls ?? [])
        .find((call) => call.id === 'call_read_norm_1');
      wireInspection.historicalToolCallPreservesContract =
        historical !== undefined && JSON.parse(historical.function?.arguments ?? '{}').contract === CONTRACT_TOKEN;
      sendSSE(response, buildTextChunks('resp_read_norm_done', 'CANARY_READ_DONE', 15));
      return;
    }

    if (toolResultId === 'call_read_err_1') {
      sendSSE(response, buildTextChunks('resp_err_done', 'CANARY_ERROR_DONE', 30));
      return;
    }

    if (toolResultId === 'call_js_cancel_1' || userText.includes('VERIFY_CANCEL_HISTORY')) {
      const cancelled = (body.messages ?? []).flatMap((message) => message.tool_calls ?? [])
        .find((call) => call.id === 'call_js_cancel_1');
      publish({ kind: 'manager.cancellation.history', value: cancelled?.function?.arguments ?? null });
      sendSSE(response, buildTextChunks(`resp_cancel_hist_${managerCompletions}`, 'CANARY_OK', 40));
      return;
    }

    if (userText.includes('READ_SAMPLE')) {
      if (Array.isArray(body.tools)) {
        wireInspection.providerVisibleToolNames = body.tools.map((tool) => tool?.function?.name ?? tool?.name).sort();
        wireInspection.providerVisibleProtocolAbsence = body.tools.every((tool) => {
          const properties = tool?.function?.parameters?.properties ?? tool?.parameters?.properties ?? {};
          return !Object.hasOwn(properties, 'estimated_readonly_rounds') && !Object.hasOwn(properties, 'self_note');
        });
        const readManagerTool = body.tools.find(
          (t) => (t?.function?.name ?? t?.name) === 'js-manager',
        );
        const contractProp = readManagerTool?.function?.parameters?.properties?.contract;
        const required = readManagerTool?.function?.parameters?.required ?? [];
        wireInspection.contractType = contractProp?.type ?? null;
        wireInspection.contractEnum = contractProp?.enum ?? null;
        wireInspection.contractRequired = Array.isArray(required) && required.includes('contract');
      }
      sendSSE(
        response,
        buildToolCallChunks(
          'call_read_norm_1',
          'js-manager',
          JSON.stringify({
            program: "class Js extends JsProgram { async run() { const f = await this.file('fixture-sample.txt'); return f.text('^', '$'); } }",
            contract: CONTRACT_TOKEN,
            estimated_readonly_rounds: 0,
          }),
          10,
        ),
      );
      return;
    }

    if (userText.includes('VERIFY_STABILITY')) {
      let foundContractInHistory = false;
      for (const msg of body.messages ?? []) {
        if (msg.role === 'assistant' && Array.isArray(msg.tool_calls)) {
          for (const tc of msg.tool_calls) {
            try {
              const parsedArgs = JSON.parse(tc.function?.arguments ?? '{}');
              if (parsedArgs.contract === CONTRACT_TOKEN) {
                foundContractInHistory = true;
              }
            } catch {}
          }
        }
      }
      wireInspection.historicalToolCallPreservesContract ||= foundContractInHistory;
      const toolNames = (body.tools ?? []).map((t) => t?.function?.name ?? t?.name);
      wireInspection.round2ToolsStable =
        JSON.stringify(toolNames.sort()) === JSON.stringify(wireInspection.providerVisibleToolNames);
      publish({ kind: 'manager.stability.done' });
      sendSSE(response, buildTextChunks('resp_stability_done', 'CANARY_STABILITY_DONE', 20));
      return;
    }

    if (userText.includes('TRIGGER_ERROR')) {
      sendSSE(
        response,
        buildToolCallChunks(
          'call_read_err_1',
          'js-manager',
          JSON.stringify({
            program: "class Js extends JsProgram { async run() { const f = await this.file('nonexistent-missing-file.txt'); return f.text('^', '$'); } }",
            contract: CONTRACT_TOKEN,
            estimated_readonly_rounds: 0,
          }),
          25,
        ),
      );
      return;
    }

    if (userText.includes('TRIGGER_CANCEL')) {
      sendSSE(
        response,
        buildToolCallChunks('call_js_cancel_1', 'js-manager', JSON.stringify(CANCEL_ARGUMENTS), 35),
      );
      return;
    }

    sendSSE(response, buildTextChunks(`resp_idle_${managerCompletions}`, 'CANARY_OK', 40));
    return;
  }

  sendJSON(response, 404, { error: `unexpected ${request.method} ${url.pathname}` });
});

// ── Host client helper ──────────────────────────────────────────────────────

const isTransientHostError = (status, text) =>
  status >= 500 && /Unexpected server error/i.test(text ?? '');

const requestOnce = async (baseUrl, method, pathname, body) => {
  const response = await fetch(baseUrl + pathname, {
    method,
    headers: { 'content-type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const text = await response.text();
  return { status: response.status, text };
};

/** Retry only session creation on Host 5xx. Replaying /message or
 * /prompt_async after a false 500 duplicates Host work and desyncs the
 * mock lane — that is the expensive failure mode. */
const request = async (baseUrl, method, pathname, body, expectedStatus) => {
  const mayRetry = method === 'POST' && pathname === '/api/session';
  const backoffsMs = mayRetry ? [0, 250, 750] : [0];
  let status = 0;
  let text = '';
  for (const delayMs of backoffsMs) {
    if (delayMs > 0) await new Promise((resolve) => setTimeout(resolve, delayMs));
    ({ status, text } = await requestOnce(baseUrl, method, pathname, body));
    if (!isTransientHostError(status, text)) break;
  }
  if (expectedStatus !== undefined) {
    const allowed = Array.isArray(expectedStatus) ? expectedStatus : [expectedStatus];
    assert.ok(allowed.includes(status), `${method} ${pathname}: ${text}`);
  }
  return { status, data: text ? JSON.parse(text) : null };
};

const sessionIdOf = ({ data }) => data?.data?.data?.id ?? data?.data?.id ?? data?.id;

const prompt = (messageID, text) => ({
  messageID,
  agent: 'manager',
  model: { providerID: 'test', modelID: 'test-model' },
  parts: [{ type: 'text', text }],
});

// ── Run Canary Scenario ─────────────────────────────────────────────────────

const scenarioDir = fs.mkdtempSync(path.join(os.tmpdir(), 'wanxiangshu-manager-review-canary-'));
const workspace = path.join(scenarioDir, 'workspace');
fs.mkdirSync(workspace, { recursive: true });

// Create test fixture file for js-manager
fs.writeFileSync(
  path.join(workspace, 'fixture-sample.txt'),
  'Hello Wanxiangshu Manager Review Tools Canary\nLine 2: sample text\n',
  'utf8',
);
await initGitWorkspace(workspace);

const host = new ProcessHost();

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

  const sessionRes = await request(
    host.baseUrl,
    'POST',
    '/api/session',
    {
      agent: 'manager',
      model: { providerID: 'test', id: 'test-model' },
    },
    200,
  );
  sessionID = sessionIdOf(sessionRes);
  assert.ok(sessionID, 'session creation failed to return sessionID');

  // 1. Normal js-manager prompt
  const msg1 = 'msg_canary_prompt_1';
  await request(host.baseUrl, 'POST', `/session/${sessionID}/message`, prompt(msg1, 'READ_SAMPLE'), 200);

  // Wait for normal before & after observations
  const normBeforeObs = await waitFor(
    ({ kind, value }) => kind === 'tool.execute.before.observed' && value?.callID === 'call_read_norm_1',
  );
  const normAfterObs = await waitFor(
    ({ kind, value }) => kind === 'tool.execute.after.observed' && value?.callID === 'call_read_norm_1',
  );
  const normalTerminal = await waitFor(
    ({ kind, value }) => kind === 'tool.terminal.observed' && value?.callID === 'call_read_norm_1',
  );
  await waitFor(({ kind, value, sequence }) =>
    kind === 'session.idle.observed' && value?.sessionID === sessionID && sequence > normalTerminal.sequence);

  // 2. Round 2: stability prompt
  const msg2 = 'msg_canary_prompt_2';
  await request(host.baseUrl, 'POST', `/session/${sessionID}/message`, prompt(msg2, 'VERIFY_STABILITY'), 200);
  const stability = await waitFor(({ kind }) => kind === 'manager.stability.done');
  await waitFor(({ kind, value, sequence }) =>
    kind === 'session.idle.observed' && value?.sessionID === sessionID && sequence > stability.sequence);

  // 3. Round 3: error path (read nonexistent file)
  const msg3 = 'msg_canary_prompt_3';
  await request(host.baseUrl, 'POST', `/session/${sessionID}/message`, prompt(msg3, 'TRIGGER_ERROR'), 200);
  const errBeforeObs = await waitFor(
    ({ kind, value }) => kind === 'tool.execute.before.observed' && value?.callID === 'call_read_err_1',
  );
  const errAfterObs = await waitFor(
    ({ kind, value }) => kind === 'tool.execute.after.observed' && value?.callID === 'call_read_err_1',
  );
  const errorTerminal = await waitFor(
    ({ kind, value }) => kind === 'tool.terminal.observed' && value?.callID === 'call_read_err_1',
  );
  await waitFor(({ kind, value, sequence }) =>
    kind === 'session.idle.observed' && value?.sessionID === sessionID && sequence > errorTerminal.sequence);

  // 4. Round 4: cancellation path (long task + abort)
  const msg4 = 'msg_canary_prompt_4';
  await request(host.baseUrl, 'POST', `/session/${sessionID}/prompt_async`, prompt(msg4, 'TRIGGER_CANCEL'), 204);
  const cancelBeforeObs = await waitFor(
    ({ kind, value }) => kind === 'tool.execute.before.observed' && value?.callID === 'call_js_cancel_1',
  );
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
    observations: observations.slice(-20).map(({ sequence, kind, value }) => ({
      sequence, kind, sessionID: value?.sessionID, callID: value?.callID, status: value?.status, toolName: value?.toolName,
    })),
    providerRequests: providerRequests.map((body) => (body.messages ?? [])
      .filter((message) => message.role === 'user')
      .map((message) => typeof message.content === 'string' ? message.content.slice(0, 150) : '<structured>')),
    providerRequestsCount: providerRequests.length, managerCompletions,
  }));
  console.error(`Host stdout:\n${host.stdoutLog}\nHost stderr:\n${host.stderrLog}`);
  process.exitCode = 1;
} finally {
  if (sessionID && host.baseUrl) {
    try {
      await request(host.baseUrl, 'POST', `/session/${sessionID}/abort`, {}, 204);
    } catch {}
  }
  try {
    await host.stop();
  } catch {}
  await stopHttpServer(provider.server);
  await new Promise((resolve) => collector.close(resolve));
  fs.rmSync(scenarioDir, { recursive: true, force: true });
}
