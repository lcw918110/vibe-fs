每次 todowrite 请求提交必填字符串 name、program、intent。Scope retention 走 scope.kind（共同法 «Scope»），不再使用回合估计字段。JS 程序自身只接收三个执行字段。工具在准入时按 UTF8Bytes(intent) × 50ms 一次性确定期限。本次所有操作及被唤醒目标共用剩余时间；进入下一次 call 或 unpark 不能重置或延长它。

独立 SDK 操作可用 Promise.all 或 Promise.allSettled。call(command) 先等本程序之前发起的 SDK 操作结清，再启动一条命令。在途操作要等待，不拒绝、不重发、不取消；真实失败和已有副作用保留。排空与命令等待消耗同一入口窗口。

call 在表达式处停住程序，即使漏写 await 也不能越过。Promise.all 中第二个 call 不能并发启动；命令并行用不同命名程序。宿主、既有 SDK 操作、输出处理和其他程序继续响应。

call 等待期间暂停扣减纯 JS 执行预算，回到模型代码后续用余量；请求期限持续推进。命令运行时长只是观察，窗口到期不杀正在运行的命令。

排空期间到期就停在启动命令之前；既有操作随后可以结清，但不能自动启动命令。命令等待到期则保留原命令；之后退出仅缓存真实结果，不自行恢复代码。

控制其他程序时提交新命名程序，并在顶层提供 intent。按权限使用 unpark(name,value?)、signal(name,signal)、send(name,input) 或 abandon(name)。被唤醒目标共用控制请求已有期限。到期时控制者与活动目标一同暂停；恢复控制者只推进自身，要继续目标就显式 unpark 其名字。回执保留目标名字和状态。

只有显式 park（含 where 歧义）接收 value；恢复 call 得到原命令真实 CallResult。signal/send 不恢复目标；信号发出不等于退出，输入被接受不等于已经处理。

abandon 请求取消和清理。确认资源结清后才报告 abandoned，未完成或强制清理按事实报告。正常展开时运行原生 finally，强制终止不说明它运行过。已落盘写入保留。

暂停 delta 是新增输出，不消费最终 CallResult。保留真实退出码或信号、留存输出及明确截断。效果未知的操作不重跑。程序 return、命令 exit、工具响应和 Work 生命周期决定各自成立。
