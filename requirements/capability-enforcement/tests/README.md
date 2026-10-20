# capability-enforcement 测试说明

WHAT 定义可见、可执行及权威消费的边界；Office 的职责上限由 office-capability 定义。

2026-10-03同步增量：以下上游实施说明中的“未运行”属于合入前截面。本批已在gen69取得019的原生JSON/codec/独立进程三项失败，原因是Fable把toJSON发射为静态函数；给私有QuiescencePermitToken补AttachMembers后，gen71官方446文件运行中019的六项active断言及两个受监督独立进程均通过，PhysicalHandle TODO保留。017的exact permit归还合同不改，execution-model-routing/012已按原生拒绝更新并保留合法准入与StaleFence正反例。完整合并验收与原始日志见[同步记录](../../../proposals/archive/2026-10-03/Upstream同步-e1e7dd3f1-2026-10-03.md)，本包局部绿色不能覆盖该次其他包的18项实际失败。

001/003/004 通过生产请求规划器观察能力集。003 实际比较请求投影不超出 Office，并验证 Strength 的只读收窄；004 只证明旧 tier 输入被忽略，不代替真实模型路由切换。005 验证副本工具策略，未实际执行每一种拒绝动作。

002/006/007/009/010/011/023 中保留的配置、工具注册与静态门禁用例有各自范围。部分 Host 权限结果由测试自己的 wildcard evaluator 解释，不能替代真实 Host 授权；注册/rolePredicate 也不等于完整运行期准入。006/010 中的 integration 用例需按设施规定另行启用，默认跳过不算通过。既有跨包和重复 fixture 尚未全面迁移。

012 增加合法同角色调用的正例，避免“全部拒绝”也能通过原来的跨角色反例。013 只证明伪造外形不能替代真正 permit；完整六类因果值与 vocabulary 分类另有 TODO。014 原来只重复跨 gate 拒绝，现在把真正缺失的 manifest 精确锚点证明列为 TODO；跨 gate 的有效用例仍在 018。

015/016 保留生产 admission 的 exact subject、版本冲突、终态后的旧准入拒绝及追加次数证明。018 覆盖现有 permit 的 typed failure 和拒绝零效果。017 保留消费→释放→再次消费的带断言 TODO。全文核对发现 10-D1 不只是实现遗漏：capability-enforcement-017 要求关闭 one-shot identity，而 crash-reconciliation-006 与 dispatch-protocol-007 明确允许确定未接受后归还同一个 exact permit；018 与 crash-006 的正式测试也要求重消费成功。当前 TryRelease 没有 rejection witness 或独立 retry-grant identity，不能只改 gate 将合法重试禁掉。新增三项已启用护栏覆盖同 session 新 attempt、重复 idle、旧 release callback、物理消息重放、tool completion 与 DropSession 后新准入；只通过公开 QuiescenceSurface 比较完整 typed result，不清墓碑、不读取私有表示，尚未执行。它们不关闭 10-D1。

019 保留两个 gate 的当前 owner 隔离证明，另新增原生 JSON、真实 Fact/Journal/Event codec、execution lease 及两个受监督独立 Node 进程的正式回归；源码已落盘但未运行。QuiescencePermitToken 和既有 execution/queue token 发行点已补原生 JSON 拒绝，其他 PhysicalHandle 仍有具名 TODO。020 是现有 fatal 依赖边界检查，不能代替真实配置失败后的物理退出。024 包含实际插件对 fork DevOps 的拒绝和零子会话创建，固定 DevOps 忙碌准入的全链还需其生命周期所有者的证据。

021 的生成轨迹保留 seed 20260914、60 次运行和全部副作用断言，每条完整轨迹作为子测试交付判决；测试之间归还事件循环，让 Node 发送已经完成的结果。子测试失败仍传回 fast-check 以保留反例收缩。这里没有增加静默预算，也不把轨迹内的循环或日志当成进展。

前轮权限测试工作没有修改产品权限实现或用户的 OpenCode 配置。此次 019 只改变现有 opaque token 的 JSON 序列化拒绝，不改变权限矩阵、gate 消费/归还或容量准入/结算规则，也不修改用户配置。正式节点先构建，再通过 verification-system/tests/run.mjs 选取本包和 requirement-system。结果、跳过、进程不退出及待决分别记录；完整门禁仍未验收，见 GAP-074/075，不以旧发布清单的 DONE 或局部投影绿色宣称完成。

