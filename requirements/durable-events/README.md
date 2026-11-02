# durable-events — README

本文件说明 022 的局部编译检查做了什么、覆盖哪些 locality；规则正文见 WHAT.md，本文件不新增规则。

## 022 — locality flat compile 缺边探测

tests/022.test.mjs 的 integration 用例按 locality 做缺边探测：每个 locality 经 planOwnerCompile 从声明的 ProjectReference 闭包推出编译清单，物化成零 ProjectReference 的 flat fsproj，用一次 Fable invocation 编译。闭包缺边时正例会红（例：workspaceeventstore→sphinx-v2-integration、sphinx-v2-integration→delegation.runtime，补边后转绿），比词法审计更贴近真实编译。

集合成员（2026-10-07 时 20 个 locality 全部在集）：

- EventStore contract tier（预算 ≤100 production sources；另受闭包白名单与反向依赖负例探针约束）：eventstore-model-contract、eventstore-append-result-contract、eventstore-port-contract、eventstore-event-vocabulary-contract、eventstore-git-contract、strength-event-vocabulary-contract、casebook-event-vocabulary-contract、js-transaction-event-vocabulary-contract。
- EventStore focused runtime tier（预算 ≤185；persistence subsystem 断言）：eventstore-core-runtime、eventstore-git-runtime。
- 扩展 contract tier（预算 ≤100，按 shard 声明的 LocalityKind=contract 归档）：interaction-authority-fold、execution-session-syncdelegaterole、journal-outcome-contract、repository-programming-js-capability。后者实际focused编译暴露曾借Git contract间接取得IEventStore；现在显式引用原Port，31个production fs，真实flat正控防止聚合构建掩盖缺边。
- 扩展 focused runtime tier（预算 ≤185，按 LocalityKind 归档）：interaction-authority-fact、interaction-authority-ledger、authority-runtime-surface、dispatch-runtime、context-companion-fold、persistence-journal-agentjournal。
- authority-runtime-surface 入集记录（2026-10-04，shard 重组卡销项）：flat 编译暴露的 5 个闭包外命名空间缺边（Change、Context.Trace、Enforcer、Execution.Fission、Persistence.EventStore——Child.fs/CompletedTurn.fs）经依赖调查确认为死 open——两文件对这些命名空间（含 Enforcer.Guidance）零符号引用，删除 12 行 open 后闭包保持 118 production sources（≤185 ratchet），零 ProjectReference 补边、零行为变化；原「拆分/下沉重依赖」待办方向作废，test.todo 转正为 flat 编译正例与 ≤185 预算断言。

扩展集合的取舍（按优先级）：(a) W1/W3/W4 施工卡入口 shard——interaction-authority（C1/IA018 的 Ledger 与 Fact、IA017 的 runtime surface）、dispatch-protocol（DP002/session008 入口 dispatch-runtime）、durable journal（DE023 可开工，persistence-journal-agentjournal）、context companion（W4 前置）、session-ontology sync delegate（上批补边 shard，正例守护该边不回退）；(b) 词法审计命中区（闭包外 open 的命名空间）；(c) 闭包规模在预算分档内。扩展 tier 不进入 EventStore 特有的闭包白名单与 subsystem 断言——那些语义属于 EventStore 边界；扩展 tier 只由 flat compile 正例与同数值预算断言承担。

静态推演的预期暴露点（近似词法推演，最终以真实编译为准）：Wanxiangshu.Participant.Provider 命名空间在 dispatch-runtime、context-companion-fold、persistence-journal-agentjournal 的闭包外被 open（定义 shard 为 runtime-platform-language、participant-provider-languagesurface、participant-provider-language）。authority-runtime-surface 原推演的 EventStore/Trace/Enforcer/Fission/Change 等闭包外 open 经 flat 编译实测定位为 Child.fs/CompletedTurn.fs 的死 open，已删除（词法审计 report-only 清单仍可能命中其他 shard 的死 open，属排查起点而非结论）。正例若红就是真实缺边：保留断言，由 DevOps 补 ProjectReference 边或删死 open 后复绿，不放宽断言。

调查过但闭包超预算未入集的 shard（静态推演闭包 production .fs 数，如实记录跳过原因）：delegation-runtime-surface 474、execution-delegation-hostturnobservedsurface 448、join-guard-surface 453、composition-turn-scheduler 239、opencode-host-chatadmission-transaction 375、context-compression-runtime-surface 281、persistence-journal-surface 303、persistence-journal-obligationsurface 305、durable-runtime-surface 352、opencode-host-workspaceeventstore 529、opencode-host-turnruntimepreparation 528。其中 durable-runtime-surface 的 Surface.fs 另有一条闭包外 open（Wanxiangshu.Sphinx.V2.Composition，定义 shard sphinx-v2-integration），供后续扩卡参考。

反向依赖负例（Git runtime 与 Host adapter 的闭包拒绝）与 journal observation owner 的编译正例保留。`durable-runtime-surface` 曾缺 Sphinx Bind 依赖，focused Fable 真实拒绝后补了显式 `sphinx-v2-integration` 边；它当前 586 个 production fs 属 composition，不算 ≤185 的 focused locality。上段的 352 等数字是静态推演，不是现行闭包数。

## 局部运行

node --test requirements/durable-events/tests/022.test.mjs（构建须 node scripts/build.mjs；编译与红绿由 DevOps 执行）。
