# host-boundary — WHAT

## [001] 业务层不消费流式碎片事件

业务逻辑严禁直接消费流式碎片事件（如 `message.updated`、`part.delta` 等）。合法交互路径仅允许由最早边界过滤碎片，转化为粗粒度的唤醒信号，随后读取完整的 SDK 快照作为唯一的业务事实来源。

## [002] 业务层信号闭集与精准分型

进入业务层的宿主信号严格限于类型化闭集（`SessionIdle`、`ProviderRetry`、`ProviderFailure`、`SessionDeleted` 与 `AttemptAborted`）。中止错误必须解码为专用的物理中断唤醒，绝不得与提供者失败混淆。

## [003] 传输与领域分离且信号仅作唤醒

宿主信号仅作为单向唤醒触发器，不得作为业务事实载体。信号内携带的尝试次数仅供诊断参考，不得直接作为领域的重试或回退计数。coarse `AttemptAborted(SessionId)` 只唤醒 reconciler；撤销 quiescence、取消 Strength owner 与推进 attempt stop fence 必须依据 SDK 快照或公开 exact cancelled assistant receipt，校验本次 `PhysicalUserMessageId` 仍拥有对应执行。coarse 信号没有 exact `PhysicalUserMessageId`，不得把该 session 的全部 current chat execution 投影为 `SessionAborted`，也不得撤销或终结已被更新 user message 接纳的新 execution。真正的当前 operator abort 仍由其 exact cancelled observation 撤销本次能力，旧执行迟到的 abort 不影响新执行。

## [004] TurnUnknown 为对齐私有观测而非业务结局

快照中未决的中间状态归类为调和器私有的 `TurnUnknown` 观测，严禁跨越调和边界发布为公开的业务完成终态，防止产生虚假的完成或缺失报告。

## [005] Reconciler 单飞快照观测与事件驱动收敛

调和器对每个会话严格保证单飞执行（single-flight），接收到粗粒度信号时执行单次完整快照读取。在无新信号或明确投影边缘时，严禁通过墙钟轮询重复读取快照。Provider failure 的 exact assistant `message.updated` 即使尚无 retry owner 的 terminal disposition，也必须用自身 exact physical identity 发出 typed failure wake；`TurnFailed` 只有在该 exact typed witness 匹配时才能发布，先到达的 idle/retry 只能等待，不得把裸错误 terminalize；同一 physical 随后的 idle/retry wake 或无 identity 的 coarse failure 不得覆盖它，新 physical 或显式 abort 才能替换它。无 current physical binding 的 coarse failure 不得读取快照或发布 terminal turn，只能等待 exact projection evidence。Exact failure 不得伪装成 managed-chat terminal，也不得因 coarse `session.error` 缺席或 disposition 缺失被丢弃。

## [006] Raw Part 与 ToolParts 状态投影一致性

工具调用的原始分段状态与业务分段状态必须保持严格一致：未完成状态映射为挂起调用，已完成或失败状态映射为调用结果，严禁出现状态分叉投影。

## [007] Compaction 观测门禁之预防与收容

宿主压缩控制实行双层防护：启动前严格校验并关闭自动压缩配置；启动探针只在第一回合窗口内拒绝非预期压缩——窗口从会话开始、截止于首个 completed assistant 消息（含该消息本身）；窗口之后的压缩（含用户手动 `/compact` 与后续回合压缩）属于运行时收容范围。运行时若观测到压缩事实，必须立即触发原子上下文重锚定。

## [008] Transform 到 ProviderRunIdentity 因果读与唯一性

