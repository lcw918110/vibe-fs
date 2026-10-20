namespace Wanxiangshu.Context.Prefix

open System.Threading.Tasks
open Wanxiangshu.Participant.Provider.Attempt

[<RequireQualifiedAccess>]
module XWireSurface =

    val coveredPrefixDigest: projection: obj -> cutoff: int -> string

    val pendingPlan: input: obj -> obj

    val bindProviderRun: pending: obj -> providerRun: string -> obj

    /// Typed owner bridges for the OpenCode recovery owner surface: wrap/unwrap
    /// the opaque JS plan handles and project the semantic JSON views. No
    /// admission or binding policy lives here.
    val internal pendingPlanView: plan: PendingAttemptPlan -> obj

    val internal boundPlanView: plan: AttemptPlan -> obj

    val internal wrapPendingPlan: plan: PendingAttemptPlan -> obj

    val internal unwrapPendingPlan: handle: obj -> PendingAttemptPlan

    val internal wrapBoundPlan: plan: AttemptPlan -> obj

    val internal unwrapBoundPlan: handle: obj -> AttemptPlan

    val suppressHostMessagesByIds: rawMessages: obj array -> hostMessageIds: string array -> obj array

    val transform: input: obj -> obj

    val reconcile: input: obj -> obj
