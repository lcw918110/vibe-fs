# 最新上游上的测试更新

本轮以 upstream `89ab1f39960e8338535e64f17ea51516b9913c93`、wanxiangli `87de56a6c203be4a79b71cb2386244e016b1e135` 为基线，保留 PR #68 的测试规则与读取支持。工作分支为 `codex/test-updates-20261028`，不扩大 PR #68，也不合并主工作树的 master。

用户选择：先修复现有失败，再优化主要慢点。按 `verification-system/AXIOMS.md`，保留现行合同的有效保护；归属调整、过时测试修订和等覆盖提速分别记录。

随后按用户要求推送两个独立分支，并合入最新 upstream `4b099d0bb`。合并后的完整 unit 为 4174 pass、0 fail；详情见末节。下文原性能对照继续对应其原始提交，不用新版本结果覆盖。

## 基线

- `node scripts/build.mjs` 成功，150 个 JS surface、753 个产物模块链接检查通过。此前缺三个产物的失败已消失。
- 规范相关 30 项：28 通过、2 失败。失败涉及包漏登记及测试归属，不修改检查器来豁免它们。
- 完整 unit：423.103 秒，793/793 文件完成，4073 pass、81 fail；另有 skip/TODO 与 cancelled，不能称全绿。源码输入摘要前后相同。
- 完整 unit 固定 Node 22.23.4、npm 11.12.1、并发 2、静默预算 30000 ms、总后备预算 900000 ms，沿用前次实验环境。没有跳过产物新鲜度检查。
- 原始记录：主工作树 `.Herdchestra/test-optimization/20261027/upstream-20261028-before.{json,log}`。记录中的 `WXS_ACCEPT_TODO=1` 仅容许原有 TODO，不把 TODO 算通过；不用于发布验收。

## 工作项

1. 已完成：按真实行为修复包登记、编号与归属，保留有效断言；替换无合同依据的源码词形断言。
2. 已完成 unit 范围：修复新版宿主输入、已退出接口等导致的失效测试；修复真实 UTF-8 栈溢出。固定基线与优化后完整 unit 均为 4167 pass、0 fail；原有 skip/TODO 保留，未算通过。
3. 已完成一组等覆盖对照：拆开主要长尾文件中的九个独立注册组，完整 unit 从 343.35 秒降至 294.68 秒。格式、静态检查和真实宿主验收的剩余边界见下文。

## 第一批：归属与有效证据

- 登记已经存在的 `latency-degeneration` 包，补齐 WHY、索引和消融节点。登记后暴露出的混合编号一并拆分；不修改规范检查器。
- Advisor 的送达与恢复测试分别归到现行 005、007；pending run 测试归到 delegation 025、026、031；Fission 单终态归到 009，provider 恢复分类归到 008。移动保留原行为断言。
- work 的宿主测试按 003、006、008、018 分组。移除 054 中扫描内部调用拼写的重复用例，其宿主行为由原有 011、039、051 及移动后的行为测试保护；将 055 的源码词形扫描换成公开 Work 状态迁移、完整事件序列与持久化重放测试，归到 001。
- 两项宿主屏障测试用真实临时目录路径，消除 macOS `/var` 与 `/private/var` 别名造成的提前失败；保留目录约束和屏障断言。
- Latency 的共享辅助代码只提取重复的受控端口与编解码组合。它不等于真实 PluginHooks 接线证据；物理 Sender 测试单列在 `host/009`。
- 验证：官方构建 generation 5 成功；规范、消融、Latency、Advisor、移动后的 delegation/Fission/provider/work 共 143 项，141 pass、0 fail、0 cancelled、1 skip、1 TODO。原有 skip/TODO 没有改为通过。日志为 `/private/tmp/vibe-fs-test-updates-ownership-verified-20261028.log`。

本批涉及用例归属和证据替换，不能把它与原失败基线的时间差直接解释为等覆盖提速。性能实验另建修复后的固定基线。

## 第二批：现行输入、行为与资源

