# prefix-stability-012 收口（2026-10-10）

- 对象：`requirements/prefix-stability/tests/012.test.mjs`，`boundary='reanchor'` 两组在 `observeFact(directory, 'ContextReanchored', …)` 永久挂起（GAP-226）。
- 结论：走实现侧。把 `HostCompactionGate.judgeStartup` 的 pseudoRuns 限定在第一回合窗口内。未运行验证，证据待 DevOps。

## 一、挂起链（复述诊断并补源码链）

`hooks.event(session.status retry)` → RetryWake pass → `HostCompactionObserver.observe` → `observeStartupProbe` 判定 `CompactedDespiteSettings` → `raiseOnStartupFailure` 抛 `InvalidOperationException` → `Scheduler.Run` 的 `try…with` 记一行 `RECONCILE-SCHEDULER` 后吞掉（`src/Wanxiangshu/Composition/Turn/Scheduler.fs`）→ 同一 pass 的 `observeDurable` 被跳过 → `ContextReanchored` 不落。该 wake 是测试发出的最后一次 wake，`observeFact` 永久等待。

两层成因：

1. 测试前提弱化。`proposals/archive/2026-10-05/失败候选与已提交前缀-2026-10-05.md` 第 26 行当时已写明「FailureRecorded 不能单独证明 startup probe 已关闭」；10-05 步骤名为「normal exact reconciliation closes the startup probe before manual compaction」。当前 L81-85 只等 `FailureRecorded` + `consecutiveFailureCount=1` 就推入 compaction，probe 可能尚未完成首次判定。
2. 实现判定边界与条款不符（本次修复对象）。`HostCompactionGate.judgeStartup` 与 `HostCompactionPolicy.judgeFirstTurn` 的注释都声明探针只断言「no compaction happened on the FIRST turn」；实现却统计整个快照的全部 compaction 伪 run。首次判定时机由任意 reconcile pass 决定，判定语义随时机漂移：首次 observe 一旦晚于手动 `/compact`，手动压缩就被读成「第一回合的压缩」，误报第二套压缩实现。

## 二、候选 durable 事实逐条排除（为什么没走测试侧）

测试侧要可靠「确认 probe 已关闭」，必须找到一个由该 pass 必然产生、且后置于 probe 关闭的既有 durable 事实。逐条读源码后全部排除：

- `FailureRecorded`：由 ProviderFailureLedger 在 `message.updated` 链路独立写入，与 reconcile pass 的 observe 无先后约束（10-05 教训原文）。
- `BlogObservationCommitted`：由 transform/onTurn 链路写，先于同一 pass 末尾的 observe。
- `PrefixRebaseCommitted`：由 transform 内 XWire 写，独立于 reconcile pass。
- `ContextReanchored`：由 `observeDurable` 自己写，用它做前置是循环。
- `Accepted` / `ProviderStarted` / `ChildWorkVoided` 等：分属 dispatch / managed-chat / 恢复链路，均不与 reconcile observe 定序。
- `ReconcilePass.fs` 本身：`observeSnapshot` 是 pass 的最后一步（`publishResolvedTurn` / `publishIfPresent` 之后），其后没有任何 durable append；`recordMaps` 是内存映射。

「让实现暴露只读 probe verdict」也不合：不修复真实误判缺陷（生产环境首次 observe 晚于用户 `/compact` 时仍会误报并启动拒绝），且为测试专用需要扩大生产接口。判定边界确实违背 HOST-006 条款意图，因此选实现侧。

## 三、新边界与改动点

新边界：启动探针的判定窗口从会话开始、截止于首个 completed assistant 消息（含该消息本身）。窗口内的 compaction 伪 run 计入并拒绝启动；窗口之后（用户 `/compact` 或后续回合压缩）属于收容层，不得作为启动拒绝。

改动：

1. `src/Wanxiangshu/Host/CompactionPolicy.fs` / `.fsi`：新增 `CompactionWindowMessage` 与纯函数 `firstTurnCompactionRuns`（窗口内计数；`None` = 第一回合未完成）。
2. `src/Wanxiangshu/OpenCode/Host/HostCompactionGate.fs · judgeStartup`：把快照投影为窗口消息，只把窗口内计数交给 `judgeFirstTurn`。签名不变。
3. `src/Wanxiangshu/Host/Contract/CompactionPolicySurface.fs` / `.fsi`：暴露 `firstTurnCompactionRuns`（JS 数组 → 计数或 null），供语义测试观察真实 policy。
4. `requirements/host-boundary/WHAT.md [007]`：写明窗口边界与窗口后的收容归属。
5. `requirements/host-boundary/tests/007.test.mjs`：新增窗口边界用例（窗口内拒绝、边界消息自身计入、窗口后 Satisfied、第一回合未完成 null）。

为什么「含首个 completed assistant 本身」：若首个完成的消息就是 compaction 摘要，严格「之前」会漏检；该消息本身即第一回合的产物，必须计入。

## 四、修复后 012 的可观察性

修复后，`reanchor` 组不再依赖「probe 是否已关闭」这个不可观察前提：

- probe 未关闭：L88 的 pass 先做窗口判定（窗口内无 compaction → Satisfied，关闭 probe），同 pass 继续 `observeDurable` → 落 `ContextReanchored`；
- probe 已关闭：跳过判定，直接 `observeDurable`。

两种情形第一次 pass 都落盘，`observeFact(directory, 'ContextReanchored', …)` 成为充分观察。`012.test.mjs` 不改。

## 五、防护没有削弱

- 窗口内 compaction → `judgeFirstTurn(n>0)` → `CompactedDespiteSettings` → 抛错，仍 fail-loud（与修复前同为 `InvalidOperationException`，链路未动）。
- 窗口后 compaction → 收容层 `nextReanchor` 照旧处理；既有 containment 用例不变。
- 修复把 probe 判定从「首次 observe 时机的偶然性」变为「与时机无关的确定性边界」，正是注释早已声明的窄断言。

## 六、剩余边界（未验证 / 待取回）

- 编译与测试均未运行。待 DevOps：`node scripts/build.mjs`；`node --test requirements/host-boundary/tests/007.test.mjs`；`node --test requirements/prefix-stability/tests/012.test.mjs`（四组合，反复运行）；`node --test requirements/context-compression/tests/002.test.mjs`；`node scripts/check.mjs`。
- 10-05 版 `012.test.mjs` 完整源码未取回（archive 只有日志与裁决记录）。需要 `git log --all --follow -- requirements/prefix-stability/tests/012.test.mjs` 佐证当时等待对象。不阻塞本修复。
- fail-loud 的物理形态是既有链路：抛错后由 `Scheduler.Run` catch 记 `RECONCILE-SCHEDULER`，仅 stderr 一行；这不是本次改动引入，也不在本次范围。
- 真实 Host 消息顺序下「首个 completed assistant」与压缩摘要的相对位置以 012 的真实插件路径覆盖；未接真实 OpenCode canary。
