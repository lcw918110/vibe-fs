class Js extends JsProgram {
  async run() {
    // 清单以不可变快照读入，再用 where 定位。这里故意留了一个歧义：
    // "dataset-3" 在清单里出现两次——索引里一次、正文小节标题一次。下面这行
    // where 会把程序就地 park，载荷是 { needle, candidates, nearest }。
    // 读 parked 载荷，选中正文标题那个 span，用
    // { unpark: { handle, value: [begin, end] } } 恢复：await 求值为注入的
    // span，程序从原行继续。这就是日常的 park——诊断直接指出修复位置，
    // 一次 unpark 就是全部修理；不用重写、不用 abandon。
    const manifest = await this.cat("downloads/manifest.md");
    const [heading] = await this.where(manifest, "dataset-3");
    const section = manifest.slice(heading);
    const url = section.slice(section.indexOf("https://")).split(/\s/)[0];

    return await js.call(class extends js.Callable {
      maxDuration = 3600000; // 整个工程：60 分钟
      interval = 10000;      // watch() 每 10 秒时间片跑一次

      // 三个大文件，严格串行。-c 让每一步幂等：再跑一次会接着盘上的
      // 文件继续，而不是覆盖它。
      files = [
        { name: "dataset-1", url: "https://cdn.example.org/data/dataset-1.img", path: "downloads/dataset-1.img" },
        { name: "dataset-2", url: "https://cdn.example.org/data/dataset-2.img", path: "downloads/dataset-2.img" },
        { name: "dataset-3", url, path: "downloads/dataset-3.img" },
      ];
      minBytesPerSlice = 524288; // 每 10 秒片不足半 MiB = 太慢

      index = 0;
      current = null;   // 正在跑 wget 的文件；hold 片上为 null
      report = null;    // 已完成的文件，等 watch() 去 park 它
      attempts = 0;
      seenBytes = -1;
      stalled = 0;
      slow = 0;

      // 控制流自证：两个 thunk，只拨状态并返回参数数组。宿主调用 next/watch
      // 并机械检查。跑在独立实例上（真实运行用全新构造）——写 thunk 就是在设计出口。
      sanity() {
        return {
          nextDone: () => {
            this.index = this.files.length;
            return [{ root: "/", step: 99 }];
          },
          watchVeto: () => {
            this.index = 0;
            this.current = this.files[0];
            this.seenBytes = 0;
            this.stalled = 6;
            return [{ root: "/", step: 0, stdout: "", stderr: "", exited: false, exitCode: null, backgrounds: [], elapsedMs: 0, remainingMs: 60000 }];
          },
        };
      }

      // Host 拥有循环：它调 next()、执行返回物、等这一步结束才回来。待交付的
      // report 必须活过一个时间片边界，因为只有 watch() 能提出 park；hold
      // 命令让循环恰好多活那一片。
      next(ctx) {
        if (this.report) return `sleep ${this.interval / 1000 + 2}`;
        if (this.index >= this.files.length) return null;
        const file = this.files[this.index];
        this.current = file;
        this.seenBytes = -1;
        this.stalled = 0;
        this.slow = 0;
        return `mkdir -p downloads && wget -c -q -O ${file.path} ${file.url}`;
      }

      // 进度从 stat 增量读，不从日志读：连续 6 片无新字节、或连续 12 片低于
      // minBytesPerSlice，就 abort。abort 会 SIGKILL 前台 wget；半截文件留给
      // wget -c。
      async watch(ctx) {
        if (this.report) {
          const report = this.report;
          this.report = null;
          const s = await this.stat(report.path);
          // 每个完成的文件一条持久记录：重启后至多重跑当时在途的那个文件。
          return { park: { ...report, bytes: s.exists ? s.size : 0, elapsedMs: ctx.elapsedMs } };
        }
        if (!this.current) return true;

        const s = await this.stat(this.current.path);
        const size = s.exists ? s.size : 0;
        const grew = this.seenBytes < 0 ? size : size - this.seenBytes;
        this.seenBytes = size;
        this.stalled = grew <= 0 ? this.stalled + 1 : 0;
        this.slow = grew > 0 && grew < this.minBytesPerSlice ? this.slow + 1 : 0;

        if (this.stalled >= 6) return { abort: `${this.current.name}: no new bytes for 6 slices` };
        if (this.slow >= 12) return { abort: `${this.current.name}: under ${this.minBytesPerSlice} bytes per slice for 12 slices` };
        return null; // 观察过了，没有异常——继续
      }

      // 退出码是机械规则：0 推进、4 和 8 重试（-c 从字节偏移续传）、3 跳过该
      // 文件。其余如实上报而不是瞎猜，run 保留剩余文件。
      onExit(result) {
        const file = this.current;
        if (!file) return; // hold 片没有工作
        this.current = null;

        if (result.exitCode === 0) {
          this.attempts = 0;
          this.report = { file: file.name, path: file.path, status: "done", exitCode: 0 };
          this.index += 1;
          return;
        }
        if (result.exitCode === 4 || result.exitCode === 8) {
          if (this.attempts < 3) {
            this.attempts += 1;
            return; // index 不动：next() 重发同一个 wget
          }
          this.attempts = 0;
          this.report = {
            file: file.name, path: file.path, status: "failed",
            exitCode: result.exitCode, attempts: 4,
          };
          this.index += 1;
          return;
        }
        if (result.exitCode === 3) {
          this.attempts = 0;
          this.report = { file: file.name, path: file.path, status: "skipped", exitCode: 3 };
          this.index += 1;
          return;
        }
        this.attempts = 0;
        this.report = { file: file.name, path: file.path, status: "unmapped", exitCode: result.exitCode };
        this.index += 1;
      }

      // onTimeout 保持默认：maxDuration 到期杀掉前台并回报 status "timeout"，
      // 半截文件留在盘上。
    });
  }
}
