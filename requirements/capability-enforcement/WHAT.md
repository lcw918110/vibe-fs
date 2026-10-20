# capability-enforcement — WHAT

## [001] 每次 provider attempt 有一个 canonical ToolCapabilitySet，由 CanonicalRole × RequestKind 唯一决定

每次 provider 请求执行的可用能力集合（ToolCapabilitySet）由 `CanonicalRole` 与 `RequestKind` 唯一确定，并在构建 `AttemptExecutionProfile` 时单点完成组装。禁止在 profile 之外另设独立的权限来源或旁路字段。

## [002] provider-visible schema 与 runtime execution gate 读同一 capability truth

Host 侧展示给模型的工具 Schema 与运行时执行拦截 Gate 读同一 capability truth：schema 是固定上限投影，执行门禁是当前事实投影；两者同源，不意味着每轮动态删工具。无权限工具不出现在 Schema 中，异常绕过 Schema 的调用亦在运行时 Gate 被即时阻断。

## [003] capability projection 可按 office + request contract 收窄，但不得扩大 office entitlement

能力投影允许根据特定 `RequestKind`（如窄通道）对角色固有权限进行收窄，但任何投影出的能力集合必须是 `Roles.permissions(role)` 的严格子集，严禁发生权能扩大。

## [004] execution tier 不改变同 office 的 authority：同一 CanonicalRole 的权限集与 tier 无关

同一 Office 的工具权限集完全由 `CanonicalRole` 决定，与执行档位无关。执行档位仅代表底层机器与推理深度，不影响权限矩阵。托管 agent 使用 canonical bare 名称 (`manager`/`orchestrator`/`engineer`/`devops`/`plan`/`bookkeeper`)，不再有 `fast-`/`deep-` 前缀；已废弃的 `blogger` 与 `predictor` 不得注册。

## [005] StrengthReplica 与只读副本工具面已废弃

`StrengthReplica` 请求类型及 `todowrite-for-predictor` 已整体废弃，系统不再为投机调查副本开放专用工具面；任何未知或已退役请求类型在权限投影与运行时门禁中一律 fail-closed（空权限集）。

## [006] internal-only participants/actions 不进无资格 participant 的工具面

内部专用工具（如 Bookkeeper 的 `todowrite-for-bookkeeper`）仅向对应内部角色开放，普通角色不可见亦不可执行；已废弃的 `chronicle` 工具与 `todowrite-for-blogger` 不再注册。认知与交互效用工具（如 `assume` 等）及 Host 工具（如 `skill`）不属于领域业务权限，不进入 `Roles.permissions`，亦不得借此扩大角色的领域权能。

## [007] Host-native/MCP/plugin 等不同技术来源的 actions 服从同一 semantic capability policy

无论工具来源于 Host 原生、MCP 外部集成还是插件内部注册，其权限控制均由唯一的领域能力令牌统管。权限控制仅覆盖本地离线能力，任何角色均无外部网络浏览工具；Sphinx MCP 经程控工作流调用，严禁为不同技术来源维护独立的权限映射表。

## [008] js-* 编程面四层同构：capability → base-class member → description → example → runtime gate

针对 JS 文件系统能力：若角色缺少对应 capability，则代码生成器生成的基类中不包含对应方法、工具描述中不提及该方法、示例代码中不展示该方法，且底层运行时 Gate 同样拦截对该方法的调用。编程面四层同构原则不变；`todowrite-for-manager` 面向 Manager，只读文件能力为 Read/Glob，另持有委托原语 Fork/Join（无任何写或执行成员），其能力过滤必须到达真实执行 API 层。

## [009] 工具名引用完整性：same tool name → 唯一 schema owner + 唯一 semantic contract

全系统内同一工具名必须对应唯一的参数 Schema 定义与唯一确定的生命周期、语义动作及返回契约。禁止不同角色在同一工具名下共享存在语义分歧的契约。`fork` 与 `resume` 属于不同生命周期契约，必须保持严格独立。

## [010] 双层 fail-closed：Role 未定 → 工具集空/拒绝执行；Host 配置异常仍写 deny 默认

