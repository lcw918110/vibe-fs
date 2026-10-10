/**
 * strict-mock-provider.js — Strict mock LLM server for OpenCode E2E.
 *
 * Driven exclusively by a compiled TOML scenario (`ScenarioRuntime`): every
 * request must hit a declared turn. On mismatch the request is recorded as
 * unexpected, the first mismatch is fatal, and a 500 is returned.
 *
 * Server lifecycle / web endpoints live in strict-mock-server.js; SSE chunks in
 * strict-mock-sse.js; request-body inspection (diagnostics + request-kind
 * classification) in strict-mock-matches.js; state record in
 * strict-mock-state.js.
 */

import { sendJSON } from './strict-mock-sse.js';
import {
  extractToolNames,
  extractLastUserMsg,
} from './strict-mock-matches.js';
import {
  startHttpServer,
  stopHttpServer,
  handleWebSearch,
  handleWebFetch,
  readRequestBody,
} from './strict-mock-server.js';
import { faultBody } from './delivery-plan.js';
import { wireOf } from './provider-wire.js';
import { createHash } from 'node:crypto';
import { StrictMockSignals } from './strict-mock-signals.js';
import { respond } from './strict-mock-responses.js';
import {
  createState,
  resetState,
} from './strict-mock-state.js';

const headerValue = (headers, names) => {
  for (const name of names) {
    const value = headers[name];
    if (typeof value === 'string' && value !== '') return value;
  }
  return null;
};

const requestContextOf = (headers) => ({
  sessionId: headerValue(headers, ['x-session-affinity', 'x-session-id', 'x-opencode-session']),
  parentSessionId: headerValue(headers, ['x-parent-session-id']),
});

const requestRecordOf = (body, context) => {
  const record = { ...body };
  Object.defineProperties(record, {
    sessionID: { value: context.sessionId, enumerable: false },
    parentSessionID: { value: context.parentSessionId, enumerable: false },
  });
  return record;
};

export class StrictMockProvider {
  constructor(options = {}) {
    this._quiet = Boolean(options.quiet);
    this._state = createState();
    this._server = null;
    this._port = null;
    this._url = null;
    this._signals = new StrictMockSignals();
    this._afterExpectation = new Map();
    this._expectationObservations = new Map();
    /** @type {import('./scenario-runtime.js').ScenarioRuntime | null} */
    this._scenario = null;
    this.onRequest = null;
    this.onExpectationConsumed = null;
    /** First unmatched script: canary must stop. Scenario wires process.exit. */
    this.onFatal = null;
  }

  /**
   * Drive this provider from a compiled TOML scenario.
   *
   * There is exactly one matching path: `ScenarioRuntime`. An attached runtime is
   * the provider's whole script; `_dispatchChat` refuses to answer without one.
   */
  attachScenario(runtime) {
    this._scenario = runtime;
  }

  expectSatisfied() {
    const errors = [];
    if (this._state.fatal) {
      const f = this._state.fatal;
      errors.push(`FIRST SCRIPT MISMATCH (mock stopped): reason=${f.reason} session=${f.sessionId} lastUser=${JSON.stringify(f.lastUser)} candidates=${JSON.stringify(f.candidates)}`);
    }

    if (this._scenario === null) {
      // No scenario attached: only fatal / unexpected state can be asserted.
      if (this._state.unexpected.length === 0 && !this._state.fatal) {
        errors.push('no scenario attached; nothing was declared');
      }
    } else {
      // A declared step no request reached. `internal` turns are exempt (production
      // decides whether to compose them at all) — a scenario that needs one says `must`.
      const unanswered = this._scenario.unanswered();
      if (unanswered.length > 0) {
        errors.push(`declared but never reached = ${unanswered.length}:\n${unanswered.slice(0, 5).map((e) => `  [${e.id}] lane=${e.lane ?? '(any)'} step=${e.step} respond=${e.respond?.type}`).join('\n')}`);
      }

      const unmet = this._scenario.unmetMust();
      if (unmet.length > 0) errors.push(`must not satisfied: ${unmet.join(', ')}`);

      const unfired = this._scenario.unfiredBoundaries();
      if (unfired.length > 0) {
        errors.push(
          `cold boundary never fired: ${unfired.map((b) => `${b.entryId} (${b.kind})`).join(', ')} — the declaration admits a break the run never produced`,
        );
      }
    }

    if (this._state.unexpected.length > 0) {
      errors.push(`unexpected requests = ${this._state.unexpected.length}: ${JSON.stringify(this._state.unexpected.slice(0, 3).map((u) => u.reason))}`);
    }

    if (errors.length > 0) throw new Error(`Scenario assertions failed:\n${errors.join('\n')}`);
  }
  reset() {
    resetState(this._state);
    this._afterExpectation.clear();
    this._expectationObservations.clear();
  }
  /**
   * Associate a scenario alias with a real session id (HOST-008).
   *
   * The scenario is told FIRST: a scenario alias names a ROLE, and production
   * forks as many children of a role as the work needs — `orchestrator-publish`
   * reviews before and after the rebase, so `fast-reviewer` legitimately holds
   * two sessions. Rebinding a role alias is therefore not an error.
   */
  bindSession(alias, sessionID) {
    if (typeof alias !== 'string' || alias.trim() === '') throw new Error('StrictMock session alias must be non-empty');
    if (typeof sessionID !== 'string' || sessionID.trim() === '') throw new Error('StrictMock session ID must be non-empty');

    this._scenario?.bindAlias(alias, sessionID);
    this._state.sessionBindings.set(alias, sessionID);
  }

