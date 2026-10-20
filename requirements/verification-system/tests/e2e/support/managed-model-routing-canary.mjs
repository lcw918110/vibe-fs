import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

import { ProcessHost } from './process-host.js';
import { initGitWorkspace } from './process-host-utils.js';
import { resolvePluginPath } from './scenario-paths.js';
import { startHttpServer, stopHttpServer, readRequestBody } from './strict-mock-server.js';
import { buildTextChunks, sendJSON, sendSSE } from './strict-mock-sse.js';

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

const request = async (baseUrl, method, pathname, body) => {
  const response = await fetch(baseUrl + pathname, {
    method,
    headers: { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const text = await response.text();
  let data;
  try { data = JSON.parse(text); } catch { data = text; }
  if (!response.ok) throw new Error(`${method} ${pathname}: ${response.status} ${text}`);
  return data;
};

const sessionIdOf = (created) => created?.data?.data?.id ?? created?.data?.id ?? created?.id;
const messageText = (body) => (body?.messages ?? [])
  .flatMap((message) => {
    if (typeof message?.content === 'string') return [message.content];
    if (Array.isArray(message?.content)) return message.content.map((item) => item?.text ?? '').filter(Boolean);
    return [];
  })
  .join('\n');

const scenarioDir = fs.mkdtempSync(path.join(os.tmpdir(), 'wanxiangshu-model-routing-canary-'));
const workspace = path.join(scenarioDir, 'workspace');
fs.mkdirSync(workspace, { recursive: true });
await initGitWorkspace(workspace);

const captured = [];
let responseCounter = 0;
const provider = await startHttpServer(async (req, res) => {
  const url = new URL(req.url, `http://${req.headers.host}`);

  if ((url.pathname === '/v1/models' || url.pathname === '/models') && req.method === 'GET') {
    sendJSON(res, 200, {
      object: 'list',
      data: [
        { id: 'test-model', object: 'model' },
        { id: 'test-model-b', object: 'model' },
      ],
    });
    return;
  }

  if (url.pathname === '/v1/chat/completions' && req.method === 'POST') {
    try {
      const body = await readRequestBody(req);
      captured.push(body);
      responseCounter += 1;
      sendSSE(res, buildTextChunks(`routing_canary_${responseCounter}`, 'CANARY_OK', 1));
    } catch (error) {
      sendJSON(res, 400, { error: error?.message ?? String(error) });
    }
    return;
  }

  sendJSON(res, 404, { error: `unexpected ${req.method} ${url.pathname}` });
});

const host = new ProcessHost();
let sessionId;
try {
  await host.start({
    scenarioDir,
    providerUrl: `${provider.url}/v1`,
    pluginPaths: [resolvePluginPath('opencode')],
        routingSource: `export const routingProtocol = 2
export const hasTheoreticalCapacity = (role, purpose) => true;
export default function route(role, running, previous, purpose) {

  if (new Set(['manager', 'orchestrator', 'engineer', 'devops', 'bookkeeper', 'plan']).has(role)) return { model: 'test/test-model', reasoning: 'none' }
  throw new Error('unexpected managed role: ' + role)
}
`,
  });

  const created = await request(host.baseUrl, 'POST', '/api/session', {
    agent: 'engineer',
    model: { providerID: 'test', id: 'test-model' },
  });
  sessionId = sessionIdOf(created);
  assert.ok(sessionId, `missing session id: ${JSON.stringify(created)}`);

  const marker = 'MODEL_ROUTING_PHYSICAL_CANARY_7F4F';
  await request(host.baseUrl, 'POST', `/session/${sessionId}/prompt_async`, {
    agent: 'engineer',
    model: { providerID: 'test', modelID: 'test-model' },
    parts: [{ type: 'text', text: marker }],
  });

  const deadline = Date.now() + 15000;
  let providerRequest;
  while (Date.now() < deadline) {
    providerRequest = captured.find((body) => messageText(body).includes(marker));
    if (providerRequest) break;
    await sleep(100);
  }

  assert.ok(
    providerRequest,
    `managed request never reached fake provider; host stderr tail:\n${host.stderrLog.slice(-4000)}`,
  );
  assert.equal(
    providerRequest.model,
    'test-model-b',
    'Host request carried test-model, but physical provider request must use MJS lease test-model-b',
  );

  console.log(JSON.stringify({
    pass: true,
    inputModel: 'test-model',
    providerModel: providerRequest.model,
    role: 'engineer',
  }));
} finally {
  if (sessionId && host.baseUrl) {
    try { await request(host.baseUrl, 'POST', `/session/${sessionId}/abort`, {}); } catch {}
  }
  try { await host.stop(); } catch {}
  await stopHttpServer(provider.server);
  fs.rmSync(scenarioDir, { recursive: true, force: true });
}
