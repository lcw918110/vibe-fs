namespace Wanxiangshu.Sphinx.V2.Persistence

open Wanxiangshu.Persistence.EventStore
open Wanxiangshu.Sphinx.V2.Core

[<RequireQualifiedAccess>]
type CurrentError =
    | DomainConflict of DomainConflict
    | SemanticRejected of reason: string

[<RequireQualifiedAccess>]
module Integrator =
    /// The `Current` key v2 inquiries are published under.
    val currentKey: string

    /// Read the exact accepted ancestor required by pure transition preparation.
    val parentState:
        obj -> InquiryId -> Wanxiangshu.Sphinx.V2.Core.EventId option -> Result<InquiryState option, string>

    /// The v2 integration rule. Folds accepted envelopes through the single reducer.
    val rule: (string -> string) -> IntegrationRule

    /// Reads one published inquiry state.
    val tryState: obj -> InquiryId -> Result<InquiryState option, CurrentError>
