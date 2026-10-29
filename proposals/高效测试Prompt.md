# 测试效率补充段落

下段可直接追加到现有测试 prompt，沿用其角色、任务范围和交付方式。测试与验收统一读取 [AXIOM → LEMMA → SPEC 测试规则](../requirements/verification-system/AXIOMS.md)。该文件是 `verification-system` 的唯一正文，旧 `WHAT.md` 仅作导航；被测业务仍按各自模块的现行合同判断。

## 可直接追加

开始编写、构建或优化测试前，阅读 `requirements/verification-system/AXIOMS.md`，按其中的 AXIOM、LEMMA 与适用 SPEC 设计验证；再阅读被测模块的业务合同。优先采用足以反驳目标命题的最小边界，保留有效覆盖；可按失效条件复用不可变准备，防止无关用例相互污染。资源回归验证结束后的释放；涉及仍需运行的工作时，同时验证其合同保护。逻辑时间与物理资源各用相应证据。按任务范围运行验证，整套提速须核对输入、环境和用例身份，并计入排空与回收。逐项保留改动、前后数据、失败和波动，不用漏执行或偶然绿色证明收益。旧 `verification-system/WHAT.md` 不提供另一套规则；文档迁移、测试通过与性能收益分别报告。

实验依据见原优化分支的[定时器优化记录](https://github.com/dyx13/vibe-fs/blob/4a29ac65e22b17b234b6c5816fb1380d53f90016/evidence/test-optimization-20261027/README.md)。其中的实现未随规则变基移植，提速幅度不作为当前上游或其他任务的目标或承诺。
