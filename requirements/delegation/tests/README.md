# delegation 测试与证明范围

2026-10-26：026 增加真实插件反例：同一 child 完成一次工作后，下一次 assignment 的发送尚未返回时先接纳一条指导，再自然停机。修复前无 Completed terminal，父 join 无结果；TurnBinding 改为以 durable authority 投影确定 Root，物理游标不再覆盖 Root。隔离 checkout（7f03d2ad8 加本修复）统一构建成功；026、host-boundary/005、managed-session-lifecycle/006、007 共 61 pass、0 fail、1 TODO。主工作树构建被既有迁移中的库 dispatch 与宿主文件编译错误阻断；此证据不代表运行中 OpenCode 已加载修复，也不证明所有现场未结算任务已恢复。

2026-10-06 A2-R0/R1/R2有限验收完成：025以实际PluginScope、同一managed owner和隔离配置驱动retry及两类repair通过；旧/晚successor的16个业务红已修。typed source在effect前交给具体call，actual PhysicalAccepted才登记其successor；旧same-root+历史kind宽匹配已删除。每次注册有独立opaque lease，关闭只释放该call资源；相邻Unknown Root和Fork边界另取证。gen184相关246/246、1290pass/0fail、28skip/137TODO，原预算及并发保留，见[正式记录](../../../proposals/archive/2026-10-06/Sphinx真实恢复与资源前置-2026-10-06.md)。实际安装版Host、物理终止/OS crash、完整025/T111/GAP-153及Sphinx答案仍未闭合；下一施工从[08接手卡](../../../proposals/TODO施工分册-2026-10-03/08-Sphinx真实Host接手.md)开始。

历史A2-D1证据：gen163六个真实业务红后，gen165定向004/007/009为36pass/0fail、1TODO；独立TCS、超时只释放自己，callback抛错仍广播exact Accepted并传播原异常。见[dispatch记录](../../../proposals/archive/2026-10-05/Sphinx确认等待隔离-2026-10-06.md)。D1本身不是025来源证明，本轮R0/R1/R2另取证，不重复施工底层确认。

历史A2-D0证据：两发送入口注册已有callback，gen157六业务红后gen159相关238/238、1178/0，见[记录](../../../proposals/archive/2026-10-05/Sphinx恢复通知前置-2026-10-05.md)。D0当时没有给三producer提供具体call source，现由上段新证据接续。

历史N06-B1-A只结算SyncDelegate的typed观察：真实native send/key、持久physical/root、普通正式响应与原checkpoint，receipt-only仍待physical、原生Unknown/Refused不按文案猜测、真Promise异常及本地订阅释放。025四个独立正式业务红后gen154相关225/225、1125/0，见[记录](../../../proposals/archive/2026-10-05/Sphinx执行观察与估值守门-2026-10-05.md)。当时的same-root+kind归属缺口已由本轮有限矩阵修补；本地Dispose仍不冒充物理abort/drain，Sphinx实际调用未接入。

e1合并的gen77官方510文件与完整36文件integration均0 fail。003实际证明取消期新assignment零放置/发送、固定DevOps原回调仍能结算、取消失败后全部两个owner订阅清理、同步异常后可重新取消以及重开不新建child；025/026与加载恢复沿实际scoped owner运行。TODO、OS crash与永久丢失替换不由这些断言代证；汇总和原始失败见[同步记录](../../../proposals/archive/2026-10-03/Upstream同步-e1e7dd3f1-2026-10-03.md)。

规则来自本包 WHAT。用例标题中的 WHAT 锚点是映射；本页说明当前证据，不增加合同。TODO 是未完成，不计通过。本站对应人工巡检 42，完整 Manager/Orchestrator 场景仍需后续站收证。

