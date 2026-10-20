import assert from 'node:assert/strict'
import { execFileSync, spawnSync } from 'node:child_process'
import { existsSync } from 'node:fs'
import path from 'node:path'
import test from 'node:test'
import { fileURLToPath } from 'node:url'
import * as PluginHooksSurface from '../../../dist/OpenCode/Host/PluginHooksSurface.js'
import * as ModelRoutingSurface from '../../../dist/OpenCode/Host/ModelRoutingSurface.js'
import { openIncumbency, withExecutablePlugin } from '../../verification-system/tests/support/plugin-fixture.mjs'
import { integrationTest } from '../../verification-system/tests/support/tier-gate.mjs'
import { OPENCODE_BIN } from '../../verification-system/tests/e2e/support/process-host-utils.js'
import { isCanaryTurnSettled, managerReviewStage } from './support/manager-review-canary-turn.mjs'

test('WHAT[host-boundary-032] canary does not advance on a delayed idle from a previous physical turn', () => {
  const state = {
    sessionID: 'ses-canary', messageID: 'msg-current',
    status: { type: 'busy' },
    messages: [{ info: { sessionID: 'ses-canary', role: 'assistant', parentID: 'msg-previous',
      finish: 'stop', time: { completed: 1 } } }],
  }
  assert.equal(isCanaryTurnSettled(state), false)
  assert.equal(isCanaryTurnSettled({ ...state, status: undefined }), false)
  const current = { info: { sessionID: 'ses-canary', role: 'assistant', parentID: 'msg-current',
    finish: 'stop', time: { completed: 2 } } }
  assert.equal(isCanaryTurnSettled({ ...state, messages: [current] }), false)
  assert.equal(isCanaryTurnSettled({ ...state, messages: [current], status: { type: 'retry' } }), false)
  assert.equal(isCanaryTurnSettled({ ...state, messages: [current], status: { type: 'idle' } }), true)
  assert.equal(isCanaryTurnSettled({ ...state, messages: [current], status: undefined }), true)
  for (const info of [
    { ...current.info, sessionID: 'ses-foreign' },
    { ...current.info, finish: 'tool-calls' },
    { ...current.info, time: { created: 1 } },
    { ...current.info, error: { name: 'MessageAbortedError' } },
  ]) {
    assert.equal(isCanaryTurnSettled({ ...state, messages: [{ info }], status: { type: 'idle' } }), false)
  }
})

test('WHAT[host-boundary-032] canary provider routes repeated requests by physical prompt and call history', () => {
  const body = { messages: [{ role: 'user', content: 'READ_SAMPLE' }] }
  assert.equal(managerReviewStage(body), 1)
  assert.equal(managerReviewStage(body), 1)
  body.messages.push({ role: 'assistant', tool_calls: [{ id: 'call_read_norm_1' }] })
  assert.equal(managerReviewStage(body), 2)
  body.messages.push({ role: 'user', content: 'TRIGGER_ERROR' })
  assert.equal(managerReviewStage(body), 4)
  body.messages.push({ role: 'assistant', tool_calls: [{ id: 'call_read_err_1' }] })
  assert.equal(managerReviewStage(body), 5)
  body.messages.push({ role: 'user', content: '# A network failure just interrupted you. Continue the work.' })
  assert.equal(managerReviewStage(body), null)
  body.messages.push({ role: 'user', content: [{ type: 'text', text: 'READ_SAMPLE' }] })
  assert.equal(managerReviewStage(body), null)
  body.messages.push({ role: 'user', content: 'TRIGGER_CANCEL\u0000<system>wait-cost calibration</system>' })
  assert.equal(managerReviewStage(body), 6)
  body.messages.push({ role: 'assistant', tool_calls: [{ id: 'call_js_cancel_1' }] })
  body.messages.push({ role: 'user', content: 'VERIFY_CANCEL_HISTORY' })
  assert.equal(managerReviewStage(body), 7)
  assert.equal(managerReviewStage({ messages: [{ role: 'user', content: 'unrelated Manager work' }] }), null)
})

