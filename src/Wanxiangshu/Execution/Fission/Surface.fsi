namespace Wanxiangshu.Execution.Fission

open System.Threading.Tasks

/// JS-native semantic surface for intra-participant Fission laws. Admission
/// runtimes are opaque Host capabilities; lifecycle observations and algebraic
/// state cross as plain JS data only.
module FissionSurface =

    val parsePrompt: prompts: string array -> obj
    val completionTargets: laneCount: int -> affinity: obj -> int array
    val deliveryEmpty: laneCount: int -> obj
    val deliveryMark: completionId: string -> laneIndex: int -> delivery: obj -> obj
    val deliveryPendingTargets: completionId: string -> delivery: obj -> int array
    val workBundleEmpty: obj
    val workBundleAdd: laneIndex: int -> viewRef: string -> bundle: obj -> obj
    val workBundleMerge: left: obj -> right: obj -> obj
    val workBundleKeys: bundle: obj -> int array
    val workBundleEntries: bundle: obj -> obj array
    val ringSuccessor: laneCount: int -> laneIndex: int -> closedLanes: int array -> obj
    val ringMergeOrder: laneCount: int -> int array
    val ringFinalLane: laneCount: int -> obj
    val settlementDecision: phase: string -> observation: string -> string
    val convergenceReady: laneCount: int -> completionIds: string array -> bundle: obj -> delivery: obj -> bool

    val createAdmission: deps: obj -> FissionAdmissionRuntime
    val admit: runtime: FissionAdmissionRuntime -> ownerSessionId: string -> parsed: obj -> Task<obj>
    val isActive: runtime: FissionAdmissionRuntime -> ownerSessionId: string -> bool
    val release: runtime: FissionAdmissionRuntime -> ownerSessionId: string -> unit
    val markSilentInterrupt: ownerSessionId: string -> unit
    val isSilentInterrupt: ownerSessionId: string -> bool
    val tryConsumeSilentInterrupt: ownerSessionId: string -> bool
    val clearSilentInterrupt: ownerSessionId: string -> unit
    val clearOwner: ownerSessionId: string -> unit
