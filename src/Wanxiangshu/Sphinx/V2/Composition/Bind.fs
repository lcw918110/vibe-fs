namespace Wanxiangshu.Sphinx.V2.Composition

open System
open Wanxiangshu.Persistence.EventStore
open Wanxiangshu.Sphinx
open Wanxiangshu.Sphinx.V2.Core
open Wanxiangshu.Sphinx.V2.Persistence

/// The v2 rule program and the storage binding.
///
/// WHAT[sphinx-v2-019]: the v2 rule is registered on the same canonical spine the rest
/// of the repository uses. It does not create a private store, a second history or a
/// session pool; it hands its rule to `CanonicalIntegrator.createWithRules`, which
/// already owns structural heads and the journal fold.
///
/// WHAT[sphinx-v2-009]: the v2 program carries exactly one Sphinx rule. The three old
/// rules (legacy observation, generic inquiry, legacy inquiry) are gone — they are the
/// second fold and the results-only table this rewrite removes.

[<RequireQualifiedAccess>]
module Bind =

    /// The v2 rule list, ready for `CanonicalIntegrator.createWithRules`.
    let rules: IntegrationRule list =
        [ Integrator.rule Wanxiangshu.Host.HostDigest.sha256Hex ]

    /// The `Current` key v2 publishes under.
    let currentKey = Integrator.currentKey

    /// Builds a local EventStore bound to the canonical spine with the v2 rule.
    ///
    /// `commonDir` must name a real durable directory. There is no memory fallback: an
    /// inquiry whose facts cannot survive a restart is not an inquiry (WHAT[sphinx-v2-033]).
    let createDurableStore (commonDir: string) (writerId: string) : Result<IEventStore, string> =
        if String.IsNullOrWhiteSpace commonDir then
            Error "SPHINX_COMMON_DIR must name a durable directory"
        elif String.IsNullOrWhiteSpace writerId then
            Error "a durable store requires a non-blank writer identity"
        else
            let integrator =
                CanonicalIntegrator.createWithRules (CanonicalIntegrator.baseRules @ rules) (fun eventType ->
                    AuthoritativeEventTypes.isKnown eventType
                    || SphinxV2EventTypes.isKnown eventType)

            Ok(EventStore.createLocal commonDir writerId integrator)

    /// Reads the published state for one inquiry. `None` means the inquiry is not in the
    /// durable record; it never means "empty inquiry".
    let tryInquiry (store: IEventStore) (inquiryId: InquiryId) : Result<InquiryState option, CurrentError> =
        store.TryCurrent currentKey
        |> Option.map (fun current -> Integrator.tryState current inquiryId)
        |> Option.defaultValue (Ok None)
