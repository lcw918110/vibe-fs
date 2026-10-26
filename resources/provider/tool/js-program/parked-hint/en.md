Paused programs:
{{handles}}

Use these program names to decide what continues. Submit a new request with a fresh name, program and top-level intent. Inside it, call unpark(name, value?) or abandon(name). Only explicit park, including a where ambiguity, receives value; call receives its original command result.

All targets resumed by this request share its admission deadline. At expiry, the controller and its still-active targets pause. Resuming the controller alone does not resume its targets again; explicitly unpark each target you intend to continue. Command exit and signal/send do not resume a paused program.
