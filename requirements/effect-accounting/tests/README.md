# effect-accounting 测试

WHAT 定义意图、确认与未知的边界，本目录只说明证据范围。

- 001 驱动真实工作区投影与事实 codec，确认意图和结果不同；另以 B4 编译隔离在 change-fact 真实闭包上正负编译：Publish 家族请求 claim（六字段）与确认 Published（三字段）载荷结构不同，确认 witness 传入 `OrchestratorProjection.recordPublishClaimed` 准入、或请求载荷传入确认构造器，均因 Fable 类型边界失败（正例先证全部符号与值合法）。Worktree 家族两 payload 同构，请求/确认区分在 `OrchestratorFactCases` case 与 fold 穷尽匹配层（上方行为测试证明），按卡面停止规则不另造测试专属 witness type。真实 Fable 编译归 integration tier，诊断正则若与实际 FCS 输出不符，由 DevOps 按实际诊断校准（ok 断言不变）。
- 002 驱动实际 Fork 生命周期，缺失有效报告时保留 pending，随后真实有效结果可完成同一 cell；不再等待 50 毫秒来猜它是否挂起。这是终态证据测试，不是所有 Requested-only 修复预算的证明。
- 003 在真实 dispatcher 的 Host 入口重开磁盘日志，核对 exact PromptKey 的意图已可恢复；另验已关闭句柄阻止发送。原源码 token 先后顺序检查已撤下。工作区边界已按 D03 落地：真 `ForkManagerJob` 链上，journal 写者关闭（`WorktreeCreateRequested` 无法 commit）时物理 `CreateWorktree` 零调用且 fork 不报成功；正例在 `CreateWorktree` 回调内冷读 ndjson——intent 已在盘、confirmation 未写、最终 intent 行先于 created 行、journaled worktree 不因后续 manager 阶段失败被释放（观察器经恰一次 create 证明有效）。append 物理悬置（held）无生产接缝未注入，由 fail 与时序两侧共同覆盖；manager-session 桩在 worktree 阶段后失败，不宣称完整发布链。旧 TODO 的 provider todo mutation 半项按 D03 裁撤，现行 Host completed→checkpoint 边界由本包 008 两条测试守护，不恢复插件前置 intent。
- 004 保留实际工作区确认不回退。周期投影会拒绝重复 receipt，并在 receipt 后重新 materialize 同 RequestId；该项允许由上层幂等拦截，不能据此断言产品违约。另一项「未确认请求幂等」随 Blogger 一并退役，改为真实入口的缺失证明 TODO；需验证同一完整材料重放、冲突材料与确认后重开。见 GAP-105 对初次判断的修正。
- 005 真实 recovery 对不可读、无证据、错误 PromptKey 和错误角色保持未决，只有正确物理证据结算；重复核对不重发。
- 006 受控 Host 在本次执行中实际写入副作用文件，然后返回 acceptance unknown；真实日志重开后仍待核对。它不等于独立进程中断。原“dispose 后写入”只得到已关闭错误，不能证明物理提交不确定，已替换。
- 007 检查真实取消观察、历史终态解码与结果渲染。删除只检查 URL 后缀、缺失 Surface 成员、以及空字符串重复测试；渲染用例增加有效角色和非空语义断言，避免空结果冒充合规。
- 008 保留真实协调/核对用例，并按新版 cognitive-workspace-003/006 验证生产 Assume runtime 的 Blob→提交顺序、两个拒绝边界与原子 canvas/todos 投影。受控端口接实际 fold；重建 runtime 证明从提供的提交事实恢复，不冒充真实磁盘、jq 或跨进程证明。旧待办实现不再加载，各效果家族的完整生命周期仍未全部证明。
- 010 用真实 codec 拒绝旧通用事实并接受现行 typed 事实；不再以源码包含某个名字为证明。
- 012 驱动真实发布程序及受控物理 port，确认变基、重新评审、Claim 和物理更新的次序，并拒绝缺少变基见证的 claim。内存 fixture 代替日志落盘，因此只证明程序的决策顺序，不重复声称持久化已证明。

原 004 中的 provider failure 幂等、两个不同 attempt 与失败预算，已有 provider-attempt-recovery 003/004/005 正式覆盖；其测试内自行过滤 Blogger/join 观察的“交错证明”不涉及生产分派，已删除。真实 handle 完成/退休用例合并迁回 delegation 013，仍检查重复操作的拒绝及状态。

完成节点后先正式构建，再用 `requirements/verification-system/tests/run.mjs` 与 `TESTS_MJS_FILES` 运行本目录及受影响所有者。TODO 不计通过。GAP-100 记录未闭合边界，发布清单不据旧测试宣称完成。
