# speculative-investigation — WHAT

## [001] 启用基线与逐工具协议呈现

未配置 Predictor 模型时，系统对本机制保持无功能基线：不装饰任何工具 schema、不追加说明、不产生委托授权、不创建 Replica，全部会话的 provider 可见字节、工具权限、retry 流程、评审终结逻辑与控制流与不存在本机制时完全一致。

已配置 Predictor 模型时，仅对第 5.2 节参与工具（read/glob/grep/js-manager/js-engineer/js-devops/edit/write/mv/rm/fetch/run 共 12 个）装饰参数：必选原生整数 `estimated_readonly_rounds` 与可选参数 `self_note`，并在原工具描述之后幂等追加事实性估计说明；原有的必选项、功能说明与安全约束保持原样。

所有未参与工具（如 fork/join/chronicle/horizon/review/fission/terminal 类工具/assume 等）及未判定动态工具均保持原有定义，无任何本协议增量，也不截取其同名业务字段。

估计填 0 表示当前批次完成后没有可合理展望的连续只读查证，与"未配置 Predictor"是两个可观察区分的不同状态；启用不依赖任何人工确认、指纹、外部开关或额外设置。

## [002] 来源判定、批次封口与参与子集 max

委托授权只能形成于同一个真实主模型 provider 响应内的完整工具批次：该响应按原始调用顺序冻结，且所有调用都有且只有一个 result；结果乱序到达时按原始调用顺序归一，孤儿结果、重复结果、重复 call id 或跨 user/provider 边界拼接的结果一律不构成有效批次。批次全部结果齐全前，一个 Replica 也不创建。

整批封口后，宿主只对参与调用的子集进行校验并取其最大值：
- 若整批全部调用均属不参与工具，判定为无估计机会（NoEstimateOpportunity），不读取、不校验其字段，也不对空集合求 max；
- 若参与子集的估计全为 0，判定为明确零估计（EstimatedZero），不产生授权、不创建 Replica；
- 若参与子集存在合法正数，授权轮数 N 取该子集 `estimated_readonly_rounds` 的最大值；0 不否决同批其他调用的正值；同批取值不一致是合法输入，不得要求一致或改写 max；一个完整批次至多形成一次授权。
- 不参与调用的同名字段不读取、不校验、不消费、不截取，原样保留在业务参数与历史证据中。

当前主模型已经生成的工具调用不计入预算，照常执行一次；它的选型与参数推理成本不发生第二次。

授权的形成还要求：来源是新鲜的真实 owner 输出；owner logical run 未被替代；该授权尚未 Bound 或 Closed；待消费的是合法普通 WorkMain 续行；来源不是 Replica、其他 InternalLeaf、interaction repair、显式恢复特殊分支或 prefix probe；EventStore、Host 边界与进程 fuse 健康。

来源身份取完成批次的 emitting assistant，不能取下一次外发请求的 assistant placeholder。来源 assistant 的 Host parent physical user message 必须与当前合法输入相符；新用户输入之后，旧批次中的正数估计不得冒充新来源。

准入不含任何经济或统计判断：不存在收益估计、成本模型、证据样本量、holdout 分组或预测得分作为条件。同批参与调用出现非法估计值（缺失、null、字符串、负数、小数、布尔、越界）按新调用参数错误处理：整批不产生新执行，不取合法子集假装成功，也不把非法值静默规范化为 0；短记形态不构成非法值。非只读的来源批次不否决委托——当前操作照常做完，委托针对的是接下来；Replica 输出中的正预算不能产生二次委托。

## [003] 事实性估计到只读执行上限的转换

`estimated_readonly_rounds` 是主模型对任务所处位置的事实性估计，单位为整批工具完成后预期后续连续只读查证的模型请求次数；它不是意图承诺，不是分工指示，也不是已证实的事实。

该事实性估计经一次显式转换（toExecutionBudget）成为内部只读执行上限。内部计数只按运行时真实接纳的外发请求记账：一次请求可并行产生多个只读调用；纯文本结束占一轮；已外发后失败的一轮仍占一轮；同一请求的重复 transform 或 terminal 通知不重复计数。Host 截断、移除旧可见批次或发生普通压缩后，已用轮数不降低，也不得从变短的对话历史重建为更小值；owner 镜像中的旧调用不计入本次。

达到上限后禁止第 N+1 次请求外发；第 N 轮已开始的工具结果必须收完；不追加额外总结请求。Replica 不新增自动 provider 重试，重复通知与真实重新外发必须分清，不得把重发当免费轮次。

启动消息的防重发与 provider 请求预算分别记账：启动消息只认领一次，不扣调查轮数；每次 provider 外发只在 transform 准入时扣一轮。常驻会话复用时保留的历史响应不得让首个新请求重复扣费。

