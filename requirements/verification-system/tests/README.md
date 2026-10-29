# verification-system 测试说明

编写、构建、运行或优化测试前读取 [AXIOMS.md](../AXIOMS.md)。它是本包唯一规则正文；旧 `WHAT.md` 仅作导航，现有测试锚点仍对应新文件中的同编号 SPEC。本文记录设施、证据与边界，不定义第二套测试规则。

最新N00证据为[1eb CI](../../../proposals/archive/2026-10-06/baselines/1eb-ci/vibe-fs-1eb-ci-receipt.txt)：实际merge38141ff83，format/check/build通过，原300秒真实截断，793/821排空、006/010活动、26queued、无summary，group3068 accepted=true。010只有31.581ms名义余量且未观察body/PID，016未启动；不由last verdict认定故障，也不补造0fail或工具回收。下方ccfc的pending-only是旧输入证书。M1-A将补actual worker pre-import/exit身份与CPU累计观测；不把墙钟差叫IO，不改变判决续期、预算或并发。

2026-10-06 N04-C3：正式016新增三个Darwin native integration case，先等实际Node consumer读只读member并exit73、实际进程组排空，再让action返回成功或抛原Error/null。scope必须拒绝同consumerError，或以原cause和精确两项errors聚合；每例同时验证原字节、PID ESRCH、四owner完整dev/ino/mode/size/bytes及清单恢复。gen167真实3/0；丢弃consumer失败变异0/3，null回退变异2/1，实际资源恢复先通过，生产helper已恢复原SHA256。最终固定输入的小物理和正式unit收据见[执行契约](../../../proposals/N04真实只读Fable执行契约-2026-10-05.md)。native定向运行不冒称完整integration orchestrator，默认unit的native skip不算通过；T418/T419/GAP-055及FD/backing/ABA/actualverify保持未闭合。

N00最新[ccfc CI](../../../proposals/archive/2026-10-05/baselines/ccfc-ci/receipt.txt)实际merge67c80a46，format/check/build通过；unit完整821/821、4585pass/0fail、119skip/390TODO，296.38秒wall，退出1仅pending，group2817 accepted=true、8.676ms。016的201个工具均有完整关闭/排空身份；新增三项Darwin native在此Linux unit为skip。历史[a15](../../../proposals/archive/2026-10-05/baselines/a15-ci/receipt.txt)的原300秒截断仍保留。跨输入/host没有CPU/IO因果证据；按[总计划N00](../../../proposals/TODO施工总计划-2026-10-03.md)继续诊断，不按最后判决归因或改预算/worker。本次只更新证据与计划，未改运行器或测试代码。

2026-10-05 当前冻结输入 gen131 已通过 Fable/check、前后 freshness 与正式选集：212/212 文件排空、1547 passed/0 failed、31 skipped、94 TODO，166.97s wall/422.80s test time；group64270 accepted=true、19.321ms，exit1 仅 pending。N00 非续期诊断切片已完成，保存真实测试父链、文件/进程身份及工具 spawn/exit/group-drain/monitor-close；真实关闭诊断 fd2 不改变原工具结果。诊断不改变预算、并发或续期分类，未实施快照复用优化。N05-A/T386 当前读取版本修复及 Sphinx 核心答案来源守门前置也已纳入该选集；native006 integration 严格 cleanup oracle 为3/0、5.493s，无 skip/TODO。完整 T406 Runtime/profile/公开入口与全仓新头 CI 仍待验收，详见[本批交付记录](../../../proposals/archive/2026-10-05/实际读取版本与答案来源-2026-10-05.md)。

历史33dc03ba6的CI通过format/check/build，unit仍在原300000ms截断：817/818排空，仅016活动，最后判决为non-executable selected Node，group3105 accepted=true、9.317ms，无权威汇总。见[原始记录](../../../proposals/archive/2026-10-05/baselines/vibe-fs-n00-33dc-ci.log)。该失败仍未唯一归因，不能称仅pending或拿gen131选集替代全仓通过。本机成本probe原始15/0、975.422s中有952秒实际工具区间对应OS Sleep/DarkWake；样本受睡眠污染，不用于解释Linux CI或证明优化收益。

2026-10-05 N04-B接续：Darwin的原foundation-identity工程已在四prepared owner的显式UDRO view上实际Fable/Node5/5、完整196entries，小物理8/8、轻量Fable/API33/0（2integration skip）。原owner不假认挂载inode，consumer排空后才detach，原Error/null、原目录恢复和过期receipt拒绝已证。统一gen127正式137/137排空、1041/0、19skip、72TODO，162.23s，仅pending exit1；readonly integration仍单独记账。[契约与原始记录](../../../proposals/N04真实只读Fable执行契约-2026-10-05.md)明列身份及范围。actual verify、backing/FD/ABA/native/OS与Linux/Windows不据此关闭，T418/T419保留。旧0d539 CI817/818总300s backstop及旧静默失败不抹去，新头CI另记。