从快照提取运行身份必须基于严格的因果规则（角色、完成时间、父节点匹配与最大序列）；当且仅当命中唯一候选时方可确认绑定，命中 0 个或多个候选时一律安全失败。`experimental.chat.messages.transform` 属于 provider inference 之前的物理边界，因此该 hook 不得把“当前 assistant run 已经存在”作为业务前置条件，也不得通过 bounded wait 把未来 run 伪装成 projection lag；需要在该 hook 冻结的 recovery 决策必须先以 exact `PhysicalUserMessageId` 保存为未绑定 attempt plan，同一 physical 的重复 transform 只能复用首次冻结值，不得重算后以冲突终止；待后续完整 Host 观测出现 exact `ProviderRunIdentity` 后再一次性绑定。各 hook（`chat.params` / transform / tool context / provider-start）通过公开物理证据（物理 message ID、assistant parentID、`ProviderRunIdentity`）关联到 exact execution，保持只读；不允许用 session-current 值回退替代 exact 证据。

## [009] Tool 身份双半边与缺失 Fail-Closed

工具执行上下文必须同时完整具备消息 ID 与调用 ID 两个半边身份；任一半边缺失直接安全失败，严禁跨上下文推测配对。

## [010] 多实例边界与共享注册表访问纪律

跨工作区实例间仅共享只读或受限的全局身份注册表，且注册表访问不得跨越异步等待点；各实例的持久化日志写入器与状态缓存完全隔离，严禁共享写入通道。

## [011] 空 Content 预防与连续 User 消息插桩

在向底层提供者交付消息前，必须对空白内容进行安全补占位符处理，并在连续出现的两条用户消息之间插桩无语义的助手消息，防止底层协议报校验错误。

**专席 Advisor 的独立出站投影例外：**专席的持久原文包含只有 reasoning、没有普通正文的 assistant 回合，或相邻的两条 user 载体时，不得给模型捏造 `assistant "."`（它会把此兼容占位错认成自己前次的真实答复）。仅在专席的 Provider 请求副本中省略没有正文也没有工具效果的 reasoning-only assistant，合并相邻 user 消息及其原始文字；从不修改原始会话、reasoning 的持久事实或已经产生的真实 assistant prose。普通角色仍受上述 Provider 非空与交替规则约束。判断专席身份依据持久的 CanonicalRole，而不是 user 消息文字。以 HOST-011 的 HNI 正反例和已建立真实 AcceptedChatExecution、ProviderRun 的 OpenCode 正式 transform 反例验证。

## [012] SessionID 与 CallID 定位唯一性

依据 `SessionID + CallID` 在完整快照中解析原始调用分段时，必须证明其能唯一确定对应的运行上下文与追踪范围；若匹配出现歧义或无法定位，必须安全失败。

## [013] Stream Sensor 专属识别与单 Run 触发限制

流式传感器仅识别对应分段中的专属标识与模式，普通正文与工具输出均不触发；每个运行周期内至多触发一次，且仅限中断当前子会话的物理尝试。

## [014] 零 Host 源码修改与 Typed Hook Membrane

系统完全基于公开的宿主 Hook 与 SDK 集成，不修改宿主源码。所有挂载 Hook 必须经过同一个 typed membrane：边界用公开 evidence 将失败穷尽归一为 `execution-failure-policy` 的 closed algebra，再解释其完整 decision。Provider/LLM 工具参数未通过已声明 wire/schema 属于 `ProtocolRejection`，原 typed rejection 返回 Host 供 provider 修正，不触发 fatal。membrane 禁止 wildcard catch 后直接 retry/fatal，禁止按 exception/error text 路由；未分类物理形状必须 fail closed 并扩展代数。

## [015] Tool 文本返回结果有界截断

自定义工具返回的文本结果在进入宿主传输层前必须执行确定性的尾部有界截断，确保超长输出不会导致传输层溢出或阻塞。

## [016] HostEventPort Run 去重与 Sticky 重放

事件端口对同一运行周期的完成事件执行幂等去重，对迟到订阅者提供粘性重放，并在监听器释放后彻底停止投递。run-scoped `Failed/Aborted` 与 `Completed` 一样必须保留其 Authority Root causal identity；future-only subscriber 不得重放既有 sticky terminal，供新 work unit 使用时只能观察订阅后的新事件。

## [017] Host 身份提取与 Managed Config 投影适配

宿主边界负责将原始事件解析为规范的会话与角色身份，并单向将托管配置投影到底层宿主，宿主适配逻辑不反向生成业务权威。

