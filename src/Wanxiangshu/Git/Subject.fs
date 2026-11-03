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

    [<Import("execFileSync", "node:child_process")>]
    let private execFileSyncJs (fileName: string) (arguments: string array) (options: obj) : string = jsNative

    [<Emit("""new Promise(function(resolve, reject) { $0($1, $2, $3, function(error, stdout) { if (error) reject(error); else resolve(stdout); }); })""")>]
    let private execFileAsync (exec: obj) (fileName: string) (arguments: string array) (options: obj) : Task<string> =
        jsNative

    let private utf8 = createObj [ "encoding", box "utf8" ]

    let private quietUtf8 =
        createObj [ "encoding", box "utf8"; "stdio", box [| "ignore"; "pipe"; "pipe" |] ]

    /// Run git with `-C directory` then `arguments`.
    let execIn (directory: string) (arguments: string array) : Task<string> =
        execFileAsync execFileJs Executable (Array.append [| "-C"; directory |] arguments) utf8

    /// Same command as `execIn`. Stderr stays quiet so a missing repository does not print.
    let execInSync (directory: string) (arguments: string array) : string =
        execFileSyncJs Executable (Array.append [| "-C"; directory |] arguments) quietUtf8

    // --- worktree root ---

    /// The worktree root of `directory`, not the entry directory and not the common Git dir.
    let revParseShowToplevel (directory: string) : Task<string> =
        task {
            let! raw = execIn directory [| "rev-parse"; "--show-toplevel" |]
            return raw.TrimEnd('\r', '\n')
        }
