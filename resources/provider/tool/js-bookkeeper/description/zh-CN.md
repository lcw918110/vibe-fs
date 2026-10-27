工具参数 name、program、intent 均为必填字符串，remainingScopeTurnsEstimate 为必填非负实数（可以带小数）。顶层 intent 按 UTF-8 字节数 × 50 ms 确定整个程序的一次准入期限，成员不能续期。

用一次原子的 JavaScript transformation 编写 staged Case 的下一形态。

本次 transaction 中 Case 已经冻结。question(matches = []) 与 answer(matches = []) 返回带有序 anchor 的不可变文本视图。view.text(from = "^", to = "$") 切出精确文本；anchor 名可以使用截断后的 +N/-N 偏移（N 是 JS 字符串下标增量，不是行号）。

setQuestion(newText) 与 setAnswer(newText) 各自暂存完整的下一侧，且各自最多只能调用一次。零次 mutation 合法。被抛出的 program 或非法 mutation 两侧都不改变。

该 program 没有任何外部世界能力。在调用前先决定 Case 应表示什么；本 program 只用来执行那一决定已经证成的、连贯的机械重塑。
