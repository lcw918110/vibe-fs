# repository-programming — WHAT


> **COMPAT（2026-10-29）现行口径**：Scope 生命周期入口是任意工具上的打平注解 `scope.kind` ∈ {`resetScope`,`keepLast`,`removeLast`,`meToo`} 与 retention（`wanxiangli/scope-retention` + 共同法 «Scope»）。**无独立 `resetScope` 宿主工具**；档案重锚 = 注解 `resetScope` → `ScopeReset.commit`。**无** `remainingScopeTurnsEstimate`／`turnBudget`／KPI 倍率产品契约；Stall／ResetDue 估计路径已从宿主产品面退役。下文若仍写独占工具、估计必填或倒计时后缀，以本条为准。PairHint 屏障优先认 `scope.kind=resetScope`，旧工具名只读兼容。


## [001] Capability 投影面与单一权限源

每次请求的 `todowrite-for-ROLE` 只从当前 Attempt 的唯一权威能力集生成，公开成员与运行时准入使用同一投影，不另算角色权限。有实际可生成的编程成员才提供编程工具；Join 只授权独立 `join()`，不单独产生编程面。旧 Attempt、目标程序原有权限和合法工具名不能代替本次授权。文件、评审、Ask 和委派继续受既有业务门禁约束。

## [002] 四层同构应用

能力在公开基类、工具描述、canonical examples 与运行时门禁中一致投影。Read → `cat/stat/whereAll/where`，Glob → `ls`，Grep → `rg`，Write → `write/mkdir`，Move → `mv`，Remove → `rm`，Exec → `call/signal/send`，Fork → `fork`，Resume → `resume`，Commission → `commission`，DevOps → `devops`，Fission → `fission`，Horizon → `horizon`，Ask → `ask`，Review → `review`。canonical 成员序为 `write, rm, mv, cat, ls, rg, stat, mkdir, whereAll, where, call, signal, send, fork, resume, commission, devops, fission, horizon, ask, review`，仅列实际授权成员。`park/unpark/abandon` 是普遍生命周期成员，不进入能力列表，仍受会话及权限边界约束。Manager 的 reviewOnly、Plan 写前评审与 PLAN_PATH、Ask 业务门禁保持不变。编程内 join/dag 的投影、实现、类型和教学全部退出；委派 resume 不是程序恢复，resetScope 与 Work 生命周期决策不成为程序成员。

## [003] 确定性生成

同一角色与能力集生成字节相同的工具名称、Schema、描述、基类及示例。执行档位不进入名称，也不改变同角色的编程面。

## [004] 生成工具名门禁

只接受当前 Attempt 实际拥有的生成工具名。其他角色或旧 Attempt 的名字须拒绝；名字合法不能代替当前能力授权。

## [005] 编程面与推荐诚实性

公开基类、描述和示例只包含当前可执行成员，不暴露宿主私有接口或注入键。（原直连宿主文件工具的说明钩子 `BuiltinToolDescriptionHook` 已随直连工具整体退役，现行契约归生成的 js 编程面。）

## [006] 独立执行进程与无 Ambient OS 权限

每个命名程序由独立执行进程承载，只获得显式纯数据和获授权原语，不直接获得宿主对象、文件系统、网络、进程或环境变量。纯 JS 计算有硬性执行预算；SDK 排空与命令等待期间暂停扣减，恢复计算后续用余量，不保留跨越等待的 VM wall-clock timeout。请求期限按 [052] 独立计量，等待不使其停表。阻塞桥接只停住所属程序；宿主、既有 SDK 操作、命令输出和其他程序继续响应。普通 Promise、忙标志或必须 await 的文字不能代替真实阻塞。`run()` 的结构化返回值才是程序结果，stdout/stderr 不作为返回值或编辑产物。

## [007] 不可变快照与 Anchor 代数

`cat(path)` 返回当前事务的不可变 UTF-8 字符串快照并登记写前 `FILE_CHANGED` 校验使用的读快照；后续 `write` 修改不改变已取得的快照字符串。非法 UTF-8 拒绝为 `INVALID_UTF8`，不猜测或修复编码。

在生成编程面中，旧 `file(path, matches)` 与 `FileView.text(from, to)` 已退役，定位与截取改由 `where`/`whereAll` 返回的 UTF-16 代码单元下标 `[begin, end]` 与 `text.slice(begin, end)` 组合完成；Plan 复用相同成员签名与 UTF-16 偏移语义。

## [008] 确定性路径枚举

`glob(pattern)` 确定性枚举 gitignore/wildmatch 路径：`*` 不跨目录，`**` 匹配零段或多段目录；遵守各级 `.gitignore` 与 exclude，不进入 `.git`，不跟随符号链接。内部不截断，超限由宿主结果留尾规则处理。

## [009] Grep Capability 投影

`rg(argv)` 只由 Grep capability 授权；Read+Glob 不代替它。参数只有一个 argv，是字符串数组。搜索固定加上 `--json` 后原样传给 `rg`，调用方不再传 `--json`；没有路径时不替它补工作区。成员不追加 `--no-ignore`；要搜索被忽略的路径，由调用方写进 argv。`--files`、`--files-with-matches`、`-l`、`--files-without-match`、`--count`、`-c`、`--count-matches`、`--quiet`、`-q`、`--vimgrep`、`--type-list` 与 `--json` 互斥，所以这些模式不加 `--json`，原样传给 `rg`。返回值都是 JSON 对象，不是文本：内容搜索是 `{ exitCode, messages }`，`messages` 是解析后的 JSON Lines；上述模式是 `{ exitCode, paths }`。`exitCode` 为 `0` 表示有结果，为 `1` 表示成功但没有结果，二者都是数据。argv 不是字符串数组时为 `INVALID_ARGUMENT`；路径解析支持工作区内及区外合法路径；其余 `rg` 失败为 `RG_FAILED`。

