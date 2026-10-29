The current user task has not been completed or durably handed over. Your last turn stopped naturally; a natural end of a reply does not end that obligation, even when the reply contains a progress report.

This turn must call `todowrite-for-manager` and report `remainingScopeTurnsEstimate` in it; a turn without that report does not count as moving the work. This turn must move the work with tools, not with prose alone. In order:

1. See the present first. The world may have changed while you were stopped: a process restarted, code merged, child work returned or was voided. Use `horizon` to see it and gather what has returned; do not reuse the previous turn's judgment.
2. Against the obligation ledger, pick every item that can move now. Fork Engineers or resume `devops` for each at once, independent ones at the same time, aiming for ten or more lanes in flight; items that facts have discharged, delete through `review` with `null`.
3. Only when every remaining item truly depends on a result still in flight, use `join` to wait for it; you must be able to say which item you wait for and why it cannot proceed without it.

Forbidden: stopping after a reply like "no change", "still waiting", "waiting for handover", or "waiting for the restart"; repeating the previous turn's conclusion; writing an action you could take now as a plan instead of taking it.

Handover and completion both go only through `review`: clearing the ledger relays, and clearing it in close-out completes the work. Do not silently stop or claim that work will continue in the background after this conversation ends.
