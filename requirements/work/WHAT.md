# work — WHAT

本包是唯一的 work 生命周期合同。公开入口只有 review。Plan 的里程碑是「事实与取舍 → 理解与路径 → 流程与伪代码 → 待执行」。Manager 的里程碑是「执行 → 收尾 → 已完成」。任期只有 Originator 与 Successor。空账询问「我需要干什么？」，清空才接力，接任第一次空账晋级，收尾空账且阻塞解除则结案。生命周期工具目录只注册 review。

## [001] 词表

生命周期事实只有这一套：WorkOpened、IncumbencyOpened、QuestionRequested、OperatorAnswered、Reviewed、IncumbencyRetired、ContextBarrier、PromptObliged、MilestoneAdvanced、DeliveryRequested、DeliverySessionBound、DeliveryAdmitted、DeliveryRejected、EffectConfirmed、StopObliged、DevOpsBound。TenureReanchored 仍属前缀 epoch，不是本词表的第二套生命周期事实。

- 允许后果：事件名不带 Plan 前缀，也不带 Road 前缀。一次 review 的领域结果是 Asked、Reviewed、Relayed、Advanced、Launched、Completed、Reopened、Blocked 或 Ignored。生命周期工具的注册名只有 review。材料交还仍用 wxs_deliver，它不是生命周期结束工具。
- 禁止后果：不得签发、携带、失效或重放 QualityCertificate。事件与退休结果不得含 snapshotId，不得使用 WorkspaceSnapshot 这个类型名。不得把 suicide 注册为结束工具，也不得把它留成仍可用的别名。不得把 Road、iteration、assessment、certificate 当作现行词。不得把 Continue 或 Accepted 留成结果名。不得把旧日志翻译进新事件；旧字节不是输入。
- 失败后果：出现证书、snapshotId、suicide 别名、旧词结果名，或把旧账当作新事件输入时，写入必须拒绝，零追加。

## [002] 开立

第一次 review 在同一笔写入 WorkOpened 与首任 IncumbencyOpened。office 是 Plan 或 Manager，开立后不变。首任是 Originator。里程碑从该 office 的第一段开始：Plan 是「事实与取舍」，Manager 是「执行」。没有单独的 Open 命令。

- 允许后果：空账开立只再写 QuestionRequested，问句是「我需要干什么？」。非空账开立再写 Reviewed。DevOpsBound 不属于开立。
- 禁止后果：不得把自动派发、spawn、确认次数或游标写进开立载荷。不得把旧开立记录读成已开立，也不得把它翻译成新事实。
- 失败后果：重复开立、office 与已开立不符、或 Open 命令，一律零写。

## [003] review

review 是唯一的生命周期入口。updates 是 `{ key, value | null }` 数组：已有键覆盖、缺失键新增、value 为 null 删除该键。空数组不删除已有键。

- 允许后果：初任空账只写 QuestionRequested，问句是「我需要干什么？」，不写 Reviewed，不推进里程碑。真实答复后仍是同一任。第一次非空账写 Reviewed 并进入工作期。其后账被清空才在同一笔写下旧任退休、Context Barrier、后任打开与继任提示。接任第一次空账只推进里程碑，不新建任期，不建屏障，并在该次 review 记下下一里程碑提示。待执行上的空 review 预留一次 Manager 启动，不消耗待执行。收尾空账且无真实资源阻塞则结案。已完成后只有新的真实操作员消息重开执行任期恰好一次。
- 禁止后果：不得用 Promoted 或 Stayed 锁定晋级资格。晋级只能由本次 review 的已证事实直接推进。不得把空数组当成清空已有义务。Advisor steer 与其他代理答复不得重开已完成的工作。
- 失败后果：已完成的工作再收到 review 时拒绝，零写。updates 含空 key 时拒绝。效果尚未确认时，另一笔 review 拒绝。

## [004] 询问尚未答复时的动作

义务账仍空、且这一任还停在询问时，只读与观察可用。写源码和向固定 DevOps 派工必须拒绝。review 可以调用。

- 允许后果：读、观察、以及不写源码、不派工的工具调用保持可用。空账 review 询问用户，不写 Reviewed。真实答复后仍是同一任。
- 禁止后果：不得把本条收成「没有评审就拒绝一切工具」。不得在询问未答复时写源码或向固定 DevOps 派工。不得在 review 之外推进里程碑。
- 失败后果：越界写入或派工被拒绝，原状态不变。

## [005] 接任空账只推进里程碑

接任的第一次 review 且账为空时，只写 MilestoneAdvanced 与下一里程碑的 PromptObliged。同一任期变成下一里程碑的 Originator。不新建任期，不写 ContextBarrier。

- 允许后果：事实与取舍进入理解与路径；理解与路径进入流程与伪代码；流程与伪代码进入待执行。执行进入收尾。提示在该次 review 提交之后才发送。
- 禁止后果：不得另开任期。不得把这次推进写成接力。不得在 review 之外推进。
- 失败后果：账非空时不得走本路径。已完成后再推进拒绝，零写。

## [006] 清空才接力，收尾空账才结案

工作期的账被清空时，同一笔写下旧任退休、ContextBarrier、后任 IncumbencyOpened 与继任 PromptObliged。后任停在同一里程碑。收尾且账为空、真实资源阻塞已解除时，同一笔写下 IncumbencyRetired 与 StopObliged，里程碑记为已完成。

- 允许后果：继任提示与停止请求都只在提交成功之后执行一次。停止请求与观察到停止成功是两件事实。待执行上的空 review 不走接力，也不结案，只预留一次 Manager 启动。
- 禁止后果：不得把空数组当成清空已有义务。不得要求模型在有效 review 之后再调用第二个生命周期工具。不得在收尾仍有真实资源阻塞时结案。
- 失败后果：效果未确认时另一笔 review 拒绝。已完成后再 review 拒绝，零写。

## [007] 身份与重放

一次 review 的身份是 requestId、providerRunId 与 toolCallId。同一 requestId 同一载荷重放原结果。同一 toolCallId 的另一请求返回原结果，不写第二次。