| 改动 | 原因与保留的保护 |
| --- | --- |
| 给有效 `chat.message` 夹具补齐宿主模型；共享 admission surface 显式接收目标 | 上游已改为使用 Host 选择。保留缺模型拒绝、同一物理输入的目标冲突、重复受理、旧租约及容量隔离。没有给产品加默认模型。 |
| 退役 MJS 调度加载、备用目标池和构建报告测试 | 用户按[处理说明](retired-tests-review.md)批准；有效容量保护移到 004/017，真实插件验证旧配置被忽略。报告工具的四项测试退出后，012 的其余编译测试从加载失败中恢复。 |
| 两个 delegation 恢复夹具先保留在途调用，观察物理接受，再等待 handler | 旧夹具先等待必须由下一步物理事件才能结束的 handler，每项耗尽约 10 秒。十种进程恢复情形均通过，每项约 0.45–0.64 秒；保留重复消息、旧调用不能结算新调用和返回前未完成断言。属于纠正因果次序，不作为独立等覆盖性能结论。 |
| Join 探针使用真实 PendingRunStore | Dictionary 已不能履行 claim/register 接口。保留实际自然完成和父级 join 保护，不修改生产委托流程。 |
| 万象理 UTF-8 尾部扫描改为迭代 | 大文本触发真实栈溢出；不缩小宿主原有大输入。新增十万字符 ASCII、中文与代理对回归，完整比较最长合法后缀和字节上限；纠正两个原本没触发截断的夹具与误把 row-10 当成 row-1 的断言。核侧提交 `9c0230b`。 |
| Plan 提示夹具使用真实自有首段 | 现行 transform 只处理自有提示。保留三个里程碑、草稿路径、幂等及替换断言，增加普通宿主提示不被改写的检查。 |
| classifier 诊断文本和工具目录更新 | abort/error 分类、错误来源及完结条件不变；固定无错误名称时的新文本。目录包含上游已经注册的 todowrite。 |

## 第三批：公开边界与检查归属

- 日志根路径、Scope 记忆和 scope-progress 改用已有公开接口；没有重建产品模型。
- 为现有 ToolRuntimeScope、Dispatch、HostSignalSubscribe 和 Planning surface 补齐原生 JS 调用；Result 转换由所属 F# 边界完成，测试仍调用真实 sender、观察实际 PhysicalAccepted，并保留未知送达、迟到拒绝、Scope 退役和资源清理检查。没有新建跨领域 facade。
- 登记 LatencyWatchdog 已有的稳定工厂与不透明句柄，不暴露内部状态表示。修正辅助代码错误读取 `case/fields`，改读 HostSignalSurface 实际返回的 `kind/sessionId`。
- 三个原始宿主边界用例登记为精确路径的 canary：delegation 037 检查投影前原始历史；managed-session 029 检查真实事件到 typed observer 的运行身份；work/host 008 检查 OpenCode 与 OMP 的实际提示屏障。相邻语义用例及辅助文件继续受检，新增子进程正反例确认没有目录级豁免。
- `durable-events-010` 允许不同职责采用不同物理格式；`advisor-007` 已明确其 SQLite owner。旧检查错误拒绝两个宿主适配器中的 `advisor-process.sqlite`。只认可这两处的该字面量，其余 SQLite、私有库、旁路写入及相邻文件仍拒绝，并用受控反例验证。不豁免整个适配器，也不改变 EventStore 折叠规则。
- 为现有可变回调、取消登记、订阅游标、试验标识和 watchdog 代次补充准确的资源用途注释；没有改弱可变状态检查器。

局部验证：公开接口与调用方 87/87 通过；检查器最后一轮 23/23 通过；durable-events 019 的 18 项通过。前面的试跑失败和修正分别保留在 `/private/tmp/vibe-fs-test-updates-*.log`。完整套件状态以随后固定输入的运行记录为准，这些局部通过不代替整套结果。

完整试跑 `contracts-20261028-before-split`（`c589d8223`）：341.540 秒，800/800 文件完成，4166 pass、1 fail、110 skip、261 TODO，输入前后稳定。唯一失败是 host-boundary 021 仍要求调用已退出的模型改写端口；改为检查现行目标比较与事务接线，并拒绝旧改写端口。对应结构测试和真实宿主模型测试 21/21 通过。此轮保留为失败记录；随后重建固定基线，不将它与原 81 项失败基线的差值算作等覆盖收益。

