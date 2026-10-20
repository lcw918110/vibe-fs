# crash-reconciliation — WHAT

## [001] process-local 状态不是恢复权威

进程重启后，所有 process-local 状态（包括 `armedByFailure`、degeneration-guard armed anomaly、`QuiescencePermit` 与 detector 状态）全部清空，绝不得作为恢复权威。没有 fresh evidence 严禁自动产生任何副作用。

## [002] 重启从 durable facts + 可信物理观察重建世界

系统恢复仅允许两类输入：EventStore 中已提交的不可变事件及其 fold 投影，以及 Host SDK 快照、Git ref 等可信物理观察。严禁使用缓存、墙钟时间或日志散文推断状态。

## [003] 未决外部 effect 先 reconcile 再决定是否可重试

结局未知的外部 effect 严禁直接视为未发生而盲目重放。Reconcile 观察中 `finish=None` 的快照属于私有观测 `TurnUnknown`，必须等待静止证据后由业务层决定处理策略。

## [004] 恢复复用普通 workflow 入口，不发明程序计数器

恢复过程遵循 `Journal facts → Fold → 纯恢复决策 → 普通 workflow 合法入口`。严禁恢复 Program 节点、continuation 或执行步数，严禁引入 `RecoveryStage` 等第二状态机。
所有工具与执行中断均由 crash-reconciliation-017 / crash-reconciliation-018 约束：工具不设隐式崩溃恢复 owner，严禁在新进程启动时自动重放、补写完成态或隐式修复；系统在加载阶段自行归位（结算遗留子 run、重新登记子会话），不存在显式续传命令；新进程不靠回填执行绑定缓存恢复发送能力，无该缓存时仍依据权威事实正确发送与查询。
## [005] ambiguous / multiple / missing 证据 fail closed

恢复证据不足、冲突或缺失时，系统必须显式停留在 `Waiting`、`Blocked` 或 `RecoveryIncomplete` 分支，严禁猜测继续。
Waiting 分支仅代表瞬态等待（例如等待新 turn 或观察稳定），任何消费端在处于 Waiting 状态时严禁执行 Ready 分支的副作用操作（不得发送请求、不得发布完成态、不得虚假声明就绪），只读观察必须与 effectful observe 严格分离。
## [006] 没有 fresh evidence 就没有自动 effect

恢复闭合后，所有副作用操作必须持有有效证明：持有 `FamilyRecoveryPermit` 才能执行 join；持有保持 fresh 的 `QuiescencePermit` 才能发送 idle-derived continuation。quiescence 是物理条件的合取：当前 provider attempt 已被 Host 观测为 idle，且该 SessionId 没有仍在执行的 tool body；Host 的 `SessionIdle` 若先于 tool completion 到达，只能建立待静止证据，permit 在最后一个 active tool 结束前不可消费。新的物理用户输入到达时立即幂等撤销旧的静止许可。permit 在物理发送边界被消费；若 Host 明确证明 acceptance 前拒绝、且同一 attempt serial 仍未被更新材料取代，则允许把该 exact permit 从 `IdleConsumed` 原子归还为 `Idle`，使仍未满足的 gate 可重试。任何更新的 provider attempt、物理用户材料或 acceptance-unknown 都使归还失败。
当前进程 join/admission 凭据仅证明本进程内的准入合法性，跨进程恢复证明已由 crash-reconciliation-017/018 显式续传取代；持有 `FamilyRecoveryPermit` 才能执行 join（保留 exact membership 闭包检查）。
## [007] TurnUnknown 是 reconciliation 私有观测

`TurnUnknown` 仅为 reconciliation 内部观测，严禁作为正式的 `TurnOutcome` 对外发布。

## [008] abort 是 typed 控制面，不是 ProviderFailure

Host 的 abort 信号解码为类型化的 `AttemptAborted` 控制面事件，撤销当前 attempt 的所有 continuation 能力，并唤醒 Reconciler；严禁改写为 `ProviderFailure`。

## [009] child recovery 没有 Aborted 终态

Child 终态仅包含 `Succeeded | Failed | Abandoned`，不存在 Aborted 终态。单纯的 abort 观察绝不构成 terminal 证据，JoinableCompletion 必须具有真实解码正文。

## [010] 恢复结果分支穷尽，Waiting ≠ Blocked

恢复结果分支必须语义互斥且穷尽：`RecoveredActive`（活跃运行中）≠ `RecoveryIncomplete`（缺少终态证据需等待）；`Waiting`（瞬态等待）≠ `Blocked`（硬性失败阻断）。

## [011] 线性序 permit → join，每 join 重新验证

每次执行 join 之前必须重新验证 `FamilyRecoveryPermit`。Permit 携带恢复闭包的成员集合；若已恢复成员丢失则拒绝执行，恢复后新增成员允许单调准入。
跨进程与当前进程 join 的证据接口必须在类型和语义上与真正核对结果一致，禁止伪造全量家族恢复完成凭证。
## [012] completion 单一 owner

HandleController 的 `recordCompletion` 是提交完成态的唯一入口，采用 blob 先于事实的原则，拒绝重复 claim，并通过 retire 墓碑保证重启后完成态不重复投递。

## [013] combine 优先级 Blocked > Waiting > Recovered，按层序无关

多个恢复结果合并时，优先级严格满足 Blocked 优于 Waiting 优于 Recovered；同层级内的合并与输入顺序无关。

## [014] closure 校验与 permit 单调准入

闭包中若出现重复 session 则判定为 `RecoveryCycle` 并 fail-closed 阻断。Permit 校验要求闭包成员单调不丢失。

