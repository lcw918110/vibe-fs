namespace Wanxiangshu.OpenCode

open System.Threading.Tasks

module SessionsSurface =
    val observeDeliveryDiscovery: input: obj -> title: string -> Task<obj>
    val familyRoot: parents: obj -> session: string -> string
    val physicalParents: parents: obj -> children: obj -> string array
    val flattenedChildAdapterProbe: unit -> Task<obj>
    val restoredOwnerFlatteningProbe: unit -> Task<obj>
    val unknownParentQueryErrorProbe: unit -> Task<obj>
    val ancestryCycleProbe: unit -> Task<obj>
    val interruptAttemptAdapterProbe: unit -> Task<obj>
    val interruptRejectedAdapterProbe: unit -> Task<obj>
    val terminationProbe: rejectAbort: bool -> Task<obj>
    val interruptTerminatedAdapterProbe: unit -> Task<obj>
    val awaitRunnerIdle: isBusy: obj -> sessionId: string -> Task