  /** Host restart / new process: old provider-visible seals are not comparable. */
  clearPrefixSeals() {
    this._state.sealedBySession.clear();
    this._scenario?.clearSeals();
  }
  /// Read-only: which session (if any) currently owns an alias. Lets a scenario
  /// route a second session to a distinct alias instead of throwing on rebind.
  sessionFor(alias) {
    return this._state.sessionBindings.get(alias) || null;
  }

  // verification-system-006: no competing absolute timeout on flow waits. When timeoutMs is
  // omitted, wait until the expectation matches; the scenario Watchdog owns
  // silence detection. Do not default to WATCHDOG_TIMEOUT_MS here — that turned
  // every unbounded `wait = "..."` into a 5s wall clock under parallel load.
  waitForExpectation(id, timeoutMs) {
    return this._signals.waitForExpectation(id, timeoutMs);
  }
  waitForAnyExpectation(ids, timeoutMs) {
    return this._signals.waitForAnyExpectation(ids, timeoutMs);
  }
  waitForExpectationAttempt(id, attempts, timeoutMs) {
    return this._signals.waitForExpectationAttempt(id, attempts, timeoutMs);
  }
  waitForIdle(timeoutMs) { return this._signals.waitForIdle(timeoutMs); }
  matchCount(id, sessionId) { return this._signals.matchCount(id, sessionId); }
  afterExpectation(id, callback, attempts = 1) {
    if (!Number.isInteger(attempts) || attempts < 1) {
      throw new Error(`afterExpectation attempts must be a positive integer: ${attempts}`);
    }
    if (this._signals.matchCount(id) >= attempts) {
      const observed = this._expectationObservations.get(id)?.get(attempts);
      return callback(observed ?? { id, attempt: attempts, sessionId: null, parentSessionId: null });
    }
    const callbacks = this._afterExpectation.get(id) || [];
    callbacks.push({ attempts, callback });
    this._afterExpectation.set(id, callbacks);
  }

  get requests() { return this._state.requests; }
  get url() { return this._url; }
  get port() { return this._port; }
  get unexpectedRequests() { return this._state.unexpected; }
  get remainingExpectations() { return this.blockedExpectations.length; }
  get blockedExpectations() {
    if (this._scenario === null) return [];
    return this._scenario.unanswered().map((entry) => ({
      id: entry.id,
      lane: `${entry.id}@${entry.lane ?? '(any)'}/${entry.kind}/step-${entry.step}`,
      blocking: entry.internal !== true || entry.turnId === 'blogger',
    }));
  }
  get activeRequestCount() { return this._signals.activeRequestCount; }

  get strict() { return this._state.strict; }
  set strict(v) { this._state.strict = !!v; }

  async start() {
    if (this._server) return this._url;
    const { server, port, url } = await startHttpServer((req, res) => this._handleRequest(req, res));
    this._server = server;
    this._port = port;
    this._url = url;
    return this._url;
  }

  stopMocking() {
    this._state.stopped = true;
    this._signals.stop();
    this._afterExpectation.clear();
    this._expectationObservations.clear();
  }

  async stop() {
    this.stopMocking();
    await stopHttpServer(this._server);
    this._server = null;
    this._port = null;
    this._url = null;
  }

