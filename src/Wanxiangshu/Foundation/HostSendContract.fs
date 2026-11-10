namespace Wanxiangshu.Foundation.HostSession

open System.Threading.Tasks
open Wanxiangshu.Foundation.Identity

/// What a detached prompt_async's eventual transport verdict means for the
/// dispatch owner. OwnedSettled: the caller-visible path (sendTask or a
/// synchronous throw) already produced an outcome — the observer must not
/// adjudicate a second time. Refused: the transport proved the payload was
/// never accepted — the owner may abandon the claim. OutcomeUnknown: the
/// result cannot be read either way — the claim stays pending on durable
/// evidence and must never be resent or assumed lost.
[<RequireQualifiedAccess>]
type DetachedSendVerdict =
    | OwnedSettled
    | Refused of reason: string
    | OutcomeUnknown of reason: string

/// Out-of-band delivery channel a SendPrompt caller leaves for the eventual
/// detached enqueue result. The owning session's dispatch layer registers the
/// callback keyed by its own PromptKey; a send that carries no listener simply
/// has no owner for a late verdict.
type DetachedSendListener = DetachedSendVerdict -> Task

type SessionPromptOptions =
    {
        Model: HostModel option
        Agent: string option
        Directory: string option
        Metadata: obj option
        /// Host delivery verb chosen by the product. None keeps the host default.
        Delivery: string option
        Tools: Map<string, bool> option
        /// Out-of-band listener for the eventual detached enqueue result. None
        /// when the caller's returned sendTask settles the send itself.
        DetachedListener: DetachedSendListener option
    }

type ChildSessionOptions =
    { Title: string option
      Agent: string option
      Directory: string option }

type ChildSessionInfo =
    { SessionId: SessionId
      ParentSessionId: SessionId option
      Agent: string option
      Title: string option }

/// OpenCode-side record used by emitted satellite surfaces. Same fields as
/// ChildSessionInfo; convert at the port boundary.
type OpenCodeChildInfo =
    { SessionId: SessionId
      ParentSessionId: SessionId option
      Agent: string option
      Title: string option }

module ChildSession =
    let toInfo (child: OpenCodeChildInfo) : ChildSessionInfo =
        { SessionId = child.SessionId
          ParentSessionId = child.ParentSessionId
          Agent = child.Agent
          Title = child.Title }
