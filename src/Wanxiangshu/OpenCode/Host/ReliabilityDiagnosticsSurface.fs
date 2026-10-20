namespace Wanxiangshu.OpenCode.Host

open System
open System.Text.RegularExpressions
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Participant.Provider.Attempt
open Wanxiangshu.Execution.Failure
open Wanxiangshu.Execution.Session.ChatExecution
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.OpenCode

module ReliabilityDiagnosticsSurface =

    type private CounterHandle(counters: ReliabilityCounters) =
        member _.Counters = counters

    [<Emit("Object.keys($0)")>]
    let private keys (value: obj) : string array = jsNative

    [<Emit("typeof $0 === 'string'")>]
    let private isString (value: obj) : bool = jsNative

    [<Emit("Number($0)")>]
    let private number (value: int64) : obj = jsNative

    [<Emit("(() => { const freeze = value => { if (value && typeof value === 'object' && !Object.isFrozen(value)) { Object.freeze(value); Object.values(value).forEach(freeze); } return value; }; return freeze($0); })()")>]
    let private deepFreeze (value: obj) : obj = jsNative

    let diagnosticsVisible () : bool =
        Environment.GetEnvironmentVariable "WANXIANGSHU_DIAG" = "1"

    [<Emit("console.error(JSON.stringify($0))")>]
    let private writeRecord (value: obj) : unit = jsNative

    let private requiredText field (value: obj) =
        if isNull value || not (isString value) then
            invalidArg field $"missing {field}"

        let text: string = unbox value

        if String.IsNullOrWhiteSpace text then
            invalidArg field $"missing {field}"

        text

    let private optionalText field (value: obj) =
        if isNull value then
            None
        else
            Some(requiredText field value)

    let private lifecycleOf field value =
        match requiredText field value with
        | "Accepted" -> DurableExecutionLifecycle.AcceptedBeforeProvider
        | "ProviderStarted" -> DurableExecutionLifecycle.ProviderStarted
        | "Terminal" -> DurableExecutionLifecycle.Terminal
        | phase -> invalidArg field $"unknown diagnostic execution phase '{phase}'"

    let private requestKindOf value =
        match requiredText "providerRequestKind" value with
        | "work-main" -> ProviderRequestKind.WorkMain
        | "interaction-repair" -> ProviderRequestKind.InteractionRepair
        | kind -> invalidArg "providerRequestKind" $"unknown provider request kind '{kind}'"


    let private failureOf commitment value =
        match requiredText "failureClass" value with
        | "LocalInvariant" -> ExecutionFailure.LocalInvariant
        | "ProtocolRejection" -> ExecutionFailure.ProtocolRejection
        | "AuthorizationDenied" -> ExecutionFailure.AuthorizationDenied
        | "UserCancelled" -> ExecutionFailure.UserCancelled
        | "Superseded" -> ExecutionFailure.Superseded
        | "CapacityQueueFull" -> ExecutionFailure.CapacityQueueFull
        | "ProviderTransient" -> ExecutionFailure.ProviderTransient
        | "ProviderPermanent" -> ExecutionFailure.ProviderPermanent
        | "AcceptanceUnknown" -> ExecutionFailure.AcceptanceUnknown
        | "StreamInterruptedAfterFirstToken" -> ExecutionFailure.StreamInterruptedAfterFirstToken
        | "PersistenceFailure" -> ExecutionFailure.PersistenceFailure commitment
        | failure -> invalidArg "failureClass" $"unknown known failure class '{failure}'"

    let private resolutionOf authorization executionKey value =
        match requiredText "resolution" value with
        | "PreserveCurrentFact" -> ExecutionFailureResolution.PreserveCurrentFact
        | "AwaitAcceptanceReconciliation" ->
            match executionKey () with
            | Some key -> ExecutionFailureResolution.AwaitAcceptanceReconciliation key
            | None -> invalidArg "resolution" "AwaitAcceptanceReconciliation requires execution key"
        | "RetryFreshAttempt" -> ExecutionFailureResolution.RetryFreshAttempt(authorization ())
        | "TerminalizeAcceptedPreProvider" ->
            match executionKey () with
            | Some key ->
                ExecutionFailureResolution.TerminalizeAcceptedPreProvider(key, ChatExecutionTerminalDisposition.Failed)
            | None -> invalidArg "resolution" "TerminalizeAcceptedPreProvider requires execution key"
        | "TerminalizeProviderStarted" ->
            match executionKey () with
            | Some key ->
                ExecutionFailureResolution.TerminalizeProviderStarted(key, ChatExecutionTerminalDisposition.Failed)
            | None -> invalidArg "resolution" "TerminalizeProviderStarted requires execution key"
        | resolution -> invalidArg "resolution" $"unknown failure resolution '{resolution}'"

    let private capacityStateOf value =
        match requiredText "capacityState" value with
        | "NotAcquired" -> DiagnosticCapacityState.NotAcquired
        | "Queued" -> DiagnosticCapacityState.Queued
        | "Active" -> DiagnosticCapacityState.Active
        | "Releasing" -> DiagnosticCapacityState.Releasing
        | "Released" -> DiagnosticCapacityState.Released
        | state -> invalidArg "capacityState" $"unknown capacity state '{state}'"

    let private recoveryOf value =
        match requiredText "recoveryDecision" value with
        | "ObserveOnly" -> DiagnosticRecoveryDecision.ObserveOnly
        | "ResumeAdmission" -> DiagnosticRecoveryDecision.ResumeAdmission
        | "ReconcileStartedProvider" -> DiagnosticRecoveryDecision.ReconcileStartedProvider
        | "MarkTerminal" -> DiagnosticRecoveryDecision.MarkTerminal
        | "FailClosed" -> DiagnosticRecoveryDecision.FailClosed
        | "ManualIntervention" -> DiagnosticRecoveryDecision.ManualIntervention
        | decision -> invalidArg "recoveryDecision" $"unknown recovery decision '{decision}'"

    let private policyClassOf value =
        match requiredText "policyClass" value with
        | "Security" -> HookCriticality.Security
        | "Workflow" -> HookCriticality.Workflow
        | "Invariant" -> HookCriticality.Invariant
        | "Degradable" -> HookCriticality.Degradable
        | "AuditOnly" -> HookCriticality.AuditOnly
        | policyClass -> invalidArg "policyClass" $"unknown hook policy class '{policyClass}'"

    let private commitmentOf value =
        match requiredText "persistenceCommitment" value with
        | "NotCommitted" -> PersistenceCommitment.NotCommitted
        | "Committed" -> PersistenceCommitment.Committed
        | "Unknown" -> PersistenceCommitment.Unknown
        | commitment -> invalidArg "persistenceCommitment" $"unknown persistence commitment '{commitment}'"

    let private recordOf (value: obj) =
        if isNull value then
            invalidArg "record" "missing causal diagnostic record"

        let allowed =
            set
                [ "operation"
                  "logicalRunId"
                  "sessionId"
                  "authorityRootUserMessageId"
                  "physicalUserMessageId"
                  "promptKey"
                  "providerRunIdentity"
                  "participant"
                  "role"
                  "providerRequestKind"
                  "transition"
                  "failureClass"
                  "resolution"
                  "capacityState"
                  "recoveryDecision"
                  "capacityFence"
                  "hook"
                  "policyClass"
                  "persistenceCommitment" ]

        keys value
        |> Array.tryFind (fun field -> not (Set.contains field allowed))
        |> Option.iter (fun field -> invalidArg field $"unknown causal diagnostic field '{field}'")

        let operation = requiredText "operation" value?operation

        let sessionIdOpt =
            optionalText "sessionId" value?sessionId |> Option.map SessionId.create

        let physicalUserMessageIdOpt =
            optionalText "physicalUserMessageId" value?physicalUserMessageId
            |> Option.map PhysicalUserMessageId.create

        let executionKey () : ChatExecutionKey option =
            match sessionIdOpt, physicalUserMessageIdOpt with
            | Some sid, Some pid ->
                Some
                    { SessionId = sid
                      PhysicalUserMessageId = pid }
            | _ -> None

        if not (ReliabilityDiagnostics.validateOperation operation) then
            invalidArg "operation" "operation must be one non-empty line"

        let transition: obj = value?transition

        if isNull transition then
            invalidArg "transition" "missing transition"

        keys transition
        |> Array.tryFind (fun field -> field <> "from" && field <> "to")
        |> Option.iter (fun field -> invalidArg field $"unknown state transition field '{field}'")

        let toState = lifecycleOf "transition.to" transition?``to``

        let logicalRunId =
            optionalText "logicalRunId" value?logicalRunId |> Option.map LogicalRunId.create

        let providerRunIdentity =
            optionalText "providerRunIdentity" value?providerRunIdentity
            |> Option.map ProviderRunIdentity.create

        let providerRequestKind =
            if isNull value?providerRequestKind then
                None
            else
                Some(requestKindOf value?providerRequestKind)

        let persistenceCommitment =
            if isNull value?persistenceCommitment then
                None
            else
                Some(commitmentOf value?persistenceCommitment)

        let authorization () =
            match logicalRunId, providerRunIdentity, providerRequestKind with
            | Some logicalRun, Some providerRun, Some requestKind ->
                ProviderRecoveryAuthorization.Create(
                    ProviderRecoveryDecisionId.Create("diagnostic-observation:" + operation),
                    logicalRun,
                    providerRun,
                    requestKind
                )
            | _ ->
                invalidArg
                    "resolution"
                    "provider recovery decision requires logicalRunId, providerRunIdentity, and providerRequestKind"

        { Operation = operation
          LogicalRunId = logicalRunId
          SessionId = optionalText "sessionId" value?sessionId |> Option.map SessionId.create
          AuthorityRootUserMessageId =
            optionalText "authorityRootUserMessageId" value?authorityRootUserMessageId
            |> Option.map AuthorityRootUserMessageId.create
          PhysicalUserMessageId =
            optionalText "physicalUserMessageId" value?physicalUserMessageId
            |> Option.map PhysicalUserMessageId.create
          PromptKey = optionalText "promptKey" value?promptKey |> Option.map PromptKey.create
          ProviderRunIdentity = providerRunIdentity
          Participant = optionalText "participant" value?participant
          Role =
            optionalText "role" value?role
            |> Option.map (fun role ->
                Roles.tryParseRole role
                |> Option.defaultWith (fun () -> invalidArg "role" $"unknown canonical role '{role}'"))
          ProviderRequestKind = providerRequestKind
          Transition =
            { From =
                if isNull transition?from then
                    None
                else
                    Some(lifecycleOf "transition.from" transition?from)
              To = toState }
          FailureClass =
            if isNull value?failureClass then
                None
            else
                Some(
                    failureOf
                        (persistenceCommitment |> Option.defaultValue PersistenceCommitment.Unknown)
                        value?failureClass
                )
          Resolution =
            if isNull value?resolution then
                None
            else
                Some(resolutionOf authorization executionKey value?resolution)
          CapacityState =
            if isNull value?capacityState then
                None
            else
                Some(capacityStateOf value?capacityState)
          CapacityFence = optionalText "capacityFence" value?capacityFence
          Hook = optionalText "hook" value?hook
          PolicyClass =
            if isNull value?policyClass then
                None
            else
                Some(policyClassOf value?policyClass)
          RecoveryDecision =
            if isNull value?recoveryDecision then
                None
            else
                Some(recoveryOf value?recoveryDecision)
          PersistenceCommitment = persistenceCommitment }

    let redactText (value: string) =
        let oneLine = Regex.Replace(value, "[\\r\\n].*$", "")

        oneLine
        |> fun text -> Regex.Replace(text, "\\bBearer\\s+[^\\s]+", "Bearer [REDACTED]", RegexOptions.IgnoreCase)
        |> fun text ->
            Regex.Replace(
                text,
                "\\b(api[_-]?key|token|password|cookie|credential)\\s*[:=]\\s*[^\\s,;]+",
                "$1=[REDACTED]",
                RegexOptions.IgnoreCase
            )
        |> fun text -> Regex.Replace(text, "(?:[A-Za-z]:\\\\|/)(?:[^\\s/\\\\]+[/\\\\])+[^\\s,;]+", "[REDACTED]")

    let private optionObject map value =
        value |> Option.map (map >> box) |> Option.defaultValue null

    let private failureLabel =
        function
        | ExecutionFailure.PersistenceFailure _ -> "PersistenceFailure"
        | failure -> string failure

    let private resolutionLabel =
        function
        | ExecutionFailureResolution.PreserveCurrentFact -> "PreserveCurrentFact"
        | ExecutionFailureResolution.AwaitAcceptanceReconciliation _ -> "AwaitAcceptanceReconciliation"
        | ExecutionFailureResolution.RetryFreshAttempt _ -> "RetryFreshAttempt"
        | ExecutionFailureResolution.TerminalizeAcceptedPreProvider _ -> "TerminalizeAcceptedPreProvider"
        | ExecutionFailureResolution.TerminalizeProviderStarted _ -> "TerminalizeProviderStarted"

    let private lifecycleLabel =
        function
        | DurableExecutionLifecycle.NoAcceptedFact -> "NoAcceptedFact"
        | DurableExecutionLifecycle.AcceptedBeforeProvider -> "Accepted"
        | DurableExecutionLifecycle.ProviderStarted -> "ProviderStarted"
        | DurableExecutionLifecycle.Terminal -> "Terminal"

    let projectTyped (record: CausalDiagnosticRecord) : obj =
        box
            {| operation = record.Operation
               logicalRunId = optionObject LogicalRunId.value record.LogicalRunId
               sessionId = optionObject SessionId.value record.SessionId
               authorityRootUserMessageId =
                optionObject AuthorityRootUserMessageId.value record.AuthorityRootUserMessageId
               physicalUserMessageId = optionObject PhysicalUserMessageId.value record.PhysicalUserMessageId
               promptKey = optionObject PromptKey.value record.PromptKey
               providerRunIdentity = optionObject ProviderRunIdentity.value record.ProviderRunIdentity
               participant = optionObject redactText record.Participant
               role = optionObject Roles.roleLabel record.Role
               providerRequestKind = optionObject ProviderRequestKind.label record.ProviderRequestKind
               transition =
                {| ``from`` = optionObject lifecycleLabel record.Transition.From
                   ``to`` = lifecycleLabel record.Transition.To |}
               failureClass = optionObject failureLabel record.FailureClass
               resolution = optionObject resolutionLabel record.Resolution
               capacityState = optionObject string record.CapacityState
               capacityFence = optionObject redactText record.CapacityFence
               hook = optionObject redactText record.Hook
               policyClass = optionObject string record.PolicyClass
               recoveryDecision = optionObject string record.RecoveryDecision
               persistenceCommitment = optionObject string record.PersistenceCommitment |}

    let private view (record: CausalDiagnosticRecord) = record |> projectTyped |> deepFreeze

    let projectRecord (value: obj) : obj = value |> recordOf |> view

    let tryEmit (value: obj) : bool =
        try
            let projected = projectRecord value

            if diagnosticsVisible () then
                writeRecord projected

            true
        with _ ->
            false

    let emitKnownFailure (value: obj) : unit =
        try
            let record = recordOf value

            if record.FailureClass.IsSome && diagnosticsVisible () then
                record |> view |> writeRecord
        with _ ->
            ()

    let private countersOf (handle: obj) =
        match handle with
        | :? CounterHandle as typed -> typed.Counters
        | _ -> invalidArg "handle" "unknown reliability counter handle"

    let createCounters () : obj =
        CounterHandle(ReliabilityCounters()) :> obj

    let recordObservation (handle: obj) (observation: string) : unit =
        let observed =
            match observation with
            | "IdentityConflict" -> ReliabilityObservation.IdentityConflict
            | "QueueFull" -> ReliabilityObservation.QueueFull
            | "FatalSettlement" -> ReliabilityObservation.FatalSettlement
            | "RecoveryObserveOnly" -> ReliabilityObservation.Recovery DiagnosticRecoveryDecision.ObserveOnly
            | "RecoveryResumeAdmission" -> ReliabilityObservation.Recovery DiagnosticRecoveryDecision.ResumeAdmission
            | "RecoveryReconcileStartedProvider" ->
                ReliabilityObservation.Recovery DiagnosticRecoveryDecision.ReconcileStartedProvider
            | "RecoveryMarkTerminal" -> ReliabilityObservation.Recovery DiagnosticRecoveryDecision.MarkTerminal
            | "RecoveryFailClosed" -> ReliabilityObservation.Recovery DiagnosticRecoveryDecision.FailClosed
            | "RecoveryManualIntervention" ->
                ReliabilityObservation.Recovery DiagnosticRecoveryDecision.ManualIntervention
            | "HookFailure" -> ReliabilityObservation.HookFailure
            | "FallbackAdvanced" -> ReliabilityObservation.FallbackAdvanced
            | "StreamAbort" -> ReliabilityObservation.StreamAbort
            | unknown -> invalidArg "observation" $"unknown reliability observation '{unknown}'"

        (countersOf handle).Record observed

    let private counterView (snapshot: ReliabilityCounterSnapshot) =
        box
            {| identityConflicts = number snapshot.IdentityConflicts
               queueFull = number snapshot.QueueFull
               fatalSettlements = number snapshot.FatalSettlements
               recoveryObserveOnly = number snapshot.RecoveryObserveOnly
               recoveryResumeAdmission = number snapshot.RecoveryResumeAdmission
               recoveryReconcileStartedProvider = number snapshot.RecoveryReconcileStartedProvider
               recoveryMarkTerminal = number snapshot.RecoveryMarkTerminal
               recoveryFailClosed = number snapshot.RecoveryFailClosed
               recoveryManualIntervention = number snapshot.RecoveryManualIntervention
               hookFailures = number snapshot.HookFailures
               fallbackAdvances = number snapshot.FallbackAdvances
               streamAborts = number snapshot.StreamAborts |}
        |> deepFreeze

    let snapshot (handle: obj) : obj =
        (countersOf handle).Snapshot() |> counterView

    let private executionSourceOf (value: obj) =
        if isNull value?identity then
            invalidArg "executions.identity" "missing canonical execution identity"

        { LogicalRunId =
            LogicalRunId.create (requiredText "executions.identity.logicalRunId" value?identity?logicalRunId)
          Lifecycle = lifecycleOf "executions.phase" value?phase }

    let queryReliability (handle: obj) (executions: obj array) (capacity: obj) (recovery: obj) : obj =
        let capacitySource =
            { QueueDepth = (unbox<obj array> capacity?waiters).Length
              ActiveLeases = unbox<int> capacity?activeCount
              DuplicateFences = unbox<int64> capacity?counters?duplicate
              StaleFences = unbox<int64> capacity?counters?stale
              ConflictingFences = unbox<int64> capacity?counters?conflict }

        let recoverySource =
            { Pending =
                (unbox<obj array> recovery?resumes).Length
                + (unbox<obj array> recovery?requeues).Length
              ManualInterventionCount = (unbox<obj array> recovery?manualInterventions).Length }

        let result =
            ReliabilityDiagnostics.querySources
                (countersOf handle)
                (executions |> Array.map executionSourceOf)
                capacitySource
                recoverySource

        box
            {| counters = counterView result.Counters
               execution =
                {| acceptedWithoutTerminal = result.Execution.AcceptedWithoutTerminal
                   providerStartedWithoutTerminal = result.Execution.ProviderStartedWithoutTerminal
                   physicalAttemptsByLogicalRun =
                    result.Execution.PhysicalAttemptsByLogicalRun
                    |> Array.map (fun attempt ->
                        box
                            {| logicalRunId = LogicalRunId.value attempt.LogicalRunId
                               physicalAttempts = attempt.PhysicalAttempts |}) |}
               capacity =
                {| queueDepth = result.Capacity.QueueDepth
                   activeLeases = result.Capacity.ActiveLeases
                   duplicateFences = number result.Capacity.DuplicateFences
                   staleFences = number result.Capacity.StaleFences
                   conflictingFences = number result.Capacity.ConflictingFences |}
               recovery =
                {| pending = result.Recovery.Pending
                   manualInterventionCount = result.Recovery.ManualInterventionCount |} |}
        |> deepFreeze
