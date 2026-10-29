# 指南自举交接（2026-10-29 JST）

| 仓 | HEAD | 备注 |
|---|---|---|
| vibe-fs | `86c103a9`（+本 handoff 提交） | 合 #70（`15a7b718`）后清债：剩余独立 resetScope／estimate 用例 skip、proposals 补注、scope-progress 宿主边界注释 |
| wanxiangli | `4a7e98a` | 合 #8（UTF-8 projection bound）；含 `3ea4f68` scope-progress 产品边界 |

## Stash（保留）
- 主仓 `stash@{0}`：PluginTransforms/managed-chat 半成品（已被 5dd99b87 覆盖）
- li `stash@{0}`：failure 半成品（已被 c8d18b9 覆盖）

## 自举仍悬（裁定类勿臆造）
1. 两项设计裁定追认（E1 已 A+A）
2. phase/step ~22 处逐处裁定（档案未收回）
3. 「22 处」清单完备性
4. wx `wanxiangli-ce-replay-wt` 占 main@f1d01a0（勿贸然删）
5. 上游额度曾空转

## 已持久化
- `docs/委托惯例-验证前查总工头合入.md`

## Scope 续收口（本轮）
**已做：** OMP 全工具 scope 注入+校验；removeLast 出站坍缩（OpenCode+OMP）；Advisor 改挂 retention advisorDue（streak≥3 / remaining=0）；冷启 journal 绑定+ensureEpoch 重放；estimate 改为可选；Stall/ResetDue 不再开顾问；steer 文案改 retention；build gen40+。

**过渡已清：** 独立 `resetScope` 工具已物理删除；档案重锚走任意工具 `scope.kind=resetScope` → `ScopeReset.commit`；`remainingScopeTurnsEstimate` schema/驱动力已删；removeLast 投影用 beat 别名（providerRunId/callId）兜底。

**PairHint 屏障：** `latestReset` / `isResetWirePart` / `isAlwaysRetained` / `neutralIsReset` 识别 `scope.kind=resetScope`（含 arguments/state.input 与打平字段），过渡仍兼容旧工具名；带 reset 注解的调用及其结果保留。宿主中立投影透传 `scope`。wx `ce-replay-wt` / 裁定仍不臆造。

## docs-li / docs-shu（本轮）
- li：capability／pair-hint／scope-retention／scope-progress AGENTS 对齐注解入口；估计路径标退役。
- shu：共同法／scope-protocol／js-program·bookkeeper 资源去掉 estimate 必填；功能导览、结对指南、fork-child 收尾口径；proposals 两份 Scope 稿打历史条；requirements README 钉现行 Scope 一句。
- **compat-pass（本轮）**：优先 pack WHAT／WHY 加 COMPAT 条；context-compression [033]/[034]/[045] 改注解+retention；scope-progress 测试标 PACKAGE-HISTORICAL；omp-scope-seam 估计／独立 resetScope 注册用例 `test.skip`；049／064 改断言无 estimate；arg-estimate 资源保留退役说明；PairHint 注释注解优先。
- **#70 / #8（本轮）**：[wanxiangli#8] 已合，gitlink 钉 `4a7e98a`；[vibe-fs#70] 本地 merge 进 master `15a7b718`（冲突按现行口径：独立 resetScope 保持删除、estimate 夹具继续剥、managed-chat 保留 master 修）。
- **清债（本轮，`74097a8a`/`9258fc26`/`86c103a9`）**：
  - 残留 estimate 夹具：advisor 007／progress-recovery 007／workspace-isolation 007 带键用例均已 `test.skip`（仅确认）；scope-hni-physical 052、051 与 omp-negotiate-physical scope-progress-018 → skip [PRODUCT-RETIRED estimate]；work／delegation／repo-programming 已无正向传键（049／064 负向断言保留）。
  - 首次强 steer 断言（scope-hni 039×2、advisor-pure-chat、omp-scope-seam 033 Keep、capability-enforcement 006）由 `remainingScopeTurnsEstimate` 改为 `newScopeRetentionBudget`（资源 `host/scope-steer/missing-estimate` 现行文案）；advisor-pure-chat 两项负向都保留。
  - 独立 resetScope 工具用例：omp-scope-seam 049、043 → skip；L187/L437 原已 skip；delegation-034 里死变量 `resetDef` 删；OpenCode scope-hni 047／050／043×4、context-compression/043 两个宿主接线例、advisor/scope-recovery/007 → skip [PRODUCT-RETIRED standalone-resetScope]；delegation-negotiation-004 去掉过早独立 reset 一步（注解路径等价未断言）。
  - proposals：Scope进度 §5／§9 补现行注，§6 注更正为「steer 资源沿用旧路径名、内容 retention」；跨Scope §1／§5／§6 补现行注。
  - scope-progress：不拆包；宿主三处 import（OMP Adapter、PluginHooks、ScopeReset）及 PluginHooks epoch 回退处加边界注释（仅 epoch 记账存活，estimate API 历史）。