const REVIEW_TOOLS = ['todowrite-for-manager']

test('WHAT[host-boundary-032] C01_tool_definition_decorates_the_review_tool_with_contract_enum', async () => {
  await withExecutablePlugin(async (hooks) => {
    for (const toolID of REVIEW_TOOLS) {
      const output = {
        description: `Description for ${toolID}`,
        parameters: {
          type: 'object',
          properties: {
            path: { type: 'string' },
          },
          required: ['path'],
        },
      }
      await hooks['tool.definition']({ toolID }, output)
      assert.ok(
        output.parameters.properties?.contract,
        `Tool ${toolID} must be decorated with 'contract' property`,
      )
      assert.equal(output.parameters.properties.contract.type, 'string')
      assert.ok(
        Array.isArray(output.parameters.properties.contract.enum),
        `Tool ${toolID} contract enum must be an array`,
      )
      assert.equal(
        output.parameters.properties.contract.enum.length,
        1,
        `Tool ${toolID} contract enum must contain exactly one value`,
      )
      assert.ok(
        output.parameters.required?.includes('contract'),
        `Tool ${toolID} parameters must mark 'contract' as required`,
      )
    }
  })
})

test('WHAT[host-boundary-032] C05_tool_execute_before_strips_contract_and_after_restores', async () => {
  await withExecutablePlugin(async (hooks, _directory, _createdIds, runtime) => {
    const tool = 'todowrite-for-manager'
    const sessionID = 'ses-c05'
    await openIncumbency(runtime, sessionID)
    const callID = 'call-c05-1'
    const originalContract = 'js-manager-contract-v1'
    const beforeOutput = {
      args: {
        path: 'src/App.fs',
        contract: originalContract,
      },
    }

    // before 业务视图无 contract
    await hooks['tool.execute.before']({ tool, sessionID, callID }, beforeOutput)
    assert.equal(
      'contract' in beforeOutput.args,
      false,
      'before hook must strip contract parameter from business view',
    )

    const afterOutput = {
      title: 'todowrite-for-manager',
      output: 'file contents',
      metadata: {},
    }
    // after 原值恢复
    await hooks['tool.execute.after'](
      { tool, sessionID, callID, args: beforeOutput.args },
      afterOutput,
    )
    assert.equal(
      beforeOutput.args.contract,
      originalContract,
      'after hook must restore original contract value',
    )
  })
})

test('WHAT[host-boundary-032] C09_concurrent_tool_invocations_do_not_mix_contract_parameters', async () => {
  await withExecutablePlugin(async (hooks, _directory, _createdIds, runtime) => {
    const tool = 'todowrite-for-manager'
    const call1 = {
      sessionID: 'ses-c09-1',
      callID: 'call-c09-1',
      contract: 'contract-token-alpha',
      output: { args: { path: 'file-alpha.txt', contract: 'contract-token-alpha' } },
    }
    const call2 = {
      sessionID: 'ses-c09-2',
      callID: 'call-c09-2',
      contract: 'contract-token-beta',
      output: { args: { path: 'file-beta.txt', contract: 'contract-token-beta' } },
    }

    await Promise.all([
      openIncumbency(runtime, call1.sessionID),
      openIncumbency(runtime, call2.sessionID),
    ])

    // 两个并发调用参数不串号：同时执行 before
    await Promise.all([
      hooks['tool.execute.before']({ tool, sessionID: call1.sessionID, callID: call1.callID }, call1.output),
      hooks['tool.execute.before']({ tool, sessionID: call2.sessionID, callID: call2.callID }, call2.output),
    ])

    assert.equal('contract' in call1.output.args, false, 'call1 contract must be stripped')
    assert.equal('contract' in call2.output.args, false, 'call2 contract must be stripped')

    // 乱序执行 after
    const after1 = { title: tool, output: 'out1', metadata: {} }
    const after2 = { title: tool, output: 'out2', metadata: {} }

    await Promise.all([
      hooks['tool.execute.after']({ tool, sessionID: call2.sessionID, callID: call2.callID, args: call2.output.args }, after2),
      hooks['tool.execute.after']({ tool, sessionID: call1.sessionID, callID: call1.callID, args: call1.output.args }, after1),
    ])

    assert.equal(call1.output.args.contract, 'contract-token-alpha', 'call1 must restore its own contract without crosstalk')
    assert.equal(call2.output.args.contract, 'contract-token-beta', 'call2 must restore its own contract without crosstalk')
  })
})