| 文件 | 当前实际观察 | 尚未证明 |
|---|---|---|
| 001、004、017、020、030 | 明确待证 | 全体委托四要素、全局工具合同唯一、返回不扩权、换工具仍守约、真正 fatal settlement/report/kill |
| 002 | 生产权限投影及兼容 calling | 实际直接/包装/转发请求的全链权限；权限表不证明模型自行返回 |
| 032 | 生产权限投影；另有真实 fork 入口断言：Manager 派发 `devops` 被拒，返回文案不宣告承接，且 `childCount`/`promptCount` 不再增长、无 durable handle（先以合法 engineer 派发证明计数器是活的） | 包装与转发请求形态上的同一拒绝；权限表不证明模型自行返回 |
| 033 | 直接 seeding 的 durable 见证 + 已链接子 handle：`publishDeliveredReturn` 在中断前发布与该见证一致的 `Completed`（role/providerRun/材料来自见证与 handle），父方 `join` 因此有返回可收；无匹配见证的围栏不发布。另一条直接驱动 `js` 工作流：`UnattributedDelivery` 以 `DELIVERY_NOT_ESTABLISHED` 失败且不留进程内交付格 | installed Host 上的完整 fork→deliver→cut→join 链路：本机真实 fork 夹具在本次改动前即超时（整合树 `dist` 陈旧、链接不闭合），留给冻结后的正式验收 |
| 034 | 生产首轮双向渲染与指令/数据分离（父到子仅传当前指令与 RootRequirements，无历史/context 字段；子到父 assistant 散文作为完成报告且不入数据字段）；（待补测试）negotiate pending 对称时序冻结（无预算后缀、不递减、弱提示关闭）、新工具结果与 assistant part 持续挂载 MUST 提醒（逐字重放）、普通工具不受阻且 resetScope 自身自拒，以及真实答复送达解除 pending。 | 真实物理回答送达后的时序恢复、跨进程闭环及 provider 缓存前缀命中仍待证。 |
| 003、006、027 | fork/resume 工具接收、拒绝 calling、复用 Byname；忙碌 Engineer/固定 DevOps 追加指导，warm/cold work 与原完成回调不变；完成后无需 join 即新工作；027 的无 prepared handoff 旧 Root Failed/Aborted 拒绝断言保留 | 跨进程物理恢复绑定；同进程 canonical reopen 不是 OS crash |
| 005、013、014 | 真实 renderer、独立 TOML 解析、handle fold；实际 Join 队列上限与完整剩余项消费；commission mailbox 的 FIFO、余项与空队列 | 全部参数/错误不泄漏拓扑、真实 agent completion 的 owner/CAS/恢复；commission 完整入口接线 |
| 007 | 真实同步普通/批次入口拒绝已废止 Coder/Inspector，拒绝前不创建 child、不发 prompt；Engineer 正例 | Sphinx 受管入口、无环与 family-root 放置的整链约束；032 的标准权限投影不替代入口授权 |
| 008—012 | 真正同步调用：相反到达顺序仍按给定 Host 顺序合并、一次发送、scope 隔离、复用新 assignment、普通完成、canonical 正文与 sibling 引用 | Host 实际收集调用列表的接线、系统模型调度与持久 binding |
| 015、027 | 实际 Join 等待器、受控 PTY 端口完成、虚假唤醒、三种中断、锁与 permit 拒绝；commission mailbox 中断后 job 保留、完成与中断竞争；可见消息去重且不预埋下一次中断；027 安装版 Host 的用户输入释放 Manager Join、Manager resume 忙碌 child、下一次实际 wire 含指导，原 work 完成并被 Join 消费 | 真实 Host 操作员取消的完整接线；端口没有启动 OS PTY |
| 019、021 | 生产 typed renderer 的两种语言、敌意 TOML 样本、指令与数据平面分离、空背景省略 | 真正首提示读取最新 durable parent、指定 peer 的材料来源、权限不克隆 |
| 022 | 完整语言资源选择、estimate fold 的去重与零饱和 | 归零后仍能真实执行、同一 durable estimate 的替换/重开 |
| 023 | 同步 workflow 消费注入 retry verdict，恢复成功或耗尽后返回 | 真实恢复 engine 各路径与最终失败组合 |
| 024—026 | 同名/同角色复用、own child delta、因果完成/失败、实际 fork 接收与不确定发送后果；Host event port 保留身份（父到子 `DelegationHandoff` delta tracking 已随显式 charge 传递与共享 ANCHOR 地址空间退役）。026 的重启未交付子工作用例现名 `RESTART_PENDING_CHILD_WORK_is_voided_at_load_and_join_reports_it_once_without_hang`：加载期把上一进程遗留、已无法交付的子工作**作废**（不是完成），horizon 名册把它显示为 `voided`，join 以 `voided` 报告一次后该工作退役并离开名册（scoped 情形被 abandon 的是工作单元格，底层 handle 绑定仍可复用，`summary` 读出终态），同一目录上再起一个进程不再重复报告 | 所有 terminal 分支、进程 crash cuts、未知接收的后续收敛 |
| 028、029 | 当前编译清单的直接/传递闭包、额度及反向注入检测；部分源码词形审计 | 各 locality 独立真实编译、类型隔离和实际 capability 行为；路径/词形只是当前结构审计 |
| 031 | 连续完成复用同一 child 并交付 bounded view、旧 root 拒绝、parent supersede 取消、完成与删除在两种顺序下各结算一次（`HandoffCheckpointSettlement` 已随父到子 `DelegationHandoff` 退役） | 真正重复 terminal；SyncDelegate 不替 fork 证明 |

