import assert from 'node:assert/strict'
import test from 'node:test'
import * as Ablation from '../../../dist/Ablation/Surface.js'

// Ablation memoises the manifest on first load, so restoring the environment is not
// enough on its own: the cache would keep answering for the profile that loaded it,
// and leak into whichever test loads it next. The cache goes down with the env.
const withEnv = (entries, run) => {
  const previous = Object.fromEntries(entries.map(([name]) => [name, process.env[name]]))
  try {
    for (const [name, value] of entries) {
      if (value === undefined) delete process.env[name]
      else process.env[name] = value
    }
    Ablation.resetRegistry()
    run()
  } finally {
    for (const [name, value] of Object.entries(previous)) {
      if (value === undefined) delete process.env[name]
      else process.env[name] = value
    }
    Ablation.resetRegistry()
  }
}

test('WHAT[feature-ablation-002] ABL_002_tri_state_semantics_distinction', () => {
  // 1. ablated 状态：该切面不启用；不得改变可见工具、行为或等价副作用——既不得出现在 provider schema，也不得被 execution gate 放行
  withEnv([
    ['WANXIANGSHU_ABLATION_PROFILE', 'station-41'],
    ['WANXIANGSHU_ABLATION_speculative_investigation', 'ablated'],
  ], () => {
    Ablation.load()
    assert.equal(Ablation.allowsTool('speculate'), false, 'ablated node tool must not be allowed')
    assert.equal(Ablation.allowsToolSchema('speculate'), false, 'ablated node tool schema must not be allowed')
  })

  // 2. active 状态：包级机制按各自 WHAT 正常运行，工具在 schema 暴露且门禁放行
  withEnv([
    ['WANXIANGSHU_ABLATION_PROFILE', 'production'],
    ['WANXIANGSHU_ABLATION_speculative_investigation', 'active'],
  ], () => {
    Ablation.load()
    assert.equal(Ablation.allowsTool('read'), true, 'active node tool must be allowed')
    assert.equal(Ablation.allowsToolSchema('read'), true, 'active node tool schema must be allowed')
  })

  // 3. borrowed 状态：仅 resources/ablation/nodes.json 与 manifest 明示的借用面可运行；不得触发完整包级下游语义
  withEnv([['WANXIANGSHU_ABLATION_PROFILE', 'station-14']], () => {
    Ablation.load()
    // station-14 中 delegation 处于 borrowed/ablated 切面：允许读，但不允许完整 async fork/fission 语义
    assert.equal(Ablation.allowsTool('read'), true, 'borrowed foundation tool read must be allowed')
    assert.equal(Ablation.allowsTool('fork'), false, 'borrowed package full downstream tool fork must be denied')
    assert.equal(Ablation.fissionVisible(), false, 'borrowed package downstream fission must remain ablated')
  })

  // 4. 非法状态组合 fail-closed：传入非法模式字符串必须抛出异常拒绝加载
  withEnv([['WANXIANGSHU_ABLATION_speculative_investigation', 'invalid_mode']], () => {
    assert.throws(() => {
      Ablation.load()
    }, /InvalidMode|fail-closed|ablation/i, 'Invalid ablation mode string must fail-closed')
  })
})

test('WHAT[feature-ablation-002] ABL_002_borrowed_surface_enforcement', () => {
  // active 全放行
  withEnv([['WANXIANGSHU_ABLATION_PROFILE', 'production']], () => {
    Ablation.load()
    assert.equal(Ablation.allowsFact('AgentFact.Delegation'), true)
    assert.equal(Ablation.allowsFact('AgentFact.Execution'), true)
    assert.equal(Ablation.allowsTool('fork'), true)
  })

  // ablated 全拒绝
  withEnv([['WANXIANGSHU_ABLATION_PROFILE', 'station-05']], () => {
    Ablation.load()
    assert.equal(Ablation.allowsFact('AgentFact.Delegation'), false)
    assert.equal(Ablation.allowsFact('AgentFact.Execution'), false)
    assert.equal(Ablation.allowsTool('fork'), false)
  })

  // borrowed 状态：仅命中本节点或父节点 borrowed_surface 清单的项放行，清单外拒绝
  withEnv([['WANXIANGSHU_ABLATION_PROFILE', 'station-15']], () => {
    Ablation.load()
    // delegation 节点在 nodes.json 中明示 borrowed_surface 包含 AgentFact.Delegation / AgentFact.Execution / delegation.sync-delegate
    assert.equal(Ablation.allowsFact('AgentFact.Delegation'), true, 'fact in borrowed_surface must be allowed')
    assert.equal(Ablation.allowsFact('AgentFact.Execution'), true, 'fact in borrowed_surface must be allowed')
    // fork 属于 delegation.async-fork，不在 delegation 借用面中，必须被拒绝
    assert.equal(Ablation.allowsTool('fork'), false, 'tool outside borrowed_surface must be denied')
    // AgentFact.Relay 属于 relay-incumbency（在 station-15 为 ablated 且无借用），必须被拒绝
    assert.equal(Ablation.allowsFact('AgentFact.Relay'), false, 'unborrowed fact must be denied')
  })
})

