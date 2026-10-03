import assert from 'node:assert/strict'
import test from 'node:test'

const {
  describeRun,
  run: executeRun,
  runToolName,
} = await import('../../../dist/OpenCode/Tools/ExecutorToolSurface.js')

const { contextDecode, contextView } = await import('../../../dist/OpenCode/Codec/ToolHostSurface.js')

const chain = (kind, extra = {}) => ({
  kind,
  ...extra,
  describe: () => chain(`${kind}-described`, extra),
  optional: () => chain(`${kind}-optional`, extra),
})

const fakeSchema = {
  string: () => chain('string'),
  number: () => chain('number'),
  enum: (values) => chain('enum', { values }),
  boolean: () => chain('boolean'),
}

const toolModule = { tool: { schema: fakeSchema } }

const context = (sessionID = 'ses-exec') => ({ sessionID })

const run = (args, ctx = context(), recovery = '') =>
  executeRun(toolModule, {}, args, ctx, recovery)

const parseToml = (text) =>
  Object.fromEntries(
    text
      .split('\n')
      .filter((line) => /^[a-z_0-9]+ = /.test(line))
      .map((line) => {
        const [name, ...rest] = line.split(' = ')
        const raw = rest.join(' = ')
        return [name, raw.startsWith('"') ? JSON.parse(raw) : raw]
      }),
  )

const SPOOL_COMMAND = "printf 'abcdefghijklmnopqrstuvwxyz0123456789'"

const SPOOL_BUDGET = { command: SPOOL_COMMAND, output_budget_bytes: 4 }

test('WHAT[process-execution-011] RUN_surface_names_the_provider_execution_verb', () => {
  assert.equal(runToolName, 'run')
  const tool = describeRun(toolModule)
  assert.equal(tool.name, 'run')
  assert.match(tool.description, /deadline_seconds (?:and|与) output_budget_bytes/)
  assert.deepEqual(tool.arguments, ['command', 'deadline_seconds', 'output_budget_bytes', 'world_lock'])
})

test('WHAT[process-execution-011] RUN_host_context_codec_exposes_plain_snapshot', () => {
  const decoded = contextDecode({ sessionID: 'ses-exec', agent: 'devops' })
  assert.deepEqual(contextView(decoded), {
    sessionId: 'ses-exec',
    agent: 'devops',
    toolCallId: null,
    providerRunId: null,
    promptText: null,
  })
})

test('WHAT[process-execution-011] RUN_missing_command_is_rejected_before_spawn', async () => {
  const result = await run({})
  assert.doesNotMatch(result, /\berror\s*=/)
  assert.match(result, /# (?:Missing command|缺少 command)/)
})

test('WHAT[process-execution-011] RUN_blank_command_is_rejected_before_spawn', async () => {
  const result = await run({ command: '   ' })
  assert.doesNotMatch(result, /\berror\s*=/)
  assert.match(result, /# (?:Missing command|缺少 command)/)
})

test('WHAT[process-execution-011] RUN_blank_session_surfaces_natural_execution_consequence_before_spawn', async () => {
  const result = await run({ command: 'true' }, context(''))
  assert.doesNotMatch(result, /sessionID|\berror\s*=/i)
  assert.match(result, /(?:cannot run from this execution context|无法在此执行上下文中运行)/i)
})

test('WHAT[process-execution-011] RUN_deadline_overrun_returns_the_fixed_timeout_consequence', async () => {
  const result = await run({ command: 'sleep 5', deadline_seconds: 0.01, output_budget_bytes: 16 })
  assert.doesNotMatch(result, /TimeoutExceeded|\berror\s*=/)
  assert.match(result, /(?:Termination was requested|已请求终止|The command was still running when its allowed time ended, so it was stopped\.|command 在允许时间结束时仍在运行，因此已被停止。)/)
})

test('WHAT[process-execution-011] RUN_world_lock_is_accepted', async () => {
  const result = parseToml(await run({ command: "printf 'ok'", world_lock: true }))
  assert.equal(result.exit_code, '0')
  assert.equal(result.stdout, 'ok')
})

test('WHAT[process-execution-011] RUN_spooled_request_without_authority_fails_before_execution_without_identity_leak', async () => {
  const result = await run(SPOOL_BUDGET, context(''))
  assert.doesNotMatch(result, /sessionID|\berror\s*=/i)
  assert.match(result, /(?:cannot run from this execution context|无法在此执行上下文中运行)/i)
})

test('WHAT[process-execution-011] RUN_spooled_output_family_blocked_surfaces_recovery_consequence', async () => {
  const result = await run(SPOOL_BUDGET, context(), 'blocked')
  assert.doesNotMatch(result, /RECOVERY_BLOCKED|\berror\s*=/)
  assert.match(result, /(?:large output cannot be reconciled while recovery is blocked|恢复受阻期间无法调和其大额输出)/i)
})

{
  const { run, queryShell, describe, nodeCommand } = await import('./support/executor.mjs')
  const { parse } = await import('smol-toml')
  const { mkdtempSync, existsSync, rmSync } = await import('node:fs')
  const { tmpdir } = await import('node:os')
  const { join } = await import('node:path')

  test('WHAT[process-execution-011] run and query-shell expose equal parameters and actually execute bounded output', { todo: 'GAP-091: query-shell still exists in code but its provider resources and active office were retired' }, async () => {
    const metadata = describe()
    assert.equal(metadata.queryShell.name, 'query-shell')
    assert.deepEqual(metadata.queryShell.arguments, metadata.run.arguments)
    for (const execute of [run, queryShell]) {
      const result = parse(await execute({ command: nodeCommand("process.stdout.write('same'); process.exitCode = 7"), deadline_seconds: 1, output_budget_bytes: 1024, world_lock: false }))
      assert.equal(result.exit_code, 7)
      assert.equal(result.stdout, 'same')
      assert.equal(result.stderr, '')
    }
  })

  const rejectsBeforeSpawn = async execute => {
    const dir = mkdtempSync(join(tmpdir(), 'wxs-pre-spawn-'))
    const marker = join(dir, 'spawned')
    const command = nodeCommand(`require('node:fs').writeFileSync(${JSON.stringify(marker)}, 'ran')`)
    try {
      for (const invalid of [{ deadline_seconds: 0 }, { deadline_seconds: Infinity }, { output_budget_bytes: -1 }, { output_budget_bytes: 0.5 }]) {
        const result = await execute({ command, ...invalid })
        assert.match(result, /(?:must|必须)/)
        assert.equal(existsSync(marker), false)
      }
    } finally {
      rmSync(dir, { recursive: true, force: true })
    }
  }
  test('WHAT[process-execution-011] run rejects invalid budgets before command side effects', () => rejectsBeforeSpawn(run))
  test('WHAT[process-execution-011] query-shell rejects invalid budgets before command side effects', { todo: 'GAP-091: missing retired query-shell resources prevent reaching validation' }, () => rejectsBeforeSpawn(queryShell))
}
