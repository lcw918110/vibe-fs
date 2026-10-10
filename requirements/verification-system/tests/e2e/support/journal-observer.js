import fs from 'node:fs';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';

const commonDirCache = new Map(); // workDir -> git common dir (immutable per checkout)

export const gitCommonDir = (workDir) => {
  const cached = commonDirCache.get(workDir);
  if (cached !== undefined) return cached;
  if (!fs.existsSync(workDir)) {
    const fallback = path.join(workDir, '.git');
    commonDirCache.set(workDir, fallback);
    return fallback;
  }
  const common = execFileSync('git', ['-C', workDir, 'rev-parse', '--git-common-dir'], {
    encoding: 'utf8',
  }).trim();
  const resolved = path.isAbsolute(common) ? common : path.resolve(workDir, common);
  commonDirCache.set(workDir, resolved);
  return resolved;
};

export const eventsDir = (workDir) => path.join(gitCommonDir(workDir), 'wanxiangshu', 'events');

/** Journal Envelope object nested under EventStore `payload`, or null. */
export function journalEnvelopeFromEventText(text) {
  try {
    const event = typeof text === 'string' ? JSON.parse(text) : text;
    if (event && typeof event === 'object' && event.payload && typeof event.payload === 'object') {
      return event.payload;
    }
    if (event && typeof event === 'object' && Object.prototype.hasOwnProperty.call(event, 'Fact')) {
      return event;
    }
    return null;
  } catch {
    return null;
  }
}

export function extractFactCasesAndIdentities(envelope, event) {
  const cases = [];
  let sessionId = null;
  let logicalRun = null;

  const walkFact = (fact) => {
    if (Array.isArray(fact)) {
      if (typeof fact[0] === 'string') {
        cases.push({ name: fact[0], payload: fact[1] });
        if ((fact[0] === 'RoadId' || fact[0] === 'SessionId') && typeof fact[1] === 'string') {
          sessionId = sessionId ?? fact[1];
        }
        if (fact[0] === 'LogicalRunId' && typeof fact[1] === 'string') {
          logicalRun = logicalRun ?? fact[1];
        }
      }
      for (const item of fact) walkFact(item);
    } else if (fact && typeof fact === 'object') {
      const sid = fact.sessionId ?? fact.session_id ?? fact.SessionId ?? fact.sessionID ?? fact.RoadId ?? fact.ManagerSessionId ?? fact.ChildSessionId;
      if (typeof sid === 'string') sessionId = sessionId ?? sid;
      const lrid = fact.logicalRunId ?? fact.logical_run_id ?? fact.LogicalRunId ?? fact.logicalRun;
      if (typeof lrid === 'string') logicalRun = logicalRun ?? lrid;
      for (const val of Object.values(fact)) walkFact(val);
    }
  };

  if (envelope?.Fact !== undefined) {
    walkFact(envelope.Fact);
  } else if (event?.Fact !== undefined) {
    walkFact(event.Fact);
  }
  if (typeof envelope?.type === 'string' && envelope.type.length > 0) {
    cases.push({ name: envelope.type, payload: envelope });
  }
  if (typeof event?.event_type === 'string' && event.event_type !== 'JournalEnvelope') {
    cases.push({ name: event.event_type, payload: event.payload });
  }
  if (typeof event?.type === 'string' && event.type !== 'JournalEnvelope') {
    cases.push({ name: event.type, payload: event.payload });
  }

  if (!sessionId && typeof event?.session_id === 'string') sessionId = event.session_id;
  if (!sessionId && typeof event?.sessionId === 'string') sessionId = event.sessionId;
  if (!logicalRun && typeof event?.logical_run_id === 'string') logicalRun = event.logical_run_id;
  if (!logicalRun && typeof event?.logicalRunId === 'string') logicalRun = event.logicalRunId;
  if (!logicalRun && typeof event?.logicalRun === 'string') logicalRun = event.logicalRun;

  return { cases, sessionId, logicalRun };
}

