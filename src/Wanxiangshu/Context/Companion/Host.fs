namespace Wanxiangshu.Context.Companion

open System
open Wanxiangshu.Context.Trace
open Wanxiangshu.Execution.Session
open Wanxiangshu.Execution.Session.Attachment
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.OpenCode
open Wanxiangshu.Persistence.Journal

type CompanionHost
    (
        primaryId: SessionId,
        sessions: ISessionHostPort,
        ?durable: ICompanionDurablePort,
        ?satelliteRuntime: CompanionLeaseRuntime
    ) =
    let companion = Companion(?durable = durable, ?sessionId = Some primaryId)

    member _.Memory = companion.Memory
    member _.RefreshXTrace(state: XTraceProjectionState) = companion.RefreshXTrace(state)
    member _.PrimarySessionId = primaryId

    interface IDisposable with
        member _.Dispose() = ()
