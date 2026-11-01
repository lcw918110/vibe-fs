# delegation 测试说明

规则见本包 WHAT。用例标题里的 `WHAT[...]` 是条款锚点。TODO 是未完成，不算通过。完整 Manager/Orchestrator 场景仍要真实宿主巡检补上。

`support` 只建真实 runtime 需要的临时 journal、可控物理端口与清理。tool schema stub 只能构造工具，不代表真实 Host schema 校验。等待同时考虑 prompt 与 invocation 拒绝，避免把合法拒绝当成挂死。新建角色限制不改历史解码。

Join 的条数上限、显示窗口与交付视图的有界物化是三笔不同预算。renderer 保留单个大 entry 的用例只说明调用方材料没被截断，不说明材料由真实 bounded projector 限定。

| 文件 | 实际观察 | 尚未覆盖 |
|---|---|---|
| 001、004、017、020、030 | 只有占位与 TODO | 全体委托四要素、全局工具合同唯一、返回不扩权、换工具仍守约、真正 fatal settlement/report/kill |
| 002 | 生产权限投影及兼容 calling | 实际直接/包装/转发请求的全链权限；权限表不能说明模型自行返回 |
| 032 | 生产权限投影；另有真实 fork 入口断言：Manager 派发 `devops` 被拒，返回文案不宣告承接，且 `childCount`/`promptCount` 不再增长、无 durable handle（先以合法 engineer 派发确认计数器是活的） | 包装与转发请求形态上的同一拒绝；权限表不能说明模型自行返回 |
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
| 031 | 连续完成复用同一 child 并交付 bounded view、旧 root 拒绝、parent supersede 取消、完成与删除在两种顺序下各结算一次（`HandoffCheckpointSettlement` 已随父到子 `DelegationHandoff` 退役） | 真正重复 terminal；SyncDelegate 的结果不能替代 fork 的观察 |
| 042 | 重启恢复按 `assistant.ParentId` 读取持久 `ChatExecution.Accepted`：重试物理 ID 不再被提升为 Root，交付见证与完成体共用同一 durable Root；已有 `HandleWorkCompleted` 的 durable 终态不要求 Accepted、不重复写入，join 仍返回材料；缺失 Accepted 只观察为 `RecoveryIncomplete` | 真实 OS 重启后的完整 Host 接线；用例直接驱动生产恢复入口与真实 journal |

## scoped work 与未交付子工作

稳定 handle、Byname、participant/TargetAgent 与物理 child 绑定不因工作结束改写。canonical authority 接纳的 exact physical landing、owner identity、child、Root、LogicalRun 与既有 binding 共同建立封闭 AdmittedWork；一个 Root 字符串或重复 HandleLinked 不发行新工作。新工作可以在旧 completion 未 join 时接纳，每份结果按 exact work 保留、消费，迟到 A 不关闭 B。024 的「两份交付视图均保留且各交付一次」只用原有 fork/resume/settle/join API；`support/scoped-work.mjs` 只调用正式 Surface，在 append capability 接缝注入未知与确认前拒绝，不模拟第二套 fold。

未交付的子工作永不写完成，但「未交付」本身不是终态：只要交付仍可能，它就保持活跃、join 不关闭。交付已不可能时（abort、取消、或重启时结算上一进程遗留的孤儿），系统追加 `ChildWorkVoided`（无 scope 的历史用 `ChildRunVoided`）。两类作废都走 abandon：scoped 工作的单元格转为 `Abandoned(Voided)`，报告后退役；底层 handle 绑定仍可复用。026 的重启用例覆盖整条路：加载期作废 → horizon 显示 `voided` → join 报告一次 → `durableLifecycleByname` 经 `summary` 读出 `Retired` → 名册不再列出 → 同目录再起进程不再报告。结算是按孤儿幂等的，每个孤儿只产生一条作废事实。

作废的持久原因是 `HandleAbandonReason` 的第四个构造子 `Voided`；`wanxiangli/handle-lifecycle` 的 `recoverChild()` 接受 `ParentCancelled`、`DeadlineExceeded`、`HostSessionGone`、`Voided` 四项。旧口径「只有前三项」已作废；库 0.66.0 起接受 `Voided`，主仓 gitlink 已前移到该版本。

历史无 scope 的 completion/retirement 保留原字段与审计含义，不自动绑定新 Root；混合或多轮无 scope 历史明确隔离，当前运行链不自动消费。旧物理布局仍不读取；要离线迁移须外部给出 exact admission/terminal/closure/consume 身份与内容地址材料，启动时不得推测。

直接 `node --test` 只用于局部调试；正式结论走 verification-system runner，TODO 会让它返回未完成。
