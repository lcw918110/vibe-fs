# OpenCode 请求投影

让宿主即将发给模型的请求符合当前委托、Scope 和临时咨询需要，同时保留宿主历史原件。本目录解释 OpenCode 消息、system 数组、工具回执和 transform hook 的形状与次序；不决定领域状态。

`RequestProjection` 拥有临时咨询的请求快照与请求副本变换顺序。`SystemProjection` 只替换产品拥有的 system 段落。`MessageProjection` 解释宿主消息中的文本、工具与附件。对原始历史的归档、Scope 的持久内容、交付和恢复决定仍交给已有主人。

产品所需的能力组合在 `PluginTransformComposition`。它把 Trace、Scope、Relay、交付等已有能力接给投影；不要把这份产品组装原样搬入中立核，也不要让适配层复制这些能力的状态。主插件通过此组合注册真实 hooks。

保持现有行为：临时咨询复用父请求视图；模型选择按现有顺序读取；旧工具结果的已发送字节不随新一轮重写；system 未拥有部分与数组身份保留；请求副本的压缩和 thinking 处理不改写宿主原历史。修改投影次序时先看这次实际影响的 hook 路径。