新上游 025/026 保留 Manager 只读窗口及 Review 前 DevOps 拒绝。旧 008 中新增的有效生成器、真实 API、沙箱、读路径和观察用例迁到 025 导入的 `support/manager-programming.mjs`；直接执行非法修改且检查磁盘，与正常读取作对照，不再以“方法不存在就不调用”或空程序证明拒绝。025 的当前事实分类、同进程插件重开、before/execute 再核验均按实际边界命名；没有物理读取计数时不声称零读，未跨进程或 compaction 也不声称恢复全程。

废止 022 的 Fission 配置/调用证明由 intra-participant-parallelism-017 接管，包括伪称 Engineer 的拒绝和零子会话、零请求。026 的原有子会话/请求数只证明已观察到的副作用。新增 D07 将零 durable append 待办转为真实无副作用拒绝合同：通过公开 Fork ToolSurface，在同一道路未接纳 Review 时对 devops 及大小写/空格变体派发同一任务，逐次检查全部隔离存储目录、文件名与完整字节不变；若越权发送 prompt，脚本等待立即报错。接纳 Review 后同一任务必须成功交接、建立 Active 生命周期、创建一个子会话、发送一个 prompt，并改变真实持久字节。incumbency、Review 与正常派工三个阳性转折排除空快照、惰性存储和全拒绝假绿。不解码私有表示，不调用私有 helper，不 mock 掉账簿。此用例属于真实本地持久化适配边界，不认证外部 Host/provider；已写下断言但尚未执行。

仍保留五项显式待办：013 缺六类因果值不可互换及 vocabulary 正向分类的完整入口证据；014 缺 exact file/symbol/source/proof anchor 的实际 manifest 准入证明；017 存在上述同一 exact permit 归还合同冲突，需 owner 裁决并授权相关签名/消费者调整；019 的 permit/执行 lease 真实序列化与两进程 fresh admission 已写成待运行断言，但 gate、routing runtime/port 与 cancellation token 等 PhysicalHandle 的原生序列化及恢复消费仍未覆盖；025 缺运行拒绝零物理读取/修改及已准入读取跨 Review 接纳的真实重叠观察。它们不因 D07 的本地拒绝证明而关闭。

## 017 的 owner 决定与最小签名依赖

现有身份是 opaque gate owner + SessionId + attempt serial，不是永久 SessionId 禁令。BeginProviderAttempt 发行新 serial，DropSession 保留 serial 墓碑；同 session 新工作能建立新 permit。生产链为 PluginSessionScope.Quiescence → PluginTransforms 的 BeginPhysicalProviderAttempt、HostSignalBootstrap 的物理消息/idle/abort → OrdinaryTurnWorkflow、Fission Host 的消费；PluginHostInterop 记录 tool body 开始/结束，session cleanup/drop 关闭资源。ReconcileSurface 另有公开 fixture 构造，不能据它宣称真实 Host 观察已证明。

OrdinaryTurnWorkflow.applyJoinGuardNudge 与 FissionHost.observeOpenLaneCompletion 把同一 permit 的 consume/release 闭包传给 HostJoinGuard.nudge；HostSessionNudge.trySendIdleGateContinuation 和 sendIdleInteractionRepairWithProfile 也捕获同一 permit。gateIdleOutcome 仅在 dispatcher 的 NotSent 上调用 releaseAdmission；Send.continuationAttemptOutcome 仅把 Retryable/Fatal 的明确拒绝归为 NotSent，AcceptanceUnknown 不走归还。曾只消费不释放的 BloggerCoordinator 随本次 cutover 一并退役。AcceptanceUnknown 不归还，因此把 TryRelease 改为永远关闭会直接改变既有“明确未发送可重试”的生产合同，不能作为范围内机械修复。

待 owner 明确决定：Quiescence 是可归还 reservation，还是每次物理 claim 各自持有 one-shot grant。若选择后者，建议保持 TryRelease : QuiescencePermit -> Result<unit, QuiescencePermitFailure> 只关闭 exact grant，另设 TryAdmitRetry : QuiescencePermit * ProvenNotAcceptedDispatch -> Result<QuiescencePermit, QuiescencePermitFailure>。此处 Proposed 型名和签名只是方案，不是现有 export。Dispatch owner 必须发行绑定 exact PromptKey/claim 的确定未接受 witness；gate 以 attempt serial + 独立 grant identity 原子发行新许可，旧 grant 永不复活，重复 witness/旧 callback 零效果。不能把新 grant 伪装为新 provider attempt，不能清墓碑或按 session 永久封锁。