## [010] Rewrite、Write 与 Mkdir 的落盘语义

生成编程面的 `write(path, content)` 覆盖已有文件或创建缺失文件（合并原 `edit`/`rewrite` 的覆盖职责），等价于 POSIX 重定向：一次原子写、即写即生效；目标已存在时记为 `Rewrite`，缺失时记为 `Create`。写入前若目标已通过 `cat` 读取且盘上内容已变，报 `FILE_CHANGED`。`mkdir` 只建一个目录，非递归；已存在报 `DIRECTORY_EXISTS`，父目录缺失报 `PARENT_DIRECTORY_MISSING`。底层提交计划单元中 `rewrite` 要求目标已存在（缺失报 `FILE_NOT_FOUND`），同一计划对同一路径只能声明一次文件修改意图（重复报 `DUPLICATE_MUTATION_TARGET`）。

## [011] 可表示的返回值与提交前校验

返回值限于 JSON 兼容子集：null、布尔值、有限数字、字符串、普通数组与对象；数组不得含 null 或异构类型。undefined、BigInt、NaN、Infinity、函数、Symbol、循环引用及其他非法值均在提交前以 `INVALID_RETURN_VALUE` 拒绝，终止事务。

## [012] 事务 As-Is 逐笔落盘与单一 EventStore 记账

一次调用只有一个事务。每笔 `write`（以及 `rm`/`mv`）在调用发生时立即经 MutationFs 原子写盘（单笔文件层原子，跨笔不回滚）；`run()` 成功结束前磁盘已经反映已发生的每笔写入。程序以任何方式终止或挂起（throw、超时、输出超限）时，那一刻之前已落盘的改动保持为事实分界线，之后的未发生。

持久化 Prepare 与 Commit 只进入统一 EventStore，不另建专有文件或存储。记账时机：每笔的 Prepared 在写盘前发起（调用时序、TransactionId 成对，写失败留下无 Committed 配对的 Prepared 作中断证据），Committed 在该笔写盘成功后发起；物理落账由程序终局的结算（settleLedger）await 全部 append 完成，成功路径保证 Prepared/Committed 成对排空。单笔的物理落账发生在该笔写盘之后的同一事件循环回合（微任务），只有进程级死亡才可能丢失末笔审计。

## [013] 写前校验、提交计划与 durable 记账失败映射

写前校验作为实现单元保持精确形状并短路：重复的修改意图最先报 `DUPLICATE_MUTATION_TARGET`，缺失目标按快照校验报 `FILE_CHANGED`，全部通过才继续。提交计划（commitPlan）输出精确的执行序列（rewrite 携带 expectedCurrent，create 携带文本），成功时把计划内全部目标写入；计划内某笔写失败时以 `TRANSACTION_COMMIT_FAILED` 失败，并按计划内还原序把已写目标还原——该还原是实现单元自身的语义，工作流主路径仍按 [012] 的 as-is 逐笔落盘。

durable 记账追加失败时（Prepare 或 Commit 阶段的各失败形态），程序结果为 Failed，给稳定码 `TRANSACTION_PREPARE_*`/`TRANSACTION_COMMIT_*`，不报告成功；按 [012]/[029] 的 as-is 语义，已落盘的文件效果如实上报且不回滚。失败不重试追加、不换新事务身份，并携带原事件 id、事务 id 与原始 cause；Prepared 失败后 pending 投影为空（记账未完成），Committed 失败后原 Prepared 仍留在 pending。

EventStore 端口的坏输入防御先结算物理事实（append、fsync、close、release 的确切计数与落盘字节），再经唯一的语义熔断（`js-transaction-semantic-cut`）以 SIGKILL 终止；对 Release 阶段的坏输入，最终报告保留原请求事件身份。

## [014] 冲突检测与无隐式重试

每次写前各自做冲突校验（校验点从统一 preflight 移到每次写前）：若目标路径在 readSnapshots 中，读取当前盘字节与快照比对，不一致时以 `FILE_CHANGED` 失败并停止；已发生的改动保留在盘上。不自动重读、不重定位锚点、不重跑程序。

## [015] 中断证据与重开不重放

Prepare 已入账而无 Committed 是中断审计证据：pending 投影保留该记录，跨 EventStore 重开仍可见。重开只观察证据，不自动重放、回滚或补交事务，磁盘保持中断时已落盘的状态。

物理写失败以 `TRANSACTION_COMMIT_FAILED` 失败，失败笔之前的 as-is 已落盘改动保留。新时序下：写成功的笔保有完整的 Prepared/Committed 配对；写失败的笔留 Prepared 作中断审计证据——中断证据即无 Committed 配对的 Prepared。程序以返回值非法等方式失败时，此前已写成功的笔同样保有完整配对，不留 pending。每笔写入经 MutationFs 单笔原子落盘，重开不盲目重试模糊的半完成提交。

CAS 还原（rollbackPlan：restore 与 removeCreated，逆序）保留为实现单元，还原时按 expectedCurrent 比对、不覆盖第三方已改内容；TransactionStore 不提供手动历史读取接口。

## [016] Synthetic TOML 结果面

