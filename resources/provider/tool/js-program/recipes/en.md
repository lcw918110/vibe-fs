Search and enumeration go through `rg(argv)`. It takes one argv and returns a JSON object, never text. A content search is `rg --json` plus that argv. `--files`, `--files-with-matches`, `-l`, `--count` and the other summary modes cannot be combined with `--json`, so those argv are passed through and returned as `paths`:

```js
const found = await this.rg(["RetryPolicy", "src", "-g", "*.fs"]);
const matches = found.messages.filter((message) => message.type === "match");
```

`found` is `{ exitCode, messages }`. `messages` is the parsed JSON Lines array (`begin`, `match`, `context`, `end`, `summary`). `exitCode` `0` means at least one match; `1` means the search succeeded and found nothing — that is data, not a failure. A bad argv fails as `RG_FAILED`.

Pass the path yourself. `rg --json` with no path does not search the workspace. The member does not add `--no-ignore`; put it in the argv when ignored paths must be searched.

`rg --files` and `rg --files-with-matches` return `{ exitCode, paths }`. A content search returns the hits.

```js
const listed = await this.rg(["--files", "src", "-g", "*.fs"]);
const paths = listed.paths;
```

A path in the argv stays inside the workspace. `..`, an absolute path outside it, or an option that reads outside it, is `PATH_DENIED`.

`where` / `whereAll` stay the edit locators. They return UTF-16 spans of one snapshot; `rg` does not.
