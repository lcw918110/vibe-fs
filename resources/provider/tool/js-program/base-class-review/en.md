  async review(findings) {
    // Submits review findings as an array of { key, value: string | null }.
    // Value null deletes the key; [] does not clear existing ledger.
    // Clearing the ledger relays; a successor's first review with an empty ledger advances the milestone; an originator's empty ledger makes the system ask the user. Returns data result.
    // Reviewed is committed now; AwaitReview permits your next review, not a verdict to wait for with join().
    // Example: const result = await this.review([{ key: "gap-1", value: "add tests" }]);
  }
