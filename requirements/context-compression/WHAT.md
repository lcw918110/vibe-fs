# context-compression — WHAT

## [001] 不推导容量，只向模型披露计量与声明常量

严禁从自身观察推导、估算或缓存任何模型上下文窗口大小；禁止上下文余量、模型容量表，或把字节/token 换算成窗口比例的逻辑。唯一允许的字节计量为合法的文件与进程计量。

可以披露的只有两类事实：已经发生的计量，与 provider 自己声明的常量。宿主向模型披露本次工具结果的 `turnBudget` 倒计时后缀（见 [034]），并在 chat.params 屏障处读取并由 Pair Hint 披露 provider 对当前模型声明的 context limit（opencode 模型配置里的值，宿主只读不推）以及 provider 实际计费的缓存命中与未命中。二者都只是模型判断自身状态的 datum——模型是唯一知道哪些上下文对任务重要的主体，披露把选择权交给它。宿主不得由这些数字推导窗口大小、剩余预算，也不得替模型做出压缩决定。

## [002] 不主动预测溢出

在请求发送前，宿主严禁判断是否接近容量上限，禁止根据投影长度比例、剩余预算或累计 Token 主动选择压缩点或触发自动裁剪。真实的 provider attempt 失败是唯一的恢复触发信号。

向模型披露计量（[001]）与工具结果携带的 `turnBudget` 后缀（[034]）不构成溢出预测：披露不改变起点，不产生任何自动压缩动作；上下文重置仅由模型在显式调用 `resetScope` 宿主工具（见 [033]）时触发，不是宿主预测。

## [003] 取消独立 Y 轴与 200 KiB Blogger delta 合同

系统不再设独立的 Y 轴、Blogger、Chronicle、200 KiB delta 切块或 `MemoryPreamble` / `FrozenRecordPrefix` 物化管线。上下文的清理边界完全由模型显式调用 `resetScope` 宿主工具作为唯一执行屏障（见 [033]）决定；最新一次 reset 之后的活跃 scope 原样保留，不另设独立的 Y 物化覆盖证明。

## [004] 输出预算属 provider

系统不计算输出应占用的 Token 数量，亦不检查压缩比例。对输出的唯一内容校验为 `isValidTerminal`（非空且非纯 XML）。

## [005] 失败不分类

控制流仅识别快照的 `Outcome`（`Completed | Failed | Aborted`），严禁根据错误字符串或异常类型细分溢出、网络或限流等原因。

## [006] 重试不构造 prefix probe，失败预算有界

每个物理 retry attempt 拥有全新 physical identity；系统已废除 Y 恢复材料与 prefix probe 候选选择机制。provider attempt 失败后的恢复策略统一为：**第一次原样重试，第二次把该 Provider 拉黑后重试，以此类推**（见 `provider-attempt-recovery` [021]）。每次确认失败经 `ProviderFailureLedger` 记账并推进 `ProviderFailureBudget` 连续失败计数；`verdict` 为 `Exhausted`（单一 `DefaultBudget`）时停止自动重试。业务主请求（`WorkMain` 的有效成功）一旦成功，清零连续失败计数。

## [007] 按 RequestKind 分派结局

每个 attempt 的结局按 `ProviderRequestKind` 进行确定性分派；RequestKind 必须来自 attempt 的 typed context / durable receipt / accepted continuation evidence，而非角色猜测或错误文本。同种 RequestKind 的同种结局必须产生完全一致的后继动作。`InteractionRepair` 成功不得清零连续失败计数；只有 `WorkMain` 的有效成功可以清零（`BloggerMain`、`BloggerSquash` 与 `StrengthReplica` 已随 clean-break 彻底退役）。

## [008] 主工作会话不发压缩请求

主工作会话（Work Session）严禁向主模型发送请求压缩历史的指令，宿主绝不在后台自行发起上下文裁剪。上下文重置完全由模型在细粒度任务块（状态闭环）的自然边界显式调用 `resetScope` 工具（见 [033]）自主决定。

## [013] 诊断不是控制输入

可观测诊断日志严禁作为控制流输入，不得使用任何诊断字段驱动 retry 的分支决策。

## [017] 真实用户消息与 scope 清理

**真正由操作员编写的用户消息**不是上下文清理边界，且永不删除。每次 `resetScope` 清理时按原序保留这些 operator 用户消息（包括确实来自操作员的普通 prompt 或 steer）；宿主上的 `role: "user"` 载体不自动意味着 operator 身份。协商、旧 Scope、Advisor 等代理答复以 `attribution: agent` 呈现时属于代理过程，不得冒充 operator；处于旧 scope 的这类答复按普通历史清理，最近一次 reset 之后属于当前活跃 scope 的内容原样保留。

