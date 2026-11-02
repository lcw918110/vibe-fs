async call(command) {
    // Drain this program's earlier SDK operations, then start one shell command.
    // Blocks this program even without await. Both stages and every later call
    // share the remaining time of the top-level intent window. Expiry does not kill the command.
    // Final result retains stdout/stderr, real exitCode or signal, bytes, durationMs,
    // explicit truncation and raw outputFiles. Delta does not consume it.
  }

  async signal(name, signal) {
    // Signal the named program's current command without resuming its code.
  }

  async send(name, input) {
    // Send input without resuming the target; append a missing newline.
    // Acceptance does not mean the command has read it.
  }
