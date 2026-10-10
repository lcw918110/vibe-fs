namespace Wanxiangshu.OpenCode

open Wanxiangshu.Interaction.Concern

[<RequireQualifiedAccess>]
module ConcernTools =
    val admission: ToolAdmission

    val specs:
        factory: HostToolFactory ->
        journal: ConcernJournalPort option ->
        toast: (string -> string -> unit) option ->
            ToolSpec list
