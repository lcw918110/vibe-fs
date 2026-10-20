# interaction-authority — WHAT

## [001] 物理用户消息不等于 authority turn

物理 `role=user` 消息仅是传输层形态。物理消息标识符必须经由唯一的显式提升通道在物理接收已确立（`PhysicalAccepted`）后方可升级为 `AuthorityRoot`。不存在从传输收据直接转换为 AuthorityRoot 的通道。

## [002] 形态不是 authority 证据

零宽字符、空白排版、固定模板、时间戳、文本长度或合成配置中的注释/字段形态均不能作为 Authority 身份证明。消息的权威性仅能由系统内建的 typed 来源机制判定。

## [003] Root 独占权

`AuthorityRoot` 具有独占权限：
1. 创建新的 Logical Run；
2. 提交显式 participant 选择，并绑定 `participant-identity` owner 为该 exact run 准备的版本化 `ParticipantIdentityEvidence`；
3. 成为新的 Fallback 根节点；
4. 重置 Interaction Repair 预算；
5. 成为后续 execution binding 的延续基准。

`AuthorityRootAccepted { SessionId; LogicalRunId; AuthorityRootId; RootKind; ParticipantIdentityEvidence; initial per-physical target/lease }` 是 root acceptance 与 identity installation 的唯一 durable fact payload，必须以一次原子 append 接受或拒绝。Authority 不得先持久化 identity 再接受 root，也不得依据显式 agent 文本自行推导或拥有 participant、Role、Persona 或 provenance/version；它只校验 evidence 的 exact key/owner witness 并保管 payload。append 成功后的同一 fold 原子建立新 active root、绑定 exact evidence、清空已关闭 prior run 的 claims/continuation 映射/序列号；append 未提交则两者均不存在。

## [004] Continuation 禁区

所有类型的 Continuation 仅用于延续已存在的 Logical Run，绝对禁止执行 Root 独占操作：不得新建 RunId、不得替换或字段级修改 `ParticipantIdentityEvidence`、不得更新底层 AuthorityProfile、不得重置 Fallback 或 repair 预算。Continuation 必须完整继承宿主 Run、Root 标识与 exact identity evidence；按 per-physical target/lease 规则的 execution binding 变化只改变物理目标与 lease，participant 保持不变。

## [005] 四类 provenance 与两种 Root

系统严格区分四类来源形式：`AuthorityRoot`（包含 `HumanRoot` 与 `AgentOwnerRoot`）、`Continuation`、`HostInternal` 与 `UnknownOrigin`。该分类为闭集合；`AgentOwnerRoot` 必须携带 participant-identity owner 为 exact child/attached/InternalLeaf run 准备的 typed owner-derived identity evidence，且 OwnerLogicalRunId、LogicalRunId 与 root key 必须精确匹配。任何 Continuation 均不可被解析为 Root，反之亦然；缺失、wrong-owner 或 wrong-run evidence 一律归入 `UnknownOrigin`。

## [006] HumanRoot 必须显式命名 participant

`HumanRoot` 必须显式指定合法 participant 本名。该名称只是交给 participant-identity owner 的 root identity 请求中的期望 participant，由 owner 校验并返回版本化 identity evidence，不是 Authority 自行推导 Persona/Role 的依据。省略名称、使用 legacy 名称、连字符/大小写变形、格式错误或缺少 owner 返回的版本化 identity evidence 必须 fail-closed，禁止静默猜测或从 Session cache 补全。

## [007] UnknownOrigin fail-closed

`UnknownOrigin` 绝对禁止更新执行 Profile、启用 Fallback 或发起任何 Continuation。无法证明来源合法性的请求必须立即阻断。

## [008] 来源解析优先级

消息来源按固定优先级严格判定：已确认的 Host 消息 > 已 Claim 的 agent-free PromptKey > Host 内部 Compaction/Synthetic > 已注册的 AgentOwnerRoot > 外部证明合法的 HumanRoot > UnknownOrigin。优先级顺序本身构成安全边界，避免真实业务消息被内部机制降级或冒充。

## [009] 纯函数永不推断 HumanRoot

来源判定中的纯计算函数绝不推断返回新的 `HumanRoot`。`HumanRoot` 只能在激活 Profile 缺席且携带合法显式 participant 时由 Ingress 边界授予；活跃 Run 中携带同一 participant 的外部用户消息只能成为绑定既有 Profile 的 `HumanMessage` continuation，缺失或漂移 participant 的未知消息必须拒绝，绝不可抬升为 Root。Ingress 边界在外部消息未显式携带 participant 字段时，缺失保持缺失，由权威入口（ChatAdmission intent 解析）通过当前 Session 的 Durable Authority Projection 按既有合法规则解析 durable active/history；若存在确定性权威，则以该既有 participant 身份接纳为 `HumanMessage` continuation 或为新任务开启 HumanRoot，禁止因 Host 界面请求体省略 agent 字段而误判为 UnknownOrigin，亦不得把 Host/session 缓存的默认值包装成显式用户输入。continuation 接纳后，Host 当前物理 user-message binding 必须推进到该消息，供 reconciler/provider-start 观察 exact 新物理目标；既有 Authority Root identity 不变。

