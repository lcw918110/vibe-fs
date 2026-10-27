park(payload) 把本程序停在原表达式，回执包含 name、state、reason、payload 和完整源码，已落盘写保留。

另提交含 name、program、intent 的工具请求，在新控制程序内调用 `return await this.unpark("target", value)`。只有显式 park（含 where 歧义）接收 value；call 取得原命令真实结果。目标共用控制者期限，之后只恢复控制者不会隐式再恢复目标。

where 歧义先读 candidates 和 nearest，再注入选定 span。取消时由新命名程序调用 `this.abandon("target")`，确认收尾后才报告 abandoned；强制终止不保证 finally。parked 或运行中的名字保持占用；程序结束后名字可开始新运行，原回执仍按原调用重放。结果未知的副作用不重跑。
