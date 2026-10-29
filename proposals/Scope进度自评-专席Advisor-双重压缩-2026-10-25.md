# Scope 进度自评、专席 Advisor 与压缩协作 — 定稿

> **历史实施契约（估计路径已退役）**：`remainingScopeTurnsEstimate` 不再进工具 schema，Stall／ResetDue 不再开顾问。Scope 生命周期以共同法 «Scope»／`scope-retention` 为准。下文可作 `scope-progress` 包考古，**勿当现行产品入口**。


状态：APPROVED FOR IMPLEMENTATION（2026-10-25）；本文件是实施契约。每一阶段需记录真实代码与物理验收，不能把“设计已定稿”误写为已实现。

## 1. 唯一进度单位

每个有真实 providerRunId 的 LLM 请求是一个物理回合。这个回合没有调用任何有效 `todowrite-for-*` 时，**进度计费为 0**，不更新估计、不比较、不触发 Advisor。若有一个或多个有效 `todowrite`，**进度计费为 1**，最终估计是同一 providerRunId 所有有效 `remainingScopeTurnsEstimate: integer >= 0` 的 **min**。不依赖工具回调完成顺序。工具调用 ID 去重，调用重放、Provider 事件重放不能再次结算。

必须等同一 LLM 回合的可观察 `todowrite` 工具集合结束（成功、失败或明确中止）后，才把该回合结算为唯一进度事实；不能第一项到达就先触发 Advisor。未被成功受理的工具不贡献估计。每个工具的 Schema 以 OpenCode/OMP 宿主实际入参为权威，`remainingScopeTurnsEstimate` 必填，类型整数、最小值 0，缺少/null/负值拒绝。模型可以一回合不调用 todo，那就完全免费；不额外设“缺报检查”。

每一 Scope 第一条计费估计建立 `oldEstimate` 基线，不触发比较。后续同 Scope 的 `newEstimate < oldEstimate` 更新即可；`newEstimate >= oldEstimate` 自动产生一次 Advisor Process 触发（不审查模型动机）。系统公开规则；模型可以策略性递减，非负整数天然限制这种做法。

若结算值为 0，创建同 epoch 持久 `ResetDue`。从**下一次**正常 Provider 请求起独立 agent-attributed **strong steer** 提示 resetScope，直到确证真实 `ScopeReanchored`。期间新一轮计费回合无论估计多少，都触发 Advisor；若不调用 todowrite，仍持续 reset 提醒，不能悄悄清除 ResetDue。Advisor 回答和非 operator steer 不改变估计和 Scope。

## 2. todowrite 模型可见参数

名称 `remainingScopeTurnsEstimate`，非负整数，必填。描述逐字如下：

> 预计完成当前 Scope 还需要多少个计费 LLM 回合，从本回合结束后算起。填写非负整数。
>
> 不调用 todowrite 的 LLM 回合免费；一个回合无论调用多少次 todowrite，都只计费一次。如果同一回合提交多个估计，系统取最小值 min(多个估计)。
>
> 系统把本回合最终估计与当前 Scope 上一个计费回合的估计比较。新值大于或等于旧值，自动启动专席 Advisor。新值为 0，下一回合开始要求 resetScope；完成重置前再次提交估计，也会启动 Advisor。

初次基线需要时强 steer：“当前 Scope 尚无剩余计费回合估计。在本次 todowrite 中提交 remainingScopeTurnsEstimate。”系统知道条件是否成立才发，不靠模型猜。

## 3. 专席 Advisor 的四段咨询与持久队列

`AdvisorSeat` 是每个真实 Session **专有、持久可认领、连续同一会话**的高能力模型，**没有工具**；不暴露 `this.advisor(intent)` 或公开 `advisor` 工具。它可以接受系统 compaction，但原始 Advisor 会话记录只能追加，压缩不能作为删历史授权。

每次触发创建 `AdvisorProcess(session,scopeEpoch,triggerReportId)`，有四段因果顺序：

