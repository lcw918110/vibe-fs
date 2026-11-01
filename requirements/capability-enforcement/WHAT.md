# capability-enforcement — WHAT


> **COMPAT（2026-10-29）现行口径**：Scope 生命周期入口是任意工具上的打平注解 `scope.kind` ∈ {`resetScope`,`keepLast`,`removeLast`,`meToo`} 与 retention（`wanxiangli/scope-retention` + 共同法 «Scope»）。**无独立 `resetScope` 宿主工具**；档案重锚 = 注解 `resetScope` → `ScopeReset.commit`。**无** `remainingScopeTurnsEstimate`／`turnBudget`／KPI 倍率产品契约；Stall／ResetDue 估计路径已从宿主产品面退役。下文若仍写独占工具、估计必填或倒计时后缀，以本条为准。PairHint 屏障优先认 `scope.kind=resetScope`，旧工具名只读兼容。


## [001] 每次 provider attempt 有一个 canonical ToolCapabilitySet，由 CanonicalRole × RequestKind 唯一决定

每次 provider 请求的可用能力集合（ToolCapabilitySet）由 `CanonicalRole` 与 `RequestKind` 唯一确定，在构建 `AttemptExecutionProfile` 时一处组装。profile 之外不再设独立的权限来源或旁路字段。

## [002] provider-visible schema 与 runtime execution gate 读同一 capability truth

Host 给模型的工具 Schema 与运行时执行 Gate 读同一份能力事实：schema 是固定上限，执行门禁按当前事实判断。两者同源，不表示每轮动态删工具。无权限工具不进 Schema，绕过 Schema 的调用在运行时 Gate 当场阻断。

## [003] capability projection 可按 office + request contract 收窄，但不得扩大 office entitlement

能力投影可以按特定 `RequestKind`（如窄通道）收窄角色固有权限，但投影出的集合必须是 `Roles.permissions(role)` 的严格子集，不得扩大。

## [004] execution tier 不改变同 office 的 authority：同一 CanonicalRole 的权限集与 tier 无关

同一 Office 的工具权限集完全由 `CanonicalRole` 决定，与执行档位无关。执行档位仅代表底层机器与推理深度，不影响权限矩阵。托管 agent 使用 canonical bare 名称 (`manager`/`orchestrator`/`engineer`/`devops`/`plan`/`bookkeeper`)，不再有 `fast-`/`deep-` 前缀；已废弃的 `blogger` 与 `predictor` 不得注册。

## [005] StrengthReplica 与只读副本工具面已废弃

`StrengthReplica` 请求类型与 `todowrite-for-predictor` 已整体废弃，不再为投机调查副本开放专用工具面；未知或已退役请求类型在权限投影与运行时门禁里一律 fail-closed（空权限集）。

## [006] internal-only participants/actions 不进无资格 participant 的工具面

内部专用工具（如 Bookkeeper 的 `todowrite-for-bookkeeper`）仅向对应内部角色开放，普通角色不可见亦不可执行；已废弃的 `chronicle` 工具与 `todowrite-for-blogger` 不再注册。认知与交互效用工具（如 `assume` 等）及 Host 工具（如 `skill`）不属于领域业务权限，不进入 `Roles.permissions`，亦不得借此扩大角色的领域权能。

## [007] Host-native/MCP/plugin 等不同技术来源的 actions 服从同一 semantic capability policy

工具无论来自 Host 原生、MCP 外部集成还是插件内部注册，权限都由同一套领域能力令牌决定。权限只覆盖本地离线能力，任何角色都没有外部网络浏览工具；Sphinx MCP 经程控工作流调用。不为不同技术来源另建权限映射表。

## [008] js-* 编程面四层同构：capability → base-class member → description → example → runtime gate

JS 文件系统能力在四处同构：角色缺对应 capability 时，生成的基类不含该方法、工具描述不提、示例不展示，运行时 Gate 也拦。`todowrite-for-manager` 面向 Manager，只读文件能力为 Read/Glob，另持有委托原语 Fork/Join（无任何写或执行成员），其能力过滤必须到达真实执行 API 层。

## [009] 工具名引用完整性：same tool name → 唯一 schema owner + 唯一 semantic contract

同一工具名只对应一份参数 Schema、一套生命周期、语义动作与返回契约。不同角色不在同一名字下共享有分歧的契约。`fork` 与 `resume` 是不同生命周期契约，各自独立。

## [010] 双层 fail-closed：Role 未定 → 工具集空/拒绝执行；Host 配置异常仍写 deny 默认

角色或 profile 解析不了时，模型可见工具集为空，运行时拦下所有调用。Host 配置校验失败时，先写入全量 deny 默认策略覆盖 Host 的宽松默认，再触发进程级致命错误退出。