## [018] Host 源码零 Fork

系统只通过受支持的公开 Hook 与 SDK 无侵入集成。Host 源码修改、补丁、私有模块 import、运行时 monkey patch 与 vendored fork 均不属于合法实现路径，严禁作为能力缺口的补偿方案。

## [019] Host 物理能力缺口必有 Canary 与 Contract 证明

业务所依赖的全部宿主物理能力（包括时序、快照解析、模型路由等）必须同时具备契约测试与真实 canary：canary 必须启动受支持的真实 Host build，经公开 Hook/SDK 发起实际场景并观察公开结果；mock adapter、源码/类型形状检查、伪造 callback 与 UI 截图均不算 canary。缺少任一级证明即判定环境不支持。一次 provider transform 不选择未提交的 probe，也不以跨 callback mutable registry/flag 传递截断决定。

## [020] 观测不足或多解严格 Fail-Closed

宿主边界在面临任何观察证据不足、查询返回 typed failure、多重冲突或数据不一致的情形时，一律执行安全失败（fail closed），严禁妥协猜测；elapsed time 本身不构成业务结论。

## [021] Plugin Load Phase 纯洁性与 Activation 分界

插件加载初始化阶段仅允许执行资源解析、静态校验与 Hook 注册，严禁调用宿主业务接口、执行崩溃恢复或追加业务持久化事实。

## [022] Fatal 前必须完成 exact settlement

Typed hook membrane 收到 `FatalAfterSettlement` 后，必须先按同一个 policy decision 完成 exact opaque capacity fence settlement，并把 typed message disposition 交给 `managed-chat-execution` durable 提交；提交未知必须写成显式 unknown。只有所有已持有 ownership 均取得 committed/unknown settlement evidence 后才可调用 `FatalProcess`。严禁先退出再依赖 `finally`、Host cleanup、session deletion、UI 提示或 best-effort count decrement 收尾。

## [023] 业务承诺只建立在公开 Host contract

Requirement、领域状态与恢复策略只能依赖受支持的公开 Hook/SDK 输入输出及真实 canary 已证明的物理行为。private Host field/module、未公开 callback ordering、内部 retry counter、DOM/UI text、toast、spinner 或渲染时机均不得成为 identity、acceptance、provider start、terminal、capacity 或 fatal settlement 的证明；无法由公开 contract 观察的能力视为不存在并 fail closed。

## [024] Hook Policy 闭集与可选观测隔离

每个 live Host Hook 必须且只能对应一行 closed metadata，声明 `Security | Workflow | Invariant | Degradable | AuditOnly` criticality、允许的 context/effect、retry permission、capacity owner 与 failure disposition。composition root 按该 closed score 显式静态注册，固定次序不得由 list iteration、动态 middleware 或 service locator 隐藏。Hook identity 权限至多只读，admission 权限至多进入唯一 owned gate；不存在 identity mutation 或 admission bypass。`Security`、`Workflow`、`Invariant` 失败始终经 `policyAwareHook` fail closed，不能降级为 best effort。已证明可选的 Casebook/audit observation 必须置于 typed best-effort boundary；其失败只发送现有 diagnostic，不能改变已完成的 critical Hook result。

## [025] 单一因果诊断与显式脱敏

Host 只通过 `ReliabilityDiagnostics.CausalDiagnosticRecord` 发布结构化因果记录。可用事实携带 exact logical run、session、physical user message、provider run、participant、role、request kind、state transition、typed failure/retry/fallback、capacity、recovery 与 persistence commitment；不可用事实必须为 `None/null`，严禁猜测。schema 不接收 prompt、content、token、credential、cookie 或 path 字段；adapter 对允许的自由文本显式脱敏并压成单行。known typed failure 只输出一行 JSON 且无 stack。diagnostic emit/counter/query 失败不得改变 Hook result、admission、retry、recovery、capacity settlement 或 durable fact。

## [026] Host Contract/Runtime 编译分界与单向依赖

