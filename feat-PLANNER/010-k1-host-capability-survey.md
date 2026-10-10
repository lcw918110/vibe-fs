# K1 阶段宿主能力先决条件调查报告

本文档是针对「Plan 接力式规划协议」（参见 `feat-PLANNER/000.md`）三条宿主能力先决条件及相关施工扩展点的只读调查产出。本文不是产品规范。产品语义只由 `requirements/<包>/WHAT.md` 定义。

文字采用 ASD-STE100 变种中文：短句，一句一事，主动语态。所有事实均附相对路径、行号与具体符号名。

---

## 一、三条核心宿主能力先决条件调查

### 1. `experimental.chat.messages.transform` 按任期隔离消息

#### 现状
- **管道装配与执行次序**：
  - 源码位置：`src/Wanxiangshu/OpenCode/Plugin/PluginTransforms.fs · normalTransform`（行 943–1167）。
  - 执行机制：普通分支按 `TransformStage list` 静态声明的 16 个阶段顺序串行执行。消息最初由宿主传入 `outObj` 的 `messages` 属性中。在阶段 1 开始前，从输出对象中预读当前物理用户消息标识 `physicalUserMessageId`（行 951–953）。每个阶段通过 `caps` 对 `outObj` 进行原地修改（`HostMessageProjection.replaceMessagesInPlace`）。
- **Hook 策略与能力元数据**：
  - 源码位置：`src/Wanxiangshu/OpenCode/Host/HookPolicy.fs · HookKey.MessagesTransform`（行 131–141）。
  - 策略属性：映射到 HostKey `"experimental.chat.messages.transform"`。声明关键级别为 `HookCriticality.Workflow`，上下文为 `HookContext.MessageTransform`，产生效果为 `HookEffect.TransformMessages`，失败策略为 `HookFailureDisposition.TypedPolicyFailClosed`。
- **退休切分挂载与实现**：
  - 挂载点：`PluginTransforms.fs` 阶段 3 `relay-projection-cut`（行 981–992）。该阶段先调用 `caps.SettleAndReplaceDeferredInspections`，再执行 `caps.ApplyRelayProjection`（行 647–707）。
  - 核心实现：`src/Wanxiangshu/Mission/Relay/OpenCode/NarrativeTransform.fs · RelayNarrativeTransform.apply`（行 159–180）与 `project`（行 145–158）。
  - 判定逻辑：调用 `staleRetirement`（行 130–134）检查当前请求是否属于已退休的旧 attempt（通过 `cutToolIndex` 匹配 `ProjectionCut.ToolCallId`）。若命中过期 attempt，触发 `interruptAttempt`，将 `outObj` 消息列表清空为 `[]`（行 154），并返回 `RelayProjectionDisposition.RetiredAttemptStopped`，阶段 3 的 Run 返回 `None`，彻底终止后续阶段。
  - 消息保留边界：若请求属于继任者，进入 `projectActive`（行 139–144）。代码注释（行 135–138）明确规定：当前 Relay 视图在退休后依然保留完整物理历史；Cut 仅用于判定旧请求身份，绝不截断或过滤 Provider 消息集。
- **前缀重锚机制**：
  - 源码位置：`src/Wanxiangshu/OpenCode/Host/HostCompactionGate.fs · reanchorObserved`（行 142–173）。
  - 触发条件：系统观测到宿主压缩伪运行（observed compaction run）时，通过 `HostCompactionPolicy.nextReanchor` 触发。它向 Agent 日志追加 `ContextFact.ContextReanchored`（包含 `SessionId`、`PreviousEpochId`、`NextEpochId`、`ObservedCompactionRun`）。
  - 约束条款：`requirements/prefix-stability/WHAT.md` 条款 [002] 与 [006] 规定，Epoch 推进只能由成功的 prefix probe、Host 压缩重锚或阶段窗口 rebase 触发。

#### 契约缺口
1. **历史隔离缺失**：现有 `RelayNarrativeTransform` 严格遵守 `relay-context-projection/WHAT.md` 条款 [001]，必须向后继者展示完整物理历史，缺少将前任 assistant/tool 消息滤除的逻辑。
2. **冷边界重锚事件缺口**：`ContextReanchored` 强绑定宿主压缩伪运行（`ObservedCompactionRun: ProviderRunIdentity`）。Plan 任期交接需要在任期第一条请求上开辟新 Epoch，但当前缺少由任期交接（Baton Handover）触发的合法重锚事件。
3. **处置分型缺失**：`RelayProjectionDisposition` 仅包含 `Unchanged | CurrentIteration | RetiredAttemptStopped`，未提供表达“任期消息隔离并就地重写消息集”的代数分支。

