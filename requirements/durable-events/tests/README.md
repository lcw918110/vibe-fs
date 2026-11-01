# durable-events 测试说明

WHAT 是规则依据；本文件说明各条用例实际跑到了哪里。

- 001–018：驱动实际 canonical codec、EventStore、Journal、payload、Git Hook 与 Integrator，覆盖字节身份、损坏拒绝、提交门禁、提交前后 Current、并发分支、retention、合并顺序与物理操作计数。临时目录与受控故障只用于隔离被测执行。
- 014：成本用例比较同一原生 ready heap 的真实 cursor 比较次数，墙钟只作诊断；512 writers × 16 = 8192 次，先核对完整输出、数量与唯一性，再核 3NH+2K 上界。
- 017：真实 activated append I/O 记录——128 条历史由原 EventStore 生成，独立 meter 进程在 activation 后清零，独立 cold 进程核对完整事件与 head；正常 append 不读旧事件内容、不碰 Git filesystem/CLI 与子进程。
- 019：让 Structural、Strength、Casebook、JsTransaction 的真实事实改变 Current，并重开同一磁盘历史核对；反例经 `programWithoutRegistration` 在各自临时目录移除真实注册。
- 020：Journal boot 不创建 writer，首次业务 append 才落盘；workspace capability 获取时容忍未消费的损坏历史，消费时拒绝。没有借此宣称整个插件加载流程已验证。
- 021：坏事实与 cut 一起落盘、同进程重开可重放重置；产生坏事实的实际进程退出与下一代进程恢复仍是 TODO。
- 022：locality 缺边探测。当前集合 20 个：8 个 EventStore contract、2 个 focused runtime、4 个扩展 contract、6 个扩展 focused runtime，另有 JS capability owner 31 fs；详见 [README](../README.md)。
- 023：日志 port 的原子发布夹具先经真实 dispatcher 接纳父子 Root，再只暂停目标 `HandleLinked` 的追加；三个 canonical handle 视图同时变化，revision 精确增加一次。
- 024/025：不再用自建 fatal 模型或源码字样检查结算与退出。025 实测同一 `Prepared` 身份不同载荷导致 `StorageInvalid`，不产生 semantic cut。

## 运行

先 `node scripts/build.mjs`，再经 `requirements/verification-system/tests/run.mjs` 的 `TESTS_MJS_FILES` 跑默认层；真实编译走 integration 的既有 compiler 预算，不在 unit 强开 tier。TODO 不算通过。