若角色或 profile 无法解析，模型可见工具集置为空集且运行时拦截一切调用。若 Host 配置校验失败，系统必须先写入全量 deny 默认策略以覆盖 Host 宽松默认，随后触发进程级致命错误终止退出。

## [011] external_directory=allow 是 Host 路径边界元权限：每 managed agent 显式写入、唯一生产写点

`external_directory = "allow"` 作为路径边界的元权限，统一由托管配置装配层显式写入各 managed agent 的 Host 配置中，不计入角色业务权限矩阵，亦不得作为普通工具放行。

## [012] 工具名投影唯一写入口 = CanonicalRole → permission；禁止第二套旧名表/手写矩阵

所有角色到工具名称的投影均以 `CanonicalRole → permission` 为唯一写入口；允许 Manager 专用别名在同一写入口内按角色解析。系统严禁引入历史别名兼容表或手工硬编码的工具名子集。

## [013] 权威值严格分为 Evidence / Decision / Witness / Capability / Receipt / PhysicalHandle

六类值的因果职责不可互换：`Evidence` 是已观察输入，`Decision` 是纯分类结果，`Witness` 是带精确对象与版本的证明，`Capability` 是对下一动作的不可伪造许可，`Receipt` 是已受理/已应用结果，`PhysicalHandle` 是当前进程资源。名称相似但只描述工具词汇的类型（例如 `JsCapability`）必须由正向 DSL/manifest 分类为 vocabulary，不得靠名称 allowlist 猜测。

## [014] owner 单点发行；manifest 以 exact file + symbol + source/proof anchor fail closed

每个敏感权威声明由其真实 owner 单点发行，并在其所属 subsystem 的公开端口与类型系统（Evidence / Decision / Witness / Capability / Receipt / PhysicalHandle）中显式定义。各消费点执行 exact subject、版本与必要 digest 的准入核验；严禁 name-only allowlist、baseline 或 suppression。

## [015] authority scope 必须精确绑定 current subject + version/sequence + 必要 digest

Witness、Capability 与 Receipt 的合同必须声明 subject、版本/序列及能区分内容的 digest/hash（若该边界具有内容身份）。Witness 不得直接驱动 append/write/send/execute 等效果；消费者必须先针对当前 subject、当前 version/sequence 与当前 digest 做 fresh admission。Retirement 继续复用 RETIRE 与 Change 门禁的 current request/snapshot/Git tree proof，不另造第二套审查权威。

## [016] freshness 在消费点重验；stale witness 只能产生新的 admission，不能复活旧能力

已记录 witness/receipt 可作为历史证据，但其旧版本不授权当前效果。subject、版本、序列或 digest 变化后，旧 witness 必须被拒绝；若当前事实再次满足条件，owner 从当前观察产生 fresh admission。恢复路径不得隐藏旧 program counter 或以历史能力跳过普通准入。

## [017] multiplicity 明示且一次性能力不可复制消费

每个权威合同必须声明 one-shot、exact-N、set/closure 或 replayable evidence 的 multiplicity。一次性 Capability/permit 的成功消费或 release 原子关闭该次机会；Receipt/Evidence 的可重放读取不等于重复执行效果。

## [018] QuiescencePermit 由 ObserveIdle 唯一发行且消费/释放返回 typed failure

`QuiescencePermit` 绑定 opaque gate owner identity、`SessionId` 与 attempt serial；只有 `SessionQuiescenceGate.ObserveIdle` 可发行。`TryConsume` 与 `TryRelease` 均返回 `Result<unit, QuiescencePermitFailure>`，其中 `QuiescencePermitFailure = WrongOwner | NoFreshIdle | AlreadyConsumed | Superseded | Revoked`。跨 gate 为 `WrongOwner`，重复消费为 `AlreadyConsumed`，更新 attempt 后旧 permit 为 `Superseded`，revoke 后为 `Revoked`，drop 或无 eligible idle 为 `NoFreshIdle`；每个 `Error` 分支零效果。JS surface 只投影稳定 typed result view，permit 保持 opaque。

## [019] process capability/permit/PhysicalHandle 非耐久；重启后由当前事实 fresh admission