readonly repository integration 必须显式给 `WXS_VERIFICATION_NUGET_PROJECT_TREE_ID`、两个 NuGet 包选择 JSON，以及 `WXS_VERIFICATION_READONLY_SDK_ARCHIVE`/`WXS_VERIFICATION_READONLY_SDK_ARCHIVE_SHA256` 的绝对路径/raw SHA256。它经原 SDK prepare 完整校验，不回退环境 SDK；normal 原工程 fixture 保持原 copy/archive 路径。挂载入口当前只支持 Darwin，其余平台在消费前拒绝；默认 unit 只跳过声明的 integration。

2026-10-05 本批状态：六 owner 局部修复及三接线切片已验收；gen116 正式 80/80、772/0、7 skip、31 TODO，进程组 accepted=true；cf6fb31e8 Linux 818/818、4292/0、104 skip、396 TODO，仅 pending exit1。实际 completed canary 及晚到 HTTP 错误回归完整 integration 21/0。最终冻结附件另列；先期 gen115 静默和原生 761/11 均保留，不用新成功替代旧失败或推断唯一原因。整体 readonly/FD/ABA/actual verify 仍未完成。

2026-10-04 最新接续为[目录所有权与生产接线](../../../proposals/archive/2026-10-04/目录所有权与生产接线-2026-10-04.md)。Archive、dotnet tools、NuGet project、Node/SDK probe、npm installation 六 owner 分别捕获私有 parent/root dev/ino，复核和清理拒绝外来 namespace；首次消费与各探针返回后复核完整工具，NuGet fresh-cache 删除前守门。真实替换、取消、失败及已发布工具证据分别记账，最终固定输入门禁见附件。下文 gen113 待验收/Archive 未施工属于历史截面：gen113 已439/0、18/18排空；cc9f44fc5 CI 已4258/0、818/818排空、103skip/404TODO。全过程 readonly/ABA/完整FD/actualverify 与 T418/T419 仍保留，后续成功不抹掉旧9909超时。

最新正式scope仍待final：gen112缺显式SDKroot的18排空436/0/7skip/2TODO（166.36s）因外借SDK反例额外skip，不替原6skip；完整选定Node22/npm11.12.1/SDKroot仍触发5003ms静默、17/18排空，不能只归因环境遗漏。npm工具归档第一正例改为有序`t.test`：真实完整工具准备完成后才发runtime叶判决，实际install及原全部assert/consumer/cleanup随后完成；held负例、强oracle与预算不动，不添加背景进展。gen113新输入结果待[验收附件](../../../proposals/archive/2026-10-04/S03源码目录身份与NuGet协议夹具-2026-10-04.md)，错误001/019名单仅setup。最后Node/npm pass不是唯一故障定位，Archive本轮未施工。

最新接手入口见[源码目录身份与NuGet协议夹具](../../../proposals/archive/2026-10-04/S03源码目录身份与NuGet协议夹具-2026-10-04.md)：source同库存foreign parent/root实际0/2红，私有dev/ino守publication/revalidate前后/dispose/catch后定向12/0；完整Git tree/.git/bytes/mode原断言不动。15个NuGet非法图只共享该组不改写的真实Git前提，各叶SDK/restore/HOME/feed/packages独立，原typed/calls断言保持。最后统一输入待附件，195→13静态Git成本不称CI因果；TOCTOU/ABA、其它archive/tools/NuGet owner、完整FD、runtime readonly与T418/T419保留。

9909 CI `37200928231`实际在300000ms physical backstop结束：817/818文件完成、1active016、0queued、无authoritative summary，不能称pending-only。最后进展是null lock framework dependency map反例test:pass，不推定其为唯一物理原因；gen110本地429/0与新输入验收各自绑定，预算/默认workers未放宽。

aac79 CI `37202396189`另为818/818、4253/0/103skip/404TODO，244.51s wall/370.10s testtime，仅pending退出1；未含当前source/NuGet共享修复，不能定位9909原因。NuGet同15叶独立/共享前提正式各15/0、23.782s/13.165s只属本机单次观测，195→13静态Git成本不作CI归因。source新增parent失配/rootmissing第三例后完整定向13/0、2.942s，原12/0为此前截面，统一输入待附件。原0/2红在revalidate断言处先失败，未执行旧dispose；误删是旧按路径删除的实现风险，修复后另正式核对dispose拒绝与foreign库存不变。

历史gen103静默失败与72e83 CI的300000ms backstop/817完成/无权威summary和活动身份保持；gen102的398/0不能替代二者。新fe9 CI `37195699694` 已818/818排空、4188 passed/0 failed/102 skipped/404 TODO，exit1 solely pending proof，仅证明新输入，不定位旧72e83。重复fixture准备只按语义等价证据评估，不删测试、调workers或扩大时限；runtime readonly/ABA/同候选actualverify和T418/T419仍未闭合。

