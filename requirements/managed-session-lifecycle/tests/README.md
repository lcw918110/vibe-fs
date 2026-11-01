# managed-session-lifecycle 测试说明

规则见本包 WHAT。这里只写各条用例实际看到了什么、还缺什么。

- 001/005 调用真实 AttachedSessionRuntime：同 owner 重用、保持绑定的 agent、显式移除后重新创建。005 还用同一个实际 owner 验证不同 scope 隔离、同键并发仅创建一次，以及不同 typed Role 的绑定可分别重用和移除。后者只说明准入后的资源键，不授予 Coder/Inspector 活跃身份；[delegation 007](../../delegation/tests/007.test.mjs) 和 [participant-identity 010](../../participant-identity/tests/010.test.mjs) 另证旧角色活跃准入被拒。`retainBinding=false` 表示主动 Remove，不能说明系统检测到永久丢失。
- 002/003/011 调用真实 satellite owner，端口提供 association 与 Host 观察，验证单次创建、确切复用、拒绝冲突和替换结果。端口数组不是一个共同的顺序日志；没有真实 journal 重启或首 prompt 悬置说明。
- 004/009/014 包含同步委托与部分真实 journal。006—010/015 保留 fold、codec、视图和属性测试；另有真实 fork/resume 与 canonical work 消费/重开的断言，但不等同于实际父取消或 OS 进程恢复。
- 016/017 调用实际 Session adapter/termination，悬置清理与 Host abort，观察终态不得提前发生、完整 Authority Root 和根会话拒绝。016 删除了只改变测试变量的“虚拟时钟”断言。017 尚未接真实父等待与全部 successor 入口。
- 016 的外部输入合同另由 host-boundary 033 安装版 Host canary 共证：当前 SSE 和非 Join 工具自然完成，新输入仅进入下一次 provider 请求，不发出 SDK abort；默认容量与 Manager 单槽容量均覆盖。Join 中断及不取消 child 见 delegation 015。
- 018 实际 fork tool detach 后读取 durable Active，断言未 abort；另经真实 Scope.CancelSessionChildren 发起受权取消，在 Host abort 端口受控悬置时读取目标 child 的 durable Abandoned，另一个 owner 的同名 child 仍 Active，取消必须等端口完成才返回。两个隔离违约分别漏写 abandon、丢弃 cleanup 等待，用于独立验证断言。四种非授权终止（TurnAborted、provider retry、Fission external-abort 形态、unknown stop）经真实 run lifecycle 投递后按各自真实语义落账、永不写 Abandoned/Retired，冷重放一致且冷重开进程 re-enlist 复用同一 handle 与 child（见下文 session018 核对段）。这些仍不说明 OS 进程退出、全部 PTY 清理或物理 Host 会话存续。020 使用实际插件重开 journal，加纯 authority 属性测试；它不说明尚未落地的五类 durable exact closure。
- 019 保留实际 execution interpreter 的局部验证，cancel/delete 的 drain 断言已落地。021 的真实 fatal 链仍缺观察。022 运行真实 Casebook：有/无 draft、Bookkeeper 不可用、归档读取与重复拒绝；尚未观察 identity 去留及五类 typed settlement。
- 023 调用实际身份解析，区分拒绝旧活跃身份与历史恢复。024 只说明 handle 拒绝第二个活动绑定、road 保留给定模型；不是 crash、模型固定或 PTY 排空说明。
- 025 保留上游的真实 HostForkRuntime 与 ToolRuntimeScope 场景。DevOps 工作先经真实 dispatcher physical acceptance 建立 scoped work，两处 Surface 显式接收同一 canonical journal，不用临时 Root 或无 journal 的 InstallRun。受控 PTY port 结束后清记账；DevOps run 终态结算时在 PTY 端口边界观察到终止效果，其名下 PTY 计数归零且其他会话的 PTY 存活。端口未启动 OS 进程，「TERM 后真实退出并按需升级 KILL」这一半仍未做，现由 `test.todo` 单独保留，不因上面那条已转真而消失。
- 030 WHAT[managed-session-lifecycle-030]：真实 journal + 真实共享 capacity 上驱动会话级取消结算——保留输入持有信用的 session 在 `signalSessionCancelled` 后 `drainSession`（生产 `Drain`，取消收尾与删除链同一 await）必须收敛：终态唯一 Cancelled、旧 credit 归还（读快照核对）、drain 先等后成、无关会话租约原样。不涉及 OS 级进程退出、共享管线其他 await 点、或 HostBootstrap 取消分支的结算归属（那部分归取消修复所有者）。
- 029.test.mjs：真实 Fork→Engineer 非空 stop→父 Join 复现 session.idle 缺失下的终态 lost-wake；修复前红 JOIN-HUNG、修复后绿色，外来 physical parent 无法冒充当前 Terminal；相邻 005/028 保持身份、故障边界。并不代表已热替换运行中 OpenCode 进程。

## 已知缺口

- 取消与删除链的完整 drain、重启后的 scoped work 重开与冷重放已有用例，但都跑在内存 harness 或受控端口上；OS 进程退出、真实 TERM/KILL 升级、半行写盘仍是未做项。
- 无 scope 的历史 completion/retirement 只读审计：保留原字段与墓碑，不由附近 Root 升格，也不自动被新运行链消费；要迁移须外部给出 exact admission/terminal/closure/consume 材料。

## 执行

先跑正式构建，再用 `TESTS_MJS_FILES` 指定本目录条款测试，经 `requirements/verification-system/tests/run.mjs` 运行；跨插件用例要开 `WXS_TIER_INTEGRATION=1`。TODO、未完成文件与静默监督中止都不算完成，局部直接运行只用于定位问题。

- 029：真实 Fork → Engineer 非空 stop → 父 Join，复现 session.idle 缺失下的终态 lost-wake；修复前红 JOIN-HUNG，修复后绿，外来 physical parent 不能冒充当前 Terminal。这不代表运行中的 OpenCode 已热替换。