## [011] external_directory=allow 是 Host 路径边界元权限：每 managed agent 显式写入、唯一生产写点

`external_directory = "allow"` 是路径边界的元权限，由托管配置装配层显式写进各 managed agent 的 Host 配置；它不进角色业务权限矩阵，也不当普通工具放行。

## [012] 工具名投影唯一写入口 = CanonicalRole → permission；禁止第二套旧名表/手写矩阵

角色到工具名的投影只有 `CanonicalRole → permission` 一个写入口；Manager 专用别名也在同一入口里按角色解析。不引入历史别名兼容表或手写的工具名子集。

## [013] 权威值严格分为 Evidence / Decision / Witness / Capability / Receipt / PhysicalHandle

六类值的因果职责不可互换：`Evidence` 是已观察输入，`Decision` 是纯分类结果，`Witness` 是绑定精确对象与版本的记录，`Capability` 是对下一动作的不可伪造许可，`Receipt` 是已受理/已应用结果，`PhysicalHandle` 是当前进程资源。名称相似但只描述工具词汇的类型（如 `JsCapability`）要在 DSL/manifest 里正名为 vocabulary，不靠名称 allowlist 猜。

## [014] owner 单点发行；manifest 以 exact file + symbol + source/proof anchor fail closed

每个敏感权威声明由真实 owner 一处发行，并在所属 subsystem 的公开端口与类型系统（Evidence / Decision / Witness / Capability / Receipt / PhysicalHandle）里显式定义。消费点按 exact subject、版本与必要 digest 核验准入；不用 name-only allowlist、baseline 或 suppression。

## [015] authority scope 必须精确绑定 current subject + version/sequence + 必要 digest

Witness、Capability 与 Receipt 的合同要声明 subject、版本/序列，以及能区分内容的 digest/hash（该边界有内容身份时）。Witness 不直接驱动 append/write/send/execute；消费者先按当前 subject、version/sequence 与 digest 重新准入。Retirement 沿用 RETIRE 与 Change 门禁的 current request/snapshot/Git tree 判断，不另造第二套审查权威。

## [016] freshness 在消费点重验；stale witness 只能产生新的 admission，不能复活旧能力

已记录的 witness/receipt 可作历史参考，但旧版本不授权当前效果。subject、版本、序列或 digest 一变，旧 witness 就被拒绝；当前事实再次满足条件时，owner 从当前观察重新准入。恢复路径不隐藏旧 program counter，也不用历史能力跳过普通准入。

## [017] multiplicity 明示且一次性能力不可复制消费

每个权威合同要声明 multiplicity：one-shot、exact-N、set/closure 或可重放 evidence。一次性 Capability/permit 被成功消费或释放时，这次机会原子关闭；Receipt/Evidence 可重读，不等于效果可以重复执行。

## [018] QuiescencePermit 由 ObserveIdle 唯一发行且消费/释放返回 typed failure

`QuiescencePermit` 绑定 opaque gate owner identity、`SessionId` 与 attempt serial，只有 `SessionQuiescenceGate.ObserveIdle` 能发行。`TryConsume` 与 `TryRelease` 返回 `Result<unit, QuiescencePermitFailure>`，失败值为 `WrongOwner | NoFreshIdle | AlreadyConsumed | Superseded | Revoked`：跨 gate 是 `WrongOwner`，重复消费是 `AlreadyConsumed`，attempt 更新后旧 permit 是 `Superseded`，revoke 后是 `Revoked`，drop 或无 eligible idle 是 `NoFreshIdle`；每个 `Error` 分支零效果。JS surface 只投影稳定的 typed result，permit 保持 opaque。

**程序待改**：`capability-enforcement` 的 one-shot 读法与 `crash-reconciliation` 006、`dispatch-protocol` 007「确定未接受可归还同一 exact permit」冲突，见 [GAP](../GAP.md) 与 tests/README 的 017 一节。

## [019] process capability/permit/PhysicalHandle 非耐久；重启后由当前事实 fresh admission

进程 capability、permit 与 `PhysicalHandle` 不进 Fact/Event、journal codec、JSON 或任何跨进程恢复载荷。崩溃后先恢复 durable Evidence/Receipt，再经普通 owner admission 发行当前进程的新能力；不持久化 capability、不读 feature history 推断能力、不设隐藏 recovery PC。Quiescence 恢复要重新经历当前 physical attempt 的 `ObserveIdle`，不消费崩溃前的 permit。

## [020] 配置不变量fatal使用mandatory injected fuse

此边界不把普通可预期admission rejection升级为fatal。

## [021] Blogger 与 Chronicle 修复机制已退役

`Role.Blogger`、`chronicle` 工具及 `BloggerCoordinator` 已整体废弃；系统不再保留 Blogger repair episode 或 `ToolPermission.Chronicle`。

