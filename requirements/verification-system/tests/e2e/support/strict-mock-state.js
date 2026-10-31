/**
 * strict-mock-state.js — Provider state record.
 *
 * K9 deleted the legacy edge forest (`pushExpectation`, template dedupe, path
 * cursors). What remains is the request/response bookkeeping plus the alias
 * binding map a scenario uses for session routing diagnostics.
 */

export function createState() {
  return {
    sessionBindings: new Map(), // diagnostic / canary routing only
    unexpected: [],
    /** Requests the plugin stopped before it said anything (see StrictMockProvider). */
    infrastructure: [],
    requests: [],
    responseCounter: 0,
    idCounter: 0,
    strict: true,
    fatal: null,
    sealedBySession: new Map(),
    stopped: false,
    /**
     * Which sessions already paid their first `resetScope`. The mock plays the
     * model, and a model cannot open a second Scope lifetime it never saw end;
     * keying it per session is what keeps two lanes' annotations independent.
     */
    retention: { sessionId: '', opened: new Set() },
  };
}

export function resetState(state) {
  state.sessionBindings.clear();
  state.unexpected.length = 0;
  state.infrastructure.length = 0;
  state.requests.length = 0;
  state.responseCounter = 0;
  state.stopped = false;
  state.fatal = null;
  state.sealedBySession.clear();
  state.retention.sessionId = '';
  state.retention.opened.clear();
}