## [010] 自动 repair 稳定 exact occasion identity

自动合成的 repair 绝不可借机抬升权限。其持久化 identity 必须绑定 exact `(SessionId, LogicalRunId, request, ProviderRunIdentity, terminal kind)`；同一 occasion 的 duplicate observation 幂等吸收，任一字段变化都是新的 repair occasion。transport receipt 只是物理进度，不参与不可变 repair identity。普通 gate nudge 的飞行态与 fresh-terminal re-arm 只由 interaction-authority-019 定义。

## [011] authority 是原子 profile 内的稳定子记录

每次执行的 `AttemptExecutionProfile` 必须原子携带 exact SessionId、LogicalRunId、AuthorityRootId、当前 per-physical target/lease，以及 `AuthorityRootAccepted` 中 participant-identity owner 准备的完整版本化 `ParticipantIdentityEvidence`。Authority fold 向 Host/execution 消费者逐字段精确暴露固定 participant、Role、稳定 Persona 与 provenance/version，但不拥有、重新解析或修改这些字段；当前 per-physical target/lease 只来自 execution binding。禁止从 Session cache、物理 parent、显式 agent 文本或分散消息拼装 profile。

## [012] degeneration-guard 是 continuation 而非 fallback 失败

degeneration-guard 自恢复消息（`DegenerationGuard`）等属于强类型 Continuation。它们延续当前 LogicalRun，复用既有 Root 与 Profile，不得建立新 Root、不得改变 per-physical target/lease、亦不得计入模型重试失败次数。`DegenerationGuard` 不得伪装成 `ProviderRetryAttempt`。

## [013] 显式 continuation 绑定保持 authority continuity

同一 LogicalRun 下的强类型 continuation 推进属于权限连续演进：仅 execution binding 的 per-physical target/lease 可按规则变化；participant、Root、identity evidence 与 Profile 关联全部保持不变。SessionId 相同但 LogicalRun 不同不构成 continuity。新物理目标的路由绝不改变 participant。

## [014] Nudge 与 JoinGuard 是 Continuation

JoinGuard、闲置 Nudge 等流转控制指令均为 Continuation，不产生新的 Authority。在存在未决后台任务时仅允许发送 JoinGuard 延续等待，禁止隐式创建新 Root。Nudge 是 gate reminder 而不是一次性预算：只要对应业务 gate 仍未满足，每个新的合法 terminal occasion 都必须重新获得提醒资格；幂等范围只允许收窄到同一 exact `ProviderRunIdentity` occasion，禁止用 Session、LogicalRun、Life 或 barrier 本身永久压掉后续 fresh terminal。

## [015] external-user ingress 不授予 authority

处于运行中途的外部用户消息仅作为低权限唤醒信号打断等待，不取消当前运行时，不直接赋予 Prompt authority，亦不重置 LogicalRun 或新建生命周期。

## [016] Root claim 不进入 continuation 映射

接受 `AgentOwnerRoot` 的 claim 不会将消息写入 Continuation 查找映射。曾经作为 Root 的物理消息不能作为后续判定 Continuation 的依据。

## [017] continuation 只能接续 active run

Continuation 只能挂靠当前活跃的 `ActiveLogicalRun`，绝对禁止回退挂靠已归档或结束的历史 Profile。

## [018] 每种 AuthorityRoot lifecycle 都收敛为 exact durable closure

每个 `AuthorityRootAccepted` 必须原子记录唯一 `ExpectedClosureKind`。闭集合为 `HumanRootManagerLife`、`HumanRootManagedRun`、`AgentOwnerChildWork`、`AgentOwnerAttachedWork`、`AgentOwnerInternalLeaf`；前两种按 HumanRoot 的实际 lifecycle 穷尽，InternalLeaf 无论物理 Ownership 为 Root 或 Attached 都只能使用最后一种。各 kind 唯一合法 source witness 分别是 exact Manager `LifeCompleted(LifeId, FactId)`、`ManagedLogicalRunTerminal(LogicalRunId, FactId)`、`ChildLogicalRunTerminal(OwnerLogicalRunId, ChildLogicalRunId, FactId)`、`AttachedLogicalRunTerminal(OwnerLogicalRunId, ChildLogicalRunId, AttachmentKind, AssociationGeneration, FactId)`、`InternalLeafTerminal(OwnerLogicalRunId, LeafLogicalRunId, DecisionOrTransactionId, FactId)`。每个 `*Terminal` 是该 lifecycle 对 `Completed | Cancelled | Failed` 合法结果的 durable closed outcome，不包含 request、signal 或 observation。任一 HumanRoot 或 AgentOwnerRoot 缺少或无法唯一归入该闭集合时不得被接受。

