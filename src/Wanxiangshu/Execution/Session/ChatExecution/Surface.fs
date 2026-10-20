namespace Wanxiangshu.Execution.Session.ChatExecution

open Wanxiangshu.Persistence.Journal.JournalOutcome
open System
open System.Collections.Generic
open System.Threading.Tasks
open Fable.Core.JsInterop
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Composition.Durable.Fact
open Wanxiangshu.Participant.Provider.Attempt
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Participant.Persona
open Wanxiangshu.Persistence.Journal

module Surface =

    let private text (value: obj) : string = unbox value

    let private requiredText fieldName (value: obj) =
        if isNull value then
            invalidArg fieldName $"missing {fieldName}"

        let parsed = text value

        if String.IsNullOrWhiteSpace parsed then
            invalidArg fieldName $"missing {fieldName}"

        parsed

    let private authorityKindOf value =
        match requiredText "authorityKind" value with
        | "HumanRoot" -> PromptRootAuthorityKind.HumanRoot
        | "AgentOwnerRoot" -> PromptRootAuthorityKind.AgentOwnerRoot
        | kind -> invalidArg "authorityKind" $"unknown authority kind '{kind}'"

    let private originOf value =
        let label = requiredText "origin" value

        match label with
        | "HumanRoot" -> PromptOrigin.AuthorityRoot PromptRootAuthorityKind.HumanRoot
        | "AgentOwnerRoot" -> PromptOrigin.AuthorityRoot PromptRootAuthorityKind.AgentOwnerRoot
        | "HostInternal" -> PromptOrigin.HostInternal
        | "UnknownOrigin" -> PromptOrigin.UnknownOrigin
        | continuation ->
            PromptAuthority.tryParseContinuationKind continuation
            |> Option.map PromptOrigin.Continuation
            |> Option.defaultWith (fun () -> invalidArg "origin" $"unknown prompt origin '{label}'")

    let private requestKindOf value =
        match requiredText "requestKind" value with
        | "work-main" -> ProviderRequestKind.WorkMain
        | "interaction-repair" -> ProviderRequestKind.InteractionRepair
        | kind -> invalidArg "requestKind" $"unknown provider request kind '{kind}'"


    let private participantIdentityOf (value: obj) =
        let roleLabel =
            requiredText "identitySeed.participantIdentity.canonicalRole" value?canonicalRole

        let role =
            if roleLabel = "bookkeeper" then
                None
            else
                Roles.tryParseRole roleLabel
                |> Option.defaultWith (fun () -> invalidArg "canonicalRole" $"unknown role '{roleLabel}'")
                |> Some

        let origin =
            match requiredText "identitySeed.participantIdentity.origin" value?origin with
            | "ResolvedAtRoot" -> PersonaOrigin.ResolvedAtRoot
            | "InheritedFromOwner" -> PersonaOrigin.InheritedFromOwner
            | label -> invalidArg "identitySeed.participantIdentity.origin" $"unknown persona origin '{label}'"

        { SelectedAgent = requiredText "identitySeed.participantIdentity.selectedAgent" value?selectedAgent
          Role = role
          Persona = requiredText "identitySeed.participantIdentity.persona" value?persona
          PersonaCatalogVersion = unbox<int> value?personaCatalogVersion
          Origin = origin }
        |> ParticipantIdentity.fromInput
        |> Result.defaultWith (fun error -> invalidArg "identitySeed.participantIdentity" $"{error}")

    let private identitySeedOf (value: obj) =
        if isNull value then
            invalidArg "identitySeed" "missing identitySeed"

        let participantIdentity = participantIdentityOf value?participantIdentity
        let participantInput = ParticipantIdentity.toInput participantIdentity

        let input =
            match requiredText "identitySeed.kind" value?kind with
            | "RootSelection" -> PromptIdentitySeedInput.RootSelectionInput participantInput
            | "InheritedFromOwner" ->
                PromptIdentitySeedInput.InheritedFromOwnerInput
                    { OwnerSessionId = SessionId.create (requiredText "identitySeed.ownerSession" value?ownerSession)
                      OwnerLogicalRunId =
                        LogicalRunId.create (requiredText "identitySeed.ownerLogicalRun" value?ownerLogicalRun)
                      OwnerAuthorityRootUserMessageId =
                        AuthorityRootUserMessageId.create (
                            requiredText "identitySeed.ownerAuthorityRoot" value?ownerAuthorityRoot
                        )
                      ParticipantIdentity = participantInput }
            | kind -> invalidArg "identitySeed.kind" $"unknown identity seed kind '{kind}'"

        PromptIdentitySeed.rehydrate input
        |> Result.defaultWith (fun error -> invalidArg "identitySeed" $"{error}")

    let private acceptedEvidenceOf (value: obj) : AcceptedChatExecutionEvidence =
        { SessionId = SessionId.create (requiredText "sessionId" value?sessionId)
          LogicalRunId = LogicalRunId.create (requiredText "logicalRunId" value?logicalRunId)
          AuthorityRootUserMessageId =
            AuthorityRootUserMessageId.create (
                requiredText "authorityRootUserMessageId" value?authorityRootUserMessageId
            )
          AuthorityKind = authorityKindOf value?authorityKind
          IdentitySeed = identitySeedOf value?identitySeed
          PhysicalUserMessageId =
            PhysicalUserMessageId.create (requiredText "physicalUserMessageId" value?physicalUserMessageId)
          Origin = originOf value?origin }

    let private participantIdentityToJs (identity: ParticipantIdentityEvidence) : obj =
        box
            {| participant = ParticipantIdentity.selectedAgent identity
               role = ParticipantIdentity.roleLabel identity
               persona = ParticipantIdentity.persona identity
               personaCatalogVersion = ParticipantIdentity.personaCatalogVersion identity
               origin =
                match ParticipantIdentity.origin identity with
                | PersonaOrigin.ResolvedAtRoot -> "ResolvedAtRoot"
                | PersonaOrigin.InheritedFromOwner -> "InheritedFromOwner" |}

    let private identitySeedToJs (seed: PromptIdentitySeed) : obj =
        let participantIdentity =
            PromptAuthority.identitySeedParticipantIdentity seed |> participantIdentityToJs

        match PromptAuthority.identitySeedOwner seed with
        | None ->
            box
                {| kind = "RootSelection"
                   ownerSession = null
                   ownerLogicalRun = null
                   ownerAuthorityRoot = null
                   participantIdentity = participantIdentity |}
        | Some(ownerSession, ownerLogicalRun, ownerAuthorityRoot) ->
            box
                {| kind = "InheritedFromOwner"
                   ownerSession = SessionId.value ownerSession
                   ownerLogicalRun = LogicalRunId.value ownerLogicalRun
                   ownerAuthorityRoot = AuthorityRootUserMessageId.value ownerAuthorityRoot
                   participantIdentity = participantIdentity |}

    let private authorityKindLabel =
        function
        | PromptRootAuthorityKind.HumanRoot -> "HumanRoot"
        | PromptRootAuthorityKind.AgentOwnerRoot -> "AgentOwnerRoot"

    let private dispositionLabel =
        function
        | ChatExecutionTerminalDisposition.Completed -> "Completed"
        | ChatExecutionTerminalDisposition.Cancelled -> "Cancelled"
        | ChatExecutionTerminalDisposition.Rejected -> "Rejected"
        | ChatExecutionTerminalDisposition.Failed -> "Failed"

    let private evidenceToJs (evidence: AcceptedChatExecutionEvidence) : obj =
        box
            {| sessionId = SessionId.value evidence.SessionId
               physicalUserMessageId = PhysicalUserMessageId.value evidence.PhysicalUserMessageId
               logicalRunId = LogicalRunId.value evidence.LogicalRunId
               authorityRootUserMessageId = AuthorityRootUserMessageId.value evidence.AuthorityRootUserMessageId
               authorityKind = authorityKindLabel evidence.AuthorityKind
               identitySeed = identitySeedToJs evidence.IdentitySeed
               origin = PromptAuthority.originLabel evidence.Origin
               participant = AcceptedChatExecutionEvidence.participant evidence
               role = Roles.roleLabel (AcceptedChatExecutionEvidence.canonicalRole evidence) |}

    let private stateToJs (state: ChatExecutionState) : obj =
        let phase, disposition =
            match state with
            | ChatExecutionState.Accepted _ -> "Accepted", null
            | ChatExecutionState.Started _ -> "ProviderStarted", null
            | ChatExecutionState.EndedBeforeStart(_, outcome) ->
                "Terminal", box (dispositionLabel (PreStartOutcome.disposition outcome))
            | ChatExecutionState.EndedAfterStart(_, terminal) -> "Terminal", box (dispositionLabel terminal)

        box
            {| sessionId = SessionId.value state.key.SessionId
               physicalUserMessageId = PhysicalUserMessageId.value state.key.PhysicalUserMessageId
               phase = phase
               disposition = disposition
               identity =
                {| logicalRunId = LogicalRunId.value state.acceptedEvidence.LogicalRunId
                   authorityRootUserMessageId =
                    AuthorityRootUserMessageId.value state.acceptedEvidence.AuthorityRootUserMessageId
                   authorityKind = authorityKindLabel state.acceptedEvidence.AuthorityKind
                   identitySeed = identitySeedToJs state.acceptedEvidence.IdentitySeed
                   providerRun =
                    state.startedEvidence
                    |> Option.map (fun evidence -> ProviderRunIdentity.value evidence.ProviderRun)
                    |> Option.toObj
                   origin = PromptAuthority.originLabel state.acceptedEvidence.Origin
                   participant = AcceptedChatExecutionEvidence.participant state.acceptedEvidence
                   role = Roles.roleLabel (AcceptedChatExecutionEvidence.canonicalRole state.acceptedEvidence)
                   requestKind =
                    state.startedEvidence
                    |> Option.map (fun evidence -> ProviderRequestKind.label evidence.RequestKind)
                    |> Option.toObj |} |}

    let private keyToJs (key: ChatExecutionKey) : obj =
        box
            {| sessionId = SessionId.value key.SessionId
               physicalUserMessageId = PhysicalUserMessageId.value key.PhysicalUserMessageId |}

    let private witnessToJs witness : obj =
        box
            {| key = ManagedChatAcceptanceWitness.key witness |> keyToJs
               evidence = ManagedChatAcceptanceWitness.evidence witness |> evidenceToJs |}

    let private acceptanceErrorToJs =
        function
        | ManagedChatAcceptanceError.IntentRejected reason ->
            box
                {| kind = "IntentRejected"
                   reason = reason |}
        | ManagedChatAcceptanceError.AuthorityRegistrationRejected _ -> box {| kind = "AuthorityRegistrationRejected" |}
        | ManagedChatAcceptanceError.AttemptEvidenceInvalid reason ->
            box
                {| kind = "AttemptEvidenceInvalid"
                   reason = reason |}
        | ManagedChatAcceptanceError.AttemptKeyMismatch(evidenceKey, requestedKey) ->
            box
                {| kind = "AttemptKeyMismatch"
                   evidenceKey = keyToJs evidenceKey
                   requestedKey = keyToJs requestedKey |}
        | ManagedChatAcceptanceError.EstablishedEvidenceConflict(established, attempted) ->
            box
                {| kind = "EstablishedEvidenceConflict"
                   established = evidenceToJs established
                   attempted = evidenceToJs attempted |}
        | ManagedChatAcceptanceError.ProjectionMissingAfterCommit key ->
            box
                {| kind = "ProjectionMissingAfterCommit"
                   key = keyToJs key |}
        | ManagedChatAcceptanceError.ProjectionConflictAfterCommit(established, attempted) ->
            box
                {| kind = "ProjectionConflictAfterCommit"
                   established = evidenceToJs established
                   attempted = evidenceToJs attempted |}
        | ManagedChatAcceptanceError.NotAttempted _ -> box {| kind = "NotAttempted" |}
        | ManagedChatAcceptanceError.CommitUnknown _ -> box {| kind = "CommitUnknown" |}
        | ManagedChatAcceptanceError.NoNewWriteReleaseFailed _ -> box {| kind = "NoNewWriteReleaseFailed" |}
        | ManagedChatAcceptanceError.FactRejected _ -> box {| kind = "FactRejected" |}

    let private intentToJs =
        function
        | ChatAdmissionIntent.AlreadyTerminal disposition ->
            box
                {| kind = "AlreadyTerminal"
                   evidence = null
                   disposition = dispositionLabel disposition |}
        | ChatAdmissionIntent.AlreadyStarted evidence ->
            box
                {| kind = "AlreadyStarted"
                   evidence = evidenceToJs evidence.Accepted
                   disposition = null |}
        | ChatAdmissionIntent.ResumeAccepted evidence ->
            box
                {| kind = "ResumeAccepted"
                   evidence = evidenceToJs evidence
                   disposition = null |}
        | ChatAdmissionIntent.NeedAcceptance evidence ->
            box
                {| kind = "NeedAcceptance"
                   evidence = evidenceToJs evidence
                   disposition = null |}

    let private admissionErrorToJs =
        function
        | ChatAdmissionError.StateKeyMismatch(suppliedStateKey, messageKey) ->
            box
                {| kind = "StateKeyMismatch"
                   suppliedStateKey = keyToJs suppliedStateKey
                   messageKey = keyToJs messageKey |}
        | ChatAdmissionError.AttemptKeyMismatch(attemptKey, messageKey) ->
            box
                {| kind = "AttemptKeyMismatch"
                   attemptKey = keyToJs attemptKey
                   messageKey = keyToJs messageKey |}
        | ChatAdmissionError.ExplicitAgentMismatch(explicitAgent, selectedAgent) ->
            box
                {| kind = "ExplicitAgentMismatch"
                   explicitAgent = explicitAgent
                   selectedAgent = selectedAgent |}
        | ChatAdmissionError.AttemptEvidenceInvalid reason ->
            box
                {| kind = "AttemptEvidenceInvalid"
                   reason = reason |}
        | ChatAdmissionError.ExistingEvidenceConflict(established, attempted) ->
            box
                {| kind = "ExistingEvidenceConflict"
                   established = evidenceToJs established
                   attempted = evidenceToJs attempted |}

    let private chatFact =
        function
        | Fact.Agent(AgentFact.ChatExecution fact) -> Ok fact
        | _ -> Error "expected a ChatExecution AgentFact"

    let private decodeChatFact serializedFact =
        FactCodec.deserializeFact serializedFact |> Result.bind chatFact

    let private rejectionText (rejection: FoldRejection) =
        rejection.Fact + ": " + rejection.Reason

    let private foldFacts (serializedFacts: string array) =
        serializedFacts
        |> Array.fold
            (fun projectionResult serializedFact ->
                projectionResult
                |> Result.bind (fun projection ->
                    decodeChatFact serializedFact
                    |> Result.bind (fun fact ->
                        ChatExecutionFactFold.fold projection fact |> Result.mapError rejectionText)))
            (Ok ChatExecutionProjection.empty)

    let private resultToJs mapValue =
        function
        | Ok value ->
            box
                {| ok = true
                   value = mapValue value
                   error = "" |}
        | Error error ->
            box
                {| ok = false
                   value = null
                   error = error |}

    let canonicalize (serializedFact: string) : obj =
        try
            decodeChatFact serializedFact
            |> Result.map (AgentFact.ChatExecution >> Fact.Agent >> FactCodec.serializeFact)
            |> resultToJs box
        with error ->
            box
                {| ok = false
                   value = null
                   error = error.Message |}

    let fold (serializedFacts: string array) : obj =
        try
            foldFacts serializedFacts
            |> resultToJs (ChatExecutionProjection.current >> List.map stateToJs >> List.toArray >> box)
        with error ->
            box
                {| ok = false
                   value = null
                   error = error.Message |}

    let nonTerminal (serializedFacts: string array) (sessionId: string) : obj =
        try
            foldFacts serializedFacts
            |> Result.map (fun projection ->
                ChatExecutionProjection.nonTerminal projection
                |> List.filter (fun execution -> SessionId.value execution.key.SessionId = sessionId))
            |> resultToJs (List.map stateToJs >> List.toArray >> box)
        with error ->
            box
                {| ok = false
                   value = null
                   error = error.Message |}

    let admitIntent (serializedFacts: string array) (messageValue: obj) (attemptedEvidenceValue: obj) : obj =
        let failure error =
            box
                {| ok = false
                   intent = null
                   error = admissionErrorToJs error |}

        let decide message state attemptedEvidence =
            ChatAdmission.decide message attemptedEvidence state
            |> function
                | Ok intent ->
                    box
                        {| ok = true
                           intent = intentToJs intent
                           error = null |}
                | Error error -> failure error

        try
            match foldFacts serializedFacts with
            | Error reason -> failure (ChatAdmissionError.AttemptEvidenceInvalid reason)
            | Ok projection ->
                let message =
                    { SessionId = SessionId.create (requiredText "message.sessionId" messageValue?sessionId)
                      PhysicalUserMessageId =
                        PhysicalUserMessageId.create (
                            requiredText "message.physicalUserMessageId" messageValue?physicalUserMessageId
                        )
                      ExplicitAgent =
                        if isNull messageValue?explicitAgent then
                            None
                        else
                            Some(text messageValue?explicitAgent) }

                let state =
                    match ChatExecutionProjection.current projection with
                    | [] -> None
                    | [ execution ] -> Some execution
                    | _ -> invalidArg "serializedFacts" "admitIntent requires at most one projected execution"

                match state with
                | Some execution when
                    execution.key.SessionId <> message.SessionId
                    || execution.key.PhysicalUserMessageId <> message.PhysicalUserMessageId
                    ->
                    decide message state execution.acceptedEvidence
                | Some(ChatExecutionState.EndedBeforeStart(accepted, _))
                | Some(ChatExecutionState.EndedAfterStart({ Accepted = accepted }, _)) -> decide message state accepted
                | _ -> decide message state (acceptedEvidenceOf attemptedEvidenceValue)
        with error ->
            failure (ChatAdmissionError.AttemptEvidenceInvalid error.Message)

    let private scenarioPersistence (appendOutcome: string) =
        let trace = ResizeArray<string>()
        // DSL-MUTABLE: algorithm-scratch
        let mutable projection = ChatExecutionProjection.empty
        let mutable appendCount = 0

        let persistence: ManagedChatAcceptancePersistence =
            { ReadExact =
                fun key ->
                    trace.Add(
                        if trace.Count > 0 && trace.[trace.Count - 1] = "Committed" then
                            "ReRead"
                        else
                            "Read"
                    )

                    ChatExecutionProjection.byKey key projection
              AppendAccepted =
                fun key evidence ->
                    task {
                        appendCount <- appendCount + 1
                        trace.Add "Append"

                        match appendOutcome with
                        | "Committed" ->
                            match ChatExecutionFactFold.applyAccepted key evidence projection with
                            | Ok updated ->
                                projection <- updated
                                trace.Add "Committed"
                                return Ok()
                            | Error rejection ->
                                return
                                    Error(
                                        JournalAppendFailure.FactRejected(
                                            EventId.create "acceptance-scenario-rejected",
                                            rejection
                                        )
                                    )
                        | "NotAttempted" ->
                            return
                                Error(
                                    JournalAppendFailure.WriterUnavailable(
                                        EventId.create "acceptance-scenario-not-attempted",
                                        JournalUnavailable.WriterDisposed
                                    )
                                )
                        | "CommitUnknown" ->
                            return
                                Error(
                                    JournalAppendFailure.WriteUnknown(
                                        EventId.create "acceptance-scenario-commit-unknown",
                                        JournalFailure.WriteFailed "scenario"
                                    )
                                )
                        | value -> return invalidArg "appendOutcome" $"unknown append outcome '{value}'"
                    } }

        persistence, trace, (fun () -> appendCount)

    let private acceptanceScenarioResult
        (trace: ResizeArray<string>)
        (appendCount: unit -> int)
        (result: Result<ManagedChatAcceptanceWitness, ManagedChatAcceptanceError>)
        =
        match result with
        | Ok witness ->
            trace.Add "Witness"

            box
                {| ok = true
                   witness = witnessToJs witness
                   error = null
                   trace = trace.ToArray()
                   acceptanceAppendCount = appendCount ()
                   capacityEffectCount = 0
                   hostEffectCount = 0 |}
        | Error error ->
            box
                {| ok = false
                   witness = null
                   error = acceptanceErrorToJs error
                   trace = trace.ToArray()
                   acceptanceAppendCount = appendCount ()
                   capacityEffectCount = 0
                   hostEffectCount = 0 |}

    let acceptanceScenario (attemptedEvidenceValue: obj) (appendOutcome: string) : Task<obj> =
        task {
            let evidence = acceptedEvidenceOf attemptedEvidenceValue

            let key =
                { SessionId = evidence.SessionId
                  PhysicalUserMessageId = evidence.PhysicalUserMessageId }

            let persistence, trace, appendCount = scenarioPersistence appendOutcome
            let! result = ManagedChatAcceptance.acceptWith persistence key evidence
            return acceptanceScenarioResult trace appendCount result
        }

    let acceptanceDuplicateScenario (attemptedEvidenceValue: obj) : Task<obj> =
        task {
            let evidence = acceptedEvidenceOf attemptedEvidenceValue

            let key =
                { SessionId = evidence.SessionId
                  PhysicalUserMessageId = evidence.PhysicalUserMessageId }

            let persistence, trace, appendCount = scenarioPersistence "Committed"
            let! first = ManagedChatAcceptance.acceptWith persistence key evidence
            trace.Clear()
            let! second = ManagedChatAcceptance.acceptWith persistence key evidence

            match first, second with
            | Ok firstWitness, Ok secondWitness ->
                trace.Add "Witness"

                return
                    box
                        {| ok = true
                           firstWitness = witnessToJs firstWitness
                           secondWitness = witnessToJs secondWitness
                           secondTrace = trace.ToArray()
                           error = null
                           acceptanceAppendCount = appendCount () |}
            | Error error, _
            | _, Error error ->
                return
                    box
                        {| ok = false
                           firstWitness = null
                           secondWitness = null
                           secondTrace = trace.ToArray()
                           error = acceptanceErrorToJs error
                           acceptanceAppendCount = appendCount () |}
        }

    let acceptanceConflictScenario (establishedEvidenceValue: obj) (attemptedEvidenceValue: obj) : Task<obj> =
        task {
            let established = acceptedEvidenceOf establishedEvidenceValue
            let attempted = acceptedEvidenceOf attemptedEvidenceValue

            let key =
                { SessionId = established.SessionId
                  PhysicalUserMessageId = established.PhysicalUserMessageId }

            let persistence, _, appendCount = scenarioPersistence "Committed"
            let! first = ManagedChatAcceptance.acceptWith persistence key established

            match first with
            | Error error ->
                return
                    box
                        {| ok = false
                           witness = null
                           error = acceptanceErrorToJs error
                           acceptanceAppendCount = appendCount () |}
            | Ok _ ->
                let! conflicting = ManagedChatAcceptance.acceptWith persistence key attempted

                return
                    match conflicting with
                    | Ok witness ->
                        box
                            {| ok = true
                               witness = witnessToJs witness
                               error = null
                               acceptanceAppendCount = appendCount () |}
                    | Error error ->
                        box
                            {| ok = false
                               witness = null
                               error = acceptanceErrorToJs error
                               acceptanceAppendCount = appendCount () |}
        }

    let private lifecycleErrorToJs =
        function
        | ManagedChatProviderLifecycleError.AttemptEvidenceInvalid reason ->
            box
                {| kind = "AttemptEvidenceInvalid"
                   detail = box reason |}
        | ManagedChatProviderLifecycleError.AttemptKeyMismatch(evidenceKey, requestedKey) ->
            box
                {| kind = "AttemptKeyMismatch"
                   detail =
                    box
                        {| evidenceKey = keyToJs evidenceKey
                           requestedKey = keyToJs requestedKey |} |}
        | ManagedChatProviderLifecycleError.MissingAccepted key ->
            box
                {| kind = "MissingAccepted"
                   detail = keyToJs key |}
        | ManagedChatProviderLifecycleError.EstablishedEvidenceConflict(established, attempted) ->
            box
                {| kind = "EstablishedEvidenceConflict"
                   detail =
                    box
                        {| established = evidenceToJs established
                           attempted = evidenceToJs attempted |} |}
        | ManagedChatProviderLifecycleError.ProviderRunConflict(established, attempted) ->
            box
                {| kind = "ProviderRunConflict"
                   detail =
                    box
                        {| established = ProviderRunIdentity.value established
                           attempted = ProviderRunIdentity.value attempted |} |}
        | ManagedChatProviderLifecycleError.ProviderNotStarted key ->
            box
                {| kind = "ProviderNotStarted"
                   detail = keyToJs key |}
        | ManagedChatProviderLifecycleError.ProviderStartedAfterTerminal disposition ->
            box
                {| kind = "ProviderStartedAfterTerminal"
                   detail = box (string disposition) |}
        | ManagedChatProviderLifecycleError.TerminalConflict(established, attempted) ->
            box
                {| kind = "TerminalConflict"
                   detail =
                    box
                        {| established = string established
                           attempted = string attempted |} |}
        | ManagedChatProviderLifecycleError.ProjectionMissingAfterCommit key ->
            box
                {| kind = "ProjectionMissingAfterCommit"
                   detail = keyToJs key |}
        | ManagedChatProviderLifecycleError.ProjectionConflictAfterCommit state ->
            box
                {| kind = "ProjectionConflictAfterCommit"
                   detail = stateToJs state |}
        | ManagedChatProviderLifecycleError.NotAttempted(eventId, unavailable) ->
            box
                {| kind = "NotAttempted"
                   detail =
                    box
                        {| eventId = EventId.value eventId
                           failure = string unavailable |} |}
        | ManagedChatProviderLifecycleError.CommitUnknown(eventId, failure) ->
            box
                {| kind = "CommitUnknown"
                   detail =
                    box
                        {| eventId = EventId.value eventId
                           failure = string failure |} |}
        | ManagedChatProviderLifecycleError.NoNewWriteReleaseFailed(eventId, failure) ->
            box
                {| kind = "NoNewWriteReleaseFailed"
                   detail =
                    box
                        {| eventId = EventId.value eventId
                           failure = failure.Cause.Message |} |}
        | ManagedChatProviderLifecycleError.FactRejected(eventId, rejection) ->
            box
                {| kind = "FactRejected"
                   detail =
                    box
                        {| eventId = EventId.value eventId
                           reason = rejection.Reason |} |}

    let private terminalDispositionOf value =
        match requiredText "disposition" value with
        | "Completed" -> ChatExecutionTerminalDisposition.Completed
        | "Cancelled" -> ChatExecutionTerminalDisposition.Cancelled
        | "Rejected" -> ChatExecutionTerminalDisposition.Rejected
        | "Failed" -> ChatExecutionTerminalDisposition.Failed
        | disposition -> invalidArg "disposition" $"unknown terminal disposition '{disposition}'"

    let private scenarioAppendFailure kind =
        match kind with
        | "NotAttempted" ->
            JournalAppendFailure.WriterUnavailable(
                EventId.create "provider-lifecycle-not-attempted",
                JournalUnavailable.WriterDisposed
            )
        | "CommitUnknown" ->
            JournalAppendFailure.WriteUnknown(
                EventId.create "provider-lifecycle-commit-unknown",
                JournalFailure.WriteFailed "scenario"
            )
        | outcome -> invalidArg "appendOutcome" $"unknown append outcome '{outcome}'"

    let providerLifecycleScenario (actions: obj array) : Task<obj> =
        task {
            let trace = ResizeArray<string>()
            // DSL-MUTABLE: algorithm-scratch
            let mutable projection = ChatExecutionProjection.empty
            let mutable scenarioKey: ChatExecutionKey option = None
            // DSL-MUTABLE: algorithm-scratch
            let mutable acceptedAppendCount = 0
            let mutable providerStartedAppendCount = 0
            // DSL-MUTABLE: algorithm-scratch
            let mutable terminalAppendCount = 0
            let mutable semanticTransitionCount = 0
            // DSL-MUTABLE: algorithm-scratch
            let mutable providerWorkCount = 0
            let mutable providerAdmitted = false

            let keyFor (evidence: AcceptedChatExecutionEvidence) : ChatExecutionKey =
                match scenarioKey with
                | Some key -> key
                | None ->
                    let key =
                        { SessionId = evidence.SessionId
                          PhysicalUserMessageId = evidence.PhysicalUserMessageId }

                    scenarioKey <- Some key
                    key

            let readExact (key: ChatExecutionKey) : ChatExecutionState option =
                trace.Add(
                    if trace.Count > 0 && trace.[trace.Count - 1] = "Committed" then
                        "ReRead"
                    else
                        "Read"
                )

                ChatExecutionProjection.byKey key projection

            let commit (fact: ChatExecutionFactCases) : Result<unit, JournalAppendFailure> =
                let before = projection

                match ChatExecutionFactFold.fold projection fact with
                | Ok updated ->
                    projection <- updated

                    if updated <> before then
                        semanticTransitionCount <- semanticTransitionCount + 1

                    trace.Add "Committed"
                    Ok()
                | Error rejection ->
                    Error(JournalAppendFailure.FactRejected(EventId.create "provider-lifecycle-rejected", rejection))

            let acceptancePersistence outcome : ManagedChatAcceptancePersistence =
                { ReadExact = readExact
                  AppendAccepted =
                    fun (key: ChatExecutionKey) (evidence: AcceptedChatExecutionEvidence) ->
                        task {
                            acceptedAppendCount <- acceptedAppendCount + 1
                            trace.Add "AppendAccepted"

                            if outcome = "Committed" then
                                return
                                    commit (
                                        ChatExecutionFactCases.Accepted
                                            {| SchemaVersion = 1
                                               Key = key
                                               Evidence = evidence |}
                                    )
                            else
                                return Error(scenarioAppendFailure outcome)
                        } }

            let lifecyclePersistence outcome : ManagedChatProviderLifecyclePersistence =
                { ReadExact = readExact
                  AppendFact =
                    fun (_: ProviderStartedEvidence) (fact: ChatExecutionFactCases) ->
                        task {
                            match fact with
                            | ChatExecutionFactCases.ProviderStarted _ ->
                                providerStartedAppendCount <- providerStartedAppendCount + 1
                                trace.Add "AppendProviderStarted"
                            | ChatExecutionFactCases.Terminal _ ->
                                terminalAppendCount <- terminalAppendCount + 1
                                trace.Add "AppendTerminal"
                            | ChatExecutionFactCases.Accepted _ -> invalidOp "provider lifecycle cannot append Accepted"

                            if outcome = "Committed" then
                                return commit fact
                            else
                                return Error(scenarioAppendFailure outcome)
                        } }

            let projectionToJs () =
                scenarioKey
                |> Option.bind (fun key -> ChatExecutionProjection.byKey key projection)
                |> Option.map (fun state ->
                    let phase, disposition =
                        match state with
                        | ChatExecutionState.Accepted _ -> "Accepted", null
                        | ChatExecutionState.Started _ -> "ProviderStarted", null
                        | ChatExecutionState.EndedBeforeStart(_, outcome) ->
                            "Terminal", box (string (PreStartOutcome.disposition outcome))
                        | ChatExecutionState.EndedAfterStart(_, terminal) -> "Terminal", box (string terminal)

                    box
                        {| sessionId = SessionId.value state.key.SessionId
                           physicalUserMessageId = PhysicalUserMessageId.value state.key.PhysicalUserMessageId
                           phase = phase
                           disposition = disposition |})
                |> Option.defaultValue null

            let result ok error =
                box
                    {| ok = ok
                       error = error
                       projection = projectionToJs ()
                       trace = trace.ToArray()
                       appendCounts =
                        {| accepted = acceptedAppendCount
                           providerStarted = providerStartedAppendCount
                           terminal = terminalAppendCount |}
                       semanticTransitionCount = semanticTransitionCount
                       providerWorkCount = providerWorkCount |}

            // DSL-MUTABLE: algorithm-scratch
            let mutable failure: obj option = None

            for action in actions do
                if failure.IsNone then
                    match requiredText "kind" action?kind with
                    | "ProviderWork" ->
                        if providerAdmitted then
                            providerWorkCount <- providerWorkCount + 1
                        else
                            failure <-
                                Some(
                                    box
                                        {| kind = "ProviderWorkNotAdmitted"
                                           detail = null |}
                                )
                    | kind ->
                        let evidenceValue: obj = action?evidence

                        let attemptedEvidence: AcceptedChatExecutionEvidence =
                            acceptedEvidenceOf evidenceValue

                        let key = keyFor attemptedEvidence
                        let appendOutcome = requiredText "appendOutcome" action?appendOutcome

                        match kind with
                        | "Accept" ->
                            match!
                                ManagedChatAcceptance.acceptWith
                                    (acceptancePersistence appendOutcome)
                                    key
                                    attemptedEvidence
                            with
                            | Ok _ -> trace.Add "AcceptedWitness"
                            | Error error -> failure <- Some(acceptanceErrorToJs error)
                        | "ProviderStarted" ->
                            let providerRun =
                                ProviderRunIdentity.create (requiredText "providerRun" evidenceValue?providerRun)

                            let requestKind = requestKindOf evidenceValue?requestKind

                            match!
                                ManagedChatProviderLifecycle.startWith
                                    (lifecyclePersistence appendOutcome)
                                    key
                                    attemptedEvidence
                                    providerRun
                                    requestKind
                            with
                            | Ok _ ->
                                providerAdmitted <- true
                                trace.Add "ProviderStartedWitness"
                            | Error error -> failure <- Some(lifecycleErrorToJs error)
                        | "Terminal" ->
                            let disposition = terminalDispositionOf action?disposition

                            let startedEvidence =
                                { Accepted = attemptedEvidence
                                  ProviderRun =
                                    ProviderRunIdentity.create (requiredText "providerRun" evidenceValue?providerRun)
                                  RequestKind = requestKindOf evidenceValue?requestKind }

                            match!
                                ManagedChatProviderLifecycle.terminalWith
                                    (lifecyclePersistence appendOutcome)
                                    key
                                    startedEvidence
                                    disposition
                            with
                            | Ok _ -> trace.Add "TerminalWitness"
                            | Error error -> failure <- Some(lifecycleErrorToJs error)
                        | unknown -> invalidArg "kind" $"unknown lifecycle action '{unknown}'"

            return
                match failure with
                | None -> result true null
                | Some error -> result false error
        }
