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

const REVIEW_TOOLS = ['js-manager']

test('WHAT[host-boundary-032] C01_tool_definition_decorates_four_dedicated_tools_with_contract_enum', async () => {
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
      // WHAT[host-boundary-032]: definition 装饰四专用工具后 contract 为 required string 且 enum 恰一个值
      // 当前生产代码中 toolDefinition 是空实现 () => {}，输出对象未被装饰，此处必将失败飘红。
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
    const tool = 'js-manager'
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
      title: 'js-manager',
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
    const tool = 'js-manager'
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
      'read',
      'write',
      'edit',
      'glob',
      'grep',
      'js-engineer',
      'js-devops',
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
      await hooks['tool.execute.before']({ tool: 'js-manager', sessionID, callID }, beforeOutput)
      assert.equal('contract' in beforeOutput.args, false, `contract must be stripped during execution for test case ${i}`)

      const afterOutput = { title: 'js-manager', output: 'ok', metadata: {} }
      await hooks['tool.execute.after']({ tool: 'js-manager', sessionID, callID, args: beforeOutput.args }, afterOutput)

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
    await hooks['tool.execute.before']({ tool: 'js-manager', sessionID, callID: call1ID }, { args: argsExplicitUndefined })
    assert.equal('contract' in argsExplicitUndefined, false)
    await hooks['tool.execute.after']({ tool: 'js-manager', sessionID, callID: call1ID, args: argsExplicitUndefined }, { title: 'js-manager', output: '', metadata: {} })
    assert.equal(Object.prototype.hasOwnProperty.call(argsExplicitUndefined, 'contract'), true, 'contract: undefined must be restored as own property')
    assert.equal(argsExplicitUndefined.contract, undefined)

    // 情况 2: 完全未提供 contract 字段
    const argsCompletelyMissing = { path: 'file.txt' }
    const call2ID = 'call-c07-missing'
    await hooks['tool.execute.before']({ tool: 'js-manager', sessionID, callID: call2ID }, { args: argsCompletelyMissing })
    assert.equal('contract' in argsCompletelyMissing, false)
    await hooks['tool.execute.after']({ tool: 'js-manager', sessionID, callID: call2ID, args: argsCompletelyMissing }, { title: 'js-manager', output: '', metadata: {} })
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
    await hooks['tool.execute.before']({ tool: 'js-manager', sessionID, callID }, beforeOutput)
    await hooks['tool.execute.before']({ tool: 'js-manager', sessionID, callID }, beforeOutput)
    assert.equal('contract' in beforeOutput.args, false, 'contract stripped')

    // 重复执行 after hook
    const afterOutput = { title: 'js-manager', output: '', metadata: {} }
    await hooks['tool.execute.after']({ tool: 'js-manager', sessionID, callID, args: beforeOutput.args }, afterOutput)
    assert.equal(beforeOutput.args.contract, originalContract, 'first restore recovers original contract')

    await hooks['tool.execute.after']({ tool: 'js-manager', sessionID, callID, args: beforeOutput.args }, afterOutput)
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

    await hooks['tool.execute.before']({ tool: 'js-manager', sessionID, callID }, beforeOutput)
    // 仅真实的 contract 字段被暂存并隐藏，伪造字段不作为私有记录、原样保留
    assert.equal('contract' in beforeOutput.args, false)
    assert.equal(beforeOutput.args._contract, 'malicious-injected-pseudo-contract')
    assert.equal(beforeOutput.args.__contract, 'another-pseudo-field')

    const afterOutput = { title: 'js-manager', output: '', metadata: {} }
    await hooks['tool.execute.after']({ tool: 'js-manager', sessionID, callID, args: beforeOutput.args }, afterOutput)
    assert.equal(beforeOutput.args.contract, 'valid-review-contract')
    assert.equal(beforeOutput.args._contract, 'malicious-injected-pseudo-contract')
    assert.equal(beforeOutput.args.__contract, 'another-pseudo-field')
  })
})

test('WHAT[host-boundary-032] C11_business_execution_failure_or_rejection_restores_contract_and_preserves_error', async () => {
  await withExecutablePlugin(async (hooks, _directory, _createdIds, runtime) => {
    const sessionID = 'ses-c11'
    await openIncumbency(runtime, sessionID)
    const callID = 'call-c11'
    const originalContract = 'contract-c11'
    const beforeOutput = { args: { path: 'nonexistent-throw.txt', contract: originalContract } }

    await hooks['tool.execute.before']({ tool: 'js-manager', sessionID, callID }, beforeOutput)
    assert.equal('contract' in beforeOutput.args, false)

    // 模拟业务执行失败 / 抛出异常
    const executionError = new Error('Simulated file system IO failure')
    try {
      throw executionError
    } catch (err) {
      // 宿主在 try...finally 或 catch 中必须同源触发 after hook 进行清理恢复
      await hooks['tool.execute.after'](
        { tool: 'js-manager', sessionID, callID, args: beforeOutput.args },
        { title: 'js-manager', output: 'error', metadata: { error: err } },
      )
    }

    assert.equal(beforeOutput.args.contract, originalContract, 'contract must be safely restored upon business execution failure')
  })
})