该方案至少涉及 Foundation/Quiescence 的合同、OpenCode/Host/SessionContract.fs/.fsi、SessionQuiescenceGate.fs/.fsi、QuiescenceSurface.fs/.fsi、Interaction/Dispatch/Dispatcher 与 Send 的 typed NotSent 合同，以及 SessionNudge、JoinGuard 的释放接线；若闭包签名改变，还涉及 OrdinaryTurnWorkflow 和 Execution/Fission/OpenCode/Host。018、crash-reconciliation-006 的现有归还正例也必须由其 owner 对齐，不能在本写区偷偷改变。这里仅交方案，不修改这些文件，不替 owner 选择冲突条款。

## 四项待办的最窄可落地证明边界

013：Repository/Programming/Js/Capability.fs 已有正向 DSL-class: Vocabulary 标注，但这只是声明。核读的 scripts/checks/js-surface-manifest.mjs::validateSurfaceManifest 与 scripts/lib/test-surface-scan.mjs 只登记 module/source/laws/representation/kind，没有六类因果值的完整受理检查。最小工作是让声明所属 owner 提供类别及 multiplicity 的正向合同，由实际 validator 接纳完整合法声明，拒绝缺失/错型声明，并把 JsCapability 作为阳性 vocabulary 对照。六类不可互换还须在各真实消费端以 owner-issued Witness/Capability/Receipt 等作错型输入，核对 typed refusal 与零效果，不能把分类标签检查当完整行为证明。需要分类合同/validator 与这些 owner 的窄测试入口，不需要 external Host 或重启；不新增万能 authority dispatcher，也不靠名称白名单。

014：现有 validateSurfaceManifest 只验证源码文件、编译成员、模块、law 与 test import 的存在；没有敏感声明的 exact symbol/source/proof anchor 准入。最小方向是在该实际运行的 manifest 校验链中补由声明 owner 提供的精确锚点合同与纯验证函数，绑定真实 file + qualified symbol + source/proof anchor，并由结构化定位材料核验对应关系。测试调用 validator：完整合法行通过，逐个缺失/替换锚点、同名不同文件、过期 proof 均失败，再由既有命令入口证明失败退出。不能从源码 grep 命中数假装已证明单点发行；不能引入第二份角色权限矩阵。改动边界是 manifest schema/data、validator 及其调用门禁/反例测试，无 external Host 或重启需要。

019：原生 JSON、codec 消费和 OS 进程边界必须分别有据。本次通过既有 QuiescenceSurface 与 ModelRoutingSurface 获得真实 permit/lease，并直接调用 JSON.stringify，包含对象/数组嵌套；正常 durable data 为阳性。QuiescencePermitToken 新增 toJSON 拒绝，ModelRoutingSurface 的既有 opaqueLeaseToken 在冻结前定义非枚举 toJSON 拒绝，WeakMap 引用身份及发行/消费规则保持不变，没有新 export 或签名。FactCodecSurface 的真实 HandleCompleted 字节、JournalCodecSurface 的真实 JournalEnvelope 与 EventCodecSurface canonical 字节在两进程间传递；活 token 在 Fact/Journal 入口及 Event JSON 编码处拒绝，合法 Evidence/Receipt 必须往返。恢复材料、包括重造的完整 execution identity，只得到 WrongOwner/StaleFence，不是 authority；随后 ordinary acquire 或 BeginProviderAttempt → ObserveIdle 的 owner-issued 许可必须成功。两个进程 fixture 经仓库既有监督器启动，前程退出及进程组验证完成后才开后程；不需要 external Host/provider，也不证明真实 Host callback 时序或整个插件崩溃恢复。其余 PhysicalHandle 仍具名留在 019 TODO。

