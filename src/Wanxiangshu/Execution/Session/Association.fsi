namespace Wanxiangshu.Execution.Session

open Wanxiangshu.Foundation.Identity

[<RequireQualifiedAccess>]
type SatelliteKind = | Companion

[<RequireQualifiedAccess>]
type ManagedSessionKind =
    | WorkSession
    | SatelliteSession of ownerSessionId: SessionId * kind: SatelliteKind

type SessionAssociation =
    { SessionId: SessionId
      Kind: ManagedSessionKind
      ParentSessionId: SessionId option }

[<RequireQualifiedAccess>]
type AssociationRejection =
    | CompanionWouldRecurse of companion: SessionId
    | SelfLink of session: SessionId
    | AlreadyLinkedToOther of existing: SessionId * proposed: SessionId
    | CompanionClaimedByOther of owner: SessionId * proposed: SessionId
    | SatelliteKindConflict of proposed: SessionId

module SessionAssociationProjection =
    val empty: Map<SessionId, SessionAssociation>
    val tryFind: sessionId: SessionId -> current: Map<SessionId, SessionAssociation> -> SessionAssociation option
    val isCompanion: sessionId: SessionId -> current: Map<SessionId, SessionAssociation> -> bool
    val isSatellite: sessionId: SessionId -> current: Map<SessionId, SessionAssociation> -> bool
    val tryCompanionOf: sessionId: SessionId -> current: Map<SessionId, SessionAssociation> -> SessionId option
    val executionClassOf: kind: ManagedSessionKind -> SessionExecutionClass
    val tryMainSessionOf: sessionId: SessionId -> current: Map<SessionId, SessionAssociation> -> SessionId option
    val tryOwnerOf: sessionId: SessionId -> current: Map<SessionId, SessionAssociation> -> SessionId option

    val linkSatellite:
        kind: SatelliteKind ->
        mainSessionId: SessionId ->
        satelliteSessionId: SessionId ->
        parentOfMain: SessionId option ->
        current: Map<SessionId, SessionAssociation> ->
            Result<Map<SessionId, SessionAssociation>, AssociationRejection>

    val link:
        mainSessionId: SessionId ->
        companionSessionId: SessionId ->
        parentOfMain: SessionId option ->
        current: Map<SessionId, SessionAssociation> ->
            Result<Map<SessionId, SessionAssociation>, AssociationRejection>

    val unlink:
        mainSessionId: SessionId -> current: Map<SessionId, SessionAssociation> -> Map<SessionId, SessionAssociation>

    val describe: rejection: AssociationRejection -> string

module SessionOwnershipClassification =
    val executionClassOf: kind: ManagedSessionKind -> SessionExecutionClass
    val classifyLegacy: entry: SessionAssociation -> SessionExecutionClass * SessionOwnership option

    val tryClassify:
        sessionId: SessionId ->
        current: Map<SessionId, SessionAssociation> ->
            (SessionExecutionClass * SessionOwnership option) option

module SyncDelegateAssociationHints =
    val dedicatedExecutionClass: SessionExecutionClass
    val dedicatedOwnership: owner: SessionId -> role: SyncDelegateRole -> SessionOwnership