最新一次 `resetScope` 之前，除所有保留的用户消息与历次成对保留的 `resetScope` 调用及其结果之外，全部 assistant 文本、thinking、非 `resetScope` 工具调用及结果均被彻底清除。系统不得借缺少编号在保留规则之外另行删除用户消息。

## [019] 重锚边界与辅助注入可见性

任何代表上下文重锚的操作（如 `ScopeReanchored`、`ContextReanchored` 或 `TenureReanchored`），必须将旧 provider horizon 中的辅助注入可见性归零。持久化事件保留作为审计事实，但新 horizon 的初始消息不再回放旧辅助材料，仅在后续各自正常触发时逐步重新生成。
## [023] retry 全事件驱动且时间无关

Provider retry 的 correctness path 严禁读取 wall clock、构造 `TimeSpan`、调用 timer/deadline/Delay 或以超时作为状态转换。已废除等待 Blogger open producer 的阻塞逻辑，确认失败且宿主停止重试后即按有界预算推进原样重试或拉黑 Provider 后重试。

## [030] assume 调用原样保留

`assume` 是短小但高价值的判断承诺点。在活跃 scope 内，承载 `assume` 调用的原始 Host 消息与对应 result 消息原封不动保留；不得把 assumption 文本抽出来重写、不得重新渲染参数。

每次 `resetScope` 都必须**原样保留所有历史 `assume` 调用及其成对结果**；与 `negotiate`、`askPreviousScope`（含旧拼法 `askPrevious`）、`advisor` 一样，不因后续任何重置而删除。模型不必再靠交接材料重抄这些工具的输入与输出。普通非豁免工具与过程文本仍按最近一次 reset 清理。

## [033] resetScope 屏障与原文逐字保留

`resetScope` 是系统核心宿主工具（loadMode: "essential", concurrency: "exclusive", approval: "read"），其签名如下：

```ts
resetScope(
  oldScopeVerboseVerbatimExperience?: string | null,
  newScopeObjective: string,
  newScopeTurnBudget: number,
  kpiCompletion: "not_started" | "far_below" | "partially_met" | "met" | "exceeded" | "not_applicable"
)
```

最新一次 `resetScope` 调用是**唯一**的上下文边界。在发往 provider 的每次请求副本中，清理上一次 `resetScope`（或 session 起点）到本次调用之间的全部上下文，但**必须且仅**保留：
1. 所有真实 operator 编写的 `role: "user"` 消息（干净 prompt、真人指令及 operator-authored steer），按原始顺序夹在历次 `resetScope` 调用之间；仅以 `role: "user"` 承载但标记为 `attribution: agent` 的消息**不是** operator 输入，不享有跨 reset 永久保留特权；
2. 历次 `resetScope` 调用及其成对的工具结果（按 `tool_call_id` 成对保留，清除旧调用上的 thinking 块）。
3. 历次 `assume`、`negotiate`、`askPreviousScope` / `askPrevious` 与 `advisor` 的原始调用 part（含完整参数）及按调用 ID 成对的原始结果 part；多次 reset 均不能排除，未配对的伪结果不得混入。

并发屏障规则：包含最新 `resetScope` 的同批工具调用中，`resetScope` 作为执行 Barrier。排在它之前的工具调用先执行并随旧 scope 一并被清理，排在它之后的工具调用在其返回后执行并保留在新 scope 中。最新一次 `resetScope` 之后产生的活跃 scope 消息原样保留。

关键原文逐字保留义务：由于旧 scope 的原始工具输出与过程文本在清理后被彻底删除，如果函数签名、报错堆栈、精确行号或关键事实在下一个 scope 仍须逐字使用，模型必须将其原样抄写进 `oldScopeVerboseVerbatimExperience`。

工具执行成功后原样返回 `Scope reset. Objective: <newScopeObjective>`；只在发往模型的请求副本中给结果追加新预算倒计时（见 [034]）。

## [034] turnBudget 递减、后缀与提醒

1 次 LLM 请求计为 1 turn。会话状态机在 `turn_start` 时将当前活跃 `turnBudget` 减 1，下限为 0。

