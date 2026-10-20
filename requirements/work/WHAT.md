# work — WHAT

本包是唯一的 work 生命周期合同。Plan work 的 sequence 是 S1、S2、S3。Manager work 与持权交付 spawn 出来的执行会话的 sequence 是 S4。结束工具名只有 deliver。结果名只有 Relayed 与 Delivered。评审结果只有 Promoted 与 Stayed。

## [001] 词表

生命周期事实只有这一套：WorkOpened、DevOpsBound、IncumbencyOpened、ConfirmationCommitted、IncumbencyRetired、Reviewed、Reopened、AdvanceCommitted、RetirementBlocked、DeliveryRequested、DeliverySessionBound、DeliveryRejected。TenureReanchored 仍属前缀 epoch，不是本词表的第二套生命周期事实。

- 允许后果：事件名不带 Plan 前缀，也不带 Road 前缀。结果名只在 Relayed 与 Delivered 中取值。评审结果只在 Promoted 与 Stayed 中取值。结束动作的注册名是 deliver。
- 禁止后果：不得签发、携带、失效或重放 QualityCertificate。事件与退休结果不得含 snapshotId，不得使用 WorkspaceSnapshot 这个类型名。不得把 suicide 注册为结束工具，也不得把它留成仍可用的别名。不得把 Road、iteration、assessment、certificate 当作现行词。不得把 Continue 或 Accepted 留成结果名。不得把旧日志翻译进新事件；旧字节不是输入。
- 失败后果：出现证书、snapshotId、suicide 别名、旧词结果名，或把旧账当作新事件输入时，写入必须拒绝，零追加。

## [002] 开立

WorkOpened 的载荷是 workId、root、sequence、权威初值。sequence 在开立时写死。确认次数、是否自动派发、持权 Delivered 是否 spawn，都从 sequence 导出，不另存字段。

- 允许后果：sequence 为 S1、S2、S3 时，确认次数为 1，后继不派发，只有交付授权已置真时 Delivered 才 spawn。sequence 为 S4 时，确认次数为 2，后继由系统派发，Delivered 不 spawn。权威初值由固定标记 authority-initial-v1 与 workId 的确定性摘要构成，不是 0，不是空串，不依赖物理用户消息标识。同一笔追加 WorkOpened 与首任 IncumbencyOpened。IncumbencyOpened 的载荷是 workId、incumbencyId、起始投影、openingCursor，不含 snapshotId。首任起始投影是 sequence 的第一个元素。openingCursor 取开立这一刻已观察到的 XTrace 头序号，不加一，不加十。
- 禁止后果：sequence 长度不是 1 也不是 3 时拒绝开立。不得把确认次数、自动派发或 spawn 写进开立载荷。缺已观察的 XTrace 头序号时不得写空游标。
- 失败后果：重复开立、双重入口或非法 sequence 一律零写。

## [003] review

Reviewed 的载荷只含 incumbencyId、stage、outcome。outcome 只是 Promoted 或 Stayed。每任恰好一次。findings 为空时记 Promoted，非空时记 Stayed。对列表只留在本次调用的返回值里，不追加成事件。

- 允许后果：序列里还有下一个元素且 outcome 为 Promoted 时，投影换成下一个元素，对象仍是这一任。序列已到最后一个元素且 outcome 为 Promoted：sequence 为 S1、S2、S3 时交付授权置真，投影留在该元素；sequence 为 S4 时交付授权保持假，只记下这一任已评。Stayed 不换投影，也不置交付授权。
- 禁止后果：不得把 findings 对、snapshotId 或证书写入 Reviewed。不得凭空 outcome 结束，也不得签发证书。S4 的 Promoted 不得置交付授权。第二评审不得写入。
- 失败后果：无活跃任期、已评、或 outcome 不是 Promoted 与 Stayed 时，零写。

## [004] tenure 尚未 review 时的动作

有任期且这一任还没有 Reviewed 时，只读与观察可用。写源码和向固定 DevOps 派工必须拒绝。

- 允许后果：读、观察、以及不写源码、不派工的工具调用保持可用。review 本身可以调用。sequence 为 S1、S2、S3 时，单笔无权 deliver 不因尚未 review 被拒；但紧接一笔无评审接力之后的再次无评审 deliver，依 [025] 的背靠背门以 assessment required 拒绝、零写。
- 禁止后果：不得把本条收成「没有评审就拒绝一切工具」。不得在尚未 review 时写源码，也不得向固定 DevOps 派工。sequence 为 S4 时，尚未 review 且尚无确认的 deliver 不得写入 ConfirmationCommitted，也不得退休；返回已完成未退休，assessment_required 为真，零写。
- 失败后果：越界写入或派工被拒绝，原状态不变。

## [005] Plan deliver 无确认阶段

sequence 为 S1、S2、S3 时，deliver 不使用 ConfirmationCommitted。无权路径在本次调用内接力退休；持权路径在本次调用内先持久预留外部执行，最终退休由该 operation 的外部观察结算，不要求模型再发第二次 deliver。

- 允许后果：无权路径在无活跃 blocker 时，同一笔追加 IncumbencyRetired（Relayed）与下一任 IncumbencyOpened。下一任起始投影等于退休当时的折叠投影，不推进。openingCursor 等于这笔退休载荷里的 retirementCursor，不加一，不加十。持权且交付授权已置真时，先写 DeliveryRequested，保存 operationId、当前任期、围栏、retirementCursor、digest 与 path；只有该预留首次 durable Committed 后才允许外部启动。外部确切承接后以 ResolveDelivery(Accepted) 写 IncumbencyRetired（Delivered），其中保存 execSessionId、digest、path 与同一 retirementCursor。
- 禁止后果：不得把一段写成两段。不得写 ConfirmationCommitted。S1 的 Promoted 之后，若交付授权仍为假，deliver 不得写成 Delivered。不得要求证书选出 Relayed 或 Delivered。
- 失败后果：无活跃任期且重放未命中时拒绝，零写。活跃 blocker 非空时写 RetirementBlocked，不写 IncumbencyRetired。持权路径在预留前缺 digest/path 时拒绝；预留后的启动结果未知时保留 pending，不写 Delivered，也不再次启动。

## [006] 两段 deliver

sequence 为 S4 时，deliver 是两次调用。第一次只落确认。另一个调用才结束。

- 允许后果：已有评审且尚无确认时，第一次追加 ConfirmationCommitted，载荷只含 incumbencyId、providerRunId、toolCallId，不结束。同一 providerRunId 与同一 toolCallId 重放返回原确认，不第二次结束。另一个 toolCallId 才进入第二次。第二次读折叠上的评审 outcome：Promoted 写 Delivered，Stayed 写 Relayed。两段式退休载荷是 retirementId、incumbencyId、outcome、ProjectionCut 与期望推进值，不含名为 retirementCursor 的字段。S4 的 Delivered 不填 digest、path、execSessionId，不 spawn。Stayed 时，退休与后任 IncumbencyOpened 同一笔追加；后任 openingCursor 来自本次调用已观察到的 XTrace 头序号，不从退休载荷读取名为 retirementCursor 的字段。
- 禁止后果：不得把两段并成一段。不得把第一次收成退休。不得用缺交付授权拒绝已评 Stayed 的第一次确认。S4 因缺 digest 或 path 不得判成 Conflict。无评审不得写下 ConfirmationCommitted。
- 失败后果：无评审且已有确认时拒绝，零写，不返回已完成未退休。同一 toolCallId、不同 providerRunId 返回 RetirementConfirmationReplayConflict。

## [007] 身份与重放

