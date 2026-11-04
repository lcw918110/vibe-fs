namespace Wanxiangshu.Participant.Provider.Attempt.Fallback

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Participant.Provider.Attempt
open Wanxiangshu.Foundation.Identity

/// Durable provider failure projection for one Logical Run.
type ProviderFailureProjection =
    { LogicalRunId: LogicalRunId
      AuthorityRootUserMessageId: AuthorityRootUserMessageId
      Budget: ProviderFailureBudget.FailureBudget
      RecentFailureKeys: string list
      Exhausted: bool
      LastTransitionWasSuccess: bool }

/// Why a failure record was not applied.
type ProviderFailureAdvanceRejection =
    | AlreadyObserved
    | AlreadyExhausted
    | DifferentRun
    | NoActiveBudget
    | InvalidTransition

/// How an already-counted failure is answered from the record.
[<RequireQualifiedAccess>]
type ProviderFailureReplay =
    | LatestMayRetry
    | LatestExhausted
    | Superseded

/// The failure budget law (exact attempt dedupe, single-step advance, exhaustion,
/// success reset) is owned by `wanxiangli/failure-budget` (CUT-14 ②). This module
/// only maps the host's typed projection to the library's plain state and back;
/// the run identities are carried over unchanged.
module ProviderFailureProjection =

    [<Import("ProviderFailureBudget", "wanxiangli/failure-budget")>]
    let private budgetLibraryClass: obj = jsNative

    [<Emit("new $0()")>]
    let private construct (cls: obj) : obj = jsNative

    let private library = construct budgetLibraryClass

    [<Emit("$0[$1]")>]
    let private field (_value: obj) (_name: string) : obj = jsNative

    [<Emit("Array.from($0)")>]
    let private arrayOf (_value: obj) : string[] = jsNative

    let private stateView (current: ProviderFailureProjection) : obj =
        createObj
            [ "logicalRun" ==> LogicalRunId.value current.LogicalRunId
              "authorityRoot"
              ==> AuthorityRootUserMessageId.value current.AuthorityRootUserMessageId
              "consecutiveFailureCount" ==> current.Budget.ConsecutiveFailureCount
              "recentFailureKeys" ==> Array.ofList current.RecentFailureKeys
              "exhausted" ==> current.Exhausted
              "lastTransitionWasSuccess" ==> current.LastTransitionWasSuccess ]

    /// The one mapping from the host projection to the library's plain state.
    /// Callers that only need to hand a projection across the boundary (the
    /// durable budget adapter) reuse this instead of reassembling the fields.
    let toLibraryState (current: ProviderFailureProjection) : obj = stateView current

    let private stateOf (current: ProviderFailureProjection) (state: obj) : ProviderFailureProjection =
        { current with
            Budget = { ConsecutiveFailureCount = unbox<int> (field state "consecutiveFailureCount") }
            RecentFailureKeys = arrayOf (field state "recentFailureKeys") |> List.ofArray
            Exhausted = unbox<bool> (field state "exhausted")
            LastTransitionWasSuccess = unbox<bool> (field state "lastTransitionWasSuccess") }

    let forAuthority (logicalRunId: LogicalRunId) (authorityRoot: AuthorityRootUserMessageId) =
        { LogicalRunId = logicalRunId
          AuthorityRootUserMessageId = authorityRoot
          Budget = ProviderFailureBudget.forNewAuthorityRoot
          RecentFailureKeys = []
          Exhausted = false
          LastTransitionWasSuccess = false }

    let private rejectionOf (name: string) =
        match name with
        | "AlreadyObserved" -> AlreadyObserved
        | "AlreadyExhausted" -> AlreadyExhausted
        | "DifferentRun" -> DifferentRun
        | _ -> InvalidTransition

    /// Apply one failure record.
    let applyFailure
        (identity: FailedProviderAttemptIdentity)
        (consecutiveFailureCount: int)
        (current: ProviderFailureProjection)
        : Result<ProviderFailureProjection, ProviderFailureAdvanceRejection> =
        let result: obj =
            library?applyFailure (
                FailedProviderAttemptIdentity.view identity,
                consecutiveFailureCount,
                stateView current
            )

        if unbox<bool> (field result "ok") then
            Ok(stateOf current (field result "state"))
        else
            Error(rejectionOf (unbox<string> (field result "rejection")))

    /// Terminal exhausted state.
    let applyExhausted (current: ProviderFailureProjection) =
        stateOf current (library?applyExhausted (stateView current))

    /// Success clears the budget streak.
    let recordSuccess (current: ProviderFailureProjection) =
        stateOf current (library?recordSuccess (stateView current))

    let mayRetry (budgetLimit: int) (current: ProviderFailureProjection) : bool =
        library?mayRetry (budgetLimit, stateView current)

    /// An already-counted failure: only the latest exact attempt may still retry.
    let replay
        (budgetLimit: int)
        (identity: FailedProviderAttemptIdentity)
        (current: ProviderFailureProjection)
        : ProviderFailureReplay =
        match
            (library?replay (budgetLimit, FailedProviderAttemptIdentity.view identity, stateView current): string)
        with
        | "RetryAuthorized" -> ProviderFailureReplay.LatestMayRetry
        | "RetryExhausted" -> ProviderFailureReplay.LatestExhausted
        | _ -> ProviderFailureReplay.Superseded