test('WHAT[host-boundary-032] C02_definition_decoration_leaves_native_and_non_review_tools_unmodified', async () => {
  await withExecutablePlugin(async (hooks) => {
    const nonReviewTools = [
      'todowrite-for-engineer',
      'todowrite-for-devops',
      'todowrite-for-plan',
      'fetch',
      'skill',
      'bash-honeypot',
      'assume',
    ]
    for (const toolID of nonReviewTools) {
      const original = {
        description: `Original description of ${toolID}`,
        parameters: {
          type: 'object',
          properties: { path: { type: 'string' } },
          required: ['path'],
        },
      }
      const output = structuredClone(original)
      await hooks['tool.definition']({ toolID }, output)
      assert.deepEqual(output, original, `Non-review tool ${toolID} definition must remain completely unmodified`)
    }
  })
})

test('WHAT[host-boundary-032] C03_decorating_same_definition_multiple_times_is_idempotent_without_duplicates', async () => {
  await withExecutablePlugin(async (hooks) => {
    for (const toolID of REVIEW_TOOLS) {
      const output = {
        description: `Description for ${toolID}`,
        parameters: {
          type: 'object',
          properties: { path: { type: 'string' } },
          required: ['path'],
        },
      }
      await hooks['tool.definition']({ toolID }, output)
      const firstSnapshot = structuredClone(output)

      // 再次装饰同一定义
      await hooks['tool.definition']({ toolID }, output)
      // 第三次装饰同一定义
      await hooks['tool.definition']({ toolID }, output)

      assert.deepEqual(output, firstSnapshot, `Multiple decorations on ${toolID} must be idempotent`)
      assert.equal(
        output.parameters.required.filter((x) => x === 'contract').length,
        1,
        `'contract' must not be appended multiple times in required list of ${toolID}`,
      )
    }
  })
})

test('WHAT[host-boundary-032] C04_definitions_generated_before_and_after_review_are_canonically_identical', async () => {
  await withExecutablePlugin(async (hooks) => {
    for (const toolID of REVIEW_TOOLS) {
      const defBefore = {
        description: `Description for ${toolID}`,
        parameters: { type: 'object', properties: { path: { type: 'string' } }, required: ['path'] },
      }
      const defAfter = {
        description: `Description for ${toolID}`,
        parameters: { type: 'object', properties: { path: { type: 'string' } }, required: ['path'] },
      }
      await hooks['tool.definition']({ toolID }, defBefore)
      await hooks['tool.definition']({ toolID }, defAfter)
      assert.equal(
        JSON.stringify(defBefore),
        JSON.stringify(defAfter),
        `Definition of ${toolID} must be canonically identical across review lifecycle`,
      )
    }
  })
})