test('WHAT[feature-ablation-002] ABL_002_station_05_denies_downstream_tools', () => {
  withEnv([['WANXIANGSHU_ABLATION_PROFILE', 'station-05']], () => {
    Ablation.load()
    assert.equal(Ablation.allowsTool('fork'), false)
    assert.equal(Ablation.allowsToolSchema('fork'), false)
    assert.equal(Ablation.allowsTool('fission'), false)
    assert.equal(Ablation.allowsTool('review'), false)
    assert.equal(Ablation.allowsTool('commission'), false)
    assert.equal(Ablation.allowsTool('read'), true)
  })
})

test('WHAT[feature-ablation-002] ABL_002_station_14_keeps_engineer_surface_and_ablates_manager_tools', () => {
  withEnv([['WANXIANGSHU_ABLATION_PROFILE', 'station-14']], () => {
    Ablation.load()
    assert.equal(Ablation.allowsTool('read'), true)
    assert.equal(Ablation.allowsTool('glob'), true)
    assert.equal(Ablation.allowsTool('grep'), true)
    assert.equal(Ablation.allowsTool('fork'), false)
    assert.equal(Ablation.allowsTool('js-manager'), false)
    assert.equal(Ablation.allowsPrimaryAgent('manager'), false)
    assert.equal(Ablation.fissionVisible(), false)
  })
})

test('WHAT[feature-ablation-002] ABL_002_primary_agents_and_legacy_inquiry_agent_are_gated_together', () => {
  // 1. station-05 下 relay-incumbency 与 change-integration 为 ablated:
  // manager 与 orchestrator 必须被拒绝 (allowsPrimaryAgent === false)
  // browser 恒 false (fail-closed)
  withEnv([['WANXIANGSHU_ABLATION_PROFILE', 'station-05']], () => {
    Ablation.load()
    assert.equal(
      Ablation.allowsPrimaryAgent('manager'),
      false,
      'station-05 must reject manager when relay-incumbency is ablated',
    )
    assert.equal(
      Ablation.allowsPrimaryAgent('orchestrator'),
      false,
      'station-05 must reject orchestrator when change-integration is ablated',
    )
    assert.equal(Ablation.allowsPrimaryAgent('browser'), false, 'browser agent must be fail-closed false')
    assert.equal(Ablation.allowsPrimaryAgent('inquiry'), false, 'inquiry agent must be false when epistemic-reasoning is ablated')
  })

  // 2. production profile 下两者均为 active:
  // manager 与 orchestrator 必须允许 (allowsPrimaryAgent === true)
  // browser 即使在 production 下也恒 false
  withEnv([['WANXIANGSHU_ABLATION_PROFILE', 'production']], () => {
    Ablation.load()
    assert.equal(
      Ablation.allowsPrimaryAgent('manager'),
      true,
      'production profile must allow manager primary agent',
    )
    assert.equal(
      Ablation.allowsPrimaryAgent('orchestrator'),
      true,
      'production profile must allow orchestrator primary agent',
    )
    assert.equal(
      Ablation.allowsPrimaryAgent('browser'),
      false,
      'browser must remain false even in production',
    )
  })

})

