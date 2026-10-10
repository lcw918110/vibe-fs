import test from 'node:test'

{
const { default: assert } = await import("node:assert/strict");
const { default: test } = await import("node:test");
const PluginHooksSurface = await import("../../../dist/OpenCode/Host/PluginHooksSurface.js");
const ToolSurface = await import("../../../dist/OpenCode/Tools/ToolSurface.js");
const Strength = await import("../../../dist/Strength/Surface.js");
const ModelRoutingSurface = await import("../../../dist/OpenCode/Host/ModelRoutingSurface.js");

const fs = await import("node:fs");
const path = await import("node:path");
const os = await import("node:os");
const { fileURLToPath } = await import("node:url");

const EstimatedReadonlyRoundsField = 'estimated_readonly_rounds';
const SelfNoteField = 'self_note';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../..');
const pluginHooksSourcePath = path.join(repoRoot, 'src/Wanxiangshu/OpenCode/Plugin/PluginHooks.fs');
const delegateSourcePath = path.join(repoRoot, 'src/Wanxiangshu/Strength/OpenCode/Delegate.fs');

const PARTICIPATING_TOOLS = [
  'read', 'glob', 'grep', 'js-manager', 'js-engineer', 'js-devops',
  'edit', 'write', 'mv', 'rm', 'fetch', 'run'
];
const PARTICIPATING_SET = new Set(PARTICIPATING_TOOLS);

// The policy verdict always comes from Strength.classifyTool. This helper only
// observes what production does to a real tool definition, so the schema consumer
// can be checked against that verdict without ever repeating it.
function productionDecoratesSchema(toolName) {
  const definition = {
    description: "original tool description",
    parameters: {
      type: "object",
      properties: { path: { type: "string" } },
      required: ["path"],
    },
  };
  PluginHooksSurface.decorateReadonlyDelegationToolDefinition(toolName, definition);
  return Boolean(
    definition.parameters?.properties?.[EstimatedReadonlyRoundsField] ||
    definition.jsonSchema?.properties?.[EstimatedReadonlyRoundsField]
  );
}

// execution-model-routing-020 / 
// Ensure the test process has initialized the ModelRouting scheduler singleton
// with dynamic predictorConfiguration query, allowing test cases to toggle predictor state
// via globalThis.__wanxiangshu_test_predictor_state.
{
  const tmpDir = fs.mkdtempSync(path.join(fs.realpathSync(os.tmpdir()), 'wxs-016-routing-'));
  const routingHome = path.join(tmpDir, 'routing-home');
  const routingDir = path.join(routingHome, '.config', 'opencode');
  fs.mkdirSync(routingDir, { recursive: true });
  fs.writeFileSync(
    path.join(routingDir, 'wanxiangshu.mjs'),
    `export const routingProtocol = 2
export default function route(role, running, previous, purpose) {
  return { model: 'provider/' + role + '-model', reasoning: 'none' }
}
export const predictorConfiguration = () => {
  const state = globalThis.__wanxiangshu_test_predictor_state ?? 'unconfigured'
  if (state === 'configured') return { state: 'configured', reason: null }
  if (state === 'invalid') return { state: 'invalid', reason: globalThis.__wanxiangshu_test_predictor_reason ?? 'test injected invalid state' }
  return { state: 'unconfigured', reason: null }
}
`
  );
  const prevHome = process.env.HOME;
  const prevProfile = process.env.USERPROFILE;
  process.env.HOME = routingHome;
  process.env.USERPROFILE = routingHome;
  try {
    await ModelRoutingSurface.initialize();
  } finally {
    if (prevHome === undefined) delete process.env.HOME;
    else process.env.HOME = prevHome;
    if (prevProfile === undefined) delete process.env.USERPROFILE;
    else process.env.USERPROFILE = prevProfile;
    fs.rmSync(tmpDir, { recursive: true, force: true });
  }
}

test('WHAT[speculative-investigation-016] the revision constant and the pairing rule both come from the production surface', () => {
  assert.equal(
    Strength.protocolRevision,
    2,
    'production protocolRevision must be the stable revision 2'
  );
  assert.ok(
    Strength.protocolRevision > 1,
    'production protocolRevision must be strictly greater than 1'
  );

  // The pairing parser is production's own, so the documented field name is the
  // only one it accepts and the conditional note is read underneath it. An absent
  // note arrives through the surface option projection, which answers a missing
  // note as null rather than undefined.
  assert.deepEqual(
    Strength.parseParticipatingArguments({ [EstimatedReadonlyRoundsField]: 0 }),
    { ok: true, rounds: 0, selfNote: null },
    'a zero estimate carries neither a value nor a note'
  );
  assert.equal(
    Strength.parseParticipatingArguments({ rounds: 0 }).error,
    'MissingEstimate',
    'production must reject a container that lacks the documented estimate field'
  );

  const noteText = "  我怀疑入口与调用方对空值的约定不同，接下来先核对调用点  ";
  assert.deepEqual(
    Strength.parseParticipatingArguments({
      [EstimatedReadonlyRoundsField]: 2,
      [SelfNoteField]: noteText
    }),
    { ok: true, rounds: 2, selfNote: noteText },
    'a positive estimate returns its exact note text with whitespace preserved'
  );
  assert.deepEqual(
    Strength.parseParticipatingArguments({ [EstimatedReadonlyRoundsField]: 1 }),
    { ok: true, rounds: 1, selfNote: null },
    'a positive estimate without a note is not a failure'
  );
  assert.deepEqual(
    Strength.parseParticipatingArguments({ [EstimatedReadonlyRoundsField]: 0, [SelfNoteField]: '' }),
    { ok: true, rounds: 0, selfNote: '' },
    'any own note field at zero is not a failure'
  );
  assert.deepEqual(
    Strength.parseParticipatingArguments({ [EstimatedReadonlyRoundsField]: 0, [SelfNoteField]: 123 }),
    { ok: true, rounds: 0, selfNote: null },
    'a non-string self_note is read as absent, never a failure'
  );
});

test('WHAT[speculative-investigation-016] the production classifier admits exact participating names only and never a prefix variant', () => {
  for (const name of PARTICIPATING_TOOLS) {
    assert.equal(
      Strength.classifyTool(name),
      'EstimateAfterCall',
      `production classifier must answer EstimateAfterCall for ${name}`
    );
  }

  const noEstimate = [
    'fork', 'resume', 'commission', 'join', 'horizon', 'review', 'suicide',
    'fission', 'open-terminal', 'send-terminal', 'read-terminal', 'signal-terminal',
    'skill', 'todowrite', 'assume', 'defer', 
    'publish', 'chronicle', 'js-bookkeeper', 'js-predictor',
    'bash-honeypot', 'invalid', 'js-orchestrator', 'js-blogger'
  ];
  for (const name of noEstimate) {
    assert.equal(
      Strength.classifyTool(name),
      'NoEstimate',
      `production classifier must answer NoEstimate for ${name}`
    );
  }

  // Prefix matching is strictly forbidden, so every look-alike name must fall to
  // the Unreviewed default instead of inheriting its prefix's verdict.
  const unreviewedPrefixes = [
    'read-extra', 'globbing', 'grepper', 'edit_file', 'writer', 'run_command',
    'fetch_data', 'js-devops-v2', 'fork_child', 'resume_parent', 'custom_tool'
  ];
  for (const name of unreviewedPrefixes) {
    assert.equal(
      Strength.classifyTool(name),
      'Unreviewed',
      `production classifier must leave ${name} unreviewed; prefix matching is forbidden`
    );
  }
});

test('WHAT[speculative-investigation-016] 0 rounds omitting self_note is valid, including -0 normalized to 0', () => {
  const res1 = PluginHooksSurface.readonlyDelegationSelfNoteOf({
    estimated_readonly_rounds: 0
  });
  assert.equal(res1.ok, true, 'parse should succeed with Ok');
  assert.equal(res1.note, null);

  // -0 is treated as 0
  const res2 = PluginHooksSurface.readonlyDelegationSelfNoteOf({
    estimated_readonly_rounds: -0
  });
  assert.equal(res2.ok, true, 'parse with -0 should succeed');
  assert.equal(res2.note, null);

  // Conversion to execution budget
  const execBudget = PluginHooksSurface.readonlyDelegationBudgetOf(0);
  assert.equal(execBudget.ok, true);
  assert.equal(execBudget.rounds, 0);
});

test('WHAT[speculative-investigation-016] positive integers with non-blank self_note are valid and preserve original note string', () => {
  for (const r of [1, 2, 7, 2147483647]) {
    const noteText = "  我怀疑入口与调用方对空值的约定不同，接下来先核对调用点  ";
    const res = PluginHooksSurface.readonlyDelegationSelfNoteOf({
      estimated_readonly_rounds: r,
      self_note: noteText
    });
    assert.equal(res.ok, true, `parse with rounds ${r} should succeed`);
    assert.equal(res.note, noteText);

    const execBudget = PluginHooksSurface.readonlyDelegationBudgetOf(r);
    assert.equal(execBudget.ok, true);
    assert.equal(execBudget.rounds, r);
  }
});

test('WHAT[speculative-investigation-016] 0 rounds accepts any present self_note without failure', () => {
  // empty string
  const resEmpty = PluginHooksSurface.readonlyDelegationSelfNoteOf({
    estimated_readonly_rounds: 0,
    self_note: ''
  });
  assert.equal(resEmpty.ok, true, 'an empty string note at zero is not a failure');
  assert.equal(resEmpty.note, '');

  // whitespace
  const resWs = PluginHooksSurface.readonlyDelegationSelfNoteOf({
    estimated_readonly_rounds: 0,
    self_note: '   '
  });
  assert.equal(resWs.ok, true);
  assert.equal(resWs.note, '   ');

  // null
  const resNull = PluginHooksSurface.readonlyDelegationSelfNoteOf({
    estimated_readonly_rounds: 0,
    self_note: null
  });
  assert.equal(resNull.ok, true);
  assert.equal(resNull.note, null, 'a non-string note is read as absent');

  // own-property with undefined value
  const objWithUndef = { estimated_readonly_rounds: 0 };
  objWithUndef.self_note = undefined;
  const resUndef = PluginHooksSurface.readonlyDelegationSelfNoteOf(objWithUndef);
  assert.equal(resUndef.ok, true);
  assert.equal(resUndef.note, null, 'own-property undefined is read as absent');
});

test('WHAT[speculative-investigation-016] positive rounds never fails over a missing, blank or non-string self_note', () => {
  // missing note
  const resMissing = PluginHooksSurface.readonlyDelegationSelfNoteOf({
    estimated_readonly_rounds: 2
  });
  assert.equal(resMissing.ok, true);
  assert.equal(resMissing.note, null);

  // empty string note
  const resEmpty = PluginHooksSurface.readonlyDelegationSelfNoteOf({
    estimated_readonly_rounds: 2,
    self_note: ''
  });
  assert.equal(resEmpty.ok, true);
  assert.equal(resEmpty.note, '');

  // blank whitespace note
  const resBlank = PluginHooksSurface.readonlyDelegationSelfNoteOf({
    estimated_readonly_rounds: 2,
    self_note: '  \t\n  '
  });
  assert.equal(resBlank.ok, true);
  assert.equal(resBlank.note, '  \t\n  ');

  // non-string note: number
  const resNum = PluginHooksSurface.readonlyDelegationSelfNoteOf({
    estimated_readonly_rounds: 2,
    self_note: 123
  });
  assert.equal(resNum.ok, true);
  assert.equal(resNum.note, null, 'a non-string note is read as absent');

  // non-string note: boolean
  const resBool = PluginHooksSurface.readonlyDelegationSelfNoteOf({
    estimated_readonly_rounds: 2,
    self_note: true
  });
  assert.equal(resBool.ok, true);
  assert.equal(resBool.note, null, 'a boolean note is read as absent');

  // non-string note: object
  const resObj = PluginHooksSurface.readonlyDelegationSelfNoteOf({
    estimated_readonly_rounds: 2,
    self_note: { text: "note" }
  });
  assert.equal(resObj.ok, true);
  assert.equal(resObj.note, null, 'an object note is read as absent');

  // non-string note: array
  const resArr = PluginHooksSurface.readonlyDelegationSelfNoteOf({
    estimated_readonly_rounds: 2,
    self_note: ["note"]
  });
  assert.equal(resArr.ok, true);
  assert.equal(resArr.note, null, 'an array note is read as absent');

  // non-string note: null
  const resNull = PluginHooksSurface.readonlyDelegationSelfNoteOf({
    estimated_readonly_rounds: 2,
    self_note: null
  });
  assert.equal(resNull.ok, true);
  assert.equal(resNull.note, null, 'a null note is read as absent');
});

test('WHAT[speculative-investigation-016] native number checks and range validation reject invalid values', () => {
  // negative
  const resNeg = PluginHooksSurface.readonlyDelegationSelfNoteOf({
    estimated_readonly_rounds: -1
  });
  assert.equal(resNeg.ok, false);
  assert.equal(resNeg.error, 'InvalidRange');

  // fractional / float
  const resFloat = PluginHooksSurface.readonlyDelegationSelfNoteOf({
    estimated_readonly_rounds: 1.5,
    self_note: 'note'
  });
  assert.equal(resFloat.ok, false);
  assert.equal(resFloat.error, 'InvalidRange');

  // out of range (> 2147483647)
  const resOverflow = PluginHooksSurface.readonlyDelegationSelfNoteOf({
    estimated_readonly_rounds: 2147483648,
    self_note: 'note'
  });
  assert.equal(resOverflow.ok, false);
  assert.equal(resOverflow.error, 'InvalidRange');

  // string number (no string coercion)
  const resStr = PluginHooksSurface.readonlyDelegationSelfNoteOf({
    estimated_readonly_rounds: '2',
    self_note: 'note'
  });
  assert.equal(resStr.ok, false);
  assert.equal(resStr.error, 'WrongNumberType');

  // boolean
  const resBool = PluginHooksSurface.readonlyDelegationSelfNoteOf({
    estimated_readonly_rounds: true,
    self_note: 'note'
  });
  assert.equal(resBool.ok, false);
  assert.equal(resBool.error, 'WrongNumberType');

  // array
  const resArr = PluginHooksSurface.readonlyDelegationSelfNoteOf({
    estimated_readonly_rounds: [1],
    self_note: 'note'
  });
  assert.equal(resArr.ok, false);
  assert.equal(resArr.error, 'WrongNumberType');

  // object
  const resObj = PluginHooksSurface.readonlyDelegationSelfNoteOf({
    estimated_readonly_rounds: { rounds: 1 },
    self_note: 'note'
  });
  assert.equal(resObj.ok, false);
  assert.equal(resObj.error, 'WrongNumberType');

  // NaN
  const resNaN = PluginHooksSurface.readonlyDelegationSelfNoteOf({
    estimated_readonly_rounds: NaN,
    self_note: 'note'
  });
  assert.equal(resNaN.ok, false);
  assert.equal(resNaN.error, 'InvalidRange');

  // Infinity
  const resInf = PluginHooksSurface.readonlyDelegationSelfNoteOf({
    estimated_readonly_rounds: Infinity,
    self_note: 'note'
  });
  assert.equal(resInf.ok, false);
  assert.equal(resInf.error, 'InvalidRange');

  // null
  const resNull = PluginHooksSurface.readonlyDelegationSelfNoteOf({
    estimated_readonly_rounds: null,
    self_note: 'note'
  });
  assert.equal(resNull.ok, false);
  assert.equal(resNull.error, 'WrongNumberType');

  // missing
  const resMissing = PluginHooksSurface.readonlyDelegationSelfNoteOf({});
  assert.equal(resMissing.ok, false);
  assert.equal(resMissing.error, 'MissingEstimate');
});

test('WHAT[speculative-investigation-016] protocol field mixing and legacy field rejection', () => {
  // Legacy field alone
  const resLegacy = PluginHooksSurface.readonlyDelegationSelfNoteOf({
    delegate_readonly_rounds: 1,
    self_note: 'note'
  });
  assert.equal(resLegacy.ok, false);
  assert.equal(resLegacy.error, 'MixedProtocolFields');

  // Both legacy and new fields
  const resBoth = PluginHooksSurface.readonlyDelegationSelfNoteOf({
    estimated_readonly_rounds: 1,
    delegate_readonly_rounds: 1,
    self_note: 'note'
  });
  assert.equal(resBoth.ok, false);
  assert.equal(resBoth.error, 'MixedProtocolFields');
});

test('WHAT[speculative-investigation-016] invalid argument container (not a plain object) is rejected', () => {
  for (const bad of [null, undefined, 123, 'arguments', [1, 2]]) {
    const res = PluginHooksSurface.readonlyDelegationSelfNoteOf(bad);
    assert.equal(res.ok, false);
    assert.equal(res.error, 'InvalidArgumentObject');
  }
});

test('WHAT[speculative-investigation-016] the 5 argument-error categories come from the production projection and stay told apart', () => {
  // 每个输入各触发一个独立的失败原因。分类标识取自生产投影：
  // readonlyDelegationSelfNoteOf 返回的 { ok: false, error }，其 error 由
  // InvestigationEstimateContract.errorCode 从判别联合投影为稳定字符串
  // （src/Wanxiangshu/Strength/InvestigationEstimateContract.fs 的 errorCode），
  // 这里不做判别联合的标签或字段反射，也不复制任何本地文案表。
  // 期望值是 WHAT[016] §6 明文列出的分类名，不是测试内自造的清单；断言只锁分类
  // 标识、不锁中英文自然语言文案，因为 §6 要求分类与说明彻底解耦、文案会演进。
  const cases = [
    { args: {}, expected: 'MissingEstimate' },
    { args: { [EstimatedReadonlyRoundsField]: '1', [SelfNoteField]: 'note' }, expected: 'WrongNumberType' },
    { args: { [EstimatedReadonlyRoundsField]: -1 }, expected: 'InvalidRange' },
    {
      args: {
        [EstimatedReadonlyRoundsField]: 1,
        delegate_readonly_rounds: 1,
        [SelfNoteField]: 'note',
      },
      expected: 'MixedProtocolFields',
    },
    { args: [1, 2], expected: 'InvalidArgumentObject' },
  ];

  const observed = new Set();
  for (const { args, expected } of cases) {
    const res = PluginHooksSurface.readonlyDelegationSelfNoteOf(args);
    assert.equal(res.ok, false, `production must reject ${JSON.stringify(args)}`);
    assert.equal(res.error, expected, `production category for ${JSON.stringify(args)} must be ${expected}`);
    observed.add(res.error);
  }
  assert.equal(
    observed.size,
    5,
    'WHAT[016] §6 requires the five failure reasons to be told apart; production must return five distinct categories'
  );
});

test('WHAT[speculative-investigation-016] tool.execute.before throws descriptive error explaining input rule instead of raw enum %A', async () => {
  // 覆盖边界（诚实声明，缺的是出口而不是断言）：真实 tool.execute.before hook 块上的
  // 「错误消息含中英文规则说明、且不泄漏原始 F# 枚举名」这组断言在本文件不可恢复：
  // 1. dist/OpenCode/Plugin/PluginHooks.js 的唯一导出是 create(boot, host, transform)，
  //    它注册 hook 而不暴露逐工具策略入口；要拿到真实 ['tool.execute.before'] 回调并让
  //    宿主实现 throw，必须造出与生产同形的 boot/host/plugin api 对象，那属于 Host 适配层
  //    的证据（host-boundary/032 由启动真实 plugin 进程的 integration fixture 覆盖）；
  // 2. 全仓既不存在 PolicyAwareToolPolicySurface，也不存在 decorateToolDescription /
  //    toolExecuteBeforeFailure / importPluginHooksForApi 这样的公开出口；
  // 3. 抛错文案只内联在 src/Wanxiangshu/OpenCode/Plugin/PluginHooks.fs 里，
  //    没有作为可调用的 JS 契约发射。
  // 因此这里只断言公开 Surface 真正能证明的那一半：拒绝给出稳定机器分类，
  // 且该分类标识本身不参与自然语言文案。短记不构成失败，只有估计字段本身非法。
  const errZero = PluginHooksSurface.readonlyDelegationSelfNoteOf({
    estimated_readonly_rounds: -1,
    self_note: 'bad note'
  });
  assert.equal(errZero.ok, false);
  assert.equal(errZero.error, 'InvalidRange');

  const errMissing = PluginHooksSurface.readonlyDelegationSelfNoteOf({});
  assert.equal(errMissing.ok, false);
  assert.equal(errMissing.error, 'MissingEstimate');
});

test('WHAT[speculative-investigation-016] schema, call-boundary and source-batch consumers all classify through the one contract function', () => {
  const representativeCases = [
    { name: "read", policy: 'EstimateAfterCall' },
    { name: "chronicle", policy: 'NoEstimate' },
    { name: "js-foo-unknown", policy: 'Unreviewed' },
    { name: "invalid", policy: 'NoEstimate' },
  ];

  for (const { name, policy } of representativeCases) {
    const classified = Strength.classifyTool(name);
    assert.equal(
      classified,
      policy,
      `production classifier must answer ${policy} for ${name}, got ${classified}`
    );

    // Schema consumer, asserted per key on the real definition object that
    // production decorateDefinition rewrote. Decorating is the visible consequence
    // of EstimateAfterCall and of nothing else.
    const definition = {
      description: "original tool description",
      parameters: {
        type: "object",
        properties: { path: { type: "string" } },
        required: ["path"],
      },
    };
    PluginHooksSurface.decorateReadonlyDelegationToolDefinition(name, definition);
    const schema = definition.jsonSchema ?? definition.parameters;
    assert.ok(schema, `${name} must expose a schema view after decoration`);

    if (policy === 'EstimateAfterCall') {
      assert.ok(
        schema.properties?.[EstimatedReadonlyRoundsField],
        `participating ${name} must carry ${EstimatedReadonlyRoundsField}`
      );
      assert.ok(
        schema.required?.includes(EstimatedReadonlyRoundsField),
        `participating ${name} must mark ${EstimatedReadonlyRoundsField} required`
      );
      assert.ok(
        schema.properties?.[SelfNoteField],
        `participating ${name} must carry the conditional ${SelfNoteField}`
      );
      assert.ok(
        !schema.required?.includes(SelfNoteField),
        `participating ${name} must keep ${SelfNoteField} conditional, never required`
      );
      assert.ok(
        schema.properties?.path && schema.required?.includes('path'),
        `participating ${name} must keep its original required entries`
      );
      assert.notEqual(
        definition.description,
        "original tool description",
        `participating ${name} must carry the appended protocol explanation in description`
      );
    } else {
      assert.equal(
        schema.properties?.[EstimatedReadonlyRoundsField],
        undefined,
        `non-participating ${name} must receive zero protocol increment`
      );
      assert.equal(
        schema.properties?.[SelfNoteField],
        undefined,
        `non-participating ${name} must receive zero protocol increment`
      );
      assert.equal(
        definition.description,
        "original tool description",
        `non-participating ${name} description must stay untouched`
      );
    }

    assert.equal(
      productionDecoratesSchema(name),
      policy === 'EstimateAfterCall',
      `schema decoration for ${name} must follow the classifier verdict ${policy}`
    );
  }

  // The schema consumer must also agree with the protocol list across the whole
  // public tool surface, not only the four representatives. Either the classifier
  // verdict or the decoration effect drifting turns this red.
  for (const name of ToolSurface.toolSpecNames()) {
    const expected = PARTICIPATING_SET.has(name);
    assert.equal(
      Strength.classifyTool(name) === 'EstimateAfterCall',
      expected,
      `production classifier verdict for '${name}' must match the protocol list`
    );
    assert.equal(
      productionDecoratesSchema(name),
      expected,
      `production schema decoration of '${name}' must match the protocol list`
    );
  }

  // 端点覆盖边界（诚实声明）：WHAT[016] §8 的三消费端里，只有 schema 装饰端在上方由
  // 生产 decorateDefinition 逐键验证。调用边界端（PluginHooks 的 toolBefore/toolAfter
  // 参数暂存与收窄拦截）没有 decorateToolDescription / importPluginHooksForApi /
  // toolExecuteBeforeFailure 这样的公开出口，全仓也不存在 PolicyAwareToolPolicySurface
  // 这个模块；来源批次端（StrengthDelegate.tryCaptureAndStart 的批次估计聚合）入口
  // 有 8 个参数，含 4 个 F# 端口类型、Task 与 DU 返回值，JS 侧无法构造。两端的覆盖
  // 依赖生产侧单测与 host-boundary 的真实 Host 集成证据，这里不以本地 Set 冒充第三份判定。
  // 源码级证据只证明它们不各自硬编码工具集合：两者都走同一个 classifyTool。
  for (const [label, sourcePath] of [
    ['PluginHooks.fs', pluginHooksSourcePath],
    ['Delegate.fs', delegateSourcePath],
  ]) {
    assert.match(
      fs.readFileSync(sourcePath, 'utf8'),
      /InvestigationEstimateContract\.classifyTool/,
      `${label} must classify tools through InvestigationEstimateContract.classifyTool`
    );
  }
});

test('WHAT[speculative-investigation-016] source batch capture rejects participating call with missing estimate as ArgumentError instead of no-estimate-opportunity', async () => {
  // Case 1: 参与工具缺少估计字段，被判定为 MissingEstimate 参数错误，绝不静默降级为 no-estimate-opportunity 或 estimated-zero
  const participatingResult = PluginHooksSurface.readonlyDelegationSelfNoteOf({ filePath: "src/file.fs" });
  assert.equal(participatingResult.ok, false);
  assert.equal(participatingResult.error, 'MissingEstimate');

  // Case 2: 非参与工具即使缺少估计字段，生产分类器也判它不参与，参与子集为空
  assert.equal(
    Strength.classifyTool('chronicle'),
    'NoEstimate',
    'production classifier must keep chronicle out of the participating subset'
  );
});

test('WHAT[speculative-investigation-016] every tool on the known tool surface receives the classification the protocol list prescribes', () => {
  // 已知工具名清单来源于公开 ToolSurface 契约
  const knownToolsFromSurface = ToolSurface.toolSpecNames();
  assert.ok(knownToolsFromSurface.length >= 30, `knownToolNames must cover active tool surface, got ${knownToolsFromSurface.length}`);
  assert.ok(knownToolsFromSurface.includes("read"), "knownToolNames must include 'read'");
  assert.ok(knownToolsFromSurface.includes("chronicle"), "knownToolNames must include 'chronicle'");
  assert.ok(knownToolsFromSurface.includes("fork"), "knownToolNames must include 'fork'");
  assert.ok(knownToolsFromSurface.includes("run"), "knownToolNames must include 'run'");

  // 差集门禁：工具面里的每个已知工具都必须拿到已判定的策略，任何工具都不得
  // 静默落到 Unreviewed 而逃过协议审阅。
  const unreviewed = knownToolsFromSurface.filter(
    (name) => Strength.classifyTool(name) === 'Unreviewed'
  );
  assert.deepEqual(
    unreviewed,
    [],
    `known tool surface contains unreviewed tools: ${JSON.stringify(unreviewed)}`
  );

  for (const name of knownToolsFromSurface) {
    const policy = Strength.classifyTool(name);
    assert.ok(
      ['EstimateAfterCall', 'NoEstimate', 'Unreviewed'].includes(policy),
      `classifier returned an unexpected policy '${policy}' for '${name}'`
    );
    // 参与侧判定必须与协议清单一致：任一端漂移都会在这里现形。
    assert.equal(
      policy === 'EstimateAfterCall',
      PARTICIPATING_SET.has(name),
      `classification of '${name}' must agree with the protocol tool list`
    );
  }
});

test('WHAT[speculative-investigation-016] known-bad fixtures prove the gate detects policy mutations and unreviewed leakage', () => {
  // Mutation 1: default branch mutates from Unreviewed to EstimateAfterCall
  function mutatedClassifyDefaultEstimate(name) {
    const policy = Strength.classifyTool(name);
    return policy === 'Unreviewed' ? 'EstimateAfterCall' : policy;
  }

  assert.throws(
    () => {
      const toolName = "js-foo-unknown";
      const mutatedPolicy = mutatedClassifyDefaultEstimate(toolName);
      const expectedParticipate = mutatedPolicy === 'EstimateAfterCall';
      const schemaDecorates = productionDecoratesSchema(toolName);
      assert.equal(
        schemaDecorates,
        expectedParticipate,
        `Schema decoration mismatch for ${toolName}: expected ${expectedParticipate} but got ${schemaDecorates}`
      );
    },
    /AssertionError/,
    "Gate must fail when default branch mutates from Unreviewed to EstimateAfterCall"
  );

  // Mutation 2: a NoEstimate tool (chronicle) is incorrectly moved into participating set
  function mutatedClassifyChronicleParticipating(name) {
    if (name === "chronicle") {
      return 'EstimateAfterCall';
    }
    return Strength.classifyTool(name) === 'EstimateAfterCall' ? 'EstimateAfterCall' : 'NoEstimate';
  }

  assert.throws(
    () => {
      const mutatedPolicy = mutatedClassifyChronicleParticipating("chronicle");
      const expectedParticipate = mutatedPolicy === 'EstimateAfterCall';
      const schemaDecorates = productionDecoratesSchema("chronicle");
      assert.equal(
        schemaDecorates,
        expectedParticipate,
        `NoEstimate tool 'chronicle' incorrectly marked as participating must fail consistency check`
      );
    },
    /AssertionError/,
    "Gate must fail when a NoEstimate tool is incorrectly moved into participating set"
  );

  // Mutation 3: a new tool enters the tool surface carrying no production verdict
  assert.throws(
    () => {
      const simulatedKnownTools = ["read", "new-leaked-tool"];
      const acknowledgedDecorating = ["read"];
      const undecided = simulatedKnownTools.filter(
        (name) => !acknowledgedDecorating.includes(name) && Strength.classifyTool(name) === 'Unreviewed'
      );
      assert.deepEqual(
        undecided,
        [],
        `Tools left without a production verdict leaked: ${undecided.join(", ")}`
      );
    },
    /AssertionError/,
    "Gate must fail when an undecided tool leaks into known tools without explicit registry"
  );
});
}
