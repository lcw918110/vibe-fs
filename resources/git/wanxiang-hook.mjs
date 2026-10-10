#!/usr/bin/env node

import { readFileSync } from 'node:fs'

// This process is launched by Git hooks and must remain independent of the
// OpenCode/Wanxiangshu plugin lifecycle. It imports only the standalone sync
// module compiled into the installed package.
if (process.env.WANXIANG_GIT_SYNC_ACTIVE === '1') {
  process.exit(0)
}

// Hard fallback for a hung converge child: the in-process budget is 600 s
// (Wanxiangshu.Git.Hook.Sync ConvergeBudgetSeconds); if it could not fire,
// stop the hook process. The store lock recovers through proper-lockfile
// staleness after exit.
const fallbackMs = 660000
const fallbackTimer = setTimeout(() => {
  console.error('Wanxiang hook runner exceeded the ' + fallbackMs + ' ms converge fallback; aborting')
  process.exit(1)
}, fallbackMs)
fallbackTimer.unref()

const [kind, arg1] = process.argv.slice(2)
let referenceTransactionInput = null

if (kind === 'reference-transaction') {
  if (arg1 !== 'committed') {
    process.exit(0)
  }

  referenceTransactionInput = readFileSync(0, 'utf8')
  const relevant = referenceTransactionInput
    .split('\n')
    .some((line) => /^[0-9a-fA-F]+[ \t]+[0-9a-fA-F]+[ \t]+refs\/wanxiang\/remotes\/[^/]+\/store$/.test(line))

  if (!relevant) {
    process.exit(0)
  }
}

const HookSync = await import(new URL('../../dist/Git/Hook/Sync.js', import.meta.url))

let error = null

switch (kind) {
  case 'pre-push':
    error = await HookSync.runPrePush(arg1 ?? '')
    break
  case 'reference-transaction': {
    error = await HookSync.runReferenceTransaction(arg1 ?? '', referenceTransactionInput ?? '')
    break
  }
  default:
    error = `Wanxiang hook runner received unknown hook kind: ${String(kind)}`
    break
}

if (error) {
  console.error(error)
  process.exit(1)
}