进程 capability、permit 与 `PhysicalHandle` 禁止进入 Fact/Event、journal codec、JSON 或任何跨进程恢复载荷。崩溃后可恢复 durable Evidence/Receipt，再经普通 owner admission 发行当前进程的新能力；不得持久化 capability、读取 feature history 推断能力、或设置隐藏 recovery PC。Quiescence 恢复必须重新经历当前 physical attempt 的 `ObserveIdle`，不得消费崩溃前 permit。

## [020] 配置不变量fatal使用mandatory injected fuse

此边界不把普通可预期admission rejection升级为fatal。

## [021] Blogger 与 Chronicle 修复机制已退役

`Role.Blogger`、`chronicle` 工具及 `BloggerCoordinator` 已整体废弃；系统不再保留 Blogger repair episode 或 `ToolPermission.Chronicle`。

## [022] Fission 仅准入已证明的 CanonicalRole 为 Engineer，其他角色与内部身份一律在运行入口与门禁 fail-closed 拒绝

Fission 能力在运行入口（Admission Gate）的准入逻辑严格限定为：
```text
允许 Fission = 已证明的 CanonicalRole 为 Engineer
             ∧ 本次执行授权包含 Fission
             ∧ 已有 subsession 来源条件成立
             ∧ 没有活跃 Fission group
             ∧ 其他现有准入条件成立
```
唯一可赋予 Fission 的角色是 Engineer。Manager、Orchestrator、DevOps、Plan、Bookkeeper 以及任何未决或内部辅助身份调用 Fission 时，必须在运行时入口与 ToolRegistry 门禁直接拒绝（fail-closed），绝不只从 Schema 隐藏。

## [023] DevOps 角色固有直接源码修改与测试编写权能，禁止 allowRepair 等逐次授权开关

DevOps 角色原生具备完整的本地工程文件操作能力（Read, Write, Edit, Glob, Grep, Move, Remove）与真实执行能力（Exec），经 `todowrite-for-devops` 编程面（`cat`/`ls`/`stat`/`where`/`whereAll`/`mkdir`/`write`/`mv`/`rm`/`run`/`join`）统一投影，不另设直接文件 Host 工具。在执行验证过程中遇到非架构级缺陷时，DevOps 拥有直接调查、修改源码、补充回归测试并重新验证的固有法定授权。系统严禁引入 `allowRepair`、`managerApprovedMutation`、`canFixAfterFailure` 等逐次审批参数，亦不得保留「只有 Manager 明确允许才可以改源码」的提示词暗门。

## [024] Fork 与 Resume 权能分离：Manager fork 仅准入 Engineer，固定 DevOps 仅允许经 resume 续做且同一道路至多一个活跃工作单元

`fork` 与 `resume` 在模型参数、生命周期与门禁上严格分离：
- `fork`：仅允许 Manager 创建新的独立 Engineer 子会话；严禁 fork DevOps 或 fork 其他角色。
- `resume`：用于 Manager 续做既有固定 DevOps 道路；同一道路同时至多允许一个活跃的 DevOps 工作单元。DevOps 忙时明确拒绝新的 assignment，不将新任务伪装为 nudge，亦不得通过新建操作员绕过忙碌状态。

## [025] Manager 评审专用只读工具固定可见与当前事实收口

Manager 评审专用只读工具（`todowrite-for-manager`）在模型 Schema 中固定可见；运行时由当前事实收口准入门禁：已接纳评审且仍有 findings、退任冻结或存在清理阻塞时拒绝执行。已接纳空集 findings 的末任 Manager 保留只读收尾能力，可读、清理并收口，但不得开新工作。已退役的直接文件工具（`read`/`grep`/`glob` 等）不进入 Host 工具目录且由默认 `* = deny` 与角色门禁拒绝；`todowrite-for-engineer`/`todowrite-for-devops` 对 Manager 的跨角色绕路调用始终拒绝。工具名到既有语义权限的映射必须维护在唯一目录中，禁止按后缀或启发式规则推测授权。

## [026] Review 接纳前禁止向固定 DevOps 派工

在当前迭代的 Review 被系统接纳前，严禁 Manager 向绑定的固定 DevOps 派发任何任务；已有只读 Engineer 的合法 `resume` 与 `fork` 只读调查不受误伤。对未接纳评审前向 DevOps 派工的拦截与拒绝，必须发生在任何持久化副作用之前。