退休身份摘要的输入两种 sequence 相同：固定标记、workId、ProviderRunId、ToolCallId、outcome、当前 durable authority。Plan 使用 oneshot-retirement-v1，S4 使用 retirement-v1，两个标记不合并。持权 Plan 的外部执行另有 operationId：delivery-operation-v1 + workId + incumbencyId + authority + 本次 requestId。

- 允许后果：同一 ProviderRunId/toolCallId 围栏重入返回已经记录的结果；已完成 delivery operation 的精确 Accepted 回执重放也只返回原退休结果。identity 里的 outcome 只是 Relayed 或 Delivered。
- 禁止后果：不得把 retirementCursor、XTrace 头序号、snapshotId 或证书放进退休 identity；不得用 Promoted/Stayed 重算 identity。operationId 不得用随机数或外部 session id 铸造。
- 失败后果：同一 requestId 异载荷返回结构化 RequestIdConflict，零写；同一围栏带另一个 provider run、或 operation 回执与已记录执行实例冲突时拒绝，零写。

## [008] 后继投影

S1/S2/S3/S4 的接力机制不再主动清除上下文；上下文的保留与裁剪全部依靠 `view` 机制。后继通过用户原文 `U`、保留在 `view` 边界内的交互、持久化账本与前任声明的 ANCHOR 视图（durable checkpoint + `view.thenRemove` 摘要）承接交棒，不注入机器物化的合成前任记录。

- 允许后果：后继接收用户原文 `U`、经 `view` 机制裁剪保留的物理交互、持久化账本与前任声明的视图（`ViewCheckpointCommitted` 与前任在 `view` 里声明的 `thenRemove` 摘要）；首任与非首任一律不生成合成记录、不物化合成前任记录。接力本身不主动剥除前任消息。ProjectionCut 只判定退休尝试的请求身份，不当成新任的活跃请求，也不拿来过滤历史。不生成合成消息，不概括前任。
- 禁止后果：不得在接力时主动清除或强制剥除前任上下文；上下文清理必须仅由显式 `view` 声明驱动。不得注入机器物化的合成前任记录消息。不得把旧日志字节翻译成新事件再注入。
- 失败后果：无稳定身份或 `view` 状态非法时拒绝装配，不猜测。

## [009] 阻塞与资源

唯一业务 blocker 是当前任期直接或递归拥有的 live child、background job、PTY/process、active tool、side-effect/execution lease、同步 descendant provider work，以及尚未观察到 terminal 的 cancel/join。

- 允许后果：遇阻塞写 RetirementBlocked，载荷是 incumbencyId 与 blockerDigest。清理之后可以再调用 deliver。固定 DevOps 与其已接收工作跨 tenure 继续，不因 Relayed 被孤儿化。
- 禁止后果：不得把评审非空、测试失败或脏工作区当成 blocker。不得在退休没有发生时写 IncumbencyRetired。
- 失败后果：只返回字符串、不写 RetirementBlocked，是违约。

## [010] 旧账不是输入

新 work 只读写自己的 wanxiangli work journal。它不读取既有 AgentJournal、旧 EventStore lifecycle envelope 或 Relay 事件文件，也不把旧字节翻译成新事实。

- 允许后果：新开立只向 `<主工作树>/.Herdchestra/wanxiangshu/work.sqlite` 的对应 work stream 追加 wanxiangli 规范 record；旧持久层仍可承担其它子系统，但不是 work lifecycle 的读取来源。
- 禁止后果：不得增加能把旧日志翻译进新事实的路径，不得让 CanonicalIntegrator/EventStore 投影与 wanxiangli 同时回答同一 work lifecycle 状态，也不得用测试把旧字节当成合法输入。
- 失败后果：翻译层写入必须拒绝。

## [011] 两段 deliver 的接力与终态停止

S4 第一次调用只确认。第二次调用依持久退休结果分流：Relayed 在返回前排队同一会话的后继提示；Delivered 没有继任者，必须在返回前完成当前 attempt 的中断。

- 允许后果：已评且第一次确认落地后，第二次 Relayed 调用在工具返回前先把同一会话的后继提示（`runtime/manager-assess`）排入队列，不中止会话。若下一次 transform 仍是旧任残余尝试，则停止该已退休尝试。同一会话只启动一个后继。后继 transform 保留经 `view` 机制管理的上下文、用户原文与后继提示（接力不主动清除上下文），不再中止一次。用户新 prompt 之后，后继 Manager 工具不再因退休冻结被拒。后任已有新根时，裁剪之后的唤醒被清空，不中止会话，也不因拒绝中断而崩溃。
- 终态后果：Delivered 先落盘退休事实，再通过既有 journal owner 关闭相应 Manager/HumanRoot logical run，然后等待 `InterruptAttempt` 的 Host 回执，最后才返回“已结束”。这只停止已完成工作的当前 attempt，不删除会话或级联取消子资源；后续真实用户输入继续受新回合保护。
- 禁止后果：不得把第一次确认收成退休。不得向 Relayed 接力发出终态 interrupt，也不得在后继已派发后再发一次旧任期的会话中止。不得把旧物理历史从后继消息集里剥掉。
- 失败后果：终态中断被拒绝或抛错时，工具必须呈现失败，不能忽略失败后返回“已结束”；已提交的 Delivered 不回滚，也不因此开继任任期。在途 Host 中断未完成精确的 provider-step 释放与物理中断之前，不得派发后继。

## [012] 开立与评审的内核判定

同一 workId 的第二笔 WorkOpened 不得被折叠成已开立。开立载荷出现确认次数或 spawn 时，真实折叠拒绝。S4 的 Promoted 不得置交付授权，S1 的 Promoted 也不得提前置真。

- 允许后果：第一笔开立读回 ActiveRebound，sequence 为 S1、S2、S3，事件名含 WorkOpened 与 IncumbencyOpened，载荷不含 confirmationCount 与 spawn。S1 的 Promoted 写入且授权保持假；终元素 Promoted 才置真，折叠记在 S3。
- 禁止后果：第二笔 WorkOpened 不得读成 ActiveRebound。带 confirmationCount 或 spawn 的开立不得读成已开立。S4 的 Promoted 不得把交付授权置真。
- 失败后果：重复开立与非法开立载荷的读回位置是 StorageInvalid，不是已开立。

## [013] 一段式内核退休

一段式单笔无评审的 deliver 不得写成 Delivered，也不得被拒绝。紧接一笔无评审接力之后、本任仍未评审的再次 deliver，依 [025] 以 ReviewRequired 拒绝、零写。S4 退休载荷出现 retirementCursor 必须被拒。缺期望推进值的已落盘 Delivered 不得返回 DeliveredIdempotent。无活跃任期时调用方不得追加 IncumbencyOpened。

- 允许后果：一段式 deliver 写入，明细为 relayed，事件名含 IncumbencyRetired 与 IncumbencyOpened，字节不含 Delivered。合法两段式 Relayed 写入后，读者才看见完整事件。
- 禁止后果：一段式无评审不得写成 Delivered。S4 载荷不得携带 retirementCursor。缺期望推进值不得被读成 DeliveredIdempotent。无活跃任期不得追加 IncumbencyOpened。
- 失败后果：带 retirementCursor 的 S4 读回位置是 StorageInvalid。无活跃任期的追加决定是 Refuse，事件名为空。背靠背无评审接力的追加决定是 ReviewRequired 观察，事件名为空，本任不退休。

## [014] 两段式内核确认

两段式第一次确认落地前不得返回已确认。同一对重放不得第二次结束。

