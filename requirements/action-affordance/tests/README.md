# action-affordance 测试说明

本包测调用界面的可理解契约。名称、参数与非空检查只是局部事实，不能凑成五问完整或语义等价。

- 001：读真实插件注册结果，检查描述非空、`assume` 参数有描述。五问完整仍要语义审阅与实际选择场景。
- 002、006、010、013：各只有一条 TODO（双语高风险描述、执行副作用说明、委派职责、纪律不泄露编排），见 [GAP](../../GAP.md)。
- 007：静态工具目录不复用活跃身份名称；不据此说所有名称都是动词。
- 008：同一个生产检查器接受不同名称、拒绝两个被识别所有者复用同名；只覆盖它能提取的所有者。
- 009：实际 commission Schema 接受 `lead`、拒绝 `coordinator`，再检查中英资源不再宣传旧的 Coordinator/Lead。不是通用的措辞判别器。
- 014：`assume` 只有 `assumption`，成功不回显输入，也不维护画板。015：宿主原生 todowrite 的 provider 定义保持原样（`todos` 字段、无注入参数、重复触发字节一致）；该工具已退役，不再是 checkpoint 来源。

原 003–005 的废止名称名单、011/012 的自建正则已撤下；双语资源存在性由 provider-language 的门禁承担，语义对等仍需审阅。

## 运行

正式构建后，经 verification-system 的正式入口选本包、requirement-system 与受影响的 provider-language 检查。真实插件用例要开 integration，未开即跳过。TODO 会让正式入口返回非零。
