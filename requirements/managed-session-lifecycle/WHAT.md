# managed-session-lifecycle — WHAT

## [001] Attached 生命周期单一 Owner

所有 Attached 会话的创建、恢复、注册、级联取消与回收由单一 lifecycle owner 负责。各 AttachmentKind 只提供参数与终态策略，不另建生命周期框架；durable chat execution transition 委托 `managed-chat-execution`。

## [002] 关联先于首 Prompt

子会话的 SessionAssociation 必须先持久确认，才可发送首个 prompt，使首次交互即可判定分类。

## [003] 恢复只认确切关联

重启时仅复用与 journal 的 SessionId、agent、title 恰好单一匹配的 Attached 会话。无关联则新建并挂到 family root，不收养无关联会话；属性冲突、多候选或查询失败均 fail closed。

## [004] Reusable 与 OneShot

Dedicated 会话在同一作用域跨调用复用，调用完成不销毁。OneShot 每次新建，完成即终止并释放；两种生命周期不混用。

## [005] ReuseScope 绑定

Dedicated 绑定键为 `(OwnerReuseScopeId, Role)`。同一 scope 内每个 Role 至多一个活动会话，兼容续问按此键复用，不同 scope 隔离。

## [006] Handle 四态

Handle 仅有 `Active | CompletedAwaitingJoin | Retired | Abandoned` 四态。Retired 与 Abandoned 为持久终态，不可恢复为活动状态。

## [007] Completion 单赋值

成功终态、发送失败与取消竞争同一 completion cell，首个事实获胜，后来者不能覆盖。

## [008] Consume 与 Retire

join 消费完成结果时原子写入 HandleRetired 墓碑；提交未确认不返回 payload。完成事实在重启后仍仅投递一次。

## [009] 父取消等待子收束

父会话获授权逻辑取消后，为全部所属活动子会话持久写入 HandleAbandoned，并等待每个子会话物理中断和清理完成，才发布父终止。

## [010] HostOwnedHidden

HostOwnedHidden handle 对父会话的列表、等待、视图及恢复不可见；持久记录只供宿主审计与恢复，不由历史角色名称决定可见性。

## [011] 永久丢失才替换

关联子会话已确认永久丢失才允许替换。替换原子进行，次序为：建立新子会话 → 持久关闭旧关联 → 建立新关联。

## [012] Child Run 与父记录分离

子会话的忙碌、空闲、中断、关闭由独立物理生命周期管理，与父工作记录解耦；父记录不充当子完成事实。

## [013] Durable re-enlist

重启仅依 durable HandleLinked 与完成数据重建生命周期，过滤隐藏 handle，不使用旧内存或猜测补状态。

## [014] Dedicated 生命周期

Dedicated 随 OwnerReuseScope 存活，仅在 scope 显式关闭时清理释放，父会话单轮迭代退出不结束它。

## [015] Handle 身份稳定

Agent 子会话的 handle 就是其运行时 Agent ID；重启后同一 handle 仍绑定同一子会话实体。

## [016] Attempt Interrupt 与 Logical Cancel

内部控制只能中断子会话的当前 attempt，不因此逻辑取消或级联销毁。正常生命周期内，自动化机制不得主动中断用户根会话；suicide 在任期 committed 退休后，可凭退休事实终止该 run 的残余尝试。

真实外部物理用户输入只允许打断 `join` 等待，不得中断当前 LLM 输出或其他工具，也不得调用 Host `AbortSession` / `InterruptAttempt` 排空当前 attempt。新输入作为下一次 LLM 请求的材料，由 Host 在当前输出或工具自然结束后纳入；当前物理 assistant 与工具结果仍须正常保存。

准入 owner 仍以 durable `Accepted` 和 owner-issued 的 `HumanRoot` / `HumanMessage` evidence 精确接纳新输入。已有同一 run 的活跃租约时，`HumanMessage` 与 `BusyAgentNudge` 先持久接纳并投影已有目标，保留旧 exact lease，供当前输出、工具及已经准备中的请求自然完成；Host 的下一次 provider 边界实际选择可见新输入后，才交接 exact lease 和原 capacity credit，不重新派工或抢占第二份容量。容量 owner 的 supersession 不授予物理中断权。

