# execution-model-routing — WHAT

## [001] 唯一模型调度 authority = `~/.config/opencode/wanxiangshu.mjs`；缺失时原子创建推荐模板

Wanxiangshu 的 managed 模型调度由 `~/.config/opencode/wanxiangshu.mjs` 的 default export 唯一决定。其余来源（`opencode.json`、环境变量、Host-final agent inventory、内建表）一律不得覆盖或替代该权威。
若文件不存在，系统在加载时确保目录存在并原子创建推荐策略模板，随后加载该文件。已有文件严禁自动覆盖。文件缺失、加载失败或导出非法时直接 fail closed。

## [002] scheduler ABI 为 `role + running + previous → target | null`，协议版本 `routingProtocol = 2`

调度函数的签名合同为：
```js
export const routingProtocol = 2
export const hasTheoreticalCapacity = (role) => { ... }
export default function route(role, running, previous) { ... }
```
- `role`：当前请求由 IdentitySeed 确立且在 logical run 内不可变的 fixed canonical Role。每次 fresh physical execution 均以该固定 Role 作为调度输入。participant 由 IdentitySeed 确立，随 acquire 输入与 capacity identity 显式传递，不作为调度函数参数。系统严格区分本地 participant 与远端 ModelTarget。
- `running`：当前进程所有活跃 provider capacity token 的 ModelTarget multiset，元素形状固定为 `{ model: string, reasoning: string }`，保留重复项。
- `previous`：仅当同一 SessionId 的当前活跃物理执行被原子取代时，才将该被取代执行的 target 作为同一 demand 的 fresh 调度输入；新 Session、exact terminal 释放后重建、无关 Session 一律传 `null`。`previous` 只是接续偏好提示，不占容量。不存在只读副本入场，也不存在第二模型池。

- `routingProtocol`：模板必须导出 `routingProtocol = 2`；它是协议格式版本，不是启用开关。加载器显式验证 `routingProtocol === 2`；缺 `route` 或 `hasTheoreticalCapacity`、或另设只读委托参数来打开第二模型池的配置，必须给出可操作的错误并 fail closed，且不得自动覆盖用户已有的 `wanxiangshu.mjs`。
- 协议 2 的导出形状除 `route` 外还包含只读查询 `hasTheoreticalCapacity(role)`。理论容量只按 Role 的模型池回答。不要求单独的配置存在性查询；该查询存在或缺失都不得打开只读副本池。
- 返回值：必须包含非空 `provider/model` 与 `reasoning`，或返回 `null`。
- 抛出异常、返回 Promise 或非法结构均视为配置错误，直接 fail closed。

## [003] `running` 是真实 provider capacity token multiset；不是 live-session / active-execution 计数

`running` 准确反映系统内部实际持有的 provider capacity token 集合。基础 token 总数即为 `running.length`。同一进程内所有 plugin 实例与 worktree 共享该 module-level multiset 真相。显式 lender 的 token 被借用方使用时只计数一次，不得重复计数。

## [004] 普通 fresh execution demand 在 `chat.message` 准入产生；`null` = 等待，不是失败

发送或排队阶段严禁抢占 model slot，`SendPrompt` 必须保持 `Model=None`。新 execution 的需求准入点是 Host 接收物理 user message 后的 `chat.message` 边界。同一活跃 run 的 `HumanMessage` / `BusyAgentNudge` 只接纳追加材料，不产生第二份调度需求；其 exact lease 交接遵循 [006] 的可见材料边界。
若调度器返回 `null`，不调用 provider、不消耗失败预算，demand 进入 pending 队列并在 occupancy 变更时由事件驱动重算。新到达的物理 user message 或会话销毁将取消并取代被 supersede 的旧 pending demand。

## [005] 模型选择策略全部属于 MJS；runtime 不再拥有 lane、容量表或候选算法

Runtime 仅负责加载 scheduler、校验 ABI、维护进程共享的 token ledger 与借贷仲裁，不拥有任何模型分类、优先级表、容量上限或调度策略。一切关于模型选取与并发限制的逻辑均属于 MJS 策略。