test('WHAT[feature-ablation-002] ABL_002_station_05_denies_ablated_durable_fact_tags', () => {
  withEnv([['WANXIANGSHU_ABLATION_PROFILE', 'station-05']], () => {
    Ablation.load()
    assert.equal(Ablation.allowsFact('AgentFact.Delegation'), false)
    assert.equal(Ablation.allowsFact('AgentFact.Relay'), false)
    assert.equal(Ablation.allowsFact('AgentFact.Cognition'), false)
    // The retired ledger family is not mapped at all, so it is neither granted nor
    // denied by a node: an unmapped tag stays allowed as the unowned default.
    assert.equal(Ablation.allowsFact('MagicTodo'), true)
    assert.equal(Ablation.allowsFact('AgentFact.UnmappedFamily'), true)
  })
})

test('WHAT[feature-ablation-002] ABL_002_station_15_borrows_delegation_facts', () => {
  withEnv([['WANXIANGSHU_ABLATION_PROFILE', 'station-15']], () => {
    Ablation.load()
    assert.equal(Ablation.allowsFact('AgentFact.Delegation'), true)
    assert.equal(Ablation.allowsFact('AgentFact.Execution'), true)
    assert.equal(Ablation.allowsFact('AgentFact.Relay'), false)
  })
})

test('WHAT[feature-ablation-002] ABL_002_strength_forced_off_when_speculation_ablated', () => {
  withEnv([['WANXIANGSHU_ABLATION_PROFILE', 'station-05']], () => {
    Ablation.load()
    assert.equal(Ablation.strengthForcedOff(), true)
  })
})

test('WHAT[feature-ablation-002] ABL_002_primary_agent_admission_follows_the_selected_profile', () => {
  withEnv([['WANXIANGSHU_ABLATION_PROFILE', 'station-05']], () => {
    Ablation.load()
    assert.equal(Ablation.allowsPrimaryAgent('manager'), false, 'station-05 must reject manager when relay-incumbency is ablated')
    assert.equal(Ablation.allowsPrimaryAgent('orchestrator'), false, 'station-05 must reject orchestrator when change-integration is ablated')
    assert.equal(Ablation.allowsPrimaryAgent('browser'), false, 'browser must be fail-closed false')
    assert.equal(Ablation.allowsPrimaryAgent('inquiry'), false, 'inquiry must be false when epistemic-reasoning is ablated')
  })

  withEnv([['WANXIANGSHU_ABLATION_PROFILE', 'production']], () => {
    Ablation.load()
    assert.equal(Ablation.allowsPrimaryAgent('manager'), true, 'production must allow manager')
    assert.equal(Ablation.allowsPrimaryAgent('orchestrator'), true, 'production must allow orchestrator')
    assert.equal(Ablation.allowsPrimaryAgent('browser'), false, 'browser must remain false in production')
  })
})

test('WHAT[feature-ablation-002] ABL_002_ablated_package_tool_is_refused_at_the_real_execute_gate', async () => {
  const { integrationTest } = await import('../../verification-system/tests/support/tier-gate.mjs')
  const { acceptAuthorityRoot, withExecutablePlugin } = await import('../../verification-system/tests/support/plugin-fixture.mjs')

  // 单点覆盖 repository-programming：mv/rm 映射到该节点。消融门在 admission 与工具体之前
  // 拒绝，因此本用例不依赖 Engineer 的具体授权，也不产生任何文件副作用。
  // station-05 marks repository-programming ablated, which also ablates its
  // downstream interaction-authority (the Prompt fact). The registry is
  // therefore switched after the authority root is admitted: the fixture
  // precondition writes under the active default, then the rm execute gate
  // reads the ablated profile at call time.
  const override = 'WANXIANGSHU_ABLATION_PROFILE'
  const previous = process.env[override]
  Ablation.resetRegistry()
  try {
    await integrationTest('WHAT[feature-ablation-002] ABL_002_ablated_package_tool_is_refused_at_the_real_execute_gate', async () => {
      await withExecutablePlugin(async (hooks, _directory, _createdIds, runtime) => {
        await acceptAuthorityRoot(runtime, 'ses-ablation-gate', 'engineer')
        process.env[override] = 'station-05'
        Ablation.resetRegistry()
        const result = await hooks.tool.rm.execute(
          { path: 'ablation-probe' },
          { sessionID: 'ses-ablation-gate', agent: 'engineer' },
        )
        assert.match(result, /消融|ablated/i)
      })
    })
  } finally {
    if (previous === undefined) delete process.env[override]
    else process.env[override] = previous
    Ablation.resetRegistry()
  }
})