  _handleRequest(req, res) {
    if (this._state.stopped) {
      sendJSON(res, 503, { error: 'mocking stopped' });
      return;
    }
    this._signals.trackResponse(res);

    const url = new URL(req.url, `http://${req.headers.host}`);
    if (!this._quiet) {
      console.error(`[mock-req] ${req.method} ${url.pathname}`);
    }
    if ((url.pathname === '/v1/models' || url.pathname === '/models' || url.pathname === '/api/models') && req.method === 'GET') {
      return sendJSON(res, 200, { object: 'list', data: [{ id: 'test-model', object: 'model' }] });
    }
    if (url.pathname === '/api/web_search' && req.method === 'POST') return handleWebSearch(req, res);
    if (url.pathname === '/api/web_fetch' && req.method === 'POST') return handleWebFetch(req, res);
    if ((url.pathname === '/v1/chat/completions' || url.pathname === '/v1/responses') && req.method === 'POST') {
      return this._handleChat(req, res);
    }
    sendJSON(res, 404, { error: 'not found' });
  }

  _handleChat(req, res) {
    readRequestBody(req).then((parsed) => {
      this._dispatchChat(res, parsed, requestContextOf(req.headers));
    })
      .catch((error) => {
        // Never mask a dispatch/hook failure as a body-parse failure: the
        // diagnostic must name the real cause (alias binding, selector, etc.).
        const badBody = error instanceof SyntaxError;
        console.error(`[MOCK-DISPATCH-ERROR] ${badBody ? 'bad json' : 'dispatch'}: ${error?.stack || error}`);
        sendJSON(res, 400, { error: badBody ? 'bad json' : `dispatch failed: ${error?.message || error}` });
      });
  }

