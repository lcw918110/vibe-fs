import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const repo = path.resolve(here, '../../..');
const wrapper = path.join(here, 'cursor-hint-channel-canary-plugin.mjs');
const production = path.join(repo, 'dist/OpenCode/Plugin/Plugin.js');
const opencode = process.env.OPENCODE_BIN ?? 'opencode';
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

const scenarioDir = fs.mkdtempSync(path.join(os.tmpdir(), 'wxs-cursor-hint-canary-'));
const home = path.join(scenarioDir, 'home');
const xdgConfig = path.join(scenarioDir, 'xdg', 'config');
fs.mkdirSync(home, { recursive: true });
fs.mkdirSync(xdgConfig, { recursive: true });
const routingDir = path.join(home, '.config', 'opencode');
fs.mkdirSync(routingDir, { recursive: true });
fs.writeFileSync(
  path.join(routingDir, 'wanxiangshu.mjs'),
  "export const routingProtocol = 2\nexport const hasTheoreticalCapacity = (role, purpose) => true;\nexport default function route(role, running, previous, purpose) { return { model: 'cursor/default', reasoning: 'none' } }\n",
  'utf8',
);

const startHost = () => {
  const child = spawn(opencode, ['serve', '--port', '0', '--hostname', '127.0.0.1'], {
    cwd: repo,
    env: {
      ...process.env,
      HOME: home,
      USERPROFILE: home,
      XDG_CONFIG_HOME: xdgConfig,
      OPENCODE_CONFIG_CONTENT: JSON.stringify({ plugin: [wrapper] }),
      WANXIANGSHU_CURSOR_CANARY_PLUGIN: production,
    },
    stdio: ['ignore', 'pipe', 'pipe'],
  });

  return new Promise((resolve, reject) => {
    let output = '';
    const timer = setTimeout(() => reject(new Error(`host startup timeout\n${output}`)), 15000);
    const observe = (chunk) => {
      output += chunk.toString();
      const match = output.match(/listening on http:\/\/127\.0\.0\.1:(\d+)/);
      if (!match) return;
      clearTimeout(timer);
      resolve({ child, baseUrl: `http://127.0.0.1:${match[1]}` });
    };
    child.stdout.on('data', observe);
    child.stderr.on('data', observe);
    child.once('error', reject);
    child.once('exit', (code) => reject(new Error(`host exited before listen: ${code}\n${output}`)));
  });
};

const request = async (baseUrl, method, pathname, body) => {
  const response = await fetch(baseUrl + pathname, {
    method,
    headers: {
      'Content-Type': 'application/json',
      'x-opencode-directory': encodeURIComponent(repo),
    },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const text = await response.text();
  let data;
  try { data = JSON.parse(text); } catch { data = text; }
  if (!response.ok) throw new Error(`${method} ${pathname}: ${response.status} ${text}`);
  return data;
};

const sessionIdOf = (created) => created?.data?.data?.id ?? created?.data?.id ?? created?.id;
const messagesOf = (payload) => {
  if (Array.isArray(payload)) return payload;
  if (Array.isArray(payload?.data)) return payload.data;
  if (Array.isArray(payload?.data?.data)) return payload.data.data;
  return [];
};

const summarize = (messages) => {
  const assistants = messages.filter((message) => message?.info?.role === 'assistant');
  const tools = assistants
    .flatMap((message) => message.parts ?? [])
    .filter((part) => part?.type === 'tool');
  const reasoning = assistants
    .flatMap((message) => message.parts ?? [])
    .filter((part) => part?.type === 'reasoning')
    .map((part) => part.text ?? '');
  const text = assistants
    .flatMap((message) => message.parts ?? [])
    .filter((part) => part?.type === 'text' && part.text)
    .map((part) => part.text)
    .at(-1) ?? '';
  return {
    tools,
    reasoning,
    text,
    stopped: assistants.some((message) => message.info?.finish === 'stop'),
  };
};

const host = await startHost();
let sessionId;
try {
  const created = await request(host.baseUrl, 'POST', '/api/session', {
    agent: 'engineer',
    model: { providerID: 'cursor', id: 'default' },
  });
  sessionId = sessionIdOf(created);
  assert.ok(sessionId, `missing session id: ${JSON.stringify(created)}`);

  await request(host.baseUrl, 'POST', `/session/${sessionId}/prompt_async`, {
    agent: 'engineer',
    model: { providerID: 'cursor', modelID: 'default' },
    parts: [{
      type: 'text',
      text: 'Call the todowrite-for-engineer tool exactly once to read AGENTS.md (using await this.cat("AGENTS.md")). After it returns, state SUCCESS if you received file content, otherwise state INTERRUPT. Do not call todowrite-for-engineer again.',
    }],
  });

  const deadline = Date.now() + 30000;
  let summary;
  while (Date.now() < deadline) {
    summary = summarize(messagesOf(await request(host.baseUrl, 'GET', `/session/${sessionId}/message`)));
    if (summary.stopped || summary.tools.length >= 4) break;
    await sleep(500);
  }

  assert.ok(summary, 'Cursor Pair Hint canary produced no session state');
  assert.equal(summary.tools.length, 1, `todowrite-for-engineer must execute exactly once; got ${summary.tools.length}`);
  assert.equal(summary.tools[0].state?.status, 'completed', 'todowrite-for-engineer must remain completed');
  assert.match(summary.text, /SUCCESS/i, `model must consume the completed result: ${summary.text}`);
  assert.equal(
    summary.reasoning.some((text) => /previous .*interrupt|was interrupted|returned .*interrupt|(?:read|todowrite-for-engineer) .* interrupted/i.test(text)),
    false,
    `model must not reinterpret the completed result as interrupt: ${JSON.stringify(summary.reasoning)}`,
  );
  assert.equal(summary.stopped, true, 'provider turn must terminate normally');
  console.log(JSON.stringify({ pass: true, toolCalls: 1, finalText: summary.text }));
} finally {
  if (sessionId) {
    try { await request(host.baseUrl, 'POST', `/session/${sessionId}/abort`, {}); } catch {}
  }
  host.child.kill('SIGTERM');
  await Promise.race([
    new Promise((resolve) => host.child.once('exit', resolve)),
    sleep(3000).then(() => host.child.kill('SIGKILL')),
  ]);
  try { fs.rmSync(scenarioDir, { recursive: true, force: true }); } catch {}
}