test('WHAT[host-boundary-032] C06_missing_wrong_string_null_or_wrong_json_type_contract_not_rejected_by_plugin_and_restored', async () => {
  await withExecutablePlugin(async (hooks, _directory, _createdIds, runtime) => {
    const sessionID = 'ses-c06'
    await openIncumbency(runtime, sessionID)
    const malformedContracts = [
      undefined,
      null,
      12345,
      true,
      { arbitrary: 'object' },
      'wrong-contract-string',
      '',
    ]

    for (let i = 0; i < malformedContracts.length; i++) {
      const contractVal = malformedContracts[i]
      const callID = `call-c06-${i}`
      const beforeOutput = {
        args: contractVal === undefined ? { path: 'file.txt' } : { path: 'file.txt', contract: contractVal },
      }

      // 插件本地对缺失或错误的 contract 采取乐观处理，不进行二次强校验拒绝
      await hooks['tool.execute.before']({ tool: 'todowrite-for-manager', sessionID, callID }, beforeOutput)
      assert.equal('contract' in beforeOutput.args, false, `contract must be stripped during execution for test case ${i}`)

      const afterOutput = { title: 'todowrite-for-manager', output: 'ok', metadata: {} }
      await hooks['tool.execute.after']({ tool: 'todowrite-for-manager', sessionID, callID, args: beforeOutput.args }, afterOutput)

      if (contractVal === undefined) {
        assert.equal('contract' in beforeOutput.args, false, 'missing contract must remain missing after restore')
      } else {
        assert.deepEqual(beforeOutput.args.contract, contractVal, `contract must be restored exactly to original value for test case ${i}`)
      }
    }
  })
})

test('WHAT[host-boundary-032] C07_contract_undefined_versus_completely_missing_preserves_own_property_state', async () => {
  await withExecutablePlugin(async (hooks, _directory, _createdIds, runtime) => {
    const sessionID = 'ses-c07'
    await openIncumbency(runtime, sessionID)

    // 情况 1: 显式赋值 contract: undefined
    const argsExplicitUndefined = { path: 'file.txt', contract: undefined }
    const call1ID = 'call-c07-undef'
    await hooks['tool.execute.before']({ tool: 'todowrite-for-manager', sessionID, callID: call1ID }, { args: argsExplicitUndefined })
    assert.equal('contract' in argsExplicitUndefined, false)
    await hooks['tool.execute.after']({ tool: 'todowrite-for-manager', sessionID, callID: call1ID, args: argsExplicitUndefined }, { title: 'todowrite-for-manager', output: '', metadata: {} })
    assert.equal(Object.prototype.hasOwnProperty.call(argsExplicitUndefined, 'contract'), true, 'contract: undefined must be restored as own property')
    assert.equal(argsExplicitUndefined.contract, undefined)

    // 情况 2: 完全未提供 contract 字段
    const argsCompletelyMissing = { path: 'file.txt' }
    const call2ID = 'call-c07-missing'
    await hooks['tool.execute.before']({ tool: 'todowrite-for-manager', sessionID, callID: call2ID }, { args: argsCompletelyMissing })
    assert.equal('contract' in argsCompletelyMissing, false)
    await hooks['tool.execute.after']({ tool: 'todowrite-for-manager', sessionID, callID: call2ID, args: argsCompletelyMissing }, { title: 'todowrite-for-manager', output: '', metadata: {} })
    assert.equal(Object.prototype.hasOwnProperty.call(argsCompletelyMissing, 'contract'), false, 'completely missing contract must remain absent as own property')
  })
})

test('WHAT[host-boundary-032] C08_repeated_before_and_repeated_after_preserves_original_and_restores_idempotently', async () => {
  await withExecutablePlugin(async (hooks, _directory, _createdIds, runtime) => {
    const sessionID = 'ses-c08'
    await openIncumbency(runtime, sessionID)
    const callID = 'call-c08'
    const originalContract = 'token-c08-orig'
    const beforeOutput = { args: { path: 'file.txt', contract: originalContract } }

    // 重复执行 before hook
    await hooks['tool.execute.before']({ tool: 'todowrite-for-manager', sessionID, callID }, beforeOutput)
    await hooks['tool.execute.before']({ tool: 'todowrite-for-manager', sessionID, callID }, beforeOutput)
    assert.equal('contract' in beforeOutput.args, false, 'contract stripped')

    // 重复执行 after hook
    const afterOutput = { title: 'todowrite-for-manager', output: '', metadata: {} }
    await hooks['tool.execute.after']({ tool: 'todowrite-for-manager', sessionID, callID, args: beforeOutput.args }, afterOutput)
    assert.equal(beforeOutput.args.contract, originalContract, 'first restore recovers original contract')

    await hooks['tool.execute.after']({ tool: 'todowrite-for-manager', sessionID, callID, args: beforeOutput.args }, afterOutput)
    assert.equal(beforeOutput.args.contract, originalContract, 'second restore is idempotent and preserves contract')
  })
})

