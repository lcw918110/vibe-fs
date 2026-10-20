# durable-events — WHAT

2026-10-15 本机状态切换：应用运行数据从 Git 元数据中完全退出。本文关于 Git hook、对象库及远端快照的旧合同由 [010]、[011]、[018]、[028] 的当前版本替代；历史验收数字不证明新目录或退役行为。

## [001] retained writer 内 Event 是唯一 durable truth 与 append-only

所有动态业务状态必须且仅能由不可变的事件（Event）表达。业务状态变更改为追加新事件，状态撤销改为追加 tombstone/retirement 事件。在 durable-convergence-011 的 writer retention 窗口内，已提交事件不得被单独修改、覆盖、删除或重新解释；超过固定 TTL 的进程 writer 允许作为完整物理流整体退出 retained history。投影（Projection）仅为衍生状态，严禁先修改内存投影后补盘。

## [002] EventEnvelope 无版本与 additive vocabulary

事件信封采用无版本的统一结构（包含 `event_id`、`stream_id`、`event_type`、`parents`、`payload`、`payload_refs` 与内嵌 `payloads`）。信封与存储层严禁携带任何形式的 format/schema version 字段。已提交的 `event_type` 载荷结构一经发布即永久冻结；引入新业务语义必须声明全新的 `event_type`（追加词汇表原则）。

## [003] canonical JSON 是 identity 协议

事件必须按规范化 JSON 序列化：UTF-8 编码、无 BOM、以单个换行符（LF）结尾；JSON 对象键必须按 Unicode 代码点升序递归排序；`parents` 与 `payload_refs` 数组必须先去重再按字符序排序，内嵌 `payloads` 对象的键同样按字符序排序。相同 `event_id` 产生不同字节流属于致命的标识碰撞（Identity Collision），必须 fail-closed；相同 `event_id` 且相同字节流则幂等去重。

## [004] local append 的提交原语为完整 NDJSON 行

本地事件持久化的唯一见证是在当前进程 writer 的当前段文件（见 [005]）末尾写入完整的 canonical 行（`JSON + LF`）。成功返回前必须完成物理落盘并同步释放存储门禁锁。运行时追加事件严禁创建 Git blob、Git tree、Git ref 或执行 Git CAS。任何半行、截断行或原地覆写均属非法；滚动切换目标段不得改写已封存段的任何字节。

## [005] Process 对应 single writer 与有界段序列

每个进程实例分配全局唯一的 `WriterId`，且仅独占追加 `.Herdchestra/wanxiangshu/events/<WriterId>-<seq>.ndjson` 段文件序列（`seq` 为四位零填充十进制，自 `0000` 起连续）。一个 writer 的逻辑流是同 `WriterId` 全部段按 `seq` 递增拼接的字节序列；事件不可篡改、按序重放、只在逻辑流末尾追加的语义对整条逻辑流成立，不依赖单文件的物理形态。单段体积上限为 64 MiB（67,108,864 字节）：追加前若当前段非空且「当前段已有字节数 + 本次编码字节数」超过上限，必须滚动到下一 `seq`；单次追加自身超过上限时允许独占一个段，其后的追加必须滚动。滚动不是独立物理事务：向新段写入的失败沿用 [006] 的既有阶段语义，已写入段字节保持不变；禁止为绕开失败而向超限旧段继续追加。进程退出后其段序列整体封存且不得被新进程接管；超过统一 writer-retention TTL 后允许整条段序列删除。新启动进程必须创建新的 `WriterId`。业务流标识、机器标识与角色均不得作为物理写者划分依据；新分配的 `WriterId` 不得以 `-` 加四位数字结尾。

无段后缀的旧形态文件（`<WriterId>.ndjson`）按其完整内容读取为单条逻辑流。物化边界允许对超上限的旧形态文件执行一次性重组：按行边界把事件行原样分配到新段序列，逻辑流与原文件逐字节等价；全部新段逐一落盘成功后方可删除旧文件。重组只在存储锁内的物化阶段执行，不产生新事件、不改写任何事件字节。旧文件与其段序列并存时，恢复必须先通过收敛判定，判定通过才继续收敛：判定为「字节前缀」（两种形态互为字节前缀或相等）时，较长一方为权威，较短物理形态在其替换或残骸字节处置完落盘后移除；判定为「行级延续」（旧文件由完整 canonical 行组成，且其全部事件 ID 与段序列的事件 ID 无重叠——这是版本过渡期旧进程在重组落地后继续向旧名追加合法事实的形态）时，收敛动作是把旧文件的行原样（字节不变）追加进段序列尾段：尾段剩余空间足够则续写尾段，否则按上限滚动新段；追加完成且落盘后方可删除旧文件。读取层遇到行级延续形态时按「段拼接在前、旧行在后」组装逻辑流，不改动磁盘，物理收敛由物化执行。重叠的事件 ID、半行、非 canonical 行不属于任何收敛判定，按 [007] fail-closed，禁止静默删除或跳过。