1. **结果后缀**：每条已完成工具结果首次进入当前 prefix epoch 的 LLM 请求副本时，记录当时的 `turnBudget`，在结果末尾呈现 `\0\uFEFF[Remaining turnBudget: N]`。**同一 epoch 后续请求必须逐字重放该结果首次呈现的后缀**，不可用最新剩余预算批量改写历史结果；新结果首次呈现时仍显示当时的当前预算。`resetScope` 结果同理；仅提交的 `ScopeReanchored` 允许在新 epoch 重新建立尾巴。真实工具结果、持久化记录与用户原始消息绝不写入该后缀。再次热修正不可重复追加。
2. **没有已确认预算时的强 Steer**：第一次成功 `resetScope` 之前，或进程重启后历史虽含 `resetScope`、当前会话的预算状态却未能可靠恢复时，均没有可用预算；每一次普通 Provider 请求都由 `wanxiangli/pair-hint` 增加一条新的强提醒 user 载体 steer（`attribution: agent`, `steering: true`, `strong: true`），要求模型估算预算并首次调用 `resetScope`。强 steer 不是 operator 用户原文，禁止挂到任何用户消息或工具结果尾巴；工具原始输出、持久化历史与已经冻结的历史字节完全不变。首次 `negotiate` 尚未得到物理答复时，强 steer 改为要求先协商。每次请求重新生成当前有效的提醒，不按旧工具身份冻结。
3. **后续用户／代理消息不作废已知预算**：当前进程已收到 `resetScope` 成功提交后的实际预算时，后来的真人用户消息、委托答复、旧我回答等 agent-attributed user 载体均沿用当前 Scope 与剩余预算；不会把预算设为零，不会重新进入初始化状态，也不单因消息到达鼓励 reset。正常回合继续递减预算，模型在实际工作闭环适合时自行决定重锚。第二次及以后重锚才有 `askPreviousScope` 首问义务，其 MUST 也在每次请求的单独强 steer 中呈现。side-request 不改变预算及强提醒状态。
4. **零预算弱提示**：仅当 scope 已初始化、当前预算由真实 `resetScope` 成功受理且可确认，且 `turnBudget` 降至 0 时，若当前请求不是 side-request 快照，宿主可在请求副本末尾追加一条弱提醒用户消息（`steering: true`，不持久化），建议模型在当前任务状态闭环的自然边界调用 `resetScope` 开启新闭环并换取新预算。该弱提醒绝对不得强行中断、暂停或阻断正在进行的工具工作。

## [035] ScopeReanchored 冷前缀边界

`resetScope` 调用的落地构成明确的冷前缀边界（Cold Prefix Boundary）。

当执行并提交一次合法的 `resetScope` 时，系统向持久化日志追加一条不可逆的 `ScopeReanchored` 事实，记录前一 epoch、新 epoch 以及本次 `resetScope` 的精确调用身份（`toolCallId`）。`ActivePrefixEpoch` 恰好加一，且系统幂等清空已缓存的 provider wire 前缀快照。

`prefix-stability` 将重锚后的下一次请求视为全新 epoch 的起点，重新建立精确字节前缀，不把旧 scope 清理造成的历史字节缺失判定为前缀漂移或违反同 epoch 前缀追加律（prefix-stability [001]）。同一 `toolCallId` 严禁重复产生重锚事实。

## [036] 跨会话只传当前指令与结构化材料的 TOML 承载

所有跨会话上下文传递统一遵循 AXIOM 7（prose 属于指令与主报告，骑乘 `# ...` 注释头；材料属于结构化参考数据，骑乘 TOML 数据平面表格）：

1. **父到子委派（`fork` / `resume` / `commission` / `devops` / `dag`）**：调用方传入的 charge / intent 散文按原样渲染为 `# ...` 注释头；只传当前指令与任务选项，严禁传递调用方的历史、完整 post-scope LLM 可见消息视图、`[[context]]` 表、附件视图或历史摘要。若存在独立、确切的结构化参考数据，作为只读材料在 TOML 数据平面承载；子会话不隐式继承父会话的物理工具调用，也不接受局部材料过滤选择。缺少上下文时，被委托方必须通过 `negotiate` 主动协商（见 `delegation` [034]）。
2. **顾问咨询（`advisor`）**：由单一请求文档承载，咨询意图按原样渲染为 `# ...` 注释头，只传当前咨询指令，严禁携带调用方的完整 post-scope 消息视图或历史上下文；若有明确结构化数据则在 TOML 数据平面承载。顾问自然停机后交回的是其末轮 assistant 散文，作为 `# ...` 注释呈现，不返回顾问的完整 post-scope 视图。
3. 传递的数据在最终边界统一 pretty print 为合法 TOML，禁止将拼好的 TOML 作为转义字符串嵌套在字段中。

## [037] 子会话自然停机终态与 deliver 工具分立

子代理会话与生命周期管理实行完成机制与工具分立：