const tallyFactNames = (fact, counts) => {
  if (Array.isArray(fact)) {
    if (typeof fact[0] === 'string') counts.set(fact[0], (counts.get(fact[0]) ?? 0) + 1);
    for (const child of fact) tallyFactNames(child, counts);
  } else if (fact && typeof fact === 'object') {
    for (const child of Object.values(fact)) tallyFactNames(child, counts);
  }
};

const tallyEnvelopeAndEvent = (envelope, event, factCounts) => {
  if (envelope?.Fact !== undefined) {
    tallyFactNames(envelope.Fact, factCounts);
  } else if (event?.Fact !== undefined) {
    tallyFactNames(event.Fact, factCounts);
  }
  if (typeof envelope?.type === 'string' && envelope.type.length > 0) {
    factCounts.set(envelope.type, (factCounts.get(envelope.type) ?? 0) + 1);
  }
  if (typeof event?.event_type === 'string' && event.event_type !== 'JournalEnvelope') {
    factCounts.set(event.event_type, (factCounts.get(event.event_type) ?? 0) + 1);
  }
  if (typeof event?.type === 'string' && event.type !== 'JournalEnvelope') {
    factCounts.set(event.type, (factCounts.get(event.type) ?? 0) + 1);
  }
};

/**
 * Journal Observer instance for a single world lifecycle.
 * Incremental, fail-closed on corrupt complete lines or prefix modification.
 */
