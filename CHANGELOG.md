# Changelog — 版本历史

## Unreleased — 接力式规划协议（Plan 模式）完整实现

- 接力式规划协议完整实现（Role.Plan、三阶段 handoff、tenure-isolation、ask 两段式、崩溃恢复；requirements/planning 包 19 条条款、40 项测试）。

## Unreleased — Blogger Provider 容量泄漏修复（GAP1 / GAP2）

- `managed-session-lifecycle` [026]（`e0527f7bb`）：Main 会话收束（宿主 `SessionDeleted` 且有 durable `CompanionBloggerLinked`）时，级联结算每个 linked Blogger 名下全部已准入 execution——pre-provider 复用 `PreProviderSettlement.settle`、after-provider-start 复用 `ManagedChatProviderLifecycle.terminal`，durable 提交确认后逐个 exact 归还容量；`AlreadyApplied`/`StaleFence` 幂等接受，`Conflict` 不吞没。
- `managed-session-lifecycle` [027]（`4133e0aa2`）：Session 收束（`ClearSession`）时取消自身与每个 linked Attached InternalLeaf 名下的全部 retained continuation input，并以 `admissionOwner.ReleasePhysical` 的 exact 路径回放其挂起的旧 credit 归还；`AlreadyApplied`/`StaleFence` 幂等接受，`Conflict` 显式暴露（带 exact key 的 `InvalidOperationException`）并保留挂起记录；不触发 `ReleaseExecution`/`ReleaseSession` 等 force 路径，重复收束幂等；`surface` 补 `sharedRetainContinuationInput` 对称暴露 shared runtime 保留操作。
- `managed-session-lifecycle` [027] 失败隔离：`ClearSession` 的 Main 自身结算/保留取消/释放、每个 linked Attached InternalLeaf 的结算/取消/释放、以及无条件 per-session 清理组逐段隔离，任一步失败不跳过其余独立义务，首个真实失败在其余义务完成后重抛；`PluginRuntimeScope.DisposeSession` 承接失败后仍执行其后续清理组。
- 对应测试：`requirements/managed-session-lifecycle/tests/026.test.mjs`、`requirements/managed-session-lifecycle/tests/027.test.mjs`、`requirements/managed-chat-execution/tests/015.test.mjs`、`requirements/provider-attempt-recovery/tests/024.test.mjs`；运行结果见提交时的工作记录。
- `managed-chat-execution` [015]（`fa40d6fbb`）：transform 在 provider 启动边界拒绝执行（attempt plan freeze 失败）时，对 exact `Accepted ∧ ¬ProviderStarted ∧ ¬Terminal` 执行写 typed pre-provider `Failed` terminal，durable 提交确认后精确归还其容量；projection 中无该 key 时不伪造结算、交准入侧；原拒绝异常仍照常上报。
- `provider-attempt-recovery` [024]（`2da1be87e`）：加载归位废弃 stale Blogger open request（`stale-open-at-load`）时，同源 `Accepted ∧ ¬ProviderStarted` 执行一并定夺为 `Failed` 并在 boot 场景发出幂等 release 请求；已启动/已终态与其他 session 不动，重复加载幂等。
- 剩余边界：终态拒绝分支显式化（GAP-227）、全量 boot sweep（GAP-228）登记于 `requirements/GAP.md`。

## Unreleased — 开发者意见 W1–W6 施工

- W1 持久化与工具链：插件路径统一 `<git-common-dir>/wanxiangshu/`（WP-001）；ndjson 事件行内嵌载荷、取消旁挂 payloads（WP-021）；UTC 日期分组 GC（WP-022）；writer 增量读与有界锁等待（WP-023，修复前后数字对比未验证）；git hook 修复后默认重开（WP-024）。
- W2 provider 与恢复：运行时漂移校验退役、交测试期性质保证（WP-003）；重启后首个新指令 prepend 状态指导（WP-020）；horizon 崩溃后不泄露未决工作（WP-025）；provider 可见文案改称「事实」（WP-026）；失败恢复去刻板实现（WP-027）；assume 穿透 LWR 核对（WP-028）。
- W3 工具面与通信：celebrate/regret 整包退役（WP-036）；enough/abandon 合入 assume（WP-029）；defer 新消费语义与 Pair Hint 鼓励（WP-030、WP-031、WP-032）；会话自动订阅自身名字（WP-033）；publish 至 user 弹窗并复制 root（WP-034）；邮箱语义双语解释（WP-035）；fork/commission calling 可选（WP-009、WP-011）；底层 id 可见面收敛核对（WP-012）。
- W4 relay：评审对象改为 findings pairs（WP-014、WP-015）；两段式 suicide 确认（WP-018）；末任收尾（WP-016）；快照去形式化（WP-017）；证书只作历史（WP-019）。
- W5 重构与遗骸：normalTransform 具名 stage 管线（WP-004）；共享 ProtocolArgumentStash 原语（WP-005）；tool.execute 具名 stage 序列（WP-006）；工具门链具名化与 capability 单源（WP-007）；Sphinx 收敛 MCP-only（WP-039）；query-shell 遗骸丢弃（WP-040）；CHANGELOG 遗骸清理（WP-041）。
- W6 文档收尾：环境变量 KISS（WP-037）；验证入口 KISS（WP-038）；指南 42 条状态标注与文档同步（算法 F）。
- 证据：各包套件与门禁由 DevOps 实跑，数字见 `proposals/` 施工记录；WP-023 修复前后同输入的数字对比未验证。

## Unreleased — WP-021 / WP-026（ndjson 内嵌载荷与措辞清理）

- `durable-events-012`：取消旁挂 payloads 目录，事件行自包含载荷。`EventEnvelope` 增内嵌 `Payloads`；`CanonicalEventCodec` 仅在事件确实引用载荷时写出 `payloads` 键（无载荷事件的 canonical 字节不变）；`IEventStore.WritePayload` 改为进程内暂存、`Append` 内嵌；`ICanonicalIntegrator.TryPayload` 成为已提交内嵌载荷的唯一读口，Store 不再自行 `readStreams` 重建缓存。删除 `ProcessEventLog` 的 payload 文件 API、`WriterStreamSync` 与 `RetentionSurface` 的远端 payload 树及缓存。条款同步 `durable-events` [002]/[003]/[010]/[012]、`durable-convergence` [010]、`speculative-investigation` [006]。
- `WP-026`：provider 可见文案统一改称「事实 / 事实链 / facts」；`证据 / evidence` 只保留在 `evidence` 字段名与「举证责任」法律用语上。双语同步，语言对等门绿。
- 修复两处测试加载器：`007-composition-loader.mjs` 与 `prefix-digest-mutation-loader.mjs` 原先对 `Uint8Array` 源做 `String()`，且在同一模块上重复应用 mutation。现按 Node 版本正确解码，并保证每个模块只改一次。
- 证据：Node 25（CI pin 为 Node 22）下 `requirements/verification-system/tests/run.mjs` 4895 passed / 0 failed；`node scripts/check.mjs` 与 `node scripts/build.mjs` 绿；`fantomas --check src/Wanxiangshu` 全树绿。

## Unreleased — 开发者意见实现手册

- 新增 `人工审订语义指南的保姆级多人协作实现法/000.md`（Knuth 式伪代码、ASD-STE100 风格短句）：把《用于人工审订的当前语义指南.md》中全部 42 条 `> {开发者…}` 意见逐条落成工作包（WP-001—WP-042），含波次划分（W0—W6）、顺序约束、冲突面、每卡算法与验收、42 行台账与覆盖率校验。本文是施工流程，不是产品规范；与 `requirements/` 冲突时以后者与源码为准。

## Unreleased — spec/000 过程规范

- 新增 `spec/000.md`（Knuth 式伪代码、简体短句）：记录本插件对 OpenCode 的全部实质性增强，27 节加附录，覆盖装载期到发布验证的完整链路；附录 A 如实登记三处观察（`sphinx` 原生工具与 `query-shell` 未在 `ToolRegistry` 注册，`CHANGELOG` 的 obligations 改名条目已被 `c8a742d23` 撤销）。README 仓库结构补 `spec/` 一行。本文是阅读地图，不是权威规范；与 `requirements/` 冲突时以后者与源码为准。

## Unreleased — S03 源码目录身份与 NuGet 协议夹具

- gen112完整Node22/npm11.12.1/SDKroot仍5003ms静默、17/18排空；缺SDKroot的436/0/7skip/2TODO只属较窄截面，误名单仅setup。npm第一正例拆真实准备完成与实际install/assert判决叶，保原强断言/held负例/预算，gen113最终待附件，不提前记绿；Archive本批未施工。

- Git源码owner私有捕获canonical parent/root dev/ino，在publication、revalidate前后和cleanup拒绝foreign；同库存目录置换正式0/2→定向12/0，完整Git/.git/bytes/mode断言保留。TOCTOU/ABA、完整FD与其它owner仍未闭合。
- 15个NuGet非法图仅共享真实不改写Git前提，每叶SDK/restore/HOME/feed/packages独立，原typed/calls断言不变；195→13是静态Git成本，不冒称CI改善。最终输入结果待[本批附件](proposals/archive/2026-10-04/S03源码目录身份与NuGet协议夹具-2026-10-04.md)。
- 9909真实CI在300000ms backstop仅817/818、active016、无权威summary，是实际超时而非pending-only；预算与默认workers不变，T418/T419继续保留。
- aac79 CI另已818/818、4253/0/103skip/404TODO，仅pending退出1，未含本批修复，不认作9909原因消除。NuGet同15叶各15/0、23.782s→13.165s只是本机单次观测；source第三个parent失配/rootmissing例加入后完整定向13/0、2.942s，最终统一输入仍待验收。source红例只观察旧revalidate接受，未运行旧dispose删除；修复后dispose拒绝与foreign库存另有正式断言。

## Unreleased — S03 编译目录身份与清理

- Fable编译owner私有捕获canonical parent/root物理dev/ino；消费、发布、revalidate与cleanup拒绝同路径foreign目录，即使完整库存相同也不能删除foreign。正常parent且rootENOENT幂等，清理拒绝保留原Error/null与Aggregate cause，不扫描unknown parked路径。
- 正式parent/root置换0/2真实红例保留；初次cp mode setup失败不冒称产品红。新批结果以[验收附件入口](proposals/archive/2026-10-04/S03编译目录身份与清理-2026-10-04.md)为准，preflight仍有TOCTOU/ABA，不声称其它owner、完整FD或readonly已闭合。
- 第二批9909/gen110已429/0/6skip/2TODO、18/18排空、groupaccepted=true；其绿色不替代新输入验收，T418/T419保持。

## Unreleased — S03 独立工具进程回收

- 判决输送让步移到beforeEach，前一项runtime判决可在下一段同步工作之前送出；原三项正式回归3/3。真实失败完整原因/位置/stack立即打印，后续挂住仍可见；相同事实不重印，不合并共享Error的不同测试，021完整18/18。

- 薄monitor拥有actual工具组，EOF或实际exit/error后回收；调用方取消Error/null在正常cleanup后精确保留，cleanup失败保留Aggregate cause。supervisor冻结原组并只等待已观察额外组，foreign对照保持。
- 四个既有取消用例先await settlement，再发第二SIGTERM并加强ESRCH；完整库存、原原因及资源断言保留。注册/仅屏障正式红例后实际接入单项1/1，最终选集待[本批附件](proposals/archive/2026-10-04/S03独立工具进程回收-2026-10-04.md)绑定。
- 首批74fc/gen108已有18/18排空、418/0/6skip/2TODO；新批不升级为fullsetsid/crash/ABA/OS或readonly闭包，控制面/bin/ps未完整固定，T418/T419保持。

## Unreleased — S03 运行器因果输送与回收

- coverage 改为异步等待真实runner/c8，父进程可响应子进程HTTP；完整报告不能掩盖signal失败，silent子进程获得stdin EOF。原分母/报告断言保留，011定向18/0。
- supervisor静默失败先收原进程组和exact HOME，再交还caller；同步spawn错误停watchdog并保原cause，真实清理失败不吞verdict或null原因。Node/npm非法角色在读归档/分配root前typed拒绝，合法库存和完整探针不变。
- [记录](proposals/archive/2026-10-04/S03运行器因果输送与回收-2026-10-04.md)分开绑定正式定向红绿与gen108的统一18验收（418/0/6skip/2TODO）。新fe9 CI4188/0只证明其输入，保gen103失败/旧72e83无结论；detached tool接上方独立监护批，runtime readonly、ABA、T418/T419未闭合。

## Unreleased — S03 实际单项目 Fable 编译

- 以选定 SDK/Fable DLL 编译已准备单项目；原 artifacts 精确复制到自有 seed，原源码、SDK、工具与工程 owner 保持，JS 及中间产物完整库存绑定四份输入身份。
- 正式编译、进程 adapter 与诊断分开记证；SDK packs 和工具自带 FSharp.Core 不被四包 NuGet graph 覆盖，只读执行、完整工程图、OS 与实际 verify 同候选仍待证明。见[记录](proposals/archive/2026-10-04/S03实际单项目Fable编译-2026-10-04.md)。
- 保留前批72e83 CI在300000ms backstop下817/818完成、无权威summary/活动身份的无结论状态；最新接续从上方运行器记录认领，不以新输入CI或本地定向通过替代旧次结果。

## Unreleased — S03 工程 NuGet 单项目准备

- 从已准备源码与 SDK 恢复原单 net10.0/no ProjectReference 项目，使用明确归档和私有 feed；派生 lock 后清空包缓存，再实际 locked 复验，完整 graph/产物身份绑定原输入。
- 区分 raw nupkg SHA512 与 NuGet contentHash；源码准备补 canonical parent、普通全库存及公开 receipt 复核。实际 Fable、全仓工程图、只读执行及同候选 verify 仍待证明，见[记录](proposals/archive/2026-10-04/S03工程NuGet单项目准备-2026-10-04.md)。

## Unreleased — S03 选定本地工具恢复

- 明确包 ID、版本和 SHA512，以已准备 SDK 私有恢复原本地工具 manifest，绑定真实 resolver、运行 DLL 与完整输出库存；取消或失败保留原因并回收自有根。
- 真实 Fable/Fantomas 版本消费不代替工程 restore 或编译；S03 不可变执行与同候选 verify 仍未闭合。见[记录](proposals/archive/2026-10-04/S03本地工具恢复-2026-10-04.md)。

## Unreleased — upstream 31e69b3dd 合并

- 吸收 raw Chronicle 基数统一检查与显式 ABSORB 声明，保持过滤前拒绝及 live 规则名核对。修正新增测试的实际日志观察与排空，保留尚未接通的 registered Host TODO。
- 两次普通合并的取舍与监督验收见[最新记录](proposals/archive/2026-10-04/Upstream增量-31e69b3dd-2026-10-04.md)；制度学习机制提炼与 S03 剩余边界未关闭。

## Unreleased — upstream aaa123b12 增量

- 吸收 occurrence frontier/coverage 分离、持久事实兼容解码、review 重放及跨任期 call 防线。补完整 binding 重放和开任期误退邮箱的正式红绿回归。
- 保留 LifeCompleted Surface 的成功接线，撤回未闭合失败恢复的新增 Suicide 邮箱退休调用；GAP-157 与生产终结 TODO 保留。合并与验收见[记录](proposals/archive/2026-10-04/Upstream增量-aaa123b12-2026-10-04.md)。

## Unreleased — S03 选定 SDK 准备

- 从明确摘要的完整 SDK 归档独立物化，在私有配置目录下使用原 global.json 实际探测 SDK 与 runtime，拒绝越界工具路径、外部 SDK 借用和身份不符。探测后完整复核，失败与取消回收自有目录并保留原原因。
- 真实 SDK10.0.302 完整库存及原10.0.100/latestFeature选择有正式定向证据；这只证明准备边界，NuGet/tool restore、实际 Fable、只读执行与 verify 同候选仍待施工。见[记录](proposals/archive/2026-10-04/S03选定SDK准备-2026-10-04.md)。

## Unreleased — upstream 590a3f69e 增量

- 合入会话 dormant 身份登记、冷读取持久消费投影刷新、取消与删除的公开 Surface，以及沙箱顶层无 JSON 返回值的类型化拒绝。吸收实际工程缺边及持久化、effect、生命周期回归。
- 保留本地 S03 准备 owner 与两个 TODO，拒绝已被真实父目录替换反例证伪的 copy/chmod 快照接线；不将顺序冷读取证明写成跨实例同时消费已闭合。编译闭包 48→56 按本批新增依赖的八个源如实记录。
- 本地受影响验收与合并裁决见[同步记录](proposals/archive/2026-10-04/Upstream增量-590a3f69e-2026-10-04.md)。
- S03 另补本仓精确 Git 输入与完整 npm11.12.1 工具归档的真实依赖安装，独立产物的 Fable、Acorn、Tar 消费者及回收已定向通过；native Host、SDK/NuGet 和不可变执行仍未证明。

## Unreleased — S03 工具归档安装与挂载输出清理

- 新增从完整选定 Node/npm 归档执行真实 npm ci 的入口，沿原工具目录运行，安装 receipt 绑定工具摘要；安装前后及返回前复核完整工具成员，失败和取消回收自有目录。
- 编译输出 reset 保留真实目录根，仅删除目录内成员，拒绝根链接或普通文件；真实 macOS 挂载输出回归证明旧删除根实现报 EBUSY，修复后可清理和写入，失败编译保留原产物。
- upstream 的复制加 chmod 快照仍能通过父目录替换读取错误源码后恢复，并返回 PASS；保留正式反例、T418/T419 与 S03 未完成范围。见[本批记录](proposals/archive/2026-10-04/S03工具归档安装与输出根-2026-10-04.md)。

## Unreleased — S03 真实 npm 安装与 Node/npm 准备

- 从选定 package/lock 字节执行真实 npm ci，显式核对 Node/npm 入口身份，使用私有配置、缓存及 HOME，禁用 lifecycle scripts。完整安装清单与独立归档产物逐项一致后才返回；锁外来源、完整性失败及取消均回收自有产物。
- Node/npm 工具准备覆盖选定归档的全部成员，必需的声明生产依赖在 npm 包内闭合，拒绝父目录补库。实际版本探测从独立目录运行，探测结束再复核完整清单；取消排空实际进程组并保留原原因。共用归档物化规则，保留外部链接、特殊文件及截断归档拒绝。
- 这两项仍是准备边界：未接入实际 verify，SDK/NuGet、只读执行和阶段内改后恢复尚待证明，T418/T419 保留。见[施工记录](proposals/archive/2026-10-04/S03真实npm与Node工具准备-2026-10-04.md)。

## Unreleased — S03 明确身份的依赖归档准备

