namespace Wanxiangshu.Composition.Turn

open System.Collections.Generic
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.OpenCode
open Wanxiangshu.Foundation.HostSession

/// Root / physical / continuation bindings for a logical run.
///
/// No Host payload reading. Bindings are explicit in-memory facts or recovered
/// from the durable PromptAuthority projection.
///
/// `ContinuationMessageIds` stays a set of raw wire addresses: it is matched
/// against transcript addresses during reconcile, where role is not yet known.
module TurnBinding =

    /// Build an ActiveRunBinding from the journal PromptAuthority projection.
    ///
    /// Directory is not a journal fact; it must be supplied by the host when the
    /// run is registered.
    ///
    /// `ActiveLogicalRun` exists only after an Authority Root was accepted, so
    /// `Some run` already carries a real root. The old blank-string guard was a
    /// sentinel check that PROMPT-001's typed identity removes.
    ///
    /// Physical stays `None` when nothing is bound. It is not backfilled from the
    /// root, because PROMPT-002 makes promotion one-way and there is deliberately
    let private tryBoundPhysical (userBindings: Dictionary<string, PhysicalUserMessageId>) (sessionId: SessionId) =
        match userBindings.TryGetValue(SessionId.value sessionId) with
        | true, bound -> Some bound
        | false, _ -> None

    /// no `AuthorityRootUserMessageId -> PhysicalUserMessageId` inverse. Reconcile
    /// resolves the missing physical against the root's own wire address instead.
    let fromProjection
        (sessionId: SessionId)
        (projection: PromptAuthority.PromptAuthorityProjection)
        (userBindings: Dictionary<string, PhysicalUserMessageId>)
        (continuationIds: Set<string>)
        : ActiveRunBinding option =
        match projection.ActiveLogicalRun with
        | None -> None
        | Some run ->
            Some
                { SessionId = sessionId
                  RunId = None
                  AuthorityRootUserMessageId = Some run.AuthorityRootUserMessageId
                  PhysicalUserMessageId = tryBoundPhysical userBindings sessionId
                  ContinuationMessageIds = continuationIds
                  Role = Some run.CanonicalRole
                  Directory = None
                  InheritedAuthorityOwner = None }

    /// Mutable store for in-memory bindings. Durable recovery uses the journal.
    type Store() =
        let gate = obj ()
        /// DSL-cross-callback-proof: physical resource — immutable message identity binding, never workflow stage
        // DSL-MUTABLE: resource — user message binding map by session.
        let userMessageBindings = Dictionary<string, PhysicalUserMessageId>()
        /// DSL-cross-callback-proof: physical resource — current Host run identity/authority binding
        // DSL-MUTABLE: resource — active run binding map by session.
        let activeBindings = Dictionary<string, ActiveRunBinding>()
        /// DSL-cross-callback-proof: physical resource — accepted continuation message identities
        // DSL-MUTABLE: resource — continuation message id set by session.
        let continuationMessageIds = Dictionary<string, Set<string>>()

        member _.UserMessageBindings = userMessageBindings

        /// Bind a new authority root (human or agent owner).
        ///
        /// The caller hands over the physical message that opened the run; the root
        /// is derived from it by PROMPT-002 promotion, never parsed separately.
        member _.BindUserMessage(sessionId: SessionId, physical: PhysicalUserMessageId, ?agentRole: Role) =
            lock gate (fun () ->
                let key = SessionId.value sessionId
                let root = PhysicalUserMessageId.promoteToAuthorityRoot physical
                userMessageBindings.[key] <- physical

                match activeBindings.TryGetValue(key) with
                | true, binding ->
                    activeBindings.[key] <-
                        { binding with
                            AuthorityRootUserMessageId = Some root
                            PhysicalUserMessageId = Some physical
                            Role = agentRole |> Option.orElse binding.Role }
                | false, _ ->
                    activeBindings.[key] <-
                        { SessionId = sessionId
                          RunId = None
                          AuthorityRootUserMessageId = Some root
                          PhysicalUserMessageId = Some physical
                          ContinuationMessageIds = Set.empty
                          Role = agentRole
                          Directory = None
                          InheritedAuthorityOwner = None })

        /// Bind a continuation physical message to the active logical run.
        member _.BindContinuationUserMessage(sessionId: SessionId, physical: PhysicalUserMessageId) =
            lock gate (fun () ->
                let key = SessionId.value sessionId
                let address = PhysicalUserMessageId.value physical

                let continuations =
                    match continuationMessageIds.TryGetValue(key) with
                    | true, set -> set.Add address
                    | false, _ -> Set.singleton address

                continuationMessageIds.[key] <- continuations
                userMessageBindings.[key] <- physical

                match activeBindings.TryGetValue(key) with
                | true, binding ->
                    activeBindings.[key] <-
                        { binding with
                            PhysicalUserMessageId = Some physical
                            ContinuationMessageIds = continuations }
                | false, _ ->
                    activeBindings.[key] <-
                        { SessionId = sessionId
                          RunId = None
                          AuthorityRootUserMessageId = None
                          PhysicalUserMessageId = Some physical
                          ContinuationMessageIds = continuations
                          Role = None
                          Directory = None
                          InheritedAuthorityOwner = None })

        /// crash-reconciliation-018: the explicit-resume material and the first ordinary
        /// material that replaces it must move reconciliation's physical cursor
        /// without minting authority or a continuation identity.
        member _.BindPhysicalUserMaterial(sessionId: SessionId, physical: PhysicalUserMessageId) =
            lock gate (fun () ->
                let key = SessionId.value sessionId
                userMessageBindings.[key] <- physical

                match activeBindings.TryGetValue(key) with
                | true, binding ->
                    activeBindings.[key] <-
                        { binding with
                            PhysicalUserMessageId = Some physical }
                | false, _ ->
                    activeBindings.[key] <-
                        { SessionId = sessionId
                          RunId = None
                          AuthorityRootUserMessageId = None
                          PhysicalUserMessageId = Some physical
                          ContinuationMessageIds = Set.empty
                          Role = None
                          Directory = None
                          InheritedAuthorityOwner = None })

        /// Register a host-provided active run (e.g. child session start).
        member _.BindActiveRun(binding: ActiveRunBinding) =
            lock gate (fun () ->
                let key = SessionId.value binding.SessionId

                let continuations =
                    match continuationMessageIds.TryGetValue(key) with
                    | true, set -> binding.ContinuationMessageIds + set
                    | false, _ -> binding.ContinuationMessageIds

                activeBindings.[key] <-
                    { binding with
                        ContinuationMessageIds = continuations })

        /// Resolve the best binding: explicit store first, then durable projection.
        /// The explicit host-provided role and directory are always preserved.
        member _.ActiveRunBinding(sessionId: SessionId, ?projection: AgentProjectionSet) : ActiveRunBinding option =
            lock gate (fun () ->
                let key = SessionId.value sessionId

                let fromExplicit () =
                    match activeBindings.TryGetValue(key) with
                    | true, binding when binding.AuthorityRootUserMessageId.IsSome -> Some binding
                    | true, binding when Option.isSome binding.PhysicalUserMessageId -> Some binding
                    | _ -> None

                let continuations =
                    match continuationMessageIds.TryGetValue(key) with
                    | true, set -> set
                    | false, _ -> Set.empty

                // A Fission lane owns no authority root of its own: it continues
                // the owner's logical run. The owner's active run comes from the
                // durable lane membership, and the lane's own accepted
                // continuation is the physical cursor.
                let inheritedBinding (proj: AgentProjectionSet) =
                    match PromptAuthorityProjectionQueries.inheritedOwnerProfile sessionId proj with
                    | None -> None
                    | Some ownerProfile ->
                        let owner = PromptAuthorityProjectionQueries.laneOwnerSessionId sessionId proj

                        let lanePhysicals =
                            PromptAuthorityProjectionQueries.ownContinuationPhysicals sessionId proj

                        Some
                            { SessionId = sessionId
                              RunId = None
                              AuthorityRootUserMessageId = Some ownerProfile.AuthorityRootUserMessageId
                              PhysicalUserMessageId = lanePhysicals |> List.tryLast
                              ContinuationMessageIds = continuations
                              Role = Some ownerProfile.CanonicalRole
                              Directory = None
                              InheritedAuthorityOwner = owner }

                let projectedBinding (proj: AgentProjectionSet) =
                    Map.tryFind sessionId proj.Sessions
                    |> Option.bind (fun session -> session.PromptAuthority)
                    |> Option.bind (fun authority ->
                        fromProjection sessionId authority userMessageBindings continuations)
                    // A lane has no authority root of its own, so its own
                    // projection yields nothing: the inherited owner binding
                    // is the only durable binding a lane can have.
                    |> Option.orElseWith (fun () -> inheritedBinding proj)

                let mergeProjectedBinding (binding: ActiveRunBinding) (projected: ActiveRunBinding) =
                    // Durable authority owns the root. Host registration can race
                    // a continuation and promote its physical cursor by mistake.
                    { binding with
                        AuthorityRootUserMessageId = projected.AuthorityRootUserMessageId
                        PhysicalUserMessageId =
                            binding.PhysicalUserMessageId |> Option.orElse projected.PhysicalUserMessageId
                        ContinuationMessageIds = binding.ContinuationMessageIds + continuations
                        Role = binding.Role |> Option.orElse projected.Role
                        InheritedAuthorityOwner =
                            projected.InheritedAuthorityOwner
                            |> Option.orElse binding.InheritedAuthorityOwner }

                let mergeBindings (explicitBinding: ActiveRunBinding option) (projected: ActiveRunBinding option) =
                    match explicitBinding, projected with
                    | Some binding, Some projected -> Some(mergeProjectedBinding binding projected)
                    | Some binding, None ->
                        Some
                            { binding with
                                ContinuationMessageIds = binding.ContinuationMessageIds + continuations }
                    | None, Some projected ->
                        Some
                            { projected with
                                ContinuationMessageIds = projected.ContinuationMessageIds + continuations }
                    | None, None -> None

                match projection with
                | None -> fromExplicit ()
                | Some proj -> mergeBindings (fromExplicit ()) (projectedBinding proj))

        /// Latest physical user message for the active logical run.
        member _.TryPhysicalUserMessage(sessionId: SessionId) : PhysicalUserMessageId option =
            lock gate (fun () ->
                let key = SessionId.value sessionId

                match activeBindings.TryGetValue(key) with
                | true, binding -> binding.PhysicalUserMessageId
                | false, _ -> tryBoundPhysical userMessageBindings sessionId)

        /// Remove all state for a session (session.deleted or explicit cleanup).
        member _.ClearSession(sessionId: SessionId) =
            lock gate (fun () ->
                let key = SessionId.value sessionId
                userMessageBindings.Remove(key) |> ignore
                activeBindings.Remove(key) |> ignore
                continuationMessageIds.Remove(key) |> ignore)
