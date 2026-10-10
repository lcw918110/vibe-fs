# planning — WHAT

## [001] 目标状态形式化与三样输入

当 Plan 模式会话被初始化或任期推进时，系统为该任期装配输入。

- 允许后果：跑者接收且仅接收三样输入：本规划任务范围内的全部真实用户输入原文（`U`）、上一任期的工作记录（`LWR_prev`，由 `LifecycleWorkRecord.materialize` 且 `includeOpening = false` 渲染；若为首任则此项为空）、以及通过编程面读写的 Markdown 伪文件（`P`）。
- 禁止后果：模型不得直接读写或篡改 `U` 与 `LWR_prev`。`P` 不得作为文本直接注入模型输入消息集。模型不得取得任何超出 `P` 之外的仓库或工作区写权限。
- 失败后果：若输入集合包含未经任期剥除的上下文或遗漏必需事实，系统必须在消息投影阶段拒绝生成请求并终止本次调用。

## [002] 角色与严格权限投影

当调度器识别到 CanonicalRole 为 `Plan`（Wire 标识为 `plan`）时，系统投影其专属权限与工具集。

- 允许后果：Plan 角色且仅限 Plan 角色被赋予五个专属工具的访问权限：JS 编程面 `js-plan`，以及编排面工具 `ask`、`resume`、`handoff`、`deliver`。
- 禁止后果：Plan 角色禁止获得代码仓库与执行工具的权限，包括但不限于 `read`、`glob`、`grep`、`edit`、`write`、`mv`、`rm`、`run`、`open-terminal`、`send-terminal`、`read-terminal`、`signal-terminal`、`fork`、`fission`、`review`、`suicide`、`commission`。严禁向 Plan 开放任何真实 shell 或命令执行通道。
- 失败后果：任何越权工具调用必须在 `ToolRegistry` 执行门禁被直接拦截（fail-closed），返回强类型拒绝错误，零副作用。

## [003] 五条轮换事实与持久化重放

Plan 的轮换生命周期完全由五条不可变 Durable 事件驱动并投影状态。

- 允许后果：状态机仅通过以下五条事件进行投影更新：
  1. `PlanWorkOpened(workId, root)`：标记规划任务开启与根路径绑定。
  2. `PlanDevOpsBound(workId, devopsId, target)`：绑定唯一的固定 DevOps 实例。
  3. `PlanIncumbencyOpened(workId, incumbencyId, stage)`：开启新任期并标记当前阶段。
  4. `PlanIncumbencyRetired(incumbencyId, outcome)`：标记任期退休，outcome 仅限 `Continue` 或 `Delivered`。
  5. `PlanDelivered(incumbencyId, workId, digest, path)`：标记终态交付，记录摘要与路径。
  同一事件的重复重放必须保持幂等。
- 禁止后果：同一 workId 同时至多一个活跃任期（`Active`）。已退休任期列表（`Retired`）只增不减，旧任期严禁复活。终态交付（`Delivered`）至多记录一次。禁止在五条事实之外引入旁路状态机、内存标记或外部计数器。
- 失败后果：遇到同一身份但载荷不同的冲突事件，或检测到不合法的状态跃迁（如重复激活、幽灵任期），系统必须抛出 `StorageInvalid` 并关闭，严禁跳过或猜测。

## [004] 三阶段单向状态机与任期约束

规划流程严格按三阶段（Stage）单向推进：`S1`（事实与取舍）、`S2`（理解与路径）、`S3`（流程与伪代码）。

- 允许后果：首任任期始于 `S1`。`S1` 阶段完成后只能交接到 `S2`。`S2` 阶段可选择交接到 `S3` 或直接发起交付。`S3` 阶段只能发起交付。
- 禁止后果：规划链条最短 2 任（S1 → S2），最长 3 任（S1 → S2 → S3）。首任（S1）严禁直接发起交付。S3 严禁发起交接。阶段严禁逆行或跳级（如 S1 直接跳到 S3）。
- 失败后果：在不匹配的阶段调用交接或交付，动作门禁必须返回强类型拒绝错误，不推进阶段，不变更任期。

## [005] 动作许可矩阵

工具调用进入执行前，动作门必须基于当前任期与事实进行单一判定。

- 允许后果：
  1. 仅当调用者为 `Plan` 且拥有活跃任期时放行；
  2. 若已存在 `Delivered` 事实，仅允许 `js-plan.read`，其余动作全部拒绝；
  3. 若存在未结算的在途外部操作（如 pending 的 `ask` 或 `resume`），只允许结算对应操作，拒绝新外部动作；
  4. 阶段动作匹配：S1 仅放行 `handoff`；S2 放行 `handoff` 与 `deliver`；S3 仅放行 `deliver`；
  5. `deliver` 调用前必须确认无未决外部操作且输入版本最新。
