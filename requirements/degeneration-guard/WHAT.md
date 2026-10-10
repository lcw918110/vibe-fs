# degeneration-guard — WHAT

## [001] 双侧退化与非目标

检测流式输出的加权相异度：低于正常包络为 `TooRepetitive`，高于包络为 `TooRandom`。本包不估算上下文窗口、不按错误文本分类、不做 AABB/fallback 重试，不按角色或语言调整阈值，不把 delta 拼成 durable 业务事实。

## [002] 只观察文本流

传感器只消费 Host 的 assistant text 与 reasoning/thinking delta；不写 Journal 业务事实，不从 delta 推断终态、权限或 fallback 状态。

## [003] 判定指标与正常包络

以 `gpt-tokenizer/o200k_base` token 维护指数衰减加权相异计数 $D_t$。下界 `MIN_WEIGHTED_DISTINCT` 为仓库正常轨迹的经验分位数 $p=0.025$，上界 `MAX_WEIGHTED_DISTINCT` 为经验最大值 $p=1.0$，区间概率参数为 $0.975$。$D_t<MIN$ 判 `TooRepetitive`，$D_t>MAX$ 判 `TooRandom`；边界及区间内均为 `Normal`。

## [004] Repository SSOT 与手动派生

Half-life 固定为 `256` 个 `o200k_base` token，不由排版推导。包络从仓库唯一语料源派生，但只允许手动更新，遵守以下合同：

- 构建不自动派生包络，也不自动检查包络是否随语料改变。入库产物位于 `resources/degeneration-guard/envelope/LoopDetectorEnvelope.js`；构建把入库产物原样复制到 `dist/Execution/Session/LoopDetectorEnvelope.js`。入库产物缺失时构建失败，并提示运行显式派生命令 `node scripts/derive-envelope.mjs`。
- 包络只允许手动更新：运行显式命令从当前仓库语料重新派生并写盘到入库位置。产物是 runtime import 文件，不是配置源；它是唯一的手动更新落点，不得把 normal/min/max 另抄进其它源码或文档作数值快照。
- selector 只返回 Git-tracked 路径；generator 拒绝 root 外路径，规范为 canonical repository-relative identity。generator、build、selector 及其输出绑定同一 staged input；raw bytes 只能经同一 tracking reader 取得，再作 strict UTF-8 和 generated marker 判定，不得旁路读取。
- 语料使用 source/document 正向类型 allowlist，排除生成物、vendor/dependency、fixture/golden 和 JSON/JSONL/CSV 等结构化数据。按 repository path 顺序连成单一文本流；并行编码只在安全换行边界（`\n` 后为可打印、非 `/` 的 ASCII 字符）切分，结果须与整流编码位等价。
- 以 $D_0=X$ 作一次仿射 replay，求唯一自洽先验 $X=mean(D_t(X))$，再从 $D_t(X)=\lambda^tX+b_t$ 的轨迹取 [003] 分位数。不用任意 seed 预热后二次 replay、Beta/连续分布拟合、运行时分位数或其它概率外推。
- 生成 JS 是 runtime import 产物：入库一份，构建复制到 dist。唯一 generated artifact row 须绑定 stable identity、output digest、selected-input digest、generator/build/selector lineage、package import target 与完整 JavaScript traversal；确定性不豁免产物实际携带的 authority。

## [005] O(1) 更新与有界内存

每个 token 只查询、更新其最近出现步数，更新时间为 $O(1)$；状态只保留有限词表中已见 token 的最近步数，内存不随输出长度增长。

## [006] 单次 ProviderRun 生命周期

每次 provider attempt 使用 fresh detector；attempt 结束、guard 中断或 session 销毁时丢弃，禁止跨 attempt 复用。

因 `TooRepetitive` 或 `TooRandom` 认领 guard 中断时，必须在同一原子边界将该 session 的算法 scratch 恢复为 `LoopDetector.create()` 的完整初始状态：weighted count 回到仓库先验、`Step = 0`、`LastSeenTokenStep` 为空。后续新输出从此重新统计，不依赖 Host 另行调用 reset；已截断 exact run 的迟到 delta 不得污染新统计。此重置只涉及算法 scratch，不得清除 armed anomaly、interruptAttempts 或 active owned-work；其身份、单次性与排空仍遵守 [007]—[009]。

## [007] 只中断当前 attempt

命中任一异常且满足 [010] 时，guard 原子记录进程内异常并调用 Host `InterruptAttempt`。同一 attempt 至多中断一次；abort 返回前不得发送 continuation。

## [008] Armed anomaly 的身份与寿命

Armed anomaly 只在当前进程内存，崩溃后丢失，不写 Journal。它只按 exact `(SessionId, ProviderRunIdentity)` 认领随后 reconciled `TurnAborted`；旧 attempt、不同 run 或错误物理消息的 abort 不得消费当前 anomaly，只能忽略或作为 stale 外部中止观察。

## [009] Guard 接续与 owned work

reconcile 消费匹配 anomaly 时，guard 发送恰好一次 continuation，返回 typed `DegenerationGuard` cause；下游 turn/fission 只 yield/no-op，不再 nudge、repair、记录确认失败或走 AABB。发送失败只作 guard continuation failure 诊断，不改道 fallback。

interrupt 与 continuation 均纳入可等待、取消和汇报的 owned-work 生命周期；区分发送失败、未知 acceptance 与确定未接受，不得丢弃任务或删除内存记录冒充续发成功。

## [010] 作用域与豁免

只检测插件 Owned 且具有 physical parent 的 managed 会话。非 Owned、user-facing root、compaction 和非 managed 内部运行均豁免。

## [011] 两类接续语义

接续使用专用 `DegenerationGuard` authority，不冒充 `ProviderRetryAttempt`：`TooRepetitive` 指出重复太多并建议换种表述；`TooRandom` 指出重复太少、不符合正常语料模式并建议换种表述。provider language resource 可翻译措辞，须保持两类语义区别。

## [012] 唯一恢复 owner

本包独占 `observe → classify → interrupt → reconcile-own-cause → continue`。不修改 fallback Offset/失败预算，不发普通 interaction repair 或 AABB retry prompt；其它模块不得为 `DegenerationGuard` 建立第二条 recovery。

## [013] Diagnostic 不拥有控制权

传感器只依赖构造时必填的窄 `emitDiagnostic: string -> (string * string) list -> unit` capability，不依赖 Host diagnostic 实现。诊断抛错不得改变 arm、单次 interrupt、cause consume 或 continuation 结果；诊断不得取得 fallback、Journal、process-fatal 或 attempt-control authority。
