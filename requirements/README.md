# Requirement Packages

> 万象术的**正式 normative 语义树**。每个 `<package>/` 是一个语义 owner，持有不可替代的 WHY、
> 唯一拥有的 WHAT 命题、显式 hard dependencies，以及 package-local 的可执行 proof。
> 全部 packages 同时为真；dependency 只表示 guarantee consumption，不表示优先级。

本树由旧 docs/、旧 changes/、`src/` 综合迁移而来（2026-08-14 cutover，非机械改名）；
旧 `tests/` 已全部分包（2026-08-14 Wave 2a/2b cutover）：测试全部包自有
`<package>/tests/`，共享 harness（support adapters、unit runner、integration
orchestrator、Long Stroke e2e）归 `verification-system/tests/`。每个包目录：

```text
WHY.md      不可替代的存在理由（保姆级）
WHAT.md     唯一 normative 合同（编号命题，对应同编号测试）
tests/      本包拥有的可执行 proof（NNN.test.mjs）
APPLIES-TO  可选的包外正向代码覆盖声明；仓库根相对、gitignore wildmatch 语法，普通行=纳入，!行=排除例外
```

`requirements/<package>/` 下的全部内容天然属于该 package 的覆盖范围，包括文档、tests 与
`APPLIES-TO` 本身；包自身路径的覆盖规则由 `requirement-system` 定义。

`APPLIES-TO` 不改变 package 的语义所有权，也不是新的 normative 文档。它只补充声明“这个
package 还覆盖哪些包外仓库路径”。同一个源码文件可以同时命中多个 package；未提供该文件的
package 仍天然覆盖自身目录，只是不额外关联包外路径。自动路径关联与 read-equivalent provider
grounding 由 `requirement-system` package 拥有。

已知 proof gap 聚合台账见 [GAP.md](GAP.md)；包清单与依赖骨架见 [INDEX.md](INDEX.md)。

## 50 包索引

### 1. Requirement system
| Package | 一句话 WHY |
|---|---|
| [requirement-system](requirement-system/WHAT.md) | 当前接受的产品真理必须有唯一 package owner、显式依赖与唯一 proof ownership。 |
| [verification-system](verification-system/WHAT.md) | requirement acceptance 必须由分层、可失败、可重放的证据体系定义。 |
| [feature-ablation](feature-ablation/WHAT.md) | 巡检与渐进验收需要节点注册表、三态语义、消融 DAG 与配置集，使下游未审机制可零影响关停而不改源码。 |
| [js-semantic-surface](js-semantic-surface/WHAT.md) | 语义测试只能经正式、稳定、JS-native 的 semantic surface 进入；Fable runtime representation 不属于 semantic contract。 |

### 2. Programming / causality
| Package | 一句话 WHY |
|---|---|
| [structured-workflow](structured-workflow/WHAT.md) | 业务流程应由宿主语言结构直接表达，不能在领域层再造第二程序计数器/runtime。 |
| [time-capability](time-capability/WHAT.md) | 时间与等待的物理能力必须显式进入系统，不能由 ambient clock/timer 偷渡业务判断。 |
| [causal-wait](causal-wait/WHAT.md) | 等待必须可诊断、可观测，但诊断观测不能升级为业务 authority。 |

### 3. Session / Host substrate
| Package | 一句话 WHY |
|---|---|
| [session-ontology](session-ontology/WHAT.md) | execution class、ownership、attachment 与 personhood 必须正交。 |
| [managed-session-lifecycle](managed-session-lifecycle/WHAT.md) | managed session 的创建、复用、取消、retire、replacement 与 owner closure 必须有单一生命周期合同。 |
| [host-boundary](host-boundary/WHAT.md) | 外部 Host 只提供最小可验证物理能力与稳定观察边界。 |