常驻副本保留的旧决策工具结果与纯文本终态只属于历史。新决策的材料收集从其实际启动 physical user message 之后开始；旧批次不重复进入 Prepared，旧纯文本不提前结束新请求。决定完成只消费本决策的 completion cell，不能凭相同 SessionId 收束后来的决策。

上限是安全门禁而不是必须做满的配额：同伴可随时提前结束，提前交还不是失败，主模型也不必在预算用尽后立即修改代码。

## [004] 同伴：同一 owner 身份的只读内部执行

同伴以 `InternalLeaf × Attached(owner, StrengthReplica)` 构造，继承 owner 的 participant、Role、Persona、provenance/version 与会话语言。每个 owner 恰有一个**常驻**只读副本会话：该会话随 owner 存活而被复用，不随单个决策结束而终止或重建，因此 provider 侧看到同一会话并复用其前缀缓存；常驻会话占用一份 provider 容量，直到 owner 结束才释放。常驻会话的消息双向同步、只增不替换：新决策不重发已镜像的前缀，只把 owner 自上一位点以来新增的 delta 追加进常驻会话（`mirror(N) ⊕ replicaTurn(N) ⊕ mainDelta(N→N+1) ⊕ …`）；位点作为持久投射随 `DelegationBound` 记录，故前缀在 provider 侧保持稳定并被缓存复用。变化的是执行用途、模型目标、可见工具集合与短期控制权，不是"扮演另一个人"。

模型目标经明确的只读委托用途，由唯一 MJS 调度权威从 Predictor 模型池选择；participant 与 Role 不改写，用途不从工具参数、用户文本、模型自述或角色名推导。Predictor 与 owner 配成相同模型是合法状态，不因模型名相同而关闭。

创建与常驻复用共用 owner 排他准备权：在第一个 Host await 之前认领，同一决策的并发准备共享结果，不同决策不得先建会话再争登记。取消与删除须阻止仍在准备中的 child 外发。每个决策只订阅其后续终态，完成、取消或卸载后移除订阅；旧终态通知不能清除新绑定。

同属一个 git common-dir 运行时的所有插件实例（如 root 根工作区与各 manager worktree 实例）必须共享同一个 Predictor 活跃登记表（`StrengthRuntime`）与物理协调器（`StrengthReplicaRuntime`），由统一的运行时路径键进行引用计数管理。某一个工作区实例的卸载（dispose）仅递减引用计数，不得处置共享的 predictor 或清理另一实例的活跃绑定；仅在最后一个实例卸载（引用归零）时方才释放底层资源与 live 登记。

进程表只是常驻身份的缓存。重启恢复仅认 durable DelegationBound 记录的 ReplicaSessionId，并核对 Host 中的 agent、title 与物理 family children；无关联不收养其他会话，多个候选、属性冲突或查询失败明确拒绝，不以查询失败为丢失证据再建一个。仅确认原 child 永久丢失时才替换，并释放原登记的容量。物理 parent 一律压平到 Host 证明的 family root；逻辑 owner、身份继承与语言仍属于实际工作的 owner，两者不可混用。

同伴只能调用唯一专用的只读 JS 编程面 `js-predictor`（能力严格限定为 {Read, Glob, Grep}）；`read`/`glob`/`grep` 原生工具不在同伴的可调用集合内，全部只读查证经该 JS 面完成。其模型可见工具 schema 与底层执行门禁同源：会话级权限规则在 deny 全部工具后精准放行此项。尝试写入、调用其他工具、借 fork/MCP/通用 JS 工具（如 `js-engineer`、`js-devops` 等）绕过只读能力，均 fail closed 且不产生任何实际效果；shell 命令不因"看起来只读"而入列。

准入与副本能力均采用现行角色集合 `Roles.all`，不得各自维护角色白名单。现行角色的副本能力一律为上述只读集合；普通请求的角色权限不变。Blogger 等内部会话仍由 root-work、request-kind 与防递归门禁限制，不能靠角色名称发起嵌套委托。

启动消息与系统约束共用双语资源 `delegation/readonly-investigation`。子会话中显示实际只读工作指令，不再发送裸 `Continue.`。镜像变换会替换 provider 的消息流水，所以 system 约束必须保留；system transform 必须原地修改 Host 持有的数组，不能只替换钩子输出对象的字段。

删除副本必须填 0 的假条件：同伴在共享 schema 下按同一事实性含义填写，防递归靠真实身份（Replica 身份在准入端无权发出新委托），不靠填写约定。同伴填写的正数、推理文本与未执行计划不产生嵌套委托。

## [005] 真实完整交换与 digest：无专属字节上限