export function createJournalObserver({ workDir, readFiles, watchDirectory } = {}) {
  let closed = false;
  let watcher = null;
  let version = 0;
  let totalAppends = 0;

  // Resolved events directory
  const resolvedEventsDir = () => {
    if (workDir) return eventsDir(workDir);
    return null;
  };

  // State per writer file: filePath -> WriterState
  // WriterState: {
  //   filePath: string,
  //   dev: number,
  //   ino: number,
  //   consumedBytes: number,
  //   prefixHash: Hash,
  //   trailingBytes: Buffer,
  // }
  const writers = new Map();

  // Deduplicated canonical events
  // eventId -> canonical JSON text
  const byEventId = new Map();

  // Committed event sequence in order of arrival/writer sequence:
  // Array<{ eventId: string, event: Object, text: string, cases: Array<{name: string, payload: any}>, sessionId: string|null, logicalRun: string|null, order: number }>
  const committedEvents = [];

  // caseName -> Array<committedEventIndex>
  const caseIndex = new Map();

  // Active subscribers: Set<(event: Object) => void>
  const subscribers = new Set();

  const defaultReadFiles = () => {
    const dir = resolvedEventsDir();
    if (!dir || !fs.existsSync(dir)) return [];
    try {
      const names = fs.readdirSync(dir).filter((n) => n.endsWith('.ndjson')).sort();
      return names.map((name) => {
        const filePath = path.join(dir, name);
        const stat = fs.statSync(filePath);
        return {
          path: filePath,
          dev: stat.dev,
          ino: stat.ino,
          size: stat.size,
          readBytes: (offset, length) => {
            const fd = fs.openSync(filePath, 'r');
            try {
              const buf = Buffer.alloc(length);
              const readCount = fs.readSync(fd, buf, 0, length, offset);
              return buf.subarray(0, readCount);
            } finally {
              fs.closeSync(fd);
            }
          },
        };
      });
    } catch {
      return [];
    }
  };

  const fileProvider = readFiles ?? defaultReadFiles;

  const processWriter = (fileDescriptor) => {
    const { path: filePath, dev, ino, size, readBytes } = fileDescriptor;
    let state = writers.get(filePath);

    if (!state) {
      state = {
        filePath,
        dev,
        ino,
        consumedBytes: 0,
        prefixHasher: createHash('sha256'),
        prefixHash: createHash('sha256').digest('hex'),
        trailingBytes: Buffer.alloc(0),
      };
      writers.set(filePath, state);
    } else {
      // Identity check: file replacement
      if (state.dev !== dev || state.ino !== ino) {
        throw new Error(`journal observer detected file identity replacement for ${filePath}`);
      }
      // Truncation check
      if (size < state.consumedBytes) {
        throw new Error(
          `journal observer detected consumed prefix truncation for ${filePath}: consumed ${state.consumedBytes} bytes, current size ${size} bytes`,
        );
      }
    }

    const availableNewBytes = size - state.consumedBytes;
    if (availableNewBytes <= 0) return [];

    // readBytes(state.consumedBytes, availableNewBytes) reads all bytes from state.consumedBytes up to size.
    // This includes any bytes that were previously trailingBytes!
    // Therefore, combined is simply chunk without prepending state.trailingBytes, or we read from state.consumedBytes + state.trailingBytes.length!
    const chunk = readBytes(state.consumedBytes, availableNewBytes);
    const combined = chunk;
    const lastNl = combined.lastIndexOf(10); // '\n' = 10

    if (lastNl === -1) {
      // No newline in combined bytes yet
      state.trailingBytes = combined;
      return [];
    }

    const completeBuf = combined.subarray(0, lastNl + 1);
    state.trailingBytes = combined.subarray(lastNl + 1);

    const newlyCommitted = [];
    let lineStart = 0;
    for (let i = 0; i < completeBuf.length; i++) {
      if (completeBuf[i] === 10) {
        const lineBuf = completeBuf.subarray(lineStart, i);
        const lineOffset = state.consumedBytes + lineStart;
        lineStart = i + 1;

        if (lineBuf.length === 0) continue; // skip blank line

        totalAppends++;

        let lineText;
        try {
          const td = new TextDecoder('utf-8', { fatal: true });
          lineText = td.decode(lineBuf);
        } catch (err) {
          throw new Error(
            `journal observer invalid UTF-8 in ${filePath} at offset ${lineOffset}: ${err.message}`,
          );
        }

        let parsed;
        try {
          parsed = JSON.parse(lineText);
        } catch (err) {
          throw new Error(
            `journal observer invalid JSON in ${filePath} at offset ${lineOffset}: ${err.message}`,
          );
        }

        if (!parsed || typeof parsed !== 'object') {
          throw new Error(`journal observer event not an object in ${filePath} at offset ${lineOffset}`);
        }

        const eventId = parsed.event_id;
        if (typeof eventId !== 'string' || eventId.length === 0) {
          throw new Error(`journal observer missing event_id in ${filePath} at offset ${lineOffset}`);
        }

        const existingText = byEventId.get(eventId);
        if (existingText !== undefined) {
          if (existingText !== lineText) {
            throw new Error(
              `journal observer saw EventId identity collision: ${eventId} (different canonical bytes)`,
            );
          }
          // Idempotent deduplication for same event_id same bytes
          continue;
        }

        byEventId.set(eventId, lineText);

        const envelope = journalEnvelopeFromEventText(parsed);
        const { cases, sessionId, logicalRun } = extractFactCasesAndIdentities(envelope, parsed);

        const record = {
          eventId,
          event: Object.freeze(parsed),
          text: lineText,
          envelope,
          cases,
          sessionId,
          logicalRun,
          order: committedEvents.length,
        };

        const idx = committedEvents.length;
        committedEvents.push(record);

        for (const c of cases) {
          let list = caseIndex.get(c.name);
          if (!list) {
            list = [];
            caseIndex.set(c.name, list);
          }
          list.push(idx);
        }

        newlyCommitted.push(record);
      }
    }

    state.prefixHasher.update(completeBuf);
    state.consumedBytes += completeBuf.length;
    state.prefixHash = state.prefixHasher.copy().digest('hex');

    return newlyCommitted;
  };

  const refresh = async () => {
    if (closed) return { appends: totalAppends, uniqueEvents: committedEvents.length, version };

    const fileList = fileProvider();
    const newRecords = [];

    for (const f of fileList) {
      const added = processWriter(f);
      for (const rec of added) newRecords.push(rec);
    }

    if (newRecords.length > 0) {
      version++;
      for (const rec of newRecords) {
        if (closed) break;
        for (const sub of subscribers) {
          try {
            sub(rec.event);
          } catch {}
        }
      }
    }

    return {
      appends: totalAppends,
      uniqueEvents: committedEvents.length,
      version,
    };
  };

  const verifyConsumedPrefixes = () => {
    const fileList = fileProvider();
    const map = new Map(fileList.map((f) => [f.path, f]));
    for (const [filePath, state] of writers.entries()) {
      if (state.consumedBytes === 0) continue;
      const cur = map.get(filePath);
      if (!cur) {
        throw new Error(`journal observer detected disappearance of writer ${filePath}`);
      }
      if (cur.dev !== state.dev || cur.ino !== state.ino) {
        throw new Error(`journal observer detected file identity replacement for ${filePath}`);
      }
      if (cur.size < state.consumedBytes) {
        throw new Error(
          `journal observer detected prefix truncation for ${filePath}: consumed ${state.consumedBytes}, current ${cur.size}`,
        );
      }
      const readPrefix = cur.readBytes(0, state.consumedBytes);
      const hash = createHash('sha256').update(readPrefix).digest('hex');
      if (hash !== state.prefixHash) {
        throw new Error(
          `journal observer detected in-place prefix modification for ${filePath}`,
        );
      }
    }
  };

  const position = () => committedEvents.length;

  const select = ({ caseName, sessionId, logicalRun, after } = {}) => {
    const minOrder = typeof after === 'number' ? after : after ? Number(after) : 0;
    let candidateIndices = null;

    if (caseName !== undefined) {
      candidateIndices = caseIndex.get(caseName) ?? [];
    }

    const filterRecord = (rec) => {
      if (rec.order < minOrder) return false;
      if (sessionId !== undefined && sessionId !== null && rec.sessionId !== sessionId) return false;
      if (logicalRun !== undefined && logicalRun !== null && rec.logicalRun !== logicalRun) return false;
      return true;
    };

    if (candidateIndices !== null) {
      const res = [];
      for (const idx of candidateIndices) {
        const rec = committedEvents[idx];
        if (filterRecord(rec)) res.push(rec.event);
      }
      return res;
    }

    const res = [];
    for (let i = minOrder; i < committedEvents.length; i++) {
      const rec = committedEvents[i];
      if (filterRecord(rec)) res.push(rec.event);
    }
    return res;
  };

  const subscribe = (cb) => {
    if (closed || typeof cb !== 'function') return () => {};
    subscribers.add(cb);
    return () => {
      subscribers.delete(cb);
    };
  };

  const close = async () => {
    if (closed) return;
    try {
      await refresh();
      verifyConsumedPrefixes();
    } finally {
      closed = true;
      subscribers.clear();
      try { watcher?.(); } catch {}
      watcher = null;
    }
  };

  // Wire up directory watcher if watchDirectory option or workDir provided
  if (typeof watchDirectory === 'function') {
    watcher = watchDirectory(() => {
      refresh().catch(() => {});
    });
  } else if (workDir) {
    watcher = watchJournal(workDir, () => {
      refresh().catch(() => {});
    });
  }

  return {
    refresh,
    position,
    select,
    subscribe,
    close,
    // Diagnostic helpers
    verifyConsumedPrefixes,
    counts: () => ({
      appends: totalAppends,
      uniqueEvents: committedEvents.length,
      version,
    }),
    tip: () => (committedEvents.length === 0 ? null : String(version)),
    allEvents: () => committedEvents.map((r) => r.event),
    allTexts: () => committedEvents.map((r) => r.text),
  };
}

