namespace Wanxiangshu.Participant.Provider.Attempt.Fallback

open System
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Foundation
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Composition.Durable.Fact
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Interaction.Dispatch
open Wanxiangshu.Participant.Persona
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Participant.Provider.Attempt
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Execution.Failure
open Wanxiangshu.Execution.Session.ChatExecution
open Wanxiangshu.Composition.Turn

/// JSON/opaque owner boundary for the pure provider failure budget and its durable fold.
/// Budget/projection identities and journal facts never cross as Fable records,
/// maps, lists or union cases; projection handles remain opaque between calls.
[<RequireQualifiedAccess>]
module ProviderFailureSurface =

    type private ProjectionHandle(projection: ProviderFailureProjection) =
        member _.Value = projection

    [<Emit("$0 == null")>]
    let private isNullish (value: obj) : bool = jsNative

    let private field (value: obj) (name: string) : obj =
        if isNullish value then
            null
        else
            emitJsExpr (value, name) "$0[$1]"

    let private text (value: obj) : string =
        if isNullish value then "" else string value

    let private intValue (value: obj) : int =
        if isNullish value then 0 else int (text value)

    let private optionalText (value: obj) : string option =
        if isNullish value then None else Some(text value)

    let private requiredText argument value =
        let parsed = text value

        if String.IsNullOrWhiteSpace parsed then
            invalidArg argument $"missing {argument}"

        parsed

    let private optionObj (value: 'a option) : obj =
        match value with
        | None -> null
        | Some item -> box item

    let private firstField (value: obj) (names: string list) : obj =
        names
        |> List.tryPick (fun name ->
            let item = field value name
            if isNullish item then None else Some item)
        |> Option.defaultValue null

    let private participantIdentityOf (value: obj) : ParticipantIdentityEvidence =
        if isNullish value then
            invalidArg "identitySeed.participantIdentity" "missing identitySeed.participantIdentity"

        let roleLabel =
            requiredText "identitySeed.participantIdentity.canonicalRole" (field value "canonicalRole")

        let role =
            if roleLabel = "bookkeeper" then
                None
            else
                Roles.tryParseRole roleLabel
                |> Option.defaultWith (fun () -> invalidArg "participantIdentity" $"unknown role '{roleLabel}'")
                |> Some

        let originLabel =
            requiredText "identitySeed.participantIdentity.origin" (field value "origin")

        let origin =
            match originLabel with
            | "ResolvedAtRoot" -> PersonaOrigin.ResolvedAtRoot
            | "InheritedFromOwner" -> PersonaOrigin.InheritedFromOwner
            | _ -> invalidArg "participantIdentity" $"unknown persona origin '{originLabel}'"

        { SelectedAgent = requiredText "identitySeed.participantIdentity.selectedAgent" (field value "selectedAgent")
          Role = role
          Persona = requiredText "identitySeed.participantIdentity.persona" (field value "persona")
          PersonaCatalogVersion = intValue (field value "personaCatalogVersion")
          Origin = origin }
        |> ParticipantIdentity.fromInput
        |> Result.defaultWith (fun error -> invalidArg "participantIdentity" $"invalid participant identity: {error}")

    let private identitySeedOf (value: obj) : PromptAuthority.IdentitySeed =
        if isNullish value then
            invalidArg "identitySeed" "missing identitySeed"

        let participantIdentity = participantIdentityOf (field value "participantIdentity")
        let identityInput = ParticipantIdentity.toInput participantIdentity
        let ownerSession = field value "ownerSession"
        let ownerLogicalRun = field value "ownerLogicalRun"
        let ownerAuthorityRoot = field value "ownerAuthorityRoot"

        let seedInput =
            match requiredText "identitySeed.kind" (field value "kind") with
            | "RootSelection" ->
                if
                    not (isNullish ownerSession)
                    || not (isNullish ownerLogicalRun)
                    || not (isNullish ownerAuthorityRoot)
                then
                    invalidArg "identitySeed" "RootSelection cannot carry inherited owner evidence"

                PromptAuthority.IdentitySeedInput.RootSelectionInput identityInput
            | "InheritedFromOwner" ->
                PromptAuthority.IdentitySeedInput.InheritedFromOwnerInput
                    { OwnerSessionId = SessionId.create (requiredText "identitySeed.ownerSession" ownerSession)
                      OwnerLogicalRunId =
                        LogicalRunId.create (requiredText "identitySeed.ownerLogicalRun" ownerLogicalRun)
                      OwnerAuthorityRootUserMessageId =
                        AuthorityRootUserMessageId.create (
                            requiredText "identitySeed.ownerAuthorityRoot" ownerAuthorityRoot
                        )
                      ParticipantIdentity = identityInput }
            | kind -> invalidArg "identitySeed" $"unknown identity seed kind '{kind}'"

        PromptIdentitySeed.rehydrate seedInput
        |> Result.defaultWith (fun error -> invalidArg "identitySeed" $"invalid identity seed: {error}")

    let private participantIdentityView (identity: ParticipantIdentityEvidence) : obj =
        box
            {| participant = ParticipantIdentity.selectedAgent identity
               selectedAgent = ParticipantIdentity.selectedAgent identity
               canonicalRole = ParticipantIdentity.roleLabel identity
               role = ParticipantIdentity.roleLabel identity
               selectedTier = "deep"
               persona = ParticipantIdentity.persona identity
               personaCatalogVersion = ParticipantIdentity.personaCatalogVersion identity
               origin =
                match ParticipantIdentity.origin identity with
                | PersonaOrigin.ResolvedAtRoot -> "ResolvedAtRoot"
                | PersonaOrigin.InheritedFromOwner -> "InheritedFromOwner" |}

    let private identitySeedView (seed: PromptAuthority.IdentitySeed) : obj =
        let participantIdentity =
            PromptAuthority.identitySeedParticipantIdentity seed |> participantIdentityView

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

    let private budgetOf (value: obj) : ProviderFailureBudget.FailureBudget =
        if isNullish value then
            ProviderFailureBudget.initial
        else
            { ConsecutiveFailureCount = intValue (firstField value [ "failures"; "ConsecutiveFailureCount" ]) }

    let private budgetView (budget: ProviderFailureBudget.FailureBudget) : obj =
        box {| failures = budget.ConsecutiveFailureCount |}

    let private identityOf (value: obj) : FailedProviderAttemptIdentity =
        { SessionId = SessionId.create (text (firstField value [ "session"; "SessionId" ]))
          LogicalRunId = LogicalRunId.create (text (firstField value [ "run"; "logicalRun"; "LogicalRunId" ]))
          AuthorityRootUserMessageId =
            AuthorityRootUserMessageId.create (
                text (firstField value [ "root"; "authorityRoot"; "AuthorityRootUserMessageId" ])
            )
          ProviderRun = ProviderRunIdentity.create (text (firstField value [ "attempt"; "ProviderRun" ])) }

    let private identityView (identity: FailedProviderAttemptIdentity) : obj =
        box
            {| session = SessionId.value identity.SessionId
               run = LogicalRunId.value identity.LogicalRunId
               root = AuthorityRootUserMessageId.value identity.AuthorityRootUserMessageId
               attempt = ProviderRunIdentity.value identity.ProviderRun |}

    let private projectionView (projection: ProviderFailureProjection) : obj =
        box
            {| logicalRun = LogicalRunId.value projection.LogicalRunId
               authorityRoot = AuthorityRootUserMessageId.value projection.AuthorityRootUserMessageId
               failures = projection.Budget.ConsecutiveFailureCount
               dedupeKeys = List.length projection.RecentFailureKeys
               exhausted = projection.Exhausted |}

    let private projectionHandleView (projection: ProviderFailureProjection) : obj =
        box
            {| logicalRun = LogicalRunId.value projection.LogicalRunId
               authorityRoot = AuthorityRootUserMessageId.value projection.AuthorityRootUserMessageId
               failures = projection.Budget.ConsecutiveFailureCount
               dedupeKeys = List.length projection.RecentFailureKeys
               exhausted = projection.Exhausted
               handle = box (ProjectionHandle projection) |}

    let private projectionOf (value: obj) : ProviderFailureProjection =
        let handle = field value "handle"

        if not (isNullish handle) then
            (unbox<ProjectionHandle> handle).Value
        else
            { LogicalRunId = LogicalRunId.create (text (field value "logicalRun"))
              AuthorityRootUserMessageId = AuthorityRootUserMessageId.create (text (field value "authorityRoot"))
              Budget = { ConsecutiveFailureCount = intValue (field value "failures") }
              RecentFailureKeys = []
              LastTransitionWasSuccess = false
              Exhausted =
                match field value "exhausted" with
                | value when isNullish value -> false
                | value -> unbox<bool> value }

    let private rejectionName (rejection: ProviderFailureAdvanceRejection) : string =
        match rejection with
        | ProviderFailureAdvanceRejection.AlreadyObserved -> "AlreadyObserved"
        | ProviderFailureAdvanceRejection.AlreadyExhausted -> "AlreadyExhausted"
        | ProviderFailureAdvanceRejection.DifferentRun -> "DifferentRun"
        | ProviderFailureAdvanceRejection.NoActiveBudget -> "NoActiveBudget"
        | ProviderFailureAdvanceRejection.InvalidTransition -> "InvalidTransition"

    let private applyFailure (identity: obj) (count: int) (current: obj) : obj =
        match ProviderFailureProjection.applyFailure (identityOf identity) count (projectionOf current) with
        | Ok projection ->
            box
                {| ok = true
                   value = projectionHandleView projection |}
        | Error rejection ->
            box
                {| ok = false
                   error = rejectionName rejection |}

    /// Pure provider failure budget API. Every method accepts/returns JSON values;
    /// only the identity key helper intentionally consumes an opaque semantic identity.
    let budget =
        box
            {| initial = budgetView ProviderFailureBudget.initial
               recordFailure = (fun value -> budgetOf value |> ProviderFailureBudget.recordFailure |> budgetView)
               recordSuccess = (fun value -> budgetOf value |> ProviderFailureBudget.recordSuccess |> budgetView)
               isValidRecord =
                (fun previousCount nextCount -> ProviderFailureBudget.isValidRecord previousCount nextCount)
               verdict =
                (fun budgetLimit value ->
                    match ProviderFailureBudget.verdict budgetLimit (budgetOf value) with
                    | ProviderFailureBudget.MayRetry _ -> "MayRetry"
                    | ProviderFailureBudget.Exhausted _ -> "Exhausted")
               defaultBudget = ProviderFailureBudget.DefaultBudget
               attemptIdentity =
                (fun session logicalRun authorityRoot providerRun ->
                    identityView
                        { SessionId = SessionId.create session
                          LogicalRunId = LogicalRunId.create logicalRun
                          AuthorityRootUserMessageId = AuthorityRootUserMessageId.create authorityRoot
                          ProviderRun = ProviderRunIdentity.create providerRun })
               dedupeKey = (fun value -> identityOf value |> FailedProviderAttemptIdentity.dedupeKey)
               read = (fun value -> budgetOf value |> budgetView) |}

    /// Durable provider failure projection API. The projection state itself is carried
    /// by an opaque handle so its bounded dedupe keys never become public JSON.
    let providerFailureProjection =
        box
            {| forAuthority =
                (fun logicalRun authorityRoot ->
                    ProviderFailureProjection.forAuthority
                        (LogicalRunId.create logicalRun)
                        (AuthorityRootUserMessageId.create authorityRoot)
                    |> projectionHandleView)
               applyFailure = (fun identity count current -> applyFailure identity count current)
               applyExhausted =
                (fun current ->
                    projectionOf current
                    |> ProviderFailureProjection.applyExhausted
                    |> projectionHandleView)
               recordSuccess =
                (fun current ->
                    projectionOf current
                    |> ProviderFailureProjection.recordSuccess
                    |> projectionHandleView)
               mayRetry =
                (fun budgetLimit current -> ProviderFailureProjection.mayRetry budgetLimit (projectionOf current))
               read = (fun current -> projectionOf current |> projectionView) |}

    let authorityRootAccepted (value: obj) : obj =
        let authorityKind = requiredText "authorityKind" (field value "authorityKind")
        let identitySeed = identitySeedOf (field value "identitySeed")

        match authorityKind, identitySeed with
        | "HumanRoot", PromptAuthority.IdentitySeed.RootSelection _
        | "AgentOwnerRoot", PromptAuthority.IdentitySeed.InheritedFromOwner _ -> ()
        | "HumanRoot", _ -> invalidArg "identitySeed" "HumanRoot requires a RootSelection identity seed"
        | "AgentOwnerRoot", _ -> invalidArg "identitySeed" "AgentOwnerRoot requires an inherited owner identity seed"
        | kind, _ -> invalidArg "authorityKind" $"unknown authority kind '{kind}'"

        box
            {| kind = "AuthorityRootAccepted"
               schemaVersion = 2
               session = text (field value "session")
               logicalRun = text (field value "logicalRun")
               authorityRoot = text (field value "authorityRoot")
               authorityKind = authorityKind
               identitySeed = identitySeedView identitySeed |}

    let providerFailureRecorded (value: obj) : obj =
        box
            {| kind = "FailureRecorded"
               session = text (field value "session")
               logicalRun = text (field value "logicalRun")
               authorityRoot = text (field value "authorityRoot")
               providerRun = text (field value "providerRun")
               consecutiveFailureCount = intValue (field value "consecutiveFailureCount")
               reason =
                match text (field value "reason") with
                | "" -> "provider_error"
                | value -> value |}

    let providerRetryExhausted (value: obj) : obj =
        box
            {| kind = "RetryExhausted"
               session = text (field value "session")
               logicalRun = text (field value "logicalRun")
               authorityRoot = text (field value "authorityRoot")
               finalConsecutiveFailureCount = intValue (field value "finalConsecutiveFailureCount") |}

    let providerSuccessRecorded (value: obj) : obj =
        box
            {| kind = "SuccessRecorded"
               session = text (field value "session")
               logicalRun = text (field value "logicalRun")
               authorityRoot = text (field value "authorityRoot")
               providerRun = text (field value "providerRun") |}

    let envelope (value: obj) : obj =
        box
            {| session = text (field value "session")
               seq = intValue (field value "seq")
               fact = field value "fact"
               providerRun = field value "providerRun" |}

    let private factOf (value: obj) : AgentFact =
        match text (field value "kind") with
        | "AuthorityRootAccepted" ->
            PromptFact.AuthorityRootAccepted
                { SchemaVersion = 2
                  SessionId = SessionId.create (text (field value "session"))
                  LogicalRunId = LogicalRunId.create (text (field value "logicalRun"))
                  AuthorityRootUserMessageId = AuthorityRootUserMessageId.create (text (field value "authorityRoot"))
                  AuthorityKind = text (field value "authorityKind")
                  IdentitySeed = identitySeedOf (field value "identitySeed") }
        | "FailureRecorded" ->
            Wanxiangshu.Composition.Durable.ProviderFailureFact.FailureRecorded
                {| SessionId = SessionId.create (text (field value "session"))
                   LogicalRunId = LogicalRunId.create (text (field value "logicalRun"))
                   AuthorityRootUserMessageId = AuthorityRootUserMessageId.create (text (field value "authorityRoot"))
                   ProviderRun = ProviderRunIdentity.create (text (field value "providerRun"))
                   ConsecutiveFailureCount = intValue (field value "consecutiveFailureCount")
                   Reason = text (field value "reason") |}
        | "RetryExhausted" ->
            Wanxiangshu.Composition.Durable.ProviderFailureFact.RetryExhausted
                {| SessionId = SessionId.create (text (field value "session"))
                   LogicalRunId = LogicalRunId.create (text (field value "logicalRun"))
                   AuthorityRootUserMessageId = AuthorityRootUserMessageId.create (text (field value "authorityRoot"))
                   FinalConsecutiveFailureCount = intValue (field value "finalConsecutiveFailureCount") |}
        | "SuccessRecorded" ->
            Wanxiangshu.Composition.Durable.ProviderFailureFact.SuccessRecorded
                {| SessionId = SessionId.create (text (field value "session"))
                   LogicalRunId = LogicalRunId.create (text (field value "logicalRun"))
                   AuthorityRootUserMessageId = AuthorityRootUserMessageId.create (text (field value "authorityRoot"))
                   ProviderRun = ProviderRunIdentity.create (text (field value "providerRun")) |}
        | other -> failwith $"ProviderFailureSurface: unsupported provider failure fact '{other}'"

    let private envelopeOf (value: obj) : Envelope =
        let session = SessionId.create (text (field value "session"))
        let sequence = int64 (intValue (field value "seq"))

        let providerRun =
            optionalText (field value "providerRun")
            |> Option.map ProviderRunIdentity.create

        { RuntimeId = RuntimeId.create "rt-provider-failure-surface"
          LocalSeq = LocalSeq.create sequence
          ObservedAt = Unchecked.defaultof<_>
          EventId = EventId.create ($"provider-failure-{sequence}")
          Stream = StreamId.Session session
          ProviderRun = providerRun
          Fact = Fact.Agent(factOf (field value "fact")) }

    let private providerFailureIn (projection: ProjectionSet) : obj =
        projection.AgentProjections.Sessions
        |> Map.toList
        |> List.tryPick (fun (_, session) -> session.ProviderFailures |> Option.map projectionHandleView)
        |> optionObj

    let private foldTyped (values: Envelope list) : Result<ProjectionSet, FoldRejection> =
        let rec loop current remaining =
            match remaining with
            | [] -> Ok current
            | envelope :: tail ->
                match Fold.foldEnvelope current envelope with
                | Ok next -> loop next tail
                | Error failure -> Error failure

        loop Fold.empty values

    let private foldResult (result: Result<ProjectionSet, FoldRejection>) : obj =
        match result with
        | Ok projection ->
            box
                {| ok = true
                   value = providerFailureIn projection |}
        | Error failure ->
            box
                {| ok = false
                   error =
                    box
                        {| Fact = failure.Fact
                           Reason = failure.Reason |} |}

    /// Fold provider failure owner envelopes through the production durable fold.
    let fold (values: obj array) : obj =
        values |> Array.toList |> List.map envelopeOf |> foldTyped |> foldResult

    let providerFailureFactCaseNames: string array =
        [| "FailureRecorded"; "RetryExhausted"; "SuccessRecorded" |]

    /// Open the first logical run through the existing PromptDispatcher owner.
    /// The JournalHandle is opaque to callers; only this failure boundary unwraps
    /// it for the production dispatcher.
    let acceptHumanRoot
        (handle: Wanxiangshu.Persistence.Journal.JournalHandle)
        (session: string)
        (physicalMessage: string)
        (agent: string)
        : Task<obj> =
        task {
            let runtime =
                PromptDispatcher.forPrompts (PromptJournalAdapter.create handle.Journal)

            let identitySeed =
                ParticipantIdentity.resolveAtRoot agent
                |> Result.map PromptAuthority.IdentitySeed.RootSelection
                |> Result.mapError (sprintf "invalid participant identity: %A")

            match identitySeed with
            | Error error -> return box {| ok = false; error = error |}
            | Ok seed ->
                let! result =
                    runtime.AcceptHumanRoot
                        (SessionId.create session)
                        (PhysicalUserMessageId.create physicalMessage)
                        (Some seed)

                return
                    match result with
                    | Ok _ -> box {| ok = true; error = "" |}
                    | Error error ->
                        box
                            {| ok = false
                               error = PromptDispatcher.describeHumanRootAcceptanceFailure error |}
        }

    let private outcomeName outcome =
        match outcome with
        | FailureAdmissionOutcome.RetryAuthorized -> "RetryAuthorized"
        | FailureAdmissionOutcome.RetryExhausted -> "RetryExhausted"
        | FailureAdmissionOutcome.EpisodeSuperseded -> "EpisodeSuperseded"
        | FailureAdmissionOutcome.NoActiveRun -> "NoActiveRun"

    let retryAttempt
        (input: obj)
        (current: obj)
        (admit: obj -> Task<string>)
        (redispatch: obj -> Task<unit>)
        : Task<string> =
        task {
            let decoded = Wanxiangshu.Execution.Failure.Surface.inputOf input
            let state = projectionOf current

            let authorizationView (authorization: ProviderRecoveryAuthorization) : obj =
                box
                    {| decisionId = authorization.DecisionId.Value
                       logicalRun = LogicalRunId.value authorization.LogicalRun
                       providerRun = ProviderRunIdentity.value authorization.ProviderRun
                       requestKind = ProviderRequestKind.label authorization.RequestKind |}

            let turn: ReconciledTurn =
                { SessionId = decoded.ExecutionKey.SessionId
                  PhysicalUserMessageId = decoded.ExecutionKey.PhysicalUserMessageId
                  AuthorityRootUserMessageId = state.AuthorityRootUserMessageId
                  ProviderRun = decoded.Provider.ProviderRun
                  Role = None
                  Directory = None
                  Parts = [||]
                  Finish = None
                  ErrorName = None
                  Model = None
                  Outcome = ReconcileProgram.TurnFailed "proof provider failure"
                  Observation = None }

            let ports: RetryPorts =
                { Admit =
                    fun authorization ->
                        task {
                            let! outcome = admit (authorizationView authorization)

                            return
                                match outcome with
                                | "RetryAuthorized" -> Ok FailureAdmissionOutcome.RetryAuthorized
                                | "RetryExhausted" -> Ok FailureAdmissionOutcome.RetryExhausted
                                | "EpisodeSuperseded" -> Ok FailureAdmissionOutcome.EpisodeSuperseded
                                | "NoActiveRun" -> Ok FailureAdmissionOutcome.NoActiveRun
                                | "Rejected" -> Error "proof admission rejected"
                                | other -> invalidArg "admit" $"unknown admission outcome '{other}'"
                        }
                  Redispatch =
                    fun authorization _ ->
                        task {
                            do! redispatch (authorizationView authorization)
                            return RetryVerdict.Dispatched
                        } }

            let! result =
                Retry.attempt
                    ports
                    { Turn = turn
                      Failure = decoded.Failure
                      OwnerSession = turn.SessionId
                      RequestKind = decoded.Provider.RequestKind
                      Current = state
                      Error = "proof provider failure" }

            return
                match result with
                | RetryVerdict.Dispatched -> "Dispatched"
                | RetryVerdict.Superseded -> "Superseded"
                | RetryVerdict.Terminal _ -> "Terminal"
        }

    /// Record one confirmed provider failure through the production ledger using
    /// an opaque JournalHandle. Only `ExecutionFailurePolicy` may licence the
    /// advance against the single `ProviderFailureBudget.DefaultBudget`, and no
    /// F# Result/DU crosses the boundary.
    let recordConfirmedFailure
        (handle: Wanxiangshu.Persistence.Journal.JournalHandle)
        (budget: int)
        (session: string)
        (providerRun: string)
        (reason: string)
        : Task<obj> =
        task {
            let sessionId = SessionId.create session
            let providerRunId = ProviderRunIdentity.create providerRun

            let! result =
                let failureState =
                    AgentProjection.tryFind sessionId (AgentJournal.snapshot handle.Journal).AgentProjections
                    |> Option.bind _.ProviderFailures

                match ProviderFailureEvidence.currentState failureState with
                | None -> Task.FromResult(Ok FailureAdmissionOutcome.NoActiveRun)
                | Some _ when budget <> ProviderFailureBudget.DefaultBudget ->
                    Task.FromResult(Error "provider failure budget must equal the declared default")
                | Some current ->
                    let providerFailureBudget =
                        if ProviderFailureProjection.mayRetry ProviderFailureBudget.DefaultBudget current then
                            ProviderRecoveryBudget.Available
                        else
                            ProviderRecoveryBudget.Exhausted

                    let decision =
                        ExecutionFailurePolicy.decide
                            { Failure = ExecutionFailure.ProviderTransient
                              Lifecycle = DurableExecutionLifecycle.ProviderStarted
                              ExecutionKey =
                                { SessionId = sessionId
                                  PhysicalUserMessageId = PhysicalUserMessageId.create ("proof-" + providerRun) }
                              Capacity = CapacityOwnership.NoCapacityFence
                              Provider =
                                { LogicalRun = current.LogicalRunId
                                  ProviderRun = providerRunId
                                  RequestKind = ProviderRequestKind.WorkMain
                                  RetryBudget = providerFailureBudget
                                  Breaker = ProviderBreakerState.Closed } }

                    match decision.Resolution with
                    | ExecutionFailureResolution.RetryFreshAttempt authorization ->
                        let port =
                            Wanxiangshu.Composition.Durable.AgentJournalPortAdapter.forProviderFailure handle.Journal

                        ProviderFailureLedger.recordAuthorizedFailure port sessionId authorization reason
                    | ExecutionFailureResolution.PreserveCurrentFact
                    | ExecutionFailureResolution.AwaitAcceptanceReconciliation _
                    | ExecutionFailureResolution.TerminalizeAcceptedPreProvider _
                    | ExecutionFailureResolution.TerminalizeProviderStarted _ ->
                        Task.FromResult(Ok FailureAdmissionOutcome.RetryExhausted)

            return
                match result with
                | Ok outcome ->
                    box
                        {| ok = true
                           outcome = outcomeName outcome |}
                | Error error -> box {| ok = false; error = error |}
        }

    /// provider-attempt-recovery-021 test seam: establish the exact durable `Accepted` +
    /// `ProviderStarted` facts for one physical request and provider run.
    let establishProviderRun
        (handle: Wanxiangshu.Persistence.Journal.JournalHandle)
        (session: string)
        (physicalMessage: string)
        (providerRun: string)
        : Task<obj> =
        task {
            let durable = handle.Journal

            let identity =
                ParticipantIdentity.resolveAtRoot "engineer"
                |> Result.defaultWith (fun error -> invalidArg "providerRun" $"invalid identity: {error}")

            let accepted: AcceptedChatExecutionEvidence =
                { SessionId = SessionId.create session
                  LogicalRunId = LogicalRunId.create $"run-{physicalMessage}"
                  AuthorityRootUserMessageId = AuthorityRootUserMessageId.create physicalMessage
                  AuthorityKind = PromptRootAuthorityKind.HumanRoot
                  IdentitySeed = PromptIdentitySeed.RootSelection identity
                  PhysicalUserMessageId = PhysicalUserMessageId.create physicalMessage
                  Origin = PromptOrigin.AuthorityRoot PromptRootAuthorityKind.HumanRoot }

            let key: ChatExecutionKey =
                { SessionId = accepted.SessionId
                  PhysicalUserMessageId = accepted.PhysicalUserMessageId }

            let! acceptance = ManagedChatAcceptance.accept durable key accepted

            match acceptance with
            | Error _ -> return box {| ok = false |}
            | Ok _ ->
                let! started =
                    ManagedChatProviderLifecycle.providerStarted
                        durable
                        key
                        accepted
                        (ProviderRunIdentity.create providerRun)
                        ProviderRequestKind.WorkMain

                return box {| ok = Result.isOk started |}
        }

    /// provider-attempt-recovery-023 test seam: establish only the durable `Accepted` fact for
    /// one physical request — the exact obligation shape clause 023 owns
    /// (`Accepted` without `ProviderStarted`).
    let establishAcceptedExecution
        (handle: Wanxiangshu.Persistence.Journal.JournalHandle)
        (session: string)
        (physicalMessage: string)
        : Task<obj> =
        task {
            let identity =
                ParticipantIdentity.resolveAtRoot "engineer"
                |> Result.defaultWith (fun error -> invalidArg "session" $"invalid identity: {error}")

            let accepted: AcceptedChatExecutionEvidence =
                { SessionId = SessionId.create session
                  LogicalRunId = LogicalRunId.create $"run-{physicalMessage}"
                  AuthorityRootUserMessageId = AuthorityRootUserMessageId.create physicalMessage
                  AuthorityKind = PromptRootAuthorityKind.HumanRoot
                  IdentitySeed = PromptIdentitySeed.RootSelection identity
                  PhysicalUserMessageId = PhysicalUserMessageId.create physicalMessage
                  Origin = PromptOrigin.AuthorityRoot PromptRootAuthorityKind.HumanRoot }

            let key: ChatExecutionKey =
                { SessionId = accepted.SessionId
                  PhysicalUserMessageId = accepted.PhysicalUserMessageId }

            let! acceptance = ManagedChatAcceptance.accept handle.Journal key accepted
            return box {| ok = Result.isOk acceptance |}
        }

    /// provider-attempt-recovery-003 test seam: the request kind a confirmed failure of this
    /// exact physical request continues with. Empty string when the durable
    /// evidence refuses to name one.
    let requestKindFor
        (handle: Wanxiangshu.Persistence.Journal.JournalHandle)
        (session: string)
        (physicalMessage: string)
        : string =
        ProviderRecoveryWorkflow.requestKindFor
            handle.Journal
            (SessionId.create session)
            (PhysicalUserMessageId.create physicalMessage)
        |> Option.map ProviderRequestKind.label
        |> Option.defaultValue ""

    /// Confirmed fact consumed by the only recovery step: this failure is itself
    /// the verbatim retry of the same provider. True only when that exact physical
    /// request was accepted as a ProviderRetryAttempt continuation AND the failed
    /// provider run established the request's durable ProviderStarted.
    /// Never derived from a failure ordinal or a clock.
    let wasSameProviderRetryAttempt
        (handle: Wanxiangshu.Persistence.Journal.JournalHandle)
        (session: string)
        (physicalMessage: string)
        (providerRun: string)
        : bool =
        ProviderRecoveryWorkflow.failureIsSameProviderRetry
            handle.Journal
            (SessionId.create session)
            (PhysicalUserMessageId.create physicalMessage)
            (ProviderRunIdentity.create providerRun)

    /// Read the durable provider failure budget for one session without exposing the
    /// projection record, map, or closed budget representation.
    let snapshot (handle: Wanxiangshu.Persistence.Journal.JournalHandle) (session: string) : obj =
        match
            let failureState =
                AgentProjection.tryFind
                    (SessionId.create session)
                    (AgentJournal.snapshot handle.Journal).AgentProjections
                |> Option.bind _.ProviderFailures in

            ProviderFailureEvidence.currentState failureState
        with
        | None -> null
        | Some current ->
            box
                {| failures = current.Budget.ConsecutiveFailureCount
                   exhausted = current.Exhausted |}
