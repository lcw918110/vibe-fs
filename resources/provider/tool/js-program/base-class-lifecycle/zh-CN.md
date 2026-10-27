async park(payload) {
    // 停在当前表达式；只有显式 park（含 where 歧义）接收恢复值。
  }

  async unpark(name, value) {
    // 恢复原命名程序；value 可省略，只交给显式 park。
    // 目标共用本次请求已有期限；没有预算参数。
    // 后续恢复控制者，不会隐式再次恢复它的目标。
  }

  async abandon(name) {
    // 请求取消并报告真实收尾，确认资源结清后才报告 abandoned。
    // 正常展开运行 finally，强制终止不能保证它执行。
  }

  async run() {
    // 返回 JSON 兼容数据；return 只结束本次程序调用。
  }