### 4. Participant / provider world
| Package | 一句话 WHY |
|---|---|
| [participant-identity](participant-identity/WHAT.md) | Role、Persona、ExecutionBinding 分离，换执行者不等于换人。 |
| [execution-model-routing](execution-model-routing/WHAT.md) | 唯一 MJS scheduler 以 `role + running` 决定 ModelTarget；runtime 只维护事件驱动 lease occupancy，`opencode.json` 不拥有 model authority。 |
| [office-capability](office-capability/WHAT.md) | office 由有资格产生的后果定义，不由 persona 名或工具白名单定义。 |
| [capability-enforcement](capability-enforcement/WHAT.md) | provider 看见的与 runtime 真能执行的 capability 同源且不扩大 office entitlement。 |
| [participant-horizon](participant-horizon/WHAT.md) | 只有会改变合法行动的最小事实应穿过 horizon。 |
| [cognitive-environment](cognitive-environment/WHAT.md) | 长期认知层与瞬时 runtime/mission 分开；knowledge 不创造 authority。 |
| [cognitive-workspace](cognitive-workspace/WHAT.md) | 持久认知画板已经退休；本包只保留负向合同，禁止 jq/canvas、TodoSink 与 Assume durable runtime 回流。 |
| [attention-regulation](attention-regulation/WHAT.md) | `enough`、`abandon` 与 `defer` 已经退休；注意力动作并入 `assume` 与编程面 ANCHOR `view.thenRemove` 摘要，本包只保留负向边界。 |
| [action-affordance](action-affordance/WHAT.md) | 决策点必须知道 act 的正负边界、成功后果与参数意义。 |
| [provider-language](provider-language/WHAT.md) | 一个 life 一个稳定 natural-language world；protocol identifiers 不翻译。 |
| [provider-projection](provider-projection/WHAT.md) | typed semantic intent 经唯一确定性投影成为 provider representation，表示不反向创造 authority。 |

### 5. Interaction / effect / durability
| Package | 一句话 WHY |
|---|---|
| [concern-routing](concern-routing/WHAT.md) | concern-addressed 邮箱（`subscribe`/`publish`）已经退休；跨参与者与用户交互并入编程面 `js.fork`/`js.join`/`js.deliver`/`js.ask`，本包只保留负向边界。 |
| [interaction-authority](interaction-authority/WHAT.md) | PhysicalUserMessage ≠ AuthorityTurn；typed provenance 才能创建/继续 logical interaction。 |
| [managed-chat-execution](managed-chat-execution/WHAT.md) | 物理消息的 durable acceptance、provider start、唯一终态与 exact settlement 由消息级执行 owner 统一管理。 |
| [dispatch-protocol](dispatch-protocol/WHAT.md) | 已获授权 interaction 穿过 unreliable Host 时避免 unknown outcome 复制逻辑效果。 |
| [durable-events](durable-events/WHAT.md) | immutable facts + atomic commit + deterministic fold = 单一可重放 substrate。 |
| [effect-accounting](effect-accounting/WHAT.md) | 外部副作用 Requested/Unknown/Accepted 分型；unknown 不能伪装未发生或成功。 |
| [durable-convergence](durable-convergence/WHAT.md) | replicas 按对象语义收敛，不靠 wall-clock/LWW 猜赢家。 |

### 6. Work / execution
| Package | 一句话 WHY |
|---|---|
| [delegation](delegation/WHAT.md) | 语义工作转交时 authority、charge、owner 与返回后果明确。 |
| [advisor](advisor/WHAT.md) | 同一调用 session 的请求永远落到同一个无工具顾问专席；law、view 与独立 intent 三条 prompt 不合并，follow-up 只追加 view 增量，`js.advisor` 以数据信封返回散文。 |
| [intra-participant-parallelism](intra-participant-parallelism/WHAT.md) | 同一 participant 可展开多个 coequal execution presents，而 identity/authority/responsibility 与最终 completion 仍保持一个。 |
| [process-execution](process-execution/WHAT.md) | 真实进程/PTY 有 bounded、可终止、物理完成可信的 execution semantics。 |
| [change-integration](change-integration/WHAT.md) | 独立 Git road 只在短原子门内发布，长 review/repair 不全局串行化。 |

