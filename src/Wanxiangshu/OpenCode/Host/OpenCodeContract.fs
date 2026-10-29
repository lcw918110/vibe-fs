namespace Wanxiangshu.OpenCode

open Wanxiangshu.Foundation.HostSession

open System.Threading.Tasks
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Foundation.Outcome

type IPromptPort =
    abstract SendPrompt:
        sessionId: SessionId -> promptText: string -> options: SessionPromptOptions -> Task<SendOutcome>

type IOpenCodePort =
    inherit IPromptPort
    abstract AbortSession: sessionId: SessionId -> Task<Result<unit, string>>

    abstract CreateSession:
        parentId: SessionId option -> options: ChildSessionOptions -> Task<Result<SessionId, string>>

    abstract GetSessionParent: sessionId: SessionId -> Task<Result<SessionId option, string>>
    abstract CreateChildSession: parentId: SessionId -> options: ChildSessionOptions -> Task<Result<SessionId, string>>
    abstract ListChildren: parentId: SessionId -> Task<Result<ChildSessionInfo list, string>>
    abstract CloseChildSession: childId: SessionId -> Task<Result<unit, string>>
    /// HOST-037: true while the Host still reports this session busy (its
    /// runner has not reached idle). A Host without a status API reports false.
    abstract IsRunnerBusy: sessionId: SessionId -> directory: string option -> Task<Result<bool, string>>