回传给主模型的材料包含同伴真实的只读工具交换与实际输出的 assistant text：每个候选帧保留 request batch 边界、原始调用顺序、原始 arguments、真实执行结果与内容 digest，call/result 严格一对一配对。assistant text 在主模型上下文中降为 reasoning，同伴原生 reasoning 不回传；正文不是工具证据，不是待主模型采信的研究报告。纯文本输出仍是提前结束信号；即使之后没有新的 provider 请求，其终止正文也必须被收集。混合工具消息中的正文按原文和部件顺序保留，未完成工具批次的正文不提前发布。
来源整批可能包含非只读调用；Frame 构造必须过滤保留其中的只读交换（read/glob/grep/js-predictor），而不是直接因整批混入非只读工具而拒绝整个帧（UnsupportedTool）。只有过滤后既无合法只读交换也无实际正文时，才按无材料（NoMaterial）正常处理。
过滤后仍有材料的批次按原顺序连续编号为 1..N；被筛空的首批或中间批次不留下编号空隙。材料编号不承担 provider 请求预算记账，失败请求与真实外发轮数仍由准入账本记录。

同伴自己看到的交换保留其真实调用名 `js-predictor`；回传注入主人会话时，该交换按主人的角色投影为其自己的 `js-<role>` 工具名（如 devops 主人即 `js-devops`），使主人看到的是它本可自行调用、结果形态一致的证据，而非一个它无权调用的工具。该重命名只发生在面向主人的投影渲染处，Frame 的材料 digest、持久化 payload 与同伴自身的 transcript 均保持原名不变。
候选首次投递与后续 replay 使用同一工具命名规则。逻辑 call/result 行在追踪与上下文投影阶段保持完整，最终 Host 外发边界才合并为原生 assistant completed `tool` parts；不能把 Host 不消费的通用 `tool-call/tool-result` 行当作已经交付。真实工具输出始终保留原始文本字节，内容看似 JSON 也不得解析成对象、数字或 null。

新字段随真实 arguments 保存。不存在 Delegate 专属的字节、token、短记长度或批次大小上限，不存在按长度丢弃、保留小前缀、"过大退回零步"或等价替身规则；超过任何历史大小的完整交换仍可构建、持久化、映射与恢复。

工具原有截断、provider 上下文限制与主模型原有压缩照常工作；Frame 保存并回放实际返回给模型的结果（含原有截断标记），不得先对未截断全文计算 digest、回放时再另切一刀。digest、byteLength、UTF-8 字节计量与 payload_refs 保留，服务完整性、存储与观测：长度或摘要被篡改必须拒绝。

正文与工具交换一同参与 frame 摘要、字节数校验和持久化；无正文的既存 frame 仍可诚实重放。首次投递与 Promoted replay 均将正文投影为 reasoning，XTrace 覆盖范围与恢复匹配包含这些 reasoning 部件。

## [006] 授权与 Prepared 的持久化前置

一份授权至少绑定：DecisionId（由协议版本 2、owner logical run 与来源 provider run 确定性派生，不按工具完成顺序或未来目标请求派生）、OwnerSessionId、owner logical run identity、authority root 与来源 physical user message、发出该批的 SourceProviderRun、固定顺序的完整 SourceToolCallIds（包含整批中不参与调用的 ID）、RequestedRounds（参与子集 max）与 ContractRevision（当前协议版本 2）。授权不增加 SelfNote、Hint 或 TrustScore 事件。

持久化时机：来源整批完成并取得真实 owner 新输出证据后、外发副本前，写入 `DelegationRequested`；为合法普通续行冻结 target 与 mirror 后、副本首次外发前，写入 `DelegationBound`，固定 target、ReplicaSessionId 与 anchor digest；候选材料先写 `Prepared` 并持久化引用，之后才可被任何主模型可见路径消费。大对象以内容哈希 `payload_refs` 命名，其字节内嵌于同一条事件行（[durable-events-012]），不引入私有存储。

写入失败或状态未知时 fail closed：先解析既有事实，未证明已提交不得外发；不得把存储错误降级为内存里的 consumed。Bound 必须先创建尚未发送 prompt 的空 child 并持久化成功，才允许发送 prompt 与进入模型准入；Bound 写失败时清理空 child，创建空 child 不得预占模型容量。

同一来源重复提交相同 Requested 幂等；同来源改变 N、call 集合或 authority 是冲突。重复 Bound 相同 target/child/anchor 幂等；改其中任何一项不得偷偷开启第二个副本。RequestedRounds 只由 Requested 持有，Bound 与 Prepared 引用同一 DecisionId，不各自复制可修改的预算字段。

## [007] 消费证明、Promotion 与关闭路径