Authority 的 durable terminal interpreter 校验 source witness 与 accepted root 的 kind、owner、SessionId、LogicalRunId、AuthorityRootId 全部精确匹配后，幂等追加唯一 `AuthorityLogicalRunClosed { SessionId; LogicalRunId; AuthorityRootId; RootKind; ClosureWitness }`。append 成功后的同一 fold 清空对应 `ActiveLogicalRun` 与 run-scoped mappings，释放 active identity-evidence binding并归档历史 Profile；重复相同 closure 幂等，冲突 closure fail-closed。source terminal 已 durable 但 closure append 尚未确认时，reconciliation 只能重放该 typed source 并重试相同 closure append；不得释放 binding 或允许 SessionId 复用。lifecycle terminal 本身、association removal、cancel request、idle/timeout、wall clock、Host observation 或旧 Profile 均不得推断 closure。

## [019] gate nudge admission、飞行态与 fresh-terminal re-arm 必须分型

`InteractionRepair` 的 claim/Submitted/PhysicalAccepted 只建立一次 gate-nudge attempt 的 admission/物理落地证据，不建立 gate completion，更不建立“提醒预算耗尽”。当前 nudge attempt 仍为 `finish=None`、`tool-calls` 或其它明确 in-progress 观测时，重复 idle/reconcile 必须保持等待，禁止并发发送第二次 nudge。若该 attempt 自身到达新的稳定但仍不满足 gate 的 terminal（例如空/XML-only `stop` 或 `length`），该 fresh `ProviderRunIdentity` 必须重新获得一次 nudge 资格；同一 terminal 的重复观测仍严格幂等。普通旧 turn 在后续 nudge 已 admitted 后再次被观察，只能幂等吸收，不能替 fresh terminal 消耗提醒资格。普通 gate nudge 不发布 `INTERACTION_REPAIR_EXHAUSTED`。

## [020] repair fatal绑定exact claim settlement与注入fuse

只有typed repair invariant incident可以请求fatal；当前 agent-free PromptKey claim、Submitted/PhysicalAccepted与fresh terminal判定必须先形成exact settlement evidence。InteractionRepair不得直接引用fatal physical adapter、optional/default/global fallback；composition注入mandatory capability。同一incident只允许一次report与kill，普通exhaustion或可恢复send failure不得升级为fatal。

## [021] 历史事件不可变与旧身份不升权

历史 EventStore 事实保持原样，历史事件中记录的旧身份（Coder、Inspector、Browser、Inquiry、Distiller）仅用于历史审计、会话回溯与不变重放，严禁重写事件日志。
历史旧身份解码严格隔离在历史边界，严禁在活跃权限计算或执行准入中将旧 Inspector/Coder 静默升级为新 Engineer 权限；新交互只接纳当前合法身份集合（Engineer、DevOps、Manager、Orchestrator、Plan）。Blogger、Predictor 与只读副本不是合法身份。非法或遗留身份的交互请求必须 fail-closed 阻断。

## [022] DevOps 恢复与续行保持固定模型与执行权威

DevOps 的固定绑定由道路初始化确立，其模型配置与 Persona 在生命周期内不可变。
后续所有的 resume 与 continuation 必须严格沿用既有绑定，严禁借 resume 动态切换模型或切换到非绑定模型；多次物理尝试或崩溃恢复均必须收束为同一逻辑执行权威，严禁形成多个并行生效的 DevOps 权威。

## [023] ProviderRetryAttempt 的 repair 抑制随 attempt 终结而失效

`missing-final-report` 与 `interaction-repair` 对 `ProviderRetryAttempt` continuation 的抑制，只在该 continuation 自身的 attempt 尚未终结时成立：观测仍为 `TurnUnknown`（finish=None）或 `TurnInProgress`（tool-calls）且没有已落地的 exact terminal 时，idle/reconcile 必须保持等待，禁止并发 nudge 抢夺该 attempt 正在生成的响应。

该 attempt 一旦终结，抑制立即失效：稳定 terminal 观测（`TurnCompleted`、`TurnFailed`、`TurnAborted`，以及 `TurnNeedsContinuation` 的来源——`length`、没有 provider error 的空/XML-only `stop`）或 exact durable `ChatExecution` Terminal 任一成立，本次 `ProviderRunIdentity` 就按 [019] 重新获得 nudge 资格；gate 未满足时，此后每个新的合法 terminal occasion 都必须重新获得资格。两类终结证据相互独立，先到者即解除抑制，不等待另一侧追平。解除抑制不改变结局归属：completed-with-error 属于 `TurnFailed`，即使输出为空也必须等待 exact typed failure，由 provider recovery 处理，不得转成 missing-final-report。

抑制判据只能是 attempt 的在途/终结状态，严禁使用 durable physical 身份、continuation kind、Session/LogicalRun 或 ledger 条目本身的存在作为永久压制条件：凡凭“该 physical 曾被接受为 ProviderRetryAttempt”压掉后续 fresh terminal 的实现均为 RED。同一 exact terminal occasion 的重复观测仍由既有 publish/dedupe 幂等吸收，不因解除抑制而重复发送。
