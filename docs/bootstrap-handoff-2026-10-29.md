# 指南自举交接（2026-10-29 JST）

| 仓 | HEAD | 备注 |
|---|---|---|
| vibe-fs | `aa2e16d8` | compat-pass WHAT/tests → annotation+retention |
| wanxiangli | `e70254c` | compat-pass scope-progress historical + PairHint comment |

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
- **compat 仍欠**（不臆造大改）：work／delegation／repo-programming 等夹具里仍可能传入 `remainingScopeTurnsEstimate`（宿主若拒多余键则需再剥）；scope-progress durable 实现未拆包；wx `ce-replay-wt`；proposals 正文仅条目标记未逐段改写；omp 其它仍写 resetScope 工具名的历史用例未全 skip。

