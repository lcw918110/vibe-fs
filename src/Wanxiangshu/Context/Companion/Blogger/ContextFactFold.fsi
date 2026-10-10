namespace Wanxiangshu.Context.Companion.Blogger

open Wanxiangshu.Context.Companion
open Wanxiangshu.Context.Companion.Blogger.Runtime
open Wanxiangshu.Context.Prefix
open Wanxiangshu.Enforcer
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Foundation

[<RequireQualifiedAccess>]
type ContextProjectionChange =
    | BloggerCyclesSet of sessionId: SessionId * cycles: BloggerCycleProjectionState
    | EnforcementSet of sessionId: SessionId * enforcement: EnforcementProjectionState
    | BlogSet of sessionId: SessionId * blog: BlogProjectionState
    | PrefixEpochSet of sessionId: SessionId * epoch: ActivePrefixEpoch
    | BlogReanchored of sessionId: SessionId
    | AuxiliaryVisibilityRetired of sessionId: SessionId

[<RequireQualifiedAccess>]
type ContextFoldRejection =
    | BloggerRequestMaterializedRejected of reason: string
    | BlogObservationCommittedRejected of reason: string
    | BlogObservationsSquashedRejected of reason: string
    | BlogObservationCommittedFrameRejected of rejection: BlogFoldRejection
    | BlogObservationsSquashedFrameRejected of rejection: BlogFoldRejection
    | PrefixRebaseCommittedRejected of reason: string
    | ContextReanchoredRejected of reason: string
    | TenureReanchoredRejected of reason: string

[<RequireQualifiedAccess>]
module ContextFoldRejection =
    val fact: ContextFoldRejection -> string
    val message: ContextFoldRejection -> string

module ContextFactFold =
    val fold:
        bloggerCyclesOf: (SessionId -> BloggerCycleProjectionState option) ->
        enforcementOf: (SessionId -> EnforcementProjectionState option) ->
        blogOf: (SessionId -> BlogProjectionState option) ->
        prefixEpochOf: (SessionId -> ActivePrefixEpoch option) ->
        fact: ContextFactCases ->
            Result<ContextProjectionChange list, ContextFoldRejection>