- 禁止后果：禁止在未决异步操作悬空时开启交接或交付。交付完成后禁止再次发起 `ask`、`resume`、`handoff` 或 `deliver`。
- 失败后果：凡不满足许可矩阵条件的调用，一律返回强类型 typed 错误，并保持原状态不变。

## [006] 任期隔离与前缀重锚

当为 Plan 角色组装 Provider 请求消息集时，应用通用的 Baton 隔离策略。

- 允许后果：组装出的消息集严格由以下部分按序构成：
  1. 当前任务因果范围内的全部真实用户输入原文（`U`），按原顺序逐字保留；
  2. 若当前任期非首任，追加上一任期的生命周期记录（`LWR_prev`）；
  3. 本任期开始后的真实 assistant 与 tool 交互消息。
  任期内的第一次 Provider 请求被视为新任期起点，触发前缀重锚（TenureReanchored），开启全新的前缀 epoch。
- 禁止后果：上一任期及更早任期的全部 assistant 消息与 tool 消息必须全部剥除，不得进入消息集。`P` 的全文不得直接拼装入消息集。非 Plan 角色严禁触发该隔离逻辑。
- 失败后果：若任期消息切分失败或边界重叠，系统必须停止本次请求组装，不得向下游发送污染的上下文。

## [007] handoff 契约

当当前跑者调用 `handoff` 时，系统触发任期交接与阶段前进。

- 允许后果：系统首先核查当前任期的递归活跃资源（包括 child、后台作业、未决工具调用与租约）。确认无 blocker 后，以单个原子事务追加两条事件：
  1. `PlanIncumbencyRetired(incumbencyId, Continue)`；
  2. `PlanIncumbencyOpened(workId, nextIncumbencyId, nextStage)`。
  当前活跃任期更新为新任期，阶段递增，意图标记为 `Handoff`。
- 禁止后果：若存在活跃阻塞资源（blocker），禁止退休任期；系统必须记录 `CleanupBlocked` 并向模型返回 blocker 摘要。S3 阶段禁止调用 handoff。禁止在未成功持久化新任期前注销当前任期。
- 失败后果：事务写入失败时，当前任期保持活跃，不产生状态漂移。

## [008] deliver 契约

当末任跑者调用 `deliver` 时，系统建立唯一成功终态。

- 允许后果：系统首先核查当前任期无活跃 blocker，随后读取 `P` 文件内容并确认其非空。计算内容哈希（`sha256`）与字节长度，并以单个原子事务追加两条事件：
  1. `PlanIncumbencyRetired(incumbencyId, Delivered)`；
  2. `PlanDelivered(incumbencyId, workId, digest, path)`。
  成功后向用户呈现最终产物路径与收据，规划宣告完成，不触发后续模型轮次，不启动代码实施。
- 禁止后果：`P` 为空或不存在时严禁交付。S1 首任严禁交付。禁止引入质量证书（QualityCertificate）或事后复核机制。系统取消交付后的读回比对校验。终态交付之后严禁再调用任何模型。
- 失败后果：若文件读取失败、为空或持久化出错，交付调用必须失败，当前任期保持不退休状态。

## [009] ask 契约

当跑者调用 `ask` 向用户提问时，系统管理该挂起交互。

- 允许后果：每次调用登记一个待回答事实，并在界面幂等展示问题与等待状态。系统立即停驻本次模型运行，释放计算资源，不占用长期 Provider 请求。收到合法的用户新输入后，将原文作为 durable accepted 事实绑定到该调用，并通过工具结果（Tool Result）或合法 Continuation 回送给模型。
- 禁止后果：同一任务同时至多存在一个未决问题。严禁在挂起期间进行轮询、催问或消耗重试预算。工具结果回送与 Continuation 回送必须严格互斥，不得同时发生。
- 失败后果：当已有未决问题仍处于挂起状态时，再次调用 `ask` 必须返回拒绝错误。

## [010] resume 契约

当跑者调用 `resume` 调度固定 DevOps 时，系统执行单飞（single-flight）委派。

- 允许后果：系统查询本规划任务绑定的唯一固定 DevOps 实例。若未绑定则由运行时一次性创建并持久化绑定事实。通过 `PromptDispatcher` 投递调查目标，等待本次调用的完成事实，确认收敛后将完整答复交回 Planner。
- 禁止后果：模型在调用参数中不得传递 DevOps 的 id、role、model 等底层拓扑标识。系统严禁对 DevOps 的返回结果做二次模型摘要，必须完整交付。同一规划任务同时至多运行一个未决 DevOps 委派。
- 失败后果：若固定 DevOps 处于忙碌状态或响应失败，返回强类型错误，不静默丢弃，不新建替代操作员。