016 最新接续[实际单项目 Fable 编译](../../../proposals/archive/2026-10-04/S03实际单项目Fable编译-2026-10-04.md)。`compileVerificationFableProject` 消费 source/SDK/tools/project 四 owner：原 artifacts 字节/mode进入自有 seed，选定 dotnet 运行原 Fable DLL、原 project 路径，MSBuild 合法生成仅在 seed，自有 JS 输出和整个 compileRoot 库存绑定四 input digest。正式轻量 adapter 与实际 SDK/Fable integration 分开记账，未结束或诊断成功不算正式通过，最终范围见验收附件。

016的namespace反例实际rename parent/root、复制完整bytes/mode后核对foreign文件保留，不用源码shape证明归属。owner拒绝失配时不能扫描/删除unknown parked原根；正常parent且root已删除可重复dispose。初次cp未保mode属于fixture setup失败，不作为产品红。新的目录检查不替其它tools/NuGet/source/archive owner发行清理保证，更不是阶段内ABA、完整FD或全过程只读证明，T418/T419保持。

该修复冻结源码的完整轻量Fable定向34/0、0skip/TODO（30top+4inventory子叶），包含四原输入变异、实际parent/root置换、missing root/replaced parent拒绝以及原取消/receipt/完整库存边界。实际SDK编译范围没有新增；该34项与统一18输入验收分开由[本批附件入口](../../../proposals/archive/2026-10-04/S03编译目录身份与清理-2026-10-04.md)记录，不能以旧29项pattern或定向绿色替代未执行的统一门禁。

轻量正式首轮 19 项为 14/5，暴露 cpSync 丢失原目录 0700 模式；修复先验全成员/字节，再仅对自有 seed 恢复捕获模式，保留完整精确库存断言。真实 integration 另加载生成 Identity/Quiescence JavaScript 的公开行为并核对四 owner 生命周期；测试 Node consumer 不等于 actualverify 工具闭包。归档保留各自原始红绿，不把 copy/chmod 的物化忠实性称为不可变保护。

实际 Fable integration 第二轮已 5/5、0 skip/TODO（约59.14s）：选定72e83 tree、Fable5.13.0、195成员完整输出，Node22实际消费SessionId、JournalRevision `7n→8n` 与Quiescence class/reflection；四输入完整库存不变、移除归档后可复核且全部ownedroot回收。第一轮0/2为夹具误拒SDK内部合法链接，不记产品红。该例没有RO或actualverify，轻量最终计数及统一输入门禁见验收附件，不能将这5项当全仓编译或整体验收。

fe9选定tree `8544d7651e99e1137495d62d424b287f9924f1fb` 的原生正式Fable integration另为5/5、49.9秒、196成员，Node22公开消费者通过。它仍`readOnlyMounted=false/actualVerifyBound=false`，身份和日志见[接续记录](../../../proposals/archive/2026-10-04/S03运行器因果输送与回收-2026-10-04.md)；不能与72e83的195成员或本批统一18门禁混成同一证书。

SDK reference packs 与工具自带 FSharp.Core 也参加实际编译；四包 NuGet graph 不是全部编译输入闭包。scope `selected-fable-project-compile` 不证明全仓工程图、RO、运行期 ABA、Git/OS 闭包或 actualverify 接线；T418/T419/GAP-055 PARTIAL 保留。下文“尚未实际 Fable”是各此前批次的证据边界，不能用来重复当前有限 owner，也不能用后批结果升级旧证明。

016 最新接续[工程 NuGet 单项目准备](../../../proposals/archive/2026-10-04/S03工程NuGet单项目准备-2026-10-04.md)。轻量用例证明输入/公开receipt、实际子进程失败与取消、非支持项目图的拒绝和资源回收，不冒充实际 NuGet restore。独立 integration 明确 `WXS_VERIFICATION_NUGET_PROJECT_TREE_ID`、`WXS_VERIFICATION_DOTNET_ROOT` 与 `WXS_VERIFICATION_NUGET_PROJECT_PACKAGES_JSON`（`id/version/archivePath/sha512`），恢复原 foundation-identity 单 net10.0/no ProjectReference 项目；原 project/props/global 字节和完整源码库存保持。最终统计以记录所链验收附件为准，未结束运行不计通过。

工程 owner 实际执行两次 restore：先派生私有 lock，再清空 packages，`--locked-mode --force` 复验同图与 lock 字节。只有私有 config/feed/cache，禁用 SDK 隐式 feeds 与 pack 下载；完整 assets/lock/cache 集合须等于四个明确选定包。raw nupkg SHA512/sidecar 与 assets.sha512、lock/metadata contentHash 分别核对，后面三者一致不等于前者。完整输出库存、source/SDK身份与公开receipt参加revalidate；dispose不删除调用方的source/SDK。源码owner另补canonical parent、包含Git metadata的普通全库存和公开receipt复核，仍只证明步骤边界。