只有协调后的轮次证据明确证明 `turn.ProviderRun` 等于候选的 TargetProviderRun、且该运行产生了真实非空输出时，才可追加 Promoted；"tool.after 跑过""child 完成""owner session 还活着"均不能替代消费证据；请求尚未发起、纯传输错误、空失败或已终止的运行不得 Promotion。Promoted 必须引用与 Prepared 完全一致的 digest 与材料；写入状态未知时重新解析，未证明前保持 fail closed。
Host 的整段工具循环未结束，不等于其中每次 provider 请求都未完成。下一次主变换在 XTrace 捕获前读取真实完整快照：仅当目标 assistant ID、physical parent、完成标志与正常 finish 均吻合，并有模型真实文本、reasoning 或具备调用身份和工具名的原生 ToolParts，才确认该次请求消费并提交 Promoted。无完成证据、错误或取消 finish、错误 parent、错误 owner 与仅有 bookkeeping 的快照均不得升格；不能只等待工具循环最终一条 assistant 而遗失中间目标请求。

无材料结束、不可继续、取消、被替代、恢复放弃、或协议升级替换时写 `DelegationClosed`：明确记录关闭原因，同一授权不得在后续 transform 再次启动，并实现终态防重；成功路径（Prepared → Promoted → Traced）不额外写 Closed；材料废弃使用 Abandoned。

投影以明确联合类型表达 Requested、Bound、Prepared、Promoted、Traced、Closed/Abandoned，拒绝非法状态跳转，不用布尔值组合猜测状态。

## [008] Promoted replay、XTrace 与普通压缩闭包

目标请求中的候选不进入 XTrace 捕获范围。Promotion 完成后的下一次主变换必须在 XTrace 捕获前，把 Promoted frames 确定性重建到其因果位置（目标 assistant 输出之前）；随后的 XTrace 捕获将其纳入持久化时间线并记录 traced 游标范围。Promoted frames 在被后续压缩机制完整覆盖前保持可 raw replay；Prepared 在 Promotion 前严禁进入 XTrace、Companion 或持久化语义历史。

老字段历史不重写，历史中的旧字段与格式原样保留；注入的 Replica 记录不是新来源，不被误认作 owner 的新输出。

Delegate 不新增压缩阈值，不主动"压到可以委托为止"，不禁用 Host/工具既有截断。普通压缩或 prefix probe 不消耗预算，不把同一 logical continuation 上未 Bound 的请求判成失效，Requested 事实不因 messages 变短而消失。

主模型确实换成另一个 physical target 时，按既有 exact-target 恢复/废弃规则处理，不把原 Prepared 冒名渲染给新 target。回传注入后形成的实际完整请求仍进入常规上下文大小/失败/压缩路径；一般溢出恢复不得反复重送同一超大候选。

## [009] 镜像、ID 重定位与未来调查展望短记

同伴的 provider 消息基础是 owner 冻结点上的语义投影加本决策已完成的局部批次：完整 call/result、原始 arguments（含 `self_note`）与调用顺序全部保留；owner 的 wire-local call id 不得复制，必须确定性重定位为决策内局部标识并保证语义不变。

主模型原有 text 与 reasoning 的镜像语义不变。已回传正文再次镜像到 predictor 时，以 Host-only 的稳定合成消息身份识别来源，还原为 predictor 的原始 text，并从 child 历史合并其自身 reasoning；不得再插入一份相同正文，也不得把普通 Main reasoning 当作副本正文。来源坐标不进入 provider 正文。最终 Host 编码必须携带当前请求模型的传输坐标，不能因合成 assistant 消息缺失模型字段而将 reasoning 再转成普通 text。

`self_note` 定义为面向未来调查的简明展望（一至三句，聚焦于准备核对的材料、关系及停点），只沿一条既有路径自然可见：原始工具调用记录 → 冻结 owner 对话 → ID 重定位 → 同伴可见对话。不得复制到 system prompt、bootstrap、额外 user 消息、子会话启动参数或新 hint 事件；不为短记保留特殊上下文窗口、补发消息或独立缓存。

同批多个正数调用的短记按原始调用顺序各自保留；只对整数取 max，不对短记取 max、不挑选最大预算对应的那一条、不合并成提示段、不丢失其他调用的短记。

当前候选在冻结之后产生，严禁反射回生成它的同伴会话。常驻会话的 transcript 不复用旧决策的语义上下文：新决策只追加 owner 自位点以来的 delta，不重发已镜像前缀；旧决策的产物不作为本决策的输入。

短记是未执行展望，不是已证实的事实，也不扩大权限；常规压缩使其不再可见时，不通过专属通道重新注入。

## [010] 授权一次性、跨版本防重与恢复

一次授权只被一个执行消费一次：Bound 之后，同一 DecisionId 不因重试、恢复、重复回调或更换 target 获得第二份预算。跨版本按真实来源防重，同一真实来源即使按新版本计算 ID 也绝不产生第二份预算。

恢复按持久化事实决策：没有 Requested 且存在本版当前真实来源批次时，可重新记录同一请求，不得扫描任意旧历史找正数；旧版本已记录但未 Bound 的旧 pending 请求，按协议替换原因明确关闭，主模型继续，不重新执行；Requested 未 Bound 且 logical continuation 未失效时重新准入，被新用户输入或 authority 替代则 Closed；Bound 之后 Prepared 之前进程内原执行已不存在时，写 Closed、主模型继续，不重跑相同预算；Prepared 且 target 尚可合法消费时，加载原 payload 渲染相同材料，不重跑只读工具；Closed/Abandoned 不启动、不复活；追加状态未知时查询解析，未证实前不外发。