## [011] P 路径推导与访问受限

底稿 `P` 是模型唯一可读写的持久化文件产物，其路径由系统确定性派生。

- 允许后果：系统以 `workId` 派生安全编码的 `canonical-work-key`，路径固定为 `<git-common-dir>/wanxiangshu/plan/<canonical-work-key>/plan.md`。编程面工具 `js-plan` 仅作用于该单一路径，提供 `read`、`edit`（局部补丁）与 `rewrite`（全量覆盖）能力。写入采用排他临时文件写入、flush 后原子替换的方式发布。
- 禁止后果：模型不得在参数中自定义或传递路径。严禁路径穿越（path traversal）、符号链接逃逸、不安全 ID 注入或根目录漂移。`js-plan` 严禁提供任何超出 `P` 之外的文件系统能力，严禁暴露目录列表或仓库文件读写。写盘完成后取消读回校验。
- 失败后果：检测到路径异常或目标文件被外部非法篡改时，操作必须立即拒绝。

## [012] 恢复契约

当系统崩溃或重启恢复时，Plan 状态必须从 Durable 事实完全重建。

- 允许后果：系统按顺序重放事件，重绑当前任务的 `Active` 任期、阶段（`stage`）、固定 DevOps 绑定以及已落盘的 `P`。若已存在 `PlanDelivered` 事实，系统幂等呈现交付成果，不再拉起模型。未决的 `ask` 或 `resume` 状态按既有悬挂规则就地归位。
- 禁止后果：系统严禁通过分析自然语言文本内容猜测当前阶段。严禁凭同名物理会话猜测 DevOps 绑定。
- 失败后果：若 Durable 事实不自洽、损坏或缺少关键绑定，系统必须停留在恢复失败状态（fail-closed），严禁进行任何猜测性自愈。

## [013] 工具注册与分类

系统启动时，必须显式注册 Plan 相关的工具与参数规范。

- 允许后果：在 `ToolRegistry` 中登记 `js-plan`（作为 JS 编程面）、`ask`、`resume`、`handoff`、`deliver`（作为编排面 ToolSpec）。在静态工具名单（`StaticTools.fs`）与投机调查分类器（`InvestigationEstimateContract.classifyTool`）中显式登记上述工具名称。
- 禁止后果：严禁使用前缀匹配、通配符或模糊规则对工具进行分类或准入。未显式登记的工具不得进入 Plan 的工具列表。
- 失败后果：若工具名称或分类缺失，系统在启动静态校验或工具构建期必须报错阻断。

## [014] 阶段资源与语言对等

Plan 角色与各阶段使用的指导文档必须在资源目录中中英双语严格成对提供。

- 允许后果：以下资源必须同时存在 `en.md` 与 `zh-CN.md` 且内容语义对等：
  - `resources/provider/role/plan/`
  - `resources/provider/planning/s1/`
  - `resources/provider/planning/s2/`
  - `resources/provider/planning/s3/`
  - `resources/provider/tool/js-plan/`
  - `resources/provider/tool/ask/`
  - `resources/provider/tool/resume/`
  - `resources/provider/tool/handoff/`
  - `resources/provider/tool/deliver/`
  系统提示转换器在 `experimental.chat.system.transform` 挂钩中，按当前任期的 `stage` 精确装配对应阶段的方法说明（S1 对应事实与取舍，S2 对应理解与路径，S3 对应 Knuth 伪代码）。
- 禁止后果：任何资源缺失单侧语言文件均属于非法状态。系统不得在缺失时静默降级为单侧语言。
- 失败后果：装载期发现任何双语不对等或缺失，系统必须在 Boot 阶段触发致命失败（fail fast），拒绝启动。

## [015] 非 Plan 路径行为不变

Plan 机制的引入不得对既有系统角色与流程产生任何侵入或行为改变。

- 允许后果：既有角色（Manager、Orchestrator、Engineer、DevOps、Blogger）的权限投影、工具注册、生命周期转换、消息变换逻辑保持字节级行为一致。
- 禁止后果：非 Plan 角色严禁获取 Plan 专属工具。Plan 的 Baton 隔离逻辑严禁污染非 Plan 会话的消息集。
- 失败后果：任何非 Plan 回归测试失败均表明破坏了全局不变量，改动必须判定为不合格。