成功以 `# ok` 开头，返回值放在 `[data]`、`data = ...` 或 `[[data]]`；有实际写盘时，文末 `[fs]` 声明 rewritten/created 路径。失败以 `# failed` 开头，根级给稳定 code 与可读 reason，并附本次调用已真实落盘的改动清单（`[fs]` 的 rewritten/created）；无已落盘改动时失败不附 fs 节。

成败由顶级结构区分，不另用 `status = "ok"` 等歧义字段；编码与组合遵守 provider-projection。

## [017] 并行程序与逐笔提交

不同名字的程序可并行执行，命令等待与暂停不得占住整个 provider run 或会话的执行锁；仅保留准入、调用身份及单笔原子提交所需顺序。同一程序一次只执行一条 call，命令并行通过不同命名程序表达。文件变更仍逐笔原子落盘，读取快照参与写前 FILE_CHANGED 校验；不承诺并发改写同一文件都成功，不自动重读、重定位或重跑。另一程序失败不回滚本程序已落盘效果。

## [018] 稳定失败代数

预期失败使用稳定具名错误码，不笼统压成 `PROGRAM_FAILED`。原因只给受控摘要，不泄露沙箱内部代码、宿主路径或敏感环境；具体原语的错误码按对应条款。

## [019] 失败不产生 durable Commit，已落盘改动保留

程序以任何方式失败（throw、返回值非法、目标缺失）时结果为 Failed，不产生 durable Commit；失败前已按 as-is 落盘的改动保留在盘上——非法返回值不撤销已应用的重写。新旧内容完全相同时成功且不做无意义写盘。

## [020] 文件变换的 POSIX 语义

编程面成员 `mv(from, to)` 移动或重命名文件及目录，支持覆盖，源缺失报错；`rm(path)` 删除文件或空目录，拒绝非空目录。参数错误与操作系统失败给稳定可读结果。（原独立宿主插件工具 `mv`/`rm` `ToolSpec` 与 `FileMutationSurface` 已退役，由 `Move`/`Remove` 权限投影的 js 成员承担同一契约。）

## [021] 禁止手写 per-role 工具变体

角色编程工具统一按角色及其已授予能力生成，不手写各角色变体；合法工具名登记中的枚举不承担额外授权。

## [022] 工具选择与失败经验引导

描述先给可执行形态与原语选择，再给显著风险和失败经验：

- 用 `cat(path)` 读取不可变快照，用 `where`/`whereAll` 定位精确 span，用 `text.slice` 重组后交由 `write(path, content)` 一次落盘；查找替换、批量替换与追加均由主原语组合表达，不手算裸字符串边界或退回盲目修补。
- 按当前角色实际投影的 capability 教授可用原语与组合食谱（`recipes`），不教授未投影成员，并保留展示角色职责的 Ultra Example。
- 在同一快照上从可信切片构造完整目标文本后一次写入，不留残片给后续 cleanup 程序清理；动手前先承诺规模、标题与 section 数等廉价不变量，越界时作为停止信号立即失败。

## [023] 精确切片替换与 Write-only 能力边界（原 Edit 成员族退役）

仓库生成编程面已退役 `edit` 与 `rewrite` 成员，`ToolPermission.Edit` 不再投影 `JsCapability.Edit`；局部替换、插入、删除、全匹配替换与整文件重写统一由 `cat` + `where`/`whereAll` + `slice` + `write(path, content)` 组合完成，每次 `write` 对已有文件产生一笔 `Rewrite` 落盘记录，内容未变时可由程序跳过写入。

仅授 `Write` 能力（无 `Read`）的生成面只暴露 `write`/`mkdir`，不泄露 `cat` 或旧 `file`；`write` 内部读取当前文件以构造 `Rewrite` 并参与写前 `FILE_CHANGED` 快照校验，但不产生显式读观察。（注：Plan 任期专用面 `todowrite-for-plan` 仍独立保留无路径 `file`/`edit`/`rewrite` 算法与契约。）

## [024] 定位歧义挂起、近邻诊断与路径边界拒绝（原 Edit 恢复协议演进）

生成编程面的定位未命中与多义匹配不再走 `edit` 内部抛错，而是由 `where(content, needle, from = 0)` 经统一 `park` 通道挂起（零修改落盘）：

- 未命中时 parked 载荷携带 `needle`、空 `candidates` 与最近邻单行诊断 `nearest: { span, text, distance }`；近似只作诊断，绝不自动获得写权限。
- 多义匹配时 parked 载荷携带至多 8 项 `candidates: [{ span, context }]`，每项 `context` 窗口独立于文件大小有界截取。
- 支持工作区内外合法路径解析，以操作系统文件权限为准；缺失目标报 `FILE_NOT_FOUND`；冻结失败目录仍保留 `INVALID_EDIT`、`EDIT_NOT_FOUND`、`EDIT_AMBIGUOUS`、`EDIT_OVERLAP` 供 Plan 任期面与目录契约使用。

## [025] 事务致命失败先结算再熔断

事务不变量失败先完成 durable semantic cut-tail 结算（保留已落盘的 as-is 文件效果与第三方变化），取得 committed/unknown 结算证据后才构造 typed incident，并调用由 composition 注入的 mandatory fatal capability。不得直接取得物理适配器或用 optional/default/global 兜底。

同一 incident 只报告、终止一次；旧快照、第三方变化及普通编辑拒绝仍为 typed nonfatal，致命处理不得覆盖工作树。

## [026] 事务快照与案例实质访问分离

