# dispatch-protocol — WHAT

## [001] PromptDispatcher 是唯一写入口

所有由插件或内部机制生成的 user-shaped 消息（包括 Guard、repair、Finality steer、nudge、重试及 Orchestrator 提示等）必须通过统一的 `PromptDispatcher` 发起，绝对禁止任何旁路机制直接绕过滤网向宿主发送提示。

## [002] 四态 claim 生命周期

单次调度的持久化事实严格遵循四态流转：`Claimed → Submitted → PhysicalAccepted` 或 `Claimed (→ Submitted) → Abandoned`。`Submitted` 记录传输回执但保持 Claim 处于待决状态；`PhysicalAccepted` 证明物理落地并完成 Claim；`Abandoned` 代表调度放弃且不再重发。在物理发送前若因状态变更失效，必须显式记录为放弃，禁止伪装成传输失败或成功。

## [003] transport receipt 不等于物理消息身份

宿主返回的 `accepted-*` 仅表示传输层已接纳该请求，不是物理消息标识符，亦不是权限生效的证明。系统不能仅凭传输收据推断消息已被实际处理。

## [004] physical acceptance 只由真实物理证据建立

`PhysicalAccepted` 状态必须且只能由真实的物理消息证据确立（例如运行时捕获明确的物理消息 ID，或在恢复阶段在宿主历史中匹配到包含完全一致的 agent-free `PromptKey` 的物理用户消息）。对包含 agent 的 PromptKey 提供只读单向解码用于恢复匹配，绝对禁止双写或作为新调度证据。

## [005] PromptKey 是确定性幂等身份

`PromptKey` 是由 SessionId、LogicalRunId、AuthorityRootId、Origin、载荷摘要（PayloadDigest）及 ClaimSequence 派生的确定性哈希，禁止使用随机数生成，亦不哈入任何 agent、peer 或 model。相同逻辑交互在任何进程中派生完全一致的 Key，任意要素变动均会导致 Key 发生迁移。对包含 EffectiveAgent 的 PromptKey 提供只读解码兼容，绝对禁止向新调度双写或派生。

## [006] 同 payload 的两个独立 logical act 仍可区分

`ClaimSequence` 在 `(SessionId, LogicalRunId, Origin, PayloadDigest)` 作用域内单调递增，且在 Claim 注册时立即消费。即使相同载荷的消息在放弃后再次发送，也会获得新的序号与新的 `PromptKey`，确保同载荷的多次独立调用能够被精确区分。

落地证据同样按 act 区分：每个 physical message 的 `PhysicalAccepted` 以该 physical message 为键精确保留它落地的 claim。按 payload 汇总的 occasion 视图只回答“这个载荷是否已落地”，后一次同载荷落地会覆盖它、放弃同载荷的待决 claim 会清空它；二者都不得抹去更早 physical message 的精确落地证据。凡是要回答“这条 physical message 是哪个 claim 落地”的判断，只能读精确落地证据。

## [007] uncertain physical outcome 不自动重发

在崩溃恢复或证据核对中若未能检索到物理落地证据，Claim 必须保持 `StillPending` 状态，绝对禁止系统自动重发，亦不得因进程重启次数累积而静默判定放弃。反之，Host 若在物理 acceptance 之前给出确定的 `Retryable/Fatal` 拒绝，则该 attempt 可显式 `Abandoned(SendFailed)`；对 idle-derived gate nudge，只有这种“确定未发送”结果允许把 exact quiescence permit 归还为可重试，任何 acceptance-unknown / 持久化不确定性都不得 re-arm。业务层判断 exact occasion 是否已经提醒，只能依赖仍 Pending 或已 Accepted 的 dispatch evidence；历史 ClaimSequence 只区分重试 PromptKey，不是 effect/admission witness。

## [008] at-most-one logical effect 不虚构 exactly-once

协议坚守至多一次（at-most-one）逻辑执行保障与未知结果 fail-closed 原则。terminal-scoped gate nudge 的 `(SessionId, LogicalRunId, Origin, PayloadDigest)` exact occasion 在同一 dispatcher runtime 内只允许一条 claim→send flight；并发观察者必须等待同一个 PromptKey 与结果，不能各自消费 ClaimSequence、写 claim 或调用 Host。flight 完成后，只有 durable projection 证明前次已明确 Abandoned 才能开启新 sequence；Pending、Submitted、PhysicalAccepted 均不可重发。禁止伪造物理投递的 exactly-once，禁止以时间窗口粗暴替代 PromptKey 校验，禁止为消除挂起状态而盲目重发。

## [009] Detached 在 durable claim 后立即交还控制

在分离模式（`AwaitMode.Detached`）下，调度器在完成 durable claim 记录与宿主异步调用入栈后即刻返回 `PromptKey`，调用方不得阻塞等待模型容量调度、provider 执行或物理落地证据。若异步入栈后续发生致命拒绝，系统应触发进程级审计报错，且保留 Claim 待决记录而不自动重试。需要同步获知传输拒绝分支的场景必须显式使用 `AwaitMode.Await`。物理消息落地后的 durable execution 由 `managed-chat-execution` 独占，dispatch 不创建或推进 execution facts。