### 7. Context continuity
| Package | 一句话 WHY |
|---|---|
| [semantic-trace](semantic-trace/WHAT.md) | participant life 的原始 semantic history append-only、可定位。 |

| [context-compression](context-compression/WHAT.md) | 历史过长时只在证据边界用 semantic memory 替换可压缩区。 |
| [prefix-stability](prefix-stability/WHAT.md) | 同一 semantic epoch 已呈现前缀 byte-stable；冷边界由事实驱动。 |

### 8. Failure / recovery
| Package | 一句话 WHY |
|---|---|
| [execution-failure-policy](execution-failure-policy/WHAT.md) | 封闭失败类型由唯一纯策略一次性裁决 retry、fallback、capacity、message 与 fatal 后果。 |
| [provider-attempt-recovery](provider-attempt-recovery/WHAT.md) | attempt 失败后可 bounded 换 execution binding，不改变 authority/personhood。 |
| [host-provider-failure-ownership](host-provider-failure-ownership/WHAT.md) | 万象术启用时接管 provider 失败恢复；Host 重试归零，claimed 错误抑制默认弹窗。 |
| [crash-reconciliation](crash-reconciliation/WHAT.md) | 中断后只从 durable facts + 可信物理观察重入普通程序。 |
| [degeneration-guard](degeneration-guard/WHAT.md) | 未结束 attempt 病态重复时主动止损再交正常 recovery。 |

### 9. Mission / work
| Package | 一句话 WHY |
|---|---|
| [obligation-ledger](obligation-ledger/WHAT.md) | 宿主待办与 compression checkpoint 归编程面 `todowrite-for-<role>` 的非空 `view` 声明；插件不维护第二份待办真相。 |
| [work](work/WHAT.md) | Work 与 Plan 共用一套生命周期：S1–S3 一段 deliver，S4 两段 deliver；review 返回 pairs，后继剥除前任 assistant 与 tool。 |

### 10. Feedback
| Package | 一句话 WHY |
|---|---|



`institutional-learning` 已随 WP-036 退役，不再是现行包。

### 11. Repository knowledge / programming
| Package | 一句话 WHY |
|---|---|
| [repository-investigation](repository-investigation/WHAT.md) | repository claim 由可定位可追溯真实 observation 建立。 |
| [knowledge-reuse](knowledge-reuse/WHAT.md) | 历史 repository knowledge 是 best-effort cache/hint，不冒充当前证明。 |
| [repository-programming](repository-programming/WHAT.md) | repository mutation 用 capability-projected、sandboxed、As-Is 逐笔落盘 surface。 |

### 12. Optimization / epistemics
| Package | 一句话 WHY |
|---|---|

| [epistemic-reasoning](epistemic-reasoning/WHAT.md) | 认识状态区分 proposal/evidence 与不确定性，由 controller 治理信息动作与停止。 |

### 13. Delivery
| Package | 一句话 WHY |
|---|---|
| [distribution](distribution/WHAT.md) | shipped artifact 携带 runtime code/resource closure；consumer 不依赖源码树/cwd。 |

## 依赖骨架

权威依赖清单见各包 WHY.md 的 DEPENDS ON 节，与 `requirements/INDEX.md` 的 157-edge 骨架一致。

## 运行与验证

```text
node requirements/verification-system/tests/run.mjs          # 单元套件（自动发现 requirements/<package>/tests/**/*.test.mjs）
node --test requirements/<pkg>/tests/NNN.test.mjs            # 单包单文件
node scripts/check.mjs                                       # 全 static gates
```

- 每个条款恰一个顶级 `tests/NNN.test.mjs`，与用例标题中的 `WHAT[<包目录名小写>-NNN]` 强力绑定；integration/release 档用例经文件内 tier-gate 门控（环境变量 WXS_TIER_INTEGRATION / WXS_TIER_RELEASE），分别由集成调度器与 --release 执行；测试落点由测试用例标题锚 WHAT[包-NNN] 定义。
- 迁移状态：旧 `docs/`、`changes/` 已于 2026-08-14 cutover 归档删除（git 可回溯）；`tests/` 已全部分包并统一编号。
