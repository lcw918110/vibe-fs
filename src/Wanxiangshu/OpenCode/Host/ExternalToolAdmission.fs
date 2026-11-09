namespace Wanxiangshu.OpenCode

open Wanxiangshu.Host
open Wanxiangshu.Foundation.HostSession

open System
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Participant.Provider

/// Runtime admission for Host-declared external tools (capability-enforcement-007).
///
/// The provider-visible projection forwards a Host-declared MCP namespace so the
/// plugin stops masking the Host's own tool registry; execution is a different
/// question and is answered here: no office capability token admits an external
/// integration tool, so a managed session's call is refused before any effect.
/// This gate asks the process-wide declared set, which only grows while the
/// process lives, so it always covers the permission map of every session this
/// process projected — including one written before a reload withdrew the
/// server (capability-enforcement-002: a call that bypasses the schema is
/// blocked at the runtime gate; a namespace no live map advertises is refused,
/// never silently admitted).
[<RequireQualifiedAccess>]
module ExternalToolAdmission =

    [<Import("ExternalNamespaces", "wanxiangli/external-namespaces")>]
    let private declaredExternal: obj = jsNative

    /// The authority of an external tool call is the session's canonical office
    /// role and nothing else (capability-enforcement-004). A call carries no
    /// provider-run or review identity, and this gate must never resolve one.
    let private admissionContext (sessionId: string) : HostToolContext =
        { SessionId = sessionId
          Agent = None
          ToolCallId = None
          ProviderRunId = None
          PromptText = None
          AttachAbort = fun _ -> id }

    /// Refuse the call before its body runs: fail closed, zero side effects.
    ///
    /// A resolved office role is the only thing this gate can refuse: no office capability
    /// admits an external integration tool. A session with no office profile is a Host
    /// session, and it keeps the Host's own MCP surface (capability-enforcement-007).
    let before (runtime: ToolRuntimeScope) (sessionId: string) (toolName: string) : Task<unit> =
        task {
            if
                not (String.IsNullOrWhiteSpace sessionId)
                && (declaredExternal?matches (toolName) |> unbox<bool>)
            then
                match runtime.RoleFor(admissionContext sessionId) with
                | Some _ ->
                    invalidOp (
                        ProviderProse.render
                            (ProviderLanguageBinding.forSessionText sessionId)
                            "tool/registry/denied-external"
                            (Map [ "tool", toolName ])
                    )
                | None -> ()
        }
