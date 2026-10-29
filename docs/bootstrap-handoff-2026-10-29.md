# 指南自举交接（2026-10-29 JST）

| 仓 | HEAD | 备注 |
|---|---|---|
| vibe-fs | `089d8c7f`（+本 handoff 提交；见文末「全量红测分拣」） | 合 #70（`15a7b718`）后清债：剩余独立 resetScope／estimate 用例 skip、proposals 补注、scope-progress 宿主边界注释 |
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


## 全量红测分拣（本轮，`f4a0efd2`/`089d8c7f`）
全量 789 文件逐文件 `node --test`（build gen48，dist 已重建）：基线 `1eeeb576` **52 败／37 文件红**（另 time-capability 004 挂起超时）→ **11 败／9 文件红**（3902 过，116 skip）；无新增失败用例名（ce-025 P15 见下）。

**产品小回归（修产品，口径已明）：**
- `ScopeAnnotationGate.fs` 缺同名 `.fsi` → 补 `.fsi`（compile-order／owner fsproj 同步）。一处违法连带 causal-wait 009、delegation 028/029、durable-events 022/023、host-boundary 026/027/028/031、structured-workflow 011/012/013/014、time-capability 008 整文件红。
- review 参数白名单不认宿主注入的 `scope` → 生产里每次带注解的 review 都被拒「unexpected fields」。`Review/Model.fs` 允许 `scope`（work 009×2、019#3 绿）。
- `716d30d8`（docs 语气 pass）误把 js-program header（en 50 行／zh 42 行）与 js-bookkeeper description（各 9 行）截成首行 → 还原正文、只保留新首行（knowledge-reuse 006×2、repo-programming 005 绿）。
- `scope-steer/reset-due` 在 `bb8bcc07` 丢了「收尾报告后不得 reset」硬规则 → 以 `scope.kind=resetScope` 口径补回 en/zh（delegation-038 四例绿）。
- Omp/Adapter、PluginTransforms 新 mutable 补 `// DSL-MUTABLE:`（resource／algorithm-scratch）。

**测旧（改测）：** cap-002 允许矩阵与准入去 `resetScope`（仍断言 advisor/unknown fail closed）；cog-env 013/017 边界改断言 «Scope»+`scope` 注解；cc-034 尾注 `Remaining retention`；cc-039 missing-budget 文案；rp-022 三个必填参数（en+zh）；emr-010 正则跟随 `ScopeAnnotationGate.wrap (gateExecute injected)`；cog-env 005 排除 git `fast-forward`；mce-003 去掉 #70 合并残留 `beforeReplay`（保留 model 对象同一性断言）；time-capability 004 OMP 工具表去 `resetScope` 与只记估计的 `todowrite`；second-host-time 夹具给 `wxs_deliver` 带 keepLast 并开 retention（此前缺注解致挂起）；delegation-038 ResetScopeTool.js 例 skip [PRODUCT-RETIRED]。

**仍红（未动）：**
| 测 | 类别 | 要点 |
|---|---|---|
| capability-enforcement 026 D07/D09/D11 | **待裁**（不动） | keepLast 落盘 vs 零副作用公理 |
| capability-enforcement 009 #2 | 需裁定 | 空 sessionID 时注解门先回 retention 内部错 `sessionId and epochId required`，盖过执行面拒绝文案；门先后／文案归属待定 |
| work-019 #4 | 需裁定 | 无任期 `review([])` 现回 `kind=Asked`（与 #3「首个 review 开工」并存），测期望 `binding-unavailable` |
| time-capability 004 #1 | 需裁定／作者 | `OpenCode/Host/Sessions.fs:85` 原生 `setTimeout`（`e412e0c4` RunnerIdleWait）；改走时间端口或进精确文件豁免，非小修 |
| js-semantic-surface 002 | 需裁定 | `dispatch-protocol/tests/016`（#70 带入）深引 dist 内部模块＋DU 形状；改公面或登记 canary 属边界政策 |
| requirement-system 017 | 需用户 | #70 带入的测无 WHAT 条款：mce 016、dispatch-protocol 016、crash-reconciliation 022、work 056/057（「notify the user」）|
| managed-chat-execution 005 #5 | 不确定 | `message.updated` 已让 providerStarted 先于 transform 落盘；测期望 false。基线既有，未判 |
| managed-session-lifecycle 022 #5 | 不确定 | EngineerCaseCaptured 1≠2。基线既有，未判 |
| capability-enforcement 025 P15 | 证据不足，不要写成已解决 | P8 并发下 rg 先退出（ESRCH），单跑 3/3 绿。这只是一次观察，不是裁定，也不是已解决。