1. **子代理自然停机完成**：Engineer 与 DevOps 角色彻底移除子代理 `this.deliver` 成员与相关工具。子代理完成工作后自然停止输出（无未决工具调用的干净终态）；宿主在观察到子代理自然停机后，把该终态轮次的 assistant 散文（正文散文、空 carry）作为完成材料交回给父会话的 `join`，并在父方边界渲染为 `# ...` 注释。宿主不捕获子代理完整的 post-scope LLM 可见消息视图，返回材料里也绝不出现 `[[session]]` 表。
2. **免除子代理催促**：Engineer 与 DevOps 免除任何完成催促（nudge），允许自然停机。仓内所有教导子代理如何调用 deliver 返回、如何撰写特定交付语法的提示词全部删除。
3. **生命周期入口只有 review**：Plan 与 Manager 的退休、接力、晋级、结案和重开只通过 `review` 进入 Work。宿主不再保留生命周期工具 `deliver`，也不再按 S1–S3 或 S4 分派它。材料交还仍是 `wxs_deliver`，它不驱动任期事实，也不挂接上下文或材料选择数组。


## [038] Scope 与 Pair Hint 的独立所有权

`wanxiangli/pair-hint` 是 scope 过滤、提示拼接、首问 pending 状态与预算状态的唯一程序所有者。每个会话独立维护当前 prompt 身份、初始化状态、首问 pending 状态与 `turnBudget`；宿主只能将真实 turn、prompt、`resetScope`、`askPreviousScope` / `negotiate` 登记与会话结束事件交给该对象，不再自建第二份预算表、pending 状态机或重复实现 filter/suffix 决策。

模型消息处理只有一个执行时机：**即将发往 LLM 时**。`wanxiangli/pair-hint` 的自治 ScopeSession 接受每轮请求的中立消息视图（kind、operator、prompt key、调用/结果身份与分段位置），输出保留位置、写入目标和后缀内容。OMP 与 OpenCode 两个 Host adapter 只解码自己的真实 envelope，并把这个 plan 应用到本次请求副本；领域对象既不认识 `role/content`，也不认识 `info/parts/state.output`。用户消息尾巴与工具结果尾巴是两种可独立呈现的注入，统统不修改真实工具执行结果、`tool_result`/`tool.execute.after` 回调、用户原文或持久化历史。

`Pair Hint` 原有的持久落点与 synthetic skill 消息仍由 Host 原生机制提供，不能与用户消息、普通工具输出的两条注入线混为一谈；宿主只负责物理消息呈现与日志定位，不再负责 scope 的领域判断。对无法观测的宿主行为不得声称执行屏障已经成立，必须由实际宿主测试证实。

## [039] OpenCode 发往模型前的热修正

OpenCode provider messages transform 必须在宿主持有的原消息数组绑定上替换**本次请求副本**里的元素，不能仅通过赋值 `outObj.messages` 指向新数组。对用户文本注入强提醒，对已完成或失败的工具结果字段显示预算或强提醒；原始 Host 消息对象、工具回调结果与 durable history 保持原样。对已删除旧 scope 的结果不得重新插入。物理断言由 `host-boundary/tests/scope-hni-physical.test.mjs` 承担。

## [040] 同消息工具调用／结果配对

从 `ISessionSnapshotPort` 获取的会话视图可能把多个调用和结果装在同一条 assistant 消息的不同 parts 里。中立过滤必须按 `toolCallId` 同时保留历史 resetScope 调用和同消息内的结果，并按最新 reset 在同批工具块中的位置裁剪；先行非 reset 工具的结果仍应删除。跨会话 Fork、Fission、Advisor 等上下文取材均使用相同的中立过滤计划，不另设 raw-message 判定。

## [041] ScopeSession 退场

OpenCode 的 `session.deleted` 事件到达时，仅清除该物理会话 ID 在 wanxiangli 中的 scope state（prompt 身份、初始化、turnBudget），不得影响同时存在的其他会话。OMP 的 `session_start`、`session_switch`、`session_branch` 继续清理其明确的会话状态。清理不写日志、不替换真实工具结果、不触碰工作历史。

## [042] 跨 Scope 回问接口与首问协议

`askPreviousScope` 是供模型在 `resetScope` 之后向紧邻上一 Scope 澄清疑问的宿主工具，其签名如下：

```ts
askPreviousScope(
  questions: string
)
```