首阶段验收必须发生在后续 effect 之前：evaluation 后复核 source/SDK；首次 restore 后完整检查输入、普通输出、feed/config 及 graph，再清空 cache 和启动 locked restore。正式首阶段反例断言调用记录仅有 `first`；外借输出、非法 asset map、越界或不一致的依赖边不得进入第二阶段。阶段/receipt 定向 28/28 通过的两包正例使用实际 Node adapter 进程，证明空缓存的两阶段协议和完整身份绑定，不等同真实 NuGet 的版本解析；版本求解仍由实际 NuGet 完成，owner 不复写 semver 模型。实际 SDK 四包 integration 与这些机械用例分开记账，最终输入范围见验收附件。

NuGet bad-graph组的共享仅限不被该组改写的真实Git源码前提，消费前后完整复核并按owner释放；会改变源码的独立反例不共享。每个叶仍启动自己的实际adapter并拥有SDK、工作根、HOME、feed/packages和原调用记录，不能跨叶复用错误图、缓存或产物。静态Git195→13不是实际CI性能证明；原图拒绝/首次失败不得启动locked restore的精确断言保持，新输入绿色只按已结束正式日志填写。

该新增scope为`selected-nuget-project-restore`，没有source已有lock、全仓项目图、实际Fable编译、Git/OS加载闭包、RO或actualverify接线结论；T418/T419与GAP-055 PARTIAL保留。下文本地工具/SDK的证明限制按各批范围阅读，工程准备不反向升级历史证据。

016 接续[本地工具恢复](../../../proposals/archive/2026-10-04/S03本地工具恢复-2026-10-04.md)：已准备 SDK 真实执行原 manifest 的 tool restore，明确包 SHA512、仅自有 feed、实际 resolver/运行 DLL 与完整库存绑定。轻量真实进程边界与实际 .NET integration 分开；后者要求显式 `WXS_VERIFICATION_DOTNET_ROOT` 及 `WXS_VERIFICATION_DOTNET_TOOLS_PACKAGES_JSON`（`id/version/archivePath/sha512` 数组），不会默认借用户缓存。它不证明工程 NuGet 图、Fable 编译、OS加载边界、只读执行或 actual verify，原两 TODO 保留。

Node/npm 回归按实际声明的 transitive 依赖选缺库叶，不将某个 npm 版本的包名硬编码为通用结构。完整工具归档正例固定0775成员，真实安装在 umask002 下执行并恢复原mask，完整产物模式与库存必须保持；不以 portable 归整模式后放宽摘要相等取绿。远端 Linux 原失败、本地明确 npm10/11 的证明层级与最终 CI 状态见[aaa记录](../../../proposals/archive/2026-10-04/Upstream增量-aaa123b12-2026-10-04.md)。

016 增加选定 SDK 的正式轻量用例与显式 integration 入口；完整归档及原 global.json 驱动实际版本、SDK/runtime 路径、库存与回收断言。integration 要求明确 `WXS_VERIFICATION_DOTNET_ROOT`，缺少该选择不默认跳过；默认层只跳过 integration，外借实际 SDK 反例另外显式披露工具条件。实际计数和证明范围见[SDK准备记录](../../../proposals/archive/2026-10-04/S03选定SDK准备-2026-10-04.md)。不证明 NuGet、实际 Fable、只读执行或实际 verify；原两个 TODO 保留。

[测试规则](../AXIOMS.md) 定义必要边界及必须统一的方法；本说明解释当前测试，不另立规则。2026-09-28用户确认008按命题要求判断断言完整性，并在成本核查后采用016固定隔离快照方向；016运行器改造与证明仍未完成。测试通过不表示全仓已满足每项证明义务。

## [006] 的测试用例与证明范围

2026-10-06 F0以真正native ps snapshot/error取得两业务红：公开caller丢原Error。现保原对象到failure.cause，定向3/3、相邻15/15，gen190完整006为52pass/0fail、无skip/TODO。两原红资源本已自然排空，不能称owned reclaim修复；仅显式inspection seam增加initial observation，默认回收/预算/worker/verdict不变。[源收据](../../../proposals/archive/2026-10-06/sphinx-host-owner/source-receipt.txt)。

同输入正式016为285/0/20skip/2TODO、156.23s wall、201actual工具各有完整终止/排空/monitor-close。四预取消Error/null用例改用真实零请求、缺输入、未分配parent和精确库存的轻量fixture，删除三次无用完整npm准备；其它原oracle和生产取消检查保持。只证明此有限减负和原边界，不声明Linux/CI savings或只读候选全链。[016收据](../../../proposals/archive/2026-10-06/sphinx-host-owner/vibe-fs-016-preaborted-gen190-receipt-20261006.txt)。