- 新增依赖准备owner：完整读取调用者指定SHA-256的安装归档，解析后独立物化node_modules，复核全部目录、字节、权限和闭合内部链接，摘要绑定归档与源码锁文件字节。拒绝外部/悬空/循环链接、重复路径、链接祖先、特殊文件及截断归档。
- 本批不执行npm安装，不证明归档符合锁文件来源，也未接入实际verify或只读执行。T418/T419保留；见[依赖准备记录](proposals/archive/2026-10-04/S03依赖归档准备-2026-10-04.md)。

## Unreleased — S03 指定 Git tree 源码准备

- 从明确指定的Git tree读取完整原始blob，保留路径、执行位和字节；在自有Git目录重新构造index/tree核对身份，避免工作区、attributes、replace refs和继承Git环境改变候选。缺对象、promisor仓、不支持的类型或无法忠实物化的路径均失败。
- 普通合并upstream `46935e5bd` 的Replica请求收尾user行；新增真实注册hook回归，验证它不建立新的physical acceptance，原预算/终态仍绑定原bootstrap，语言变更只影响提示正文。
- 源码准备尚未接入实际verify，依赖与只读执行未封闭，T418/T419保留。见[本批记录](proposals/archive/2026-10-04/S03指定树源码准备-2026-10-04.md)。

## Unreleased — S03 输入枚举前置

- 验证输入根、普通输入及tracked corpus路径的符号链接映射现在明确失败，不再静默漏收或读取外部可写目标；合法输出根链接与普通同名文件保持原边界。
- 新增正式WHAT016反例；不可变候选、同阶段改后恢复及依赖隔离仍待完成，两项TODO保留。见[S03记录](proposals/archive/2026-10-03/S03输入链接边界-2026-10-03.md)。

## Unreleased — 忙碌子任务可随时接收指导

- Manager 的 `resume` 可向忙碌 Engineer 或固定 DevOps 追加 `BusyAgentNudge`，影响下一次 LLM 请求；保留原任务、Root、完成回调和 handoff frontier，不取消、重派或新建任务。删除忙碌拒绝及按旧 charge 文本假装成功的路径，返回明确的指导已发送结果。
- 用户输入只释放已经开始的 Join，由 Manager 决定后续动作。唤醒移到 Host 保存后的可见回执，重复消息不打断下一次 Join。
- 同 run 的追加材料先持久接纳，旧 exact lease 保留到新材料实际选入 provider 请求后才交接同一 capacity credit，涵盖旧输出已自然 stop 的窗口；修复真实 Host 中保存前唤醒及提前撤销旧 lease 导致的 EMR-009/010。补充连续提示、owned/borrowed credit 交接与取消、迟到旧回调和原工作冷重放回归，以及安装版 OpenCode 1.18.29 的完整 Join → resume → child 下一次 wire → 原任务完成场景。

## Unreleased — 用户输入只打断 Join 等待

- 删除 PR #51（`558ed1c75`）引入的外部输入物理中断端口及全部接线。新 user prompt 不再调用 Host abort，当前 LLM 输出与非 Join 工具自然完成，新输入留到下一次 LLM 请求；Join 继续使用独立等待唤醒，不取消 child。
- 准入资源仍按 exact execution 交接，旧回调不能释放新执行。同步纠正 managed-session-lifecycle 016 与 delegation 015，删除“新输入排空 Guard”的旧测试合同。
- 回归包含 unwanted abort 的先红后绿、pending demand 替代，以及安装版 OpenCode 1.18.29 的 SSE/生产工具悬置场景；默认容量与 Manager 单槽容量均检查旧结果完整、新输入出现在下一次 provider wire 和物理清理。

## Unreleased — 程序无返回值不再击穿沙箱

- 现场：`js-predictor` 程序只 `console.log`、没有 `return`，沙箱报 `PROGRAM_FAILED: undefined is not an object (evaluating 'json.startsWith')`。根因：`JSON.stringify(undefined)`（及函数、Symbol）返回 `undefined` 而非字符串，`decodeRunResult` 对它调用 `StartsWith`。现在包装器在序列化结果不是字符串时与循环引用同样返回 `INVALID_RETURN_VALUE`，符合 repository-programming WHAT[011]。
- 回归：`repository-programming/tests/011` 覆盖缺省返回、`return undefined`、函数与 Symbol。

## Unreleased — Replica 请求不得以 model turn 结尾

- Gemini Cloud Code Assist 拒绝以 model turn 结尾的请求（400 `Requests ending with a model turn are not supported`）。Replica 的镜像加本决策已完成批次总以 assistant 行结尾；`StrengthReplicaTransform` 在写回 Host 前，若末行为 assistant，则追加一条 user 行，正文为与启动时相同的 `delegation/readonly-investigation` 提示。该行 id 由 session 与末行 id 派生，重复 transform 字节一致；末行已是 user 时不追加。
- 回归：`speculative-investigation/tests/009`（末行恒为 user、重复 transform id 稳定、仅 user 的镜像不变）；005、009 旧断言改为含收尾 user 行的新形态。

## Unreleased — upstream fcfba389e 与 PR 合并冲突

- 普通合并上游 `3f9aa1636` / `fcfba389e`，清理误提交的零字节工程临时文件，吸收 CI 镜像依赖和经过复核的工程接线。保留既有 exact work、Guard 交接和固定 DevOps 取消边界。
- 正式回归阻止 `WXS_ACCEPT_TODO` 将欠证判成完整通过；保持单次真实 canary、精确版本围栏及完整 Long Stroke。cold-boundary 改匹配完整双语资源及合成 ack，不以 Chronicle 关键词删普通用户历史。
- 更新同步和施工入口；本批验证范围、吸收/拒绝依据与原始证据见[同步记录](proposals/archive/2026-10-03/Upstream增量-fcfba389e-2026-10-03.md)。既有 GAP 状态不因上游 CI 绿或统计自述而关闭。

## Unreleased — upstream b7768f478 增量

- 合并 Blogger 双语提示资源和真实 journal/lease 注入观察，以及制度学习 candidate 机械准入、Born 持久事实/投影和 revision 计算。GAP-077/181 仅进入 PARTIAL，保留真实 Host、语义提炼、生产并发及原子提交义务。
- 按现行需求保留 exact AdmittedWork、墓碑、固定 DevOps 原终态回调和取消排空；Sphinx 保留严格 Revision 与 native MCP，词汇登记改引用 Core 清单。修正新增 Surface 的 opaque resource 登记，拆平新增控制分支，不增加基线。
- ProcessHost 的 verbose/debug 诊断进入 stderr，避免破坏 canary JSON 协议；保持原预算和默认并发。正式结果及原始失败见 `proposals/archive/2026-10-03/Upstream增量-b7768f478-2026-10-03.md`。

## Unreleased — 合并 upstream e1e7dd3f1

- 合入四个上游提交，包含空只读Replica终态、重启后空闲DevOps Join、Predictor正文降格与往复恢复，以及Sphinx canonical持久化和MCP合同。
- 合并保留本地Guard替代、Host就绪与判决输送修复；补齐Sphinx严格事件版本登记、原生MCP协议与opaque permit的Fable JSON拒绝，以及exact scoped子工作的加载期void。
- 固定DevOps的空闲道路可见性、父取消后在途工作与终态结算继续按实际所有者处理；受影响测试的准入前置改用真实physical acceptance，不由裸Root或稳定handle补造工作权限。
- 更新施工总计划、生命周期/Sphinx分册与GAP台账。具体红绿、生成输入和未完成边界见[本批同步记录](proposals/archive/2026-10-03/Upstream同步-e1e7dd3f1-2026-10-03.md)；Sphinx的业务driver、现存inquiry读取DTO/trace及完整发布验收仍待完成。

## Unreleased — 原工作区迁移代码编译修复

- 补齐 Sphinx Representation、BodyDto、Persistence Surface 的编译登记，调整 completion codec 的声明和实现顺序；修正直接阻塞编译的局部缩进、元组、保留字、缺失声明/引用，以及类型和参数名不一致。
- 直接在原工作区通过 Fable 构建，并验证 predictor 正文降为 reasoning、原生 reasoning 不回传、重启及子 owner 往复；未回退现有迁移或扩展同步逻辑。

## Unreleased — predictor 正文降格回传与往复恢复

- predictor → Main 现在回传 assistant 正文并降为 reasoning，原生 reasoning 不回传；混合工具消息与无后续请求的终止正文都纳入 frame 的持久化、校验、重放和 XTrace 覆盖。
- 最终 Host 编码补齐模型传输坐标，防止合成 reasoning 被 Host 再转为普通正文。Main → predictor 保持普通正文与思考的原语义；已同步正文恢复原类型，合并 predictor 自身 reasoning，且不重复插入。
- 补充往复前缀性质、重复正文次数、EventStore 重开与终止正文回归；真实 Host 验证常驻 predictor、重启重放和子 owner 的 reasoning_content 外发。

## Unreleased — 修复重启后空闲伴随句柄阻塞 Manager Join

- 现场根因：进程重启后，Manager 经 `horizon` 重新收养持久化的空闲 `devops` 伴随句柄时，`syncAdoptDevOps → AdoptExisting → runtime.Restore` 会新建一个 completion cell 打开、CTS 未取消的 `ChildRun`，使 `ActiveRunCount ≥ 1`，即使该句柄当前没有任何在跑任务。`HostForkJoin.parentHasJoinWork` 以 `runtime.Runtime.ActiveRunCount > 0` 短路判为有工作，Manager 的无超时 `join` 于是永久等待一个永远不会完成的幽灵运行，整条主会话挂起。
- 修复：`parentHasJoinWork` 只在**无 journal 的纯 PTY 模式**才以进程内 `ActiveRunCount` 作为 agent 工作信号；有 journal 时真实 Host agent 运行由 `PendingRuns`/`PendingCompletionCount`/`PtyRuns` 可见，可 join 的 durable handle 由 journal 投影可见，重启后重新收养的空闲伴随句柄只注册身份、不误判为有工作。真实在跑的 agent 仍会经 `PendingRuns` 正确阻塞 join。
- 回归：新增 `RESTART_ADOPTED_IDLE_DEVOPS_does_not_block_join_with_hang`，重开同一 durable journal 后收养空闲 devops 再 `join` 必须返回 `NothingToJoin`。修复前该测试 `JOIN_HANG_DETECTED` 失败，修复后通过；完整 delegation 套件 71 通过 / 0 失败 / 22 TODO。

## Unreleased — 修复只读同伴在空正文提前停止时导致的决策挂起

- 现场根因：子代理（如 gate-scout）在委托只读同伴（Strength Replica）调查时，若模型在最后回合直接以 `finish='stop'` 结束且输出正文为空（仅有 step-start/step-finish 等骨架），OpenCode 的分类器会将其判定为需要交互修复的 `TurnNeedsContinuation EmptyFormalText`。
- 由于只读同伴属于内部叶子（InternalLeaf），不享有也不执行 InteractionRepair，此前 `StrengthReplicaRuntime` 忽略了该结果，导致 completion `Task` 永不结算，决策永久卡死在 `Bound`，进而使主模型会话永远阻塞在下一次变换与 `join` 等待。
- 现将该物理停止信号在只读同伴所有权内正确确认为决策提前结束（`TextCompleted`），并安全清理物理会话与租约；不放宽普通 Work 会话的交互修复门禁。
- 补齐空正文提前停止、前缀证据保留、常驻副本连续复用及真实 Host 下 6 次委托（含空终止闭环）的端到端可复现回归测试。

## Unreleased — predictor 回传到主会话的映射修复

- 现场 `InvalidRequestOrdinal (1, 2)` 来自第二次常驻委托：首轮工具名误成 `<tool_call>js-predictor`，Host 记录错误，次轮正常只读调用完成。筛掉首轮后仍保留材料编号 2，导致 bundle 拒绝并返回 500。现在 surviving readonly batches 连续编号，provider 请求预算仍独立记账；不放宽帧校验、不压制熔断。
- Candidate 与 replay 都按 owner 角色映射 `js-predictor`；最终 Host 外发边界将逻辑 call/result 合并为原生 completed tool parts。追踪前仍保留完整交换与稳定 ID，已有 Host 行原样保留；JSON-looking 工具结果不再被解析，输出文本字节不变。
- 补齐中间 provider 请求的消费确认：工具循环尚未结束时，下一次主变换按真实快照中的 exact assistant、physical parent、完成状态与模型输出提交 Promoted，再进入 XTrace 和 Traced。错误、取消、未完成、错 owner/parent 与空输出不升格。
- 回归覆盖筛空首批/中间批、原生输出字节、并行 call/result 配对、物理行保留及 exact 消费拒绝边界。真实 OpenCode + 隔离 HTTP provider 场景覆盖两轮只读、错误工具后恢复、常驻复用、进程重启和 Engineer 子 owner：四次 Bound/Prepared、九次 predictor 请求，主模型实际收到五条完整交换，两个决策完成 Promoted → Traced；无 bundle 错误或主请求 500。

## Unreleased — predictor 的源消息、常驻生命周期与物理父链纠正

- `../wanxiang/.git/wanxiang/events/b1ca41f13bc24d709cc25219737ec160.ndjson` 中 14 次只读委托全部在 Requested 阶段以 `cannot-continue` 关闭，零 Bound；授权记录把下一条 assistant 占位消息当成发出工具批次的源消息。现在源 ID 与工具批次从同一实际 assistant 取出，并核对它的真实 physical parent；新输入不能借用旧批次的估计。日志未开启逐门诊断，不能把全部历史关闭逐一归因于同一分支。
- 常驻 predictor 在第一次 await 前认领 owner；同一 decision 共享准备结果，不同 decision 不创建竞争子会话。取消与卸载会排空未完成的创建；无模型容量的未发送委托立即退出，不等待不存在的物理终态。复用只订阅未来终态，退出时移除监听；终态按实际 physical input 精确匹配，旧回合不能结束新委托。结果保留到持久发布确认，避免物理清理先到时把并发消费者误判为恢复孤儿。
- 每次结果收集仅扫描本次 bootstrap 之后的消息，历史工具材料与旧纯文本结尾不再污染或中止新委托。重启复用依据 durable Bound 中的子会话 ID 与 Host 的精确 agent/title；查询失败、历史多候选或元数据冲突明确拒绝，不另建副本，不按标题或时间任意收养，也不删除历史会话。
- Host 父链在缓存缺失时按原生 session metadata 恢复；创建与查询统一使用实际 family root，子 owner 的 predictor 与其他受管同伴不再成为物理孙会话。逻辑 owner、预算、语言和材料归属不变；父链查询失败或出现环时不创建会话。需求条款补上不变量与条件性进展证明，模型是否给出合法正数估计及外部服务可用性不作无条件保证。
- root/worktree 实例按同一 git common-dir runtime key 共享 predictor 登记表、物理协调器与 fuse；引用计数归零才卸载。通用实例清理不释放共享常驻会话及其 owner 的模型租约，最后卸载再交还两者。带有真实 physical parent 证据的早到终态先暂存，再由实际绑定精确重放；不丢失通知，也不借旧通知认领新决策。

## Unreleased — provider 错误不再被空输出修复抢走

- 修复 `CompletedTurnClassifier` 将 `completed + error + 空/XML-only 输出` 降级为 `TurnNeedsContinuation` 的错误。错误终态现在始终是 `TurnFailed`：先到的 idle 等待 exact typed failure，不再发送 `missing-final-report`；确切失败观察仍由原有 provider recovery 记账、派发 fresh `ProviderRetryAttempt` 并重新选择可用的 LWR 前缀。无 provider error 的空/XML-only `stop` 仍走内容修复，operator abort 仍为取消。
- 现场证据：`../wanxiang` 的 DevOps 会话 `ses_f0dca4962ffeQSTPc5KnX6V0tZ` 在连续 400 超限期间记录了 210 次 `InteractionRepair`，却只有 8 次 `FailureRecorded` / `ProviderRetryAttempt`。repair 反复携带 cutoff=450 的旧 probe；最后一次真正的 provider retry 才选到 Blogger 已证明的 cutoff=656。修复不按 400 文案分支，不引入容量估算、非法截断或新会话兜底。
- 回归覆盖空输出、reasoning-only、XML-only、partial answer、error finish、正常内容修复及取消边界；新增真实 `ReconcilePass.run` 的 idle 先到、exact provider terminal 后到回归。修改前回归失败为 `TurnNeedsContinuation`；修改后，用数据库中本次报错的原始 assistant message 离线重放，idle 交付 0 次，exact failure 交付 1 次 `TurnFailed / ProviderTransient`，不携带 idle repair 权限。
- 定向验证：相关 12 个测试文件 90 通过、0 失败、5 TODO；构建通过。用真实 400 消息继续驱动 retry policy、生产 LWR candidate 物化与 Host-id prefix replacement 的离线 smoke，覆盖历史确实移除，Opening 与当前回合原样保留。扩展套件 630 项中 563 通过、20 失败、47 TODO；失败集中在未修改的 Chronicle 旧文案断言、Blogger flight/capacity 交错及 ingress provenance 测试。仓库 check 另报 184 项，未定位到本次修改文件；不宣称全仓门禁绿。

## Unreleased — F# 控制金字塔债务清零

- **`fsharp-control-pyramid` 从 42 项降为 0，baseline 清空为 `{ "version": 1, "files": {} }`**：13 个文件逐处按 `structured-workflow-004` 提取具名 helper 或改用组合子消除 `depth>=2` decision，不再依赖按文件记账的 ratchet。`Batching.fs`（10）把 charge 渲染、消息替换、pending 取走各自成函数；`Delegate.fs`（8）把 wire 批次扫描收成 `wireBatchStep` 单步 Result、把预算聚合收成 `ofRounds`/`ofParsedResults`/`parseCall`；`InvestigationEstimateContract.fs`（8）拆出 `validateNumber`/`validateNoteText` 与中英文文案表；`Send.fs`（4）拆出 `persistSubmittedFact`/`admissionVerdict`/`settleAdmittedReceipt`，try 只包一层。
- **其余清零点**：`PhysicalAcceptance.fs` 环境变量解析、`AgentJournal.fs` 用 `Result.map fst`、`ToolRegistry.fs` 提取 `executeReplica`、`ProviderSystemTransform.fs` 提取 `isReplicaConstraintLine`、`SessionRecoveryHost.fs` 提取 `settleUnresumedFor`、`OpenCodePort.fs` 提取 `promptDispatchOutcome`（HTTP 前缀判定改为 guard）、`ModelRouting.fs` 提取 `normalizeProviderStepRecording`、`Runtime.fs` 提取 `settleInFlightDelegateExecution`、`PluginHooks.fs` 提取参数恢复与估计校验 helper（`toolAfter` 的 participating 恢复语义不变）。
- **门禁说明同步**：`fsharp-control-pyramid-guide.mjs` 第 14 节改为「baseline 为空对象，任何 depth>=2 decision 即新增债务」，删去按文件历史记账的表述。
- 验证：`node scripts/build.mjs` 绿（165 surfaces / 836 modules）；`node scripts/check.mjs` 中 `fsharp-control-pyramid` 报 0（余下 174 项为既有 `js-boundary-gate` 债务，不在本次范围）；定向套件 `structured-workflow-004`、`host-boundary-032`、`speculative-investigation-{002,004,011,012,013,016}`、`delegation-{007..012,023..025,028,031,032}`、`execution-model-routing-{004,010}`、`managed-session-lifecycle-{004,009,014}`、`provider-attempt-recovery-021`、`feature-ablation-002`、`crash-reconciliation-015` 共 32 个文件 0 failed。

