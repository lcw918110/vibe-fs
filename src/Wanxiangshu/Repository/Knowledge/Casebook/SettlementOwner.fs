namespace Wanxiangshu.Repository.Knowledge.Casebook

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Persistence.EventStore

module private SettlementInterop =
    [<Import("CasebookSettlementOwner", "wanxiangli/casebook-maintenance")>]
    let settlementOwnerCtor: obj = jsNative

    [<Emit("new $0($1)")>]
    let createOwner (ctor: obj) (onConflict: obj -> unit) : obj = jsNative

    let eventKey (eventId: EventId) : string = EventId.value eventId

    let decodeOutcome (tag: string) : CasebookSettlementOutcome =
        match tag with
        | "LogScopeFailure" -> CasebookSettlementOutcome.LogScopeFailure
        | "CaseUnavailable" -> CasebookSettlementOutcome.CaseUnavailable
        | "CaseUnavailableKnown" -> CasebookSettlementOutcome.CaseUnavailableKnown
        | _ -> CasebookSettlementOutcome.Unscoped

    let classifySemantic (failure: CasebookAppendFailure) : string * string * obj =
        match CasebookSemanticCutIncident.tryFromSettlement failure with
        | None -> "", "Unscoped", null
        | Some candidate -> eventKey candidate.Failure.EventId, "SemanticCut", box candidate

    let classifyFailure (failure: CasebookAppendFailure) : string * string * obj =
        match failure.Error with
        | AppendError.StorageInvalid _ ->
            let incident = CasebookSemanticCutIncident.OfFailure failure
            eventKey incident.Failure.EventId, "StorageInvalid", box incident
        | _ -> classifySemantic failure

/// Owner of the Casebook settlement outcomes.
///
/// The case owns its own archive, refresh and invalidation result: a conflict
/// that affects one case makes that case unavailable and is reported once. It
/// never terminates the host process, never revokes finished engineering work
/// and never discards saved artifacts. A failure of the shared event log keeps
/// its own scope: appends to that log stop and the failure stays a failure.
[<Sealed>]
type CasebookSettlementOwner(onConflict: CasebookSemanticCutIncident -> unit) =
    do
        if isNull (box onConflict) then
            nullArg "onConflict"

    let inner =
        SettlementInterop.createOwner
            SettlementInterop.settlementOwnerCtor
            (fun raw -> onConflict (unbox<CasebookSemanticCutIncident> raw))

    member _.Observe(failure: CasebookAppendFailure) : CasebookSettlementOutcome =
        let eventId, kind, incident = SettlementInterop.classifyFailure failure
        let tag: string = inner?Observe(eventId, kind, incident)
        SettlementInterop.decodeOutcome tag

    member _.Redeliver(incident: CasebookSemanticCutIncident) : CasebookSettlementOutcome =
        let eventId = SettlementInterop.eventKey incident.Failure.EventId
        let tag: string = inner?Redeliver(eventId, box incident)
        SettlementInterop.decodeOutcome tag