**A → prose1**：借用工作者当前最新真实 Provider 请求视图。估计相等时问“工作进展受到了什么阻碍？哪些困难还没有解决？”估计增加时问“最近有什么新发现或额外工作？请说明目前的难点。”在零值后仍未 reset 时问“接下来的工作还有哪些未完成？遇到了什么阻碍？请说明目前的情况。”

**B → prose2**：在同一个顾问会话内自然发问：“关于下面这个问题，还需要哪些背景材料才能给出可靠的建议？\n{prose1}”

**C → prose3**：从工作者**此刻最新**的真实请求视图发问，不复用 A 的旧请求：“刚才讨论的问题：\n{prose1}\n请补充以下信息：\n{prose2}\n说明相关事实和你目前的判断。”

**D → prose4**：在同一个顾问会话中自然接续：“补充信息如下：\n{prose3}\n请结合之前的问题给出分析和建议。”

prose4 原文通过现有 agent-attributed steer 物理投递工作 Session，不附加任何顾问称谓或流程标签。不冒充操作员；不改变剩余估计、不解除 ResetDue、不代替人决定是否 reset。

每个工作 Session 最多一个 Process 在执行；执行时额外触发形成一个待处理项，第三个起与待处理项合并（保留每条原触发身份和原因，不改变执行中 Process）。首个完成后，从最新真实快照运行合并项。跨不同 session 可并行。每段必须拥有真正的 Provider 请求身份和回复回执；重启只恢复已确认阶段，不盲目重发效果未知的付费模型调用；晚到、重复、不同 epoch、会话停止不得送错。若 Scope epoch 已变化，旧 Process 放弃对当前 Session 的 steer，不把旧建议当成新 Scope 当下建议。

**宿主可见、模型不可见：** A/B/C/D、专席身份、会话复用、快照投影、队列与交付回执只属于内部编排。工作模型和顾问模型收到的每一条请求，都只是普通聊天中的问题、追问或补充材料。唯一公开的升级约束是用户明确指定写入 todowrite 工具参数说明的计费规则；不因公开触发条件而泄露幕后四段协议。

## 4. resetScope 及保留规则

`resetScope` 仅有 `oldScopeVerboseVerbatimExperience?: string | null` 与必填 `newScopeObjective: string`。删除 `newScopeTurnBudget`、`kpiCompletion`、旧预算倍率/倒计时及全部旧辅助提醒。真正重锚提交清除当前 epoch 的 Progress 基线/ResetDue，下一次 todo 重新建立基线。第一次 reset 不要求 askPreviousScope；第二次及以后才有独立首问强 steer。

每次历史裁剪**原样保留**所有可取得的 assistant 普通文本和 provider 实际公开的 thinking/reasoning、所有真实 operator 消息、全部 reset 调用和配对结果，以及已指定关键工具 `assume/negotiate/askPreviousScope/askPrevious/advisor` 的完整调用/结果。其余低价值普通工具调用与成对结果从后续 Provider 请求副本中剔除；混合消息按真实 part 身份裁剪，不能误删同消息的 assistant 文本与 reasoning，不能遗留孤儿工具结果。任何私有/未公开的模型内部推理不能编造。

reset 返回值依据**持久化委托台账**列出在 reset 因果截面尚未归队的 fork/resume/commission，每一子会话最近一次**已受理派出指令全文**，明确任务句柄/方式/未知或在途身份；真实已返回的不列入。不因 agent-attributed user steer 而清掉预算或触发 reset。

## 5. 系统 compaction 的独立协议

允许开启宿主自动 compaction，它**不推进 Scope epoch**、不收费、不修改剩余估计、不启动 Advisor、不假设业务完成；保留委托义务、专席会话身份和状态机。保留所有原始持久事实与归档，不允许压缩路径直接把唯一权威物理删除。旧 `compaction.auto=false` 和无条件否决 autocontinue 的启动合同需更换为能证明新 Provider run 身份和跨 compaction 的真实测试合同；`prune` 是否开启取决于能否证明不删除唯一路径中的原始行，不能因为需要 auto 就默认允许删历史。