025：ToolRegistry.gateExecute 在调用 original ToolSpec.Execute 前读取当前 Manager facts；JsToolSpec → JsToolWorkflow.runCore → JsToolsBindings.createApi 才进入文件 API。runObserved/FileAccessObservation 只在 sandbox 返回后的成功快照路径处观察，含去重且不记录失败访问，不能证明零物理 read，更不能把悬住该回调当读取在途。最小生产 affordance 是在这条真实执行链注入只负责文件 I/O 的窄端口，默认接既有物理实现；读取/枚举/修改入口可记录真实调用，并以显式 Promise 屏障暂停已获准的 read，而不改变权限决策。它需连同 ToolsBindings、ToolWorkflow、ToolHost 和 registry/composition 的参数签名一起接入，不能只在测试伪造另一个 API 绕过 current-facts gate。拒绝场景走 before 后接纳 Review 再 execute，断言既有拒绝结果及零 I/O/持久修改；阳性场景走相同 registered tool 完成非空真实读取。重叠场景先等 read-entered，再接纳 Review、拒绝新调用，然后放行旧 read 并核对原字节/after 恢复。属于本地 runtime/单文件适配边界，不需 provider 或 OS restart；若进一步承诺真实 Host 会按该顺序调用 before/execute/after，则另需公开 Host canary，不由 Surfacefixture 代替。

此前 017 只补三项护栏并保留 one-shot 反例；此次未改 017、WHAT 或 013/014/025 的实现与测试。各轮源码都不能代替执行证据。

## 019 上游实施时的交付与运行边界（历史截面）

本轮生产改动只在 OpenCode/Host/SessionQuiescenceGate.fs 的 file-private QuiescencePermitToken，以及 OpenCode/Host/ModelRoutingSurface.fs 的既有 opaqueLeaseToken 发行点。两处阻止原生 JSON 编码，不更换 owner 引用、WeakMap 关联、attempt serial、retry 归还或 capacity custody；.fsi 全部不变。旧的 execution token 是冻结空对象，JSON 可编码成 {}；新断言要求明确的 process-local 拒绝，因此旧策略不可能靠恢复对象没有字段来假绿。Quiescence 的断言也要求由该 owner 报出明确拒绝，不能拿 incidental BigInt 编码错误当合同证明。异常文案只用于这道原生序列化拒绝的诊断断言，不用于生产准入、恢复或重试分支。

019.test.mjs 新增四项启用断言：permit 原生序列化拒绝且合法消费/归还/新 attempt 不受伤；真实三层 codec 往返与拒绝；真实 execution lease 的原生/codec 拒绝及 restored material 的 StaleFence；两个独立进程的字节传递与 fresh admission。此前两个有效用例保留，共六项 active tests，另保留一项具名 PhysicalHandle TODO。support/quiescence-durability.mjs 只组织公开 Surface 的输入与完整结果断言，不读取 opaque 内部；support/process-capability.fixture.mjs 每个进程注册一项真实 node:test，phase 非法即失败，未加载/未注册不能通过。

前程建立真实 permit 和 capacity lease，拒绝其直接/嵌套 JSON 与 codec 编码，验证正常准入，输出由真实 codec 产生的 durable bytes。后程从文件读取相同字节，经真实 parser 恢复 Evidence/Receipt；这些值对 quiescence 的 consume/release 均是 WrongOwner，对 execution target/commit/release 是 null/StaleFence。历史 idle 与重复 idle 在没有新 attempt 时为 NoFreshIdle；本进程 BeginProviderAttempt → ObserveIdle 后才有 accepted，重复消费为 AlreadyConsumed，旧 release callback 为 Superseded 且不重开新许可。capacity 另经新 owner 的正常 acquire 建立当前 lease，并以 exact pre-provider release 的 Applied 作阳性与资源收口。拒绝不是靠偶然调用计数；合法字节、fresh permission、exact return 和 release 均为阳性对照。

父测试复用 verification-system 的 superviseNodeTest 与原有 UNIT_VERDICT_SILENCE_MS，没有扩大时限，没有自造子进程监督或通过 stderr 文案算成功。每程须有真实 verdict/完整文件 completion、authoritative summary、drained、零退出码及干净进程组；测试错误、runner:error、信号退出、不退出、TODO、缺 summary 或残留组都失败。只有前程监督返回后才启动后程，只有监督结束后才移除临时目录；group 检查/回收失败不得计为干净通过。没有启动真实 Host/provider，不能把这层结果扩大为 Host canary。

**已知兼容冲突**：requirements/execution-model-routing/tests/012.test.mjs 的 “admission lease is opaque and projects only its frozen target” 仍明确断言 JSON.stringify(lease) === '{}'。它与 capability-enforcement-019 的原生序列化拒绝不相容，新 guard 会使它报错；没有运行也已能静态定位这条旧预期。该测试不在本轮写区，未改。需授权其 owner 将这一句改为支持的 native JSON 拒绝断言，同时保留 frozen target、真实 lease 正向准入和 {} 的 StaleFence 反例；不要删除用例、放宽权限或改 WHAT。此冲突处理前不能宣称相关门禁全绿。业务上仍依赖把活 lease 放入 JSON 的调用也将失败，这是恢复 019 底线的预期兼容收紧，不提供序列化替身。