## Unreleased — Pair Programming 指引七条纪律真理版本

- `resources/provider/host/pair-programming-guideline/zh-CN.md` 重写为七条编号纪律（使用中文、小步快跑、不要吝賂、进度更新、极高并发、超越常识、善于内省）；`en.md` 按该真理版本忠实重写，语言纪律按绑定语言镜像（英文版要求全程英文）。cognitive-environment WHAT 013/016 与 tests/013、016 锚点同步：`ready frontier` 改为就绪前沿、`先抽象，再笃定` 改为分析与抽象后钉住反常识判断，并新增 en 版七条纪律结构断言。

## [Unreleased]

- speculative-investigation / host-boundary: 规范切换到  新合同。
  - **共享合同与协议修订版 2**：新增 `src/Wanxiangshu/Strength/InvestigationEstimateContract.fs`，将面向模型的意图性字段 `delegate_readonly_rounds` clean-break 重构为事实性连续只读轮数估计 `estimated_readonly_rounds`（0..2147483647 原生整数）。
  - **条件性调查展望**：`self_note` 明确为条件参数——估计大于 0 时必须提供一至三句非空白未来展望；估计为 0 时必须完全省略。提供与规则解释文案彻底解耦的机器稳定标识（`NotePresentWhenZero`、`MissingOrBlankNoteWhenPositive` 等）。
  - **逐工具判定矩阵**：严格按工具操作性质判定（全系统 12 个参与工具装饰，28 个不参与工具无任何增量）。
  - **身份边界与历史防重**：彻底剔除执行意图与信任叙事；同伴防递归完全由真实身份边界保证，去除副本必须填 0 的虚假约定；旧字段 clean-break 废止，运行时拒绝旧字段与新旧混用，历史落盘事件原样保留不重写；古老 K1/K2 迁移工具保持契约修订版 1，当前运行时采用修订版 2。
  - **负资产清理与教训**：
    1. 彻底删除纸糊门禁 `scripts/checks/test-source-read-gate.mjs` 三件套。教训：该门禁仅对静态路径做 `allowMap.has(relPath)` 查表，从不核对测试运行时真实读取路径；单行正则完全漏检多行 `path.join` 写法；白名单中 33 项记录大面积超额登记。抓不住真实反例、靠超额豁免制造虚假安全感的门禁比没有更糟。
    2. 堵死 `requirements/verification-system/tests/e2e/support/long-stroke-oracles.mjs` 约 L1415-1424 的降级断言分支。教训：原实现在两个协议标记皆缺失时，退化为拿整段工具描述当增量文本做负向断言，将“描述装饰丢失”伪装成“描述包含旧叙事”，使排查者顺着报错永远查不到真因；现改为严格断言标记存在，标记缺失立即亮红。


## Unreleased — PROMPT-006 执行绑定去层化收尾（P1–P6）

- **删除 Host 侧平行执行身份/模型权威（PROMPT-006 全类根因）**：`SessionExecutionBinding` 的进程本地身份表（`agents`/`parents`/`internalRoots`/`hostAuxiliaryChildren`/`acceptedPromptBindings`/`providerAttemptBindings`/`persistentDevOpsModels`）与 JS 测试面 `SessionBindingSurface` 全部删除。模块只保留两件事：durable `Accepted` 证据查询（`isManagedExecution`）与 transform 的 provider-step 门（`beginPhysicalProviderAttemptForTransform`）。原 24 处 `PROMPT-006` 报错（"no accepted execution binding"/"no frozen agent binding"/agent drift from cache 等）随平行表一起消失；`chat.params` 只剩 4 处基于 exact 证据的 fail-closed（agent/model 漂移、缺 lease、缺观察）。
- **工具边界改为 exact run→physical**：`ModelRouting` 新增 `providerStepIdentityByRun`，只在权威 Host start observation（assistant `message.updated` 的 `parentID`）与 `EndProviderStep` 写入，`ReleaseExecution`/`ReleasePhysicalExecution` 随执行退休清除；`ToolRegistry.providerToolBoundary` 用 `tryProviderStepIdentity` 解析本 run 的 physical，不再读 session-current 绑定副本。
- **chat.params 三态判定**：durable `Accepted` + committed lease → 校验漂移并投影 temperature；durable `Accepted` 无 lease → fail closed；无 durable `Accepted` → 完全交给 Host（Host compaction/title/auxiliary 不再误报）。
- **发送端 model-free + 死参数删除**：`SessionBindingIntent` 从 `OpenCodePromptOptions` 与全部调用方删除；`prepareManagedPrompt`/`prepareUserFacingPrompt`/`participantAgent` 链删除。
- **父边 durable 化**：`HostSignalBootstrap.durableParentOf` 直接读 `HandleByChildSession` + `SessionAssociationProjection`，不再有可被重启清掉的父边缓存。
- **`ChatExecutionState` 改为互斥 union**（`Accepted | Started | EndedBeforeStart | EndedAfterStart`）："ProviderStarted 无 started evidence"、"Terminal 无 terminal evidence"、"pre-start Completed" 不可表示；durable wire 格式不变。
- 测试迁移：`bindManagedChild` 改为追加真实 durable `Execution.HandleLinked`；crash-021/host-006/host-033/ia-011/pid-008/office-007/ipp-013/behavior-diagnosis-006/emr-010 全部迁到 durable 证据出口；`providerBinding` 观察字段（第二表计数）删除，改为 `exactLeaseCommitted`。

## Unreleased — Manager 循环 clean cutover

- **修复 Blogger 同伴每个新请求的首个 provider step 自杀，覆盖材料永久断流、主会话前缀替换不再前移（context-compression-024 / dispatch-protocol-006 / capability-enforcement-021）**：实机 2026-09-28 00:58:03 与 07:27:19，主会话派发 Blogger 请求后，同伴 run 在调 provider 之前就被插件自己 cancel（journal：`ChatExecution Terminal Cancelled/PreProvider` 早于宿主 `cancel` 15 ms），flight 一直被占，后续材料全走 `SkippedInFlight`，不再有 `BlogObservationCommitted`，K 窗口 cutoff 不前移，主会话 raw 历史涨到 1 MiB 上限报 400。三层根因叠加：
  1. **step 位置判错**：transform 输入里没有正在构造的 step；新 physical message 的首个 step 看到的“最后一个 assistant”是回答上一条 physical message 的历史尾巴（这里是被中止的空 assistant），continuation 却把它当成本 step 的散文终态去修复；
  2. **落地证据按 payload 覆盖**：所有 Blogger Main 派发都是同一段固定指令、同一 payload digest，`AcceptedDispatches` 以 `(session, payloadDigest)` 为键，后一次落地覆盖前一次，07:02:41 一条同载荷 claim 被放弃时还清空了该槽，旧 physical message 的落地证据就此消失；
  3. **Unproven 被当作 Current**：证明不了归属就照常认领修复，修复 episode 回 `PendingRepairWait`，continuation 于是 `StopPhysicalRun` 停掉自己刚派发的 run；而停下的 turn 在 idle 时是 `TurnAborted`，idle 又不把它交给修复 owner，flight 永远不释放。同一路径还会让每次 nudge / AABB / ProviderRetry 续提示在首个 step 自杀。
  重写：`EnforcerCycleDecode.stepPosition` 按 Host `MessageV2.latest` + assistant `parentID` 给出 typed `First | After(previous) | NoRequest`，修复谓词全部作用于 typed `AssistantStep`；continuation 先判位置、再证归属：首个 step 从不评判历史，只投影 live request 的 canonical 视图（本 request 的 repair 落地时在视图后保留这条 repair 原文）；之后的 step 只有 physical message 被证明为 Current 才可 commit、交给 repair owner 或停止 run，Superseded/Unproven 一律原样投影；删除 durable open 回读（`resolveCycleContext`）与未归属 fatal 分支。PromptAuthority 投影新增按 physical message 精确保留的 `PhysicalLandings`，归属判定、`Child.ensureForLinkedChild` 与 Dispatcher 的两处“这条 physical 是否由该 claim 落地”全部改读它；`BloggerRequestOwnership.decide` 把“open 尚未绑定 PromptKey”判为 Unproven；coordinator 的 transform / idle 入口对 Unproven 返回新增的 `UnprovenIgnored`，不消耗预算。`CompletedTurnClassifier.bloggerIdleRoute` 让被中止（非 degeneration guard）的 Blogger turn 在 idle 交给同一 repair owner 后再走常规中止观察。
  验证：`requirements/context-compression/tests/024.test.mjs` 新增 6 条回归（首个 step 不评判历史尾巴、superseded 步永不 commit/stop、repair 落地首步可见 repair 原文、Unproven idle 不耗预算、未绑定 open 判 Unproven、同载荷 claim 放弃不抹去 open request 依赖的落地）；`dispatch-protocol/tests/006.test.mjs` 新增同载荷落地逐 physical 精确保留；`capability-enforcement/tests/021.test.mjs` 新增 Unproven 不耗预算与中止 turn 的 idle 路由；既有 021/024 与 flight 交错支撑改用真实 durable 归属（生产写入口：HumanRoot + 落地派发 + `bindRequestDispatch`），`018` 结构断言改为“continuation 从不回读 durable open 权威”；真实 journal 副本离线重放 07:27:18 那一步：修复后首个 step 投影 canonical 视图、不含旧 aborted 尾巴，旧 P0 步判为 superseded 且 flight 保持。单元层 3799 通过；余 11 项失败全部落在本改动未触及的在途文件（speculative-investigation、crash-reconciliation 测试形态、Fission `mutable` 注解、integration 入口登记）。

- **修复跨进程重启后同伴 Blogger 恢复派发抛 PROMPT-006 导致覆盖材料永久断流（crash-reconciliation-020）**：进程重启后 `SessionExecutionBinding` 丢弃内存会话绑定，从 durable journal 恢复子会话身份时，`SessionBindingRecovery.evidenceFor` 原先仅查询 `HandleByChildSession`，遗漏了以 `SessionAssociationProjection.Associations` 形式记录的 Companion / Blogger 会话。这导致重启后首个由主会话触发的 `BloggerRequestMaterialized` 请求在 `prepareUserFacingPrompt` 中被误判为 user-facing 会话，继而抛出 `PROMPT-006: user-facing session has no observed user binding` 并标记 `PluginPromptAbandoned`，使同伴再也无法写入 `BlogObservationCommitted`，主会话前缀覆盖永久冻结并在几轮后突破 1M 上下文限制触发超限错误。
  修复：`SessionBindingRecovery.evidenceFor` 补齐对 `SessionAssociationProjection.tryMainSessionOf` 的回退查找，正确还原 `(parentSessionId, "blogger")`，使重启后同伴会话仍被正确判定为 managed session 并通过 `prepareManagedPrompt` 顺利派发。

- **修复重启后 Blogger catch-up 永久停摆（crash-reconciliation-020 / context-compression-024）**：实机 manager 会话的 Blogger 在某次重启后 coverage 冻结在 173（已折叠的 prefix cutoff），而主 loop 一路跑到 turn 232 —— ctx 只能继续膨胀。根因：那次重启让一个已 materialize 的 Blog 请求（`4d17d08d…`）的 provider run 在提交后被取消，durable open `BloggerRequestMaterialized` 从此悬空。悬空 open 让 coordinator 每次都走 `hasOpenProducer` 的"暂存材料"分支（只 `OfferMaterial`，不再物化新请求），而它等的 parked producer 是进程本地的、已随旧 runtime 消失；enforcer 对"durable open 但无 live cycle context"的既有处置是明确等待 owner 复活。Load Phase 的子 run 结算（`ChildWorkRecovery`）只覆盖解析得到 delegation handle 的子会话，companion/Blogger 无 handle，因此每次重启都只剩 `CompanionBloggerLinked`、再无 cycle。
  修复：`BloggerAbandon.staleOpenRequests`（纯枚举：仍未 commit/abandon 的 open request，排除本进程仍有同 RequestId live flight 的）+ `BloggerAbandon.settleStaleOpenAtLoad`（逐条置 `BloggerRequestAbandoned`，reason `stale-open-at-load`），由 `PluginRecoveryWiring` 在 Load Phase 与子 run 结算同一处、`PluginRuntimeReloaded` 之前调用；Abandon / PromptKey bind / crash recovery 仍共用同一 materialization admission 与单一 abandon writer。
  验证：真实 journal 副本上跑生产结算——结算前 11 条悬空 open request（含 manager 的 `4d17d08d…`），结算后 0 条且写入 11 条 `BloggerRequestAbandoned`（reason `stale-open-at-load`）；`requirements/crash-reconciliation/tests/020.test.mjs` 新增两条回归（无 live flight 的 open request 被枚举/结算、结算后 open slot 清空且枚举幂等；Load Phase 在 `PluginRuntimeReloaded` 之前调用）；crash-reconciliation + context-compression 401 项、prefix-stability/host-boundary/provider-attempt-recovery 414 项（余 1 项为既有 WIP 失败 host-boundary-032 C35，未含本改动时同样失败）。

- **大重构：子会话解析改为“durable 为真源、进程表为缓存”（crash-reconciliation-020 / managed-session-lifecycle-024）**：本会话四次故障（权威冲突、绑定缺失、登记缺失、门禁拒绝，到 `[COMMIT-REUSE-ERR: "Unknown agent id: opghu8"]`）同一个病根——进程本地表被当成存在性真源，重启后每处都要单独补一次。本次按一条规则统一切换：**durable handle 投影回答“这个子会话是否存在、属于谁、用哪个执行 agent”；进程内表只记录“本进程当前在驱动什么”**。
  落地：新增 `Execution/Delegation/DurableChildLookup`（`byHandleId` / `byByname` 纯查询）；`HostForkRuntime` 新增 `TryChildFromDurable`、`TryFindAgentOrAdopt`、`ReusableChildOrAdopt`、`AwaitChild`、`HasChild`，并把 `Reuse`、`TryChildSession`、join 的目标 await、reuse 门禁、`announceChild`、orchestrator 的 `hasChild`、父取消时 devops 绑定的保留全部改走它们；`SessionExecutionBinding` 增加 `installDurableChildEvidence`，`tryParent`/`tryAgent` 未命中本地缓存时按需从 durable 解析并回填；`SessionBindingRecovery` 重写为“安装该解析器”（`installFrom`/`install`）。删除两个装载期特例通道 `DurableChildAdoption`（重新登记）与原先的绑定预热扫描——同一件事现在只有一个按需规则。
  验证：构建 generation 42/43；新增回归 `CRASH_020_reuse_resolves_the_child_from_its_durable_handle`、`CRASH_020_reuse_reports_unknown_for_an_unlinked_handle_id`、`CRASH_020_host_owned_hidden_leaves_yield_no_binding_evidence`，并把既有重绑/结算用例改为断言 durable 解析与“handle 仍 Active、joinable 为空”；fission lane 同规则（`FissionRuntime.installDurableLaneEvidence` + `SESSIONBINDING` 的 `fissionLaneFor`）；`crash-reconciliation + delegation + managed-session-lifecycle + change-integration + host-boundary + intra-participant-parallelism` 共 828 项，余 5 项为本会话之前即存在的 WIP（host-boundary-032 C34–C38）。

- **激进重构第一刀：reuse/placement/await 改为以 durable handle 为单一真源（crash-reconciliation-020）**：实机 `[COMMIT-REUSE-ERR: "Unknown agent id: opghu8"]` 追到 `Fork/Host/Agent.fs:761` —— `Reuse` 只用**进程本地**的 `TryReusableChild` 判存在，重启后空表即判「Unknown agent id」，而同一个 handle 在 durable 投影里完好（`opghu8` = `triage-misc`）。这是本会话第四次同类病：进程本地表被当作存在性真源。
  改动：`HostForkRuntime` 新增 `TryChildFromDurable`（按 handle id 读 durable 投影）与 `ReusableChildOrAdopt`（先查进程登记，未命中则按需收养后返回子会话），`Reuse` 改用它；`.fsi` 同步；`Tool.fs` 的 `printfn "[COMMIT-REUSE-ERR]"` 调试输出替换为 `Diagnostic.emit "fork-reuse-commit-failed"`。
  验证：构建 generation 36；crash-reconciliation + delegation + managed-session-lifecycle + host-boundary 共 655 项，余 5 项为既有 WIP（host-boundary-032 C34–C38）。运行时级回归（重启后直接 reuse 既有 byname）待补：现有 Fork surface 无运行时入口，需要一个测试缝。

- **重启归位改为“作废中断 run”：horizon / join / reuse 三处状态一致（crash-reconciliation-020）**：上一版归位把遗留子 run 写成 `HandleCompleted(Cancelled)`，留下一个没有 body 的“未收交付”；但 `horizon` 的名单只收本进程持有或 Abandoned 的 handle（收养的子会话故意 dormant），于是 horizon 空空、join 无可收，而 reuse 门禁却认定有未清交付——三处各说各话（实机：六个既有 Engineer 子会话逐一 resume 全被答“此人目前无法再接下另一项托付”）。用户裁决：中断的 run 什么结果都没产出，就不欠任何人交代——horizon 为空、join 为空、直接 resume 才是正确时序。
  修复：新增 durable 事实 `ExecutionFactCases.ChildRunVoided`（fold 只产出 `TerminatedChildHandle`，关闭子 run 权威、**不动 handle 生命周期**；codec 与 `ExecutionFact` 帮手同步），`ChildWorkRecovery` 的 Load Phase 结算改用它——子会话保持可复用，horizon 与 join 都不再出现幽灵交付。另修 `Tool.fs::reuseResolvedAgent` 的 `TryFindAgent = None` 分支：重启后的进程登记可能还没有该子会话，此时以 durable handle 为准走正常 reuse 路径，而不是回 `person-unavailable`。
  验证：`CRASH_020_run_without_terminal_is_settled_at_load` 改为断言“结算后 handle 仍 Active、join 可收集合为空”；`CRASH_020_cancelled_completion_is_reported_and_retired_by_join` 继续覆盖真正带 `Cancelled` 完成时的单次报告；`crash-reconciliation + delegation + managed-session-lifecycle` 共 400 项全绿（durable-events 的 3 红为本会话之前即存在的 WIP：Strength fact 父边/oracle 清单）。