- 允许后果：询问、接力、推进、启动与结案都用持久身份。确认的 requestId 由 effectId 与结果确定性派生。
- 禁止后果：不得用 Promoted 或 Stayed 重算身份。不得用随机数或外部 session id 铸造 operationId。不得把游标或证书放进身份。
- 失败后果：同一 requestId 异载荷返回 RequestIdConflict，零写。

## [008] 后继投影

后任能看见的 Provider 历史由已提交的 Context Barrier 裁切。切点是触发这次接力的 review 工具调用及其结果中较晚的那一条。切点与它之前的消息，后任都看不见。切点之后只保留标明同一后任、且不是合成行的消息。原始持久历史逐字节保留。resetScope、negotiate、askPreviousScope 与系统 compaction 的状态不因这次裁切而改变。

- 允许后果：没有已提交屏障，或活跃任期不是该屏障的后任时，请求原文不动。无法归属的消息不进入后任。有 Host 身份但没有后任身份的消息不进入后任。
- 禁止后果：不得靠消息下标、固定加一或猜测游标决定切点。不得无条件保留全部历史 user 消息。不得另建第二套投影。不得改写已存储的原文。
- 失败后果：触发调用或它的结果对不上时，后任视图为空，不得因此改成展示全部历史。

## [009] 阻塞与资源

唯一业务 blocker 是当前任期直接或递归拥有的 live child、background job、PTY/process、active tool、side-effect/execution lease、同步 descendant provider work，以及尚未观察到 terminal 的 cancel/join。

- 允许后果：收尾的空 review 遇到真实资源阻塞时，记为 Blocked 观察，不写退休。清理之后可以用新的 toolCallId 再 review。固定 DevOps 与其已接收工作跨任期继续，不因接力被孤儿化。
- 禁止后果：不得把评审非空、测试失败或脏工作区当成 blocker。Blocked 不写 IncumbencyRetired，也不占用这次 toolCallId 的围栏。
- 失败后果：把阻塞写成退休，或因此拒绝以后的结案，是违约。

## [010] 旧账不是输入

新 work 只读写自己的 wanxiangli work journal。它不读取既有 AgentJournal、旧 EventStore lifecycle envelope 或 Relay 事件文件，也不把旧字节翻译成新事实。

- 允许后果：新开立只向 `<主工作树>/.Herdchestra/wanxiangshu/work.sqlite` 的对应 work stream 追加 wanxiangli 规范 record；旧持久层仍可承担其它子系统，但不是 work lifecycle 的读取来源。
- 禁止后果：不得增加能把旧日志翻译进新事实的路径，不得让 CanonicalIntegrator/EventStore 投影与 wanxiangli 同时回答同一 work lifecycle 状态，也不得用测试把旧字节当成合法输入。
- 失败后果：翻译层写入必须拒绝。

## [011] 接力与结案的外部效果

接力提交成功后，才按持久 prompt 身份发送继任提示恰好一次。结案提交成功后，才请求停止当前 Provider attempt 恰好一次。崩溃发生在提交之后、效果确认之前时，重启用同一持久身份补齐一次。

- 允许后果：继任提示不中止会话。停止只作用于当前 attempt，不删除会话。未知结果只观察或续办原请求。停止请求与观察到停止成功分别取证。
- 禁止后果：不得在 Committed 之前发送或停止。不得把 Replayed、Conflict 或 Unknown 再执行一次外部动作。不得删改已存储的原文；后任看不见的部分由 [008] 的屏障裁切。
- 失败后果：效果确认不明时保持未知。已提交的接力或结案不回滚，也不因此再开一任。

## [012] 开立与评审的判定

同一 workId 的第二笔 WorkOpened 不得被折叠成已开立。Open 命令与带 sequence 的旧开立载荷拒绝，不翻译。review 按账与里程碑决定询问、记账、接力、推进、启动或结案。

- 允许后果：第一笔 review 读回 WorkOpened 与 IncumbencyOpened。空账是 QuestionRequested，不是 Reviewed。非空账是 Reviewed，里程碑不变。
- 禁止后果：不得把空账读成晋级资格。不得在 review 之外推进里程碑。
- 失败后果：重复开立与旧载荷的读回是拒绝，不是已开立。

## [013] 接力必须成对

接力的一笔记录同时含退休、ContextBarrier、后任打开与继任提示。缺任一项整笔拒绝。接任第一次空账推进不得写成退休。

- 允许后果：读者看见完整事件。用已提交记录恢复后，等待点与屏障和提交前相同。
- 禁止后果：不得把只有退休、没有后任的记录修补成合法接力。无活跃任期不得追加 IncumbencyOpened。
- 失败后果：半对记录整份历史拒绝。

## [014] 启动与结案的幂等

待执行的一次空 review 预留一个 Manager，不退休待执行。同一请求重放返回既有记录。收尾空账且无真实资源阻塞才结案。

- 允许后果：多个不同的有效调用可以各预留一次启动。同一请求重放不第二次结束，也不第二次启动。
- 禁止后果：不得要求先写另一笔确认才允许结案。重放不得产生新的退休或第二个 Manager。
- 失败后果：非当前任期调用拒绝，零写。收尾仍有 blocker 时返回 Blocked 观察，不退休。

## [015] durable 提交与恢复位置

持久提交结果只按 wanxiangli journal 的 Committed、Replayed、Conflict、NotWritten、Unknown 解释；Observed 是 Work 的无新事实观察，不是假 durable append。接力的退休、屏障、后任与提示必须位于同一个规范 record，恢复不接受半对。

- 允许后果：Committed 才证明本次新 record 已落地；Replayed 证明同 requestId 的同一 record 已存在；NotWritten 证明本次没有提交；Unknown 保持未知并要求重开 journal/组合后核对。Relayed 恢复到后任，并补齐尚未确认的继任提示。Advanced 恢复到同一任期的下一里程碑。Completed 恢复到已完成，并补齐尚未确认的停止。Launched 恢复到同一预留，不另开 Manager。
- 禁止后果：不得继续把旧 AppendNotAttempted/CommitUnknown 名称当现行结果。不得把只有 IncumbencyRetired(Relayed) 的篡改 record 修补为合法后任，不得靠裁掉坏字节、补换行或猜身份恢复。完整 record 本身不依赖 NDJSON 结尾换行。
- 失败后果：规范 JSON、字段、摘要、事件对或状态跃迁任一损坏时整条 work 历史拒绝；保留原始存储证据，不跳过坏 record。

