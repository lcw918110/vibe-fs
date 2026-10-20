# session-ontology — WHAT

## [001] 容器分类与参与者身份分离

managed session 分类只描述物理容器的执行能力与归属，不表示 logical participant run 或 `ParticipantIdentity`。

## [002] ExecutionClass 与 Ownership 为穷尽正交组合

每个 managed session 恰属 `Work | InternalLeaf` 与 `Root | Attached` 的四种正交组合之一。`Attached` 恰有一个 `ownerSessionId` 和一个 `AttachmentKind`。

## [003] Dedicated SyncInspector 与 SyncCoder 属于 Work + Attached

Dedicated SyncInspector 与 SyncCoder 属于 `Work + Attached`，具备完整的执行能力路径与上下文支持，可拥有独立的 Companion，不得实现为无 Companion 的 InternalLeaf。

## [004] Companion 与 Bookkeeper 属于 InternalLeaf + Attached

Companion 与 Bookkeeper 属于 `InternalLeaf + Attached`。不存在只读副本这种会话种类。InternalLeaf 不得拥有任何 Attached 实体，包括 Companion 和其他内部叶子。

## [005] Attached 节点单一 Owner 且禁止自链

Attached session 只有一个 owner，且不得以自身为 owner；冲突归属与自链均拒绝。

## [006] 物理 Host Parent 恒为 Family Root 且逻辑归属由 Journal 承载

所有 managed child 在 Host 中直接挂在 family root 下。逻辑归属只由持久化关联事实决定，不从物理 parentID 推断；物理父节点也不构成 Role、Persona、身份来源或继承证据。对于空本地映射的已恢复会话或深层逻辑 owner，创建托管子会话与列表子节点时必须权威查询 Host 物理父链解析真实 Family Root（查询失败或检测到环路一律安全失败拒绝创建，禁止无证猜测 root）；托管子会话列表亦在真实根节点下查询物理子节点，保证平坦拓扑一致。

## [007] Durable 关联事实与正交分类派生视图解耦

持久化关联是最小事实，`ExecutionClass × Ownership` 是由这些事实只读派生的视图；不得反向改写关联或另建身份状态。

## [008] 关联写操作不变量与原子拒绝集

关联写入原子校验：自链、向 Companion 递归附挂、替换有效 Companion、抢占其他 Work 的 Companion 或将同一 child 注册为冲突种类时，全部拒绝且不改变已有事实。同一 owner、child 的重复链接幂等。

## [009] Work Root 唯一 Companion 规则

`Work + Root` 主会话至多且恰有一个 Companion（且 ID ≠ owner）；`Work + Attached` 的 Companion 为可选；未记录仅代表未延迟初始化，绝不表示匿名或未绑定状态。

## [010] Runtime 拓扑不决定业务分类与角色

容器分类不由 Role、Persona、工具或 logical run 决定，Companion 资格不设角色白名单。分类、关联、Session 缓存和 Host parent 也不得生成或修改 `ParticipantIdentity`。

## [011] 不存在可跨决策复用的只读副本会话

只读副本不是会话种类，不得登记为 InternalLeaf、持久 satellite 或常驻副本。

## [012] Bookkeeper 绑定具体 TransactionId

Bookkeeper attachment 显式绑定目标 transactionId，只用于该事务的临时取数审计，不与 Companion 或同步受托身份混用。

## [014] 单一平坦拓扑与合法角色边界

系统运行拓扑严格限定于当前合法角色集合及其正交的执行类；任何解析、映射及运行时均严格保证拓扑平坦与单一职责，拒绝任何未预期的分层或复合拓扑。

## [015] SessionId 是可复用物理容器，不是 identity scope

`SessionId` 只命名可复用的物理容器，身份作用域与复用准入遵循 participant-identity-009。容器分类和关联不缓存或解析身份、不发布 run closure；移除关联、detach/attach、分类变化、idle/timeout 或 Host 观察均不代表 lifecycle terminal 或 closure。