## [006] exact lease 与 fresh execution 路由；活跃 run 的追加材料延后交接

物理执行租约与 `(SessionId, PhysicalUserMessageId)` 绑定。同一 PhysicalUserMessageId 的执行与重试严格复用已有 target 与 capacity fence，不重新触发调度器，亦严禁在同一 physical 内改变 Role、participant 或切换 agent；同一 SessionId 出现新 PhysicalUserMessageId（fresh physical execution）时原子替代旧租约，并将该 run 不可变的 canonical Role 重新经 MJS 调度器路由至 target（可选择新 target；仅当旧执行仍是当前活跃执行时才将其 target 作为 `previous` 传入供优先续用），但绝不改变 participant identity 或 Role。provider step 结束时必须把实际 lease target 与 exact `ProviderRunIdentity` 绑定；failure settlement 只可原子消费该 witness，禁止从 mutable session-last target 猜测失败 provider。每个 session 最多保留 latest run witness，新 run 自动废除旧 witness。租约不以 SessionId 为单位跨物理执行永久绑定；exact terminal 释放后不存在 session 级 previous 缓存。

成功建立新物理执行的 `Admitted` lease 或 `Queued` demand 才构成原子替代。调度返回 `null` 且 pending queue 已满时，新输入必须以 `CapacityQueueFull` 拒绝，保留旧 exact lease、token 和 pending ownership；不得先退休旧执行后再发现无处排队。队列已满但 scheduler 能立即发放 target 时，仍允许直接入场，不得仅凭队列长度拒绝。

同一 LogicalRun、authority root 与 IdentitySeed 的 `HumanMessage` / `BusyAgentNudge` 若遇到旧活跃 committed lease，属于追加材料而非上述 fresh 调度。`chat.message` 保留旧 lease 并投影其 target；实际 provider transform 选择可见新输入后，才按旧 opaque lease 验证所有权、转交同一 token/借用 credit 并建立新 physical key 的 fresh fence。保持原 role、participant、target 与 lender，不再调用 scheduler，不另占容量，不提前取消旧请求的 step。旧 exact 结算不能释放新 fence。

durable Accepted 之后、Host projection 之前，容量 owner 以 exact 新输入 key 保留旧 opaque lease 的 credit。旧输出自然终结时若仍有未选入请求的已接纳材料，返回 typed `HeldForInput`，不丢失已投影的模型与借用关系；下一次可见输入准入才交接并撤销本次保留。较晚的未选入材料随同一 credit 更新保留的 opaque handle，不能被较早请求结算掉。投影失败、材料精确终结或作用域关闭均须清理自己的保留；最后一份材料撤销后完成延后的旧资源归还，borrowed credit 不退休 lender。

## [007] physical execution identity / end evidence 释放 occupancy；session/业务 lifecycle 不拥有槽

租约释放必须依赖确切的物理执行终结证据（无 error、completed assistant、`finish` 明确属于 `stop | length | content-filter`，且其 parentID 匹配 PhysicalUserMessageId）。
`finish="tool-calls"` 仅终结单步并归还 step token，不解除 physical execution binding；assistant error，以及 Host 将流错误归一化后的 `finish="unknown" | "error"`，都只作为单步终结证据，不直接删除 execution binding，以便 Host 继续同 material retry。业务层的 handle 完成、join 或 finality 不直接操作租约。

## [008] `opencode.json` model 不再具有 authority；不校验不同角色 model 互异

Host 的 `opencode.json` 不作为 managed model 的真相源。系统不要求不同 canonical 角色使用互异的物理模型字符串；两者解析至相同 target 属于合法状态 (历史 `fast-`/`deep-` 档亦然)。

## [009] managed model 准入与读取分离；dispatch message 保持 model-free

