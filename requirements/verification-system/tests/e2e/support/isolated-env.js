/**
 * isolated-env.js — Per-scenario environment isolation.
 *
 * Creates a fully isolated environment with temporary HOME, XDG dirs, TMPDIR,
 * and overrides variables that could leak state between scenarios.
 *
 * Usage:
 *   const env = createIsolatedEnv({
 *     scenarioDir: '/tmp/scenario-xxx',
 *     llmUrl: 'http://127.0.0.1:9999/v1',
 *   });
 *   const child = spawn('opencode', ['serve', '--port', '0'], {
 *     env: { ...process.env, ...env },
 *   });
 *
 * NOTE: The returned object only contains vars that MUST be overridden.
 * Callers should spread process.env first, then spread this result.
 * To truly isolate, callers may also omit process.env entirely and use
 * a minimal set of base vars (PATH, etc).
 *
 * Per the E2E contract:
 *   One scenario = one temp HOME + one temp XDG + one workspace
 *                + one mock provider + one opencode process.
 */

import fs from 'node:fs';
import path from 'node:path';
import { provisionPluginDependency } from './plugin-dependency.js';

/**
 * Generate the OpenCode config for a mock provider.
 */
function makeConfig(llmUrl, pluginPaths = [], opts = {}) {
  const modelDef = {
    id: 'test-model',
    name: 'Test Model',
    attachment: false,
    reasoning: false,
    temperature: false,
    tool_call: true,
    release_date: '2025-01-01',
    limit: {
      context: opts.contextLimit ?? 100000,
      input: opts.contextLimit ?? 100000,
      output: 10000,
    },
    cost: { input: 0, output: 0 },
    options: {},
    variants: { none: {} },
  };
  const modelBDef = {
    ...modelDef,
    id: 'test-model-b',
    name: 'Test Model B',
  };
  // Canonical single-version agents: provider rotation happens at model level
  // via the routing function below, not via fast-/deep- agent doubles.
  const managedAgents = {
    orchestrator: { model: 'test/test-model' },
    manager: { model: 'test/test-model' },
    engineer: { model: 'test/test-model' },
    devops: { model: 'test/test-model' },
    bookkeeper: { model: 'test/test-model' },
  };
  return {
    formatter: false,
    lsp: false,
    // OpenCode's file-snapshot tracking exists to power revert, and no scenario asserts on it.
    // It costs the Host ~84 synchronous `git` spawns per canary (measured: 62
    // `-c core.autocrlf=false` plus 22 against the snapshot gitdir, ~160ms) on the very event loop
    // the run needs, so the harness runs without it. Anything that goes red with it off is a
    // dependency to fix, not a reason to turn it back on.
    snapshot: false,
    permission: { '*': 'allow' },
    model: opts.model || 'test/test-model',
    provider: {
      test: {
        name: 'Test',
        id: 'test',
        env: [],
        npm: '@ai-sdk/openai-compatible',
        models: { 'test-model': { ...modelDef }, 'test-model-b': { ...modelBDef } },
        options: { apiKey: opts.apiKey || 'test-key', baseURL: `${llmUrl}` },
      },
      opencode: {
        name: 'OpenCode',
        id: 'opencode',
        env: [],
        npm: '@ai-sdk/openai-compatible',
        models: { 'test-model': { ...modelDef } },
        options: { apiKey: opts.apiKey || 'test-key', baseURL: `${llmUrl}` },
      },
      backup: {
        name: 'Backup',
        id: 'backup',
        env: [],
        npm: '@ai-sdk/openai-compatible',
        models: { 'test-model': { ...modelDef } },
        options: { apiKey: opts.apiKey || 'test-key', baseURL: `${llmUrl}` },
      },
    },
    agent: managedAgents,
    plugin: pluginPaths,
    // undefined is dropped by JSON.stringify, so a caller that passes no
    // mcpServers keeps the config bytes byte-identical to before; only a
    // scenario that declares an MCP fixture grows a key.
    mcp: opts.mcpServers ?? undefined,
  };
}

/**
 * Build a fully isolated environment object.
 *
 * Returns ONLY the variables that MUST be overridden for a clean scenario.
 *
 * @param {object} opts
 * @param {string} opts.scenarioDir - Root directory for this scenario
 * @param {string} opts.llmUrl - Mock LLM base URL (e.g. "http://127.0.0.1:PORT/v1")
 * @param {string[]} [opts.pluginPaths] - Paths to plugin JS files
 * @param {object} [opts.config] - Extra config overrides
 * @param {number} [opts.contextLimit] - Provider context limit
 * @param {string} [opts.model] - Model ID string (default "test/test-model")
 * @param {string} [opts.apiKey] - Provider API key
 * @param {string} [opts.routingSource] - Exact isolated ~/.config/opencode/wanxiangshu.mjs body; overrides the frozen default when present
 * @param {object} [opts.mcpServers] - MCP server map for Config.mcp ({ [key]: { type: 'local', command, enabled } })
 * @param {object} [opts.extraEnv] - Additional env vars for this test
 * @returns {object} Environment key-value object to merge over process.env
 */