| 目标与观察 | 用例证明 |
|---|---|
| 首次进展前没有活动 | 从监测开始计时，达到静默限值后诊断并结束 |
| 套件完成判定 | 当前运行器的 pass、fail、complete 续期；失败判定也是执行进展，不改变失败结论 |
| 挂起用例持续打印 | stdout、stderr、diagnostic 不续期；记录所有背景活动不是规范义务 |
| 同一目标的新观察 | pending 到 accepted 的样本续期，重复 accepted 不续期；不代表真实业务接线已全部核实 |
| 持续推进与窗口变更 | 总时长可超过静默窗口；调整窗口不能重置最近进展时间；停止后不复活 |
| 诊断不可得 | 缺少、损坏快照以及采集异常、卡住均披露原因；采集有界，诊断先于退出 |
| 单次终止 | 无收集器、采集成功、采集异常、采集挂起及诊断输出抛错均只终止一次；终止后晚到进展和窗口调整不能重启监测，虚拟时间推进覆盖显式配置的静默窗口 |

`006.test.mjs` 使用 `support/watchdog-harness.mjs` 注入真实共享虚拟时间端口。`support/verdict-feed.mjs` 分类运行器事件，`e2e/support/causal-observation.js` 去重观察。事件名、状态值及辅助函数是当前设施，不是唯一允许的形式；去重器不判断业务变化是否真的推进目标。

文件启动和结果流排空另报生命周期事实，用于区分尚未派发、正在运行、已有文件判决但流未排空；这些消息不续期。超时诊断列出活动文件及其最近判决，不把尚未派发的整个选集当作活动集合。watchdog 的 background 计数是本轮累计值，显示的 lane 只属于最后一条消息；Node 每个单文件运行结束都会产生汇总 diagnostic，不能由累计数推断该 lane 输出异常。

006首批真实caller反例证明：silence失败先观察inner原进程组清空，exact HOME回收后才交还失败，caller catch/finally实际执行；同步spawn抛错保留原TypeError且停watchdog，standalone inner只释放自己分配的HOME。清理失败保留原verdict与cleanup cause，null原值不被吞掉；权限反例不冒称无法诚实制造的访问拒绝。该首批只证明原组，后续独立监护批另补actual detached tool及同组后代，foreign组保持。

第二批006在同一1000ms终止deadline内冻结原组，确认该ps截面的T/t成员并捕获额外PGID，finally只kill原组，只等待观察组清空；inspect失败或残留拒绝。monitor actual工具不继承lifeline/IPC，EOF、exit/error先回收，不先等后代持管道的close；外层须收到唯一合法typed终局且monitor实际close/输出排空才settle。caller已死时没有可交回的消息，不能据此称crash、重parent或setsid逃逸已闭合。正式单项readiness前缺caught.json的尝试只记诊断失败，不当产品红。

薄owner协议正式定向8/8核对实际env、PGID、exit23、self SIGTERM、ENOENT和missing/malformed/duplicate终局；Darwin CF编码的首轮6/2是fixture误差，最终仍全字段deepEqual。016在helper修改期间的238/9/6skip/2TODO只属诊断，冻结后结果待[第二批附件](../../../proposals/archive/2026-10-04/S03独立工具进程回收-2026-10-04.md)。monitor crash而工具继续持管道时可能只有exit、尚无close；本批不称此路径已取得有界回收。

连续同步操作与已完成 Promise 可使 Node 子进程的原生 reporter 得不到事件循环调度：测试已完成，父进程却迟迟收不到判决。运行器通过专用 `verdict-transport.mjs` 的全局 `beforeEach`，在下一用例开始前让出一次调度；上一用例的真实判决只有在其 after hooks 完成后才成立。当前 hook 同时记录真实 body-start 的 entryFile、父用例全名与 pid/ppid；该观察和让步均不续期，不改变原静默窗口。006 的真实子进程反例覆盖文件总时长超过窗口但逐项完成、完成若干项后持续打印并挂起、after hook 抛错，以及持续 body-start 输出仍须超时。挂起用例不会得到完成判决，背景输出仍不能续期。

005 的插件夹具回归用子进程加载钩子观察并拒绝真实包入口：仅导入夹具或跳过 integration 测试不会初始化插件；实际创建时仍加载真实入口，导入异常不得被吞掉。夹具其它同步导出及生产模块保持原契约。

共用预算见 `e2e/support/time-budget.js`，这里不抄写数值。虚拟时间证明不能替代真实子进程的退出、泄漏和监测接线；这些 Adapter 用例在 `integration/harness/`。对全部构建和验证阶段的全程覆盖仍需补齐，特别是只设总时长上限的阶段。

ProcessHost 的 health 与项目 `/path` 各自在原阶段 deadline 内观察；同阶段的重试共用该 deadline，不续期。合法在途请求使用剩余预算，不再被 100ms 切片反复取消；只有明确尚未就绪才轮询。HTTP 非成功、非法 JSON/字段、错误目录与同一个 child 提前退出均拒绝，诊断不续期。目录比较采用 canonical workspace，避免 `/var` 与 `/private/var` 别名误拒绝。新增正式物理 harness 分别证明慢响应可成功、拒绝路径和原 deadline 仍会终止；本批完整 harness 285/285。该局部证据不宣称上轮全量项目健康失败的唯一原因已被重现，完整输入结果见[本批记录](../../../proposals/archive/2026-10-03/Host就绪与Guard替代修复-2026-10-03.md)。

