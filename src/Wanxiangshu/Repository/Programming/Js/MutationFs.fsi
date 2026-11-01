namespace Wanxiangshu.Repository.Programming.Js

module JsMutationFs =
    val resolveToolPath: root: string -> path: string -> string
    val existsPath: path: string -> bool
    val commitPlan: root: string -> plan: JsCommitMutation list -> Result<unit, JsFailure>
    val rollbackPlan: root: string -> plan: JsRollbackMutation list -> unit
