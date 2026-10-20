namespace Wanxiangshu.Context.Companion

open System.Threading.Tasks
open Wanxiangshu.Context.Trace
open Wanxiangshu.Foundation.Identity

type CompanionMemory = { XTrace: XTraceProjectionState }

type ICompanionDurablePort =
    abstract Load: sessionId: SessionId -> Task<Result<CompanionMemory option, string>>
