The parked receipt carries name/state, pause reason, payload, full source and command facts. SDK drain explicitly shows the command has not started. Delta is new output, separate from the final result. Source lines use ` | `; `>| ` is used only when the pause location is reliable.

Submit another { name, program, intent } request to unpark or abandon the target by name. signal/send require Exec and do not resume it. unpark(name,value?) shares the new request's existing deadline.