## 第四批：缩短整套 unit 的调度长尾

### 改动和因果依据

固定基线中，`verification-system/tests/016.test.mjs` 从文件启动到结果流排空用时 152.26 秒，是最后完成的文件。九组彼此独立的工具与输入测试都在该文件串行注册，其他文件完成后，两个 worker 不能共同推进这个长尾。

提交 `b455d8eea` 只把九个既有注册调用移到各自的 `016.test.mjs`：readonly-inputs、archive-namespace、node-tool-candidate、npm-tool-archive、npm-install-setup、dotnet-sdk、dotnet-tool-restore、nuget-project、fable-project。原文件保留其直接定义的测试和六项 integration 场景。没有改运行器、增加并发或添加缓存。

对应九个 support 模块在前后提交中字节完全一致；其测试正文、断言、样本、seed、超时、清理及组内共享准备均未改变。NuGet 的共享只读夹具及 before/after 仍属于原来的 describe，没有拆散依赖同一生命周期的场景。新入口沿用原诊断设置与层级门禁。

### 有效对照

| 项目 | 修复后基线 | 拆分后 |
| --- | --- | --- |
| 提交 | `8e80217cf76f3f1be621013898d11f65223a1e55` | `b455d8eea0111e8554a637b0cc681816843933ca` |
| 记录 ID | `contracts-20261028-fixed-baseline` | `contracts-20261028-after-split` |
| 完整进程 wall time | 343.353 秒 | 294.682 秒 |
| 文件完成数 | 800/800 | 809/809 |
| pass / fail | 4167 / 0 | 4167 / 0 |
| skip / TODO | 110 / 261 | 110 / 261 |
| 已启动用例身份多重集 | 4428 项 | 完全一致 |
| skip/TODO 身份与原因多重集 | 371 项 | 完全一致 |
| 测试正文耗时总和 | 521.07 秒 | 532.09 秒 |
| 每轮输入前后摘要 | 相同 | 相同 |
| 退出码 | 0 | 0 |

整套 unit 等待减少 **48.671 秒，14.18%**。4428 个启动身份包含 261 个以 TODO 标记执行的用例；这些 TODO 没有计入 4167 个通过项。九个新增文件仅改变调度单位，没有新增或移除场景。

前后均使用 Node 22.23.4、npm 11.12.1、Bun 1.3.14、dotnet 10.0.302、rg 15.2.0，worker 并发 2，静默预算 30000 ms、整套后备预算 900000 ms。同样关闭 integration/release 层，同样保留 `WXS_ACCEPT_TODO=1`；产物新鲜度检查正常执行。工具版本及所有已记录的运行器环境变量逐项相同。

这是同一机器上的一组有效对照。1 分钟负载采样范围前为 2.515–3.616，后为 2.062–12.232，不能据此承诺其他环境的固定收益。正文耗时总和未减少，因此本次证据支持“调度长尾缩短”，不支持“计算总量下降”或“完整发布流水线提速 14.18%”。

### 可复核记录和后续方向

- [comparison.json](comparison.json) 保存版本、环境、输入摘要、原始日志校验和、启动身份摘要、完整耗时和前后最慢的 15 个文件。
- [compare-runs.py](compare-runs.py) 校验原始日志 SHA-256，按主运行器的文件生命周期和 worker 启动记录比较身份多重集；过滤嵌套测试运行器的诊断。用例移动时不把文件路径当成用例身份，重复标题仍按出现次数比较。
- 原始 `.json` 和 `.log` 保留在 `/Users/yuanxi/Workwork/vibe-fs/.Herdchestra/test-optimization/20261027/`，记录 ID 见上表。复核命令：`python3 evidence/test-updates-20261028/compare-runs.py DATA_DIRECTORY contracts-20261028-fixed-baseline contracts-20261028-after-split OUTPUT_JSON`。
- 前后九个 support 文件可直接用 `git diff 8e80217cf b455d8eea -- requirements/verification-system/tests/support` 复核，输出为空；该性能提交的完整 diff 只包含注册入口调整。

