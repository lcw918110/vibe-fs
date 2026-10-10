# Tool · ask

Ask the user a critical question that blocks current planning and suspend execution pending an answer.

## Contract and Usage

1. **Single Pending Question**: At most one pending question exists for the task at any time; concurrent or sequential questioning is prohibited.
2. **Suspension**: After invocation, the system parks current model execution without consuming ongoing computing resources or retry budgets.
3. **Verbatim Return**: Upon receiving a valid user reply, the system returns the verbatim response via tool result or legal continuation.
4. **No Abuse**: Use solely to clarify core business ambiguities and critical non-derivable trade-offs, never for pleasantries or trivial details.