## [006] commit outcome 只由本地事实存在性判定

提交成功的判定标准仅在于该事件的 `event_id` 是否已携带完全一致的 canonical 字节存在于本地事实流中。严禁通过 Git ref、内存状态或退出码反推提交状态。物理追加真正发生后的异常标记为 `CommitUnknown`；而在进入追加门禁前因写者关闭或损坏导致的拒绝必须标记为明确的未尝试状态。

本次调用在真正进入物理追加之前的异常为 `AppendNotAttempted`；进入追加之后的写入、durability barrier、Current commit 或锁释放异常为 `CommitUnknown`。重复请求或空请求没有产生新追加，其锁释放失败独立为 `NoNewWriteReleaseFailed`，不撤销已有事实，也不证明发生了新追加。错误必须保留原请求、实际 preparation（含 cut-tail）、阶段、原异常对象及有序 cleanup 异常；cleanup 不得覆盖主故障。Journal 懒写 `RuntimeStarted` 的结果属于初始化事实，初始化失败时当前业务事实仍为未尝试，后续 poison 拒绝保留首次失败的事件身份和原因。

## [007] StorageInvalid 全局 fail-closed

遇到格式损坏、非规范化 JSON、标识碰撞、retained writer 集合内部缺失父事件、成环依赖、载荷缺失/哈希失配或未知的权威 `event_type` 时，必须彻底拒绝以此快照构建投影或启动运行时，直接进入 fail-closed 状态。durable-convergence-011 已整体淘汰 writer 中的 parent 属于 retention boundary，不构成缺失父事件。绝对禁止跳过 retained writer 内的损坏事件继续折叠。

## [008] 并发 fork 不升级为全局 corruption

因并发产生的合法分支（DomainConflict）属于正常的物理并体现象，底层必须保留全部竞争事实，严禁将其升级判定为底层的 `StorageInvalid` 损坏。

## [009] 无 schema/store generation 与旧物理布局 shock cutover

存储层不维护任何数据迁移代次或平滑升级机制。对于历史遗留的旧物理格式、私有文件与早期临时存储结构一律采取完全不读取（leave-unread）的策略，禁止运行时双读、双写或自动数据迁移。

## [010] 本机状态统一位于 .Herdchestra

主工作树的 `.Herdchestra/` 是唯一项目状态根；linked worktree 共用它，非 Git 工作区使用给定工作区下同名目录。通用事件写入 `.Herdchestra/wanxiangshu/events/`，Work 与 Ownership 由各自 owner 管理 `.Herdchestra/wanxiangshu/` 下的 SQLite，诊断写入 `.Herdchestra/diagnostics/`。不同职责可以有不同物理格式，同一事实不能有两份权威。原 `.git/wanxiang/` 与 `.git/wanxiangshu/` 完全不读、不写、不迁移；也不回退到旧目录或 XDG 状态作为恢复来源。

## [011] 不提供运行数据的 Git 对象与远端同步能力

当前产品只承诺同机持久化。运行数据不得写入 Git objects、refs 或远端分支，不能把 `fetch`、`push`、源码提交当成状态保存或恢复入口。`ProcessGitRawStore`、`WriterStreamSync` 与独立 hook 同步入口退役；本机分段、完整行追加和恢复验证继续保留。

## [012] PayloadRef 与本地 payload closure

体积庞大的正文内容按内容哈希生成不透明的 `PayloadRef`，其内容内嵌于同一事件行的内嵌 `payloads` 字段，事件行自包含（ndjson 是唯一载体，不产生旁挂文件）。事件追加成功前，其引用的所有 payload 必须已内嵌于事件行且哈希完全匹配；引用缺失或哈希失配构成 `StorageInvalid`。内嵌后单行体积随载荷增大，无固定上限；实际体积边界由存储性能与 GC 条款联合验证。

## [013] 查询只读正规 Integrator 的 Current 且先 commit 后 integrate

任何业务查询不得手动全量扫描、过滤或折叠事件历史，必须且只能读取唯一的规范 Integrator 维护的 `Current` 积分状态。修改状态可以先计算待提交状态，但只有本地事实完整追加成功后才能原子推进 `Current`；追加失败或提交闭包未执行时，事件、结构 head 与所有业务 Current 必须保持提交前状态。

## [014] k-way 输入顺序与确定性积分

