# participant-identity 测试说明

规则见本包 WHAT。表示检查、静态检查、生产状态转换与插件场景各有范围，不能互相代替。

- 001：目录解析、完整身份恢复、独立字段冲突拒绝与 SessionId 缓存扫描反例。目录的固定列表不说明整个运行期身份不变。
- 002：持久 fold 用例实际读取每次失败记录之后的 IdentitySeed、完整身份与 exact run/root，说明失败折叠不改身份；不说明真实 provider 的全部重试调度。其余用例检查原子表示与现有边界扫描器。
- 003：Persona 与版本拒绝；不单独说明磁盘失败时 root 与身份都未安装。
- 004/005：真实派生消费者与 prompt 投影；同输入两次投影不能替代真实执行绑定切换。
- 007：Bookkeeper 私有身份表示；内部角色、Predictor 与公开调度的完整隔离仍需先消除规范歧义。
- 008：精确 owner/run/root、继承证据与历史往返；部分用例还牵涉其他所有者，本轮未全面迁移。
- 009：真实插件同容器身份替换、重启与实例隔离，以及生产 fold 的关闭边界。
- 010：新建身份与历史解码分开；反例覆盖 owner 派生的旧名字被自动赋成 Engineer，以及伪造继承证据进入 authority；历史 Inspector 仍按原角色解码且无写权。
- 011：当前与历史角色的持久标签，不依赖会话关联等无关前置。

## 运行

先构建，再把本包与受影响的 interaction-authority、dispatch-protocol、session-ontology、managed-session-lifecycle 编号测试交给 verification-system 正式入口。共享解码入口是否允许历史身份进入全部活跃路径，不能由 010 的两个反例推断为已关闭。
