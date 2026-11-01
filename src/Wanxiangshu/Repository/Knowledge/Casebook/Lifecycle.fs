namespace Wanxiangshu.Repository.Knowledge.Casebook

open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Persistence.EventStore

/// Product enablement and host observation shapes for the shared case service.
module CasebookLifecycle =

    [<Import("sessionDraftStore", "wanxiangli/casebook")>]
    let private sessionDraftStore: obj = jsNative

    [<Import("CasebookObservationCollector", "wanxiangli/casebook-access")>]
    type private LibraryObservationCollector() =
        member _.Collect(sessionId: string, toolName: string, args: obj, output: string) : unit = jsNative
        member _.RecordFileRead(sessionId: string, path: string, output: string, ?contentHash: string) : unit = jsNative
        member _.Count(sessionId: string) : int = jsNative

    type CasebookObservationCollector internal (value: obj) =
        let inner = unbox<LibraryObservationCollector> value

        new() = CasebookObservationCollector(box (LibraryObservationCollector()))

        member _.Collect(sessionId: string, toolName: string, args: obj, output: string) : unit =
            inner.Collect(sessionId, toolName, args, output)

        member _.RecordFileRead(sessionId: string, path: string, output: string) : unit =
            let hash = CasebookCapture.contentHash output
            inner.RecordFileRead(sessionId, path, output, hash)

        member _.Drain(sessionId: string) : Observation list =
            CasebookServiceBinding.drainCollector value sessionId

        member _.Count(sessionId: string) : int = inner.Count(sessionId)

    let collector = CasebookObservationCollector(CasebookServiceBinding.observationCollector)

    let private stateGate = obj ()
    // DSL-MUTABLE: resource
    let mutable private enabledWorkspace: string option = None

    let setEnabled (workspaceRoot: string option) : unit =
        lock stateGate (fun () ->
            enabledWorkspace <-
                match workspaceRoot with
                | Some root when CasebookFeature.isEnabled root -> Some root
                | _ -> None)

    let isEnabled () : bool =
        lock stateGate (fun () -> enabledWorkspace.IsSome)

    let notePrompt (delegateSessionId: string) (q: string) : unit =
        sessionDraftStore?setQ (delegateSessionId, q)

    let noteAnswer (delegateSessionId: string) (a: string) : unit =
        sessionDraftStore?setA (delegateSessionId, a)

    let cleanupDraft (delegateSessionId: string) : unit =
        CasebookServiceBinding.cleanupDraft delegateSessionId

    let tryFinalizeDraft
        (workspaceRoot: string)
        (store: IEventStore)
        (delegateSessionId: string)
        : Task<CaseFinalizeSettlement> =
        CasebookServiceBinding.finalizeDraft workspaceRoot store delegateSessionId

    let finalizeEngineerCase
        (store: IEventStore)
        (identity: string)
        (sourceTrace: string)
        (q: string)
        (a: string)
        (relatedPaths: string list)
        (completionStateRef: string)
        : Task<CaseFinalizeSettlement> =
        let case: Case =
            { Identity = identity
              SourceTrace = sourceTrace
              Q = q
              A = a
              RelatedPaths = relatedPaths
              CompletionFileState = completionStateRef
              MaintenanceFileState = completionStateRef
              AccessOrder = 0L
              Observations = [] }

        CasebookServiceBinding.finalizeCaptured store case

    let touchAccess
        (workspaceRoot: string)
        (store: IEventStore)
        (sessionId: string)
        : Task<Result<unit, CasebookMutationError>> =
        CasebookServiceBinding.touch workspaceRoot store sessionId
