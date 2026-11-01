# execution-model-routing 测试说明

WHAT 定义权威。配置路径、公开 ABI、exact identity 与次序属于合同；当前模板选了哪些供应商、多少并发、内部函数叫什么，不是通用规则。

| 范围 | 实际观察 | 边界 |
|---|---|---|
| 001–002 | 实际磁盘加载、原子创建、已有文件保留；scheduler ABI 正反例 | 直接 invoke 的 ABI 不代替角色准入 |
| 003、006 | 真实 routing owner、两个插件共享、子进程重新初始化、exact retry；006 的 owned/borrowed credit 在 tool step 或 terminal output 后交接，旧回调不释放下一步 | 003 的子进程只说明本地容量 |
| 004–005 | 真实 pending/supersede/唤醒；只换策略就改变 target 与容量限制 | optional reservation 与「未接收不占容量」冲突，见 GAP |
| 007–009 | 真实释放 owner、Host decoder、SDK adapter、插件配置优先权与消息投影 | decoder 与释放各自通过不代表接线全程已通；低层 SDK 可编码 model 不代表内部 synthetic send 能带 model |
| 010 | 真实 token ledger/借贷、显式 lender、公平等待、召回与有界随机序列 | 内部名单检查已撤；真实 tool 入口交接仍 TODO |
| 011–012 | 012 真实 fence 身份、旧 epoch、跨 owner 拒绝与单次结算 | 完整准入顺序与负向编译 TODO |
| 013–015 | 实际队列上界与先后、immutable snapshot、无修复 reconciliation、diagnostic 投影 | fixture 的规模不是产品上限；快照不能改状态 |
| 016 | 仍是 TODO | 原手写 fatal descriptor 不代表生产入口或 physical kill |
| 017–018 | 真实失败 witness 消费、保留/驱逐与当前/历史角色准入 | 旧角色可读与可准入仍由身份与生命周期包负责 |
| 019 | 本地绑定可用时拒绝冲突 | 道路持久化、换物理 session、真实恢复未覆盖 |

014 的满队列回归：旧 Guard 已 committed、provider step 在途、32 个真实 pending demand 占满队列时，无 target 的新 Human 被拒，旧 exact lease 与完整容量快照不变；同样满队列下，可立即取得 target 的新输入仍能替代。

## 运行

先构建，再经 `verification-system/tests/run.mjs` 选本目录编号文件。TODO 不算通过。
