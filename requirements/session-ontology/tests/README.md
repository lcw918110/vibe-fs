# session-ontology 测试说明

WHAT 规定容器分类、关联与复用边界。这里只说明设施与覆盖范围。

- 003/004：专用受托调用生产分类函数，不在 Surface 里另写常量。Engineer 到现有 attachment 值的映射只作当前生产词汇，不赋予旧角色新的调度权限。
- 005/007/008/009/010：驱动真实关联投影——双向关系、幂等、冲突拒绝、解除关联与只读派生。008 覆盖 Work 已有 Companion 时不得改挂到另一个 Companion，否则会留下悬空关系。009 不再拿任意会话 ID 命名旧角色来充当资格。
- 006：真实持久关联的父节点投影；旧 physicalParents/familyRoot 只在 Surface 里重写祖先遍历，没有调用 Host 创建接口，已改为 TODO。物理扁平创建仍要真实适配器观察。
- 011 的固定 satellite 字符串表与 014 的 Teacher 反例已改为 TODO；无实际语义来源的 Surface 常量已删除。
- 012：类型化 Bookkeeper attachment 携带准确事务 ID；其私有身份与公共角色排除归 participant-identity-007。
- 原 015 的复用/身份关闭情景由 participant-identity-009 的同一生产情景覆盖，重复文件已删除；WHAT-015 保留「容器操作不冒充身份关闭」的边界并引用身份所有者。

仍待统一：003 的旧角色名称、009 的「恰一个」与延迟初始化、007 与 011 的持久/进程内分类关系、014 的「平坦」含义。Bookkeeper 事务运行、Strength 单活/释放/不复用、真实 Host 身份隔离与完整四格分类仍未覆盖。

## 运行

先 `node scripts/build.mjs`，再经正式运行器：

```sh
TESTS_MJS_FILES="$(rg --files requirements/session-ontology/tests | rg '/[0-9]{3}\.test\.mjs$' | sort | paste -sd, -)" node requirements/verification-system/tests/run.mjs
```

TODO 阻断完整验收；局部断言通过不代表现场巡检通过。
