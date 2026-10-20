# participant-identity — WHAT

## [001] `ParticipantIdentity` 是 logical participant run 的唯一私有身份 owner

每个 durable logical participant run 恰有一个私有强类型 `ParticipantIdentity`。它原子包含 `Role`、稳定 `Persona` 与 Persona provenance/version；字段不得被其它包分拆拥有或独立改写。该 identity 在 exact run 内不可变，不以 `SessionId` 生命周期为作用域。Role 是本名词汇（Manager/Orchestrator/Engineer/DevOps/Plan），每个 Role 在运行时恰对应一个 Persona（如 Engineer 为 Engineer，Manager 为 Lead，Orchestrator 为 Director，DevOps 为 Operator，Plan 为 Planner）。Engineer 是独立真身份，拥有本地事实调查与源码读写实现权能，绝非其他角色的别名或 Persona 包装。活跃身份集合与调度路径仅包含当前合法活跃角色。Bookkeeper 沿用内部身份边界用于案例维护，不参与普通调度、工具门禁与公开 fork 候选；Blogger 与 Predictor 已彻底废弃。

## [002] ParticipantIdentity ≠ ExecutionBinding

`ParticipantIdentity` 表达由 `IdentitySeed` 派生并在 logical participant run 内不可变的参与者身份：`Role`、`Persona` 与 `SelectedAgent` 是 canonical identity evidence。系统中不存在 `PeerAgent`，亦无游标选择的 `EffectiveAgent` 轮换。`ExecutionBinding` 表达某次具体物理执行的动态分配：每个 fresh physical execution 将固定的 Role 经 MJS scheduler 路由至 model target，并签发租约与容量栅栏（capacity exact identity 严格绑定为 `session + physical + Role + Participant + target + fence`）。重试或显式执行调度只能按固定 Role 申请新物理执行的 model target/lease binding，不得把物理目标或租约回写为 identity，亦不得轮换或变更角色身份。StrengthReplica、只读副本与 Predictor 不是执行形态。

## [003] Persona 由 Role × versioned provenance resolve-once

logical participant run 建立时，identity owner 以 `Role × persona provenance/version` 解析一次完整 `ParticipantIdentityEvidence`。该 evidence 包含 Role/Persona/SelectedAgent evidence 且在 run 内不可变，只有作为同一个 durable `AuthorityRootAccepted` payload 的必填字段被原子追加后才算安装；禁止先追加独立 identity-installation fact，再追加可失败的 root acceptance。任一追加失败时 identity 与 root 均未安装；相同 acceptance payload 的重放幂等，run 内不同 payload 一律拒绝。

## [004] 换执行者 ≠ 换人

物理执行重试（Retry）与模型目标重绑仅改变物理目标与租约 binding（通过 MJS scheduler 为固定 Role 重新分配 model target/lease）；严禁 PeerAgent 与 side/cursor 轮换。执行期间暴露的 Role、Persona、SelectedAgent 与 provenance/version 必须逐字段等于该 run durable 的 `ParticipantIdentityEvidence`；它们不得被分配的 provider/model 目标或租约覆盖。

## [005] system prompt identity 只消费 ParticipantIdentity

system prompt 的身份标识由 `ParticipantIdentity.Role` 与其稳定 Persona 产生，不读取当前 EffectiveAgent、provider 或 model。机器执行切换不得改变同一 run 的身份 prompt 标识。

## [006] 稳定 Role identity 与可变机器 binding 严格分界

`Role`、`Persona` 与 `SelectedAgent` 是稳定的 canonical identity evidence，由 `IdentitySeed` 派生且不可变，不是当前机器 binding。物理执行分配的 model target、fence 与租约标识仅属于 `ExecutionBinding`，不得覆盖 Role/Persona/SelectedAgent evidence。系统中不存在 cursor-selected EffectiveAgent 及 PeerAgent 语义。

## [007] 内部身份仍受同一原子模型约束

Bookkeeper 等内部 logical participant run 同样拥有机器身份可见性之外的私有 `ParticipantIdentity` 与稳定 Persona；其内部 Role 绝不进入公开 `Role` 联合类型或 Manager 的公开 fork 候选。内部身份不得拆成独立 Persona 缓存。Bookkeeper 仅在案例维护机制中使用，不暴露给普通 participant 调度与公开工具门禁。

## [008] 派生 root 只能安装显式 owner-derived identity evidence

child、attached 与 InternalLeaf 的 root 必须携带 identity owner 为 exact logical participant run 签发的 typed owner-derived evidence（`IdentitySeed` 派生）；该 evidence 原子命名 OwnerLogicalRunId、LogicalRunId、Role、SelectedAgent、稳定 Persona 与 provenance/version（不包含 PeerAgent 或可变 EffectiveAgent）。只有 owner、run 与 root acceptance 全部精确匹配时，它才可进入原子 `AuthorityRootAccepted` payload；wrong-owner、wrong-run 与字段缺失均 fail-closed。后续物理执行的 model target 由 MJS scheduler 独立裁决并绑定 target/lease。Persona 继承关系只能由该 evidence 证明，严禁根据 Session 缓存、Host physical parent 或其它物理拓扑推断、补全或重新解析。

## [009] exact prior-run closure 后才可在同一 SessionId 安装 fresh identity

`SessionId` 可复用为物理容器。同一 SessionId 上存在未精确关闭的 logical participant run 时，任何不同 identity 或 fresh root 必须 fail-closed。只有 interaction-authority 为 exact `(SessionId, LogicalRunId, AuthorityRootId)` 持久化唯一 `AuthorityLogicalRunClosed`，并由同一 fold 释放该 run 的 active identity binding 后，fresh root 才可通过新的原子 `AuthorityRootAccepted` payload 安装全新的 `ParticipantIdentity`。新身份不得继承旧 run 的缓存字段；lifecycle terminal、association removal、时间、idle/timeout 或 Host 观察均不得单独推断 closure。

## [010] 活跃身份解析与历史身份隔离解码

活跃身份解析（如通过名字解析或新建 root）严格限定于当前合法活跃身份集合（`engineer`、`manager`、`orchestrator`、`devops`、`plan`）；任何非此集合的身份在活跃调度路径中均被严格拒绝。历史事件、日志与归档中的身份必须隔离在历史解码边界，严禁在读取或恢复时将只读历史身份静默自动升级为具备源码写入权限的 `engineer`，亦严禁将 `devops` 误解析为具备 Fission 权能的 `engineer`。
