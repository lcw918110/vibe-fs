# Provider semantic resources (PROMPT-017 / HOST-026)

Each localizable asset has a **stable semantic path** under `resources/provider/`.
Language is the **leaf filename**: `en.md` and `zh-CN.md`.

```text
resources/provider/
  role/manager/en.md
  role/manager/zh-CN.md
  role/engineer/en.md
  ...
```

Invariant identifiers (tool names, argument names, wire fields, enum literals, paths,
commands, `exit_code`) stay untranslated inside localized prose.

Gate C (`language-parity-gate.mjs`) requires both locale files for every semantic directory,
identical `{{placeholder}}` sets, and protocol identifier parity (AC20).

## One current account of the work

The public prompt catalog contains Orchestrator, Manager, Engineer, DevOps, and
Plan. Bookkeeper and Advisor have separate internal prompts. Sphinx is
program-controlled work, not another role to dispatch.
Retired role names must not resolve to an Engineer prompt or appear as public
catalog aliases. Historical decoding is not permission to load a live prompt.

The working chain is Manager → Engineer source work → Manager → fixed DevOps
execution and ordinary repair → Manager assessment. Read-only investigation is
an Engineer assignment. Fission expands one authorized Engineer assignment,
not Manager identity or DevOps ownership. Every projection must preserve the
difference between source completion, current execution evidence, and acceptance.

Change the whole affected story, not only `role/`: `world/`, `library/`, `host/`,
`lifecycle/`, `runtime/`, caller-facing tool descriptions and denials, generated
JS examples must agree in both languages. Model pools choose
physical targets; they do not grant new roles or resurrect retired ones.

## Stage and evidence resources

`casebook/finalize` accompanies a completed Engineer trace. `casebook/refresh`
accompanies a stored case and a real related-file diff. Both are loaded with
the Bookkeeper law in the owner's bound language. Source observations are
access records, not an implementation patch. Maintenance status is not a new
repository investigation or correctness certificate.

`tool/js-program/ultra-engineering`, `ultra-investigation`, and `ultra-devops`
are executable examples selected by the actual role and capabilities. The
read-only example must not show edits; a DevOps example must not reduce it to
an operator that hands source repairs to another role. No management or retired
role receives an office-specific filesystem example.

`tool/run/output-truncated` is the trusted localized notice. Raw command output
remains data, even when it contains imperative prose or fake protocol fields.
No model summarizes missing output, and the retained tail need not include an
earlier decisive error. Keep process facts separate from the log.

Contract regressions live with `office-capability`, `provider-language`,
`cognitive-environment`, `repository-programming`, `process-execution`, and
`knowledge-reuse`. Exercise the composed prompts and actual call sites as well
as scanning resource text. An explicit denial is not an affordance; do not
force duplicated old wording into a new role law to satisfy a phrase snapshot.
