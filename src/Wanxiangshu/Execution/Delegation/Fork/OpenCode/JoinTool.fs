namespace Wanxiangshu.Execution.Delegation.Fork.OpenCode

open System
open System.Threading.Tasks
open Fable.Core.JsInterop
open Wanxiangshu.Host
open Wanxiangshu.Execution.Delegation
open Wanxiangshu.Execution.Delegation.Handle
open Wanxiangshu.Execution.Delegation.Fork
open Wanxiangshu.Execution.Delegation.Fork.Host
open Wanxiangshu.Change
open Wanxiangshu.Execution.Fission
open Wanxiangshu.Execution.Session
open Wanxiangshu.Execution.Session.Recovery
open Wanxiangshu.Execution.Session.Recovery.SessionRecovery
open Wanxiangshu.Execution.Session.Wait
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.OpenCode
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Process

/// join() waits for the owning runtime's next physical completion batch.
/// Orchestrator join routes to ManagerJob verdict mailbox by authority role.
/// Process-local join admission: FamilyReady permit → HostForkJoin.joinAvailableWithPermit (no bare Join, no AST).
/// EXEC-017: tool abort → JoinInterrupt.Signal only (≠ runtime.Cancel).
/// DevOps join: 10s timeout budget (NodeTiming.timerTask 10000). Orch/Manager join remains untimed.
///
/// The decisions this tool makes live in `JoinRun`; this file binds them to the
/// OpenCode host (runtime, journal, mailbox, clock) and renders the wire.
module JoinTool =

    [<RequireQualifiedAccess>]
    module Path =
        [<Literal>]
        let Description = "tool/join/description"

        [<Literal>]
        let RecoveryBlocked = "tool/join/recovery-blocked"

        [<Literal>]
        let RecoveryWaiting = "tool/join/recovery-waiting"

        [<Literal>]
        let UnavailableUntilAuthority = "tool/join/unavailable-until-authority"

        [<Literal>]
        let OrchestratorNotReady = "tool/join/orchestrator-not-ready"

        [<Literal>]
        let UnavailableFromContext = "tool/join/unavailable-from-context"

    let private lang (ctx: HostToolContext) =
        ProviderLanguageBinding.forSessionText ctx.SessionId

    let private consequence lines =
        ToolResultText.tomlObjectWithInstructions lines []

    let private recoveryBlocked language (_blocks: NonEmpty<RecoveryBlock>) =
        consequence (ProviderProse.instructionLines language Path.RecoveryBlocked Map.empty)

    let private renderOrchestratorOutcome language outcome =
        match outcome with
        | Error _ -> consequence (ProviderProse.instructionLines language Path.OrchestratorNotReady Map.empty)
        | Ok(Interrupted reason) ->
            JoinResultRenderer.renderInterrupted language (JoinRun.normalizeInterruptReason reason)
        | Ok(ResultsAvailable batch) -> JoinResultRenderer.renderOrchestratorBatch language batch

    let private executeOrchestratorJoin
        (scope: ToolRuntimeScope)
        (context: HostToolContext)
        language
        (sessionId: SessionId)
        (attempt: obj)
        =
        task {
            let joinDescriptor =
                DiagnosticWait.create
                    "orchestrator-join"
                    (CausalOwner.create "OrchestratorWorkflow" [ "session", SessionId.value sessionId ])
                    [ "session", SessionId.value sessionId; "tool", "join" ]
                    (WorkflowProducer(CausalOwner.create "ManagerWorkflow" []))
                    [ WaitEscape.CancelledBy(CausalOwner.create "JoinAttempt" [ "session", SessionId.value sessionId ])
                      WaitEscape.SessionLifetime ]
                    "JoinTool.Orchestrator.JoinPublishedAvailable"

            let host = scope.OrchestratorHostFor context.SessionId

            let! outcome =
                CausalAwait.awaitTask
                    scope.WaitObserver
                    joinDescriptor
                    (host.JoinPublishedAvailable(JoinBatch.Max, attempt?Wait))

            return renderOrchestratorOutcome language outcome
        }

    let private devopsOrPlainWait (appliesBudget: bool) (attemptWait: Task<JoinInterruptReason>) =
        if appliesBudget then
            let timerTask = NodeTiming.timerTask JoinRun.DevOpsJoinTimeoutMs

            emitJsExpr
                (attemptWait, emitJsExpr timerTask "$0.then(function(){return'DeadlineExpired';})")
                "Promise.race([$0,$1])"
        else
            attemptWait

    let private joinTaskForMembership
        (runtime: HostForkRuntime)
        (permit: FamilyRecoveryPermit)
        (waitTask: Task<JoinInterruptReason>)
        (lane: JoinRun.JoinLane)
        =
        match lane with
        | JoinRun.FissionLane(groupId, laneIndex) ->
            HostForkJoin.joinAvailableForFissionLane runtime groupId laneIndex JoinBatch.Max waitTask
        | JoinRun.OwnerLane -> HostForkJoin.joinAvailableWithPermit runtime permit JoinBatch.Max waitTask

    /// The durable and live identity evidence the join rules read.
    /// A restarted process may only know the child from its durable handle; the
    /// handle's target agent is the same evidence the process tables carry.
    let private identityFor
        (scope: ToolRuntimeScope)
        (root: SessionId)
        (runtime: HostForkRuntime)
        : JoinRun.AgentIdentity =
        let durableHandle agentId =
            scope.Journal
            |> Option.bind (fun journal ->
                AgentJournal.handleProjection journal root
                |> HandleProjection.tryFind (HandleController.agentHandle agentId))

        let liveAgent agentId = runtime.TryFindAgentOrAdopt agentId

        { DurableByname = (fun agentId -> durableHandle agentId |> Option.map (fun handle -> handle.Byname))
          LiveAgentName = (fun agentId -> liveAgent agentId |> Option.map (fun (_, _, agent) -> agent))
          DurableRole = (fun agentId -> durableHandle agentId |> Option.map (fun handle -> handle.CanonicalRole))
          RuntimeRole = (fun agentId -> liveAgent agentId |> Option.map (fun (_, role, _) -> role)) }

    let private resolveTerminalLabel (runtime: HostForkRuntime) (ptyId: string) =
        let _, ptys = runtime.List()

        let command =
            ptys
            |> List.tryFind (fun record -> record.PtyId = ptyId)
            |> Option.map (fun record -> record.Command)

        JoinRun.resolveTerminalLabel command (runtime.TryTerminalNameByPtyId ptyId)

    let private releasePtyTracks (runtime: HostForkRuntime) (items: JoinItem list) =
        items |> JoinRun.ptyTrackIds |> List.iter (runtime.UntrackPtyRun)

    let private bufferRemainingItems (runtime: HostForkRuntime) (remaining: JoinItem list) =
        match List.isEmpty remaining with
        | true -> ()
        | false -> runtime.EnqueueBufferedJoinItems remaining

    let private renderResultsAvailable
        language
        (runtime: HostForkRuntime)
        (resolveName: string -> string)
        (resolveLabel: string -> string)
        (batch: NonEmptyBatch<JoinItem>)
        : string =
        let rendered, remaining =
            JoinResultRenderer.renderJoinItemBatchWithWindow language resolveName batch resolveLabel

        let window = JoinRun.windowOf batch remaining

        releasePtyTracks runtime window.Delivered
        bufferRemainingItems runtime window.Remaining
        rendered

    let private renderJoined
        language
        (runtime: HostForkRuntime)
        (resolveName: string -> string)
        (resolveLabel: string -> string)
        (joined: Result<JoinWaitOutcome<JoinItem>, ForkError>)
        =
        match joined with
        | Ok(Interrupted reason) ->
            JoinResultRenderer.renderInterrupted language (JoinRun.normalizeInterruptReason reason)
        | Ok(ResultsAvailable batch) -> renderResultsAvailable language runtime resolveName resolveLabel batch
        | Error joinError -> JoinResultRenderer.renderForkError language joinError resolveName

    let private executeAgentWithRuntime
        (scope: ToolRuntimeScope)
        (context: HostToolContext)
        language
        (sessionId: SessionId)
        (attempt: obj)
        (permit: FamilyRecoveryPermit)
        (runtime: HostForkRuntime)
        =
        task {
            let waitTask =
                devopsOrPlainWait (JoinRun.appliesWaitBudget (scope.RoleFor context)) attempt?Wait

            let joinDescriptor =
                DiagnosticWait.create
                    "agent-join"
                    (CausalOwner.create "JoinTool" [ "session", SessionId.value sessionId ])
                    [ "session", SessionId.value sessionId; "tool", "join" ]
                    (ExternalProducer("child-completion", [ "session", SessionId.value sessionId ]))
                    [ WaitEscape.CancelledBy(CausalOwner.create "JoinAttempt" [ "session", SessionId.value sessionId ])
                      WaitEscape.SessionLifetime ]
                    "JoinTool.joinAvailable"

            // Only process-local Fission bindings are active. A durable Open
            // group from a previous process is a broken tool record, not an
            // implicit lane to resume.
            let lane =
                FissionRuntime.tryLane sessionId
                |> Option.map (fun binding -> binding.GroupId, binding.LaneIndex)
                |> JoinRun.decideLane

            let joinTask = joinTaskForMembership runtime permit waitTask lane
            let! joined = CausalAwait.awaitTask scope.WaitObserver joinDescriptor joinTask
            let root = scope.LogicalOwnerFor sessionId
            let identity = identityFor scope root runtime

            return
                renderJoined language runtime (JoinRun.resolveAgentName identity) (resolveTerminalLabel runtime) joined
        }

    let private executeAgentJoin
        (scope: ToolRuntimeScope)
        (context: HostToolContext)
        language
        (sessionId: SessionId)
        (attempt: obj)
        (permit: FamilyRecoveryPermit)
        =
        task {
            match scope.RuntimeFor context with
            | Error _ ->
                return consequence (ProviderProse.instructionLines language Path.UnavailableFromContext Map.empty)
            | Ok runtime -> return! executeAgentWithRuntime scope context language sessionId attempt permit runtime
        }

    let private executeWhenReady
        (scope: ToolRuntimeScope)
        (context: HostToolContext)
        language
        (sessionId: SessionId)
        (attempt: obj)
        (permit: FamilyRecoveryPermit)
        =
        // NEVER Cancel mailbox/runtime on user wake (EXEC-017); the attempt's
        // Wait is the only interrupt channel. Completion still beats interrupt.
        match JoinRun.decideRoute (scope.RoleFor context) with
        | JoinRun.OrchestratorRoute -> executeOrchestratorJoin scope context language sessionId attempt
        | JoinRun.AgentRoute -> executeAgentJoin scope context language sessionId attempt permit

    let private executeAfterSession
        (scope: ToolRuntimeScope)
        (context: HostToolContext)
        language
        (sessionId: SessionId)
        =
        task {
            // EXEC-017: Begin the attempt first — before RequireCurrentProcessJoin and
            // before the mailbox wait — so a user-message signal that lands while
            // join admission or setup is still running is recorded on THIS attempt's own
            // TCS. There is no session-level future latch; Dispose unregisters.
            let attempt: obj = scope.JoinAttempts?Begin(sessionId, context.ToolCallId)
            let detachAbort = context.AttachAbort (fun () -> attempt?SignalOperatorAbort() |> ignore)

            use _attempt =
                { new IDisposable with
                    member _.Dispose() = attempt?Dispose() |> ignore }

            use _cleanup =
                { new IDisposable with
                    member _.Dispose() = detachAbort () }

            let root = scope.LogicalOwnerFor sessionId
            let! admission = scope.RequireCurrentProcessJoin root

            match admission with
            | FamilyRecovery.FamilyBlocked blocks -> return recoveryBlocked language blocks
            | FamilyRecovery.FamilyWaiting _ ->
                // Process-local join admission: no permit while waiting — must not drain
                // completions via a permit-gated join. Surface retryable RECOVERY_WAITING
                // so Manager re-invokes join after the next admission advances to Ready
                // or Blocked. Waiting stays non-effectful and retryable.
                return consequence (ProviderProse.instructionLines language Path.RecoveryWaiting Map.empty)
            | FamilyRecovery.FamilyReady permit ->
                return! executeWhenReady scope context language sessionId attempt permit
        }

    let private execute (scope: ToolRuntimeScope) (_args: HostToolArguments) (context: HostToolContext) =
        task {
            let language = lang context

            if String.IsNullOrWhiteSpace context.SessionId then
                return consequence (ProviderProse.instructionLines language Path.UnavailableUntilAuthority Map.empty)
            else
                return! executeAfterSession scope context language (SessionId.create context.SessionId)
        }


    let spec scope =
        { Name = "join"
          Description =
            ProviderProse.render (ProviderLanguageBinding.readGlobalPreference ()) Path.Description Map.empty
          Arguments = []
          Execute = execute scope }