仍悬：OMP `askPreviousScope`+`scope.kind=resetScope` 静默丢（待裁，未动）；被 skip 的独立 reset 档案链路尚无注解路径等价新测；scope 注解 schema 未承载「收尾后不得 reset」（delegation-038 第 5 例 skip 后的欠账）。

## 待裁补缺（本轮）

已有记录只补缺，不把待裁改成裁定。

仍待裁定，上文已写，此处不改写：capability-enforcement 026 的 keepLast 零副作用；`askPreviousScope` 与 `scope.kind=resetScope` 同用时静默丢弃；capability-enforcement 009 空 session；work 019 第 4 例；time-capability 004 的 `setTimeout`；js-semantic 与 requirement-system 017 无 WHAT。

补上的事实：`requirements/js-semantic-surface/WHAT.md`、`requirements/requirement-system/WHAT.md` 两份文件都在，[002] 与 [017] 条款也在。上文「无 WHAT」指 017 点名的测没有对应条款（mce 016、dispatch-protocol 016、crash-reconciliation 022、work 056/057），不是文件缺失。不代裁。

有实现声称、未见裁定，故仍待裁。不得回滚，也不得代裁，不升格为已裁定，也不写成已解决：
- 畸形 identity carrier 按缺省处理。
- 受管 intent 缺持久 authority 时，改为边界级显式失败。

树里没有这两项的报告路径，不编出处。先前报告指向主仓 `5dd99b875`、子模块 `c8d18b9`、generation 462；这不是本轮读回的当前树事实。`wanxiangli-ce-replay-wt` 不删。

## phase/step 档案（建议分类，不是裁定）

只收回下面三处。约 22 处里其余仍未收回，不在此补完，也不逐处代裁。建议分类不是负责人裁定。

1. `ExecutionPermits.IllegalTransition` 在 `wanxiangli/src/permit/ExecutionPermits.fs:115`。本轮读到第 115 行是 Rejection 用例 `IllegalTransition`，不是函数体。托付原意：守的是 completeRelease 只能从同一 Release 的 Releasing 完成，Pending/Committed 必须拒绝。建议保留。不是裁定。
2. `ChatAdmissionFlightPhase` 在 `src/Wanxiangshu/OpenCode/Host/HostSignalBootstrap.fs:54`。本轮读到 `Running` / `Finished`。第 1155 行在 `Phase.Value = Running` 时交回已有 `Work`，否则新开航班。建议保留到有计算表达式接过这个合并。不是裁定。
3. `ChatAdmissionTransactionStep` 在 `src/Wanxiangshu/Interaction/Admission/ChatAdmissionTransaction.fs:23`。本轮读到的是步骤名枚举。托付：没有用当前 step 决定下一步，顺序就是 task 顺序；形态符合指南第 2 节要去掉的 phase/step 标签。指南点的是形状，不是逐字类型名；本轮在 docs 里用 phase/step 只命中本交接文件，不另编指南路径。`ChatAdmissionTransactionSurface.fs` 有 `CrashB`/`AcquireLease`、`CrashC`/`LeaseTarget`、`CrashD`/`ProjectHost` 匹配臂。这些名字是否仍被依赖，未测量。未测量前不删。不是裁定。

## 待裁补缺（续，不代裁）

不重复上文已写的待裁名单。

