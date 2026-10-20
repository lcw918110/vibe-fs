# session-ontology 测试说明

WHAT 规定容器分类、关联和复用边界。这里说明设施和证据范围，不增加新的分类或角色规则。


- 003/004 的专用受托现在调用生产分类函数，不再在 Surface 中另写相同常量。Engineer 到现有 attachment 值的映射只作为当前生产词汇证据，不赋予旧角色新的调度权限。
- 005/007/008/009/010 驱动真实关联投影：双向关系、幂等、冲突拒绝、解除关联和只读派生。008 新增 Work 已有 Companion 时不得被改挂为另一 Companion 的回归；否则会遗留悬空关系。009 的任意会话 ID 不再被命名为旧角色来冒充角色资格证明。
- 006 保留真实持久关联的父节点投影；旧 physicalParents/familyRoot 只在 Surface 中重写祖先遍历，没有调用 Host 创建接口，已改为 TODO。物理扁平创建仍需真实适配器证据。
- 011 的固定 satellite 字符串表和 014 的 Teacher 反例不证明副本生命周期或全部合法拓扑，已改为 TODO。相应无实际语义来源的 Surface 常量已删除。
- 012 验证类型化 Bookkeeper attachment 携带准确事务 ID。其私有身份和公共角色排除归 participant-identity-007，原有断言已移交。
- 原 015 的 Session 复用/身份关闭情景由 participant-identity-009 的既有同一生产情景与插件验证覆盖，删除重复文件。WHAT-015 保留容器操作不冒充身份关闭的边界，并引用身份所有者；这并不表示所有容器操作的非干扰性已全面证明。

003 的旧角色名称、009 的“恰一个”与延迟初始化、007 与 011 的持久/进程内分类关系、014 的“平坦”含义仍需统一。没有为了绿色假定这些问题已定稿。Bookkeeper 事务运行、Strength 单活/释放/不复用、实际 Host 身份隔离及完整四格分类证据尚未闭合，见 GAP 与施工记录。

先运行 `node scripts/build.mjs`，再使用正式运行器：

```sh
TESTS_MJS_FILES="$(rg --files requirements/session-ontology/tests | rg '/[0-9]{3}\.test\.mjs$' | sort | paste -sd, -)" node requirements/verification-system/tests/run.mjs
```

TODO 阻断完整验收，局部断言通过不代表现场巡检通过。新基线验证范围见[本批记录](../../../proposals/archive/2026-10-03/35模块PR施工记录-2026-09-28.md)。
