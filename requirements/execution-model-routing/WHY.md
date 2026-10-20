# execution-model-routing — WHY

## 领域动力与核心张力

模型调度的核心张力在于**物理资源占用的真实性**与**资源调度策略的多样性**之间的分离：

```text
Runtime 维护事实    ──► IdentitySeed 确立的固定 canonical role 与 participant、当前已占用租约 multiset running、被原子取代的活跃执行 target previous（余者为 null）
MJS 表达策略        ──► (role, running, previous) -> { model, reasoning } | null
```

如果将调度规则、候选列表、并发容量硬编码在 runtime 内，任何模型池调整或策略变动都会导致产品核心代码修改。反之，若由 runtime 猜测默认模型或允许多处配置覆盖，则会失去单一真相。

`execution-model-routing` 的核心不变量：
- **单一权威**：唯一配置源为 `~/.config/opencode/wanxiangshu.mjs`；缺失时自动原子生成推荐模板。
- **职责划分**：Runtime 仅维护真实 lease multiset 与串行仲裁；选择算法完全交由 MJS 纯函数表达。
- **租约生命周期绑定物理执行**：Managed lease 绑定至 `(SessionId, PhysicalUserMessageId)`，不以 session 为长期独占单位。
- **固定 Role 路由与解耦**：调度输入为由 IdentitySeed 确立且在 logical run 内不可变的 fixed canonical Role，participant 随 acquire 输入显式传递。同一 physical execution 复用原 target 与 fence，新 physical execution 经调度器路由可选取新 target 但绝不改变 participant identity；严格区分本地 participant 与远端 ModelTarget。`previous` 只来自被原子取代的当前活跃执行，其余一律为 null，不存在 session 级 previous 缓存。
- **Capacity 确切身份**：Capacity exact identity 严格包含 SessionId + PhysicalUserMessageId + fixed Role + Participant + target + fence。execution binding 仅改变 target 与 lease，绝不修改 participant identity。借用只能使用 acquire 输入中显式 lender 的 credit。
- **终结证据保守**：只有 Host 明确给出的成功终结理由才能结束 physical execution；`unknown`、`error` 等含混/失败归一化只结束 provider step，不能猜测 material 已永久结束。
- **背压语义**：`null` 表示当前并发占满的等待状态（backpressure），绝非执行失败或错误。
- **容量与绑定解耦**：物理执行绑定（ExecutionBinding）与请求容量令牌（Capacity Token）分离，容量只凭显式 lender 借用并在 step 边界召回。
- **先接受、后占用**：只有 `(SessionId, PhysicalUserMessageId)` 已由 `managed-chat-execution` durable 接受后，才可进入 bounded typed queue 或获取 exact opaque capacity fence；未接受意图不占容量。
- **确切结算**：容量 fence 具有不可伪造 identity 与 epoch，只能被同一次物理 admission 的 settlement 原子消费；失败后果由 `execution-failure-policy` 决定，不由路由器解析错误文本。

## 核心不变量扩充

- **当前角色集合路由**：调度权威以 Engineer、DevOps、Manager、Orchestrator、Plan 为准；当前配置仅要求活跃角色的模型槽位。
- **DevOps 模型绑定持久性**：DevOps 模型在道路初始化时确立并持久化，后续 resume 与恢复必须严格沿用既有绑定，严禁借 resume 换模型。



## 破裂后果

- 配置多源分叉，模型池变更破坏运行时核心代码。
- 租约与 session 错误绑定，导致 idle 会话永久霸占物理容量，或 retry 期间发生模型漂移。
- 混淆本地 participant 身份与远端 ModelTarget，或试图在同一 physical execution 内切换 Role/agent/participant，导致执行身份与容量审计撕裂。
- 凭错误 Role/participant 结算 fence，或凭缺席/错误的 lender 借用信用，导致容量归属错位。
- `null` 被误判为 provider 失败，错误消耗重试预算。
- 跨进程或跨工作区无法共享真实的并发占用视图，导致向底层 provider 超额发牌。

## 边界与关系

- `participant-identity`：提供 CanonicalRole 与稳定 Persona 构成的 canonical participant identity 定义（routing identity 只使用 fixed canonical Role，participant 随 acquire 输入显式传递）。
- `managed-session-lifecycle`：提供 managed session 生命周期边界与销毁信号。
- `host-boundary`：提供 plugin 启动、物理 message/hook 拦截与 Host 消息改写边界。

- 配置未能正确提供活跃角色的模型槽位，阻碍正常部署。
- DevOps 在 resume 过程中发生模型漂移，破坏固定执行权威的模型一致性。

## DEPENDS ON

- `participant-identity`
- `managed-session-lifecycle`
- `managed-chat-execution`
- `execution-failure-policy`
- `host-boundary`

## Physical fatal boundary

Routing owns exact capacity fence与execution binding settlement；Host process fuse不属于router。direct fatal可在lease仍active或settlement unknown时终止，留下无法区分的capacity world，并允许Host与router双重报告。
