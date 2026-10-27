# delegation — WHAT

## [001] 委托的四项要素

委托必须明确 charge、允许产生的 office 后果、logical owner 和返回给调用方的 bounded 后果。以权能后果识别委托，不以 persona 名称或工具白名单代替。

## [002] Calling 别名不扩权

同一 Office 的 calling 别名不改变权限。fast/deep 仅作兼容参数，不独立路由或分档；每个 Role 对应单一 Persona。

## [003] 新道路与续做分开

`fork` 的 `calling` 可选，创建独立道路。省略时按 `name` 推导：`name` 为 `devops`（大小写不敏感）时推导为 `devops`，否则推导为 `engineer`；显式 `calling` 与推导不一致须 typed 拒绝。Manager 只能 fork `engineer`，`name` 为 `devops` 时不能用 `fork`。`resume` 必填 name，续做既有道路，复用该 participant 的完整历史和 binding；传入 calling 须 typed 拒绝。Manager 固定 DevOps 的 name 为 `devops`。同一目标的后续阶段、纠正和重试仍属同一道路，不另建道路。

## [004] 不同合同使用不同工具名

同一工具名在全系统只有一个确定语义。Orchestrator 的 `commission`、Manager 的 `fork` 和既有道路的 `resume` 不得混用。`commission` 的 `calling` 可选：Orchestrator 只委任 Manager，省略时推导为 Manager persona `lead`，显式 `calling` 与推导不一致须 typed 拒绝。`name` 未出现时新建道路；`name` 已出现时，省略 `calling` 续做既有道路，显式给出 `calling` 则拒绝重名新建。

## [005] 委托面不暴露机器拓扑

参数和返回结果不得包含 SessionId、AgentId、ManagerJobId、worktree、reused 等物理拓扑标识。跨 horizon 只传语义后果与子代理末轮 assistant 散文（渲染为 `# ...` 注释），不传子代理的完整 post-scope 视图。

## [006] 承接后果与绑定连续性

fork 成功只表示该 Byname 已承接 charge。续做按 Byname 找到既有 participant，沿用 execution binding 和模型档位，不得重选或篡改绑定。

## [007] SyncDelegate

同步委托依赖必须无环。Sphinx 程序内部受管调用标准 Engineer，完成当前工作项即返回调用点；沿用标准 OfficeCapability，不另禁写入或 Fission，不额外授予真实执行或 DevOps 差遣权限。

物理会话经 family root 压平，与当前 sub session 同级，不创建孙 session；结果接纳绑定本次调用的 exact terminal identity。

## [008] 批次由 Host 调用集合确定

同一 assistant 运行中指向同一 SyncDelegateRole 的调用构成一个批次，成员与顺序取自 Host tool-call 列表，不按到达时序或微任务猜测。合并 charges/prompts 后只发送一次。

## [009] 按直接调用方串行化

以直接调用方的 ReuseScope 为 key，同时至多一个 active batch；前批未完成时拒绝新请求。不同层级各占本层 scope，互不阻塞。

## [010] 模型绑定由系统决定

模型与执行配置由系统调度和有效配置决定，被委托模型不得自选 target。tier、fast/deep 仅作兼容参数，不授予模型选择权或额外权限；复用 child 沿用已绑定 managed agent。

## [011] 普通完成结束同步批次

不设独立 return 工具或双重 await。被委托方的普通 Assistant completion（自然停机，无未决工具调用）结束批次，由宿主交回该终态轮次的 assistant 散文（渲染为 `# ...` 注释），不捕获被委托方的完整 post-scope 视图。

## [012] Canonical 正文与 sibling 引用

批次内仅首个 canonical 调用方获得完整 view，其余 sibling 只获得指向该结果的简要引用。此前已经交回过的内容，即使再次出现，也不再返回。

## [013] Join 有界消费

Join 只消费当前 owner 可用的 completion，批次受全局上限约束，稳定排序并逐项 CAS 消费。每个完成项呈现为该子代理末轮的 assistant 散文（渲染为 `# ...` 注释），不以字段式 DTO 包装，也不承载子代理的完整 post-scope 视图。

## [014] Commission Join

Orchestrator 的 commission 批量 join 按 FIFO 排空，遵守与 [013] 相同的批次上限。

## [015] Join 中断不取消 child

外部用户输入、操作员取消或超时结束 Join 等待时，返回 `Interrupted`，不作为 ForkError。外部输入只打断等待，不取消 child 或剥夺权限。

用户输入不得中断当前 LLM 输出或非 Join 工具，也不得为了唤醒 Join 而物理 abort 整个 session；输入留给下一次 LLM 请求。