- **取消的归位结果必须能被 join 收取：无主体的 `Cancelled` 完成改为报告一次后退休（crash-reconciliation-020）**：重启归位会把遗留子 run 结算成 `HandleCompleted(Cancelled)`，该完成没有 body；`JoinDrain.missingBodyOutcome` 对 `CompletedAwaitingJoin{Cancelled}` 返回 `None`（静默跳过），于是 join 报“没有在外可接收的工作”，而 fork 的 reuse 门禁又认定该子会话有未清交付——实机表现是 manager 对 domain-core / replica-runtime / spec-author / schema-contract / frame-integrity / recon 六个既有 Engineer 子会话逐一 resume 全被答“此人目前无法再接下另一项托付”，只能不断 fork 新名字。
  修复：`JoinDrain` 新增 `tryConsumeCancelledCompletion`，对无 body 的 `Cancelled` 完成按既有单次报告语义消费（`afterConsumeCas` 参数化 runId 前缀为 `cancelled-`），报告后退休该 handle，等价于 Abandoned 的单报墓碑；有 body 的完成路径不变。
  验证：新增 `CRASH_020_cancelled_completion_is_reported_and_retired_by_join`（真实 `drainFromJournalWhere` + 假 journal port：一次报告、durable append、handle 退休、不重复投递）；crash-reconciliation + delegation + managed-session-lifecycle 共 400 项全绿。

- **移除 `/continue` 命令：重启归位改由系统在加载阶段自行完成（crash-reconciliation-018）**：显式续传命令及其全部痕迹删除——`ExplicitSessionResume`、`ExplicitResumeSurface`、`ExplicitResumeSuppression`、`SessionResumePort` 四个模块，`HookKey.CommandExecution` / `HookEffect.AdmitExplicitResume` 策略项，`PluginHooks` 的命令注册与 `command.execute.before` 钩子，以及 `HostSignalBootstrap`/`ChatParamsHook`/`HostTurnObserver`/`PluginTransforms`/`HostSessionDeletion`/`Companion/Transform` 中的 disclosure-only 分支与 `ExplicitResumeDisclosure` 变换模式。规范 [018] 改写为：重启后由系统自行归位（结算遗留子 run、重建执行绑定、重新登记子会话），只做持久记账、不重放命令；被中断的工具保持失败并留在可见历史。测试同步改写：`018.test.mjs` 改为断言“加载不注册任何命令 + 源码树无显式续传残骸”，`019/020` 与 host-boundary `014/019` 去掉命令与分支探针，`context-compression/018` 删除两条披露材料用例。

- **重启后 reuse 子会话被答 person-unavailable：Load Phase 重建 fork runtime 的子会话登记（crash-reconciliation-020）**：`fork` 的 reuse 门禁 (`Execution/Delegation/Fork/OpenCode/Tool.fs` 的 `reuseResolvedAgent`) 依赖进程本地的 `runtime.TryFindAgent`；重启后登记为空，于是对任何 durable 子会话（固定 DevOps、forked Engineer 子会话如 `frame-integrity`）都回 “此人目前无法再接下另一项托付”——（此前只有已移除的 `/continue` 会重新登记，普通重启没有。）
  修复：新增 `OpenCode/Host/DurableChildAdoption.fs(i)`（纯计划）并在 `PluginHooks` 的 Load Phase 调用既有的 `ToolRuntimeScope.AdoptExistingChild` 重新登记：只登记 `Active` / `CompletedAwaitingJoin` 且 `DurableParentHandle` 的 handle；`Abandoned`/`Retired` 墓碑与 Host-owned hidden 叶子不入列。登记只是记账，不发任何 prompt、不重放命令，复用仍是 manager 的显式动作。
  验证：新增回归 `CRASH_020_durable_children_are_addressable_after_a_plain_restart`（普通重启后现存 durable 子会话可被 reuse）与 `CRASH_020_tombstones_and_hidden_leaves_are_not_addressable`（retired / host-hidden 不可用，awaiting-join 仍可用）；crash-reconciliation + managed-session-lifecycle + delegation 共 412 项 411 pass。

- **重启后仍被拒绝的托付：上一个 runtime 遗留的活跃子 run 在 Load Phase 结算（crash-reconciliation-020）**：硬杀进程时子 run 可能既无 terminal 也无后续事实（实机：devops run 01:57:46 登记、01:58:01 `ProviderStarted`，02:07 重启时无任何 terminal），`ChatExecutionRecovery` 把上一个 runtime 的物理证据判为 stale 而 `Ignore`，于是 `ActiveLogicalRun` 永远开着，下一次交接继续被 `ActiveRunIdentityConflict` 拒绝、join 无对象。
  修复：新增 `OpenCode/Host/ChildWorkRecovery.fs(i)`，在 durability 激活前的 Load Phase 为每个「子工作 run 仍 Active 且能解析到 Activity 父 handle」的会话追加一条 `HandleCompleted(Cancelled)`；已有的 delegation fold 从该事实导出 `TerminatedChildHandle`，关闭子会话权威并让父 handle 进入 `CompletedAwaitingJoin`。Manager/HumanRoot 自身道路与无 handle 的 session 不入列；transcript 保留，不重发任何命令。
  验证：新增回归 `CRASH_020_run_without_terminal_is_settled_at_load`（无 terminal 的子 run → 计划出 orphan → 结算事实 fold 后权威关闭且 handle 得 `Cancelled`）与 `CRASH_020_human_root_and_unlinked_sessions_are_never_settled`；crash-reconciliation + managed-session-lifecycle + delegation 共 410 项 409 pass。

- **重启后固定 DevOps 无法接手：进程本地执行绑定在恢复时重建（crash-reconciliation-020 / managed-session-lifecycle-024）**：重启丢掉 `SessionExecutionBinding` 的进程本地绑定（父边 + 冻结 agent），于是下一次交接走 `prepareManagedPrompt` 时被拒 —— 实机事实 `Prompt.PluginPromptAbandoned { Reason = ["SendFailed", "PROMPT-006: parented session has no frozen agent binding"] }`，manager 看到“尚不能再接下另一项托付”，改去 join 那个永无结果的 handle。
  修复：新增 `OpenCode/Host/SessionBindingRecovery.fs(i)`，在 `PluginRecoveryWiring` 启动阶段用 durable handle 投影重建每个父会话→子会话的绑定（只恢复 `Active` / `CompletedAwaitingJoin`；`Abandoned`/`Retired` 墓碑不复活），模型与 Persona 仍按既定验收路径冻结，不重发任何旧命令。恢复的是 handle 的 `TargetAgent`（Host 执行 agent），不是逻辑复用地址 `Byname`：forked Engineer 子会话正是 `engineer` 与 `decision-record-readonly` 两个名字，绑错会在下一次派发时以 participant drift fail closed。
  验证：新增回归测试（同文件）`CRASH_020_restart_rebinds_the_durable_parented_child`（重启后能重新派发到固定 DevOps）、`CRASH_020_engineer_child_rebinds_its_execution_agent_not_its_byname`（Engineer 子会话按执行 agent 重建）与 `CRASH_020_abandoned_handle_tombstone_is_never_rebound`；crash-reconciliation + managed-session-lifecycle + delegation 共 408 项全绿。

- **重启后固定 DevOps 无法归位：子工作的中断运行现在会显式重置（crash-reconciliation-020 / managed-session-lifecycle-024）**：子工作 run（AgentOwnerRoot）以非 `Completed` 终态结束（进程被杀、回合取消、派发被拒）时，先前只写入 `ChatExecution.Terminal`，没人关闭该会话的 `ActiveLogicalRun`，父会话的 handle 也停在 Active。后果：道路的 DevOps 重新派发被 `ActiveRunIdentityConflict` 拒绝，manager 的 join 又等在那个永不会完成的 handle 上，状态无法归位（实机：devops 会话 08:01 启动的 run 在 08:18 记作 Cancelled，之后每次交接都被拒）。
  修复：`Composition/Durable` 新增 `DelegationProjectionBridge.settleUncompletedChildRun`，并在 `Fold` 的 ChatExecution 分支应用：子 run 以非 `Completed` 终态结束时，关闭该子会话的逻辑 run（下一次交接重新按全新 AgentOwnerRoot 生根，horizon/join 看到的是刚出生的 devops；transcript 自然保留），同时把父 handle 结算为 `Cancelled` 完成（join 得到明确结果，而不是无限等）。规范禁止自动续跑，因此重启/重做仍是 manager 的显式决定；这里只做持久记账的归位，不重发任何命令。
  验证：crash-reconciliation 与 managed-session-lifecycle 全绿，新增回归 `requirements/crash-reconciliation/tests/020.test.mjs`（中断子 run → 子权威关闭 + 父 handle 得到 `Cancelled` 完成；正常完成仍走原完成路径）；delegation 144/144；构建 176 surfaces / 831 modules linkage 通过。

- **修复配置 Predictor 后宿主内建工具的装饰抛错、会话不可用（speculative-investigation-013）**：opencode 1.18.32 交给 `tool.definition` 的定义里，内建工具（`read`/`edit`/`question`/`glob`…）的 `parameters` 是 Effect 参数 schema、`jsonSchema` 为空，provider 侧 schema 由宿主 `ToolJsonSchema.fromTool`（`src/tool/json-schema.ts`）现场渲染。原装饰要求 `parameters.properties` 存在，对这类定义直接抛 `Tool … parameters schema missing object properties`，工具解析失败，用户消息到不了 provider（实机现象：manager 续话直接不工作）。
  修复：新增 `OpenCode/Host/ToolSchemaJson.fs(i)`，用宿主同版本（`4.0.0-beta.83`）的 `effect` 调 `Schema.toJsonSchemaDocument(parameters, { additionalProperties: true })`，并复刻宿主 `normalize`/`inlineLocalReferences`/`dropDefinitionsIfResolved` 的渲染结果，按 schema 对象缓存；装饰改为发布到 JSON schema 视图（`output.jsonSchema`），`parameters`（含 Effect schema）保持原样供宿主解码参数；带 `properties` 的定义仍在原地装饰并同时发布为 JSON schema。`effect` 升为显式运行时依赖。
  验证：新增真实 Host canary（`requirements/host-boundary/tests/support/run-readonly-delegation-schema-canary.mjs` 与 `requirements/speculative-investigation/tests/013.test.mjs`）：配置 Predictor 后 wire 上参与工具带 `required: [estimated_readonly_rounds]` 与条件性 `self_note`（非参与工具零增量），内建工具原有 required 不变，协作说明送达，携带协议字段的内建调用真实执行且 wire 历史保留原调用；另与宿主 `/experimental/tool` 逐工具对拍 11 个内建工具，0 处差异。

- **只读委托升级说明（DELEGATE 12 交付项）**：旧 Strength 环境变量（`WANXIANGSHU_STRENGTH_MODE`、`WANXIANGSHU_STRENGTH_DRY_RUN_BUDGET`、`WANXIANGSHU_STRENGTH_HOST_CANARY`、`WANXIANGSHU_STRENGTH_K1_MARGIN`、`WANXIANGSHU_STRENGTH_K2_MARGIN`、`WANXIANGSHU_STRENGTH_K2_MIN_EVIDENCE`、`WANXIANGSHU_STRENGTH_CONTROL_BPS`、`WANXIANGSHU_STRENGTH_POLICY_VERSION`、`WANXIANGSHU_STRENGTH_SAVED_DEEP_*` 等）全部失效——残留旧值不阻止运行，也不启用任何功能；模型调度配置升级到协议 2（`routingProtocol = 2`、四参数 `route(role, running, previous, purpose)`、`hasTheoreticalCapacity(role, purpose)`、`predictorConfiguration()` 三态存在性查询；`readonly-delegate` 用途从 Predictor 模型池选择，角色与参与者身份不变），旧三参数配置被加载器明确拒绝并给出可操作错误，运行时不覆盖用户已有的 `wanxiangshu.mjs`；Predictor 模型配置成为唯一生产启用依据（配置存在即启用，未配置即无委托，没有独立开关、环境变量或消融选项）；旧 Strength 历史经离线脚本 `scripts/migrate-delegation-history.mjs` 在 EventStore 备份副本上迁移（只追加 `DelegationHistoryImported` 导入事实，cold replay 对照，拒绝活库），未迁移的旧存储在集成规则入口被拒绝消费。用户升级指引落在 README「升级：只读委托（调度协议 2）」节；README 环境变量表与人工巡检文档中的旧变量示范同批清除。

- **修复 K 窗口前缀折叠在单条用户消息的 agent loop 内永不生效（context-compression-028/029）**：`XWire` 的常规 WorkMain 折叠上界原先取“请求里最后一条 `role=user` 消息”的回合。OpenCode 的 loop 把 tool result 挂在 assistant 消息上，因此一条 user 消息驱动的整段 loop 里该上界恒为 turn 0，`PrefixProbeSelection.limit = min(desired, coverage, 0) = 0` 使 `framesThroughCutoff` 恒为空集 → `NoCoverage` → 阶段窗口虽已提交、coverage 也追平，前缀却永不折叠，raw 历史无界增长（实机：manager 会话 15+ 次 assume 提交、coverage 29、窗口 desire 27，`PrefixRebaseCommitted` 0 次，而所有带第二条 user 消息的会话均按 Bⱼ 正常折叠）。
  修复：新增 `XTraceProjection.frontierTurn`（当前 generation 最新语义回合，即本请求正在回答的消息），`XWire.requestStartCutoff` 改由它推导；折叠上界不再与“是否来了新用户消息”耦合，loop 内的 phase-boundary probe 与既有 CTX-011/012 提升路径一致生效。删除失效的 retry 位置回退与 `ProviderWireCapture.trySemanticTurnOfHostMessageId` 在 Wire 的调用点。
  验证：`requirements/context-compression/tests/028.test.mjs` 新增生产级回归（真实 `XWire.applyTransform` + K=2 窗口 + loop 形态请求：修复前 `NoProbeReason` 让计划退化为 `UseCommittedEpoch`，修复后冻结 `UsePrefixProbe` 且 cutoff = B₁，并把被覆盖前缀换成 LWR memory、Opening 与窗口内回合保持 raw）；context-compression 278、prefix-stability 71、host-boundary + provider-attempt-recovery 321 全绿；对该仓库真实 journal 离线重放，manager 会话的 loop 内请求现取 cutoff 27。

- **构建系统增量编译、签名风险检测与模式判定统一（修复与优化）**：
  - **签名风险检测与注释剥离**：签名风险判定改为先严格剥离 F# 注释与字符串（覆盖嵌套块注释与多行/转义字符串）再进行模式扫描，彻底消除注释/字符串中 `[<Literal>]`、`let inline` 等字样导致的误升级（实锤文件改动实测保持 focused 模式）；同时补齐复合属性中任意位置的 Literal 以及全限定名 `Microsoft.FSharp.Core.Literal` 的精确识别。
  - **未归属源文件守卫**：`planImpactCompile` 与 `planImpactFromInventory` 对未归属 `.fs` 改动统一抛出明确的覆盖失配异常（`coverage mismatch unassigned=[...]`），废除原静默 full 兜底。
  - **构建跟踪输入补齐**：将 `compile-order.txt`、根 `global.json`、`scripts/lib/compile-shards.mjs` 及 `scripts/lib/build-state.mjs` 纳入追踪输入；`compile-order.txt` 变更判定为工具链/项目结构变更并触发 full 构建。
  - **增量编译暂存（staging）隔离与导入重定向**：`compileIncremental` 产物先写入独立暂存目录，成功后原子同步至 `dist/`，编译失败不污染输出目录；同步前对逃逸出暂存目录且物理目标落在输出目录内的相对导入执行相对路径重定向（解决跨目录产物 import 逃逸问题），越界与正常导入保持不变。
  - **Fable 缓存复用与输出隔离**：focused 编译的 flat 项目工作目录改按“不含源字节的集合指纹”确定，复用 Fable 编译缓存；编译产物输出目录仍严格按内容指纹隔离；warm 重入继续保证必定调用编译器。
  - **构建模式判定对齐与清单保护**：`scripts/build.mjs` 中 plan 与 run 共享模式判定逻辑（涵盖 no-op / focused / full / clean）；非 `.fs/.fsi` 跟踪输入或工具链变更进入 full 构建且保留 `dist/`（仅显式 `--clean` 或源图删除、重命名清空输出目录）；构建失败或中断不再预先删除旧清单，仅在全链路编译、验证与输出同步成功后才原子写入新 manifest。
  - **回归测试覆盖**：012 套件补齐签名形态独立断言、反面注释/字符串误命中、未映射源抛错、指纹稳定与隔离、staging 失败保护与相对导入重定向、以及构建模式规划与运行判定一致性等多项严格回归断言。

- **修复 Manager 退休被 road 级固定 DevOps PTY 误拦（road/incumbency 资源分层）与固定 DevOps 工作返回收束**：
  - **分层阻塞语义与生产接线**：`RetirementBlockersFor` 明确区分 road 级基础设施与 incumbency 级任期资源——通过递归会话树判定排除固定 DevOps 子会话及其递归子树的 runtime，并在 Manager runtime 上通过显式身份精确剔除 devops agent 资源，使固定 DevOps 的 PTY/agent/pending 不再计入 Manager 的 suicide blockers，同时完好保持 Engineer 等 incumbency 派生子会话与 Manager 自身任务的阻塞语义；完成 `relay-retirement` [003]/[009] 的生产接线，使生产行为与 `decideWithRoadResources` 纯函数规格完全一致。
  - **managed-session-lifecycle 新增 [025] 规约**：PTY 物理退出即清 `HostForkRuntime` 记账（`ptyRuns`/`terminalByName`）；固定 DevOps 每次工作返回（run 终态结算）时收束其 PTY（单 PTY 精准 TERM → 有界等待 → KILL → 等真实物理退出），由 DevOps 工作完成触发而非 Manager 退休触发，不误伤其他会话；[024] 崩溃恢复收束边界保持不变。
  - **PTY 收束能力接线**：新增 `PtyPort.ClosePty`、`HostForkRuntime.CloseOwnedPtys`/`DrainOwnedWork` 及可选 `drainChildPtys` 注入；`ToolRuntimeScope` 中的 `createRuntime` 完成生产接线。
  - **质量缺陷结构修复（D1-D3）**：
    - D1：将 `ToolRuntimeScopeSurface` 提取为独立文件 `ToolRuntimeScopeSurface.fs`/`.fsi`，纯强类型 F# 实现 `DummySessionHostPort`，彻底拔除核心文件中的 `emitJsExpr` raw JS 假桩，`017.test.mjs` 静态门禁自然合规；
    - D2：删除 `isDevOpsPty` 子串匹配启发式（`pty.Contains "devops"`），改由会话树所有权精确判定，彻底消除误伤含 `devops` 命名之正常任期 PTY 的风险；
    - D3：pending-runs 统计彻底废除跨字典混合减法（`PendingRunCount - devopsAgentRunsCount`），改为基于显式身份过滤的 `relevantAgents.Length` 单一真源。
  - **能力插件勘误**：`capability-enforcement-010` 插件工具常量勘误（`'sphinx'` → `'js-manager'`），严格区分插件注册面与角色权限面。
  - **真实执行验证**：`node scripts/build.mjs --clean` 通过（176 surfaces / 829 modules）；`npm run format-build-test` 全绿（format/check/build；unit 3696 passed / 0 failed / 24 skipped；integration 261 passed / 0 failed）；`relay-retirement/009`（5）、`managed-session-lifecycle/017`（4）与 `025`（3）目标用例全数通过。

