# repository-programming 测试范围

本包验证能力生成、真实沙箱、文件快照、编辑及事务。保留既有真实正反例；WHAT 规定必要规则，这里只说明用例覆盖和局限。

- 001—005、021—022 的生成器与源码门禁是局部证据，不等于实际 Attempt、Provider 可见面和运行门禁全部一致。003 不再用两次相同调用冒充 fast/deep 切换；特定例句不作为角色行为的证明。005/016 中 `BuiltinToolDescriptionHook`（`builtinTools`/`annotate`/`validateRecommendation`）已随直连宿主文件工具退役。
- 006 真实执行程序，检查可见全局、同步超时、过期后 API 拒绝和输出预算。多字节结果按现有接口的 UTF-8 字节预算验证，包含恰好等于上限和超出一字节的对照。异步等待、await 后计算及内存隔离尚缺受控进程证据，不能从同步循环超时外推。
- 007 的快照不变用例实际提交后核对 `cat` 取得的旧快照和 `write` 后的新磁盘。007/010 中 `FileToolsSurface` 的直连工具旧回归已随 `FileTools` 退役移除，由编程面 `cat`/`where`/`write` 承担契约。
- 008—016、018—020、023—024 在直连文件工具（`read`/`write`/`edit`/`glob`/`grep`/`patch` 与插件 `mv`/`rm` `ToolSpec`）及旧 `file`/`edit`/`rewrite` 成员退役后，对齐到 `cat` + `where`/`whereAll` + `slice` + `write` 组合、Write-only 表面边界、`where` 歧义/未命中 park 载荷、`write` as-is 落盘语义（011/019 验证非法返回后 as-is 已落盘改动保留）以及编程面 `mv`/`rm` 的 POSIX 语义（020）。
- 017 证明连续事务读取前一次提交；同一助理消息的实际并行调用由宿主按序执行仍是 TODO。描述里写着安全不算执行证据。
- 025 的真实 EventStore 追加、fold 和同进程日志重开继续保留，名称改为实际场景；没有注入不确定追加或杀进程。删去辅助模型构造 incident 自证和源码词形代替 mandatory 注入的断言，真实结算先于唯一熔断留 TODO。
- 026 验证 `runObserved` 与 `AccessTracker` 对显式读取（`cat`）、目录枚举/元数据扫描（`ls`/`stat`）、失败中断与提交后修改（`write`/`mv`）的实质访问分离：内部写前快照不进入显式读取，失败程序不触发已提交访问观察。
- 027 证明 Engineer 与 DevOps 相同能力生成相同成员；直连文件工具已退役，两者统一经生成的编程工具访问文件系统。
- 028—029 以 dist 产物真实运行：programPromise 句柄与 deadline 的过期、重置与催断，逐笔落盘的成功、失败与 FILE_CHANGED 预检结果面。异步等待与内存隔离的受控证据仍沿 006 的 TODO。
- 030—037、039 真实运行 park/unpark/abandon 全周期：parked 载荷与渲染、handle 恰好消费一次、abandon 的 finalizer 与清理窗口、并发上限与释放、进程内存活；031 与 039 另含源码静态断言，核对三件套接线与现场表归属。会话 close 的 reap 仍是接线证据，未以真实会话关闭运行。
- 040 真实运行 rm/mv 的 POSIX 语义、越界拒绝、durable 单笔记账与稳定失败码目录；EXDEV 只查目录登记，不造真实跨设备场景。
- 041 是生产接线的源码静态断言：lifecycle persistence 沿 PluginHostInterop → ToolRegistry.create → JsToolSpec/execute 与 PluginBoot → PluginRuntimeScope → ClearSession 贯通，执行链不留硬编码 None，四类事件同住单一审计流，ParkedSites 表不触碰 durable。它证明接线存在，不证明运行期审计真实落盘。
- 045—047 真实运行主原语面：cat/ls/stat/mkdir 的成功与稳定失败形态、write 覆盖与读快照冲突、whereAll 规范化匹配与 from、where 唯一/零/多三态与 park 注入、组合模式（find/replace、批量替换、append、锚点切片、grep/glob 组合）。长程异步与内存隔离仍沿 006 的 TODO，不在本批外推。
- 048 真实运行 parked source：完整程序回传、挂起行 `>| ` 标记、where 调用点标记与渲染字面块；栈解析失败的降级路径未单独造桩。
- 049 以真实 ToolSpec 装配检查 unpark.value 的 host schema 为可选任意值，并核对描述中的自由注入责任文案。
- 050 真实运行 js.call 单进程循环：规格拒绝、前台命令与结构化结局、onExit 退出码派发、check abort 杀死命令、check park 挂起与 unpark 继续、maxDuration 超时。
- 051 真实运行多进程代数：spawn 后台、waitFor 端口就绪、signal 收割、waitFor 超时与未知后台稳定码、后台崩溃经 backgrounds 观察并 abort。
- 052 以本地限速 Range 服务器真实驱动 `wget -c`：中断后断点续传、部分文件保留、逐文件 park 汇报、404 退出码 8 分派给 onExit。wget 缺失或网络不可达的环境不适用。
- 049 同时核对生成工具声明 `program/unpark/abandon/view` 四个参数与 view 自由值描述。
- 054 真实运行 deliver：显式终值结束程序、哨兵被捕获后程序存活、非 JSON 值以 INVALID_RETURN_VALUE 失败、每个生成面携带 deliver 成员。
- 055 真实运行 vm 并行边界：Promise.all 并行读写、allSettled 兄弟失败不撤销已落盘写、十六路 cat 批处理。
- 056 委托原语端到端：fork/join/fission 返回数据信封（缺名派生、拒绝信封不抛、无宿主 DELEGATION_UNAVAILABLE）、`dag` 波次推进与依赖记录传递、节点失败 park 后注入 `"skip"` 收尾。
- 057 真实运行 ls 的 ignore 语义（逐级 `.gitignore`、否定模式、嵌套规则、根 `.git/info/exclude`）与 glob/grep 退役后的组合写法（walk + `cat` + `whereAll`）。

2026-10-18 交付验证：`node scripts/build.mjs` 构建成功（169 surfaces、813 模块链接闭合），本包 001-059 全套 278 例 0 败 0 跳 7 todo（045-058 以本次 dist 产物真实运行：九原语、where 三态与组合模式、parked source、unpark 自由值 schema、js.call 单进程/多进程循环、wget 断点续传剧本、view 声明捕获、deliver 显式终值、vm 并行边界、委托原语信封与 dag park、ls 的 ignore 语义与 walk/search 组合、file/edit/rewrite 退役后的组合替代，以及 js.call 的交互 send 形态）。直连文件工具已整体退役（GAP-235）：005/007/010/016 的旧实现回归删除，020 改走 js `mv`/`rm` 成员，026 改走 `runObserved`。全量 unit 套件同版本 **865 文件 5113 例 0 败**（GAP-231 ~ GAP-237 CLOSED）。006 异步/内存隔离、017 同消息并行调用、027 直接与程序工具共享保护相关 TODO 随退役收窄；未闭合范围见 GAP-088。