- 允许后果：已有评审且尚无确认时，第一次写入，事件名是 ConfirmationCommitted。同一 providerRunId 与同一 toolCallId 重放返回 Done，事件名为空，不第二次结束。
- 禁止后果：落地前不得返回 confirmed。重放不得写 delivered 或 relayed。
- 失败后果：无评审的第一次返回 Done，明细是 assessment required，不是 confirmed，零写。

## [015] durable 提交与恢复位置

持久提交结果只按 wanxiangli journal 的 Committed、Replayed、Conflict、NotWritten、Unknown 解释；Observed 是 Work 的无新事实观察，不是假 durable append。Relayed 与后任必须位于同一个规范 record，恢复不接受半对。

- 允许后果：Committed 才证明本次新 record 已落地；Replayed 证明同 requestId 的同一 record 已存在；NotWritten 证明本次没有提交；Unknown 保持未知并要求重开 journal/组合后核对。S4 的 Relayed 恢复到未评后任并由 Manager 工作流派发后继；S1、S2、S3 的 Relayed 恢复到新任 Review。
- 禁止后果：不得继续把旧 AppendNotAttempted/CommitUnknown 名称当现行结果。不得把只有 IncumbencyRetired(Relayed) 的篡改 record 修补为合法后任，不得靠裁掉坏字节、补换行或猜身份恢复。完整 record 本身不依赖 NDJSON 结尾换行。
- 失败后果：规范 JSON、字段、摘要、事件对或状态跃迁任一损坏时整条 work 历史拒绝；保留原始存储证据，不跳过坏 record。

## [016] 旧名字不是新事件

旧生命周期名字不得被统一解码收成新事件。

- 允许后果：内核写下的评审事件类型是 Reviewed。同一字节经统一解码，名字仍是 Reviewed，不含 WorkOpened。
- 禁止后果：RoadOpened 与 AssessmentCommitted 不得被解码成 Reviewed 或其它新事件。
- 失败后果：改名后的字节解码失败，事件名为空，故障携带该旧名字。

## [017] 权威推进

同一期望权威的后到推进被拒绝，不得改成后到的 next，也不得因此退休或派工。

- 允许后果：期望权威匹配时写入，事件名是 AdvanceCommitted，明细是 advanced，字节只含被接受的 next。开立权威不是空串，也不是调用方自报的错误期望。
- 禁止后果：后到推进不得改权威、不得写 IncumbencyRetired、不得写 DevOpsBound。错期望的探测不得被写成已推进。
- 失败后果：错期望与后到推进的决定是 Refuse，明细是 advance expected mismatch，事件名为空，字节为空。

## [018] 结束工具的注册名

结束工具的注册名是 deliver。注册表和资源加载都不再认 suicide。

- 允许后果：Manager 的工具名含 deliver。deliver 的准入权威是 office。工具描述含 deliver，不含 suicide。
- 禁止后果：工具名不得含 suicide。suicide 不得仍是可加载的工具资源。
- 失败后果：对 suicide 查询准入权威得到 unknown。`resources/provider/tool/suicide` 不得存在。

## [019] 执行入口按 sequence 与当前事实收口

执行入口按 sequence 与当前事实收口。工具名到权限的映射按角色解析 deliver，不按列表顺序，也不按后缀猜测。

- 允许后果：没有任期且本次围栏未命中已落盘退休时，read、review、fork、resume、deliver 在工具执行入口被拒绝；命中已落盘退休时按 [007] 重放，不按无任期拒绝。有任期且评审尚未接纳时，只读与观察放行，派工与 deliver 被拒绝。评审已接纳且尚未确认时，read 与固定 DevOps 派工放行。按角色解析时，Manager 的 deliver 是 Finality，Plan 的 deliver 是 Deliver，两者不相同。
- 禁止后果：有确认且没有评审时，deliver 不得写入。未知名字不得当成 deliver 放行。工具名到权限的映射不得按列表顺序或按后缀猜测。
- 失败后果：上述拒绝发生在工具执行入口，不创建子会话。未知名字解析为 null，既不是 Finality 也不是 Deliver。

## [020] 目标状态形式化与三样输入

当 Plan 模式会话被初始化或任期推进时，系统为该任期装配输入。

- 允许后果：跑者接收本规划任务范围内的全部真实用户输入原文（`U`）、经 `view` 机制裁剪保留的交互（durable `ViewCheckpointCommitted` 与 `view.thenRemove` 摘要），以及通过编程面读写的 Markdown 伪文件（`P`）；接力不主动清除上下文，系统不生成合成工作记录，不做机器物化的合成前任记录注入。
- 禁止后果：模型不得直接读写或篡改 `U`。系统不得向消息集注入合成前任记录，也不得在接力时主动清除上下文。`P` 不得作为文本直接注入模型输入消息集。模型不得取得任何超出 `P` 之外的仓库或工作区写权限。
- 失败后果：若输入集合遗漏必需事实或 `view` 状态损坏，系统必须在消息投影阶段拒绝生成请求并终止本次调用。

## [021] 角色与严格权限投影

当调度器识别到 CanonicalRole 为 `Plan`（Wire 标识为 `plan`）时，系统投影其专属权限与工具集。

- 允许后果：Plan 的工作工具为 `todowrite-for-plan`、`devops`、`ask`、`review`、`deliver`。`todowrite-for-plan`、`devops` 与 `ask` 只向 Plan 开放；`ask` 供 Plan 明确询问用户（[049]），Manager 禁用 `ask`，且 `ask` 不再表示 DevOps 调查。`review` 与 Manager 共用同一工具名与同一语义（见 [038] 与 [003]）。
- 禁止后果：Plan 角色禁止获得代码仓库与执行工具的权限，包括但不限于 `read`、`glob`、`grep`、`edit`、`write`、`mv`、`rm`、`run`、`open-terminal`、`send-terminal`、`read-terminal`、`signal-terminal`、`fork`、`fission`、`commission`。严禁向 Plan 开放任何真实 shell 或命令执行通道。`resume` 与 `handoff` 不再属于 Plan 的工具面。
- 失败后果：任何越权工具调用必须在 `ToolRegistry` 执行门禁被直接拦截（fail-closed），返回强类型拒绝错误，零副作用。

## [022] 轮换事实与持久化重放

Plan 的轮换生命周期由不可变 wanxiangli 事实驱动并投影状态。结束工具名是 deliver。

- 允许后果：投影只通过以下事件更新：
  1. `WorkOpened(workId, root)`：标记规划任务开启与根路径绑定。
  2. `DevOpsBound(workId, devopsId, target)`：绑定唯一的固定 DevOps 实例。
  3. `IncumbencyOpened(workId, incumbencyId, stage, openingCursor)`：开启新任期并标记当前阶段。
  4. `IncumbencyRetired(incumbencyId, outcome)`：标记任期退休，outcome 仅限 `Relayed` 或 `Delivered`。`Relayed` 表示接力退休（任期让位于下一任），`Delivered` 表示终态交付退休。
  5. `Reviewed(incumbencyId, stage, outcome)`：记录本任的 review 裁决，outcome 仅限 `Promoted`（晋级或授权）或 `Stayed`（留任原 stage）。
  6. `Reopened(retirementId)`：把已交付的工作回滚到 active 状态并开启一个新任期（见 [037]）。
  7. `ConfirmationCommitted(incumbencyId, providerRunId, toolCallId)`：仅 S4 第一次 deliver 的确认。
  8. `AdvanceCommitted(incumbencyId, expected, next)`：以精确 expected 推进 authority。
  9. `RetirementBlocked(incumbencyId, blockerDigest)`：记录本次退休被真实资源阻塞。
  10. `DeliveryRequested(operationId, incumbencyId, providerRunId, toolCallId, retirementCursor, digest, path)`：持权 Plan 在外部启动前的 durable 预留。
  11. `DeliverySessionBound(operationId, execSessionId)`：真实执行 session 已创建且其身份已经 durable 绑定；不表示首 prompt 已被接受。
  12. `DeliveryRejected(operationId, reason)`：只有已确定 NotStarted 时结算该预留，使后续显式新尝试成为可能。
  同一事件的重复重放必须保持幂等。
