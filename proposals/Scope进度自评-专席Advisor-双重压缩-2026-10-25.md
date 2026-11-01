# Scope、专席 Advisor 与上下文压缩：设计记录

2026-10-25 定稿，2026-10-29 按 `scope-retention` 改写。**这不是活指南**：现行的 Scope 协议在 `resources/provider/host/scope-protocol/`，规则在 `wanxiangli/src/scope-retention/AGENTS.md`。本文只保留仍生效的约束与当前实现的边界；退役的估计计费路径只在末尾一段留一句交代，细节翻 git。

## 1. 现行 Scope 生命周期

- 每次工具调用带必选注解 `scope`：`scope.kind` ∈ {`resetScope`, `keepLast`, `removeLast`, `meToo`}，另有 `newScopeRetentionBudget`（保留额度，是信息容量，不是回合数）。
- 没有独立的 `resetScope` 工具。旧转录里出现的该工具名只作历史屏障读取，新回合一律用注解。
- `todowrite-for-ROLE` 只收 `name`、`program`、`intent` 三个字符串；宿主另注入 `scope`。不得再提交 `remainingScopeTurnsEstimate`，schema 已删（负向断言见 `repository-programming` 049／064）。
- `removeLast` 把上一拍整包收成一条 assistant message（`replacement` 写教训，有副作用须写明），并累计连续空转次数；不物理改持久库。
- `resetScope` 之前的调用在屏障前完成、旧范围结果被清理；屏障之后的调用成为新范围内容。每一范围只承诺一个能独立完成的状态闭环。

## 2. 专席 Advisor

`AdvisorSeat` 是每个真实 Session 专有、持久、连续的高能力模型会话，**没有工具**，也不暴露 `this.advisor(...)`。触发源是 retention 注解：

- `RetentionExhausted`：剩余保留额度为 0；
- `IdleSpin`：连续 `removeLast` ≥ 3。

触发后按四段顺序跑一次咨询：A 借用工作者当前最新真实请求视图提问 → B 在同一顾问会话追问需要哪些背景 → C 从工作者**此刻最新**视图补充材料（不复用 A 的旧请求）→ D 让顾问结合问题与材料给出建议。最终建议原文经 agent-attributed steer 送回工作 Session，不加顾问称谓或流程标签。

工作模型与顾问模型看到的都只是普通聊天中的问题、追问与材料；A/B/C/D、专席身份、会话复用、快照投影、队列与回执属内部编排，不向模型泄露。

每个工作 Session 至多一个 Process 在执行；执行期间再触发形成待处理项，第三个起与待处理项合并（保留每条触发身份与原因）。首个完成后从最新真实快照运行合并项。跨 Session 可并行。每段必须有真实 Provider 请求身份与回复回执；重启只恢复已确认阶段，效果未知的付费调用不盲目重发。Scope epoch 变化后旧 Process 放弃对当前 Session 的 steer。

顾问会话可以接受系统 compaction，但原始记录只追加；压缩不是删历史的授权。

## 3. 临时只读 SDK 会话

`negotiate`、`askPreviousScope`、Advisor A/C 使用短命 SDK 临时 Session。它们不是 Managed Work 回合，也不拥有独立容量租约：只有 PairHint 确认的短命 Session 可在其 `chat.message`／`chat.params` 阶段绕开真实工作 Session 的 admission/committed lease 结算；其余 managed physical message 仍须确证自己的 lease，无凭据继续拒绝 `PROMPT-006`。

临时请求原样携带正常 Provider tool schema 与模型身份，禁止工具在 `tool.execute.before` 前靠删减 tools 实现只读。

结果只有三类：有效非空 assistant prose 直接完成；真实观察到工具执行违规或响应正文确为空，才能在上限内再问；SDK error、租约/容量异常或网络错误属宿主失败，立即结算短命问询，不把报错 JSON 伪装成 user 消息，也不自动追加重试指令。Advisor 任一段失败时释放该 Process 的单飞队列并处理唯一合并等待项，前段错误不当 `proseN` 传递。

现行反例见 `requirements/host-boundary/tests/scope-hni-physical.test.mjs`：`delegation-negotiation-004`（工具 veto 有限重问）、`035`（临时 chat 无 managed lease、schema 原样、工具执行拒绝）、`036`（SDK error 含 PROMPT-006 只发一次）、`037`（CapacityQueueFull 抛错只发一次），以及 `advisor-031`（顾问递归自身角色拒绝）。本地 Provider 不等于商业 Provider 的端到端运维保障。

## 4. 三类咨询共用最新一次 Transform View

同一真实工作 Session 在内存中至多一份当前 LLM transform view，存于唯一 PairHint owner：本次 transform 进入过滤/Scope purge 前的 input、最终要发给 Provider 的 output，以及实际观测到的 system/model 身份。每次被宿主观测的新 transform 原子覆写该槽，不再额外保留三份永不失效的 recorded 数组。

临时咨询只描述来源 Session 与所需视角：`current-delegator`（negotiate / Advisor A、C）或 `preceding-scope`（askPreviousScope）。发出临时 Provider 请求的当下再从最新槽投影：current-delegator 取最新 output；preceding-scope 在最新 input 找最近一次真实 reset 屏障，只交出屏障之前的旧内容，之后的新 Scope 输入严禁泄漏。旧 Scope 的 model/system 取持久归档中验证过的少量原始元数据；找不到真实分界或证明不了旧模型身份时必须拒绝咨询，不得把当前 Scope 的回答冒充旧我回答。只有 input 缺可用旧边界、而最新 output 确含合法边界时，才能用 output，且仍严格分界。最新槽被下一次 transform 覆写后，任何后续问答立刻读新版本。

反例：新 transform 覆盖旧视图后旧临时会话不得恢复旧数组；pre-reset input 有旧信息而 post-reset output 已裁剪时 askPreviousScope 仍能取到旧信息；无 reset 分界必须 fail-close；新 Scope 私有正文不得出现在旧问询里；真实 Provider schema 不变；同一临时问题不得写回工作 Session。

## 5. 历史

2026-10-25 版曾以 `remainingScopeTurnsEstimate`（每回合自报剩余回合数）驱动进度比较、`ResetDue` 强 steer 与自动 Advisor，并计划独立的 `resetScope` 工具与 `newScopeTurnBudget`。该路径已整体退役：工具 schema 不再有估计字段，宿主不再结算估计，也不再因 progress 健康度拒绝回合。`wanxiangli/scope-progress` 现只保留顾问席与 epoch 载体为产品面，`record`／`settle*` 属包内历史（见该包 AGENTS「产品面边界」）。旧文的计费回合、min 聚合、A→B→C→D 触发条件与提示词稿均为考古材料，需要时翻 git。