// Global cached observer per workDir to share snapshot between waiters and event ceilings
const sharedObservers = new Map();

export function getOrCreateSharedObserver(workDir) {
  let obs = sharedObservers.get(workDir);
  if (!obs) {
    obs = createJournalObserver({ workDir });
    sharedObservers.set(workDir, obs);
  }
  return obs;
}

export function releaseSharedObserver(workDir) {
  const obs = sharedObservers.get(workDir);
  if (obs) {
    sharedObservers.delete(workDir);
    return obs.close();
  }
  return Promise.resolve();
}

/**
 * Local durable snapshot token used only by the E2E observer diagnostics/wake logic.
 * It is deliberately NOT a Git object/ref identity.
 */
export function storeTip(workDir) {
  return readLocalSnapshot(workDir).token;
}

const readLocalSnapshot = (workDir) => {
  const directory = eventsDir(workDir);
  if (!fs.existsSync(directory)) return { lines: [], factCounts: new Map(), token: null };

  const byEventId = new Map();
  const files = fs.readdirSync(directory)
    .filter((name) => name.endsWith('.ndjson'))
    .sort();

  for (const name of files) {
    const file = path.join(directory, name);
    let text;
    try {
      text = fs.readFileSync(file, 'utf8');
    } catch {
      continue;
    }

    const completeLength = text.endsWith('\n') ? text.length : text.lastIndexOf('\n') + 1;
    const complete = completeLength > 0 ? text.slice(0, completeLength) : '';

    for (const line of complete.split('\n')) {
      if (line === '') continue;
      let parsed;
      try {
        parsed = JSON.parse(line);
      } catch {
        continue;
      }
      const eventId = parsed?.event_id;
      if (typeof eventId !== 'string' || eventId.length === 0) continue;
      const existing = byEventId.get(eventId);
      if (existing !== undefined && existing !== line) {
        throw new Error(`journal observer saw EventId identity collision: ${eventId}`);
      }
      byEventId.set(eventId, line);
    }
  }

  const lines = [...byEventId.entries()]
    .sort(([left], [right]) => left.localeCompare(right))
    .map(([, text]) => text);

  const factCounts = new Map();
  for (const line of lines) {
    const envelope = journalEnvelopeFromEventText(line);
    let parsed = null;
    try { parsed = JSON.parse(line); } catch {}
    tallyEnvelopeAndEvent(envelope, parsed, factCounts);
  }

  const token = lines.length === 0
    ? null
    : createHash('sha256').update(lines.join('\n')).digest('hex');

  return { lines, factCounts, token };
};

