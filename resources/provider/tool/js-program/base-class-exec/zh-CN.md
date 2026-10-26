async call(command) {
    // 先等本程序既有 SDK 操作结清，再启动一条 shell 命令。
    // 漏写 await 也停住本程序。排空、等待及后续 call
    // 共用顶层 intent 窗口的剩余时间；到期不杀原命令。
    // 最终结果保留 stdout/stderr、真实 exitCode 或 signal、字节数、
    // durationMs、明确截断和原始 outputFiles；delta 不消费最终结果。
  }

  async signal(name, signal) {
    // 向指定程序当前命令发信号，不恢复其代码。
  }

  async send(name, input) {
    // 向指定程序当前命令写输入，缺换行时补上，不恢复目标。
    // 接受输入不证明命令已处理。
  }