ReadSnapshots 只用于事务隔离与冲突检测，不直接作为案例关联文件。案例只记录显式读取（`cat`）的规范路径及已提交的修改、创建、删除、移动路径；内部扫描（`ls`/`stat`）不算实质访问。提交前 effectPaths 是意图，未提交、校验失败或回滚均不追加修改访问。

## [027] Engineer 与 DevOps 的统一文件面

直接文件系统宿主工具（`read`/`write`/`edit`/`glob`/`grep`/`patch`）及独立插件 `mv`/`rm` `ToolSpec` 已整体退役；`Read`/`Write`/`Edit`/`Glob`/`Grep`/`Move`/`Remove` 权限不再投影独立宿主工具名，而是统一通过生成的 `todowrite-for-engineer` 与 `todowrite-for-devops` 投影 `cat`/`where`/`whereAll`/`write`/`mkdir`/`ls`/`stat`/`mv`/`rm` 成员，共享路径边界、UTF-8 校验、符号链接防护和 As-Is 逐笔落盘写语义（每笔写即原子落盘、跨笔不回滚、失败保留已落盘改动）。

Manager 的 `todowrite-for-manager` 程序入口始终按角色准入；仓库访问仅经 `this.reviewOnly.cat/stat/ls/rg`，由 Host 逐次检查当前 Review 窗口，无顶层别名。`where/whereAll` 为纯文本操作，保持顶层；委托及生命周期成员各守权限。生成描述、示例、成员目录与真实 API 同步命名空间，规则遵循 capability-enforcement-025；不因此取得修改或执行权。

## [028] 程序结果与业务义务

普通 return 只结束当前程序；命令退出、程序终结、工具响应结束和业务交付是四种事实。成功返回、自动暂停、显式 park、恢复和放弃均不能绕过 Work、评审、Ask、Ownership 等准入门禁。错误、计算预算耗尽和现场丢失按真实失败或中断报告；暂停不能伪装成终结。已有读观察、文件效果和资源义务随原对象保留，恢复继续原状态，不重新创建运行。

## [029] 单次终态结算与真实 effects

每个程序只结算自己实际发生的效果，终态通过原程序 settle 一次并保留结果；调用重放、重复控制和终态查询不得重复记账或重发效果。结算等待该程序应结清的 SDK、文件观察和账本操作，真实失败沿既有契约报告。成功、失败和放弃均保留已落盘 effects；暂停观察不替代终态结算。控制程序取得目标回执时，目标结果和 effects 保留在目标回执中，不并入控制程序自身 fs 清单，不以外层成功 return 改写目标状态。

## [030] park 的真实暂停

`this.park(payload)` 把当前程序停在原表达式，恢复前不执行后续模型代码；where 歧义沿同一通道，保留 [046] 字面匹配和 UTF-16 span 语义。请求期限耗尽也真实暂停续体，不把 running 对象作为 call 结果。既有 SDK 操作结清、命令退出只保存事实，不能自行解除暂停。回执按名字报告状态、原因和载荷。

## [031] 未结清现场与业务交付

Work 终局门禁按所属会话的程序名字及真实状态查询；暂停程序须先恢复或放弃，拒绝与提醒列名字，不要求抄写内部 handle。程序 return、parked 回执和命令 exit 不解除业务义务。会话关闭向程序设施发送关闭消息，复用 abandon 收尾；资源未确认结清不得先删登记或报告完成。提醒沿既有 Pair Hint 路径组织，不回写旧工具结果及已呈现历史字节。

## [032] 按名字恢复原现场

唯一公开恢复入口是新命名控制程序内的 `this.unpark(name, value?)`。预算只来自控制请求顶层 intent 在准入时确定的期限。恢复直接定位原对象，继续 SDK 排空、原命令等待或显式 park，不重新编译、重建沙箱、扫描工作区或重发操作。只有显式 park（含 where）接收 value，call 取得原命令真实结果。目标及继续唤醒的目标共用控制请求期限；到期仍活动的控制者和目标均暂停。后续恢复控制者只推进其自身、领取已有回执，不隐式再次唤醒目标。终态返回原回执，未知现场明确报告，权限拒绝不消费现场；目标 name/state/effects 不被外层 return 覆盖。

## [033] 调用重放与迟到消息

登记键为所属会话 + name，一个名字同一时刻只对应一次运行。同一物理工具调用重试重放已有回执，不再启动程序；新调用占用仍在活动（运行中或 parked）的名字报 NAME_TAKEN，已结束（completed、failed、abandoned）的名字可登记为新运行。调用围栏、暂停和命令代际、恢复关系由程序设施私有维护。重复或迟到 unpark 不消费后来暂停，迟到 signal/send 不误击后续命令，观察到旧运行的控制不作用于同名新运行，旧窗口不干扰新请求。重启不恢复丢失 JS 栈；只恢复实际持久化归属及已确认终态，丢失现场报告中断或未知，不自动重跑源码。

## [034] abandon 与确认收尾

`this.abandon(name)` 取消并清理整个程序拥有的执行资源，确认结清后才报告 abandoned；尚未结清、清理失败或强制结束按事实报告。发送信号、删除名字和工具响应结束不是资源终态证据。正常展开时运行原生 finally，其中合法操作遵守既有权限与 as-is 规则；强制结束不假称 finally 已运行。捕获取消不撤销取消，之后 return 不成为成功。排空中、命令中、park 后及会话关闭共用收尾，effects 按 [029] 结算一次。

## [035] 放弃期间禁止再次暂停

