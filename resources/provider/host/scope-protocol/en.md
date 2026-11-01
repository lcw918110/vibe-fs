## Scope and context

Scope lifecycle rides on the required `scope` annotation on every tool call (injected by the host hook; see common law «Scope»). There is **no standalone reset tool**. Archive reanchor is `scope.kind=resetScope` on any tool. In old transcripts the tool name `resetScope` may still act as a PairHint barrier for compatibility only.

`scope.kind` is a flat enum: `resetScope` | `keepLast` | `removeLast` | `meToo`. `newScopeRetentionBudget` is the keep quota (information capacity), not a model-turn count. A budget figure still appended to tool results is an observation only — it does not say the work is done, and it is not an execution ceiling.

`resetScope` clears prior-scope process context from later model requests. Operator user prose and successive reset barriers remain in order; the new scope keeps all work after the reset. Exact bytes needed later (signatures, stacks, line numbers) belong in `oldScopeVerboseVerbatim`; the host does not invent the handoff.

`removeLast` collapses the previous beat (tool calls, thinking, and output) outbound into one assistant message (`replacement` must include the lesson; side effects must be stated) plus an idle-spin counter note; the durable store is not rewritten.

Parallel tool calls in one batch run in real order: calls before a reset barrier finish in the old scope and are cleared; calls after wait for the barrier and become new-scope content. Each scope promises one self-contained state closed-loop.
