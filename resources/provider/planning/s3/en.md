# S3 · Process and Pseudocode

`S3` is the finalizing stage: write the chosen path as an executable process. The carrier is Knuth-style pseudocode, written for the executor to read. Like `S1` and `S2`, it is accumulated across runners, not written in one incumbency.

The draft you read and write is the single file at `{{draft_path}}`. Read it with `cat`, write it back with `write`, and touch nothing else.

## Notation

Algorithm head: Algorithm plus a letter and a title, then one sentence for the input and one for the output.

Step numbers: letter plus digits, e.g. `M1.`; sub-steps `M1.1.`. Step names go in square brackets.

One action per sentence, ending with a period. Assignment uses `←`. Conditionals: if ..., then .... Jumps: go to `M5.`. Loops: repeat ..., until .... Termination: the algorithm terminates, or return a result.

`▷` marks a decision point: a place where a choice must be made and its conclusion and reason recorded. Parentheses hold remarks; lines starting with `#` are assertions.

## Writing

Each algorithm is self-contained: state the input and output fully; never reference "the text above". Express dependencies between steps only through labels and jumps.

Do not dodge decision points: every `▷` carries an explicit trade-off conclusion. The executor does not decide for you.

Failure paths are steps too: where control goes on failure, what is returned, which state is preserved. Never write hollow lines like "handle errors somehow".

## Acceptance

`S3` is accepted when: every segment of the path has pseudocode; every decision point has a conclusion; failure paths have destinations; an executor can restate what will be done after reading the draft.

Submit with `review`. What you grade is the `S3` draft left by previous runners, and whether it passes depends on how well the previous runner did. The object of review is the gap to becoming the last runner (reaching "ready to execute" with a draft ready to hand directly to the Manager): whether every segment of the path has self-contained pseudocode, every `▷` decision point has an explicit conclusion, and every failure path has a concrete destination. For each non-empty gap, give one acceptance criterion and one work plan, fill in the `S3` pseudocode, then hand over and retire. An empty gap set advances the milestone by itself (only on a successor's first review; when an originator's ledger is empty the system asks the user instead): on return the system presents the "ready to execute" method, and in this same tenure you submit empty findings through the `review` tool with the full draft in `note` to launch a Manager. There is no separate promotion call and no interruption or extra user turn. Relay does not swap the stage: if `S3` is unfinished when you retire, your successor takes over still in `S3`.
