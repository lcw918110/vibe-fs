namespace Wanxiangshu.Repository.Knowledge.Casebook

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Persistence.EventStore



/// DSL-class: DurableFact — CASE-003: one typed observation captured from the
/// final execution layer (builtin read/glob/grep Host execution). Never
/// inferred from transcript text; capture may be incomplete.
[<RequireQualifiedAccess>]
type Observation =
    | FileRead of path: string * contentHash: string
    | GlobResult of pattern: string * paths: string list
    | GrepResult of pattern: string * matches: (string * int * string) list

/// DSL-class: DurableFact — CASE-002 / KR-002: minimal Case model with dual baselines.
/// Identity is the stable logical case identity (scoped to invocation).
type Case =
    { Identity: string
      SourceTrace: string
      Q: string
      A: string
      RelatedPaths: string list
      CompletionFileState: string
      MaintenanceFileState: string
      AccessOrder: int64
      Observations: Observation list }

    member this.SessionId = this.Identity

/// DSL-class: DurableFact — CASE-007 / KR-007: the Casebook domain events. Physical
/// persistence is the unified EventStore; these are the fold inputs.
[<RequireQualifiedAccess>]
type CasebookEvent =
    | CaseCaptured of Case
    | CaseRefreshed of
        identity: string *
        q: string *
        a: string *
        maintenanceFileState: string *
        relatedPaths: string list *
        observations: Observation list
    | CaseAccessed of identity: string
    | CaseEvicted of identity: string

/// DSL-class: Decision — CASE-004/005: the replay classification. No-delta is
/// only a freshness hint, never a correctness proof.
[<RequireQualifiedAccess>]
type ReplayResult =
    | Fresh
    | Stale

module internal CasebookValues =

    let observationToNative (observation: Observation) : obj =
        match observation with
        | Observation.FileRead(path, contentHash) ->
            createObj [ "kind" ==> "file-read"; "path" ==> path; "contentHash" ==> contentHash ]
        | Observation.GlobResult(pattern, paths) ->
            createObj
                [ "kind" ==> "glob-result"
                  "pattern" ==> pattern
                  "paths" ==> List.toArray paths ]
        | Observation.GrepResult(pattern, matches) ->
            createObj
                [ "kind" ==> "grep-result"
                  "pattern" ==> pattern
                  "matches" ==> List.toArray matches ]

    let observationOfNative (value: obj) : Observation =
        match unbox<string> value?kind with
        | "file-read" -> Observation.FileRead(unbox value?path, unbox value?contentHash)
        | "glob-result" -> Observation.GlobResult(unbox value?pattern, unbox<string array> value?paths |> Array.toList)
        | "grep-result" ->
            Observation.GrepResult(
                unbox value?pattern,
                unbox<(string * int * string) array> value?matches |> Array.toList
            )
        | kind -> invalidOp $"unknown casebook observation kind: {kind}"

    let observationsToNative (observations: Observation list) : obj array =
        observations |> List.map observationToNative |> List.toArray

    let observationsOfNative (values: obj array) : Observation list =
        values |> Array.map observationOfNative |> Array.toList

    let caseToNative (case: Case) : obj =
        createObj
            [ "identity" ==> case.Identity
              "sourceTrace" ==> case.SourceTrace
              "q" ==> case.Q
              "a" ==> case.A
              "relatedPaths" ==> List.toArray case.RelatedPaths
              "completionFileState" ==> case.CompletionFileState
              "maintenanceFileState" ==> case.MaintenanceFileState
              "accessOrder" ==> case.AccessOrder
              "observations" ==> observationsToNative case.Observations ]

    let caseOfNative (value: obj) : Case =
        { Identity = unbox value?identity
          SourceTrace = unbox value?sourceTrace
          Q = unbox value?q
          A = unbox value?a
          RelatedPaths = unbox<string array> value?relatedPaths |> Array.toList
          CompletionFileState = unbox value?completionFileState
          MaintenanceFileState = unbox value?maintenanceFileState
          AccessOrder = unbox<int64> value?accessOrder
          Observations = observationsOfNative (unbox value?observations) }

    let eventToNative (event: CasebookEvent) : obj =
        match event with
        | CasebookEvent.CaseCaptured case -> createObj [ "kind" ==> "captured"; "case" ==> caseToNative case ]
        | CasebookEvent.CaseRefreshed(identity, q, a, maintenanceFileState, relatedPaths, observations) ->
            createObj
                [ "kind" ==> "refreshed"
                  "identity" ==> identity
                  "q" ==> q
                  "a" ==> a
                  "maintenanceFileState" ==> maintenanceFileState
                  "relatedPaths" ==> List.toArray relatedPaths
                  "observations" ==> observationsToNative observations ]
        | CasebookEvent.CaseAccessed identity -> createObj [ "kind" ==> "accessed"; "identity" ==> identity ]
        | CasebookEvent.CaseEvicted identity -> createObj [ "kind" ==> "evicted"; "identity" ==> identity ]

module Observations =

    [<Import("normalizeObservations", "wanxiangli/casebook")>]
    let private normalizeValues (items: obj array) : obj array = jsNative

    [<Import("sameObservations", "wanxiangli/casebook")>]
    let private sameValues (left: obj array) (right: obj array) : bool = jsNative

    let normalize (observations: Observation list) : Observation list =
        observations
        |> CasebookValues.observationsToNative
        |> normalizeValues
        |> CasebookValues.observationsOfNative

    let classifyReplay (stored: Observation list) (replayed: Observation list) : ReplayResult =
        if sameValues (CasebookValues.observationsToNative stored) (CasebookValues.observationsToNative replayed) then
            ReplayResult.Fresh
        else
            ReplayResult.Stale

/// Converts decoded storage events and reads the immutable Current held by the Integrator.
module CasebookProjection =

    [<Import("CasebookState", "wanxiangli/casebook")>]
    let private casebookStateClass: obj = jsNative

    [<Emit("new $0()")>]
    let private construct (cls: obj) : obj = jsNative

    type State = private State of obj

    let emptyState: State = State(construct casebookStateClass)

    let apply (State state) (event: CasebookEvent) : State =
        State(state?apply (CasebookValues.eventToNative event))

    let internal currentNative (store: IEventStore) : obj =
        let (State state) =
            match store.TryCurrent "Casebook" with
            | None -> emptyState
            | Some current -> unbox<State> current

        state

    let projectCases (store: IEventStore) (capacity: int) : Map<string, Case> =
        match store.TryCurrent "Casebook" with
        | None -> Map.empty
        | Some current ->
            let (State state) = unbox<State> current
            let visible: obj = state?visible capacity

            unbox<obj array> visible?cases
            |> Array.map (fun value ->
                let case = CasebookValues.caseOfNative value
                case.Identity, case)
            |> Map.ofArray

    let resolve (store: IEventStore) (capacity: int) (shelfmark: string) : Result<Case option, string> =
        let optionalCase (value: obj) =
            if isNull value then
                None
            else
                Some(CasebookValues.caseOfNative value)

        match store.TryCurrent "Casebook" with
        | None -> Ok None
        | Some current ->
            let (State state) = unbox<State> current
            let value: obj = state?resolve (capacity, shelfmark)

            Ok(optionalCase value)