- 禁止后果：同一 workId 同时至多一个活跃任期（`Active`）。已退休任期列表（`Retired`）只增不减。`Reviewed` 每任至多一条；已评任期不得再次评。`Reopened` 清除投影中的 `Delivered`，并以同一笔追加开启一个新任期：沿用原 stage，交付授权重置为假，新 incumbencyId 由固定标记 `reopened-incumbency-v1` 确定性派生、与旧任期不同；`Retired` 列表中的历史记录不删除。`Relayed` 退休的任期严禁复活；`Delivered` 退休的任期仅可经 `Reopened` 触发一次重开，旧任期永不复活。禁止在这些事实之外引入第二份工作阶段或任期账本。向用户提问的等待属于 PromptAuthority 的 `UserQuestionAsked` / `UserQuestionAnswered`（[049]），不改写工作阶段、评审或退休事实。
- 失败后果：遇到同一身份但载荷不同的冲突事件（含同一任期的第二条不同 `Reviewed`），或检测到不合法的状态跃迁（如重复激活、幽灵任期、无 `Delivered` 时的 `Reopened`、`Reopened` 的 retirementId 与退休记录不匹配），系统必须抛出 `StorageInvalid` 并关闭，严禁跳过或猜测。

## [023] 阶段推进、授权与状态正交

规划流程的阶段（Stage）严格单向：`S1`（事实与取舍）、`S2`（理解与路径）、`S3`（流程与伪代码）。工作状态（state）为 `active | delivered`。stage 与 state 正交。

- 允许后果：阶段推进唯一由 `Reviewed(Promoted)` 驱动：`S1` 晋级到 `S2`；`S2` 晋级到 `S3`；`S3` 晋级即授权（`Authorized = true`，本任获得交付权，见 [026]）。接力（`deliver` 无权路径，见 [025]）开出的下一任沿用退休时的 stage，不推进阶段。`state` 由结果为 Delivered 的 `IncumbencyRetired` 进入 `delivered`、由 `Reopened` 回到 `active`。
- 禁止后果：阶段严禁逆行、跳级或由 review 之外的任何事件推进（接力、交付、重开均不推进 stage）。未获授权（`Authorized = false`）的 `deliver` 严禁走终态交付路径，只能走接力。授权一旦授予，本任保持至退休；重开出的新任期交付授权为假。
- 失败后果：在不满足推进条件时收到 `Reviewed(Promoted)`（如对已退休任期、或 stage 越界），Fold 必须返回强类型错误，底层存储零写入。

## [024] 动作许可矩阵

工具调用进入执行前，动作门必须基于当前任期、授权与工作状态进行单一判定。

- 允许后果：
  1. 仅当调用者为 `Plan` 且工作处于 `active`（有活跃任期）时放行 `todowrite-for-plan`、`devops`、`ask`、`review`、`deliver`；`ask` 另需当前 Plan 权威与精确调用身份（[049]），Manager 禁用 `ask`；
  2. 每一任期在调用 `review` 工具之前，`todowrite-for-plan` 工具是严格只读的（仅允许读操作 `read`）；若在此之前尝试写入（`edit`、`rewrite` 等修改操作），动作门与执行层均强制拦截拒绝，并向模型报错说明必须先完成 `review`。当前任期成功调用 `review` 工具之后方解锁写权限；接力（无权 deliver）开启的新任期在其成功调用 `review` 之前重新锁定为只读；
  3. 工作处于 `delivered`（`post-S3` 态）时，`todowrite-for-plan` 仅允许 `read`，`devops` 与 `review` 拒绝；当用户在 `post-S3` 态下输入命令要求执行交付时，允许调用 `deliver` 启动 `S4` 执行会话（且用户可反复要求启动 `S4`）；
  4. `review` 为硬门：本任已有 `Reviewed` 事实时拒绝再次调用（每任恰好一次，见 [038]）；
  5. `devops` 在同一 work 已有一个未决委派时拒绝新委派（见 [028]）；`ask` 正在等待用户时，不准再启动其他工作工具；
  6. `deliver` 要求无活跃 blocker（递归 live 资源）；行为按授权分支：持权走交付（[026]），无权走接力（[025]）。
- 禁止后果：stage 不作为动作门：各 stage 的工具许可判定一致，阶段推进完全由 review 硬门承担。禁止在未决 devops 委派悬空时开启新的外部动作。`handoff` 与 `resume` 不在 Plan 许可矩阵中。禁止在当前任期完成 review 之前向底稿 `P` 写入任何改动。
- 失败后果：凡不满足许可矩阵条件的调用，一律返回强类型 typed 错误，并保持原状态不变。

## [025] 接力契约（deliver 无权路径）

当跑者调用 `deliver` 且本任未获授权（`Authorized = false`）时，系统执行任期接力。`handoff` 不再是独立工具。

接力不得成为 no-op：连续两笔都未评审的接力构成阶段永不推进的空转。系统以折叠派生的「上一笔已落盘退休是否为无评审接力」作门——本任未评审且上一笔退休也未评审时，本次 deliver 拒绝接力。该门只读退休轮次（`reviewOutcome` 与 `outcome`），不引入第二份工作阶段或任期账本，也不写进任何事件载荷。

- 允许后果：系统首先核查当前任期的递归活跃资源（包括 child、后台作业、未决工具调用与租约）。确认无 blocker 后，以单个原子事务追加两条事件：
  1. `IncumbencyRetired(incumbencyId, Relayed)`；
  2. `IncumbencyOpened(workId, nextIncumbencyId, stage, openingCursor)`，其中 stage 等于退休时的 stage（接力不推进阶段）。
  退休游标取自当前 `deliver` 所属物理交互的真实 XTrace 边界，表示已消费到的最后一个语义 part；下一任的 `openingCursor` 等于该游标，首个新 part 从其后开始，不另加一。较新的用户输入或其它 provider run 不得扩大本次退休范围。
  当前活跃任期更新为新任期，阶段不变；接力不再主动清除上下文，上下文的保留与裁剪全部依靠 `view` 机制（见 [034]）。
- 禁止后果：若存在活跃阻塞资源（blocker），禁止退休任期；系统必须记录 `RetirementBlocked` 并向模型返回 blocker 摘要。接力严禁改写 `stage`、严禁产生 `Reviewed`、严禁影响 `Delivered` 投影。禁止在未成功持久化新任期前注销当前任期。禁止使用固定增量、消息数组下标或缺省值伪造退休游标。本任未评审且上一笔退休也未评审时，严禁再次接力开任。
- 失败后果：退休边界不可证明时，在追加事件之前拒绝接力；事务写入失败时，当前任期保持活跃，不产生状态漂移。背靠背无评审接力的拒绝是 ReviewRequired 观察、零写，当前任期保持活跃，runner 提交本任 `review` 后方再次接力。

## [026] deliver 契约（持权交付路径）

当跑者调用 `deliver` 且本任已获授权（`Authorized = true`）时，系统将规划收束进 `post-S3` 态；随后在 `post-S3` 态下由用户命令触发 `deliver` 启动 `S4` 执行。