#### 成立判断
- **结论**：**能成立**。
- **技术可行性**：
  - transform 管道本身支持在阶段 3 或新增的前置阶段就地重写 `outObj.messages`。
  - 系统可以在阶段 3（或新增专用 Stage）插入 Plan 专有处理：检查当前会话的 Plan 任期事实；若处于新任期首请求，过滤掉上一任期的全部 assistant 与 tool 消息，仅保留因果范围内的真实用户消息（`U`）、上一任期工作记录（`LWR_prev`，由运行时注入为 synthetic 消息）以及当前任期消息。
  - 需要在 `requirements/prefix-stability` 与 `ContextFactCases` 中补充任期交接冷边界事件，使 Epoch 正常递增并不破坏前缀稳定性。

---

### 2. `js-plan` 绑定单一路径与仓库能力剥离

#### 现状
- **JS 编程面能力投影机制**：
  - 源码位置：`src/Wanxiangshu/Repository/Programming/Js/Capability.fs · JsCapability`（行 9–15）与 `JsCapability.ofToolPermission`（行 20–31）。
  - 机制说明：`ToolPermission` 经 `ofToolPermission` 纯投影为 `JsCapability`（`Read`、`Write`、`Edit`、`Glob`、`Grep`）。`JsFragmentRegistry`（行 57–165）为每个 capability 提供唯一的成员名称、签名、描述、示例与运行时绑定键。`ToolPermission.Edit` 同时投影出 `edit(path, changes)` 与 `rewrite(path, newText)`。
  - 四层同构：`requirements/repository-programming/WHAT.md` 条款 [002] 强制要求：capability、公开基类成员、工具描述、示例代码与底层运行时门禁四层必须逐字同构。
- **文件系统路径边界强制层**：
  - 源码位置：`src/Wanxiangshu/Repository/Programming/Js/ToolsBindings.fs · resolveInside`（行 48–60）。
  - 机制说明：在沙箱 API 边界，所有路径经 `pathResolve (pathJoin root path)` 展开，再经 `pathRelative root full` 计算相对路径。只要相对路径包含前导 `..` 或属于绝对路径，立即以 `JsFailure.PathDenied path` 拒绝。
- **`edit`/`rewrite` 底层原语实现位置**：
  - 源码位置：`src/Wanxiangshu/Repository/Programming/Js/ToolsBindings.fs · makeEditMember`（行 182–194）。
  - 机制说明：沙箱宿主端暴露的底层接口名为 `edit`，接收 `(path: string) (newText: obj)`。内部调用 `resolveInside root path` 并读取当前内容，将改动暂存至 `staging`（`JsStagedMutation.Rewrite(path, current, replacement)`）。高层客户端 `edit(path, changes)`（支持锚点与差异替换）与 `rewrite(path, newText)` 声明并实现在注入沙箱的客户端基类 `JsProgram` 中。
- **现有静态清单位置**：
  - `src/Wanxiangshu/OpenCode/Tools/StaticTools.fs · knownToolNames`（行 107–142）：`"js-engineer"` 位于行 136，`"js-manager"` 位于行 137。
  - `src/Wanxiangshu/Strength/InvestigationEstimateContract.fs · classifyTool`（行 29–68）：`"js-manager"` 位于行 34，`"js-engineer"` 位于行 35（均属于 `InvestigationToolPolicy.EstimateAfterCall`）。

#### 契约缺口
1. **路径锁定缺口**：`resolveInside` 允许访问仓库根目录内的任意文件，未提供“仅允许访问唯一定义的目标文件”的收窄接口。
2. **工具静态白名单未登记**：`StaticTools.fs` 的 `knownToolNames` 与 `InvestigationEstimateContract.fs` 的 `classifyTool` 均无 `js-plan` 词条。若直接暴露 `js-plan`，会触发差集门禁和未知白名单错误。
3. **权限模型未分化**：现有 `ToolPermission.Edit` 意味着对整个工作区的编辑能力，缺少针对“单一受限伪文件”的细粒度权能定义。

