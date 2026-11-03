# 变更进入固定目标

中立发布流程使用 `wanxiangli/change`。`ChangeHistory` 持有作业与工作树效果的唯一历史；`Projection.fs` 只保留产品类型与 DTO 映射，`Fold.fs` 将原有事实转成库消息。Journal 的事实字节与存储方式不变。查询按已提交历史进行，不在主程序维护影子 Map。

`ChangeJob` 编排候选、变基、短锁内发布、恢复和收尾；`ChangeRuntime` 编排工作树准入、Manager 激活及结果邮箱。产品侧 Program/Runtime 是 Git、Relay、Journal、锁与资源的类型连接。具体会话、提示词和工作目录约定留在 Host；库不读取这些宿主对象。

发布 claim 不是发布成功；已 fast-forward 而事实未补齐时先观察目标，只补记录，不重做效果。发布之后清理失败仍保留已发布结果。没有 Journal 时沿用原有无持久记录路径，不补一套内存“持久镜像”。审核与审计策略不属于本次抽取。