- 允许后果：`S3` 持权任期调用 `deliver` 时，系统核查当前任期无活跃 blocker、确认 `P` 非空后，将工作结算为 `Delivered`（进入 `post-S3` 态），永久关闭该会话的自动催促（nudge），并在 Host 确认该 `deliver` 工具结果完成时像 `ask` 一样调用 `InterruptAttempt` 中断当前对话等待用户，不直接启动 `S4` 执行会话。
- 允许后果：处于 `post-S3` 态时，若用户输入命令要求执行 `deliver`，模型调用 `deliver` 即读取 `P` 的非空原文并启动无 parent 的 `S4`（Manager）执行会话；用户可以反复要求启动 `S4`。底层预留路径在 `Committed + DeliveryRequested` 时同样遵守先绑定 `execSessionId` 再发送 `P` 全文的持久边界：确切承接才提交 `ResolveDelivery(Accepted, execSessionId, digest, path)`；确切启动前拒绝才提交 `NotStarted`；`AcceptanceUnknown`、创建回执丢失或异常结果保持 pending，不写 NotStarted、不再次启动。
- 禁止后果：`P` 为空或不存在时严禁进入 `post-S3` 态或启动 `S4`。无授权时严禁走本路径（走 [025] 接力）。`execSessionId` 严禁进入 provider 可见面。进入 `post-S3` 态后永久禁止对该会话发送工作催促（nudge）。
- 失败后果：预留前的文件读取/校验失败为零写入；预留提交失败为零启动。预留后的明确 NotStarted 可以追加拒绝事实，当前任期继续活跃；结果未知则保留 `DeliveryRequested` 作为未完成义务。任何路径都不得用“启动函数抛异常”本身证明外部效果没有发生。

## [027] 长程任务禁止静默停止

Manager 与 Plan 的目标义务独立于物理回合：空 stop、合格收尾散文或进度小结均不证明目标完成。2026-10-15 的本条修订取代 Plan 的自然停止豁免。

- 允许后果：没有有效交接/完成事实，也没有 [049] 的用户等待时，新的自然停机必须在真实 idle 许可上得到同运行 nudge。提示指出尚未解除的任务义务与下一步；Manager 保留当前评审/工作/收尾提示，并说明 `join` 可以临时等待后台工作。Plan 提醒 `devops`、`review`、`deliver` 的职责，以及 `ask` 是唯一自主暂停等用户的入口。
- 禁止后果：不得先发布 Completed 或关闭逻辑运行，再尝试催促；不得以无后台句柄、有正式文本或缺工作投影当作目标已完成。相同终态重复通知幂等，后续新的偷停仍须提醒，不因之前已经 nudge 过一次而永久静默。无 idle 许可时等待真实闲置，不抢占正在运行的工具。
- 边界：已退休 run、旧权威回执、已交付工作、显式用户取消不被复活。provider 故障仍交给既有失败 owner；提醒或存储失败必须可见，不能伪造已发送或已完成。其他非长程角色保留各自的完成合同。

## [028] devops 契约（同步委派固定 DevOps）

当 Plan 跑者调用 `devops(intent)` 时，系统把调查意图同步委派给本 work 绑定的固定 DevOps。原工具名 `ask` 不再是本能力的别名。

- 允许后果：系统查询本规划任务绑定的唯一固定 DevOps 实例；若未绑定则由运行时 single-flight 创建并持久化 `DevOpsBound` 绑定事实。将 `intent` 作为调查目标投递给该 DevOps 做只读调查（含真实 bash 执行，只读约束由 office 提示词承担，不做运行时门）。等待本次调用的完成事实，确认收敛后把 DevOps 经 `js.deliver` 交回的增量去重 TOML 视图完整交回 Planner。
- 禁止后果：模型在调用参数中只传 `intent`，不得传递 DevOps 的 id、role、model 等底层拓扑标识。系统严禁对 DevOps 的返回结果做二次模型摘要，必须完整交付。同一规划任务同时至多运行一个未决 devops 委派；已有未决委派时再次调用必须返回强类型拒绝。返回材料不得携带底层 id（SessionId/AgentId/PtyId）。
- 失败后果：若固定 DevOps 处于忙碌状态或响应失败，返回强类型错误，不静默丢弃，不新建替代操作员。

## [029] P 路径推导与编程面工具

底稿 `P` 是模型唯一可读写的持久化文件产物，其路径由系统确定性派生。

- 允许后果：系统以 `workId` 派生安全编码的 `canonical-work-key`，路径固定为 `<主工作树>/.Herdchestra/wanxiangshu/plan/<canonical-work-key>/plan.md`。`todowrite-for-plan` 是标准的 JS 编程面工具（接受单一参数 `program: string`，在沙箱中执行继承自 `JsProgram` 的类并实现 `async run()`），仅作用于该单一底稿 `P`。它直接模仿其它 `js-*` 工具的锚点编辑法，但因为作用域严格限定于单一底稿 `P`，严禁且无需传入任何路径参数：提供 `file(matches)` 读快照与锚点切片、`edit(changes)` 局部修改与 `rewrite(newText)` 全量覆盖能力；无 `glob`/`grep` 仓库检索能力。写入采用排他临时文件写入、flush 后原子替换的方式发布。
- 允许后果：交给模型的 `todowrite-for-plan` 工具描述复用其它 `js-*` 的同一套教程面：有序锚点、`text(from, to)`、精确 `edit`、结构重组，以及一份按底稿职责成形的样例。教程按本工具的无路径签名填写：`file(matches = [])`、`edit(changes)`、`rewrite(newText)`。`P` 的单一作用域、禁路径和写前评审只作为作用域约束放在教程前面，不另写一套锚点用法。
- 禁止后果：模型不得在工具参数中使用多余字段，只传递 `program`。在 `JsProgram` 的 `file`、`edit`、`rewrite` 方法中严禁传入任何路径参数，若调用时尝试传入路径参数，沙箱必须直接报错拒绝。严禁路径穿越（path traversal）、符号链接逃逸、不安全 ID 注入或根目录漂移。`todowrite-for-plan` 严禁提供任何超出 `P` 之外的文件系统能力，严禁暴露目录列表或仓库文件读写。写盘完成后取消读回校验。
- 禁止后果：工具描述不得另写精简教程替代共享教程，不得在教程或样例中教模型传入路径参数，不得把运行时内部对象暴露给模型。
- 失败后果：检测到路径异常、传入路径参数、尝试访问 `P` 之外的文件或目标文件被外部非法篡改时，操作必须立即拒绝。

## [030] 工具注册与分类

系统启动时，必须显式注册 Plan 相关的工具与参数规范。

- 允许后果：在 `ToolRegistry` 中登记 `todowrite-for-plan`（JS 编程面）、`devops`、`ask`、`review`、`deliver`（编排面）。静态工具名单显式登记五个名称；`ask` 参数为 `question`，`devops` 参数为 `intent`，能力映射不得混用。`review` 与 Manager 共用同一 ToolSpec（见 [038]）。
- 禁止后果：严禁使用前缀匹配、通配符或模糊规则对工具进行分类或准入。未显式登记的工具不得进入 Plan 的工具列表。`handoff` 与 `resume` 不得作为 Plan 工具登记或注册。
- 失败后果：若工具名称或分类缺失，系统在启动静态校验或工具构建期必须报错阻断。

## [031] 阶段资源、语言对等与真注入

Plan 角色与各阶段使用的指导文档必须在资源目录中中英双语严格成对提供，并经系统提示转换器真正装配进 Plan 会话。