#### 成立判断
- **结论**：**能成立**。
- **技术可行性**：
  - 类似模式在仓库中已有先例（例如私有附着的 `js-predictor` 仅放行只读能力）。
  - 针对 `js-plan`，可实现独立的 `JsPlanBindings`：将路径检查特化为仅放行固定的 `root/plan/<canonical-work-key>/plan.md`，传入任何其他路径直接返回 `PathDenied`。
  - 在能力绑定层中，仅提供 `file`（或只读）及 `edit`/`rewrite`，完全不注入 `makeGlobMember` 与 `makeGrepMember`，从结构上剥除仓库检索能力。
  - 必须在 `StaticTools.knownToolNames` 与 `InvestigationEstimateContract.classifyTool` 中显式登记 `js-plan`（将其归入 `NoEstimate`）。

---

### 3. `materialize` 的 range 语义正好等于一个任期

#### 现状
- **函数签名与参数**：
  - 源码位置：`src/Wanxiangshu/Mission/WorkRecord/Materialize.fs · lifecycleWorkRecordBounded`（行 374–382）。
  - 参数定义：接收 `(journal: AgentJournal option) (sessionId: SessionId) (range: XTraceRange)`。
- **范围类型与语义**：
  - 源码位置：`src/Wanxiangshu/Context/Trace/Cursor.fs · XTraceRange`（行 16、行 50–59）。
  - 结构定义：`type XTraceRange = private XTraceRange of startInclusive: XTraceCursor * endExclusive: XTraceCursor`。
  - 语义定义：半开游标区间 `[startInclusive, endExclusive)`，表示 XTrace 时间线上属于本次 invocation 的确定因果切片。
- **任期事件中的位置数据**：
  - 源码位置：`src/Wanxiangshu/Mission/Relay/Facts.fs`。
  - 事件字段：
    - `IncumbencyOpened`（行 7）：携带 `IncumbencyId * WorkspaceSnapshotId`。
    - `IncumbencyRetired`（行 10）：携带 `IncumbencyId * WorkspaceSnapshotId * AuthorityRevision * ProjectionCut * RetirementOutcome`。
    - `ProjectionCut`（行 16）：仅包含 `ProviderRunId: string` 与 `ToolCallId: string`。

#### 契约缺口
1. **任期事实缺少游标字段**：Relay 任期开启与退休事实仅记录了快照 ID 与调用 ID（`ToolCallId`），**未在事件中原生持久化 `XTraceCursor`**。
2. **因果映射需间接推算**：调用 `lifecycleWorkRecordBounded` 必须提供显式的 `XTraceRange`。现有系统必须通过在 XTrace 部件中反向查找 `ToolCallId` 对应的游标来推导截断点，无法通过任期事实自身直接构造 `XTraceRange`。
3. **类型层面未绑定单任期**：`lifecycleWorkRecordBounded` 接收通用的 `XTraceRange`，其范围完全由调用方传入的游标决定，函数本身不校验该范围是否严格等于单个任期。

#### 成立判断
- **结论**：**底层语义能表达，但领域事件存在数据缺口**。
- **技术可行性**：
  - `lifecycleWorkRecordBounded` 具备按指定区间物化记录的完整能力。
  - Plan 协议不应复用 Relay 丢失游标的旧做法，必须在 Plan 自身的领域事件（如 `PlanIncumbencyOpened` 与 `PlanHandoffCommitted`）中直接记录当时的 `XTraceCursor`。
  - 任期交接时，直接使用两个事件的游标构建 `XTraceRange`，即可精确物化单任期记录。

---

## 二、K2–K7 施工辅助事实清单

### 4. 固定 DevOps 绑定机制
- **源码位置**：
  - 领域事件：`src/Wanxiangshu/Mission/Relay/Facts.fs · RoadDevOpsBound`（行 6）。
  - 状态投影：`src/Wanxiangshu/Mission/Relay/Fold.fs`（行 702–703、行 836–842）。将 `RoadId`、`devopsId` 与 `modelTarget: string option` 固化到 Road 状态。
  - 运行时绑定解析：`src/Wanxiangshu/OpenCode/Tools/ToolRuntimeScope.fs · ensureRoadDevOpsBound`（行 693–699）与 `obtainRoadDevOpsTask`（行 680–692）。检测到 Manager Road 会话时，从持久投影读取绑定；若无绑定则触发创建并追加 `RoadDevOpsBound`。
  - 模型恢复注入：`src/Wanxiangshu/OpenCode/Plugin/PluginRecoveryWiring.fs · seedBoundDevOpsModelTargets`（行 47–62）。在插件加载时将持久化记录的固定模型重新填入 `ModelRouting`。
