namespace Wanxiangshu.Sphinx.V2.Hosts.OpenCode

open System.Threading.Tasks
open Wanxiangshu.Execution.Delegation.SyncDelegate
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Sphinx.V2.Core

/// Borrows the original managed delegation owner. The composition must admit
/// the durable intent before calling this internal effect boundary.
type OpenCodeHostPort internal (runtime: SyncDelegateRuntime) =
    member _.Capabilities() : string list = DispatchPolicy.hostCapabilities

    /// `context` is the neutral host turn context resolved for this inquiry:
    /// it names the session that owns the dispatch and the workspace the
    /// dispatched prompt belongs to.
    member internal _.Dispatch
        (owner: SessionId, request: DispatchRequestedBody, context: Wanxiangshu.Host.Ports.TurnContext)
        : SyncDelegateObservedExecution =
        match runtime.ValidateObservedOwner owner with
        | Error reason ->
            { Admission = Task.FromResult(SyncDelegateObservedAdmission.NotDispatched reason)
              Completion = Task.FromResult(Error reason) }
        | Ok() ->
            let prompt: Wanxiangshu.Host.Ports.PromptRequest =
                { Text = DispatchPolicy.dispatchedPromptText request
                  Route = context.Route
                  Role = context.Role
                  Workspace = context.Workspace
                  Correlation = None
                  ToolPolicy = None
                  Delivery = None }

            runtime.InvokeObservedPrepared(
                owner,
                prompt.Text,
                (fun () -> Task.FromResult(LlmFacing.instruction prompt.Text))
            )

/// The host boundary of the Sphinx v2 OpenCode adapter: product admission and the
/// host effect in one place, so a body that is not a dispatch request never
/// reaches the host.
[<RequireQualifiedAccess>]
module HostPort =
    /// Product capabilities this host may advertise (the product decision lives in Core).
    let capabilities () : string list = DispatchPolicy.hostCapabilities

    /// Why a dispatch was refused before any host call.
    let refused (reason: string) : SyncDelegateObservedExecution =
        { Admission = Task.FromResult(SyncDelegateObservedAdmission.NotDispatched reason)
          Completion = Task.FromResult(Error reason) }

    /// Product admission + the host effect, in one boundary: a body that is not a
    /// dispatch request is refused here and never reaches the host.
    let startDispatch
        (runtime: SyncDelegateRuntime)
        (owner: SessionId)
        (body: Result<InquiryEventBody, string>)
        (context: Wanxiangshu.Host.Ports.TurnContext)
        : SyncDelegateObservedExecution =
        // The product decides admission in one step: a decode failure and an
        // admission refusal are the same kind of answer — nothing was
        // dispatched — and the adapter only maps that answer to the host's
        // refusal effect.
        match DispatchPolicy.decide body with
        | Error reason -> refused reason
        | Ok request -> OpenCodeHostPort(runtime).Dispatch(owner, request, context)
