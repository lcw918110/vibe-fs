namespace Wanxiangshu.OpenCode

#nowarn "3511"

open System
open System.Collections.Generic
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Change
open Wanxiangshu.Change.Host
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Participant.Provider.Attempt
open Wanxiangshu.Execution.Delegation.Fork.OpenCode
open Wanxiangshu.Execution.Delegation.Handle.OpenCode
open Wanxiangshu.Execution.Delegation.OpenCode
open Wanxiangshu.Execution.Delegation.SyncDelegate.OpenCode
open Wanxiangshu.Execution.Fission.OpenCode
open Wanxiangshu.Execution.Session.OpenCode
open Wanxiangshu.Git
open Wanxiangshu.Interaction.Dispatch.OpenCode
open Wanxiangshu.Mission.Work
open Wanxiangshu.Persistence.EventStore
open Wanxiangshu.Repository.Investigation.Semble
open Wanxiangshu.Repository.Investigation.WarmStart
open Wanxiangshu.Resources


open Wanxiangshu.Persistence.EventStore
open Wanxiangshu.Context.Companion
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Outcome
open Wanxiangshu.Repository.Programming.Js
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Context.Companion
open Wanxiangshu.Execution.Delegation.Fork
open Wanxiangshu.Execution.Delegation.Fork.Host
open Wanxiangshu.Execution.Delegation.Handle
open Wanxiangshu.Execution.Delegation.SyncDelegate
open Wanxiangshu.Execution.Fission
open Wanxiangshu.Execution.Session
open Wanxiangshu.Execution.Session.Attachment
open Wanxiangshu.Execution.Session.Recovery
open Wanxiangshu.Execution.Session.Wait
open Wanxiangshu.Execution.Failure
open Wanxiangshu.Execution.Session.ChatExecution
open Wanxiangshu.Interaction.Repair
open Wanxiangshu.Participant.Persona
open Wanxiangshu.Participant.Provider


open CompanionProjection

