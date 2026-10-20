## 编译大型工程

```js
class Js extends JsProgram {
  async run() {
    return await js.call(class extends js.Callable {
      maxDuration = 900000; // 15 分钟工程总上界
      interval = 10000;
      failureReason = null;

      next(ctx) {
        if (this.failureReason) return `sleep ${this.interval / 1000 + 2}`;
        return ctx.step === 0 ? "cmake --build build --parallel" : null;
      }

      // 只有 watch() 能返回 { abort }；非零退出后借一个保持时间片让 watch() 报出
      // 稳定中止原因。
      watch(ctx) {
        if (this.failureReason) return { abort: this.failureReason };
        if (ctx.elapsedMs > 600000 && ctx.remainingMs < 30000) {
          return { abort: "build_time_loss: under 30s left after 10m elapsed" };
        }
        return null; // 观察过了，没有异常——继续
      }

      onExit(result) {
        if (result.exitCode === 0) return;
        // 完整输出在手：在 JS 里找第一个 error 行——不用 `| grep`（它会扔掉
        // 日志前头的字节），原文也留在手上备查。
        const firstError = result.stdout.split("\n").find((l) => /error:/i.test(l));
        this.failureReason = `build_failed:exit_${result.exitCode}${firstError ? ": " + firstError.trim() : ""}`;
      }

      // 自证 thunk，先跑在独立实例上。只拨状态、返回参数数组；宿主调用
      // next/watch。「全部完成」（越过构建步）→ next 返回 null；「出了问题」
      // （有失败记录）→ watch 否决。写它们就是在设计出口。
      sanity() {
        return {
          nextDone: () => [{ root: "/", step: 1 }],
          watchVeto: async () => {
            this.failureReason = "prove: 模拟构建失败";
            return [{ root: "/", step: 0, stdout: "", stderr: "", exited: false, exitCode: null, backgrounds: [], elapsedMs: 0, remainingMs: 60000 }];
          },
        };
      }
    });
  }
}
```

## npm 服务 + 前端测试

```js
class Js extends JsProgram {
  async run() {
    let testExitCode = null;
    const outcome = await js.call(class extends js.Callable {
      maxDuration = 300000; // 服务启动 + 测试套件的 5 分钟上界
      interval = 2000;

      next(ctx) {
        if (ctx.step === 0) return { spawn: "npm run dev", name: "srv" };
        if (ctx.step === 1) return { waitFor: { port: 5173, timeoutMs: 30000 } };
        if (ctx.step === 2) return "npm run test:e2e";
        if (ctx.step === 3) return { signal: "SIGTERM", name: "srv" };
        return null;
      }

      watch(ctx) {
        const srv = ctx.backgrounds.find((b) => b.name === "srv");
        if (srv && srv.exited && srv.exitCode !== 0) {
          return { abort: `server_crashed:exit_${srv.exitCode}` };
        }
        return null; // 观察过了，没有异常——继续
      }

      onExit(result) {
        testExitCode = result.exitCode;
      }

      // 自证：越过最后一步，宿主调用 next 得到 null；观察窗里 srv 崩溃，
      // 宿主调用 watch 得到否决。thunk 只返回参数数组。
      sanity() {
        return {
          nextDone: () => [{ root: "/", step: 4 }],
          watchVeto: () => [{ root: "/", step: 2, stdout: "", stderr: "", exited: false, exitCode: null, backgrounds: [{ name: "srv", exited: true, exitCode: 1 }], elapsedMs: 0, remainingMs: 60000 }],
        };
      }
    });
    return { ...outcome, testExitCode };
  }
}
```

## 交互式驱动 REPL 子进程

```js
class Js extends JsProgram {
  async run() {
    const answers = [];
    const outcome = await js.call(class extends js.Callable {
      maxDuration = 60000;
      interval = 200;
      lines = ["1 + 1", "2 * 21", "exit"];
      sent = 0;
      reaped = false;

      // 事件驱动：读实例状态再决定。发下一行；全部行发完就收割子进程；
      // 收割之后返回 null——出口存在，sanity() 会展示它。
      next(ctx) {
        if (this.reaped) return null;
        if (this.sent >= this.lines.length) {
          this.reaped = true;
          return { signal: "SIGTERM", name: "py" };
        }
        return { send: { name: "py", input: this.lines[this.sent++] } };
      }

      // 前台命令与全部后台子进程的片增量合并进同一个观察窗；收集 REPL 打出的
      // 内容。
      watch(ctx) {
        const py = ctx.backgrounds.find((b) => b.name === "py");
        if (py && py.exited && this.sent < this.lines.length) {
          return { abort: "repl died mid-session" };
        }
        if (ctx.stdout.trim()) answers.push(ctx.stdout.trim());
        return null; // 观察过了，没有异常——继续
      }

      sanity() {
        return {
          nextDone: () => {
            this.reaped = true;
            return [{ root: "/", step: 9 }];
          },
          watchVeto: () => [{ root: "/", step: 0, stdout: "", stderr: "", exited: false, exitCode: null, backgrounds: [{ name: "py", exited: true, exitCode: 1 }], elapsedMs: 0, remainingMs: 30000 }],
        };
      }
    });
    return { ...outcome, answers };
  }
}
```

交互形态就是同一个循环，没有独立的 pty 工具：用 `{ spawn, name }` 起进程、
`{ send: { name, input } }` 注入行、在 `watch(ctx)` 里读本片 `ctx.stdout` /
`ctx.stderr` 增量。