`integration/harness/timeout-cases.mjs` 的waitFact用例读取当前EventStore，证明无事实与无关背景记录不续期、声明的进展可续期，并完整核对5个CandidateReady、2个Published及总计7个事实。该夹具分别限制启动和实际等待，启动通知不作为套件进展。七项协议反例覆盖未就绪退出、错误或重复通知、启动静默、监测后无判决、外部终止及后代持管道；预期拒绝之外的检查、回收或排空错误不能算通过，同组后代须实际确认清空。

原 006 的报告计数与结果流完整性用例归 021；因果前沿先于事件尾部的输出义务归 causal-wait-007。去掉装饰性标题、固定辅助入口和事件类清单的锁定，不取消诊断信息本身。原事故样本字段检查不证明挂死判据，已从 006 移除。

## 条款与证据

| 条款 | 当前证据及边界 |
|---|---|
| 001、003、019 | 层级选择、准备隔离与行为测试原则需结合人工审阅；不靠读取条文或统计断言函数证明 |
| 002、014 | 注册、场景编译及 release 控制的真实 Long Stroke；普通套件跳过真实宿主用例，不能据此宣称发布通过 |
| 004、005 | 受控门禁反例、非零失败、停止后续步骤；不是全仓每一门禁已逐一闭合 |
| 006 | 见上表；全程接线和每种业务进展的有效性仍有审阅工作 |
| 007 | 明确虚拟时间、取消与相同 trace 重放；保留既有业务时序样本。固定排列数是特定 fixture 的有限域，不是通用覆盖要求；持久化样本包含物理边界，不因此成为纯 Temporal 证明 |
| 008 | 按命题要求检查完整结果与副作用；规范要求精确结构或文本时完整比较。没有可忠实判定任意测试断言充分性的机械检查器，仍需人工审阅；不为此添加措辞扫描或恒真占位用例 |
| 009 | 真实目标路径、发现集和执行集，排除路径不存在造成的空跑 |
| 010 | 事件预算和不重试调度的局部证据；不能证明全部冻结判据从未被放宽 |
| 011 | 完整生产分母、未载入模块计零、损坏或陈旧覆盖率报告被拒绝；实际runner/c8向同父进程请求HTTP时异步等待不阻塞响应，完成报告后signal仍拒绝，silent stdin提供EOF。完整011定向18/0不是全仓验收 |
| 012、018 | 已知机械行数/FCS 扫描模式；受控正反例调用与仓库扫描相同的检查逻辑，仍不是任意动态构造的完备检测 |
| 015 | UTF-8/行边界、正文诱饵、损坏、冲突、截断、身份替换、多写者、通知和关闭；不替日志写入方证明原子追加 |
| 016 | 输入集合和步骤边界变更检测；真实 Git 夹具证明 loop-detector 的 tracked corpus 文件（含 proposals）内容和跟踪集合均进入验证身份，Git inventory 失败由生成输入与验证输入收集入口传出，验证不启动阶段。新增输入根、普通输入和corpus父目录符号链接拒绝；真实外部目标反例证明未知映射不被漏收或跟随，输出根链接和同名普通文件保留原边界。这是输入闭包的前置修复，不等价于不可变快照。“阶段内改后恢复”仍是尚未通过的可执行反例；固定隔离快照方案已选，实际隔离、准备时一致性、各阶段同源及结论绑定待证。需区别原工作区继续编辑与快照输入被改写，不能把复制耗时测量当隔离证明。55撤掉的旧Change绑定伪证明仍待真实证据替代 |
| 017 | 日常/发布顺序、范围、失败停止；调度替身通过不代表真实发布执行 |
| 020 | fast-check 固定 seed 与 run budget、失败收缩路径可重放的设施反例；不证明全仓生成器均正确配置，也不代替 oracle 独立性审阅 |
| 021 | 相同结果在不同报告模式下保持一致，跳过/TODO/取消及原因可见，同名不同用例不混淆，矛盾终局不能覆盖失败，真实文件完成和结果流排空。全仓报告链对证据范围的传播仍需核查 |

016的2026-10-04准备增量使用真实Git tree/blob/index证明指定源码身份：工作区/index后改不污染原tree，特殊路径/二进制/执行位保留，SHA1/SHA256均可重构；attributes与replace refs不改原blob，继承Git环境不重定向读取或写回，缺对象/不支持的entry/promisor仓/无法忠实物化的tree都拒绝并回收自有root。该API尚未接实际verify；源码receipt不证明运行期不可改、依赖封闭或全阶段同源，两个TODO仍保留。