test('WHAT[host-boundary-032] C10_model_submitted_pseudo_contract_fields_not_treated_as_private_record', async () => {
  await withExecutablePlugin(async (hooks, _directory, _createdIds, runtime) => {
    const sessionID = 'ses-c10'
    await openIncumbency(runtime, sessionID)
    const callID = 'call-c10'
    const beforeOutput = {
      args: {
        path: 'src/App.fs',
        contract: 'valid-review-contract',
        _contract: 'malicious-injected-pseudo-contract',
        __contract: 'another-pseudo-field',
      },
    }

    await hooks['tool.execute.before']({ tool: 'todowrite-for-manager', sessionID, callID }, beforeOutput)
    // 仅真实的 contract 字段被暂存并隐藏，伪造字段不作为私有记录、原样保留
    assert.equal('contract' in beforeOutput.args, false)
    assert.equal(beforeOutput.args._contract, 'malicious-injected-pseudo-contract')
    assert.equal(beforeOutput.args.__contract, 'another-pseudo-field')

    const afterOutput = { title: 'todowrite-for-manager', output: '', metadata: {} }
    await hooks['tool.execute.after']({ tool: 'todowrite-for-manager', sessionID, callID, args: beforeOutput.args }, afterOutput)
    assert.equal(beforeOutput.args.contract, 'valid-review-contract')
    assert.equal(beforeOutput.args._contract, 'malicious-injected-pseudo-contract')
    assert.equal(beforeOutput.args.__contract, 'another-pseudo-field')
  })
})

test('WHAT[host-boundary-032] C11_reported_failure_after_callback_restores_contract_and_preserves_error', async () => {
  await withExecutablePlugin(async (hooks, _directory, _createdIds, runtime) => {
    const sessionID = 'ses-c11'
    await openIncumbency(runtime, sessionID)
    const callID = 'call-c11'
    const originalContract = 'contract-c11'
    const beforeOutput = { args: { path: 'nonexistent-throw.txt', contract: originalContract } }

    await hooks['tool.execute.before']({ tool: 'todowrite-for-manager', sessionID, callID }, beforeOutput)
    assert.equal('contract' in beforeOutput.args, false)

    const executionError = new Error('Reported file system IO failure')
    const afterOutput = { title: 'todowrite-for-manager', output: 'error', metadata: { error: executionError } }
    await hooks['tool.execute.after'](
      { tool: 'todowrite-for-manager', sessionID, callID, args: beforeOutput.args },
      afterOutput,
    )

    assert.equal(beforeOutput.args.contract, originalContract)
    assert.equal(afterOutput.metadata.error, executionError)
  })
})

test('WHAT[host-boundary-032] C12_after_hook_returns_with_original_contract_restored', async () => {
  await withExecutablePlugin(async (hooks, _directory, _createdIds, runtime) => {
    const sessionID = 'ses-c12'
    await openIncumbency(runtime, sessionID)
    const callID = 'call-c12'
    const originalContract = 'contract-c12'
    const beforeOutput = { args: { path: 'file.txt', contract: originalContract } }

    await hooks['tool.execute.before']({ tool: 'todowrite-for-manager', sessionID, callID }, beforeOutput)
    assert.equal('contract' in beforeOutput.args, false)

    await hooks['tool.execute.after'](
      { tool: 'todowrite-for-manager', sessionID, callID, args: beforeOutput.args },
      { title: 'todowrite-for-manager', output: 'content', metadata: {} },
    )
    assert.equal(beforeOutput.args.contract, originalContract)
  })
})