export function createIsolatedEnv(opts) {
  const { scenarioDir, llmUrl } = opts;
  const home = path.join(scenarioDir, 'home');
  const xdgData = path.join(scenarioDir, 'xdg', 'data');
  const xdgConfig = path.join(scenarioDir, 'xdg', 'config');
  const xdgCache = path.join(scenarioDir, 'xdg', 'cache');
  const xdgState = path.join(scenarioDir, 'xdg', 'state');
  const tmpDir = path.join(scenarioDir, 'tmp');

  for (const dir of [home, xdgData, xdgConfig, xdgCache, xdgState, tmpDir]) {
    fs.mkdirSync(dir, { recursive: true });
  }

  // execution-model-routing intentionally ignores XDG_CONFIG_HOME and owns the
  // fixed ~/.config/opencode/wanxiangshu.mjs path. E2E must therefore seed the
  // isolated HOME explicitly; otherwise production would bootstrap its real-world
  // recommended providers into a test provider process.
  const routingDir = path.join(home, '.config', 'opencode');
  fs.mkdirSync(routingDir, { recursive: true });
  const defaultRoutingSource = `export const routingProtocol = 2
export const hasTheoreticalCapacity = (role, purpose) => true;
export default function route(role, running, previous, purpose) {
  if (new Set(['manager', 'orchestrator', 'engineer', 'devops', 'bookkeeper']).has(role)) return { model: 'test/test-model', reasoning: 'none' }
  throw new Error('unexpected managed role: ' + role + ' | stack: ' + new Error().stack)
}
\n`;
  // An explicit scenario/host routingSource wins; the frozen default keeps every
  // caller that passes none byte-identical to the previous behavior.
  fs.writeFileSync(path.join(routingDir, 'wanxiangshu.mjs'), opts.routingSource ?? defaultRoutingSource, 'utf8');

  provisionPluginDependency(path.join(xdgConfig, 'opencode'));

  const realCacheDir = process.env.XDG_CACHE_HOME || path.join(process.env.HOME || '', '.cache');
  if (fs.existsSync(realCacheDir)) {
    const src = path.join(realCacheDir, 'tree-sitter-language-pack');
    const dest = path.join(xdgCache, 'tree-sitter-language-pack');
    if (fs.existsSync(src) && !fs.existsSync(dest)) {
      try { fs.symlinkSync(src, dest, 'dir'); } catch {}
    }
  }

  const repoNodeModules = path.resolve(process.cwd(), 'node_modules');
  if (fs.existsSync(repoNodeModules)) {
    const destNodeModules = path.join(scenarioDir, 'node_modules');
    if (!fs.existsSync(destNodeModules)) {
      try { fs.symlinkSync(repoNodeModules, destNodeModules, 'dir'); } catch {}
    }
  }

  const mockApiBase = llmUrl.replace(/\/v1$/, '') + '/api';
  const config = makeConfig(llmUrl, opts.pluginPaths, opts);
  const extraEnv = opts.extraEnv || {};
  return {
    // extraEnv first, so non-isolation variables pass through; isolation vars below will always win.
    ...extraEnv,
    // Isolation dirs
    HOME: home,
    USERPROFILE: home,
    XDG_DATA_HOME: xdgData,
    XDG_CONFIG_HOME: xdgConfig,
    XDG_CACHE_HOME: xdgCache,
    XDG_STATE_HOME: xdgState,
    TMPDIR: tmpDir,
    TMP: tmpDir,
    TEMP: tmpDir,

    // OpenCode configuration
    NO_PROXY: '*',
    OPENCODE_OFFLINE: '1',
    OPENCODE_TELEMETRY_DISABLED: '1',
    CHECK_UPDATE: '0',
    DISABLE_TELEMETRY: '1',
    OPENCODE_DISABLE_AUTOUPDATE: '1',
    OPENCODE_DISABLE_AUTOCOMPACT: '1',
    OPENCODE_DISABLE_MODELS_FETCH: '1',
    OPENCODE_AUTH_CONTENT: '{}',
    OPENCODE_EXPERIMENTAL_EVENT_SYSTEM: 'true',
    OPENCODE_ENABLE_EXA: '0',
    OPENCODE_CONFIG_CONTENT: JSON.stringify(config),
    OPENCODE_PERMISSION: JSON.stringify({ '*': 'allow' }),

    // Mock LLM
    OLLAMA_API_KEY: opts.apiKey || 'test-key',
    OLLAMA_API_BASE: mockApiBase,

    // Prevent Bun/Node debug ports and heap profiling from leaking into spawned opencode
    NODE_PATH: `${path.join(scenarioDir, 'node_modules')}${path.delimiter}${repoNodeModules}${path.delimiter}${extraEnv.NODE_PATH || process.env.NODE_PATH || ''}`,
    NODE_OPTIONS: '',
    BUN_OPTIONS: '',

    // Zero-out the fallback continuation rate-limit governor in E2E.
    WANXIANGSHU_TEST: 'true',
    WANXIANGSHU_PROVIDER_LANGUAGE: 'en',
    WANXIANGSHU_DIAG: '1',

    PATH: extraEnv.PATH || process.env.PATH || '',
  };
}
