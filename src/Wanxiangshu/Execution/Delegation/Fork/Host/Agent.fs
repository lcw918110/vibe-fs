namespace Wanxiangshu.Execution.Delegation.Fork.Host

open Wanxiangshu.Composition.Durable
open Wanxiangshu.Execution.Delegation.Handle
open Wanxiangshu.Execution.Session
open Wanxiangshu.Execution.Session.Attachment
open Wanxiangshu.Execution.Session.Wait
open Wanxiangshu.Interaction.Repair
open Wanxiangshu.Participant.Provider.Attempt.Fallback

open System
open System.Threading.Tasks
open FsToolkit.ErrorHandling
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Foundation
open Wanxiangshu.Composition.Durable.Fact
open Wanxiangshu.Composition.Turn
open Wanxiangshu.Context.Companion
open Wanxiangshu.Context.Trace
open Wanxiangshu.Execution.Delegation.Fork
open Wanxiangshu.Execution.Delegation.SyncDelegate
open Wanxiangshu.Execution.Fission
open Wanxiangshu.Execution.Session.Recovery
open Wanxiangshu.Foundation
open Wanxiangshu.Host
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Interaction.Dispatch
open Wanxiangshu.Participant.Persona
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Participant.Provider.Attempt
open Wanxiangshu.Participant.Provider.Projection
open Wanxiangshu.Persistence.EventStore
open Wanxiangshu.Context.Trace
open Wanxiangshu.Execution.Delegation
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.OpenCode

/// Existing-child send must use the session's bound managed agent.
/// Never invent a managed name from CanonicalRole; never let the caller overwrite the bound agent.
module HostForkBinding =
    let private tryName (value: string) =
        if String.IsNullOrWhiteSpace value then
            None
        else
            Some(value.Trim())

    let managedAgent (journal: AgentJournal option) (childId: SessionId) : string option =
        journal
        |> Option.bind (fun durable ->
            let projections = (AgentJournal.snapshot durable).AgentProjections

            PromptAuthorityProjectionQueries.activeProfile childId projections
            |> Option.orElseWith (fun () -> PromptAuthorityProjectionQueries.lastAuthorityProfile childId projections)
            |> Option.bind (fun profile -> tryName profile.SelectedAgent))

