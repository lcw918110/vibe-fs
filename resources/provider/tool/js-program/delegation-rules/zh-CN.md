只使用当前权限实际生成的 fork(payload)、resume(payload)、commission(payload)、devops(intent) 以及获授权的 fission、horizon。明确表达本次 charge 或 intent；既有准入、归属、评审门禁、投递结果和失败规则不变。成员返回结构数据，由工具边界渲染。投递结果未知时，先观察原结果，再决定是否发起另一笔投递。

依赖由模型明确安排：程序派工，独立工具 join() 收拢有资格取得的结果，再提交下一个命名程序。join() 无参数，不带 name、program、intent；归属、批次、终态凭据、去重和中断规则保持不变。结束本次等待不取消子工作，也不构成业务交付。

每次编程请求提供 name、program 和顶层 intent，所有操作共用准入期限，成员不能续期。resume 续做受委托道路；unpark(name, value?) 从新的控制程序恢复命名程序。
