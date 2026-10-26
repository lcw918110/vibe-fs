namespace Wanxiangshu.Execution.Delegation.Fork

open System.Collections.Generic
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Outcome
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Interaction.Dispatch
open Wanxiangshu.Foundation.HostSession
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Persistence.Journal

/// EXEC-016: join-capable roles must join outstanding work before terminal idle.
module HostJoinGuard =

    [<Import("joinGuardReservationStore", "wanxiangli/delegate-batch")>]
    let private reservationStore: obj = jsNative

    [<RequireQualifiedAccess>]
    type JoinGuardNudgeOutcome =
        | Sent of PromptKey
        | AlreadyOutstanding
        | AdmissionRejected of QuiescencePermitFailure
        | Superseded
        | NotSent
        | Failed of reason: string

    let private hasOutstandingJoinClaimOnJournal
        (journal: AgentJournal)
        (targetSessionId: SessionId)
        (terminalProviderRun: ProviderRunIdentity)
        =
        let payloadDigest =
            PromptAuthority.gateNudgePayloadDigest RuntimeNudge.BackgroundJoin terminalProviderRun

        AgentProjection.tryFind targetSessionId (AgentJournal.snapshot journal).AgentProjections
        |> Option.bind (fun session -> session.PromptAuthority)
        |> Option.map (fun authority ->
            authority.PendingClaims
            |> Map.exists (fun _ claim ->
                claim.Origin = PromptAuthority.PromptOrigin.Continuation PromptAuthority.ContinuationKind.JoinGuard
                && claim.PayloadDigest = payloadDigest))
        |> Option.defaultValue false

    let private hasOutstandingJoinClaim
        (port: HostJoinGuardJournalPort option)
        (journal: AgentJournal option)
        (targetSessionId: SessionId)
        (terminalProviderRun: ProviderRunIdentity)
        =
        match port, journal with
        | Some p, _ -> p.HasOutstandingJoinClaim targetSessionId terminalProviderRun
        | None, Some j -> hasOutstandingJoinClaimOnJournal j targetSessionId terminalProviderRun
        | None, None -> false

    /// One reservation per exact terminal occasion. A fresh ProviderRun is a
    /// fresh reminder opportunity while outstanding work still exists.
    let private nudgeKey (targetSessionId: SessionId) (terminalProviderRun: ProviderRunIdentity) : string =
        unbox<string> (
            reservationStore?nudgeKey (SessionId.value targetSessionId, ProviderRunIdentity.value terminalProviderRun)
        )

    let private tryReserveNudge
        (port: HostJoinGuardJournalPort option)
        (journal: AgentJournal option)
        (nudgeKeys: HashSet<string>)
        (sessionId: SessionId)
        (terminalProviderRun: ProviderRunIdentity)
        : string option =
        let sid = SessionId.value sessionId
        let run = ProviderRunIdentity.value terminalProviderRun
        let hasClaim () = hasOutstandingJoinClaim port journal sessionId terminalProviderRun
        let reserved: obj = reservationStore?tryReserve (nudgeKeys, sid, run, hasClaim)
        if isNull reserved then None else Some(unbox<string> reserved)

    let private releaseNudgeKey (nudgeKeys: HashSet<string>) (key: string) : unit =
        reservationStore?release (nudgeKeys, key) |> ignore

    let private sendReservedNudge
        (sessionPort: ISessionHostPort)
        (rootWorkspace: IRootWorkspaceReader)
        (durable: AgentJournal)
        (nudgeKeys: HashSet<string>)
        (physicalAdmission: unit -> Result<unit, QuiescencePermitFailure>)
        (releaseAdmission: unit -> Result<unit, QuiescencePermitFailure>)
        (key: string)
        (sessionId: SessionId)
        (terminalProviderRun: ProviderRunIdentity)
        (directory: string option)
        : Task<JoinGuardNudgeOutcome> =
        task {
            let releaseKey () = releaseNudgeKey nudgeKeys key

            let! sent =
                HostSessionNudge.trySendGateContinuationWithAdmission
                    physicalAdmission
                    releaseAdmission
                    sessionPort
                    rootWorkspace
                    sessionId
                    (ProviderProse.documentFor sessionId RuntimeNudge.BackgroundJoin Map.empty)
                    PromptAuthority.ContinuationKind.JoinGuard
                    directory
                    (Some durable)
                    RuntimeNudge.BackgroundJoin
                    terminalProviderRun
                    PromptDispatcher.AwaitMode.Await

            match sent with
            | HostSessionNudge.IdleContinuationOutcome.Sent promptKey -> return JoinGuardNudgeOutcome.Sent promptKey
            | HostSessionNudge.IdleContinuationOutcome.AlreadyAdmitted ->
                return JoinGuardNudgeOutcome.AlreadyOutstanding
            | HostSessionNudge.IdleContinuationOutcome.AdmissionRejected failure ->
                releaseKey ()
                return JoinGuardNudgeOutcome.AdmissionRejected failure
            | HostSessionNudge.IdleContinuationOutcome.Retired ->
                releaseKey ()
                return JoinGuardNudgeOutcome.Superseded
            | HostSessionNudge.IdleContinuationOutcome.NotSent _ ->
                releaseKey ()
                return JoinGuardNudgeOutcome.NotSent
            | HostSessionNudge.IdleContinuationOutcome.Failed error ->
                releaseKey ()
                return JoinGuardNudgeOutcome.Failed error
        }

    let private nudgeWithJournal
        (sessionPort: ISessionHostPort)
        (rootWorkspace: IRootWorkspaceReader)
        (port: HostJoinGuardJournalPort option)
        (durable: AgentJournal)
        (nudgeKeys: HashSet<string>)
        (physicalAdmission: unit -> Result<unit, QuiescencePermitFailure>)
        (releaseAdmission: unit -> Result<unit, QuiescencePermitFailure>)
        (sessionId: SessionId)
        (terminalProviderRun: ProviderRunIdentity)
        (directory: string option)
        : Task<JoinGuardNudgeOutcome> =
        task {
            let reserved =
                tryReserveNudge port (Some durable) nudgeKeys sessionId terminalProviderRun

            match reserved with
            | None -> return JoinGuardNudgeOutcome.AlreadyOutstanding
            | Some key ->
                return!
                    sendReservedNudge
                        sessionPort
                        rootWorkspace
                        durable
                        nudgeKeys
                        physicalAdmission
                        releaseAdmission
                        key
                        sessionId
                        terminalProviderRun
                        directory
        }

    /// Send JoinGuard Continuation. The business caller has already proven that
    /// background work remains outstanding. Transport dedupes only the exact
    /// terminal occasion and consumes the fresh idle permit at physical send.
    let nudge
        (sessionPort: ISessionHostPort)
        (rootWorkspace: IRootWorkspaceReader)
        (port: HostJoinGuardJournalPort option)
        (journal: AgentJournal option)
        (nudgeKeys: HashSet<string>)
        (physicalAdmission: unit -> Result<unit, QuiescencePermitFailure>)
        (releaseAdmission: unit -> Result<unit, QuiescencePermitFailure>)
        (sessionId: SessionId)
        (terminalProviderRun: ProviderRunIdentity)
        (directory: string option)
        : Task<JoinGuardNudgeOutcome> =
        task {
            match journal with
            | None -> return JoinGuardNudgeOutcome.Failed "Join guard nudge requires an AgentJournal"
            | Some durable ->
                return!
                    nudgeWithJournal
                        sessionPort
                        rootWorkspace
                        port
                        durable
                        nudgeKeys
                        physicalAdmission
                        releaseAdmission
                        sessionId
                        terminalProviderRun
                        directory
        }
