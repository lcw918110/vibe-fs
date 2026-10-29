# cognitive-workspace — WHY

这里记录的是一个已经做出的删减决定：持久 JSON 画板把工作判断、待办 UI、压缩 checkpoint 和一套 jq 语言绑在了同一个工具上，模型使用效果并不好，系统边界也因此变得模糊。

现在三件事各归其位：

- `assume` 只负责“已笃定”的认知承诺点；
- 编程面 `todowrite-for-<role>` 负责当前待办，插件不改写工具定义与参数；
- context-compression 由显式 `scope.kind=resetScope` 注解屏障管理细粒度状态闭环与上下文清理。

旧 Cognition journal 仍须可读，是为了诚实恢复历史，不是为了保留产品能力。兼容读取必须停在 no-op tombstone，不能借迁移之名重新生成 canvas projection。