所有内部 synthetic prompt 分派均保持 `Model=None`。Host 接收物理 user message 后的 `chat.message` hook 负责获取租约，并将 `{providerID, modelID, variant}` 投影至 mutable message。后续 hook（`chat.params`、transform、tool）通过容量所有者的只读查询读取该 exact execution 已提交的租约；该查询不得调用 scheduler、不得发放 fence、不得 commit/release，也不得因读取增加 duplicate/stale/conflict 计数；观测通过后，managed 请求只投影 `temperature = 1.0`，不改写宿主 model 对象。运行时观测只读，不再校验 agent 或 model/reasoning 与租约是否一致；「agent 与 model/reasoning 不漂移」由测试期 fast-check 性质保证（`execution-model-routing/tests/009`：对任意 `route(role, running, previous)` 决策，`chat.message` 投影的 target 恒等于已提交租约 target，`chat.params` 对同一 target 的观测恒通过且只投影 temperature）。取舍：运行时不再校验漂移，若漂移真实发生将静默通过、不阻断，不再抛 PROMPT-006 漂移拒绝。

[006] 的追加材料在 `chat.message` 只投影旧目标；provider transform 先调用独立的可见输入准入操作，完成 exact lease 交接，再执行上述只读查询。这不是缺失租约的通用回填：没有 exact Accepted evidence、同 run 证明或旧 committed lease 时仍须 fail closed。

## [010] provider capacity 独立成可抢占 token；只凭显式 lender 借用

ModelTarget 物理绑定与 provider capacity token 严格解耦。在 provider 请求发出前，`experimental.chat.messages.transform` 负责获取对应 provider 的 capacity token。
借用只能使用 acquire 输入中 `lenderSessionId` 显式指定的 lender 的 credit；无 lender 输入的 demand 只走普通容量，绝不因派生关系、 ambient 拓扑或同名 session 而获得信用。token 仅在 provider-step 边界转移。显式召回（release/retire）须等待借用方 step 结束；但当 lender 自己的 `experimental.chat.messages.transform` 为同一 owned credit 再次进入后续 provider step 时，容量所有者必须在仲裁前回收该 credit 上任何外来 InFlight/Retiring step（transform 入口抢占召回）：回合内后代借用可阻塞属主，属主 transform 一旦触发即回收。等待中的 borrower 仍按单调序号优先于较晚的 lender owned step，不得被该回收饿死。
同一 token 同时面对多个可执行 provider-step demand 时，必须按 demand 的单调序号选择最早者；owned、borrowed、ordinary 只决定该 demand 可使用哪枚 token，不构成调度优先级。较晚到达的 lender owned step 不得越过已等待且可借用该 token 的 child step。
Host 开始执行某个 managed tool 时，tool context 的 exact `ProviderRunIdentity` 构成 provider→tool 的因果 step 边界：在任何 capability/role gate 与 tool body 运行前，必须用当前冻结的 `PhysicalUserMessageId` 结束该 provider step，使 token 进入可借用的 idle 状态。工具体可以同步等待 descendant provider work，因此严禁把 provider capacity 持有到 tool body 返回、严禁以 wall-clock timeout 猜测何时释放，也严禁通过允许借用真实仍在执行的 token 伪造并发容量。

同一物理执行通知重复进入相同 fence 的 provider step 时，opt-in 携带 `requestKey`（由容量所有者从 `(sessionId, physicalUserMessageId, fence)` 派生）以实现精确幂等：若当前持有 token 的 step 匹配相同三元组且 fence 不超前，立即返回已完成 Task 复用已持有 step；若 waiters 队列中已存在相同 `(SessionId, PhysicalUserMessageId, RequestKey=Some k)` 的 demand，直接返回该 demand 既有的 Task（同一 Task 对象且 waiters 不增长）。不同 requestKey 或无 requestKey（None）的调用则保持正常的 FIFO 排队与因果辈分。此设计遵循 的 `admit(exactRequestKey)` 精确幂等原则，杜绝无幂等重入 transform 造成的容量死锁。