同一 session 的准入投影与交接由一个 owner 串行处理，容量 pending 的完成等待不阻挡后来的输入入场；旧执行按 `Superseded` policy 精确结算，迟到旧回调不得取消或释放新执行。同次请求已包含、但未单独启动 provider 的较早追加材料也须精确结算，不留下可恢复的悬空执行。新输入不逻辑取消根会话、不级联 children，不自动发起新 mission 或工作记录；原 authority root、LogicalRunId、participant 与 obligation 延续。`PendingPromptIntent`、Guard、retry 与 HostInternal 也不得借此中断根会话，原有自动根会话中断禁令保持有效。

## [017] 中断必须有后继

内部中断发起物理 abort 前，必须已有唯一 successor；没有 successor 则形成明确 Failed 终态并唤醒父等待。Failed 携当前物理用户消息提升所得的 Authority Root；缺失则 fail closed，不退化成 session-scoped/rootless 终态。

## [018] Abandon 需要不可逆授权

HandleAbandoned 表示 child 不会再沿当前 handle 返回，只由已确认的 logical parent/session 终止或 child 永久丢失证据授权。

Attempt/TurnAborted、provider failure/retry、degeneration-guard、Fission、插件卸载、进程 shutdown 或 runtime dispose 均无此权限，只结束当前观察者/attempt 或 detach 本地资源，保留 durable Active handle 供恢复与 Join/Horizon。未知停止信号不升级成 ParentCancelled。

## [019] Cancel/Delete 精确排空

获授权的 logical cancel/delete 等待 `managed-chat-execution` 确认作用域内全部已准入 execution 均 durable terminal 且 exact capacity 已归还，才发布完成。lifecycle 不复制执行状态机、不 blind release、不用 timer/deadline/sleep/polling 推断排空；process/plugin shutdown 仍只 detach。

## [020] Fresh identity 先关闭旧 run

复用 SessionId 安装新身份前，依次完成 exact execution settlement、受权 child drain、`interaction-authority` 的 durable exact prior-run closure，并取得匹配 witness。association removal、detach、idle、timeout、Host observation、restart 都不能替代；缺证或 run 不匹配则拒绝。

## [021] Lifecycle fatal

Session delete、turn observation 或 strength semantic-cut incident 先完成其要求的 exact settlement、child drain 与 durable closure，再使用 composition 注入的 mandatory fatal capability。detach 不授权 fatal，不直接使用 physical adapter 或 optional/default/global fallback；同一 incident 至多一次 report/kill，不伪造 session terminal 或 child abandon。

## [022] Inspector finalize 的结算边界

Finalize 恒带 exact Inspector identity，commitment 闭合为 `Finalized | NothingToFinalize | NotCommitted | Unknown | PhaseConflict`。Bookkeeper 不可用或 store 写失败为 NotCommitted；archive 已提交但 index refresh 失败为 Unknown，不重做 archive；重复 finalize 为 PhaseConflict。

仅 Finalized/NothingToFinalize 释放 identity；其他状态保留以便恢复取证。Session deletion 先取得结算证据再决定 identity 去留，不在 finally 无条件删除。该收尾边界不授权创建新的活跃 Inspector。

## [023] 旧身份只收束、不升权

新任务和新子会话仅接纳当前合法活跃身份。历史事件原样保留，旧身份解码隔离在历史边界，不自动升级为 Engineer 或获得写入/Fission 权限。

运行或重启发现遗留旧角色活跃会话时，按受权 cancel/drain 或 retirement 显式写终态并排空资源，不自动恢复成合法活跃运行链。

## [024] 固定 DevOps 恢复

同一道路只有一个 DevOps 逻辑操作员权威；故障恢复可替换物理 Session，但同时至多一个物理权威可执行。沿用初始化绑定的模型和 Persona，不借 resume 重置或切换。

恢复不重复未决物理命令。真实进程及 PTY 随会话、道路关闭彻底排空，不留孤儿进程。

## [025] DevOps 每次工作返回即排空 PTY

固定 DevOps 每次 run 终态结算，立即收束其拥有的全部 PTY：TERM 后等待真实退出，必要时升级 KILL，随后清除记账。不得留到下次 resume，不因 Manager 退休触发，不影响其他会话的资源和生命周期。

## [026] Main 收束级联 Attached InternalLeaf 的 execution 结算

