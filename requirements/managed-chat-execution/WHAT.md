# managed-chat-execution — WHAT

## [001] 唯一 owner 与 exact execution key

`managed-chat-execution` 是 durable managed chat execution 的唯一 owner。每个执行仅由 exact `(SessionId, PhysicalUserMessageId)` 标识；`SessionId` 可依次承载多个执行，不拥有 session-scoped current execution 终态、租约或替代身份。执行身份的权威来自 durable `Accepted` evidence 与容量所有者（ModelRouting）持有的 exact lease；不要求 Host 侧第二次注册身份或另立一张执行绑定表。

## [002] Versioned durable fact vocabulary

`Accepted` 携带 pre-provider `AcceptedChatExecutionEvidence`：exact key、由 IdentitySeed 派生且在 logical run 内不可变的固定 participant+role（含 persona、personaCatalogVersion 与 provenance evidence）、PromptOrigin 与 authority evidence，禁止包含尚未存在的 ProviderRun。系统中不存在 PeerAgent，亦无可变的 EffectiveAgent 身份字段；provider retry 只由单一连续失败预算拥有，managed-chat 恢复不重试。`ProviderStarted` 携带 `ProviderStartedExecutionEvidence`：完整 accepted evidence 加 Host 实际观察到的 ProviderRunIdentity、ProviderRequestKind 与 projection choice。terminal evidence 是 closed DU：`PreProvider` 携带 accepted evidence，或 `AfterProviderStart` 携带 started evidence；不得用 option/bool 拼接阶段。旧版本必须经纯、确定、逐级升级后再折叠；进程状态、日志文字与 Host mutable projection 均不得冒充 durable fact。

## [003] 固定 transaction order

普通 managed chat 准入必须遵循 `resolve pre-provider identity → durable Accepted → acquire exact capacity → project into Host → commit → provider effect`。成功路径没有独立的 bind/unbind 步骤：容量所有者持有的 exact lease 取得即为该执行的绑定。Host 实际暴露 ProviderRun 后才可建立 started evidence 并持久 `ProviderStarted`。`Accepted` 落盘确认前禁止获取容量、修改 Host message 或调用 provider；任一步失败不得越过其后继边界，任何边界不得预测或伪造 ProviderRunIdentity。

同一 LogicalRun、authority root 与 IdentitySeed 的 `HumanMessage` / `BusyAgentNudge` 是追加材料，不是新 assignment。存在旧 committed lease 时，先 durable Accepted，再向 Host 投影旧 target；此时不替代旧 lease。Host 实际选择已保存的新材料进入 provider 请求时，准入 owner 从其 exact Accepted evidence 幂等取得 witness，转交原 capacity credit、建立新 exact lease 并结算旧执行，然后才能越过 provider 门禁。已经准备中的旧请求仍使用旧 lease；同一请求包含的较早未启动追加材料精确结算，不另行恢复发送。

追加材料接纳后，由容量 owner 保留旧 opaque lease 对应的 credit，涵盖旧输出已自然终结而新材料尚未进入 provider 的窗口；具体资源交接与清理遵循 execution-model-routing-006。它是已接受材料的本地资源所有权，不是第二份身份绑定或恢复缓存。

## [004] Accepted 单次建立且 replay 幂等

同一 key 的等值 pre-provider `Accepted` evidence 重放是幂等 no-op；与既有 identity 或 acceptance 内容冲突必须 fail closed。supplied state 的 key 不匹配时必须先拒绝，不能借其 terminal 绕过 exact key；exact terminal 重放则不得因新提交的 malformed attempt payload 改写既有结论。新物理消息即使复用同一 `SessionId` 也必须建立新 key，不得继承前一 execution 的事实。ProviderStarted 只能在 accepted evidence 完全相等后增加 Host-observed run evidence。

## [005] ProviderStarted 是 provider effect 的 durable 前置

首次 provider body 只能在 exact execution 已 durable `Accepted`、已取得并绑定 exact capacity，Host 已建立 exact ProviderRunIdentity，且对应 `ProviderStarted` 落盘确认后发生。每个 exact provider-start observation 必须在任何 failure/idle wake 前把 reconciler physical cursor 推到自身 `PhysicalUserMessageId`，但不得因此铸造 authority。该事实把整个 physical user-message execution 推入 provider phase；同一 physical execution 内 tool result 触发的后续 Host assistant ProviderRun 复用这一已持久化 phase，不得要求第二份 frozen admission plan，也不得追加第二个 `ProviderStarted`。等值 public `message.updated` 重放幂等；terminal 后到达的首次启动事实必须拒绝。

## [006] Terminal 单赋值且只接受 typed disposition