- 清除 HEAD 上残留的验证红灯（测试、静态门禁与格式化），工作区回到 `format-build-test` 全绿：
  - **语义面债务清零**（`js-semantic-surface-002/004`、`verification-system-013`）：语义测试区最后 5 个深层 dist 导入全部改走已登记 surface。`capability-enforcement` 002/008/025 与 `prefix-stability` 016 的断言改由 owner 面表达：`ToolSurface` 新增评审工具目录（`reviewToolNames`/`isReviewTool`/`reviewToolPermissions`）与角色 Host 权限投影（`rolePermissionRules`）；`OfficeCapabilitySurface` 以普通对象承载 Manager 收口事实（`managerFacts`/`managerFactsAllowed`）；`JsRuntimeSurface.createApiFor` 按 JS capability label 组装绑定（未授予的写成员在 `api.js` 上直接不存在）；`PluginHooksSurface.decorateReviewToolDefinition` 走真实 `tool.definition` 装饰。`verification-system` 夹具 `plugin-fixture.mjs` 的 `injectAcceptedAssessment` 改为 `ObligationJournalSurface.grantWorkOwned`（幂等：已开道路复用 active incumbency/snapshot/authority revision，已接纳评审不重放），夹具不再自建 relay fact，20 个使用方行为不变。`OfficeCapability.permissionLabel`/`permissionOfLabel` 成为 `ToolPermission` ↔ label 的唯一映射点（此前 `OfficeCapabilitySurface` 与 `GeneratorSurface` 各存一份）。
  - **生产控制金字塔清零**：`ManagerReviewContract`、`PluginHooks`、`ToolRegistry`、`Fork/OpenCode/Tool`、`Cognition/{Runtime,Workspace}`、`Js/{ToolWorkflow,ToolsBindings}` 逐处提取具名 helper 消除 `if`/`match`/`try` 嵌套（`fsharp-control-pyramid` 36 项 → 0，基线未放宽）；`plugin-composition` 分片把 `ManagerReviewContract` 排到消费它的 `PluginHooksSurface` 之前（与 `compile-order.txt` 一致）。
  - **测试与门禁对齐**：`prefix-stability/tests/016.test.mjs` 两条用例锚定 001/007，按 `requirement-system-018` 合并进 `001.test.mjs`/`007.test.mjs` 并删除该文件；`cognitive-workspace` 的 005 用例移出 `001.test.mjs` 独立成 `005.test.mjs`；`delegation-028` adapter ratchet 307 → 312（durable spine 与 prefix 分片把 `context-prefix-epoch` 词表改为显式 `ProjectReference` 声明，WHAT 同批记账）；`structured-workflow-014` 的 statictools 分片编译项补 `ManagerReviewTools`；`host-boundary/032` 接入 integration 步序；`capability-enforcement-026` 断言接受真实英文资源文案；`context-compression-016` 的失败夹具补帧 `cutoff`，使 corrupt blob 读取路径真正被走到。
  - 全仓 `fantomas` 一次性对齐（24 个文件此前未格式化）。
  - 验证：`npm run format-build-test` 全阶段绿（unit 3690 passed / 0 failed / 24 skipped；integration 3 组绿，suites 261 + harness 265 passed / 0 failed；`check.mjs` exit 0；js-surface-manifest 175 面、js-module-linkage 828 模块）。
- relay-context-projection 修订（随本批一并落盘）：物理 transcript、durable audit 与下一迭代 provider 投影保留同一份完整历史，`ProjectionCut` 收窄为 durable retirement binding 的请求身份判定与 stale 拦截；`NarrativeTransform`/`ProjectionSurface` 与 WHAT/WHY、`relay-incumbency-010`、`relay-retirement-008`、`relay-context-projection` 001~009 同步。
- **Long Stroke release 层与治理门禁同步到「保留完整历史」合同（relay-context-projection-001）**：Manager 后继迭代不再丢弃退休流量，因此冷边界准入、剧本阶梯、oracle 计数全部重写，`verify:release` 从红转绿。
  - 冷边界准入（`cold-boundary.js` 的 `manager-loop`）：系统/工具计划不变的前提下，上一条 wire 必须是下一条的**有序前缀**，前缀内唯一容许的差异是已退休的辅助注入（Host 的 `\0\uFEFF` guidance 后缀、companion frame 的 preamble 行与其合成 ack 行），源自 context-compression-019 / GAP-022；改写、丢弃或插入历史消息仍 fail closed。历史评测保留在被比较的消息体内，不靠“truncate 后再比”。
  - harness gate case 同步（`cold-boundary-cases.mjs`）：`MANAGER_RETIRED` 现在带真实的 guidance 后缀，`MANAGER_NEXT` 是「同历史 + 去掉已退休 occurrence + 追加自己的 fresh-head prompt」；新增「丢弃已保留的 nudge / 插入合成 wake」变红用例，旧「丢 nudge 被接受」断言删除（它是旧合同的直接表达）；派生序列（forest-lib）保持旧的两交付形状，并在注释里注明后继迭代由 production 组装、不在可派生范围内。
  - 剧本与 oracle（`long-stroke.toml`、`long-stroke-oracles.mjs`、`014.test.mjs`）：Manager 会话认知工具改 `assume`（update/todos）、工具列表与 `toolsGate` 同步；`manager-reopened-loop` 文案改为现行 `runtime/manager-assess`，阶梯补到 step 3/4（fork → join → close），后继审计计数按 session 统计（`matchCount(id, sessionId)`，复用 turn family 在两个 lane 上都出现）；preflow 保留 `humanroot-*` 族各一次交付、后继迭代改由 assess-resource 族回答；已废弃的 `[[epoch]] manager-loop`（manager-loop.0 / humanroot-loop.0）声明删除，改为唯一 `manager-reopened-loop.0` 声明。
  - 已退休的 todowrite 膜 canary 收口：`assume` 是直写工具，不再有 provider-arg 重写与 after 钩子 enrichment 可供观察，故删除 wrapper 插件 `manager-tool-surface-canary-plugin.mjs`（及其在 `scenario-parallel.js` / `scenario-driver.mjs` / 剧本 `setup.magicTodoHostCanary` 的接线）与 792 行膜断言，改由 `manager-tool-surface-evidence.mjs` 用真实 wire 证据断言「`assume` 已公布、Manager 脊柱工具齐备、已退休 `todowrite` 从未出现」；host-boundary-019 保留自带的膜载体。
  - 验证：`npm run verify:release` 全阶段绿（format:check / check / build --clean / unit 3690 / integration 261 + harness 265 / **e2e Long Stroke** / package）。

- Manager 直接只读取证的窗口写回提示词（此前角色法一律禁止 Manager 亲自检视，与既有条款相互矛盾）：`role/manager` 双语在开篇与“亲自检视”一节写明唯一例外——评审未接纳前用评审专用只读工具（`js-manager`：Read/Glob/Grep）直接阅读静态快照，评审接纳即关闭，窗口外仍不亲自调查、修改或运行工作树；评审阶段“建立事实”条目补上这条直接阅读通道。`lifecycle/manager/t1-revelation` 双语同步：不再一律禁止检视，改为评审未接纳前静态快照由 Manager 亲自直接阅读，接纳后事实来自 Engineer 与 DevOps。`office-capability-007` 新增 `manager_direct_read_window_is_bounded_by_review_acceptance`，断言角色法双语同时写明只读工具与评审接纳的开闭边界；`docs/index.html` 角色表 Manager 行同步订正（读代码标为“评审前只读”，边界句去掉“不读写源码，全靠委派”）。验证：office-capability（24 passed）、provider-language、relay-assessment、participant-horizon、cognitive-environment、distribution、prefix-stability（71 passed）、action-affordance 与 capability-enforcement-025 全绿；`language-parity-gate` OK（333 资源）；`check.mjs` 本批文件零告警。

- **context-compression-028/029 落地：K=2 阶段窗口真正决定前缀 cutoff**（`PhaseWindow` 此前只有纯函数与测试，无生产消费者；默认 K 连常量都没有）。本批把窗口接到既有 freeze 机制上，不新增事件类型、不改动已提交事件的载荷：
  - 冻结默认与载体：`PhaseWindow.defaultK = 2`（owner 开启时冻结的唯一来源，`validateK` 拒绝非正数）；新增有界窗口 `PhaseWindow.PhaseCommitWindow`（`PhaseCallIds`，commit 顺序，`appendPhase` 保留最后 K 个）与 `desiredCutoffOf`（注入 `ToolCallId → turn` 映射以保持纯函数；最旧保留阶段的回合不可寻址时返回 `NoPhases`，不回退到下一个阶段）。
  - 投影接线：`AgentProjectionSet.PhaseCommits: Map<SessionId, PhaseCommitWindow>`（键为会话，与认知投影“哪个画板是当前”的 owner 键分开），由 `Composition/Durable/Fold.fs` 在**认知提交被接纳时**追加（被拒绝或重放的行不移动窗口），经 `AgentJournalPortAdapter.forWire` 进入 `WireSessionState`。
  - turn 对齐：`XTraceProjection.tryTurnOfToolCallId` 只在当前 generation 回答阶段自己的回合起点。
  - 材料子集：`BlogFrame.CutoffExclusive`（每帧自己的完整回合证明，entry 取观测的 next cutoff、squash 取被替换帧的最大值）+ `BlogProjection.framesThroughCutoff`——跨过边界的帧不用而不是截断（“必须使用可验证的完整材料子集，否则本次不前移”）。
  - 决定与证明：`PrefixProbeSelection.limit` 给出实现上界（窗口 desire、已证 coverage、request start 三者取小），`select` 改为校验**调用方实际冻结的材料边界**并分型拒绝（新增 `BeyondPhaseBoundary`、`MaterialBeyondBoundary`，删除不再可能出现的 `CoverageNotAheadOfRequest`）；证明改为在 Companion 自己的 cutoff 上核对 digest，快照记录材料边界的 digest——两者不再是同一个数。`ProbeBound.PhaseBoundary | CoverageOnly` 把常规窗口推进与 WHAT-029 的失败恢复例外显式分开（后者按已证 coverage 折叠，可越过窗口）。
  - 生产接线：`Wire.planPhaseBoundaryPrefix` 在**常规 WorkMain**请求上按窗口 desire 建 probe（`Wire.candidate` 从覆盖前沿改为窗口+子集），用该请求在 ChatExecution 投影里记录的真实 `PromptOrigin` 冻结 attempt plan，并只在本 attempt 成功时由既有 `PrefixRebaseCommitted` 提升 epoch；阶段内部窗口不变，故同一阶段内两次请求投影同一份 committed prefix（freeze 不变）。写回把窗口内 assume 回合作为 `retainedAssumeCallIds` 传入，紧急 probe 越过窗口时活动画板不会被摘要替换。
  - 测试：`028.test.mjs` 增补默认 K、窗口等价性（`min(N,K)` 截断后与全历史取同一 `Bj`）、按 commit 顺序有界、无地址阶段不给 cutoff；新增 `029.test.mjs`（窗口边界限幅、材料越过窗口/请求上界拒绝、紧急例外可越过窗口、不回退已提交 cutoff、前沿与材料两个 digest 的分工、失配 fail-closed）；`016`/`018` 与 `host-boundary 008/020/021`、`provider-attempt-recovery 011` 的夹具按新证明契约更新（材料边界与 digests）。验证：以上五包 `694 passed / 0 failed`，`node scripts/build.mjs` 绿（174 surfaces / 827 modules）。
  - 已知未验证：常规 arm 的真实 provider wire 证据（方案 P7 的退出条件，需要 long-stroke/e2e）；聚合 fold → 窗口的投影级单测因 `js-boundary` 债务门禁不允许测试直接 import `dist/Composition/Durable/*.js`，改由编译与代码路径覆盖。

- 文档对齐（README 与万象术投资材料按当前实现修正）：README 角色工具面表补 `js-engineer`、`js-devops` 与 Engineer 的 `bash-honeypot`，权限落点由 `Roles.permissions` 更正为 `OfficeCapability.permissions`（`src/Wanxiangshu/Foundation/OfficeCapability.fs`）；构建表述改为默认增量（no-op / focused / clean 三模式，拓扑变化或 `--clean` 时清空 `dist/` 全量重建，其余情况增量编译）；验证日志与 CI 表述对齐实际（默认写 `.fable-build/verify-logs/`，被 .gitignore 忽略；`.github/workflows/ci.yml` 运行 `verify:release`，无 artifact 上传步骤）。万象体系两份投资材料同步补充 Orchestrator 与角色订正。
- README 订正两处滞后表述并补一处仓库结构：角色工具面表移除 DevOps 的 `sphinx`（`OfficeCapability.permissions` 的 DevOps 集合为 Read/Write/Edit/Glob/Grep/Move/Remove/Exec/Pty/Join/Horizon，无 `ToolPermission.Sphinx`，`StaticTools.permissionFor` 亦无 DevOps 单开 sphinx 的特例；Manager/Orchestrator/Engineer 的 sphinx 不变）；`WANXIANGSHU_SKIP_AUTO_INJECTED` 一行删去「`provider=cursor` 时同样跳过新注入」的失效限定（skip 逻辑忽略 providerId，cursor 模式已统一至所有 provider，已落盘历史 pair 仍 replay）；仓库结构清单补 `万象体系/`（投资人材料 DOC.html、PPT.html）一行。
- 万象体系投资人稿（`万象体系/DOC.html`、`万象体系/PPT.html`）修正四处与实现不符的表述：制度学习段（DOC）由「自动制度学习仍属研究方向」改为机制落地叙述——经验经 `celebrate` 与 `regret` 登记，有界评估决定被既有规则吸收、直接淘汰或申请成为新规则，通过准入门槛的规律才进入统一制度规则库，全程受纪律约束；附录注释（DOC）订正内部机制归类——Bookkeeper（案例维护）与 Predictor（Strength 降级指定廉价模型）属不进公开调度的内部机制，Coder / Inspector / Browser / Inquiry / Distiller 已退役；Fission 点名两处（DOC 正文与 PPT Slide 9）——工程师可现场把工作裂成并行路线，路线仍属于同一个工程责任，不因多开执行路径而分裂出多个责任主体。
- 修复 `suicide` 后旧 Manager 未被中断：`Continue` 退休保留 LogicalRun，不能据此将旧请求当作后继。Transform 以当前物理消息的正式 manager-loop gate／新 HumanRoot／已接纳的人类 continuation 识别后继；旧请求释放 exact provider step 并等待 Host interrupt 后才派发下一迭代。Host 允许仍持有旧 authority root 的退休 attempt 中断，拒绝中断不再静默吞掉；`relay-retirement-008` 覆盖旧请求清空、Host abort、下一迭代派发及不继承旧中断。
- verification-system 契约面测试行为化重写与验证闭环（行为化重写与门禁闭合）：
  - `requirements/verification-system/tests/008.test.mjs` 行为化重写：删除全部匹配源代码/编译器内部表示的脆弱断言（深层导入未注册 dist 模块、mangled 符号 `BloggerDelta_nextChunk`/`Parallel_mapBounded` 等、`surfaceOf` 反射探测及 dist 全遍历动态加载）；保留并加强行为级回归（`assertBuildFresh` 防陈旧/防损坏、manifest 损坏、产物缺失/陈旧、reverse-consumer 重编译、未跟踪源盘点失败及 release 重置），呼应重写后 WHAT [001] 新增的「测试应该测试行为而非实现，因此不要写匹配源代码的测试」；VERIFICATION-SYSTEM-008 机器载体 = `requirements/verification-system/tests/008.test.mjs`（9/9 全绿）。
  - `scripts/checks/js-module-linkage.mjs` 新增 `validateModuleLoadability`：构建期动态 import 全部生产 dist 模块（821 个），加载失败即门禁失败；run/check/runCli 同步执行静态链接与动态可加载性校验；`scripts/build.mjs:190` 相应改为 await 调用，修复原同步假设导致的 `Promise !== 0` 假阴性。
  - `scripts/lib/test-surface-scan.mjs`：移除 008.test.mjs 的 `BUILD_VERIFICATION_FILES` 豁免，使 js-boundary-gate 在零豁免下全面管辖该文件（A-D 扫描零债务）；清理 5 条指向不存在文件的幽灵条目，保留 9 条有真实理由的条目。
  - VERIFICATION-SYSTEM-004 机器载体补齐：`requirements/verification-system/tests/004.test.mjs` 新增 js-boundary-gate 受控反例回归，运行时动态拼装含 deep-dist-import 与 export-discovery 的临时夹具，断言门禁精确报告两类债务、run 退出码 1、清除后归零，补齐 [004] 对每个静态门禁具备可红性的要求（该门禁此前无反例回归）。
  - 运行器中断断言归一化与死代码清理（依据 [010]）：在 001/002/009/010.test.mjs 中删除四处机械复制的 runner 失败中断断言（五处经逐行复核实质等价，保留 005.test.mjs 正本），清理因删除伴生的未用导入与死代码夹具，明确删除冗余治理断言不属于放宽验收红线。
  - `scripts/checks/js-surface-manifest.mjs` 头部注释校准为 WHAT[js-semantic-surface-003] / WHAT[verification-system-013]，准确反映现行条款关系。
  - 验证证据：`npm run check` 18/18 通过；`build` 成功（js-surface-manifest 173 注册面闭合、js-module-linkage 821 模块链接且加载通过）；008 9/9；单元套件全绿；集成套件 3 组全绿（34 文件 242 例 + distribution + harness 265 例）。
