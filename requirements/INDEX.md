# Package index

当前索引包含 **50 个活跃规范包**与 **2 个历史包**（epistemic-reasoning、institutional-learning）。包数不是目标，也不是稳定 API；后续按独立 WHY、失败含义与独立变更边界继续核对拆并。

## 1. Requirement system

| Package | 一句话 WHY |
|---|---|
| `requirement-system` | 当前接受的产品真理必须有唯一 package owner、显式依赖与唯一 proof ownership。 |
| `verification-system` | requirement acceptance 必须由分层、可失败、可重放的证据体系定义，而不是测试类型或人工印象。 |
| `feature-ablation` | 巡检与渐进验收需要节点注册表、三态语义、消融 DAG 与配置集，使下游未审机制可零影响关停而不改源码。 |
| `js-semantic-surface` | 语义测试只能经正式、稳定、JS-native 的 semantic surface 进入；Fable runtime representation 不属于 semantic contract。 |

## 2. Programming / causality

| Package | 一句话 WHY |
|---|---|
| `structured-workflow` | 业务流程应由宿主语言结构直接表达，不能在领域层再造第二程序计数器/runtime。 |
| `time-capability` | 时间与等待的物理能力必须显式进入系统，不能由 ambient clock/timer 偷渡业务判断。 |
| `causal-wait` | 等待必须可诊断、可观测，但诊断观测不能升级为业务 authority。 |

## 3. Session / Host substrate

| Package | 一句话 WHY |
|---|---|
| `session-ontology` | execution class、ownership、attachment 与 personhood 必须正交，否则 runtime topology 会冒充业务身份。 |
| `managed-session-lifecycle` | managed session 的创建、复用、取消、retire、replacement 与 owner closure 必须有单一生命周期合同；旧身份显式收束，固定 DevOps 崩溃恢复维持单一执行权威。 |
| `host-boundary` | 外部 Host 只有提供一组最小、可验证的物理能力与稳定观察边界，业务语义才不依赖传输噪声或私有实现。 |

## 4. Participant / provider world

| Package | 一句话 WHY |
|---|---|
| `participant-identity` | Role、Persona、ExecutionBinding 必须分离，使换执行者不等于换人；活跃身份解析与历史身份隔离解码。 |
| `execution-model-routing` | 固定 Role 身份与物理模型策略必须分离；唯一 MJS scheduler 以 `role + running` 决定 ModelTarget，固定 DevOps 模型绑定持久锁定。 |
| `office-capability` | office 必须由有资格产生的后果定义，而不是 persona 名或工具白名单；确立 Engineer（独占 Fission）、DevOps（固有自修与真实执行）、Manager、Orchestrator 四大核心角色。 |
| `capability-enforcement` | provider 看见的 capability 与 runtime 真能执行的 capability 必须同源且不扩大 office entitlement；Fission 独占 Engineer，DevOps 固有自修无需逐次开关，Fork/Resume 权能分离。 |
| `participant-horizon` | machine knowledge 大于 participant experience；只有会改变合法行动的最小事实应穿过 horizon；Manager 并行来自派出多名 Engineer 而非自身分身。 |
| `advisor` | 顾问是按需请来的第二意见：同一 session 专用、无工具的评审参与者，读调用者交出的 view 与 intent 只回散文，不持有任何工具、不产生授权副作用。 |
| `cognitive-environment` | 世界观、身份、自我职责与继承知识必须按稳定认知层组织，瞬时 runtime/mission 不能伪装成长期身份。 |
| `cognitive-workspace` | 持久认知画板已退休；只保留负向边界，禁止 jq/canvas、TodoSink 与 Assume durable runtime 回流。 |
| `attention-regulation` | `enough`、`abandon` 与 `defer` 工具及 `Attention` 持久投影已退休；注意力动作并入 `assume` 与编程面 ANCHOR `view.thenRemove` 摘要，本包只保留负向边界。 |
| `action-affordance` | participant 在采取一个 action 的决策点必须知道该 act 的正边界、负边界、成功后果与参数意义。 |
| `provider-language` | 语言由全局设置唯一决定，无会话绑定与持久化记录，改设置即改下一次渲染的语言；protocol identity 保持语言不变，核心角色双语 Prompt 语义同源一致。 |
| `provider-projection` | 已决定可见的 typed semantic intent 必须经唯一确定性投影变成 provider representation，表示不能反向创造 authority。 |

