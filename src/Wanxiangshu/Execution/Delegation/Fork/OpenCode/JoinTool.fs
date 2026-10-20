namespace Wanxiangshu.Execution.Delegation.Fork.OpenCode

open System
open System.Threading.Tasks
open Fable.Core.JsInterop
open Wanxiangshu.Execution.Delegation
open Wanxiangshu.Execution.Delegation.Fork
open Wanxiangshu.Execution.Delegation.Fork.Host
open Wanxiangshu.Execution.Delegation.Handle
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
module JoinTool =

    [<Literal>]
    let DevOpsJoinTimeoutMs = 10_000

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

    type JoinRecord =
        { Name: string
          AgentId: string
          Role: string
          Status: string
          View: string }

    let private lang (ctx: HostToolContext) =
        ProviderLanguageBinding.forSessionText ctx.SessionId

    let private consequence lines =
        ToolHostCodec.tomlObjectWithInstructions lines []

    let private recoveryBlocked language (_blocks: NonEmpty<RecoveryBlock>) =
        consequence (ProviderProse.instructionLines language Path.RecoveryBlocked Map.empty)

    let private renderOrchestratorOutcome language outcome =
        match outcome with
        | Error _ -> consequence (ProviderProse.instructionLines language Path.OrchestratorNotReady Map.empty)
        | Ok(Interrupted reason) -> JoinResultRenderer.renderInterrupted language reason
        | Ok(ResultsAvailable batch) -> JoinResultRenderer.renderOrchestratorBatch language batch

    let private executeOrchestratorJoin
        (scope: ToolRuntimeScope)
        (context: HostToolContext)
        language
        (sessionId: SessionId)
        (attempt: JoinAttemptLease)
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
                    (host.JoinPublishedAvailable(JoinBatch.Max, attempt.Wait))

            return renderOrchestratorOutcome language outcome
        }

    let private devopsOrPlainWait (isDevOps: bool) (attemptWait: Task<JoinInterruptReason>) =
        if isDevOps then
            let timerTask = NodeTiming.timerTask DevOpsJoinTimeoutMs

            emitJsExpr
                (attemptWait, emitJsExpr timerTask "$0.then(function(){return'DeadlineExpired';})")
                "Promise.race([$0,$1])"
        else
            attemptWait

    let private joinTaskForMembership
        (runtime: HostForkRuntime)
        (permit: FamilyRecoveryPermit)
        (waitTask: Task<JoinInterruptReason>)
        (fissionMembership: (string * int) option)
        =
        match fissionMembership with
        | Some(groupId, laneIndex) ->
            HostForkJoin.joinAvailableForFissionLane runtime groupId laneIndex JoinBatch.Max waitTask
        | None -> HostForkJoin.joinAvailableWithPermit runtime permit JoinBatch.Max waitTask

    /// A restarted process may only know the child from its durable handle; the
    /// handle's target agent is the same evidence the process tables carry.
    let private liveAgentName (runtime: HostForkRuntime) (agentId: string) =
        match runtime.TryFindAgentOrAdopt agentId with
        | Some(_, _, agent) -> agent
        | None -> ""

    let private resolveAgentName
        (scope: ToolRuntimeScope)
        (root: SessionId)
        (runtime: HostForkRuntime)
        (agentId: string)
        =
        let durableByname =
            scope.Journal
            |> Option.bind (fun journal ->
                AgentJournal.handleProjection journal root
                |> HandleProjection.tryFind (HandleController.agentHandle agentId))
            |> Option.map (fun handle -> handle.Byname)
            |> Option.filter (String.IsNullOrWhiteSpace >> not)

        match durableByname with
        | Some byname -> byname.Trim()
        | None -> liveAgentName runtime agentId

    let private terminalNameFromMap (runtime: HostForkRuntime) (ptyId: string) =
        runtime.TryTerminalNameByPtyId ptyId |> Option.defaultValue ""

    let private resolveTerminalLabel (runtime: HostForkRuntime) (ptyId: string) =
        let _, ptys = runtime.List()

        match ptys |> List.tryFind (fun record -> record.PtyId = ptyId) with
        | Some record when not (String.IsNullOrWhiteSpace record.Command) -> record.Command.Trim()
        | _ -> terminalNameFromMap runtime ptyId

    let private renderInterruptedReason language (reason: JoinInterruptReason) =
        match reason with
        | JoinInterruptReason.OperatorAbort
        | JoinInterruptReason.UserMessageArrived -> JoinResultRenderer.renderInterrupted language reason
        | JoinInterruptReason.DeadlineExpired ->
            JoinResultRenderer.renderInterrupted language JoinInterruptReason.DeadlineExpired

    let private untrackJoinItem (runtime: HostForkRuntime) (item: JoinItem) =
        match item with
        | JoinItem.PtyItem ptyItem -> runtime.UntrackPtyRun(PtyJoinItem.ptyId ptyItem)
        | JoinItem.AgentItem _ -> ()

    let private releasePtyTracks (runtime: HostForkRuntime) (batch: NonEmptyBatch<JoinItem>) =
        NonEmptyBatch.toList batch |> List.iter (untrackJoinItem runtime)

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

        let deliveredCount = NonEmptyBatch.length batch - List.length remaining

        let deliveredItems =
            NonEmptyBatch.toList batch
            |> List.take deliveredCount
            |> NonEmptyBatch.tryOfList

        deliveredItems |> Option.iter (releasePtyTracks runtime)
        bufferRemainingItems runtime remaining
        rendered

    let private renderJoined
        language
        (runtime: HostForkRuntime)
        (resolveName: string -> string)
        (resolveLabel: string -> string)
        (joined: Result<JoinWaitOutcome<JoinItem>, ForkError>)
        =
        match joined with
        | Ok(Interrupted reason) -> renderInterruptedReason language reason
        | Ok(ResultsAvailable batch) -> renderResultsAvailable language runtime resolveName resolveLabel batch
        | Error joinError -> JoinResultRenderer.renderForkError language joinError resolveName

    let private executeAgentWithRuntime
        (scope: ToolRuntimeScope)
        (context: HostToolContext)
        language
        (sessionId: SessionId)
        (attempt: JoinAttemptLease)
        (permit: FamilyRecoveryPermit)
        (runtime: HostForkRuntime)
        =
        task {
            let isDevOps = scope.IsRole(context, Role.DevOps)
            let waitTask = devopsOrPlainWait isDevOps attempt.Wait

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
            let fissionMembership =
                FissionRuntime.tryLane sessionId
                |> Option.map (fun binding -> binding.GroupId, binding.LaneIndex)

            let joinTask = joinTaskForMembership runtime permit waitTask fissionMembership
            let! joined = CausalAwait.awaitTask scope.WaitObserver joinDescriptor joinTask
            let root = scope.LogicalOwnerFor sessionId

            return
                renderJoined
                    language
                    runtime
                    (resolveAgentName scope root runtime)
                    (resolveTerminalLabel runtime)
                    joined
        }

    let private executeAgentJoin
        (scope: ToolRuntimeScope)
        (context: HostToolContext)
        language
        (sessionId: SessionId)
        (attempt: JoinAttemptLease)
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
        (attempt: JoinAttemptLease)
        (permit: FamilyRecoveryPermit)
        =
        // NEVER Cancel mailbox/runtime on user wake (EXEC-017); the attempt's
        // Wait is the only interrupt channel. Completion still beats interrupt.
        if scope.IsRole(context, Role.Orchestrator) then
            executeOrchestratorJoin scope context language sessionId attempt
        else
            executeAgentJoin scope context language sessionId attempt permit

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
            let attempt = scope.JoinAttempts.Begin(sessionId, context.ToolCallId)
            let detachAbort = context.AttachAbort attempt.SignalOperatorAbort

            use _attempt = attempt

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

    let admission: ToolAdmission =
        ToolAdmission.OfficeRole(fun _ r -> OfficeCapability.isAllowed r ToolPermission.Join)

    let spec scope =
        { Name = "join"
          Description =
            ProviderProse.render (ProviderLanguageBinding.readGlobalPreference ()) Path.Description Map.empty
          Arguments = []
          Admission = admission
          Execute = execute scope }

    let private findDurableAgentRole (scope: ToolRuntimeScope) (root: SessionId) (agentId: string) : string option =
        scope.Journal
        |> Option.bind (fun journal ->
            AgentJournal.handleProjection journal root
            |> HandleProjection.tryFind (HandleController.agentHandle agentId))
        |> Option.map (fun handle -> Roles.roleLabel handle.CanonicalRole)

    let private findRuntimeAgentRole (runtime: HostForkRuntime) (agentId: string) : string option =
        match runtime.TryFindAgentOrAdopt agentId with
        | Some(_, role, _) -> Some(Roles.roleLabel role)
        | None -> None

    let private resolveAgentRole
        (scope: ToolRuntimeScope)
        (root: SessionId)
        (runtime: HostForkRuntime)
        (agentId: string)
        (fallback: Role option)
        : string =
        match fallback with
        | Some role -> Roles.roleLabel role
        | None ->
            findDurableAgentRole scope root agentId
            |> Option.orElseWith (fun () -> findRuntimeAgentRole runtime agentId)
            |> Option.defaultValue ""

    let private decideCompletedStatus (view: string) =
        if String.IsNullOrWhiteSpace view then
            "undelivered"
        else
            "completed"

    let private mapJoinItem
        (resolveName: string -> string)
        (resolveRole: string -> Role option -> string)
        (resolveLabel: string -> string)
        (item: JoinItem)
        : JoinRecord =
        match item with
        | JoinItem.AgentItem(AgentCompletedItem payload) ->
            let view = if isNull payload.View then "" else payload.View

            { Name = resolveName payload.AgentId
              AgentId = payload.AgentId
              Role = Roles.roleLabel payload.Role
              Status = decideCompletedStatus view
              View = view }
        | JoinItem.AgentItem(AgentFailedItem payload) ->
            { Name = resolveName payload.AgentId
              AgentId = payload.AgentId
              Role = resolveRole payload.AgentId payload.Role
              Status = "failed"
              View = if isNull payload.Message then "" else payload.Message }
        | JoinItem.AgentItem(AgentAbandonedItem(agentId, _)) ->
            { Name = resolveName agentId
              AgentId = agentId
              Role = resolveRole agentId None
              Status = "abandoned"
              View = "" }
        | JoinItem.PtyItem(PtyExited payload) ->
            { Name = resolveLabel payload.PtyId
              AgentId = payload.PtyId
              Role = Roles.roleLabel Role.DevOps
              Status = "completed"
              View = if isNull payload.Outcome then "" else payload.Outcome }
        | JoinItem.PtyItem(PtyFailed payload) ->
            { Name = resolveLabel payload.PtyId
              AgentId = payload.PtyId
              Role = Roles.roleLabel Role.DevOps
              Status = "failed"
              View = if isNull payload.Message then "" else payload.Message }
        | JoinItem.PtyItem(PtyAborted payload) ->
            { Name = resolveLabel payload.PtyId
              AgentId = payload.PtyId
              Role = Roles.roleLabel Role.DevOps
              Status = "abandoned"
              View = if isNull payload.Message then "" else payload.Message }

    let private mapResultsAvailable
        language
        (runtime: HostForkRuntime)
        (resolveName: string -> string)
        (resolveRole: string -> Role option -> string)
        (resolveLabel: string -> string)
        (batch: NonEmptyBatch<JoinItem>)
        : JoinRecord list =
        let _, remaining =
            JoinResultRenderer.renderJoinItemBatchWithWindow language resolveName batch resolveLabel

        let deliveredCount = NonEmptyBatch.length batch - List.length remaining

        let deliveredList = NonEmptyBatch.toList batch |> List.take deliveredCount

        deliveredList
        |> NonEmptyBatch.tryOfList
        |> Option.iter (releasePtyTracks runtime)

        bufferRemainingItems runtime remaining
        deliveredList |> List.map (mapJoinItem resolveName resolveRole resolveLabel)

    let private mapForkError (error: ForkError) : string =
        match error with
        | ForkError.NothingToJoin
        | ForkError.Empty -> "nothing-to-join"
        | ForkError.JoinInProgress -> "join-in-progress"
        | ForkError.Cancelled
        | ForkError.TimedOut -> "interrupted"
        | ForkError.Abandoned _ -> "abandoned"
        | ForkError.NotFound _
        | ForkError.TerminalMaterializationFailed _ -> "not-ready"

    let private resolveManagerJobName (scope: ToolRuntimeScope) (jobId: ManagerJobId) : string =
        scope.Journal
        |> Option.bind (fun journal ->
            (AgentJournal.snapshot journal).AgentProjections.Orchestrator
            |> OrchestratorProjection.tryFind jobId)
        |> Option.map (fun job -> job.Byname)
        |> Option.filter (String.IsNullOrWhiteSpace >> not)
        |> Option.map (fun name -> name.Trim())
        |> Option.defaultValue ""

    let private mapOrchestratorVerdict (scope: ToolRuntimeScope) (verdict: OrchestratorVerdict) : JoinRecord =
        match verdict with
        | OrchestratorVerdict.Published(jobId, head)
        | OrchestratorVerdict.PublishedPendingCleanup(jobId, head, _) ->
            { Name = resolveManagerJobName scope jobId
              AgentId = ManagerJobId.value jobId
              Role = Roles.roleLabel Role.Manager
              Status = "completed"
              View = CommitHash.value head }
        | OrchestratorVerdict.Cancelled jobId ->
            { Name = resolveManagerJobName scope jobId
              AgentId = ManagerJobId.value jobId
              Role = Roles.roleLabel Role.Manager
              Status = "abandoned"
              View = "" }
        | OrchestratorVerdict.RejectedDirty reason ->
            { Name = ""
              AgentId = ""
              Role = Roles.roleLabel Role.Manager
              Status = "failed"
              View = if isNull reason then "" else reason }
        | OrchestratorVerdict.IntegrationFailed(jobId, errorDetails) ->
            { Name = resolveManagerJobName scope jobId
              AgentId = ManagerJobId.value jobId
              Role = Roles.roleLabel Role.Manager
              Status = "failed"
              View = if isNull errorDetails then "" else errorDetails }
        | OrchestratorVerdict.Empty ->
            { Name = ""
              AgentId = ""
              Role = Roles.roleLabel Role.Manager
              Status = "abandoned"
              View = "" }

    let private executeOrchestratorJoinReceipt
        (scope: ToolRuntimeScope)
        (context: HostToolContext)
        (sessionId: SessionId)
        (attempt: JoinAttemptLease)
        : Task<Result<JoinRecord list, string>> =
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
                    (host.JoinPublishedAvailable(JoinBatch.Max, attempt.Wait))

            return
                match outcome with
                | Error err when err.StartsWith("JOIN_IN_PROGRESS", StringComparison.Ordinal) ->
                    Error "join-in-progress"
                | Error _ -> Error "not-ready"
                | Ok(Interrupted _) -> Error "interrupted"
                | Ok(ResultsAvailable batch) ->
                    Ok(NonEmptyBatch.toList batch |> List.map (mapOrchestratorVerdict scope))
        }

    let private executeAgentWithRuntimeReceipt
        (scope: ToolRuntimeScope)
        (context: HostToolContext)
        language
        (sessionId: SessionId)
        (attempt: JoinAttemptLease)
        (permit: FamilyRecoveryPermit)
        (runtime: HostForkRuntime)
        : Task<Result<JoinRecord list, string>> =
        task {
            let isDevOps = scope.IsRole(context, Role.DevOps)
            let waitTask = devopsOrPlainWait isDevOps attempt.Wait

            let joinDescriptor =
                DiagnosticWait.create
                    "agent-join"
                    (CausalOwner.create "JoinTool" [ "session", SessionId.value sessionId ])
                    [ "session", SessionId.value sessionId; "tool", "join" ]
                    (ExternalProducer("child-completion", [ "session", SessionId.value sessionId ]))
                    [ WaitEscape.CancelledBy(CausalOwner.create "JoinAttempt" [ "session", SessionId.value sessionId ])
                      WaitEscape.SessionLifetime ]
                    "JoinTool.joinAvailable"

            let fissionMembership =
                FissionRuntime.tryLane sessionId
                |> Option.map (fun binding -> binding.GroupId, binding.LaneIndex)

            let joinTask = joinTaskForMembership runtime permit waitTask fissionMembership
            let! joined = CausalAwait.awaitTask scope.WaitObserver joinDescriptor joinTask
            let root = scope.LogicalOwnerFor sessionId
            let resolveName = resolveAgentName scope root runtime
            let resolveRole = resolveAgentRole scope root runtime
            let resolveLabel = resolveTerminalLabel runtime

            return
                match joined with
                | Ok(Interrupted _) -> Error "interrupted"
                | Ok(ResultsAvailable batch) ->
                    Ok(mapResultsAvailable language runtime resolveName resolveRole resolveLabel batch)
                | Error joinError -> Error(mapForkError joinError)
        }

    let private executeWhenReadyReceipt
        (scope: ToolRuntimeScope)
        (context: HostToolContext)
        language
        (sessionId: SessionId)
        (attempt: JoinAttemptLease)
        (permit: FamilyRecoveryPermit)
        : Task<Result<JoinRecord list, string>> =
        let isOrchestrator = scope.IsRole(context, Role.Orchestrator)
        let runtimeResult = scope.RuntimeFor context

        match isOrchestrator, runtimeResult with
        | true, _ -> executeOrchestratorJoinReceipt scope context sessionId attempt
        | false, Error _ -> Task.FromResult(Error "context-unavailable")
        | false, Ok runtime -> executeAgentWithRuntimeReceipt scope context language sessionId attempt permit runtime

    let private executeAfterSessionReceipt
        (scope: ToolRuntimeScope)
        (context: HostToolContext)
        language
        (sessionId: SessionId)
        : Task<Result<JoinRecord list, string>> =
        task {
            let attempt = scope.JoinAttempts.Begin(sessionId, context.ToolCallId)
            let detachAbort = context.AttachAbort attempt.SignalOperatorAbort

            use _attempt = attempt

            use _cleanup =
                { new IDisposable with
                    member _.Dispose() = detachAbort () }

            let root = scope.LogicalOwnerFor sessionId
            let! admission = scope.RequireCurrentProcessJoin root

            match admission with
            | FamilyRecovery.FamilyBlocked _ -> return Error "not-ready"
            | FamilyRecovery.FamilyWaiting _ -> return Error "not-ready"
            | FamilyRecovery.FamilyReady permit ->
                return! executeWhenReadyReceipt scope context language sessionId attempt permit
        }

    let executeReceipt (scope: ToolRuntimeScope) (context: HostToolContext) : Task<Result<JoinRecord list, string>> =
        task {
            let language = lang context

            if String.IsNullOrWhiteSpace context.SessionId then
                return Error "context-unavailable"
            else
                return! executeAfterSessionReceipt scope context language (SessionId.create context.SessionId)
        }