1. **访问边界与首次调用**：第一版仅允许访问同会话（session）刚关闭的、紧邻上一 epoch 的已封存出站视图；来源身份由持久化 epoch 决定，不暴露任意跨会话查询，不接受模型指定外部档案引用。在会话首次发生 `resetScope` 之前调用本工具，宿主明确返回 `NoPreviousScope` 错误。**第一次成功 reset 不强制 askPreviousScope**：首次只是建立可工作预算，第二次及以后成功 reset 才建立前任回问首问义务；首次重置后仍可主动咨询但不催问。
2. **模型自主发问与形式校验**：`questions` 为非空字符串，疑问内容完全由重置后的新模型根据旧模型交接材料（`oldScopeVerboseVerbatimExperience`）与当前任务目标自主组织，允许将多个相关问题集中一次问出。宿主严禁为 `questions` 预填模板文本，严禁对问题内容进行语义审查或关键词过滤；宿主仅校验 `questions` 为非空字符串等形式合规性。
3. **Receipt 即解除 pending**：当模型发起合法的首问调用时，宿主入口在核对会话、epoch 与调用身份后，持久登记该询问的 receipt，并立即向模型返回已接受受理的回执（或明确的形式拒绝），不同步等待旧模型回答。**宿主一旦成功登记首问 receipt，当前 epoch 的首问 pending 状态即告解除**，预算递减与初始化时序恢复正常推进。首问义务的解除绝对不以旧模型回答送达、排队成功或 `PhysicalAccepted` 为前置条件。
4. **回答作为 User Steer 迟到送达**：旧模型在冻结的只读旧视图上完成单轮推理后，其回答经由 PromptDispatcher 路径以真实 Host 用户消息载体（`role: "user"`）注入接收方会话，但归属标记为 agent（`ScopeConsultation`）。该消息携带持久 PromptKey 并排队供后续 LLM 请求自然消费；严禁将回答伪装成 operator 用户消息，回答到达不得作废当前 `turnBudget`，亦不得触发未初始化提醒。
5. **异常、Stale 与重放语义**：
   - **Stale 废弃**：当回答送达时，若接收方会话已发生新的 `resetScope`（epoch 已推进）、会话已被删除（`session.deleted`）或操作员已取消，该在途回答判定为 `Stale` 并予以丢弃，严禁跨 epoch 注入后续上下文。
   - **Unknown 处理**：调用向 Provider 发起旧模型推理若返回 Unknown 或超时，宿主保留持久 Pending 记录与原调用身份，严禁判定为伪造的成功或失败，严禁盲目发起二次计费重试。
   - **调用重放幂等**：同一 `toolCallId` 的重复调用或重放必须返回原持久 receipt 或结算记录，严禁重复向旧模型派发推理请求。
   - **后续普通追问**：首问义务解除后，模型可在当前 epoch 随时再次调用 `askPreviousScope` 发起主动追问；后续追问属于普通咨询，每次真实调用拥有独立 receipt，不阻断执行、不重新进入 pending 状态。

## [043] resetScope 档案与首问义务的原子绑定

`resetScope` 的执行必须与上一 epoch 的出站视图档案及新 epoch 的首问义务建立原子持久化绑定。

1. **不可变档案存储与原子提交**：当执行合法的 `resetScope` 时，宿主必须依据导致该次调用发生的真实 Provider 出站请求，捕获完整的 system、messages、model/variant、工具目录及必要 run 身份，并首先将其作为不可变内容地址 blob 写入持久存储（`.Herdchestra/`）。随后，在同一笔原子 durable 提交中，将 `ScopeReanchored(epoch -> epoch + 1)`、该档案的内容地址引用（`archiveRef` / digest）以及**仅从第二次成功 reset 起**的新 epoch 首问义务（`AwaitFirstPreviousScopeQuestion`）共同落盘。第一次成功 reset 只建立真正的预算和档案，不构造旧我必须答复的义务。
2. **提交确证先于内存状态更新**：内存中的活跃 scope 状态、当前 epoch 与新预算（`newScopeTurnBudget`）**只有在上述 durable 提交确证成功（Committed）之后**方可更新。提交完成前、提交失败或持久化结果 Unknown 时，严禁更新内存状态；若 Provider 出站视图不可捕获或档案 blob 写入失败，宿主必须直接报错，严禁伪造无档案的成功 reset。
3. **调用重放与幂等边界**：同一 `resetScope` 的 `toolCallId` 重放时，必须识别为已提交的重锚，直接返回既有确证结果，严禁新开第二个 epoch，严禁重复产生新的首问义务或重复追加 `ScopeReanchored` 事实。
4. **原始返回文本不变**：`resetScope` 工具在 Host 层面执行成功的原始返回值严格保持为 `"Scope reset. Objective: <newScopeObjective>"`。首问义务提示、预算倒计时与 MUST 提醒均仅在即将发往模型的请求副本中进行热修正呈现（见 [034] 与 [044]），严禁改写盘上工具原始执行结果或持久化历史。
5. **在途咨询收尾与资源结清**：
   - **再次 reset**：当模型在当前 epoch 再次成功执行 `resetScope` 时，前一 epoch 尚未完成的在途回问（in-flight consultation）立即结清并标记为 Stale，其临时执行资源被释放，迟到答案不得污染新 epoch。
   - **会话退场**：当接收到 `session.deleted` 或会话销毁事件时，属于该会话的全部在途回问进程与临时会话资源必须立即排空并终止，未落地的回答一并作废。

## [044] 跨 Scope 回问与首问协商的对称 pending 状态

