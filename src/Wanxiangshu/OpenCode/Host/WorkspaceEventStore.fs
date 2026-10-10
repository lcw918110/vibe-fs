namespace Wanxiangshu.OpenCode

open System
open System.Collections.Generic
open Wanxiangshu.Persistence.EventStore
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Sphinx.V2.Composition

/// Process-local EventStore owners keyed by git common-dir.
/// One acquired entry owns exactly one WriterId.ndjson and one CanonicalIntegrator.
/// No GitRawStore is created on the runtime append/replay path.
module WorkspaceEventStore =

    /// The single host-wide store serves the workspace journal, casebook
    /// lifecycle/fetch, js-transaction durability and strength durable
    /// promotion, so its history program is the journal spine plus exactly
    /// those owning oracles. Registration order matches the historical full
    /// program. Native Sphinx inquiries share this same writer and Current.
    let private hostProgram: IntegrationRule list =
        CanonicalIntegrator.baseRules
        @ Wanxiangshu.Strength.StrengthIntegrationRules.rules
        @ Wanxiangshu.Sphinx.V2.Composition.Bind.rules
        @ Wanxiangshu.Repository.Knowledge.Casebook.CasebookIntegrationRules.rules
        @ Wanxiangshu.Repository.Programming.Js.JsTransactionIntegrationRules.rules
        @ Wanxiangshu.Mission.Planning.PlanIntegrationRules.rules

    /// durable-events-019 registration-necessity seam: the production history
    /// program with exactly one named registration removed. Every other
    /// registration, including Sphinx v2, keeps production order. Removing
    /// Structural or Journal is rejected by the canonical integrator's
    /// fail-closed base-rule precondition; that construction refusal is the
    /// observable form for those two spine registrations.
    let programWithoutRegistration (ruleName: string) : IntegrationRule list =
        hostProgram |> List.filter (fun rule -> rule.Name <> ruleName)

    /// DSL-state-combination: physical — shared local writer + canonical Current.
    type private SharedEntry =
        { Store: IEventStore
          mutable RefCount: int }

    let private gate = obj ()
    // DSL-MUTABLE: resource — shared workspace event store refcount registry
    let private shared = Dictionary<string, SharedEntry>()

    let private deferredStore (commonDir: string) : IEventStore =
        let active =
            lazy
                (let writerId = Guid.NewGuid().ToString("N")

                 let integrator =
                     CanonicalIntegrator.createWithRules hostProgram AuthoritativeEventTypes.isKnown

                 EventStore.createLocal commonDir writerId integrator)

        { new IEventStore with
            member _.Append(events) = active.Value.Append events
            member _.WritePayload(content) = active.Value.WritePayload content
            member _.ReadPayload(payloadRef) = active.Value.ReadPayload payloadRef
            member _.TryCurrent(key) = active.Value.TryCurrent key
            member _.TryEvent(eventId) = active.Value.TryEvent eventId
            member _.TryHeads(streamId) = active.Value.TryHeads streamId
            member _.TryHead(streamId) = active.Value.TryHead streamId
            member _.AllHeads() = active.Value.AllHeads()
            member _.ReloadLocal() = active.Value.ReloadLocal() }

    let acquire (commonDir: string) : IEventStore =
        if String.IsNullOrWhiteSpace commonDir then
            failwith "WorkspaceEventStore.acquire: commonDir is empty"

        lock gate (fun () ->
            match shared.TryGetValue commonDir with
            | true, entry ->
                entry.RefCount <- entry.RefCount + 1
                entry.Store
            | false, _ ->
                // durable-events-020: owning the workspace capability is not the
                // same thing as consuming durable semantics. Canonical replay is
                // deliberately behind the IEventStore methods so plugin load can
                // finish without folding the entire workspace history.
                let store = deferredStore commonDir

                shared.[commonDir] <- { Store = store; RefCount = 1 }

                store)

    let private lookupCurrent commonDir : IEventStore option =
        lock gate (fun () ->
            match shared.TryGetValue commonDir with
            | true, entry -> Some entry.Store
            | false, _ -> None)

    /// Borrow the already-owned process-local store without changing ownership.
    let tryCurrent (commonDir: string) : IEventStore option =
        if String.IsNullOrWhiteSpace commonDir then
            None
        else
            lookupCurrent commonDir

    let private decrementEntry commonDir (entry: SharedEntry) =
        let remaining = entry.RefCount - 1

        if remaining <= 0 then
            shared.Remove commonDir |> ignore
        else
            entry.RefCount <- remaining

    let private releaseOwned commonDir =
        lock gate (fun () ->
            match shared.TryGetValue commonDir with
            | true, entry -> decrementEntry commonDir entry
            | false, _ -> ())

    let release (commonDir: string) =
        if String.IsNullOrWhiteSpace commonDir then
            ()
        else
            releaseOwned commonDir

    let bootPort (commonDir: string) : IJournalEventStoreBoot =
        let store = acquire commonDir

        { new IJournalEventStoreBoot with
            member _.ResumeOrCreate(runtimeId, processId, startedAt) =
                EventStoreJournalWriter.resumeOrCreate (runtimeId, processId, startedAt, store) }
