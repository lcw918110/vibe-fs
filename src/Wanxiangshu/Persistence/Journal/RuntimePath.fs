namespace Wanxiangshu.Persistence.Journal

open System.Threading.Tasks
open System
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Git

module RuntimePath =

    [<Import("resolve", "node:path")>]
    let private resolvePath (path: string) : string = jsNative

    [<Import("realpathSync", "node:fs")>]
    let private realpathSync (path: string) : string = jsNative

    [<Import("join", "node:path")>]
    let private joinPath (left: string) (right: string) : string = jsNative

    [<Import("lstatSync", "node:fs")>]
    let private lstat (path: string) (options: obj) : obj = jsNative

    [<Import("mkdirSync", "node:fs")>]
    let private mkdir (path: string) (options: obj) : unit = jsNative

    [<Import("writeFileSync", "node:fs")>]
    let private writeFile (path: string) (content: string) (options: obj) : unit = jsNative

    [<Import("readFileSync", "node:fs")>]
    let private readFile (path: string) (encoding: string) : string = jsNative

    [<Emit("$0 != null && $0.code === 'EEXIST'")>]
    let private alreadyExists (error: exn) : bool = jsNative

    let private runtimeDirectory root =
        joinPath (joinPath root "wanxiangshu") "runtimes"

    let private canonicalPath path =
        try
            realpathSync path
        with _ ->
            resolvePath path

    let private outsideGit (path: string) =
        let segments = path.Split([| '/'; '\\' |], StringSplitOptions.RemoveEmptyEntries)

        if
            segments
            |> Array.exists (fun part -> part.Equals(".git", StringComparison.OrdinalIgnoreCase))
        then
            invalidArg "workspace" "Runtime persistence cannot be placed inside .git"

        path

    // DSL-MUTABLE: resource — 主工作树位置缓存，不保存任何业务事实。
    let private roots = Collections.Generic.Dictionary<string, Task<string>>()

    let private evictFailedRoot key (task: Task<string>) =
        match roots.TryGetValue key with
        | true, cached when obj.ReferenceEquals(cached, task) -> roots.Remove key |> ignore
        | _ -> ()

    let private parsePrimaryWorktree (first: string array) =
        if first |> Array.contains "bare" then
            invalidArg "workspace" "Runtime persistence requires a working tree, not a bare repository"
        elif
            (match Array.tryHead first with
             | Some field -> not (field.StartsWith("worktree ", StringComparison.Ordinal))
             | None -> true)
        then
            invalidOp "Git did not identify the primary working tree"
        else
            (Array.head first).Substring("worktree ".Length) |> canonicalPath |> outsideGit

    let private primaryWorktree (workspace: string) : Task<string> =
        task {
            let! listing =
                task {
                    try
                        let! text = GitSubject.execIn workspace [| "worktree"; "list"; "--porcelain"; "-z" |]
                        return Some text
                    with _ ->
                        return None
                }

            match listing with
            | None -> return workspace
            | Some text ->
                return
                    text.Split([| '\u0000' |])
                    |> Array.takeWhile (fun field -> field <> "")
                    |> parsePrimaryWorktree
        }

    /// Git 只提供只读拓扑：主工作树和 linked worktree 共用主工作树下的状态。
    /// 没有 Git 仓库时使用给定目录；不回退到 .git、XDG 或旧日志。
    let stateRoot (workspace: string) : Task<string> =
        task {
            if String.IsNullOrWhiteSpace workspace then
                invalidArg "workspace" "A workspace directory is required"

            let key = workspace |> canonicalPath |> outsideGit

            let primaryTask =
                match roots.TryGetValue key with
                | true, taskValue -> taskValue
                | _ ->
                    let taskValue = primaryWorktree key
                    roots.[key] <- taskValue
                    taskValue

            let! primary =
                task {
                    try
                        return! primaryTask
                    with ex ->
                        evictFailedRoot key primaryTask
                        return raise ex
                }

            let target = joinPath primary ".Herdchestra"
            let existing = lstat target (createObj [ "throwIfNoEntry" ==> false ])

            if
                not (isNull existing)
                && (unbox<bool> (existing?isSymbolicLink ())
                    || not (unbox<bool> (existing?isDirectory ())))
            then
                invalidArg "workspace" ".Herdchestra must be a real directory, not a link or file"

            return target
        }

    /// 仅写入者初始化时创建自身忽略规则，不修改项目的 .git/info/exclude。
    let initializeStateRoot (workspace: string) : Task<string> =
        task {
            let! target = stateRoot workspace
            mkdir target (createObj [ "recursive" ==> true ])
            let ignore = joinPath target ".gitignore"

            try
                writeFile ignore "*\n" (createObj [ "flag" ==> "wx"; "encoding" ==> "utf8" ])
            with error when alreadyExists error ->
                let entry = lstat ignore (createObj [ "throwIfNoEntry" ==> false ])

                if
                    isNull entry
                    || unbox<bool> (entry?isSymbolicLink ())
                    || not (unbox<bool> (entry?isFile ()))
                    || (readFile ignore "utf8").Trim() <> "*"
                then
                    invalidOp ".Herdchestra/.gitignore must be a regular file excluding all runtime data"

            return target
        }

    let forWorkspace (workspace: string) : Task<string> =
        task {
            let! target = stateRoot workspace
            return runtimeDirectory target
        }

    let forStateRoot (stateDirectory: string) =
        stateDirectory |> canonicalPath |> outsideGit |> runtimeDirectory
