/**
 * Shared timing budgets for the current verification facilities.
 * WHAT 006 defines causal silence; these constants implement the current windows,
 * diagnostic ceilings and physical backstops. Their values are unchanged in this revision.
 */

// Historical discriminator retained in the frozen budget table; its scanner was retired.
export const LITERAL_BUDGET_THRESHOLD_MS = 1000;

/**
 * 依赖注入入口：同名环境变量覆盖默认值，让门禁用例把同一套监督语义跑到更小的时间尺度。
 *
 * 判据不依赖值的绝对大小，依赖值之间的不等式（静默窗口 > 单测界、轮询切片 < 窗口、
 * 合法工作总量 > 窗口），故整体缩放不破坏任何判据，只缩短墙钟。只接受正有限数；非法值
 * 拒绝启动而非静默回退——一个拼错的环境变量若回退默认值，门禁会在错误的窗口上给出绿灯。
 */
const budgetFromEnv = (name, fallback) => {
  const raw = process.env[name];
  if (raw === undefined) return fallback;
  const value = Number(raw);
  if (!Number.isFinite(value) || value <= 0) {
    throw new Error(`${name} must be a positive finite number, got ${JSON.stringify(raw)}`);
  }
  return value;
};

// ── causal progress ─────────────────────────────────────────────────────────

/**
 * Silence budget of a scenario-local watchdog: how long without causal progress before the
 * canary is declared hung. Short on purpose — verification-system-006 asks 「距上次因果进展过了多久」, and 3s
 * means the diagnostic is taken at the causal scene instead of minutes downstream. Also the
 * default budget for a canary `wait` expectation (`strict-mock-provider.js:154`), so a single
 * fork→worktree bootstrap or join mailbox gate (~2.3-2.4s) fits inside it without a per-step
 * override.
 */
export const WATCHDOG_TIMEOUT_MS = budgetFromEnv('WATCHDOG_TIMEOUT_MS', 5000);

/**
 * Integration harness case-silence (verification-system-006). Must exceed the longest
 * single harness case wall time. Unit-runner renew probes intentionally last
 * longer than the e2e canary silence (WATCHDOG_TIMEOUT_MS); if the harness dog
 * reused that budget, the last open case would be killed mid-work.
 */
export const HARNESS_CASE_SILENCE_MS = budgetFromEnv('HARNESS_CASE_SILENCE_MS', 20000);

/**
 * Ceiling on the watchdog's own teardown once it has already decided to fire. `watchdog.js`
 * and `scenario-parallel.js` each held this as an independent 3000 literal for one idea: the
 * diagnostic dump and `host.stop` must not themselves hang, so they race a timer. Larger than
 * the silence budget is correct here — this runs after the verdict, so it costs a healthy run
 * nothing, and truncating the dump would breach 「删除 watchdog 的诊断转储」.
 */
export const DIAGNOSTIC_RACE_MS = 3000;

/**
 * Window for the 就绪门禁: a canary that has not printed its exact ready marker within this is
 * failed, not waved through. Previously three literals — the timer plus two failure strings
 * spelling the same value as "10s" — which is how a message outlives the budget it describes.
 */
export const CANARY_READY_MS = 10000;

/**
 * How long one startup STAGE may be silent before the canary is declared stuck (W5).
 *
 * Replaces `CANARY_READY_MS` as the startup criterion; that constant survives as the total 兜底.
 * The clause forbids a window with no causal criterion inside it, and a flat ten seconds from spawn
 * to ready was exactly that: a canary that bound its port and then wedged was indistinguishable
 * from one still compiling.
 *
 * Per stage rather than per startup, so total startup time is unbounded in the same way a healthy
 * scenario's runtime is — what is bounded is silence. Four seconds because the slowest observed
 * stage is `host.start`'s health wait under the sole Long Stroke entry (G4R-4; no multi-canary pool).
 */
export const READINESS_STAGE_MS = 4000;

/**
 * Provider-failure backstop ceiling for one Host process: [006] 禁止把 wall-clock 总超时当作唯一判据——watchdog 才是首要判据，本兜底因此成立。Restart-heavy strokes need roughly 45s solo headroom;
 * the bound stays generous as a backstop only. Still overridable through the CANARY_TIMEOUT_MS
 * environment variable; the env read stays at the call site so this module reads no process state.
 */
export const CANARY_TIMEOUT_MS = 90000;

/**
 * Outer wall-clock window of a `waitFact` barrier, whose real criterion is journal facts
 * appearing. A publish/fast-forward chain crosses many host events, so the span is long;
 * whether that loop's renewal is genuinely causal is package W6's subject, and this constant
 * is only the fallback it must not be leaning on.
 */
