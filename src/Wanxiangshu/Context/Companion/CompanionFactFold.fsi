namespace Wanxiangshu.Context.Companion

open Wanxiangshu.Context.Trace
open Wanxiangshu.Execution.Session
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Foundation

[<RequireQualifiedAccess>]
type CompanionProjectionChange =
    | AssociationsSet of Map<SessionId, SessionAssociation>
    | CompanionSet of SessionId * CompanionProjection
    | XTraceSet of SessionId * XTraceProjectionState

[<RequireQualifiedAccess>]
type CompanionFoldRejection =
    | XTraceOpeningRejected of XTraceFoldRejection
    | XTracePartRejected of XTraceFoldRejection
    | XTraceTerminalRejected of XTraceFoldRejection

[<RequireQualifiedAccess>]
module CompanionFoldRejection =
    val fact: CompanionFoldRejection -> string
    val message: CompanionFoldRejection -> string

module CompanionFactFold =
    val fold:
        associations: Map<SessionId, SessionAssociation> ->
        companionOf: (SessionId -> CompanionProjection option) ->
        xTraceOf: (SessionId -> XTraceProjectionState option) ->
        fact: CompanionFactCases ->
            Result<CompanionProjectionChange list, CompanionFoldRejection>