abandoning 后不接受新的 park，显式 park 返回 CANNOT_PARK_WHILE_ABANDONING，不能以新暂停延长现场。原先获授权且正常清理需要的操作继续遵守自身合同，不额外禁止 finally 内合法清理命令。最终以资源是否真实结清为准。

## [036] 名称登记与会话边界

名称登记归 wanxiangli/program 的 Session 所有，不建跨会话全局名字表；不同会话可用相同名字，控制不得越过会话与权限。每个名字指向最近一次登记运行的现场、暂停点、输出和终态；该运行完成、失败或放弃后名字重新可用，新调用以新运行接替，旧运行的回执仍按原物理调用重放，旧运行观察不能作用于新运行。同一名字的活动运行不被覆盖。旧进程级 parked-site 表与默认八现场上限退出，不以删登记代替资源收尾。

## [037] parked 载荷、源码与输出事实

parked 回执沿既有 Synthetic TOML 和宿主渲染边界呈现 name、state、原因、完整 source、原 payload 及适用命令状态与新增 delta；排空超时明确命令未启动，不暴露模型操作 handle。可靠定位时以 `>| ` 标记暂停行，其余行 ` | `；不能定位时全部降级 ` | `，不误标、不使暂停失败，完整源码不进入每回合提醒。delta 不消费最终 CallResult；真实退出码、信号和最终输出仍可取得。截断明确且原始输出可取回，信号退出缺失退出码不补成成功。输出文件保留，关闭不夹带自动保留期 GC。

## [038] 生命周期成员的生成面

每个基类都有 park(payload)、unpark(name,value?)、abandon(name)，只读角色同样能管理本会话程序，但不扩权或绕过业务门禁；它们不进入能力列表。Exec 投影 call(command)、signal(name,signal)、send(name,input)。预算仅由工具顶层必填 intent 声明，成员不能动态续期。教学采用新命名控制程序、目标名字、共享期限及显式恢复；委派 resume 不是生命周期别名。

## [039] 状态所有权与两包边界

wanxiangli/program 拥有 Session 名称登记、Run 程序状态、计算预算规则、请求准入期限、恢复关系、私有代际、控制及收尾，以及独立 join 的等待接线。Work、权限、评审、Ownership、Journal 各自拥有业务事实。wanxiangli/runtime 拥有独立进程、隔离、文件与锚点、原子写和版本检查、命令、终端、输入输出、信号、输出外存及物理时钟；不解释 intent、不拥有程序生命周期、不认识角色或 Work 终局规则。program → runtime 是明确的单向公开依赖。旧 programming/sandbox/fs/process/pty/spool/time/wait 的真实消费者全部切换，旧入口、owner、计时路径及兼容别名退出；两包不是转发壳，主仓不继续拥有程序状态。

## [040] rm/mv：Move/Remove 编程面与 as-is 落盘

`rm(path)` 与 `mv(from, to)` 由 Remove/Move capability 投影（`JsCapability.ofToolPermission` 是唯一映射），与 `write` 同属 as-is 逐笔落盘路径。四层同构：生成基类含 `rm`/`mv` 方法、工具描述与 canonical example 含对应成员、运行时门禁按 capability 挂载 rm/mv 执行器。Manager 的 `todowrite-for-manager` 仍只读，不含 Move/Remove。

- `rm` 删除文件或空目录；非空目录报 `DIRECTORY_NOT_EMPTY`，缺失目标报 `FILE_NOT_FOUND`。成功返回 `{ ok: true, removed: path }`。
- `mv` 按 POSIX 语义移动或重命名文件及目录，支持覆盖；源缺失报 `FILE_NOT_FOUND`。成功返回 `{ ok: true, moved: from, destination: to }`。
- 路径放行：rm 的目标、mv 的源与目标均支持工作区内外合法路径，由操作系统文件权限保证安全，源缺失报 `FILE_NOT_FOUND`。继承 [014] 的写前冲突校验：`cat()` 取得的快照仍新鲜时，目标可照常被 rm 删除落盘。
- 每次 rm/mv 各记一笔 durable 条目，Prepared/Committed 成对排空 pending 投影；渲染结果报 `removed = [...]` 与 `moved = ["from -> to"]`。
- 稳定失败码 `DIRECTORY_NOT_EMPTY`、`CROSS_DEVICE_MOVE`、`INVALID_ARGUMENT` 登记在冻结失败目录；EXDEV 依靠 renameSync 原子语义保证源与目标都不残留中间态。

## [041] Lifecycle 审计与会话收束

程序设施产生真实 Parked、Unparked、Abandoned、Reaped 事实，经既有 lifecycle persistence 接统一审计流，不另建业务账本。生产工具及会话关闭均接线，不能以缺省 persistence 冒充持久化。关闭复用收尾，真实资源结清后才报告回收。既有 durable 流名和机器词汇按 [060] 保留，不因名字接口任意改写历史字段。进程内事实与 durable 提交分别报告，审计失败如实记录，不重跑原效果或伪造资源终态。

## [042] rm/mv 落盘后程序失败的清单形态

rm/mv 与 edit/write 同属 as-is 逐笔落盘路径：程序在 rm/mv 已落盘之后以任何方式失败（throw、超时、返回值非法）时，结果为 Failed，已发生的删除/移动保留在盘上并如实上报——`[fs]` 的 removed/moved 清单列出本调用已真实落盘的全部条目；无已落盘改动时失败不附 fs 节。被拒绝的 rm/mv（非空目录、越界、源缺失等）不进入清单：该笔未落盘，此前已落盘的其他条目照常列出。

