# durable-convergence — WHAT

## [001] 活跃 writer 的合并是集合并集

同一 retention 截止时刻，活跃 writer 的合并必须是 append-only 事实的集合并集，按 `event_id` 幂等去重。retention 只能按 UTC 日期分组整体移除过期 writer，不得按时间、版本或到达顺序裁掉保留 writer 内的事实。

## [002] 统一 KWayMerge

所有多流合并使用同一 `KWayMerge(writerStreams[])`，满足结合律、交换律、幂等性和确定性。同一组有序 writer 流不因枚举顺序、进程或机器而改变规范事件序列。因果顺序优先于 `EventId` 字典序，parent 必须先于 child。

## [003] 完整 writer 流与 retained union 等价

本地与远端完整 writer 流按同一截止时刻过滤，再以 k-way merge 流式归并；结果必须等价于保留 writer 的全量集合并集。每个本地 writer 文件和远端 writer blob 都是不可分段的有序输入，不按事件 TTL、writer 分段或额外增量对象协议同步。

## [004] 合法并发是 DomainConflict

同一业务流从相同父事件合法分叉时，保留全部竞争 Heads，业务投影明确呈现确定的 `DomainConflict`，不得当作 `StorageInvalid`。

## [005] 裁决覆盖全部 Heads

解决冲突的事件必须以全部竞争 Heads 为 `parents`；只有裁决事件及这些父事件均已折叠，业务投影才可离开 `DomainConflict` 并收敛为唯一权威状态。

## [006] 不以时间或版本挑选赢家

保留 writer 内不得以墙钟、revision 或到达顺序挑选赢家或丢弃事件。物理时间只用于 [011] 的整条 writer retention。

## [007] 同一历史导出同一 Current

`Current(now) = CanonicalIntegrator(KWayMerge(Retain(now, writerStreams)))`。相同截止时刻与 writer 集合必须导出相同 Structural frontier 和每个已注册业务 oracle 的 production Current；不得直接合并投影状态。事件集合、源码调用或空投影相同不足以证明业务收敛。

## [008] 持久化激活安装 Hook，用户 Git 触发同步

插件加载不修改 Git 配置；首次激活持久化能力才确保安装 `reference-transaction` 与 `pre-push`。同步由用户 Git 操作启动独立 Hook 进程执行，不设后台同步或自动上传服务。

安装 Hook 后，ensure 为每个 remote 补齐 `+refs/wanxiang/store:refs/wanxiang/remotes/<remote>/store` 与 `+refs/heads/*:refs/remotes/<remote>/*` 两条 fetch 映射。只追加缺失行，不替换、删除或重排既有配置；重复 ensure 不重复添加，已有 store 行仍须补缺失的 heads 行。

本地 fingerprint、retention expiry 与上次成功快照未变，且 tracking ref 仍指向该快照时，`pre-push` 必须零网络复用。writer/payload、TTL 或已观察 tracking ref 变化时，双向读取、归并完整 writer 流，原子替换本地集合并 CAS 发布远端快照。未被本机观察的远端推进不由 clean no-op 主动拉取，也不得被覆盖；下次本地事实或 tracking 变化时再完整收敛。

收敛的跨进程文件锁只覆盖本地 writer 字节边界：读取本地 writer 流、过期 writer 删除、远端事件导入写回、快照物化与物化缓存写入必须在同一互斥窗口内完成。网络发现与发布（`ls-remote`、`fetch`、`push`）不得在持锁期间执行；远端 Git 对象读取不需要锁。锁外不得读取本地 writer 文件字节；tracking ref 与物化缓存的只读短路是明确豁免的非权威优化。

Hook 进程对单条 Git 命令与整次收敛设置有限 deadline：网络命令按类型取上限（`ls-remote` 30 秒，`fetch`/`rev-parse`/`push` 120 秒），整次收敛 600 秒，锁内 Git 子进程调用同样受 120 秒超时约束。deadline 超时只终止 hook 自身的物理等待并报错，不产生任何 durable 事实，也不改变失败同步阻塞用户 Git 操作的既有行为。

## [009] Dumb remote

远端只提供标准 Git 对象读写、引用推进和 CAS，不解释领域事件，不执行合并，不依赖万象术专有后端。

## [010] 同步成本随变化量增长

物理 fingerprint 缓存不具权威。满足 [008] 的 clean no-op 必须在任何同步 transport 前返回，不另行 `ls-remote`、`fetch` 或内部 `push`。发生变化时只读写、验证变动文件，远端竞争仍走 CAS 收敛。

writer 的 remote-read 判定必须比较 manifest activity。事件行自包含载荷（[durable-events-012]），远端快照只含 `writers/` 与 `writer-manifest`，没有独立 payload 树；因此不存在「因缺少 writer manifest 而重读全部历史 payload」的路径。

Hook 安装器只在当前仓库为未自定义 multiplex 的 SSH 命令追加短生命周期 `ControlMaster=auto`，保留原命令和 identity 参数；已有 `ControlMaster` 或 `ControlPath` 不得覆盖。自有 wrapper 每次执行前重建并收紧 repo-scoped socket 目录，永久配置不得依赖安装时的易失目录；须迁移旧版自有的过长 repo-local 和易失 tmp socket 路径。

## [011] Writer 整体过期且不被旧快照复活

每个 writer 是一次进程输出流。保留单位是最近活动时间的 **UTC 日期分组**：今天与昨天保留，前天及更早整体过期。`Retain(now, W) = {w ∈ W | utcDay(lastActivity(w)) >= utcDay(now) - 1}`。日期只作 GC 分组，不携带历史语义，允许 writer 因活动推进而移动到更晚的日期。同次同步按统一截止时刻满足 `Retain(A ∪ B) = Retain(A) ∪ Retain(B)`；过期 writer 同时退出本地集合和新远端快照，缓存不得跨下一 expiry 命中。

活动时间优先取 writer 尾部 Journal 的 `payload.ObservedAt`；连续 `ProjectionCutTail` 须向前越过后再判定。非 Journal 尾部才可回退到 producer-side file activity。导入不得以 fetch 时间或新 mtime 刷新活动性。

`writer-manifest v2` 原子绑定并传播每个 writer blob OID 与 lastActivity。缺 manifest 或旧 mtime 语义 v1 的远端 writer tree 直接忽略；声明 v2 后，manifest 与 writers 必须逐项一一对应且 OID 相等，缺项、多项、重复、格式错或 OID 不符均拒绝。

保留 writer 指向窗口外 parent 时，将其视为已满足的因果边界；保留集合内部的依赖缺失或成环仍须拒绝。
