namespace Wanxiangshu.Interaction.Dispatch

open System.Collections.Generic
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Execution.Delegation.Fork
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Persistence.Journal

/// JoinGuard-owned JS boundary. Reservation state is an opaque capability;
/// HostJoinGuard remains the production decision owner.
[<RequireQualifiedAccess>]
module JoinGuardSurface =

    [<Import("joinGuardReservationStore", "wanxiangli/delegate-batch")>]
    let private reservationStore: obj = jsNative

    type private ReservationCapability() =
        let keys: HashSet<string> = unbox (reservationStore?createScope())

        member _.Keys = keys

    let newReservations () : obj = ReservationCapability() :> obj

    let private outcomeView outcome : obj =
        match outcome with
        | HostJoinGuard.JoinGuardNudgeOutcome.Sent promptKey ->
            box
                {| outcome = "Sent"
                   promptKey = PromptKey.value promptKey
                   reason = null |}
        | HostJoinGuard.JoinGuardNudgeOutcome.AlreadyOutstanding ->
            box
                {| outcome = "AlreadyOutstanding"
                   promptKey = null
                   reason = null |}
        | HostJoinGuard.JoinGuardNudgeOutcome.AdmissionRejected failure ->
            box
                {| outcome = "Superseded"
                   promptKey = null
                   reason = string failure |}
        | HostJoinGuard.JoinGuardNudgeOutcome.Superseded ->
            box
                {| outcome = "Superseded"
                   promptKey = null
                   reason = null |}
        | HostJoinGuard.JoinGuardNudgeOutcome.NotSent ->
            box
                {| outcome = "NotSent"
                   promptKey = null
                   reason = null |}
        | HostJoinGuard.JoinGuardNudgeOutcome.Failed reason ->
            box
                {| outcome = "Failed"
                   promptKey = null
                   reason = reason |}

    /// Run the real JoinGuard reservation/send path and expose only its decision.
    let nudge
        (port: obj)
        (handle: obj)
        (reservations: obj)
        (session: string)
        (terminalProviderRun: string)
        (directory: obj)
        : Task<obj> =
        task {
            let journal =
                if isNull handle then
                    None
                else
                    Some((unbox<JournalHandle> handle).Journal)

            let keys: HashSet<string> =
                if isNull reservations then
                    unbox (reservationStore?createScope())
                else
                    (unbox<ReservationCapability> reservations).Keys

            let sessionId = SessionId.create session

            let! outcome =
                HostJoinGuard.nudge
                    (DispatchSurface.sessionPort port)
                    (DispatchSurface.rootWorkspaceReader directory)
                    None
                    journal
                    keys
                    (fun () -> Ok())
                    (fun () -> Ok())
                    sessionId
                    (ProviderRunIdentity.create terminalProviderRun)
                    (if isNull directory then None else Some(string directory))

            return outcomeView outcome
        }