## [016] 旧名字不是新事件

旧生命周期名字不得被统一解码收成新事件。

- 允许后果：内核写下的评审事件类型是 Reviewed。同一字节经统一解码，名字仍是 Reviewed，不含 WorkOpened。
- 禁止后果：RoadOpened 与 AssessmentCommitted 不得被解码成 Reviewed 或其它新事件。
- 失败后果：改名后的字节解码失败，事件名为空，故障携带该旧名字。

## [017] DevOps 绑定

一条工作只有一个逻辑 DevOps。首次绑定写入 DevOpsBound。同一身份再次绑定是观察，不写第二笔。

- 允许后果：绑定跨接力保留。身份与模型目标分开保存。恢复路由只读模型目标。
- 禁止后果：不得把另一个目标写成同一次绑定。不得借绑定退休任期或推进里程碑。
- 失败后果：目标不同的再次绑定拒绝，零写。

## [018] 结束工具的注册名

生命周期工具的注册名只有 review。注册表和资源加载只认当前的 review 生命周期入口，不接受未知名字。材料交还工具 wxs_deliver 仍然注册。

- 允许后果：Plan 与 Manager 的生命周期工具名只有 review。能力映射中没有第二个终局工具入口。工具描述写义务账，不写资格锁定，不写 S1。
- 禁止后果：工具名不得含 suicide。生命周期目录、权限映射和模型可见帮助只描述 review。独立的 wxs_deliver 保留原来的材料交还职责。
- 失败后果：对未知生命周期工具查询准入得到 unknown。`resources/provider/tool/suicide` 不得存在。

## [019] 执行入口按当前事实收口

工具名到权限的映射只把 review 解析为生命周期权限，不按列表顺序，也不按后缀猜测。

- 允许后果：尚未开立 Work 时，首任可只读观察，第一次 review 按 [002] 原子开立；已经结案而没有活跃任期时，新的 review 拒绝。询问尚未答复时，只读与观察放行，写源码与派工拒绝。其它终局权限不对应模型工具。命中已落盘的同一请求时按 [007] 重放。
- 禁止后果：未知名字不得当成有效生命周期工具放行。工具名到权限的映射不得按列表顺序或按后缀猜测。
- `join` 与 `horizon` 按已确立角色和当前进程子任务归属执行，不要求 Work 活跃任期或 Review 接纳；清理 blocker 与退任冻结不能阻断既有子任务的观察、等待与收取。它们不因此开立 Work，也不取得新派工权限；角色、用户等待、消融与恢复边界不变。
- 失败后果：上述拒绝发生在工具执行入口，不创建子会话。未知名字的生命周期权限查询结果为 null。

## [020] 目标状态形式化与三样输入

当 Plan 模式会话被初始化或任期推进时，系统为该任期装配输入。

- 允许后果：跑者接收本规划任务范围内、且按 [008] 仍属于当前任期的真实用户输入原文（`U`）、当前活跃 scope 的可见交互，以及通过编程面读写的 Markdown 底稿。系统不生成合成工作记录。
- 禁止后果：模型不得直接读写或篡改 `U`。系统不得向消息集注入合成前任记录。不得无条件保留屏障前的用户消息。底稿不得作为文本直接注入模型输入消息集。模型不得取得任何超出底稿之外的仓库或工作区写权限。
- 失败后果：若输入集合遗漏必需事实，系统必须在消息投影阶段拒绝生成请求并终止本次调用。

## [021] 角色与严格权限投影

当调度器识别到 CanonicalRole 为 `Plan`（Wire 标识为 `plan`）时，系统投影其专属权限与工具集。

- 允许后果：Plan 的工作工具权限集为 `todowrite-for-plan`、`devops`、`review`：宿主注册的只有 `todowrite-for-plan` 与 `review`，`devops` 以编程面成员 `this.devops(intent)` 存活，由 `todowrite-for-plan` 承载（见 [028] 与 [030]）。`todowrite-for-plan` 只向 Plan 开放。空账询问走 review，不注册独立宿主工具，也不表示 DevOps 调查。`review` 与 Manager 共用同一工具名与同一语义（见 [038] 与 [003]）。
- 禁止后果：Plan 角色禁止获得代码仓库与执行工具的权限，包括但不限于 `read`、`glob`、`grep`、`edit`、`write`、`mv`、`rm`、`run`、`open-terminal`、`send-terminal`、`read-terminal`、`signal-terminal`、`fork`、`fission`、`commission`。严禁向 Plan 开放任何真实 shell 或命令执行通道。`resume` 与 `handoff` 不再属于 Plan 的工具面；`ask` 不注册独立宿主工具，独立 `ask` 工具调用不是公开调用面。
- 失败后果：任何越权工具调用必须在 `ToolRegistry` 执行门禁被直接拦截（fail-closed），返回强类型拒绝错误，零副作用。

## [022] 轮换事实与持久化重放

生命周期由不可变 wanxiangli 事实驱动并投影状态。结束工具名是 review。

- 允许后果：投影只通过 [001] 的事实更新。`QuestionRequested` 是询问，不是 Reviewed。`Reviewed` 只在非空账时写入，同一任期可以多次。`IncumbencyRetired` 只出现在接力或结案。`ContextBarrier` 只出现在接力。`MilestoneAdvanced` 只出现在接任第一次空账。`DeliveryRequested` 只预留 Manager 启动，不退休待执行。`DeliverySessionBound` 只表示会话已绑定，不表示底稿已被接受。`DeliveryAdmitted` 才表示真实受理。`StopObliged` 只出现在结案。已完成后的操作员消息只写一笔新的 `IncumbencyOpened`。同一事件的重复重放必须保持幂等。
- 禁止后果：同一 work 同时至多一个活跃任期。已退休任期不复活。不得用 Promoted 或 Stayed 记录资格。不得另建第二份里程碑或任期账本。空账询问不改写里程碑。Advisor steer 与其他代理答复不得重开已完成的工作。
- 失败后果：同一身份异载荷、已完成后再 review、或半对接力，整笔拒绝，零写。严禁跳过或猜测损坏记录。

