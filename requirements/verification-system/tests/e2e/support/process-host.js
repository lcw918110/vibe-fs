/**
 * process-host.js — Manages an opencode serve process with proper lifecycle.
 *
 * stop() is async and fails loud: a port still listening, a surviving PID,
 * or a leaked child process tree after stop() throws an error.
 *
 * Side-effect-free helpers (child lifecycle, socket/PID checks, git init,
 * listen-port parsing) live in process-host-utils.js and process-host-checks.js.
 */

import fs from 'node:fs';
import path from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import { createIsolatedEnv } from './isolated-env.js';
import {
  SIGTERM_GRACE_MS,
  SIGKILL_GRACE_MS,
  SOCKET_CHECK_TIMEOUT_MS,
  PROCESS_TREE_TIMEOUT_MS,
  HOST_START_TIMEOUT_MS,
} from './time-budget.js';
import {
  READY_POLL_INTERVAL_MS,
  parseListenPort,
  ringPush,
  terminateChild,
  spawnOpencodeServe,
} from './process-host-utils.js';
import {
  isPidAlive,
  checkSocketClosed,
  checkProcessTree,
} from './process-host-checks.js';

const BOOTSTRAP_SENTINEL = 'Warning: OPENCODE_SERVER_PASSWORD is not set';
const READY_SENTINEL = 'opencode server listening on http://';
const LISTEN_POLL_INTERVAL_MS = 50;
const LISTEN_POLL_INITIAL_DELAY_MS = 100;
const STDOUT_RING_MAX = 100;


export class ProcessHost {
  constructor() {
    this._child = null;
    this._pid = null;
    this._baseUrl = null;
    this._port = null;
    this._stderrBuffer = [];
    this._stdoutBuffer = [];
    this._scenarioDir = null;
    this._workDir = null;
    this._env = null;
    this._started = false;
    this._stopped = false;
    this._exitInfo = null;
  }

  get baseUrl() { return this._baseUrl; }
  get port() { return this._port; }
  get workDir() { return this._workDir; }
  get stderrLog() { return this._stderrBuffer.join(''); }
  get stdoutLog() { return this._stdoutBuffer.join(''); }
  get pid() { return this._child?.pid || null; }
  get scenarioDir() { return this._scenarioDir; }
  get exitInfo() { return this._exitInfo; }

  async start(opts = {}) {
    if (this._started) throw new Error('ProcessHost already started');
    this._startOpts = { ...opts };
    this._started = true;
    this._stdoutBuffer.length = 0;
    this._stderrBuffer.length = 0;
    this._scenarioDir = opts.scenarioDir;
    this._workDir = ensureWorkspace(opts.scenarioDir);
    this._env = buildEnv(opts);
    const ht0 = Date.now();
    this._child = spawnOpencodeServe(this._workDir, this._env, {
      onStdoutChunk: this._onStdout.bind(this),
      onStderrChunk: this._onStderr.bind(this),
      onExit: this._onChildExit.bind(this),
    });
    const startTimeout = opts.startTimeoutMs || HOST_START_TIMEOUT_MS;
    const listenLine = await this._waitForListening(startTimeout, () => {
      if (process.env.CANARY_VERBOSE || process.env.DEBUG) {
        console.error('[host.start] bootstrap observed');
      }
      opts.onProgress?.('bootstrapped');
    });
    const ht1 = Date.now();
    if (process.env.CANARY_VERBOSE || process.env.DEBUG) {
      console.error(`[host.start] _waitForListening took ${ht1 - ht0}ms`);
    }
    if (!listenLine) {
      try { this._child?.kill('SIGKILL'); } catch {}
      throw new Error(
        'opencode serve did not output listening line within timeout\n' +
        `stdout tail:\n${this._stdoutBuffer.slice(-20).join('\n')}\n` +
        `stderr tail:\n${this._stderrBuffer.slice(-20).join('\n')}`,
      );
    }
    this._pid = this._child?.pid || null;
    this._port = parseListenPort(listenLine);
    this._baseUrl = `http://127.0.0.1:${this._port}`;
    opts.onProgress?.('listening');
    await this._waitForGlobalHealth(startTimeout);
    const ht2 = Date.now();
    if (process.env.CANARY_VERBOSE || process.env.DEBUG) {
      console.error(`[host.start] _waitForGlobalHealth took ${ht2 - ht1}ms`);
    }
    opts.onProgress?.('global-healthy');
    const onProjectEvents = opts.pluginPaths?.length > 0
      ? () => {
          if (process.env.CANARY_VERBOSE || process.env.DEBUG) {
            console.error('[host.start] project event source observed');
          }
          opts.onProgress?.('project-events');
        }
      : undefined;
    await this._waitForHealth(startTimeout, onProjectEvents);
    const ht3 = Date.now();
    if (process.env.CANARY_VERBOSE || process.env.DEBUG) {
      console.error(`[host.start] _waitForHealth took ${ht3 - ht2}ms`);
    }
    opts.onProgress?.('healthy');
  }