Bound 之后崩溃但尚未得到 Prepared，允许损失本次同伴调查机会；不得为挽回它引入自动重复消费。晚到的旧 child 回调必须先恢复/确认身份边界，不得落入普通 owner 分支；没有 Bound 的空 child 从未获准发 provider 请求，只作为空资源清理，不得补发 prompt。

重复 transform 在启动前重读 canonical 状态：仅 Requested 能启动；Bound 共享原决策的语义 completion，Prepared 重放已存材料，已关闭状态不重新准入。语义 outcome 保留到 durable publication/closure 确认之后，物理尾部先清理不能让并发消费方误判为“进程已丢失执行”。

是否启动、启动几轮的判断，不存在任何统计预测器、成本公式、收益门槛、学习样本、control holdout 或 rollout 分支参与。Predictor 模型池槽位保留为本机制的模型配置位。

## [011] 失败、取消、熔断与参数错误边界

严格区分普通参数错误与严重不变量错误：
- 参与调用的估计字段类型错误、范围非法、或新旧字段混用，均属于新调用参数错误，仅拒绝当前调用/批次的准入，不执行委托，绝不触发全进程 fuse；短记形态不构成参数错误；
- 只有出现持久化歧义、投影冲突、权限突破、材料 digest 不一致等严重不变量失败时，才执行进程全局熔断；熔断在当前进程生命周期内保持生效，已完成的 Promoted 历史不受影响。

终止只来自显式因果事件：达到上限、真实 provider turn terminal、owner/operator 取消或删除、授权按明确原因关闭。不得以 elapsed time、deadline race、sleep 或超时先后决定是否收集或取消；不增加 deadline、按毫秒竞争的提前结束或新 failure budget。
只读叶子会话（Replica）不拥有也不运行交互修复（InteractionRepair）。当模型以 finish=stop 提前返回且正文为空（或仅含活动骨架）时，宿主分类器将其判定为交互修复候选（TurnNeedsContinuation EmptyFormalText）；在只读同伴的所有权边界内，该物理停止信号必须按正常提前结束（TextCompleted）收束并清理物理尾部，不能将其作为无限等待的进行中状态忽略，从而导致绑定的 Delegation 永久卡死在 Bound。

普通 Replica provider/tool 失败只结束当前委托决策，主会话正常继续；已完成且通过结构/权限校验的完整前缀可以回传，残缺批次不补造；普通文件不存在不触发进程级熔断。

上限用完与主动结束都保持语义终态与物理尾部分离：先停止接纳新请求，再按真实 Host terminal 清理 child 与租约；语义结束后的晚到 callback 仍识别为 Replica，不走普通 Work，资源只清理一次。

owner 取消或删除时级联取消并释放 Replica 与 capacity fence，未消费候选不 Promotion。

常驻 SessionId 不是决策终态的充分证据。精确 Host turn 必须匹配本决策实际启动的 physical user message；晚到的前一决策 turn 只被识别，不得收束新决策。模型容量拒绝发生在外发前时，该决策没有可等待的 provider terminal，必须立即清理其 live binding，不能使 owner 永久忙碌。卸载后的 coordinator 不再接受新准备。

带有真实 assistant parent 证据的完成通知若先于 physical 绑定到达，按 physical 暂存；实际 dispatch acceptance 或 transform 确认绑定后，只重放相符终态。通知不能自行指定本决策的 physical，也不能让已完成的请求多发一轮。实例卸载只释放该实例拥有的模型租约；常驻 predictor 及其仍需继续的 owner 的租约受共享协调器保留，不能被某个实例的通用 session 清理提前释放。最后一个实例卸载时，协调器同时交还这些 owner 与 resident 的租约。

## [012] 模型可见协议：事实性估计与建议性短记

面向模型的协议完全剥离任何执行分工、信任建立、保留控制权或同伴指派的动机叙事，不要求模型理解宿主的 max 调度算法。

参与工具可见 schema 严格定义为：
1. `estimated_readonly_rounds`：必选原生非负整数（0..2147483647），表示当前整批工具完成后，预期后续连续只读查证的模型请求次数。0 表示当前批次完成后没有可合理展望的连续只读查证，或下一步已到达实质修改、命令执行、用户确认、关键权衡或结论边界。
2. `self_note`：可选的未来调查展望（string）。
   - 该填不填、不该填填了、填了空白或非字符串，一律不视为失败：字符串短记原样保留，其余按缺失读取；
   - 建议在 `estimated_readonly_rounds > 0` 时用一至三句话说明准备核对的材料、消除的疑问与停点；
   - 未参与工具无此两字段，亦不接纳此类输入。

