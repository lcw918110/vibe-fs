# capability-enforcement 测试说明

WHAT 定义可见、可执行与权威消费的边界；Office 的职责上限由 office-capability 定义。本文件只说明各条用例观察了什么、还缺什么，不另立规则。

## 各条观察

- 001/003/004：经生产请求规划器看能力集。003 比较请求投影不超出 Office，并看 Strength 的只读收窄；004 只看旧 tier 输入被忽略，不代替真实模型路由切换。005 看副本工具策略，没有实际执行每一种拒绝。
- 002/006/007/009/010/011/023：配置、工具注册与静态门禁各有范围。部分 Host 权限结果由测试自己的 wildcard evaluator 解释，不代表真实 Host 授权；注册与 rolePredicate 也不等于运行期准入。006/010 的 integration 用例要按设施规定启用，默认跳过不算通过。
- 012：加了合法同角色调用的正例，避免「全部拒绝」也能通过原来的跨角色反例。013 只看伪造外形不能替代真正 permit；六类因果值与 vocabulary 分类仍缺。014 原先把跨 gate 拒绝重复了一遍，现在把 manifest 精确锚点的缺失列为待办；跨 gate 的有效用例仍在 018。
- 015/016：生产 admission 的 exact subject、版本冲突、终态后旧准入拒绝与追加次数。018 覆盖现有 permit 的 typed failure 与拒绝零效果。017 保留消费→释放→再次消费的带断言 TODO。
- 019：两个 gate 的 owner 隔离；另加原生 JSON、真实 Fact/Journal/Event codec、execution lease 与两个受监督 Node 进程的回归。QuiescencePermitToken 与既有 execution/queue token 的发行点补了原生 JSON 拒绝；其他 PhysicalHandle 仍有具名 TODO。020 是 fatal 依赖边界检查，不代替配置失败后的物理退出。024 覆盖插件对 fork DevOps 的拒绝与零子会话创建；固定 DevOps 忙碌准入的全链仍要其生命周期所有者观察。
- 021：生成轨迹固定 seed 20260914、60 次运行，每条完整轨迹作为子测试判决；测试之间归还事件循环。子测试失败传回 fast-check 保留反例收缩。
- 025/026：Manager 只读窗口与 Review 前 DevOps 拒绝。旧 008 的有效生成器、真实 API、沙箱、读路径与观察用例迁到 `support/manager-programming.mjs`；直接执行非法修改并检查磁盘，与正常读取对照。没有物理读取计数时不声称零读。
- 022 的 Fission 配置与调用检查由 intra-participant-parallelism-017 接管。026 的 D07 把零 durable append 待办落成真实无副作用拒绝：经公开 Fork ToolSurface，未接纳 Review 时对 devops 及大小写/空格变体派发同一任务，逐次检查隔离存储目录、文件名与完整字节不变；接纳 Review 后同一任务必须成功交接、建立 Active 生命周期、创建一个子会话、发一个 prompt 并改变真实持久字节。

## 仍未完成

- 013：六类因果值不可互换、vocabulary 正向分类的完整入口观察。
- 014：manifest 的 exact file/symbol/source anchor 准入，目前 validator 只查存在性。
- 017：one-shot permit 与「确定未接受可归还同一 exact permit」两条合同冲突（crash-reconciliation-006、dispatch-protocol-007 要求重消费成功），需 owner 裁决。
- 019：gate、routing runtime/port 与 cancellation token 等 PhysicalHandle 的原生序列化与恢复消费未覆盖；`execution-model-routing/tests/012` 仍断言 `JSON.stringify(lease) === '{}'`，与新拒绝冲突，需其 owner 改断言。
- 025：运行期拒绝时零物理读取/修改、已准入读取跨 Review 接纳的重叠观察。

## 017 的两种读法

现有身份是 opaque gate owner + SessionId + attempt serial，不是永久 SessionId 禁令：`BeginProviderAttempt` 发行新 serial，`DropSession` 保留 serial 墓碑，同 session 的新工作能建立新 permit。生产链是 PluginSessionScope.Quiescence → PluginTransforms 的 BeginPhysicalProviderAttempt、HostSignalBootstrap 的物理消息/idle/abort → OrdinaryTurnWorkflow、Fission Host 的消费；PluginHostInterop 记录 tool body 起止，session cleanup/drop 关资源。`ReconcileSurface` 另有公开 fixture 构造，不能据它说真实 Host 观察已成立。

`OrdinaryTurnWorkflow.applyJoinGuardNudge` 与 `FissionHost.observeOpenLaneCompletion` 把同一 permit 的 consume/release 闭包交给 `HostJoinGuard.nudge`；`HostSessionNudge.trySendIdleGateContinuation` 与 `sendIdleInteractionRepairWithProfile` 也捕获同一 permit。`gateIdleOutcome` 只在 dispatcher 的 NotSent 上调用 `releaseAdmission`；`Send.continuationAttemptOutcome` 只把 Retryable/Fatal 的明确拒绝归为 NotSent，AcceptanceUnknown 不归还。把 TryRelease 改成永远关闭，会直接改掉「明确未发送可重试」这条生产合同。

待 owner 决定：Quiescence 是可归还 reservation，还是每次物理 claim 各自持 one-shot grant。若取后者，可保留 `TryRelease : QuiescencePermit -> Result<unit, QuiescencePermitFailure>` 只关闭 exact grant，另设 `TryAdmitRetry : QuiescencePermit * ProvenNotAcceptedDispatch -> Result<QuiescencePermit, QuiescencePermitFailure>`。这些只是方案，不是现有 export。Dispatch owner 要发行绑定 exact PromptKey/claim 的确定未接受凭据；gate 以 attempt serial + 独立 grant identity 原子发行新许可，旧 grant 不复活，重复凭据或旧 callback 零效果。

方案涉及 Foundation/Quiescence 的合同、Foundation/HostSession、SessionQuiescenceGate、QuiescenceSurface、Interaction/Dispatch 的 Dispatcher 与 Send 的 typed NotSent 合同，以及 SessionNudge、JoinGuard 的释放接线；闭包签名若变，还涉及 OrdinaryTurnWorkflow 与 Execution/Fission/OpenCode/Host。018 与 crash-reconciliation-006 的现有归还正例也要由其 owner 对齐。

## 运行

先 `node scripts/build.mjs`，再用 `TESTS_MJS_FILES=requirements/capability-enforcement/tests/NNN.test.mjs node requirements/verification-system/tests/run.mjs`。019 的 PhysicalHandle TODO 会让监督器 fail closed，这不是通过；不要 skip 或删 TODO 来让局部运行看起来干净。完整门禁仍未验收，见 GAP。
