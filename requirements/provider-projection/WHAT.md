# provider-projection — WHAT

## [001] 投影是纯代数管线而非 AST 解释器

Provider 消息投影采用类型化组合与直接计算，不引入中介 AST 及二次解释器；功能模块不得就地修改消息列表。

## [002] 投影输入为不可变的 ProjectionSnapshot

投影以不可变的 `ProjectionSnapshot` 为输入，只含当前规范语义投影，不含功能缓存或生命周期状态；投影不得查外部存储或推测会话状态。

## [003] 输出管线严格分层且 Semantic 与 Wire 类型隔离

输出依次为 `SemanticEventTree → ProviderSemanticProjection → ProviderWireProjection`。语义视图去除易失 ID，用于等价比对与规范摘要；Wire 视图保留物理 ID 和本地序号。两者使用不同类型，不得隐式混用。

## [004] 严格划分 Coordinator、Planner 与 Renderer 三层

系统结构严格划分为三层：
1. **Effectful Coordinator**：负责读取宿主状态并生成不可变快照；
2. **Pure Projection Planner**：负责收集意图、规范排序与冲突仲裁；
3. **Canonical Renderer**：负责纯函数式渲染最终字节与确定性表示。

功能所有者先把决策物化为 Provider rows；Renderer 只处理通用行意图，Host 写回只搬运结果，不重新解释功能策略。

## [005] 功能模块仅声明 ProjectionIntent

功能所有者只能提交 `ReplaceMessageBase` 或 `InsertMessageRows` 两类通用 `ProjectionIntent`，不得直接修改 Host 消息流。投影不得定义 prefix/context/repair/Strength 等功能专用意图。

## [006] 规范排序与显式合并冲突，禁止注册顺序依赖

同批出现不同消息基底，或同一 insertion key 对应不同 anchor、rows 或 Host metadata，必须返回 `ProjectionConflict` 并终止。同值重复幂等；合法插入按 anchor+key 规范排序，不依赖注册顺序。

## [007] 投影 DSL 不承担生命周期驱动

投影只根据不可变快照和通用行生成呈现，不决定功能策略，不启动或等待 Agent、执行工具、写存储、推进生命周期或处理心跳。

## [008] TOML 编码由唯一所有者负责

合成 TOML 的布局、转义和字符串编码由唯一所有者统一完成，同输入产生相同字节。该所有者不提供反向解析器，业务逻辑不得解析呈现文本作为控制流依据。

## [009] 指令面与数据面的划分由消费语义决定

按当前接收者的消费语义分面，不按来源、可信度、语法或材料名称分面：

- **Instruction Plane**：要求行动、施加约束、改变推理前提或交还责任的内容，置于顶部连续 `#` 注释。
- **Data Plane**：仅供参考、不由材料本身产生行动要求的状态、参数、观测与证据，编码为 TOML 字段或表。

事实也可能是指令：child → parent 由 `js.deliver` 交回的 view 交还责任，属于 Instruction；repository hint 等参考材料属于 Data。

## [010] 表示层严禁反向创造权威与状态

投影只能由类型化状态生成表示。合成 user/system/assistant 标记不构成权威根或完成凭证，输出文本不得反向驱动业务控制流。

## [011] 规范摘要唯一派生自语义投影

Canonical Digest 对语义投影作确定性规范序列化后计算 SHA-256，排除时间戳、耗时、成本等传输字段，不解析 Wire 文本反算。

## [012] 确定性渲染器保证同输入必同字节

渲染采用统一 LF、转义规则和字段排序，以 UTF-8 字节计算长度；相同语义输入在不同环境下产生相同字节。

## [013] 所有 LLM-facing 合成内容只有一个表示所有者

所有面向 LLM 的合成提示、消息、交接、工具结果及上下文材料，必须先用统一的强类型构造能力组合 Instruction 与 Data，再由唯一表示所有者渲染。业务模块不得自行拼接注释、表格、字段、envelope、分隔或转义，不得直接调用低层 TOML writer。

新增表示需求应扩展统一构造能力，不在功能模块复制格式规则。

## [014] 一个物理 LLM-facing payload 只能 render 一次

一个物理 payload 只渲染一次；Instruction 与 Data 必须先组合，所有指令位于第一个数据字段或表之前。禁止 `render(A) + render(B)`、渲染结果与散文拼接及等价做法，批量结果、附录和交接同样适用。