优化后最慢文件为 NuGet project 38.15 秒、npm tool archive 36.14 秒、verification 006 28.40 秒、Node tool candidate 23.75 秒、Fable project 19.60 秒、dotnet tool restore 17.60 秒。后续应先测这些组的准备与正文成本，区分可复用的只读准备、真实进程开销和合同要求的等待；不要仅为继续拆文件而重复昂贵准备。若调整资源共享，须另证隔离与失效条件，再做同范围完整对照。本轮没有实施这些后续策略。

## 性能实验后的收尾验证（同步前）

性能对照后，补齐 `latency-degeneration` 的 53 个消融配置项：station-05 至 station-56 为 ablated，production 为 active，与 station-60 引入该节点的登记一致。修正本次新增接口的格式，不改变接口或执行逻辑。这些收尾改动不混入上表的性能提交。

最终状态、相关原始日志的路径与校验和见 [delivery-validation.json](delivery-validation.json)。

- 最终官方 Fable 构建 generation 11 成功；151 个公开 JS surface 和 753 个产物模块链接检查通过。
- 收尾后按影响范围复测 11 个文件：42 pass、0 fail、1 个原有 skip、0 TODO，输入摘要前后相同。记录 ID 为 `contracts-20261028-delivery-targeted`。没有因纯格式及配置补全再重复整套性能实验。
- 消融配置门禁通过。先前的 53 项缺失确为本次登记遗漏，已修复；没有修改门禁来豁免它们。
- 官方完整入口 `node scripts/verify.mjs` 在 format:check 阶段失败，后续阶段在该次流水线中未运行。最终仍有 107 个文件需要格式调整：99 个文件与上游相同；另外 8 个本轮触及的文件，其上游原文在相同配置下也未通过。新引入的 5 个文件格式问题已逐个手工修正并通过检查。完整格式日志为 `.fable-build/verify-logs/2026-10-28T10-41-14-226Z/format-check.log`。
- 独立静态检查仍有 118 项控制结构嵌套问题，集中于 `fsharp-control-pyramid`。忽略行号移动后，与上游逐项相同；检查实现未改动。详见 [control-pyramid-comparison.json](control-pyramid-comparison.json)。这属于未修复的上游问题，不能写成检查通过。

本轮没有重新执行 integration/release 或连续真实宿主验收，也没有清偿既有 110 skip、261 TODO。原失败基线的 81 项失败已不再出现，但 423.10 秒到 294.68 秒之间包含合同迁移、回归修复和执行范围变化，不能整体算作等覆盖收益。

## 同步最新 upstream 后的验证

2026-10-28 将主仓 `752c36d4c` 与内核 `9c0230b` 推送到各自 fork 的 `codex/test-updates-20261028`。随后获取 upstream：主仓从 `89ab1f399` 前进到 `4b099d0bb68d92e8c2a437d7c11909b548725e64`，新增五个提交；内核 main 仍为 `87de56a6`。采用 merge 保留已发布提交和固定性能对照的引用，合并提交为 `66f92bfdd`，没有冲突。PR 提交前再次读取远程，主仓 upstream 仍为上述版本。

新增上游行为包括：空回合和长程角色的迟到咨询处理、OpenCode step 标记的分类、horizon 改读 join 的账本，以及根身份的完整闭合分类。`scripts/lib/test-surface-scan.mjs` 自动合并后已检查：上游删除的 HorizonSurface 登记继续删除，本轮的 LatencyWatchdog 登记仍保留。

合并后发现 `interaction-authority/018` 的新增分类测试直接导入内部 `Origin.js`，并依赖联合类型表示，造成两条 JS 边界报错。提交 `97c6f6462` 把解码留在已有 `RuntimeSurface`，返回 JS 原生对象并调用上游真正的 `closureKindForRoot`。七种分类期望完整保留，另检查非法根身份拒绝；没有改检查器或增加豁免。

| 验证范围 | 结果 |
| --- | --- |
| 官方 Fable 构建 | generation 13 成功；150 个公开 surface、752 个产物模块检查通过 |
| 上游改动与公开边界相关的 9 个文件 | 70 pass、0 fail、0 skip、1 个原有 TODO |
| 完整 unit，提交 `97c6f6462b2ab7275d54b8fd3947fc7c61a20bfe` | 811/811 文件完成；4174 pass、0 fail、110 skip、260 TODO；313.271 秒；退出 0，输入摘要前后相同 |
| 内核 projection 全部测试，提交 `9c0230b` | 39 pass、0 fail、0 skip、0 TODO |
| 独立静态检查 | 仅剩控制结构嵌套的 120 项；与新 upstream 逐项相同，忽略行号移动后摘要相等；检查实现未改 |
| 官方完整验证入口 | format:check 失败，109 个文件需格式调整；该次流水线后续阶段未运行 |