- **Plan 最小扩展点**：
  - Plan 若需要专属的固定 DevOps，不应侵入 Relay 的 Road 实体。最小扩展点是在 Plan 自身的轮换状态中增加固定 DevOps 绑定事实（如 `PlanDevOpsBound(workId, devopsId, modelTarget)`），并在 `ToolRuntimeScope` 中增加对 Plan workId 的 DevOps 绑定查询与种子恢复。

### 5. 编排面工具注册方式
- **源码位置**：
  - 注册装配入口：`src/Wanxiangshu/OpenCode/Tools/ToolRegistry.fs · createTools`（行 470–558）。所有工具在 `baseSpecs` 中统一列出（行 495–525），随后经 `gateExecute`（行 546–553）附加能力门禁，最终通过 `ToolHostCodec.registry` 提交。
  - 静态名册：`src/Wanxiangshu/OpenCode/Tools/StaticTools.fs · knownToolNames`（行 107–142）。
  - 权限定义：`src/Wanxiangshu/Foundation/OfficeCapability.fs · permissions`（行 61–107）。
- **新增 Plan 角色步骤**：
  - 在 `src/Wanxiangshu/Foundation/Roles.fs · Role` 中新增 `| Plan`，并在 `Roles.all` 与 `roleLabel` 中同步；
  - 在 `src/Wanxiangshu/Foundation/OfficeCapability.fs` 的 `permissions` 中定义 `Role.Plan` 的工具权限集；
  - 在 `StaticTools.fs` 的 `knownToolNames` 与 `ToolRegistry.fs` 的 `baseSpecs` 中注册 Plan 工具（`js-plan`、`ask`、`resume`、`handoff`、`deliver`）。

### 6. ManagedAgent 配置装配
- **源码位置**：
  - 配置钩子挂载：`src/Wanxiangshu/OpenCode/Plugin/PluginHooks.fs · configurePluginHost`（行 769–775）挂在 `HookKey.Config`。
  - 核心装配层：`src/Wanxiangshu/OpenCode/Host/ManagedAgentConfig.fs · applyOwnedFields`（行 195–200）与 `ownedConfigForRole`（行 116–134）。
  - 名册定义：`src/Wanxiangshu/Participant/Persona/ManagedCatalog.fs · requiredNames`（行 83–85）与 `legacyAgentNames`（行 93–106）。
- **关键事实与扩展位置**：
  - **冲突事实**：`"plan"` 目前被显式列在 `ManagedCatalog.fs` 的 `legacyAgentNames` 中（行 101）。若要新增 Plan 代理，**必须首先将其从 `legacyAgentNames` 移除**，否则启动时会被 `rejectLegacy` 直接拒绝并抛错。
  - 在 `ownedConfigForRole`（`ManagedAgentConfig.fs` 行 116）增加分支：`| Role.Plan -> Some(StaticTools.planAgentConfig prompts.PlanSystemPrompt)`。

### 7. 等待用户输入先例与 G 算法（ask）复用分析
- **源码事实**：
  - `concern-routing`：`publish("user", ...)`（`WHAT[003]`）仅向用户界面弹出通知，属于异步单向发送，不等待回答。
  - `delegation`：`join`（`WHAT[015]`、`Join.fs` 行 220–236）等待子任务期间，用户输入被拦截为 `JoinInterruptReason.UserMessageArrived` 并返回 `Interrupted`，打断等待并结束工具调用，将新输入留给下一轮。
  - `managed-chat-execution`：`WHAT[003]` 规定真实用户新消息在 `chat.message` 准入为 `HumanMessage` 追加材料，如果当前正在执行，输入延后至下一次未开始的请求。
- **G 算法复用建议**：
  - 宿主与 Provider 通信中不存在“在单个工具调用内部无限挂起等待人类输入并同步返回”的机制。长时阻塞会导致网络超时并死锁容量租约。
  - G 算法（ask）应复用“两段式状态停驻”模式：调用 `ask` 时持久化待回答事实（`PlanQuestionAsked`），同时正常收束当前 Provider Attempt 并释放容量；当用户在宿主输入新文本时，经 `chat.message` 准入并关联该问题，作为后续轮次的输入交给模型。

