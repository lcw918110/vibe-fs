namespace Wanxiangshu.Repository.Programming.Js

open Fable.Core
open Fable.Core.JsInterop

/// Ephemeral staging, all-or-nothing commit and rollback, bridged to
/// `wanxiangli/runtime` (CUT-24). The write/rollback algorithm lives in the
/// library; this module keeps the main-repo signature stable. Durable facts
/// never live here; EventStore owns them (JS-012).
module JsMutationFs =

    [<Import("Filesystem", "wanxiangli/runtime")>]
    let private Filesystem: obj = jsNative

    [<Emit("$0({ root: $1 })")>]
    let private createFilesystem (factory: obj) (root: string) : obj = jsNative

    /// Commit-plan mutation as the library's staged shape: a rewrite's
    /// originalText is the expectedCurrent content.
    let private commitToJs (mutation: JsCommitMutation) : obj =
        match mutation with
        | JsCommitMutation.RewriteFile(path, expectedCurrent, newText) ->
            createObj
                [ "kind" ==> "rewrite"
                  "path" ==> path
                  "originalText" ==> expectedCurrent
                  "newText" ==> newText ]
        | JsCommitMutation.CreateFile(path, newText) ->
            createObj [ "kind" ==> "create"; "path" ==> path; "text" ==> newText ]

    /// Rollback-plan mutation as the library's staged shape: a restore maps
    /// to Rewrite(originalText = text to restore, newText = expectedCurrent);
    /// a removeCreated maps to Create(expectedCurrent).
    let private rollbackToJs (mutation: JsRollbackMutation) : obj =
        match mutation with
        | JsRollbackMutation.RestoreFile(path, expectedCurrent, originalText) ->
            createObj
                [ "kind" ==> "rewrite"
                  "path" ==> path
                  "originalText" ==> originalText
                  "newText" ==> expectedCurrent ]
        | JsRollbackMutation.RemoveCreatedFile(path, expectedCurrent) ->
            createObj [ "kind" ==> "create"; "path" ==> path; "text" ==> expectedCurrent ]

    let private ofResult (result: obj) : Result<unit, JsFailure> =
        if unbox<bool> (result?ok) then
            Ok()
        else
            Error(JsFailure.ofWire (string (result?code)) (string (result?reason)))

    /// Resolve a tool path under root: relative paths join root; absolute
    /// paths resolve as-is (the bindings enforce the inside-root boundary).
    let resolveToolPath (root: string) (path: string) : string =
        let api = createFilesystem Filesystem root
        unbox<string> (api?resolveToolPath (path))

    /// Existence probe used by preflight (JS-013). Root-independent.
    let existsPath (path: string) : bool =
        let rootlessApi = createFilesystem Filesystem ""
        unbox<bool> (rootlessApi?existsPath (path))

    let undoIfMatches (root: string) (path: string) (expectedCurrent: string) (restoreTo: string option) : unit =
        let api = createFilesystem Filesystem root

        let restore =
            match restoreTo with
            | Some text -> box text
            | None -> null

        api?undoIfMatches (path, expectedCurrent, restore)

    let commitPlan (root: string) (plan: JsCommitMutation list) : Result<unit, JsFailure> =
        let api = createFilesystem Filesystem root
        let mutations = plan |> List.map commitToJs |> Array.ofList
        ofResult (api?applyCommit (mutations))

    let rollbackPlan (root: string) (plan: JsRollbackMutation list) : unit =
        let api = createFilesystem Filesystem root
        let mutations = plan |> List.map rollbackToJs |> Array.ofList
        api?applyRollback (mutations)