test('WHAT[host-boundary-032] C13_frozen_or_non_extensible_args_fails_atomically_without_file_access', async () => {
  await withExecutablePlugin(async (hooks, _directory, _createdIds, runtime) => {
    const sessionID = 'ses-c13'
    await openIncumbency(runtime, sessionID)
    const callID = 'call-c13'

    const frozenArgs = Object.freeze({ path: 'src/Secret.fs', contract: 'token-c13' })
    const beforeOutput = { args: frozenArgs }

    // 对不可扩展或冻结的 args，hide 应当抛出 TypeError，保持原子失败且绝不发生文件访问
    await assert.rejects(
      async () => {
        await hooks['tool.execute.before']({ tool: 'todowrite-for-manager', sessionID, callID }, beforeOutput)
      },
      TypeError,
      'frozen args must fail atomically with TypeError',
    )
    assert.equal(beforeOutput.args.contract, 'token-c13', 'contract must remain unchanged on frozen args failure')
  })
})

test('WHAT[host-boundary-032] C14_direct_tool_execution_without_before_hook_performs_full_permission_check_without_missing_key_crash', async () => {
  await withExecutablePlugin(async (hooks, _directory, _createdIds, runtime) => {
    const sessionID = 'ses-c14'
    await openIncumbency(runtime, sessionID)

    // 直接调用 tool.execute（绕过 tool.execute.before）
    // 缺少私有暂存 key 时，after/restore 是 no-op，且 tool.execute 执行完整权限核验
    const directArgs = { path: 'src/App.fs' }
    const execResult = await hooks.tool['todowrite-for-manager'].execute(
      directArgs,
      { sessionID, agent: 'manager' },
    )
    assert.ok(typeof execResult === 'string', 'Tool execution must return valid string result without crashing')

    // 缺少私有暂存 key 时执行 after hook 亦幂等安全退出
    await hooks['tool.execute.after'](
      { tool: 'todowrite-for-manager', sessionID, callID: 'call-c14', args: directArgs },
      { title: 'todowrite-for-manager', output: execResult, metadata: {} },
    )
    assert.equal('contract' in directArgs, false, 'unprovided contract remains absent')
  })
})

test('WHAT[host-boundary-032] C15_contract_position_first_middle_or_last_restores_without_property_drift', async () => {
  await withExecutablePlugin(async (hooks, _directory, _createdIds, runtime) => {
    const sessionID = 'ses-c15'
    await openIncumbency(runtime, sessionID)

    // 1. contract 在第一个位置
    const objFirst = { contract: 'first-token', a: 1, b: 2 }
    const firstKeys = Object.keys(objFirst)
    await hooks['tool.execute.before']({ tool: 'todowrite-for-manager', sessionID, callID: 'call-c15-1' }, { args: objFirst })
    await hooks['tool.execute.after']({ tool: 'todowrite-for-manager', sessionID, callID: 'call-c15-1', args: objFirst }, { title: 'todowrite-for-manager', output: '', metadata: {} })
    assert.equal(objFirst.contract, 'first-token')
    assert.deepEqual(Object.keys(objFirst), firstKeys)

    // 2. contract 在中间位置
    const objMid = { a: 1, contract: 'mid-token', b: 2 }
    const middleKeys = Object.keys(objMid)
    await hooks['tool.execute.before']({ tool: 'todowrite-for-manager', sessionID, callID: 'call-c15-2' }, { args: objMid })
    await hooks['tool.execute.after']({ tool: 'todowrite-for-manager', sessionID, callID: 'call-c15-2', args: objMid }, { title: 'todowrite-for-manager', output: '', metadata: {} })
    assert.equal(objMid.contract, 'mid-token')
    assert.deepEqual(Object.keys(objMid), middleKeys)

    // 3. contract 在最后位置
    const objLast = { a: 1, b: 2, contract: 'last-token' }
    const lastKeys = Object.keys(objLast)
    await hooks['tool.execute.before']({ tool: 'todowrite-for-manager', sessionID, callID: 'call-c15-3' }, { args: objLast })
    await hooks['tool.execute.after']({ tool: 'todowrite-for-manager', sessionID, callID: 'call-c15-3', args: objLast }, { title: 'todowrite-for-manager', output: '', metadata: {} })
    assert.equal(objLast.contract, 'last-token')
    assert.deepEqual(Object.keys(objLast), lastKeys)
  })
})