## 5. Interaction / effect / durability

| Package | 一句话 WHY |
|---|---|
| `concern-routing` | concern-addressed 邮箱（`subscribe`、`publish`、自动订阅与 Pair Hint 消息捎带）已退休；跨参与者与用户交互并入编程面 `js.fork`/`js.join`/`js.deliver`/`js.ask`，本包只保留负向边界。 |
| `interaction-authority` | 物理 user-shaped message 不等于 authority；历史事件保持原样且旧身份不升权，DevOps 恢复与续行锁定固定模型与单一执行权威。 |
| `managed-chat-execution` | 每个物理消息的 durable acceptance、provider start、唯一终态与 exact settlement 必须由消息级执行 owner 统一管理。 |
| `dispatch-protocol` | 已获授权的 interaction 穿过不可靠 Host 时必须避免 uncertain outcome 复制逻辑效果。 |
| `durable-events` | durable truth 必须以不可变事实、原子提交与确定性 fold 形成单一可重放 substrate。 |
| `effect-accounting` | 外部副作用的请求、物理发生与确认必须分型；unknown outcome 不能伪装成未发生或成功。 |
| `durable-convergence` | 多个各自合法发展的 durable replicas 必须按对象语义收敛，而不是靠 wall-clock/LWW 猜赢家。 |

## 6. Work / execution

| Package | 一句话 WHY |
|---|---|
| `delegation` | 一项语义工作交给另一 participant 时，authority、charge、owner 与返回后果必须明确；Manager 派发 Engineer 与续做固定 DevOps，禁止跨角色向后差遣；Sphinx 程序内部同步调用标准 Engineer，并压平为同级子会话。 |
| `advisor` | 同一调用 session 的 advisor 请求永远路由到同一个按需建立的顾问专席，父级是家族根而不产生 sub-subagent；请求由顾问 law、顾问自有 TOML view 与独立 intent 三条不合并的 prompt 组成，follow-up 只追加 view 增量与新的 intent；顾问无任何工具、只回散文，`js.advisor` 以数据信封返回且不介入调用者授权。 |
| `intra-participant-parallelism` | 同一个 participant（仅限 Engineer）可拥有多个 coequal execution presents，而 identity/authority/responsibility 与最终 completion 仍保持一个。 |
| `process-execution` | participant 控制真实进程/PTY 时必须得到有界、可终止、物理完成可信的 execution semantics，承接大输出零 Distiller 留尾截断与 Large Gate 门禁。 |
| `change-integration` | 独立 Git 工作道路进入共享 ref 时必须在短原子门内发布，长 review/repair 不应被全局串行化；DevOps 自修推进快照触发证书失效与独立重评。 |

## 7. Context continuity

| Package | 一句话 WHY |
|---|---|
| `semantic-trace` | participant life 中不可丢失的原始语义历史必须有 append-only、可定位的事实表示；Fission 多 present 确定性 keyed 汇聚，独立 invocation 范围与 resume 边界隔离。 |

| `context-compression` | 当历史过长时，只能以受控、证据边界明确的 semantic memory 替代可压缩部分。 |
| `prefix-stability` | 同一 semantic epoch 内已呈现给 provider 的前缀必须保持稳定；冷边界只能由事实驱动。 |

## 8. Failure / recovery