每个 exact execution 最多持有一个 terminal disposition。`PreProvider` 只允许 `Cancelled | Rejected | Failed`，禁止 `Completed`；`AfterProviderStart` 绑定 exact started evidence并允许四种 disposition。相同 evidence+terminal 重放幂等，任何不同 evidence 或 terminal 竞争均 fail closed 且不得覆盖首个事实。terminal 决策只接受 `execution-failure-policy` 发布的 closed typed disposition 或明确的 Host success/cancel/delete evidence；free-form text 仅供诊断，不承载 retry、fallback、breaker、capacity、message 或 fatal 语义。

## [007] Pre-provider failure 精确 settlement

`Accepted` 后、`ProviderStarted` 前发生的拒绝、取消、删除、binding 或 Host projection 失败，必须针对 exact key 写入 typed terminal disposition；若已取得容量则在该 terminal 持久化确认后精确归还。该路径不得调用 provider，不得释放或终结同一 session 的其他 execution。

追加材料失败或取消时，只撤销它自己的 continuation credit 保留；旧执行仍在运行则不动其资源。旧执行已终结且再无追加材料拥有该 credit 时，才完成此前延后的归还。

## [008] Recovery 只在 durability activation 后事件驱动

插件构造必须是纯 wiring：不得读取 durable execution、启动恢复、获取容量或注册会推进状态的后台工作。durable substrate 激活成功后，recovery 才可折叠非终态 execution，并由 projection activation、capacity change、Host evidence 或 typed failure 事件驱动重入普通准入/settlement 流程；禁止 timer、sleep、deadline、轮询或重启次数参与正确性。

## [009] Process-local artifact 永不持久化

capacity lease handle、waiter、callback、queue node、cancellation token 与 subscription 均属 process-local artifact，不得写入 execution facts、快照或恢复 token。恢复只能从 durable semantic facts 重建新的本地 artifact；旧 artifact 的缺失不能被解释为 terminal disposition。

OS process crash/restart 不等于 graceful teardown/reconstruction：同一 durable workspace 重启后，已落盘的 `Accepted` 必须保留；旧进程持有的 exact lease、capacity ownership、token、custody、execution 与 waiter 必须全部消失，除非新进程从 durable facts 经正常业务路径重新建立。process-local artifact 不得跨进程身份继承。

## [010] Cancel/Delete 精确终结并排空

logical cancel 与 session delete 必须枚举 durable projection 中该作用域内尚未 terminal 的 exact execution key，逐个请求 typed settlement，并等待每个已准入 execution 完成 durable terminal 与 exact capacity 归还后再宣告生命周期排空。禁止 session-wide blind release、timer grace period 或 polling 判断完成。

## [011] Acceptance 原子消费 pre-provider authority evidence

`Accepted` 必须原子消费 Task14 frozen managed intent 与 `interaction-authority` 发布的 current authority evidence，建立 exact `AcceptedChatExecutionEvidence`，包括完整版本化 `ParticipantIdentityEvidence`，但不包含 ProviderRunIdentity。Provider-start owner 随后只能把 Host-observed ProviderRunIdentity 与 accepted evidence 组合成 `ProviderStartedExecutionEvidence`。
两阶段均只逐字段投影 owner-issued evidence；不得从显式 agent 文本、Session cache、Host parent、model 或旧 execution 推导、补全、改写或独立缓存 participant、Role、initial Tier、Persona、provenance/version 或物理 run。显式外部 agent 仍作为输入保留并与 participant 做一致性校验；continuation 保持 participant 不变，fresh physical target 路由永不改变 participant。此处所述 binding 指容量所有者持有的 exact lease 身份；执行身份不要求 Host 侧第二张绑定表登记。

## [012] Recovery decision 只由 durable execution 与显式 physical evidence 决定

`managed-chat-execution` 独占纯 `Evidence → Decision` 公式。Evidence 只包含 canonical `ChatExecutionState`、exact public provider receipt observation（missing、ambiguous、absent、alive、terminal）、exact physical resource observation、typed persistence commitment，以及 `execution-failure-policy` 已发布的 terminal decision 与失败决议。Decision 是封闭代数：`Ignore | ReconcilePhysical | ResumePreProvider | Finalize | MarkManualIntervention`；不存在 `RequeueEligible` 门面——managed-chat 崩溃与资源恢复不拥有 provider retry/fallback 解释权，严禁启动 provider 工作或发布惰性的 requeue 请求。所有 provider-started retry 与 fallback 唯一由 `Wanxiangshu.Participant.Provider.Attempt.Fallback.ProviderRecoveryWorkflow` 解释。每个 effectful case 携带所需的 exact execution evidence，但本公式不执行 effect。

