namespace Wanxiangshu.Composition.Turn

open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Interaction.Repair
open Wanxiangshu.OpenCode
open Wanxiangshu.Foundation.HostSession

/// Pure `snapshot + binding -> ReconciledTurn option`. Unknown origin is None.
///
/// Works in raw wire addresses and constructs typed identities only where the
/// role makes them meaningful: a `role=user` message becomes a
/// `PhysicalUserMessageId` (PROMPT-001), a `role=assistant` message becomes a
/// `ProviderRunIdentity` (HOST-010). Never reads the raw host payload.
module TurnReconcile =

    /// PROMPT-005: a transport receipt looks like `accepted-*` and is not a message
    /// address. `TransportReceipt.isAdmissionShaped` owns that test, so the prefix
    /// is not spelled a second time here.
    let private isAdmissionAddress (address: string) =
        TransportReceipt.isAdmissionShaped (TransportReceipt.create address)

    let private users (messages: SessionMessage list) =
        messages |> List.filter (fun message -> message.Role = "user")

    let private containsAddress (messages: SessionMessage list) (address: string) =
        messages |> List.exists (fun message -> message.Id = address)

    let private resolveRoot (messages: SessionMessage list) (declaredRoot: string) (inherited: bool) =
        if containsAddress messages declaredRoot then
            Some declaredRoot
        elif isAdmissionAddress declaredRoot then
            // The Host's async prompt endpoint returns a session-scoped admission
            // receipt, never a message address. A newly registered AgentOwnerRoot is
            // the first physical user in that child transcript; this is causal
            // correlation, not text guessing.
            users messages |> List.tryHead |> Option.map (fun message -> message.Id)
        elif inherited then
            // A Fission lane continues the owner's logical run: the owner's
            // authority root is not in the lane's transcript, and must not be.
            // The inheritance is proven by the durable lane membership, and the
            // physical cursor is still required to be in this transcript.
            Some declaredRoot
        else
            None

    let private resolvePhysical
        (messages: SessionMessage list)
        (declaredRoot: string)
        (root: string)
        (physical: string)
        (declaredPhysicalIsContinuationUser: bool)
        (inherited: bool)
        =
        if declaredPhysicalIsContinuationUser && isAdmissionAddress physical then
            users messages |> List.tryLast |> Option.map (fun message -> message.Id)
        elif physical = declaredRoot then
            // The bound physical is the inherited root itself: for a lane that
            // root lives in the owner's transcript, so it can never be this
            // lane's physical cursor.
            if inherited then None else Some root
        elif containsAddress messages physical then
            Some physical
        elif isAdmissionAddress physical then
            // A registered continuation appends exactly one new physical user.
            // Select the newest appended user while keeping the resolved authority
            // root separate.
            users messages |> List.tryLast |> Option.map (fun message -> message.Id)
        else
            None

    let private findAssistantAfter (messages: SessionMessage list) (userAddress: string) : SessionMessage option =
        let rec skipUntilUser remaining =
            match remaining with
            | [] -> None
            | head :: tail when head.Id = userAddress -> Some tail
            | _ :: tail -> skipUntilUser tail

        skipUntilUser messages
        |> Option.map (List.filter (fun message -> message.Role = "assistant"))
        |> Option.bind List.tryLast

    let private resolveAssistant (messages: SessionMessage list) (physical: string) =
        // host-boundary-005: once the current physical user is known, only an
        // assistant after that exact user can reconcile this occasion. Falling
        // back to an assistant after the authority root turns projection lag
        // into a stale terminal from an older continuation; AABB then stops
        // advancing and Manager idle dedupe sees an already-spent ProviderRun.
        // Absence is therefore `NoTurn` evidence for the event-driven scheduler,
        // never permission to reuse historical assistant state.
        findAssistantAfter messages physical

    let private isAbortedAssistant (message: SessionMessage) =
        CompletedTurnClassifier.isAbortErrorName message.ErrorName
        || (message.Finish |> Option.exists (fun value -> value.Equals("aborted", System.StringComparison.OrdinalIgnoreCase)))

    /// WHAT[delegation-039]: the late-consultation stop aborted the step this
    /// answer woke. The answer was queued while the agent was still writing its
    /// closing prose, so the Host loop ran straight into it and never went idle
    /// on the report; the only turn after the answer is the stop's own empty
    /// abort. The occasion is the finished report before the answer — exactly
    /// the turn the stop judged finished — reconciled under its own physical
    /// user. This is not a historical fallback: the stop is the recorded fact
    /// that nothing after the report belongs to the run.
    let private stoppedContinuationTerminal (messages: SessionMessage list) (floor: string option) (physical: string) =
        // Only the stretch after the run's own physical (when known) may hold
        // the report: an older work unit's turn is never this occasion.
        let afterFloor =
            match floor with
            | Some address when messages |> List.exists (fun message -> message.Id = address) ->
                messages |> List.skipWhile (fun message -> message.Id <> address) |> List.skip 1
            | _ -> messages

        let before = afterFloor |> List.takeWhile (fun message -> message.Id <> physical)

        before
        |> List.filter (fun message -> message.Role = "assistant")
        |> List.tryLast
        |> Option.filter (fun report -> report.Completed && not (isAbortedAssistant report))
        |> Option.map (fun report ->
            let reportPhysical =
                report.ParentId
                |> Option.filter (fun parent ->
                    floor = Some parent
                    || before |> List.exists (fun message -> message.Id = parent && message.Role = "user"))
                |> Option.defaultValue physical

            reportPhysical, report)

    let private resolveOccasion (sessionId: SessionId) (messages: SessionMessage list) (physical: string) =
        let stopped answer =
            Wanxiangshu.Foundation.TerminalSignal.CompletedContinuationStops.contains sessionId answer

        let plain assistant = Some(physical, assistant)

        // The stop returns before the ordinary stages, so the stopped answer
        // never becomes the bound physical: the abort shows up as the newest
        // assistant after the run's own physical, parented by the answer.
        match resolveAssistant messages physical with
        | Some assistant when isAbortedAssistant assistant ->
            match assistant.ParentId with
            | Some answer when stopped answer ->
                stoppedContinuationTerminal messages (Some physical) answer
                |> Option.orElse (plain assistant)
            | _ -> plain assistant
        | Some assistant -> plain assistant
        | None when stopped physical -> stoppedContinuationTerminal messages None physical
        | None -> None

    /// Reconcile a full snapshot against an active run binding.
    ///
    /// `None` when the origin is unknown (no authority root) or no assistant
    /// message follows the current physical user message.
    ///
    /// With no bound physical message the root's own wire address is used: the
    /// Authority Root was promoted from a physical message, so that address exists
    /// in the transcript. The address is read out rather than converted back into a
    /// `PhysicalUserMessageId`, because PROMPT-002 makes the promotion one-way and
    /// there is deliberately no inverse.
    let reconcile (messages: SessionMessage list) (binding: ActiveRunBinding) : ReconciledTurn option =
        let inherited = binding.InheritedAuthorityOwner.IsSome

        binding.AuthorityRootUserMessageId
        |> Option.bind (fun declaredRootId ->
            let declaredRoot = AuthorityRootUserMessageId.value declaredRootId

            resolveRoot messages declaredRoot inherited
            |> Option.bind (fun root ->
                let declaredPhysical =
                    binding.PhysicalUserMessageId
                    |> Option.map PhysicalUserMessageId.value
                    |> Option.defaultValue declaredRoot

                let declaredPhysicalIsContinuationUser =
                    binding.ContinuationMessageIds.Contains declaredPhysical

                resolvePhysical
                    messages
                    declaredRoot
                    root
                    declaredPhysical
                    declaredPhysicalIsContinuationUser
                    inherited
                |> Option.bind (fun physical ->
                    resolveOccasion binding.SessionId messages physical
                    |> Option.map (fun (physical, assistant) ->
                        CompletedTurnClassifier.buildTurn
                            binding.SessionId
                            (PhysicalUserMessageId.create physical)
                            (AuthorityRootUserMessageId.create root)
                            assistant
                            binding.Role
                            binding.Directory))))
