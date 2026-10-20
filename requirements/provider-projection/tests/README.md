# provider-projection 测试范围

2026-10-05 Grounding新增LlmFacing.OriginalMaterial：已经实际读出的正文原样保留，只有来源metadata经typed Data生成；通用Instruction/Data布局与合同不变。有限实际调用证明归grounding007/012：gen137正式相关223/223、1454/0及真实Host serialization1/0。原正文、完整carrier、canonical工具原结果与旧持久呈现分别验证，不能据此宣称所有表示路径只渲染一次、legacy/user或完整Long Stroke已证，见[本批记录](../../../proposals/archive/2026-10-05/原始材料载体与后缀重放-2026-10-05.md)。

- 001/002/004/006 直接调用生产投影，验证快照隔离、输入不被修改、Host metadata 对齐、规范排序、同值幂等及不同 anchor/rows/metadata 的冲突。同输入重复渲染不等于执行了在线/重放全链。
- 003/005 还保留 Host codec 的有效编解码回归。这些用例不完整证明 Semantic/Wire 的类型隔离或业务只提交两种意图，后续应按实际边界拆分归属，不能为统一目录而丢掉覆盖。
- 007/013 使用仓库现有检查器及受控正反例。格式门禁目前主要识别直接调用 SyntheticToml，无法证明所有散文拼接和间接调用均受统一所有者管理。
- 009 覆盖接收语义（指令面与数据面）及确定性渲染。
- 010 保留实际 pair-programming 注入不伪造消息、原输入不变的用例；这不证明所有合成文本都不能取得权威。重复的“导出函数存在”断言已撤去，实际权威隔离仍为 TODO。
- 014 执行实际 Join 结果与热启动附录组合，确认后出现的责任说明也在全部数据之前。仅凭结果正确不能证明所有调用路径都只渲染一次。

正式构建后选择本包、requirement-system、participant-horizon 013 与 repository-investigation 007—009。GAP-082 汇总全链、权威隔离和统一所有者的缺失证明；TODO 不计入通过。
