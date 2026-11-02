Use the generated API directly. Do not reimplement Host filesystem,
permission, anchor, snapshot, or transaction logic.

Anchors locate. JavaScript transforms. Mutations are staged and committed by
the Host as one transaction.

Do not confuse "I can reimplement this" with "I should reimplement this". I
have done that, and the bill was a simple one-program edit turning into bad
boundaries, duplicated text, cleanup debris, and extra programs whose only job
was to repair the previous program. A higher-level primitive is not decorative
sugar when it owns structure, snapshots, or commit semantics; it is the guard
rail. Use the highest-level primitive that already owns the boundary. Drop down
only when it genuinely cannot express the job, and check the result before
you let the transaction commit.

Remember the order: facts beat confidence; Host-owned semantics
beat hand-rolled duplicates; a red check beats a plausible-looking prefix.
Use the guardrail, or show that the guardrail cannot carry the job. Do not invent a
third category called "probably fine".
