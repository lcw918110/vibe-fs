一个 program 对每个 canonical path 只能 mutation 恰好一次。对同一路径的第二次 edit/rewrite/write
是 DUPLICATE_MUTATION_TARGET。独立局部修改放进一个 edit 数组；需要计算的多阶段编辑应放在
JavaScript 变量里，然后一次 rewrite/write。

我也犯过更难看的版本：先拼太多，再用 replace 删局部，留下空行、悬空
分隔符和残片，然后写第二、第三个 cleanup program 去修第一个 program。
这不叫「健康的多阶段重构」；这说明第一轮根本没有把目标文件定义清楚。
先从可信切片在内存里构造最终文本，再对这个 path 暂存一次 mutation。

return 之前，检查那些便宜却能让坏结果现形的规矩：大致行数/长度量级、必须存在
的标题或 sentinel、关键 section 的预期数量。一个普通重排如果从约 8k 行
突然变成约 31k 行，不要只读前五十行，觉得「开头看起来正常」就继续猜。
直接在 return 前 throw。mutation 还在 staging，当前 program 失败就会零
提交。离谱的数字不是噪音，是信号；先按信号处理，别再制造第二个问题去修
第一个问题。

先定规矩，再动手：mutation 前就决定哪些廉价规矩必须成立。program 一旦把
规矩写出来，看见不方便的结果后就没有资格临时改口。「开头看起来正常」说明不了什么；
「再 replace 一次大概就干净了」也不是信号。数字、必须存在的 sentinel、
section 数优先于你在坏结果出现后给自己编的解释。

停止信号：如果规模、section 数、sentinel 数或其它廉价规矩明显越界，当前
program 就已经失去 commit 的资格。让它失败。不要拿一个可疑结果去奖励自己
再做一轮猜测性变换。红灯之后最快的路是回到事实，不是冲进 cleanup。

生成的 class 没有 commit、rollback、snapshot 或 transaction 方法。
run() 正常返回 → Host preflight → prepare → commit。run() 抛出或
任何已生成的文件系统方法失败都会丢弃全部已暂存 mutation。

run() 必须返回 JSON 兼容值：null、boolean、有限 number、string、
array 或 plain object（递归）。undefined、BigInt、NaN、Infinity、function、
symbol、循环或奇异对象在 commit 前失败为 INVALID_RETURN_VALUE。
