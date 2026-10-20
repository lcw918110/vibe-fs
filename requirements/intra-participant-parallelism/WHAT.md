# intra-participant-parallelism — WHAT

## [001] 一位 participant，多条 lane

Fission 只增加同一 logical participant 的并发 execution presents。各 lane 共享 logical identity、CanonicalRole、authority/responsibility owner、逻辑父子关系和外部子任务集合；不得新增对外 AgentId、handle 或父级 join 义务。

## [002] Canonical lane array

`fission(prompts: String Array)` 的每个元素对应一条 lane，数量 N≥2，每项含非空白字符。完整保留每项 prompt 的字节、换行和格式，不二次拆分、修剪或静默丢弃空项。

## [003] Fresh sibling replacement

调用者须为有物理 Host parent 的 subsession。每条 lane 新建独立 Host session，parentID 与原 caller 的 parentID 相同；初始任务由该 lane 输入组成，caller 已有已交付视图时附带 caller 当时的已交付视图，不成为新委托主体。caller 尚未交付视图不构成准入阻碍，lane 以自身输入启动。

## [004] All-or-none admission

全部 N 条 lanes 原子准入。任一创建、绑定或初始化发送失败，回滚所有已建立 lanes，原 caller 继续正常执行；不得部分生效或缩减数量。

## [005] Silent interrupt

全部 lanes 建立并准入后，才静默退休被替代的旧物理 caller。不发布业务 Aborted completion，不触发故障恢复，不取消已有子任务。

## [006] 裂变前 completion 广播

准入前未决的子任务和进程归 logical owner 共同所有。每项 completion 以确定性载荷向每条 lane 恰好交付一次，不重复已交付的 view 或逻辑完成。

## [007] 裂变后 completion 亲和

准入后新子任务或进程的 completion 只由发起它的 lane 消费；对子会话的后续 nudge 不改变原亲和归属。

## [008] Keyed work convergence

以 lane index 为工作记录唯一 key。同 key 同内容合并幂等，内容冲突必须拒绝。最终产物由 keyed union 决定，不依赖到达时序或无序字符串拼接。

## [009] Single logical completion

全部 lane 自有记录、裂变前广播债权及 lane 亲和任务结算后，group 才可收敛；向逻辑父级只交付一次普通 terminal completion，写回原 participant 的 completion cell。

最终接管属于同一 logical lane 生命周期。nudge、provider recovery/AABB 或 degeneration interruption 后的 continuation 仍归该 lane；后继链普通 TurnCompleted 后才可写入 FissionConverged 并发布 logical completion。

## [010] Durable replay

group、lane 成员、替换与收敛终结由不可变 durable facts 审计和重放，不扫描相似外部会话猜测。进程中断后的未完成裂变记为中断，不自动隐式恢复。

## [011] 单 active group

同一 logical participant 同时至多一个 active group。活跃 lane 再次裂变须拒绝为 already-fissioned，不递归裂变。

## [012] 权能单一来源

从 office consequence 同一来源投影模型工具集与运行时门禁，同 office 的不同档位权限相同。只有已证明的 Engineer 且本次授权含 Fission 才具备裂变权能；完整准入条件见 [017]。

## [013] Subsession-only origin

来源校验独立于角色权能。根会话须在资源预留、记录物化或中断前拒绝；其模型工具集中也须去除 Fission。

## [014] 控制面先于 lane 结算

nudge、provider fallback/AABB 和 degeneration 恢复由各自 owner 负责。lane 的 TurnInProgress、TurnNeedsContinuation、TurnFailed 及 DegenerationGuard 引发的 TurnAborted 交给普通 Turn/Application owner，不物化 lane、不失败 group、不发布 logical completion。

只有稳定 TurnCompleted 可进入 lane 物化或最终接管完成；真正外部 abort 才可终止 group。

## [015] Deterministic ring convergence

V1 按 canonical index 从 lane 0 到 N−1 环行，以 keyed union 合并；终点 N−1 接受最终 takeover。不同完成到达顺序须得到相同 merge order、takeover lane 和 aggregate，不以最后到达或最后物化的 lane 选择接管者。

## [017] 准入公式与身份边界

准入须同时满足：已证明 CanonicalRole=Engineer、本次授权含 Fission、subsession 来源、无 active group，以及其余现有准入条件。运行时入口与 ToolRegistry 都须拒绝不满足条件的调用，不能只隐藏 schema。

角色名、Persona 别名、参数、自称身份和附带已交回的 view 均不授予能力。所有非 Engineer 身份（含 Manager 各任期、Orchestrator、DevOps、Bookkeeper 及未知或内部辅助身份）、Engineer 根会话、已有 active group 的 lane、Sphinx 内部只读 Engineer 均拒绝。历史 Manager Fission 只供读取和重放，不重建 lane、恢复执行或扩权。
