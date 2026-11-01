A program may mutate each canonical path exactly once. A second edit/rewrite/write
on the same path is DUPLICATE_MUTATION_TARGET. Put independent local changes in
one edit array; computed multi-phase edits belong in JavaScript variables, then
one rewrite/write.

I have also made the worse version of that mistake: build too much, delete pieces
with replace, leave blank separators and dangling fragments, then write second
and third cleanup programs to repair the first program. That is not a healthy
"multi-step refactor"; it shows that the first program never defined the
target file clearly enough. Build the final text in memory from trusted slices,
then stage one mutation for that path.

Before return, check cheap rules that would make a bad result obvious:
rough line/length scale, required headings or sentinels, expected number of
critical sections. If an ordinary reorganization turns roughly 8k lines into
roughly 31k lines, do not inspect the first fifty lines, decide they look fine,
and continue guessing. throw before return. Because mutations are staged, that
failure leaves zero committed mutations. A ridiculous number is a signal; treat
it as one before you create a second problem to repair the first.

Commit before you act: decide the cheap rules before you mutate. Once the
program has stated what must remain true, it does not get to renegotiate those
rules after seeing an inconvenient result. "The first screen looks fine" does not
show anything. "One more replace will probably clean it up" is not a signal. Numbers,
required sentinels, and section counts outrank the story you are tempted to tell
yourself after a bad transformation.

STOP SIGNAL: if size, section count, sentinel count, or another cheap rule
is wildly outside the expected range, the current program has lost the right to
commit. Fail it. Do not reward a suspicious result with another speculative
transformation. The fastest path after a red flag is back to facts, not
forward into cleanup.

The generated class has no commit, rollback, snapshot, or transaction methods.
run() returning normally → Host preflight → prepare → commit. run() throwing or
any generated filesystem method failure discards every staged mutation.

run() must return a JSON-compatible value: null, boolean, finite number, string,
array, or plain object (recursive). undefined, BigInt, NaN, Infinity, function,
symbol, cyclic or exotic objects fail as INVALID_RETURN_VALUE before commit.