系统为委托首轮协商（`negotiate`，见 `delegation` [034]）与跨 Scope 首次回问（`askPreviousScope`，见 [042]）建立对称的 pending 机制。两者共用完全相同的 pending 状态生命周期与呈现路径，仅在咨询类型（kind）、提醒文案、目标来源及解除条件上保持必要区分。

1. **唯一所有权归属**：`wanxiangli/pair-hint`（PairHint）中的 `ScopeSession` 是会话 pending 状态、预算时序与 scope 提示呈现的**唯一程序所有者**。宿主不设第二份状态机或并发权威；OpenCode 与 OMP 适配器仅消费 PairHint 计算出的中立计划并热修正请求副本。
2. **提示词与时序驱动而非 Tool Gate**：首问义务通过提示词与时序约束强制执行，严禁设立全局工具拦截门禁（Tool Gate）。在 pending 期间，模型调用任何普通工作工具（如代码修改、文件读写、只读查询等）均正常执行并产生业务效果，宿主绝对不得在 `tool.execute.before` 因首问未完成而阻断普通工具执行。
3. **持续 MUST 只通过独立强 Steer**：在 pending 未解除的每一次正常 Provider 请求，系统都另行生成一条 `attribution: agent`、`steering: true`、`strong: true` 的独立 user 载体消息。其内容由 PairHint 当前 `negotiate` 或 `askPreviousScope` pending 决定；未问就每次继续提醒。工具结果、assistant tool part、旧 reset 结果及操作员原文不承载这段提醒，也不因 pending 改写其原始或已冻结内容。该合成 steer 不冒充操作员，也不使预算作废。
4. **时序与预算完全冻结**：在 pending 期间，会话时序严密冻结：
   - 工具结果在请求副本中**绝对不呈现 `\0\uFEFF[Remaining turnBudget: N]` 预算倒计时后缀**；
   - 物理回合不递减 `turnBudget`；
   - 初始化时序不前进（`initialized` 保持为 `false`）；
   - 零预算弱提示（weak reminder）保持关闭。
5. **resetScope 自身执行自拒**：在 pending 未解除期间，模型若再次尝试调用 `resetScope`，该工具自身直接拒绝执行，返回说明文字提示模型必须先完成当前首问义务。该拒绝由工具自身返回，不产生任何 durable 提交或 `ScopeReanchored` 事实，且不影响其他普通工具的正常调用。
6. **对称性与解除条件分立**：
   - **结构对称**：`negotiate` 与 `askPreviousScope` 遵循相同的时序冻结（无预算后缀、不递减、弱提示关闭）、相同的每请求强 steer 持续 MUST 提醒，以及相同的 `resetScope` 自拒行为。
   - **解除条件分立**：
     * **`askPreviousScope` 的解除条件**：宿主入口成功登记首问有效调用的 receipt 事实后，pending 状态立即解除，预算递减与初始化时序恢复正常；不等待旧模型推理完成，不等待回答送达；
     * **`negotiate` 的解除条件**：必须等待委托者的首次真实答复作为实体 user steer 物理送达接收方会话（`PhysicalAccepted` 确认）后，方可解除 pending 状态并激活 `resetScope` 初始化（见 `delegation` [034]）。
7. **初始与后续重锚**：首次成功 `resetScope` 不建立回问 pending；从第二次成功重锚起，每个新 epoch 才建立一次首问 pending 与有效前任询问义务。首问 receipt 受理后，模型发起的后续追问属于普通咨询，不进入 pending 状态，不阻塞时序，不重复产生首问义务。


## [045] KPI 枚举与下一 Scope 预算因子

`resetScope` 必须携带当前 Scope 的 KPI 达成枚举 `kpiCompletion`：`not_started` 完全未做（0.50 倍）、`far_below` 明显不足（0.75 倍）、`partially_met` 部分完成（0.90 倍）、`met` 完成（1.00 倍）、`exceeded` 超额完成（1.25 倍）；首次没有先前 KPI 可用 `not_applicable`（1.00 倍）。`newScopeTurnBudget` 是模型估计的下一 Scope 原始预算，持久重置成功后实际预算为 `ceil(estimate × factor)`（非负且上限 32 位整数）。因子由 `wanxiangli/pair-hint` 的单一确定性函数计算，两个 Host 不得重复写倍率表。未知枚举、漏传字段、负预算在执行前拒绝，不写 archive 或 ScopeReanchored，不推进 epoch/预算；不审查 KPI 语义真实性。

证明：`wanxiangli/src/pair-hint/tests/012.test.mjs` 与两个宿主真实 `resetScope` 的参数注册、有效调用、拒绝与最终预算测试。

## [046] 跨 Scope 永存工具原始成对输入与输出

