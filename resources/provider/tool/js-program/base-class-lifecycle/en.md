async park(payload) {
    // Pause at this expression. Only explicit park, including where ambiguity, receives value.
  }

  async unpark(name, value) {
    // Resume the original named program. value is optional and only for explicit park.
    // The target shares this request's existing deadline; there is no budget argument.
    // Resuming a controller later does not implicitly resume its targets again.
  }

  async abandon(name) {
    // Cancel and report actual cleanup. abandoned requires confirmed resource settlement.
    // Normal unwinding runs finally; forced termination cannot promise it.
  }

  async run() {
    // Return JSON-compatible data. Returning only completes this program invocation.
  }