  /**
   * One request against the compiled scenario.
   *
   * The seal, the fault plan and the content lookup all live in `ScenarioRuntime`; this
   * method only turns its verdict into HTTP. Nothing here inspects the request — that
   * would be a second matcher, which is what K1-K8 spent their whole effort removing.
   */
  _dispatchScenario(res, parsed, context) {
    const selection = this._scenario.select(parsed, context);
    if (selection.unmatched !== undefined) {
      const key = selection.unmatched.key;
      return this._recordUnexpected(
        res,
        parsed,
        'no-declared-turn',
        selection.unmatched.candidates.map((entry) => ({ expectation: entry })),
        `lane=${key.lane ?? '(unbound)'} kind=${key.kind} step=${key.step}`,
        context,
      );
    }
    if (selection.ambiguous !== undefined) {
      return this._recordUnexpected(
        res,
        parsed,
        'ambiguous-turn',
        selection.ambiguous.entries.map((entry) => ({ expectation: entry })),
        '',
        context,
      );
    }
    if (selection.sealBroken !== undefined) {
      // 诊断：打印 previous 与 next wire 的逐条哈希
      try {
        const sessionId = context?.sessionId;
        const prev = this._scenario?.seals?.get(sessionId);
        const next = wireOf(parsed);
        const h = (s) => createHash('sha256').update(s).digest('hex').slice(0, 12);
        const fmt = (w) => (w ? w.messages.map((m) => `${m.role}:${h(JSON.stringify(m.parts))}`).join('|') : 'null');
        const prevW = prev ? prev.messages : [];
        const nextW = next ? next.messages : [];
        const minLen = Math.min(prevW.length, nextW.length);
        const diffs = [];
        for (let i = 0; i < minLen; i += 1) {
          const a = h(JSON.stringify(prevW[i].parts));
          const b = h(JSON.stringify(nextW[i].parts));
          if (a !== b) diffs.push(`msg[${i}] ${prevW[i].role}:${a} != ${nextW[i].role}:${b}`);
        }
        if (prevW.length !== nextW.length) diffs.push(`len ${prevW.length} != ${nextW.length}`);
        // system 内容差异定位
        const prevSys = Array.from(prevW[0]?.parts ?? [])[0]?.text ?? '';
        const nextSys = Array.from(nextW[0]?.parts ?? [])[0]?.text ?? '';
        if (typeof prevSys === 'string' && typeof nextSys === 'string' && prevSys !== nextSys) {
          let at = -1;
          const max = Math.min(prevSys.length, nextSys.length);
          for (let i = 0; i < max; i += 1) {
            if (prevSys[i] !== nextSys[i]) { at = i; break; }
          }
          if (!this._quiet) {
            console.error(`[SEAL-SYS] firstDiffAt=${at} prevLen=${prevSys.length} nextLen=${nextSys.length} prevHead=${prevSys.slice(Math.max(0, (at < 0 ? 0 : at) - 60), at < 0 ? 120 : at + 60).replace(/\n/g, '\\n')}`);
            console.error(`[SEAL-SYS] nextHead=${nextSys.slice(Math.max(0, (at < 0 ? 0 : at) - 60), at < 0 ? 120 : at + 60).replace(/\n/g, '\\n')}`);
            console.error(`[SEAL-SYS] prevTail=${prevSys.slice(-160).replace(/\n/g, '\\n')}`);
            console.error(`[SEAL-SYS] nextTail=${nextSys.slice(-160).replace(/\n/g, '\\n')}`);
          }
        } else if (!this._quiet) {
          console.error(
            `[SEAL-SYS] equal-content prevLen=${prevSys.length} nextLen=${nextSys.length} `
            + `prevParts=${prevW[0]?.parts?.length ?? 0} nextParts=${nextW[0]?.parts?.length ?? 0}`,
          );
        }
        if (!this._quiet) {
          console.error(`[SEAL-DIAG] session=${sessionId} reason=${selection.sealBroken.reason} prev=[${fmt(prev)}] next=[${fmt(next)}]`);
          console.error(`[SEAL-DIFF] ${diffs.join(' || ') || '(no message diff — tools/model differ)'}`);
        }
      } catch (diagErr) {
        console.error(`[SEAL-DIAG-ERR] ${diagErr?.message}`);
      }
      return this._recordUnexpected(res, parsed, `seal-${selection.sealBroken.reason}`, [], '', context);
    }

    this._scenario.consume(parsed, selection, context);
    this._state.requests.push(requestRecordOf(parsed, context));

    // A refused delivery still matched an entry: wake afterExpectation / matchCount so
    // crash-window canaries (X-A) can kill on the faulted attempt. Content waiters that
    // need a real response must wait on a non-faulted edge (fallback waits on continue).
    // `never-end` is different: its content arrives below, but the SSE transport
    // deliberately withholds its terminal marker.
    const neverEnds = selection.fault?.kind === 'never-end';
    if (selection.fault !== undefined && !neverEnds) {
      const fault = selection.fault;
      const faulted = selection.entry;
      if (faulted) {
        for (const id of new Set([faulted.id, faulted.turnId])) {
          this._signals.consume({ id, permanent: true, sessionId: context?.sessionId });
          this._runAfterExpectation(id, context);
        }
      }
      if (fault.kind === 'disconnect') {
        res.writeHead(200, { 'Content-Type': 'text/event-stream', 'Cache-Control': 'no-cache' });
        return res.destroy();
      }
      return sendJSON(res, fault.status ?? 500, faultBody(fault));
    }

    const entry = selection.entry;
    // Both the step id and its turn id, so a flow may wait on either granularity —
    // `wait = "mgr"` for "the turn happened" and `wait = "mgr.1"` for a specific step.
    for (const id of new Set([entry.id, entry.turnId])) {
      this._signals.consume({ id, permanent: true, sessionId: context?.sessionId });
      this._runAfterExpectation(id, context);
    }

    if (process.env.MOCK_TRACE) {
      console.error(`[MOCK-TRACE] -> ${entry.id} session=${context.sessionId ?? '(none)'} lane=${entry.lane ?? '(any)'} step=${entry.step} attempt=${selection.attempt}`);
    }

    this.onExpectationConsumed?.({
      id: entry.id,
      lane: { scenario: this._scenario.scenario.name, session: entry.lane, role: entry.turnId, requestKind: entry.kind },
      blocking: entry.internal !== true || entry.turnId === 'blogger',
      requestKind: entry.kind,
    });

    const responseEntry = neverEnds
      ? { ...entry, respond: { ...entry.respond, neverEnd: true } }
      : entry;
    return respond(this._state, res, responseEntry, parsed);
  }

  _dispatchChat(res, parsed, context) {
    this.onRequest?.(parsed);
    const s = this._state;
    // First script mismatch already happened: do not attempt further matches.
    if (s.fatal) {
      sendJSON(res, 503, {
        error: 'mock stopped after first script mismatch',
        reason: s.fatal.reason,
        id: s.fatal.id,
      });
      return;
    }
    if (process.env.MOCK_TRACE) {
      const tools = (parsed.tools || []).map((t) => t?.function?.name || t?.name || '?');
      const lastUser = JSON.stringify(extractLastUserMsg(parsed) || '').slice(0, 80);
      console.error(`[MOCK-TRACE] tools=${JSON.stringify(tools)} msgs=${(parsed.messages || []).length} chars=${JSON.stringify(parsed.messages || []).length} lastUser=${lastUser}`);
    }

    if (this._scenario !== null) return this._dispatchScenario(res, parsed, context);

    // No scenario attached: every chat request is unexpected (ARCH-004). A strict
    // mock must be told what the model may say; an empty script answers nothing.
    this._recordUnexpected(res, parsed, 'no-scenario-attached', [], '', context);
  }