| Package | 一句话 WHY |
|---|---|
| `execution-failure-policy` | 执行失败必须先收敛为封闭类型，再由唯一纯策略一次性裁决 retry、fallback、capacity、message 与 fatal 后果。 |
| `provider-attempt-recovery` | 单次 provider attempt 已失败后，可在不改变 authority/personhood 的前提下有界换执行绑定继续。 |
| `host-provider-failure-ownership` | 万象术启用时无条件拥有 provider 失败恢复；Host 重试归零，claimed 错误抑制默认弹窗并由万象术逐 provider 恢复。 |
| `crash-reconciliation` | 进程/插件中断后只能从 durable facts 与可信物理观察重新进入普通程序，不能从临时内存或猜测恢复；固定 DevOps 崩溃恢复保持单一执行权威与命令去重；进程本地注册表只是缓存，存在性判定以 durable 投影为准。 |
| `degeneration-guard` | 尚未结束的 attempt 若 token 多样性越出正常语料经验边界，应在污染更多历史前主动终止并由本包自行要求改写。 |

## 9. Mission / work

| Package | 一句话 WHY |
|---|---|
| `obligation-ledger` | 宿主待办与 compression checkpoint 归编程面 `todowrite-for-<role>` 的非空 `view` 声明；插件不改写工具定义与参数，只按 Host 终态追加 checkpoint，不维护第二份 todo 真相。 |
| `work` | Work 生命周期只有一套事件：S1–S3 一段 deliver，S4 两段 deliver；结果只有 Relayed 与 Delivered，评审只有 Promoted 与 Stayed；后继剥除前任 assistant 与 tool，交棒由前任声明的 ANCHOR 视图承担，不注入机器物化的合成记录。Plan 的编程面、devops 调查、任期隔离、阶段资源、review 与持权交付，以及 Manager/Plan 禁止静默停止、ask 显式用户等待同属本包。 |




## 11. Repository knowledge / programming

| Package | 一句话 WHY |
|---|---|
| `repository-investigation` | repository claim 必须由可定位、可追溯的真实观察建立，reasoning 不能冒充 evidence acquisition。 |
| `knowledge-reuse` | 过去的 repository knowledge 可作为 best-effort cache/hint 复用，但不能冒充当前证明；双基线引用与真实 diff 驱动 Bookkeeper 刷新，废除严格 replay 循环。 |
| `repository-programming` | repository 变换需要能力投影、可组合、sandboxed、As-Is 逐笔落盘的 programming surface，而不是多套漂移 RPC；事务 ReadSnapshots 与案例实质访问严格分离，直接文件工具已退役，编程面是唯一文件契约。 |

## 12. Optional optimization / epistemics

| Package | 一句话 WHY |
|---|---|

| `epistemic-reasoning` | 已由 sphinx-v2 取代的历史包；保留设计沿革和旧证据，不参与当前验收。 |
| `sphinx-v2` | clean-break 后的 Sphinx 内核：LLM 负责语义判断，程序负责科学问法、作用域估值与资源内调度；旧手写语义评分表整类删除；WP-039 后收敛为 MCP-only 单一入口，原生工具面不恢复。 |

## 13. Delivery

| Package | 一句话 WHY |
|---|---|
| `distribution` | 可安装 artifact 必须携带运行所需代码与 semantic resources，同时排除不属于交付面的源码/开发资产；打包资源与活动注册严格同步。 |

# 规范条款索引

本节汇总活跃条款，历史包单独标记；以各包 `WHAT.md` 实际文本为准。序号只作导航，不是人工巡检站号。

