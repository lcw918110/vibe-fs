import path from 'node:path';
import fs from 'node:fs';
import { fileURLToPath } from 'node:url';
import { ProcessHost } from './process-host.js';
import { EventProbe } from './event-probe.js';
import { FsOracle, HttpClient } from './scenario-http.js';
import { initGitWorkspace } from './process-host-utils.js';
import { resolvePluginPath } from './scenario-paths.js';
import { ReceiptAcceptanceGate, receiptAcceptanceGatePluginPath } from './receipt-acceptance-gate.mjs';
import { StrictMockProvider } from './strict-mock-provider.js';
import { createScenarioTurn } from './scenario-turn.js';
import { Watchdog } from './watchdog.js';
import { journalFactTail, readJournal, watchJournal } from './journal-observer.js';
import { createNodeDelayPort } from './delay-port.mjs';
import { WATCHDOG_TIMEOUT_MS, DIAGNOSTIC_RACE_MS } from './time-budget.js';
import { collectCausalWaits, formatCausalSection } from './diagnostics-causal.js';

const JOURNAL_WAKE_GUARD_MS = 50;
const delayPort = createNodeDelayPort();

/**
 * MCP fixtures available to e2e scenarios. A scenario declares a fixture by
 * name (`setup.mcpFixture = "semble"`); the harness owns the name -> command
 * mapping so the scenario file stays machine-independent and no e2e config
 * hard-codes an absolute path. An unknown name fails closed at setup: a
 * silently unmatched fixture would leave the world without MCP tools while
 * every other assertion stayed green.
 */
const MCP_FIXTURES = {
  semble: () => [
    process.execPath,
    path.join(
      path.dirname(fileURLToPath(import.meta.url)),
      '..', '..', 'support', 'semble-mcp-fixture.js',
    ),
  ],
};

const MCP_SERVER_KEY = 'wanxiang_fixture';

function resolveMcpServers(mcpFixture) {
  if (mcpFixture === undefined || mcpFixture === null || mcpFixture === false) return undefined;
  if (typeof mcpFixture !== 'string' || mcpFixture.trim() === '') {
    throw new Error(`mcpFixture must be a non-empty fixture name; got ${JSON.stringify(mcpFixture)}`);
  }
  const build = MCP_FIXTURES[mcpFixture];
  if (typeof build !== 'function') {
    throw new Error(`unknown mcp fixture '${mcpFixture}'; declared: ${Object.keys(MCP_FIXTURES).join(', ')}`);
  }
  return { [MCP_SERVER_KEY]: { type: 'local', command: build(), enabled: true } };
}

async function observeRestartJournal(workDir, watchdog, state) {
  let observed = readJournal(workDir).total;
  while (!state.done) {
    await new Promise((resolve) => {
      let settled = false;
      const done = () => {
        if (settled) return;
        settled = true;
        stop();
        resolve();
      };
      const stop = watchJournal(workDir, done);
      delayPort.delay(JOURNAL_WAKE_GUARD_MS).then(done);
    });
    if (state.done) break;
    const next = readJournal(workDir).total;
    if (next > observed) {
      watchdog?.advance({
        reason: `restart-journal:${next}`,
        lane: 'runtime',
        blocking: true,
      });
      observed = next;
    }
  }
}

export class Scenario {
  constructor(ctx) {
    this.host = ctx.host;
    this.provider = ctx.provider;
    this.events = ctx.events;
    this.client = ctx.client;
    this.fs = ctx.fs;
    this.scenarioDir = ctx.scenarioDir;
    this.acceptanceGate = ctx.acceptanceGate ?? null;
    this.sessionIds = [];
    this.sessionCreatedDiagnostics = [];
    this.turn = createScenarioTurn(this);
    this.watchdog = null;
    this._tornDown = false;
  }

