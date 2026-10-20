# context-compression — WHAT

## [001] 不推导容量，只向模型披露计量与声明常量

严禁从自身观察推导、估算或缓存任何模型上下文窗口大小；禁止上下文余量、模型容量表，或把字节/token 换算成窗口比例的逻辑。唯一允许的字节计量为合法的文件与进程计量。

可以披露的只有两类事实：已经发生的计量，与 provider 自己声明的常量。宿主向模型披露本次请求的计量数字（`total / used / cached`，见 [031]），以及在 chat.params 屏障处读到的 provider 对当前模型声明的 context limit（opencode 模型配置里的值，宿主只读不推）。二者都只是模型判断自身状态的 datum——模型是唯一知道哪些上下文对任务重要的主体，披露把选择权交给它。宿主不得由这些数字推导窗口大小、剩余预算，也不得替模型做出压缩决定。

## [002] 不主动预测溢出

在请求发送前，宿主严禁判断是否接近容量上限，禁止根据投影长度比例、剩余预算或累计 Token 主动选择压缩点。真实的 provider attempt 失败是唯一的恢复触发信号。

向模型披露计量（[001]）不构成溢出预测：披露不改变起点，不产生任何自动压缩动作；模型在视图声明中的选择是显式请求，不是宿主预测。

## [003] 取消独立 Y 轴与 200 KiB Blogger delta 合同

系统不再设独立的 Y 轴、Blogger、Chronicle、200 KiB delta 切块或 `MemoryPreamble` / `FrozenRecordPrefix` 物化管线。历史截断与保留完全由模型在编程面显式提交的 ANCHOR `view` 声明（`prefixUntil` / `thenRemove`，见 [031]）决定；完整 turn 的覆盖由 `view` 声明的锚点边界不证自明，不另设独立的 Y 物化覆盖证明。

## [004] 输出预算属 provider

系统不计算输出应占用的 Token 数量，亦不检查压缩比例。对输出的唯一内容校验为 `isValidTerminal`（非空且非纯 XML）。

## [005] 失败不分类

控制流仅识别快照的 `Outcome`（`Completed | Failed | Aborted`），严禁根据错误字符串或异常类型细分溢出、网络或限流等原因。

## [006] 重试不构造 prefix probe，失败预算有界

每个物理 retry attempt 拥有全新 physical identity；系统已废除 Y 恢复材料与 prefix probe 候选选择机制。provider attempt 失败后的恢复策略统一为：**第一次原样重试，第二次把该 Provider 拉黑后重试，以此类推**（见 `provider-attempt-recovery` [021]）。每次确认失败经 `ProviderFailureLedger` 记账并推进 `ProviderFailureBudget` 连续失败计数；`verdict` 为 `Exhausted`（单一 `DefaultBudget`）时停止自动重试。业务主请求（`WorkMain` 的有效成功）一旦成功，清零连续失败计数。

## [007] 按 RequestKind 分派结局

每个 attempt 的结局按 `ProviderRequestKind` 进行确定性分派；RequestKind 必须来自 attempt 的 typed context / durable receipt / accepted continuation evidence，而非角色猜测或错误文本。同种 RequestKind 的同种结局必须产生完全一致的后继动作。`InteractionRepair` 成功不得清零连续失败计数；只有 `WorkMain` 的有效成功可以清零（`BloggerMain`、`BloggerSquash` 与 `StrengthReplica` 已随 clean-break 彻底退役）。

## [008] 主工作会话不发压缩请求

主工作会话（Work Session）严禁向主模型发送请求压缩历史的指令。上下文裁剪与保留完全由模型在 [031] ANCHOR `view` 声明中自主选择。

## [013] 诊断不是控制输入

可观测诊断日志严禁作为控制流输入，不得使用任何诊断字段驱动 retry 的分支决策。

## [017] 真实用户消息与 view 取代规则

在模型显式提交 `view` 声明（[031]）取代指定锚点之前，所有真实 Host 消息均原样保留；当模型在 `thenRemove` 中显式点名某个锚点编号并提供非空摘要时，按 [031] 的纯函数规则将该锚点至下一存活锚点之间的区段替换为同槽位 `#summary` 合成消息。无标识消息保守保留，永不静默丢弃。