## [011] 物理 admission 顺序固定为 accept → acquire → project

Managed chat execution 必须先为 exact `(SessionId, PhysicalUserMessageId)` durable 写入 `Accepted`（携带 IdentitySeed 确立的不可变 canonical participant 证据），路由器随后才可排队或获取容量。
一次物理消息执行（physical message execution）的 scheduler/acquire 严格在 durable `Accepted` 之后，顺序为：
1. durable accept：写入包含 exact session、physical message 与 canonical participant 的 durable `Accepted` 事实；
2. exact capacity acquire：将 IdentitySeed 确立的 fixed canonical Role 经 MJS scheduler 解析为 ModelTarget（同一 physical execution 复用原 target；acquire 输入同时携带 IdentitySeed 派生的 participant 与可选的 `lenderSessionId`），并以包含 SessionId + PhysicalUserMessageId + fixed Role + Participant + target + fence 的 exact capacity identity 获取 capacity fence；
3. Host projection：将 target 投影至 Host 消息。
任一步失败只能交给 `execution-failure-policy` 结算已拥有的事实与资源；严禁 acquire-before-accept、先改 Host message 后补 capacity acquire，或让未接受的发送意图预占容量。此顺序只约束物理消息执行的普通准入。不存在只读副本的非等待预约。

[006] 的活跃 run 追加材料不预占新的容量：durable accept 后可投影已经取得的旧 target；新 exact lease 只能在可见材料进入下一次 provider 请求时交接，provider effect 前仍须完成 exact commit 与 durable ProviderStarted。

## [012] Capacity 是 exact opaque fenced capability

每次成功 acquire 返回不可伪造、单次结算的 capacity fence。
Capacity exact identity 严格为 `(SessionId, PhysicalUserMessageId, Role, Participant, ModelTarget, CapacityFence)`，严格区分本地 participant（Role/Persona/SelectedAgent 证据）与远端模型目标（ModelTarget）。
同一 physical execution 的重试严格复用已有 target 与 fence；fresh physical execution 则获取对应新 execution 的 fresh fence。
capacity fence 至少因果绑定 exact session、physical message、fixed Role + Participant、target、owner lineage 与 fence epoch。borrow/recall 只转移同一 fence 的合法 custody，不复制 token。release、retain 与 transfer 必须携带 exact fence 并验证包含 fixed Role+Participant 与 exact target 的完整 capacity identity 及当前 custody；旧 epoch、重复 settlement、错误 physical id 或按计数/session 猜测释放均 fail closed。capacity settlement 的选择由 `execution-failure-policy` 输出，路由器只验证并原子执行。

## [013] Pending demand 是 bounded typed queue

`null` 或暂不可 acquire 的已接受 demand 只能进入有明确上限的 typed queue；entry 必须携带 exact `(SessionId, PhysicalUserMessageId)`、resolved target、role/participant/lender 输入与 supersession identity。队列满返回 typed `CapacityQueueFull` 并交给 failure policy，绝不得丢弃、无限扩容、解析错误文本后重试，或借 wall-clock timeout 清退。capacity release、exact supersede、session deletion 与 shutdown 是队列重算/移除事件；队列 correctness 不依赖 polling、sleep 或 elapsed time。

已确认的 provider 驱逐同样触发队列重算。每个既有 demand 按自己的 role 查询理论容量：仍有候选时继续正常调度或排队；已无候选时以与新 admission 相同的 typed `CapacityQueueFull` 结算，不能等待已不存在的容量生产者。其它池的 demand 与已提交 exact lease 不因此被取消或释放。普通 route 返回 `null` 不等于理论容量为 false。

## [014] Capacity snapshot 与 reconciliation 只观察、绝不修复

