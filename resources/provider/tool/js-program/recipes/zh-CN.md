搜索和枚举走 `rg(argv)`。它只有一个 argv，返回 JSON 对象，不是文本。内容搜索等价于 `rg --json` 加上这个 argv。`--files`、`--files-with-matches`、`-l`、`--count` 及其他 summary 模式不能和 `--json` 同时使用，所以这些 argv 原样传给 `rg`，结果收成 `paths`：

```js
const found = await this.rg(["RetryPolicy", "src", "-g", "*.fs"]);
const matches = found.messages.filter((message) => message.type === "match");
```

`found` 是 `{ exitCode, messages }`。`messages` 是解析后的 JSON Lines 数组（`begin`、`match`、`context`、`end`、`summary`）。`exitCode` 为 `0` 表示至少一条命中；为 `1` 表示搜索成功但没有命中——这是数据，不是失败。argv 不合法时以 `RG_FAILED` 失败。

路径要自己传。`rg --json` 没有路径时不会搜索工作区。成员不追加 `--no-ignore`；要搜索被忽略的路径，自己写进 argv。`rg --files` 和 `rg --files-with-matches` 返回 `{ exitCode, paths }`。内容搜索直接返回命中。

```js
const listed = await this.rg(["--files", "src", "-g", "*.fs"]);
const paths = listed.paths;
```

argv 里的路径必须留在工作区内。`..`、工作区外的绝对路径，或会读到区外的选项，都是 `PATH_DENIED`。

`where` / `whereAll` 仍是编辑定位器。它们返回一份快照上的 UTF-16 span；`rg` 不返回 span。
