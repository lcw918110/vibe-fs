/**
 * gate-budget-cases.mjs — budget table relations retained after scripts/budget-gate.mjs removal.
 *
 * 0.5.3 retired the budget-gate scanner. Cases that only imported that scanner were removed.
 * Existing frozen values belong to 010; primary silence bounds belong to 006.
 */

import { assertEq, assertTrue } from './lib.mjs';
import * as budget from '../../e2e/support/time-budget.js';

export const budgetCases = [
  {
    name: 'verification-system-010 existing frozen verification budgets retain their values',
    fn: () => {
      const expected = {
        LITERAL_BUDGET_THRESHOLD_MS: 1000,
        WATCHDOG_TIMEOUT_MS: 5000,
        HARNESS_CASE_SILENCE_MS: 20000,
        DIAGNOSTIC_RACE_MS: 3000,
        CANARY_READY_MS: 10000,
        READINESS_STAGE_MS: 4000,
        CANARY_TIMEOUT_MS: 90000,
        WAIT_FACT_WINDOW_MS: 120000,
        FORK_COMPLETION_WINDOW_MS: 10000,
        FORK_RECONCILE_SLICE_MS: 2000,
        PER_TEST_TIMEOUT_MS: 2500,
        SUITE_BACKSTOP_MS: 900000,
        UNIT_VERDICT_SILENCE_MS: 5000,
        PROJECT_CHECK_TIMEOUT_MS: 180000,
        UNIT_RUNNER_PROBE_PER_TEST_MS: 2000,
        UNIT_RUNNER_PROBE_SILENCE_MS: 7000,
        UNIT_RUNNER_PROBE_TIGHT_SILENCE_MS: 3500,
        DEFAULT_AWAIT_TIMEOUT_MS: 1000,
        DEFAULT_NEVER_TIMEOUT_MS: 5000,
        GATE_PROBE_TIMEOUT_MS: 3000,
        GATE_HOST_START_TIMEOUT_MS: 5000,
        TEARDOWN_IDLE_MS: 2000,
        SIGTERM_GRACE_MS: 5000,
        SIGKILL_GRACE_MS: 1000,
        PROCESS_TREE_TIMEOUT_MS: 2000,
        SOCKET_CHECK_TIMEOUT_MS: 2000,
        HOST_START_TIMEOUT_MS: 5000,
        ORPHAN_MIN_AGE_MS: 5000,
        LEDGER_ENTRY_TTL_MS: 1800000,
        ENFORCER_POLL_SLICE_MS: 500,
      };

      const actual = Object.fromEntries(
        Object.entries({ ...budget }).filter(([, value]) => typeof value === 'number'),
      );

      // CI may inject UNIT_VERDICT_SILENCE_MS (ci.yml hang headroom). Frozen
      // table asserts committed fallbacks; prove the override applied, then
      // compare the rest against defaults (no product-logic change).
      if (process.env.UNIT_VERDICT_SILENCE_MS !== undefined) {
        assertEq(
          actual.UNIT_VERDICT_SILENCE_MS,
          Number(process.env.UNIT_VERDICT_SILENCE_MS),
          'UNIT_VERDICT_SILENCE_MS env override must apply at runtime',
        );
        actual.UNIT_VERDICT_SILENCE_MS = expected.UNIT_VERDICT_SILENCE_MS;
      }

      const canonical = (table) =>
        JSON.stringify(Object.fromEntries(Object.entries(table).sort(([a], [b]) => (a < b ? -1 : 1))), null, 1);

      assertEq(canonical(actual), canonical(expected), 'the budget table changed');
    },
  },

  {
    name: 'verification-system-006 silence bounds precede physical backstops',
    fn: () => {
      assertTrue(
        budget.WATCHDOG_TIMEOUT_MS < budget.CANARY_TIMEOUT_MS,
        'the silence budget must be tighter than the process fallback, or the fallback becomes primary',
      );
      assertTrue(
        budget.WATCHDOG_TIMEOUT_MS < budget.WAIT_FACT_WINDOW_MS,
        'same for the waitFact window: it is a fallback, the watchdog is the criterion',
      );
      assertTrue(
        budget.PER_TEST_TIMEOUT_MS < budget.SUITE_BACKSTOP_MS,
        'a per-test bound at or above the suite ceiling would make the suite ceiling the only hang criterion',
      );
      assertTrue(
        budget.UNIT_VERDICT_SILENCE_MS > budget.PER_TEST_TIMEOUT_MS,
        'the verdict-silence window must cover one whole test plus jitter, or an overrun reads as a hang',
      );
      assertTrue(
        budget.UNIT_VERDICT_SILENCE_MS < budget.SUITE_BACKSTOP_MS,
        'the silence window is the primary criterion; the suite ceiling is only 兜底 (verification-system-004)',
      );
      assertTrue(
        budget.HARNESS_CASE_SILENCE_MS >= budget.UNIT_RUNNER_PROBE_SILENCE_MS
          && budget.UNIT_RUNNER_PROBE_SILENCE_MS > budget.UNIT_RUNNER_PROBE_PER_TEST_MS
          && budget.UNIT_RUNNER_PROBE_TIGHT_SILENCE_MS <= budget.UNIT_RUNNER_PROBE_SILENCE_MS,
        'physical probe budgets must fit inside the harness silence window',
      );
      assertTrue(
        budget.READINESS_STAGE_MS < budget.CANARY_READY_MS,
        'a stage budget at or above the total startup 兜底 makes the ladder decorative (verification-system-004)',
      );
      assertTrue(
        budget.PROJECT_CHECK_TIMEOUT_MS > budget.PER_TEST_TIMEOUT_MS
          && budget.PROJECT_CHECK_TIMEOUT_MS < budget.SUITE_BACKSTOP_MS,
        'the project-check step bound is a per-step exception above the ordinary per-test bound and below the ' +
          'suite 兜底; at or above the backstop it would become the only criterion those steps have',
      );
    },
  },
];
