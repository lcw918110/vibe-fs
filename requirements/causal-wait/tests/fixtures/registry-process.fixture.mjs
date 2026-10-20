import { readFileSync } from 'node:fs'
import { join } from 'node:path'
import * as causal from '../../../../dist/Execution/Session/Wait/Surface.js'

const [mode, workspace] = process.argv.slice(2)
const registry = causal.createRegistry()
if (mode === 'write') {
  causal.enter(registry, causal.createWait({
    waitKind: 'old-process-wait',
    owner: causal.owner('workflow', { id: 'old-owner' }),
    subject: { task: 'old-task' },
    producer: causal.externalProducer('external', { id: 'old-producer' }),
    escapes: [causal.escape('processLifetime')],
    source: 'old-process',
  }))
  await causal.writeSnapshot(workspace, registry)
}
const previous = JSON.parse(readFileSync(join(workspace, '.Herdchestra', 'diagnostics', 'causal-waits.json'), 'utf8'))
const current = causal.snapshot(registry)
process.stdout.write(JSON.stringify({ pid: process.pid, previousPid: previous.pid, previousActive: previous.active.length, active: current.active.length, history: current.history.length }))
