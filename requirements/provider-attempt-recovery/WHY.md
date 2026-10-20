# provider-attempt-recovery — WHY

A failed provider request should not erase the task or create an endless stream of attempts. A budget owned by the Logical Run bounds automatic recovery without charging an unrelated later task. Exact failure identity prevents concurrent observers and late callbacks from spending the same budget twice.

Changing the executor is different from changing who acts and under whose authority. Durable participant identity preserves responsibility, language and context across retries. Failure accounting and target settlement answer different questions. Recovery has one rule: the first authorized failure of a provider retries that same target unchanged; the second authorized failure of that same target blacklists the provider and retries the next candidate, and so on. There is no second recovery path and no budget exemption for a readonly copy.

Host transport activity, unusable response content and confirmed provider failure carry different evidence. Typed policy permission prevents cleanup, ambiguous acceptance or diagnostic prose from creating a new request. RequestKind likewise keeps maintenance and speculative success from pretending that the owner's business task recovered.

A committed failure explains why recovery is needed; it does not prove the Host stopped sending or that a new process may resume a callback. The exact terminal observation prevents overlapping physical attempts. Conversely, acceptance without execution creates a real obligation: either resume that accepted material with the required capability or report its terminal failure.


## DEPENDS ON

- `participant-identity`
- `execution-failure-policy`
- `execution-model-routing`
- `interaction-authority`
- `context-compression`
- `prefix-stability`