module PluginHostInterop =

    [<RequireQualifiedAccess>]
    type internal HookSettlementEvidence =
        | NoOwnedExecution
        | ExactSettlementComplete
        | DurableOutcomeUnknown
        | SettlementIncomplete

    [<RequireQualifiedAccess>]
    type internal HookFailurePolicy =
        | RethrowUnchanged
        | FatalAfterSettlement
        | RejectFatalBeforeSettlement

    type internal HookFailureOutcome =
        { Failure: ExecutionFailure
          Lifecycle: DurableExecutionLifecycle
          ExecutionKey: ChatExecutionKey option
          Settlement: HookSettlementEvidence }

    [<Emit("import('@opencode-ai/plugin/tool')")>]
    let importToolModule () : Task<obj> = jsNative

    [<Emit("$0 instanceof Error ? String($0.message) : String($0)")>]
    let private diagnosticErrorText (error: obj) : string = jsNative

    let private emitFatalRecord operation error =
        Diagnostic.fatal operation [ "result", diagnosticErrorText error ]

    /// Exact execution identity recoverable from the hook arguments themselves.
    /// Session comes from a `sessionID` field (tool hooks, event/tool input) or
    /// from the trailing transcript's single session; the physical user message
    /// id is read from the host `output.messages`/`messages` transcript.
    /// A hook whose arguments carry neither has no provable owned execution:
    /// None is honest, a fabricated identity is not.
    let private nonEmptyText (value: obj) : string option =
        if isNull value then
            None
        else
            string value
            |> fun text -> if String.IsNullOrWhiteSpace text then None else Some text

    [<Emit("$0 == null ? undefined : $0[$1]")>]
    let private fieldValue (carrier: obj) (name: string) : obj = jsNative

    /// Fable-safe array unbox: a non-array typed field returns [] instead of
    /// throwing across the boundary.
    let private tryArray (raw: obj) : obj list =
        try
            unbox<obj array> raw |> Array.toList
        with _ ->
            []

    /// Decode `value.messages` into an F# list or silently return [] — kept
    /// apart so `messagesArrayOf`'s `if` stays flat under the pyramid lint.
    let private messagesFieldOf (value: obj) : obj list =
        let raw = fieldValue value "messages"

        match raw with
        | null -> []
        | _ -> tryArray raw

    let private messagesArrayOf (value: obj) : obj list =
        if isNull value then [] else messagesFieldOf value

    let private transcriptOf (args: obj) (context: obj) : obj list =
        [ fieldValue context "output"; fieldValue args "output"; context; args ]
        |> List.map messagesArrayOf
        |> List.tryFind (fun transcript -> not (List.isEmpty transcript))
        |> Option.defaultValue []

    let private hookSessionId (args: obj) (context: obj) : string option =
        [ args; fieldValue args "input"; context; fieldValue context "input" ]
        |> List.map (fun carrier -> fieldValue carrier "sessionID")
        |> List.tryPick nonEmptyText
        |> Option.orElseWith (fun () ->
            [ fieldValue context "output"; context; fieldValue args "output"; args ]
            |> List.tryPick (fun carrier ->
                if isNull carrier then
                    None
                else
                    ProviderWireDecode.projectionSessionIdFromMessages carrier))

    /// Execution identity PROVABLE from hook arguments alone: session id plus
    /// the transcript's trailing physical user message. Absent evidence ⇒ None,
    /// never a guessed key.
    let internal hookArgumentsKey (args: obj) (context: obj) : ChatExecutionKey option =
        match hookSessionId args context with
        | None -> None
        | Some session ->
            ProviderWireCapture.lastUserMessageId (transcriptOf args context)
            |> Option.map (fun physical ->
                { SessionId = SessionId.create session
                  PhysicalUserMessageId = physical })




    /// Host hook whose F# value stayed CURRIED after compilation.
    /// Keep this arity adaptation as a direct Emit call at the registration site:
    /// moving it behind an ordinary F# helper changes how Fable boxes the original
    /// function and silently turns paired hooks into curried no-ops.
    [<Emit("(args, context) => $0(args)(context)")>]
    let curriedHook (fn: obj) : obj = jsNative

    /// Host hook that Fable emitted as a two-arity arrow.
    ///
    /// Passing that arrow through an `obj` boundary can make Fable substitute a
    /// `curry2(fn)` adapter for `$0`. Calling that adapter with two JS arguments
    /// returns its second-stage function without executing the hook body. Accept
    /// both runtime shapes here: invoke the supplied callable positionally, then
    /// finish the curried second stage when Fable inserted one.
    [<Emit("(args, context) => { const result = $0(args, context); return typeof result === 'function' ? result(context) : result; }")>]
    let pairedHook (fn: obj) : obj = jsNative

    [<Emit("(args, _context) => $0(args)")>]
    let unaryHook (fn: obj) : obj = jsNative

    [<Emit("(_args, _context) => $0()")>]
    let nullaryHook (fn: obj) : obj = jsNative

    let private releaseCompleted =
        function
        | ChatAdmissionReleaseOutcome.Settled CapacityTransitionOutcome.Applied
        | ChatAdmissionReleaseOutcome.Settled CapacityTransitionOutcome.AlreadyApplied ->
            HookSettlementEvidence.ExactSettlementComplete
        | ChatAdmissionReleaseOutcome.Settled CapacityTransitionOutcome.StaleFence
        | ChatAdmissionReleaseOutcome.Settled CapacityTransitionOutcome.Conflict
        | ChatAdmissionReleaseOutcome.BoundaryFailed _ -> HookSettlementEvidence.SettlementIncomplete

    let private persistenceOutcome failure =
        match JournalAppendOutcome.toExecutionFailure failure with
        | ExecutionFailure.PersistenceFailure PersistenceCommitment.NotCommitted as typed ->
            typed, HookSettlementEvidence.SettlementIncomplete
        | ExecutionFailure.PersistenceFailure PersistenceCommitment.Committed as typed ->
            typed, HookSettlementEvidence.SettlementIncomplete
        | ExecutionFailure.PersistenceFailure PersistenceCommitment.Unknown as typed ->
            typed, HookSettlementEvidence.SettlementIncomplete
        | ExecutionFailure.PersistenceFailure PersistenceCommitment.NoNewWrite as typed ->
            typed, HookSettlementEvidence.SettlementIncomplete
        | ExecutionFailure.LocalInvariant
        | ExecutionFailure.ProtocolRejection
        | ExecutionFailure.AuthorizationDenied
        | ExecutionFailure.UserCancelled
        | ExecutionFailure.Superseded
        | ExecutionFailure.CapacityQueueFull
        | ExecutionFailure.ProviderTransient
        | ExecutionFailure.ProviderPermanent
        | ExecutionFailure.AcceptanceUnknown
        | ExecutionFailure.StreamInterruptedAfterFirstToken ->
            invalidOp "journal append failure produced a non-persistence execution failure"

    let private acceptanceFailure =
        function
        | ManagedChatAcceptanceError.IntentRejected _ ->
            ExecutionFailure.ProtocolRejection, HookSettlementEvidence.NoOwnedExecution
        | ManagedChatAcceptanceError.NotAttempted _ ->
            ExecutionFailure.PersistenceFailure PersistenceCommitment.NotCommitted,
            HookSettlementEvidence.NoOwnedExecution
        | ManagedChatAcceptanceError.CommitUnknown _ ->
            ExecutionFailure.AcceptanceUnknown, HookSettlementEvidence.DurableOutcomeUnknown
        | ManagedChatAcceptanceError.NoNewWriteReleaseFailed _ ->
            ExecutionFailure.PersistenceFailure PersistenceCommitment.NoNewWrite,
            HookSettlementEvidence.SettlementIncomplete
        | ManagedChatAcceptanceError.FactRejected _ ->
            ExecutionFailure.PersistenceFailure PersistenceCommitment.Committed, HookSettlementEvidence.NoOwnedExecution
        | ManagedChatAcceptanceError.EstablishedEvidenceConflict _ ->
            ExecutionFailure.LocalInvariant, HookSettlementEvidence.ExactSettlementComplete
        | ManagedChatAcceptanceError.AuthorityRegistrationRejected _
        | ManagedChatAcceptanceError.AttemptEvidenceInvalid _
        | ManagedChatAcceptanceError.AttemptKeyMismatch _
        | ManagedChatAcceptanceError.ProjectionMissingAfterCommit _
        | ManagedChatAcceptanceError.ProjectionConflictAfterCommit _ ->
            ExecutionFailure.LocalInvariant, HookSettlementEvidence.SettlementIncomplete

    let private settlementFailure =
        function
        | PreProviderSettlementError.PersistenceFailed failure -> persistenceOutcome failure
        | PreProviderSettlementError.MissingAccepted _
        | PreProviderSettlementError.EvidenceConflict _
        | PreProviderSettlementError.ProviderAlreadyStarted _
        | PreProviderSettlementError.TerminalConflict _
        | PreProviderSettlementError.InvalidDisposition _
        | PreProviderSettlementError.ProjectionMissingAfterCommit _
        | PreProviderSettlementError.ProjectionConflictAfterCommit _ ->
            ExecutionFailure.LocalInvariant, HookSettlementEvidence.SettlementIncomplete

    let private handoffFailure =
        function
        | ChatAdmissionHandoffSettlement.TerminalCommitted release ->
            ExecutionFailure.LocalInvariant, releaseCompleted release
        | ChatAdmissionHandoffSettlement.SettlementIncomplete failure -> settlementFailure failure
        | ChatAdmissionHandoffSettlement.SettlementBoundaryFailed _ ->
            ExecutionFailure.LocalInvariant, HookSettlementEvidence.SettlementIncomplete

    let private supersessionSettlementFailure =
        function
        | ManagedChatSupersessionError.PreProviderSettlementFailed error -> settlementFailure error
        | ManagedChatSupersessionError.ProviderSettlementFailed(ManagedChatProviderLifecycleError.NotAttempted _) ->
            ExecutionFailure.PersistenceFailure PersistenceCommitment.NotCommitted,
            HookSettlementEvidence.SettlementIncomplete
        | ManagedChatSupersessionError.ProviderSettlementFailed(ManagedChatProviderLifecycleError.CommitUnknown _) ->
            ExecutionFailure.PersistenceFailure PersistenceCommitment.Unknown,
            HookSettlementEvidence.SettlementIncomplete
        | ManagedChatSupersessionError.ProviderSettlementFailed(ManagedChatProviderLifecycleError.NoNewWriteReleaseFailed _) ->
            ExecutionFailure.PersistenceFailure PersistenceCommitment.NoNewWrite,
            HookSettlementEvidence.SettlementIncomplete
        | ManagedChatSupersessionError.ProviderSettlementFailed(ManagedChatProviderLifecycleError.FactRejected _) ->
            ExecutionFailure.PersistenceFailure PersistenceCommitment.Committed,
            HookSettlementEvidence.SettlementIncomplete
        | _ -> ExecutionFailure.LocalInvariant, HookSettlementEvidence.SettlementIncomplete

    let private transactionFailure =
        function
        | ChatAdmissionTransactionError.AdmissionRejected _ ->
            ExecutionFailure.LocalInvariant, HookSettlementEvidence.NoOwnedExecution
        | ChatAdmissionTransactionError.AcceptanceFailed failure -> acceptanceFailure failure
        | ChatAdmissionTransactionError.AcceptanceBoundaryFailed _ ->
            ExecutionFailure.AcceptanceUnknown, HookSettlementEvidence.DurableOutcomeUnknown
        | ChatAdmissionTransactionError.PreProviderSettlementFailed failure -> settlementFailure failure
        | ChatAdmissionTransactionError.PreProviderSettlementBoundaryFailed _ ->
            ExecutionFailure.AcceptanceUnknown, HookSettlementEvidence.SettlementIncomplete
        | ChatAdmissionTransactionError.LeaseAcquisitionFailed _ ->
            ExecutionFailure.LocalInvariant, HookSettlementEvidence.ExactSettlementComplete
        | ChatAdmissionTransactionError.LeaseHandoffFailed(_, settlement) -> handoffFailure settlement
        | ChatAdmissionTransactionError.SupersessionSettlementFailed failure -> supersessionSettlementFailure failure
        | ChatAdmissionTransactionError.InputProjectionFailed _ ->
            ExecutionFailure.LocalInvariant, HookSettlementEvidence.ExactSettlementComplete
        | ChatAdmissionTransactionError.LeaseTargetFailed(_, release)
        | ChatAdmissionTransactionError.LeaseTargetBoundaryFailed(_, release)
        | ChatAdmissionTransactionError.LeaseTargetProjectionFailed(_, release)
        | ChatAdmissionTransactionError.HostProjectionFailed(_, release)
        | ChatAdmissionTransactionError.LeaseCommitFailed(_, release)
        | ChatAdmissionTransactionError.LeaseCommitBoundaryFailed(_, release) ->
            ExecutionFailure.LocalInvariant, releaseCompleted release

    let private stoppedTransactionFailure =
        function
        | ChatAdmissionTransactionOutcome.Superseded _ ->
            ExecutionFailure.Superseded,
            DurableExecutionLifecycle.Terminal,
            HookSettlementEvidence.ExactSettlementComplete
        | ChatAdmissionTransactionOutcome.Cancelled _ ->
            ExecutionFailure.UserCancelled,
            DurableExecutionLifecycle.Terminal,
            HookSettlementEvidence.ExactSettlementComplete
        | ChatAdmissionTransactionOutcome.CapacityQueueFull _ ->
            ExecutionFailure.CapacityQueueFull,
            DurableExecutionLifecycle.Terminal,
            HookSettlementEvidence.ExactSettlementComplete
        | ChatAdmissionTransactionOutcome.AlreadyStarted _ ->
            ExecutionFailure.Superseded, DurableExecutionLifecycle.Terminal, HookSettlementEvidence.NoOwnedExecution
        | ChatAdmissionTransactionOutcome.AlreadyTerminal _ ->
            ExecutionFailure.Superseded, DurableExecutionLifecycle.Terminal, HookSettlementEvidence.NoOwnedExecution
        | ChatAdmissionTransactionOutcome.Settled _
        | ChatAdmissionTransactionOutcome.DeferredInput _ ->
            invalidOp "settled chat admission cannot cross the failure membrane"

    let private lifecycleAfterFailedTransaction =
        function
        | HookSettlementEvidence.ExactSettlementComplete -> DurableExecutionLifecycle.Terminal
        | HookSettlementEvidence.NoOwnedExecution -> DurableExecutionLifecycle.NoAcceptedFact
        | HookSettlementEvidence.DurableOutcomeUnknown
        | HookSettlementEvidence.SettlementIncomplete -> DurableExecutionLifecycle.AcceptedBeforeProvider

    let private managedFailure =
        function
        | HostSignalBootstrap.ChatAdmissionHookFailure.IntentRejected _ ->
            ExecutionFailure.ProtocolRejection,
            DurableExecutionLifecycle.NoAcceptedFact,
            HookSettlementEvidence.NoOwnedExecution
        | HostSignalBootstrap.ChatAdmissionHookFailure.TransactionStopped outcome -> stoppedTransactionFailure outcome
        | HostSignalBootstrap.ChatAdmissionHookFailure.TransactionFailed failure ->
            let typed, settlement = transactionFailure failure
            let lifecycle = lifecycleAfterFailedTransaction settlement
            typed, lifecycle, settlement

    let private placeholderKey =
        { SessionId = SessionId.create "host-hook-without-managed-execution"
          PhysicalUserMessageId = PhysicalUserMessageId.create "host-hook-without-managed-execution" }

    let private policyDecision (outcome: HookFailureOutcome) =
        ExecutionFailurePolicy.decide
            { Failure = outcome.Failure
              Lifecycle = outcome.Lifecycle
              ExecutionKey = outcome.ExecutionKey |> Option.defaultValue placeholderKey
              Capacity = CapacityOwnership.NoCapacityFence
              Provider =
                { ProviderRun = ProviderRunIdentity.create "host-hook-before-provider"
                  LogicalRun = LogicalRunId.create "host-hook-before-provider"
                  RequestKind = ProviderRequestKind.WorkMain
                  RetryBudget = ProviderRecoveryBudget.Exhausted
                  Breaker = ProviderBreakerState.Closed } }

    let internal interpretHookFailure outcome =
        let decision = policyDecision outcome

        match decision.Resolution, decision.Breaker with
        | (ExecutionFailureResolution.PreserveCurrentFact | ExecutionFailureResolution.AwaitAcceptanceReconciliation _ | ExecutionFailureResolution.TerminalizeAcceptedPreProvider _ | ExecutionFailureResolution.TerminalizeProviderStarted _),
          BreakerDecision.NoBreakerTransition -> ()
        | ExecutionFailureResolution.RetryFreshAttempt _, _
        | _, BreakerDecision.RecordProviderTransientFailure
        | _, BreakerDecision.RecordProviderPermanentFailure ->
            invalidOp "hook membrane cannot own provider retry, fallback, or breaker transitions"

        let decisionStillRequiresSettlement =
            match decision.CapacitySettlement, decision.Resolution with
            | _, ExecutionFailureResolution.RetryFreshAttempt _ -> true
            | _, ExecutionFailureResolution.TerminalizeAcceptedPreProvider _
            | _, ExecutionFailureResolution.TerminalizeProviderStarted _ -> true
            | CapacitySettlement.ReleaseExactFence _, _ -> true
            | _, _ -> false

        match decision.Fatality, outcome.Settlement, decisionStillRequiresSettlement with
        | FatalityDecision.NoFatality, _, _ -> HookFailurePolicy.RethrowUnchanged
        | FatalityDecision.FatalAfterSettlement, _, true -> HookFailurePolicy.RejectFatalBeforeSettlement
        | FatalityDecision.FatalAfterSettlement, HookSettlementEvidence.NoOwnedExecution, false
        | FatalityDecision.FatalAfterSettlement, HookSettlementEvidence.ExactSettlementComplete, false
        | FatalityDecision.FatalAfterSettlement, HookSettlementEvidence.DurableOutcomeUnknown, false ->
            HookFailurePolicy.FatalAfterSettlement
        | FatalityDecision.FatalAfterSettlement, HookSettlementEvidence.SettlementIncomplete, false ->
            HookFailurePolicy.RejectFatalBeforeSettlement

    let internal normalizeHookFailure (args: obj) (context: obj) (error: obj) =
        let key = hookArgumentsKey args context

        match error with
        | :? JournalAppendException as persistence ->
            let failure, settlement = persistenceOutcome persistence.Failure

            { Failure = failure
              Lifecycle = lifecycleAfterFailedTransaction settlement
              ExecutionKey = key
              Settlement = settlement }
        | :? HostSignalBootstrap.ChatAdmissionHookException as managed ->
            let failure, lifecycle, settlement = managedFailure managed.Failure

            { Failure = failure
              Lifecycle = lifecycle
              ExecutionKey = managed.ExecutionKey
              Settlement = settlement }
        | _ ->
            // Unclassifiable: the hook's own args are the only honest evidence.
            // A key proves an owned execution, so its settlement is incomplete
            // until proven — never fabricated as NoAcceptedFact/NoOwnedExecution.
            { Failure = ExecutionFailure.LocalInvariant

              Lifecycle = DurableExecutionLifecycle.AcceptedBeforeProvider
              ExecutionKey = key
              Settlement = HookSettlementEvidence.SettlementIncomplete }

    let private handleHookFailure operation args context error =
        let outcome = normalizeHookFailure args context error

        match interpretHookFailure outcome with
        | HookFailurePolicy.RethrowUnchanged
        | HookFailurePolicy.RejectFatalBeforeSettlement -> ()
        | HookFailurePolicy.FatalAfterSettlement -> emitFatalRecord operation error

    [<Emit("(args, context) => { const handle = (err) => { $2($0, args, context, err); throw err; }; try { return Promise.resolve($1(args, context)).catch(handle); } catch (err) { return handle(err); } }")>]
    let private guardedPolicyAwareHook
        (operation: string)
        (fn: obj)
        (onError: string -> obj -> obj -> obj -> unit)
        : obj =
        jsNative

    let policyAwareHook (operation: string) (adaptedHook: obj) : obj =
        guardedPolicyAwareHook operation adaptedHook handleHookFailure

    let registeredHook (key: HookKey) (adaptedHook: obj) : string * obj =
        let metadata = HookPolicy.metadata key |> HookPolicy.validate
        metadata.HostKey, policyAwareHook metadata.DiagnosticOperation adaptedHook

    let projectionSessionIdFromMessages (output: obj) : string option =
        ProviderWireDecode.projectionSessionIdFromMessages output

    let toolHooks
        (toolModule: obj)
        (sessionPort: ISessionHostPort)
        (waitObserver: IWaitObserver)
        (rootWorkspace: IRootWorkspaceReader)
        (journal: AgentJournal option)
        (workspaceDirectory: string option)
        (scope: PluginRuntimeScope)
        (currentPhysicalUserMessage: string -> string option)
        (onRunStarted: (SessionId -> Role -> string option -> unit) option)
        (viewForSession: (string -> Task<string option>) option)
        (childViewForRun: SessionId -> ProviderRunIdentity -> Task<string option>)
        (snapshot: ISessionSnapshotPort option)
        (cancelSignals: (SessionId seq -> unit) option)
        (eventPort: IEventObservationPort option)
        (casebookToolSpecs: ToolSpec list)
        (continueManagerLoop: SessionId -> string -> Task<Result<unit, string>>)
        (userNotify: (string -> string -> unit) option)
        : Task<ToolRegistration> =
        task {
            let! commonDirOpt =
                match workspaceDirectory with
                | Some ws ->
                    task {
                        let! dir = RuntimePath.stateRoot ws
                        return Some dir
                    }
                | None -> Task.FromResult None

            let jsTransactionPersistence =
                commonDirOpt
                |> Option.bind WorkspaceEventStore.tryCurrent
                |> Option.map JsToolsTransactionStore.createPersistence

            let jsLifecyclePersistence =
                commonDirOpt
                |> Option.bind WorkspaceEventStore.tryCurrent
                |> Option.map JsToolsTransactionStore.createLifecyclePersistence

            let quiescence = scope.Sessions.Quiescence :> ISessionQuiescenceGate

            let registration =
                ToolRegistry.create
                    toolModule
                    sessionPort
                    waitObserver
                    rootWorkspace
                    journal
                    workspaceDirectory
                    scope.Sessions.SessionParents
                    currentPhysicalUserMessage
                    scope.Sessions.SessionDirectories
                    onRunStarted
                    viewForSession
                    snapshot
                    cancelSignals
                    (fun sessionId -> quiescence.BeginToolExecution(SessionId.create sessionId))
                    (fun sessionId -> quiescence.EndToolExecution(SessionId.create sessionId))
                    eventPort
                    scope.SyncDelegateRuntime
                    casebookToolSpecs

                    jsTransactionPersistence
                    jsLifecyclePersistence
                    continueManagerLoop
                    (Some childViewForRun)
                    userNotify

            // Process-local join admission: JoinTool RequireCurrentProcessJoin → PluginRuntimeScope.
            registration.Runtime.AttachCurrentProcessJoin(fun root -> scope.RequireCurrentProcessJoin root)
            // EXEC-017: JoinTool Begin(user-message wake) shares this process-local
            // attempt-scoped registry.
            registration.Runtime.AttachJoinAttempts scope.Sessions.JoinInterrupts
            return registration
        }
