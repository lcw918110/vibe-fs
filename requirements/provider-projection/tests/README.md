# provider-projection 测试说明

- 001/002/004/006：直接调用生产投影，看快照隔离、输入不被修改、Host metadata 对齐、规范排序、同值幂等，以及不同 anchor/rows/metadata 的冲突。同输入重复渲染不等于在线或重放全链。
- 003/005：另保留 Host codec 的编解码回归；这些用例不说明 Semantic/Wire 的类型隔离，也不说明业务只提交两种意图。
- 007/013：用仓库现有检查器与受控正反例。格式门禁主要识别直接调用 SyntheticToml，无法说明所有散文拼接与间接调用都归同一所有者。
- 009：接收语义（指令面与数据面）与确定性渲染。
- 010：实际 pair-programming 注入不伪造消息、原输入不变；不说明所有合成文本都不能取得权威，权威隔离仍是 TODO。
- 014：实际 Join 结果与热启动附录组合，后出现的责任说明也在全部数据之前；结果正确不说明所有调用路径只渲染一次。

## 运行

正式构建后选本包、requirement-system、participant-horizon 013 与 repository-investigation 007–009。TODO 不计入通过。