| 序号 | 规范包 (`Package`) | 活跃条款数 | 活跃条款清单与演进导航 |
|---|---|---|---|
| 1 | `requirement-system` | 13 | requirement-system-001 ~ 008、010 ~ 011、015、017 ~ 018 |
| 2 | `verification-system` | 20 | verification-system-001 ~ 012、014 ~ 021 |
| 3 | `feature-ablation` | 5 | feature-ablation-001 ~ 005（节点注册表、三态语义、消融 DAG、配置集、编译分片注册） |
| 4 | `js-semantic-surface` | 6 | js-semantic-surface-001 ~ 006 |
| 5 | `structured-workflow` | 19 | structured-workflow-001 ~ 019 |
| 6 | `time-capability` | 8 | time-capability-001 ~ 008 |
| 7 | `causal-wait` | 9 | causal-wait-001 ~ 009 |
| 8 | `session-ontology` | 14 | session-ontology-001 ~ 012、014 ~ 015 |
| 9 | `managed-session-lifecycle` | 28 | managed-session-lifecycle-001 ~ 022、managed-session-lifecycle-023（身份替换后旧活跃会话显式收束）、managed-session-lifecycle-024（固定 DevOps 崩溃恢复单一权威与进程排空）、managed-session-lifecycle-025（固定 DevOps 每次工作返回时 PTY 进程彻底收束与记账清理）、managed-session-lifecycle-026（Main 收束级联 Attached InternalLeaf 的 execution 结算）、managed-session-lifecycle-027（Session 收束的作用域保留取消与延后归还）、managed-session-lifecycle-028（子完成投递必达与 join guard 有出口） |
| 10 | `host-boundary` | 33 | host-boundary-001 ~ 031、host-boundary-032（Contract 提示字段解耦与参数清理安全）、host-boundary-033（旧租约保持到可见输入进入下一步与流式/非 join 工具保留） |
| 11 | `participant-identity` | 10 | participant-identity-001 ~ 009、participant-identity-010（活跃身份解析与历史身份隔离解码） |
| 12 | `execution-model-routing` | 20 | execution-model-routing-001 ~ 017、execution-model-routing-018（新角色集合模型路由解耦）、execution-model-routing-019（固定 DevOps 模型绑定持久性与禁止借 resume 换模型）、execution-model-routing-020（多席位路由与外部占位模型被租约覆盖） |
| 13 | `office-capability` | 13 | office-capability-001、003 ~ 007、011 ~ 012、015 ~ 016、office-capability-017（DevOps 执行与固有非架构级自修授权）、office-capability-018（Sphinx 程控探究与内部标准 Engineer）、office-capability-019（Plan 规划角色与四工具权限集） |
| 14 | `capability-enforcement` | 26 | capability-enforcement-001 ~ 026（008 四层同构、021 已退役修复机制不得恢复为运行时分支、022 Fission 仅准入已证明 Engineer 均在本包 WHAT 内） |
| 15 | `participant-horizon` | 15 | participant-horizon-001 ~ 014、participant-horizon-015（Manager 并行来自派出多名 Engineer 而非自身分身） |
| 16 | `cognitive-environment` | 15 | cognitive-environment-001 ~ 013、015 ~ 016 |
| 57 | `cognitive-workspace` | 10 | cognitive-workspace-001 ~ 010 |
| 17 | `attention-regulation` | 6 | attention-regulation-001 ~ 006 |
| 18 | `action-affordance` | 15 | action-affordance-001 ~ 015 |
| 19 | `provider-language` | 13 | provider-language-001 ~ 013 |
| 20 | `provider-projection` | 14 | provider-projection-001 ~ 014 |
| 21 | `concern-routing` | 7 | concern-routing-001 ~ 007 |
| 22 | `interaction-authority` | 23 | interaction-authority-001 ~ 020、interaction-authority-021（历史事件不可变与旧身份不升权）、interaction-authority-022（DevOps 恢复与续行锁定固定模型与执行权威）、interaction-authority-023（ProviderRetryAttempt 的 repair 抑制随 attempt 终结而失效） |
| 23 | `managed-chat-execution` | 15 | managed-chat-execution-001 ~ 014、managed-chat-execution-015（Provider 启动边界拒绝的精确定夺） |
| 24 | `dispatch-protocol` | 15 | dispatch-protocol-001 ~ 015 |
| 25 | `durable-events` | 28 | durable-events-001 ~ 028 |
| 26 | `effect-accounting` | 10 | effect-accounting-001 ~ 008、010、012 |
| 27 | `durable-convergence` | 11 | durable-convergence-001 ~ 011 |
| 28 | `delegation` | 30 | delegation-001 ~ 015、017、019 ~ 032 |
| 58 | `advisor` | 6 | advisor-001 ~ 006（专席归属与家族根父级、law/view/intent 三材料独立 prompt、view 增量与已交付游标、无工具散文、无授权副作用、`js.advisor` 编程面成员） |
| 29 | `intra-participant-parallelism` | 16 | intra-participant-parallelism-001 ~ 015、017 |
| 30 | `process-execution` | 17 | process-execution-001 ~ 017；通用工具结果边界归 host-boundary |
| 31 | `change-integration` | 14 | change-integration-001 ~ 009、011、013 ~ 015、017 |
| 32 | `semantic-trace` | 12 | semantic-trace-001 ~ 010、semantic-trace-011（Fission keyed convergence 与多 Present 轨迹归并）、semantic-trace-012（独立 Invocation 范围与 Resume 边界） |

