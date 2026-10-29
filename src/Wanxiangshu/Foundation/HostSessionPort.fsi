namespace Wanxiangshu.Foundation.HostSession

open Wanxiangshu.Foundation.TerminalSignal

open System
open System.Threading.Tasks
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Foundation.Outcome

type ISessionHostPort =
    abstract SubscribeTerminal: sessionId: SessionId * listener: TerminalCompletionListener -> IDisposable
    abstract SubscribeFutureTerminal: sessionId: SessionId * listener: TerminalCompletionListener -> IDisposable
    abstract SendPrompt: sessionId: SessionId * text: string * opts: SessionPromptOptions -> Task<SendOutcome>
    abstract AbortSession: sessionId: SessionId -> Task<Result<unit, string>>
    abstract InterruptAttempt: sessionId: SessionId -> Task<Result<unit, string>>
    abstract IsManagedChild: sessionId: SessionId -> bool
    abstract AbortChildren: parentId: SessionId -> Task

    abstract CreateSiblingSession:
        ownerSessionId: SessionId * physicalParentId: SessionId option * options: ChildSessionOptions ->
            Task<Result<SessionId, string>>

    abstract TryGetParentSession: sessionId: SessionId -> Task<Result<SessionId option, string>>
    /// HOST-015: resolves physical parent to the authoritative family root (using in-process proved
    /// ancestry or querying Host GetSessionParent with loop safety; query failure or cycle refuses creation).
    /// Creates the child under that root and registers in-process linkage while inheriting immediate owner language.
    abstract CreateChildSession: parentId: SessionId * options: ChildSessionOptions -> Task<Result<SessionId, string>>
    /// HOST-015: normalizes parentId to the proved physical family root so callers query the flat physical
    /// children and identify exact managed children by durable ID rather than assuming exclusive immediate sub-sessions.
    abstract ListChildren: parentId: SessionId -> Task<Result<ChildSessionInfo list, string>>
    abstract FamilyRootOf: sessionId: SessionId -> SessionId
    /// HOST-037: completes once the Host runner for this session has left its
    /// current run. OpenCode joins a prompt that arrives while a run is still
    /// finishing (after its exit decision) to that finishing run: the user
    /// message is stored but no new provider loop starts. An idle-derived
    /// continuation therefore waits here before its admission and send.
    abstract AwaitRunnerIdle: sessionId: SessionId * directory: string option -> Task