每个 writer 的逻辑流（同 `WriterId` 段序列按 `seq` 拼接，见 [005]）内部按自然追加顺序排列；同 `WriterId` 的段必须先聚合为单条逻辑流才可作为 k-way merge 的输入，禁止把段当作独立 writer 流参与合并。先按 durable-convergence-011 的统一截止时刻过滤整条过期 writer（段序列整体），再通过确定的 k-way merge 算法产生全局一致的规范序列。Integrator 按此顺序确定性消费事件，相同截止时刻与相同 writer 集合在任何环境中必须计算出完全一致的 `Current`。

## [015] business fold 不变量与失败拒绝

业务折叠规则对关键事件不变量进行严格校验（如不可重复捕获、游标单调递增、摘要与哈希匹配等）。若任一不变量不满足，当前事实必须被语义拒绝，并触发持久化重置机制，防止脏状态污染投影。

## [016] Git 物理概念不外泄至领域层

`GitObjectId`、`RootOid`、`StoreSnapshot` 等底层物理概念仅限内部基础设施使用。业务领域层严禁直接引用或感知这些底层类型，领域层只与 `EventEnvelope` 和 `PayloadRef` 交互。

## [017] local append 复杂度不得依赖 history 与 Git

向本地追加事件时，运行时的 Git 对象、树和引用操作数必须恒为 0。追加开销仅与当前事件及关联 payload 的字节大小相关，绝对不随历史事件总数或历史文件大小增长。

## [018] 启动和诊断不得修改 Git 配置或安装 hooks

插件加载、首次激活、事件追加和诊断都不能改 `.git/config`、`.git/info/exclude` 或 `.git/hooks`，不能安装 SSH wrapper、refspec 或后台同步。`.Herdchestra/.gitignore` 由应用在自身目录内创建并排除全部运行数据。源码 Git 操作与 submodule 管理仍由 Git 自己处理元数据，不是应用持久化。

## [019] 唯一 canonical Integrator 与业务注册 integration oracle

系统仅存在一个规范的 Integrator 负责历史事实的解释与积分。各业务模块仅向 Integrator 注册单个信封的纯计算折叠规则，业务模块自身不拥有读取底层历史或重写重放循环的权限。Structural、Journal、Casebook 与 JsTransaction 的注册必须可由 production surface 上的反例观察：删除任一注册后，该领域的合法 live fact 不得仍产生预期 Current；源码 token、注册名称或调用次数不构成语义证明。

## [020] plugin load 仅验证物理可读性且延迟业务激活

插件加载阶段仅验证存储文件的物理可读性，绝对不执行业务重放、崩溃修复或新事实写入。业务积分与水印追加必须延迟至首次实际消费持久化能力时触发；激活重放只枚举当前 retention 窗口内的 writer，过期文件无需读取或解码。

## [021] semantic failure 写入 durable cut-tail 且当前进程 fatal

当注册的业务折叠规则对合法信封返回语义错误时，系统在持久化保留该错误事实的同时紧随写入 `ProjectionCutTail` 重置事件，以隔离故障作用域。产生坏事实的当前进程必须立即 fatal 退出以防内存状态被污染，由重启后的下一代进程从重置后的 `Current` 安全接续。

## [022] EventStore contract/runtime 编译闭包必须单向且有界

`EventStore.Model.Contract` 只拥有 `EventEnvelope`、`EventStreamId`、`PayloadRef` 等稳定领域模型；`EventStore.Port.Contract` 只拥有 append/read 所需 request/result/error 与 `IEventStore` capability；`EventStore.EventVocabulary.Contract` 只拥有 canonical EventStore event type 集合，并仅依赖 `Strength.EventVocabulary.Contract` 等更基础词汇。`EventStore.Core.Runtime`、`EventStore.Git.Runtime`、Canonical Integrator、Journal、Host acquisition 与 test surface 必须依赖这些 contract，反向依赖禁止。业务 consumer 的 transitive compile input 不得包含 Git object/ref、process/file codec、Canonical Integrator 或 Host adapter。

旧 Git/sync physical adapters 已退役，不得重新引入业务 consumer 闭包。Contract locality 的 transitive production `.fs` 不得超过 100；focused EventStore runtime locality 不得超过 185。所有 locality compile 必须由 ProjectReference closure 生成一个零 ProjectReference flat project，并由一次 Fable invocation 编译。

## [023] canonical codec、领域 fold 与物理 store 必须分层

`CanonicalEventCodec`六个公开操作共同定义同一canonical identity协议，必须形成durable-events拥有的bounded pure contract；merge、sync、integrator、surface及core runtime按locality显式引用该完整slice。禁止让merge runtime为取得codec反向依赖`EventStore.Core.Runtime`。Delegation及其它领域constructor只能产生owner-owned fact case、intent、fold state与closed rejection；`AgentFact` outer union、跨projection组合及journal append只由durable composition routing拥有。`ProcessEventLog`、Store factory、file/lock lifecycle继续是effect，不得进入codec或领域fold contract。