/** Canonical event JSON texts from every local process writer file. */
export const journalEventLines = (workDir) => readLocalSnapshot(workDir).lines;
const journalEventTexts = journalEventLines;

/**
 * Payloads of the named fact case wherever it nests inside EventStore journal envelopes.
 * Accepts either workDir or an array of event JSON texts / Envelope-like objects.
 */
export function factPayloads(workDirOrLines, caseName) {
  const lines = typeof workDirOrLines === 'string' ? journalEventLines(workDirOrLines) : workDirOrLines;
  const found = [];
  const walk = (value) => {
    if (Array.isArray(value)) {
      if (typeof value[0] === 'string' && value[0] === caseName) found.push(value[1]);
      for (const item of value) walk(item);
    } else if (value && typeof value === 'object') {
      for (const child of Object.values(value)) walk(child);
    }
  };
  for (const line of lines) {
    const envelope =
      typeof line === 'string'
        ? journalEnvelopeFromEventText(line)
        : line && typeof line === 'object'
          ? line.payload && typeof line.payload === 'object'
            ? line.payload
            : line
          : null;
    if (envelope) walk(envelope.Fact);
  }
  return found;
}

export const countFactCase = (workDirOrLines, caseName) => {
  if (typeof workDirOrLines === 'string') {
    return readLocalSnapshot(workDirOrLines).factCounts.get(caseName) ?? 0;
  }
  return factPayloads(workDirOrLines, caseName).length;
};