`wanxiangli/pair-hint` 按工具调用身份保留 `assume`、`negotiate`、`askPreviousScope` / `askPrevious`、`advisor` 的调用及结果。无论位于最新 reset 前多少代、与普通调用共处一个 assistant 多 part 批次、或结果独立成消息，均精确保留。对其它工具仍严格裁剪；不保留无对应调用 ID 的伪结果或其相邻普通调用。OpenCode 与 OMP 只能投影该中立过滤计划，不能按英文工具名重复维护自己的历史保留清单。实际是 `this.advisor` 等程序内成员、而不是物理 `advisor` 工具时，必须先由生产调用归属给出确证身份，不能猜测脚本文本并误判。

证明：`wanxiangli/src/pair-hint/tests/011.test.mjs`、Host 已注册请求副本与多次 reset 前缀稳定性反例。

## [047] 已落地的跨 Scope 回问与真实可证范围

OpenCode 物理实现已通过 Host `resetScope` 先写完整已捕获 system/messages/model/variant 的内容地址 blob、durable `ScopeReanchored` 与独立的 `ScopeQuestionAsked(session, targetEpoch, actualToolCallId, sha256(questions))` 事实。Host 接受首次 `askPreviousScope(questions)` 必须先确证已归档源且 digest 正确、目标 epoch 仍有效；同一事实身份重放（含清除内存缓存）不再重复旧模型调用，异问题复用 callId 拒绝。旧请求经与 `negotiate` 共用的短命只读 SDK session 重新呈现给 Provider，工具执行前阻断，回答沿同一有物理落地观察的 PromptDispatcher steer 路径送达。临时旧模型仅可提供当时上下文的新推理，不能复活隐藏思考。

上述并不表示设计稿全部实施：**锁定的 OpenCode V1 插件接口 1.18.29 的 archive 仍明确 `toolsCaptured=false`**：该版本 `chat.params` 只有参数和 model，`experimental.chat.messages.transform` 只有 messages，system transform 只有 system，`tool.definition` 仅逐项暴露描述和 schema，没有携带同一 Provider 请求的完整工具目录；所以没有证据证明工具目录与真实 provider-run ID 在同一出站请求上因果匹配；OpenCode 只可在确证 system/messages/model/variant 可靠时归档。OMP 则通过实际注册的 `before_provider_request` 对**同一 Provider 请求**捕获完整原生 payload（system/messages/model 和原始工具目录），并明确存储 `toolsCaptured=true`；成功 ScopeReanchored 后已消费的旧快照不得再被下一 epoch 的 reset 重用。进程异常时尚无独立的在途旧模型任务恢复与精确物理模型缓存命中率证据；重复 receipt 保证不再次推理，但也可能留下已受理而无实际回问的新结果，必须如实报告。`this.advisor` 是 JS 编程面内部成员时不可冒充为外部物理 `advisor` toolCall；[046] 的保留判定仅依据真正工具调用身份。

OMP **已真实实现并在本地 OMP SDK + SSE 假 Provider 验证**原生旧请求回放：从实际 `before_provider_request` 提取同一请求的 model/system/messages/tools，在 reset durable blob 和 `ScopeReanchored` 后由新模型真实调用 `askPreviousScope(questions)`，以 target epoch/toolCallId/digest 的 `ScopeQuestionAsked` 幂等登记。受理后经与 `negotiate` 共用的 `runEphemeralConsultation` 原生短命回合，在其真正的 `before_provider_request` 钩子识别短命标记、匹配当前实际 provider/model，并替换成冻结的 native payload 原始 system/messages/tools + 当前提问。只有真实命中过这次替换、同一 target epoch 仍有效且旧模型返回普通文本时才投递 `attribution=agent` 的物理 steer；真实 `message_start` 观察到同 session/receipt/epoch 的代理消息后才确认 PhysicalAccepted。同一 callId 的串行和并发重放均禁止第二次旧模型请求，PairHint 物理确认后不删除原问题身份且 session_shutdown 取消所有未完成旧 Provider lease；当前会话模型不匹配会在真正 Provider 出站前拒绝回放，绝不凭当前上下文生成伪旧我答复。没有完整旧档案、模型或原生 side-turn 能力时只可记录问询并返回 `answerDelivered=false`，不得伪造已调用旧模型。

证明：`requirements/host-boundary/tests/scope-hni-physical.test.mjs` 的 `WHAT[context-compression-047]`，`requirements/host-boundary/tests/omp-scope-seam.test.mjs` 的 `WHAT[context-compression-048]` 和 `[049]`（真注册 hook、短命 Provider 替换、工具目录、型号失配、跨重置旧快照拒绝、steer），以及 `requirements/host-boundary/tests/omp-negotiate-physical.test.mjs` 的 opt-in OMP SDK + 本地 SSE 网络请求 canary（完整原文前缀、工具目录、agent-attributed 消息、同 callId 物理请求至多一次）。本地假 Provider 不是商业 Provider 缓存命中证明。