- verification-system 规范重写与验证金字塔收敛（规范修正）：WHAT.md 重写为朴素四层金字塔（Pure laws → Temporal → Adapter → Long Stroke），删除原第 0 层静态门禁层与第 5 层 Release 层的冗余层述，层序由 scripts/verify.mjs 固定阶段调度的事实保证，并明令「测试应该测试行为而非实现」；条款标题与正文去实现细节（[002] Long Stroke、[003] 阶梯、[005] 中止、[006] 因果推进、[012] 禁止机械行数检查、[013] JS 语义边界）；VERIFICATION-SYSTEM-006 禁止退化清单整理为 14 项；VERIFICATION-SYSTEM-016 收窄为「执行前捕获输入快照与内容哈希」与「运行期输入被改写、新增或删除时立即中断并非零退出」两条核心不变量（logDirectory 与 TESTS_MJS_FILES 剥离等偶发细节移出规范，实现与测试保留不动）。影响面落点：WHY.md 增补 [012]/[015]/[016] 存在理由与 RED 不变量，scripts/verify.mjs 每个 step 后即时 diff 输入快照、扰动即中断后续阶段，看门狗语义用例自 004.test.mjs 迁至 006.test.mjs 且 tests 树旧编号清零，src 下 13 处 F# 注释条款引用更新，README 对齐四层金字塔与入口分流，requirements/INDEX.md 条款区间更正为 verification-system-001 ~ 016。
- 修复 provider 限流（429 / `Rate limit reached`）后既不重试也不报错的静默停摆：实机事故为 Manager 会话连续两步 rate limit，第二步 errored assistant message 因 formal 文本为空被 PAR-008 判为 `TurnNeedsContinuation`，而该失败 attempt 的 durable `ProviderStarted` 缺失（`Accepted ∧ ¬ProviderStarted` 形状），恢复链取不到 request kind 直接 `notifyFailure` 收摊；同时宿主精确终态投影被 `settleExactTerminal` 的 start 证据门禁拒绝，发送栅栏永不 observe，任何已授权重投都会在 `AwaitStop` 上永久等待。三处修正：① `continueProviderStart` 在 start 事实持久化失败时仍执行 `continueStartedLifecycle`，② `settleExactTerminal` 对 `ProviderFailure` 不再要求 start 证据（精确终态投影本身即 attempt-stop 观察），③ `requestKindFor` 对非 satellite session 在缺 start 事实时以 accepted evidence 的 ordinary origin 命名 kind（Blogger 仍要求 durable start）；另修 Manager `observe` 静默丢弃 `TurnNeedsContinuation` 的旁路。落点：`provider-attempt-recovery` WHAT[004]/[008]/[022]、`requirements/provider-attempt-recovery/tests/004.test.mjs`（accepted-without-start 仍命名 ordinary kind）、`008.test.mjs`（unfinished manager turn 进入 bounded Interaction Repair）、managed-chat-execution 005/006 形状断言。
- 修复 Inquiry/主会话并发流式调用多个 Inspector 时后续调用返回“未能完成”：采用延期收拢与 Transform 历史持久化替换方案。流式阶段各 `inspect` 调用瞬时登记并返回占位接受文本（避免流式阶段单项早决锁死与批次冲突）；宿主进入下一步前在 `PluginTransforms`（`experimental.chat.messages.transform`）集中提取本轮全部 charges 并单次调度 Inspector 子会话，取得权威 `WorkRecord` 与 sibling 引用持久化缓存，并在每次 Transform 时就地替换所有历史 tool 结果。端到端验证覆盖 56 步 Long Stroke G2 及全部 33 项 delegation 单元测试。
- 实机事故补证（PAR-021 残余偏差）：首败保留目标上线后，用户实测仍观察到未经连续失败的 provider 被换路。根因：`failedAttemptWasLwrRetry` 按 `(session, physicalUserMessageId)` 读 durable `ProviderRetryAttempt` 接受事实，但一个物理用户消息驱动多个 provider step（agent loop）：LWR 重试 step0 成功后，同一请求 step2 的失败仍命中该事实，被误判为「LWR 重试失败」而首次失败即定罪换路。修复：定罪需双 durable 事实——continuation 接受 AND 失败的 `ProviderRunIdentity` 正是为该请求建立 `ProviderStarted` 的 run（请求级唯一，后续 step 不覆写）；后续 step 失败走 retain 路径重新绑定＋再试 LWR。`ProviderFailureSurface.wasLwrRetryAttempt` 加 providerRun 参数并新增 `establishProviderRun` 测试 seam；落点：`requirements/provider-attempt-recovery/tests/lwr-retry-before-condemn.test.mjs` 双事实断言（pr-run-1 定罪 / pr-run-2 后续 step 不定罪）。
- 修正 provider 失败驱逐时机（PAR-021 / EMR-017）：失败恢复不再在 `Retry.attempt` 之前无条件 `markProviderFailed`，消除首次失败即全进程驱逐 provider 的偏差。首次确认失败保留原物理目标：结算以 exact witness 写入单次消费的 recovery retry 绑定，下一次 fresh admission 优先路由回该目标，并按既有 wire 机制以 LWR 替换上下文发起重投；只有失败 attempt 自身就是 `ProviderRetryAttempt` continuation（durable 接受事实，绝不按失败序号推断）时，才 poison 该 provider 并进入调度轮换。普通恢复、SyncDelegate 装饰器与恢复重入共用 `Fallback/Workflow.fs` 内同一结算；重复通知、旧回调、取消与提交未知不取得该权限。落点：`requirements/provider-attempt-recovery/tests/lwr-retry-before-condemn.test.mjs`（6/6），同步更新 WHAT/HOW/WHY 与 EMR-017。
- 操作者决定：Host 错误边界不再做失败分类（新增 EXECFAIL-009）。除 typed control（abort → `UserCancelled`、supersede → `Superseded`）外，`session.error` 与 assistant message error 一律解码为 `ProviderTransient` 进入 provider recovery；工具调用等语义错误仍由 OpenCode 在自身循环内处理，不经过本边界。封闭代数保留（provider adapter / 持久化边界 / legacy 解码仍使用），HOSTFAIL-004 的 hook 失败 fail-loud 与 HOSTFAIL-006 的预算耗尽唯一 typed terminal 不变；重试以替换上下文方式发出（wire 重建 prefix + `ProviderRetryAttempt` continuation）。`HostEventCodec` 删除 status/name 映射表与 `statusCodeOf`；落点更新：host-codec（EXECFAIL-001/008）、cancel-retry-stream（EXECFAIL-002）、provider-terminal-accounting（CHATEXEC-006）。
- 修复子会话 provider 报错直达调用方（无重试）：实际事故为上游代理返回 `{type:"upstream_error"}` 且 assistant 正文只有 reasoning（formal 文本为空）。旧路径把这类出错 attempt 判为 `TurnFailed`（无 typed provider 证据 → 策略终结），子会话于是把失败直接交回 main session。按 PAR-008 修正：`CompletedTurnClassifier.classifyErroredContent` 对 formal 文本不可用（`TerminalValidity`：空/XML-only）的出错 attempt 保留 `TurnNeedsContinuation`；`ReconcilePass.materializeFailureWitness` 仅在 `failureWitnessMintsTerminal`（confirmed provider 类，或 formal 内容可用）时铸造 `TurnFailed`。这类失败因此落入 `InteractionRepairWorkflow.repairMissingFinalReport`（idle 门控、替换失效上下文的 repair nudge），既不推进失败预算也不向调用方报终态；confirmed provider 类（transient/permanent）仍由策略决定重试/终结，fallback 行为不变。落点：`WHAT[PAR-008] an_errored_attempt_with_unusable_content_never_mints_a_provider_terminal`、`WHAT[PAR-008] RECON_formal_content_gate_is_shared_with_terminal_validity`。
- 事故：首条消息 hang。根因是 git `pre-push` 钩子进程（`resources/git/wanxiang-hook.mjs pre-push`）在 push 结束后成为孤儿（PPID 1）并卡住，但仍持有 `.git/wanxiang.lock`（`owner.json` 指向该活进程）；`ProcessEventLog.acquireStoreLock` 只在 owner pid 死亡时才判定陈旧，且以 `retries.forever` 等待，因此之后每次 durable 激活（首条消息）永久阻塞。处置：杀掉孤儿钩子进程并释放锁（本仓库实测锁1ms 可获取），git-hook 集成默认关闭（`WANXIANG_GIT_SYNC=1` 才安装），仓库内两个 `Wanxiangshu HookDispatcher` 钩子已移除；事件真值仍为 `.git/wanxiang/events/<WriterId>.ndjson`，本地追加不受影响。待办：钩子内的 converge 不得跨网络 I/O 持锁且必须有超时，`acquireStoreLock` 应有界重试并给强类型诊断。
- 修复 HostInternal 分类误把带 synthetic 回显 part 的真实用户消息（TUI @-文件提及）视为纯 Host 合成提示，导致跳过受管 admission/模型投影、`cursor/auto` 残留并使 `chat.params` 以 PROMPT-006 drift fail-closed 的 def 缺：synthetic 判定收紧为整条消息所有 part 均为 `synthetic:true` 才归 HostInternal；INTERACTION-AUTHORITY HOW 补充混存消息约定；新增 `MISC_ingress_host_synthetic_requires_fully_injected_material` 回归测试，真实 wire payload 端到端复验为 `ExternalRootIntent` 进受管路径。
- 审计并移除 `mission-relay-suicidetool` 与 `foundation-temporal` 对 `chat-execution/outcome` 的 2 条冗余跨 subsystem ProjectReference，彻底切断 `relay -> chat-execution` 与 `process -> chat-execution` 两条跨子系统边，全仓声明引用数降至 1874，全部通过独立 Fable 聚焦编译及消费者闭包验证。
- 清理 `Distillation.fs` 与 `DistillationRuntime.fs` 中未使用的 `Strength` 命名空间引入，将 `process-largegatesurface` 对 `host-digest` 的宽引用收窄为 `runtime-platform/digest`，并移除其对 `execution-session-recovery-model` 与 `process-processrequest` 的 2 条冗余跨 subsystem ProjectReference，全仓声明引用数降至 1876，全部通过独立 Fable 聚焦编译及消费者闭包验证。
- 清理 `Capture.fs`、`SyncDelegate/Runtime.fs` 与 Fork/Host 源码中未使用的 `Host.Contract`、`Strength` 与 `Strength.Prediction` 命名空间引入；系统审计并移除 `execution-fission-opencode-host`、`opencode-codec-providerprojectionsurface`、`opencode-host-requirementgrounding-runtime`、`enforcer-guidance-tip`、`interaction-authority-fold`、`opencode-host-chatadmission-transaction`、`execution-session-opencode-horizontool`、`mission-relay-reviewtool`、`mission-relay-suicidetool`、`context-trace-semantictracesurface`、`strength-opencode-settings` 与 `strength-turnevidence` 等 12 个分片中的跨 subsystem ProjectReference，全仓声明引用数降至 1878，全部通过独立 Fable 聚焦编译及消费者闭包验证。
- 审计并移除 `hostturnobservedsurface`、`host-diagnostics-runtime`、`casebook-lifecyclesurface`、`magictodosemanticsurface`、`opencode-tools-ptytool` 与 `participant-provider-attempt-planner` 中 6 条无真实符号消费的跨 subsystem ProjectReference，显式补齐真实消费声明，全仓声明引用数降至 1890，全部通过独立 Fable 聚焦编译验证。
- 审计并移除 `resources-promptsurface`、`context-companion-companionfactfold`、`context-compression-runtime-surface`、`delegation-fold`、`delegation-ledger`、`strength-persistence-durabilityport` 与 `strength-policy` 等 7 个分片中 7 条无真实符号消费的跨 subsystem ProjectReference，全仓声明引用数降至 1895，全部通过独立 Fable 聚焦编译及消费者闭包验证。
- 审计并移除 `enforcer/enforcer-codec`、`enforcer/enforcer-continuation` 与 `enforcer/repair` 中 8 条无真实源码符号消费的跨 subsystem ProjectReference，全仓声明引用数降至 1902，全部通过聚焦 Fable 编译及消费者闭包验证。
- 审计并移除 `interaction/opencode-tools-executortoolsurface`、`enforcer/enforcer-institutionallearning-fold`、`knowledge/repository-knowledge-casebook-bookkeeper` 等分片中 5 条无真实源码符号消费的跨 subsystem ProjectReference，全仓声明引用数降至 1910，全部通过聚焦 Fable 编译及消费者闭包验证。
- 审计并移除 `persistence/git-hook-sync` 对 `change/change-fact` 的冗余跨 subsystem ProjectReference，并通过聚焦 Fable 编译及消费者闭包验证。
- 审计并移除 `opencode-tools-executortoolsurface` 对 `opencode-tools-ptytool` 的冗余 ProjectReference，使其前向闭包从 144 项目缩减为 141 项目，并通过独立 Fable 聚焦编译验证。
- persistence（17分片）、provider（17分片）与 host（17分片）共计 51 个 compile shard 全部补齐显式 `<WanxiangshuSubsystem>` 与 `<WanxiangshuCompileShard>` 元数据，全仓全部 26 个子系统共 213 个编译分片达成 100% 显式归属。
- session-lifecycle 与 delegation 两个子系统剩余 35 个 compile shard 补齐显式 `<WanxiangshuSubsystem>` 与 `<WanxiangshuCompileShard>` 元数据，使这两个子系统达成 100% 显式归属，全仓共 23 个子系统实现全显式归属。
- interaction 与 context 两个子系统剩余 23 个 compile shard 补齐显式 `<WanxiangshuSubsystem>` 与 `<WanxiangshuCompileShard>` 元数据，使这两个子系统达成 100% 显式归属，全仓共 21 个子系统实现全显式归属。
- authority、relay、chat-execution 与 enforcer 四个子系统剩余 28 个 compile shard 补齐显式 `<WanxiangshuSubsystem>` 与 `<WanxiangshuCompileShard>` 元数据，使这四个子系统达成 100% 显式归属，全仓共 19 个子系统实现全显式归属。
- repository-programming、requirements、dispatch、knowledge 与 work 五个子系统剩余 19 个 compile shard 补齐显式 `<WanxiangshuSubsystem>` 与 `<WanxiangshuCompileShard>` 元数据，使这五个子系统达成 100% 显式归属，全仓共 15 个子系统实现全显式归属。
- change、strength、resources、output 与 repository-investigation 五个子系统剩余 15 个 compile shard 补齐显式 `<WanxiangshuSubsystem>` 与 `<WanxiangshuCompileShard>` 元数据，使这五个子系统达成 100% 显式归属，全仓共 10 个子系统实现全显式归属。
- participant、sphinx、process 与 verification 四个子系统剩余 compile shard 补齐显式 `<WanxiangshuSubsystem>` 与 `<WanxiangshuCompileShard>` 元数据，使这四个子系统达成 100% 显式归属。
- causal-wait、time-capability 与 host-boundary 边界验证统一迁移至 subsystem/compile-shard inventory，移除对已退役 `readCompileShardInventoryV1` helper 及其 legacy locality/kind 属性的依赖；彻底清理 `readCompileShardInventoryV1`。
- EventStore 编译边界与容量禁用检查统一迁移至 subsystem/compile-shard inventory，移除重复 fsproj readdir/XML legacy kind 字符串解析及 legacy owner 属性依赖；按 persistence/strength subsystem 与显式 compile shard 验证闭包排除、生产源码预算及禁止容量词汇。显式元数据及非契约分片、错误 subsystem 反例通过，生产工程不变。

- 增量编译证明的临时工程改用显式 subsystem／compile-shard，去掉旧 owner／locality／kind 夹具参数。保留精确影响集合、合并编译、缓存及 CLI 断言；实现错误扩入反向消费者和签名遗漏反向消费者的两个反例均被拒绝，生产 planner 与工程不变。

- 生产 compiler-boundary 证明统一读取现有 subsystem inventory，移除重复 fsproj 扫描、XML 引用解析和 GitGateway 的旧 locality 标签断言。保留 GitGateway、NodeFs／工具合同、request kind／fallback facts 的源码与依赖边界；显式元数据正例、错误 subsystem 与缺失物理 provider 反例通过，planner fixtures 和生产工程不变。

- Delegation 编译边界证明复用 subsystem/compile-shard inventory，移除旧 kind 与 owner 文件名筛选依赖，验证真实 subsystem 归属。保留 DELEG-028 明文预算、增长 ratchet、物理隔离与必要 provider 断言；显式元数据正例及错误归属、Process 依赖反例通过，生产工程与合同不变。

- Host 闭包测试复用 compile-shard/subsystem inventory，移除退役 locality/kind、数量预算与 legacy owner 文件筛选；保留真实依赖与隔离断言，覆盖显式 Host 分片和 runtime-platform 摘要归属。显式声明正例及工具能力泄漏、错误归属反例通过，生产工程与行为不变。

- 按用户裁决同步 HOST-BOUNDARY-026：独立列出消息、SDK 类型、终端合同与工具适配器，摘要原语归 runtime-platform；普通业务契约按实际知识消费窄合同，不再受两个旧名称限制。保留物理能力隔离、唯一实现和失败语义；未修改源码、工程或验证阈值，残余 Host legacy 验证缺口记入 GAP-033。

- 将既有 ToolHostCodec／ToolHostSurface 独立为 Host 工具适配分片，工具消费者不再为参数解码、schema 与注册编入信号路由和终端总线；bootstrap 显式装配工具与信号两侧。保留所有源码、公开签名、取消释放、输出截断与 aggregate 顺序，不复制物理实现。

- 终端事件合同不再传递 SDK 类型、MessagePart 或摘要实现；SessionSnapshot 显式引用唯一消息合同，诊断与消息可见性移除不使用的终端引用。补齐 signal adapter 原本漏报的 failure、chat-execution 与 RuntimePath 静态依赖，清除失效 namespace 引入；保持终端重放、精确 authority、取消与公开签名。

- 将既有 OpencodeTypes sibling 源码独立到零引用的 Host SDK 类型分片；OpenCodeContract 与模型路由直接消费它，不再为 OpencodeModel 引入终端事件和 MessagePart。保持 SDK wire 类型、模型投影、公开签名与 aggregate 顺序；既有编译边界回归拒绝两条 consumer 恢复宽 Host 引用。

- 将既有 MessagePart sibling 源码独立到零引用的 host/message contract 分片；HostMessageCodec 直接消费它，不再编入无关 SDK DTO、终端事件和摘要实现。保持消息 union、decoder 行为、全部公开签名与 aggregate 顺序；编译闭包回归明确拒绝重新引入宽 Host 引用。

- 删除无调用方的 OpenCode GitTree 适配器与 GitTreePort，保留 EventStore GitTree 和 Relay snapshot；JoinAttemptRegistry 独立为窄 delegation 分片，补齐 PluginSessionScope 的真实静态依赖，不再靠聚合编译补入 registry。

- Provider wire decoder 与 Git hook 分片直接引用 runtime-platform/digest，去掉未使用的 OpenCode 消息／事件合同闭包；保留媒体 URL 摘要、Git common-dir socket key、用户 SSH 配置、全部源码与公开签名。

- Change 事实分片直接引用 runtime-platform/digest，不再为 RuntimePath 的工作区摘要引入 OpenCode 消息／事件合同；保留 Git common-dir、XDG fallback、事实投影与全部公开签名。

- Requirement Grounding 模型与 Relay workspace snapshot 直接引用 runtime-platform/digest，去掉未使用的 OpenCode 消息／事件编译输入；保留规范材料与 package 摘要字节、Git snapshot canonical 输入和全部公开签名。

- Institutional Learning 的事实／Enhancer 分片直接引用 runtime-platform/digest，不再通过摘要取得 OpenCode 消息、事件和角色闭包；保留规则版本输入、学习 disposition 与冻结重放语义。