Join 只在新输入已由 Host 保存、且属于该 owner 的已接纳物理消息时唤醒。重复可见回执不得打断下一次 Join，Join 尚未开始时收到的输入不预埋中断。Manager 听取输入后自行决定是否向 child 追加指导、取消工作或调整安排；系统不自动取消、重派或转发。

## [017] 返回不转移权威

返回的 view 或建议只是调用方的决策证据，不自动改变全局请求方向，不扩权，也不免除既定义务。

## [019] Typed 首提示词

fork 首 prompt typed 渲染的正文**只有本次 Assignment 指令**，不再携带调用方的完整 post-scope LLM 可见视图、`[[context]]`、attachment view 或其他历史摘要；`resume`、commission、同步委托、dag 与忙碌追加指导同样只交本次指令。需要消歧义时，被委托方必须先通过 `negotiate(questions: string)` 真正向委托者提问，而不是预先复制历史。子到父完成项仍是子代理末轮 assistant 散文（渲染为 `# ...` 注释），不返回子方完整 post-scope 视图。

## [020] 语义不依赖物理工具名

工具改名或替换不改变委托的权能、所有权与生命周期合同。委派成员一律走编程面（`this.fork`/`this.resume`/`this.commission`/`this.devops`/`this.dag`），不再有独立的 Host 工具注册；工具名不变，但调用面、物理注册与工具名都不改变委托语义。

## [021] 父到子只传当前指令、历史依靠主动协商

父到子、同步委托、dag、DevOps 与 Advisor 请求只发送本次 charge / intent，**不传任何调用方历史、完整 post-scope 视图、`[[context]]`、筛选数组或附件 view**，也不附加父方未完成的活跃回合。子方只能从收到的当前指令开始，缺少必要事实时用 `negotiate(questions: string)` 向委托者提问；不得擅自推定未提供的上下文。charge 是任务本身（以 `# ...` 注释头呈现），协商返回的事实只是新的 steer，并不克隆 authority。

`askPreviousScope`、`negotiate` 和 Advisor 的异步答复只在实际发往 provider 的 transform 边界裁决：以真实物理消息标识咨询答复，查其之前最近一条已完成 assistant 回合；该回合没有工具调用时中断本次续跑，不依赖可能被排队消息拖住的 work 或 session 终态。有工具调用、没有可确认的前一回合，以及当前输入是操作员消息或新 assignment 时不得据此中断。此判断只决定本次咨询续跑，不把自然 stop 写成 Work 完成事实。拦截只作用于出站请求副本；已落地的答复保留在会话历史中，后续真正续上的任务仍可使用，不设会话级永久禁令。

## [022] 建议性工具次数

调用方可提供 `expected_tool_calls` 估算。真实工具调用逐次递减，至零饱和；估算只帮助规划，归零不得阻断执行、改变权限或触发异常流。

## [023] 恢复耗尽才报告失败

单次尝试失败不立即交付父调用方；子会话所有恢复路径耗尽或确定性终结后，才交付最终失败。

## [024] Logical route 上的 work unit

复用 participant 的每个新工作是独立 direct-CE invocation，不建立 durable Stage/Phase/ActiveWorkUnit 或第二状态机。resume route 由 Byname 标识，SyncDelegate route 由 caller scope + dedicated role 标识；物理 SessionId 不拥有 handoff 连续性。

输入为新 charge 与任务选项。charge 承载任务本身（以 `# ...` 注释头呈现）；父 → 子不再转发任何历史背景，SyncDelegate 与 dag 使用同一规则，每个节点仅收到自己的当前指令，后续通过 `negotiate` 消歧义。

委派成员全部走编程面：`this.fork(payload)`、`this.resume(payload)`、`this.commission(payload)`、`this.devops(intent)`；不再有独立的 Host 工具注册，语义不因工具名变化而改变（[020]）。委派调用不得包含局部背景数组参数。

`this.fission(payload)` 是同身份换道，无条件继承本对话衣钵。独立 `join()`（无参数）与程序内 `this.horizon()` 是拉取动作；编程内 join/dag 已退出，依赖顺序由模型显式安排。

同步调用等待自己完成，返回被委托方末轮的 assistant 散文（渲染为 `# ...` 注释）。异步 fork/resume 按 [026] 完成 admission/dispatch 即返回 Byname 承接后果，不等待 completion，不直接返回该材料。完成结果只经 join/horizon 拉取。子到父的返回只来自子代理自然停机那一轮的 assistant 散文，不再捕获其 post-scope 视图。子代理（Engineer/DevOps）凭有正文、无未决工具调用的自然终态（proven terminal，见 [025]）完成该 work unit；无正文无工具的停机只获 nudge。

