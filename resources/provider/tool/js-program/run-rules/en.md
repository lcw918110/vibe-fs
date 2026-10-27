Every todowrite request supplies name, program and intent as required strings, plus remainingScopeTurnsEstimate as a required nonnegative number that may carry a fraction. The JS program itself receives only the three execution fields. At admission, UTF8Bytes(intent) × 50ms establishes one deadline. Every operation and every target resumed during the request shares the remaining time. Starting another call or unpark never resets or extends it.

Independent SDK operations may use Promise.all or Promise.allSettled. call(command) first waits for this program's earlier SDK operations to settle, then starts one command. Pending operations are waited for, not rejected, restarted or cancelled; real failures and applied effects remain facts. Draining and command waiting consume the same entry window.

call blocks at its expression even without await. A second call in Promise.all cannot start concurrently. Use different named programs for parallel commands. The host, existing SDK operations, command output handling and other programs remain responsive.

The pure-JS execution budget pauses during call waiting and resumes from its remainder when model code runs again. The request deadline keeps advancing. Command duration is an observation; window expiry does not kill a running command.

Expiry during drain pauses before command startup. Earlier operations may settle later but cannot start it automatically. Expiry during command waiting preserves the original command; a later exit caches its result without resuming code.

Control another program from a new named program with its own top-level intent. Use unpark(name,value?), signal(name,signal), send(name,input), or abandon(name) as authorized. Every resumed target shares the control request's existing deadline. At expiry, controller and active targets pause. Resuming the controller advances only the controller; explicitly unpark each target to continue it. Keep target name and state in its receipt.

Only explicit park, including where ambiguity, receives value. A resumed call receives its original real CallResult. signal and send do not resume the target. A sent signal is not an exit receipt; accepted input is not proof of processing.

abandon requests cancellation and cleanup. Report abandoned only after resource settlement; report incomplete or forced cleanup as observed. Native finally runs when normal unwinding is possible; forced termination cannot prove it ran. Applied writes remain.

Pause delta contains new output and does not consume final CallResult. Preserve actual exit code or signal, retained output and explicit truncation. Do not rerun an operation whose effects are unknown. Program return, command exit, tool response and Work lifecycle decisions remain separate facts.
