# Tool · resume

Dispatch the task's bound fixed DevOps runner for single-flight investigation and return the complete results.

## Contract and Usage

1. **Fixed Binding**: Directly dispatches the unique fixed DevOps instance bound to this planning task; callers must not specify or override low-level IDs, roles, or models.
2. **Single-Flight Investigation**: At most one in-flight DevOps investigation is permitted per task, awaiting formal completion and convergence.
3. **Complete Delivery**: The system delivers all technical facts returned by DevOps verbatim without secondary model summarization.
4. **No Authority Usurpation**: Used solely for inspecting real repository state and execution verification evidence, never for delegating architectural decisions.