## [043] rm/mv 双语资源与四层接线契约

rm/mv 的成员文案是 zh-CN.md/en.md 成对双语资源（`tool/js-program/rm`、`tool/js-program/mv`），两语言陈述同一契约要素——签名、POSIX 语义、as-is 即时生效、EXDEV 拒绝且不留两侧并存中间态——且不携带未替换占位符。四层接线保持同构：JsFragmentRegistry 的 rm/mv fragment（含 `js.rm`/`js.mv` RuntimeBindingKey）、ToolsBindings 的成员绑定、Description 的资源路径与 capability 条件规则块、ToolHost 的双语装载。底层文件系统效果与稳定失败映射归 MutationFs 原语（removePath/movePath），不在成员绑定内二次定义。

## [044] rm/mv 失败码的 reason 稳定与成对上报

rm/mv 相关稳定失败码（`DIRECTORY_NOT_EMPTY`、`CROSS_DEVICE_MOVE`、`INVALID_ARGUMENT`、`FILE_NOT_FOUND`、`FILE_CHANGED`、`FILE_READ_FAILED`）在冻结失败目录中携带可读且稳定的 reason 文案。运行期失败结果的 code 与 reason 成对出现，reason 携带涉事路径（如 `directory is not empty: <path>`、`target file does not exist: <path>`）。

## [045] 文件原语：cat/ls/stat/write/mkdir

`cat(path)` 读严格 UTF-8 文件并登记读快照；缺失报 `FILE_NOT_FOUND`，非法编码报 `INVALID_UTF8`。`ls(path)` 只列一层，排序返回，目录条目以 `/` 结尾，并应用 `.gitignore` 语义（[057]）；递归需求按 [047] 的组合写法展开。`stat(path)` 返回 `{ exists, size, mtime, isDir }`，缺失是正常观察而非失败。`mkdir(path)` 非递归，已存在报 `DIRECTORY_EXISTS`，父目录缺失报 `PARENT_DIRECTORY_MISSING`；它不产生 durable mutation 条目。

`write(path, content)` 覆盖或创建，一次原子写、即写即生效（[010]）。五个原语都继承路径边界、符号链接防护、写前 `FILE_CHANGED` 与 as-is 逐笔落盘；底层文件系统效果与稳定失败映射归 MutationFs 原语。

## [046] where/whereAll：字面匹配与歧义即 park

`whereAll(content, needle, from = 0)` 做字面匹配：`IndexOf` 作用在原样的 needle 上。返回全部非重叠匹配的原文 UTF-16 下标 `[[begin, end], ...]`，零个返回 `[]`，`from` 是原文起始下标。空白按原文参与匹配。

`where(content, needle, from = 0)` 的唯一匹配直接返回 `[begin, end]`。零个或多个匹配时，程序经与 `js.park` 相同的信号通道挂起，载荷为 `{ needle, candidates, nearest }`：`candidates` 至多 8 项，每项 `{ span, context }`；零匹配时 `nearest` 为最接近的一行 `{ span, text, distance }` 或 null。unpark 注入的值原样成为 `where` 的返回值；注入非 span 时由后续程序自然失败，宿主不预校验。`where` 永不抛定位错误，也永不静默选择匹配。

## [047] 原语组合与教学资源

编辑由 cat、字面 where/whereAll、slice 和一次 write 组合，枚举与内容搜索用 rg(argv)，保持既有路径、权限、读版本和 as-is 语义，不改变 [007]–[027] 普通文件及错误合同。header、成员说明、示例、配方、生命周期资源 en/zh-CN 成对且语义一致；先声明必填 name/program/intent，再教授授权原语及名字控制。清除自动名字、单 program 顶层、成员预算、单参 signal/send、handle、编程内 join/dag 和 all-or-nothing 过期教学；保留 WARNING 与精确锚点纪律。

## [048] 暂停源码与恢复值分离

显式 park、where 歧义和请求期限暂停保留原现场；再次暂停更新同一程序的私有代际及 source，不生成模型 handle。source 标记对应模型源码实际暂停点，不把包装器或 eval 位置当模型行号；不能可靠定位按 [037] 降级。unpark(name,value?) 的 value 只交给显式 park/where，不是预算或 call 结果。目标回执、CallResult 和恢复值分别保存，不因恢复控制者重发原恢复动作。

## [049] 三项必填的工具入口

所有公开 todowrite-for-* ToolSpec 参数恰好为必填字符串 name、program、intent，JSON Schema required 仅含这三项。缺项、非字符串、空白 name/program、空字符串 intent 或未知顶层字段在程序启动前拒绝。名字由模型填写，不按 provider-run/tool-call 自动补名。没有顶层 unpark/abandon/handle。program 为 class Js extends JsProgram 与 async run()，说明首先展示三项参数与完整模板，角色不改变输入形状。

## [050] call：先排空，再独占本程序

this.call(command) 只接受一条 shell 字符串，先检查参数及权限，再使用入口期限；不接受成员预算。此前已发起 SDK 操作须先结清，不以 PENDING_OPERATIONS 拒绝、不重发、不取消。失败沿原契约，落盘效果保留。排空与命令等待共用剩余时间，排空结束不重新计时；排空超时实际暂停且命令未启动，操作后来结清也不自行启动。同步桥接保证漏 await 或 Promise.all 两个 call 都不能越过首个调用点，不设第二命令隐式队列。期限到不杀命令，park 后退出只保存结果，恢复不重启。最终结果保留真实退出码或信号、完整留存输出、截断、字节数、时长，不被 delta 消费。

