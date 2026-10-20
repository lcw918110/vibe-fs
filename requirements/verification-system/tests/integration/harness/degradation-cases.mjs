/**
 * degradation-cases.mjs — Source-level negative assertions for harness topology & execution.
 *
 * Retained: One World topology checks (sole entry, no shuffle-repeat pool),
 * internal expectations background classification, and flow waits not competing with silence watchdog.
 */

import { existsSync, readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

import { assertEq, assertTrue } from './lib.mjs';

const REPO_ROOT = fileURLToPath(new URL('../../../../../', import.meta.url));
const SOLE_ENTRY = 'requirements/verification-system/tests/014.test.mjs';

const readSource = (relative) => readFileSync(`${REPO_ROOT}${relative}`, 'utf8');

export const degradationCases = [
  {
    name: 'verification-system-004 One World sole entry has no multi-canary shuffle-repeat pool',
    fn: () => {
      // Covers the pool/launcher degradations that belonged to the retired multi-canary runner
      // (fixed-sleep bark stagger, ready-timeout-as-pass, release-gate --repeat 1..3). One World
      // replaces that topology with a sole entry: there is no stagger to sleep-replace, no pool
      // pass condition to omit ready terms from, and no --repeat release gate to raise into
      // until-pass. Detection at the source — an absence has no input that exhibits it.
      assertTrue(
        existsSync(`${REPO_ROOT}${SOLE_ENTRY}`),
        `${SOLE_ENTRY} must exist as the sole top-level E2E entry`,
      );

      const pkg = JSON.parse(readSource('package.json'));
      const dailyPipeline = pkg.scripts?.['format-build-test'];
      const releasePipeline = pkg.scripts?.['verify:release'];
      assertTrue(
        typeof dailyPipeline === 'string' && dailyPipeline.includes('verify.mjs'),
        'package.json format-build-test must dispatch to scripts/verify.mjs',
      );
      assertTrue(
        typeof releasePipeline === 'string' && releasePipeline.includes('verify.mjs'),
        'package.json verify:release must dispatch to scripts/verify.mjs',
      );
      assertTrue(
        !dailyPipeline.includes('tests/e2e/run.mjs') && !dailyPipeline.includes('requirements/verification-system/tests/e2e/run.mjs'),
        'no retired multi-canary launcher',
      );
      assertTrue(!dailyPipeline.includes('--repeat'), 'format-build-test must not reintroduce a --repeat release-gate pool');

      const verifySource = readSource('scripts/verify.mjs');
      const integrationAt = verifySource.indexOf('tests/integration/run.mjs');
      const e2eAt = verifySource.indexOf('tests/014.test.mjs');
      assertTrue(
        integrationAt >= 0 && e2eAt >= 0 && integrationAt < e2eAt,
        'verify.mjs must run integration before Long Stroke e2e',
      );
      assertTrue(
        verifySource.includes('tests/014.test.mjs') && !verifySource.includes('tests/e2e/run.mjs'),
        'the sole e2e step must remain entry.test.mjs, not the retired multi-canary launcher',
      );
      const packageAt = verifySource.indexOf('verify-package.mjs');
      assertTrue(
        packageAt >= 0 && e2eAt < packageAt,
        'release must run verify-package after the Long Stroke',
      );

      const integration = readSource('requirements/verification-system/tests/integration/run.mjs');
      assertEq(
        integration.split('scripts/warmup-opencode.mjs').length - 1,
        1,
        'integration orchestrator must own exactly one opencode warmup',
      );
      assertEq(
        integration.split('requirements/distribution/tests/integration/package/run.mjs').length - 1,
        1,
        'integration orchestrator must own exactly one distribution package child',
      );
      assertTrue(
        integration.indexOf('scripts/warmup-opencode.mjs') < integration.indexOf('await superviseNodeTest'),
        'integration orchestrator must warm opencode before any integration child',
      );

      const entry = readSource(SOLE_ENTRY);
      // Pin absence when G4R-4 has already deleted the pool artifacts. Soft during cutover:
      // sole-entry scripts above already refuse to wire them.
      assertTrue(
        !existsSync(`${REPO_ROOT}requirements/verification-system/tests/e2e/run.mjs`) || !pipeline.includes('requirements/verification-system/tests/e2e/run.mjs'),
        'multi-canary run.mjs must be gone, or at least unused by format-build-test',
      );
      assertTrue(
        !existsSync(`${REPO_ROOT}requirements/verification-system/tests/e2e/support/manifest.mjs`) || !/from\s*['"][^'"]*manifest/.test(entry),
        'canary manifest must be gone, or at least unused by the sole entry',
      );

      assertTrue(!/\bshuffle\b/.test(entry), 'the sole entry must not shuffle a canary pool');
      assertTrue(!entry.includes('--repeat'), 'the sole entry must not implement a repeat release gate');
      assertTrue(!entry.includes('MAX_PARALLEL'), 'the sole entry must not enforce a canary concurrency pool');
      assertTrue(!entry.includes('STARTUP_WIDTH'), 'the sole entry must not stagger launches by startup width');
      assertTrue(!/from\s*['"][^'"]*manifest/.test(entry), 'the sole entry must not import the canary manifest');
    },
  },

  {
    name: 'verification-system-004 internal expectations are background progress',
    fn: () => {
      const source = readSource('requirements/verification-system/tests/e2e/support/strict-mock-provider.js');

      assertTrue(
        source.includes('blocking: entry.internal !== true'),
        'internal lanes must never renew the blocking watchdog',
      );
      assertTrue(
        !source.includes('blocking: true'),
        'an unconditional blocking classification lets background loops mask a dead path',
      );
    },
  },

  {
    name: 'verification-system-004 flow waits do not start a competing total timeout',
    fn: () => {
      const source = readSource('requirements/verification-system/tests/e2e/support/scenario-driver.mjs');

      assertTrue(
        !source.includes('waitForExpectation(step.wait, step.timeoutMs || WATCHDOG_TIMEOUT_MS)'),
        'the fixed watchdog owns silence; a per-wait total window races healthy causal progress',
      );
      assertTrue(
        !source.includes('awaitSessionsByAgent(scenario, agent, step.timeoutMs || WATCHDOG_TIMEOUT_MS)'),
        'child discovery must use the same watchdog rather than a second total deadline',
      );
      assertTrue(
        !source.includes('timeoutMs: step.timeoutMs || WATCHDOG_TIMEOUT_MS'),
        'turn terminals must renew the same watchdog at each causal checkpoint',
      );
      assertTrue(
        !source.includes('}, step.timeoutMs || WATCHDOG_TIMEOUT_MS)'),
        'event waits must not race the fixed watchdog with a total deadline',
      );

      const turnSource = readSource('requirements/verification-system/tests/e2e/support/scenario-turn.js');
      assertTrue(
        !turnSource.includes('timeoutMs: opts.timeoutMs || WATCHDOG_TIMEOUT_MS'),
        'Turn must leave its local timeout absent unless the scenario explicitly declares one',
      );

      const providerSource = readSource('requirements/verification-system/tests/e2e/support/strict-mock-provider.js');
      assertTrue(
        !providerSource.includes('timeoutMs = WATCHDOG_TIMEOUT_MS'),
        'provider wait helpers must not default every flow wait to the silence window as a total deadline',
      );
    },
  },
];