## [023] 里程碑单向推进

Plan 的里程碑单向前进：事实与取舍、理解与路径、流程与伪代码、待执行。Manager 的里程碑单向前进：执行、收尾、已完成。推进不新建任期。

- 允许后果：接任第一次空账把同一工作者变成下一里程碑的 Originator。待执行保持待执行，每次有效的空 review 可以再预留一个 Manager。收尾空账且资源阻塞已解除则记为已完成。已完成后只有新的真实操作员消息重开到执行，恰好一次。
- 禁止后果：里程碑不得逆行或跳级。不得用第一次空账锁定资格。不得在 review 之外推进。待执行不被某一次启动消耗。
- 失败后果：不满足推进条件时零写，并返回拒绝原因。

## [024] 动作许可矩阵

工具调用进入执行前，动作门按当前任期与里程碑判定。

- 允许后果：
  1. Plan 在有活跃任期时放行 `todowrite-for-plan` 与 `review`。DevOps 调查经编程面成员 `this.devops(intent)` 发起，其准入与拒绝依 [028]。空账询问由 review 承担。
  2. 底稿写入在本任第一次非空 review 之前不暴露，本任只能读底稿；这次 review 之后写作解锁并保持到本任退休。接力开启的新任期在它自己进入工作期之前重新只读。写入只触碰系统告知的底稿路径。
  3. 已完成之后，review 拒绝。只有新的真实操作员消息可以重开执行。
  4. `review` 同一任期可多次调用。updates 按键覆盖、新增或以 null 删除。空数组不删除已有键。
  5. `this.devops` 在同一 work 已有一个未决委派时拒绝新委派（见 [028]）。询问尚未答复时，不准再启动其他工作工具。
  6. 接力、推进、启动与结案都由这一次 review 的账和里程碑决定，见 [003]、[005]、[006]、[014]。
- 禁止后果：不得把首次空 review 仅作为资格锁定后另开一个生命周期动作。禁止在未决 devops 委派悬空时开启新的外部动作。`handoff` 与 `resume` 不在 Plan 许可矩阵中。禁止在当前任期进入工作期之前向底稿写入任何改动。
- 失败后果：凡不满足许可矩阵条件的调用，一律返回强类型错误，并保持原状态不变。

## [025] 接力

工作期的账被清空时，系统执行任期接力。`handoff` 不是独立工具。

- 允许后果：确认账在应用本次 updates 之后为空，然后以单个原子事务写下旧任退休、ContextBarrier、后任 IncumbencyOpened 与继任 PromptObliged。后任停在同一里程碑。屏障的切点是这次 review 的工具调用及其结果。提交成功之后才发送继任提示。resetScope 的状态不因这次接力而改变。
- 禁止后果：账仍非空时不得接力。不得在未成功持久化后任前注销当前任期。不得用消息下标或固定加一伪造屏障。不得无条件保留屏障前的用户消息。
- 失败后果：事务写入失败时，当前任期保持活跃。空数组不是清空。

## [026] 启动与结案

待执行上的空 review 预留一次 Manager 启动，不消耗待执行。收尾空账且真实资源阻塞已解除则结案。

- 允许后果：启动的持久顺序是预留、创建 Session、绑定 Session ID、发送底稿全文、确认真实受理。每次有效调用各预留一次。结案先提交 IncumbencyRetired 与 StopObliged，再请求停止当前 Provider attempt。停止请求与观察到停止成功是两件事实。
- 禁止后果：创建结果未知时不得发送底稿，也不得另开 Manager。不得把一次启动写成待执行退休。收尾仍有真实资源阻塞时不得结案。`execSessionId` 不得进入 provider 可见面。
- 失败后果：预留提交失败为零启动。明确 NotStarted 可以追加拒绝事实，待执行保持可再次启动。结果未知则保留原预留。不得用抛异常本身证明外部效果没有发生。

## [027] 长程任务禁止静默停止

Manager、Orchestrator 与 Plan 的目标义务独立于物理回合：空 stop、合格收尾散文或进度小结均不证明目标完成；Manager 与 Plan 严禁静默停止。作为对等收敛规则，子代理角色（Engineer、DevOps）豁免催促（nudge）并允许自然停机（context-compression [037]），其自然停机只交回末轮 assistant 散文（渲染为 `# ...` 注释）。

- 允许后果：对非豁免角色（Manager、Orchestrator、Plan），没有有效交接/完成事实，也没有 [049] 的用户等待时，新的自然停机必须在真实 idle 许可上得到同运行 nudge：提示指出尚未解除的任务义务与下一步；Manager 保留当前评审/工作/收尾提示，并说明 `join` 可以临时等待后台工作；Orchestrator 保留委任与接续职责；Plan 提醒 `devops` 与 `review` 的职责；空账 review 是等待用户的入口。Engineer 与 DevOps 彻底免除 nudge 催促，允许在工作完成后自然停机并交回成果。internal 末梢 Advisor/Bookkeeper 沿专用提交事实收敛，不承担结案义务。
- 禁止后果：不得先发布 Completed 或关闭逻辑运行，再尝试催促；不得以无后台句柄、有正式文本或缺工作投影当作目标已完成。相同终态重复通知幂等，后续新的偷停仍须提醒，不因之前已经 nudge 过一次而永久静默。无 idle 许可时等待真实闲置，不抢占正在运行的工具。严禁对 Engineer 或 DevOps 发送完成催促 nudge。
- 边界：已退休 run、旧权威回执、已交付工作、显式用户取消不被复活。provider 故障仍交给既有失败 owner；提醒或存储失败必须可见，不能伪造已发送或已完成。internal 末梢 Advisor/Bookkeeper 沿各自的专用提交事实收敛，不以一段公开工具回执为完成证据，也不让一段内部散文解除外层目标义务。