## [024] semantic cut fatal 必须先settle再经mandatory capability执行

semantic fold失败必须先durable写入对应bad fact与`ProjectionCutTail`，取得committed或unknown settlement evidence，再构造typed fatal incident。caller只持有构造时必填的fatal capability；不得直接引用`FatalProcess` physical implementation、optional/default fallback、module-global binding或service locator。同一incident只有一个report owner与一个kill owner，重复调用及fatal-before-settlement一律拒绝。

## [025] persistence cut stores禁optional fatal hook，唯一fatal owner在composition

`CasebookStore` 与 `JsToolsTransactionStore` 不得保留 module-global optional `fatalTripHandler`（默认 None、从未注册即为 dead capability）：semantic-cut 分支只把 settlement evidence 交给 composition 侧唯一 owner 决定，不在 store 内部另设 optional fallback。`JsToolsTransactionStore.appendPrepared/appendCommitted` 的 `FatalProcess.trip "js-transaction-semantic-cut"` 是同一 boundary 的 process-fatal 实现：cut 已由 Integrator 先行 durable settle（bad fact + `ProjectionCutTail` 同一次 append 落盘），trip 之前 caller 已持有 committed 或 unknown settlement evidence，fatal 不是对未结算截断的伪称。

## [026] 历史材料只能经离线迁移导入为全新导入事实

本条不授权迁入旧 `.git/wanxiang*` 状态。该目录在本轮切换中直接退役；不能把离线导入工具当成恢复旧 Git 数据的后门。

协议升级需要接纳旧事实时，唯一合法路径是离线一次性迁移：经 EventStore 所有者的读取、payload 与 append 边界，在备份或副本上追加**仅承载历史材料的导入事实**。导入事实是全新的 `event_type`（追加词汇表原则，不与旧类型同名复用），载荷只重建旧事实的因果位置、digest、payload 引用与覆盖范围等历史证据。导入事实不得充当任何新时代协议的准入凭证：不得由它推导出可运行的授权、绑定或候选，不得产生副本，也不参与新委托的生命周期判定。

存储层不因导入而出现迁移代次：[009] 的 leave-unread 与拒绝语义不变，运行时不双读、双写、不自动迁移、不维护旧载荷解析；未迁移的旧存储由入口检查拒绝以新运行时消费并给出明确迁移指引，禁止边读边跳过。迁移不得原地改写 append-only 历史、Git raw object 或 refs，不得清库，不得覆盖用户配置；envelope 父边、payload refs、decision 引用与 XTrace 因果锚点必须保留或正确重建，并以 cold replay 对照证明业务 Current 一致。迁移工具、输入/输出版本与一次性操作步骤由 owning package 提供自动化测试。

## [027] 退役生命周期词汇保持 fail-closed 拒绝

迁移前内核经由 ndjson 事件流持久化的 9 类生命周期事件类型（`WorkOpened`、`DevOpsBound`、`IncumbencyOpened`、`ConfirmationCommitted`、`IncumbencyRetired`、`Reviewed`、`Reopened`、`AdvanceCommitted`、`RetirementBlocked`）已退出权威词汇表。保留历史中残留任一此类 `event_type` 的 canonical 行在 boot/reload 校验时必须整体拒绝装载：以 boot 阶段的可观察形态 `local EventStore boot failed: unknown durable event type during replay: <event_type>`（核心形态为 `unknown durable event type during replay: <event_type>`）fail-closed，不得跳过、静默装载、改写或删除该行。移除该行后同一目录的其余合法历史必须可正常装载，以证明拒绝来自该行本身；上述类型不得被重新注册回权威词汇表，也不得放宽 `isKnown` 使其重新可读。

## [028] 状态根独立于 Git 元数据与旧数据

目录解析只查询 Git 的公开 worktree 拓扑，不打开旧运行数据。主工作树、其子目录及 linked worktree 必须定位同一 `.Herdchestra`；独立仓库、submodule 及非 Git 工作区保持隔离。给定 `.git` 内路径、bare 仓库或链接伪装的 `.Herdchestra` 必须拒绝，不能把这些位置当成状态目录。

新根内的已确认日志和 SQLite 正常恢复；旧 `.git` 数据即使存在、损坏或拥有同一工作身份，也不能影响新根的读取。清理只涉及已确认的应用专属目录、配置及引用，不清空源码 objects，不重写代码历史。测试区分拓扑查询、实际写入和旧数据隔离，不能只凭路径字符串通过宣称整个持久化已经验证。
