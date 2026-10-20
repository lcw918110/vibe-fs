namespace Wanxiangshu.Execution.Session

open Wanxiangshu.Foundation.Identity

/// HOST-008 / COMPANION-002: which kind of managed session this is.
///
/// This is what replaced Companion eligibility. The transform boundary no longer
/// asks "does this role deserve a Companion" — a policy whitelist over ten roles,
/// which COMPANION-001 deleted — but "is this session itself a Companion", which is
/// a structural fact with one answer.
///
/// `CompanionSession` carries its main session because the relation must be
/// answerable in both directions from one keyed lookup (PERSIST-008). Storing only
/// `WorkSession → main` would force a scan of every session to discover whether a
/// given id is somebody's Companion.
///
/// Long-lived ownership is `SessionExecutionClass` × `SessionOwnership` /
/// `AttachmentKind` in `Wanxiangshu.Foundation`. Dedicated SyncInspector/SyncCoder/SyncEngineer are
/// Work+Attached and must not be stuffed into `SatelliteKind`. See
/// `SessionOwnershipClassification` and `SyncDelegateAssociationHints`.
[<RequireQualifiedAccess>]
type SatelliteKind = | Companion

/// Durable HOST-008 association kind (FactCodec / projection surface).
///
/// Map via `SessionOwnershipClassification` onto `SessionExecutionClass` ×
/// `SessionOwnership` without changing these cases or the codec. `WorkSession` ≈
/// Work (+ Root, or Attached Sync* when known outside this record);
/// `SatelliteSession(_, Companion)` ≈ InternalLeaf+Attached(Companion).
[<RequireQualifiedAccess>]
type ManagedSessionKind =
    /// A session that can issue ordinary provider requests. Has exactly one Companion.
    /// G2: `SessionExecutionClass.Work` (Root, or Attached Sync* when proven elsewhere).
    | WorkSession
    /// HOST-008: every internal child is a leaf owned by one WorkSession.
    /// Companion → `SessionExecutionClass.InternalLeaf` + Attached(Companion).
    | SatelliteSession of ownerSessionId: SessionId * kind: SatelliteKind

/// HOST-008: the durable Work ↔ Companion relation for one session.
///
/// Record fields and FactCodec stay on the ManagedSessionKind model.
/// Orthogonal ExecutionClass × Ownership is a derived view
/// (`SessionOwnershipClassification`), not a durable rewrite.
/// DSL-state-combination: domain — optional parent identities are
/// relationship facts on one durable session association, not a workflow state
/// product.
type SessionAssociation =
    {
        SessionId: SessionId
        Kind: ManagedSessionKind
        /// COMPANION-002: a Companion session's `Some` points at the Work session
        /// that owns it; a Work session's `Some` points at its own parent when it
        /// is itself a fork child. The invariant is enforced by
        /// `SessionAssociationProjection.link`, not left to callers.

        ParentSessionId: SessionId option
    }

/// Why an association write was refused.
[<RequireQualifiedAccess>]
type AssociationRejection =
    /// COMPANION-002: a Companion session may not have a Companion of its own.
    | CompanionWouldRecurse of companion: SessionId
    /// A session cannot be its own Companion.
    | SelfLink of session: SessionId
    /// COMPANION-002: this Work session already has a different Companion. Creating a second
    /// one is what the lazy-creation rule exists to prevent, so the fold refuses it
    /// rather than silently repointing.
    | AlreadyLinkedToOther of existing: SessionId * proposed: SessionId
    /// The proposed Companion already belongs to a different Work session.
    | CompanionClaimedByOther of owner: SessionId * proposed: SessionId
    /// A child already has the other Satellite kind or the requested owner/kind differs.
    | SatelliteKindConflict of proposed: SessionId