const digFactLabel = (fact) => {
  if (typeof fact === 'string') return fact;
  if (!Array.isArray(fact) || typeof fact[0] !== 'string') return null;
  if (fact.length >= 2 && Array.isArray(fact[1]) && typeof fact[1][0] === 'string') {
    return digFactLabel(fact[1]) ?? fact[0];
  }
  return fact[0];
};

const factLabelFromEvent = (text) => {
  try {
    const envelope = journalEnvelopeFromEventText(text);
    const label = digFactLabel(envelope?.Fact);
    if (label) return label;
    const match = text.match(
      /"(?:Plugin|Prompt|ProviderFailure|Review|Execution|Orchestrator|Companion|Context|Host|Runtime|Life|Handle|Pair)[A-Za-z0-9]+"/,
    );
    return match?.[0]?.slice(1, -1) ?? 'UnknownFact';
  } catch {
    return 'malformed';
  }
};

/**
 * Strict domain fact reading without text.includes fallback.
 */
export function readJournal(workDir, factName, renewOn = []) {
  const snap = readLocalSnapshot(workDir);
  const texts = snap.lines;
  let named = 0;
  if (factName !== undefined) {
    named = snap.factCounts.get(factName) ?? 0;
  }
  let renew = 0;
  if (renewOn.length > 0) {
    for (const name of new Set(renewOn)) {
      renew += snap.factCounts.get(name) ?? 0;
    }
  }
  return { named, total: texts.length, renew, tip: snap.token };
}

export function journalFactTail(workDir, limit) {
  const tip = storeTip(workDir) ?? 'missing-local-truth';
  return journalEventTexts(workDir)
    .slice(-limit)
    .map((text, index) => `${tip}:${index}:${factLabelFromEvent(text)}`);
}

/**
 * Watch `.git/wanxiangshu/events` directly. The directory may not exist at watcher
 * creation time, so attach upward and descend when runtime truth appears.
 */
export function watchJournal(workDir, onChange) {
  let closed = false;
  let eventsWatcher = null;
  let parentWatcher = null;
  let debounce = null;
  let lastTip = storeTip(workDir);

  const notify = () => {
    if (closed || debounce !== null) return;
    debounce = setImmediate(() => {
      debounce = null;
      if (closed) return;
      const tip = storeTip(workDir);
      if (tip !== lastTip) {
        lastTip = tip;
        onChange();
      } else {
        onChange();
      }
    });
  };

  const stopEvents = () => {
    try { eventsWatcher?.close(); } catch {}
    eventsWatcher = null;
  };

  const attachEvents = () => {
    if (closed || eventsWatcher !== null) return;
    const directory = eventsDir(workDir);
    if (!fs.existsSync(directory)) return;
    try {
      eventsWatcher = fs.watch(directory, notify);
      eventsWatcher.unref?.();
      eventsWatcher.on('error', () => {});
      try { parentWatcher?.close(); } catch {}
      parentWatcher = null;
      notify();
    } catch {}
  };

  attachEvents();
  if (eventsWatcher === null) {
    const common = gitCommonDir(workDir);
    const wanxiangshu = path.join(common, 'wanxiangshu');
    const watchRoot = fs.existsSync(wanxiangshu) ? wanxiangshu : common;
    try {
      parentWatcher = fs.watch(watchRoot, attachEvents);
      parentWatcher.unref?.();
      parentWatcher.on('error', () => {});
    } catch {}
    attachEvents();
  }

  return () => {
    closed = true;
    if (debounce !== null) {
      clearImmediate(debounce);
      debounce = null;
    }
    stopEvents();
    try { parentWatcher?.close(); } catch {}
    parentWatcher = null;
  };
}