## [028] devops 契约（同步委派固定 DevOps）

当 Plan 跑者经编程面成员 `this.devops(intent)` 调用时，系统把调查意图同步委派给本 work 绑定的固定 DevOps；原工具名 `ask` 不再是本能力的别名。

- 允许后果：系统查询本规划任务绑定的唯一固定 DevOps 实例；若未绑定则由运行时 single-flight 创建并持久化 `DevOpsBound` 绑定事实。将 `intent` 散文作为 `# ...` 注释头，连同 Planner 当前完整的 post-scope LLM 可见消息视图（渲染为 TOML 数据平面），一并投递给该 DevOps 做只读调查（含真实 bash 执行，只读约束由 office 提示词承担，不做运行时门）。等待该 DevOps 自然停机完成，确认收敛后把 DevOps 末轮 assistant 散文作为 `# ...` 注释完整交回 Planner，不返回 DevOps 的完整 post-scope 视图。
- 禁止后果：模型只传调查问题 `intent`，不得传递背景数组或底层拓扑标识。系统严禁对 DevOps 的返回结果做二次模型摘要，必须完整交付。同一规划任务同时至多运行一个未决 devops 委派；已有未决委派时再次调用必须返回强类型拒绝。返回材料不得携带底层 id（SessionId/AgentId/PtyId）。
- 失败后果：若固定 DevOps 处于忙碌状态或响应失败，返回强类型错误，不静默丢弃，不新建替代操作员。

## [029] 底稿路径与普通读写工具

底稿是模型唯一可读写的持久化文件产物，其路径由系统确定性派生，系统把确切路径告知模型。

- 允许后果：系统以 `workId` 派生安全编码的 `canonical-work-key`，路径固定为 `<主工作树>/.Herdchestra/wanxiangshu/plan/<canonical-work-key>/plan.md`。`todowrite-for-plan` 是标准的 JS 编程面工具（接受单一参数 `program: string`，在沙箱中执行继承自 `JsProgram` 的类并实现 `async run()`），走普通读写成员：`this.cat(底稿路径)` 读取、`this.where` / `this.whereAll` 定位、切片重组后由 `this.write(底稿路径, content)` 写回；不提供仓库目录枚举或搜索能力。写入采用排他临时文件写入、flush 后原子替换的方式发布。只触碰底稿这一份文件，是信任边界，不是运行时路径门禁。
- 允许后果：交给模型的工具描述复用共享基类伪代码与切片重组教程，并附一份底稿样例。伪代码整体本地化，方法体内使用紧凑的 `//` 注释。底稿路径、单文件边界与写前评审约束在作用域说明中给出。`this.ask` 是共享的 park 特化成员，不注册独立宿主工具，也不锁定晋级资格。空账询问由 review 承担。
- 禁止后果：模型不得在工具参数中使用多余字段，只传递 `program`。`todowrite-for-plan` 严禁提供任何超出底稿之外的文件系统能力，严禁暴露目录列表或仓库文件读写。写盘完成后取消读回校验。
- 禁止后果：工具描述不得另写精简教程替代共享教程，不得把运行时内部对象暴露给模型。
- 失败后果：目标文件被外部非法篡改时，操作必须立即拒绝。

## [030] 工具注册与分类

系统启动时，必须显式注册 Plan 相关的工具与参数规范。

- 允许后果：在 `ToolRegistry` 中登记 `todowrite-for-plan`（JS 编程面）与 `review`（编排面）。静态工具名单显式登记这两个名称。空账询问不注册独立宿主工具。`devops` 只以编程面成员 `this.devops(intent)` 存活，由 `todowrite-for-plan` 承载。能力映射不得混用。`review` 与 Manager 共用同一 ToolSpec（见 [038]）。材料交还仍登记 `wxs_deliver`，它不是生命周期工具。
- 禁止后果：严禁使用前缀匹配、通配符或模糊规则对工具进行分类或准入。未显式登记的工具不得进入 Plan 的工具列表。`handoff` 与 `resume` 不得作为 Plan 工具登记或注册；已退役的 `devops` 同样不得回到宿主注册工具面。
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
  - `resources/provider/tool/review/`
  - `resources/provider/tool/deliver/`
  上述目录名是资源路径，不是生命周期阶段。系统提示转换器在 `experimental.chat.system.transform` 挂钩中真正装配 Plan 提示：`catalogPrompt` 对 `Role.Plan` 返回 Plan 的 role law（`PlanSystemPrompt` 进入 PromptCatalog），`activeRoles` 包含 `Plan`，替换逻辑与其它 office 角色同规则（字节识别自己的段落、按当前全局语言本地化）。在 Plan 会话的系统提示中，按当前里程碑以稳定字节幂等追加对应方法说明：事实与取舍、理解与路径、流程与伪代码。里程碑变化时下一次请求呈现新的方法说明。
- 禁止后果：任何资源缺失单侧语言文件均属于非法状态。`resources/provider/tool/resume/` 与 `resources/provider/tool/handoff/` 不再属于 Plan 工具面，不得保留为可加载的工具资源。系统不得在 Plan 提示缺失时静默降级为无提示或单侧语言。严禁在 system transform 之外用消息注入代替系统提示装配。
- 失败后果：装载期发现任何双语不对等或缺失，系统必须在 Boot 阶段触发致命失败（fail fast），拒绝启动。

## [032] 非 Plan 路径行为不变

Plan 的里程碑与任期机制不得污染其他角色；[027] 明确规定的长程连续性与豁免除外。