- 将既有 UTF-8 字符串 SHA-256 原语抽到无领域引用的 runtime-platform/digest 编译分片；Casebook 与 Sphinx 删除重复 crypto 实现，Sphinx 全部调用方迁移并删除旧摘要导出。摘要输入、canonical JSON、事件身份、salt 与 Casebook null 语义不变；二进制 SHA-1／SHA-256 不合并。真实捕获测试改为独立固定摘要，删除只测试测试内 crypto 的伪 Host 证明。

- CompletionMailbox、Change VerdictMailbox 与 HostForkJoin 的等待竞争改用 typed Choice，删除手写数字标签和无类型结果字段解码；保持单次 Promise 映射、注册顺序、drain-first 与局部中断语义。补充真实 verdict mailbox 的优先级、waiter 释放和有界 FIFO 证明，删除误称等待证明的重复 renderer／源码 token 检查。

- ToolHostSurface 的 schema 解包移回 ToolHostCodec 的 internal typed 合同，删除 Fable 私有表示探针，避免把原生 schema 的 `.value` 错解为返回值；保留私有 HostSchema 构造器。删除迎合旧解包的 mock 形状测试，改以真实 SDK validator 和旧败新胜的 literal schema 反例验证。

- Prefix Wire 的 Replica 识别与 Authority 检查改用既有 StrengthRuntime／StrengthReplicaBinding 合同，删除私有字典路径、无类型字段读取和异常吞没；Fallback Workflow 改用 SessionAssociationProjection 的类型安全查询，删除 Map 扫描与手写 union tag。两处补齐真实静态依赖，不改变重试预算、前缀选择、材料等待或 Replica 权限策略。

- CanonicalIntegrator 的 Casebook／JS transaction oracle、需求接地 glob 匹配和 repository 观察 Surface 恢复静态 typed 调用及真实编译引用，删除动态模块加载、编译器 union 布局解码与缺失模块 fallback。Generator 的既有 `typedRole` 合同公开给真实 composition consumer，不复制生成流程；接地失败词汇、观察失败不阻断已提交修改、事务 Current 与重放语义保持不变。

- RequirementGroundingTransform 与 PromptResources 恢复对既有消息投影、ProviderResources 的静态 typed 调用及真实 ProjectReference，删除动态加载、手写成功 union、空资源与静默跳过校验的 fallback。规范重放证明改为比较冻结终端结果字节，验证磁盘内容变化不会改写历史，新的内容版本仅追加；删除只匹配入口符号的伪证明。

- 修复成功 retry 的后续 tool step 因新增 Blogger coverage 再次选择 prefix probe、造成未声明冷边界的问题。候选资格读取已结算的连续失败计数，零失败保持 committed prefix 并跳过候选物化；已有 frozen plan 保持不变，新失败仍可恢复 probe。

- 修复 HostSignalBootstrap 动态查找 LoopSensor 失败时静默丢失退化保护：静态构造并装配真实 sensor，reset／observe／drop 全部使用 typed 合同，复用异常类别资源映射。真实插件回归证明 managed child 重复流触发一次物理中断，root 与 foreign session 保持豁免。

- ToolRegistry 与 PluginHooks 恢复静态 typed 工具注册、Casebook 门禁／观察捕获／工具接线，删除动态模块查找、备用权限判定和静默漏注册。JS 事务持久化能力从 PluginHostInterop factory 到 registry 全程保持 `IJsTransactionPersistence`；真实 consumer 编译补齐 ExecutorTool 已有 Distillation 依赖，不以丢失持久化或隐藏引用冒充解耦。

- Host provider 校验、chat admission 绑定／释放和证明 Surface 恢复对 `SessionExecutionBinding` 的静态 typed 调用，移除动态加载、手写 union、静默 no-op 和虚假零值；SyncDelegate 证明入口直接构造并执行真实 Inspector tool，不依赖生成 JavaScript 的构造器布局。真实 consumer 并集编译同时清除 JoinSurface 的陈旧 Manager namespace 引入，保留 exact settlement、child 复用与 bounded WorkRecord 语义。

- Fork WarmStart 与 Prefix WorkRecord 恢复静态 typed 依赖，删除动态加载、union 解码和默认成功／备用渲染路径；保留 WarmStart 查询级 fail-open、无关键词零工作、同 session Opening 排除与 frame 读取失败语义。移除只匹配源码注释的 Opening 伪证明，记录真实行为证明的覆盖边界。

- Coder WarmStart 恢复静态 typed 调用，保留查询级 fail-open，移除 adapter 动态加载、union 解码与 catch-all；Blogger context 构造归入 MainContext，删除旧 Enforcer Host 和 Recovery 副本。Coverage 出生门测试改走真实 trace fold／生产函数，补足无法映射与同 turn 推进的反例，移除虚假 precheck 证明；JoinGuard 证明 adapter 独立编译并显式声明实际依赖。

- `ToolRuntimeScope` 的 Relay 查询改用既有 typed `RoadView`，移除 JavaScript Map／union 布局探针；退休围栏以 `IncumbencyId` 存储并暴露，保持 assessment、证书三项绑定、同任期冻结与新任期清除旧围栏的语义。

- 恢复 `ToolRuntimeScope` 对 `OrchestratorHost` 的静态构造和 typed dependencies，消除 `createObj + box` 抹掉回调调用约定后触发的 `computation.then is not a function`；工作区快照保持 `WorkspaceSnapshotId`，取消与卸载直接调用真实 Host，不再动态查找模块或以默认成功掩盖缺失。

- 恢复规则书校验与 Context fact fold 的静态类型依赖，移除动态模块查找、手写 union tag 和校验缺失时的默认成功；将 Nudge、Enforcer repair、provider system transform 分成可由真实 consumer 独立编译的窄分片。
- authority gate 分离源码 subsystem 身份与 WHAT package 归属，退役 legacy 身份别名；结构测试改用唯一归属、合法增长、真实 shard DAG／subsystem SCC 和平台隔离的正反例，不再锁住迁移数量快照。
- 修复已有 execution parent binding、共享 parent cache 尚空时，真实子会话被误判为根会话并禁用 Fission 的问题。请求投影与父关系发现使用一致证据；真实 chat hook 回归同时保留根请求和 `/continue` 的 origin deny。

- 证明缺口与测试错误分离：缺少 active test/HOW proof edge 继续输出 GAP，保持 OPEN/PARTIAL，不再导致 check 或 meta-verifier 失败；已有证明的悬空引用、非法归属与真实断言失败仍严格报错。共享 proof graph 不伪造证明边。

- 删除独立 FCS 扫描器、compiler-dependent extractor/report CLI、扫描计时命令、专用 fixtures 与所有 compiler-evidence 消费分支；DSL、authority、decorator、owner-contract 检查恢复纯源码路径。187 项针对性回归通过；真实 check 越过原挂点并在证明追踪缺口处正常失败。STRUCTURED-WORKFLOW-013 的完整新合同证明继续保持 GAP-031 OPEN，不以删除代码代替证明。

- 修复 fork/resume 将正常本机派发回执误报为“不确定”：派发成功且 Submitted 已持久化即返回已承接，不等待 PhysicalAccepted 或 child completion；后续真实消息仍负责绑定 Authority Root，真正发送结果未知时保留恢复权且不重发。

- 全仓自建 FCS 扫描禁令（规范修正，取代下条旧 FCS 优化 rationale）：仓库自建 FCS（FSharp.Compiler.Service）扫描在全仓任何位置一律禁止——直接调用、wrapper/reflection/fsx 封装、typed AST / symbol / application / inferred type / source-edge 提取等任何形态，whole-tree、focused/locality、fixture、report-only、CLI、CI、prebuild、cache/snapshot/delta/reuse/externally supplied evidence 等任何执行入口，均不得作为验收证据、门禁手段或临时 report lane；Fable 内部正常编译与纯源码文本静态门禁不受影响，但不得为提取证据而加做额外/instrumented 编译。F# 执行依据为声明式 ProjectReference DAG 与精确编译闭包、sibling `.fsi` 与普通 Fable 签名/私有可见性编译 canary、已注册行为证明；C(W) 只含 direct byte-backed 的 explicit interop 与 JS/generated observations（见 VERIFICATION-SYSTEM-001；STRUCTURED-WORKFLOW-011 载有同规则并回指 001；`requirements/GAP.md` GAP-031 已同步改写为 PARTIAL 缺口记录）。下条“优化 check 的 FCS 路径”保留为历史记录，其优化思路已被本禁令取代，不得再作为批准依据；现存 FCS producer/consumer/test 及引用 FCS 证据的过期 schema、测试与文档断言尚未移除，属未解决的非合规缺口。

- 优化 check 的 FCS 路径：DSL 只提取完整 declaration/application evidence，不执行无消费者的 capability 类型递归分类；逐文件 checker 调用改为一次 implementation project check，反射属性元数据按类型复用。完整 report 保留原有分类覆盖，真实 compiler fixture 精确验证两条提取路径的证据等价。

- 执行故障链收敛为单一 `ExecutionFailureResolution`：删除可组合出非法状态的 retry/fallback/message 三轴，F# CE 每回合只返回一个互斥恢复或终结动作；provider fallback 以 exact `ProviderRunIdentity` + durable authorization 去重，managed-chat 不再成为第二 retry owner。Reconciler 的 `TurnFailed` 强制等待匹配的 typed physical witness，idle/retry 抢先到达不能裸终结；delegated completion 维持 first-proven-terminal 单次赋值。Long Strike 现连续注入两个非重试 provider failure，证明两次独立 durable recovery、第三 provider 成功、无并发 terminal exhaustion。
- Host failure presentation 明确边界：恢复期 Wanxiangshu 不额外产生 final presentation，耗尽后仅一个 typed terminal；OpenCode 1.18.29 的 post-publication plugin event 无法拦截上游原始 `session.error`，不再声称虚假 UI suppression。依赖同步至当前解析版本：`@fable-org/fable-library-js` 2.6.0、`@opencode-ai/plugin` / `opencode-ai` 1.18.29、`zod` 4.5.4；lockfile 与 Host compatibility fixture 同步。
- **修复 ProviderRetryAttempt 的 repair 抑制永久化（interaction-authority-023）**：`missing-final-report` / `interaction-repair` 对 `ProviderRetryAttempt` continuation 的抑制原先以 durable physical 身份为键（`AcceptedContinuationIds[PhysicalUserMessageId]`，全历史只增不删），一旦某条重试续提示成为该会话当前 physical，此后所有稳定但未满足 gate 的 terminal（`finish=length`、空/XML-only `stop`）都被永久压掉，idle nudge 一次不发——实机 manager 会话 00:13:01 因 1M 上下文窗口截断（`finish=length`、output=1 token、仅 reasoning）而静默停机，其后 7h17m 内 `Agent/Prompt/PluginPromptClaimed` 记录 0 条，直到人工输入“继续”。
  修复：抑制改为 attempt 维度——只有该 retry attempt 尚未终结（观测 `TurnUnknown`/`TurnInProgress` 且无 durable terminal）才抑制；稳定 terminal 观测或 exact durable `ChatExecution` Terminal 任一先到即解除，fresh `ProviderRunIdentity` 按 interaction-authority-019 重获 nudge 资格。新增纯判据 `CompletedTurnClassifier.retryContinuationSuppressesRepair` 与 exact 证据读口 `TurnObservationJournalPort.HasExecutionTerminal`，删除身份轴抑制 `isRecoveryContinue`。
  验证：`requirements/interaction-authority/tests/023.test.mjs` 新增 4 组回归（在途抑制、稳定 terminal 解除并换回 `RequestRepair`、durable terminal 独立解除、非 retry 不受影响）；interaction-authority + requirement-system 109、9 个受影响包 981、host-boundary/verification-system/managed-chat-execution/intra-participant-parallelism/prefix-stability 548 全绿。`verification-system-013` 的 2 项 semantic-debt/boundary 失败为 HEAD 既有（由已提交的 `context-compression/tests/028.test.mjs` 深度 dist 导入触发），与本提交无关。

- Manager baton/successor 模型按 clean cutover 退役，无别名与兼容路径：删除 `BatonSource`、`BatonId`、`ProjectionCutId`、`BatonEnvelope`、`ActiveSource` 与存储态 `OpenObligations`，删除 `SuccessorRequested` / `SuccessorActivated` 与 `Decision.activateSuccessor`；`Decision.openIncumbency` 不再接受 source 参数，初始开启唯一经真实权威接受后的 Plugin `BeginPhysicalProviderAttempt`，Change Host 不再伪造 `PhysicalUserMessageId`。
- 新循环语义：每一轮 Manager 都在共享工作区上从权威用户消息重新开始并独立评估，评审后指派的修复由本轮承担，完成后清理资源并退出；是否开启下一轮只由系统裁决 `RetirementOutcome = Continue | Accepted of QualityCertificateId`，`Accepted` 提供证书绑定的候选接受、发布成功则退出，若 Change 准入因快照/rebase/CAS 现实变化使证书失效则以另一轮普通独立迭代继续。`ProjectionCut = { ProviderRunId; ToolCallId }` 精确绑定已退休 provider run 与结束工具调用；`RetirementSummary = { Id; IncumbencyId; ProjectionCut; SnapshotId; AuthorityRevision; Outcome }`（其中 `SnapshotId: WorkspaceSnapshotId`）以工作区快照与权威版本绑定退休观察，拒绝陈旧 `Accepted`/`Continue` 重放。
- 上下文切段：新 active 迭代只保留类型化权威消息与本轮消息，移除所有前轮消息与仅用于唤醒循环的首个非权威用户延续，并跳过 XWire/Companion 历史投影；两种 retirement 都在 transform 边界清空退休 run 的后续请求并精确归还其 provider-step admission，`Continue` 随后自动激活下一轮，`Accepted` 只终止旧 attempt。发布成功即退出；若 Change 准入因快照/rebase/CAS 现实变化使证书失效，则以另一轮普通独立迭代继续。审计保留全量历史，证书、CAS 发布与退休资源围栏保持不变；推进提醒去重归 durable `PromptAuthority` 门控，`ExitRequiredNudgeScheduled` 重复 Relay 状态已删除。
- 配套改名：`RelaySuccessorGate` → `ManagerLoopGate`（kind 前缀 `manager-loop:`），`RequestSuccessor` → `ContinueLoop : ManagerJobId -> Task<Result<IncumbencyId,string>>`（去掉 reason 与冗余 WorktreePath），`runtime/relay-successor` 与 `runtime/relay-exit-required` 由 `runtime/manager-assess`、`runtime/manager-work`、`runtime/manager-finish` 取代，无 `runtime/manager-loop` 资源，循环唤醒与 `AuditPending` 提醒均复用规范 `runtime/manager-assess`；公开文档不再教授接力棒或合成交接，`requirements/GAP.md` 移除已过时的后继准入 GAP-033。

## 0.9.0

- JS capability-projected 编辑面升级为渐进式双层协议：
  - 新增默认 `edit(path, changes)`，用 `{ find, put, all? }` 覆盖精确替换、插入、删除、全匹配与同文件批量修改；所有 change 基于同一不可变快照规划并至多暂存一个 Rewrite，既有 `rewrite(path, newText)` 继续作为完整文件计算与结构重组的无上限逃生舱。
  - 新增 `INVALID_EDIT`、`EDIT_NOT_FOUND`、`EDIT_AMBIGUOUS`、`EDIT_OVERLAP` 稳定失败码；近似文本只生成有界、双语、copy-ready 诊断，绝不自动获得写权限。
  - 工具说明改为 action-first 决策阶梯与 replace / insert / delete / all 规范示例；说明、成员、示例与 runtime binding 均按实际 capability 裁剪，较弱模型不再被推荐调用不存在的方法。
  - 保持事务、ReadSet 冲突检测、CRLF、同路径单意图、跨文件全有或全无与 no-op 零写盘语义。

## 0.8.4

- Obligation & Magic Todo 强类型化与恢复去令牌化（OBL-002/004）：
  - MagicTodo checkpoint 生命周期及进度追踪实现全链路强类型化，彻底消除字符串弱类型推导。
  - 删除 `JobRecoveryAction` 控制令牌调度器；崩溃恢复流程从持久化 facts 重新进入普通 CE workflow。
  - 清理 AGENTS.md 历史义务账与旧控制流。

- Finality & Review Judgement 裁决去未决态与生命周期收口：
  - 审阅裁决（`judge`）提交流程引入强类型请求标识与去重；消除未决分支（undecided outcomes）。
  - Finality 工具支持数组提示词（array prompts），完善审阅者裁决差距（reviewer judgement gaps）。

- Degeneration Guard / Loop Detection 动态校准：
  - LoopDetector 常量解耦硬编码，转为基于构建产物动态校准分布参数。
  - LoopSensor 准确捕获并处理 reasoning 与 thinking 增量。

- Session 生命周期与 Abort / 级联中断模型精细化：
  - 会话级联中断与中止（cascading abort / InterruptAttempt）模型细化，引入 typed assistance outcomes。
  - 增强会话终止、Daemon 管理与父子会话发现（`bindManagedChild`）。
  - 内部中断后续生命周期收口。

- Host Boundary & 执行模型路由适配：
  - 修复连续 user message 之间自动插入 assistant dot message，符合 Host 对话契约。
  - 插件 Hook 增强柯里化函数与生成适配器的兼容性处理。
  - 强化物理执行绑定（execution binding）、披露类参数与会话 ID 抽取；显式 `/continue` 命令处理与抑制保持。

- Blogger / Chronicle 与借用容量管理：
  - 稳定 Blogger 飞行状态与路由默认值；重构 chronicle thought 注入。
  - 实现借用容量（borrowing capacity）与 credit source 路由管理。

- Requirement Grounding 规范接地系统：
  - 引入 APPLIES-TO 清单与规范接地上下文压缩、观测闭环。

## 0.8.3

- 依赖整备：bun-pty `^0.4.10`、gpt-tokenizer `^4.0.0`（`o200k_base` API 不变，滴定常量未漂移）、@opencode-ai/plugin `^1.18.18`、opencode-ai `1.18.18`、smol-toml `1.8.0`；删除零引用的 `eventsource`。Fable 5.13.0 / fable-library-js 2.5.1 已是最新。
- HOST-BOUNDARY-008 projection catch-up 事件驱动化：armed retry 的 bounded re-read 改由 session `message.updated` 信号唤醒（`MessageVisibilityHub`，ITimerPort deadline 仅作无信号 backstop）；消除 Fable 5.13.0 把 `Task.Delay` 编译为 fable-library-js 未导出的 `delay`、导致 dist 模块图不可加载的根因。authoritative suite 回到 0 fail。

- Managed agent 默认温度硬编码为 1.0：`chat.params` hook 在校验 observed provider 绑定的同时，对 managed agent provider request 输出投影 `temperature = 1.0`；非 managed 会话保持 untouched。

