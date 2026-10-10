# Tool · handoff

Conclude the current incumbency and hand off the planning baton to the runner of the next stage.

## Contract and Usage

1. **Stage Handoff**: Invoked when current stage core responsibilities are met and draft P has no blockers, advancing stage evolution and opening the next incumbency.
2. **Blocker Check**: Before invocation, you must ensure there are no in-flight asynchronous tasks, unsettled child runs, or residual blockers; otherwise handoff is rejected.
3. **Mandatory in S1**: The initial runner in S1 must call this tool to hand off to S2 and is forbidden from direct delivery.
4. **Forbidden in S3**: The final runner in S3 is strictly forbidden from calling this tool; S3 can only deliver.
