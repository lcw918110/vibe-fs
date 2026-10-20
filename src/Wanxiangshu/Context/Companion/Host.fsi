namespace Wanxiangshu.Context.Companion

open System
open Wanxiangshu.Context.Trace
open Wanxiangshu.Execution.Session
open Wanxiangshu.Execution.Session.Attachment
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.OpenCode
open Wanxiangshu.Persistence.Journal

type CompanionHost =
    new:
        primaryId: SessionId *
        sessions: ISessionHostPort *
        ?durable: ICompanionDurablePort *
        ?satelliteRuntime: CompanionLeaseRuntime ->
            CompanionHost

    member Memory: CompanionMemory
    member RefreshXTrace: state: XTraceProjectionState -> unit
    member PrimarySessionId: SessionId

    interface IDisposable