Host subsystem 的公开 Contract、Runtime 与物理 Adapter 必须保持编译隔离。以下名称标识合同和编译边界，不赋予 compile shard 独立的架构治理身份或 consumer 授权：
- `Host.Session.Contract`（`host-session-contract`）：仅包含会话静止 capability 与纯 `SessionSnapshot` 词汇、端口、定位 decision，不依赖 SDK/HTTP 投影、具体 Host 运行时、进程控制、诊断或 Sphinx MCP。
- `Host.Signal.Contract`（`host-signal-contract`）：仅发布共享终端词汇 `Foundation.TerminalSignal`（原 `OpenCode/Signals/EventContract`，已移出适配层），无状态且零副作用；不得因终端词汇而传递摘要实现、消息词汇、SDK 类型、工具注册或物理启动配置，不作为兼容 umbrella。
- `Host.Message.Contract`（`host-message-contract`）：独立发布 `Message`/`MessagePart` 消息词汇，不携带 SDK 类型、终端事件或摘要实现。
- `Host.SDK.Types`（`host-opencode-types`）：独立发布 `OpencodeTypes` SDK 数据类型与 `HostModel` 宿主中立模型词汇（原 `OpencodeModel`，HNI-10 于 2026-10-22 就地改名），不携带消息词汇、终端事件或物理 Host 能力。
- `Host.Event.Envelope`（`host-event-envelope`）：只发布 raw Host envelope unwrap、event type、session identity 与 message-session identity 的唯一无状态公式；不得修改输入对象。
- `Host.Message.Codec`（`host-message-codec`）：只发布 raw Host message part → `MessagePart` 的无状态 decode；其 bounded audience 只包含实际 message consumer。
- `Host.Fatal.Effect`（`host-fatal-effect`）：进程级 fatal fuse（`Foundation/FatalProcess`）的窄物理效果边界，不是纯合同；其 capability 注入、唯一物理实现与结算顺序遵守 HOST-BOUNDARY-029。
- `Host.Diagnostics.Runtime`（`host-diagnostics-runtime`）：封闭 Hook 策略元数据与因果诊断单向消费 Contract，不得反向侵入应用契约闭包。
- `Host.Signal.Adapter`（`host-signal-adapter`）：宿主信号词汇 `HostSignal`、完整 provider failure/terminal `HostEventCodec`、信号路由（`HostSignalAdapter`）、物理订阅与事件总线适配器（`TerminalWiring`/`Events`）；按实际知识消费窄 codec contract，不向 message/loop consumer 输出自身完整闭包，也不编入工具注册实现。
- `Host.Tool.Adapter`（`host-tool-adapter`）：独立拥有 `ToolHostCodec` 与 `ToolHostSurface` 的参数解码、上下文身份配对、SDK schema、工具注册、abort listener 与有界输出接线；它是物理适配器，不是纯合同。工具注册闭包不得取得信号路由或终端总线实现；同时需要两侧的 composition 显式装配，不恢复宽 adapter 或复制物理实现。
- `Host.Session.Runtime`（`host-session-runtime`）：SDK/HTTP 快照投影、进程级静止门禁状态机（`SessionQuiescenceGate`、`QuiescenceSurface`）、消息就地变更与宿主上下文投影，禁止被普通业务契约直接引用。
- `Sphinx.Host.Adapter`（`sphinx-host-adapter`）：`Hosts/OpenCode` 的 dispatch 借道端口（`OpenCodeHostPort`，Capabilities 仅 `dispatch`），隔离于核心契约之外；不注册原生工具，不启动或注入 Sphinx MCP。

`HostDigest` 属于 `runtime-platform/digest` 的无领域摘要原语，不属于 `Host.Signal.Contract`；摘要计算不应使 consumer 获得 Host 消息、SDK、终端或物理适配能力。物理启动配置留在对应适配器，不回填共享终端合同。

普通业务契约只按真实知识依赖消费窄 public contract、纯数据/decision 或 capability port，包括会话、终端、消息与 SDK 数据合同；允许消费窄合同不等于授予物理能力。严禁传递编译 Sphinx、诊断、消息就地修改、工具注册、信号订阅或具体 Host 运行时；需要物理效果时由 composition 注入窄 capability。

