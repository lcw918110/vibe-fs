# attention-regulation — WHAT


> **COMPAT（2026-10-29）现行口径**：Scope 生命周期入口是任意工具上的打平注解 `scope.kind` ∈ {`resetScope`,`keepLast`,`removeLast`,`meToo`} 与 retention（`wanxiangli/scope-retention` + 共同法 «Scope»）。**无独立 `resetScope` 宿主工具**；档案重锚 = 注解 `resetScope` → `ScopeReset.commit`。**无** `remainingScopeTurnsEstimate`／`turnBudget`／KPI 倍率产品契约；Stall／ResetDue 估计路径已从宿主产品面退役。下文若仍写独占工具、估计必填或倒计时后缀，以本条为准。PairHint 屏障优先认 `scope.kind=resetScope`，旧工具名只读兼容。


本包现为退休边界：`enough`、`abandon` 与 `defer` 工具及其 `Attention` 持久投影均已退役，职责由 `assume` 与 `resetScope` 协议承接。保留这些条款，是为了锚定替代路径并防止已退役的注意力状态机回流生产路径。

## [001] enough 语义并入 assume

`enough` 工具已退休。声明「当前信息已足以支持下一步行动；除非出现此前未消费且足以改变决策路径的新事实，否则不再重开同一判断」的语义，改由 `assume(assumption)` 表达。它不证明决策正确，不创造权威事实或持久认知状态；无新事实的重复调用不增加语义。禁止以兼容、别名或历史名义重新注册 `enough` 或任何同名工具。

## [002] abandon 语义并入 assume

`abandon` 工具已退休。放弃自行形成的计划、推论或假设（无须审批或理由检验，使其不再自动占用注意力）的语义，改由 `assume(assumption)` 表达。放弃自我承诺不得取消真实任务义务、撤销用户授权、删除工作产物或终止会话。禁止以兼容、别名或历史名义重新注册 `abandon` 或任何同名工具。

## [003] defer 退役，旁支记录并入编程面与 resetScope 经验传承

`defer(new_work)` 工具已彻底退役，不在 `StaticTools.knownToolNames`、`permissionFor`、`requestToolMap`、`ToolRegistry` 或 `resources/ablation/tool-map.json` 中登记或放行。非阻塞旁支工作改由编程面（`todowrite-for-<role>`）程序编排、活跃 scope 内部记录，以及 `assume(assumption)` 承载；跨 scope 的关键备忘在 `resetScope` 时逐字原样抄写进 `oldScopeVerboseVerbatimExperience`（见 context-compression [033]）。Pair Hint（`resources/provider/host/pair-programming-guideline/**`）不再注入 `defer` 鼓励句；`resources/provider/attention-regulation/**` 资源文件全部删除。

## [004] 不存在 Interaction/Attention 运行时或工具模块

生产编译图不得包含 `Interaction/Attention`、`OpenCode/Tools/AttentionTools`、`OpenCode/Tools/AttentionToolSurface` 或 `Composition/Durable/AttentionConcernJournalAdapter`，也不得保留 `Wanxiangshu.Owner.attention-regulation.*` 编译分片。

## [005] 终点与退休流程不再执行 DeferredWork 消费注入

`OrdinaryTurnWorkflow` 在 Engineer、DevOps 或 Orchestrator 自然终止时不再读取或发送 `presentPendingDeferredWork` continuation；Manager `DeliverTool` 不再从 `AgentProjection` 读取 `pendingAttentionWorkPairs` 待办列表。`PromptContinuationKind` 不再包含 `DeferredWorkPresentation`：它已无生产者，保留只会是一个无人发出的准入标签。

## [006] durable fact 与 projection 不含 Attention

`AgentFact`、`AgentProjectionSet`、`ProjectionUpdate`、`Fold`、`AgentJournalPortAdapter` 与 `resources/ablation/fact-map.json` 均不得包含 `Attention`、`DeferredWorkRecorded`、`DeferredWorkConsumed` 或同义持久状态。