## [048] 未交付旧模型回复时的回执状态

`ScopeQuestionAsked` 只证明模型主动提交的问题及 Host 对其接纳已持久，绝不等于旧 Provider 已收到、答复已生成、stale 判断已通过、steer 已排队或真实模型已消费。任何缺后续事实的受理回执必须报告为 pending/unknown，而不是 Answered。OpenCode 与 OMP 都已在其真实物理宿主上观察过旧模型答案送达；但外部 Provider 无幂等键时，网络错误或崩溃产生的 Unknown 效果不能严格保证「调用了一次且只有一次」；已经记录的 receipt 不得盲目重新计费。已受理而没有发送、没有答复或没有实际消费的情形必须保持明确未知状态。


## [050] 首轮初始化的强 Steer、真实后续消息与首次不回问

会话尚未成功执行第一次 `resetScope` 时没有预算，因此在每次普通模型请求副本尾部生成**独立的一条强 agent steer**，持续要求预算初始化，绝不把它附到操作员用户原文、代理答复或工具结果上；`negotiate` 尚待首次物理回答时强 steer 只催协商。首次成功 `ScopeReanchored` 之后，包含真人新指令、协商答复、旧 Scope 代理答复在内的所有后续 user 载体只作为当前范围的继续输入；新消息不触发 resetScope 催促，不归零预算，不重置初始化标记。原有本 epoch 的工具预算尾巴逐字稳定，新工具结果照常呈现当前预算。

第一次成功 `resetScope` 只产生可工作 Scope 预算与旧请求归档，不需要 `askPreviousScope`。**从第二次成功 reset 起**，前一 Scope 的工作才构成必须主动回问的先决条件，新的 `askPreviousScope` pending 使用每请求强 steer 维持，普通工作工具仍不被硬阻断；有效真实首问 receipt 受理后继续当前预算。第一次 reset 后仍可选用回问工具，但宿主不主动催促。完整时序只由持久 PrefixEpoch 的已重锚计数和 `wanxiangli/pair-hint` 的唯一状态负责人协作确定，不按 user 角色字符串或消息次数猜。

验收：`wanxiangli/src/pair-hint/tests/014.test.mjs`（首次无限重复 strong、无用户/工具后缀、真人和 agent 新消息连续预算、冷重放、首问两态）及 `requirements/host-boundary/tests/scope-hni-physical.test.mjs` 中 `WHAT[context-compression-050]`（实际一次与二次 durable `resetScope`，非工具催促、原文不变、预算冻结）。OpenCode/OMP 共用 PairHint，OMP 额外在 `omp-scope-seam.test.mjs` 的 `[049]` 实体双次重锚中验证首问触发点。


## [051] 历史有 reset 但预算无法恢复时，持续强催重新建立预算

预算不能以“未知”为可工作状态。重启／冷恢复后即使历史中包含一个或多个 `resetScope` 工具调用，若唯一 `wanxiangli/pair-hint` ScopeSession 没有确认本轮真实 `turnBudget`，不能因为历史有 reset 就把 `initialized` 设置为 true，也不能把内存默认 0 解释为“确实耗尽”，不得在历史结果显示伪造的 `[Remaining turnBudget: 0]`，更不能发零预算弱提醒。每次普通 Provider 请求都必须使用独立 agent-attributed 强 steer 要求立即 `resetScope`，直到该会话真正再次提交 reset 并获得有来源的预算。只有 budgetKnown=true 才允许正常递减、追加预算后缀和发真实零预算弱提醒。side-request 不制造强 steer、不改变 Scope 时序；用户追加任务及代理答复不会使已经确认的预算丢失。

本规则是对 [034] 与 [050] 的恢复例外：已经确认预算的会话不因新消息催 reset；冷恢复中**缺少预算证据**才持续强催，并不是按照 operator 消息次数或其文字推断。此处预算有无证据只由库内同一 ScopeSession 判定；宿主不另维护一份可覆盖真相的表。真正的重启预算精确恢复仍需独立 durable 因果事实才能证明，在那之前宁愿要求 reset，也不虚报余额。

证明：`wanxiangli/src/pair-hint/tests/015.test.mjs`（冷历史 reset／持续强提醒／无虚假 0 与已知预算自然递减）及 `requirements/host-boundary/tests/scope-hni-physical.test.mjs` 中 `WHAT[context-compression-051]`（真实 OpenCode Provider 请求副本、代理 strong steer、原始工具结果不变、重新建立预算后恢复正常后缀）。
