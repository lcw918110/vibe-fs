namespace Wanxiangshu.Repository.Programming.Js

open System.Threading.Tasks
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Host
open Wanxiangshu.Participant.Provider

/// HNI-09: the product decisions of one generated js-* tool.
///
/// Everything the host-neutral js domain owns about a generated tool lives
/// here: which role may call it, which directory it executes in, what its
/// arguments are described as, and how a view declaration is validated and
/// committed. The OpenCode binding (`JsToolSpec`) keeps the host-wire
/// plumbing — the argument codec, the sandbox call, the host `ToolSpec` — and
/// asks this module for the decisions.
[<RequireQualifiedAccess>]
module JsToolPolicy =

    /// The directory one session's js-* surface executes in.
    ///
    /// The Host creates a Manager and her forked children rooted at the directory
    /// their owner registered — for a Change road that is the job's own worktree
    /// (`OrchestratorHost.directoryFor` → `HostForkRuntime.CreateChildSession`).
    /// The surface must execute where its session lives: resolving the plugin's
    /// project directory instead writes every road mutation into the target's own
    /// working tree, leaves the job worktree empty, and turns the candidate into a
    /// no-op commit whose publish rebase cannot conflict.
    ///
    /// A registered path is used as given, never existence-checked into the
    /// fallback: a worktree released under a live session must fail the write
    /// closed, not silently redirect it onto the target's checkout.
    let rootForSession
        (sessionDirectories: System.Collections.Generic.Dictionary<string, string>)
        (fallback: string)
        (sessionId: string)
        : string =
        match System.String.IsNullOrWhiteSpace sessionId, sessionDirectories.TryGetValue sessionId with
        | false, (true, path) when not (System.String.IsNullOrWhiteSpace path) -> path
        | _ -> fallback

    /// Same predicate as StaticTools.jsPermission and JsToolGenerator: the
    /// role's office permissions must project a non-empty programming surface
    /// (JsPlan expands inside ofToolCapabilities). No second permission list.
    let admitsRole (roleName: string) (role: Role) : bool =
        (string role).ToLowerInvariant() = roleName.ToLowerInvariant()
        && JsCapability.admitsProgrammingSurface (OfficeCapability.permissions role)

    /// Manager cannot read by default.
    let defaultReadCheck
        (roleName: string)
        (readCheck: ('context -> Task<bool>) option)
        : ('context -> Task<bool>) option =
        match readCheck with
        | None when roleName.Trim().ToLowerInvariant() = "manager" -> Some(fun _ -> Task.FromResult false)
        | configured -> configured

    let argNameProse () : string =
        ProviderProse.render (ProviderLanguageBinding.readGlobalPreference ()) "tool/js-program/arg-name" Map.empty

    /// The prose of the `program` argument, already localized for the host.
    let argProgramProse () : string =
        JsDescriptionAssets.argProgram (ProviderLanguageBinding.readGlobalPreference ())

    let argIntentProse () : string =
        ProviderProse.render (ProviderLanguageBinding.readGlobalPreference ()) "tool/js-program/arg-intent" Map.empty

    let argEstimateProse () : string =
        ProviderProse.render (ProviderLanguageBinding.readGlobalPreference ()) "tool/js-program/arg-estimate" Map.empty

    /// The prose of the refusal of a call that carries no state at all.
    let missingProgramProse () : string =
        JsDescriptionAssets.missingProgram (ProviderLanguageBinding.readGlobalPreference ())

    /// The refusal tool result when the required Host program argument is absent.
    let missingProgramResult () : string =
        ToolResultText.tomlObject [ "error", ToolResultText.TString(missingProgramProse ()) ]

    let renderCommitted
        (sessionId: string)
        (callIdentity: (string * string) option)
        (declaration: obj)
        outcome
        : string =
        JsToolsResult.render outcome