  async restart() {
    this.watchdog?.advance({ reason: 'restart-stop-host', lane: 'runtime', blocking: true });
    await this.host.stop({ assert: true });
    this.watchdog?.advance({ reason: 'restart-close-events', lane: 'runtime', blocking: true });
    await this.events.close();
    // A Host restart cannot continue an old physical turn cursor. New work after
    // restart must explicitly start a new turn; retaining these cursors would let
    // the harness "recover" an interrupted invocation that production forbids.
    this.turn.clear();
    this.watchdog?.advance({ reason: 'restart-start-host', lane: 'runtime', blocking: true });
    const journalMonitorState = { done: false };
    const journalMonitor = observeRestartJournal(this.host.workDir, this.watchdog, journalMonitorState);
    try {
      await this.host.start({
        ...this.host._startOpts,
        onProgress: (stage) => this.watchdog?.advance({
          reason: `restart-host-${stage}`,
          lane: 'runtime',
          blocking: true,
        }),
      });
    } finally {
      journalMonitorState.done = true;
      await journalMonitor;
    }
    this.client._baseUrl = this.host.baseUrl;
    this.events._baseUrl = this.host.baseUrl;
    // New host process: prior provider-visible seals are not comparable across restart.
    this.provider?.clearPrefixSeals?.();
    this.watchdog?.advance({ reason: 'restart-connect-events', lane: 'runtime', blocking: true });
    await this.events.connect();
    this.watchdog?.advance({ reason: 'restart-complete', lane: 'runtime', blocking: true });
  }
}

function configureProvider(provider, opts) {
  if (opts.strict !== undefined) provider.strict = !!opts.strict;
  return provider;
}

function ensureWorkspace(scenarioDir) {
  const workDir = path.join(scenarioDir, 'workspace');
  fs.mkdirSync(workDir, { recursive: true });
  return workDir;
}

async function prepareWorkspace(workDir, project) {
  if (project) {
    for (const [relPath, content] of Object.entries(project.files || {})) {
      const absPath = path.join(workDir, relPath);
      fs.mkdirSync(path.dirname(absPath), { recursive: true });
      fs.writeFileSync(absPath, content);
    }
  }
  await initGitWorkspace(workDir);
}

