# OpenCode 请求延迟看护

只计量一次模型请求消耗的时间，不把工具执行当成模型延迟。统计模型仍由 `wanxiangli/degeneration` 提供；本目录拥有 OpenCode 的请求计时、打断回执、延后续发观察者及其释放。

`LatencyWatchdog` 按 session 和精确 provider-run 身份拥有在途状态。`RequestLatency` 解释真实宿主事件，连接单调时钟、计时器、打断、续发、终止通知和文案，并持有续发观察者。插件仅把 event、tool-start、session-delete 和 dispose 交给这个对象。compaction 是否正在发生由现有 compaction owner 提供。

旧请求的迟到事件不能取消后继计时。工具开始即结束模型样本；后来的 tool-calls 事件不能重复计入。打断被拒、尚无回执、物理取消和最终接纳仍是不同事实；续发排队不能冒充已经接纳。删除会话或释放插件时取消计时和观察者，删除会话还清掉其统计。

阈值、学习参数、默认超时、恢复文案和 managed-child 判断沿用当前实现。本次整理不启用新产品能力，不改变审计，也不把 OpenCode 协议塞进中立核。
