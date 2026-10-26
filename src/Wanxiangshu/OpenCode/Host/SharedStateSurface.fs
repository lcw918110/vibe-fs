namespace Wanxiangshu.OpenCode

open Fable.Core
open Wanxiangshu.Foundation.HostSession

/// JS-native boundary for host-boundary-010 shared cross-instance state and
/// host-boundary-031 root workspace first-binding. The physical Map/Set
/// singletons and RootWorkspace runtime stay opaque. Every operation
/// closes over the same module-level singleton, so a mutation made through
/// one import is visible through another — the behavioral proof that
/// SessionParents / RootWorkspace are live singletons
/// shared across plugin instances (root + worktree), not per-instance copies.
module SharedStateSurface =

    [<Fable.Core.Import("sessionTopologyStore", "wanxiangli/ownership")>]
    let private topologyStore: obj = jsNative

    /// SessionParents: cross-instance parent registry.
    let putSessionParent (childId: string) (parentId: string) : unit =
        Fable.Core.JsInterop.(?) topologyStore "putParent" (childId, parentId)

    let getSessionParent (childId: string) : string =
        Fable.Core.JsInterop.(?) topologyStore "getParent" childId

    let clearSessionParents () : unit =
        Fable.Core.JsInterop.(?) topologyStore "clearParents" ()

    let tryBindRootWorkspace (path: string) : bool =
        (RootWorkspaceProcess.local ()).Binder.TryBind(Option.ofObj path)

    let tryGetRootWorkspace () : string =
        (RootWorkspaceProcess.local ()).Reader.TryRead() |> Option.defaultValue null

    let firstBoundRootWorkspace (candidates: string array) : string =
        let runtime = RootWorkspaceRuntime()

        candidates
        |> Array.iter (fun candidate -> runtime.Binder.TryBind(Option.ofObj candidate) |> ignore)

        runtime.Reader.TryRead() |> Option.defaultValue null

    let selectContinuationDirectory (candidate: string) (candidateExists: bool) (rootWorkspace: string) : string =
        let reader =
            { new IRootWorkspaceReader with
                member _.TryRead() = Option.ofObj rootWorkspace }

        RootWorkspaceDirectory.select (fun path -> candidateExists && path = candidate) reader (Option.ofObj candidate)
        |> Option.defaultValue null
