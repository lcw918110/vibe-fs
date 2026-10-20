namespace Wanxiangshu.OpenCode

open System.Threading.Tasks

/// Pair-programming provider projection owner surface. The transform owns its
/// journal and placement invariants; callers observe only JSON messages/results.
module PairProgrammingThoughtSurface =

    /// Boot the production EventStore journal behind one opaque capability.
    val createJournal: directory: string -> Task<obj>

    val disposeJournal: journal: obj -> unit

    /// Append one anchored guideline fact and return only the owner projection flags.
    val appendAnchoredPair: journal: obj -> payload: obj -> Task<obj>

    val pairCount: journal: obj -> session: string -> int

    val appendContextReanchored:
        journal: obj -> session: string -> previousEpoch: int64 -> nextEpoch: int64 -> observedRun: string -> Task<obj>


    val projectionFlags: journal: obj -> session: string -> obj

    /// Fold one anchored pair without opening a journal; used by the pure law test.
    val foldAnchoredPair: payload: obj -> obj

    val text: string
    val source: string
    val markerSource: string
    val markerToolName: string
    val canonicalText: string
    val deniedText: string

    /// Executes the production pair constructor with an observable pure callback.
    val gapConstructorTrace: messageId: string -> obj

    /// Observes fail-fast behavior without weakening the production exception edge.
    val gapConstructorFailureTrace: messageId: string -> obj

    val stableCallId: sessionId: obj -> ordinal: int64 -> string

    val isPairProgrammingThought: raw: obj -> bool

    val providerIdOfMessage: raw: obj -> obj

    val providerIdFromMessages: raw: obj array -> obj

    val skipAutoInjectedRequested: providerId: obj -> bool

    val tryInject: sessionId: obj -> markerText: string -> rawMessages: obj array -> Task<obj>

    /// Durable owner path for anchored replay tests. The journal remains opaque to
    /// callers; only the transform may read/append its pair-placement facts.
    val tryInjectWithJournal:
        journal: obj -> sessionId: obj -> markerText: string -> rawMessages: obj array -> Task<obj>
