暂停中的程序：
{{handles}}

按这些程序名字决定下一步。提交新请求，提供新 name、program 和顶层 intent，在程序中调用 unpark(name, value?) 或 abandon(name)。只有显式 park（包括 where 歧义）接收 value，call 取得原命令结果。

本次唤醒的所有目标共用控制请求的准入期限。到期时控制者和仍活动的目标一同暂停。只恢复控制者不会再次恢复目标；要继续哪个目标，就显式 unpark 其名字。命令退出及 signal/send 都不恢复暂停程序。