/// HOST-008. One map, keyed lookup, no scan (PERSIST-008).
///
/// Both sides of a link live in the same map as separate entries, written together
/// from one fact. That is what makes "is this a Companion" and "which Companion does
/// this Work session have" both O(1) without a second index that could disagree.
module SessionAssociationProjection =

    let empty: Map<SessionId, SessionAssociation> = Map.empty

    let tryFind (sessionId: SessionId) (current: Map<SessionId, SessionAssociation>) = Map.tryFind sessionId current

    /// COMPANION-002: is this session a Companion.
    ///
    /// An unknown session answers `false`. COMPANION-001 gives every managed work
    /// session a Companion, so "no record yet" means a work session whose Companion
    /// has not been lazily created — the state the next transform resolves. It never
    /// means "a Companion we have not heard of": a Companion's association is written
    /// before its first prompt, so a Companion always has a record by the time a
    /// transform can fire for it.
    let isCompanion (sessionId: SessionId) (current: Map<SessionId, SessionAssociation>) =
        match tryFind sessionId current with
        | Some { Kind = ManagedSessionKind.SatelliteSession(_, SatelliteKind.Companion) } -> true
        | _ -> false

    let isSatellite (sessionId: SessionId) (current: Map<SessionId, SessionAssociation>) =
        match tryFind sessionId current with
        | Some { Kind = ManagedSessionKind.SatelliteSession _ } -> true
        | _ -> false

    /// ManagedSessionKind → SessionExecutionClass (no Ownership yet).
    let executionClassOf (kind: ManagedSessionKind) : SessionExecutionClass =
        match kind with
        | ManagedSessionKind.WorkSession -> SessionExecutionClass.Work
        | ManagedSessionKind.SatelliteSession _ -> SessionExecutionClass.InternalLeaf

    /// COMPANION-002: the companion belonging to this work session.
    let tryCompanionOf (sessionId: SessionId) (current: Map<SessionId, SessionAssociation>) =
        current
        |> Map.tryPick (fun candidateId candidate ->
            match candidate.Kind with
            | ManagedSessionKind.SatelliteSession(owner, SatelliteKind.Companion) when owner = sessionId ->
                Some candidateId
            | _ -> None)

    /// COMPANION-002: the main session a Companion belongs to.
    let tryMainSessionOf (sessionId: SessionId) (current: Map<SessionId, SessionAssociation>) =
        match tryFind sessionId current with
        | Some { Kind = ManagedSessionKind.SatelliteSession(main, SatelliteKind.Companion) } -> Some main
        | _ -> None

    let tryOwnerOf (sessionId: SessionId) (current: Map<SessionId, SessionAssociation>) =
        match tryFind sessionId current with
        | Some { Kind = ManagedSessionKind.SatelliteSession(owner, _) } -> Some owner
        | _ -> None

    let private workSession sessionId parent =
        { SessionId = sessionId
          Kind = ManagedSessionKind.WorkSession
          ParentSessionId = parent }

    /// HOST-008: record one Work ↔ Companion link, both directions at once.
    ///
    /// One function rather than two writes, because the two entries are one fact. A
    /// projection holding `Work session → Companion` without
    /// `Companion → CompanionSession Work session` would answer "is this a
    /// Companion" with `false`, and the next transform on that Companion would give
    /// it a Companion of its own — the recursion COMPANION-002 forbids.
    ///
    /// Idempotent for the same pair: re-linking a Work session to the same
    /// Companion is what restart recovery does, and refusing it would turn recovery
    /// into a startup failure.
    let private satelliteKindConflict
        (kind: SatelliteKind)
        (satelliteSessionId: SessionId)
        (current: Map<SessionId, SessionAssociation>)
        =
        match tryFind satelliteSessionId current with
        | Some { Kind = ManagedSessionKind.WorkSession } -> true
        | Some { Kind = ManagedSessionKind.SatelliteSession(_, existingKind) } when existingKind <> kind -> true
        | _ -> false

    let private linkedSatellite
        (kind: SatelliteKind)
        (mainSessionId: SessionId)
        (satelliteSessionId: SessionId)
        (parentOfMain: SessionId option)
        (current: Map<SessionId, SessionAssociation>)
        =
        let owner = tryFind mainSessionId current

        let parent =
            parentOfMain
            |> Option.orElse (owner |> Option.bind (fun e -> e.ParentSessionId))

        Ok(
            current
            |> Map.add mainSessionId (workSession mainSessionId parent)
            |> Map.add
                satelliteSessionId
                { SessionId = satelliteSessionId
                  Kind = ManagedSessionKind.SatelliteSession(mainSessionId, kind)
                  ParentSessionId = Some mainSessionId }
        )

    let private linkValidated
        (kind: SatelliteKind)
        (mainSessionId: SessionId)
        (satelliteSessionId: SessionId)
        (parentOfMain: SessionId option)
        (current: Map<SessionId, SessionAssociation>)
        =
        let existingSatellite =
            match kind with
            | SatelliteKind.Companion -> tryCompanionOf mainSessionId current

        let existingOwner = tryOwnerOf satelliteSessionId current
        let kindConflict = satelliteKindConflict kind satelliteSessionId current

        match existingSatellite, existingOwner, kindConflict with
        | Some existing, _, _ when existing <> satelliteSessionId ->
            Error(AssociationRejection.AlreadyLinkedToOther(existing, satelliteSessionId))
        | _, Some owner, _ when owner <> mainSessionId ->
            Error(AssociationRejection.CompanionClaimedByOther(owner, satelliteSessionId))
        | _, _, true -> Error(AssociationRejection.SatelliteKindConflict satelliteSessionId)
        | _ -> linkedSatellite kind mainSessionId satelliteSessionId parentOfMain current

    let linkSatellite
        (kind: SatelliteKind)
        (mainSessionId: SessionId)
        (satelliteSessionId: SessionId)
        (parentOfMain: SessionId option)
        (current: Map<SessionId, SessionAssociation>)
        : Result<Map<SessionId, SessionAssociation>, AssociationRejection> =
        if mainSessionId = satelliteSessionId then
            Error(AssociationRejection.SelfLink mainSessionId)
        elif isSatellite mainSessionId current then
            Error(AssociationRejection.CompanionWouldRecurse mainSessionId)
        else
            linkValidated kind mainSessionId satelliteSessionId parentOfMain current

    let link mainSessionId companionSessionId parentOfMain current =
        linkSatellite SatelliteKind.Companion mainSessionId companionSessionId parentOfMain current

    /// The Companion child was aborted. The Work session keeps its record and loses
    /// its Companion, so the next transform creates a fresh one.
    ///
    /// The Companion's own entry is REMOVED rather than kept as a tombstone. Unlike a
    /// handle (EXEC-009), a Companion session id is never re-presented by the model
    /// and never joined, so there is no request that a stale entry would have to
    /// refuse — and leaving one would make `isCompanion` true for a session that no
    /// longer exists.
    let unlink (mainSessionId: SessionId) (current: Map<SessionId, SessionAssociation>) =
        match tryCompanionOf mainSessionId current with
        | Some companionId -> Map.remove companionId current
        | None -> current

    let describe (rejection: AssociationRejection) =
        match rejection with
        | AssociationRejection.CompanionWouldRecurse companion ->
            sprintf "session %s is a Companion and may not have one (COMPANION-002)" (SessionId.value companion)
        | AssociationRejection.SelfLink session ->
            sprintf "session %s cannot be its own Companion (HOST-008)" (SessionId.value session)
        | AssociationRejection.AlreadyLinkedToOther(existing, proposed) ->
            sprintf
                "work session already has Companion %s; refusing to repoint to %s (COMPANION-002)"
                (SessionId.value existing)
                (SessionId.value proposed)
        | AssociationRejection.CompanionClaimedByOther(owner, proposed) ->
            sprintf
                "Companion %s already belongs to work session %s (HOST-008)"
                (SessionId.value proposed)
                (SessionId.value owner)
        | AssociationRejection.SatelliteKindConflict proposed ->
            sprintf "Satellite %s is already linked with a different kind (HOST-008)" (SessionId.value proposed)

