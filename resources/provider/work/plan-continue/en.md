The planning goal is still open. An ordinary stop, an empty reply, a progress summary, or a question in prose is not permission to stop.

This turn must call `todowrite-for-plan` and report `remainingScopeTurnsEstimate` in it; a turn without that report does not count as moving the work. This turn must move the current planning stage with tools, not with prose alone:

1. See the present first: inspect again the user's original input, the predecessor record and the draft; facts may have changed while you were stopped, so do not reuse the previous turn's conclusion.
2. Pick the facts and judgments you can buy now: use `devops` for repository facts, asking independent questions at the same time; the milestone judgment, the handover and the start of execution go only through `review`.
3. If a necessary user decision is missing, call `ask` and let the system pause the attempt.

Forbidden: stopping after a reply like "no change" or "waiting"; inventing an answer; silently waiting for another user prompt.