| 34 | `context-compression` | 31 | context-compression-001 ~ 027、context-compression-028（逐次 view checkpoint K 窗口）、context-compression-029（落后不丢 raw）、context-compression-030（assume call/result 原文保留）、context-compression-031（ANCHOR 编址、视图声明与 token 经济） |
| 35 | `prefix-stability` | 15 | prefix-stability-001 ~ 015；view checkpoint 窗口见 context-compression-028 |
| 36 | `execution-failure-policy` | 14 | execution-failure-policy-001 ~ 014 |
| 37 | `provider-attempt-recovery` | 24 | provider-attempt-recovery-001 ~ 023、provider-attempt-recovery-024（已接受但未启动的执行必须定夺） |
| 38 | `host-provider-failure-ownership` | 7 | host-provider-failure-ownership-001 ~ 007 |
| 39 | `crash-reconciliation` | 21 | crash-reconciliation-001 ~ 019、crash-reconciliation-020（固定 DevOps 崩溃恢复单一逻辑权威与命令去重）、crash-reconciliation-021（进程本地表是缓存，durable 投影是存在性真源） |
| 40 | `degeneration-guard` | 13 | degeneration-guard-001 ~ 013 |
| 41 | `obligation-ledger` | 7 | obligation-ledger-001 ~ 007 |
| 42 | `work` | 50 | work-001 ~ 050（011 起是内核合同；020 起是 Plan 编程面、devops 调查、任期隔离、阶段资源、review 与交付；027 禁止长程静默停止；040 起是唯一任期、DevOps 绑定、资源交接、durable 回执及恢复；049 是 ask 显式人类等待；050 是 Plan 编程面的 js.ask 与 S1 门禁） |


| 48 | institutional-learning（已退役） | 0 | 随 WP-036 整包退役，目录与条款已删除 |
| 49 | `repository-investigation` | 9 | repository-investigation-001 ~ 009 |
| 50 | `knowledge-reuse` | 16 | knowledge-reuse-001 ~ 016 |
| 51 | `repository-programming` | 60 | 原 001 ~ 027 不变；028 起 programPromise/deadline 可重置、029 起 As-Is 逐笔落盘与失败清单形态、030 ~ 036 park/unpark/abandon 全周期、037 ~ 039 Parked 渲染/投影/现场表、040 ~ 044 rm/mv POSIX 与双语四层接线、045 ~ 047 九个文件原语、where 歧义即 park 与组合教学、048 ~ 049 parked source 与 unpark 自由值、050 ~ 052 js.call 宿主循环/多进程代数/wget 断点续传剧本、053 view 声明捕获、054 deliver 显式终值、055 vm 并行边界、056 委托原语 fork/join/fission/dag（全部返回数据信封，dag 节点失败 park）、057 ls 的 ignore 语义与 glob/grep 退役、058 js.call 的交互形态与唯一执行面、059 js.deliver 的终值记录、060 "js" 机器词汇的终局裁决 |

| 54 | `epistemic-reasoning` | 0 | 旧001 ~ 036仅保留历史，现行替代关系见 sphinx-v2/SUPERSEDES.md |
| 55 | `sphinx-v2` | 36 | sphinx-v2-001 ~ 036（取代 epistemic-reasoning 旧内核条款，关系见 SUPERSEDES.md） |
| 56 | `distribution` | 11 | distribution-001 ~ 011（010 打包资源与活动注册同步、011 安装产物闭包在场） |
| 57 | `advisor` | 6 | advisor-001 ~ 006（001 专席归属与「孙子变儿子」、002 请求三材料不合并、003 follow-up 追加增量、004 无工具散文返回、005 无授权副作用、006 js.advisor 编程面成员） |