- 允许后果：Orchestrator 的既有角色合同不变。Engineer 与 DevOps 在工作完成后以有正文的自然终态交回材料（context-compression [037]），但无正文无工具的空终态仍须 nudge；Manager 与 Plan 保持长程连续性，严禁静默停止，并只用宿主生命周期 `review` 驱动里程碑、接力、启动与结案（[003]、[025]、[026]）。internal 末梢 Advisor/Bookkeeper 沿专用提交事实收敛。Manager 的空账询问走 review，`review` 的业务判断不变。
- 禁止后果：非 Plan 角色严禁获取 `todowrite-for-plan` 或 `ask`；宿主注册工具面已无 `devops` 这一名称，DevOps 调查只以编程面成员 `this.devops(intent)` 形态承载，不构成任何角色的宿主工具面。Plan 的 Baton 隔离逻辑严禁污染非 Plan 会话。任何角色都不得再使用已经撤销的 Plan 空停豁免。
- 失败后果：任何非 Plan 回归测试失败均表明破坏了全局不变量，改动必须判定为不合格。

## [033] wanxiangli record、SQLite 持久化与投影

work lifecycle 的唯一 durable owner 是 wanxiangli：Work 负责规则和规范 record，SqliteJournal 负责原子字节提交，DurableWork 负责两者的提交协作。旧 EventStore/CanonicalIntegrator 不再投影 work lifecycle。

一次宿主投影中的当前任期、轮次、退休历史和推进记录必须来自同一次 records 读取；其他连接在读取之后提交的事实只能出现在下一次投影。固定 DevOps 的身份与模型目标分别投影为 DevOpsId 和 DevOpsTarget，恢复路由只能读取后者，不能把身份字符串作为模型目标。

- 允许后果：每次状态变化先在 Work 候选上完整验证，随后把一条规范 JSON record 作为 UTF-8 BLOB 原子写入 `<主工作树>/.Herdchestra/wanxiangshu/work.sqlite` 的 workId stream；同流 revision 连续，requestId 绑定同一 record。JSON 对象键按 Unicode 码点稳定排序。身份是良构标识，不另要求开立游标或退休游标。读取时由同一组 records 重建 Work 并校验规范编码、摘要与全部领域跃迁。
- 禁止后果：规则校验失败不得进入 SQLite append。严禁让旧 EventStore envelope、CanonicalIntegrator 投影或进程内缓存成为第二份 lifecycle 权威；严禁自动修复、跳过损坏 record 或把 Conflict/Unknown 当成功。
- 失败后果：非法命令在 append 前拒绝且零写；revision/request 冲突显式返回 Conflict；已知未写返回 NotWritten；COMMIT 结果不明返回 Unknown 并要求重开后核对。读到损坏历史时整份拒绝。

## [034] 任期接力与 resetScope 上下文管理

发往模型的请求按已提交 Context Barrier 裁切，规则同 [008]。resetScope 仍按 context-compression [033] 管理 scope，不改它的状态，也不用它代替屏障。

- 允许后果：OpenCode 的 provider 请求在原消息数组里替换元素。OMP 的 context 钩子在 scope 提醒之前裁切一份视图。库存原文与另一份数组都不被改写。没有屏障时两处都不裁。
- 禁止后果：严禁无条件保留屏障前的 user、assistant、reasoning、工具调用和工具结果。严禁注入机器物化的合成前任记录。规划底稿的正文严禁直接拼装入消息集。
- 失败后果：若消息装配失败，系统必须停止当前请求组装，拒绝发送残缺或受污染的上下文。

## [035] 崩溃恢复判定与位置重绑

系统在进程重启或插件重新加载时执行崩溃恢复判定。恢复只依据 wanxiangli work journal 重建的里程碑、任期、义务账与未确认效果，不按底稿正文猜测里程碑，也不凭同名 session 识别 DevOps。

- 触发条件：进程启动激活持久化层后，对已存在的全部工作视图进行状态判定与恢复。
- 允许后果：
  1. 已完成的工作恢复为已完成，不开启新任期，也不再调用模型。重开只由其后的真实操作员消息触发（[037]），不属于恢复。
  2. 有活跃任期时，重绑同一任期、同一里程碑与同一义务账。底稿文件存在与否只影响写作，不改变里程碑。
  3. 尚未开立时，恢复位置为空，不猜测。
  4. 未确认的询问、提示、启动或停止，用原持久身份补齐恰好一次。
  5. 创建结果未知的启动不发送底稿，也不另开 Manager。
- 禁止后果：严禁根据底稿正文推测里程碑。严禁在恢复期间创建新任务、开启新任期、自动派发 DevOps 或自动重开。严禁把已完成的工作在恢复路径上重新激活。严禁把旧记录翻译成新事实。
- 失败后果：视图损坏时 fail-closed，保留原始证据。一个坏 work 不挡住其他 work。
- 已知边界：本仓库的崩溃惯例是重置到一个自洽态，不是崩溃后接续。「外部效果已执行、确认尚未落盘」窗口里崩溃，恢复按第 4 条以原持久身份再补一次，可能多出一次询问、提示或 Manager；这由重置后的自洽态承接，不靠效果身份（effect id）去重或重放接续。效果端口因此不携带 effect id，也不为此窗口建外部幂等键。

## [036] 入口：第一次 review 开 work

工作由第一次 review 开启，不由单独的 Open 命令开启。

- 允许后果：workId 是调用方会话。office 是 Plan 或 Manager。同一笔写入 WorkOpened 与首任 IncumbencyOpened。Plan 从「事实与取舍」起，Manager 从「执行」起。DevOpsBound 不属于开立，首次 devops 调查成功收敛时再按 [028]/[042] 绑定。已存在的同一 work 重放原开立，不因后到调用再开一次。
- 禁止后果：严禁凭同名物理会话或自然语言内容猜测已有 work。严禁用 Open 命令或旧 sequence 开立。严禁把旧日志翻译成这次开立。
- 失败后果：同一身份重放幂等；异载荷拒绝。写入失败则零写。

## [037] 已完成后的重开

已完成之后，只有新的真实操作员消息重开执行任期恰好一次。Advisor steer 与其他代理答复重开零次。