[<AutoOpen>]
module HostForkAgent =

    let private forkInstructions (sessionId: SessionId) : ForkChildInstructions =
        Wanxiangshu.Execution.Delegation.SyncDelegate.HostForkProse.forkInstructions sessionId

    let private buildRelayEnvelope
        (runtime: HostForkRuntime)
        (_role: Role)
        (prompt: string)
        (payload: string option)
        : Task<Result<string list * string, string>> =
        let requirements = []

        Task.FromResult(
            Ok(
                requirements,
                ForkChildPayload.relay (forkInstructions runtime.ParentId) prompt None None requirements payload
            )
        )

    let private enrichFirstPrompt
        (runtime: HostForkRuntime)
        (role: Role)
        (prompt: string)
        (payload: string option)
        (renderedPrompt: string option)
        : Task<Result<string list * string, string>> =
        match renderedPrompt with
        | Some rendered -> Task.FromResult(Ok([], rendered))
        | None -> buildRelayEnvelope runtime role prompt payload

    let private maybeReplaceToolEstimate
        (journal: AgentJournal option)
        (expectedToolCalls: int option)
        (childId: SessionId)
        : Task<unit> =
        match journal, expectedToolCalls with
        | Some journal, Some expected ->
            let port = AgentJournalPortAdapter.forDelegatedToolEstimate journal
            DelegatedToolEstimateLedger.replace port childId expected
        | _ -> Task.FromResult(())

    let private sendFirstPromptAndInstallRun
        (runtime: HostForkRuntime)
        (agentId: string)
        (role: Role)
        (agentName: string)
        (identitySeed: PromptAuthority.IdentitySeed)
        (enrichedPrompt: string)
        (childId: SessionId)
        : Task<Result<ForkResult, string>> =
        task {
            let! sent =
                runtime.ObservePromptSend(
                    agentId,
                    ignore,
                    fun observer ->
                        HostForkAgentOwner.sendFirstPromptObserved
                            runtime.Sessions
                            runtime.Journal
                            childId
                            identitySeed
                            (runtime.DirectoryOf agentId)
                            enrichedPrompt
                            observer
                )

            match sent with
            | HostForkRunLifecycle.AgentOwnerDispatchOutcome.Accepted(_, authorityRoot) ->
                let run = runtime.InstallRun(agentId, childId, role, authorityRoot)

                let result =
                    runtime.Runtime.Fork(agentId, role, agentName, runWork = (fun () -> run.Source.Task))

                return Ok result
            | HostForkRunLifecycle.AgentOwnerDispatchOutcome.AcceptanceUncertain _ ->
                return Ok(ForkResult.DispatchUncertain agentId)
            | HostForkRunLifecycle.AgentOwnerDispatchOutcome.Rejected err -> return Error err
        }

    let private continueNewChildFork
        (runtime: HostForkRuntime)
        (agentId: string)
        (role: Role)
        (agentName: string)
        (identitySeed: PromptAuthority.IdentitySeed)
        (prompt: string)
        (requirements: string list)
        (enrichedPrompt: string)
        (isFirstPrompt: bool)
        (deferSend: bool)
        (expectedToolCalls: int option)
        (childId: SessionId)
        : Task<Result<ForkResult, string>> =
        task {
            lock runtime.Gate (fun () -> runtime.Children.[agentId] <- childId)

            runtime.ChildCreated agentId role childId
            runtime.ChildCreatedDir agentId childId (runtime.DirectoryOf agentId)

            if isFirstPrompt then
                let! _ =
                    XTraceCapture.captureOpeningWithReceipt runtime.Journal childId prompt requirements
                    |> TaskResult.mapError (fun error -> sprintf "fork opening trace capture failed: %A" error)

                ()

            do!
                maybeReplaceToolEstimate runtime.Journal expectedToolCalls childId
                |> TaskResultCE.ofTask
                |> TaskValue.map ignore

            if deferSend && isFirstPrompt then
                runtime.DeferredFirstPrompts.[agentId] <-
                    {| ChildId = childId
                       IdentitySeed = identitySeed
                       Prompt = enrichedPrompt |}

                return Ok(ForkResult.Created agentId)
            else
                return! sendFirstPromptAndInstallRun runtime agentId role agentName identitySeed enrichedPrompt childId
        }

    let private afterLinkage
        (runtime: HostForkRuntime)
        (agentId: string)
        (role: Role)
        (agentName: string)
        (identitySeed: PromptAuthority.IdentitySeed)
        (prompt: string)
        (requirements: string list)
        (enrichedPrompt: string)
        (isFirstPrompt: bool)
        (deferSend: bool)
        (expectedToolCalls: int option)
        (childId: SessionId)
        (linkageResult: Result<unit, string>)
        : Task<Result<ForkResult, string>> =
        match linkageResult with
        | Error err ->
            task {
                let! _ = runtime.Sessions.AbortSession childId
                return Error err
            }
        | Ok() ->
            continueNewChildFork
                runtime
                agentId
                role
                agentName
                identitySeed
                prompt
                requirements
                enrichedPrompt
                isFirstPrompt
                deferSend
                expectedToolCalls
                childId

    let private forkNewChild
        (runtime: HostForkRuntime)
        (agentId: string)
        (role: Role)
        (agentName: string)
        (providerByname: string)
        (prompt: string)
        (requirements: string list)
        (enrichedPrompt: string)
        (handleOwnership: HandleOwnership)
        (isFirstPrompt: bool)
        (deferSend: bool)
        (expectedToolCalls: int option)
        : Task<Result<ForkResult, string>> =
        let interpretChildResult =
            function
            | Error error -> Task.FromResult(Error error)
            | Ok(identitySeed, childId) ->
                task {
                    let journalPort =
                        runtime.Journal |> Option.map AgentJournalPortAdapter.fromAgentJournal

                    let! linkageRaw =
                        HandleController.linkNamed
                            journalPort
                            runtime.ParentId
                            agentId
                            childId
                            agentName
                            providerByname
                            role
                            handleOwnership

                    let linkageResult =
                        linkageRaw |> Result.mapError (sprintf "Failed to persist HandleLinked: %s")

                    return!
                        afterLinkage
                            runtime
                            agentId
                            role
                            agentName
                            identitySeed
                            prompt
                            requirements
                            enrichedPrompt
                            isFirstPrompt
                            deferSend
                            expectedToolCalls
                            childId
                            linkageResult
                }

        task {
            let! childResult =
                taskResult {
                    let! identitySeed =
                        HostForkRunLifecycle.issueCurrentOwnerIdentitySeed runtime.Journal runtime.ParentId agentName

                    let! childId =
                        runtime.Sessions.CreateChildSession(
                            runtime.ParentId,
                            { Title = Some agentId
                              Agent = Some agentName
                              Directory = runtime.DirectoryOf agentId }
                        )

                    return identitySeed, childId
                }

            return! interpretChildResult childResult
        }

    let private forkExistingChild
        (runtime: HostForkRuntime)
        (agentId: string)
        (childId: SessionId)
        (role: Role)
        (prompt: string)
        (enrichedPrompt: string)
        (isFirstPrompt: bool)
        (expectedToolCalls: int option)
        : Task<Result<ForkResult, string>> =
        task {
            match HostForkBinding.managedAgent runtime.Journal childId with
            | None -> return Error(sprintf "Agent handle '%s' has no active managed agent identity" agentId)
            | Some sendAgent ->
                do! maybeReplaceToolEstimate runtime.Journal expectedToolCalls childId

                return!
                    HostForkChildDispatch.sendToExistingChild
                        runtime.Gate
                        runtime.PendingRuns
                        runtime.Journal
                        runtime.ParentId
                        runtime.Sessions
                        runtime.ChildViewOfRun
                        runtime.TrackOwnedWork
                        runtime.Runtime
                        runtime.SendChildPrompt
                        runtime.SendBusyNudge
                        (fun child role -> runtime.RunStarted child role (runtime.DirectoryOf agentId))
                        agentId
                        childId
                        role
                        prompt
                        sendAgent
                        (if isFirstPrompt then Some enrichedPrompt else None)
        }

    let private forkAfterEnrich
        (runtime: HostForkRuntime)
        (agentId: string)
        (role: Role)
        (agentName: string)
        (providerByname: string)
        (prompt: string)
        (handleOwnership: HandleOwnership)
        (isFirstPrompt: bool)
        (deferSend: bool)
        (expectedToolCalls: int option)
        (retired: bool option)
        (existing: SessionId option)
        (requirements: string list)
        (enrichedPrompt: string)
        : Task<Result<ForkResult, string>> =
        match runtime.IsCancelling, retired, existing with
        | true, _, _ -> Task.FromResult(Error "Parent cancellation is in progress")
        | false, Some true, _ -> Task.FromResult(Error(sprintf "RetiredHandle: %s" agentId))
        | false, _, Some childId ->
            forkExistingChild runtime agentId childId role prompt enrichedPrompt isFirstPrompt expectedToolCalls
        | false, _, None ->
            forkNewChild
                runtime
                agentId
                role
                agentName
                providerByname
                prompt
                requirements
                enrichedPrompt
                handleOwnership
                isFirstPrompt
                deferSend
                expectedToolCalls

    let private resolveReuseEnrichedPrompt
        (runtime: HostForkRuntime)
        (prompt: string)
        (renderedPrompt: string option)
        : Task<string option> =
        match renderedPrompt with
        | Some rendered -> Task.FromResult(Some rendered)
        | None ->
            Task.FromResult(Some(ForkChildPayload.relay (forkInstructions runtime.ParentId) prompt None None [] None))

    let private reuseWithManagedAgent
        (runtime: HostForkRuntime)
        (agentId: string)
        (childId: SessionId)
        (role: Role)
        (agentName: string)
        (prompt: string)
        (renderedPrompt: string option)
        (wasDormant: bool)
        : Task<Result<ForkResult, string>> =
        task {

            let providerBynameOpt =
                runtime.Journal
                |> Option.bind (fun durable ->
                    AgentJournal.handleProjection durable runtime.ParentId
                    |> HandleProjection.tryFind (HandleController.agentHandle agentId))
                |> Option.map (fun handle -> handle.Byname)
                |> Option.filter (String.IsNullOrWhiteSpace >> not)

            match providerBynameOpt with
            | None -> return Error(sprintf "Agent handle '%s' has no provider byname" agentId)
            | Some providerByname ->
                runtime.ActivateDormantChildIfNeeded(wasDormant, agentId, childId, role)
                let! enriched = resolveReuseEnrichedPrompt runtime prompt renderedPrompt

                return!
                    HostForkChildDispatch.sendToExistingChild
                        runtime.Gate
                        runtime.PendingRuns
                        runtime.Journal
                        runtime.ParentId
                        runtime.Sessions
                        runtime.ChildViewOfRun
                        runtime.TrackOwnedWork
                        runtime.Runtime
                        runtime.SendChildPrompt
                        runtime.SendBusyNudge
                        (fun child role -> runtime.RunStarted child role (runtime.DirectoryOf agentId))
                        agentId
                        childId
                        role
                        prompt
                        agentName
                        enriched
        }

    let private interpretDevOpsFirstSent (runtime: HostForkRuntime) agentId childId role agentName sent =
        match sent with
        | HostForkRunLifecycle.AgentOwnerDispatchOutcome.Accepted(_, authorityRoot) ->
            let run = runtime.InstallRun(agentId, childId, role, authorityRoot)

            let result =
                runtime.Runtime.Fork(agentId, role, agentName, runWork = (fun () -> run.Source.Task))

            Ok result
        | HostForkRunLifecycle.AgentOwnerDispatchOutcome.AcceptanceUncertain _ ->
            Ok(ForkResult.DispatchUncertain agentId)
        | HostForkRunLifecycle.AgentOwnerDispatchOutcome.Rejected err -> Error err

    let private sendDevOpsFirstPrompt
        (runtime: HostForkRuntime)
        agentId
        childId
        prompt
        renderedPrompt
        expectedToolCalls
        =
        task {
            do! maybeReplaceToolEstimate runtime.Journal expectedToolCalls childId
            let agentName = "devops"
            let role = Role.DevOps

            match HostForkRunLifecycle.issueCurrentOwnerIdentitySeed runtime.Journal runtime.ParentId agentName with
            | Error err -> return Error err
            | Ok identitySeed ->
                let! _ =
                    XTraceCapture.captureOpeningWithReceipt runtime.Journal childId prompt []
                    |> TaskResult.mapError (fun error -> sprintf "devops opening trace capture failed: %A" error)

                let enrichedPrompt = defaultArg renderedPrompt prompt

                let! sent =
                    runtime.ObservePromptSend(
                        agentId,
                        ignore,
                        fun observer ->
                            HostForkAgentOwner.sendFirstPromptObserved
                                runtime.Sessions
                                runtime.Journal
                                childId
                                identitySeed
                                (runtime.DirectoryOf agentId)
                                enrichedPrompt
                                observer
                    )

                return interpretDevOpsFirstSent runtime agentId childId role agentName sent
        }

    let private reuseLiveChild
        (runtime: HostForkRuntime)
        (agentId: string)
        (childId: SessionId)
        (wasDormant: bool)
        (prompt: string)
        (renderedPrompt: string option)
        (expectedToolCalls: int option)
        : Task<Result<ForkResult, string>> =
        task {
            let recordOpt =
                runtime.Runtime.List()
                |> fst
                |> List.tryFind (fun agent -> agent.AgentId = agentId)

            let boundAgent = HostForkBinding.managedAgent runtime.Journal childId

            let durableHandleOpt =
                runtime.Journal
                |> Option.bind (fun durable ->
                    let projection = AgentJournal.handleProjection durable runtime.ParentId
                    let handle = HandleController.agentHandle agentId
                    HandleProjection.tryFind handle projection)

            let isDevOpsHandle =
                match durableHandleOpt with
                | Some r -> r.CanonicalRole = Role.DevOps || r.Byname = "devops"
                | None -> agentId = "devops"

            let roleOpt =
                recordOpt
                |> Option.map (fun r -> r.Role)
                |> Option.orElseWith (fun () -> durableHandleOpt |> Option.map (fun h -> h.CanonicalRole))
                |> Option.orElseWith (fun () -> if isDevOpsHandle then Some Role.DevOps else None)

            match roleOpt, boundAgent with
            | _, Some agentName when isDevOpsHandle ->
                // When devops already has an active or previous authority profile,
                // reuse the child session via normal continuation/reuse rather than
                // creating a duplicate AgentOwnerRoot which causes ActiveRunIdentityConflict.
                do! maybeReplaceToolEstimate runtime.Journal expectedToolCalls childId

                return!
                    reuseWithManagedAgent runtime agentId childId Role.DevOps agentName prompt renderedPrompt wasDormant
            | _, None when isDevOpsHandle ->
                return! sendDevOpsFirstPrompt runtime agentId childId prompt renderedPrompt expectedToolCalls
            | Some role, Some agentName ->
                do! maybeReplaceToolEstimate runtime.Journal expectedToolCalls childId

                return! reuseWithManagedAgent runtime agentId childId role agentName prompt renderedPrompt wasDormant
            | None, _ -> return Error(sprintf "Unknown agent id: %s" agentId)
            | _, None -> return Error(sprintf "Agent handle '%s' has no active managed agent identity" agentId)
        }

    let private appendGuidanceToRun (runtime: HostForkRuntime) (run: PendingHostRun) prompt =
        taskResult {
            let! profile = HostForkBusyNudge.profileForRun runtime.Journal run
            do! runtime.SendBusyNudge run.AgentId run.ChildId profile prompt
            return ForkResult.Nudged run.AgentId
        }

    let private activeRun (runtime: HostForkRuntime) agentId =
        lock runtime.Gate (fun () ->
            match runtime.PendingRuns.TryGetValue agentId with
            | true, run -> Some run
            | false, _ -> None)

    type HostForkRuntime with

        /// Current durable Authority Root binding for an already-linked child.
        member this.BoundManagedAgent(childId: SessionId) : string option =
            HostForkBinding.managedAgent this.Journal childId

        /// PROMPT-008: `agent` is the managed agent name the caller selected, and
        /// it is required. It is never defaulted, and the selected name travels to the
        /// Host send boundary as chosen.
        ///
        /// `renderedPrompt` (PENDING 7): the caller already rendered the first-prompt
        /// ARCH-010 payload and wants it sent verbatim instead of the Host's own relay
        /// envelope. Used by `ForkTool` when a Coder TDD phase must reach the child as
        /// the durable `[tdd]` table. `prompt` stays the original assignment so opening
        /// capture and journal facts keep the task text, not the envelope.
        member this.Fork
            (
                agentId: string,
                role: Role,
                agent: string,
                prompt: string,
                payload: string option,
                ?firstPrompt: bool,
                ?renderedPrompt: string,
                ?ownership: HandleOwnership,
                ?deferSend: bool,
                ?byname: string,
                ?expectedToolCalls: int
            ) : Task<Result<ForkResult, string>> =
            let agentName = agent.Trim()

            let providerByname =
                match byname with
                | Some value when not (String.IsNullOrWhiteSpace value) -> value.Trim()
                | _ -> agentName

            let isFirstPrompt = defaultArg firstPrompt true
            let deferSend = defaultArg deferSend false
            let handleOwnership = defaultArg ownership this.HandleOwnership

            task {
                // GREEN-4: recovery ownership is SessionRecoveryWorkflow only.
                let retired = this.IsRetiredHandle agentId

                let existing =
                    lock this.Gate (fun () ->
                        match this.Children.TryGetValue agentId with
                        | true, childId -> Some childId
                        | false, _ -> None)

                // The ARCH-010 first-prompt payload is computed once and used by
                // both child paths: a brand-new child AND an idle restored child
                // receive the same envelope, so a canary declaration anchored on
                // the envelope cannot tell which path produced the request.
                // Continuations (busy nudge, nudge, manager resume) opt out.
                //
                // PENDING 7: `?renderedPrompt: string` (not `string option`) so the
                // body sees `string option`. Caller supplies a pre-rendered ARCH-010
                // document when present; otherwise Host builds the relay envelope.
                let! enrichedResult =
                    if isFirstPrompt then
                        enrichFirstPrompt this role prompt payload renderedPrompt
                    else
                        Task.FromResult(Ok([], prompt))

                match enrichedResult with
                | Error err -> return Error err
                | Ok(requirements, enrichedPrompt) ->
                    return!
                        forkAfterEnrich
                            this
                            agentId
                            role
                            agentName
                            providerByname
                            prompt
                            handleOwnership
                            isFirstPrompt
                            deferSend
                            expectedToolCalls
                            retired
                            existing
                            requirements
                            enrichedPrompt
            }

        member this.Reuse
            (agentId: string, prompt: string, ?renderedPrompt: string, ?expectedToolCalls: int)
            : Task<Result<ForkResult, string>> =
            task {
                // GREEN-4: recovery ownership is SessionRecoveryWorkflow only.
                // After join retires the prior work unit, reuse of the same
                // agent id reopens Labor on the same child session (GLORY-068 /
                // "十年修得同船渡"). Abandoned handles stay unusable.
                let abandoned =
                    this.Journal
                    |> Option.map (fun durable ->
                        let projection = AgentJournal.handleProjection durable this.ParentId
                        let handle = HandleController.agentHandle agentId
                        HandleProjection.isAbandoned handle projection)

                let existing = this.ReusableChildOrAdopt agentId
                let active = activeRun this agentId

                match this.IsCancelling, active, abandoned, existing with
                | true, _, _, _ -> return Error "Parent cancellation is in progress"
                | false, Some run, _, _ -> return! appendGuidanceToRun this run (defaultArg renderedPrompt prompt)
                | false, _, Some true, _ -> return Error(sprintf "RetiredHandle: %s" agentId)
                | false, _, _, None -> return Error(sprintf "Unknown agent id: %s" agentId)
                | false, _, _, Some(childId, wasDormant) ->
                    return! reuseLiveChild this agentId childId wasDormant prompt renderedPrompt expectedToolCalls
            }

        member this.ActiveGuidanceProfile(agentId: string) =
            match activeRun this agentId with
            | None -> Error(sprintf "Agent has no active assignment: %s" agentId)
            | Some run -> HostForkBusyNudge.profileForRun this.Journal run

        member this.AppendGuidance
            (agentId: string, expected: PromptAuthority.AuthorityExecutionProfile, prompt: string)
            : Task<Result<ForkResult, string>> =
            taskResult {
                do! Result.requireFalse "Parent cancellation is in progress" this.IsCancelling

                let! run =
                    activeRun this agentId
                    |> Result.requireSome (sprintf "Agent has no active assignment: %s" agentId)

                let! current = HostForkBusyNudge.profileForRun this.Journal run
                do! Result.requireTrue "Busy guidance assignment has changed" (current = expected)
                do! this.SendBusyNudge agentId run.ChildId expected prompt
                return ForkResult.Nudged agentId
            }
