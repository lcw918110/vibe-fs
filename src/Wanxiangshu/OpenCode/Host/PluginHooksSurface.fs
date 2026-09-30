namespace Wanxiangshu.OpenCode

open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Context.Companion
open Wanxiangshu.Context.Companion.Blogger
open Wanxiangshu.Context.Companion.Blogger.Runtime
open Wanxiangshu.Execution.Failure
open Wanxiangshu.Execution.Session.Attachment
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Interaction.Dispatch
open Wanxiangshu.OpenCode.Host
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Strength

module PluginHooksSurface =

    /// Opaque Host-owned observation for the Blogger adapter proof.
    type BloggerAdapterObservation private (first: string, second: string) =
        member internal _.First = first
        member internal _.Second = second

        static member internal Create(first, second) =
            BloggerAdapterObservation(first, second)

    let policyAwareHook operation (adaptedHook: obj) : obj =
        PluginHostInterop.policyAwareHook operation adaptedHook

    /// Classify a thrown JS value through the real failure membrane and expose
    /// the normalized outcome fields (failure kind, lifecycle, settlement
    /// evidence, whether hook args proved an owned execution).
    let normalizeHookFailureOutcome (args: obj) (context: obj) (error: obj) : obj =
        let outcome = PluginHostInterop.normalizeHookFailure args context error

        box
            {| failure = sprintf "%A" outcome.Failure
               lifecycle = sprintf "%A" outcome.Lifecycle
               settlement = sprintf "%A" outcome.Settlement
               hasExecutionKey = outcome.ExecutionKey.IsSome |}

    let decorateReviewToolDefinition (toolID: string) (definition: obj) : unit =
        ManagerReviewContract.decorateDefinition (box {| toolID = toolID |}) definition

    let hookFailurePolicy failure settlement : string =
        let typedFailure =
            match failure with
            | "LocalInvariant" -> ExecutionFailure.LocalInvariant
            | "ProtocolRejection" -> ExecutionFailure.ProtocolRejection
            | "UserCancelled" -> ExecutionFailure.UserCancelled
            | "Superseded" -> ExecutionFailure.Superseded
            | "CapacityQueueFull" -> ExecutionFailure.CapacityQueueFull
            | "AcceptanceUnknown" -> ExecutionFailure.AcceptanceUnknown
            | "PersistenceNotCommitted" -> ExecutionFailure.PersistenceFailure PersistenceCommitment.NotCommitted
            | "PersistenceCommitted" -> ExecutionFailure.PersistenceFailure PersistenceCommitment.Committed
            | "PersistenceUnknown" -> ExecutionFailure.PersistenceFailure PersistenceCommitment.Unknown
            | other -> invalidArg "failure" $"unknown hook proof failure '{other}'"

        let settlementEvidence =
            match settlement with
            | "NoOwnedExecution" -> PluginHostInterop.HookSettlementEvidence.NoOwnedExecution
            | "ExactSettlementComplete" -> PluginHostInterop.HookSettlementEvidence.ExactSettlementComplete
            | "DurableOutcomeUnknown" -> PluginHostInterop.HookSettlementEvidence.DurableOutcomeUnknown
            | "SettlementIncomplete" -> PluginHostInterop.HookSettlementEvidence.SettlementIncomplete
            | other -> invalidArg "settlement" $"unknown hook proof settlement '{other}'"

        let lifecycle =
            match settlementEvidence with
            | PluginHostInterop.HookSettlementEvidence.NoOwnedExecution -> DurableExecutionLifecycle.NoAcceptedFact
            | PluginHostInterop.HookSettlementEvidence.ExactSettlementComplete -> DurableExecutionLifecycle.Terminal
            | PluginHostInterop.HookSettlementEvidence.DurableOutcomeUnknown
            | PluginHostInterop.HookSettlementEvidence.SettlementIncomplete ->
                DurableExecutionLifecycle.AcceptedBeforeProvider

        let outcome: PluginHostInterop.HookFailureOutcome =
            { Failure = typedFailure
              Lifecycle = lifecycle
              ExecutionKey = None
              Settlement = settlementEvidence }

        match PluginHostInterop.interpretHookFailure outcome with
        | PluginHostInterop.HookFailurePolicy.RethrowUnchanged -> "RethrowUnchanged"
        | PluginHostInterop.HookFailurePolicy.FatalAfterSettlement -> "FatalAfterSettlement"
        | PluginHostInterop.HookFailurePolicy.RejectFatalBeforeSettlement -> "RejectFatalBeforeSettlement"

    let private effectLabel =
        function
        | BloggerCoordinator.DecisionEffect.Started -> "Started"
        | BloggerCoordinator.DecisionEffect.StartedSquash -> "StartedSquash"
        | BloggerCoordinator.DecisionEffect.OfferedParked -> "OfferedParked"
        | BloggerCoordinator.DecisionEffect.NoMaterial -> "NoMaterial"
        | BloggerCoordinator.DecisionEffect.SkippedInFlight -> "SkippedInFlight"
        | BloggerCoordinator.DecisionEffect.Sealed -> "Sealed"
        | BloggerCoordinator.DecisionEffect.StartFailed reason -> "StartFailed:" + reason
        | BloggerCoordinator.DecisionEffect.MaterializeFailed reason -> "MaterializeFailed:" + reason

    /// Real Coordinator -> CompanionHost -> PromptDispatcher Host adapter. The
    /// same frozen context is offered twice while one physical flight remains
    /// unresolved, proving the second decision stops before Host submission.
    let coordinateBloggerUnresolvedTwice
        (port: obj)
        (handle: JournalHandle)
        (mainSession: string)
        (bloggerSession: string)
        (requestId: string)
        : Task<BloggerAdapterObservation> =
        task {
            let scope = new PluginRuntimeScope(None)
            let durable = AgentJournalCompanionPort handle.Journal :> ICompanionDurablePort
            let sessionPort = DispatchSurface.sessionPort port
            let satellites = SatelliteRuntime(sessionPort)

            let host =
                new CompanionHost(
                    SessionId.create mainSession,
                    sessionPort,
                    durable = durable,
                    restoredBloggerId = bloggerSession,
                    journal = handle.Journal,
                    satelliteRuntime = satellites
                )

            let context =
                BloggerRequestContext.Squash(
                    match
                        BloggerRequestMaterial.createSquash
                            { RequestId =
                                BloggerRequestContext.squashRequestId
                                    (SessionId.create mainSession)
                                    (SessionId.create bloggerSession)
                                    (FrameEpochId.create 1L)
                                    1
                                    [ BlobDigest.create "blogger-effect-frame" ]
                              MainSessionId = SessionId.create mainSession
                              BloggerSessionId = SessionId.create bloggerSession
                              FrameEpochId = FrameEpochId.create 1L
                              CoveredFrameCount = 1
                              FrameDigests = [ BlobDigest.create "blogger-effect-frame" ]
                              ObservedPrefixEpochId = PrefixEpochId.create 1L }
                    with
                    | Ok verified -> verified
                    | Error rejection ->
                        invalidOp (
                            sprintf "coordinateBloggerUnresolvedTwice staged an unverifiable context: %A" rejection
                        )
                )

            let! first =
                CompanionTransform.coordinateBloggerContext
                    satellites
                    scope.BloggerRuntimeHost
                    host
                    (Some handle.Journal)
                    context

            let! second =
                CompanionTransform.coordinateBloggerContext
                    satellites
                    scope.BloggerRuntimeHost
                    host
                    (Some handle.Journal)
                    context

            return BloggerAdapterObservation.Create(effectLabel first, effectLabel second)
        }

    let firstBloggerEffect (observation: BloggerAdapterObservation) = observation.First

    let secondBloggerEffect (observation: BloggerAdapterObservation) = observation.Second

    /// DELEGATE.md 4.2: run the real read-only delegation schema decoration for
    /// one tool id. Production gates this behind the Predictor configuration
    /// existence query; this surface entry calls the same contract function so
    /// the schema contract, idempotence, conflict rejection and bilingual
    /// prose can be proven without asserting on production enablement.
    let decorateReadonlyDelegationToolDefinition (toolID: string) (definition: obj) : unit =
        ReadonlyDelegationContract.decorateDefinition (box {| toolID = toolID |}) definition

    /// DELEGATE.md 3.2: JS-boundary budget validation as a JS-native result:
    /// { ok = true; rounds = <int> } or { ok = false; error = <code> }.
    let readonlyDelegationBudgetOf (value: obj) : obj =
        match ReadonlyDelegationContract.tryReadonlyRoundBudget value with
        | Ok budget ->
            box
                {| ok = true
                   rounds = ReadonlyRoundBudget.value budget |}
        | Error message -> box {| ok = false; error = message |}

    [<Emit("Object.prototype.hasOwnProperty.call($0, $1)")>]
    let private hasOwn (target: obj) (key: string) : bool = jsNative

    /// DELEGATE_REVISE.md 7.1/7.2: self_note validation as a JS-native result:
    /// { ok = true; note = <string|null> } or { ok = false; error = <code> }.
    let readonlyDelegationSelfNoteOf (arguments: obj) : obj =
        match InvestigationEstimateContract.parseParticipatingArguments arguments with
        | Ok(rounds, _) ->
            let rawRounds = InvestigationEstimateContract.EstimatedReadonlyRounds.value rounds

            let noteVal =
                if rawRounds = 0 then null
                elif hasOwn arguments "self_note" then arguments?self_note
                else null

            box {| ok = true; note = noteVal |}
        | Error err ->
            box
                {| ok = false
                   error = InvestigationEstimateContract.errorCode err |}

    /// Production tool.execute.before calls the same hide: the business
    /// argument view drops both protocol fields while provider evidence keeps
    /// the saved descriptors under a private Symbol.
    let hideReadonlyDelegationArgs (args: obj) : unit = ReadonlyDelegationContract.hide args

    /// Production tool.execute.after calls the same restore: original
    /// property descriptors return to the args object; no-op when absent.
    let restoreReadonlyDelegationArgs (args: obj) : unit = ReadonlyDelegationContract.restore args