  _recordUnexpected(res, parsed, reason, candidates = [], detail = '', context = {}) {
    const sessId = context.sessionId || '(no-session-id)';
    const parentSessionId = context.parentSessionId || null;
    const msgs = parsed?.messages || [];
    const hasToolResults = msgs.some((m) => m?.role === 'tool' || m?.role === 'toolResult');
    // A scenario entry carries `lane` as a plain alias string. The diagnostic labels
    // it directly; there is no legacy lane record any more.
    const candidateLabels = candidates.map(({ expectation }) =>
      `${expectation.id}@${expectation.lane}/${expectation.kind}/step-${expectation.step}`,
    );
    const lastUser = extractLastUserMsg(parsed);
    const fatal = {
      id: `unexpected-${this._state.unexpected.length + 1}`,
      reason: detail === '' ? reason : `${reason} (${detail})`,
      sessionId: sessId,
      parentSessionId,
      lastUser: typeof lastUser === 'string' ? lastUser.slice(0, 400) : JSON.stringify(lastUser).slice(0, 400),
      candidates: candidateLabels,
    };
    // First script mismatch: record once, reject all waiters, notify the test to stop.
    const isFirst = !this._state.fatal;
    this._state.fatal = this._state.fatal || fatal;
    this._state.unexpected.push({
      body: parsed,
      sessId,
      parentSessionId,
      hasToolResults,
      reason,
      candidates: candidateLabels,
    });
    if (!this._quiet) {
      console.error(`[MOCK-FATAL] first script mismatch: reason=${reason} session=${sessId} parent=${parentSessionId || '-'} model=${JSON.stringify(parsed.model)} tools=${JSON.stringify(extractToolNames(parsed))} msgs=${msgs.length} lastUser=${JSON.stringify(fatal.lastUser)} candidates=${JSON.stringify(candidateLabels)}`);
      try {
        const roles = msgs.map((m) => `${m.role}`).join(' ');
        console.error(`[MSG-ROLES] ${roles}`);
        const recentToolResults = msgs
          .filter((message) => message?.role === 'tool' || message?.role === 'toolResult')
          .slice(-4)
          .map((message) => String(message?.content ?? message?.text ?? '').slice(0, 300));
        console.error(`[MSG-TOOL-RESULTS] ${JSON.stringify(recentToolResults)}`);
      } catch {}
    }
    if (isFirst) {
      const err = new Error(
        `FIRST SCRIPT MISMATCH: ${reason} session=${sessId} lastUser=${JSON.stringify(fatal.lastUser)} candidates=${JSON.stringify(candidateLabels)}`,
      );
      err.fatal = fatal;
      this._signals.fail(err);
      try { this.onFatal?.(fatal, err); } catch (hookErr) {
        console.error(`[MOCK-FATAL] onFatal threw: ${hookErr?.stack || hookErr}`);
      }
    }
    // Clean 500 for this request. Later chats see state.fatal → 503.
    return sendJSON(res, 500, {
      error: 'first-script-mismatch',
      reason,
      sessionId: sessId,
      tools: extractToolNames(parsed),
      candidates: candidateLabels,
    });
  }

  _runAfterExpectation(id, context = {}) {
    const matchCount = this._signals.matchCount(id);
    const observation = {
      id,
      attempt: matchCount,
      sessionId: context.sessionId ?? null,
      parentSessionId: context.parentSessionId ?? null,
    };
    const byAttempt = this._expectationObservations.get(id) || new Map();
    byAttempt.set(matchCount, observation);
    this._expectationObservations.set(id, byAttempt);

    const callbacks = this._afterExpectation.get(id);
    if (!callbacks) return;

    const waiting = callbacks.filter((hook) => hook.attempts > matchCount);
    if (waiting.length === 0) this._afterExpectation.delete(id);
    else this._afterExpectation.set(id, waiting);

    for (const hook of callbacks) {
      if (hook.attempts <= matchCount) hook.callback(observation);
    }
  }
}

export { extractToolNames };
