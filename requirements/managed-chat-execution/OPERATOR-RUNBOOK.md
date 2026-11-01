# Managed chat execution operator runbook

## Safety boundary

Incident handling is observation plus owner effect requests. Never edit journal facts, capacity maps, fences, queue nodes, counters, bindings, recovery ownership, Host messages, or provider receipts. Never clear a counter, release capacity, terminalize an execution, authorize retry/fallback, or resend an accepted message by hand. Facts are corrected only by an ordinary owner append; physical reconciliation runs only through `ChatExecutionRecoveryRuntime` ports.

Evidence must not contain prompt/content/payload, stack, token, cookie, credential, filesystem path, or free-form provider response. `ReliabilityDiagnosticsSurface.projectRecord` is the only causal-record adapter; it rejects payload fields and redacts credential/path material.

## Exact read surfaces

All imports below are compiled, registered `dist` surfaces. Use exact `(sessionId, physicalUserMessageId)`; a session alone is not execution identity.

| Question | API | Result |
|---|---|---|
| Durable identity and lifecycle | `Execution/Session/ChatExecution/Surface.js`: `canonicalize(serializedFact)`, `fold(serializedFacts)`, `nonTerminal(serializedFacts, sessionId)` | canonical facts and projection containing logical run, authority root, effective agent, provider run, request kind, phase, disposition |
| Exact status | `Execution/Session/ChatExecution/StatusSurface.js`: `queryFacts(serializedFacts, sessionId, physicalUserMessageId)` | `{ accepted, providerStarted, terminal, disposition }` |
| Live admission | `ModelRoutingSurface.admissionSnapshot(routingRuntime, sessionId, physicalUserMessageId)` | active capacity, pending admissions, exact provider binding count |
| Capacity, queue, fences | `OpenCode/Host/ModelRoutingSurface.js`: `sharedCapacitySnapshot()`; isolated runtime: `capacitySnapshot(runtime)` | ledger/tokens/custodies/executions/waiters/owners/lineage, token-state and active counts, duplicate/stale/conflict counters |
| Capacity consistency | `ModelRoutingSurface.reconcileCapacityEvidence(snapshot)` | immutable `NoOp` or typed `FailClosed` reasons; never repairs the snapshot |
| Failure policy | `Execution/Failure/Surface.js`: `decide(typedFailureInput)` | one exclusive resolution plus orthogonal breaker, capacity settlement, and fatality decisions |
| Causal record | `OpenCode/Host/ReliabilityDiagnosticsSurface.js`: `projectRecord(record)` | immutable redacted record; unknown schema fields fail |
| Reliability summary | `ReliabilityDiagnosticsSurface.queryReliability(counters, canonicalExecutions, capacitySnapshot, recoveryOwnershipSnapshot)` | canonical lifecycle, queue/fence and typed recovery ownership counts; read-only |
| Recovery representation | `Execution/Session/ChatExecution/RecoveryRuntimeSurface.js`: `recoverScenarios([scenario])` | typed decisions and append-only owner-port invocation trace; proof representation only |
| Supported Host contract | `requirements/host-boundary/fixtures/opencode-chat-admission-1.18.29.json` | exact OpenCode/plugin `1.18.29`, public hooks/order and terminal evidence |

Re-observe the installed public Host contract:

```sh
node --test requirements/host-boundary/tests/023.test.mjs
```

A version mismatch, missing exact assistant `message.updated` start/terminal evidence, or changed hook order is unsupported. Stop recovery action and escalate.

## Identity conflict

1. Run `Surface.fold(serializedFacts)` and isolate the exact `(sessionId, physicalUserMessageId)` plus `logicalRunId`, authority root and effective agent.
2. Run `StatusSurface.queryFacts(serializedFacts, sessionId, physicalUserMessageId)` for each competing physical message. Same session with different physical ids is valid; conflicting evidence for one exact key is not.
3. Project the conflict with `ReliabilityDiagnosticsSurface.projectRecord`; attach no prompt, content, stack or path.
4. Preserve the first durable fact and escalate. Never overwrite identity, reuse a session-scoped binding, or clear `identityConflicts`.

## Accepted nonterminal

1. Select `phase === 'Accepted'` from `Surface.nonTerminal(serializedFacts, sessionId)` and confirm `providerRun === null`.
2. Obtain exact public provider observation and explicit persistence commitment. Absence of a process-local binding is not provider absence.
3. Capture/replay evidence. Only `ResumePreProvider` from the recovery owner is actionable; call no provider or capacity mutation directly.

## Attempt amplification

1. Use `ReliabilityDiagnosticsSurface.queryReliability(...)` and inspect `physicalAttemptsByLogicalRun` together with each canonical provider run from `Surface.fold`.
2. Correlate attempts by logical run and exact physical message; do not count log lines, hook deliveries or retries inferred from prose.
3. More physical attempts than typed failure-policy authorizations → stop new admission and escalate with facts, policy decisions and public provider receipts.

## Queue saturation

1. Read `ModelRoutingSurface.sharedCapacitySnapshot()`.
2. Record `waiters`, `activeCount`, ledger bounds and `counters`; correlate every waiter to its exact owner.
3. Query `ReliabilityDiagnosticsSurface.queryReliability(...)` for `queueDepth`/`queueFull` evidence.
4. Do not delete waiters, enlarge bounds, bypass admission or force-release a lease. Capacity owner alone consumes typed cancellation/release requests.

## Capacity divergence

1. Freeze one `sharedCapacitySnapshot()` and pass that same object to `reconcileCapacityEvidence(snapshot)`.
2. `NoOp` permits continued observation. `FailClosed` reasons (`ActiveOutsideLedgerBounds`, token/map/owner divergence or counter regression) require immediate escalation.
3. Preserve snapshot and reasons. Never edit ledger, token state, custody, lineage, fences or counters to make reconciliation pass.