原依赖归档准备回归校验明确SHA-256归档、gzip/tar完整性和独立物化目录；真实Node import与完整字节/mode/隐藏文件/内部.bin链接证明所选依赖可独立读取。外部、悬空、循环、重复、特殊entry、链接祖先及受限语法外的路径拒绝，失败不发布root。该API只绑定所选归档与lock字节，不单独证明安装来源符合lock。

2026-10-04接续[真实npm与Node工具准备](../../../proposals/archive/2026-10-04/S03真实npm与Node工具准备-2026-10-04.md)，由正式`016.test.mjs`调用实际owner与支持夹具。下列数字是Node22定向用例报告计数（含父组），不是assert调用数，也不是本批官方选集或全仓验收数量。

| 定向范围 | 已取得的证据 | 仍不证明 |
|---|---|---|
| 真实npm安装：45 passed / 0 failed | 显式Node与真实npm CLI，两个锁定registry包及独立候选内真实import；失败的lifecycle scripts被禁用，恶意HOME/NODE_OPTIONS/NODE_PATH/npm配置不进入执行；真实EINTEGRITY与缺transitive lock失败；registry origin/path、link/workspace/非法依赖及递归override预拒绝，裸本地目录或tar路径也在启动前拒绝；工具SHA/版本/packageManager不符拒绝；挂起实际tarball请求后取消、原Error/null原因保留、HTTP关闭及安装root回收 | receipt的`bootstrap-admission`只绑定启动Node和npm CLI入口准入，不冻结整个npm工具包。fixture实际npm11.18.0，不证明仓库声明11.12.1依赖已实际安装 |
| 完整selected Node/npm bundle：15 passed / 0 failed | 明确摘要归档包含完整选定Node/npm包；从独立root运行实际版本/platform/arch探针；摘要错、入口越界/链接/无执行位、缺真实npm内部模块均拒绝；ambient配置隔离；npm声明的必需生产依赖图闭合于所选npm包，direct `graceful-fs`/transitive `@gar/promise-retry`缺失时不能借父目录实际补包；实际copied CLI新增文件后，即使真实版本正确也不能发布旧成员身份；实际挂起npm探针取消保留Error/null原原因，POSIX进程组与后代退出、pipe排空及ownedroot回收，已取消调用先于缺失归档读取 | `selected-node-npm-bundle`绑定完整所选成员及实际探针；optional缺失允许，存在则递归。仍不证明任意loaded module、绝对文件读取、官方分发来源、OS动态加载库或全部外部工具闭包；Windows子树回收未证 |
| 完整工具归档安装：10 passed / 0 failed | `installVerificationDependenciesFromToolArchive`自己准备并回收完整工具，直接使用原Node/npm角色路径，保留实际Node布局；真实安装两锁定包并独立import；在ci前、依赖物化后及公开发布段完整复核工具，固定toolDigest纳入installation/dependencyDigest；held真实tarball后改非CLI库/新增成员，npm正常结束后拒绝发布并回收；actual Node/npm版本不符拒绝，Error/null取消保留原原因、PID退出及所有ownedroot回收，启动前取消先于缺失输入读取 | 新入口不升级旧raw bootstrap的45项范围；API缺失10失败是入口红，不冒称旧实现已执行工具变更的行为红。边界复核与digest绑定不证明阶段内改后恢复、不可写输入或actualverify同候选 |
| 实际application安装：4 passed / 0 failed / 0 skipped / 0 TODO（1父3叶） | 选定174c2a2533的完整Git tree/sourceDigest，完整Node22.23.3/npm11.12.1归档真实安装仓库236个锁定包；package/lock原字节、toolDigest及11693完整安装成员绑定receipt；darwin-arm64的7项optional存在、16项缺失符合平台取舍；独立候选内实际Fable List/Acorn/Tar消费者通过，全部ownedroot回收 | 本行是原生Node定向证据，不是当前合并树或全套验收；lifecycle scripts禁用，不证明native Host/lifecycle/SDK、实际Fable编译、RO、actualverify或外部Git/NuGet/OS加载闭包 |

归档实现共用`scripts/lib/verification-archive.mjs`，完整核对路径、类型、mode、字节、目录成员与内部链接。工具探针后按独立预期清单重新验证实际物理目录；去掉该复核的隔离变异使新增文件用例失败，这是oracle敏感性证据，不冒充原生产基线缺陷。复核只是步骤边界检查，输入及receipt仍可写，不证明阶段内write→restore不可发生。

早期45项默认夹具实际npm为11.18.0；完整npm11.12.1工具包另取得15项定向通过，这两组本身不证明实际仓库依赖安装，后续独立4项证明范围如上。追加Homebrew Node26的58项定向只有45通过、13失败，缺`libnode`导致真实启动拒绝，原失败保留。后续平台证据统一见[本批记录](../../../proposals/archive/2026-10-04/S03真实npm与Node工具准备-2026-10-04.md)，不从下载完成推导通过。

