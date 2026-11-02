# OpenCode 会话传输

这里解释 OpenCode SDK/HTTP 的会话发送、读取和目录协议。产品从 `OpenCodeTransport.openConnection` 取得同一次选择的 Sending、Snapshot、SessionCatalog；PluginBoot 保存这组端口，Host、恢复和公开产品端口复用它。没有第二份会话缓存、Journal、family 或工作状态。

选择顺序保持 client.session → serverUrl → baseUrl → localhost port。选定 SDK 后，某方法不存在就是该端口原有的错误，不改走 HTTP 重发。持有原 client 引用，操作时仍按原时点读取方法/会话；不冻结宿主对象。

SDK v1/v2 请求字段、Unicode workspace header、prompt metadata 和 steer 参数按原协议处理。已入队不等于物理接纳，晚到拒绝或未知仍交给调用方原 listener；适配不重试，也不替产品结算工作。消息投影继续让 Parts/ToolParts 解释同一失败状态，消息次序、compaction标记和身份读取不变。

不同端口的错误出口不可合并：Sending/Snapshot 保留字符串 Result，SessionCatalog 保留 HostError 的 NotFound/Refused/Transport 等类别。Sending 的 HTTP CloseChildSession 是 POST abort；SessionCatalog.Close 是 DELETE session，二者原语义不同。SDK close能力的选择次序也不改。

`InjectedSessionPort` 中 managed/root 保护、family topology、listener-before-send 和 ModelRouting 结算仍是产品规则；不因它调用宿主而搬进此传输对象。本次移动保留真实SDK/HTTP实现，退出原三份选择器和产品组装处的重复构造。

程序待改：`Snapshot.fs` 的 HttpSessionPort 和 HttpSnapshotPort 各自把 fetch Emit 放在类内，当前 Fable 输出将接收者放到了 URL 参数位置。这会使 HTTP 快照和 catalog 在发出请求前失败；发送端的模块级 fetch 不受此问题影响。原 `OpenCode/Host/SessionSnapshotPort.fs` 已有同样写法，本次保持原行为，不把迁移当作修复授权。`session-transport.test.mjs` 保留两条真实 HTTP 失败用例，后续单独修复这处调用边界。
