namespace Wanxiangshu.Git

open System.Threading.Tasks

module GitSubject =
    [<Literal>]
    val Executable: string = "git"

    val execIn: directory: string -> arguments: string array -> Task<string>
    val revParseGitCommonDir: workspace: string -> Task<string>
    val diffHeadBinary: directory: string -> Task<string>
    val lsFilesUntracked: directory: string -> Task<string>
    val lsFilesUntrackedZ: directory: string -> Task<string>
    val statusPorcelainV2Z: directory: string -> Task<string>
    val lsFilesStageZ: directory: string -> Task<string>
    val hashObjectNoFilters: directory: string -> path: string -> Task<string>
    val revParseHeadTree: directory: string -> Task<string>
    val tryRevParseHeadTree: directory: string -> Task<string option>