- 允许后果：重开在同一 work 账本上写入一笔新的 IncumbencyOpened。新任期是 Originator，里程碑是「执行」。旧任期不复活，原记录不删除。同一消息重放不再造第三任。
- 禁止后果：严禁在尚未完成时重开。严禁借重开推进或回退里程碑、复活旧任期或清除已有事实。严禁在恢复或调度时自动重开。退休记录严禁删除。
- 失败后果：同一身份重放幂等；不是操作员消息时零写。

## [038] review 与义务台账

Plan 与 Manager 的评审都是同一个 review。updates 按键覆盖、新增或以 null 删除。没有「第一次空账锁定晋级资格」。

- 允许后果：非空账进入工作期。账被清空才接力。接任第一次空账推进到下一里程碑，同一工作者成为该里程碑的 Originator。Plan 走到待执行后保持待执行，每次有效的空 review 可以再启动一个 Manager。Manager 从执行走到收尾，收尾空账且资源阻塞已解除则结案。
- 禁止后果：严禁用 S1、S2、S3 推出下一评审目标。晋级只能由有效 review 执行。严禁在无活跃任期或已完成后调用 review。
- 失败后果：身份冲突或已完成后再评审时零写，并返回拒绝原因。

## [039] durable 预留后起执行会话

待执行上的空 review 提交成功后，系统启动一个 Manager 执行会话来承接当时的底稿。这次启动不消耗待执行，也不退休 Plan。

- 允许后果：只有首次 Committed 的 DeliveryRequested 才创建会话一次。顺序是持久预留、创建 Session、持久绑定 Session ID、发送底稿全文、确认真实受理。底稿字节在发送前再核对。受理写成 DeliveryAdmitted，不写成 Plan 退休。
- 允许后果：底稿即这次 review 的 note。`review` 工具的 `note` 参数与编程面 `this.review(findings, note)` 的第二参是同一份 note，原样成为 Manager 的会话标题与发送的底稿；编程面缺省或空白时读取规范路径上已保存的底稿全文，缺失或为空则在创建会话前拒绝。非字符串 note 在写入前拒绝，不预留、不启动。
- 禁止后果：Replayed、Observed、Unknown、NotWritten 与 Conflict 不得再次创建。创建结果未知时不得发送底稿。不得摘要或改写底稿。execSessionId 不得进入 provider 可见面。不得用异常本身证明没有启动。
- 失败后果：确切的创建前拒绝写 DeliveryRejected。无法证明是否承接时保留原预留，恢复只观察该 operation。待执行保持可再次接受另一个有效调用。

## [040] 唯一活跃任期

同一 open Work 至多一个 active tenure。精确事实重放只产生一个逻辑任期。身份或载荷冲突须拒绝。

## [041] 固定 DevOps 控制权

每条 open Work 只有一个逻辑 DevOps。交接时新任取得调用权，退休的旧任立即失去新派工与调用权。

## [042] DevOps 绑定幂等

Work 首次绑定一个逻辑 DevOps 后持久化，之后只可 resume，不可 fork 第二个。初始化、晚到结果、接收未知和崩溃恢复均保持唯一性与幂等。不支持并发 Manager 共享 DevOps。

## [043] 三问与 pairs

review 的 updates 是 `{ key, value | null }` 数组。空数组不删除已有键，也不是晋级资格。非空写入 Reviewed。不得使用数值或总评分，不得把空账记成 Promoted 或 Stayed。

## [044] 资源交接

接力只注销旧任的派工与控制租约。固定 DevOps、已接收工作及其持久后台进程继续运行，留给后任，不得隐式孤儿化或强制销毁。结案或致命异常时，按资源责任规则完成物理收束。不得遗留无主资源。待执行上的多次 Manager 启动各有自己的 operation，不共享一个 DevOps。

## [045] 历史不代替工作区

provider 上下文由物理历史、用户原文与显式资源构成。后任能看见的历史由已提交的 Context Barrier 裁切，规则同 [008]，不由「非合成 user 一律保留」决定。工作区由调用方自行检查，projection 不转述其内容。当前动作由事实约束的资源与能力表达，不由前任摘要代替。resetScope 的 view 不代替这道屏障。

## [046] durable 结果的观察与启动条件

work host 观察 durable 执行结果时原样保留调用方传入的 requestId，replay 沿用原 id，不铸造替代。物理启动只对 committed 的 DeliveryRequested 为真；Committed 的其它 kind、Replayed、Unknown、NotWritten 与 Conflict 一律不得启动。work journal 固定位于 `<主工作树>/.Herdchestra/wanxiangshu/work.sqlite`，不在事件流目录内。

Manager 开立的 root 取 Git 证明的当前工作树根目录，不取插件进入时的任意子目录。同一工作树的多个入口必须指向同一 root；不同 linked worktree 即使共用 git-common-dir，也不能因此冒充同一工作根。无法确认工作树根时拒绝开立，不以路径猜测替代。

## [047] lifecycle SQLite 宿主边界

Lifecycle 的 SQLite 连接由宿主边界按实际 JavaScript 运行时选择：Node 使用 `node:sqlite` 的 `DatabaseSync`，Bun 使用 `bun:sqlite` 的 `Database`。运行时选择只有一处；Surface 的内存 probe 与 durable 工作路径必须复用同一选择，不得各自静态导入某一运行时专属 SQLite 模块。Node 与 Bun 都必须能够真实加载 Lifecycle 产物、完成持久开立、关闭连接并重开读取同一工作；模块装载不得因另一个运行时不存在的内建模块而失败。

## [048] 已绑定执行会话的崩溃恢复

DeliveryRequested 首次 Committed 之后，外部效果按持久身份分步确认：先创建 Manager session，取得真实 `execSessionId`；发送底稿之前先写入 `DeliverySessionBound`。只有绑定持久确认后才允许发送。这次启动不把 Plan 写成已完成。

- 允许后果：进程在绑定确认之后、发送确认之前崩溃时，恢复只发送一次，不再创建第二个 session。进程在创建已发生但回执丢失时，只观察原 operation，不另开 Manager。受理确认使用同一 operation 身份。
- 禁止后果：不得仅凭 session 存在、模糊 title、时间、异常文本或“未查到消息”推断已受理或未启动。发现结果为零、多于一个或满页时保持未绑定。不得把 messageID 当完整幂等发送许可。
- 失败后果：绑定未持久确认前不得发送。创建或发送结果未知时保持 fail-closed，不开启新的 operation。确切拒绝可以写 DeliveryRejected，待执行仍可接受另一个有效调用。

