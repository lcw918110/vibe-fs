  async review(findings, note) {
    // Submits review findings as an array of { key, value: string | null }.
    // Value null deletes the key; [] does not clear the existing ledger.
    // Clearing the ledger relays; a successor's first review with an empty ledger advances the milestone; an originator's empty ledger makes the system ask the user. Returns data result.
    // Reviewed is committed now; AwaitReview means you may submit your next review, not a verdict to wait for with join().
    // note (optional string): when this review launches a Manager, the note is its title and full draft; without it the saved draft is sent.
    // Example: const result = await this.review([{ key: "gap-1", value: "add tests" }]);
  }