work 019 第 4 例仍待裁。不改测试，也不改代码。`requirements/work/tests/019.test.mjs` 109-123 期望 `binding-unavailable` 或 `review-unavailable`。源码路径是 `ToolRegistry.reviewMember` 不看任期；空账走到 lifecycle `finishActive` `Asked`，返回 `ok=true`、`kind=Asked`，并打开初始任期。`binding-unavailable` 当前无调用方。与第 3 例承认的开工路径相反。

交接表排除清单外，没有同时写明期望行为和当前偏差的可修项。不据此开修。

scope 注解 schema 缺少「收尾后不得 reset」，只是缺口，没有字段名，不代设计。

`arg-estimate` 旧路径名没有指定替代名，不改。

## 另记（只补缺，不代裁）

reset 043／047／049／050 两宿主文案不一致，补测试等于代裁。只记下缺口，不补等价新测，也不裁定哪一家文案为准。

P15 证据不足，不要写成已解决。上表类别已从「负载抖动」改成这句话。单跑绿不能升格成已解决。

lsof 留删：文档里原先没有这条。留还是删，未裁定、不代裁。本轮不删任何调用。


## 待裁补缺（gen465 笔记入账，不代裁、不回滚）

委派人所告：`04d4a9a8` 已提交并推送，主题是待裁登记不是裁定。本段只补上文还没有的缺口，不回滚该提交，不改产品代码，不改测试，不把待裁写成裁定。落笔前交账仍是 17166 字节、mtime 2026-10-29T10:01:07.972Z，文末仍是「本轮不删任何调用。」；对不上就不会写这一段。

managed-session-lifecycle 022 第 5 例不改。本轮在 requirements 的 WHAT.md 检索 EngineerCaseCaptured，无命中：WHAT 没有这条数。实现一次 archive 写一条。测试期望 2 是夹具算术，不是现行契约。缺的书面裁定是：删除归档后总数以谁为准，decoy 与子会话各算不算。上文表里「1≠2、未判」不改写。

time-capability 004 不改。本轮读到 `src/Wanxiangshu/OpenCode/Host/Sessions.fs:84` 起是 `RunnerIdleWait`，第 85 行从 `node:timers/promises` 导入 `setTimeout`，`sleep` 走这个导入。轮询参数是 `IOpenCodePort`，没有接到 `ITimerPort`。走时间端口还是精确豁免，未裁。上文表内 `Sessions.fs:85` 与这次读到的行号一致，原句不动。

「收尾后不得 reset」不在 `ToolHostCodec.scopeAnnotationSchema`。本轮读到的字段只有 `kind`、`oldScopeVerboseVerbatim`、`newScopeGoal`、`newScopeRetentionBudget`、`replacement`，没有这项的字段名。不设计字段。delegation 038 第 5 例 skip 的是已删除的 ResetScopeTool.js 那句英文，不是 schema 断言。上文「schema 未承载、没有字段名」不改写成已设计。

ChatAdmissionFlightPhase 的类型名本轮只出现在 `HostSignalBootstrap.fs`。第 1155 行是进程内字典 `admissionInFlight.TryGetValue` 命中、且 `Phase.Value = Running` 时交回已有 Work。`admissionInFlight` 的 .fs 命中限于：src/Wanxiangshu/OpenCode/Host/HostSignalBootstrap.fs。按类型名，恢复和 journal replay 不读它；本轮没有再用别名把 replay 路径搜完。`completeRelease` 定义在 `wanxiangli/src/permit/ExecutionPermits.fs:150`；.fs 里另一处是第 259 行，`release` 在 `beginRelease` 已 Transitioned 之后调用 `Lifecycle.completeRelease`。这是读到的位置，不是删留裁定。上文「建议保留到有计算表达式接过这个合并」不改成裁定。

计算表达式事实重放：本轮读到 `wanxiangli/src/program/Program.fs` 有 `replay`，`wanxiangli/src/lifecycle/State.fs:215` 写明程序位置只按事实重放推进。笔记还点了 Work.fs。本轮用 replay／事实重放／计算表达式去对 wanxiangli/src 下的 Work.fs，没有命中。不把笔记改写成「本轮已在 Work.fs 读到重放」。这只说明重放还在，不说明指南第 49 行四段控制流已经重写完。