正常交付在异步 prompt 返回后重新查询资源 owner；恢复也必须查询同一个 owner，不得以空数组冒充已经排空。仍有 blocker 时保留 pending，不提交固定的 Accepted 请求，以免以后真实排空时因同 requestId 的不同载荷产生冲突。资源 owner 尚未安装时不能当成没有资源。

发现查询显式请求最多两条候选；返回满页时不能声称结果唯一，即使本地过滤后只剩一条。精确候选必须有有效 session identity 并明确属于 Manager；SDK 错误、非列表响应、缺身份和错误 agent 都是拒绝，不得静默丢行后推断唯一性。

恢复以单个 work 为失败边界。初次读历史或等待 Host 之后再读历史发生错误时，诊断必须指明 workId，保留坏记录和未结算义务，继续恢复其他独立 work。不得把损坏记录解释为“没有工作”，也不得通过跳过记录修复该 work；正常业务读取仍拒绝这份历史。

启动枚举通过同一连接批读候选 work，共用一次全库完整性检查；每个 stream 的修订、字节校验和语义折叠仍各自执行。不得为每个历史 session 重开数据库、重复扫描全库。交付恢复与固定 DevOps 目标恢复复用本次枚举结果，不把它保存为跨调用权威；待执行的外部效果仍在其原有边界重读并核对当前事实。

## [049] ask：向用户提问与显式暂停

空账 review 经既有 PromptAuthority 询问「我需要干什么？」，不写 Reviewed，不推进里程碑。编程面 `this.ask`（[050]）不是生命周期阶段门，也不代替这次询问。`ask` 不注册独立宿主工具。它不调用 DevOps，不假装拿到了用户回答，也不完成工作。

- 真实呈现询问并成功登记 `UserQuestionAsked` 后，返回“你的询问已经发出”一类占位回执。事实绑定 session、logical run、authority root、提问所依据的物理输入、provider run、tool call 与原问题。占位回执不是用户原文，也不是完成报告。
- 在 Host 已确认同一个 ask 工具结果的 `message.part.updated/completed` 上，立即调用 `InterruptAttempt`，不靠延时器猜测完成顺序，不在返回结果前销毁工具。不得使用逻辑终止、关闭 authority 或递归取消子资源替代。重复终态不重复中断；别的 call、别的会话或已回答提问的迟到事件不许中断新执行。
- 等待只从 durable 问题事实派生，进程缓存不成为另一份权威。未回答期间不发工作 nudge、不继续其他工作工具；Host-native 工具的 before 门和插件执行门同样拒绝。`ask` 精确重放只返回原确认，不重复发问题；异载荷同身份、第二个未决问题、空问题、无可用通知或持久写入失败都明确失败。
- 只有同一逻辑运行的新真实人类输入才追加 `UserQuestionAnswered` 并解除等待。导致 ask 的旧输入、内部提示、插件 continuation、通知和 provider 文本都不算答复。原提问者的终态即使晚于用户答复到达，仍不得关闭或重催新一轮。
- 用户答复沿既有 HumanMessage 准入继续同一任期，不强制新建会话，也不让用户另行调用恢复工具。若显示、存储或 interrupt 失败，必须明确报告实际完成到哪一步，不伪造“正在等待/已经继续”的事实。

[027]、[049] 的宿主行为由 `tests/027.test.mjs` 与 `tests/049.test.mjs` 以真实插件/日志组合的行为断言承载：工具结果先落地、再 interrupt 的实际顺序、无关与迟到事件不作暂停见证、重复终态幂等、用户回复与旧终态交错时的恢复行为。Long Stroke 级真实宿主验收按 verification-system [014] 归发布验证执行。

## [050] js.ask：Plan 编程面的提问成员

Plan 的编程面（`todowrite-for-plan`）携带 `js.ask(question)`：程序内调用即登记 durable 问题并通知用户，然后经统一 park 通道挂起，工具返回 parked 载荷；程序不继续执行，直到恢复。这次 park 不写 Reviewed，不推进里程碑，也不锁定晋级资格。等待期间的新调用受既有等待门禁拒绝，只有真实用户输入解锁。真实用户输入解除等待后，提交新名字的控制程序，在工具顶层提供必填 intent，在程序内调用 `unpark(name, value?)` 继续原现场；恢复使用控制请求准入时确定的剩余期限，不重跑原程序，恢复控制者不隐式再次唤醒目标。放弃通过新控制程序的 `abandon(name)`。parked 载荷携带完整程序与执行点 source 标记（与生成面同规）。

## [051] 首任 Manager 空账评审的真实问询

初任空账的 review 不得写成 Reviewed，也不得推进里程碑。问句是「我需要干什么？」。连续三次空账、三次真实答复，产生三次询问、零次 Reviewed、零次接力。答复之后仍是同一任期。第一次非空账才进入工作期。问询失败时不得假装 Reviewed，也不得伪造用户答复。

证明：`wanxiangli/src/lifecycle/tests/002.test.mjs` 核对三次询问与同一任期。宿主物理 park 仍按 [049] 取证。

## [052] Manager 交付之后的正常新循环与末任收尾

收尾空账且真实资源阻塞已解除时结案，不另开验收任期。已完成之后，只有新的真实操作员消息重开执行恰好一次。Advisor steer 与其他代理答复重开零次。旧退休记录不删除，同一消息重放不再造第三任。

工作期的 Manager 不因某次空账获得或失去资格。任期仍活跃、且没有真实资源阻塞时，Fork、Resume、Join 与后续 review 保持可用。收尾仍有真实阻塞时不得结案。已完成后再 review 拒绝。

证明：`wanxiangli/src/lifecycle/tests/006.test.mjs` 与 `tests/007.test.mjs` 核对阻塞、结案与一次操作员重开。
