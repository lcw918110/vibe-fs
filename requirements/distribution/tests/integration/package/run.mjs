// requirements/distribution/tests/integration/package/run.mjs — package integration supervision.
//
//   node tests/integration/package/run.mjs
// Requires dist/ built (node scripts/build.mjs) before pack/install/import checks.
//
// Workspace layout and distribution checks: merged into a single supervision call.

import path from 'node:path'
import { fileURLToPath } from 'node:url'

import { superviseNodeTest } from '../../../../verification-system/tests/e2e/support/supervise-node-test.mjs'
import { discoverIntegrationTests } from '../../../../verification-system/tests/support/discover-suite-tests.mjs'

const here = path.dirname(fileURLToPath(import.meta.url))
const testsDir = path.resolve(here, '../..')

const suites = discoverIntegrationTests(testsDir)

if (suites.length === 0) {
  console.error(`package integration: no integration suites discovered in ${testsDir}`)
  process.exit(1)
}

if (process.argv.includes('--dry-run') || process.argv.includes('--print')) {
  console.log('package integration: dry run')
  const root = path.resolve(here, '../../../../..')
  for (const file of suites) console.log(`    ${path.relative(root, file).split(path.sep).join('/')}`)
  process.exit(0)
}

console.log(`\n=== package integration (${suites.length} suites) ===`)
await superviseNodeTest({
  files: suites,
  label: 'requirements/distribution/tests/integration/package',
  silenceMs: 60000,
  logPrefix: 'package',
  env: {
    ...process.env,
    WXS_TIER_INTEGRATION: '1',
    WXS_ACCEPT_TODO: '1',
  },
})

console.log('\npackage integration: all suites passed')