指南第 49 行四段，按笔记尚未收成单一 CE。咨询与答复是 task。一轮对话、委托与 Join 是 CE 与 phase 枚举并排。打断与续跑两边都不是。不要把 `ChatExecutionLaw.Phase` 或 `HandleLifecycleLaw.Phase` 当证明税删除。本轮在 markdown 检索 `ChatExecutionLaw` 无命中，不另编指南路径，也不删这两个 Phase。

六个旧拒收名，笔记说测试路径没有断言行，名字本身没有列出，不补全。本轮读到 requirements/managed-chat-execution/tests/018.test.mjs:9 // `MANAGED-CHAT-005 … frozen-attempt-plan-missing` because the process-local frozen。

capability-enforcement 026 缺的裁定上文已写：拒派改了 durable 字节时，豁免 scope-retention 流，还是让门在业务拒绝前不落注解。此处不另选，不改写原句。

capability-enforcement 009 第 2 例补一句，不改上文表。这是短路：调用方只看见 `sessionId and epochId required`，执行面拒绝没有生成。门序和文案归属未裁。

scope-progress 包与出口都在，不拆。宿主不再写估计，仍读估计日志认 epoch。上文「不拆包」不动。

回执没来仍被收成 Superseded 并 Ok 的旧状，笔记称已按指南派工改成未知不得冒充。AbortedObserved 仍是合法观察种类，现行重启不喂它；LegacyFalseAbort 拒绝路径保留。本轮在 *.fs 见到这些符号，只作定位，不解释成裁定：wanxiangli/src/assembly/DurableWork.fs:200、wanxiangli/src/assembly/DurableWork.fs:202、wanxiangli/src/chat-execution/ChatExecutionLaw.fs:293、wanxiangli/src/handle-lifecycle/HandleLifecycleLaw.fs:201、wanxiangli/src/handle-lifecycle/HandleLifecycleLaw.fs:214、wanxiangli/src/lifecycle/State.fs:624、wanxiangli/src/lifecycle/Work.fs:213、wanxiangli/src/lifecycle/Work.fs:216、wanxiangli/src/lifecycle/Work.fs:220、wanxiangli/src/session/Message.fs:14、wanxiangli/src/session/Message.fs:60、wanxiangli/src/session/Message.fs:62、wanxiangli/src/session/Message.fs:85、wanxiangli/src/session/Ownership.fs:30、wanxiangli/src/session/Ownership.fs:168、wanxiangli/src/session/Ownership.fs:172、wanxiangli/src/session/Ownership.fs:173、wanxiangli/src/session/Ownership.fs:176、wanxiangli/src/session/Ownership.fs:267、src/Wanxiangshu/Mission/Lifecycle/DurableWorkHost.fs:286、src/Wanxiangshu/Mission/Lifecycle/DurableWorkHost.fs:327、src/Wanxiangshu/Mission/Lifecycle/DurableWorkHost.fs:332、src/Wanxiangshu/Mission/Planning/PlanTools.fs:29、src/Wanxiangshu/Execution/Session/ChatExecution/Surface.fs:630、src/Wanxiangshu/Execution/Session/ChatExecution/Surface.fs:632。这不是新裁定，也不回滚 `04d4a9a8`。

lsof 留删仍未裁。本轮检索 lsof，命中 2 处：docs/bootstrap-handoff-2026-10-29.md:123；requirements/verification-system/tests/e2e/support/process-host.js:245。上文「本轮不删任何调用」仍然有效。本轮不删。

requirement-system 017：正文按显式引用没有点名测试路径。五个外包装测试是否补 WHAT，仍待裁。本轮在 WHAT.md 见到的 017 相关行：51 ## [017] 包结构测试。不代裁，不补条款。

createSession 传输超时仍是 Unknown。NotStarted 只用于未绑定 manager 或宿主 HTTP 拒绝。不改，不代裁。

ChatAdmissionTransactionStep 的展示字符串可有可无。CrashB/C/D 的中断点是枚举臂。未裁定，不删。上文第 3 条已写未测量前不删，此处只补这两句，不改成删留裁定。