Main session 收束（宿主 `SessionDeleted` 事件，且存在 durable Attached Companion 关联 `CompanionBloggerLinked` 作为 linked InternalLeaf 证据）时，lifecycle owner 除排空 Main 自身已准入 execution 外，还必须对每个 linked Attached InternalLeaf（Blogger）名下全部已准入 execution 逐一完成 typed terminal 与 exact capacity 归还：

- 未终态 execution 先写 typed `Cancelled` terminal：pre-provider 阶段复用 `PreProviderSettlement.settle`，after-provider-start 阶段复用 `ManagedChatProviderLifecycle.terminal`；两者都等待 durable 提交确认后才可归还。
- 已 terminal 但物理容量仍 held 的 execution 直接请求 exact release（`ModelRouting.releasePhysicalExecution`）；`AlreadyApplied` 与 `StaleFence` 按幂等结果接受，`Conflict` 不得吞没。
- 只允许 exact per-execution fence 释放。禁止 session-wide blind release、计数减一、以错误文本或超时猜测释放。
- 归还完成后，该 execution 不得再出现在 shared capacity 快照中。
- 重复收束幂等：重放不得产生第二份 terminal、第二次释放副作用或抛错；已归还容量的 execution 再次处理为 no-op。
- Main 自身与 linked InternalLeaf 各自的 execution 只由自己的 exact key 结算；本条款不改变 Main 自身容量的既有结算路径。

## [027] Session 收束取消作用域保留并完成延后归还

Session 收束（`PluginSessionScope.ClearSession`，覆盖宿主 `SessionDeleted` 的 Main 收束与 linked Attached InternalLeaf 的自删）时，lifecycle owner 除按 [026] 结算该作用域名下已准入 execution 外，还必须取消该作用域名下的全部 retained continuation input（`execution-model-routing` [006] 的「作用域关闭均须清理自己的保留」在删除链上的落地），并完成由此解锁的旧 exact capacity 归还；对 Main 自身与每个 linked Attached InternalLeaf（Blogger）都执行。

- 触发：作用域收束调用发生时，该 session 名下仍存在 retained continuation input（含其保留键没有对应 durable execution 的残余形态）。取消在自身与每个 linked leaf 上各自先于该 leaf 的 exact release 请求执行。
- 允许后果：取消只移除该 session 名下的保留，并以 exact `admissionOwner.ReleasePhysical` 路径回放其挂起的旧 key 归还；最后一份保留撤销后旧 credit 立即归还，borrowed credit 不退休 lender。归还完成后旧 execution 不得再出现在 shared capacity 快照中。
- 禁止后果：不得使用 `ReleaseExecution`/`ReleaseSession` 等 force 路径；不得 session-wide blind release、计数减一或以时间猜测释放；保留未取消时不得宣称归还完成；取消操作不得取消无关的 pending demand 或触碰其他 session 的保留。
- 失败后果：取消回放对 `AlreadyApplied` 与 `StaleFence` 按幂等结果接受，视同归还完成并清除挂起记录；对 `Conflict` 不得吞没——以带 exact key 信息文本的 `InvalidOperationException` 显式暴露（风格对齐 [026] 的 `SessionRecoveryHost.release`），且暴露时保留挂起记录，使「取消已完成、归还未完成」的状态不被静默吞掉。重复收束幂等：保留已取消时不产生第二次释放副作用；除真实所有权冲突外不抛错。
- 失败隔离：收束链的任一步失败只隔离该步自身，不得跳过其余独立义务。`ClearSession` 内，Main 自身的结算、保留取消或释放失败不得跳过 linked Attached InternalLeaf 的结算、保留取消与 exact 释放；任一作用域的失败也不得跳过本次收束的无条件 per-session 清理组（registry 条目、quiescence、join-interrupt、companion 等）。`PluginRuntimeScope.DisposeSession` 承接 `ClearSession` 失败后仍必须执行其后续清理组（recovery scope、attempt plan、Blogger parked/episodes、LoopSensor 等）。失败在完成其余义务后仍显式暴露：首个真实失败被保留并在其余义务完成后重新抛出；上一段的真冲突例外语义不因隔离被吞掉或降级。重复收束继续幂等，已消费的失败源不得在第二次收束中再次产生副作用或吞掉新的失败。
