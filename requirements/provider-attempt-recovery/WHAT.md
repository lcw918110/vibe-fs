# provider-attempt-recovery — WHAT

## [001] Failure budget belongs to a Logical Run

Each Logical Run owns its consecutive provider-failure count and fixed participant (SelectedAgent, Role and Persona identity). A new run starts with no failures inherited from a previous run.

## [002] Corrupt budget evidence fails closed

The budget counts consecutive failures only. Invalid persisted bytes return a decode error and reject the envelope; they must neither escape as an uncaught exception nor become an unknown submission.

## [003] One failure advances once

A single authority records confirmed failures and exhaustion. Deduplicate by `(SessionId, LogicalRunId, AuthorityRootUserMessageId, ProviderRun)`: the latest failure replays the same admission without another fact or count; an older failure returns `EpisodeSuperseded` and starts no recovery. Each retry has fresh PhysicalUserMessageId and ProviderRunIdentity; one failure permits at most one physical send.

## [004] Failure accounting and request-kind evidence

Each confirmed failure adds one and, while budget remains, yields an exact typed failure licence. Business-main success (`WorkMain`) records success and resets the count. Budget accounting neither changes roles nor decides provider health; [021] owns target settlement.

Recovery inherits RequestKind from durable ProviderStarted matching `(SessionId, PhysicalUserMessageId)`, including subsequent tool steps; exact ProviderRun authorization is checked separately. Ordinary children retain WorkMain regardless of association.

For Accepted without ProviderStarted, accepted origin may establish WorkMain or InteractionRepair. Text, failure ordinal, role and session association cannot supply a missing kind.

## [005] Bounded automatic retry

Automatic retry has a finite consecutive-failure limit. At the limit the run is exhausted and sends no further automatic physical request. Later recovery requires a new Authority Root or explicit user action.

## [006] Fixed participant, separate model routing

A retry preserves the Logical Run's participant and system prompt. Model routing selects an executor for that participant; it cannot change identity or bypass exhaustion.

## [007] Invalid budget records stop replay

Reject a failure record whose count is not the valid successor, exceeds the budget, or advances an exhausted run. Rejection stops replay.

## [008] 空终态与 Provider 故障统一恢复（合并至 ReconcilePass，nudge 机制遵循 InteractionRepair）

无有效正文且无工具调用的终态（包括自然 stop 但正文为空/XML-only、thought-only 无最终输出、以及 upstream_error 等 provider 错误）与普通 provider 故障不再人为割裂，统一在 ReconcilePass 中汇聚为需要修复催促的未完成态（TurnNeedsContinuation）。
在真实闲置（quiescence/idle）到达时，系统遵循 InteractionRepair 的 exact-terminal gate 机制向会话分发统一催促提示词（“刚刚你异常停止，请你继续工作。”），催促模型在同一会话中重新输出。
统一恢复受 10 次连续失败预算控制；未达 10 次时每次异常停止均可催促，达到 10 次耗尽时才真正判定为终端失败。真实人工取消（Abort / Cancelled）保持终结，严禁误触发催促。

## [009] Host retries are not domain failures

The Host's transport-attempt number neither enters the domain failure count nor determines budget or identity.

## [011] Immutable retry plan

Each AttemptPlan fixes retry choices from RequestKind and persisted material before rendering, bound to its own physical message. Transient cross-callback state must neither decide these choices nor carry recovery permission.

## [012] Abort residue is not failure

Host cleanup marking an in-flight tool call interrupted never advances the provider-failure budget.

## [013] Executor changes preserve identity

Retries preserve the durable ParticipantIdentity (Role, Persona, provenance/version), SessionProviderLanguage, system prompt, CanonicalRole and Authority. Persona and provenance come from that run's durable identity evidence. A participant binds one remote LLM target per execution; a new identity requires a new run after exact closure of the old one. Machine bookkeeping stays outside the provider horizon.

## [014] Continuation requires failure permission and Host stop

A recovery continuation requires a recorded confirmed failure, remaining budget and observation that the exact failed ProviderRunIdentity has stopped Host retry under [022]. Sending itself neither advances nor resets the budget. Coarse session.error cannot authorize a send; [023] covers an accepted retry that never starts.

## [016] Success accounting follows proven RequestKind

Only successful `WorkMain` records SuccessRecorded and clears consecutive failures. `InteractionRepair` does not (`BloggerMain`, `BloggerSquash` and `StrengthReplica` are retired). `finish=tool-calls` is a valid provider-attempt success even while the Host turn continues.

Continuation kind requires AcceptedContinuationIds. Role or terminal text cannot turn maintenance into business-main success.

## [019] Only typed policy permission authorizes retry

Recovery consumes execution-failure-policy's RetryFreshAttempt licence for ProviderTransient or ProviderPermanent, bound to the exact ProviderRunIdentity, RequestKind and policy decision. The failure ledger verifies the current attempt without reclassifying failure or recomputing budget/breaker policy.

LocalInvariant, ProtocolRejection, AuthorizationDenied, UserCancelled, Superseded, CapacityQueueFull, AcceptanceUnknown, StreamInterruptedAfterFirstToken and every PersistenceFailure neither spend provider budget nor create a retry. Wildcard retry, diagnostic-text classification and treating pending acceptance as failure are forbidden.

## [020] Budget evidence is not resume authority

Budget folds only committed Authority Root, typed provider-failure and eligible business-main success facts; identical facts yield the same view. It holds no callback, continuation, next action, send permission or workflow entry. Every retry still consumes its exact typed failure licence and derives execution content from retry policy.

## [021] 无 fallback 模型策略，严格遵循 OpenCode 原生配置

系统彻底废除 wanxiangshu.mjs 自定义调度与模型 fallback 机制。所有角色与 agent 执行模型完全与 OpenCode 官方机制一致：优先使用用户选择，其次使用 opencode.json 或 opencode.jsonc 中指定的模型。
发生失败或催促重试时，严格使用该确定模型原样重试，严禁拉黑 provider 或在后台自动切换至候选模型池。失败预算统一为 10 次。
## [022] 发送须等待确切宿主终态

Host 停止自动重试的唯一证据是确切 `(SessionId, ProviderRunIdentity)` 的 finalized errored assistant message 投影。session.error 只触发失败定局；session.idle、其它 run 或其它尝试均不能授权发送。缺少 durable ProviderStarted 不妨碍观察该宿主终态和判定失败。

该发送栅栏仅在当前进程有效，不写 Journal，也不从 crash recovery 重建；没有观察就不自动发送。[023] 处理悬挂义务。中止、替换或删除 session 永久撤销其 pending 恢复发送。等待仅由宿主终态事件推动，不用 timer、deadline 或 polling。

## [023] Accepted 未启动必须定夺

已 Accepted 且没有 ProviderStarted 的执行不得静默悬挂。session.idle 只检查该 session 中恰好处于此形状的执行；boot recovery sweep 检查重启后同样的义务。

session-only idle 是唤醒，不能证明同 session 所有已接受输入都已被 Host 停止。在线 sweep 必须由公开 Host snapshot 的最新已完成 assistant 的 exact `parentID` 确认停止的物理执行，只定夺它自己的未启动义务。已被 owner 明确取代的旧 assistant idle 不定夺新输入，不铸造新输入的 idle permit；尚未保存到 Host 或仍在容量排队的已接受 successor 由其准入 owner 和独立 recovery sweep 负责。

定夺必须终局：有 typed resume capability 时，用确切已接受 material 恢复执行；否则将该执行结为终态并报告该 turn 失败。本义务不发送新文本、不生成替换 PromptClaim，也不放宽 [003] 的单次物理发送约束。
