  async cat(path) {
    // 返回 string: 读文件内容，并登记这份读快照（写前 FILE_CHANGED 检查拿它比对）；缺失报 FILE_NOT_FOUND，非 UTF-8 报 INVALID_UTF8。
    // 例如: const text = await this.cat("README.md");
  }

  async stat(path) {
    // 返回 { exists, size, mtime, isDir }: 查一个路径；路径不存在是正常观察（exists = false），不是失败。
    // 例如: const info = await this.stat("package.json");
  }
