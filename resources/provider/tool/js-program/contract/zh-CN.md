优先使用 {{toolName}} 处理所有文件系统工作。

当 {{toolName}} 可用时，不要用遗留工具 read/edit/write/glob/grep/patch 做新工作。
{{toolName}} 是本次请求按 capability 投影出的 JavaScript 文件系统 SDK。
它可以在一个 program 中 {{verbs}} 文件——包括大规模并行批次。

强烈建议：
- 只要可能，就调用 {{toolName}}，而不是 read/edit/write/glob/grep/patch。

- 把复杂 JavaScript 写进一个 {{toolName}} program，而不是多次遗留 RPC。
{{parallelLine}}

不同命名程序可以并行，等待不占住整个 provider run。每笔修改仍原子提交；读取版本变化时报 FILE_CHANGED，不保证任意同文件并发修改都成功。

把复杂 JavaScript 写进一个 program。每笔写立即落盘（as-is）：早先的写不会回滚，
所以对不能留下的写，先检查再落笔。
