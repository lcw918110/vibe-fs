namespace Wanxiangshu.Git

open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop

/// Subject verbs that own the production `git` executable token (§37).
/// Source-control and read-only worktree topology callers use this boundary.
module GitSubject =

    /// Sole production spelling of the git executable name.
    [<Literal>]
    let Executable = "git"

    [<Import("execFile", "node:child_process")>]
    let private execFileJs (fileName: string) (arguments: string array) (options: obj) (callback: obj) : unit = jsNative

    [<Emit("""new Promise(function(resolve, reject) { $0($1, $2, $3, function(error, stdout) { if (error) reject(error); else resolve(stdout); }); })""")>]
    let private execFileAsync (exec: obj) (fileName: string) (arguments: string array) (options: obj) : Task<string> =
        jsNative

    let private utf8 = createObj [ "encoding", box "utf8" ]

    let private quietUtf8 =
        createObj [ "encoding", box "utf8"; "stdio", box [| "ignore"; "pipe"; "pipe" |] ]

    /// Run git with `-C directory` then `arguments`.
    let execIn (directory: string) (arguments: string array) : Task<string> =
        execFileAsync execFileJs Executable (Array.append [| "-C"; directory |] arguments) utf8

    // --- common-dir ---

    let revParseGitCommonDir (workspace: string) : Task<string> =
        task {
            let! result = execIn workspace [| "rev-parse"; "--git-common-dir" |]
            return result.Trim()
        }

    // --- tree-hash ---

    let diffHeadBinary (directory: string) : Task<string> =
        execIn directory [| "diff"; "HEAD"; "--binary"; "--no-ext-diff"; "--" |]

    let lsFilesUntracked (directory: string) : Task<string> =
        execIn directory [| "ls-files"; "--others"; "--exclude-standard" |]

    let lsFilesUntrackedZ (directory: string) : Task<string> =
        execIn directory [| "ls-files"; "--others"; "--exclude-standard"; "-z" |]

    let statusPorcelainV2Z (directory: string) : Task<string> =
        execIn directory [| "status"; "--porcelain=v2"; "-z"; "--untracked-files=all" |]

    let lsFilesStageZ (directory: string) : Task<string> =
        execIn directory [| "ls-files"; "--stage"; "-z" |]

    let hashObjectNoFilters (directory: string) (path: string) : Task<string> =
        task {
            let! result = execIn directory [| "hash-object"; "--no-filters"; "--"; path |]
            return result.Trim()
        }

    let revParseHeadTree (directory: string) : Task<string> =
        task {
            let! result = execIn directory [| "rev-parse"; "HEAD^{tree}" |]
            return result.Trim()
        }

    let tryRevParseHeadTree (directory: string) : Task<string option> =
        task {
            try
                let! output =
                    execFileAsync execFileJs Executable [| "-C"; directory; "rev-parse"; "HEAD^{tree}" |] quietUtf8

                return Some(output.Trim())
            with _ ->
                return None
        }