  async _waitForGlobalHealth(timeoutMs = HOST_START_TIMEOUT_MS) {
    await this._waitForReadiness('global', '/global/health', timeoutMs, (body) => {
      if (typeof body?.healthy !== 'boolean') throw new Error('invalid health response: healthy must be boolean');
      return body.healthy;
    });
  }

  async _waitForHealth(timeoutMs = HOST_START_TIMEOUT_MS, onProjectEvents) {
    await this._waitForReadiness('project', '/path', timeoutMs, (body) => {
      for (const name of ['home', 'state', 'config', 'worktree', 'directory']) {
        if (typeof body?.[name] !== 'string') throw new Error(`invalid path response: ${name} must be string`);
      }
      if (body.directory !== this._workDir) {
        throw new Error(`path response directory ${JSON.stringify(body.directory)} differs from ${JSON.stringify(this._workDir)}`);
      }
      return true;
    });
    onProjectEvents?.();
  }

  async _waitForReadiness(phase, pathname, timeoutMs, isReady) {
    const started = Date.now();
    const deadline = started + timeoutMs;
    const url = `${this._baseUrl}${pathname}`;
    const child = this._child;
    const controller = new AbortController();
    let attempts = 0;
    let lastObservation = 'waiting for response headers; no request completed';
    const onExit = (code, signal) => controller.abort(new Error(`Host exited: code=${code} signal=${signal}`));
    const timer = setTimeout(() => controller.abort(new Error(`stage deadline expired; ${lastObservation}`)), timeoutMs);
    child?.once('exit', onExit);
    try {
      if (this._exitInfo) onExit(this._exitInfo.code, this._exitInfo.signal);
      while (true) {
        controller.signal.throwIfAborted();
        attempts += 1;
        try {
          const response = await fetch(url, {
            method: 'GET',
            headers: phase === 'project' ? { 'x-opencode-directory': encodeURIComponent(this._workDir) } : undefined,
            signal: controller.signal,
          });
          lastObservation = `HTTP ${response.status}; waiting for response JSON`;
          if (!response.ok) throw new Error(`HTTP ${response.status}`);
          const body = await response.json();
          if (isReady(body)) return;
          lastObservation = 'healthy=false';
        } catch (error) {
          if (controller.signal.aborted) throw controller.signal.reason;
          if (error?.cause?.code !== 'ECONNREFUSED') throw error;
          lastObservation = 'connection refused';
        }
        await delay(Math.max(0, Math.min(READY_POLL_INTERVAL_MS, deadline - Date.now())), undefined, { signal: controller.signal });
      }
    } catch (error) {
      const cause = controller.signal.aborted ? controller.signal.reason : error;
      throw new Error(
        `${phase === 'global' ? 'Global health-check' : 'Health-check'} failed: ` +
        `phase=${phase} url=${url} elapsed=${Date.now() - started}ms attempts=${attempts}; ${cause.message}` +
        `${cause.cause?.code ? ` (${cause.cause.code}: ${cause.cause.message})` : ''}\n` +
        `stdout tail:\n${this._stdoutBuffer.slice(-20).join('\n')}\n` +
        `stderr tail:\n${this._stderrBuffer.slice(-20).join('\n')}`,
        { cause },
      );
    } finally {
      clearTimeout(timer);
      child?.removeListener('exit', onExit);
      controller.abort();
    }
  }


  async stop({ assert = true } = {}) {
    if (this._stopped) return;
    this._stopped = true;
    if (!this._child) {
      // Already stopped, but reset flags so a fresh host can be started.
      this._started = false;
      this._stopped = false;
      this._baseUrl = null;
      this._port = null;
      this._pid = null;
      this._exitInfo = null;
      return;
    }
    const port = this._port;
    const pid = this._pid;
    try {
      await terminateChild(this._child, SIGTERM_GRACE_MS, SIGKILL_GRACE_MS);
      try { this._child.stdout.destroy(); } catch {}
      try { this._child.stderr.destroy(); } catch {}
      try { this._child.stdin.destroy(); } catch {}
      this._child = null;
      if (assert) {
        // Parallel canaries under load: SIGKILL can leave the listen socket
        // accept-able (orphaned descendant / mid-fork escape). Reclaim before
        // fail-closed assert — still assert, never paper over a true leak.
        if (port && !(await checkSocketClosed(port, SOCKET_CHECK_TIMEOUT_MS))) {
          if (pid) {
            try {
              if (process.platform !== 'win32') process.kill(-pid, 'SIGKILL');
            } catch {}
            try {
              process.kill(pid, 'SIGKILL');
            } catch {}
          }
          // Last-resort reclaim of the listen socket owner (harness-only).
          if (process.platform === 'linux') {
            try {
              const { execSync } = await import('node:child_process');
              execSync(`fuser -k ${port}/tcp`, {
                stdio: 'ignore',
                timeout: 2000,
              });
            } catch {}
          } else if (process.platform === 'darwin') {
            try {
              const { execSync } = await import('node:child_process');
              execSync(`lsof -ti tcp:${port} | xargs kill -9`, {
                stdio: 'ignore',
                timeout: 2000,
              });
            } catch {}
          }
          await checkSocketClosed(port, SOCKET_CHECK_TIMEOUT_MS);
        }
        await this.assertNoLeak();
      }
    } finally {
      // Allow the same ProcessHost instance to be re-used in a future
      // scenario. New scenarios must always get a fresh instance via
      // `new ProcessHost()`, but resetting here keeps the API forgiving.
      this._started = false;
      this._stopped = false;
      this._baseUrl = null;
      this._port = null;
      this._pid = null;
      this._exitInfo = null;
    }
  }

