namespace Wanxiangshu.OpenCode.Host

open System.Threading.Tasks
open Wanxiangshu.Persistence.Journal

/// Production fold observations and a direct durable child-settlement entry point.
/// No process restart or plugin activation ordering is simulated.
module LoadRecoverySurface =
    val create: unit -> obj
    val foldCanonical: state: obj -> factJson: string -> obj
    val childSettlements: state: obj -> string array
    val settleChildRuns: handle: JournalHandle -> Task<unit>
    val childView: state: obj -> parent: string -> child: string -> obj
    val lookupChild: state: obj -> parent: string -> key: string -> byName: bool -> obj
    val bindingEvidence: state: obj -> child: string -> obj
    val installResolvers: state: obj -> unit
    val clearResolvers: unit -> unit
    val lane: session: string -> obj
    val clearLane: session: string -> unit
    val drainCompletions: state: obj -> parent: string -> maxCount: int -> completedAt: string -> Task<obj>