/// View over durable `SessionAssociation` → `SessionExecutionClass` × `SessionOwnership`.
///
/// Additive only: does not change `SessionAssociation` fields or FactCodec.
/// Dedicated SyncInspector/SyncCoder/SyncEngineer are not represented on this durable record;
/// use `SyncDelegateAssociationHints` when registering them as Work+Attached.
module SessionOwnershipClassification =

    let executionClassOf (kind: ManagedSessionKind) : SessionExecutionClass =
        SessionAssociationProjection.executionClassOf kind

    /// Map one durable association entry onto the orthogonal ExecutionClass × Ownership view.
    ///
    /// - WorkSession → Work × Root. `ParentSessionId` may still be set for fork
    ///   children; that alone does not prove SyncInspector/SyncCoder/SyncEngineer Attached
    ///   ownership, so we do not invent `Attached(_, Sync*)` here. Callers that
    ///   know the SyncDelegate role should use `SyncDelegateAssociationHints`.
    /// - Satellite Companion → InternalLeaf × Attached(owner, Companion).
    let classifyLegacy (entry: SessionAssociation) : SessionExecutionClass * SessionOwnership option =
        match entry.Kind with
        | ManagedSessionKind.WorkSession -> SessionExecutionClass.Work, Some SessionOwnership.Root
        | ManagedSessionKind.SatelliteSession(owner, SatelliteKind.Companion) ->
            SessionExecutionClass.InternalLeaf, Some(SessionOwnership.Attached(owner, AttachmentKind.Companion))

    let tryClassify
        (sessionId: SessionId)
        (current: Map<SessionId, SessionAssociation>)
        : (SessionExecutionClass * SessionOwnership option) option =
        SessionAssociationProjection.tryFind sessionId current
        |> Option.map classifyLegacy

/// Hints for SyncDelegateRuntime: dedicated Inspector/Coder are Work+Attached,
/// not SatelliteKind InternalLeaf.
module SyncDelegateAssociationHints =

    let dedicatedExecutionClass = SessionExecutionClass.Work

    let dedicatedOwnership (owner: SessionId) (role: SyncDelegateRole) : SessionOwnership =
        SessionOwnership.Attached(owner, SyncDelegateRole.toAttachmentKind role)
