# execution-failure-policy — WHAT

## [001] 封闭失败分类

执行失败在最早可信边界归入 `LocalInvariant`、`ProtocolRejection`、`AuthorizationDenied`、`UserCancelled`、`Superseded`、`CapacityQueueFull`、`ProviderTransient`、`ProviderPermanent`、`AcceptanceUnknown`、`StreamInterruptedAfterFirstToken` 或 `PersistenceFailure(NotCommitted | Committed | Unknown | NoNewWrite)`。`NoNewWrite` 只承接已明确没有新追加、但存储释放失败的 receipt，保留其操作失败，不降格为成功或提交未知。异常类型、状态码与公开 Host 证据可参与解码；message、stack、stderr 等自由文本只作诊断，不决定类别与后果。

## [002] 单一完整决策

唯一纯策略根据失败类别、durable execution phase、确切容量所有权与 provider recovery facts，一次给出互斥的 `PreserveCurrentFact`、`AwaitAcceptanceReconciliation`、`RetryFreshAttempt`、`TerminalizeAcceptedPreProvider` 或 `TerminalizeProviderStarted`，同时确定 breaker、容量结算和 fatality。调用方只解释这份不可拆分的决策，不另算、覆盖其维度或默认重试；重试与终态不得并存。

`PersistenceFailure(NotCommitted)` 固定保留当前事实和已持有的 exact fence（未持有则不结算），不改变 breaker、不 fatal，并停止在未提交步骤的所有后继边界之前。只有新的 typed persistence/recovery event 可重新裁决，不因此扩大 provider retry 权限。

`PersistenceFailure(NoNewWrite)` 保留当前事实和 exact fence，不改变 breaker、不授权 provider retry，不自动执行成功后的步骤；存储释放故障按 [006] 的结算前提保留 fatality，不能用已有事实反推本次操作已成功释放资源。

## [003] Provider retry 授权

仅 `ProviderTransient`、`ProviderPermanent` 可在 `ProviderStarted × Closed breaker × Available retry budget × 可恢复 request kind` 下授权 retry；`Open` 或 `Exhausted` 输出终态，其他 phase 不重试（`StrengthReplica` 与 `BloggerMain` 已随 clean-break 彻底退役）。恢复事实只含一个预算、一个 breaker、request kind 与 logical/provider run identity。其他失败类别不授权 retry；提交未知先 reconciliation，已有可见 token 的中断不自动重放。

每次 retry 携带 caller 不可构造的授权，精确绑定 `LogicalRunId`、源 `ProviderRunIdentity`、request kind 及由三者纯派生的稳定 `ProviderRecoveryDecisionId`。同一决策重放 identity 不变，新 attempt 产生新 identity；恢复 prompt 标识含 decision 与源 attempt identity，可见文本保持一致。同一授权的重复物理发射由 ledger owner 去重。

## [004] Exact capacity settlement

容量结算仅为 `NoCapacitySettlement`、`RetainExactFence` 或 `ReleaseExactFence`。释放必须携带本次 admission 的不可伪造 fence，由 `execution-model-routing` 原子消费；无 fence、旧 epoch、错误 target 或 physical message 不得释放容量。失败、取消、supersede 与 fatal 均不得用计数减一、session-wide release 或 best-effort cleanup 代替确切结算。

## [005] 按事实阶段终结

终态处置随 Resolution 携带 exact execution key 与 typed disposition：`TerminalizeAcceptedPreProvider` 只处理已 Accepted、未 ProviderStarted 的执行；`TerminalizeProviderStarted` 只处理已 ProviderStarted 的执行。`managed-chat-execution` 验证 key 与 durable phase，拒绝跨阶段、跨执行处置，独占消息事实和合法迁移；本包不直接写消息事实，也不以诊断文本决定终态。

## [006] 结算之后才可 fatal

