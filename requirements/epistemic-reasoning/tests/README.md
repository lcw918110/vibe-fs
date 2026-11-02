# epistemic-reasoning：退役测试说明

本包 WHAT/WHY 保留上游 `1450f49d` 的历史文本，当前 Sphinx 合同及逐条替代关系分别见 [sphinx-v2 WHAT](../../sphinx-v2/WHAT.md) 和 [SUPERSEDES](../../sphinx-v2/SUPERSEDES.md)。旧 36 条不再作为当前验收义务，本目录不恢复旧 runtime、MCP 驾驶协议、价格公式或测试入口。

本次三方迁移带回的旧测试与 APPLIES-TO 共 15 份输入原样保存在 [historical-tests](../historical-tests/)，文件改用 `.mjs.txt`；`001` 保留的合并输入含冲突标记，采用无损 gzip。它们只供审阅，不进入 `.test.mjs` 发现集。每份归档的解压后 SHA-256 与移动前完全相同，见 [SHA256SUMS](../historical-tests/SHA256SUMS)。完整旧施工仍可从备份提交 `1d7098a38f8419fa8586a6f695af61a18125f959` 读取；归档输入不冒充某个提交的完整可运行套件。

旧数学、幂等、恢复、取消和单一权威的有效义务仍按 SUPERSEDES 对应到新条款。当前 v2 测试保留真实的数值/选择/恢复分类/工具名单观察，完整运行时、持久化和 Host 轨迹的覆盖范围见 [v2 测试说明](../../sphinx-v2/tests/README.md)。旧 Core 测试通过、工具名字存在、声明退役，均不说明新内核已实现。