## [051] 按名字 signal 与 send

this.signal(name,signal)、this.send(name,input) 是与 call 平级的 Exec 成员，从新命名程序作用于目标当前唯一活动命令。没有活动命令（包括 SDK 排空）明确报告，不猜最近进程、不缓存输入给未来命令；send 为缺行尾换行的输入补一个。两者不解除 park，信号发出不代表退出，输入提交不代表处理成功。退出后仍显式恢复领取结果；重复、迟到、跨会话按授权、身份和私有命令代际隔离。

## [052] 准入期限、执行预算与物理时长

参数及权限准入时一次计算 budgetMs = UTF8Bytes(intent) * 50、deadline = admissionTime + budgetMs。按 UTF-8 字节，不截短合法预算、不加秘密短上限。所有操作、SDK 排空、命令等待及本次唤醒目标共用该绝对期限；连续 call、unpark 和嵌套控制不能重置、叠加或续期。到期仍活动控制者与目标均暂停，恢复控制者不隐式再恢复目标。新工具请求可以取得新期限，原操作不重跑。执行预算只约束模型计算，等待停表，计算续用余量；命令时长只观察。期限、恢复和代际由 program 唯一拥有，runtime 只物理计量与唤醒，旧回调不影响新窗口。

## [055] SDK 并行与 call 屏障

独立 SDK 操作可用 Promise.all/Promise.allSettled，各自按合同结清，兄弟失败不回滚落地效果。进入 call 后先结清全部既有操作，续体停在调用点，排空消耗入口剩余期限。call 不参与同程序命令并行：漏 await 或 Promise.all 两个 call，第二表达式只能在首个 call 真实结果放行后求值。宿主和其他程序保持响应；同文件竞态靠原子写与 FILE_CHANGED 处理，不靠整程序互斥掩盖。

## [056] 委托原语：fork / resume / commission / devops / fission / horizon

**所有 js.* 成员的返回值都是数据，绝不返回渲染后的 prose**（宿主在工具结果与 parked 载荷边界统一把值渲染给模型）。委托成员统一用信封：

```
{ ok: true, ... } / { ok: false, code: "<stable kebab code>" }
```

- `fork(payload)`：`payload` 是字符串（任务）或 `{ charge, name?, calling?, keywords? }`；缺 `name` 时由 charge 派生（派生名冲突报 `name-already-belongs`）。成功返回 `{ ok, name, agentId, outcome }`，`outcome ∈ created | nudged | commissioned`。背景由调用方完整 post-scope 视图承载。拒绝码：`name-required`/`charge-required`/`unknown-calling`/`calling-conflict`/`name-already-belongs`/`warm-start-unavailable`/`context-unavailable`/`not-a-delegation-role`/`placement-failed`/`dispatch-uncertain`。
- `resume(payload)`：续做既有道路，`payload` 必带 `name` 并携带本次 `charge`；复用该 participant 的完整历史与 binding，不重选绑定；目标正忙时 charge 作为忙碌提示追加到既有工作，不新建任务（`outcome` 为 `nudged`）。传 `calling` 一律拒绝。拒绝码：`name-required`/`charge-required`/`calling-not-allowed`/`warm-start-unavailable`/`context-unavailable`/`assessment-pending-for-devops`/`dispatch-closed`/`placement-failed`/`dispatch-uncertain`。
- `commission(payload)`：委任既有角色的道路，`payload` 与 fork 同形（`{ charge, name?, calling? }`）；`calling` 省略时按角色推导，显式给出与推导不一致即拒绝。成功返回 `{ ok, name, agentId, outcome }`。拒绝码：`name-required`/`charge-required`/`unknown-calling`/`name-already-belongs`/`context-unavailable`/`placement-failed`/`dispatch-uncertain`。
- `devops(intent)`：请求本调用 session 绑定的固定 DevOps 席位做一次工作；`intent` 是本次交给 DevOps 的意图文本（不像 fork/resume 那样承载 charge）。返回 `{ ok, kind, status, returned: [{ text }] }`；材料条目最终随结果统一渲染为 TOML。DevOps 绑定未就绪或已关闭时以 `assessment-pending-for-devops` 拒绝。
- 独立宿主工具 `join()`（无参数，无 name/program/intent，不创建临时 JS 程序）：按本会话成员归属收拢，返回 `{ ok, records: [{ name, agentId, role, status, returned: [{ text }] }] }`，不重复输出 `byName` 索引；子智能体（Engineer/DevOps）以无未决调用的自然停机作为 proven terminal，其末轮 assistant 散文随 join 记录以 `# ...` 注释交回，`returned` 不再承载子智能体的完整视图；`status ∈ completed | failed | abandoned | voided`。执行已不可能（abort、cancel 或重启加载结算）的成员以 `voided` 进入 join 记录，`returned` 为空数组：作废结清该成员的未解除义务，join 关闭，报告一次后该成员退役。拒绝码：`context-unavailable`/`not-ready`/`nothing-to-join`/`join-in-progress`/`interrupted`/`abandoned`。
- `fission({ charge, lanes })`：同身份多 lane 的任务分片执行（宿主负责 lane 绑定与收拢），返回 `{ ok, groupId, lanes: [{ index, sessionId }] }`；owner 的 `join()` 收拢本组 lane。拒绝码：`fission-unavailable`/`lane-start-failed`/`not-ready`/`context-unavailable`/`invalid-prompt`。
- `horizon()`：返回在场名册的数据信封 `{ ok, agents: [{ label, status, pace? }], ptys: [{ terminal }] }`，与 join 读同一份账（participant-horizon [011]）。`status ∈ still-away | stalled | returned | did-not-return | voided | idle`：`returned`/`did-not-return`/`voided` 恰是下一次 join 会交出的后果，`still-away` 恰是 join 会等的在跑任务，`stalled` 是未结清但无人在跑的任务，`idle` 是在场待命、手上无任务的固定 DevOps。`pace = { turnsUsed, turnsLeft, timeUsedMs, timeLeftMs }` 只出现在 `still-away` 行且委任者 estimate 与子会话起点两者都可读时；不可读即缺省该字段，绝不以 0 顶替。名册不带底层 id、target 或报告正文（正文由 join 交付）。拒绝码：`horizon-unavailable`。
派遣成员 `fork`、`resume`、`commission`、`devops` 均不携带任何背景数组参数。任务只由 `charge` 或 `intent` 表达一次，背景材料由发送方当前的完整 post-scope 消息视图承载并在最终边界统一渲染为 TOML。

