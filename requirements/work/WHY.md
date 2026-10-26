# work — WHY

同一件事不能有两套生命周期。Plan 与 Manager 共用 `review` 这一入口和同一套事件。Plan 的里程碑是「事实与取舍 → 理解与路径 → 流程与伪代码 → 待执行」。Manager 的里程碑是「执行 → 收尾 → 已完成」。任期只有 Originator 与 Successor。

评审写入的是义务账。空账在初任只询问，在工作期清空才接力，在接任第一次才推进里程碑，在收尾且资源已解除时才结案。接力写下 Relayed 与 Context Barrier。结案写下 Completed。证书、快照和旧的晋级名字都不能替这些事实说话。

后任能看见的历史由已提交的 Context Barrier 裁切。触发这次接力的 review 及其结果，以及它之前的消息，后任都看不见。原始持久历史逐字节保留。底稿与显式资源仍由调用方自己读取，投影不转述工作区。

有任期而评审尚未落盘时，人还可以读和观察，但不能写源码，也不能向固定 DevOps 派工。这道门窄在写入与派工，不是把一切工具关掉。

## DEPENDS ON

- participant-identity
- semantic-trace
- office-capability
- durable-events
- prefix-stability