## [019] 重锚边界与辅助注入可见性

任何代表上下文重锚的操作（如 `ContextReanchored` 或 `TenureReanchored`），必须将旧 provider horizon 中的辅助注入可见性归零。持久化事件保留作为审计事实，但新 horizon 的初始消息不再回放旧辅助材料，仅在后续各自正常触发时逐步重新生成。

## [023] retry 全事件驱动且时间无关

Provider retry 的 correctness path 严禁读取 wall clock、构造 `TimeSpan`、调用 timer/deadline/Delay 或以超时作为状态转换。已废除等待 Blogger open producer 的阻塞逻辑，确认失败且宿主停止重试后即按有界预算推进原样重试或拉黑 Provider 后重试。

## [030] assume 调用原样保留

`assume` 是短小但高价值的判断承诺点。在未被模型显式 `thenRemove` 折叠之前，承载 `assume` 调用的原始 Host 消息与对应 result 消息原封不动保留；不得把 assumption 文本抽出来重写、不得重新渲染参数。

## [031] ANCHOR 编址、视图声明与 token 经济

进程内每条带 Host 消息 id 的 user message 与 tool result 在进入下一请求时获得稳定 `[ANCHOR: N]` 编号：N 是进程内全局单调流水，同一 session 内同一消息 id 复用同一编号；重启重置编号、现场与取代集（与 parked 同命）。每回合的注入块列出有界（最近 20 条）锚点映射，使模型能引用编号；已被取代的锚点不再出现在映射里。

生成工具接受可选 `view` 参数 `{ prefixUntil, thenRemove? }`：`prefixUntil` 是必填锚点编号，语义左闭右开——编号小于它的锚点留在缓存前缀里（便宜），编号大于等于它的段每回合按原价重新计算；传一个比见过的都大的编号即全部保留。`thenRemove` 是可选映射 `{ 锚点编号: 摘要 }`：摘要从该回合起永久取代该锚点的内容，被取代的锚点从此不再存在（退出锚点映射、不再可被点名），其摘要覆盖到下一个仍然存在的锚点为止——因此之后取代更靠前的锚点时，区段内已取代的摘要会被折进新的那段。空摘要被拒。声明只被下一请求消费一次，不落 durable；无声明时消息列表不变（已生效的取代集除外）。

没有任何锚点豁免取代：`prefixUntil` 是价格边界而非保护围栏。取代 `prefixUntil` 之下的锚点即破坏该回合的缓存前缀，是一次性代价；留下的旧段则每回合按原价计费。双语资源 `tool/js-program/view-declaration-extra` 向模型讲明这套经济模型：该删早删，决定破前缀时一次多取代。

视图应用是纯函数：取代集跨回合累计生效，被取代的行以同槽位合成消息呈现（保留原消息角色，id 派生为 `#summary` 并标 synthetic，故不再进入编号），区段内被折叠的行整行消失。无标识消息保守保留，永不静默丢弃。声明形状非法（缺 `prefixUntil`、`prefixUntil` 不是编号、`thenRemove` 不是映射、键不是锚点编号、摘要为空或非字符串）即整次调用失败（稳定码 `VIEW_DECLARATION_INVALID`）：不执行程序，也不改动任何现场。

应用后注入块回执本次声明、本回合保留/取代/折叠计数、会话累计取代锚点集与 token 经济 `total / used / cached`：`cached` 是 `prefixUntil` 之下存活段的 token 数（命中缓存前缀区，只花零头），`used` 是本次实际发出的 token 数（含摘要文本），`total` 是声明前全部消息的 token 数。计数使用与校验器一致的 `gpt-tokenizer` o200k_base 编码。

每回合的 Pair Hint 另向模型披露上一轮请求的 token 经济（provider 实际计费的缓存命中与未命中 token 数）与 provider 声明的 context limit（见 [001]），使模型对自身状态有数。
