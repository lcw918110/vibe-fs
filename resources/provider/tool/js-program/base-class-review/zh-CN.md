  async review(findings, note) {
    // 提交评审结果，findings 为 { key, value: string | null } 数组。
    // 值为 null 删除该键；[] 不会清空既有台账。
    // 台账清空即接力；接任者第一次评审台账为空即推进里程碑；初任台账为空时系统向用户提问。返回数据结果。
    // Reviewed 已经提交；AwaitReview 表示可以提交下一次评审，不是要用 join() 等待的结论。
    // note（可选字符串）：这次评审启动 Manager 时，note 就是它的标题和完整底稿；不传则发送已保存的底稿。
    // 例如: const result = await this.review([{ key: "gap-1", value: "补充测试" }]);
  }
