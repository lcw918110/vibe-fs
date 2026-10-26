# Tool · review

Review is the only lifecycle entry for Plan and Manager. It applies the keyed obligation ledger and returns the committed decision; it does not submit work to another reviewer.

- `findings` is an array of `{ key, value }`. A string adds or replaces that key; null deletes it. An empty array does not delete existing obligations.
- An originator with no obligations asks the user what to do and waits for a real answer. The first non-empty review starts ordinary work.
- Clearing the working ledger relays to a successor at the same milestone. The successor's first empty review advances the milestone in the same tenure; a non-empty review starts another working period.
- In close-out, an empty ledger completes the work only when real resource blockers are gone.
- In ready-to-execute, each new empty review launches one Manager; the same call replays without a second launch. Supply the full draft in `note` for that launch.
- `Reviewed` is already recorded. `AwaitReview` means ready for your next review, not an asynchronous verdict. Do not wait with `join` for a reviewer that does not exist.