中英文工具说明幂等追加在原描述之后且保持稳定：不含剩余轮数、随机标识、价格或时间戳。真实工具结果保持工具证据身份，实际 assistant 正文只降格为 reasoning；同伴原生 reasoning 不回传，不自造正文或未执行工具结果。

## [013] 逐工具断言的真实 Host 集成证明

本版不提供生产 DryRun / 影子执行模式；交付正确性只能由本版真实 Host 集成证据证明。只在 mock 对象上增加字段、只写 canary 日志而无断言，均不算证明。

真实 Host canary 必须按第 5 节的逐工具判定清单进行独立断言，不再要求对每个可见工具全量加字段：
- 证明已配置 Predictor 时，仅第 5.2 节参与工具带有必填估计与可选短记；
- 证明所有不参与工具（fork/join/chronicle/terminal/fission 等）原样无增量；
- 证明未参与工具的同名业务字段不被侵犯；
- 证明同一工具在真实 provider wire 上原参数执行，且原始 arguments（含协议字段）同源恢复进入下一次历史。

provider-wire 证据必须观察 owner 与 Replica 的实际 provider/model 与请求用途，证明后者实际使用用户配置的 Predictor 池。同 provider 容量为 1 时，父等子不得死锁、不得双占；取消时 capacity fence 与 child 正确释放。两个 owner 并发执行不串 schema、call id、授权、预算、结果或模型资源。

E2E 同时覆盖正常完成、提前结束、自然截断/压缩与恢复。旧三参数 routingProtocol 必须被明确拒绝。

### 可证明的边界

证明对象是宿主状态机，不是模型必然给出正数估计或 provider 必然成功。初态为空或由唯一、无冲突的 durable/Host 证据恢复；每个合法转换保持以下不变量：

1. **单 owner 单执行**：准备权在首个异步边界前排他认领；相同决策共享准备与 outcome，不同决策不能同时创建或登记。
2. **来源一次性**：DecisionId 由真实完成来源推导；只有 Requested 能消费预算，已 Bound 或已关闭的来源不重启。
3. **物理压平**：创建和查询共用 Host 证明的 family root；查询失败或祖先环不产生会话，不改变逻辑 owner。
4. **材料与预算局部**：预算单调且仅在真实请求准入时扣除；只收集本次启动消息之后的完整 readonly 批次，旧材料不再刊发。
5. **终态不串代**：physical request 身份与 completion cell 同时限定终态；清理只作用于本次登记，完成订阅与 outcome 都有明确释放点。

在 Predictor 已配置、估计合法且为正、WorkMain 准入成立、持久化/Host/容量正常、各次模型请求最终返回合法工具结果或真实终态的前提下，决策沿 Requested → Bound → Prepared 或 Closed 推进，不因宿主的重复回调、常驻历史、进程重启而永久搁置。配置缺失、0、模型主动提前结束、容量拒绝及外部失败均有明确关闭或拒绝语义，不可宣称为“无条件必然启动”。

## [014] Predictor 配置是唯一启用依据

启用状态只从实际模型配置的 Predictor 槽位派生：槽位不存在或候选为空即未配置；目标合法且非空即已配置；目标结构非法是配置错误，必须明确报告，不得静默变成"未配置"。工具装饰与委托准入共用同一份只读配置存在性查询，不维护第二份 enabled 真相。

不存在独立 enabled 开关、环境变量、二次 opt-in、人工 canary 指纹或生产消融选项能否决或替代该配置；外部特性注册不得把本特性变成 Predictor 配置之外的第二个启用条件。

容量暂满或 provider 暂时不可用是运行时调度状态，不是配置缺失，不改变已配置状态，不来回改 schema；具体请求按既有等待、失败与取消规则处理，不得偷换成 Predictor 缺失，也不得回退到 owner 的模型池。

未配置 Predictor 时不装饰任何字段、不追加说明、不产生新授权、不启动 Replica。移除配置只影响新委托：未 Bound 的请求在新配置加载后明确关闭；已 Bound 的物理执行遵守原有 target 与结束/取消规则；Prepared/Promoted 恢复独立于模型配置继续。

协议版本是代码中的稳定契约版本，不由环境变量改变。合法历史（既有 EventStore、XTrace 与会话）不因本机制被删除或篡改。

## [015] 历史分界与迁移规范

本规范明确区分两类历史场景：
1. 古老 K1/K2 预测材料的离线迁移：旧存储未迁移就拒绝以新运行时继续消费；已 Promoted/Traced 的旧只读材料通过离线一次性脚本导入为"已接纳历史材料"（DelegationHistoryImported），保留原因果位置、digest 与 trace 范围，旧档位仅作为历史证据落地，绝不伪造主模型从未发出的授权，亦不产生副本。
2. 本次 v1→v2 输入协议修订的历史兼容：已持久化的历史事件（如已有的 Requested/Bound/Prepared/Promoted）和调用记录按原样读取，保留旧字段名与旧数据，绝不改写磁盘历史；运行时新输入严格执行 v2 协议（只接纳 estimated_readonly_rounds），拒绝新输入中的旧字段。