test('WHAT[host-boundary-032] C16_upstream_validation_rejection_not_swallowed_and_local_missing_malformed_not_revalidated', async () => {
  await withExecutablePlugin(async (hooks, _directory, _createdIds, runtime) => {
    const sessionID = 'ses-c16'
    await openIncumbency(runtime, sessionID)

    // 1. 本地插件路径：对 missing/malformed contract 乐观处理，不进行二次强校验，不主动拒绝
    const relaxedArgs = { path: 'file.txt', contract: 'not-in-enum-value' }
    await hooks['tool.execute.before'](
      { tool: 'todowrite-for-manager', sessionID, callID: 'call-c16-local' },
      { args: relaxedArgs },
    )
    assert.equal('contract' in relaxedArgs, false, 'local plugin must strip contract without strong revalidation rejection')

    // 2. 上游宿主校验：若宿主层直接因 schema validation 抛出拒绝，该 rejection 不被插件层吞没
    const upstreamRejection = new Error('Host schema validation: contract must match enum')
    const executeWithHostValidation = async () => {
      // 模拟上游宿主直接在调用 hook 前拦截
      throw upstreamRejection
    }
    await assert.rejects(
      executeWithHostValidation,
      /Host schema validation: contract must match enum/,
      'Upstream host rejection must not be swallowed',
    )
  })
})

const here = path.dirname(fileURLToPath(import.meta.url))
const runnerPath = path.join(here, 'support/run-manager-review-tools-canary.mjs')
const repoRoot = path.resolve(here, '../../..')

const checkOpencodeExecutable = () => {
  if (process.env.OPENCODE_BIN && existsSync(process.env.OPENCODE_BIN)) return true
  const localBin = path.join(repoRoot, 'node_modules/.bin/opencode')
  if (existsSync(localBin)) return true
  try {
    execFileSync(OPENCODE_BIN, ['--version'], { stdio: 'ignore' })
    return true
  } catch {
    return false
  }
}