## [027] Host codec 按语义与consumer cohort切片

无状态`HostEventEnvelope`是raw Host envelope unwrap、event type与session/message-session identity读取的唯一公式；`HostEventCodec`必须消费该contract，禁止复制dynamic field parser。`HostMessageCodec`形成独立bounded contract，只服务message consumer。`Host.Session.Runtime`不得为message codec引用完整signal adapter；signal adapter不得反向引用diagnostics runtime。

## [028] signal subscription 与optional diagnostic 必须分型

Host signal subscription必须返回closed `HostSignalSubscriptionError`与`LocalEventHook | EventsListen of HostSignalSubscription` mode，不得以`option + string`表达非法组合。顶层input与显式非null `events` carrier必须是plain record；`client`是opaque OpenCode SDK object capability，可为plain record或class instance，缺少legacy `events`成员时必须返回`LocalEventHook`。primitive、array、boxed scalar、Date与坏direct events/client-events一律`InvalidInput`，且坏direct events不得借合法client旁路。顶层/client carrier getter或Proxy抛错收敛为`InvalidInput`；`events.listen`读取或调用抛错收敛为`EventsListenFailed`，Surface promise必须resolve typed error而非reject。`EventsListen`必须携带唯一opaque disposable owner；缺失或非函数`listen`、缺失或非函数disposer均返回对应typed failure，禁止延迟到dispose时才爆炸；合法disposer自身抛出的异常必须原样传播给资源owner。未提供legacy events capability时只返回`LocalEventHook`，不得制造disposer。callback等JavaScript decode failure只在Surface膜上fail closed，不污染production DU。adapter只报告typed failure且不得到达Diagnostic或Temporal；`HostSignalBootstrap` composition是`signal-subscribe-failed`的唯一fatal解释者。Loop diagnostic经必填窄`emitDiagnostic` capability注入；其失败只能产生非权威诊断outcome，不得改变arm、interrupt、consume或continuation结果。

## [029] fatal process 是唯一注入的physical adapter

`FatalProcessPort` contract只含immutable incident vocabulary与capability type，不得含value、factory或Node import。唯一fatal adapter拥有该路径的`console.error`、`process.kill`与`process.exit`；所有caller由composition获得mandatory capability，普通contract/runtime/adapter不得直接引用physical implementation。fatal前置settlement由caller owner提供typed evidence；同一incident只允许一次report与一次kill。

物理退出语义由真实子进程证明，不由 harness flag 证明：`requirements/host-boundary/tests/029.test.mjs` 经 `tests/fixtures/fatal-process-child.fixture.mjs` 触发编译后的 `FatalProcess.trip` / `Diagnostic.fatal` 真实路径，父进程观察硬退出（平台交付 signal 则断言 signal，`process.exit(1)` 回退则断言 code 1，不硬编码 Unix），并重开此前已提交的 store 验证 durable 事实幸存；console renderer 抛错、stdio 关闭、重复 incident 均不得绕过 fuse 或产生第二次 report；正常拒绝、stale callback、修复耗尽路径必须 exit 0。SIGKILL 语义不得为让 finally 运行而弱化。

## [030] raw Host membrane 只接受精确 JavaScript 类型

布尔marker只接受primitive `true | false`；字符串、数字、对象、boxed value均不得借truthiness成为compaction、synthetic或abort。parts只接受真实Array，其他值安全投影为空且hook不得抛异常。session event与`session.get`响应中的SessionId、parentID、agent只接受原始非空白primitive string，禁止`string value`制造领域值。所有dynamic reader必须在Fable边界执行显式JavaScript type predicate；正确性不得依赖`unbox`、异常捕获或下游字符串函数偶然拒绝。

## [031] Root workspace first-bind effect隔离

