import { readFileSync, readdirSync } from 'node:fs'
import { join } from 'node:path'
import { testDeclarations } from './test-declarations.mjs'

/**
 * Deterministic discovery of `*.test.mjs` files directly inside a suite
 * directory. Non-test files (including any `run.mjs`) and subdirectories are
 * excluded; results are sorted so ordering is reproducible.
 *
 * A required suite directory must exist and be readable; errors propagate.
 *
 * @param {string} dir absolute directory to scan (flat; non-recursive)
 * @param {{ testSuffix?: string }} [opts]
 * @returns {string[]} sorted bare filenames (e.g. `['contents.test.mjs', ...]`)
 */
export function discoverSuiteTests(dir, { testSuffix = '.test.mjs' } = {}) {
  const entries = readdirSync(dir, { withFileTypes: true })
  return entries
    .filter((entry) => entry.isFile() && entry.name.endsWith(testSuffix))
    .map((entry) => entry.name)
    .sort()
}

export function discoverIntegrationTests(dir) {
  return discoverSuiteTests(dir)
    .map((name) => join(dir, name))
    .filter((file) => testDeclarations(readFileSync(file, 'utf8')).some(({ kind }) => kind === 'integrationTest'))
}

export function discoverRepositoryIntegrationTests(root) {
  const requirements = join(root, 'requirements')
  return readdirSync(requirements, { withFileTypes: true })
    .filter((entry) => entry.isDirectory() && !['distribution', 'proposals'].includes(entry.name))
    .sort((a, b) => a.name.localeCompare(b.name))
    .flatMap((entry) => discoverIntegrationTests(join(requirements, entry.name, 'tests')))
}