- 允许后果：以下资源必须同时存在 `en.md` 与 `zh-CN.md` 且内容语义对等：
  - `resources/provider/role/plan/`
  - `resources/provider/planning/s1/`
  - `resources/provider/planning/s2/`
  - `resources/provider/planning/s3/`
  - `resources/provider/tool/js-plan/`
  - `resources/provider/tool/ask/`
  - `resources/provider/tool/devops/`
  - `resources/provider/tool/review/`
  - `resources/provider/tool/deliver/`
  系统提示转换器在 `experimental.chat.system.transform` 挂钩中真正装配 Plan 提示：`catalogPrompt` 对 `Role.Plan` 返回 Plan 的 role law（`PlanSystemPrompt` 进入 PromptCatalog），`activeRoles` 包含 `Plan`，替换逻辑与其它 office 角色同规则（字节识别自己的段落、按当前全局语言本地化）。在 Plan 会话的系统提示中，按当前任期的 `stage` 以稳定字节幂等追加对应阶段的方法说明（S1 对应事实与取舍，S2 对应理解与路径，S3 对应 Knuth 伪代码）；stage 变化时下一次请求呈现新 stage 方法。
- 禁止后果：任何资源缺失单侧语言文件均属于非法状态。`resources/provider/tool/resume/` 与 `resources/provider/tool/handoff/` 不再属于 Plan 工具面，不得保留为可加载的工具资源。系统不得在 Plan 提示缺失时静默降级为无提示或单侧语言。严禁在 system transform 之外用消息注入代替系统提示装配。
- 失败后果：装载期发现任何双语不对等或缺失，系统必须在 Boot 阶段触发致命失败（fail fast），拒绝启动。

## [032] 非 Plan 路径行为不变

Plan 的阶段与任期机制不得污染其他角色；[027] 明确规定的 Manager 长程连续性除外。

- 允许后果：Orchestrator、Engineer、DevOps 的既有角色合同不变。Manager 禁止静默停止且禁用 `ask`，`review` 的业务判断不变。
- 禁止后果：非 Plan 角色严禁获取 `todowrite-for-plan`、`devops` 或 `ask`。Plan 的 Baton 隔离逻辑严禁污染非 Plan 会话。任何角色都不得再使用已经撤销的 Plan 空停豁免。
- 失败后果：任何非 Plan 回归测试失败均表明破坏了全局不变量，改动必须判定为不合格。

## [033] wanxiangli record、SQLite 持久化与投影

work lifecycle 的唯一 durable owner 是 wanxiangli：Work 负责规则和规范 record，SqliteJournal 负责原子字节提交，DurableWork 负责两者的提交协作。旧 EventStore/CanonicalIntegrator 不再投影 work lifecycle。

一次宿主投影中的当前任期、轮次、退休历史和推进记录必须来自同一次 records 读取；其他连接在读取之后提交的事实只能出现在下一次投影。固定 DevOps 的身份与模型目标分别投影为 DevOpsId 和 DevOpsTarget，恢复路由只能读取后者，不能把身份字符串作为模型目标。

- 允许后果：每次状态变化先在 Work 候选上完整验证，随后把一条规范 JSON record 作为 UTF-8 BLOB 原子写入 `<主工作树>/.Herdchestra/wanxiangshu/work.sqlite` 的 workId stream；同流 revision 连续，requestId 绑定同一 record。JSON 对象键按 Unicode 码点稳定排序，opening/retirement cursor 必须是 JS safe integer。读取时由同一组 records 重建 Work 并校验规范编码、摘要与全部领域跃迁。
- 禁止后果：规则校验失败不得进入 SQLite append。严禁让旧 EventStore envelope、CanonicalIntegrator 投影或进程内缓存成为第二份 lifecycle 权威；严禁自动修复、跳过损坏 record 或把 Conflict/Unknown 当成功。
- 失败后果：非法命令在 append 前拒绝且零写；revision/request 冲突显式返回 Conflict；已知未写返回 NotWritten；COMMIT 结果不明返回 Unknown 并要求重开后核对。读到损坏历史时整份拒绝。

## [034] 任期接力与 `view` 驱动上下文管理

当为处于活跃任期的会话组装 Provider 请求消息集时，接力机制不再主动清除上下文；上下文的保留与裁剪全部依靠 `view` 机制。

- 允许后果：先以稳定 Host 消息及 part 身份捕获本次请求中新观察到的真实交互，再按 `view` 声明（`prefixUntil` / `thenRemove`）组装消息集：
  1. 当前规划任务因果范围内的真实用户输入原文（U）与交互按原因果顺序保留，受显式 `view` 声明裁剪；
  2. 交棒与上下文清理完全由模型在编程面提交的 ANCHOR `view`（durable `ViewCheckpointCommitted` + `view.thenRemove` 摘要）承担，装配阶段不主动清除前任消息、不生成合成记录、不注入机器物化的合成前任记录。
  `openingCursor` 记录任期开启时的观察边界，不触发自动剥除前任交互。
- 禁止后果：严禁在 S1/S2/S3/S4 接力时主动清除上下文；严禁注入机器物化的合成前任记录。规划底稿 P 的正文严禁直接拼装入消息集。用户消息 U 严禁篡改。
- 失败后果：若消息装配失败或 `view` 状态损坏，系统必须停止当前请求组装，拒绝发送残缺或受污染的上下文。

## [035] 崩溃恢复判定与位置重绑

系统在进程重启或插件重新加载时执行崩溃恢复判定。恢复仅依据 wanxiangli work journal 重建的 durable 工作视图与确切的底稿文件存在性检查，绝对不按底稿正文猜测阶段，亦不凭同名 session 识别 DevOps。

- 触发条件：进程启动激活持久化层后，对已存在的全部工作视图进行状态判定与恢复。
- 允许后果：
  1. 若工作视图标记为 `Delivered`（且持有合法的交付凭据与摘要），恢复位置为 `Delivered`，幂等呈现交付回执与底稿路径，绝不再调用模型，亦不开启新任期；此后用户 reprompt 经 `Reopened` 回 active（[037]），该路径属于运行时交互，不属于恢复；
  2. 若工作视图处于 `Active(incumbencyId, stage)` 且底稿文件（`plan.md`）存在，恢复位置为 `Active`，准确重绑当前任期、阶段与授权（`Authorized`），并将底稿存在标志设为 true；
  3. 若工作视图处于 `Active(incumbencyId, stage)` 但底稿文件尚未创建，恢复位置仍为 `Active` 并标记底稿不存在，系统视为可继续推进（模型可从头编写底稿），绝对不得凭空假定已交付；
  4. 若工作视图既无活跃任期亦未交付，恢复位置为 `Nothing`，系统不执行猜测与推断。
  5. ask 为同步委派（[028]），不存在悬挂的 ask 状态，恢复判定无需处理 ask。
  6. 若存在 pending DeliveryRequested，恢复义务是 `ObserveExecution`：只查询同一个 operation 的真实外部结果，严禁重新启动执行会话。只有 Accepted 或确定 NotStarted 才能把它收敛。
- 禁止后果：严禁根据底稿文件内部的文本内容、标题或关键词推测任期阶段。严禁在恢复期间无故创建新任务、私自开启新任期、自动派发 DevOps 命令或自动执行 `Reopened`。严禁在恢复路径把 `Delivered` 工作重新激活为 `Active`（重开只由用户 reprompt 触发）。
- 失败后果：若视图数据损坏或出现冲突（例如 Delivered 状态与 Active 任期同时并存），恢复函数必须 fail-closed 返回 `Conflict` 错误状态，保留原始证据，拒绝进入任何执行分支。

## [036] 入口：首 prompt 开 work

当用户在 Plan mode 会话中敲入首条 prompt 时，系统开启规划任务。

