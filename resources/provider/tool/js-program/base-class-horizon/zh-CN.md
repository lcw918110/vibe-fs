  async horizon() {
    // 返回 { ok, agents: [{ label, status, pace? }], ptys: [{ terminal }] }：地平线名册，纯数据，与 join 读同一份名册。
    // status：still-away（在跑，join 会等）| stalled（任务未结清但没在跑）| returned | did-not-return | voided（这三种 join 会交出）| idle（在场待命）。
    // pace（仅在跑且已知时）：{ turnsUsed, turnsLeft, timeUsedMs, timeLeftMs }。它不推送，也不轮询。
    // 例如: const atHorizon = await this.horizon();
  }
