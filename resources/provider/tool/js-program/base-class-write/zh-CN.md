  write(path, content) {
    // 写入一个 UTF-8 文件（覆盖已有或新建缺失）：一次原子写、写入即生效；写前拿读快照比对，变化时报 FILE_CHANGED。
    // 例如: this.write("generated/version.txt", "1.2.3\n");
  }

  mkdir(path) {
    // 建一个目录（POSIX，非递归）；目标已存在报 DIRECTORY_EXISTS，父目录缺失报 PARENT_DIRECTORY_MISSING。
    // 例如: this.mkdir("generated");
  }