## [016] Plan 事件持久化、Canonical 序列化与投影集成

Plan 的生命周期事实接入 EventStore 统一持久化存储与 CanonicalIntegrator 投影体系。

- 允许后果：五条 Plan 事件按规范化 JSON 序列化并以自包含的 EventEnvelope 形式写入 `.git/wanxiangshu/events/<WriterId>.ndjson`。JSON 对象键必须按 Unicode 代码点严格升序排列，游标以 64 位整数无损保存。写入路径由 `PlanEventStore.append` 统一提供，在提交到底层存储前必须先以 `PlanFold.applyWorkEvent` 校验当前工作状态的合法性。读取路径由 `ICanonicalIntegrator` 与 `IEventStore` 的统一 `Current "Plan"` 导出 `PlanState`，并按 `PlanWorkId` 派生 `PlanWorkView`。
- 禁止后果：严禁在状态机规则校验失败时向 EventStore 追加事件。严禁使用非规范化、键序混乱或篡改字段的 JSON 格式。严禁绕过 CanonicalIntegrator 私自全量扫描历史文件或在内存中维护第二份状态。
- 失败后果：若追加前 Fold 规则校验失败，返回带具体原因的强类型错误，底层存储零写入；遇到标识碰撞或损坏的事件信封，Integrator 必须 fail-closed 拒绝并进入安全关闭。

## [017] 任期隔离与前缀重锚

当为处于活跃任期的会话组装 Provider 请求消息集时，系统应用通用的 Baton 隔离策略。

- 允许后果：输入消息集按序由以下部分组成：
  1. 当前规划任务因果范围内的全部真实用户输入原文（U），按原顺序完整保留；
  2. 若当前任期非首任，且存在上一任期有效覆盖范围，追加由上一任期记录渲染而成的记录消息（LWR_prev，由 `LifecycleWorkRecord.materialize` 且 `includeOpening = false` 渲染；若为首任则此项为空）；
  3. 开任游标（`openingCursor`）之后产生的本任期真实 assistant 与 tool 交互消息。
  若当前请求为任期内的首条请求（消息集中尚不存在当前任期的新生成交互，或任期刚发生切换），系统请求触发前缀重锚（TenureReanchored，reanchorRequested = true），开启全新的前缀 epoch。
- 禁止后果：上一任期及更早任期的全部 assistant 消息与 tool 消息必须全部剥除，不得进入消息集。规划底稿 P 的正文严禁直接拼装入消息集。用户消息 U 严禁压缩、重排、重编码或篡改。对于非 Plan 角色或无活跃任期的普通会话，严禁触发隔离与剥除。
- 失败后果：若任期消息装配失败或游标越界，系统必须停止当前请求组装，拒绝发送受污染的上下文。
## [018] 崩溃恢复判定与位置重绑

系统在进程重启或插件重新加载时，遵循算法 J 执行 Plan 崩溃恢复判定（`planRecoveryPosition`）。恢复仅依据 CanonicalIntegrator 导出的持久化 `PlanWorkView` 与确切的底稿文件存在性检查，绝对不按底稿正文猜测阶段，亦不凭同名 session 识别 DevOps。

- 触发条件：进程启动激活持久化层后，对已存在的全部 Plan 工作视图进行状态判定与恢复。
- 允许后果：
  1. 若工作视图标记为 `Delivered`（且持有合法的交付凭据与摘要），恢复位置为 `Delivered`，幂等呈现交付回执与底稿路径，绝不再调用模型，亦不开启新任期；
  2. 若工作视图处于 `Active(incumbencyId, stage)` 且底稿文件（`plan.md`）存在，恢复位置为 `Active`，准确重绑当前任期与阶段，并将底稿存在标志设为 true；
  3. 若工作视图处于 `Active(incumbencyId, stage)` 但底稿文件尚未创建，恢复位置仍为 `Active` 并标记底稿不存在，系统视为可继续推进（模型可从头编写底稿），绝对不得凭空假定已交付；
  4. 若工作视图既无活跃任期亦未交付，恢复位置为 `Nothing`，系统不执行猜测与推断。
- 禁止后果：严禁根据底稿文件内部的文本内容、标题或关键词推测任期阶段。严禁在恢复期间无故创建新任务、私自开启新任期，或在未获授权时自动派发 DevOps 命令。严禁在 `Delivered` 状态下重新激活会话或重试。
- 失败后果：若视图数据损坏或出现冲突（例如 Delivered 状态与 Active 任期同时并存），恢复函数必须 fail-closed 返回 `Conflict` 错误状态，保留原始证据，拒绝进入任何执行分支。
## [019] ask 两段式挂起与 Continuation 回送

