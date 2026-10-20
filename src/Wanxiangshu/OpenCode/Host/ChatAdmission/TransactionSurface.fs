namespace Wanxiangshu.OpenCode

open Wanxiangshu.Persistence.Journal.JournalOutcome
open System
open System.Threading.Tasks
open Fable.Core.JsInterop
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Composition.Durable.Fact
open Wanxiangshu.Execution.Session.ChatExecution
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Interaction.Attempt
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Participant.Persona
open Wanxiangshu.Participant.Provider.Attempt
open Wanxiangshu.Persistence.Journal

module TransactionSurface =

    let private requiredText fieldName (value: obj) =
        if isNull value then
            invalidArg fieldName $"missing {fieldName}"

        let parsed: string = unbox value

        if String.IsNullOrWhiteSpace parsed then
            invalidArg fieldName $"missing {fieldName}"

        parsed

    let private profileOf (value: obj) : AttemptExecutionProfile =
        let sessionId = SessionId.create (requiredText "sessionId" value?sessionId)

        let physicalId =
            PhysicalUserMessageId.create (requiredText "physicalUserMessageId" value?physicalUserMessageId)

        let logicalRunId =
            LogicalRunId.create (requiredText "logicalRunId" value?logicalRunId)

        let authorityRoot =
            AuthorityRootUserMessageId.create (
                requiredText "authorityRootUserMessageId" value?authorityRootUserMessageId
            )

        let selectedAgent =
            requiredText
                "identitySeed.participantIdentity.selectedAgent"
                value?identitySeed?participantIdentity?selectedAgent

        let identity =
            ParticipantIdentity.resolveAtRoot selectedAgent
            |> Result.defaultWith (fun error -> invalidArg "selectedAgent" $"{error}")

        let authority =
            PromptAuthority.createAuthorityExecutionProfile
                sessionId
                logicalRunId
                authorityRoot
                PromptAuthority.RootAuthorityKind.HumanRoot
                identity
            |> Result.defaultWith (fun error -> invalidArg "authority" error)

        InteractionAttempt.buildAttemptExecutionProfile
            authority
            physicalId
            (ProviderRunIdentity.create (requiredText "providerRun" value?providerRun))
            (PromptAuthority.PromptOrigin.AuthorityRoot PromptAuthority.RootAuthorityKind.HumanRoot)
            ProviderRequestKind.WorkMain

    let private initialState
        (stateLabel: string)
        (evidence: AcceptedChatExecutionEvidence)
        : ChatExecutionState option =
        let started =
            { Accepted = evidence
              ProviderRun = ProviderRunIdentity.create "provider-transaction"
              RequestKind = ProviderRequestKind.WorkMain }

        match stateLabel with
        | "None" -> None
        | "Accepted" -> Some(ChatExecutionState.Accepted evidence)
        | "ProviderStarted" -> Some(ChatExecutionState.Started started)
        | "Terminal" -> Some(ChatExecutionState.EndedAfterStart(started, ChatExecutionTerminalDisposition.Completed))
        | value -> invalidArg "state" $"unknown transaction state '{value}'"

    let private stepLabel =
        function
        | ChatAdmissionTransactionStep.ResolveState -> "ResolveState"
        | ChatAdmissionTransactionStep.Accept -> "Accept"
        | ChatAdmissionTransactionStep.AcceptedWitness -> "AcceptedWitness"
        | ChatAdmissionTransactionStep.AcquireLease -> "AcquireLease"
        | ChatAdmissionTransactionStep.LeaseTarget -> "LeaseTarget"
        | ChatAdmissionTransactionStep.ProjectHost -> "ProjectHost"
        | ChatAdmissionTransactionStep.CommitLease -> "CommitLease"
        | ChatAdmissionTransactionStep.TerminalizeAccepted -> "TerminalizeAccepted"
        | ChatAdmissionTransactionStep.ReleaseBeforeProvider -> "ReleaseBeforeProvider"
        | ChatAdmissionTransactionStep.Settled -> "Settled"

    let private acceptanceErrorKind =
        function
        | ManagedChatAcceptanceError.IntentRejected _ -> "IntentRejected"
        | ManagedChatAcceptanceError.AuthorityRegistrationRejected _ -> "AuthorityRegistrationRejected"
        | ManagedChatAcceptanceError.NotAttempted _ -> "NotAttempted"
        | ManagedChatAcceptanceError.CommitUnknown _ -> "CommitUnknown"
        | ManagedChatAcceptanceError.NoNewWriteReleaseFailed _ -> "NoNewWriteReleaseFailed"
        | ManagedChatAcceptanceError.AttemptEvidenceInvalid _ -> "AttemptEvidenceInvalid"
        | ManagedChatAcceptanceError.AttemptKeyMismatch _ -> "AttemptKeyMismatch"
        | ManagedChatAcceptanceError.EstablishedEvidenceConflict _ -> "EstablishedEvidenceConflict"
        | ManagedChatAcceptanceError.ProjectionMissingAfterCommit _ -> "ProjectionMissingAfterCommit"
        | ManagedChatAcceptanceError.ProjectionConflictAfterCommit _ -> "ProjectionConflictAfterCommit"
        | ManagedChatAcceptanceError.FactRejected _ -> "FactRejected"

    let private releaseLabel =
        function
        | ChatAdmissionReleaseOutcome.Settled CapacityTransitionOutcome.Applied -> "Applied"
        | ChatAdmissionReleaseOutcome.Settled CapacityTransitionOutcome.AlreadyApplied -> "AlreadyApplied"
        | ChatAdmissionReleaseOutcome.Settled CapacityTransitionOutcome.StaleFence -> "StaleFence"
        | ChatAdmissionReleaseOutcome.Settled CapacityTransitionOutcome.Conflict -> "Conflict"
        | ChatAdmissionReleaseOutcome.BoundaryFailed _ -> "BoundaryFailed"

    let private errorToJs =
        function
        | ChatAdmissionTransactionError.AdmissionRejected _ -> box {| kind = "AdmissionRejected" |}
        | ChatAdmissionTransactionError.AcceptanceFailed error -> box {| kind = acceptanceErrorKind error |}
        | ChatAdmissionTransactionError.AcceptanceBoundaryFailed _ -> box {| kind = "AcceptanceBoundaryFailed" |}
        | ChatAdmissionTransactionError.PreProviderSettlementFailed _ -> box {| kind = "PreProviderSettlementFailed" |}
        | ChatAdmissionTransactionError.PreProviderSettlementBoundaryFailed _ ->
            box {| kind = "PreProviderSettlementBoundaryFailed" |}
        | ChatAdmissionTransactionError.LeaseAcquisitionFailed _ -> box {| kind = "LeaseAcquisitionFailed" |}
        | ChatAdmissionTransactionError.SupersessionSettlementFailed error ->
            box
                {| kind = "SupersessionSettlementFailed"
                   error = string error |}
        | ChatAdmissionTransactionError.LeaseHandoffFailed(cause, settlement) ->
            match settlement with
            | ChatAdmissionHandoffSettlement.TerminalCommitted release ->
                box
                    {| kind = "LeaseHandoffFailed"
                       cause = cause.Message
                       settlement = "TerminalCommitted"
                       release = releaseLabel release |}
            | ChatAdmissionHandoffSettlement.SettlementIncomplete error ->
                box
                    {| kind = "LeaseHandoffFailed"
                       cause = cause.Message
                       settlement = "SettlementIncomplete"
                       error = string error |}
            | ChatAdmissionHandoffSettlement.SettlementBoundaryFailed error ->
                box
                    {| kind = "LeaseHandoffFailed"
                       cause = cause.Message
                       settlement = "SettlementBoundaryFailed"
                       error = error.Message |}
        | ChatAdmissionTransactionError.LeaseTargetFailed(_, release) ->
            box
                {| kind = "LeaseTargetFailed"
                   release = releaseLabel release |}
        | ChatAdmissionTransactionError.LeaseTargetBoundaryFailed(_, release) ->
            box
                {| kind = "LeaseTargetBoundaryFailed"
                   release = releaseLabel release |}
        | ChatAdmissionTransactionError.LeaseTargetProjectionFailed(_, release) ->
            box
                {| kind = "LeaseTargetProjectionFailed"
                   release = releaseLabel release |}
        | ChatAdmissionTransactionError.HostProjectionFailed(_, release) ->
            box
                {| kind = "HostProjectionFailed"
                   release = releaseLabel release |}
        | ChatAdmissionTransactionError.InputProjectionFailed _ -> box {| kind = "InputProjectionFailed" |}
        | ChatAdmissionTransactionError.LeaseCommitFailed(_, release) ->
            box
                {| kind = "LeaseCommitFailed"
                   release = releaseLabel release |}
        | ChatAdmissionTransactionError.LeaseCommitBoundaryFailed(_, release) ->
            box
                {| kind = "LeaseCommitBoundaryFailed"
                   release = releaseLabel release |}

    let private outcomeLabel =
        function
        | ChatAdmissionTransactionOutcome.Settled _ -> "Settled"
        | ChatAdmissionTransactionOutcome.DeferredInput _ -> "DeferredInput"
        | ChatAdmissionTransactionOutcome.Superseded _ -> "Superseded"
        | ChatAdmissionTransactionOutcome.CapacityQueueFull _ -> "CapacityQueueFull"
        | ChatAdmissionTransactionOutcome.Cancelled _ -> "Cancelled"
        | ChatAdmissionTransactionOutcome.AlreadyStarted _ -> "AlreadyStarted"
        | ChatAdmissionTransactionOutcome.AlreadyTerminal _ -> "AlreadyTerminal"

    let transactionScenario (evidenceValue: obj) (failurePoint: string) (stateLabel: string) : Task<obj> =
        task {
            let profile: AttemptExecutionProfile = profileOf evidenceValue

            let evidence: AcceptedChatExecutionEvidence =
                ManagedChatAcceptance.evidenceFromIntent profile.Authority profile.PhysicalUserMessageId profile.Origin
            // DSL-MUTABLE: algorithm-scratch
            let mutable state: ChatExecutionState option = initialState stateLabel evidence
            let trace = ResizeArray<string>()
            // DSL-MUTABLE: algorithm-scratch
            let mutable acceptCount = 0
            let mutable appendCount = 0
            // DSL-MUTABLE: algorithm-scratch
            let mutable acquireCount = 0
            // DSL-MUTABLE: algorithm-scratch
            let mutable hostCount = 0
            let mutable commitCount = 0
            // DSL-MUTABLE: algorithm-scratch
            let mutable releaseCount = 0
            // DSL-MUTABLE: algorithm-scratch
            let mutable activeCapacity = 0
            // DSL-MUTABLE: algorithm-scratch
            let mutable hostProjected = false
            let mutable crashed = false

            let persistence: ManagedChatAcceptancePersistence =
                { ReadExact = fun _ -> state
                  AppendAccepted =
                    fun key acceptedEvidence ->
                        task {
                            appendCount <- appendCount + 1

                            match failurePoint with
                            | "CrashA" ->
                                crashed <- true
                                return raise (InvalidOperationException "crash A before Accepted append")
                            | "AcceptNotAttempted" ->
                                return
                                    Error(
                                        JournalAppendFailure.WriterUnavailable(
                                            EventId.create "transaction-not-attempted",
                                            JournalUnavailable.WriterDisposed
                                        )
                                    )
                            | "AcceptCommitUnknown" ->
                                return
                                    Error(
                                        JournalAppendFailure.WriteUnknown(
                                            EventId.create "transaction-commit-unknown",
                                            JournalFailure.WriteFailed "recording surface"
                                        )
                                    )
                            | _ ->
                                state <- Some(ChatExecutionState.Accepted acceptedEvidence)

                                return Ok()
                        } }

            let target =
                { Model = "openai/gpt-5"
                  Reasoning = "high" }

            let exactIdentity: ExecutionAdmissionExactIdentity =
                { SessionId = SessionId.value evidence.SessionId
                  PhysicalUserMessageId = PhysicalUserMessageId.value evidence.PhysicalUserMessageId
                  Role = AcceptedChatExecutionEvidence.canonicalRole evidence
                  Participant = AcceptedChatExecutionEvidence.participant evidence
                  Target = target }

            let lease =
                ExecutionAdmissionLease.Create(
                    obj (),
                    CapacityCreditId.first,
                    CapacityLeaseId.first,
                    CapacityFence.first,
                    exactIdentity
                )

            let ports: ChatAdmissionTransactionPorts =
                { Accept =
                    fun _ ->
                        task {
                            acceptCount <- acceptCount + 1

                            let key =
                                { SessionId = evidence.SessionId
                                  PhysicalUserMessageId = evidence.PhysicalUserMessageId }

                            return! ManagedChatAcceptance.acceptWith persistence key evidence
                        }
                  Acquire =
                    fun witness ->
                        task {
                            acquireCount <- acquireCount + 1

                            let witnessEvidence = ManagedChatAcceptanceWitness.evidence witness

                            let _lenderSessionId: string option =
                                PromptAuthority.identitySeedOwner witnessEvidence.IdentitySeed
                                |> Option.map (fun (ownerSession, _, _) -> SessionId.value ownerSession)

                            if failurePoint = "AcquireLease" then
                                return Error(InvalidOperationException "injected acquisition failure")
                            elif failurePoint = "AcquireSuperseded" then
                                return Ok ExecutionAdmissionAcquisition.Superseded
                            elif failurePoint = "AcquireQueueFull" then
                                return Ok ExecutionAdmissionAcquisition.QueueFull
                            elif failurePoint = "AcquireCancelled" then
                                return Ok ExecutionAdmissionAcquisition.Cancelled
                            else
                                activeCapacity <- 1

                                if failurePoint.StartsWith("Handoff", StringComparison.Ordinal) then
                                    return
                                        raise (
                                            ChatAdmissionLeaseHandoffException(
                                                InvalidOperationException "superseded settlement failed",
                                                ExecutionAdmissionAcquisition.Admitted lease
                                            )
                                        )
                                else
                                    return Ok(ExecutionAdmissionAcquisition.Admitted lease)
                        }
                  LeaseTarget =
                    fun _ ->
                        if failurePoint = "LeaseTarget" then
                            Error ExecutionAdmissionRejection.WrongTarget
                        else
                            Ok target

                  ProjectHost =
                    fun _ ->
                        hostCount <- hostCount + 1

                        if
                            failurePoint = "ProjectHost"
                            || failurePoint = "BindExecution"
                            || failurePoint = "ReleaseBeforeProvider"
                        then
                            Error(InvalidOperationException "injected Host projection failure")
                        else
                            hostProjected <- true
                            Ok()
                  Commit =
                    fun _ _ ->
                        commitCount <- commitCount + 1

                        if failurePoint = "CommitLease" then
                            CapacityTransitionOutcome.StaleFence
                        else
                            CapacityTransitionOutcome.Applied
                  ReleaseBeforeProvider =
                    fun _ ->
                        releaseCount <- releaseCount + 1

                        if failurePoint = "ReleaseBeforeProvider" then
                            raise (InvalidOperationException "injected release failure")
                        else
                            activeCapacity <- 0
                            CapacityTransitionOutcome.Applied
                  SettlePreProvider =
                    fun key acceptedEvidence disposition ->
                        let settlementPersistence: PreProviderSettlementPersistence =
                            { ReadExact = fun _ -> state
                              AppendTerminal =
                                fun _ _ _ ->
                                    task {
                                        if failurePoint = "HandoffTerminalUnknown" then
                                            return
                                                Error(
                                                    JournalAppendFailure.WriteUnknown(
                                                        EventId.create "handoff-terminal-unknown",
                                                        JournalFailure.FlushFailed "terminal flush failed"
                                                    )
                                                )
                                        elif failurePoint = "HandoffTerminalUnavailable" then
                                            return
                                                Error(
                                                    JournalAppendFailure.WriterUnavailable(
                                                        EventId.create "handoff-terminal-unavailable",
                                                        JournalUnavailable.WriterClosing
                                                    )
                                                )
                                        else
                                            match
                                                state
                                                |> Option.map (fun current ->
                                                    ChatExecutionFactFold.applyTerminal
                                                        key
                                                        (ChatExecutionTerminalEvidence.PreProvider acceptedEvidence)
                                                        disposition
                                                        { ByKey = Map.ofList [ key, current ] })
                                            with
                                            | Some(Ok updated) ->
                                                state <- ChatExecutionProjection.byKey key updated
                                                return Ok()
                                            | Some(Error rejection) ->
                                                return
                                                    Error(
                                                        JournalAppendFailure.FactRejected(
                                                            EventId.create "transaction-terminal-rejected",
                                                            rejection
                                                        )
                                                    )
                                            | None ->
                                                return
                                                    Error(
                                                        JournalAppendFailure.WriterUnavailable(
                                                            EventId.create "transaction-terminal-missing",
                                                            JournalUnavailable.WriterDisposed
                                                        )
                                                    )
                                    } }

                        PreProviderSettlement.settleWith settlementPersistence key acceptedEvidence disposition
                  ReadExact = fun _ -> state }

            let key: ChatExecutionKey =
                { SessionId = evidence.SessionId
                  PhysicalUserMessageId = evidence.PhysicalUserMessageId }

            let managed =
                ChatAdmissionIntent.ManagedIntent.ExternalRoot
                    { Key = key
                      ExplicitAgent = ParticipantIdentity.selectedAgent profile.Authority.ParticipantIdentity
                      Origin = profile.Origin
                      IdentitySeed = profile.Authority.IdentitySeed }

            let observe step =
                trace.Add(stepLabel step)

                match failurePoint, step with
                | "CrashB", ChatAdmissionTransactionStep.AcquireLease
                | "CrashC", ChatAdmissionTransactionStep.LeaseTarget
                | "CrashD", ChatAdmissionTransactionStep.ProjectHost ->
                    crashed <- true
                    raise (InvalidOperationException $"crash {failurePoint}")
                | _ -> ()

            let! result =
                task {
                    try
                        return! ChatAdmissionTransaction.executeWith observe ports managed
                    with error ->
                        return Error(ChatAdmissionTransactionError.AcceptanceBoundaryFailed error)
                }

            if failurePoint = "CrashE" then
                crashed <- true

            let outcome, targetValue, error =
                match result with
                | Ok(ChatAdmissionTransactionOutcome.Settled _) ->
                    "Settled",
                    box
                        {| model = target.Model
                           reasoning = target.Reasoning |},
                    null
                | Ok value -> outcomeLabel value, null, null
                | Error transactionError -> null, null, errorToJs transactionError

            return
                box
                    {| ok = Result.isOk result
                       outcome = outcome
                       target = targetValue
                       error = error
                       trace = trace.ToArray()
                       acceptCount = acceptCount
                       appendCount = appendCount
                       acquireCount = acquireCount
                       hostCount = hostCount
                       commitCount = commitCount
                       releaseCount = releaseCount
                       providerCount = 0
                       crashed = crashed
                       durableLifecycle = state |> Option.map _.lifecycleName |> Option.defaultValue "None"
                       terminalDisposition =
                        state |> Option.bind _.terminalDisposition |> Option.map string |> Option.toObj
                       admission =
                        {| activeCapacity = activeCapacity

                           hostProjected = hostProjected |} |}
        }

    let preProviderSettlementScenario (evidenceValue: obj) (failureKind: string) (releaseMode: string) : Task<obj> =
        task {
            let profile = profileOf evidenceValue

            let acceptedEvidence: AcceptedChatExecutionEvidence =
                ManagedChatAcceptance.evidenceFromIntent profile.Authority profile.PhysicalUserMessageId profile.Origin

            let key: ChatExecutionKey =
                { SessionId = acceptedEvidence.SessionId
                  PhysicalUserMessageId = acceptedEvidence.PhysicalUserMessageId }

            let facts = ResizeArray<ChatExecutionFactCases>()
            // DSL-MUTABLE: algorithm-scratch
            let mutable projection = ChatExecutionProjection.empty
            let mutable activeCapacity = 0
            // DSL-MUTABLE: algorithm-scratch
            let mutable providerEffectCount = 0
            let trace = ResizeArray<string>()

            let apply fact =
                match ChatExecutionFactFold.fold projection fact with
                | Ok updated ->
                    projection <- updated
                    facts.Add fact
                    Ok()
                | Error rejection ->
                    Error(
                        JournalAppendFailure.FactRejected(EventId.create "pre-provider-settlement-rejected", rejection)
                    )

            let acceptancePersistence: ManagedChatAcceptancePersistence =
                { ReadExact = fun requested -> ChatExecutionProjection.byKey requested projection
                  AppendAccepted =
                    fun requested evidence ->
                        ChatExecutionFactCases.Accepted
                            {| SchemaVersion = 1
                               Key = requested
                               Evidence = evidence |}
                        |> apply
                        |> Task.FromResult }

            let settlementPersistence: PreProviderSettlementPersistence =
                { ReadExact = fun requested -> ChatExecutionProjection.byKey requested projection
                  AppendTerminal =
                    fun requested evidence disposition ->
                        ChatExecutionFactCases.Terminal
                            {| SchemaVersion = 1
                               Key = requested
                               Evidence = ChatExecutionTerminalEvidence.PreProvider evidence
                               Disposition = disposition |}
                        |> apply
                        |> Task.FromResult }

            let rawMembraneInput =
                not (isNull evidenceValue?effectiveAgent)
                && string evidenceValue?effectiveAgent = "AGENT-028"

            let acceptanceConflict =
                failureKind = "IdentityConflict"
                || failureKind = "PluginReplay"
                || rawMembraneInput

            if acceptanceConflict then
                ChatExecutionFactCases.Accepted
                    {| SchemaVersion = 1
                       Key = key
                       Evidence = acceptedEvidence |}
                |> apply
                |> Result.defaultWith (fun error -> invalidOp (JournalAppendFailure.describe error))

            let attemptedEvidence: AcceptedChatExecutionEvidence =
                if acceptanceConflict then
                    { acceptedEvidence with
                        LogicalRunId = LogicalRunId.create "run-conflict" }
                else
                    acceptedEvidence

            let accept _ =
                ManagedChatAcceptance.acceptWith acceptancePersistence key attemptedEvidence

            let target =
                { Model = "openai/gpt-5"
                  Reasoning = "high" }

            let exactIdentity: ExecutionAdmissionExactIdentity =
                { SessionId = SessionId.value key.SessionId
                  PhysicalUserMessageId = PhysicalUserMessageId.value key.PhysicalUserMessageId
                  Role = AcceptedChatExecutionEvidence.canonicalRole acceptedEvidence
                  Participant = AcceptedChatExecutionEvidence.participant acceptedEvidence
                  Target = target }

            let lease =
                ExecutionAdmissionLease.Create(
                    obj (),
                    CapacityCreditId.first,
                    CapacityLeaseId.first,
                    CapacityFence.first,
                    exactIdentity
                )

            let managed: ChatAdmissionIntent.ManagedIntent =
                if failureKind = "PluginReplay" then
                    let promptKey = PromptKey.create "prompt-plugin-replay"

                    ChatAdmissionIntent.ManagedIntent.PendingPrompt
                        { Key = key
                          PromptKey = promptKey
                          Claim =
                            { PromptKey = promptKey
                              SessionId = key.SessionId
                              Origin = acceptedEvidence.Origin
                              LogicalRunId = Some acceptedEvidence.LogicalRunId
                              AuthorityRootUserMessageId = Some acceptedEvidence.AuthorityRootUserMessageId
                              IdentitySeed = acceptedEvidence.IdentitySeed
                              PayloadDigest = "prompt-plugin-replay"
                              Receipt = None
                              ClaimedAtRuntimeStartCount = 0 }
                          Origin = acceptedEvidence.Origin
                          IdentitySeed = acceptedEvidence.IdentitySeed }
                else
                    ChatAdmissionIntent.ManagedIntent.ExternalRoot
                        { Key = key
                          ExplicitAgent = AcceptedChatExecutionEvidence.participant acceptedEvidence
                          Origin = acceptedEvidence.Origin
                          IdentitySeed = acceptedEvidence.IdentitySeed }

            let ports: ChatAdmissionTransactionPorts =
                { Accept = accept
                  Acquire =
                    fun witness ->
                        let witnessEvidence = ManagedChatAcceptanceWitness.evidence witness

                        let _lenderSessionId: string option =
                            PromptAuthority.identitySeedOwner witnessEvidence.IdentitySeed
                            |> Option.map (fun (ownerSession, _, _) -> SessionId.value ownerSession)

                        if failureKind = "Supersession" then
                            Task.FromResult(Ok ExecutionAdmissionAcquisition.Superseded)
                        else
                            activeCapacity <- 1
                            Task.FromResult(Ok(ExecutionAdmissionAcquisition.Admitted lease))
                  LeaseTarget = fun _ -> Ok target
                  ProjectHost =
                    fun _ ->
                        if
                            failureKind = "ProjectionError"
                            || failureKind = "ExecutionBindingError"
                            || failureKind = "FatalMembraneInput"
                        then
                            Error(InvalidOperationException "injected pre-provider projection failure")
                        else
                            Ok()
                  Commit = fun _ _ -> CapacityTransitionOutcome.Applied
                  ReleaseBeforeProvider =
                    fun _ ->
                        if releaseMode = "Exact" then
                            activeCapacity <- 0

                        CapacityTransitionOutcome.Applied
                  SettlePreProvider = PreProviderSettlement.settleWith settlementPersistence
                  ReadExact = fun _ -> ChatExecutionProjection.byKey key projection }

            let current = ChatExecutionProjection.byKey key projection

            let! transactionResult = ChatAdmissionTransaction.executeWith (stepLabel >> trace.Add) ports managed

            let state = ChatExecutionProjection.byKey key projection

            let disposition =
                state |> Option.bind _.terminalDisposition |> Option.map string |> Option.toObj

            let classification =
                match transactionResult with
                | Ok(ChatAdmissionTransactionOutcome.Superseded _) -> "Recoverable"
                | _ -> "Permanent"

            let serializedFacts =
                facts
                |> Seq.map (AgentFact.ChatExecution >> Fact.Agent >> FactCodec.serializeFact)
                |> Seq.toArray

            return
                box
                    {| key =
                        {| sessionId = SessionId.value key.SessionId
                           physicalUserMessageId = PhysicalUserMessageId.value key.PhysicalUserMessageId |}
                       facts = serializedFacts
                       admission = {| activeCapacity = activeCapacity |}
                       acceptedFactCount =
                        facts
                        |> Seq.filter (function
                            | ChatExecutionFactCases.Accepted _ -> true
                            | _ -> false)
                        |> Seq.length
                       providerEffectCount = providerEffectCount
                       trace = trace.ToArray()
                       failure =
                        {| kind = failureKind
                           classification = classification
                           disposition = disposition |}
                       transactionOk = Result.isOk transactionResult |}
        }