test('WHAT[host-boundary-032] C12_after_hook_grounding_failure_does_not_affect_already_restored_args', async () => {
  await withExecutablePlugin(async (hooks, _directory, _createdIds, runtime) => {
    const sessionID = 'ses-c12'
    await openIncumbency(runtime, sessionID)
    const callID = 'call-c12'
    const originalContract = 'contract-c12'
    const beforeOutput = { args: { path: 'file.txt', contract: originalContract } }

    await hooks['tool.execute.before']({ tool: 'js-manager', sessionID, callID }, beforeOutput)
    assert.equal('contract' in beforeOutput.args, false)

    // after 回调中，首先完成 contract 恢复；即便后续 downstream 审计或观察报错，参数恢复不被破坏
    await hooks['tool.execute.after'](
      { tool: 'js-manager', sessionID, callID, args: beforeOutput.args },
      { title: 'js-manager', output: 'content', metadata: {} },
    )
    assert.equal(beforeOutput.args.contract, originalContract, 'args.contract must be intact regardless of subsequent observation outcome')
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
        await hooks['tool.execute.before']({ tool: 'js-manager', sessionID, callID }, beforeOutput)
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
    const execResult = await hooks.tool['js-manager'].execute(
      directArgs,
      { sessionID, agent: 'manager' },
    )
    assert.ok(typeof execResult === 'string', 'Tool execution must return valid string result without crashing')

    // 缺少私有暂存 key 时执行 after hook 亦幂等安全退出
    await hooks['tool.execute.after'](
      { tool: 'js-manager', sessionID, callID: 'call-c14', args: directArgs },
      { title: 'js-manager', output: execResult, metadata: {} },
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
    await hooks['tool.execute.before']({ tool: 'js-manager', sessionID, callID: 'call-c15-1' }, { args: objFirst })
    await hooks['tool.execute.after']({ tool: 'js-manager', sessionID, callID: 'call-c15-1', args: objFirst }, { title: 'js-manager', output: '', metadata: {} })
    assert.equal(objFirst.contract, 'first-token')

    // 2. contract 在中间位置
    const objMid = { a: 1, contract: 'mid-token', b: 2 }
    await hooks['tool.execute.before']({ tool: 'js-manager', sessionID, callID: 'call-c15-2' }, { args: objMid })
    await hooks['tool.execute.after']({ tool: 'js-manager', sessionID, callID: 'call-c15-2', args: objMid }, { title: 'js-manager', output: '', metadata: {} })
    assert.equal(objMid.contract, 'mid-token')

    // 3. contract 在最后位置
    const objLast = { a: 1, b: 2, contract: 'last-token' }
    await hooks['tool.execute.before']({ tool: 'js-manager', sessionID, callID: 'call-c15-3' }, { args: objLast })
    await hooks['tool.execute.after']({ tool: 'js-manager', sessionID, callID: 'call-c15-3', args: objLast }, { title: 'js-manager', output: '', metadata: {} })
    assert.equal(objLast.contract, 'last-token')
  })
})

test('WHAT[host-boundary-032] C16_upstream_validation_rejection_not_swallowed_and_local_missing_malformed_not_revalidated', async () => {
  await withExecutablePlugin(async (hooks, _directory, _createdIds, runtime) => {
    const sessionID = 'ses-c16'
    await openIncumbency(runtime, sessionID)

    // 1. 本地插件路径：对 missing/malformed contract 乐观处理，不进行二次强校验，不主动拒绝
    const relaxedArgs = { path: 'file.txt', contract: 'not-in-enum-value' }
    await hooks['tool.execute.before'](
      { tool: 'js-manager', sessionID, callID: 'call-c16-local' },
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

// ---------------------------------------------------------------------------
// /4.2/4.3: explicit read-only delegation protocol.
// Production tool.definition decoration is gated behind the Predictor
// configuration existence query (ModelRouting, ); the schema
// contract itself is proven through the same registered contract function
// (PluginHooksSurface.decorateReadonlyDelegationToolDefinition), while the
// argument-boundary assertions run through the real plugin hooks, where the
// cleanup mechanism is the same unconditional hide/restore as the review
// contract.
// ---------------------------------------------------------------------------

test('WHAT[host-boundary-032] C17_delegation_schema_adds_required_budget_and_optional_note_without_dropping_tool_contract', async () => {
  const previousLanguage = process.env.WANXIANGSHU_PROVIDER_LANGUAGE
  const language = await import('../../../dist/Participant/Provider/LanguageSurface.js')
  process.env.WANXIANGSHU_PROVIDER_LANGUAGE = 'en'
  try {
    language.refreshGlobalLanguage()
    const definition = {
      description: 'Original description of the tool',
      parameters: {
        type: 'object',
        properties: { path: { type: 'string' } },
        required: ['path'],
        additionalProperties: false,
      },
    }
    PluginHooksSurface.decorateReadonlyDelegationToolDefinition('read', definition)

    const props = definition.parameters.properties
    // 估计字段：integer、范围 [0, 2147483647]、7.1 事实性估计说明，且必填
    assert.equal(props.estimated_readonly_rounds.type, 'integer')
    assert.equal(props.estimated_readonly_rounds.minimum, 0)
    assert.equal(props.estimated_readonly_rounds.maximum, 2147483647)
    assert.ok(
      typeof props.estimated_readonly_rounds.description === 'string' &&
        props.estimated_readonly_rounds.description.includes('consecutive read-only investigation rounds'),
      'budget must carry the description',
    )
    assert.deepEqual(
      definition.parameters.required,
      ['path', 'estimated_readonly_rounds'],
      'original required entry must be preserved and only the budget appended',
    )
    // 短记：string、7.1 条件说明、绝不进 required、不设 minLength
    assert.equal(props.self_note.type, 'string')
    assert.ok(
      typeof props.self_note.description === 'string' &&
        props.self_note.description.includes('estimated_readonly_rounds is greater than 0'),
      'self_note must carry the description',
    )
    assert.equal(
      definition.parameters.required.includes('self_note'),
      false,
      'self_note must never join required',
    )
    assert.equal('minLength' in props.self_note, false, 'self_note must not carry a minLength gate')
    // 原有属性与兼容约束保持
    assert.deepEqual(definition.parameters.properties.path, { type: 'string' })
    assert.equal(definition.parameters.additionalProperties, false)
    // 调查展望短说明幂等追加在原描述之后，不替换原描述，删去旧同伴/信任措辞
    assert.ok(
      definition.description.startsWith('Original description of the tool'),
      'original tool description must stay as the prefix',
    )
    assert.ok(
      definition.description.includes('Investigation outlook: estimated_readonly_rounds estimates'),
      'English investigation outlook prose must be appended once',
    )
    assert.equal(
      definition.description.includes('Fill in delegate_readonly_rounds on every tool call'),
      false,
      'legacy delegation prose must not appear',
    )
    assert.equal(
      /companion/i.test(definition.description),
      false,
      'companion narrative must not appear in new tool description',
    )

    // 非参与工具（如 join）零增量断言：无新字段、无短说明、required 不变
    const joinDefinition = {
      description: 'Original join description',
      parameters: {
        type: 'object',
        properties: {},
        required: [],
      },
    }
    const joinSnapshot = structuredClone(joinDefinition)
    PluginHooksSurface.decorateReadonlyDelegationToolDefinition('join', joinDefinition)
    assert.deepEqual(
      joinDefinition,
      joinSnapshot,
      'non-participating tool join must remain completely unmodified',
    )
  } finally {
    if (previousLanguage === undefined) {
      delete process.env.WANXIANGSHU_PROVIDER_LANGUAGE
    } else {
      process.env.WANXIANGSHU_PROVIDER_LANGUAGE = previousLanguage
    }
    language.refreshGlobalLanguage()
  }
})

test('WHAT[host-boundary-032] C18_budget_rejects_illegal_values_and_never_coerces_them', () => {
  // 边界与正常值：原生有限整数且在 [0, 2147483647] 内
  for (const value of [0, 1, 2, 5, 2147483647]) {
    const result = PluginHooksSurface.readonlyDelegationBudgetOf(value)
    assert.equal(result.ok, true, `budget ${value} must be accepted`)
    assert.equal(result.rounds, value)
  }
  // 缺失、null、负数、小数、数值字符串、布尔、越界、非有限值、对象/数组
  const rejected = [
    undefined,
    null,
    -1,
    -0.5,
    1.5,
    '3',
    '0',
    true,
    false,
    2147483648,
    Number.NaN,
    Number.POSITIVE_INFINITY,
    Number.NEGATIVE_INFINITY,
    {},
    [],
  ]
  for (const value of rejected) {
    const result = PluginHooksSurface.readonlyDelegationBudgetOf(value)
    assert.equal(result.ok, false, `budget ${typeof value}:${String(value)} must be rejected without coercion`)
    assert.equal(typeof result.error, 'string')
    assert.equal(result.rounds, undefined, 'rejected budget must not produce a value')
  }
})

test('WHAT[host-boundary-032] C19_self_note_is_advisory_and_never_fails_the_call', () => {
  // self_note 是建议性短记：任何形态都不构成调用失败（speculative-investigation-016）
  // 属性不存在时合法
  const zeroOmitted = PluginHooksSurface.readonlyDelegationSelfNoteOf({ estimated_readonly_rounds: 0 })
  assert.equal(zeroOmitted.ok, true, 'absent self_note is required when estimated_readonly_rounds is 0')
  assert.equal(zeroOmitted.note, null)

  // 估计为 0 但提供了任何形式的 self_note 也不失败；字符串保留，非字符串按缺失读取
  for (const noteValue of [undefined, null, '', '   ', 'some note', 0, 1, true, {}, []]) {
    const result = PluginHooksSurface.readonlyDelegationSelfNoteOf(0, noteValue)
    assert.equal(result.ok, true, `self_note of ${JSON.stringify(noteValue)} at zero is not a failure`)
    assert.equal(result.note, typeof noteValue === 'string' ? noteValue : null)
  }

  // 2. 正数估计时：短记缺失或形态不合法也不失败
  const missingForPositive = PluginHooksSurface.readonlyDelegationSelfNoteOf({ estimated_readonly_rounds: 2 })
  assert.equal(missingForPositive.ok, true)
  assert.equal(missingForPositive.note, null)

  for (const invalidNote of ['', '   ', '\t\n', null, undefined, 0, 123, true, {}, []]) {
    const result = PluginHooksSurface.readonlyDelegationSelfNoteOf(2, invalidNote)
    assert.equal(result.ok, true, `note ${JSON.stringify(invalidNote)} at positive rounds is not a failure`)
    assert.equal(result.note, typeof invalidNote === 'string' ? invalidNote : null)
  }

  // 3. 合法正数 + 非空白短记：round-trip 原样保留（包括首尾空格、换行等原始证据）
  const noteText = '我怀疑入口与调用方对空值的约定不同，接下来先核对调用点'
  const validPositive = PluginHooksSurface.readonlyDelegationSelfNoteOf(2, noteText)
  assert.equal(validPositive.ok, true)
  assert.equal(validPositive.note, noteText, 'note content must round-trip verbatim')
})

test('WHAT[host-boundary-032] C20_delegation_decoration_is_idempotent_and_coexists_with_review_contract', async () => {
  const previousLanguage = process.env.WANXIANGSHU_PROVIDER_LANGUAGE
  const language = await import('../../../dist/Participant/Provider/LanguageSurface.js')
  process.env.WANXIANGSHU_PROVIDER_LANGUAGE = 'en'
  try {
    language.refreshGlobalLanguage()
    const definition = {
      description: 'Description for js-manager',
      parameters: {
        type: 'object',
        properties: { path: { type: 'string' } },
        required: ['path'],
      },
    }
    PluginHooksSurface.decorateReadonlyDelegationToolDefinition('js-manager', definition)
    PluginHooksSurface.decorateReviewToolDefinition('js-manager', definition)
    const firstSnapshot = structuredClone(definition)

    // 重复装饰、两类装饰交替都不产生重复项
    PluginHooksSurface.decorateReadonlyDelegationToolDefinition('js-manager', definition)
    PluginHooksSurface.decorateReadonlyDelegationToolDefinition('js-manager', definition)
    PluginHooksSurface.decorateReviewToolDefinition('js-manager', definition)
    PluginHooksSurface.decorateReadonlyDelegationToolDefinition('js-manager', definition)
    PluginHooksSurface.decorateReviewToolDefinition('js-manager', definition)

    assert.deepEqual(definition, firstSnapshot, 'coexisting decorations must be idempotent across repeats')
    assert.equal(
      definition.parameters.required.filter((x) => x === 'estimated_readonly_rounds').length,
      1,
      'budget must appear exactly once in required',
    )
    assert.equal(
      definition.parameters.required.filter((x) => x === 'self_note').length,
      0,
      'note must never join required',
    )
    assert.equal(
      definition.parameters.required.filter((x) => x === 'contract').length,
      1,
      'review contract must appear exactly once and stay intact beside the delegation protocol',
    )
    assert.equal(
      definition.description.split('Investigation outlook: estimated_readonly_rounds estimates').length - 1,
      1,
      'collaboration prose must be appended exactly once',
    )
  } finally {
    if (previousLanguage === undefined) {
      delete process.env.WANXIANGSHU_PROVIDER_LANGUAGE
    } else {
      process.env.WANXIANGSHU_PROVIDER_LANGUAGE = previousLanguage
    }
    language.refreshGlobalLanguage()
  }
})

test('WHAT[host-boundary-032] C21_collaboration_prose_follows_language_binding_and_switches_cleanly', async () => {
  const previousLanguage = process.env.WANXIANGSHU_PROVIDER_LANGUAGE
  const language = await import('../../../dist/Participant/Provider/LanguageSurface.js')
  try {
    process.env.WANXIANGSHU_PROVIDER_LANGUAGE = 'zh-CN'
    language.refreshGlobalLanguage()
    const definition = {
      description: '原始工具描述',
      parameters: { type: 'object', properties: {}, required: [] },
    }
    PluginHooksSurface.decorateReadonlyDelegationToolDefinition('read', definition)
    assert.ok(
      definition.description.includes('调查展望：estimated_readonly_rounds 估计当前整批完成后的连续只读查证轮数'),
      'Chinese collaboration prose required under zh-CN preference',
    )
    assert.equal(
      definition.description.includes('Investigation outlook: estimated_readonly_rounds estimates'),
      false,
      'English prose must not arrive under a Chinese preference',
    )
    const zhSnapshot = structuredClone(definition)
    PluginHooksSurface.decorateReadonlyDelegationToolDefinition('read', definition)
    assert.deepEqual(definition, zhSnapshot, 'Chinese decoration must be idempotent')

    // 语言切换后只剩当前语言一段，不叠加
    process.env.WANXIANGSHU_PROVIDER_LANGUAGE = 'en'
    language.refreshGlobalLanguage()
    PluginHooksSurface.decorateReadonlyDelegationToolDefinition('read', definition)
    assert.ok(definition.description.includes('Investigation outlook: estimated_readonly_rounds estimates'))
    assert.equal(
      definition.description.includes('调查展望：estimated_readonly_rounds'),
      false,
      'prior-language prose must be replaced, not stacked',
    )
    assert.ok(
      definition.description.startsWith('原始工具描述'),
      'original description must remain the prefix after a language switch',
    )
  } finally {
    if (previousLanguage === undefined) {
      delete process.env.WANXIANGSHU_PROVIDER_LANGUAGE
    } else {
      process.env.WANXIANGSHU_PROVIDER_LANGUAGE = previousLanguage
    }
  }
})

test('WHAT[host-boundary-032] C22_conflicting_same_name_properties_and_bad_required_fail_loudly', () => {
  const budgetConflict = {
    description: 'D',
    parameters: {
      type: 'object',
      properties: {
        path: { type: 'string' },
        estimated_readonly_rounds: { type: 'integer', minimum: 0, maximum: 100, description: 'tool-local budget' },
      },
      required: ['path'],
    },
  }
  // ReadonlyDelegationContract 内部使用 F# invalidOp (InvalidOperationException) 抛出异常，
  // 经 Fable 编译后为纯 JavaScript Error 对象，未提供如 .code 等结构化错误码。
  // 因此此处保留正则表达式精确匹配自然语言异常文案，作为当前在宿主边界能够区分不同 Schema 拒绝原因的唯一起效判定信号。
  assert.throws(
    () => PluginHooksSurface.decorateReadonlyDelegationToolDefinition('read', budgetConflict),
    /^Tool read defines a conflicting estimated_readonly_rounds property that differs from the readonly delegation protocol$/,
    'a same-name property that differs from the protocol must fail, not be overwritten',
  )

  const noteConflict = {
    description: 'D',
    parameters: {
      type: 'object',
      properties: {
        path: { type: 'string' },
        self_note: { type: 'string', description: 'tool-local note' },
      },
      required: ['path'],
    },
  }
  assert.throws(
    () => PluginHooksSurface.decorateReadonlyDelegationToolDefinition('read', noteConflict),
    /^Tool read defines a conflicting self_note property that differs from the readonly delegation protocol$/,
    'a same-name note that differs from the protocol must fail',
  )

  // 匹配自然语言散文断言 required 非数组：同样因底层抛出无错误码的标准 Error，正则散文匹配是唯一可用信号
  const badRequired = {
    description: 'D',
    parameters: { type: 'object', properties: { path: { type: 'string' } }, required: 'path' },
  }
  assert.throws(
    () => PluginHooksSurface.decorateReadonlyDelegationToolDefinition('read', badRequired),
    /^Tool read parameters schema required field is not an array$/,
    'a non-array required must fail loudly instead of publishing a partial protocol',
  )

  // 匹配自然语言散文断言 properties 缺失：同样因底层抛出无错误码的标准 Error，正则散文匹配是唯一可用信号
  const missingProperties = {
    description: 'D',
    parameters: { type: 'object' },
  }
  assert.throws(
    () => PluginHooksSurface.decorateReadonlyDelegationToolDefinition('read', missingProperties),
    /^Tool read parameters schema missing object properties$/,
    'a root schema that cannot be legally extended must fail loudly',
  )
})

// ---------------------------------------------------------------------------
// 两态门控：ModelRouting.initialize 的 scheduler 是进程
// 单例（每测试进程只 import 一次），因此两个可观察态由测试自有的动态源驱动
// ——fixture 在隔离 HOME 下写出的 wanxiangshu.mjs 导出 predictorConfiguration，
// 读 globalThis 上的测试注入值，不触碰用户真实配置。默认（未注入）即未配置。
// ---------------------------------------------------------------------------

const setPredictorState = (state, reason) => {
  globalThis.__wanxiangshu_test_predictor_state = state
  if (reason === undefined) {
    delete globalThis.__wanxiangshu_test_predictor_reason
  } else {
    globalThis.__wanxiangshu_test_predictor_reason = reason
  }
}

const clearPredictorState = () => {
  delete globalThis.__wanxiangshu_test_predictor_state
  delete globalThis.__wanxiangshu_test_predictor_reason
}

test('WHAT[host-boundary-032] C23_delegation_fields_stripped_from_business_view_but_preserved_as_evidence', async () => {
  setPredictorState('configured')
  await withExecutablePlugin(async (hooks, _directory, _createdIds, runtime) => {
    const tool = 'read'
    const sessionID = 'ses-c23'
    await openIncumbency(runtime, sessionID)
    const callID = 'call-c23-1'
    const note = '我怀疑入口与调用方对空值的约定不同，接下来先核对调用点'
    const beforeOutput = {
      args: {
        filePath: 'src/A.fs',
        estimated_readonly_rounds: 5,
        self_note: note,
      },
    }

    await hooks['tool.execute.before']({ tool, sessionID, callID }, beforeOutput)
    assert.equal(
      'estimated_readonly_rounds' in beforeOutput.args,
      false,
      'business view must not see the budget field',
    )
    assert.equal('self_note' in beforeOutput.args, false, 'business view must not see the note field')
    assert.deepEqual(beforeOutput.args, { filePath: 'src/A.fs' }, 'business view keeps only tool parameters')

    await hooks['tool.execute.after'](
      { tool, sessionID, callID, args: beforeOutput.args },
      { title: tool, output: 'file contents', metadata: {} },
    )

    assert.equal(
      beforeOutput.args.estimated_readonly_rounds,
      5,
      'original provider arguments must preserve the budget as evidence',
    )
    assert.equal(
      beforeOutput.args.self_note,
      note,
      'original provider arguments must preserve the note as evidence',
    )

    // 非参与工具（如 join）即使 arguments 携带同名字段也不被 hide/restore 触碰
    const nonParticipatingOutput = {
      args: {
        id: 'job-1',
        estimated_readonly_rounds: 5,
        self_note: note,
      },
    }
    await hooks['tool.execute.before']({ tool: 'join', sessionID, callID: 'call-c23-join' }, nonParticipatingOutput)
    assert.equal('estimated_readonly_rounds' in nonParticipatingOutput.args, true, 'non-participating tool arguments must not be stripped')
    assert.equal('self_note' in nonParticipatingOutput.args, true, 'non-participating tool note must not be stripped')
  })
})

test('WHAT[host-boundary-032] C24_review_contract_and_delegation_fields_coexist_without_crosstalk', async () => {
  await withExecutablePlugin(async (hooks, _directory, _createdIds, runtime) => {
    const tool = 'js-manager'
    const sessionID = 'ses-c24'
    await openIncumbency(runtime, sessionID)
    const callID = 'call-c24-1'
    const beforeOutput = {
      args: {
        path: 'src/App.fs',
        contract: 'js-manager-contract-v1',
        estimated_readonly_rounds: 2,
        self_note: 'note-c24',
      },
    }

    await hooks['tool.execute.before']({ tool, sessionID, callID }, beforeOutput)
    assert.deepEqual(
      beforeOutput.args,
      { path: 'src/App.fs' },
      'both contract families must leave the business view together',
    )

    await hooks['tool.execute.after'](
      { tool, sessionID, callID, args: beforeOutput.args },
      { title: tool, output: 'file contents', metadata: {} },
    )

    assert.equal(beforeOutput.args.contract, 'js-manager-contract-v1', 'review contract must be restored')
    assert.equal(beforeOutput.args.estimated_readonly_rounds, 2, 'budget must be restored beside the contract')
    assert.equal(beforeOutput.args.self_note, 'note-c24', 'note must be restored beside the contract')
  })
})

test('WHAT[host-boundary-032] C25_frozen_delegation_fields_fail_atomically_without_losing_evidence', async () => {
  await withExecutablePlugin(async (hooks, _directory, _createdIds, runtime) => {
    const sessionID = 'ses-c25'
    await openIncumbency(runtime, sessionID)
    const frozenArgs = Object.freeze({ path: 'src/Secret.fs', estimated_readonly_rounds: 3, self_note: 'secret' })

    await assert.rejects(
      async () => {
        await hooks['tool.execute.before'](
          { tool: 'read', sessionID, callID: 'call-c25' },
          { args: frozenArgs },
        )
      },
      TypeError,
      'frozen args with protocol fields must fail atomically with TypeError',
    )
    assert.equal(frozenArgs.estimated_readonly_rounds, 3, 'budget evidence must remain unchanged on failure')
    assert.equal(frozenArgs.self_note, 'secret', 'note evidence must remain unchanged on failure')

    // 非参与工具（如 join）的冻结入参即使包含同名字段也不被 hide 触碰，因此不会抛出 TypeError
    const frozenNonParticipating = Object.freeze({ id: 'part-1', estimated_readonly_rounds: 3, self_note: 'secret' })
    const joinOutput = { args: frozenNonParticipating }
    await hooks['tool.execute.before'](
      { tool: 'join', sessionID, callID: 'call-c25-join' },
      joinOutput,
    )
    assert.equal(joinOutput.args.estimated_readonly_rounds, 3)
  })
})

test('WHAT[host-boundary-032] C26_concurrent_delegation_restores_do_not_crosstalk', async () => {
  await withExecutablePlugin(async (hooks, _directory, _createdIds, runtime) => {
    const call1 = {
      sessionID: 'ses-c26-1',
      callID: 'call-c26-1',
      output: { args: { path: 'file-alpha.txt', estimated_readonly_rounds: 1, self_note: 'note-alpha' } },
    }
    const call2 = {
      sessionID: 'ses-c26-2',
      callID: 'call-c26-2',
      output: { args: { path: 'file-beta.txt', estimated_readonly_rounds: 9, self_note: 'note-beta' } },
    }

    await Promise.all([
      openIncumbency(runtime, call1.sessionID),
      openIncumbency(runtime, call2.sessionID),
    ])

    await Promise.all([
      hooks['tool.execute.before']({ tool: 'read', sessionID: call1.sessionID, callID: call1.callID }, call1.output),
      hooks['tool.execute.before']({ tool: 'read', sessionID: call2.sessionID, callID: call2.callID }, call2.output),
    ])

    assert.equal('estimated_readonly_rounds' in call1.output.args, false)
    assert.equal('self_note' in call1.output.args, false)
    assert.equal('estimated_readonly_rounds' in call2.output.args, false)
    assert.equal('self_note' in call2.output.args, false)

    // 乱序恢复也不串值
    await Promise.all([
      hooks['tool.execute.after'](
        { tool: 'read', sessionID: call2.sessionID, callID: call2.callID, args: call2.output.args },
        { title: 'read', output: 'out2', metadata: {} },
      ),
      hooks['tool.execute.after'](
        { tool: 'read', sessionID: call1.sessionID, callID: call1.callID, args: call1.output.args },
        { title: 'read', output: 'out1', metadata: {} },
      ),
    ])

    assert.equal(call1.output.args.estimated_readonly_rounds, 1, 'call1 must restore its own budget')
    assert.equal(call1.output.args.self_note, 'note-alpha', 'call1 must restore its own note')
    assert.equal(call2.output.args.estimated_readonly_rounds, 9, 'call2 must restore its own budget')
    assert.equal(call2.output.args.self_note, 'note-beta', 'call2 must restore its own note')
  })
})

test('WHAT[host-boundary-032] C27_business_tool_execution_never_receives_protocol_fields', async () => {
  await withExecutablePlugin(async (hooks, _directory, _createdIds, runtime) => {
    const sessionID = 'ses-c27'
    await openIncumbency(runtime, sessionID)
    const directArgs = { path: 'src/App.fs', estimated_readonly_rounds: 0 }

    await hooks['tool.execute.before'](
      { tool: 'js-manager', sessionID, callID: 'call-c27' },
      { args: directArgs },
    )

    const execResult = await hooks.tool['js-manager'].execute(directArgs, { sessionID, agent: 'manager' })
    assert.ok(typeof execResult === 'string', 'tool execution must succeed on the cleaned business view')

    // 业务执行视图在 before 暂存之后不再持有协议字段；在 after 恢复之前断言，
    // 证明 execute 收到的确实是剥净后的视图（C24 已在 before 后证明同一机制）。
    assert.equal(
      'estimated_readonly_rounds' in directArgs,
      false,
      'business execution view must not carry the budget field',
    )
    assert.equal(
      'self_note' in directArgs,
      false,
      'business execution view must not carry the note field',
    )

    await hooks['tool.execute.after'](
      { tool: 'js-manager', sessionID, callID: 'call-c27', args: directArgs },
      { title: 'js-manager', output: execResult, metadata: {} },
    )

    // after 的同源恢复之后，provider 原始 arguments 证据重新出现；这与
    // C23/C24/C26 证明的恢复契约同构——字段不丢，只是不进业务视图。
    assert.equal(
      directArgs.estimated_readonly_rounds,
      0,
      'budget evidence must be restored to the original arguments',
    )
    assert.equal(
      'self_note' in directArgs,
      false,
      'self_note must remain absent when rounds is 0',
    )
  })
})

test('WHAT[host-boundary-032] C28_unconfigured_predictor_leaves_tool_definitions_undecorated', async () => {
  clearPredictorState()
  await withExecutablePlugin(async (hooks) => {
    for (const toolID of ['js-manager', 'read', 'write']) {
      const originalDescription = `Description for ${toolID}`
      const output = {
        description: originalDescription,
        parameters: {
          type: 'object',
          properties: { path: { type: 'string' } },
          required: ['path'],
        },
      }
      await hooks['tool.definition']({ toolID }, output)
      assert.equal(
        output.parameters.properties?.estimated_readonly_rounds,
        undefined,
        `${toolID} must not gain the budget field while Predictor is unconfigured`,
      )
      assert.equal(
        output.parameters.properties?.self_note,
        undefined,
        `${toolID} must not gain the note field while Predictor is unconfigured`,
      )
      assert.equal(
        output.parameters.required.includes('estimated_readonly_rounds'),
        false,
        `${toolID} must not require the budget while Predictor is unconfigured`,
      )
      assert.equal(
        output.description,
        originalDescription,
        `${toolID} description must gain no collaboration prose while Predictor is unconfigured`,
      )
    }
  })
})

test('WHAT[host-boundary-032] C29_configured_predictor_decorates_every_tool_through_the_definition_hook', async () => {
  setPredictorState('configured')
  const previousLanguage = process.env.WANXIANGSHU_PROVIDER_LANGUAGE
  process.env.WANXIANGSHU_PROVIDER_LANGUAGE = 'en'
  try {
    await withExecutablePlugin(async (hooks) => {
      for (const toolID of ['js-manager', 'read', 'write']) {
        const originalDescription = `Description for ${toolID}`
        const output = {
          description: originalDescription,
          parameters: {
            type: 'object',
            properties: { path: { type: 'string' } },
            required: ['path'],
          },
        }
        await hooks['tool.definition']({ toolID }, output)
        assert.equal(
          output.parameters.properties.estimated_readonly_rounds.type,
          'integer',
          `${toolID} must gain the required budget when Predictor is configured`,
        )
        assert.equal(
          output.parameters.properties.self_note.type,
          'string',
          `${toolID} must gain the optional note when Predictor is configured`,
        )
        assert.equal(
          output.parameters.required.includes('estimated_readonly_rounds'),
          true,
          `${toolID} must require the budget when Predictor is configured`,
        )
        assert.equal(
          output.parameters.required.includes('self_note'),
          false,
          `${toolID} must keep the note omissible when Predictor is configured`,
        )
        assert.equal(
          output.parameters.required.includes('path'),
          true,
          `${toolID} must keep its original required entry`,
        )
        assert.ok(
          output.description.startsWith(originalDescription),
          `${toolID} original description must stay as the prefix`,
        )
        assert.ok(
          output.description.length > originalDescription.length,
          `${toolID} description must gain the collaboration prose`,
        )
        if (toolID === 'js-manager') {
          // 与评审 contract 在同一 hook 路径并存，互不排斥
          assert.equal(
            output.parameters.required.includes('contract'),
            true,
            'review contract must coexist with the delegation budget on js-manager',
          )
          assert.equal(
            output.parameters.properties.contract.type,
            'string',
            'review contract property must coexist with the delegation protocol',
          )
        }
      }

      // 非参与工具（如 join）在配置 Predictor 时保持零增量
      const joinOutput = {
        description: 'Original join description',
        parameters: { type: 'object', properties: {}, required: [] },
      }
      await hooks['tool.definition']({ toolID: 'join' }, joinOutput)
      assert.equal(
        joinOutput.parameters.properties?.estimated_readonly_rounds,
        undefined,
        'join must not gain estimated_readonly_rounds even when Predictor is configured',
      )
      assert.equal(
        joinOutput.parameters.properties?.self_note,
        undefined,
        'join must not gain self_note even when Predictor is configured',
      )
      assert.equal(
        joinOutput.parameters.required.includes('estimated_readonly_rounds'),
        false,
        'join must not require estimated_readonly_rounds',
      )
    })
  } finally {
    clearPredictorState()
    if (previousLanguage === undefined) {
      delete process.env.WANXIANGSHU_PROVIDER_LANGUAGE
    } else {
      process.env.WANXIANGSHU_PROVIDER_LANGUAGE = previousLanguage
    }
  }
})

test('WHAT[host-boundary-032] C30_configured_decoration_through_hook_is_idempotent', async () => {
  setPredictorState('configured')
  try {
    await withExecutablePlugin(async (hooks) => {
      const output = {
        description: 'Description for read',
        parameters: {
          type: 'object',
          properties: { path: { type: 'string' } },
          required: ['path'],
        },
      }
      await hooks['tool.definition']({ toolID: 'read' }, output)
      const firstSnapshot = structuredClone(output)
      await hooks['tool.definition']({ toolID: 'read' }, output)
      await hooks['tool.definition']({ toolID: 'read' }, output)
      assert.deepEqual(
        output,
        firstSnapshot,
        'repeated definition hook calls must not duplicate required entries or prose',
      )
      assert.equal(
        output.parameters.required.filter((x) => x === 'estimated_readonly_rounds').length,
        1,
        'budget must appear exactly once in required across repeats',
      )
    })
  } finally {
    clearPredictorState()
  }
})

test('WHAT[host-boundary-032] C31_invalid_predictor_configuration_fails_closed_without_publishing_partial_protocol', async () => {
  setPredictorState('invalid', 'predictor candidates must be [model, reasoning] pairs')
  try {
    await withExecutablePlugin(async (hooks) => {
      const output = {
        description: 'Description for read',
        parameters: {
          type: 'object',
          properties: { path: { type: 'string' } },
          required: ['path'],
        },
      }
      await assert.rejects(
        async () => {
          await hooks['tool.definition']({ toolID: 'read' }, output)
        },
        /execution-model-routing: Predictor model configuration is invalid: predictor candidates must be \[model, reasoning\] pairs/,
        'invalid Predictor configuration must fail closed on the tool.definition hook',
      )
      assert.equal(
        output.parameters.properties?.estimated_readonly_rounds,
        undefined,
        'no partial protocol may be published on the fail-closed path',
      )
      assert.equal(
        output.description,
        'Description for read',
        'no collaboration prose may be appended on the fail-closed path',
      )

      // 修复配置后同一 hook 立即恢复装饰：失败只属于那一回，无缓存状态
      setPredictorState('configured')
      await hooks['tool.definition']({ toolID: 'read' }, output)
      assert.equal(
        output.parameters.properties.estimated_readonly_rounds.type,
        'integer',
        'the hook must decorate again once the configuration is repaired',
      )
    })
  } finally {
    clearPredictorState()
  }
})

test('WHAT[host-boundary-032] C32_decoration_gate_shares_the_single_predictor_configuration_query', async () => {
  clearPredictorState()
  await withExecutablePlugin(async (hooks) => {
    assert.equal(
      ModelRoutingSurface.sharedPredictorConfiguration().kind,
      'NotConfigured',
      'the shared query must report the unconfigured state',
    )
    const output = {
      description: 'Description for read',
      parameters: {
        type: 'object',
        properties: { path: { type: 'string' } },
        required: ['path'],
      },
    }
    await hooks['tool.definition']({ toolID: 'read' }, output)
    assert.equal(
      output.parameters.properties?.estimated_readonly_rounds,
      undefined,
      'decoration must stay off while the shared query reports NotConfigured',
    )
  })

  setPredictorState('configured')
  await withExecutablePlugin(async (hooks) => {
    assert.equal(
      ModelRoutingSurface.sharedPredictorConfiguration().kind,
      'Configured',
      'the shared query must report the configured state',
    )
    const output = {
      description: 'Description for read',
      parameters: {
        type: 'object',
        properties: { path: { type: 'string' } },
        required: ['path'],
      },
    }
    await hooks['tool.definition']({ toolID: 'read' }, output)
    assert.equal(
      output.parameters.properties.estimated_readonly_rounds.type,
      'integer',
      'decoration must follow the same single query result',
    )
  })
})

test('WHAT[host-boundary-032] C33_gate_follows_configuration_changes_within_one_plugin_instance', async () => {
  setPredictorState('configured')
  try {
    await withExecutablePlugin(async (hooks) => {
      const makeDefinition = () => ({
        description: 'Description for read',
        parameters: {
          type: 'object',
          properties: { path: { type: 'string' } },
          required: ['path'],
        },
      })

      const configuredOutput = makeDefinition()
      await hooks['tool.definition']({ toolID: 'read' }, configuredOutput)
      assert.equal(
        configuredOutput.parameters.properties.estimated_readonly_rounds.type,
        'integer',
        'configured state must decorate within one plugin instance',
      )

      clearPredictorState()
      const revertedOutput = makeDefinition()
      await hooks['tool.definition']({ toolID: 'read' }, revertedOutput)
      assert.equal(
        revertedOutput.parameters.properties?.estimated_readonly_rounds,
        undefined,
        'removing the configuration must stop decoration in the same instance',
      )
      assert.equal(
        revertedOutput.description,
        'Description for read',
        'removing the configuration must stop the collaboration prose',
      )

      setPredictorState('configured')
      const reconfiguredOutput = makeDefinition()
      await hooks['tool.definition']({ toolID: 'read' }, reconfiguredOutput)
      assert.equal(
        reconfiguredOutput.parameters.properties.estimated_readonly_rounds.type,
        'integer',
        'reconfiguring must resume decoration without a second enabled truth',
      )
    })
  } finally {
    clearPredictorState()
  }
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

    // Linux CI (OpenCode 1.18.29) intermittently flakes mid-canary (Host 5xx or
    // observation timeout) while Darwin 1.18.31 stays green. Retry the whole
    // runner only on those transients; product contract assertions stay unchanged.
    const isTransientHostCanaryFailure = (proc) => {
      const text = `${proc.stderr || ''}\n${proc.stdout || ''}`
      return (
        /Unexpected server error/i.test(text) ||
        /Timed out waiting for/i.test(text) ||
        /ECONNRESET|socket hang up|ECONNREFUSED/i.test(text)
      )
    }

    let launched = null
    for (let attempt = 1; attempt <= 3; attempt += 1) {
      launched = spawnSync(process.execPath, [runnerPath], {
        cwd: repoRoot,
        encoding: 'utf8',
        timeout: 120000,
      })
      if (launched.status === 0) break
      if (!isTransientHostCanaryFailure(launched) || attempt === 3) break
    }

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
      ['js-manager'],
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
      visibleToolNames.includes('js-manager'),
      `provider-visible set must include the plugin tool js-manager; observed: ${visibleToolNames.join(',')}`,
    )
    assert.ok(
      ['skill', 'read', 'glob', 'grep'].some((name) => visibleToolNames.includes(name)),
      `provider-visible set must include host builtin tools; observed: ${visibleToolNames.join(',')}`,
    )
    assert.equal(
      stdoutSummary.wireInspection?.providerVisibleProtocolAbsence,
      true,
      'every provider-visible tool must be free of the delegation protocol fields while Predictor is unconfigured',
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
  },
)

// host-boundary-032 / the Host persists tool-call input after
// the before hook strips the protocol fields, so the next provider request is
// built from stripped history. The provider-facing transform restores the
// vaulted wire originals into that history before any consumer reads it.

test('WHAT[host-boundary-032] C34_transform_restores_hidden_protocol_fields_into_persisted_history', async () => {
  setPredictorState('configured')
  await withExecutablePlugin(async (hooks, _directory, _createdIds, runtime) => {
    const tool = 'js-manager'
    const sessionID = 'ses-c34'
    await openIncumbency(runtime, sessionID)
    const callID = 'call-c34-1'
    const beforeOutput = {
      args: {
        path: 'src/App.fs',
        contract: 'js-manager-contract-v1',
        estimated_readonly_rounds: 3,
        self_note: 'note-c34',
      },
    }

    await hooks['tool.execute.before']({ tool, sessionID, callID }, beforeOutput)

    // The persisted history the next provider request is built from: protocol
    // fields stripped from the durable tool-call input.
    const transformed = {
      messages: [
        {
          role: 'assistant',
          info: { id: 'asst-c34', sessionID },
          parts: [
            {
              type: 'tool',
              tool,
              callID,
              state: { status: 'completed', input: { path: 'src/App.fs' }, output: 'file contents' },
            },
          ],
        },
      ],
    }

    await hooks['experimental.chat.messages.transform']({}, transformed)

    const restoredInput = transformed.messages[0].parts[0].state.input
    assert.equal(
      restoredInput.contract,
      'js-manager-contract-v1',
      'review contract must return to the persisted history on the wire',
    )
    assert.equal(
      restoredInput.estimated_readonly_rounds,
      3,
      'budget must return to the persisted history on the wire',
    )
    assert.equal(
      restoredInput.self_note,
      'note-c34',
      'note must return to the persisted history on the wire',
    )
    assert.equal(
      restoredInput.path,
      'src/App.fs',
      'business arguments must survive the restore untouched',
    )
  })
})

test('WHAT[host-boundary-032] C35_unknown_tool_call_leaves_persisted_history_untouched', async () => {
  await withExecutablePlugin(async (hooks) => {
    // No before hook ran for this call: nothing was vaulted (e.g. history from
    // before a process restart). The restore must fail open and leave the wire
    // untouched; the pair-programming guideline marker is another mechanism's
    // legitimate output and is excluded from the comparison below.
    const transformed = {
      messages: [
        {
          role: 'assistant',
          info: { id: 'asst-c35', sessionID: 'ses-c35' },
          parts: [
            {
              type: 'tool',
              tool: 'read',
              callID: 'call-c35-unknown',
              state: { status: 'completed', input: { path: 'src/Old.fs' }, output: 'old contents' },
            },
          ],
        },
      ],
    }
    const before = structuredClone(transformed)

    await hooks['experimental.chat.messages.transform']({}, transformed)

    // The restore must fail open on a call with no vault entry. The
    // pair-programming guideline injector (HOST-013 Cursor mode,
    // prefix-stability-010) legitimately appends its NUL+BOM marker to
    // terminal tool results on the provider wire; that is a different
    // mechanism with its own contract, so the wire is compared with only
    // that marker removed — every other mutation still fails here.
    const wireWithoutGuidelineMarker = structuredClone(transformed)

    for (const message of wireWithoutGuidelineMarker.messages) {
      for (const part of message.parts ?? []) {
        if (typeof part?.state?.output === 'string') {
          part.state.output = part.state.output.split('\u0000\uFEFF')[0]
        }
      }
    }

    assert.deepEqual(
      wireWithoutGuidelineMarker,
      before,
      'a call with no vault entry must fail open and leave the wire untouched but for the pair-programming guideline marker',
    )

    assert.deepEqual(
      transformed.messages[0].parts[0].state.input,
      { path: 'src/Old.fs' },
      'the persisted call input must keep exactly its business arguments',
    )

    assert.deepEqual(
      Object.keys(transformed.messages[0].parts[0].state.input),
      ['path'],
      'no protocol key may appear in the persisted call input',
    )
  })
})

test('WHAT[host-boundary-032] C36_repeated_transforms_restore_once_and_stay_stable', async () => {
  await withExecutablePlugin(async (hooks, _directory, _createdIds, runtime) => {
    const tool = 'js-manager'
    const sessionID = 'ses-c36'
    await openIncumbency(runtime, sessionID)
    const callID = 'call-c36-1'

    await hooks['tool.execute.before'](
      { tool, sessionID, callID },
      { args: { path: 'src/App.fs', contract: 'js-manager-contract-v1', estimated_readonly_rounds: 0 } },
    )

    const transformed = {
      messages: [
        {
          role: 'assistant',
          info: { id: 'asst-c36', sessionID },
          parts: [
            {
              type: 'tool',
              tool,
              callID,
              state: { status: 'completed', input: { path: 'src/App.fs' }, output: 'file contents' },
            },
          ],
        },
      ],
    }

    await hooks['experimental.chat.messages.transform']({}, transformed)
    const afterFirst = structuredClone(transformed)

    await hooks['experimental.chat.messages.transform']({}, transformed)
    await hooks['experimental.chat.messages.transform']({}, transformed)

    assert.deepEqual(
      transformed,
      afterFirst,
      'repeated transforms must not duplicate or reorder the restored fields',
    )
    assert.deepEqual(
      Object.keys(transformed.messages[0].parts[0].state.input),
      ['path', 'contract', 'estimated_readonly_rounds'],
      'business keys must keep their order before the appended protocol key',
    )
  })
})

test('WHAT[host-boundary-032] C37_tool_results_are_never_rewritten_by_the_restore', async () => {
  await withExecutablePlugin(async (hooks, _directory, _createdIds, runtime) => {
    const tool = 'js-manager'
    const sessionID = 'ses-c37'
    await openIncumbency(runtime, sessionID)
    const callID = 'call-c37-1'

    await hooks['tool.execute.before'](
      { tool, sessionID, callID },
      {
        args: {
          path: 'src/App.fs',
          contract: 'js-manager-contract-v1',
          estimated_readonly_rounds: 1,
          self_note: 'note-c37',
        },
      },
    )

    const transformed = {
      messages: [
        {
          role: 'assistant',
          info: { id: 'asst-c37', sessionID },
          parts: [
            {
              type: 'tool',
              tool,
              callID,
              state: { status: 'completed', input: { path: 'src/App.fs' }, output: 'file contents' },
            },
          ],
        },
        {
          role: 'tool',
          info: { id: 'asst-c37-result', sessionID },
          parts: [{ type: 'tool-result', callID, result: 'file contents' }],
        },
      ],
    }

    await hooks['experimental.chat.messages.transform']({}, transformed)

    assert.deepEqual(
      transformed.messages[1].parts[0].result,
      'file contents',
      'tool results must stay verbatim through the restore',
    )
    assert.equal(
      transformed.messages[0].parts[0].state.input.contract,
      'js-manager-contract-v1',
      'the call itself must still be restored beside its result',
    )
    assert.equal(
      transformed.messages[0].parts[0].state.input.estimated_readonly_rounds,
      1,
      'the budget must still be restored beside its result',
    )
  })
})

test('WHAT[host-boundary-032] C38_restore_only_touches_protocol_fields_never_business_arguments', async () => {
  await withExecutablePlugin(async (hooks, _directory, _createdIds, runtime) => {
    const tool = 'read'
    const sessionID = 'ses-c38'
    await openIncumbency(runtime, sessionID)
    const callID = 'call-c38-1'

    await hooks['tool.execute.before'](
      { tool, sessionID, callID },
      {
        args: {
          path: 'src/App.fs',
          pattern: 'TODO',
          limit: 10,
          estimated_readonly_rounds: 2,
          self_note: 'note-c38',
        },
      },
    )

    const transformed = {
      messages: [
        {
          role: 'assistant',
          info: { id: 'asst-c38', sessionID },
          parts: [
            {
              type: 'tool',
              tool,
              callID,
              state: {
                status: 'completed',
                input: { path: 'src/App.fs', pattern: 'TODO', limit: 10 },
                output: 'matches',
              },
            },
          ],
        },
      ],
    }

    await hooks['experimental.chat.messages.transform']({}, transformed)

    const input = transformed.messages[0].parts[0].state.input
    assert.deepEqual(
      { path: input.path, pattern: input.pattern, limit: input.limit },
      { path: 'src/App.fs', pattern: 'TODO', limit: 10 },
      'every business argument must survive verbatim',
    )
    assert.equal(
      input.estimated_readonly_rounds,
      2,
      'the protocol field must be restored beside the business arguments',
    )
    assert.equal(
      input.self_note,
      'note-c38',
      'the note field must be restored beside the business arguments',
    )
    assert.deepEqual(
      Object.keys(input),
      ['path', 'pattern', 'limit', 'estimated_readonly_rounds', 'self_note'],
      'business keys must keep their order before the appended protocol key',
    )
  })
})

test('WHAT[host-boundary-032] C39_legacy_delegate_readonly_rounds_rejected_by_tool_before_for_participating_tools', async () => {
  await withExecutablePlugin(async (hooks, _directory, _createdIds, runtime) => {
    const sessionID = 'ses-c39'
    await openIncumbency(runtime, sessionID)

    // 1. 参与工具（read）仅携带旧字段 delegate_readonly_rounds 时，必须在 tool.execute.before 真实路径触发 MixedProtocolFields 拒绝
    await assert.rejects(
      async () => {
        await hooks['tool.execute.before'](
          { tool: 'read', sessionID, callID: 'call-c39-legacy-only' },
          { args: { path: 'src/App.fs', delegate_readonly_rounds: 2 } },
        )
      },
      /Invalid investigation estimate arguments: (不得携带旧协议字段 delegate_readonly_rounds|The legacy delegate_readonly_rounds field must not be used)/,
      'participating tool with legacy delegate_readonly_rounds must be rejected on tool.execute.before',
    )

    // 2. 参与工具同时携带新旧字段（混合字段）时，同样在 tool.execute.before 真实路径抛出异常拒绝
    await assert.rejects(
      async () => {
        await hooks['tool.execute.before'](
          { tool: 'read', sessionID, callID: 'call-c39-mixed' },
          {
            args: {
              path: 'src/App.fs',
              estimated_readonly_rounds: 1,
              self_note: 'investigate',
              delegate_readonly_rounds: 2,
            },
          },
        )
      },
      /Invalid investigation estimate arguments: (不得携带旧协议字段 delegate_readonly_rounds|The legacy delegate_readonly_rounds field must not be used)/,
      'participating tool with mixed protocol fields must be rejected on tool.execute.before',
    )

    // 3. 非参与工具（如 join）携带同名旧字段时，属于 no-op 路径，tool.execute.before 不得抛错且字段保持透传
    const nonParticipatingOutput = { args: { id: 'part-c39', delegate_readonly_rounds: 2 } }
    await hooks['tool.execute.before'](
      { tool: 'join', sessionID, callID: 'call-c39-join' },
      nonParticipatingOutput,
    )
    assert.equal(
      nonParticipatingOutput.args.delegate_readonly_rounds,
      2,
      'non-participating tool must pass through legacy field untouched without throwing',
    )
  })
})

test('WHAT[host-boundary-032] C40_sanitize_snapshot_cross_narrowing_non_review_and_non_participating_negative_cases', async () => {
  await withExecutablePlugin(async (hooks, _directory, _createdIds, runtime) => {
    const sessionID = 'ses-c40'
    await openIncumbency(runtime, sessionID)

    // 负向 1：非评审工具 read 意外携带 contract（同时携带合法的只读估计字段）
    // sanitizeSnapshot 必须将 Contract 清洗为 None，因此只有只读估计字段能进入 vault，contract 绝不进入 vault。
    // 在后续 transform 恢复 provider 历史时，contract 字段必须保持不存在（undefined），不被恢复。
    const readCallID = 'call-c40-read'
    const readBeforeOutput = {
      args: {
        path: 'src/App.fs',
        contract: 'unexpected-contract-on-read',
        estimated_readonly_rounds: 2,
        self_note: 'note-c40',
      },
    }

    await hooks['tool.execute.before'](
      { tool: 'read', sessionID, callID: readCallID },
      readBeforeOutput,
    )

    // 若非参与工具（如 join）仅携带 contract 而没有任何只读估计字段，sanitizeSnapshot 将三项全部清洗为 None，commitRecordedSnapshot 直接跳过，完全不入 vault
    const readOnlyContractCallID = 'call-c40-read-only-contract'
    const readOnlyContractOutput = {
      args: {
        taskID: 'task-c40-1',
        contract: 'unexpected-contract-only',
      },
    }
    await hooks['tool.execute.before'](
      { tool: 'join', sessionID, callID: readOnlyContractCallID },
      readOnlyContractOutput,
    )

    // 负向 2：非参与工具（如 review / join）若携带 estimated_readonly_rounds / self_note
    // sanitizeSnapshot 根据 classifyTool = NoEstimate，将其 ReadonlyRounds 与 SelfNote 均清洗为 None。
    // 注：在当前实现中，ManagerReviewTools.isReviewTool 仅硬编码为 js-manager（而 js-manager 在 classifyTool 中恰好亦属于参与工具）。
    // 工具 'review' 在 classifyTool 中虽名为 review 但属于 NoEstimate，且在 ManagerReviewTools 中不属于 review 工具。
    // 当非参与工具被调用并携带协议字段时，sanitizeSnapshot 将其过滤，三项均为 None，不进 vault，后续 transform 亦绝不写入/改写历史。
    const nonParticipatingCallID = 'call-c40-non-participating'
    const nonParticipatingOutput = {
      args: {
        id: 'review-target-1',
        contract: 'some-contract',
        estimated_readonly_rounds: 3,
        self_note: 'should-not-be-vaulted',
      },
    }
    await hooks['tool.execute.before'](
      { tool: 'review', sessionID, callID: nonParticipatingCallID },
      nonParticipatingOutput,
    )

    // 模拟构建下游恢复前的持久化消息历史
    const transformed = {
      messages: [
        {
          role: 'assistant',
          info: { id: 'asst-c40', sessionID },
          parts: [
            {
              type: 'tool',
              tool: 'read',
              callID: readCallID,
              state: { status: 'completed', input: { path: 'src/App.fs' }, output: 'ok' },
            },
            {
              type: 'tool',
              tool: 'join',
              callID: readOnlyContractCallID,
              state: { status: 'completed', input: { taskID: 'task-c40-1' }, output: 'ok' },
            },
            {
              type: 'tool',
              tool: 'review',
              callID: nonParticipatingCallID,
              state: { status: 'completed', input: { id: 'review-target-1' }, output: 'ok' },
            },
          ],
        },
      ],
    }

    await hooks['experimental.chat.messages.transform']({}, transformed)

    const restoredRead = transformed.messages[0].parts[0].state.input
    assert.equal(
      restoredRead.contract,
      undefined,
      'non-review tool read unexpected contract must be sanitized to None and never restored from vault',
    )
    assert.equal(
      restoredRead.estimated_readonly_rounds,
      2,
      'participating tool estimate must be restored normally from vault',
    )
    assert.equal(
      restoredRead.self_note,
      'note-c40',
      'participating tool note must be restored normally from vault',
    )

    const restoredReadOnlyContract = transformed.messages[0].parts[1].state.input
    assert.equal(
      restoredReadOnlyContract.contract,
      undefined,
      'non-participating tool carrying only contract must have empty vault entry and never restore contract',
    )

    const restoredNonParticipating = transformed.messages[0].parts[2].state.input
    assert.equal(
      restoredNonParticipating.estimated_readonly_rounds,
      undefined,
      'non-participating tool estimate must never be recorded into vault or restored into wire history',
    )
    assert.equal(
      restoredNonParticipating.self_note,
      undefined,
      'non-participating tool note must never be recorded into vault or restored into wire history',
    )
    assert.equal(
      restoredNonParticipating.contract,
      undefined,
      'tool not recognized as review tool must never have contract recorded into vault or restored',
    )
  })
})

test('WHAT[host-boundary-032] C41_unconfigured_predictor_no_interference_passthrough_and_no_vault', async () => {
  // Incident 2 回归测试：协议未开启时无操作（no-op）基线与免侵入保证
  //
  // 【真实源码依据与独立佐证】
  // 1. isDelegationActive 门控：
  //    - 源码位置：src/Wanxiangshu/OpenCode/Plugin/PluginHooks.fs:411-412
  //      `let isDelegationActive = readonlyDelegationPredictorConfigured () && isParticipatingTool`
  //    - 工具分类依据：src/Wanxiangshu/OpenCode/Plugin/PluginHooks.fs:409-410
  //      `let isParticipatingTool = InvestigationEstimateContract.classifyTool toolName = InvestigationEstimateContract.InvestigationToolPolicy.EstimateAfterCall`
  //    - 独立佐证 A：src/Wanxiangshu/Strength/InvestigationEstimateContract.fsi:18-24 导出的
  //      `InvestigationToolPolicy.EstimateAfterCall` 与 `classifyTool: toolName: string -> InvestigationToolPolicy`
  //    - 独立佐证 B：src/Wanxiangshu/OpenCode/Plugin/PluginHooks.fs:277-280 的
  //      `readonlyDelegationPredictorConfigured ()` 消费 `ModelRouting.sharedPredictorConfiguration ()`，
  //      当 Predictor 未配置时返回 false（见本测试套件 C28/C32 的实测断言）。
  // 2. toolBefore 行为守卫：
  //    - 源码位置：src/Wanxiangshu/OpenCode/Plugin/PluginHooks.fs:414 与 451-452
  //      参数校验（L414）与参数剥离（L451-452 `ReadonlyDelegationContract.hide toolOutput?args`）
  //      均由 `isDelegationActive` 强行门控。未配置时为 false，完全跳过校验与 hide。
  //    - 独立佐证：src/Wanxiangshu/OpenCode/Host/ReadonlyDelegationContract.fsi:31-37 明确规定
  //      非参与工具及未启用状态下保持零增量且参数原样透传。
  // 3. sanitizeSnapshot 快照清洗：
  //    - 源码位置：src/Wanxiangshu/OpenCode/Plugin/PluginHooks.fs:331-342
  //      `let isDelegationActive = readonlyDelegationPredictorConfigured () && InvestigationEstimateContract.classifyTool toolName = InvestigationEstimateContract.InvestigationToolPolicy.EstimateAfterCall`
  //      未配置时 `isDelegationActive` 为 false，`Snapshot.ReadonlyRounds` 与 `Snapshot.SelfNote` 均被强制清洗为 `None`。
  //    - commitRecordedSnapshot（src/Wanxiangshu/OpenCode/Plugin/PluginHooks.fs:349-357）检查三项均为 `None`
  //      时直接跳过 `ProtocolArgumentVault.record`，绝不向私有暂存（Vault）录入任何快照。
  //    - 独立佐证：src/Wanxiangshu/OpenCode/Host/ProtocolArgumentVault.fs:15-20 之 Snapshot 结构。
  //
  // 【当时条件】
  // Predictor 未配置（globalThis.__wanxiangshu_test_predictor_state 未设置或为 unconfigured）。
  //
  // 【本来应该】
  // 系统处于无操作（no-op）基线：
  // 1. toolBefore 不校验、不 hide 参与工具参数，漏填估计不报错，携带估计不剥离，业务参数原样透传；
  // 2. sanitizeSnapshot 将协议字段清洗为 None，不向 Vault 录入任何记录；
  // 3. experimental.chat.messages.transform 在还原历史时绝不恢复只读协议字段。
  //
  // 【实际上发生了什么】
  // 历史上 toolBefore 曾全局无差别执行 hide；sanitizeSnapshot 曾只按 isReviewTool 过滤，
  // 导致非参与工具或未配置环境下的协议字段可能逃脱清洗或被非预期篡改。
  //
  // 【可区分的 observable】
  // 1. 未配置时调用参与工具 read（即使漏填 estimated_readonly_rounds）正常执行，不抛 MissingEstimate；
  // 2. 若入参显式带有 estimated_readonly_rounds 与 self_note，toolBefore 不得对其进行 hide 剥离，输出 args 原样保留；
  // 3. transform 执行后，消息历史中绝不存在从 vault 恢复的协议字段。
  //
  // 【DevOps 物理变异验证路径】
  // 变异点 A（破坏 toolBefore 未配置门控）：
  //   修改 src/Wanxiangshu/OpenCode/Plugin/PluginHooks.fs:411-412，将 `isDelegationActive` 改为忽略配置：
  //   `let isDelegationActive = isParticipatingTool`
  //   预期结果：未配置下对漏填估计的调用（call-c41-read-omitted）将因触发校验抛出 MissingEstimate 异常而飘红，
  //   对携带估计的调用（call-c41-read-passthrough）将因字段被 hide 导致 passthroughOutput 断言失败飘红。
  // 变异点 B（破坏 sanitizeSnapshot 未配置门控）：
  //   修改 src/Wanxiangshu/OpenCode/Plugin/PluginHooks.fs:333-335，将 `isDelegationActive` 设为 `true`：
  //   预期结果：协议字段绕过清洗录入 Vault，下游 transform 错误恢复出字段，导致 restored 断言变红。
  clearPredictorState()
  try {
    await withExecutablePlugin(async (hooks, _directory, _createdIds, runtime) => {
      const sessionID = 'ses-c41'
      await openIncumbency(runtime, sessionID)

      // 观察点 1：未配置时参与工具漏填必填字段，不校验、不抛错，业务参数透传
      const omittedOutput = { args: { path: 'src/UnconfiguredOmitted.fs' } }
      await hooks['tool.execute.before'](
        { tool: 'read', sessionID, callID: 'call-c41-read-omitted' },
        omittedOutput,
      )
      assert.equal(
        omittedOutput.args.path,
        'src/UnconfiguredOmitted.fs',
        'unconfigured predictor must not reject omitted estimate on participating tool',
      )

      // 观察点 2：未配置时参与工具即使携带估计与短记，toolBefore 绝不 hide，原样透传给下游业务
      const passthroughOutput = {
        args: {
          path: 'src/UnconfiguredPassthrough.fs',
          estimated_readonly_rounds: 3,
          self_note: 'unconfigured-note',
        },
      }
      await hooks['tool.execute.before'](
        { tool: 'read', sessionID, callID: 'call-c41-read-passthrough' },
        passthroughOutput,
      )
      assert.equal(
        passthroughOutput.args.estimated_readonly_rounds,
        3,
        'unconfigured predictor must not hide estimated_readonly_rounds from tool args',
      )
      assert.equal(
        passthroughOutput.args.self_note,
        'unconfigured-note',
        'unconfigured predictor must not hide self_note from tool args',
      )

      // 观察点 3：未配置下绝不录入 vault，历史消息 transform 绝不恢复协议字段
      const transformed = {
        messages: [
          {
            role: 'assistant',
            info: { id: 'asst-c41', sessionID },
            parts: [
              {
                type: 'tool',
                tool: 'read',
                callID: 'call-c41-read-passthrough',
                state: {
                  status: 'completed',
                  input: { path: 'src/UnconfiguredPassthrough.fs' },
                  output: 'ok',
                },
              },
            ],
          },
        ],
      }

      await hooks['experimental.chat.messages.transform']({}, transformed)
      const restored = transformed.messages[0].parts[0].state.input
      assert.equal(
        restored.estimated_readonly_rounds,
        undefined,
        'unconfigured predictor must never record into vault or restore estimated_readonly_rounds in transform',
      )
      assert.equal(
        restored.self_note,
        undefined,
        'unconfigured predictor must never record into vault or restore self_note in transform',
      )
    })
  } finally {
    clearPredictorState()
  }
})

test('WHAT[host-boundary-032] C42_mid_flight_predictor_revocation_restores_parameters_from_snapshot', async () => {
  // Incident 3 回归测试：调用中途撤销配置时 toolAfter 仍同源还原协议参数
  //
  // 【真实源码依据与独立佐证】
  // 1. toolAfter 真实恢复逻辑：
  //    - 源码位置：src/Wanxiangshu/OpenCode/Plugin/PluginHooks.fs:455-469
  //      ```fsharp
  //      let toolAfter (toolInput: obj) (toolOutput: obj) =
  //          task {
  //              let toolName = toolField toolInput "tool"
  //              let isParticipatingTool =
  //                  InvestigationEstimateContract.classifyTool toolName = InvestigationEstimateContract.InvestigationToolPolicy.EstimateAfterCall
  //
  //              if not (isNull toolInput) && not (isNull toolInput?args) then
  //                  if isParticipatingTool then
  //                      ReadonlyDelegationContract.restore toolInput?args
  //                  ManagerReviewContract.restore toolInput?args
  //      ```
  //    - 独立佐证 A：src/Wanxiangshu/Strength/InvestigationEstimateContract.fsi:18-24 导出的
  //      `InvestigationToolPolicy.EstimateAfterCall` 与 `classifyTool`。
  //    - 独立佐证 B：src/Wanxiangshu/OpenCode/Plugin/PluginHooks.fs:458-467 内部实读：
  //      恢复仅依据 `isParticipatingTool`，完全不调用也不受 `readonlyDelegationPredictorConfigured ()` 约束！
  // 2. ReadonlyDelegationContract.restore 私有 Symbol 恢复机制：
  //    - 源码位置：src/Wanxiangshu/OpenCode/Host/ReadonlyDelegationContract.fs:17
  //      `let private savedArgsKey: obj = emitJsExpr () "Symbol('readonly-delegation-args')"`
  //    - 源码位置：src/Wanxiangshu/OpenCode/Host/ReadonlyDelegationContract.fs:181-187 (`hideProtocolFields`)
  //      使用 `defineProperty args savedArgsKey symbolDescriptor` 将原始 descriptor 存入私有 Symbol；
  //    - 源码位置：src/Wanxiangshu/OpenCode/Host/ReadonlyDelegationContract.fs:223-228 (`restore`)
  //      ```fsharp
  //      let restore (args: obj) : unit =
  //          if not (isNull args) && isPlainObject args && hasOwn args savedArgsKey then
  //              restoreProtocolFields args
  //      ```
  //    - 独立佐证：src/Wanxiangshu/OpenCode/Host/ReadonlyDelegationContract.fsi:39-43 明确保证：
  //      `val restore: args: obj -> unit`
  //      "Restores both protocol fields from the private module Symbol on the args object. Idempotent..."
  //      恢复过程完全基于对象自有私有 Symbol，与任何外部全局配置无关。
  //
  // 【当时条件】
  // 1. toolBefore 执行时 Predictor 为已配置状态（configured）；
  // 2. 参与工具 read 携带合法的只读估计与短记，toolBefore 成功校验、录入快照并 hide 剥离字段；
  // 3. 在 toolBefore 之后、toolAfter 之前，Predictor 配置被撤销（变为 unconfigured）；
  // 4. toolAfter 随后被触发执行。
  //
  // 【本来应该】
  // toolAfter 必须依据当前调用挂在 args 上的私有 Symbol 快照，确定性地将原始 arguments
  // （含 estimated_readonly_rounds 与 self_note）同源恢复回 args，不依赖撤销后的全局配置。
  //
  // 【实际上发生了什么】
  // 若 toolAfter 在执行恢复时错误引入了全局配置状态判断（例如重读配置发现为 unconfigured），
  // 就会误跳过恢复逻辑，导致被 hide 剥离的协议字段永久丢失，破坏下游证据链。
  //
  // 【可区分的 observable】
  // 中途撤销配置后，toolAfter 执行完毕时，toolOutput.args 中的 estimated_readonly_rounds (2) 和
  // self_note ('mid-flight-verification') 是否被完整还原。
  //
  // 【DevOps 物理变异验证路径】
  // 变异点（让 toolAfter 错误依赖运行时配置）：
  //   修改 src/Wanxiangshu/OpenCode/Plugin/PluginHooks.fs:466，将 `if isParticipatingTool then` 改为：
  //   `if isParticipatingTool && readonlyDelegationPredictorConfigured () then`
  //   预期结果：由于中途执行了 clearPredictorState()，toolAfter 发现 Predictor 未配置因而跳过恢复，
  //   导致 toolOutput.args.estimated_readonly_rounds 仍为 undefined，
  //   断言 assert.equal(toolOutput.args.estimated_readonly_rounds, 2) 必然失败变红。
  setPredictorState('configured')
  try {
    await withExecutablePlugin(async (hooks, _directory, _createdIds, runtime) => {
      const sessionID = 'ses-c42'
      await openIncumbency(runtime, sessionID)

      const callID = 'call-c42-read-revocation'
      const toolInput = { tool: 'read', sessionID, callID }
      const toolOutput = {
        args: {
          path: 'src/MidFlightRevocation.fs',
          estimated_readonly_rounds: 2,
          self_note: 'mid-flight-verification',
        },
      }

      // Step 1: 在 Predictor 已配置下执行 toolBefore
      await hooks['tool.execute.before'](toolInput, toolOutput)

      // 验证 toolBefore 已正确 hide 剥离字段
      assert.equal(
        toolOutput.args.estimated_readonly_rounds,
        undefined,
        'toolBefore must hide estimated_readonly_rounds when predictor is configured',
      )
      assert.equal(
        toolOutput.args.self_note,
        undefined,
        'toolBefore must hide self_note when predictor is configured',
      )
      assert.equal(
        toolOutput.args.path,
        'src/MidFlightRevocation.fs',
        'business path argument must be preserved intact',
      )

      // Step 2: 中途撤销 Predictor 配置
      clearPredictorState()

      // Step 3: 在配置已撤销的状态下执行 toolAfter
      await hooks['tool.execute.after'](toolInput, toolOutput)

      // Step 4: 断言 toolAfter 必须成功通过私有快照还原原始协议参数
      assert.equal(
        toolOutput.args.estimated_readonly_rounds,
        2,
        'toolAfter must restore estimated_readonly_rounds from private snapshot even after predictor is revoked mid-flight',
      )
      assert.equal(
        toolOutput.args.self_note,
        'mid-flight-verification',
        'toolAfter must restore self_note from private snapshot even after predictor is revoked mid-flight',
      )
      assert.equal(
        toolOutput.args.path,
        'src/MidFlightRevocation.fs',
        'business path argument must remain intact after restoration',
      )
    })
  } finally {
    clearPredictorState()
  }
})

test('WHAT[host-boundary-032] C43_participating_tool_missing_estimate_rejected_before_business_body', async () => {
  // Incident 1 回归测试：hasProtocolFields 守卫绕过缺陷变成可执行记忆
  //
  // 【当时成立条件】
  // 1. Predictor 已配置（globalThis.__wanxiangshu_test_predictor_state = 'configured'）；
  // 2. 被调用的工具经 InvestigationEstimateContract.classifyTool 判定为参与工具（如 'read' 为 EstimateAfterCall）；
  // 3. 本次调用的 arguments 完全缺失协议字段（既无 estimated_readonly_rounds 也无 self_note，例如仅有 { path: 'src/MissingEstimateRepro.fs' }）。
  //
  // 【本该发生什么】
  // 依据 WHAT[host-boundary-032] 第 148 行与 DELEGATE §9.1 规范：
  // 参与工具在 Predictor 已配置时必须提供合法的 estimated_readonly_rounds。
  // 当参数完全缺失协议字段时，必须在工具业务 body 执行之前以参数错误拒绝，
  // 错误标识为 MissingEstimate（抛出 Invalid investigation estimate arguments: 必须提供...），
  // 业务 body 零执行，输入参数对象零污染、零改写。
  //
  // 【实际发生了什么（生产事故真实原因）】
  // 历史上 toolBefore 上曾存在一道 `hasProtocolFields` 守卫：
  // `let hasProtocolFields = hasOwnProperty args "estimated_readonly_rounds" || hasOwnProperty args "self_note"`
  // 并仅在 `isDelegationActive && hasProtocolFields` 时才进入参数校验分支。
  // 当调用方完全未传两个协议字段时，`hasProtocolFields` 为 false，整个校验分支被跳过，
  // MissingEstimate 在生产链路不可达，工具业务 body 被错误放行执行，§9.1 门控失去强制力。
  //
  // 【真实源码修复方案与支撑佐证】
  // 1. 拆除 hasProtocolFields 守卫，改用 isDelegationActive 刚性门控：
  //    - 源码位置：src/Wanxiangshu/OpenCode/Plugin/PluginHooks.fs:409-414
  //      ```fsharp
  //      let isParticipatingTool =
  //          InvestigationEstimateContract.classifyTool toolName = InvestigationEstimateContract.InvestigationToolPolicy.EstimateAfterCall
  //      let isDelegationActive =
  //          readonlyDelegationPredictorConfigured () && isParticipatingTool
  //
  //      if isDelegationActive && not (isNull toolOutput) && not (isNull toolOutput?args) then
  //          let args = toolOutput?args
  //          match InvestigationEstimateContract.parseParticipatingArguments args with
  //          | Ok _ -> ()
  //          | Error err ->
  //              ...
  //              let explanation = InvestigationEstimateContract.formatArgumentError language err
  //              invalidOp (sprintf "Invalid investigation estimate arguments: %s" explanation)
  //      ```
  // 2. parseParticipatingArguments 严格产生 MissingEstimate：
  //    - 源码位置：src/Wanxiangshu/Strength/InvestigationEstimateContract.fs:153-154
  //      `elif not (hasOwn arguments EstimatedReadonlyRoundsField) then Error EstimateArgumentError.MissingEstimate`
  //    - 错误文案输出：src/Wanxiangshu/Strength/InvestigationEstimateContract.fs:172-173 (zh) 与 190-191 (en)
  //      "必须提供 estimated_readonly_rounds 估计字段" / "The estimated_readonly_rounds field must be provided"
  //    - 独立佐证 A：src/Wanxiangshu/Strength/InvestigationEstimateContract.fsi:35-36 导出的 `EstimateArgumentError.MissingEstimate`
  //    - 独立佐证 B：src/Wanxiangshu/Strength/InvestigationEstimateContract.fsi:46-47 导出的 `parseParticipatingArguments` 签名
  //    - 独立佐证 C：requirements/host-boundary/WHAT.md [032] 第 148 行参数清理与非法输入 before 阶段拒绝条款。
  //
  // 【可区分的 observable】
  // 1. 参与工具 read 在 Predictor 已配置且 arguments 没有任何协议字段时，调用 hooks['tool.execute.before']
  //    必须被拒绝（reject），抛出的错误信息精确匹配 MissingEstimate 文案；
  // 2. 拒绝发生后，toolOutput.args 对象的内容与键结构完全未被改写或污染（业务零副作用）；
  // 3. 对照组：非参与工具 join 在 Predictor 已配置且缺少协议字段时，toolBefore 属于 no-op 路径，必须正常放行通过、不抛错。
  //
  // 【DevOps 物理变异验证路径】
  // 变异点 1（原样重现 hasProtocolFields 守卫缺陷）：
  //   修改 src/Wanxiangshu/OpenCode/Plugin/PluginHooks.fs:414，在条件中加回协议字段存在性守卫：
  //   `let hasProtocolFields = hasOwnProperty toolOutput?args "estimated_readonly_rounds" || hasOwnProperty toolOutput?args "self_note"`
  //   `if isDelegationActive && hasProtocolFields && not (isNull toolOutput) && not (isNull toolOutput?args) then`
  //   预期结果：当调用 read 时完全未传协议字段，`hasProtocolFields` 为 false 导致校验被跳过，
  //   `hooks['tool.execute.before']` 将不会抛出异常而成功 resolve，
  //   导致本测试中的 `await assert.rejects(...)` 失败变红！
  // 变异点 2（守卫只看 Predictor 配置而不判定是否为参与工具）：
  //   修改 src/Wanxiangshu/OpenCode/Plugin/PluginHooks.fs:411-412：
  //   `let isDelegationActive = readonlyDelegationPredictorConfigured ()`
  //   预期结果：非参与工具（如 join）在未带协议字段时也将被强制执行参与工具校验，
  //   导致对照组 `await hooks['tool.execute.before']({ tool: 'join', ... })` 抛出 MissingEstimate 异常，
  //   导致本测试中对 join 正常放行的断言失败变红！
  setPredictorState('configured')
  try {
    await withExecutablePlugin(async (hooks, _directory, _createdIds, runtime) => {
      const sessionID = 'ses-c43'
      await openIncumbency(runtime, sessionID)

      // 观察点 1：参与工具（read）在 Predictor 配置下完全未传两个协议字段
      const participatingOutput = {
        args: {
          path: 'src/MissingEstimateRepro.fs',
        },
      }
      const originalArgs = structuredClone(participatingOutput.args)

      // 必须在业务 body 之前被 toolBefore 拒绝，且抛出 MissingEstimate 错误
      await assert.rejects(
        async () => {
          await hooks['tool.execute.before'](
            { tool: 'read', sessionID, callID: 'call-c43-missing-estimate' },
            participatingOutput,
          )
        },
        /Invalid investigation estimate arguments: (必须提供 estimated_readonly_rounds 估计字段|The estimated_readonly_rounds field must be provided)/,
        'participating tool missing estimated_readonly_rounds must be rejected on tool.execute.before with MissingEstimate',
      )

      // 业务 body 零副作用：拒绝发生在业务执行前，且输入对象未被改写或污染
      assert.deepEqual(
        participatingOutput.args,
        originalArgs,
        'rejected call must leave tool arguments untouched with zero business side effect',
      )

      // 观察点 2：对照组非参与工具（join）在 Predictor 配置下完全未传协议字段
      // 必须正常透传放行，不抛出异常，不产生协议增量
      const nonParticipatingOutput = {
        args: {
          taskID: 'task-c43-join',
        },
      }
      await hooks['tool.execute.before'](
        { tool: 'join', sessionID, callID: 'call-c43-join-passthrough' },
        nonParticipatingOutput,
      )
      assert.equal(
        nonParticipatingOutput.args.taskID,
        'task-c43-join',
        'non-participating tool without protocol fields must pass through untouched without rejection',
      )
      assert.equal(
        'estimated_readonly_rounds' in nonParticipatingOutput.args,
        false,
        'non-participating tool must not gain estimated_readonly_rounds',
      )
      assert.equal(
        'self_note' in nonParticipatingOutput.args,
        false,
        'non-participating tool must not gain self_note',
      )
    })
  } finally {
    clearPredictorState()
  }
})