compaction 后若需要恢复模型上下文，只呈现自然的工作材料：“当前任务目标：{currentScopeObjective}\n已确认的背景：{availableWorkSummary}\n尚未结束的工作：{outstandingWorkSummary}\n继续处理。”进度状态、顾问队列、Provider 请求身份只存在宿主账本，不暴露给任一模型。

系统压缩后的有限 Provider 上下文不能保证容纳无限的原始 assistant 内容；须保证这些原始消息能从持久源恢复，不能伪称压缩后的当次请求仍携带全部原文。

## 6. 模型提示词（定稿）

- 顾问初始（仅确需时）：“你擅长分析复杂问题。根据已提供的信息作答；需要补充材料时直接提出问题。”不提专席身份、无工具、持久会话或四阶段机制。无工具权限由宿主实施。
- A/B/C/D 对话原文：仅采用 §3 的自然聊天问题，不包含 Advisor、专席、prose 字段、阶段或快照身份。
- 最终回答 steer：原样 `{prose4}`，不添加顾问标题；`attribution: agent` 是宿主内部元数据。
- ResetDue 强 steer：“你已报告当前 Scope 尚需 0 个计费回合。现在调用 resetScope，提交上一 Scope 的交接文本和新 Scope 的目标。”直到重锚成功，每次正常请求重复。
- resetScope 工具描述：“结束当前 Scope，交接已有工作并建立下一 Scope。提交旧 Scope 的交接文本和新 Scope 的目标。万象术保留操作员消息、assistant 的可获得原文与指定关键工具的完整记录，清理其他旧工具过程，并返回尚未归队的外派工作。”
- 未归队提醒：“Scope 已重置。下一 Scope 目标：{newScopeObjective}。以下工作尚未归队，列出各项最近一次实际接受的派出指令全文：{outstandingDelegations}”。无在途时不加空列表。
- 模型报告规则完全公开；没有劝导“诚实”“不要人为下降”的提示词。系统负责触发时机，不让模型判断条件是否适用。

## 7. 职责分工和真实验收

唯一 `wanxiangli` ScopeProgress 拥有报告按 providerRunId 分组、min 聚合、估计/ResetDue、幂等/并发/中止事务；专席队列与持续会话分别由自治 AdvisorProcessQueue 和 AdvisorSeat 拥有。主仓 Host 仅做真实 Tool Schema、Provider 出站和物理 steer 执行；OpenCode/OMP 不维护第二套业务估计/触发判断。

分层逐项通过：① 非 todo 回合免费，同一回合 N 个 todo 的 `min` 不受执行完成先后影响；跨回合升降/零值/重复工具 ID/失败均正确。② 一 Session 一个可恢复专席；四段顺序、最新快照、无工具、队列两项及以上合并、迟到/异常/崩溃 Unknown 均有正反例。③ 双宿主 reset 新 schema 仅两文本；thinking/assistant 完整保留、普通工具配对清理、实际未归队指令全文。④ 真实 OpenCode/OMP 自动 compaction 后的请求、状态与 Advisor/在途工作连续。⑤ npm pack 后 Node/Bun 消费真实库；Fable 编译/链接和全仓静态/相关 WHAT 门禁不新增红项。

只有真实通过的部分标明已实现。代码尚未接入的部分保持未完成，不以 proposal 本身冒充运行证明。

## 8. 三类咨询共用最新一次 Transform View（2026-10-25 增补定稿）

同一真实工作 Session 在内存**至多有一份当前 LLM transform view**，存于唯一 PairHint owner。其内容包括本次 transform **进入过滤／Scope purge 前的消息 input** 与最终准备发给 Provider 的 **output**，以及实际观测的 system/model 身份。每一次新的、被真实宿主观测的 transform 都原子覆写该槽；不得额外用三个永不失效的 recordedMessages/recordedSystems/recordedModels 或为每一个临时问询冻结复制新的大数组。永久历史／reset 事实的 durable archive 属于另一个持久职责，不能拿进程缓存当耐久证据。

