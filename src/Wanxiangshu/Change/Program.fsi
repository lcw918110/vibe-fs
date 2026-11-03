namespace Wanxiangshu.Change

open System.Threading.Tasks

module internal ChangeInterop =
    val mapTask: ('value -> 'mapped) -> Task<Result<'value, 'error>> -> Task<obj>
    val verdict: obj -> OrchestratorVerdict
    val collaborators: OrchestratorProgramDeps -> obj

/// ORCH-004/005/006/007: Relay quality candidate → deterministic artifact
/// admission → invalidation/rebase/loop when needed → short-CAS ff-only
/// publish.
module OrchestratorProgram =
    val run: deps: OrchestratorProgramDeps -> job: ManagerJob -> Task<OrchestratorVerdict>
