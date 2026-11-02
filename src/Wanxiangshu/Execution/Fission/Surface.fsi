namespace Wanxiangshu.Execution.Fission

open System.Threading.Tasks

/// JS test access to product admission and runtime cleanup.
module FissionSurface =

    val parsePrompt: prompts: string array -> obj
    val createAdmission: deps: obj -> FissionAdmissionRuntime
    val admit: runtime: FissionAdmissionRuntime -> ownerSessionId: string -> parsed: obj -> Task<obj>
    val isActive: runtime: FissionAdmissionRuntime -> ownerSessionId: string -> bool
    val release: runtime: FissionAdmissionRuntime -> ownerSessionId: string -> unit
    val markSilentInterrupt: ownerSessionId: string -> unit
    val isSilentInterrupt: ownerSessionId: string -> bool
    val tryConsumeSilentInterrupt: ownerSessionId: string -> bool
    val clearSilentInterrupt: ownerSessionId: string -> unit
    val clearOwner: ownerSessionId: string -> unit