- 允许后果：系统读取会话 sessionId，核验 owner role 为 `Plan`，以一条 durable record 原子追加两条事实：
  1. `WorkOpened(workId, root)`，其中 `workId = sessionId`，root 为 Git 证明的当前工作树根目录（见 [046]）；
  2. `IncumbencyOpened(workId, incumbencyId, S1, openingCursor)`。openingCursor 取开立这一刻已观察到的 XTrace 头序号，不加一，不加十。缺这一序号则拒绝，不写空游标。
  首任任期始于 `S1`。DevOpsBound 不属于开立，首次 devops 调查成功收敛时再按 [028]/[042] 绑定。若该 work 已存在且 root/sequence 相同，入口重读 durable work 后幂等成功，不因后到调用观察到不同 head 再开一次；并发开立收到 Conflict 或 Unknown 时也必须先重读，只有持久事实能证明同一 work 已开立才算成功。入口挂点在消息 transform pipeline 的 Plan 分支（`resolveTenure` 之前）。
- 禁止后果：非 Plan 会话严禁触发本入口。严禁凭同名物理会话或自然语言内容猜测已有 work。严禁在入口之外的位置追加首条 `WorkOpened` 或首个 `IncumbencyOpened`。
- 失败后果：同一身份重放幂等；异载荷拒绝。若 store 不可得或写入失败，入口必须失败并保持零写入，严禁伪造 S1 兜底状态。

## [037] 工作状态机与 reprompt 重开

Plan 工作的状态（state）为 `active | delivered`，由 Durable 事实驱动，与 stage（[023]）正交。

- 允许后果：结果为 Delivered 的 `IncumbencyRetired` 把 state 置为 `delivered`；`Reopened` 把 state 回滚为 `active`。处于 `delivered` 的 work 收到用户新 prompt（reprompt）时，系统以同一笔追加写入 `Reopened(retirementId)`（retirementId 取自该 work 的 `Delivered` 退休记录）与新的 `IncumbencyOpened`：投影清除 `Delivered`，`Active` 转为新任期——新 incumbencyId 由固定标记 `reopened-incumbency-v1` 与 retirementId、本次请求身份确定性派生，stage 沿用重开时的 stage，交付授权重置为假；旧任期永不复活。恢复后模型在新任期上继续工作：新任期在成功调用 `review` 前重新锁定为只读，之后可修改 `P`、可再次 `deliver`。重开的新任期保持被重开任期最初开任时继承的上任记录范围；被重开任期自己的 `Delivered` 退休记录不作为新任期的上一任期。
- 禁止后果：严禁在无 `Delivered` 事实时追加 `Reopened`。严禁借重开推进或回退 stage、复活旧任期、清除 `Reviewed` 历史或恢复旧任期的交付授权。严禁在非 reprompt 场景（如恢复、调度）自动触发重开。`Retired` 列表的历史记录严禁删除。
- 失败后果：同一身份重放幂等；载荷或 retirementId 与 `Delivered` 退休记录不符时拒绝；底层存储零写入。

## [038] review 晋级门

Plan 的阶段推进由 `review` 硬门承担。`review` 与 Manager 共用同一工具名、同一 schema 与同一三问语义（findings 为 `(acceptance_criteria, work_plan)` pairs，空集表示缺口为空，详见 [003]），按角色换评审目标。

- 允许后果：Plan 后端的评审目标 = 下一里程碑的准入条件，由当前 stage 推出：`S1` 的目标是能晋级 `S2`（`S1` 事实与取舍底账完备，`S2` 能开始）；`S2` 的目标是能晋级 `S3`（`S2` 理解与路径检验完备，`S3` 能开始）；`S3` 的目标是「成为最后一任」（`S3` 流程与伪代码完备，获得交付授权）。评审对象是「离目标的 gap」，第一任与后任同一标准。findings 为空集时，系统追加 `Reviewed(incumbencyId, stage, Promoted)`：非 S3 任期晋级到下一 stage，并在本次 `review` 工具返回值中立即附带新 stage 的方法说明（`planning/s2` 或 `planning/s3`），供本任直接开始下一阶段写作；S3 任期置 `Authorized = true`。findings 非空时，系统追加 `Reviewed(incumbencyId, stage, Stayed)`，本任留在原 stage，gaps 作为修复义务返回模型。每任恰好一次 review：已评任期再次调用返回强类型拒绝。
- 禁止后果：严禁第二评审、reverify 或自证（对齐 [003]）。严禁向评审者泄露晋级后的流程编排。严禁以数值评分或八维必填代替 findings pairs。严禁在无活跃任期或已 delivered（未重开）时调用 review。
- 失败后果：同任第二条不同载荷的 `Reviewed` 必须拒绝（`StorageInvalid`）；工具门禁对已评任期的调用返回强类型拒绝错误，不消耗 provider 请求。

## [039] durable 预留后起执行会话

持权交付（[026]）时，系统启动一个无 parent 的 Manager 执行会话来承接规划产物。

- 允许后果：只有 `Committed + DeliveryRequested` 才调用执行 spawn 一次。会话以 `agent = manager` 创建，不带 `parentID`；init prompt 为预留 digest/path 对应的 `P` 全文，启动前再次核对字节不变。Host 只有在 session 创建且首 prompt 得到确切 admitted 结果后才返回 Started(execSessionId)；随后提交 ResolveDelivery(Accepted)，由同一 operation 写 Delivered retirement。该 Manager 会话后续生命周期由既有 Manager 体系管理，与 Plan work 不再耦合。
- 禁止后果：Replayed/Observed/Unknown/NotWritten/Conflict 的 DeliveryRequested 一律不得再次 spawn。严禁以有 parent 的会话或其它角色承接交付，严禁摘要/改写 P。execSessionId 严禁进入 provider 可见面。创建或 prompt 接受结果未知时不得写 NotStarted，也不得以异常证明“没有启动”。
- 失败后果：确切的创建前/提示准入拒绝写 DeliveryRejected(NotStarted)，任期保持活跃并允许新的显式 delivery 请求；无法证明是否承接时保留 pending DeliveryRequested，恢复与重放只观察原 operation，不自动重试。只有 Accepted 才写 Delivered。

## [040] 唯一活跃任期

同一 open Work 至多一个 active tenure。精确事实重放只产生一个逻辑任期。身份或载荷冲突须拒绝。

## [041] 固定 DevOps 控制权

每条 open Work 只有一个逻辑 DevOps。交接时新任取得调用权，退休的旧任立即失去新派工与调用权。

## [042] DevOps 绑定幂等

Work 首次绑定一个逻辑 DevOps 后持久化，之后只可 resume，不可 fork 第二个。初始化、晚到结果、接收未知和崩溃恢复均保持唯一性与幂等。不支持并发 Manager 共享 DevOps。

## [043] 三问与 pairs

review 必填 findings，它是 [(acceptance_criteria, work_plan), …] 的数组。空数组表示缺口为空，结果是 Promoted。非空表示 Stayed。不得缺项，不得使用数值或总评分。

## [044] 资源交接

Relayed 只注销旧 Manager 的派工与控制租约。固定 DevOps、已接收工作及其持久后台进程继续运行，留给后任，不得隐式孤儿化或强制销毁。Work 最终关闭、Delivered 收束或致命异常时，按资源责任规则完成物理收束。不得遗留无主资源，也不得假定并发 Manager 分身。

## [045] 历史不代替工作区

provider 上下文由物理历史、用户原文与显式资源构成；历史保留与裁剪只由 `view` 声明决定。工作区由调用方自行检查，projection 不转述其内容。当前动作由事实约束的资源与能力表达，不由前任摘要代替。

## [046] durable 结果的观察与启动条件