当 abort、cancel 或重启加载结算已经使完成不再可能（无存活执行可以继续运行），系统**必须**追加 `ChildWorkVoided` 作废该工作，不得让父方继续等待一个不会到来的完成。作废**不是完成**：它不产生完成交付（既不写完成正文，也不把已有视图当作完成凭据），`status` 不是 `completed`。但作废**结清父方的未解除义务**：join 以 `voided` 状态把它报告一次（join 状态只有 `completed | failed | abandoned | voided`），报告之后该工作从名册退役（durable），重启不重复报告。作废在报告之前仍是可见的在场后果，父方据此得知该子工作已被结清，而不是任其消失。已被 abort 的子工作绝不写成 `completed`。S1/S2/S3/S4 接力不再主动清除上下文。

同一条规则跨两个委派通道一致：异步 fork/resume 通道与同步 SyncDelegate 通道里，子智能体自然停机即完成；两个通道同样适用作废分支：执行已不可能时 `ChildWorkVoided` 必须结清该义务，join 报告一次 `voided` 后退役。

## [025] Completion 以因果身份认领

terminal 只有属于该 work unit 实际接受的 Authority Root/provider execution，才可完成或失败它。completion cell 单次赋值：首个因果有效的 proven terminal 可 claim，恢复 continuation 不可 claim，晚到 terminal 幂等忽略。

run-scoped Completed/Failed/Aborted 均须保留因果身份；只有 session-wide 物理故障可无 Authority Root。sticky terminal、旧 run 或旧 cache 不得结算新 work unit，订阅后的到达时序不能代替身份。

## [026] Effect truth 单向前进

新 assignment 以 direct F# CE 表达。同步为 `dispatch → await own completion`；异步 fork/resume 同步确认接收方的身份校验与持久接收，随后异步工作，不在 invocation 内等待 child 完成。发送前 claim 复用 PromptAuthority 事实，不另持久化程序状态。

仅 exact `AcceptedAssignment` 可宣告已承接，并发布为 join 可等待的运行任务；Submitted transport receipt 不足以证明接收。确定未发送或已拒绝时，返回调用失败并释放预留，不遗留运行中任务。

acceptance unknown 或确认超时时，保留 durable Pending 与原 PromptKey，不伪造失败、不自动重发；返回明确的 `DispatchUncertain`，说明可能已接受、不要重复委托，且不得发布为 join 等待任务。已经发生的 effect 不得被后置失败否认为“未放置”。

Join 只等待当前进程真正在跑或已产生可 join 结果的工作：真实 Host agent 运行经 `PendingRuns`/`PendingCompletionCount`/`PtyRuns` 可见，可 join 的 durable handle 经 journal 投影可见。重启后按 durable handle 重新收养的**空闲伴随句柄**（如 Manager 的 `devops`）只注册其身份、不代表有在跑任务，不得让 Join 误判为有工作而无限等待；无 journal 的纯 PTY 模式才以进程内 active run 计数作为 agent 工作信号。已作废的工作不是"在跑"，join 无须为它等待：它以 `voided` 报告一次后即从名册退役；仍可能交付的未交付工作才是 join 可以等待的那种在跑。

## [027] Busy nudge 不承载新 assignment

同一 logical route 同时至多一个 active work unit。对忙碌 participant 调用 `resume` 时，charge 作为 `BusyAgentNudge` 追加到既有 LogicalRun，在 Host 保存后进入下一次尚未开始准备的 LLM 请求，不中断当前输出或工具。Engineer 与固定 DevOps 均允许此行为；保留原 work 的 Root、身份与完成订阅，不创建新 assignment，不因提示文本与旧 charge 相同而假装发送成功。

忙碌提示只包含本次追加指令，不再夹带调用方任何 post-scope 历史；它不改写既有 work，也不新建 assignment。已有真实回复后的可选 `negotiate` 不抑制正常 `resetScope`。

前一 work unit 完成后，`resume` 才承接下一项，不依赖 join 消费或重建 participant。忙碌提示的发送路径不得因异步准备期间任务结束而自动改走新 assignment。

## [028] 编译依赖按 effect 边界分层

委托合同只含 command/result、fact、typed payload、logical route、completion evidence 和 injected capability；fold 纯计算，durable composition 连接 canonical journal，各 workflow 消费合同与 fold。Host dispatch 和 PTY adapter 只由 composition root 绑定，合同的直接及传递依赖不得包含 Host、工具、PTY、process、EventStore runtime 或委托/恢复 workflow。