109 个格式失败中，101 个文件与新 upstream 相同；另外 8 个文件的上游原文已用同一格式配置复现失败，其原文在这五次上游提交中未变。本轮新增的 RuntimeSurface 接口单独通过格式检查。没有把这些问题算成通过，也没有顺手批量整理无关生产代码。

TODO 从 261 变成 260 来自 upstream `cd55bd251`：horizon 不再读独立的 latest-record 渲染路径，旧 HorizonSurface 及对应 TODO 退出，替换为真实 join 账本行为测试。实际用例身份的新增、退出及唯一退出的 TODO 均记录在 [upstream-verification.json](upstream-verification.json)。未新增 skip/TODO，新的完整测试不是一组与旧版本等覆盖的性能对照；313.271 秒不能用来重算前述 14.18% 收益。

内核修复单独提交为 [wanxiangli PR #8](https://github.com/PamelaSprin47685ghall/wanxiangli/pull/8)。官方仓库的 `refs/pull/8/head` 和 fork 分支均指向 `9c0230b`，主仓 gitlink 不依赖仅本机存在的提交。主仓 PR 将引用该依赖。规则与读取支持的 [PR #68](https://github.com/PamelaSprin47685ghall/vibe-fs/pull/68) 仍单独保留；因其尚未合并，本分支包含该规则基础，建议先审阅、合入它。

完整运行记录 ID 为 `contracts-20261028-upstream-merged`，定向运行 ID 为 `contracts-20261028-upstream-focused`，原始日志仍位于前述实验目录。新记录保留工具版本、环境、摘要、日志路径、实际失败及其归因；未运行 integration/release 或连续真实宿主验收。

## 小范围、低成本清理

用户随后要求只修能独立完成、无需大改的问题。以 `9e1e71ae4` 为起点，手工修改 10 个文件，保持现有公开接口与判定结果：

- `LifeClassification` 将根类别、角色和已知生命周期的分类展开为平坦匹配，保留 HumanRoot 的角色区分及 AgentOwnerRoot 的 Child 默认值。
- `PhysicalAcceptance` 将 Accepted、Rejected 和空结果展开为平坦匹配；订阅创建、等待、超时和释放路径不变。
- `CompletedTurn` 将正式文本判定放入同文件私有函数；存在真实工具调用时仍立即完成，只有无工具调用时才判定正文。没有把文本读取提前到工具分支。
- 其余为签名换行、调用间距和布局修正。四个仅改格式的实现模块（Ablation Gate/Settings、ToolResultBound、LargeGate）生成的 JS 与修改前逐字节相同；三个 `.fsi` 的非空白内容完全一致。

格式失败从 109 个文件降至 99 个；控制流检查从 120 项降至 116 项，恰好移除上述三处的四项嵌套，没有新增问题。检查器、测试正文、断言、预算与跳过设置均未修改。

官方 Fable 构建 generation 14 成功；11 个相关测试文件完成，72 pass、0 fail、0 skip、2 个原有 TODO，约 5.08 秒，输入前后稳定。选择范围覆盖根身份闭合、物理接收/拒绝/未知/超时、正式文本判定、空回合继续及迟到咨询。详见 [quick-fixes.json](quick-fixes.json)。

本轮没有重跑整套 unit；前节的 4174 pass 仍对应同步上游后的旧提交，不能直接当成本轮全量结果。官方完整入口仍停在格式阶段，独立静态检查仅剩 116 项控制流问题。剩余大文件格式整理，以及 watchdog、OMP 适配和取消/恢复状态机的控制流重构未纳入这次快速清理；它们需要另行逐段审阅。

## 边界

完整 unit 不等于 format/check/build/unit/integration 的全流水线，也不等于真实宿主验收。后续结果仅适用于实际运行的版本与范围。
