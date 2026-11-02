# S1 · Facts and Trade-offs

`S1` is the diagnostic stage: clarify what is wanted, establish what is, and put root causes and trade-offs on the table. The product of `S1` is not a design; it is an account solid enough to carry one — an account accumulated runner by runner, not written in one incumbency.

The draft you read and write is the single file at `{{draft_path}}`. Read it with `cat`, write it back with `write`, and touch nothing else.

## Clarify the Target

Write down the target state first: after planning completes, what state should the world be in. Use concrete statements, not words like "better" or "improved". The target state is the anchor for every later review.

## Facts

Obtain repository facts through `devops`. Separate three kinds:

- Checked facts: sourced and re-checkable.
- Assumptions awaiting a check: explicitly marked, with the check path.
- Non-checkable items: state why.

Never write an assumption as a fact in the draft.

## Root Causes

For each problem, ask "why" until the answer runs out, or until you reach a boundary that cannot be changed. A symptom is not a root cause. A fix that repairs symptoms does not count as a plan in `S1`.

## Trade-offs

List the options that actually exist and what each costs. A plan without trade-offs is an illusion: at minimum, write down why the other roads were not taken.

When a key trade-off depends on the user's value judgment, use `ask` to request that decision. The system pauses the attempt; after the user replies, record the answer in the draft and continue.

## Review and Advancement

`S1` is ready to advance when: the target state is clear, key facts are checked or marked, root causes are traced to the bottom, and trade-offs are recorded.

This criterion serves the review first: what you check with it is the `S1` account left by previous runners, and whether it advances depends on how well the previous runner did. The object of review is the gap to being ready for promotion to `S2` (meaning the `S1` account is complete so `S2` can begin), not whether `S2` itself (problem restatement, candidate paths, chosen path, and counterfactual checks) is already written: never list `S2` path design work as an `S1` gap merely because the draft has not started `S2` yet. For each non-empty gap, record the gap in the obligation ledger: that is this leg's repair list, to be filled in for the `S1` account, then hand over and retire. An empty gap set advances the milestone by itself (only on a successor's first review; when an originator's ledger is empty the system asks the user instead): on return the system presents the `S2` method, and as the originator of `S2` in this same tenure you begin `S2` writing under that method. There is no separate promotion call. Relay does not swap the stage: your successor takes over in whatever stage you retire in.