迁移是离线操作，运行时永不静默双解析。迁移工具按登记表校验版本参数，保证升级的确定性与可审计性。

CLI 先校验参数、登记版本、备份路径和活库禁令，再加载迁移所需运行时；前置拒绝不得加载 EventStore/Strength 业务模块或创建工作副本。参数非法且 dist 缺失时，以参数拒绝为准。合法 dry-run 仍加载真实模块、完整规划并保留原备份字节与库存。


## [016] 协议常量、逐工具分类与严格成对输入合同

推测性调查只读轮次估计与短记协议由代码级纯逻辑共享合同（`InvestigationEstimateContract`）独立定义，不依赖 OpenCode/Plugin、模型配置、日志、文件系统、EventStore、租约或 runtime：

1. **协议常量与字段标识**：
   - 协议修订版本 `ProtocolRevision` 是代码中确定的稳定整数常量 `2`（严格大于 1），由代码确定，不得从环境变量、模型参数或运行时配置读取；
   - 轮次估计字段名为 `estimated_readonly_rounds`，建议性短记字段名为 `self_note`。

2. **逐工具判定（classifyTool）**：
   工具策略采用严格的三态联合类型，禁止任何形式的前缀匹配（如 `js-` 前缀）或名称模糊匹配（如包含 read/search）：
   - `EstimateAfterCall`（参与工具，共 12 个）：`read`、`glob`、`grep`、`js-manager`、`js-engineer`、`js-devops`、`edit`、`write`、`mv`、`rm`、`fetch`、`run`。调用完成后允许主模型提供后续只读查证估计；
   - `NoEstimate`（不参与工具，显式白名单共 24 个）：`fork`、`resume`、`commission`、`join`、`horizon`、`review`、`suicide`、`fission`、`open-terminal`、`send-terminal`、`read-terminal`、`signal-terminal`、`skill`、`todowrite`、`assume`、`defer`、`publish`、`chronicle`、`js-bookkeeper`、`js-predictor`、`bash-honeypot`、`invalid`、`js-orchestrator`、`js-blogger`。本协议不向其装饰任何字段与说明；
   - `Unreviewed`（未判定工具）：所有不在上述 36 个固定名称表内的工具（包括带有已知前缀的衍生工具名如 `read-extra`、`globbing`、`grepper`、`edit_file`、`writer`、`run_command`、`fetch_data`、`js-devops-v2`、`fork_child`、`resume_parent`、`custom_tool` 等）一律判定为未判定，保持原有业务行为，不增加协议字段。

3. **入参容器与协议混合排斥**：
   - 参与工具的参数容器必须是普通非空、非数组的 JavaScript 对象（`isPlainObject`），传入 `null`、`undefined`、数字、字符串或数组等非普通对象一律拒绝；
   - 严格排斥旧协议字段：只要入参容器拥有自有属性 `delegate_readonly_rounds`（无论单独存在还是与新字段同时存在），均作为协议字段混用明确拒绝，不进行旧协议容错或降级。

4. **轮次估计的数值校验与范围**：
   `estimated_readonly_rounds` 必须是原生 JavaScript 数字类型（`typeof === 'number'`），且为有限整数（`Number.isFinite` 且 `Number.isInteger`）：
   - 合法范围严格限定在闭区间 `[0, 2147483647]` 的非负整数；
   - 拒绝字符串形式的数字（禁止隐式类型转换）、布尔值、数组、对象、null、缺失属性；
   - 拒绝负数、浮点数/小数、NaN、Infinity 以及大于 2147483647 的越界数值；
   - `-0`（负零）按原生浮点比较等价于 `0.0`，在语义上归一为整数 `0`；
   - 合法的 `EstimatedReadonlyRounds` 值可通过显式单向转换 `toExecutionBudget` 转换为内部只读执行预算 `ReadonlyRoundBudget`。

5. **建议性短记与原始文本保真**：
   - `self_note` 是纯建议性短记：该填不填（估计为正却缺失）、不该填填了（估计为 0 却携带）、填空白或非字符串，一律不视为失败；
   - 字符串短记完整保留原始字符串（含首尾空格、制表符、换行与特殊字符），不进行 trim 截断或任何修改；非字符串短记按缺失读取，不报错、不修正；
   - 短记不参与批次判定：同批任何调用因短记形态产生的差异，不影响该调用估计的合法性。