## Hook criticality

1. Query `OpenCode/Host/HookPolicySurface.js` with `rows()`; match the failing public hook to its registered criticality and failure disposition.
2. `Security`, `Workflow` and `Invariant` failures remain typed fail-closed. Only the registered degradable/audit-only disposition may continue.
3. Verify installed hook names/order with the public Host canary command above. Never downgrade a critical hook because provider work appears healthy.

## Safe rollback and canary stop

1. Stop rollout/canary traffic on any Host version drift, public hook-order drift, projection mismatch, capacity `FailClosed`, or integrity failure.
2. Preserve the failing evidence envelope before rollback. Roll back only the deployed code/configuration through normal deployment control; never roll back or edit durable facts.
3. Restart normally, wait for `DurabilityActivated` and `PluginRuntimeReloaded`, then capture a fresh envelope and rerun the Host canary.
4. Resume traffic only when the installed version is exactly the passing canary version, capacity reconciliation is `NoOp`, and replay returns only expected typed owner requests.

## Evidence collection

Collect exactly: canonical serialized facts; exact key/status/projection; one immutable capacity snapshot plus reconciliation result; projected causal diagnostics; checked Host canary fixture/version/order; typed recovery observation/runtime decision; SHA-256 integrity. Record absent facts as typed null/absence, never guesses. Never collect secret/token/cookie/credential, stack, or filesystem path. Reject and recollect any envelope containing prompt/content/payload or provider response.

Use `captureEvidence(input, surfaces)`; save only its returned sealed JSON. Validate operationally by replaying with the exact command below. Never collect private Host API output or process-local handle representations.

## Incident evidence

The v1 envelope schema is `requirements/managed-chat-execution/tests/fixtures/incident-evidence-v1.schema.json`. Capture through `captureEvidence(input, surfaces)` from `requirements/managed-chat-execution/tests/support/incident-evidence.mjs`; inputs must come directly from the APIs above and the checked Host canary fixture. The adapter canonicalizes every fact, projects exact status, reconciles capacity, projects/redacts diagnostics, records typed recovery observation/runtime output, then seals deterministic JSON with SHA-256.

Replay a captured envelope:

```sh
node requirements/managed-chat-execution/tests/support/incident-evidence.mjs replay <evidence.json>
```

Success prints canonical execution/capacity/recovery equality, `EffectRequestOnly` owner actions, and `mutations: []`. Tamper, unsupported version, unknown/missing evidence, capacity divergence, projection/status mismatch, unsupported Host contract, or recovery-decision mismatch exits non-zero. Replaying the same envelope never accumulates state or authority.

## Decision tree

1. **No `Accepted` fact / pre-provider**
   - Verify exact key and authority evidence through `fold` + `queryFacts`.
   - If nothing durable exists, there is no managed execution to repair.
   - If `Accepted` exists and public observation proves provider absent, recovery may return `ResumePreProvider`; submit that effect request to `managed-chat-execution`. Do not call provider directly.

2. **In-flight provider**
   - Require exact assistant start evidence: session, parent physical user message, assistant id/provider run, `time.created`.
   - Provider alive → observe only. Exact terminal → owner may request `Finalize`. Missing/ambiguous receipt → manual intervention. Never infer terminal from idle, text, age, registry presence, or process loss.

3. **Terminal but resource unreleased**
   - Confirm durable terminal plus exact capacity owner/fence in the immutable snapshot.
   - `reconcileCapacityEvidence` must be `NoOp`; recovery may request `ReleaseTerminalResource` through capacity owner. Never release by session or edit the fence.

4. **Persistence unknown**
   - Treat as manual intervention. Re-read durable facts through the journal owner and obtain explicit persistence commitment. Do not append a guessed duplicate or continue provider work.

5. **Stale key/provider/policy or capacity conflict**
   - Preserve both observations. Replay must fail closed/manual. Escalate with exact key, provider run, immutable capacity reconciliation reasons and causal record. Do not overwrite the current execution or reset conflict/stale counters.

6. **Fatal after settlement**
   - First prove typed terminal settlement and exact capacity outcome. Only then follow the failure-policy `FatalAfterSettlement` decision. A fatal diagnostic is not settlement evidence and never authorizes process termination before owners settle.

## Restart or plugin reload

1. Capture the pre-restart envelope and retain its SHA-256 seal.
2. Stop/reload only through the deployment's normal process control; this repository exposes no operator command that mutates recovery state.
3. On startup, wait for durable substrate activation. `HostSignalBootstrap` emits `DurabilityActivated`; `PluginRecoveryWiring` emits `PluginRuntimeReloaded`. These events re-enter ordinary recovery from canonical facts. Do not invoke a private Host API or inject a lifecycle event.
   A recovery pass reads each active session transcript once. Settled executions do not read transcripts; held or unknown resources still receive recovery attention. Admission currently waits for the load-phase recovery of other sessions too.
4. Query the exact status and shared capacity snapshot again. Compare owner projections, not process-local lease handles, waiter tokens, callbacks, subscriptions, or old bindings.
5. Capture and replay the post-restart envelope. Submit only the returned typed effect request to its named owner. If replay fails closed, preserve evidence and escalate.

## Mandatory escalation

The current public Host canary proves duplicate `chat.message` delivery and Host-side transform/provider deduplication. It does **not** prove an exact accepted-message replay capability. Therefore any recovery that would require replaying/resending an already accepted physical user message must stop at escalation until a passing public Host canary proves that exact capability. Restart count, operator judgment, raw HTTP, private SDK methods, or manual message construction cannot substitute for that prerequisite.