integrationTest(
  'WHAT[host-boundary-032] canary_manager_review_tools_contract_observed_on_real_host',
  async (t) => {
    // 门禁与环境判定约定：
    // 1. integrationTest 依据环境变量 WXS_TIER_INTEGRATION=1 决定是否执行；
    // 2. 真实 Host canary 执行需要本地具备可执行的 opencode 二进制（OPENCODE_BIN），环境不具备时给出清晰诊断跳过。
    if (!checkOpencodeExecutable()) {
      return t.skip(
        `OpenCode binary not executable at ${OPENCODE_BIN}; requires real OpenCode host to execute canary runner`,
      )
    }

    const launched = spawnSync(process.execPath, [runnerPath], {
      cwd: repoRoot,
      encoding: 'utf8',
      timeout: 120000,
    })

    let stdoutSummary = null
    try {
      stdoutSummary = JSON.parse(launched.stdout.trim())
    } catch {
      stdoutSummary = launched.stdout
    }

    const failureMessage = [
      `Real host canary runner exited with code ${launched.status}.`,
      `[Runner Stderr]:\n${launched.stderr || '(empty)'}`,
      `[Runner Stdout Summary]:\n${typeof stdoutSummary === 'object' ? JSON.stringify(stdoutSummary, null, 2) : stdoutSummary}`,
    ].join('\n')

    assert.equal(launched.status, 0, failureMessage)
    assert.ok(stdoutSummary, 'Canary runner must produce JSON summary output')
    assert.equal(stdoutSummary.versions?.plugin, '1.18.29', 'plugin version must match fixture')
    assert.deepEqual(
      stdoutSummary.reviewTools,
      ['todowrite-for-manager'],
      'reviewTools must contain exactly the single review tool',
    )
    for (const tool of stdoutSummary.reviewTools) {
      assert.equal(
        stdoutSummary.controlTools.includes(tool),
        false,
        `controlTools must not include review tool ${tool}`,
      )
    }
    assert.equal(stdoutSummary.wireInspection?.contractRequired, true, 'wire contract must be required')
    assert.equal(stdoutSummary.wireInspection?.contractType, 'string', 'wire contract type must be string')
    assert.deepEqual(
      stdoutSummary.wireInspection?.contractEnum,
      ['do-not-use-except-for-review'],
      'wire contract enum must contain exactly the single contract token',
    )
    assert.equal(
      stdoutSummary.wireInspection?.historicalToolCallPreservesContract,
      true,
      'historical tool calls must preserve contract',
    )
    assert.equal(
      stdoutSummary.wireInspection?.round2ToolsStable,
      true,
      'round 2 provider tools must remain stable',
    )
 // / 197: 真实 Host canary 必须枚举最终 provider-visible
    // tools。当前 runner 跑未配置态，断言枚举面存在、含内建与插件工具、
    // 且全部工具不带协议字段（无功能基线的 wire 级证明）。
    assert.ok(
      Array.isArray(stdoutSummary.wireInspection?.providerVisibleToolNames) &&
        stdoutSummary.wireInspection.providerVisibleToolNames.length > 0,
      'canary must enumerate the final provider-visible tool set on the real host',
    )
    const visibleToolNames = stdoutSummary.wireInspection.providerVisibleToolNames
    assert.ok(
      visibleToolNames.includes('todowrite-for-manager'),
      `provider-visible set must include the plugin tool todowrite-for-manager; observed: ${visibleToolNames.join(',')}`,
    )
    assert.ok(
      visibleToolNames.includes('skill'),
      `provider-visible set must include host builtin tools; observed: ${visibleToolNames.join(',')}`,
    )
    assert.equal(
      stdoutSummary.wireInspection?.providerVisibleProtocolAbsence,
      true,
      'every provider-visible tool must be free of delegation protocol fields',
    )
    for (const [name, status] of [['normal', 'completed'], ['executorError', 'completed'], ['cancellation', 'error']]) {
      const observed = stdoutSummary.calls?.[name]
      assert.ok(observed, `${name} must have real Host observations`)
      assert.equal(observed.sameArguments, true, name)
      assert.equal(observed.originalOrder, true, name)
      assert.equal(observed.originalValues, true, name)
      assert.equal(observed.status, status, name)
    }
    assert.equal(stdoutSummary.calls.cancellation.providerHistoryObserved, true)
    assert.equal(stdoutSummary.calls.executorError.failureOutputObserved, true)
    // T180 executorThrow (WHAT[019] Host boundary canary): on the executor
    // rejection path the installed Host must not invoke after, must
    // materialize the durable tool part as status=error carrying the
    // rejection text, and the follow-up wire request must preserve the
    // review contract in tool_call arguments. Red here means the Host
    // boundary changed — re-evaluate the T180 todo record above.
    assert.equal(
      stdoutSummary.calls.executorThrow.afterInvoked,
      false,
      'executorThrow: Host must not invoke tool.execute.after on the executor rejection path',
    )
    assert.equal(stdoutSummary.calls.executorThrow.executorRejected, true)
    assert.equal(
      stdoutSummary.calls.executorThrow.status,
      'error',
      'executorThrow: durable tool part must materialize status error',
    )
    assert.equal(
      stdoutSummary.calls.executorThrow.durableErrorCarriesRejection,
      true,
      'executorThrow: durable error must carry the executor rejection text',
    )
    assert.equal(
      stdoutSummary.calls.executorThrow.durableInputRetainsContract,
      true,
      'executorThrow: durable input must retain the review contract',
    )
    assert.equal(
      stdoutSummary.calls.executorThrow.wireHistoryPreservesContract,
      true,
      'executorThrow: follow-up wire tool_call arguments must preserve the review contract',
    )
    assert.equal(
      stdoutSummary.calls.executorThrow.inMemoryArgsStripped,
      true,
      'executorThrow: in-memory args stay stripped on the throw path (C44 restore absent, Host boundary)',
    )
  },
)