Root workspace 是process-local Host资源定位结果，不是公开可变状态。private Host runtime只在当前值为`None`且候选为非空白`Some path`时完成首次绑定；`None`、空串与纯空白均不占用绑定，首次绑定后的任意候选不得改写结果。Host composition是binder的唯一production consumer；其余production路径只能消费显式注入的只读capability。不得从Git推导workspace family root，不得按consumer自行重算或直接读取全局atom。

## [032] 不注入评审保证书参数

`todowrite-for-manager` 的工具定义不得追加 `contract` 参数、固定 token 或必填签署字段。评审接纳前后的定义保持一致；是否允许执行只由当前 Manager capability facts 决定，不依赖模型承诺。

工具入参中的 `contract` 仅作为对 provider 的提示字段；插件本地对缺失或错误的 `contract` 采取乐观处理，不进行二次强校验。执行清理时，对需要暂存的参数执行私有暂存，并在 `after` 回调中原样恢复；异常退出路径同样保证同源恢复，确保历史原始调用与上下文记录不被参数清理逻辑改写。此处「历史」指每次 provider 请求发给模型的 provider wire 层历史投影：参数清理后由 provider-facing transform 从 before 期私有暂存还原协议字段，使 wire 层历史保留原始入参；宿主持久化快照的内部形态属宿主实现，在公开 Hook API 内既不可满足也不可观测，不作为本仓门禁对象。参数恢复后的对象身份与原始键顺序必须以真实 canary 得到严格证明。参数清理机制只按字段所有权处理服务评审提示字段 `contract`。不为工具定义追加估计轮次或短记字段。原始参数保证主要落在 provider wire 历史投影，不得承诺 Host 持久化快照对象形态或宣称重启后宿主内部字段必然完整。

## [033] 读取端 hook 的 exact 只读租约校验

`chat.params`、provider step 门禁与 provider attempt 生命周期一律以 exact `(SessionId, PhysicalUserMessageId)` 读取容量所有者已提交的租约完成校验：查询不得调用 scheduler、不得发放 fence、不得建立或回填第二份绑定状态。同一物理消息的重复观察幂等，拒绝原因保持一致；A/B 交错消息各自校验自身物理 id，互不串读。受管输入缺少已提交租约时 fail-closed 拒绝且 Host 输出不被改写，不得回退到 session-current 绑定副本，也不得把观察升级为受管租约；未绑定 Host 辅助会话保持豁免。attempt plan 冻结与 `ProviderStarted` 持久化按 exact key 查验 durable Accepted 执行，缺失即明确拒绝，不得兜底重建准入；terminal 后的迟到事件不得复活准入。

活跃 run 的 `HumanMessage` / `BusyAgentNudge` 在 Host 保存前不得撤销旧 committed lease。provider transform 选择可见新输入时，先经 managed-chat-execution-003 的独立准入操作验证 exact Accepted、同 run 与旧 opaque lease，交接原容量并提交新 exact lease，然后执行只读门禁。不得把任意缺失租约视为追加材料或从 session-current 猜造身份。Join 的输入唤醒只消费已保存消息的 exact 可见回执，不在 `chat.message` 保存前唤醒。

## [034] adapter 薄度与 Host 命名空间层级的门禁契约

adapter 文件（`src/Wanxiangshu` 下的 `OpenCode/`、`Omp/` 适配面）不得自行承担产品决策。引用数量与端口出现与否不作为硬性计数棘轮：一个职责明确的合法 adapter 即使引用较多领域命名空间，只要不越界替领域对象作决策、不持有第二业务账本且不控制外部生命周期，即为合规；单纯的命名空间计数或基线不作为交付否决依据。