## [015] Attached restore 复用/替换/fail-closed

重启后附加子会话恢复时：匹配唯一关联 ID、agent 与 title 时复用；关联不存在时新建；发生冲突或多重匹配时 fail-closed 阻断。Replacement 必须先证明旧物理会话消失，显式执行 Close 后再 Link 新会话。



## [017] 工具中断不恢复；未来 session 续传必须显式

工具执行本身不设隐式的崩溃恢复 owner。进程死亡时正在运行的工具调用均按中断处理，严禁在新进程启动时自动重放、补写完成态或隐式修复。

## [018] 重启后由系统自行归位，不再有显式 resume 命令

进程重启后不存在任何显式续传命令（`/continue` 已移除）。系统在加载阶段自行完成归位，且只做持久记账，不重放任何命令：

- 上一个 runtime 遗留的活跃子 run 被**作废**：已有 scoped 工作须用其实际准入身份追加 `ExecutionFactCases.ChildWorkVoided`；合法的无 scope 历史才使用 `ChildRunVoided`，不得由裸 Root 补造工作准入。作废只关闭对应子会话的逻辑 run，不产生任何“待收交付”；该 run 什么都没产出，就不欠父会话一次 join——`horizon` 与 `join` 对它都为空，直接 `resume` 才是正确时序；
- 父子会话的执行绑定与 fission lane 归属按 durable 投影**按需解析**（取 handle 的 `TargetAgent`，不取逻辑 `Byname`），进程本地表只是缓存，装载阶段不做任何预登记扫描；
- 复用仍由 manager 显式发起；复用门禁、placement、await 一律以 durable handle 为存在性依据。

被中断的工具调用保持失败并原样留在可见历史中，不得推断其完成、隐藏它或伪造终态。
本进程完成加载归位后，会在下一次真实用户指令之前 prepend 一次只读的状态指导，向模型说明万象术已被重启及其含义。状态指导只出现一次；它不改写用户原文、不创建或延续 Interaction Authority、不改变角色、权限或主体，并冻结已交付的 provider wire 字节。除此之外，不存在独立的续传材料通道，也没有 disclosure-only 的 provider 轮次。

## [019] 外部 effect 必须逐项闭合 crash reconciliation 合同

每个高价值外部 effect 必须在唯一 owner 下登记类型化 `intent → process-local admission → physical receipt → durable outcome`；不适用阶段必须给出明确理由。登记项必须锚定物理 effect identity、有限且穷尽的歧义状态、查询或补偿入口、安全重试律（仅 `proven-not-applied` 或 `never`）、以及复用普通 CE 的重入入口。Host、provider、Git 与 process 边界必须同时具有确定性歧义证明和 Adapter 或 Long-Stroke 证据；证据层级由 verification owner 的独立 registry 按精确 `(path, title, WHAT)` 唯一分类，effect 行自报、改标或未登记分类均不得计入证明。Prompt dispatch 必须锚定物理发送前的 process-local `physicalAdmission`；外部 receipt 是 `TransportReceipt`/`PluginPromptSubmitted` 以及随后接受的 `PhysicalUserMessageId`，不是预先可派生的 `PromptKey`。恢复不得持久化 capability、continuation、`ResumeAt`、`RecoveryStage`、`RecoveryStep` 或 `NextAction` 程序计数器；未知、冲突、缺失证据一律 fail closed。登记的 owner、WHAT、source symbol 与 executable proof title 均为精确锚点，重复 WHAT ID、过期锚点或未闭合 effect 必须使 gate 失败。

## [020] 固定 DevOps 崩溃恢复的单一逻辑执行权威与命令去重

同一道路绑定的固定 DevOps 在崩溃恢复后必须且仅能映射到唯一的当前活跃物理会话，严禁生成两个并行生效的可执行物理权威。
崩溃前未决的物理命令（`run`、PTY 输入）一律按中断处理，系统严禁在重启后自动重放、补写或隐式续发命令，杜绝物理副作用重复发生。
恢复流程必须严格沿用道路初始化时持久化的绑定模型（ModelTarget）与 Persona，严禁在恢复或 resume 时切换模型；新会话与恢复只接纳合法新角色集合，历史旧状态不隐式跨边界恢复。
Load Phase 必须一次性结算上一 runtime 遗留、本进程无法继续持有的 durable 未决工作（仍活跃的子工作 run）。
该结算是加载归位的首个持久动作：作废事实必须先于下一次交接的身份判定落定，否则新交接会读到仍然活跃的旧根，把已经退休的子会话判成身份冲突。

## [021] 进程本地表是缓存，durable 投影是存在性真源

任何“这个子会话/lane/handle 是否存在、属于谁、由谁执行”的判定都必须能从 durable 投影回答；进程本地注册表（子会话登记、dormant 集、执行绑定、lane 注册表）只记录“本进程当前在驱动什么”，不得作为拒绝、忽略或“未知”的唯一依据。

- 未命中缓存时的正确行为是**按需解析并回填**（`DurableChildLookup` by handle id / byname、执行身份按 durable authority 投影与 exact lease 解析、`FissionRuntime` 的 durable lane 证据），而不是回 `Unknown agent id`、`person-unavailable` 或静默跳过；
- 装载阶段不再有“预登记/预热”特例通道：同一件事只有一个按需规则，避免重启路径每多一处读取就多一处补丁；
- 只有进程资源归属（本进程持有的 PTY、pending run、teardown 集合、live companion host）可以只读本地表；它们描述的是进程，而不是世界。