真正临时咨询的描述仅为来源 Session 与需要哪个视角：`current-delegator`（negotiate/Advisor A+C）或 `preceding-scope`（askPreviousScope）。**发出临时 Provider 请求的当下**再从最新槽投影：current-delegator 选最新 output；preceding-scope 优先在最新 input 寻找最近一次真实 `resetScope` 工具边界，只交出其前面的旧内容，后面的新 Scope 输入严禁泄漏。前一 Scope 的 model/system 采用持久归档验证过的少量原始元数据；无法找到对应旧 Scope 的真实分界、无法证明旧模型身份或请求 schema，必须拒绝模型咨询，不得把当前 Scope 回答冒充旧我回答。仅当 input 缺少真实可用旧边界、而最新 output 确实包含对应合法边界时，才能使用 output 且仍严格分界；不得退回上一次过时 transform。唯一最新槽若已被下一次 transform 覆写，任何临时会话的后续问答都应立刻读到新版本。

所有临时询问使用**隔离的真实短命会话**。只保留 Provider 原始 tools/schema 和自然语言正文；特殊咨询身份、内部 marker 和四阶段调度不向模型可见。禁止工具执行由宿主的 `tool.execute.before` fail-close / OMP 原生临时 runner 行为负责，而非从 Provider 请求删除工具目录。Advisor 仅看正常聊天；真正工作 Session 的历史不追加这些临时询问。连续的 Advisor 自己对话遵循其独立持久 Seat 身份，不与某次临时 worker 投影混淆。

物理反例：新 transform 覆盖旧视图后旧临时会话不得恢复旧数组；pre-reset input 有旧信息而 post-reset output 已裁剪时 askPreviousScope 仍能获取旧信息；没有 reset 分界必须 fail-close；最新新 Scope 私有正文不能出现在旧问询；真实 Provider schema 不改变；同一临时问题不能写回工作 Session。


## 9. 只读临时 SDK 的准入与结果分类（2026-10-25 实测补充）

由 `negotiate`、`askPreviousScope`、Advisor A/C 使用的 SDK 临时 Session 不是新的 Managed Work 回合，也不拥有独立调度容量租约。**只有** PairHint 确认的短命 Session 可在其 `chat.message` / `chat.params` 阶段绕开对真实工作 Session 的 admission/committed lease 结算；任何其它 managed physical message 仍须严格确证其自己的 lease，无凭据则继续拒绝 `PROMPT-006`。临时请求原样携带正常 Provider tool schema 和模型身份，禁止工具在 `tool.execute.before` fail-close，绝不靠删减 tools schema 实现只读。

临时 SDK 结果只有三种：**有效非空 assistant prose** 直接完成；**真实观察到工具执行违规或正常响应正文确为空** 才能在上限内再次询问；**SDK 返回 error 字段、Provider/租约/容量异常或网络错误** 属于宿主失败，立即结算短命问询，不应把报错 JSON 伪装成 user 消息，也不自动追加“刚才的回答不完整”或网络重试指令。对 Advisor 四段流程任意阶段的失败，释放当前 Process 的单飞队列并处理唯一合并等待项；不得把前一段错误当成 `proseN` 传递。

验收：`requirements/host-boundary/tests/scope-hni-physical.test.mjs` 的 `delegation-negotiation-004`（工具 veto 仍有限重问）、`035`（临时 chat 无 managed lease、schema 原样、工具执行拒绝）、`036`（SDK error 中含 PROMPT-006 只发一次）、`037`（SDK Promise 因 CapacityQueueFull 抛错只发一次）；以及 Advisor 递归自身角色拒绝 `advisor-031`。本地 Provider 不等于商业 Provider 端到端真实运维保障。