剩余 019 具名问题是 SessionQuiescenceGate 自身、ModelRoutingSurface 的 RuntimeHandle/PortHandle，以及 Foundation/ParallelSurface 的 TokenHandle 的 PhysicalHandle 合同与原生序列化/恢复消费证明；本轮不把 permit 和 execution token 的局部证明扩成它们的证书。ModelCapacity/Model.fs 的 internal ExecutionAdmissionLease/ExecutionAdmissionQueueNode 与公开 WeakMap token 也不能混为同一表示。本轮只改真实公开 token 的发行点，未改这些 internal 类、gate/port 的对外合同或 cancellation 生命周期；如继续，需要在各真实 owner 处决定拒绝策略并沿实际消费 API 写正反证明，不新增测试 export。

建议运行顺序（由 Manager 安排 DevOps，本轮未执行）：先 node scripts/build.mjs；再以 TESTS_MJS_FILES=requirements/capability-enforcement/tests/019.test.mjs 调用 node requirements/verification-system/tests/run.mjs，保留正常 freshness 检查。随后复核 capability-enforcement 的 017/018 和 crash-reconciliation-006/dispatch-protocol-007 的兼容正例，以及 execution-model-routing-012 的原生 JSON 旧预期。019 的 PhysicalHandle TODO 会使完整验收监督器 fail closed，这不是通过；必要时只分开观察 active assertions 的执行结果，不能 skip/delete TODO 或把局部运行当全包认证。最后仍需 requirement-system/js-semantic-surface 的既有 import/manifest 检查确认新增 support 的传递引用。

以上只经过文件静态回读。Fable 发射 toJSON、native 拒绝、codec 字节往返、两个真实子进程和 cleanup、以及兼容 suite 都尚无本轮运行证据。

2026-10-18（007 增补）：Host 自己配置的 MCP 工具此前被 `"*": "deny"` 兜底连带隐藏，Long Stroke DELEGATE 4.1 因此看不到 MCP fixture 工具。裁决依据 006/007 两条：工具权威只由领域能力令牌授予、技术来源不得另立权限映射表，因此 MCP 命名空间既不进 `Roles.permissions`、也不由插件另行裁决，而是「provider wire 可见、运行期不符 capability 即拒绝」。007 新增四条用例：(1) 声明 MCP server 的 Host config 下，office agent 的 permission 出现 `<server>_*` 通配键且 `"*"` 仍为 deny；(2) 与无 `mcp` 的同一 config 逐键对照，除该通配键外 role 投影逐字节相同（Blogger 仍恰为 `{chronicle}`、Bookkeeper 仍全 deny）；(3) 未声明或 `enabled: false` 的连接不产生任何键；(4) integration tier 下真实插件 `tool.execute.before` 对持 canonical role 的会话拒绝该命名空间（零副作用），插件自有工具名与无 office 权威的 Host 会话不受影响。Host 原生 `todowrite` 等名字不在该门范围内（action-affordance-015 的透传语义不变）；本组仍属 projected permission + 插件 hook 证据，真实 Host 授权由 Long Stroke e2e 观察。
同日复核（独立性发现，已修）：gate 的命名空间集合若随每次 `config` 应用整体替换，会与「早先应用写下、仍然在线的 session permission map」分离——重新加载把 server 关掉、或 HOST-012 同进程多 worktree 实例各自投影，都会让旧 map 仍 advertise `<server>_*` 而 gate 已不再匹配，MCP 工具即可执行。现投影只写**本次应用自己 resolve 出的**模式（另一 worktree/config 的 namespace 不会串进本配置，也不会丢），而 gate 读的声明集合单调只增（`declare`），因此 gate 的真值始终覆盖本进程写过的任何 map（capability-enforcement-002 的 fail-closed 方向；无人 advertise 的 namespace 也一律拒绝）。007 新增 integration 用例：连续三次 `config`（A 声明 → 只声明 B → 无 `mcp`）后，第一次应用投影的会话调用 A 命名空间工具仍被拒绝。