## 红测清零（本轮，`178f4586`/`4569528b`）
基线 `b34204e5`：scope-hni-physical 17/8/8、omp-scope-seam 11/6/7 → 现 **25/0/8**、**17/0/7**（build gen43）。
- **产品回归（1 处，修产品）**：en scope-protocol 标题已改 `## Scope and context`，但 OpenCode `ProviderSystemTransform` 与 OMP `Adapter` 的 `isScopeSection` 只认旧 `## Scope & Context Management`／`## Scope 与上下文` → 英文会话每次重复追加 Scope 段。补认现行标题（旧标题仍原地升级）。影响 ce-039、OMP ce-034×2。
- **测旧（其余，改测）**：negotiate 回执文案本未变，失败因调用缺 `scope` 注解（共同法：每次工具调用都带注解，negotiate 只豁免额度）及缺 `messageID`（门需 call+run id）→ 补 `scope:{kind:'keepLast'}`+messageID（neg-004/006/007/036/037/038、OMP e2e）；006 的 fork 走 `scopeRetention.ensureEpoch+seed` 后 keepLast（`real-fork.mjs` `forkCall` 加可选 `scope` 参，缺省行为不变）；neg-005 删 `resetScope` 显式 deny 项 [PRODUCT-RETIRED]；OMP 034 schema required 含 `scope`；ce-048 带注解；OMP 034 premature-reset 原载体误选 askPreviousScope（豁免工具、非 toolDefinition 路径）→ 夹具加普通产品工具 `probe` 当载体，门文案「unavailable until the first negotiate」产品本就有。
- **仍欠**：scope-progress 包内 durable／estimate 实现与测（PACKAGE-HISTORICAL，拆包需另裁）；被 skip 的 OpenCode／OMP reset 档案链路（043／047／049／050）尚无注解路径等价新测；`arg-estimate`／`missing-estimate` 资源路径名残留；`requirements/**/WHAT` 中旧句未全扫；wx `ce-replay-wt`（勿贸然删）；上列裁定仍悬；~~OpenCode 大量用例因缺 `scope` 注解红~~ → 已由夹具中心默认注解收口（见下节「夹具默认 scope 注解」）；OMP `askPreviousScope` 带 `scope.kind=resetScope` 时 reanchor 回执被忽略（既不门控也不 commit，静默丢弃），未改，待裁是拒收还是走同一 reset 路径。
- **测况**（本机 build gen42 实跑）：pair-hint 018 5/5；repo 049 3/3、064 11/11；capability-enforcement 006 14 过 2 skip；context-compression/043 6 过 2 skip；omp-negotiate-physical 2 过 1 skip；advisor 007 系全过/skip。scope-hni-physical 17 过 8 败 8 skip（基线 15a7b718：14 过 19 败）；omp-scope-seam 11 过 6 败 7 skip（基线 10 过 9 败）。剩余失败均为基线既有：negotiate 回执文案（`Questions accepted`／`Clarification requested`）、OMP scope 协议标题（`Scope & Context Management`）、negotiation-005 期望 `resetScope` 在 agent config 显式 deny、delegation-034 期望「resetScope unavailable until first negotiate」——需另判是测旧还是产品回归，未臆改。

## 夹具默认 scope 注解（本轮）
中心改在 `requirements/verification-system/tests/support/plugin-fixture.mjs`（`real-fork.mjs`、work `plugin.mjs` 等均经 `withExecutablePlugin`，自动受益；测内不手抄）：
- 包装 `hooks.tool[*].execute`（`withExecutablePlugin`／`withRestartablePlugin`／裸 boot／`withPluginClient`）：args **无自有 `scope` 键** → 注入 `{kind:'keepLast'}`；调用前 `primeScope(session)`＝`scopeRetention.ensureEpoch(session, 现 epoch||'0')`+`seed(session, 48)`（仅在 retention 未知或额度 0 时）；门回「turn already carries a real scope annotation」则同参改 `meToo` 重试一次（拒绝在执行前，无副作用）。缺 `callID`／`messageID`（codec 二者缺一即都丢）→ 补 `fixture-call-<n>`／`msg-<callID>`。
- args **有自有 `scope` 键**（含 null/undefined）→ 原样透传，供断言门本身的测；`withExecutablePlugin(body,{scopeAnnotation:false})` 整体关闭包装。从不注入 `resetScope`。导出 `primeScope`、`DEFAULT_SCOPE_BUDGET`。
- 全量 `requirements/*/tests/*.test.mjs`（789 文件，逐文件 `node --test`，dist 未重建）：基线 `df386061` **3716 过／161 败（78 文件红）** → **3828 过／52 败（37 文件红）**；无新增失败用例名。整文件转绿 41 个：delegation 006/021/024/026/027/032/033/036/039/040、work 011/021/024/027/029/032/033/039/049/050/051、managed-session-lifecycle 006/007/008/013/015/028/029、effect-accounting 002/013、semantic-trace 013、repository-programming 031/065、intra-participant-parallelism 003/010/013/017、participant-horizon 011、capability-enforcement 010/024/025；capability-enforcement 026 12→3 败、work 019 6→2 败（余红与 scope 无关）。
- **新待裁**：capability-enforcement 026 剩 3 红（D07／D09／D11）——「被拒派工不得追加任何 durable 字节」，而 keepLast 注解在业务拒绝前已由 scope-retention 落盘（新建 `.Herdchestra/wanxiangshu/advisor-process.sqlite`）。是零副作用断言应豁免 scope-retention 流，还是门应在业务拒绝时不落注解——未改，待裁。
- 剩余 37 红文件里其余均与 scope 注解无关（基线既有），本轮未动。OMP `askPreviousScope`+`scope.kind=resetScope` 静默丢仍**待裁**。