Capacity owner 必须发布不可变 snapshot：ledger entry、token state、exact execution owner、pending waiter、role/participant 身份与单调 duplicate/stale/conflict transition counter。始终满足 `0 <= active <= ledger entries`，且每个 token、waiter 与 map owner 均可追溯到 snapshot 内同一 exact identity。纯 `Evidence -> Decision` reconciliation 对合法 evidence 返回 `NoOp`；任何 map/ledger divergence、无 owner token/waiter 或不可能计数返回 typed `FailClosed`，不得自动补删 ledger/map、清零 counter/config 或借时间推断。release、commit、cancel 仅返回封闭 outcome `Applied | AlreadyApplied | StaleFence | Conflict`；duplicate 不得二次递减，旧 fence 不得触碰新 execution。

## [015] Reliability query 复用 capacity owner snapshot

Reliability query 的 queue depth、active lease 与 duplicate/stale/conflict fence 数必须逐字段投影 execution-model-routing-014 immutable snapshot；diagnostic 模块严禁维护第二份 capacity/release counter、重算不同公式、reset/repair owner state 或把 query result 反馈给 routing。`CapacityQueueFull` observation 是缺失的 process-local monotonic diagnostic counter，只用于观测，不授权重试。

## [016] routing fatal绑定exact fence settlement并经注入fuse执行

fatal incident必须携带exact execution key、capacity fence及`Committed | Unknown` settlement evidence；未settle、stale fence与coarse session identity无权fatal。routing/Host port只接受composition注入的mandatory fatal capability，不得直接引用physical adapter。同一incident只允许一次report与kill，fatal不得修复、清零或释放capacity state。

## [017] provider 恢复重投的原目标绑定与失败驱逐

provider 恢复的一次已确认失败由 `ModelRouting` 结算失败 attempt 的 exact witness（execution-model-routing-006），且只有两种结局：

- 保留目标：为同一 `SessionId` 写入一次单次消费的 recovery retry 绑定；该 session 的下一次 fresh admission 以该 target 作为调度偏好（优先于被原子取代执行提供的 `previous`），绑定随之被消费，无论该 admission 成功、排队还是被更新物理消息取代。
- 驱逐目标：其 provider 在进程生命周期内被 poison，后续 fresh admission 回到普通调度。

两种结局都只属于该 exact witness：witness 单次消费，重复或过期证据不产生第二结局；无 witness（例如进程重启后）不产生任何结局。recovery retry 绑定不是 session 级 previous 缓存：它只由失败结算从 exact witness 显式写入、只服务该 session 的下一次 fresh admission，并在 force cleanup（`ReleaseExecution`）时清除。

provider 健康变化只参与 fresh demand 的准入与既有队列的重算。同一 exact physical execution 重放时，先核对既有 role、participant 并复用原 fence；不得把后来的理论容量不足误作该已提交准入的拒绝，也不得为此替换或释放旧 lease。

## [018] 新角色集合模型路由与旧角色槽位解耦

Wanxiangshu 的模型调度权威以当前合法角色集合（Engineer、DevOps、Manager、Orchestrator、Plan）为唯一合法输入。
推荐策略模板与 MJS 调度器仅要求当前合法角色模型槽位；若调度输入为已废除角色，系统必须 fail closed，严禁为其分配模型租约或发起 provider 物理准入。

## [019] 固定 DevOps 模型绑定持久性与禁止通过 resume 换模型

同一道路内固定绑定的 DevOps 的 ModelTarget 由道路初始化阶段确定并持久化记录。
后续该道路内所有针对 DevOps 的 resume、续行或崩溃恢复，必须严格继承并复用该既有 ModelTarget，严禁通过 resume 参数或运行时策略重新分配、篡改或覆盖 DevOps 的物理模型。固定 DevOps 目标绑定只约束原 owner execution。不存在只读委托用途，也不得另立副本池覆盖该绑定。

## [020] 不存在只读委托池

只读委托用途与副本调度不是当前模型合同。加载器不要求单独的配置存在性查询，也不因任何查询存在或缺失而打开第二条调度轴。理论容量只按 Role 回答。容量暂满、provider 不可用或一次 route 返回 `null`，按既有 pending、取消与失败规则处理，不另造启用状态。
