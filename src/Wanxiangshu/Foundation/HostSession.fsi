namespace Wanxiangshu.Foundation.HostSession

open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity

type ProviderAttemptSource =
    { SessionId: SessionId
      PhysicalUserMessageId: PhysicalUserMessageId
      AuthorityRootUserMessageId: AuthorityRootUserMessageId
      ProviderRun: ProviderRunIdentity }

type ContinuationAcceptanceObserver =
    { Notify: PhysicalUserMessageId -> unit
      AttachDisposable: System.IDisposable -> unit
      /// How long the caller is willing to wait for PhysicalAccepted. A busy
      /// target lands the message only after its current turn ends, so callers
      /// that know this state a larger budget than the admission default.
      Budget: int option }

type ISessionQuiescenceGate =
    abstract BeginProviderAttempt: sessionId: SessionId -> unit
    abstract BeginToolExecution: sessionId: SessionId -> unit
    abstract EndToolExecution: sessionId: SessionId -> unit
    abstract ObservePhysicalUserMessage: sessionId: SessionId * physicalUserMessageId: PhysicalUserMessageId -> unit
    abstract ObserveIdle: sessionId: SessionId -> QuiescencePermit
    abstract TryConsume: permit: QuiescencePermit -> Result<unit, QuiescencePermitFailure>
    abstract TryRelease: permit: QuiescencePermit -> Result<unit, QuiescencePermitFailure>
    abstract RevokeCurrentAttempt: sessionId: SessionId -> unit
    abstract DropSession: sessionId: SessionId -> unit
