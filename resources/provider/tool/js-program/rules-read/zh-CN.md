用 `cat(path)` 读取：返回文件字符串，并登记写前 FILE_CHANGED 检查使用的读快照。路径缺失报 FILE_NOT_FOUND，非法编码报 INVALID_UTF8。`stat(path)` 返回 `{ exists, size, mtime, isDir }`——路径缺失是 `exists: false`，不是错误。

用 `where` / `whereAll` 定位：

- `whereAll(content, needle, from = 0)` 返回全部规范化匹配 `[[begin, end], ...]`。
- `where(content, needle, from = 0)` 返回单个 span。零个或多个匹配时经与 `park` 相同的通道挂起，载荷为 `{ needle, candidates, nearest }`；恢复时注入选中的 span。

规范化把每一段空白折叠成一个空格并裁掉 needle 首尾空白；下标始终指向原字符串。`where` 永不抛定位错误，也永不静默挑一个匹配：唯一匹配是机械的，歧义归你。

用切片重组——已经定位到的内容不要重新搜索：

```js
const text = await this.cat(path);
const [begin, end] = await this.where(text, "old");
this.write(path, text.slice(0, begin) + "new" + text.slice(end));
```

`Promise.all` / `Promise.allSettled` 在一个程序内给出并行 IO；宿主不加锁、不排队，兄弟分支失败不会撤销已经落盘的写。同文件竞态自负。

我为这个错误付过代价。有一次我把约 8k 行的重排当成手工字符串手术：`indexOf`、`substring`、再 join；一次运行把它变成约 31k 行。难看的不只是一次坏输出——我又花了很多次调用猜损伤，扫重复标题、刮残留。裸搜索只是找候选，并不拥有结构。原语本已给我不可变快照与精确 span，我自己算边界等于丢掉这些保护，把 bug 重新造出来。

所以结构性的活——保留这些段、丢掉那些段、重排这些节——先定位结构，再只拼接该进目标文件的切片。不要让熟悉冒充事实：`indexOf` 显得「简单」只是因为你见过它千万次；这并不说明它拥有这个文件的结构。契约更强的原语拥有优先权。