中立端口纯度是严格约束：`src/Wanxiangshu/Host/Ports/` 下的每个 `.fs` 与 `.fsi` 文件不得引用适配器命名空间、产品域或宿主词汇，只允许 Foundation、`Wanxiangshu.Host` 与 System。宣称纯计算的中立端口签名严禁携带具体 Host SDK 客户端句柄、物理文件句柄（如 `System.IO`、`FileStream`、`StreamReader`、`StreamWriter`）或可变注册表（如 `Dictionary`、`HashSet`、`ResizeArray`）。纯度范围从源码发现，不依赖任何硬编码族名：任意名称的新端口族自动纳入范围，其签名文件声明的 `type` 名即成为中立端口词汇表的一部分，词汇表同样从源码发现。端口族完整性由族签名自述：每个端口族签名必须以 `HNI P-xx` 头标记声明恰好一个保留端口 id，不得声明零个或多个；两个族不得声明同一保留 id；直接位于 `src/Wanxiangshu/Host/` 下的文件不得占用某个中立族的基名，否则该族一旦被移入 `Host/Ports/` 就会静默覆盖。

`src/Wanxiangshu/Host/` 承载两层且每层只有一个命名空间：A 层 `Host/Ports/` 必须声明 `Wanxiangshu.Host.Ports`，B 层 `Host/Domain/` 必须声明 `Wanxiangshu.Host.Domain`，直接位于 `Host/` 下的文件两者均不得声明——目录位置与命名空间因此不得各自漂移。层范围从源码发现，新增的族与接缝自动被覆盖。

## [035] omp 入口模块不持有产品决策

omp 入口模块（`src/Wanxiangshu/Composition/Omp/` 下携带 `[<ExportDefault>]` 的那个文件及其 `.fsi`）是手写、无产品决策的接线层：它只建立子会话接缝、调用 `activateOnOmp`，并以普通 `IHostPlugin` 值接收 Fable 编译出的产品。它不得反射产品、不得动态 import 产品内部、不得在运行时查产品符号。

入口模块不得出现任何产品决策：不写准入/拒绝规则、不写重试、不算预算、不渲染产品散文、不保留产品状态；并且不 import 任何产品模块。允许的命名面只有中立端口、omp 适配器与 second-host plugin 取值面（`System`、`Fable.Core`、`Wanxiangshu.Host.Ports`、`Wanxiangshu.Host.SecondHost`、`Wanxiangshu.Omp`），产品散文只存在于产品资源树。

该约束由门禁 `scripts/checks/omp-entry-decision-gate.mjs` 强制：门禁只读取入口模块与其接口，不扫描其他 lane 的文件；入口模块缺失、导出点缺失或歧义时必须 fail closed。门禁自带 planted-violation 自证——每一类禁止项都必须在植入违规后被拒绝，且真实入口模块必须被接受；自证失败本身就是门禁 issue。


## [036] 宿主功能性角色与自动 compaction 隔离契约

宿主内置的功能性代理（title、compaction、summary、explore、generate）服务于宿主自身的生命周期与元数据维护，不隶属于万象术受托治理的 managed roles（manager、orchestrator、engineer、devops、plan、advisor）：

1. **请求级系统所有权与视图隔离**：OpenCode 1.19.4 的 system hook 不携带当前 agent，且先于 chat.params；title 可复用 managed 会话及用户消息。只有以万象术自有角色提示词完整字节开头（后接换行或结束）的请求，才进入产品 system 改写与出站快照；临时协商沿专用前缀投影处理。其他请求原样保留，即使自定义功能性提示词、与 managed 请求同会话交错，或在尾部引用产品提示词，也不得注入 Scope 或污染快照。禁止用宿主英文提示词片段、历史用户 agent 或会话身份猜当前角色；
2. **历史会话压缩服从 as-is 语义**：`session.compacting` 只授权该会话紧接的一次 `messages.transform` 旁路，读取时即消费，不延续到 system/params 或后续普通请求。宿主传入当前存活上下文的什么内容，就原样提供给压缩模型；绝不恢复前任已被 tenure cut 裁切的历史，也不得注入预算后缀。其他会话不受影响；
3. **宿主自动 compaction 策略保留**：万象术不得物理禁用宿主自动压缩。配置钩子必须保留 `auto=true, prune=false, autocontinue=true`，允许宿主自发触发安全压缩，且该压缩不推进 Scope epoch、不扣减计费估计、不更改在途委托事实；
4. **准入与执行旁路**：功能性角色在 `chat.message` 中不受 managed lease 约束或强加 tools 屏蔽，允许其无阻碍直接执行。
5. **参数隔离**：chat.params 以实际调用的 agent 为准，非 managed 请求不得结算 Scope 进度、触发 Advisor、覆盖模型快照或记录 managed 模型容量；宿主参数原样保留。
6. **共享历史中的作者身份**：产品在请求副本追加的 Scope 提醒必须将每个文本 part 标记为宿主原生 `synthetic=true`，不伪装成真实用户消息。OpenCode 的后台 title 与普通请求变换可共享同一数组；变换后 title 的真实用户计数必须仍只包含原始用户消息，不能因产品提醒从一条变成两条而静默取消自动命名。