`Accepted` 且 exact provider absent 才可恢复 pre-provider admission；跨进程崩溃恢复与普通会话续传必须服从 crash-reconciliation-017 与 crash-reconciliation-018：新进程不得自动重放中断的工具或无边界自发推进执行，重启后的归位由系统在加载阶段自行完成，不存在显式续传命令。若宿主提供 `ExactAcceptedMessageRecoveryPort`，仅且仅在此 port 明确接受（返回 true）时才代表 exact 准入被接管，且 failure policy 绝不重算；若未提供 port（production `None` 构造）或 port 拒绝接管，恢复决策绝不产生后台隐式消费的假象，而是生成明确、可观察的 manual/blocked 处置事实并记录 briefing。observed provider 必须先 reconcile durable started/terminal facts。`ProviderStarted` 且 provider alive 只观察，exact terminal 才 finalize，exact absent 只由 failure policy 发布 terminal 并进行终态结算，不通过 managed-chat 重试；typed supersession 因而只能使用 policy 发布的 exact cancelled terminal。durable terminal 若 physical resource 仍 held 则请求 exact reconciliation，否则幂等忽略。missing/ambiguous receipt、unknown persistence/resource、无 policy authorization 均 fail closed 为 manual intervention；stale external evidence 不得改写当前 execution。禁止 Role、error text、terminal prose、idle、timer、process age、cursor、registry presence 或 process-local capacity state参与判断。同一 Evidence 必须永远产生同一 Decision。

排列、重复事件与 crash cut proof 必须调用已注册 production Surface。测试内重建 decision、terminal、release 或 dispatch 公式并 mutation 该副本，不构成本命题的 executable proof。

Recovery Surface 的 port observation 必须逐次追加每个真实 invocation，不得在 observer 内去重。process restart 两侧必须创建独立 observer；跨重启幂等只能由 durable owner 实现，测试观察器不得代替 owner 吞掉重复请求。

## [013] Execution reliability query 与 diagnostic 表生命周期

`Accepted without Terminal`、`ProviderStarted without Terminal` 与每个 `LogicalRunId` 的 physical attempt 数只能从 canonical `ChatExecutionProjection` 只读导出。查询返回不可变 process-local snapshot，不写 durable fact，不 terminalize execution，不授权 retry/fallback，不读取 diagnostic counter 作为恢复 evidence。Recovery pending/manual intervention 只投影 `PluginRecoveryScope.PendingChatRecoveryOwnership()` 的 typed ownership，不复制或清理其状态；且 manual intervention DTO 是纯报告与处置事实，绝不是进程间恢复命令。当 exact execution 达到真实终态时，其对应的 pending 与 manual 诊断登记必须立即被撤销，禁止已失效的请求永久驻留诊断快照中。

## [014] Incident evidence capture 与 replay 无 correctness authority

Incident envelope 必须 versioned、确定序列化且只包含 canonical serialized ChatExecution facts/status、immutable capacity snapshot 与 reconciliation decision、causal diagnostics、exact public Host version/contract evidence、typed recovery observation/decision。capture 必须复用 owner projection 并拒绝未知字段；diagnostic owner 负责清除 credential、path、stack、prompt/content/payload。replay 必须重新折叠 canonical projection、重跑 capacity reconciliation 与同一 `ChatExecutionRecoveryRuntime` representation，tamper、未知 schema/字段、缺证据、Host contract 不受支持或 observation 不匹配时 fail closed。

Replay 只返回 typed owner effect request；不得写 fact、清 counter、释放 fence、改变 queue/capacity、执行 retry/fallback 或把 operator 变成 recovery authority。相同 envelope 重放必须幂等。若 exact accepted-message public replay capability 没有 Host canary evidence，operator 必须升级处理，禁止重发或手工补状态。

## [015] Provider 启动边界拒绝的精确定夺

transform 在 provider 启动边界拒绝某个 exact execution 时（例如 attempt plan freeze 失败：`accepted-execution-missing`、`frozen-attempt-plan-missing`、`attempt-plan-freeze-failed`、`blogger-request-missing`、`authority-evidence-invalid`、`persistence-failed`），该拒绝必须作为一次 typed pre-provider failure 报告给 settlement owner；报告携带 exact `(SessionId, PhysicalUserMessageId)` 与诊断码，free-form 文本只作诊断，不决定 disposition。

- projection 中该 exact key 处于 `Accepted ∧ ¬ProviderStarted ∧ ¬Terminal` 时，settlement owner 写入 typed pre-provider terminal `Failed`，并在 durable 提交确认后精确归还该执行持有的 exact capacity fence；定夺为终局，同一 key 的重复报告幂等（已 terminal 为 no-op，terminal 已存在而容量仍 held 时只请求精确 release）。
- projection 中不存在该 exact key（`AcceptedExecutionMissing`）时，不得伪造执行或结算，也不得登记非本 owner 的 manual；保留原始拒绝与诊断，交准入 owner 处置。
- 禁止以 session-wide release、计数减一、idle/time 猜测代替 exact settlement；禁止调用 provider；禁止终结同一 session 的其他 execution；原始拒绝异常仍按既有 hook 失败路径上报，不得被定夺吞没。
- terminal 提交未知时保留显式未知，不释放；诊断不改变定夺。

