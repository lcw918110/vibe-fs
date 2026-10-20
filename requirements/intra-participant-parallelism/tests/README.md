# Fission 测试及证明范围

WHAT 是合同；本文记录当前证据，不增加产品义务。先用 `node scripts/build.mjs` 生成当前产物，再通过 `requirements/verification-system/tests/run.mjs` 指定本目录编号文件，启用 `WXS_TIER_INTEGRATION=1`。TODO 不计通过；正式 runner 因缺证返回非零是预期结果。

| 条款 | 已有证据 | 尚未证明 |
| --- | --- | --- |
| 001 | 无完整执行证据；07 保留真实 inherited seed 验证 | 实际多 lane 全程身份、责任与外部义务不变 |
| 002 | 实际 parser 保留空白、换行、Unicode，拒绝不足两项及任意空白项 | Host schema 和实际 dispatch 对相同材料的处理 |
| 003—005 | 真实 admission owner 配受控外部端口；同 parent、独立 session、初始材料；三 lane 每个创建/发送位置失败的回滚；暂停最后一次启动证明退休先后 | 真实绑定、durable commit、未知接受、失败清理；无业务 abort、故障恢复或子任务取消 |
| 006—008 | 生产纯目标、交付账与 keyed bundle；重复、隔离、非法 index、冲突双向 merge | 实际确定性广播、亲和路由、持久重放与物化 |
| 009 | 每个缺失记录/广播债权阻止纯收敛；ring 路由；真实旧 caller observer；显式 kick 的 reconciler 观察完成 | terminal 到 Fission 的生产接线、后继恢复链及原 completion cell 恰好完成一次 |
| 010 | 待证 | 真实 facts 重放、进程死亡、中断归档与不隐式重启 |
| 011 | 真实 admission pending/active 拒绝重复，release 后可再准入 | lane 到 logical owner 映射与递归拒绝 |
| 012—013 | 角色 predicate；实际 Engineer 根/子 chat.message 权限；强制根调用在解析前拒绝且无 Host 副作用 | 所有入口权限同源；单个根拒绝不证明所有身份组合 |
| 014—015 | 真实 settlement 纯裁决；不同记录到达顺序的有序 bundle 和 ring 终点 | 实际控制面接续、乱序完成下唯一 N−1 takeover |
| 017 | 配置投影；真实 Manager/DevOps 工具拒绝，伪称 Engineer 无效且无副作用 | 完整公式、只读 Sphinx Engineer、所有 Manager 生命周期及历史 Fission 只读重放 |

`support/admission.mjs` 仅给生产 admission 注入 Host 端口并记录实际调用，不自行复制准入算法。插件用例运行生产工具与 journal，但 Host 是受控端口，未运行真实模型。`missingIdleTerminalBridgeScenario` 直接给 scheduler 发布投影并 kick；名称不能证明 Host terminal 回调已接通 Fission。

原 `startedLane` 接缝忽略物理 session，仅返回固定的 `hasAgentId/hasHandle/hasParent=false`，已删除。原源码词形与调用顺序扫描不能证明真实收敛/恢复，转为 GAP-158。两项发布节点同步降为 TODO。