2026-10-27 实测：上一轮仅 system/params 旁路不能修复 title。未标记的 Scope steer 使真实 DevOps 会话不发出 title 请求；补齐 synthetic 后，OpenCode 1.19.4 在开启插件的真实 DevOps 新会话中发出 `small=true agent=title`，并将持久标题更新为「自动标题生成诊断」。普通 DevOps 模型请求在诊断上限被停止，此证据仅证明后台自动标题完成，不冒充普通任务完成。

源码依据：[OpenCode v1.19.4 request.ts](https://github.com/anomalyco/opencode/blob/v1.19.4/packages/opencode/src/session/llm/request.ts)、[compaction.ts](https://github.com/anomalyco/opencode/blob/v1.19.4/packages/opencode/src/session/compaction.ts)、[prompt.ts](https://github.com/anomalyco/opencode/blob/v1.19.4/packages/opencode/src/session/prompt.ts)。

## [037] 空闲续轮只在宿主 runner 真正空闲后投递

由空闲许可派生的续轮（ManagerGuard、JoinGuard 的空闲门提醒、Interaction Repair）必须等宿主 runner 离开当前 run 后，才做物理发送准入与 `SendPrompt`。理由是宿主语义：OpenCode 1.19.4 的 `SessionRunState.ensureRunning` 在 runner 仍处 Running 时，把新 prompt 并入那个 run 的结果；而 run 在 `exiting loop` 之后还要跑完尾段（prune、读取最后的 assistant）才转 Idle。落在这段尾窗里的 prompt 会落库成 user 消息，却不会开新 loop。[029] 允许用确切完成的 assistant `message.updated` 铸造空闲许可，而它早于宿主 idle，所以续轮必须另行等待 runner 空闲。

- 等待依据只取宿主自报的会话状态：`session.status` 中该会话 busy/retry 即未空闲，缺席或 idle 即空闲。轮询退避、总时长有界；状态读不到或宿主没有状态 API 时，按原来的立即发送处理，不新增拒收。
- 等待发生在去重检查与许可消费之前；准入检查与 Host 调用仍是同步相邻的两步。等待期间如有新物理输入，许可照旧被撤销，续轮按 Superseded 放弃。
- 非空闲派生的发送（AgentOwnerRoot、ManagedDelegationAssignment、BusyAgentNudge steer、接力后继提示）不等待，语义不变。
- 长程角色（Manager/Orchestrator/Plan）无工具自然结束得到的 nudge 必须真的开出下一轮；不得出现「nudge 已落库、宿主不调度」。

真实事故（2026-10-29 05:23:50 JST，Manager `ses_e7ed7cdb2ffe…`）：`msg_1842ec5e0` 以正文 stop 结束；ManagerGuard nudge `msg_1842eecaa` 在 `exiting loop` 之后 87 ms 落库，此后再没有 `loop step=0`。之后的 session.idle 也没有再开出续轮，Manager 静止约 1.5 小时。此前几十次同样的 nudge 都在 runner 回到 Idle 之后才到达，所以碰巧生效。

物理证明：tests/037.test.mjs 覆盖三处：SDK 端口按 `session.status` 读 busy；InjectedSessionPort 在 runner busy 期间持续等待、转 idle 后放行；真实 ManagerWorkflow 空闲续轮在 runner 空闲前不调用 SendPrompt，空闲后同一条 nudge 才发出。
