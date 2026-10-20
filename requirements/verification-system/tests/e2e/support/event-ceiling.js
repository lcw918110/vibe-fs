/**
 * event-ceiling.js — exact event-count upper bounds that fail closed on dead loops.
 *
 * Wall-clock silence (watchdog) is a 兜底. A join-guard/XTrace storm can keep
 * appending durable facts and SSE frames forever while never producing the awaited
 * causal fact — and a widened waitFact window (60s+) will sit through that storm.
 *
 * G4R / verification-system-010: the primary hang criterion for runaway production is a declared
 * exact ceiling on how many events the scenario is theoretically allowed to trigger.
 *
 * Counting rules (authoritative):
 *   maxJournalEvents — EventStore envelope count (`readJournal(...).total`)
 *   maxSseEvents     — OpenCode EventProbe frames excluding `server.heartbeat`
 *
 * Exceeding either bound fails the canary immediately (no silence wait).
 */

import { readJournal, watchJournal, journalFactTail, getOrCreateSharedObserver } from './journal-observer.js';

export const SSE_EXCLUDED_TYPES = new Set(['server.heartbeat']);

export function isCountedSseEvent(event) {
  const type = event?.type;
  return typeof type === 'string' && type !== '' && !SSE_EXCLUDED_TYPES.has(type);
}

export function normalizeEventCeilings(setup = {}) {
  const ceilings = {};
  for (const key of ['maxJournalEvents', 'maxSseEvents']) {
    if (setup[key] === undefined) continue;
    const value = setup[key];
    if (!Number.isInteger(value) || value < 1) {
      throw new Error(
        `${key} must be a positive integer (theoretical exact trigger count), got ${JSON.stringify(value)}`,
      );
    }
    ceilings[key] = value;
  }
  return ceilings;
}

/**
 * Attach live ceiling enforcement to a running scenario.
 *
 * Returns a controller with `stop()` / `snapshot()`; `stop` is idempotent.
 * On breach: dumps counts + tails via `onBreach`, then `process.exit(1)` unless
 * `onBreach` throws (tests may inject a throwing handler).
 */
export function attachEventCeilings(scenario, ceilings, { onBreach } = {}) {
  const maxJournal = ceilings?.maxJournalEvents;
  const maxSse = ceilings?.maxSseEvents;
  if (maxJournal === undefined && maxSse === undefined) {
    return {
      stop() {},
      snapshot: () => ({
        journalEvents: 0,
        sseEvents: 0,
        maxJournalEvents: null,
        maxSseEvents: null,
      }),
    };
  }

  let stopped = false;
  // Count frames already buffered before attach (setup connects SSE first).
  let sseEvents = Array.isArray(scenario.events?.allEvents)
    ? scenario.events.allEvents.filter(isCountedSseEvent).length
    : 0;
  let journalEvents = 0;
  let unsubSse = null;
  let stopJournalWatch = null;

  const snapshot = () => {
    const obs = (maxJournal !== undefined && scenario.host?.workDir) ? getOrCreateSharedObserver(scenario.host.workDir) : null;
    const counts = obs?.counts?.();
    return {
      journalEvents,
      sseEvents,
      appends: counts?.appends ?? journalEvents,
      uniqueEvents: counts?.uniqueEvents ?? journalEvents,
      sseFrames: sseEvents,
      maxJournalEvents: maxJournal ?? null,
      maxSseEvents: maxSse ?? null,
    };
  };

  const breach = (kind, observed, limit) => {
    if (stopped) return;
    stopped = true;
    try {
      stopJournalWatch?.();
    } catch {}
    try {
      unsubSse?.();
    } catch {}
    try {
      scenario.watchdog?.stop();
    } catch {}

    const detail = {
      kind,
      observed,
      limit,
      ...snapshot(),
    };
    const message =
      `event-ceiling breached: ${kind} ${observed} > max ${limit} ` +
      `(journal=${detail.journalEvents}/${detail.maxJournalEvents ?? '—'}; ` +
      `sse=${detail.sseEvents}/${detail.maxSseEvents ?? '—'}; heartbeats excluded)`;

    if (typeof onBreach === 'function') {
      onBreach(detail, message);
      return;
    }
    try {
      console.error(`── ${message} ──`);
      console.error(`── event tail ──\n${scenario.events?.dump?.(20) ?? ''}`);
      console.error(`── journal fact tail ──\n${journalFactTail(scenario.host.workDir, 20).join('\n')}`);
    } catch {}
    console.error(message);
    process.exit(1);
  };

  const checkJournal = () => {
    if (stopped || maxJournal === undefined) return;
    const obs = scenario.host?.workDir ? getOrCreateSharedObserver(scenario.host.workDir) : null;
    if (obs) {
      obs.refresh().catch(() => {});
      journalEvents = obs.counts().uniqueEvents;
    } else {
      journalEvents = readJournal(scenario.host.workDir).total;
    }
    if (journalEvents > maxJournal) breach('maxJournalEvents', journalEvents, maxJournal);
  };

  const checkSse = (event) => {
    if (stopped || maxSse === undefined) return;
    if (!isCountedSseEvent(event)) return;
    sseEvents += 1;
    if (sseEvents > maxSse) breach('maxSseEvents', sseEvents, maxSse);
  };

  if (maxSse !== undefined && sseEvents > maxSse) {
    breach('maxSseEvents', sseEvents, maxSse);
  }
  if (maxSse !== undefined && typeof scenario.events?.onEvent === 'function') {
    unsubSse = scenario.events.onEvent(checkSse);
  }
  if (maxJournal !== undefined) {
    checkJournal();
    stopJournalWatch = watchJournal(scenario.host.workDir, checkJournal);
  }

  scenario.eventCeilings = {
    stop() {
      if (stopped) return;
      stopped = true;
      try {
        stopJournalWatch?.();
      } catch {}
      try {
        unsubSse?.();
      } catch {}
    },
    snapshot,
    checkJournal,
  };

  return scenario.eventCeilings;
}

/** Schema / compile-time problems for setup event ceilings. */
export function eventCeilingSetupProblems(setup) {
  if (setup === undefined) return [];
  if (setup === null || typeof setup !== 'object' || Array.isArray(setup)) {
    return ['setup must be a table'];
  }

  const problems = [];
  for (const key of ['maxJournalEvents', 'maxSseEvents']) {
    if (setup[key] === undefined) continue;
    const value = setup[key];
    if (!Number.isInteger(value) || value < 1) {
      problems.push(`setup.${key} must be a positive integer (theoretical exact trigger count)`);
    }
  }
  return problems;
}