export const WAIT_FACT_WINDOW_MS = 120000;

/**
 * How long the nudge canary reconciles the fork tool from the session API before giving up.
 * Long because it covers a real fork plus a nudge round trip; the loop inside it feeds the
 * watchdog whenever a fork completes, so this bound is reached only when nothing completes.
 */
export const FORK_COMPLETION_WINDOW_MS = 10000;

/**
 * One slice of that reconcile loop: how long it waits for the next session event before
 * re-reading the API. Note for W6 — this sits below the silence budget (2000 < 3000), and the
 * loop feeds the watchdog only when a fork completes, so a slice that runs to its full length
 * cannot outlast the watchdog's window; it would have at the old 2000/2000 equality.
 * Centralizing it is what makes that visible; changing it is a semantic decision W6 owns, not a
 * migration.
 */
export const FORK_RECONCILE_SLICE_MS = 2000;

/**
 * ENFORCER-160 polling slice for the parked-transform canary. Must be faster
 * than the window it sits inside (WATCHDOG_TIMEOUT_MS) — a slice that is not
 * strictly smaller than its bound is the bound, not a probe.
 */
export const ENFORCER_POLL_SLICE_MS = 500;

// ── unit suite (layers 1-3) ─────────────────────────────────────────────────

/**
 * Default for a leaf test that explicitly requests timeout-and-forget. It is not passed to
 * node:test's process-isolated `run()` wrapper: under Node 20 that wrapper represents the whole
 * file, so doing so turns one leaf budget into a total budget for module load plus every test in the
 * file. Pure folds and fake-clock trajectories that opt in still finish well under this bound.
 */
export const PER_TEST_TIMEOUT_MS = budgetFromEnv('PER_TEST_TIMEOUT_MS', 2500);

/**
 * Whole-suite ceiling owned by the external node:test supervisor, as a 兜底 rather than the
 * hang criterion.
 *
 * Before W4 this WAS the real criterion: a test that hangs while holding a handle prevents
 * node:test from emitting `end`, so nothing else terminated the run. verification-system-006 forbids that
 * (「把 wall-clock 总超时当作唯一挂死判据」) and W4 removed it — `UNIT_VERDICT_SILENCE_MS` below is the
 * primary criterion, and this survives only for a child that outlives its supervisor.
 *
 * [006] 禁止把 wall-clock 总超时当作唯一判据；非首要的物理兜底因此成立。
 */
export const SUITE_BACKSTOP_MS = budgetFromEnv('SUITE_BACKSTOP_MS', 900000);

/**
 * How long the unit suite may go without a test VERDICT before it is declared hung.
 *
 * The primary hang criterion for `tests/unit`, fed by `test:pass` / `test:fail` / `test:complete`.
 * Not `PER_TEST_TIMEOUT_MS`, because a verdict arrives only after a test finishes: node:test's
 * timeout is a verdict line rather than an abort line (measured), so an overrunning test keeps
 * running and the gap between two verdicts can legitimately exceed the per-test bound. Three times
 * that bound covers the overrun plus scheduling jitter under `concurrency: true` while still ending
 * a genuinely hung run two orders of magnitude sooner than the backstop.
 */
export const UNIT_VERDICT_SILENCE_MS = budgetFromEnv('UNIT_VERDICT_SILENCE_MS', 5000);

/**
 * Verdict-silence basis for the integration step whose tests ARE real Fable
 * compiler-boundary invocations (`dotnet tool run fable` over fixture/owner
 * projects, compile-impact CLI focused emits).
 *
 * Scoped on purpose. The integration default (15s test budget + 5s grace = 20s) is the right
 * criterion for every step that only loads modules and asserts: at 20s such a step is hung, and
 * the SIGKILL plus the outstanding-file dump name the causal scene. Raising that global to fit a
 * compiler invocation would buy the compiler-boundary step its headroom by removing the hang
 * criterion from the other steps — 「超时放大掩盖资源泄漏而非修复因果信号」, and 「延长静默窗口或测试超时以
 * 掩盖竞态」 (VERIFICATION-SYSTEM-006). So the bound is declared per step, and only the
 * compiler-boundary step carries it.
 *
 * The value is headroom, not padded to taste. The compiler-boundary lanes live in separate test
 * files, because Node 20 process isolation releases leaf verdicts only when the file wrapper
 * completes. A completed physical file is causal progress, while nested leaves and stdout remain
 * non-authoritative. 180s stays well under `SUITE_BACKSTOP_MS`, so the suite ceiling remains the
 * 兜底 and the verdict-silence window derived from this stays the primary criterion.
 */