## [022] Fission 仅准入已证明的 CanonicalRole 为 Engineer，其他角色与内部身份一律在运行入口与门禁 fail-closed 拒绝

Fission 在运行入口（Admission Gate）的准入条件是：
```text
允许 Fission = 已证明的 CanonicalRole 为 Engineer
             ∧ 本次执行授权包含 Fission
             ∧ 已有 subsession 来源条件成立
             ∧ 没有活跃 Fission group
             ∧ 其他现有准入条件成立
```
只有 Engineer 能拿到 Fission。Manager、Orchestrator、DevOps、Plan、Bookkeeper 与任何未决或内部辅助身份调用时，运行时入口与 ToolRegistry 门禁直接拒绝（fail-closed），不只从 Schema 隐藏。

## [023] DevOps 角色固有直接源码修改与测试编写权能，禁止 allowRepair 等逐次授权开关

DevOps 自带本地工程文件能力（Read、Write、Edit、Glob、Grep、Move、Remove）与真实执行能力（Exec），经 `todowrite-for-devops` 编程面（`cat`/`ls`/`stat`/`where`/`whereAll`/`mkdir`/`write`/`mv`/`rm`/`run`/`join`）统一投影，不另设直接文件 Host 工具。验证中遇到非架构级缺陷时，DevOps 可直接调查、改源码、补回归并重跑。系统不引入 `allowRepair`、`managerApprovedMutation`、`canFixAfterFailure` 这类逐次审批参数，也不留「只有 Manager 明确允许才能改源码」的提示词暗门。

## [024] Fork 与 Resume 权能分离：Manager fork 仅准入 Engineer，固定 DevOps 仅允许经 resume 续做且同一道路至多一个活跃工作单元

`fork` 与 `resume` 在模型参数、生命周期与门禁上分开：
- `fork`：只有 Manager 能创建新的独立 Engineer 子会话；不能 fork DevOps 或其他角色。
- `resume`：Manager 续做既有固定 DevOps 道路；同一道路同时至多一个活跃的 DevOps 工作单元。DevOps 忙时明确拒绝新的 assignment，不把新任务伪装成 nudge，也不靠新建操作员绕过忙碌状态。

## [025] Manager 程序入口常开，仓库访问仅限 Review

`todowrite-for-manager` 对已确立身份的 Manager 固定可见，不因评审阶段、末任或清理窗口整票拒绝；身份、取消、等待与存储等独立边界照旧。仓库访问仅经 `this.reviewOnly.cat/stat/ls/rg` 暴露，无顶层别名；每次操作在 Host 访问仓库前核验当前任期仍活跃、评审尚未接纳、无清理阻塞且未冻结。末任不例外；保存对象或方法引用、挂起恢复与插件重开均不得复用过期授权。拒绝用稳定机器码 `REVIEW_ONLY`，且不读目标文件、不启动 rg。

`where/whereAll` 只处理已有文本，保持顶层；普通计算、view 声明和生命周期成员不受 Review 阶段整票限制。委托、收拢、交付等操作仍各守自身权限。空账首评审或 `Promoted` 只表示具备最终交付资格，不是任务已完成；活跃的末任 Manager 仍有 `Fork` 派 Engineer、`Resume` 续派固定 DevOps、`Join`、ReviewAssessment 与 Finality。不凭 `IsFinalIncumbent` 或空 findings 静态拒绝这些收尾动作。无活跃任期、尚未通过 Review 的 DevOps 请求、资源 blocker 与用户问询等待依旧按既有独立门禁拒绝。其他角色保持原有直接成员和既定权限，共用底层文件实现，不另设 Manager 文件引擎。基类、描述与示例使用同一命名空间，评审前后保持同一工具定义。

已退役的直接文件工具不进入 Host 目录；Manager 不得借用 `todowrite-for-engineer/devops` 绕过能力边界。

`Join` 与 `Horizon` 依据已确立的角色和当前进程的子任务归属准入，不依赖 Work 活跃任期、Review 接纳、清理 blocker 或退任冻结。首次 Review 之前以及收尾期间仍须能观察、等待和收取已经建立的子任务；顶层工具与程序成员不能因是否经过 Work 门禁而产生不同结果。角色未定、用户问询等待、消融及子任务恢复与归属边界仍有效；观察和收拢不创建 Work 任期，也不授予新派工权限。

## [026] Review 接纳前禁止向固定 DevOps 派工

当前迭代的 Review 被接纳前，Manager 不向绑定的固定 DevOps 派发任务；已有只读 Engineer 的合法 `resume` 与 `fork` 只读调查不受影响。这类拒绝发生在任何持久化副作用之前。
