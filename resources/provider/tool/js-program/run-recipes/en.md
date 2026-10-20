## Compile a large project

```js
class Js extends JsProgram {
  async run() {
    return await js.call(class extends js.Callable {
      maxDuration = 900000; // 15 min engineering bound
      interval = 10000;
      failureReason = null;

      next(ctx) {
        if (this.failureReason) return `sleep ${this.interval / 1000 + 2}`;
        return ctx.step === 0 ? "cmake --build build --parallel" : null;
      }

      // Only watch() can return { abort }; after a non-zero exit, one hold slice
      // lets watch() raise the stable abort reason.
      watch(ctx) {
        if (this.failureReason) return { abort: this.failureReason };
        if (ctx.elapsedMs > 600000 && ctx.remainingMs < 30000) {
          return { abort: "build_time_loss: under 30s left after 10m elapsed" };
        }
        return null; // observed, nothing alarming — continue
      }

      onExit(result) {
        if (result.exitCode === 0) return;
        // Full transcript in hand: find the first error line in JS — no
        // `| grep` that would have thrown the front of the log away, and the
        // original stays available for a second look.
        const firstError = result.stdout.split("\n").find((l) => /error:/i.test(l));
        this.failureReason = `build_failed:exit_${result.exitCode}${firstError ? ": " + firstError.trim() : ""}`;
      }

      // Self-test thunks, run first on their own instance. They only set state
      // and return argument arrays; the Host calls next/watch. "all done" (past
      // the build step) → next returns null; "something is wrong" (a recorded
      // failure) → watch vetoes. Writing them is designing the exits.
      sanity() {
        return {
          nextDone: () => [{ root: "/", step: 1 }],
          watchVeto: async () => {
            this.failureReason = "prove: simulated build failure";
            return [{ root: "/", step: 0, stdout: "", stderr: "", exited: false, exitCode: null, backgrounds: [], elapsedMs: 0, remainingMs: 60000 }];
          },
        };
      }
    });
  }
}
```

## npm server + frontend tests

```js
class Js extends JsProgram {
  async run() {
    let testExitCode = null;
    const outcome = await js.call(class extends js.Callable {
      maxDuration = 300000; // 5 min bound for server startup + test suite
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
        return null; // observed, nothing alarming — continue
      }

      onExit(result) {
        testExitCode = result.exitCode;
      }

      // Self-test: past the last step, the Host calls next and gets null; a crashed
      // srv in the observation window makes the watch call veto. Thunks return
      // argument arrays only.
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

## Drive a REPL child interactively

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

      // Event-driven: read the instance state, decide. Send the next line,
      // reap the child once every line is sent, and return null after the
      // reap — the exit exists and sanity() shows it.
      next(ctx) {
        if (this.reaped) return null;
        if (this.sent >= this.lines.length) {
          this.reaped = true;
          return { signal: "SIGTERM", name: "py" };
        }
        return { send: { name: "py", input: this.lines[this.sent++] } };
      }

      // The slice deltas of the foreground command and every background child
      // merge into one observation window; collect what the REPL printed.
      watch(ctx) {
        const py = ctx.backgrounds.find((b) => b.name === "py");
        if (py && py.exited && this.sent < this.lines.length) {
          return { abort: "repl died mid-session" };
        }
        if (ctx.stdout.trim()) answers.push(ctx.stdout.trim());
        return null; // observed, nothing alarming — continue
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

The interactive form is the same loop, no separate pty tool exists: spawn the
process with `{ spawn, name }`, inject lines via `{ send: { name, input } }`,
and read the incremental `ctx.stdout` / `ctx.stderr` deltas in `watch(ctx)`.