`support` 只建立真实 runtime 所需的临时 journal、可控物理端口和清理。tool schema stub 仅支持构造工具，不证明真实 Host schema validation。等待同时考虑 prompt 与 invocation 拒绝，避免将合法拒绝误作挂死。新建角色限制不改变历史解码。

Join 的条数上限、显示窗口和交付视图本身的有界物化不是同一预算。renderer 保留单个大 entry 的测试只证明未截断调用方给的材料，不能证明材料已由真实 bounded projector 限定。

014/015 保留 PR #42 新增的四项 mailbox 回归。三方合并曾各复制两遍；核实两份逐字相同后只删除重复副本，断言均保留一次。028 保留上游 Host adapter ratchet 312；旧说明中的 307 是此前认知 runtime 引入时的历史值，312 来自后续 prefix shard 显式依赖。

旧 chooseWork/evidenceBoundary 的固定答案、serializationDecision 的复制判断、reuseBinding 的单次读取及 batchOrder 的过滤器已移除；无用 deferred-result exports 同步删除。实际受控队列与有效渲染证据保留。源码中有方法名、WHAT 自我匹配、手写 descriptor 都不算实现达标。

旧角色在拒绝前创建 child 的修复及回归保留，待新基线复验（GAP-154）。完整待补范围见 GAP-153；编译额度归属沿用 22-D1，DevOps binding 与 work handle 复用沿用 34/35 待决，不在测试中另设规则。旧发布清单及fatal库存的历史状态不能代替当前真实执行证明。

## GAP-132 的 scoped work 实现与证据范围

稳定 handle、Byname、participant/TargetAgent 与物理 child 绑定不因工作结束改写。canonical authority 接纳的 exact physical landing、owner identity、child、Root、LogicalRun 与既有 binding 共同建立封闭 AdmittedWork；一个 Root 字符串或重复 HandleLinked 不发行新工作。新工作可以在旧 completion 未 join 时接纳，每份结果按 exact work 保留、消费，迟到 A 不关闭 B。

024 增加只使用原有 fork/resume/settle/join API 的“两份交付视图均保留且各交付一次”测试，旧裸 link 重开并覆盖结果会被此断言击红。managed-session-lifecycle 006/007/008/015 再验证 scoped cell、历史墓碑、回执未知、并发消费与 canonical 冷重放。support/scoped-work.mjs 只调用正式 Surface；未知/确认前拒绝注入在 append capability 接缝，不模拟第二套 fold，不证明真实 kill/crash。

历史无 scope completion/retirement 保留原字段、引用与审计含义，不自动绑定到新的 Root；混合或多轮无 scope 历史明确隔离。当前这些结果不由新运行链自动消费。旧物理布局仍不读取；若需要离线迁移，须外部 exact admission/terminal/closure/consume 身份与内容地址材料，不能在启动中推测。

本次合并已接入 scope vocabulary、Surface 登记、编译引用与正式测试，统一 Fable 构建通过。gen72 的 Node22 定向生命周期与 Scope 诊断为 15 passed、0 failed、5 TODO；该范围只包括 managed-session-lifecycle/013、018、025 及 relay-retirement/009，不替 delegation 全包背书。固定 DevOps 道路的空闲可见性、普通取消保留固定 work、回调排空与 detach 订阅释放仍以本批新 owner 的最终正式 runner 为准，证据与未通过的旧产物反例见[本批同步记录](../../../proposals/archive/2026-10-03/Upstream同步-e1e7dd3f1-2026-10-03.md)。GAP-132 的完整迁移、历史隔离及物理恢复范围仍须分别验收。没有新增 durable ActiveWorkUnit、generation 或时钟胜者，也没有改模型路由/Role 政策。