实现边界：delegation shard 提供结构化入口（`ForkTool.executeReceipt`/`JoinTool.executeReceipt`/`FissionTool.executeReceipt`，返回数据与上述稳定码；宿主 `join`/`horizon`/`fission` 工具的 prose 输出保持不变，`fork`/`resume`/`commission`/`devops` 已无宿主工具注册，一律经编程面成员调用）；接线层在每次编程调用期间安装进程内委托宿主驱动这些入口，调用结束清除；未安装宿主时成员以 `{ ok:false, code:"DELEGATION_UNAVAILABLE" }` 拒绝，不静默降级。

委托面按能力投影：Manager 持有 Fork/Resume/Join/Horizon 权限，因而获得 `fork`/`resume`/`horizon` 成员，以及独立 `join()` 工具；Plan 的 DevOpsConsultation 权限投影出 `devops` 成员；Orchestrator 没有文件权限，其 Fork 权限投影为 `commission`（同一委派执行入口），因而是纯委派编程面。`hasFsCapability`/`jsToolSpec.admissionFor` 同步把 Fork/Resume/DevOps/Fission 计入编程面授权，Join 只授权独立工具，四层一致。

## [057] ls 的 ignore 语义与搜索面

`ls(path)` 在列出一层目录时应用 gitignore 语义：逐级 `.gitignore`（含否定模式与目录剪枝）加上根 `.git/info/exclude`。递归枚举与内容搜索由 `rg(argv)` 承担（[009]）。

旧 `glob` js 成员已退役。旧 `grep(needle, pattern)` 成员已退役，其能力现在只投影 `rg(argv)`。旧 `file`/`edit`/`rewrite` 成员仍从仓库生成编程面退役（包括 Plan；Plan 的文件参数受 `PLAN_PATH` 闸门限制）。

编程内 join/dag 全部删除。模型显式安排程序派工、独立 join() 收拢、下一命名程序。join 的现有资格、批次、结果身份去重、终态及拒绝码不变；用户输入、取消或宿主中断可结束本次等待，不顺手取消子工作，不以隐藏轮询或超时伪造完成。

## [058] 交互与唯一命令执行面

Host 原生 run 与 pty 工具继续退役。命令走 call(command)，交互走 send(name,input)、signal(name,signal)，恢复和放弃走 unpark(name,value?)、abandon(name)。控制由新命名程序及顶层 intent 准入，成员不能创建预算；无独立恢复工具、顶层恢复参数或命令 handle。delta 不消费最终结果；控制效果、命令退出不自行恢复续体，未知效果不变成重发许可。

## [060] "js" 机器词汇的终局

模型视野之外的名字一律保留 `js` 前缀，不做改名：durable 流名（`js-tools/transactions`、`js-tools/lifecycle-audit`）、资源目录（`js-program`、`js-plan`、`js-bookkeeper`）、`RuntimeBindingKey`（`js.cat`、`js.where`、`js.park` …）与包装器内部哨兵（`__jsParked`/`__jsAbandoned`/`__jsFailure`）。理由：这些名字是 durable 身份与机器对机器契约（流名改名等于数据迁移，哨兵改名等于重放保真风险），而模型看到的是 `todowrite-for-ROLE` 工具名与成员名——模型面与机器面本就应当分离。此裁决与第 28 轮改名（门面级命名即教学）不冲突：改的是模型面，不动机器面。

## [064] 实际 schema 与唯一公开生命周期路径

实际注册的 ToolSpec 及其 JSON Schema 转换结果严格为必填 { name:string, program:string, intent:string }，不能仅靠描述证明。绕过 schema 直调执行器也在启动前同样校验，无效输入没有程序或命令效果。源码为 class Js extends JsProgram 与 async run()。公开恢复/放弃只经成员；会话关闭复用内部收尾，不成为第二公开入口。真实宿主测试检查注册后工具与直接执行器。

## [065] 宿主并行派发与调用身份

同一助理消息或 provider run 的不同命名程序可并行执行，旧 todowrite 整段串行临界区不得让等待阻塞其他程序、控制或独立 join。保留准入、物理调用围栏、同次重放与单笔提交的确定性；新调用同名拒绝，同次重试重放回执，不重复副作用。后发程序不承诺读到未发生的写，同文件并发按原子写和 FILE_CHANGED 处理，不丢弃冲突或隐式重跑。窗口只约束实际参与程序，不是会话或全局锁。

