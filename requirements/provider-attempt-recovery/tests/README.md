# provider-attempt-recovery 测试

WHAT 是合同；本目录说明当前证据，不补充产品规则。

- 001—007：实际预算代数、durable fold、ledger 准入和磁盘重开。005 使用实现公开的当前有限上限，不把数值 12 升格为产品要求；重开仍在同一进程。007 的两个可执行 TODO 揭示耗尽后新事实被忽略、超预算后继被接受，见 GAP-140。
- 005/019：通过薄接缝调用生产 Retry.attempt；注入可暂停的 admission 与 redispatch 端口，观察授权前不重投、非 provider 失败不进入 admission、同一 exact licence 传递。这里的 redispatch 是受控端口，不是实际 Host send，也没有接入真实 ledger；ledger 证据另列，不能拼成完整流程。
- 006/013：实际 planner 的新建身份观察及不同 request kind/role 的对照，不声称跨物理重试保持身份。016 直接读取生产 RequestKind 的成功计账判别，不以手工调用 recordSuccess 假装上层会正确选择。
- 008：实际内容有效性分类。工具错误不等于 provider 失败；分类本身不证明预算没有变化。真实 Manager repair 与 reconciliation 仍待补。
- 011/017：计划冻结/绑定/消费；不冒称完整发送。
- 019 的 retry-owner 扫描器是当前路径和词形的启发式检查，两项违约 fixture 只证明已识别模式会报警；它不是语义完备或替代实现可接受性的证明。真实 engine 矩阵与此分开。
- 020：同一事实序列折叠及计划操作前后的 manual 计数。零 manual 不等于没有后台 resume；完整重启副作用边界仍待补。
- 021：真实路由 runtime、可替换的受控调度器、单次 target witness、durable retry 判别与 recoveryStep 纯策略决策（首次原样重试，二次拉黑重试）。retain/condemn 由测试显式选择；ordinary/delegate 组合证据仍 TODO。
- 022：真实进程内 stop fence，覆盖其它 key、重复、迟到、撤销后不能复活。尚未连到真实 Host 事件和物理发送。
- 023：真实 recovery Host 对指定 session 的窄化扫描、端口调用与 manual 处置。GAP-141 的实际反例显示无 capability 时仍为 Accepted；与 managed-chat-execution[012] 的 manual/blocked 合同需统一。boot 后自动扫描与进程重启仍待补。

用 `requirements/verification-system/tests/run.mjs` 的 `TESTS_MJS_FILES` 选择本包编号文件，节点前先刷新正式构建。局部直接执行只作诊断。TODO 不计为通过；正式 runner 遇 TODO 返回未完成验收。