6. **错误类型辨识度**：
   解析错误至少能精准区分以下独立失败原因：
   - `MissingEstimate`：缺失 `estimated_readonly_rounds` 估计字段；
   - `WrongNumberType`：估计值不是 JavaScript 原生数字类型（如为字符串、布尔、对象、数组、null）；
   - `InvalidRange`：估计值不在合法范围（负数、小数、NaN、Infinity、超过 2147483647）；
   - `MixedProtocolFields`：入参携带旧协议字段 `delegate_readonly_rounds`；
   - `InvalidArgumentObject`：入参不是合法的普通对象。

7. **字段所有权与透传**：
   协议字段 `estimated_readonly_rounds` 与建议性字段 `self_note` 的解析责任仅归本协议及 12 个参与工具所有；短记本身不校验、不拒绝；未参与（NoEstimate）及未判定（Unreviewed）工具的同名业务参数原样透传，本协议不读取、不校验、不拦截、亦不破坏其原有的业务参数传递与持久化证据。

8. **三消费端单一判定来源与行为一致性**：
   逐工具判定函数 `classifyTool` 是全系统唯一的分类来源，供 schema 装饰端（`ReadonlyDelegationContract.decorateDefinition`）、调用边界端（`PluginHooks` 的 `toolBefore`/`toolAfter` 参数暂存与收窄拦截）和来源批次端（`StrengthDelegate.tryCapture` 批次估计聚合）三处共同使用，严禁在各端重复硬编码工具集合或 `Set<string>`。对任何工具名称（至少包括参与工具 `read`、显式不参与工具 `chronicle`、未在表内的未知工具 `js-foo-unknown` 与合成占位工具 `invalid`），三端在「是否装饰协议字段」、「是否在调用边界参与收窄与暂存」、「是否在来源批次中读取并聚合估计」上的行为判定必须完全一致。在协议未开启（未配置 Predictor）时，调用边界端与来源批次端均不拦截、不剥离、不校验任何工具的参数，参数原样透传；在协议开启（已配置 Predictor）时，调用边界端仅对参与工具执行必选校验与剥离隐藏，且对完全缺失估计字段的参与调用无条件按参数错误拒绝，绝不带缺陷放行或静默退化为无估计机会。

9. **已知工具差集清点与未判定候选显式登记**：
   系统通过机械方式清点仓库已知工具名权威来源（`StaticTools.knownToolNames`）与 `classifyTool` 已判定工具名单的差集。任何未判定工具必须显式可见并登记为待审阅候选；门禁严禁 fail-open（空差集无条件放行通过），亦不得在存在差集时未经登记无条件失败，防止新工具加入仓库时通过“未知默认不加”而静默逃避协议审阅。

## [020] 主副本双射恢复：骨架保序、言语还原与前缀保真

常驻副本与主人之间的消息双向同步及恢复重建由纯逻辑双射机制（`TwinBijection` / `TwinBijectionSurface`）严格保证，确保在追加式增长过程中 provider 侧的前缀缓存有效复用，并在恢复投影时维系严格的因果与内容不变性：

1. **骨架与主人保序（preservesOwnerOrder）**：
   主人消息序列构成双向同步的骨架。在恢复（restore）重建生成的请求序列中，主人原始工具交换（tool-call 与 tool-result）的相对顺序与内容必须严格完整保留，不发生重排、置换或丢失。

2. **言语还原不漏不造（dropsNoSpeech 与 introducesNothing）**：
   副本自身在查证过程中产生的纯言语消息（包含 text 与 reasoning 类型的部件）必须被完备恢复到重建序列中，绝不丢失任何副本言语；同时，恢复过程除将副本言语归位到记录的间隙之外，严禁凭空构造或捏造任何未曾发生的额外消息或部件。

   已同步正文由稳定合成 Host 身份定位，回到副本时恢复 text 类型，并与 child 中的原始正文和自身 reasoning 合并一次。混合工具消息中的正文也遵守此规则。相同正文在不同批次实际发生两次时必须保留两次，不能按字符串全局去重；普通 Main 正文与 reasoning 不因内容相同而被误认。

3. **间隙因果锚定**：
   在某一间隙（gap）内产生的副本言语消息，在恢复后必须确定性锚定在对应主人工具交换之前，保持副本调查先于后续主人决策的严格因果先后关系。

4. **追加式增长前缀保真（extensionIsPrefix）**：
   常驻会话随主人演进而只增不替换。当主人与副本历史仅发生追加式扩展（追加新的主人工具交换、副本言语或查证调用）时，后一轮次恢复出的请求消息序列必须严格以先前半程恢复出的请求消息序列为其前缀（Prefix），保证 provider 侧能够逐字节复用既有的 prompt 缓存。

5. **非平衡历史容错**：
   当副本历史与主人历史出现交换数量不对齐（如副本工具交换多于主人已记录交换）的非平衡状态时，恢复逻辑仍必须稳定产出合法的可用请求序列，且同样严格满足骨架保序与言语不丢失，不得崩溃或抛出异常。
