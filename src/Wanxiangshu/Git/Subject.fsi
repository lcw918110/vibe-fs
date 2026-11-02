namespace Wanxiangshu.Git

open System.Threading.Tasks

module GitSubject =
    [<Literal>]
    val Executable: string = "git"

    val execIn: directory: string -> arguments: string array -> Task<string>
    /// Same command as `execIn`. Stderr stays quiet so a missing repository does not print.
    val execInSync: directory: string -> arguments: string array -> string
    /// `rev-parse --show-toplevel`: the worktree root of `directory`, line terminator removed.
    val revParseShowToplevel: directory: string -> Task<string>