`FatalAfterSettlement` 仅来自 `LocalInvariant` 或无法安全继续的 `PersistenceFailure`。解释器遵守各 phase 的结算依赖：pre-provider terminal append 取得 `Committed` receipt 后才可释放 exact fence，`NotCommitted` 或 `Unknown` 不释放。fatal 始终最后执行，前提是所需 disposition 与 exact settlement 成功，或已有对应提交未知的 durable evidence；不得吞掉结算失败、立即退出或用 Host/UI 行为代替证据。

## [007] 未知不得当作未发生

`AcceptanceUnknown`、`PersistenceFailure(Unknown)` 保持显式未知，依靠 durable reconciliation 或外部物理证据收敛，不映射为未发生、`NotCommitted`、可重试 provider 失败或成功。收敛前不重复发消息、启动 provider attempt、获取或释放容量；只有确证本次 append 未写入事实的 receipt 才能判定 `NotCommitted`。

## [008] 时间不授权处置

策略与解释器只由 typed input、durable fact、capacity event、Host terminal evidence 或 persistence result 推进。deadline、sleep、elapsed time、轮询次数与错误文本不授权 retry、breaker transition、容量结算、终态或 fatality。

## [009] Host session error 边界

Host 上报的 session error 一律解码为 `ProviderTransient`，只有 typed operator abort 与 supersede 分别为 `UserCancelled`、`Superseded`。工具语义错误留在 Host 的 part 循环；plugin、config、schema、permission 等 hook 失败仍按 `host-provider-failure-ownership` fail-loud，其他可信边界仍可产生 [001] 的其他类别。

恢复由 `provider-attempt-recovery` 独占：去掉旧 retry 行，以 `ProviderRetryAttempt` 执行原样重试或拉黑 Provider 后重试（[021]）；不盲目重放已有可见输出的 attempt。预算耗尽按 `host-provider-failure-ownership` 产生唯一 typed terminal。

## [010] 致命入口逐处有据

所有生产 fatal 入口及转入边界必须登记触发前提、所属状态和不可达证明或实际回归；新增入口、入口漂移、缺失证据使标准验证失败。性质测试调用生产实现，以固定 seed 重放反例，不用自造模型替代。

协议修复耗尽、过期回调、确定未发送和运行环境拒绝，不因“未预料”就成为 `LocalInvariant`。可达失败由所属请求或资源收敛，不释放他人的所有权、不重发未知 effect；保留的熔断满足 [006]。

## [014] 可检查的致命入口索引

fatal 索引用稳定 ID 登记 owner、源符号与 operation、触发与输入、exact identity、phase、不变量、提交和资源处置、影响范围、证据及正式测试。按符号与 operation 定位，不以行号定位；状态为 `Open | Proved | PropertyChecked | FixedWithRegression | RetainedFuse | ExcludedWithEvidence`。

标准检查拒绝未登记入口（含间接转接）、过期定位及缺失的正式回归。真实的进程致命入口与受管子进程终止、存活探测、解码选项、结果类别等同名概念分开，排除也须有据。索引不另定失败策略。

## [011] 未分类 hook 失败保留自有证据

不可识别 hook 异常只能由本次实参证明 execution key：会话标识与末尾物理用户消息必须无歧义。能证明则下发 `SettlementIncomplete`，由所属 phase 的 settlement owner 处置；不能证明则 key 为 None，均不得补造干净终态。原异常不变地交回 Host，不升级为 process fatal，也不降为可重试 provider 噪音。

## [012] StopPhysicalRun

`StopPhysicalRun` 先封住确切 execution 的 provider admission、抑制在途 step 并交还 custody，再独立请求 Host abort。abort 在途、拒绝、抛错只作诊断，不恢复该 execution 的 admission，不影响其他 execution。

## [013] 分清致命路径的生命周期

不同触发、commitment 与结算状态不得因共用 fatal operation 而合并处置。成功结算的请求进入 catch-up，协议违例保留其 fuse，`SettlementIncomplete` 与 `Unknown` 未经证据不得当作未执行；空 transform、repair 协议违例与 committed/unknown settlement 毁约分别保留其精确路径。