export async function setupScenarioParallel(opts, tmpDir) {
  const scenarioDir = tmpDir();
  const workDir = path.join(scenarioDir, 'workspace');
  fs.mkdirSync(workDir, { recursive: true });

  const provider = configureProvider(new StrictMockProvider(), opts);
  const host = new ProcessHost();
  const productionPluginPath = opts.plugin !== false ? resolvePluginPath(opts.variant || 'opencode') : null;
  const acceptanceGateDirectory = opts.acceptanceGate ? path.join(scenarioDir, 'receipt-acceptance-gate') : null;
  if (acceptanceGateDirectory) fs.mkdirSync(acceptanceGateDirectory, { recursive: true });
  const acceptanceGate = acceptanceGateDirectory ? new ReceiptAcceptanceGate(acceptanceGateDirectory) : null;
  const pluginPaths = productionPluginPath === null
    ? []
    : [acceptanceGateDirectory ? receiptAcceptanceGatePluginPath : productionPluginPath];

  try {
    const t0 = Date.now();
    const providerUrl = await provider.start();
    const t1 = Date.now();
    if (process.env.CANARY_VERBOSE || process.env.DEBUG) {
      console.log(`[setupScenario] provider.start took ${t1 - t0}ms`);
    }
    // Prepare workspace before starting opencode; it reads AGENTS.md at startup.
    await prepareWorkspace(workDir, opts.project);
    const t2 = Date.now();
    if (process.env.CANARY_VERBOSE || process.env.DEBUG) {
      console.log(`[setupScenario] prepareWorkspace took ${t2 - t1}ms`);
    }
    await host.start({
      scenarioDir,
      providerUrl: `${providerUrl}/v1`,
      pluginPaths,
      contextLimit: opts.contextLimit,
      routingSource: opts.routingSource,
      mcpServers: resolveMcpServers(opts.mcpFixture),
      extraEnv: {
        // The Host env denylist drops `WANXIANG*` so plugin configuration is always explicit
        // rather than inherited by accident. That also drops the diagnostics flag, so it is
        // forwarded here on purpose: `WANXIANGSHU_DIAG=1 node tests/e2e/cases/x.test.mjs` then
        // surfaces the plugin's own records in the Host stderr tail the watchdog already dumps.
        ...(process.env.WANXIANGSHU_DIAG === '1' ? { WANXIANGSHU_DIAG: '1' } : {}),
        ...(acceptanceGateDirectory ? {
          WANXIANGSHU_E2E_ACCEPTANCE_GATE_DIR: acceptanceGateDirectory,
          WANXIANGSHU_E2E_ACCEPTANCE_GATE_PLUGIN: productionPluginPath,
          WANXIANGSHU_E2E_ACCEPTANCE_GATE_CONFIG: JSON.stringify(opts.acceptanceGate),
        } : {}),        ...(opts.extraEnv || {}),
      },
    });
    const t3 = Date.now();
    if (process.env.CANARY_VERBOSE || process.env.DEBUG) {
      console.log(`[setupScenario] host.start took ${t3 - t2}ms`);
    }

    const client = new HttpClient(host.baseUrl, host.workDir);
    const events = new EventProbe(host.baseUrl, host.workDir);
    await events.connect();
    const t4 = Date.now();
    if (process.env.CANARY_VERBOSE || process.env.DEBUG) {
      console.log(`[setupScenario] events.connect took ${t4 - t3}ms`);
    }
    console.log('[setupScenario] ready');

    const scenario = new Scenario({
      host,
      provider,
      events,
      client,
      fs: new FsOracle(host.workDir),
      scenarioDir,
      acceptanceGate,
    });
    events.onEvent((e) => {
      if (e.type === 'session.created' && e.sessionAgent && e.sessionID) {
        try { provider.bindSession(e.sessionAgent, e.sessionID); } catch {}
      }
    });
    client.onSessionCreated = (sid) => {
      if (!scenario.sessionIds.includes(sid)) scenario.sessionIds.push(sid);
      scenario.sessionCreatedDiagnostics.push({
        sessionID: sid,
        observedAt: Date.now(),
        causal: false,
      });
    };

    const watchdogTimeout = opts.watchdogMs || WATCHDOG_TIMEOUT_MS;
    const watchdog = new Watchdog({
      timeoutMs: watchdogTimeout,
      label: opts.watchdogLabel || "canary",
      onTimeout: async () => {
        try {
          const diag = {};
          collectCausalWaits(diag, scenario);
          const lines = formatCausalSection(diag);
          if (lines.length) console.error(lines.join('\n'));
          else console.error('════════════ CAUSAL FRONTIER ════════════\n(no causal-waits.json)');
        } catch (e) {
          console.error(`CAUSAL FRONTIER unavailable: ${e.message}`);
        }
        console.error(`── watchdog event tail ──\n${events.dump(20)}`);
        console.error(`── watchdog Host stdout tail ──\n${host.stdoutLog.split('\n').slice(-20).join('\n')}`);
        console.error(`── watchdog Host stderr tail ──\n${host.stderrLog.split('\n').slice(-20).join('\n')}`);
        console.error(`── watchdog Journal fact tail ──\n${journalFactTail(host.workDir, 20).join('\n')}`);
        console.error('── watchdog blocked expectations ──');
        for (const expectation of provider.blockedExpectations) {
          console.error(`  ${expectation.blocking ? 'blocking' : 'background'} ${expectation.id} ${expectation.lane}`);
        }
        try {
          await Promise.race([
            host.stop({ assert: false }),
            new Promise((r) => setTimeout(r, DIAGNOSTIC_RACE_MS)),
          ]);
        } catch (e) {
          console.error(`[Watchdog Cleanup Error] host.stop failed: ${e.message}`);
        }
        try { await provider.stop(); } catch {}
      },
    });
    scenario.watchdog = watchdog;
    // First mock script mismatch: stop the entire canary process (not just 500).
    provider.onFatal = (fatal) => {
      try {
        console.error(`── fatal script mismatch: ${fatal.reason} ──`);
        console.error(`session=${fatal.sessionId} lastUser=${JSON.stringify(fatal.lastUser)}`);
        console.error(`candidates=${JSON.stringify(fatal.candidates)}`);
        console.error(`── event tail ──\n${events.dump(20)}`);
        console.error(`── Host stderr tail ──\n${host.stderrLog.split('\n').slice(-30).join('\n')}`);
        console.error(`── Journal fact tail ──\n${journalFactTail(host.workDir, 30).join('\n')}`);
        for (const expectation of provider.blockedExpectations) {
          console.error(`  pending ${expectation.blocking ? 'blocking' : 'background'} ${expectation.id} ${expectation.lane}`);
        }
      } catch {}
      try { watchdog.stop(); } catch {}
      process.exit(1);
    };
    provider.onExpectationConsumed = ({ id, lane, blocking }) => {
      watchdog.advance({
        reason: `expectation:${id}`,
        lane: `${lane.scenario}/${lane.session}/${lane.role}/turn-${lane.turn}`,
        expectationId: id,
        blocking,
      });
    };
    return scenario;
  } catch (err) {
    if (host.stdoutLog || host.stderrLog) {
      console.error('--- Setup Scenario Host Logs ---');
      if (host.stdoutLog) console.error(`stdout:\n${host.stdoutLog}`);
      if (host.stderrLog) console.error(`stderr:\n${host.stderrLog}`);
    }
    try { await host.stop({ assert: false }); } catch {}
    try { await provider.stop(); } catch {}
    throw err;
  }
}
