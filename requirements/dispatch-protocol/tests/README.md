# dispatch-protocol 测试

规则以 WHAT 为准。本目录区分调度事实、Host 证据、准入交接与执行结果，不把“函数存在”当作没有旁路的证明。

2026-10-06新增有限证据：007验证每次callback注册的独立lease、同步Host异常/Unknown只释放本次资源、effect前交接及旧Unknown A不撤销B；006/007验证fresh Root与exact close保留LogicalRunId=None的Pending和已消费序号，清理Some continuation，包含合法Some("\0absent")反例。序号记录携带typed session/run所有权，不再从编码前缀推断；真实Manager Ledger出口复用同一exact close，非Manager/AgentOwner为对照。007的测试时钟在创建实际确认计时器前开启，保留原deadline与断言。gen184相关246/246、1290pass/0fail、28skip/137TODO，见[记录](../../../proposals/archive/2026-10-06/Sphinx真实恢复与资源前置-2026-10-06.md)。这是本地资源与相邻authority清理证明，不是完整GAP-123 durable closure、真实Host或OS crash证明。下列D0/D1的未证项是各自历史证书边界，本轮新增范围由本段另结算。

- 001 实际 owner-root/continuation 在 Host 端观察注册好的 claim；全体合成消息来源仍需逐一验证。
- 002/005/006 有真实生产 projection、sequence、hash 输入与兼容解码。005 单独改变六个组成要素；它以注入 hash 观察确定性输入，未声称穷尽哈希碰撞或跨进程故障。
- 002 新增实际 `ManagerWorkflow.observeIdle` 发送链：有效许可只发送一次并被消费；旧 idle 调度暂停后，新 Human 入场撤销许可，恢复旧调度只追加 exact claim 的 `Abandoned(SupersededBeforePhysicalSend)`，SDK 发送为零。隔离副本恢复旧发送函数时，两条行为断言均失败。此接缝调用生产 owner 与受控 Host port，不是安装版 Host 或 OS 重启证明。
- 002 另新增两条 claim-append 边界证明（与 effect-accounting/003 共证同一生产边界）：journal 写者关闭、claim 无法持久化时，SendPrompt 零调用、发送在 journal 边界失败，且重开冷读无 pending claim、无已消费 sequence；transport 已入栈未完成的窗口内，第二个 writer 冷读已能恢复 exact pending claim（receipt 为 null、sequence 已消费、恰一次 send）。claim append 的物理悬置（held）未注入生产 barrier 接缝，由 fail 与时序两侧断言共同覆盖；跨进程 crash-cut（B3）未做，冷读为同进程第二 writer，不宣称 OS 重启证明。
- 003 实际 receipt 发送、关闭并重开 journal，确认两种外观的 receipt 均不授予权限。004/007/008 的历史用例多为同进程额外 journal writer，不是 OS crash。保留实际拒绝、未知、查不到证据、不可读及并发 flight 的行为断言。
- 009 把 Host Promise 保持未决，直接观察 Detached 是否返回，再释放端口；不再用 120ms 竞争当完成事实。晚到 verdict 的用例注入于 dispatcher 端口，不证明 SDK/HTTP 适配器自身正确分类。构造器字面名单已删除。
- 009 的 A2-D0 新增 root/continuation 两入口各四例：SDK仍未决时早返回并由真正 managed ingress 通知 exact physical，callback内已无Pending且managed execution已Accepted；原 Host listener 的 OwnedSettled/Unknown 不替代物理证据，Unknown不重发；既有 typed Refused 后原key拒绝晚接纳且不通知。gen157六个callback-empty正式业务红后拆开register/waiter，gen159相关238文件1178/0。关联003/004/007只作条款关系，标题保持唯一009锚点；gen158多锚点的017失败原样保留。Refused反例不独立证明内存registry移除，不关闭GAP-137晚Fatal分岔；callback抛错、同步Host抛错后的通知、取消/Dispose及三producer的call来源仍未证。
- A2-D1有限完成：004两路径callback原Error传播且durable Accepted不倒退、两个确认者仍得exact Accepted；007两路径的非FIFO short timeout及全部waiter过期不干扰observer/journal，009两路径的明确Refused仍结算全部确认等待。gen163旧业务6个真实红，gen165正式3/3、36/0、1TODO，freshness前后一致；测试时钟只控制实际确认deadline，不用wall时间猜业务身份。D0上条“callback抛错未证”已在该范围补齐；lease、同步Host异常、call来源、真实Host及OS crash仍另验。证据见[正式记录](../../../proposals/archive/2026-10-05/Sphinx确认等待隔离-2026-10-06.md)。
- 010/011 使用实际接受的 root profile 续行，验证发送参数、完整 PromptKey、Origin 与 LogicalRun 元数据。profile 没有 model 字段是投影证据，不代替全部调用者证明。
- 010 的 B4 负编译（GAP-136 的 F# 类型级证明）按 durable-events/023 与 effect-accounting/001 的仓库成熟模式落地：正例 probe 先证明 root witness record（`OwnerIdentityWitnessInput` 四字段）、identity seed union 两个 case、`SendOutcome` 回执值与真实 `SendAgentOwnerRoot` 七参数公开构造在 dispatch-runtime shard 真实闭包内全部合法；随后三个 probe 分别尝试注入 witness record 的 `ModelTarget` 字段、root send 的第八个 model 参数、把 `SendOutcome` 回执塞进 identity seed 参数位置，各自必须以类型边界诊断失败。真实 `Send.fs`/`Send.fsi`/`Dispatcher.fs` 以 bytes/mtime 快照证明未被触碰。诊断正则含 Fable 实际措辞变体；若 Fable 措辞校准需要增补变体，属于措辞校准而非断言弱化。
- 010 的 managed assignment 回归在 journal 重开后继续 Manager 与 Engineer 子会话，确认 run/root/participant 不变，副本的精确工具权限不丢失，且 continuation 不重新取得 root identity seed。
- 012 实际 claim acceptance 与独立 turn reconciliation，完整 handoff/容量所有权仍有 TODO。013 是 journal 重开与显式恢复，不能证明真实插件构造无副作用。014 的自造 fatal descriptor 已撤，真实 invariant incident 仍需证明。
- 015 的载体属性测试、诱饵/getter/冲突输入继续保留；新增实际插件的非法 PromptKey 和非法 agent 反例，防止错误被折叠成 Missing 后获得新 authority。

`support/authority.mjs` 仅准备实际 Persona、Journal 与 dispatch 入口所需的测试输入，并负责临时目录和 journal 重开；不在夹具中复制权限或恢复算法。

GAP-136 记录证明缺口，GAP-137 保留 [009] 晚到 Fatal 的合同分岔；GAP-138 记录真实非法 carrier 升权缺陷及修复。测试里注入的低层观察不是新的验收规则。

正式执行先构建，再通过 verification-system/tests/run.mjs 的 TESTS_MJS_FILES 选择本目录及受影响的 authority、chat admission 测试。TODO 和跳过不算成功。实际插件用例依赖隔离的 fixture 配置，不修改开发者 OpenCode 配置；目前不使用真实 provider，不宣称 Long Stroke 完成。