### 8. 崩溃恢复挂载点（J 算法）
- **源码位置**：
  - 恢复接线入口：`src/Wanxiangshu/OpenCode/Plugin/PluginRecoveryWiring.fs · attach`（行 63–138）。
  - 执行时机：在 `scope.AttachDurabilityActivation` 触发的后台任务中进行。
  - 既有流程：依次执行 `SessionBindingRecovery.install`（行 71）、`ChildWorkRecovery.settleOrphanedChildRuns`（行 118）、`settleAbandonedStaleBloggerSessions`（行 119）、`SignalChatRecovery(PluginRuntimeReloaded)`（行 122）、`seedBoundDevOpsModelTargets`（行 132）以及 `MarkRestartGuidancePending`（行 137）。
- **J 算法挂载点**：
  - Plan 崩溃恢复应挂在 `PluginRecoveryWiring.fs` 的 `AttachDurabilityActivation` 中，位于 `settleOrphanedChildRuns` 之后。
  - 它应扫描 Plan 领域投影，结算上一进程遗留的在途 Plan 执行，重新定位活动任期、阶段与 `P` 文件；若已处于 `Delivered` 终态，则标记完成不再触发模型调用。

### 9. requirements/INDEX.md、GAP.md 与 nodes.json 结构
- **`requirements/INDEX.md`**：
  - 需修改第 3 行，将活跃规范包数量从 55 递增为 56。
  - 在适当章节（如新增规划相关小节或并入工作执行类）登记 `planning` 包条目与一句话 WHY。
- **`requirements/GAP.md`**：
  - 采用 8 列主表格格式（GAP、包、命题、缺口、状态、现状承载、补法计划、Owner），登记 `planning` 相关的实现缺口。
- **`resources/ablation/nodes.json`**：
  - `nodes.json` 严格按包枚举基础节点（`"kind": "package"`）。
  - 新增 `planning` 包必须在 `nodes.json` 中追加对应节点对象，分配新的连续 `station` 序号，以通过架构消融门禁。

### 10. 双语资源目录结构与语言对等门
- **组织结构**：
  - 角色规范：`resources/provider/role/<role-name>/` 下必须成对存在 `en.md` 与 `zh-CN.md`。
  - 工具描述：`resources/provider/tool/<tool-name>/` 下必须成对存在 `en.md` 与 `zh-CN.md`。
- **门禁机制**：
  - 源码位置：`scripts/checks/language-parity-gate.mjs`。
  - 检查逻辑：通过 `listSemanticResourceDirs`（行 74–83）扫描所有包含语言叶子文件的目录。要求双语文件必须成对存在，占位符 `{{...}}` 严格对称，且代码块中的技术标识符（如工具名、字段名）不得在中文翻译中被修改。
- **施工要求**：
  - 新增 Plan 角色必须创建 `resources/provider/role/plan/{en.md, zh-CN.md}`。
  - 新增 Plan 工具必须分别在 `resources/provider/tool/js-plan/`、`resources/provider/tool/ask/`、`resources/provider/tool/deliver/`、`resources/provider/tool/handoff/` 下提供成对的双语说明文件。

---

## 三、结论与施工建议

1. **三条宿主能力先决条件评估结果**：
   - 条件 1（消息隔离）：**可实现**。管道天然支持就地修改消息数组，需扩展冷边界重锚事件与任期裁剪 disposition。
   - 条件 2（单一路径 js-plan）：**可实现**。已有 `js-predictor` 同构先例，需定制 `resolveInside` 校验并补齐静态名册。
   - 条件 3（任期 range 语义）：**可实现但需扩展事件**。物化逻辑支持游标区间，但 Plan 领域事件必须原生记录 `XTraceCursor`。

2. **已知阻塞陷阱预警**：
   - `ManagedCatalog.fs` 中 `"plan"` 现存为 `legacyAgentNames`，必须优先移除，否则配置装配会 fail-closed。
   - 工具名必须同步在 `StaticTools.knownToolNames` 与 `InvestigationEstimateContract.classifyTool` 登记，否则触发工具差集静态门禁失败。
