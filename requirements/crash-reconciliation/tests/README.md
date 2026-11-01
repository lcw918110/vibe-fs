# crash-reconciliation 测试说明

WHAT 是合同。这里只说明各条用例实际跑到了哪里，不增加隐含的恢复规则。

| 文件 | 实际观察 | 尚未覆盖 |
|---|---|---|
| 001、006、008 | 实际 quiescence gate 的 owner 隔离、单次消费、active tool、撤销、exact release | OS 重启；真实 Host abort 解码与物理发送处的完整授权链 |
| 002、005、009、010、012 | 实际 child resolver、completion decoder、handle projection 与恢复结果映射 | 磁盘恢复、blob-before-fact、实际 join 消费、完整投递幂等 |
| 003、007 | 实际恢复决策；`TurnUnknown` 不作为业务终态发布 | 未知外部 effect 的物理查询、全体普通入口重入 |
| 011、013、014 | 实际闭包、permit membership、合并与授权函数 | 每次生产 join/await 都调用检查；全家族的物理恢复 |
| 015 | 实际附加会话分类、SyncDelegate 的观察/创建端口与并发共用创建 | 重启替换时「先证明消失、Close 再 Link」的完整链 |
| 018 | 实际插件 config hook 不注册显式续传命令 | 真正重启归位、中断历史可见性、观察者不触发副作用 |
| 020 | 实际 canonical codec/fold、child load selection/void、取消结算桥接、JoinDrain；真实 child 结算的持久写入、失败传播与 journal 重开 | 插件激活顺序、普通执行前的结算门禁、真实命令不重放、进程崩溃 |
| 021 | durable handle 按 id/byname 查询、binding cache fallback、由已折叠 Fission fact 解析 lane | 所有 reuse/placement/await 入口接线、真实重启、物理资源所有权 |
| 023 | 真实插件双 incarnation：无关会话 transcript 读门控下的第一条件 admission 不阻塞；本会话遗留恢复先于准入；无关会话恢复不丢 | 屏障故障的诚实传播与等待者共享单飞只在 journal 单元覆盖 |

- 011 的接缝构造已授权输入夹具，调用生产 permit/membership 函数，但不实际恢复这些成员。009 的 trace 检查器拒绝乱序夹具，合法夹具不是产品真实执行记录。
- 015 的受控 SessionPort 在观察发送后拒绝自己挂起的请求并等调用结束，关掉测试自己创建的生命周期，不扩大正式监督时限。
- 006 的 gate 与 dispatch-protocol-002 的 Manager owner 接续共用同一发送边界：发送消费有效许可，拒绝被新 Human 撤销的旧许可。002 的 SDK 零发送与 durable claim 终态不能代替本包的 OS 重启观察。
- 020/021 的 LoadRecoverySurface 保留 canonical fact 转换、实际 owner 与投影观察；`settleChildRuns` 只把真实 JournalHandle 转交 ChildWorkRecovery，不模拟 append。020 的 integration 回归经真实 Dispatcher 建立合法 parent 与 Engineer/DevOps 子 run，重开 journal 后结算。这些没有驱动 PluginRecoveryWiring、插件 activation 或 OS crash。
- 004、017、019 与 020 的整链部分仍是 TODO。原 JoinSurface 自复制校验规则、原 DevOps crash helper 只启动进程不跑命令，这两处假通过已删除。

作废与「不写完成」不同：作废结清父方未解除的工作义务（join 以 `voided` 报告一次后退役），「不写完成」只是没交付。结算按孤儿幂等，每个孤儿只产生一条作废事实。

完整进程中断、持久事实重开与物理发送切点见 [GAP](../../GAP.md)。019 的 registry/symbol 归属、020 的 DevOps 模型与 handle 身份仍待决。

## 运行

先 `node scripts/build.mjs`，再经 `requirements/verification-system/tests/run.mjs` 选本目录编号文件。TODO 不算通过。