系统遵循算法 G 执行 `ask` 的两段式挂起与 Continuation 回送协议：受理（追加待回答事实）→ 呈现问题 → 停驻模型输出（不轮询不催问）→ 等待合法真实用户输入 → 原文作为 Durable Accepted 事实绑定 → 回送合法 Continuation 或工具结果，二者严格互斥。

- 触发条件：处于活跃任期的跑者调用 `ask` 工具，或系统在任期隔离后检测到针对未决 `ask` 的真实用户新输入。
- 允许后果：
  1. 跑者首次发起提问时，系统在底盘原子追加 `PlanAskPending(workId, incumbencyId, question, cursor)` 事实，并在当前投影中登记未决提问（`PendingAsk`）；工具执行立即返回 `{ kind: "waiting_for_user", question }` 提示模型收束本次物理输出并停驻，不占用长期模型连接；
  2. 当处于 `PendingAsk` 状态时，若组装出的消息集中存在开问游标之后的新真实用户输入（`role=user` 且非合成记录），系统将其识别为回答，通过 `assembleAskContinuation` 将首条命中用户输入组装为 `role=tool`、`name=ask` 的合法工具结果 Continuation，携带调用身份回送给模型，并向底盘原子追加 `PlanAskResolved(incumbencyId, cursor)` 事实消灭未决状态；
  3. 同一任期内相同载荷的重复 `ask` 调用或相同游标的 `PlanAskResolved` 事件重放保持幂等。
- 禁止后果：同一任期内同时至多存在一个未决问题（`PendingAsk`）；在前序未决提问未被 `PlanAskResolved` 消灭前，严禁发起第二次 `ask`，违者必须返回强类型拒绝错误。已标记为 `Delivered` 的规划任务严禁再次调用 `ask`。系统严禁依据用户回答的具体文本内容（如是否为“是/否”）推测有效性，任何非合成用户原文均属于合法回答。
- 失败后果：若在无活跃任期、无未决提问或载荷冲突时触发解析与解决，Fold 状态机与工具门禁必须返回强类型错误并拒绝状态跃迁，底层存储零变更。

## [021] 任期隔离消息装配与重锚请求

当系统为 Plan 角色组装 Provider 请求消息集时，`assembleTenureMessages` 负责任期切分、LWR_prev 注入与重锚请求判定。

- 允许后果：消息装配按 §017 规则执行任期隔离，并返回 `reanchorRequested` 布尔标志供上游消费。
- 禁止后果：严禁在无活跃任期时触发装配逻辑。严禁伪造默认 S1 数据。
- 失败后果：装配失败时停止请求组装，不向下游发送污染上下文。

## [022] 退休范围与任期推进

当任期退休时，系统记录其游标覆盖范围 `PreviousRange`，供下一任期在消息装配中精确切分历史。

- 允许后果：`latestRetirementRange` 返回最近一次退休的 `[openingCursor, retirementCursor]` 区间。`assembleTenureMessages` 消费该区间，将区间外的旧消息剥除，区间内的历史渲染为 `LWR_prev`。
- 禁止后果：严禁在无退休记录时返回伪造范围。严禁跨任期泄露 assistant/tool 消息。
- 失败后果：范围越界或数据冲突时，装配必须失败并拒绝发送。

## [023] 任期重锚事件与幂等

当 Plan 任期切换触发前缀重锚时，系统追加 `TenureReanchor` 事件记录任期身份与 epoch 跃迁。

- 允许后果：`applyTenureReanchor` 接受 `{ previousEpoch, nextEpoch, incumbencyId }`，追加到前缀状态。同一 `incumbencyId` 的重锚事件幂等拒绝（`TenureAlreadyReanchored`）。`reanchorRequested` 在 `isFreshHandover` 时返回 true。
- 禁止后果：严禁同一任期多次重锚。严禁在无任期切换时伪造重锚事件。
- 失败后果：重复重锚返回强类型拒绝，状态零变更。

## [024] 连续三任期接力契约

系统在单个物理会话内支持连续三次任期接力（S1 → S2 → S3），每次交接遵循 §007 handoff 契约，阶段单向推进，终态交付遵循 §008 deliver 契约。

- 允许后果：`decideAction` 依据当前任期阶段与事实单一判定放行/拒绝动作，三任期接力全程状态机不变量保持。
- 禁止后果：阶段严禁逆行或跳级。终态交付后严禁再次发起任何动作。
- 失败后果：不合法动作门禁返回强类型拒绝，状态零变更。