合同传递 production `.fs` 上限100；fold/runtime/sync/fork 目标185。Host/PTY adapter 因组装共享 durable/wait/signal/identity 能力，以全仓 production `.fs` 的60%为硬上限，超过须 full fallback；实测规模作 ratchet，增长须修订本条，当前 Host adapter 为312。owner compile 将所选 locality 的传递 ProjectReference 展成唯一计划，单次 Compile Include 保持拓扑偏序，不得退回目录通配。

## [029] 领域意图与物理能力单向注入

委托领域只产生 typed intent；linkage、estimate、child handle index 和 closed rejection 由委托纯 fold/decision 拥有。只有 durable composition 将意图包入外层持久事件并组合 projection。

各委托/恢复 workflow 只消费委托拥有的 append/query/wait/clock/PTY capability。PTY adapter 不得读取 Host/fork runtime、process 实现或其 Gate、Dictionary、registry、TCS 状态；Temporal、CausalWait、Process 物理实现只由 composition 构造最窄能力并注入。

## [030] Fatal 使用必填注入能力

sync invariant 违例由委托 owner 构造 typed incident；先保留已发生 effect 与 durable settlement，再调用必填的 fatal capability。runtime 不直接引用物理 fatal adapter，不设 optional/default/global fallback；同一 incident 至多一次 report 和 kill。

## [031] Checkpoint 保留已完成事实

child 已完成的事实独立保留：同步调用连续复用同一 child 并各自交回其末轮 assistant 散文，旧 authority root 的晚到完成不能认领新调用，父会话 supersede 或取消与完成在任意顺序下均精确结算一次，不遗留孤儿 claim，不重跑已完成 child。

## [032] Engineer 与 DevOps 不相互差遣

Engineer 完成工作或到达需 Manager 决定的边界即返回，不自行组织验证链，不直接、包装或转发任务给 DevOps。DevOps 不创建或差遣其他工程子代理。运行验证和进一步组织工作由 Manager 调度。

## [033] 自然停机终态与完成结算

子代理（Engineer、DevOps）以带有效正文且没有未决工具调用的自然停机作为完成依据。当子代理自然停机时，宿主把该终态轮次的 assistant 散文（正文散文、空 carry）原子结算为 `completed` 终态并发布给父会话的 `join`，在 join 边界渲染为 `# ...` 注释；不捕获子代理完整的 post-scope LLM 可见视图，也不返回 `[[session]]` 表。

- 允许后果：子代理正常结束输出时，终态为 `completed`，父方收到的完成材料就是该子代理的末轮 assistant 散文，以 `# ...` 注释呈现，不承载子代理的完整视图；
- 禁止后果：严禁要求子代理调用交付工具；不得向已停止输出的子代理发送完成催促（nudge）；不得因子代理没有特定的交付调用而扣留完成或报告未完成；
- 边界：Manager / Plan 的生命周期由 review（见 work [025]–[026]）处理，不经过父 join；本条只约束有 handle 链接的子会话。


## [034] 首轮协商代替全文上下文传输

父→子首 prompt 的当前任务与当前 RootRequirements 以顶层 `# ...` 指令行渲染，任务正文恰好呈现一次，**不再生成任何 `content`、`context` 或 `attached_view` 数据字段**；子→父自然停机报告仍由 assistant 散文与允许的返回 carry 独立呈现，两方向不得混淆。

受委托的新工作先接收**当前指令**，接收方必须首次调用单参数 `negotiate(questions: string)`，鼓励把相关的多个问题集中问出；工具调用立刻返回「已接受发送」的回执，不等待委托者回答。问题在委托者上下文上的临时、不开副作用的模型回合里解答，答复可能迟到数轮。在首次 `negotiate` 调用被受理之前，会话处于协商 pending 状态。**在 pending 期间，时序与预算完全冻结**：请求副本不呈现 `\0\uFEFF[Remaining turnBudget: N]` 预算后缀、物理回合不递减 `turnBudget`、初始化时序不前进、弱提醒保持关闭；操作员消息、**每一个新首次呈现的非 resetScope 工具结果**与 **assistant tool part** 均在请求副本中持续携带 `NegotiateReminder` 的 MUST 提醒（同一呈现身份只写一次并逐字重放，不再整体抑制工具结果提示）；普通工作工具调用不被阻断，但 `resetScope` 自身执行自拒且不得提交 `ScopeReanchored`。**negotiate 受理即完成**：首次有效调用被宿主受理（返回回执）时 pending 立即解除，时序继续，可立即调用 `resetScope`；不管、也不等答复 steer，答复迟到或投递失败都不重新上锁，也不再催促 negotiate。首次受理之后可随时再次 `negotiate`，但属于普通追问，不会重新进入 pending 状态，亦不会抑制或重启已激活的 `resetScope`。

