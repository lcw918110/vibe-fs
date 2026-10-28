namespace Wanxiangshu.OpenCode

open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Participant.Provider.Projection.ProviderProjection
open Wanxiangshu.Participant.Provider.Projection.CapturedWire

module ProviderWireCapture =

    val decodeCapturedMessage: rawObj: obj -> CapturedWireMessage option
    val isSyntheticMessage: rawObj: obj -> bool
    val decodeMessage: rawObj: obj -> WireMessage option
    val visibleProviderRuns: rawMessages: obj list -> Set<ProviderRunIdentity>
    val decodeRequest: requestObj: obj -> ProviderWireProjection
    val decodeMessageView: rawMessages: obj list -> ProviderWireProjection
    val decodeCapturedMessageView: rawMessages: obj list -> CapturedWireMessage list
    val wireMessageView: captured: CapturedWireMessage list -> ProviderWireProjection
    val lastUserMessageId: rawMessages: obj list -> PhysicalUserMessageId option

    /// managed-chat-execution-017: the Host's own post-compaction continuation
    /// of this request's trailing user row.
    val isHostCompactionContinuation: rawMessages: obj list -> physicalUserMessageId: PhysicalUserMessageId -> bool

    val trySemanticTurnOfHostMessageId: messageId: string -> rawMessages: obj list -> int option
    val lastUserPromptKey: rawMessages: obj list -> PromptKey option
    val formalText: rawObj: obj -> string
