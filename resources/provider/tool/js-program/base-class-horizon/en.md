  async horizon() {
    // Returns { ok, agents: [{ label, status, pace? }], ptys: [{ terminal }] }: the horizon roster as data, read from the same roster join reads.
    // status: still-away (running; join waits) | stalled (open charge, nothing running) | returned | did-not-return | voided (join hands these over) | idle (at hand, no charge).
    // pace (only while running, when known): { turnsUsed, turnsLeft, timeUsedMs, timeLeftMs }. It does not push and does not poll.
    // Example: const atHorizon = await this.horizon();
  }