# 依赖骨架

这不是权威优先级，只表示定义所需保证。下表保留既有依赖导航并同步本轮明确迁移；新增画板与Sphinx v2的全部语义依赖尚须专门审查，不能将这份导航当作完整架构证明。

```text
requirement-system       → 无
verification-system      → requirement-system
feature-ablation         → requirement-system, verification-system
js-semantic-surface      → requirement-system, verification-system
structured-workflow      → 无
time-capability          → 无
causal-wait              → 无
session-ontology         → 无
managed-session-lifecycle→ session-ontology, crash-reconciliation, managed-chat-execution, interaction-authority, participant-identity
host-boundary            → 无
participant-identity     → session-ontology
execution-model-routing  → participant-identity, managed-session-lifecycle, managed-chat-execution, execution-failure-policy, host-boundary
office-capability        → participant-identity
capability-enforcement   → office-capability, participant-identity, attention-regulation, concern-routing
participant-horizon      → 无
cognitive-environment    → participant-identity, office-capability, attention-regulation, concern-routing
attention-regulation     → participant-identity, cognitive-environment, repository-programming
action-affordance        → office-capability, participant-horizon, cognitive-workspace, obligation-ledger
provider-language        → session-ontology
provider-projection      → participant-horizon, provider-language
concern-routing          → participant-identity, delegation, repository-programming
interaction-authority    → participant-identity, session-ontology
managed-chat-execution   → durable-events, interaction-authority, participant-identity, execution-model-routing, execution-failure-policy, host-boundary
dispatch-protocol        → interaction-authority, effect-accounting, host-boundary, durable-events, managed-chat-execution
effect-accounting        → durable-events
durable-events           → 无
durable-convergence      → durable-events
delegation               → office-capability, session-ontology, managed-session-lifecycle, participant-horizon
advisor                  → office-capability, capability-enforcement, participant-identity, provider-projection, repository-programming, host-boundary, managed-session-lifecycle
intra-participant-parallelism → participant-identity, session-ontology, managed-session-lifecycle, office-capability, capability-enforcement, participant-horizon, process-execution, durable-events, crash-reconciliation
process-execution        → time-capability, host-boundary, participant-horizon
change-integration       → effect-accounting, durable-events, crash-reconciliation
semantic-trace           → durable-events

context-compression      → semantic-trace, provider-projection, obligation-ledger
prefix-stability         → provider-projection, context-compression, provider-language, participant-identity
execution-failure-policy → 无
provider-attempt-recovery→ participant-identity, execution-failure-policy, execution-model-routing, interaction-authority, context-compression, prefix-stability
host-provider-failure-ownership → execution-failure-policy, provider-attempt-recovery, host-boundary
crash-reconciliation     → durable-events, effect-accounting, structured-workflow, host-boundary
degeneration-guard       → interaction-authority, dispatch-protocol, host-boundary
obligation-ledger        → host-boundary, context-compression
work                     → participant-identity, semantic-trace, office-capability, durable-events, prefix-stability

institutional-learning   → 已退役，不再定义当前依赖
repository-investigation → office-capability, participant-horizon
knowledge-reuse          → repository-investigation, durable-events, durable-convergence
repository-programming   → office-capability, capability-enforcement, effect-accounting, durable-events, participant-horizon

epistemic-reasoning      → 历史包，不再定义当前依赖
sphinx-v2                → 取代关系及当前合同见本包，完整依赖待审
distribution             → 特殊：所有声明 runtime resource 的 semantic packages（不获其语义 ownership）
```

Phase E 审计结论：3 条 coupling edge 已删（见 `AUDIT.md` Phase E）：

```text
structured-workflow  → causal-wait         删（CE builder 是实现耦合，非定义前提）
time-capability      → causal-wait         删（deadline 是可选 escape，条件依赖非 hard）

```

旧157条边的审计属于当时基线，不证明本次上游重写后的全图。索引不得恢复已退役包的权威；完整反向覆盖与依赖审查仍须从现行WHAT逐项验证。