**与跨 Scope 回问的对称性与交叉引用**：本协商 pending 状态与 `context-compression` [044] 中定义的跨 Scope 回问（`askPreviousScope`）pending 机制共用完全相同的底层 `PairHint` 呈现路径与时序冻结规则（请求副本不呈现预算后缀、不递减、弱提示关闭、每个新首次呈现的工具结果及 assistant part 持续携带 MUST 提醒且逐字重放、普通工具不被拦、`resetScope` 自身自拒）；两者解除条件相同：首问有效调用的 receipt 经宿主受理后即刻解除 pending 并恢复时序；协商即告完成，不等待答复。若取得答复，可能随后作为 steer 补到，只是内容，不是门禁，也不作送达承诺。

OpenCode 暂时建立一个作为接收方子会话的临时孙会话，Host 中仅有本次协商问题；在发往 LLM 的 provider transform 把这条消息扩展为委托者最后一次出站请求的只读 system、messages 与已观察的模型身份，工具定义不因临时任务而改写。临时会话任何工具调用都必须在 `tool.execute.before` 阶段无条件拒绝；一旦确实尝试工具或没有普通文本回复，应在临时回合重新提示、直到正常文本回答，而不能执行工具或伪造答复。实际回答**必须复用父→子追加指导的真实发送接口**：忙碌子会话走 `HostForkBusyNudge → PromptDispatcher.SendContinuation(BusyAgentNudge, Detached)`；已经关闭当前 Logical Run 的接收方则经同一 PromptDispatcher 建立新的受控 AgentOwnerRoot。文本成为宿主拥有的可见 user 消息，带持久 PromptKey，并排队供之后的 LLM 请求自然消费。严禁私自调用 SDK `session.promptAsync`、猜测 busy/noReply、轮询 `session.messages` 伪造回复到达，更严禁通过 provider transform 偷偷注入协商答案。

**omp (oh-my-pi) 宿主实现机制：**

澄清回合不派生孙会话或空会话投影，而是直接调用委托者 `ExtensionContext.runEphemeralTurn` 原生单轮边转（闭包持有 delegator 真实会话、模型与上下文历史），省略 `tools: false` 以保留原生工具目录（保持请求中的 native tool catalog，真实 provider 侧 prompt cache 命中未验证；SDK 原生剥离 toolCall 且绝不执行工具）。遇到空白回复或工具调用尝试则带入前序历史进行有界重新提示。
- 回答交付经由中立域缝 `INegotiationTransport.DeliverAndObserve` 派发：在接收方子会话写入 PromptKey 关联项（`pi.appendEntry("wxs.prompt-key", ...)`）。
- 交付实体观测：在发送前通过 `session.subscribe` 挂载事件监听器，观测接收方实体通道产生的 `message_end`（role="user" 且包含收据标识文本），观测到实体着陆后再行调用 `Surface.confirmNegotiationSteer` 结算该答复回执（门禁早在受理时已解除）；等待过程跨越 `turn_end`（因 steer 交付可能在后续轮次或空闲时落地，仅在会话遭遇 `agent_end` 或关闭且未观测到收据时判定失败）。严禁伪造 metadata 并在 entries 轮询。
- 根会话防护：在 `before_provider_request` 钩子中对根会话/非受管会话执行浅拷贝并自 `tools` 列表过滤掉 `negotiate` 工具；在 `tool_call` 拦截阶段对于未受管会话硬拦截拒绝。

`Detached` 回执仅证明本次调用已提交持久发送，**不等于真实回答已抵达子会话**；只有 Dispatcher 的 `PhysicalAccepted` 观察者在真实物理用户消息被接纳后，按接收方、回执、委派代际确认，才结算该答复回执。首次 `resetScope` 的门禁只看 negotiate 是否受理，与答复是否送达无关。伪造的 part.metadata 或出站 provider 文本不能结算回执。不得把「工具已发送」或临时会话已创建等同于答复已送达。

**user-facing session 不提供协商工具。** 在 OpenCode 的 `chat.message` 钩子里，凡 `IsManagedChild = false` 的根会话，其真实 `UserMessage.tools.negotiate` 必须显式为 `false`，因此工具不进入该次 LLM 请求可用列表；`tool.execute.before` 仍必须对根会话伪造的协商调用硬拒绝。受委托子会话和 Advisor 必须保留工具。本判定按真实物理会话父子身份，**不能按 Manager / Engineer 等角色统一禁用**，以免误杀同角色子代理的首次协商。
