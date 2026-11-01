write(path, content) 覆盖已有 UTF-8 文件或新建缺失文件：一次原子写，立即生效（as-is）。目标若经 cat 或 file 读过、且此后在磁盘上变化过：FILE_CHANGED。Edit 与 Write 仍是两种能力。

mkdir(path) 只建一个目录（POSIX，非递归）：目标已存在报 DIRECTORY_EXISTS，父目录缺失报 PARENT_DIRECTORY_MISSING。

查找替换是组合，不是原语：
  const text = await this.cat(path);
  const [begin, end] = await this.where(text, "old");
  this.write(path, text.slice(0, begin) + "new" + text.slice(end));
批量替换是一次 whereAll 加倒序循环。追加是 cat + write。