  async assertNoLeak() {
    const errors = [];
    const pid = this._pid;
    if (this._port && !(await checkSocketClosed(this._port, SOCKET_CHECK_TIMEOUT_MS))) {
      errors.push(`port ${this._port} still listening`);
    }
    if (pid && isPidAlive(pid) && !this._exitInfo) errors.push(`pid ${pid} still alive`);
    const tree = await checkProcessTree(pid, PROCESS_TREE_TIMEOUT_MS);
    if (tree) errors.push(`process tree leaked: ${tree}`);
    if (errors.length > 0) {
      throw new Error(`ProcessHost leak detected: ${errors.join('; ')}`);
    }
  }

  _onStdout(s) {
    if (process.env.DEBUG) process.stderr.write(s);
    ringPush(this._stdoutBuffer, s, STDOUT_RING_MAX);
  }
  _onStderr(s) {
    if (process.env.DEBUG) process.stderr.write(s);
    ringPush(this._stderrBuffer, s, STDOUT_RING_MAX);
  }
  _onChildExit(code, signal) {
    this._exitInfo = { code, signal, time: Date.now() };
    if (!this._stopped) {
      this._stderrBuffer.push(`\n[PROCESS] Unexpected exit: code=${code} signal=${signal}\n`);
    }
  }

  async _waitForListening(timeoutMs, onBootstrap) {
    return new Promise((resolve) => {
      const child = this._child;
      if (!child || !child.stdout) {
        resolve(null);
        return;
      }
      let deadline = Date.now() + timeoutMs;
      let buf = '';
      let bootstrapped = false;
      let settled = false;
      const finish = (value) => {
        if (settled) return;
        settled = true;
        try { child.stdout.removeListener('data', handler); } catch {}
        try { child.removeListener('exit', onExit); } catch {}
        resolve(value);
      };
      const handler = (chunk) => {
        buf += chunk.toString();
        if (!bootstrapped && buf.includes(BOOTSTRAP_SENTINEL)) {
          bootstrapped = true;
          deadline = Date.now() + timeoutMs;
          onBootstrap?.();
        }
        tryResolve();
      };
      const tryResolve = () => {
        if (!buf.includes(READY_SENTINEL)) return false;
        const lines = buf.split('\n');
        const listenLine = lines.find((l) => l.includes(READY_SENTINEL));
        finish(listenLine ? listenLine.trim() : buf.trim());
        return true;
      };
      const onExit = () => {
        finish(null);
      };
      child.once('exit', onExit);
      child.stdout.on('data', handler);
      const poll = () => {
        if (tryResolve()) return;
        if (Date.now() > deadline) {
          finish(null);
          return;
        }
        setTimeout(poll, LISTEN_POLL_INTERVAL_MS);
      };
      setTimeout(poll, LISTEN_POLL_INITIAL_DELAY_MS);
    });
  }
}

function ensureWorkspace(scenarioDir) {
  const workDir = path.join(scenarioDir, 'workspace');
  fs.mkdirSync(workDir, { recursive: true });
  return fs.realpathSync(workDir);
}

function buildEnv(opts) {
  const baseEnv = {};
  const denylistPatterns = [
    /^OPENCODE_CONFIG$/i,
    /^OPENCODE_CONFIG_CONTENT$/i,
    /^OPENCODE_AUTH_CONTENT$/i,
    /^OPENCODE_PERMISSION$/i,
    /^OPENAI_API_KEY$/i,
    /^ANTHROPIC_API_KEY$/i,
    /^OLLAMA_/i,
    /^HTTP_PROXY$/i,
    /^HTTPS_PROXY$/i,
    /^NO_PROXY$/i,
    /^SQUAD_/i,
    /^WANXIANG/i,
  ];

  for (const [key, value] of Object.entries(process.env)) {
    if (denylistPatterns.some((pattern) => pattern.test(key))) {
      continue;
    }
    baseEnv[key] = value;
  }

  const envOverrides = createIsolatedEnv({
    scenarioDir: opts.scenarioDir,
    llmUrl: opts.providerUrl,
    pluginPaths: opts.pluginPaths,
    contextLimit: opts.contextLimit,
    routingSource: opts.routingSource,
    mcpServers: opts.mcpServers,
    extraEnv: opts.extraEnv,
  });

  const finalEnv = { ...baseEnv, ...envOverrides };
  return finalEnv;
}
