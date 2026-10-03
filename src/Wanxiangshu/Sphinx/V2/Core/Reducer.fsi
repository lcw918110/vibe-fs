namespace Wanxiangshu.Sphinx.V2.Core

module Reducer =
    /// Fold one event. The same event input always yields the same state: no clock, no
    /// randomness, no network, no model call (WHAT[sphinx-v2-016]).
    val apply: InquiryState option -> InquiryEvent -> Result<InquiryState, CoreError>

    /// Atomic bodies at one logical revision; validates base and complete post-state fingerprint.
    val applyTransition:
        (string -> string) -> EventId -> InquiryState option -> TransitionBatch -> Result<InquiryState, CoreError>

    /// Standalone typed-event history; not the durable one-revision TransitionBatch boundary.
    val foldBatch: InquiryEvent list -> Result<InquiryState, CoreError>

    val fold: InquiryEvent list -> Result<InquiryState, CoreError>