export const PROJECT_CHECK_TIMEOUT_MS = budgetFromEnv('PROJECT_CHECK_TIMEOUT_MS', 180000);

/**
 * Fixed probe lattice for `tests/integration/harness/unit-runner-cases.mjs`.
 * Independent of production PER_TEST/UNIT_VERDICT so raising production CI headroom
 * does not collapse fixture headroom (verification-system-006 inequalities still hold).
 */
export const UNIT_RUNNER_PROBE_PER_TEST_MS = budgetFromEnv('UNIT_RUNNER_PROBE_PER_TEST_MS', 2000);
export const UNIT_RUNNER_PROBE_SILENCE_MS = budgetFromEnv('UNIT_RUNNER_PROBE_SILENCE_MS', 7000);
export const UNIT_RUNNER_PROBE_TIGHT_SILENCE_MS = budgetFromEnv(
  'UNIT_RUNNER_PROBE_TIGHT_SILENCE_MS',
  3500,
);

// ── waiting for one semantic event ──────────────────────────────────────────

/**
 * Default window for a single awaited semantic event — one SSE event, one prompt reaching idle,
 * one scenario step. Four files defaulted to this same 1000 for this same reason.
 */
export const DEFAULT_AWAIT_TIMEOUT_MS = 1000;

/**
 * How long a "this must never arrive" assertion waits before absence counts as proven.
 * Deliberately longer than DEFAULT_AWAIT_TIMEOUT_MS: a negative claim is only as strong as the
 * window it watched.
 */
export const DEFAULT_NEVER_TIMEOUT_MS = 5000;

/**
 * Window for one awaited event inside the harness's own gate cases, which drive a local fake
 * SSE server rather than a Host. Wider than DEFAULT_AWAIT_TIMEOUT_MS because these cases run
 * under `test:harness` alongside process spawns competing for the same CPU.
 */
export const GATE_PROBE_TIMEOUT_MS = 3000;

/**
 * Silence window for real `opencode serve` to print its listening marker inside a gate case.
 * Missing this bound means the startup ladder lacks progress or does too much work; scheduler
 * contention is not grounds for widening the criterion.
 */
export const GATE_HOST_START_TIMEOUT_MS = 5000;

/**
 * How long teardown waits for each session to report idle after an abort. Best-effort — the
 * caller swallows the rejection, because a session that never idles is caught by the leak check
 * that follows, with a better diagnostic than a bare timeout.
 */
export const TEARDOWN_IDLE_MS = 2000;

// ── process lifecycle ───────────────────────────────────────────────────────

/** Grace after SIGTERM before escalating, so a Host gets to run its own shutdown. */
export const SIGTERM_GRACE_MS = 5000;

/** Grace after SIGKILL before the process is reported un-killable. Was declared twice. */
export const SIGKILL_GRACE_MS = 1000;

/** Ceiling on the `ps`/`pgrep` call that enumerates a process tree during leak checks. */
export const PROCESS_TREE_TIMEOUT_MS = 2000;

/** How long a port may still accept connections after dispose before it counts as leaked. */
export const SOCKET_CHECK_TIMEOUT_MS = 2000;

/** Window for `opencode serve` to print its listen line and then answer a health probe. */
export const HOST_START_TIMEOUT_MS = 5000;

/**
 * Minimum age before a process carrying no run-id marker may be reaped as an orphan. Ownership
 * is the primary criterion, so this is only the 兜底 for a bare process that never inherited the
 * marker — hence short. Overridable through REAPER_ORPHAN_MIN_AGE_MS.
 */
export const ORPHAN_MIN_AGE_MS = 5000;

/**
 * How long a spawn-ledger entry stays authoritative. Far longer than anything else here because
 * it bounds a different thing: not how long to wait for progress, but how long a record of a
 * spawned process remains worth acting on. Was written as `30 * 60 * 1000`, which the gate reads
 * as three separate numbers and a reader as none.
 */
export const LEDGER_ENTRY_TTL_MS = 1800000;

// Suite-level multi-canary / stability-repeat windows (SCENARIO_SUITE_WINDOW_MS,
// STABILITY_*) were retired with G4R-4. Long Stroke is one continuous Host lifetime;
// silence + causal progress (WATCHDOG_TIMEOUT_MS / WAIT_FACT_WINDOW_MS) are the bounds.
