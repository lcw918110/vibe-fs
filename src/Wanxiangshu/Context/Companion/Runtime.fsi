namespace Wanxiangshu.Context.Companion

open Wanxiangshu.Context.Trace
open Wanxiangshu.Foundation.Identity

type Companion =
    new: ?initialMemory: CompanionMemory * ?durable: ICompanionDurablePort * ?sessionId: SessionId -> Companion

    member Memory: CompanionMemory
    member RefreshXTrace: state: XTraceProjectionState -> unit
