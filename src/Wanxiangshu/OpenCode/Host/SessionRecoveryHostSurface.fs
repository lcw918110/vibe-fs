namespace Wanxiangshu.OpenCode

open Wanxiangshu.Persistence.Journal.JournalOutcome
open System
open System.Threading.Tasks
open Fable.Core.JsInterop
open Wanxiangshu.Context.Prefix
open Wanxiangshu.Execution.Session.ChatExecution
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Foundation.Outcome
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Participant.Persona
open Wanxiangshu.Participant.Provider.Attempt
open Wanxiangshu.Persistence.EventStore
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Composition.Durable.Fact

/// JS semantic boundary over the compiled session recovery host.
///
/// The host, scope and journal stay opaque handles: JS obtains them from
/// `bootRecoveryHost`, passes them back, and never inspects them. Proof
/// inputs cross as plain strings (session/physical/provider-run ids and the
/// `absent`/`reject`/`accept` port answer); observations return as plain JSON.
/// Every decision — resume/manual disposition, provider-started seeding,
/// terminal settlement and manual revocation — delegates to the real
/// SessionRecoveryHost, PluginRecoveryScope and ManagedChat journal owners.
/// No recovery model is copied here.
module SessionRecoveryHostSurface =

    /// Opaque recovery host bundle: JS obtains it from `bootRecoveryHost`,
    /// passes it back, and never inspects it.
    type RecoveryHostHandle =
        { Journal: JournalHandle
          Scope: PluginRecoveryScope
          Sessions: PluginSessionScope
          Host: SessionRecoveryHost
          PortOutcome: string
          TerminalGate: TaskCompletionSource<unit>
          TerminalArrival: TaskCompletionSource<unit>
          ResumeCalls: System.Collections.Generic.List<bool> }


    let private acceptedEvidence (sessionId: string) (physicalUserMessageId: string) : AcceptedChatExecutionEvidence =
        let identity =
            ParticipantIdentity.resolveAtRoot "engineer"
            |> Result.defaultWith (fun error -> invalidOp (sprintf "%A" error))

        { SessionId = SessionId.create sessionId
          LogicalRunId = LogicalRunId.create $"run-{sessionId}"
          AuthorityRootUserMessageId = AuthorityRootUserMessageId.create $"root-{sessionId}"
          AuthorityKind = PromptRootAuthorityKind.HumanRoot
          IdentitySeed = RootSelection identity
          PhysicalUserMessageId = PhysicalUserMessageId.create physicalUserMessageId
          Origin = PromptOrigin.AuthorityRoot PromptRootAuthorityKind.HumanRoot }

    let private keyOf (evidence: AcceptedChatExecutionEvidence) : ChatExecutionKey =
        { SessionId = evidence.SessionId
          PhysicalUserMessageId = evidence.PhysicalUserMessageId }

    let private reasonName =
        function
        | ManualInterventionReason.MissingExternalReceipt -> "MissingExternalReceipt"
        | ManualInterventionReason.AmbiguousExternalReceipt -> "AmbiguousExternalReceipt"
        | ManualInterventionReason.PhysicalOutcomeUnknown -> "PhysicalOutcomeUnknown"
        | ManualInterventionReason.PersistenceOutcomeUnknown -> "PersistenceOutcomeUnknown"
        | ManualInterventionReason.NoAuthorizedProviderDisposition -> "NoAuthorizedProviderDisposition"

    let private observationName =
        function
        | ProviderPhysicalObservation.ReceiptMissing -> "ReceiptMissing"
        | ProviderPhysicalObservation.ReceiptAmbiguous -> "ReceiptAmbiguous"
        | ProviderPhysicalObservation.ProviderAbsent _ -> "ProviderAbsent"
        | ProviderPhysicalObservation.ProviderAlive _ -> "ProviderAlive"
        | ProviderPhysicalObservation.ProviderTerminal _ -> "ProviderTerminal"

    let private dispositionName =
        function
        | ChatExecutionTerminalDisposition.Completed -> "Completed"
        | ChatExecutionTerminalDisposition.Cancelled -> "Cancelled"
        | ChatExecutionTerminalDisposition.Rejected -> "Rejected"
        | ChatExecutionTerminalDisposition.Failed -> "Failed"

    let private lifecycleView (state: ChatExecutionState) : obj =
        match state with
        | ChatExecutionState.Accepted _ ->
            box
                {| phase = "Accepted"
                   disposition = null |}
        | ChatExecutionState.Started _ ->
            box
                {| phase = "ProviderStarted"
                   disposition = null |}
        | ChatExecutionState.EndedBeforeStart(_, outcome) ->
            box
                {| phase = "Terminal"
                   disposition = dispositionName (PreStartOutcome.disposition outcome) |}
        | ChatExecutionState.EndedAfterStart(_, disposition) ->
            box
                {| phase = "Terminal"
                   disposition = dispositionName disposition |}

    let private manualView (request: ManualInterventionRequest) : obj =
        let lifecycle = lifecycleView request.ExecutionState

        box
            {| reason = reasonName request.InterventionReason
               observation = observationName request.ProviderObservation
               lifecycle = lifecycle?phase
               disposition = lifecycle?disposition
               sessionId = SessionId.value request.ExecutionState.key.SessionId
               physicalUserMessageId = PhysicalUserMessageId.value request.ExecutionState.key.PhysicalUserMessageId |}

    let private manualsOf (scope: PluginRecoveryScope) : obj =
        scope.ManualChatInterventions() |> Array.map manualView |> box

    let private portOf
        (calls: System.Collections.Generic.List<bool>)
        (portOutcome: string)
        : ExactAcceptedMessageRecoveryPort option =
        match portOutcome with
        | "absent" -> None
        | "accept" ->
            Some
                { ResumeAccepted =
                    fun _ ->
                        calls.Add true
                        Task.FromResult true }
        | "reject" ->
            Some
                { ResumeAccepted =
                    fun _ ->
                        calls.Add true
                        Task.FromResult false }
        | value -> invalidArg "portOutcome" $"unknown recovery proof port outcome '{value}'"

    /// Boot a temp journal, empty snapshot scope and the production recovery
    /// host. The snapshot port is the fixed empty physical read (no assistant
    /// material observed); only the accept/reject port answer varies.
    let bootRecoveryHost (directory: string) (portOutcome: string) : Task<RecoveryHostHandle> =
        task {
            let calls = System.Collections.Generic.List<bool>()
            // Fail fast on an unknown port answer before booting durability.
            let port = portOf calls portOutcome

            let! opened =
                JournalSurface.bootWithWriterId
                    directory
                    (Guid.NewGuid().ToString("N"))
                    $"recovery-proof-{portOutcome}"
                    4242
                    "2026-08-30T00:00:00Z"

            let ok: bool = opened?ok

            if not ok then
                let error: string = opened?error
                return invalidOp $"recovery proof journal boot failed: {error}"
            else
                let journalHandle: JournalHandle = opened?journal
                let scope = PluginRecoveryScope(None)

                let snapshot =
                    { new ISessionSnapshotPort with
                        member _.GetMessages _ = Task.FromResult(Ok []) }

                let host = SessionRecoveryHost(journalHandle.Journal, snapshot, scope, port)

                let plainGate =
                    TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

                let plainArrival =
                    TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

                plainGate.SetResult()
                plainArrival.SetResult()

                return
                    { Journal = journalHandle
                      Scope = scope
                      Sessions = PluginSessionScope(Some journalHandle.Journal, (fun _ -> false))
                      Host = host
                      PortOutcome = portOutcome
                      TerminalGate = plainGate
                      TerminalArrival = plainArrival
                      ResumeCalls = calls }
        }

    /// Resume one accepted execution through the production host and report
    /// the complete observable output: port invocations and manual
    /// interventions. Recovery retains no resume DTO; the returned view is
    /// the whole effect.
    let resumeAccepted (handle: RecoveryHostHandle) (sessionId: string) (physicalUserMessageId: string) : Task<obj> =
        task {
            let evidence = acceptedEvidence sessionId physicalUserMessageId

            do!
                handle.Host.ResumePreProvider
                    { ExecutionKey = keyOf evidence
                      AcceptedEvidence = evidence }

            return
                box
                    {| calls =
                        if handle.PortOutcome = "absent" then
                            0
                        else
                            handle.ResumeCalls.Count
                       manuals = manualsOf handle.Scope |}
        }

    let seedAccepted (handle: RecoveryHostHandle) (sessionId: string) (physicalUserMessageId: string) : Task<unit> =
        task {
            let evidence = acceptedEvidence sessionId physicalUserMessageId

            match! ManagedChatAcceptance.accept handle.Journal.Journal (keyOf evidence) evidence with
            | Ok _ -> ()
            | Error error -> return invalidOp $"recovery proof acceptance seeding failed: {error}"
        }

    /// Seed Accepted + ProviderStarted through the real managed-chat journal
    /// owners so a later terminal settlement revokes through production fold.
    let seedProviderStarted
        (handle: RecoveryHostHandle)
        (sessionId: string)
        (physicalUserMessageId: string)
        (providerRun: string)
        : Task<unit> =
        task {
            let evidence = acceptedEvidence sessionId physicalUserMessageId
            let key = keyOf evidence
            let journal = handle.Journal.Journal
            let run = ProviderRunIdentity.create providerRun

            do! seedAccepted handle sessionId physicalUserMessageId

            match!
                ManagedChatProviderLifecycle.providerStarted
                    journal
                    key
                    evidence
                    run
                    ProviderRequestKind.WorkMain
                    XProjectionChoice.UseCommittedEpoch
            with
            | Ok _ -> ()
            | Error error -> return invalidOp $"recovery proof provider-started seeding failed: {error}"
        }

    /// Finalize with provider-started terminal evidence through the
    /// production host and report the settled projection lifecycle plus the
    /// remaining manuals (revocation proof).
    let finalizeCompleted
        (handle: RecoveryHostHandle)
        (sessionId: string)
        (physicalUserMessageId: string)
        (providerRun: string)
        : Task<obj> =
        task {
            let evidence = acceptedEvidence sessionId physicalUserMessageId
            let key = keyOf evidence

            let started =
                { Accepted = evidence
                  ProviderRun = ProviderRunIdentity.create providerRun
                  RequestKind = ProviderRequestKind.WorkMain
                  ProjectionChoice = XProjectionChoice.UseCommittedEpoch }

            do!
                handle.Host.Finalize
                    { ExecutionKey = key
                      TerminalEvidence = ChatExecutionTerminalEvidence.AfterProviderStart started
                      TerminalDisposition = ChatExecutionTerminalDisposition.Completed }

            let projection =
                (AgentJournal.snapshot handle.Journal.Journal).AgentProjections.ChatExecutions

            let lifecycle =
                ChatExecutionProjection.byKey key projection
                |> Option.map lifecycleView
                |> Option.defaultWith (fun () -> invalidOp "recovery proof terminal settlement left no projection")

            return
                box
                    {| manuals = manualsOf handle.Scope
                       lifecycle = lifecycle?phase
                       disposition = lifecycle?disposition
                       sessionId = sessionId
                       physicalUserMessageId = physicalUserMessageId |}
        }

    let executionStatus (handle: RecoveryHostHandle) (sessionId: string) (physicalUserMessageId: string) : obj =
        let key = keyOf (acceptedEvidence sessionId physicalUserMessageId)

        (AgentJournal.snapshot handle.Journal.Journal).AgentProjections.ChatExecutions
        |> ChatExecutionProjection.byKey key
        |> Option.map lifecycleView
        |> Option.defaultValue null

    let journalExecutionStatus (handle: JournalHandle) (sessionId: string) (physicalUserMessageId: string) : obj =
        let key: ChatExecutionKey =
            { SessionId = SessionId.create sessionId
              PhysicalUserMessageId = PhysicalUserMessageId.create physicalUserMessageId }

        (AgentJournal.snapshot handle.Journal).AgentProjections.ChatExecutions
        |> ChatExecutionProjection.byKey key
        |> Option.map lifecycleView
        |> Option.defaultValue null

    let signalCancelled (handle: RecoveryHostHandle) (sessionId: string) (physicalUserMessageId: string) : Task<obj> =
        task {
            let evidence = acceptedEvidence sessionId physicalUserMessageId
            let key = keyOf evidence
            do! handle.Host.Signal(ChatExecutionRecoveryLifecycleEvent.SessionCancelled key)

            let projection =
                (AgentJournal.snapshot handle.Journal.Journal).AgentProjections.ChatExecutions

            let lifecycle =
                ChatExecutionProjection.byKey key projection
                |> Option.map lifecycleView
                |> Option.defaultWith (fun () -> invalidOp "recovery proof cancellation left no projection")

            return
                box
                    {| manuals = manualsOf handle.Scope
                       lifecycle = lifecycle?phase
                       disposition = lifecycle?disposition
                       sessionId = sessionId
                       physicalUserMessageId = physicalUserMessageId |}
        }

    /// Release the journal capability. The caller removes the directory.
    let disposeRecoveryHost (handle: RecoveryHostHandle) : unit = JournalSurface.dispose handle.Journal

    /// provider-attempt-recovery-023: the Host published session idle. The sweep considers only the
    /// session's `Accepted ∧ ¬ProviderStarted` executions; the returned view is the
    /// whole observable effect (port invocations + manual interventions).
    let signalSessionQuiesced (handle: RecoveryHostHandle) (sessionId: string) : Task<obj> =
        task {
            do! handle.Host.Signal(ChatExecutionRecoveryLifecycleEvent.SessionQuiesced(SessionId.create sessionId))

            return
                box
                    {| calls =
                        if handle.PortOutcome = "absent" then
                            0
                        else
                            handle.ResumeCalls.Count
                       manuals = manualsOf handle.Scope |}
        }

    /// managed-chat-execution-015: the transform refused the provider start
    /// boundary for this exact execution. The returned view is the whole
    /// observable decision: the settlement result plus the remaining manuals.
    let signalProviderStartBoundaryRejected
        (handle: RecoveryHostHandle)
        (sessionId: string)
        (physicalUserMessageId: string)
        (reason: string)
        : Task<obj> =
        task {
            let key: ChatExecutionKey =
                { SessionId = SessionId.create sessionId
                  PhysicalUserMessageId = PhysicalUserMessageId.create physicalUserMessageId }

            let! result = handle.Host.SettleProviderStartBoundaryRejected(key, reason)

            return
                box
                    {| result = result
                       manuals = manualsOf handle.Scope |}
        }

    /// provider-attempt-recovery-024: the load phase abandoned a stale Blogger
    /// open request; settle the same-source Accepted-without-ProviderStarted
    /// executions and report the counts.
    let settleStaleBloggerAcceptedExecutions (handle: RecoveryHostHandle) (sessionId: string) : Task<obj> =
        task {
            let! settled, alreadyTerminal = handle.Host.SettleStaleBloggerAcceptedExecutions(SessionId.create sessionId)

            return
                box
                    {| settled = settled
                       alreadyTerminal = alreadyTerminal
                       manuals = manualsOf handle.Scope |}
        }

    /// managed-chat-execution-006 B1 seam: a transparent pass-through writer that
    /// holds or fails only ChatExecution Terminal appends. Every other fact,
    /// read and lifecycle call forwards to the real writer unchanged; a held
    /// append still executes on the real writer once the gate opens, and the
    /// unknown answer never writes a line.
    type private ControlledTerminalWriter
        (inner: IJournalWriter, mode: string, gate: TaskCompletionSource<unit>, arrival: TaskCompletionSource<unit>) =

        interface IJournalWriter with
            member _.RuntimeId = inner.RuntimeId
            member _.BlobWriter = inner.BlobWriter
            member _.LocalSeq = inner.LocalSeq
            member _.LastCommittedLocalSeq = inner.LastCommittedLocalSeq
            member _.IsPoisoned = inner.IsPoisoned
            member _.TryCurrent key = inner.TryCurrent key

            member _.Append stream providerRun fact =
                match fact with
                | Fact.Agent(AgentFact.ChatExecution(ChatExecutionFactCases.Terminal _)) ->
                    arrival.SetResult()

                    match mode with
                    | "held" ->
                        task {
                            do! gate.Task
                            return! inner.Append stream providerRun fact
                        }
                    | "commitUnknown" ->
                        let unknown: CommitResult<Envelope> =
                            CommitUnknown(
                                EventId.create "controlled-terminal-unknown",
                                JournalFailure.WriteFailed "controlled terminal commit unknown"
                            )

                        Task.FromResult(unknown)
                    | _ -> inner.Append stream providerRun fact
                | _ -> inner.Append stream providerRun fact

            member _.Release() = inner.Release()
            member _.ReleaseAsync() = inner.ReleaseAsync()
            member _.RefreshCurrent() = inner.RefreshCurrent()

    /// managed-chat-execution-006: boot the same real recovery host over a real
    /// on-disk journal whose writer passes Terminal appends through a controlled
    /// barrier. `terminalMode` is "committed" (plain pass-through), "held" (the
    /// Terminal append parks on the gate until `releaseTerminalBarrier`), or
    /// "commitUnknown" (the Terminal append answers a typed unknown and never
    /// writes a line).
    let bootControlledRecoveryHost
        (directory: string)
        (portOutcome: string)
        (terminalMode: string)
        : Task<RecoveryHostHandle> =
        task {
            let calls = System.Collections.Generic.List<bool>()
            // Fail fast on unknown answers before booting durability.
            let port = portOf calls portOutcome

            match terminalMode with
            | "committed"
            | "held"
            | "commitUnknown" -> ()
            | other -> invalidArg "terminalMode" $"unknown controlled terminal mode '{other}'"

            let gate =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            let arrival =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            let integrator =
                CanonicalIntegrator.createWithRules CanonicalIntegrator.baseRules AuthoritativeEventTypes.isKnown

            let store =
                EventStore.createLocal directory (Guid.NewGuid().ToString("N")) integrator

            let! result =
                EventStoreJournalWriter.resumeOrCreate (
                    RuntimeId.create $"recovery-controlled-{terminalMode}",
                    4242,
                    DateTimeOffset.Parse "2026-10-04T00:00:00Z",
                    store
                )

            match result with
            | Error error -> return invalidOp $"recovery controlled journal boot failed: {error.Fact}: {error.Reason}"
            | Ok(writer, _, projection) ->
                let controlled = ControlledTerminalWriter(writer, terminalMode, gate, arrival)

                match AgentJournal.createFromProjection controlled projection with
                | Error error ->
                    return invalidOp $"recovery controlled journal boot failed: {error.Fact}: {error.Reason}"
                | Ok journal ->
                    let scope = PluginRecoveryScope(None)

                    let snapshot =
                        { new ISessionSnapshotPort with
                            member _.GetMessages _ = Task.FromResult(Ok []) }

                    let host = SessionRecoveryHost(journal, snapshot, scope, port)

                    return
                        { Journal = JournalHandle.Create(journal)
                          Scope = scope
                          Sessions = PluginSessionScope(Some journal, (fun _ -> false))
                          Host = host
                          PortOutcome = portOutcome
                          TerminalGate = gate
                          TerminalArrival = arrival
                          ResumeCalls = calls }
        }

    /// managed-chat-execution-006: wait until the controlled writer has parked a
    /// Terminal append (the barrier is holding the terminal writer).
    let awaitTerminalBarrier (handle: RecoveryHostHandle) : Task<unit> = handle.TerminalArrival.Task

    /// managed-chat-execution-006: open the held Terminal barrier; the parked
    /// append then executes on the real writer.
    let releaseTerminalBarrier (handle: RecoveryHostHandle) : unit = handle.TerminalGate.SetResult()

    /// managed-chat-execution-006: deliver the exact assistant terminal event
    /// through the production recovery signal path (Signal -> decision ->
    /// Finalize -> terminal append -> exact release) and report the settled
    /// lifecycle view. A persistence failure rejects the returned task.
    let signalExactTerminal
        (handle: RecoveryHostHandle)
        (sessionId: string)
        (physicalUserMessageId: string)
        (providerRun: string)
        (disposition: string)
        : Task<obj> =
        task {
            let evidence = acceptedEvidence sessionId physicalUserMessageId
            let key = keyOf evidence

            let started =
                { Accepted = evidence
                  ProviderRun = ProviderRunIdentity.create providerRun
                  RequestKind = ProviderRequestKind.WorkMain
                  ProjectionChoice = XProjectionChoice.UseCommittedEpoch }

            let terminal =
                match disposition with
                | "Completed" -> ChatExecutionTerminalDisposition.Completed
                | "Cancelled" -> ChatExecutionTerminalDisposition.Cancelled
                | "Rejected" -> ChatExecutionTerminalDisposition.Rejected
                | "Failed" -> ChatExecutionTerminalDisposition.Failed
                | other -> invalidArg "disposition" $"unknown terminal disposition '{other}'"

            do! handle.Host.Signal(ChatExecutionRecoveryLifecycleEvent.ExactAssistantTerminal(started, terminal))

            let projection =
                (AgentJournal.snapshot handle.Journal.Journal).AgentProjections.ChatExecutions

            return
                ChatExecutionProjection.byKey key projection
                |> Option.map lifecycleView
                |> Option.defaultWith (fun () ->
                    box
                        {| phase = "Missing"
                           disposition = null |})
        }

    /// managed-session-lifecycle-019: drive the session-deletion drain owner —
    /// the same PluginSessionScope.ClearSession the runtime's DisposeSession
    /// awaits — so the returned task is the public lifecycle completion
    /// promise of the delete drain.
    let clearSession (handle: RecoveryHostHandle) (sessionId: string) : Task = handle.Sessions.ClearSession sessionId

    /// managed-session-lifecycle-019: drive the logical-cancel drain owner —
    /// the same SessionRecoveryHost.SignalSession the runtime routes
    /// SignalChatRecoverySession through — settling every key of the session.
    let signalSessionCancelled (handle: RecoveryHostHandle) (sessionId: string) : Task =
        handle.Host.SignalSession(SessionId.create sessionId, ChatExecutionRecoveryLifecycleEvent.SessionCancelled)