实际application安装支持夹具为`support/repository-npm-install-tests.mjs`，输入明确来自提交174c2a2533的tree `66fcb921999e598a4cae5904ad5f6f945723c4fb`，sourceDigest `e85e25d5…`、完整工具toolDigest `1e25872a…`；完整身份和此次结果见[590同步记录](../../../proposals/archive/2026-10-04/Upstream增量-590a3f69e-2026-10-04.md)。Fable List操作通过只是实际安装的JavaScript库消费，不是Fable编译成功；原生Node定向4项结果不冒充尚未执行的正式integration选集。

第二批安装入口与输出边界详见[工具归档安装与输出根](../../../proposals/archive/2026-10-04/S03工具归档安装与输出根-2026-10-04.md)。Mac真实只读输入内的可写输出挂载证明reset保留root、只清子项、不改输入和owned挂载回收；`compileIncremental`使用受控spawn替身，只证明输出发布清理，不能算actual Fable只读执行。upstream `590a3f69e` copy/chmod实际审核中，check读取父目录替换后的新字节，再恢复原父目录与文件inode/ctime，verify仍报告PASS、exitCode0。该反例不是TODO通过，也不由归档安装回归闭合；真正只读输入阻止替换或实际替换使运行失效才满足命题。

T418/T419与GAP-055 PARTIAL保留。工具、SDK、单项目工程与编译各有有限scope，不重做已完成owner。下一步沿编译owner核对并接入实际构建/验证阶段，再证明Git/OS、真正只读输入/可写输出、actualverify统一候选及结论绑定；多项目/多目标闭包单独扩展。准备或单项目编译的定向绿色不能替代这两条TODO。

016角色准入反例在缺归档、parent未分配时检验越界/alias/absolute/空值/非字符串先返回`verification-tool-entry-invalid`，已取消Error/null仍优先保持原原因。仅将原词法谓词移到归档读取前，不normalize或放宽角色；合法但缺失的missing.js仍需真实物化库存拒绝。定向10/0和既有完整工具父组原生15/0仍记录在[首批](../../../proposals/archive/2026-10-04/S03运行器因果输送与回收-2026-10-04.md)，不升级任意加载/OS/只读证明。第二批的四个既有取消场景先await实际settlement，再发第二SIGTERM并断言ESRCH，Error/null、fallback未执行、后代退出及ownedroot清理断言保持；cleanup失败需AggregateError保原cause。后续沿独立监护批验收→编译owner接同候选实际阶段。

009 的覆盖检查实际调用父集成入口和 distribution 子入口的 `--dry-run`，将二者公布的文件计划与独立发现的声明集核对，拒绝漏项、过时项和重复归属。正式入口共用 `support/discover-suite-tests.mjs`：只选择实际 `integrationTest` 声明，不把注释、示例字符串或单独导入当作集成用例；必需目录缺失、非目录以及源码解析错误向上报告。计划核对证明可达性，不代表这些集成用例已执行或通过。

009 的仓库封闭反例先证明合规输入通过，再分别验证未归属生产源与不合规打包白名单被拒绝。白名单直接运行 distribution-004 的同一测试，核对实际断言失败及用例计数；加载失败或未匹配到测试不能冒充违约被识别。该用例不证明真实 tarball 或独立消费者验收。

2026-09-26 既有归属迁移保持有效：原 008 的构建产物用例归 distribution-005、structured-workflow-011/012、js-semantic-surface-006；原 013 的接口用例归 js-semantic-surface-002/003，013 永久空缺。迁移范围见 [本批记录](../../../proposals/archive/2026-10-03/35模块PR施工记录-2026-09-28.md)。

[GAP](../../GAP.md) 区分实现缺陷、证明不足与人工审阅责任。[本批记录](../../../proposals/archive/2026-10-03/35模块PR施工记录-2026-09-28.md) 保存新上游基线的实际验证结果，不沿用旧版本的通过数字。

## 运行

在仓库根目录先执行 `node scripts/build.mjs`，再运行 00—02 条款测试：

```sh
TESTS_MJS_FILES="$(rg --files requirements/feature-ablation/tests requirements/requirement-system/tests requirements/verification-system/tests | rg '/[0-9]{3}\.test\.mjs$' | sort | paste -sd, -)" node requirements/verification-system/tests/run.mjs
```

运行器检查产物新鲜度并标明 scoped 范围。TODO 是待完成证据，skip 是未执行，均不是通过；TODO 使监督器返回非零退出码，阻止上层整体验收通过。本轮 016 反例尚未闭合，因此上述命令会报告已通过项后以失败退出。此命令不包含完整 integration 或真实 Long Stroke。

验证设施的物理协议回归：`node requirements/verification-system/tests/integration/harness/run.mjs`。它运行隔离的辅助进程和协议替身，不是多个真实 E2E 世界。

日常入口 `npm run format-build-test`，发布入口 `npm run verify:release`。Long Stroke 的设施见 [e2e/README](e2e/README.md)。