本次合并将 `OpenCode/Host/ChildWorkRecovery.fs` 的启动结算接到 exact `ChildWorkVoided`：只选择 canonical Active work 与 child 当前 Root、LogicalRun、physical child 一致的已有 `AdmittedWork`，不创建 completion 或 join payload；无 scoped work 的历史恢复独立保留 `ChildRunVoided`，不能为新 work 补造 scope。`crash-reconciliation/018` 新回归通过真实 dispatcher physical acceptance、journal 重开和生产恢复入口，验证旧 work 作废、稳定道路保留及显式新 work；`020` 保留持久化写入受阻与保持健康的 writer 用例。gen70 构建通过，Node22 定向启用 integration 的 018/020 为 10 passed、0 failed、3 TODO；正式 runner 汇总仍待本批验收，真实 plugin activation 与 OS crash 不在这份局部证据内。旧 `Retired` 或不同 child 不授予 DevOps physical replacement；永久丢失、关联关闭及固定 executor 证明仍须由 lifecycle owner 提供，未扩出猜测性的 replacement writer。

## 未交付子工作的作废结清（2026-10-21 裁决）

未交付的子工作**永不写完成**。但「未交付」本身不是一种终态：只要交付仍可能，它保持活跃，join 不关闭，父方可以继续等待或重派。交付已不可能时（被 abort、取消，或重启加载期结算上一进程遗留的孤儿工作），系统追加 `ChildWorkVoided`（合法但无 scope 的历史用 `ChildRunVoided`）——**两类作废事实都走 abandon，没有一类是 abandon 规则的例外**，差别只在于被 abandon 的是工作单元格还是 handle 绑定：对 scoped 工作，具体工作单元格（`Works[exact id]`）转为 `Abandoned(Voided)` 并在报告后退役，它承载的那份**工作义务已是终态**；底层 handle 绑定仍**可复用**（它标识可再次参与的参与者身份，不是被作废的那份工作），`summary` 投影叠盖工作并读出终态；对无 scope 的历史，其 unscoped handle 绑定自身转为 `Abandoned(Voided)` 并在报告后退役。**作废不是完成**：不写完成正文，也不把已有交付视图当作完成凭据；可它**结清父方的未解除义务**：join 以 `voided` 状态报告一次，报告之后该工作退役（durable）并离开名册。join 的状态词表因此是 `completed | failed | abandoned | voided`。

本包 026 的重启用例正是这条合同的唯一端到端见证：加载期作废 → horizon 显示 `voided` → join 报告一次 `voided`（既非 `completed` 也非 `undelivered`）→ `durableLifecycleByname` 读出 `Retired`（**经 `summary` 投影读出的终态**，不是从原始 handle 绑定直接读出的）、名册不再列出 → 同一目录上再起一个进程不再报告。**结算按孤儿幂等**：每个孤儿一生只产生一条作废事实，**重启的进程不得再次作废同一份孤儿工作**（journal 里不会出现第二条作废事实，join 也不再重复报告）。crash-reconciliation-018/020 从 journal 侧见证同一结算（每个孤儿一条作废事实、零 completion 事实、重复结算不再追加事实）。**本页只描述合同与见证范围，不记任何测试结论**；这两组用例的通过与否由行为泳道与正式 runner 给出。

作废的持久原因也是 `HandleAbandonReason` 的第四个合法构造子 `Voided`：`wanxiangli/handle-lifecycle` 的 `recoverChild()` 接受的 `durableReason` 全集是 `ParentCancelled`｜`DeadlineExceeded`｜`HostSessionGone`｜`Voided`。**已被 2026-10-21 作废裁决取代的旧口径**是「只有前三项」，现已作废。库侧 0.66.0 起接受 `Voided`；**主仓的 `wanxiangli` gitlink 也已随 `cfa8d0dad` 前移到 0.66.0（`663ba59`）**，该 gitlink bump 已随行为泳道的 `6ccc761c2` 进入 `origin/master`，主仓恢复路径交出去的 `durableReason = "Voided"` 现在交到的就是已发布并接受它的库版本——落地链上这一环**已闭合**。

本次结果见[迁移记录](../../../proposals/archive/2026-10-03/20模块迁移-恢复与委托-2026-09-28.md)。直接 `node --test` 只用于局部调试；正式结论使用 verification-system runner，TODO 会使它返回未完成。
