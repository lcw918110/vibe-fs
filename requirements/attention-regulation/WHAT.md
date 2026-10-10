# attention-regulation — WHAT

## [001] enough 语义并入 assume

`enough` 工具已退休。声明「当前信息已足以支持下一步行动；除非出现此前未消费且足以改变决策路径的新事实，否则不再重开同一判断」的语义，改由 `assume(assumption)` 表达。它不证明决策正确，不创造权威事实或持久认知状态；无新事实的重复调用不增加语义。禁止以兼容、别名或历史名义重新注册 `enough` 或任何同名工具。

## [002] abandon 语义并入 assume

`abandon` 工具已退休。放弃自行形成的计划、推论或假设（无须审批或理由检验，使其不再自动占用注意力）的语义，改由 `assume(assumption)` 表达。放弃自我承诺不得取消真实任务义务、撤销用户授权、删除工作产物或终止会话。禁止以兼容、别名或历史名义重新注册 `abandon` 或任何同名工具。

## [003] defer 暂存非阻塞新工作

`defer(new_work)` 接受非空自然语言，将新发现的非阻塞工作登记为 DeferredWork，以便继续当前主线。它不是活动义务、后台作业或授权，不自动委派、执行或成为当前欠账。

## [004] occurrence 与 life 隔离

DeferredWork 有稳定 occurrence 标识，归属特定 participant life；重启、重放不丢失、不重复、不跨参与者泄漏。life 在消费前终止时，剩余条目随之结束，不继承到新 life 或自动变成持久任务债务。

## [005] defer 的消费载体

DeferredWork 只在下列真实终点被消费；消费即消灭，不激活义务，participant 可选择处理、再次 defer、正式立项或忽略：

1. Manager：第一次 `suicide` 返回本任尚未消费的 DeferredWork 待办，供调用方核对；随后完成的正常退休确认使这些条目消灭。该载体依赖 relay-retirement 的两次 suicide 合同。
2. Engineer 与 DevOps：participant 自然终止且仍有未消费条目时，系统以一次 user prompt 回合呈现这些条目，呈现后消灭。
3. Orchestrator：按上述精神，在其交接或收尾时呈现并消灭。当前落点为其 run 自然终止（收尾），与 Engineer、DevOps 共用同一呈现入口；三者的呈现都恰好一次，由消费凭据保证。

消费只记录消费凭据，不产生新义务，不改变 office 权限，也不伪造用户交互权威。消费凭据是独立 durable 事实（`DeferredWorkConsumed`，携带被消费的 occurrence 标识集合）：进程重启、journal 重放与乱序合并都不得让已消费条目复活；同一凭据重复折叠幂等。

## [006] 最小持久状态

本包只持久化 DeferredWork 的追加投影与消费凭据：`DeferredWorkRecorded` 追加条目，`DeferredWorkConsumed` 落消费凭据；life 结束时其剩余条目同样转化为消费凭据。不引入阶段、优先级、截止期、依赖图、自动恢复、后台执行器或通用认知状态机。
