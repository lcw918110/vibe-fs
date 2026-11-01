# interaction-authority

## 017 — continuation 只能接续 active run

`DispatchSurface.sendContinuation` 在构造任何 runtime、写入任何 durable claim 或触达 Host 传输之前，先做两层只读校验：

1. `activeProfileAt`：目标 session 的 durable 投影里必须存在 `ActiveLogicalRun`（Fission retirement 与无 active profile 分别给出 `Session is retired by Fission` / `No active authority profile`）。
2. `requireExactActiveProfile`：调用方提供的 `AuthorityExecutionProfile` 必须与 durable active profile 完全相等（session、logical run、authority root、identity seed 逐字段）。外部传入的 profile 只是主张，不是 authority；任何不匹配都在 claim 前拒绝。

正例路径（active exact）由 `sendAgentOwnerRootAwait` → `acceptAgentOwnerRoot` → `sendContinuation` 的真实 journal 流程覆盖；反例矩阵（never-active、wrong run、wrong root）断言 Host send 次数为零、`observation` 为 null、投影中 `pendingClaims`/`claimSequences` 为零。

Closed-target 反例（run 已 durable 关闭、归档 profile 仍可读）要等真实的统一 `IncumbencyRetired` closure fact，属 018 的 lifecycle closure 工作；本包保留对应 todo。

生产侧 `SendContinuationWithDigestAttempt` 的 claim 前验证由 `HostSessionNudge.sendContinuationResult`（`tryActiveProfile` + Fission 检查）与 DispatchSurface 的上述两层共同承担；两者共享同一 durable 投影，不引入平行状态机。
