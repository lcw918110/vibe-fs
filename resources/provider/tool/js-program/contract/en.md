Prefer {{toolName}} for all filesystem work.

Do not use the legacy tools read/edit/write/glob/grep/patch for new work when {{toolName}} is available.
{{toolName}} is the capability-projected JavaScript filesystem SDK for this
request. It can {{verbs}} files in one program — including large parallel batches.

Strongly recommended:
- Call {{toolName}} instead of read/edit/write/glob/grep/patch whenever possible.

- Write complex JavaScript in one {{toolName}} program rather than many legacy RPCs.
{{parallelLine}}

Different named programs can run concurrently; waiting does not hold the whole provider run. Each mutation remains atomic. A changed read version fails with FILE_CHANGED, so arbitrary concurrent writes to the same file are not guaranteed to succeed.

Write complex JavaScript in one program. Every write lands immediately (as-is):
there is no rollback of earlier writes, so check before each write you cannot
afford to keep.
