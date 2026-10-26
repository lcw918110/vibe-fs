暂停回执包含 name/state、原因、payload、完整源码和命令事实。SDK 排空明确标示命令尚未启动。delta 是新增输出，与最终结果独立。源码行用 ` | `；只有可靠定位时才用 `>| `。

另提交 { name, program, intent } 请求，按名字 unpark 或 abandon 目标。signal/send 需要 Exec，不恢复代码。unpark(name,value?) 共用新请求已有期限。