askPreviousScope 同带 resetScope，不改。本轮读到 ScopeAnnotationGate.fs:135 为 `| Ok _ -> return! inner args ctx`。上文「静默丢、待裁是拒收还是走同一 reset 路径」不改写成已修。


## 待裁补缺（P15 与符号位置，不代裁、不回滚）

不回滚 `04d4a9a8`。委派人所核：该提交只含 `docs/bootstrap-handoff-2026-10-29.md`。本轮没有跑 git，不把这句话写成自己查过的提交范围。

P15 当前树重读后仍是证据不足。不写成代码缺陷，不写成负载抖动，不写成已解决，本轮不重跑。上文表里那句 ESRCH 摘要还在，没有栈，没有 stderr。本轮在 capability-enforcement 与这份交接里检索 P8，命中：docs/bootstrap-handoff-2026-10-29.md:79。没有读到 P8 的定义。本轮读到 `requirements/capability-enforcement/tests/025.test.mjs:441` 为 `test('WHAT[capability-enforcement-025] P15_concurrent_physical_rg_overlap_with_review_acceptance_admits_inflight_and_refuses_subsequent', { timeout: 5000 }, async () => {`。笔记说这里的并发 rg 用无保护的 SIGSTOP/SIGCONT。附近原文：

```
431|      assert.equal((await execute(hooks, sessionID, reads[3], 'late-rg')).code, 'REVIEW_ONLY')
432|      assert.equal(spawns, 1)
433|    } finally {
434|      released.resolve()
435|      processes.spawn = spawn
436|      syncBuiltinESMExports()
437|    }
438|  })
439|})
440|
441|test('WHAT[capability-enforcement-025] P15_concurrent_physical_rg_overlap_with_review_acceptance_admits_inflight_and_refuses_subsequent', { timeout: 5000 }, async () => {
442|  const { default: processes } = await import('node:child_process')
443|  const { syncBuiltinESMExports } = await import('node:module')
444|  await withExecutablePlugin(async (hooks, directory, _ids, runtime) => {
445|    const sessionID = 'ses-concurrent-inflight-review'
446|    await seed(runtime, sessionID, directory)
447|    const paused = Promise.withResolvers()
448|    const spawn = processes.spawn
449|    let spawns = 0
450|    let liveChild = null
451|    try {
452|      processes.spawn = function (...args) {
453|        const child = spawn.apply(this, args)
454|        if (args[0] !== 'rg') return child
455|        spawns += 1
```

符号位置按本轮检索入账，不是形态裁定。生产路径指 `src/` 与 `wanxiangli/src/` 里非测试、非 markdown 的文件。

MalformedIdentityCarrier：2 处。生产路径 无。其余：指南.txt。
DurableAuthorityUnavailable：2 处。生产路径 无。其余：指南.txt、proposals/archive/2026-10-03/baselines/2026-10-03-r04-concurrent-hook-handles-red.log。
ClosureKindUnproven：1 处。生产路径 无。其余：指南.txt。
frozen-attempt-plan-missing：3 处。生产路径 无。其余：docs/bootstrap-handoff-2026-10-29.md、requirements/managed-chat-execution/tests/018.test.mjs、requirements/managed-chat-execution/WHAT.md。
Input has no exact accepted evidence：1 处。生产路径 无。其余：指南.txt。
AuthorityRootAppendFailure：本轮 `rg --no-ignore` 全树零命中。

本轮读到 `requirements/capability-enforcement/tests/026.test.mjs` 第 406-409 行：

```
406|// 跨进程隔离：DevOps 派工门的能力不得以进程局部状态跨进程走私。produce 进程
407|// 在评审接纳前被拒、接纳后被放行；recover 进程在同一个工作区重新打开时，未评审
408|// 的会话仍被 assessment-pending-for-devops 拒绝，已确认的会话被 REVIEW_PENDING
409|// 拒绝。两个进程必须真的不同。
```

上文已有的「畸形 identity carrier」「缺持久 authority」两条实现声称，不因这次检索升格为裁定，也不改写成已解决。
