# 轨迹的宿主适配

用户交入的任务、宿主实际提供的材料和最终输出，经过这里转为 `wanxiangli/trace` 的捕获输入。生产 namespace 保持 `Wanxiangshu.Context.Trace`，调用方不需要了解文件位置。

`Capture.fs` 解释宿主消息、工具部件、重试注入、稳定身份与位置身份，以及旧 `g:/msg:/host-part:` provenance。它把这些关系转换为不透明身份键，向库提供当前 History、Blob 写入与 Journal 事实追加。事实格式、原始文字、错误映射和无 Journal 行为保持现状。

捕获的会话串行、批次去重索引、游标分配、正文与事实的写入顺序和前后差额归库 `TraceCapture`；这里不保留第二份历史或索引。呈现与终端通知仍由原调用方安排，不借捕获迁移改变产品策略。