## [010] Root 与 dispatch 不得选择、等待或覆盖 model

调度与 Authority Root 阶段严禁指定、修改或等待底层物理模型 ID。发送参数固定为未指定模型（managed send 固定 `Model=None`），具体的模型分配与算力租赁严格延迟至宿主执行准入阶段由专门路由模块裁决。
发送的 Host `agent` 固定为不可变的 `participant`：Root 直接使用已验证的 `IdentitySeed` 投影，Continuation 使用既有 active profile / `AuthorityExecutionProfile`；`model` 固定为 null。发送不要求会话形态的执行绑定缓存存在。continuation 保持 participant 不变，fresh physical target 路由永不改变 participant。显式外部 agent 仍作为输入保留，并与 participant 做一致性校验；wire 中未提供 agent 时保持缺失，由 authority ingress 按既有合法规则解析 durable active/history，不得从会话缓存补出字符串再称为 ExplicitAgent。

## [011] 插件 user-shaped message 一律经 PROMPT-005

受管委托的工作派发使用 `SendManagedAssignment`：目标尚无 active run 时认领 `AgentOwnerRoot`，已有 active run 时发送 `ManagedDelegationAssignment` continuation，继承原有身份与 root。只有首次 root 需要取得 owner identity seed；两条路径保持各自明确的工具权限，不从 owner 的普通工具集合继承权限。

所有内部生成的合成用户消息必须携带合法的 `PromptKey` 与结构化来源元数据。此举保证缺乏插件元数据的消息能够被无歧义地识别为真实的外部物理用户输入。

## [012] PhysicalAccepted 后只交接 exact identity

Dispatch 在建立 `PhysicalAccepted` 后只向 `managed-chat-execution` 交接 exact `(SessionId, PhysicalUserMessageId)`、agent-free `PromptKey` 与 `interaction-authority` 发布的原子 `AttemptExecutionProfile`；该 profile 必须直接包含由 `IdentitySeed` 派生且在 logical run 内不可变的 `ParticipantIdentityEvidence`（原子包含固定的 participant、role、persona、personaCatalogVersion 与 provenance evidence；continuation 保持 participant 不变，不存在 peer/effective agent 轮换语义），不得退化为可重新推导的 authority metadata。每个 fresh physical execution 将固定的 role 经 MJS scheduler 路由至模型 target，且 capacity exact identity 严格绑定为 `session + physical + role + participant + target + fence`。Turn reconciliation 不依赖会话形态的执行绑定缓存；需要 participant/role 时从同一 durable authority profile 解析；显式 Host agent 证据优先，但缺席的 role 不得遮蔽 durable role，否则同一 Manager continuation 会被误路由为 Ordinary。Accepted execution evidence 只暴露由 `IdentitySeed` 派生的 participant+role，不含 effectiveAgent。`managed-chat-execution` 独占 durable execution acceptance、provider start、terminal 与 settlement；dispatch 不复制其 transition law，不获取容量，不建立 execution binding，不解释 provider failure。

## [013] Construction 纯 wiring，recovery 晚于 durability activation

插件构造阶段只装配 dispatcher 与 handoff ports，不读 journal、不调和 pending claim、不恢复 execution、不启动 timer 或 polling。durable substrate 激活成功后，dispatch recovery 才可依据 durable claim 与 Host physical evidence 运行；execution recovery 委托 `managed-chat-execution`，且两者都不得以 wall clock 推进事实。

## [014] dispatch fatal先保留claim truth再经注入fuse执行

只有typed dispatch invariant incident可以请求fatal。已发生或outcome unknown的send必须先保留durable Pending/PhysicalAccepted truth与exact PromptKey settlement；fatal不得把它重写为未发送。dispatch runtime只接受composition注入的mandatory fatal capability，不得直接引用physical adapter、optional/default/global fallback；同一incident只允许一次report与kill�

## [015] chat.message 身份 carrier 是封闭且无歧义的 wire 代数

`PromptIngressCodec`只接受JSON record中的正式carrier。每个字符串carrier必须是原始字节保持不变的非空白字符串；`SessionId`支持`sessionID`、`sessionId`、字符串`session`及plain own-property record `session.id/sessionID/sessionId`，并在`input`、`output`、`output.message`、`output.info`四个正式source中统一收集。任一显式carrier类型非法、对象不是plain record、继承字段冒充own field、或两个合法carrier原始字节不同，整个SessionId投影必须fail-closed；缺失与非法不得合并为同一状态。多个同值carrier只产生一个opaque `SessionId`，禁止trim、字符串化、truthiness或值前缀猜测。wire 上正式传输的 PromptKey carrier 必须是无 agent 的 `PromptKey`；PromptKey 与单向只读历史解码保留的 agent 多 carrier 投影遵循同一 typed、同值唯一规则。含 agent 的 PromptKey 仅在读取持久化载荷时做单向解码，绝对不向新写回写或双写；新 dispatch 严禁携带或投影 peer/side/fallback agent。
