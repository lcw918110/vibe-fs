# Restart Status Guidance

The Wanxiangshu process has just restarted. Process-local state is gone, but durable facts, the shared workspace, and the Git repository remain.

This means:

- Tool calls that were running in the previous process are treated as interrupted: they stay failed in visible history and are never replayed or completed implicitly.
- Active child work left over by the previous runtime can no longer return, so it is voided rather than completed: `horizon` shows it as voided and `join` reports it once, then it retires.
- This process has already re-normalized from durable facts. Continue from the current real facts and do not assume the previous process's temporary state still exists.

The world may have changed. A restart is often the very act that loads new code or a fix, and it may have happened after your last memory. Therefore:

- Treat every pending item of the kind "wait for the restart", "wait for the new code", or "wait for some process" as stale: the restart has happened; do not keep waiting for it.
- Conclusions in your context about process ids, build generation, HEAD, or child-work state may no longer hold. When you need them, read the current fact again and act on it; do not reason forward from the old conclusion.

Do this now:

1. Look again at the child work you own (offices that have `horizon` use it): whatever was voided but is still needed, send out or do again.
2. Go through the obligations you still hold, fix the next step for each, and move them forward in this turn; move independent ones at the same time. Offices with a todowrite program entry report `remainingScopeTurnsEstimate` truthfully in this turn's call.
3. Do not end this turn with "no change", "waiting for handover", "waiting for the restart" or similar. If every useful action truly depends on a fact that has not arrived, name that fact and where it will come from.