- Managed model routing 改为 `~/.config/opencode/wanxiangshu.mjs` 单一 authority：同步 `route(role, running)` 返回 `{ model, reasoning } | null`；`running` 是同一 OpenCode process 跨 root/worktree plugin instance 共享的 session×EffectiveAgent lease multiset，`null` 形成事件驱动 backpressure，不推进 provider AABB failure。
  - 文件缺失时以原子 create-if-absent 生成可编辑推荐模板；已有文件永不覆盖。模板只承载推荐七组策略，runtime 不拥有 lane / capacity / candidate 算法。
  - `opencode.json` managed agent `model` 不再参与路由，也不再要求 fast/deep 物理 model 不同；managed request 在 `chat.message` 被 lease model+variant 覆盖，`chat.params` 只验证真实 provider binding。
  - 缺 catalog 名由 `config` hook 投影到 live Host config，不再要求 `opencode.json` 手写 22 个 agent；旧名仍 fail-closed。
  - `fast-browser` / `deep-browser` 独立配置；Host `title` / `compaction` 不属于此 model-routing 合同。

- 机械检查瘦身（2026-08-15，用户要求）：删除 `kolmogorov-size` 行数 advisory（`scripts/checks/kolmogorov-size.mjs` + baseline + `kolmogorov-size-advisory.test.mjs`）与 `enforcer-cross-family-collision` A40 机械替代（gate + GD-010 条款 + 本体测试）。
  - 行数从此不做任何机械检查（VERIFICATION-SYSTEM-012 更新：非门禁且无 advisory）；检测语料可区分性归 review 判断（A40 人类 tournament）。
  - VERIFICATION-SYSTEM-012 机器载体 = `requirements/verification-system/tests/012.test.mjs`（结构性 absence：本包 tests 与 scripts/checks 无行数检查指纹）。
  - check.mjs wired gate 20→18；proof-ladder 下限同步下调；e2e/support 13 处 advisory 注释清理；verification-system 四文档与 guidance-delivery 三文档同步。

- 结构重排第一轮（平衡树式旋转，2026-08-14）：`Application/Reconciliation` 拆散归各语义 owner，
  `Journal` 掏空为持久化基板；此后 **namespace = dir** 为仓库规则。
  - `Composition/Turn/`（ReconciledTurn→Observation、TurnBinding→Binding、Reconciler→Scheduler、
    ReconcileSupervisor→Supervisor、TurnWorkflow→Workflow + TurnReconcile/ReconcilePass/OrdinaryTurnWorkflow）、
    `Composition/Durable/`（AgentProjection→Projection、ProjectionState、ProjectionUpdate、FoldRejection、Fold 路由）、
    `Composition/Bridges/FinalityReview/`（FinalityReviewCohort 接缝显式化）。
  - 各 bounded projection/fact-fold 归家：Context/{Trace,Prefix,Companion/Blogger}、
    Interaction/{Authority,Dispatch,Repair}、Feedback/Enforcer(+Guidance)、
    Execution/{Session,Delegation,Fission}、Mission/{Manager/Life,Obligation/Todo,Review,Review/Barrier,Review/Assurance}、
    Change/Orchestration、Participant/Provider/Attempt/Fallback、OpenCode/Contract。
  - `Persistence/Journal/` 仅剩 substrate：Envelope/Codec/Writer/Boot/AgentJournal/SharedJournal/RuntimePath/FactCodec/EventStoreJournalWriter。
  - `Kernel/Fact.fs` 外层 union 与 per-family facts 拆分（第三刀）留待下一轮（wire-compat 评估后）。
  - 移动文件 namespace 跟随目录；引用按编译器驱动补 opens；测试 dist import 与 requirements 文档路径同步更新；
    `dsl-ownership` host-boundary 白名单扩展（过渡项，第二轮后移除）。

- Requirement Package cutover 收尾：`docs/`、`changes/`、`tests/` 全部腾空。
  - 45 包 normative 树 `requirements/<package>/{WHY,WHAT,HOW,PROOF}.md` 为唯一语义权威；
    旧 Clause 与变更记录已归档（2026-08-14 cutover；git 历史可回溯）。
  - 测试全部分包：`tests/unit` 146 文件 MOVE/SPLIT/DELETE 归各包 `tests/`；
    `tests/eval` → `office-capability`；`tests/integration` suites 归 owner 包；
    e2e Long Stroke、support harness、unit/integration runner 归 `verification-system/tests/`。
  - `package.json` release ladder 与新路径对齐；meta-verifier 骨架源迁入 `requirements/INDEX.md`。
  - 迁移 ratchet 退休：`g4r-freeze`、`student-teacher-absence`、`enforcer-rulebook-gate`（retired stub）。

## 0.8.2

- Provider Surface Grand Repair：ARCH-017 Office Capability；PROMPT-020 Tool Affordance；PROMPT-021 Critical Semantic Redundancy；ARCH-016 Gate F。Role Law 教身份，Tool Law 教动作，Delegation Law 教他人能成为什么。
- HOST-013 ordinary renderer：OpenCode Host 不再写 pending FakeReq。每个 occurrence 在 ResultGap 渲染一条 completed `auto-injected` tool part，由 `toModelMessagesEffect` 展开为 provider tool-call + tool-result，消除伪中断文案。
- 持久化、Git 与 session 工作流统一采用异步 Task 调用链，减少 Node 事件循环中的同步等待；GitGateway、EventStore、AgentJournal 与 blob 路径完成贯通。
- SyncDelegate 语义批处理、WorkRecord/Lifecycle 物化、HostFork/Join/Recovery/Enforcer/Finality/Manager/Review 的 durable 顺序进一步收口。
- 发布 Fork `attach`、Horizon 最新子 Agent 工作摘要，以及 Magic Todo / dedicated reviewer 的 obligation 与 assignment 改进；包入口与 durable store schema version 不变。

## 0.8.1

- REVIEW-003 skeptical challenge 迁入 `resources/provider/review/challenge`；tool result / nudge / seal 跟 Reviewer session `ProviderLanguage`；英文 canonical 字节不变（`ChallengeTextVersion = 1`）。
- journal / 公开 wire 合同相对 0.8.0：无 domain protocol 破坏。

## 0.8.0

- Provider-visible prose ownership（PROMPT-019 / ARCH-016 Gate E）：进入 participant horizon 的 Class A 自然语言经 `ProviderResources` 装载、由 session `ProviderLanguage` 管辖。Gate E baseline `{}`。
- Gate C 现行面补齐：叶对 + `{{placeholder}}` 集合一致 + Role Law semantic-anchor 同 ID 双语命中（`scripts/checks/semantic-anchors.mjs`）。
- HOST-013 pair guideline 迁入 `resources/provider/host/pair-programming-guideline`；生产路径 `ProviderProse.render`，禁止 `match lang` 挑选正文。
- Role Law 是身份文本，不列工具名。REVIEW-003 challenge 仍为固定英文协议句。
- journal / 公开 wire 合同相对 0.7.0：无 domain protocol 破坏。

## 0.7.0

- Kolmogorov 所有权二级拆分（语义汇流点，非按行数切文件）：
  - LWR journal 物化 → `LifecycleWorkRecordProjection`；`XTraceCapture` 只保留 semantic capture。
  - Manager durable open / migrate / activate → `ManagerLifeWorkflow`；`ManagerNarrativeTransform` 只保留 wire 门控与 provider rewrite。
  - `PluginTransforms` 只保留 hook 顺序；Strength replay/traced → `StrengthReplay`。
  - `HostSignalBootstrap` 退回订阅/路由；政策 → `HostTurnObserver` / `HostCompactionObserver` / `HostSessionDeletion`。
  - `Reconciler.Scheduler`（coalesce/drain）与 `ReconcilePass`（causal reread/publish）分居；`ReconcileProgram` 不变。
- Wave 0–5 Kolmogorov 重构收口：`kolmogorov-size` ratchet；JsTools / ProjectionAlgebra / PluginRuntimeScope / Fold / EnforcerHost / SpikePlugin / Codec Projection / HostForkJoin / SyncDelegate 等按 owner 装箱（详见 `changes/completed/refactor.md`）。
- Strength 提案闭环；EnforcerContinuation 从 EnforcerHost 二次抽出。
- G6 Casebook / G9 Session ownership ratchet Product Exit（问卷八 kind + 接线 gate）。
- JS capability-projected tools：structured TOML result；grep/glob 能力面收紧。
- AGENT-026/027：Stealth Browser MCP Host 接线 + 内部 Semble MCP；共享 `McpLaunch` 词汇（Disabled / Fixture / Uvx），消除 dup-cases。
- 持久化写入延迟：EventStore 的 Git raw store 不再为每个对象 spawn 一次 `git`。新 `GitObjectDatabase` 直接读写 loose object（`sha1` + `zlib` + `objects/xx/yyyy`，tmp+rename），并对内容寻址的对象/tree 读取与 `mktree` 结果做实例级 memo。单事件 append 由 **24 次同步 git 子进程 / ~60ms** 降到 **2 次 / ~7.5ms**；由于 `execFileSync` 会阻塞 Node 事件循环，这段成本此前会让同一 Host 内所有 session 串行等待。oid、on-disk 布局与 `git cat-file` 可读性完全不变（`tests/integration/persist/object-identity.test.mjs` 对真实 git 二进制逐项比对）；`gc` 之后的 packed 对象仍回落 git CLI 读取。
- FALLBACK-013：Host abort/cleanup 残留（在途工具被标 `status=error` + `metadata.interrupted=true`）不再推进 A/A/B/B cursor、不消耗自动恢复预算。此前 owner 的一次 provider 失败会被记两次——一次来自它自己的失败路径，一次来自被同一次 abort 清理打断的 Companion cycle（且用 Blogger 的 `ProviderRunIdentity`，FALLBACK-003 去重无法折叠）——导致 provider 可见的 A/A/B/B 顺序取决于两次 append 的竞争，恢复可能落回刚失败的同一侧。Companion 侧仍注入一次 `# Protocol repair`，有界性由 ENFORCER-153 marker 保证；`ToolExecutionError`（无 `interrupted`）仍按 ENFORCER-065/068 推进 cursor。
- journal / 公开 wire 合同相对 0.6.0：无 domain protocol 破坏；控制流与 Host 边界所有权收紧。

## 0.6.0

- Causal CE / 时序所有权：可观察因果等待、Wait Graph、waitFact 续期归因；Reconciler 去业务轮询；Join interrupt / user-wake 收口；Diagnostic Bridge。
- Manager Finality / lifecycle：`FinalityTool`、terminal frontier、sibling steering / durable revision；PERFECT 后的收口与 rest-in-peace 路径。
- HOST-013：guideline pair 永久 append-only；prefix-cache 不变量；idle-derived continuation 资格门控（SessionQuiescenceGate）。
- Student–Teacher CE collapse：Teacher 侧单一 CE await 链；durable evidence；相关单元/回归收口。
- Projection Algebra / Glory：attempt-local PrefixProbe 与 plain-X 前缀投影迁入投影 DSL；idle / revise / MISSING_FINAL_REPORT 观察路径加固。
- Coder 工具面：`bash-honeypot` 禁未授权 shell；严禁 Coder 跑测试；PTY prompt 补齐换行。
- EXEC-028：同步 one-shot `inspector`/`coder` 返回统一为 entry-local LWR 注释（`includeOpening=false`）+ 末条 TurnFormalText，禁字段式 `work_record`；与 Join 共用 COMPANION-003 物化器。Opening 在 send 前从原始 assignment 捕获以便物化；`Completed` 无法物化非空 LWR 时 fail-closed 返回工具级 `error=`，不 soft-omit。
- LWR 段标题在 materialize 中为纯文本（`Opening task` / `Work log` / …）；`# ` 仅由 `SyntheticToml.comment` 在 wire 注入，消除 join/oneshot/finality 上的 `# # Work log`。
- Enforcer / Blogger-as-Enforcer rebase 文档收口：`how`/`shape`/`proof` 对齐 tip-v2 基线（PartOrdinal-first 多调用 tip、物理所有权轴、`§13` 证明清单）。`bounds.test.mjs` 永久回归锁定归并 size/count 越界 fail-closed（>32 calls / text >512 KiB / evidence >128 KiB）；未恢复 wire/runtime score 路径。
- 文档治理：变更单文件生命周期 `changes/{proposed,active,completed}`；条款 ID 唯一归属正式层；`PENDING.md` 收口为 COMPLETED/HISTORICAL；`AGENTS.md` 修正 architecture 文件数与 `gate:dsl-ownership --threshold=0`。
- Canary unbend：纠正迎合错误生产的声明扭曲；e2e 事件驱动等待取代固定 poll slice。
- journal / 公开 wire 合同相对 0.5.4 兼容方向：控制流、投影与 Host 不变量收紧；破坏性细节见上列条目与 `docs/`。

## 0.5.4

- AGENT-019：managed agent Host-final permission 固定 `external_directory = allow`，覆盖 Host 默认 ask，取消项目外路径的交互确认。
- DSL 全面主导化（ARCH-001 / FLOW）：门禁债 `157 → 0`。
  - 删业务 Program AST / Interpreter；Child/Session Recovery、Orchestrator/Reconcile/Join 直接 CE。
  - `Kernel/Flow` → `Kernel/Parallel`（仅 `mapBounded`）；`CycleDisposition`、`DrainWindow`、`BloggerRuntimeHost`。
  - `dsl-ownership` 契约：合法 mutable（Domain/Session/Application/Parallel）；Host 边界 `open` basename 白名单；`--threshold=0`。
- e2e 稳定性：`gitConflictProof` 挂 worktree 已存在之后；`ProcessHost.stop` 在 leak assert 前回收残留 listen 端口。
- AGENTS.md 收束为现行纪律（P0–P3 施工表退役）；`TASK.md` 作历史档案。
- 无 journal / wire 协议破坏；控制流与门禁契约收紧，产品对外协议与 0.5.3 兼容。

## 0.5.3

- No runtime protocol changes.
- Normalized source, resource, specification, test, and build layouts.
- Replaced the generated Enforcer catalog with packaged runtime data.
- Packaging now uses the repository root and includes resources directly.
- Removed migration evidence, generated conformance ledgers, and legacy gates.
- Renamed internal files and test directories without changing public behavior.

## 0.5.2 — 全 SSOT 收敛

- 收敛目标：Active 规范全部收敛。
- 规范：spec/14 Strength、spec/16 Student&Teacher、ENFORCER nudge/throttle/规则目录迁出到 `RFC/`，spec/15 仅保留 0.5.1 已交付的 Blogger 工具化子集。
- 版本：全仓文案从 `0.5.0-rc.1` / `0.5.1` 统一到 `0.5.2`。

## 0.5.1 — Blogger vertical-slice convergence (spec/15)

生产闭环 Blogger 请求形状 / 挂起 / Squash / 恢复载体（不做 Enforcer throttle、nudge、Strength、Student&Teacher）。

### Runtime authority
- 生产 `BloggerRuntimeCell`（Idle / InFlight / Parked / Disposed）
- `CurrentRequest` 与 `PendingOffer` 双槽；唯一 busy 定义 = InFlight
- 唯一入口 `BloggerCoordinator.onMainMaterial`；删除 `offerToBlogger` 旁路与 `inFlightTask` busy 权威

### Projection & commit
- 发送前冻结 typed context 并落盘 `BloggerRequestMaterialized`
- 首次 / resume / Squash 共用 `CompanionProjectionBuilder`；删除 raw TOML 抽取与 `BloggerNeedsReset`
- Squash 迁入 blog tool continuation，提交 `BlogSquashCommitted`（coverage 不变）
- 仅 `KnownCommitted` 后 Park；`KnownNotCommitted` / `CommitUnknown` 不 Park、不重问
- 统一 `BloggerCycleReceipt`（Entry|Squash）按 ProviderRun 幂等
- 一次 `RepairSpent` repair；资源上限；Main Entry 成功清 fallback

### Recovery & teardown
- crash-window recovery 挂 `EnsureRecoveryDone`；live CurrentRequest 不 stomp
- fail-closed `loadEffectiveFrames`；`CompanionIdentity.newWorkMessageId`
- Host 重建消息带 synthetic/source 标记；main dispose 清 linked Blogger waiter

### Evidence
- layer-4：`host-transform-capability-canary`（park/resume、第三 turn 单飞、materialize）
- layer-4：`companion-canary`（同 child 两轮 blog tool）
- 静态 `blogger-convergence` 防回退门禁
- 条款收敛：`COMPANION-005/008`、`CTX-006/007/012`、`ENFORCER-010`

## 0.5.0 — 正式版

- 正式发布：0.5.0（从 rc.1 收口；breaking changes 见 `0.5.0-rc.1` 条目）
- 生产可用：canary 森林 17 驱动（18 剧本）× 3 轮全绿，`test:release`（gate:static →
  build → unit → harness → P0×3）完整通过
- Review 双 PERFECT 见证（REVIEW-006/007）
- Orchestrator 恢复链（ORCH-005/006/007）：restart 后 exactly-once publish、rebase
  冲突恢复
- guard nudge seal 稳定性修复（ORCH-006/ARCH-004）：session worktree 目录绑定
- 来源解析顺序（PROMPT-004/009）、发送格式（PROMPT-006）、fire-and-forget（PROMPT-007）
- 工具权限双层 fail-closed（AGENT-007）
- 未验证条款清零（8 条批量段条款补第 1 层判据）

## 0.5.0-rc.1 — docs freeze / RC development

Breaking changes:
- All agents now require explicit `fast-*` or `deep-*` names
- Unprefixed agent names, `build`, `plan` aliases removed
- Agent-to-model bindings read exclusively from `opencode.json`
- All Wanxiangshu model environment variables removed
- No longer persists or overrides model IDs
- Provider fallback cycles A/A/B/B within budget（Cursor 无限定义；自动恢复上限默认 12 连续失败）
- Provider retry count no longer kills a Logical Run
- Blogger and Executor Agent are now internal fast/deep pairs
- Pre-0.5.0 runtime journals not supported

## 0.4.0 — 最终版

- Structured Agent Program (Flow CE, no Stage/Phase/Lease platform)
- Prompt Authority / Logical Run rules
- Companion + ActivePrefixEpoch / FrozenB cache protection
- Manager `fork-agent / join / list`; Orchestrator `fork-manager / join`
- Static role matrix with full system prompts
- Logical-Run Fallback A/A/B/B with durable retry writer
- Dual PERFECT Review with ProviderRunIdentity binding
- Process/Executor: 3× estimate, large gate, 200KB ripple-carry
- PTY via DevOps `fork-pty` only; onExit-only completion; structured signals
- Orchestrator: clean gate, worktree, serial publish lock, rebase, re-review, ff-only
- OpenCode adapter: idle/retry/deleted signal + single-flight reconcile
- Private distribution: `private: true`, provisional commercial LICENSE
