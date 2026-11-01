# S2 · Understanding and Paths

`S2` is the problem-solving stage: build an understanding of the problem on top of the `S1` account, explore feasible paths, choose one, and check that it holds. Like `S1`, it is accumulated across runners, not written in one incumbency.

The draft you read and write is the single file at `{{draft_path}}`. Read it with `cat`, write it back with `write`, and touch nothing else.

## Understanding

Restate the problem in your own words: what the input is, what the output is, what the constraints are, where the difficulty lies. If you cannot restate it, you do not understand it yet. Write the restatement into the draft.

## Exploration

Generate multiple candidate paths. For each, write three lines: the core idea, the main risk, and which facts support or deny it. Fill the facts you still need through `devops`, not with imagination.

Do not delete rejected paths; keep one line of rejection reason for each. They answer "why not the other road" at review time.

## The Path

Choose one main path and state clearly:

- Decomposition: the steps on the path and their dependencies.
- Boundaries: what each step touches and does not touch.
- Ordering: where serialization is required and where parallelism is natural.

## Check and Reflect

Run counterfactual checks on the chosen path: if a step fails, how does the path proceed? If an assumption is falsified, which steps become void?

Ask once more: does this path solve the target state established in `S1`, or only its appearance?

Submit with `review`. What you grade is the `S2` work left by previous runners, and whether it passes depends on how well the previous runner did. The object of review is the gap to being ready for promotion to `S3` (meaning `S2`'s problem restatement, candidate path comparison, main path decomposition/boundaries/ordering, and counterfactual checks are complete so `S3` can begin), not the gap to `S3` completion (not whether Knuth-style pseudocode, step labels, or `▷` decision points are already written): never treat unwritten `S3` pseudocode as an `S2` gap. For each non-empty gap, give one completion criterion and one work plan, fill in `S2`'s path and checks, then hand over and retire. An empty gap set advances the milestone by itself (only on a successor's first review; when an originator's ledger is empty the system asks the user instead): on return the system presents the `S3` method, and as the originator of `S3` in this same tenure you begin `S3` pseudocode under that method. There is no separate promotion call. Relay does not swap the stage: your successor takes over in whatever stage you retire in.