work host 观察 durable 执行结果时原样保留调用方传入的 requestId，replay 沿用原 id，不铸造替代。物理启动只对 committed 的 DeliveryRequested 为真；Committed 的其它 kind、Replayed、Unknown、NotWritten 与 Conflict 一律不得启动。work journal 固定位于 `<主工作树>/.Herdchestra/wanxiangshu/work.sqlite`，不在事件流目录内。

Manager 开立的 root 取 Git 证明的当前工作树根目录，不取插件进入时的任意子目录。同一工作树的多个入口必须指向同一 root；不同 linked worktree 即使共用 git-common-dir，也不能因此冒充同一工作根。无法确认工作树根时拒绝开立，不以路径猜测替代。

## [047] lifecycle SQLite 宿主边界

Lifecycle 的 SQLite 连接由宿主边界按实际 JavaScript 运行时选择：Node 使用 `node:sqlite` 的 `DatabaseSync`，Bun 使用 `bun:sqlite` 的 `Database`。运行时选择只有一处；Surface 的内存 probe 与 durable 工作路径必须复用同一选择，不得各自静态导入某一运行时专属 SQLite 模块。Node 与 Bun 都必须能够真实加载 Lifecycle 产物、完成持久开立、关闭连接并重开读取同一工作；模块装载不得因另一个运行时不存在的内建模块而失败。

## [048] 已绑定执行会话的崩溃恢复

持权 Plan 的 `DeliveryRequested` 首次 Committed 后，宿主把外部效果拆成两步：先创建 parentless Manager session，取得真实 `execSessionId`；在发送首 prompt 之前，先提交 `ResolveDelivery(SessionCreated)`，由 wanxiangli 写入 `DeliverySessionBound(operationId, execSessionId)`。只有绑定持久确认后才允许向这个 session 发送 `P`。

- 允许后果：若进程在 prompt 接受后、`ResolveDelivery(Accepted)` 提交前崩溃，重启从 pending work 读出精确 `operationId + execSessionId + digest + path`，只查询这个已绑定 session。若 Host 中首条真实 user prompt 的字节摘要等于预留 digest，重启以同一个 `operationId + ":accepted"` 请求结算 Accepted；不创建第二个 session，不重发 prompt。若崩溃发生在 CreateSession 已成功但创建回执丢失、因而还没有 DeliverySessionBound，恢复只可用稳定 title `wanxiangshu-delivery:<operationId>` 查询 root sessions；恰好一个精确 title 命中时才补 SessionCreated 绑定，再按同一 prompt 证据结算。
- 禁止后果：不得仅凭 session 存在、模糊 title、时间、异常文本或“未查到消息”推断 Accepted/NotStarted。title 查询为零或多于一个精确 root 命中时保持 unbound pending。锁定的 OpenCode 1.18.29 即使接受 caller-supplied messageID，重复 prompt 仍可能生成新的 part，因此 recovery 不得把 messageID 当完整幂等发送许可。已绑定 session 读不到首 user prompt、摘要不符或 snapshot 查询失败时保持 pending。
- 失败后果：SessionCreated 未持久确认前不得发送 prompt；确切 prompt 拒绝时先销毁该空 session，销毁成功后才可写 NotStarted。session 清理、绑定提交或 Accepted 结算结果未知时都保持 fail-closed，不开启新的 delivery operation。

正常交付在异步 prompt 返回后重新查询资源 owner；恢复也必须查询同一个 owner，不得以空数组冒充已经排空。仍有 blocker 时保留 pending，不提交固定的 Accepted 请求，以免以后真实排空时因同 requestId 的不同载荷产生冲突。资源 owner 尚未安装时不能当成没有资源。

发现查询显式请求最多两条候选；返回满页时不能声称结果唯一，即使本地过滤后只剩一条。精确候选必须有有效 session identity 并明确属于 Manager；SDK 错误、非列表响应、缺身份和错误 agent 都是拒绝，不得静默丢行后推断唯一性。

恢复以单个 work 为失败边界。初次读历史或等待 Host 之后再读历史发生错误时，诊断必须指明 workId，保留坏记录和未结算义务，继续恢复其他独立 work。不得把损坏记录解释为“没有工作”，也不得通过跳过记录修复该 work；正常业务读取仍拒绝这份历史。

启动枚举通过同一连接批读候选 work，共用一次全库完整性检查；每个 stream 的修订、字节校验和语义折叠仍各自执行。不得为每个历史 session 重开数据库、重复扫描全库。交付恢复与固定 DevOps 目标恢复复用本次枚举结果，不把它保存为跨调用权威；待执行的外部效果仍在其原有边界重读并核对当前事实。

## [049] ask：向用户提问与显式暂停

只有 S1 阶段的 Plan 可以使用 `ask(question)` 请求继续目标所必需的用户输入；其他阶段与其他角色一律禁用（Manager 同样禁用）。它不调用 DevOps，不假装拿到了用户回答，也不完成工作。

- 真实呈现询问并成功登记 `UserQuestionAsked` 后，返回“你的询问已经发出”一类占位回执。事实绑定 session、logical run、authority root、提问所依据的物理输入、provider run、tool call 与原问题。占位回执不是用户原文，也不是完成报告。
- 在 Host 已确认同一个 ask 工具结果的 `message.part.updated/completed` 上，立即调用 `InterruptAttempt`，不靠延时器猜测完成顺序，不在返回结果前销毁工具。不得使用逻辑终止、关闭 authority 或递归取消子资源替代。重复终态不重复中断；别的 call、别的会话或已回答提问的迟到事件不许中断新执行。
- 等待只从 durable 问题事实派生，进程缓存不成为另一份权威。未回答期间不发工作 nudge、不继续其他工作工具；Host-native 工具的 before 门和插件执行门同样拒绝。`ask` 精确重放只返回原确认，不重复发问题；异载荷同身份、第二个未决问题、空问题、无可用通知或持久写入失败都明确失败。
- 只有同一逻辑运行的新真实人类输入才追加 `UserQuestionAnswered` 并解除等待。导致 ask 的旧输入、内部提示、插件 continuation、通知和 provider 文本都不算答复。原提问者的终态即使晚于用户答复到达，仍不得关闭或重催新一轮。
- 用户答复沿既有 HumanMessage 准入继续同一任务，不强制新建会话、不触发 `Reopened`、不重置评审资格，也不让用户另行调用恢复工具。若显示、存储或 interrupt 失败，必须明确报告实际完成到哪一步，不伪造“正在等待/已经继续”的事实。

[027]、[049] 的宿主行为由 `tests/027.test.mjs` 与 `tests/049.test.mjs` 以真实插件/日志组合的行为断言承载：工具结果先落地、再 interrupt 的实际顺序、无关与迟到事件不作暂停见证、重复终态幂等、用户回复与旧终态交错时的恢复行为。Long Stroke 级真实宿主验收按 verification-system [014] 归发布验证执行。

## [050] js.ask：Plan 编程面的提问成员

Plan 的编程面（`todowrite-for-plan`）携带 `js.ask(question)`：程序内调用即登记 durable 问题并通知用户，然后经统一 park 通道挂起，工具返回 parked 载荷；程序不继续执行，直到恢复。阶段门禁与 [049] 相同：只有 S1 阶段的 Plan 可以提问，其他阶段以 `ASK_REFUSED` 拒绝；拒绝以失败哨兵进入程序，程序可捕获或让其失败。恢复沿用同一工具的三态入参：`{ unpark: { handle, value } }` 让 await 取到注入值，`{ abandon: { handle } }` 展开为 ABANDONED；等待期间的新调用受等待门禁拒绝，只有真实用户输入解锁。parked 载荷携带完整程序与执行点 source 标记（与生成面同规）。
